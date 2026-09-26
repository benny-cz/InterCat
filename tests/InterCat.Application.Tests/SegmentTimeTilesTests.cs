using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// A segment's tiles are the first level of §12.1's overview pyramid (S4). A count taken through them must be exactly
/// the count its rows give, for any interval and any number of columns (I4, §10.3's exact boundary fragments).
/// </summary>
public sealed class SegmentTimeTilesTests
{
    private static readonly Mechanism[] Kinds = [Mechanism.Tcp, Mechanism.Udp, Mechanism.NamedPipe, Mechanism.Rpc];

    [Theory(DisplayName = "I4: counting segments through their tiles equals counting their rows, for any interval and columns")]
    [InlineData(3)]
    [InlineData(41)]
    [InlineData(20_260_928)]
    public void TilesCountWhatRowsCount(int seed)
    {
        var random = new Random(seed);
        for (int trial = 0; trial < 12; trial++)
        {
            ObservationRowV1[] rows = RandomRows(random, ordered: random.Next(4) != 0);
            using var session = new TemporarySession();
            Publish(session.Store, rows, rowsPerSegment: random.Next(1, rows.Length + 1));
            IReadOnlyList<SegmentReaderV1> segments = Segments(session.Store);
            long[] ticks = [.. rows.Where(row => row.SessionRelativeTicks is not null).Select(row => row.SessionRelativeTicks!.Value / 100)];
            long low = ticks.Min() - random.Next(0, 50);
            long high = ticks.Max() + random.Next(1, 50);
            for (int query = 0; query < 20; query++)
            {
                long start = random.Next(3) == 0 ? low : low + random.NextInt64(0, high - low);
                long end = random.Next(3) == 0 ? high : start + 1 + random.NextInt64(0, high - start);
                var interval = new TimeRange(start, end);
                int columns = random.Next(1, 300);
                Assert.Equal(Snapshot(ByRows(segments, interval, columns)), Snapshot(ByTiles(segments, interval, columns)));
            }
        }
    }

    [Theory(DisplayName = "I4: every minimap column is a union of whole tiles of every segment, so the minimap reads no row")]
    [InlineData(5)]
    [InlineData(77)]
    public void MinimapColumnsAreWholeTiles(int seed)
    {
        var random = new Random(seed);
        for (int trial = 0; trial < 12; trial++)
        {
            ObservationRowV1[] rows = RandomRows(random, ordered: true);
            using var session = new TemporarySession();
            Publish(session.Store, rows, rowsPerSegment: random.Next(1, rows.Length + 1));
            IReadOnlyList<SegmentReaderV1> segments = Segments(session.Store);
            SegmentTimeTiles[] tiles = [.. segments.Select(segment => SegmentTimeTiles.Of(segment))];
            var extent = new TimeRange(tiles.Min(tile => tile.First!.Value), tiles.Max(tile => tile.Last!.Value) + 1);
            (TimeRange span, int count) = SessionMinimap.ColumnsFor(extent);
            long width = span.SpanTicks / count;
            Assert.True(count <= SessionMinimap.MaximumColumns);
            Assert.True(span.StartTicks <= extent.StartTicks && span.EndTicks >= extent.EndTicks);
            Assert.Equal(0, span.StartTicks % width);
            Assert.Contains(width / (long)Math.Pow(10, Math.Floor(Math.Log10(width))), new long[] { 1, 2, 5 });
            Assert.All(tiles, tile => Assert.Equal(0, width % tile.Width));
            var columns = new TimelineColumns(span, count, tallyMechanisms: false);
            foreach (SegmentTimeTiles tile in tiles)
            {
                for (int index = 0; index < tile.Count; index++)
                {
                    (long first, long last) = tile.Readings(index);
                    Assert.Equal(columns.ColumnOf(first), columns.ColumnOf(last));
                }
            }

            // Counting the minimap therefore reads no row, and still counts every timed record.
            Assert.All(segments, segment => Assert.Equal(0, SegmentTimeTiles.Of(segment).CountInto(segment, columns, CancellationToken.None)));
            Assert.Equal(rows.Count(row => row.SessionRelativeTicks is not null), columns.Counts.Sum());
        }
    }

    [Theory(DisplayName = "I4: a record is counted in the column whose stated interval holds it, at every boundary")]
    [InlineData(1)]
    [InlineData(2_026)]
    public void ColumnsHoldWhatTheyCount(int seed)
    {
        // §10.3's boundaries are b(i) = t0 + floor((t1 - t0) * i / W). A span that W does not divide leaves boundaries
        // that are not multiples of the span / W, and a record exactly at one must fall in the column it begins.
        var random = new Random(seed);
        for (int trial = 0; trial < 400; trial++)
        {
            long start = random.NextInt64(-10_000, 10_000);
            var interval = new TimeRange(start, start + random.Next(1, 3_000));
            int requested = random.Next(1, 300);
            var columns = new TimelineColumns(interval, requested, tallyMechanisms: false);
            int count = columns.Counts.Count;
            for (int column = 0; column < count; column++)
            {
                TimeRange bounds = TimelineColumns.IntervalOf(interval, count, column);
                Assert.Equal(column, columns.ColumnOf(bounds.StartTicks));
                Assert.Equal(column, columns.ColumnOf(bounds.EndTicks - 1));
            }

            Assert.Null(columns.ColumnOf(interval.StartTicks - 1));
            Assert.Null(columns.ColumnOf(interval.EndTicks));
        }
    }

    [Fact(DisplayName = "I4: a segment whose session times go backwards has no tiles and is counted row by row")]
    public void DisorderedTimesAreCountedByRows()
    {
        ObservationRowV1[] rows =
        [
            .. Enumerable.Range(0, 40).Select(index => Transfer(10 + index, ObservationKind.Send, AccountingSide.SendSide, 8, 100,
                (ulong)(index + 1)).Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = (40 - index) * 1_000L }),
        ];
        using var session = new TemporarySession();
        Publish(session.Store, rows);
        SegmentReaderV1 segment = Assert.Single(Segments(session.Store));
        Assert.False(SegmentTimeTiles.Of(segment).Ordered);
        var interval = new TimeRange(0, 500);
        Assert.Equal(Snapshot(ByRows([segment], interval, 7)), Snapshot(ByTiles([segment], interval, 7)));
        Assert.Equal(40, ByTiles([segment], interval, 7).Counts.Sum());
    }

    private static TimelineColumns ByTiles(IReadOnlyList<SegmentReaderV1> segments, TimeRange interval, int columns)
    {
        var counted = new TimelineColumns(interval, columns, tallyMechanisms: true);
        foreach (SegmentReaderV1 segment in segments)
        {
            SegmentTimeTiles.Of(segment).CountInto(segment, counted, CancellationToken.None);
        }

        return counted;
    }

    private static TimelineColumns ByRows(IReadOnlyList<SegmentReaderV1> segments, TimeRange interval, int columns)
    {
        var counted = new TimelineColumns(interval, columns, tallyMechanisms: true);
        foreach (SegmentReaderV1 segment in segments)
        {
            for (int row = 0; row < segment.RowCount; row++)
            {
                ObservationRowV1 read = segment.Row(row);
                if (read.SessionRelativeTicks is { } nanoseconds && counted.ColumnOf(nanoseconds / 100) is { } column)
                {
                    counted.Add(column, read.Mechanism);
                }
            }
        }

        return counted;
    }

    /// <summary>Every column's count and every mechanism's count in it, as comparable values.</summary>
    private static List<(int Column, Mechanism? Mechanism, int Count)> Snapshot(TimelineColumns columns)
    {
        var values = new List<(int, Mechanism?, int)>();
        for (int column = 0; column < columns.Counts.Count; column++)
        {
            values.Add((column, null, columns.Counts[column]));
        }

        foreach (MechanismTimelineLane lane in columns.MechanismLanes(null, TestClock))
        {
            for (int column = 0; column < lane.Buckets.Count; column++)
            {
                values.Add((column, lane.Mechanism, lane.Buckets[column].ObservationCount));
            }
        }

        return values;
    }

    /// <summary>
    /// Records in native order with session times that mostly advance in bursts, stand still, cross zero or are missing;
    /// with <paramref name="ordered"/> false, session times that jump backwards as well.
    /// </summary>
    private static ObservationRowV1[] RandomRows(Random random, bool ordered)
    {
        int count = random.Next(1, 400);
        long nanoseconds = random.NextInt64(-5_000_000, 5_000_000);
        var rows = new ObservationRowV1[count];
        for (int index = 0; index < count; index++)
        {
            nanoseconds += random.Next(5) switch
            {
                0 => 0,
                1 => random.Next(1, 100),
                2 => random.Next(100, 10_000),
                3 => random.Next(10_000, 5_000_000),
                _ => ordered ? random.Next(1, 1_000) : -random.Next(1, 100_000),
            };
            ObservationRowV1 row = Transfer(1_000 + index, ObservationKind.Send, AccountingSide.SendSide, 8, 100, (ulong)(index + 1))
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { Mechanism = Kinds[random.Next(Kinds.Length)] };
            rows[index] = random.Next(8) == 0 ? row : row with { SessionRelativeTicks = nanoseconds };
        }

        if (rows.All(row => row.SessionRelativeTicks is null))
        {
            rows[0] = rows[0] with { SessionRelativeTicks = nanoseconds };
        }

        return rows;
    }
}
