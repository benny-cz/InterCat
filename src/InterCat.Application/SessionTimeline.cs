using System.Runtime.CompilerServices;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// The whole retained extent at a coarse fixed resolution, for the minimap (plan §6.2): observed rows per column, and
/// the capture's own coverage per column. Capture coverage does not depend on what was observed, so a column emptied by
/// a capture gap still shows the gap.
/// </summary>
public sealed record SessionMinimap(TimeRange Extent, IReadOnlyList<int> Counts, IReadOnlyList<CoverageState> Capture)
{
    /// <summary>Plan §6.2 and §23: the minimap draws the retained extent in at most this many columns.</summary>
    public const int MaximumColumns = 2_000;

    public TimeRange IntervalOf(int column) => TimelineColumns.IntervalOf(Extent, Counts.Count, column);
}

/// <summary>
/// The timeline over one interval at the resolution a viewport needs, from one leased generation. Its buckets follow the
/// overview's rules exactly, so the whole extent at the overview's column count is the overview's own timeline.
/// </summary>
public sealed record SessionTimelineDetail(
    Guid SessionId,
    long Generation,
    TimeRange Interval,
    IReadOnlyList<TimelineBucket> Buckets);

public static class SessionTimelineQuery
{
    /// <summary>A viewport never needs more columns than the minimap holds for the whole session.</summary>
    public const int MaximumColumns = SessionMinimap.MaximumColumns;

    /// <summary>
    /// Counts the observations inside a half-open interval of presentation ticks into equal columns. A row without a
    /// usable session time cannot be placed and is not counted, exactly as the overview's timeline omits it. The scan is
    /// cancellable and runs off the input path; the overview's buckets stand in until it answers (P25).
    /// </summary>
    public static SessionTimelineDetail Detail(
        SessionStore store,
        TimeRange interval,
        int columns,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columns, MaximumColumns);

        using EvidenceLease lease = store.AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        SourceClockDescriptor clock = SessionSegments.SourceClock(store.Root, manifest)
            ?? throw new InvalidDataException("This generation names no source clock, so its timeline cannot be placed.");
        var counted = new TimelineColumns(interval, columns, tallyMechanisms: true);
        foreach (string name in SessionSegments.Names(manifest))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SegmentReaderV1 segment = SessionSegments.Open(store.Root, manifest, name);
            SegmentColumnSlice times = segment.Slice(SegmentColumnId.SessionRelativeTicks);
            SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
            for (int row = 0; row < segment.RowCount; row++)
            {
                if ((row & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (times.SignedAt(row) is { } nanoseconds && counted.ColumnOf(nanoseconds / 100) is { } column)
                {
                    counted.Add(column, (Mechanism)mechanisms.UnsignedAt(row)!.Value);
                }
            }
        }

        return new(
            manifest.SessionId,
            manifest.Generation,
            interval,
            Array.AsReadOnly(counted.Buckets(SessionSegments.CoverageLedger(store.Root, manifest), clock)));
    }
}

/// <summary>
/// Observations counted into equal half-open columns of one interval. Column <c>i</c> holds
/// <c>[start + i·span/n, start + (i+1)·span/n)</c> in presentation ticks, so the columns partition the interval exactly
/// and a column never straddles two others' records.
/// </summary>
internal sealed class TimelineColumns
{
    private readonly TimeRange interval;
    private readonly int[] counts;
    private readonly Dictionary<Mechanism, int>[]? mechanisms;

    public TimelineColumns(TimeRange interval, int columns, bool tallyMechanisms)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        this.interval = interval;
        counts = new int[(int)Math.Min(columns, interval.SpanTicks)];
        if (tallyMechanisms)
        {
            mechanisms = new Dictionary<Mechanism, int>[counts.Length];
            for (int index = 0; index < counts.Length; index++) mechanisms[index] = [];
        }
    }

    public IReadOnlyList<int> Counts => counts;

    /// <summary>The column holding a presentation tick, or null outside the interval.</summary>
    /// <remarks>
    /// Called once per row. It is compiled optimized at once rather than waiting to be promoted from the first tier,
    /// which a projection called every couple of seconds would spend its first seconds waiting for.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int? ColumnOf(long tick) => interval.Contains(tick)
        ? (int)Math.Min(counts.Length - 1, (long)(((Int128)(tick - interval.StartTicks) * counts.Length) / interval.SpanTicks))
        : null;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Add(int column, Mechanism mechanism)
    {
        if (counts[column] == int.MaxValue)
        {
            throw new InvalidOperationException(
                "One timeline bucket has more than 2,147,483,647 observed rows. The viewer count cannot "
                + "represent it without compaction, so it is refused rather than wrapped to a false value.");
        }

        counts[column]++;
        if (mechanisms is not null)
        {
            Dictionary<Mechanism, int> tally = mechanisms[column];
            if (tally.GetValueOrDefault(mechanism) == int.MaxValue)
            {
                throw new InvalidOperationException("One mechanism exceeds the timeline bucket's count bound.");
            }

            tally[mechanism] = tally.GetValueOrDefault(mechanism) + 1;
        }
    }

    /// <summary>
    /// The buckets, each with its dominant mechanism and the coverage of the mechanisms observed in it. A bucket with
    /// nothing observed has no mechanism to judge and stays unknown: an empty interval is not proof of inactivity.
    /// </summary>
    public TimelineBucket[] Buckets(CoverageLedgerV1? coverage, SourceClockDescriptor clock)
    {
        if (mechanisms is null)
        {
            throw new InvalidOperationException("These columns were counted without their mechanisms.");
        }

        return [.. Enumerable.Range(0, counts.Length).Select(index =>
        {
            TimeRange range = IntervalOf(interval, counts.Length, index);
            return new TimelineBucket(
                range,
                counts[index],
                null,
                mechanisms[index].OrderByDescending(entry => entry.Value).ThenBy(entry => entry.Key)
                    .Select(entry => entry.Key).DefaultIfEmpty(Mechanism.UnknownMechanism).First(),
                BucketCoverage(coverage, clock, range, counts[index] == 0 ? [] : mechanisms[index].Keys));
        })];
    }

    /// <summary>The capture's own coverage over each column, independent of what was observed in it.</summary>
    public CoverageState[] CaptureCoverage(CoverageLedgerV1? coverage, SourceClockDescriptor clock)
    {
        if (coverage is null)
        {
            return [.. Enumerable.Repeat(CoverageState.UnknownCoverage, counts.Length)];
        }

        // Adjacent columns share a boundary, so each boundary is mapped to its native reading once.
        long?[] boundaries = [.. Enumerable.Range(0, counts.Length + 1).Select(index => FirstNativeAt(clock,
            index == counts.Length ? interval.EndTicks : IntervalOf(interval, counts.Length, index).StartTicks))];
        return [.. SessionCoverage.CaptureStates(coverage, [.. Enumerable.Range(0, counts.Length).Select(index =>
            boundaries[index] is { } first && boundaries[index + 1] is { } last && last > first
                ? new TimeRange(first, last)
                : (TimeRange?)null)])];
    }

    public static TimeRange IntervalOf(TimeRange interval, int columns, int column)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(column);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(column, columns);
        return new(
            interval.StartTicks + (long)(((Int128)column * interval.SpanTicks) / columns),
            interval.StartTicks + (long)(((Int128)(column + 1) * interval.SpanTicks) / columns));
    }

    internal static CoverageState BucketCoverage(
        CoverageLedgerV1? ledger,
        SourceClockDescriptor clock,
        TimeRange presentationInterval,
        IEnumerable<Mechanism> mechanisms)
    {
        if (ledger is null)
        {
            return CoverageState.UnknownCoverage;
        }

        Mechanism[] scoped = [.. mechanisms];
        if (scoped.Length == 0 || NativeInterval(clock, presentationInterval) is not { } nativeInterval)
        {
            return CoverageState.UnknownCoverage;
        }

        return SessionCoverage.Worst(SessionCoverage.ForMechanisms(ledger, scoped, nativeInterval)
            .Select(result => result.State));
    }

    /// <summary>
    /// The native readings a presentation interval covers, or null when it maps to none. The presentation tick is
    /// nanoseconds / 100 with C# truncation toward zero; these are the exact nanosecond boundaries of that mapping,
    /// including its asymmetric zero tick (I3, I8). An interval that cannot be mapped is unknown, never inferred from
    /// neighboring bins.
    /// </summary>
    private static TimeRange? NativeInterval(SourceClockDescriptor clock, TimeRange presentationInterval) =>
        FirstNativeAt(clock, presentationInterval.StartTicks) is { } first
            && FirstNativeAt(clock, presentationInterval.EndTicks) is { } lastExclusive
            && lastExclusive > first
                ? new TimeRange(first, lastExclusive)
                : null;

    /// <summary>The first native reading at or after a presentation tick's lower bound, or null when it cannot be mapped.</summary>
    private static long? FirstNativeAt(SourceClockDescriptor clock, long presentationTick)
    {
        try
        {
            return SourceClockMath.FirstNativeAtOrAfter(clock, new SessionTimestamp(PresentationLowerNanoseconds(presentationTick)));
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static long PresentationLowerNanoseconds(long tick) => tick switch
    {
        < 0 => checked(tick * 100 - 99),
        0 => -99,
        _ => checked(tick * 100),
    };
}
