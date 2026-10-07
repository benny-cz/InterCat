using System.Runtime.CompilerServices;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// One segment's timed records counted into aligned decimal tiles: the first level of §12.1's overview pyramid (S4),
/// built once per verified reader. A tile is <c>[j·10^k, (j+1)·10^k)</c> presentation ticks and holds its records' count
/// per mechanism, the row range they occupy, and their earliest and latest reading.
/// </summary>
/// <remarks>
/// <para>
/// A segment's rows are sorted by native reading (segment-v1 §4), and session time is a monotonic scaling of it that a
/// quarantined record lacks, so a tile's records are one run of rows with, at most, untimed rows among them. Counting
/// into columns then takes a tile whole wherever its records fall in one column, and reads its rows only where a column
/// boundary falls among them (§10.3's exact boundary fragments): a query reads at most the rows it would have read, and
/// usually a few per boundary. A segment whose timed readings ever go backwards has no tiles and is read row by row.
/// </para>
/// <para>
/// The tile width is the smallest power of ten that spans the segment's timed readings in at most
/// <see cref="MaximumTilesPerSpan"/> tiles. A tile is then never wider than a tenth of the narrowest 1-2-5 column that
/// spans the session in <see cref="SessionMinimap.MaximumColumns"/> columns, so every such column is an exact union of
/// every segment's tiles, and the minimap reads no row at all.
/// </para>
/// </remarks>
internal sealed class SegmentTimeTiles
{
    /// <summary>At most this many tile widths span one segment's timed readings.</summary>
    public const long MaximumTilesPerSpan = 20_480;

    private static readonly ConditionalWeakTable<SegmentReaderV1, SegmentTimeTiles> Built = new();

    private readonly long[] indexes;
    private readonly long[] lows;
    private readonly long[] highs;
    private readonly int[] firstRows;
    private readonly int[] endRows;
    private readonly Mechanism[] mechanisms;

    /// <summary>Per tile, one count per mechanism of <see cref="mechanisms"/>, flat.</summary>
    private readonly int[] counts;

    private SegmentTimeTiles(
        int rows,
        int untimed,
        long? first,
        long? last,
        long width,
        long[] indexes,
        long[] lows,
        long[] highs,
        int[] firstRows,
        int[] endRows,
        Mechanism[] mechanisms,
        int[] counts,
        bool ordered)
    {
        Rows = rows;
        Untimed = untimed;
        First = first;
        Last = last;
        Width = width;
        this.indexes = indexes;
        this.lows = lows;
        this.highs = highs;
        this.firstRows = firstRows;
        this.endRows = endRows;
        this.mechanisms = mechanisms;
        this.counts = counts;
        Ordered = ordered;
    }

    /// <summary>Every row of the segment, timed or not.</summary>
    public int Rows { get; }

    /// <summary>The rows with no usable session time, which no tile holds.</summary>
    public int Untimed { get; }

    /// <summary>The earliest timed reading, in presentation ticks; null when no row is timed.</summary>
    public long? First { get; }

    /// <summary>The latest timed reading, in presentation ticks.</summary>
    public long? Last { get; }

    /// <summary>A tile's width in presentation ticks, a power of ten.</summary>
    public long Width { get; }

    /// <summary>How many tiles hold a record.</summary>
    public int Count => indexes.Length;

    /// <summary>Whether the timed readings never go backwards, so tiles are usable; otherwise every count reads rows.</summary>
    public bool Ordered { get; }

    /// <summary>The earliest and latest reading of one tile's records.</summary>
    internal (long Low, long High) Readings(int tile) => (lows[tile], highs[tile]);

    /// <summary>The tiles of a segment, built on first use and kept for as long as its reader is.</summary>
    /// <summary>Whether a reader's tiles have been built: what a zoom's cost depends on, read only by tests of it.</summary>
    internal static bool IsBuilt(SegmentReaderV1 segment) => Built.TryGetValue(segment, out _);

    public static SegmentTimeTiles Of(SegmentReaderV1 segment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (Built.TryGetValue(segment, out SegmentTimeTiles? known))
        {
            return known;
        }

        SegmentTimeTiles built = Build(segment, cancellationToken);
        return Built.GetValue(segment, _ => built);
    }

    /// <summary>
    /// Counts the segment's timed records inside <paramref name="columns"/>' interval into them: a tile whose records
    /// all fall in one column by its counts, and any other tile, or the whole segment when it has no usable tiles, by
    /// reading its rows. The result is exactly what reading every row would count. Returns how many rows it read.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int CountInto(SegmentReaderV1 segment, TimelineColumns columns, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(columns);
        TimeRange interval = columns.Interval;
        if (First is not { } first || Last is not { } last || last < interval.StartTicks || first >= interval.EndTicks)
        {
            return 0;
        }

        if (!Ordered)
        {
            return CountRows(segment, 0, segment.RowCount, columns, cancellationToken);
        }

        // Tiles come in time order, so most fall in the column the tile before fell in: its bounds are kept, and a column
        // is looked up only when a tile leaves them.
        int column = -1;
        int read = 0;
        long columnStart = 0;
        long columnEnd = 0;
        for (int tile = 0; tile < indexes.Length; tile++)
        {
            if ((tile & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            long low = lows[tile];
            long high = highs[tile];
            if (high < interval.StartTicks || low >= interval.EndTicks)
            {
                continue;
            }

            if (low >= interval.StartTicks && high < interval.EndTicks)
            {
                if (column < 0 || low < columnStart || low >= columnEnd)
                {
                    column = columns.ColumnOf(low)!.Value;
                    TimeRange bounds = TimelineColumns.IntervalOf(interval, columns.Counts.Count, column);
                    (columnStart, columnEnd) = (bounds.StartTicks, bounds.EndTicks);
                }

                if (high < columnEnd)
                {
                    int offset = tile * mechanisms.Length;
                    for (int slot = 0; slot < mechanisms.Length; slot++)
                    {
                        if (counts[offset + slot] > 0)
                        {
                            columns.Add(column, mechanisms[slot], counts[offset + slot]);
                        }
                    }

                    continue;
                }
            }

            read += CountRows(segment, firstRows[tile], endRows[tile], columns, cancellationToken);
        }

        return read;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int CountRows(SegmentReaderV1 segment, int from, int to, TimelineColumns columns, CancellationToken cancellationToken)
    {
        SegmentColumnSlice times = segment.Slice(SegmentColumnId.SessionRelativeTicks);
        SegmentColumnSlice kinds = segment.Slice(SegmentColumnId.Mechanism);
        for (int row = from; row < to; row++)
        {
            if ((row & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (times.SignedAt(row) is { } nanoseconds && columns.ColumnOf(nanoseconds / 100) is { } column)
            {
                columns.Add(column, (Mechanism)kinds.UnsignedAt(row)!.Value);
            }
        }

        return to - from;
    }

    /// <summary>Reads the segment's time and mechanism columns once and counts its timed records into tiles.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static SegmentTimeTiles Build(SegmentReaderV1 segment, CancellationToken cancellationToken)
    {
        SegmentColumnSlice times = segment.Slice(SegmentColumnId.SessionRelativeTicks);
        SegmentColumnSlice kinds = segment.Slice(SegmentColumnId.Mechanism);
        int untimed = 0;
        long first = long.MaxValue;
        long last = long.MinValue;
        long previous = long.MinValue;
        bool ordered = true;
        Span<bool> present = stackalloc bool[TimelineColumns.MechanismCodes];
        for (int row = 0; row < segment.RowCount; row++)
        {
            if ((row & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (times.SignedAt(row) is not { } nanoseconds)
            {
                untimed++;
                continue;
            }

            long tick = nanoseconds / 100;
            ordered &= tick >= previous;
            previous = tick;
            first = Math.Min(first, tick);
            last = Math.Max(last, tick);
            present[TimelineColumns.CodeOf((Mechanism)kinds.UnsignedAt(row)!.Value)] = true;
        }

        int timed = segment.RowCount - untimed;
        if (timed == 0 || !ordered)
        {
            return new(segment.RowCount, untimed, timed == 0 ? null : first, timed == 0 ? null : last, 1,
                [], [], [], [], [], [], [], ordered);
        }

        // The narrowest power of ten that spans the readings in at most MaximumTilesPerSpan tiles.
        long width = 1;
        Int128 span = (Int128)last - first + 1;
        while (span > (Int128)MaximumTilesPerSpan * width)
        {
            width = checked(width * 10);
        }

        var observed = new List<Mechanism>();
        for (int code = 0; code < present.Length; code++)
        {
            if (present[code])
            {
                observed.Add((Mechanism)code);
            }
        }

        Mechanism[] mechanisms = [.. observed];
        Span<int> slotOf = stackalloc int[TimelineColumns.MechanismCodes];
        for (int slot = 0; slot < mechanisms.Length; slot++)
        {
            slotOf[(int)mechanisms[slot]] = slot;
        }

        var indexes = new List<long>();
        var lows = new List<long>();
        var highs = new List<long>();
        var firstRows = new List<int>();
        var endRows = new List<int>();
        var counts = new List<int>();
        for (int row = 0; row < segment.RowCount; row++)
        {
            if ((row & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (times.SignedAt(row) is not { } nanoseconds)
            {
                continue;
            }

            long tick = nanoseconds / 100;
            long index = FloorDivide(tick, width);
            if (indexes.Count == 0 || indexes[^1] != index)
            {
                indexes.Add(index);
                lows.Add(tick);
                highs.Add(tick);
                firstRows.Add(row);
                endRows.Add(row + 1);
                for (int slot = 0; slot < mechanisms.Length; slot++)
                {
                    counts.Add(0);
                }
            }

            highs[^1] = tick;
            endRows[^1] = row + 1;
            counts[((indexes.Count - 1) * mechanisms.Length) + slotOf[(int)(Mechanism)kinds.UnsignedAt(row)!.Value]]++;
        }

        return new(segment.RowCount, untimed, first, last, width,
            [.. indexes], [.. lows], [.. highs], [.. firstRows], [.. endRows], mechanisms, [.. counts], ordered: true);
    }

    /// <summary>The largest integer at or below <paramref name="value"/> / <paramref name="divisor"/>.</summary>
    internal static long FloorDivide(long value, long divisor)
    {
        long quotient = value / divisor;
        return (value % divisor != 0 && (value < 0) != (divisor < 0)) ? quotient - 1 : quotient;
    }
}
