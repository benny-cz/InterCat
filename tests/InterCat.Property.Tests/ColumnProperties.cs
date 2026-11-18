using System.Globalization;
using InterCat.Application;
using InterCat.Domain;
using Xunit;

namespace InterCat.PropertyTests;

/// <summary>
/// Boundary and conservation properties of the timeline's columns - the column a reading falls in, and the interval each
/// column holds - generated over adversarial extents, some as wide as a reading allows, near the ends of its range. A
/// record at a column boundary, at the extent's edges and at its final observation is conserved (I3, I4, I5). These are
/// the columns every count, focus and byte lane of the timeline is placed in (§10.3).
/// </summary>
public sealed class ColumnProperties
{
    private const int Cases = 300;

    public static TheoryData<int> Seeds => [3, 11, 97, 20_260_921];

    [Theory(DisplayName = "I4: every eligible point belongs to exactly one timeline column, at any span a reading allows")]
    [MemberData(nameof(Seeds))]
    public void EveryPointBelongsToExactlyOneColumn(int seed)
    {
        var random = new Random(seed);
        for (int index = 0; index < Cases; index++)
        {
            (TimeRange extent, TimelineColumns columns) = Generate(random);
            int count = columns.Counts.Count;
            for (int sample = 0; sample < 32; sample++)
            {
                long tick = SampleTick(random, extent, count);
                int? column = columns.ColumnOf(tick);
                int hits = 0;
                for (int candidate = 0; candidate < count; candidate++)
                {
                    if (TimelineColumns.IntervalOf(extent, count, candidate).Contains(tick))
                    {
                        hits++;
                    }
                }

                Assert.True(hits <= 1, Describe(seed, index, extent, count, $"tick {tick} fell into {hits} columns"));
                Assert.Equal(hits == 1, column is not null);
                if (column is not null)
                {
                    Assert.True(
                        TimelineColumns.IntervalOf(extent, count, column.Value).Contains(tick),
                        Describe(seed, index, extent, count, $"tick {tick} is not inside column {column}"));
                }
            }
        }
    }

    [Theory(DisplayName = "I3: timeline column boundaries are half-open and contiguous, every column holding a reading, at any span a reading allows")]
    [MemberData(nameof(Seeds))]
    public void ColumnBoundariesAreHalfOpenAndContiguous(int seed)
    {
        var random = new Random(seed);
        for (int index = 0; index < Cases; index++)
        {
            (TimeRange extent, TimelineColumns columns) = Generate(random);
            int count = columns.Counts.Count;
            TimeRange previous = TimelineColumns.IntervalOf(extent, count, 0);
            Assert.Equal(extent.StartTicks, previous.StartTicks);
            Assert.Equal(extent.EndTicks, TimelineColumns.IntervalOf(extent, count, count - 1).EndTicks);
            Assert.True(count <= extent.SpanTicks, Describe(seed, index, extent, count, "more columns than readings"));

            foreach (int column in Columns(random, count))
            {
                TimeRange current = TimelineColumns.IntervalOf(extent, count, column);
                TimeRange before = TimelineColumns.IntervalOf(extent, count, column - 1);
                Assert.True(
                    current.StartTicks == before.EndTicks,
                    Describe(seed, index, extent, count, $"column {column} starts at {current.StartTicks}, the one before ended at {before.EndTicks}"));
                Assert.True(current.SpanTicks > 0, Describe(seed, index, extent, count, $"column {column} holds no reading"));
                Assert.False(
                    before.Contains(current.StartTicks),
                    Describe(seed, index, extent, count, $"boundary tick {current.StartTicks} belongs to two columns"));
                Assert.Equal(column, columns.ColumnOf(current.StartTicks));
                Assert.Equal(column - 1, columns.ColumnOf(current.StartTicks - 1));
            }
        }
    }

    [Theory(DisplayName = "I4: the timeline's columns hold the whole extent, its final observation in the last, and no reading outside it")]
    [MemberData(nameof(Seeds))]
    public void TheColumnsHoldTheWholeExtent(int seed)
    {
        var random = new Random(seed);
        for (int index = 0; index < Cases; index++)
        {
            (TimeRange extent, TimelineColumns columns) = Generate(random);
            int count = columns.Counts.Count;
            Assert.Equal(0, columns.ColumnOf(extent.StartTicks));
            Assert.Equal(count - 1, columns.ColumnOf(extent.EndTicks - 1));
            Assert.Null(columns.ColumnOf(extent.EndTicks));
            if (extent.StartTicks > long.MinValue)
            {
                Assert.Null(columns.ColumnOf(extent.StartTicks - 1));
            }
        }
    }

    [Theory(DisplayName = "I5: timeline column counts match the records those columns' intervals hold, every record counted once or outside")]
    [MemberData(nameof(Seeds))]
    public void CountingByColumnMatchesCountingByInterval(int seed)
    {
        var random = new Random(seed);
        for (int index = 0; index < Cases; index++)
        {
            (TimeRange extent, TimelineColumns columns) = Generate(random);
            int count = columns.Counts.Count;
            long[] ticks = new long[64];
            for (int sample = 0; sample < ticks.Length; sample++)
            {
                ticks[sample] = SampleTick(random, extent, count);
            }

            var byColumn = new Dictionary<int, int>();
            int outside = 0;
            foreach (long tick in ticks)
            {
                if (columns.ColumnOf(tick) is { } column)
                {
                    byColumn[column] = byColumn.GetValueOrDefault(column) + 1;
                }
                else
                {
                    outside++;
                }
            }

            // The column counts plus the readings outside the extent account for every sample: no record is lost at an
            // edge and none is counted twice (I5).
            Assert.Equal(ticks.Length, outside + byColumn.Values.Sum());
            foreach ((int column, int held) in byColumn)
            {
                TimeRange interval = TimelineColumns.IntervalOf(extent, count, column);
                Assert.Equal(held, ticks.Count(interval.Contains));
            }
        }
    }

    /// <summary>
    /// An extent and its columns: mostly a capture's spans, and one in eight as wide as a reading allows, beginning near
    /// the least a reading can be; up to the timeline's widest request, and never more columns than readings.
    /// </summary>
    private static (TimeRange Extent, TimelineColumns Columns) Generate(Random random)
    {
        (long start, long span) = random.Next(8) == 0
            ? ((long.MinValue / 2) + random.NextInt64(0, 1_000), long.MaxValue - random.NextInt64(0, 1_000))
            : (random.NextInt64(-500_000_000L, 500_000_000L), random.NextInt64(1, 5_000_000_000L));
        var extent = new TimeRange(start, checked(start + span));
        return (extent, new TimelineColumns(extent, random.Next(1, SessionTimelineQuery.MaximumColumns + 1), tallyMechanisms: false));
    }

    /// <summary>Up to 64 columns past the first: each, where there are few, else some at random and the last.</summary>
    private static IEnumerable<int> Columns(Random random, int count) => count <= 65
        ? Enumerable.Range(1, count - 1)
        : Enumerable.Range(0, 63).Select(_ => random.Next(1, count)).Append(count - 1);

    /// <summary>A reading at an edge of the extent or of a column, just outside the extent, or anywhere around it.</summary>
    private static long SampleTick(Random random, TimeRange extent, int count)
    {
        TimeRange column = TimelineColumns.IntervalOf(extent, count, random.Next(count));
        return random.Next(0, 6) switch
        {
            0 => extent.StartTicks,
            1 => extent.EndTicks - 1,
            2 => column.StartTicks,
            3 => column.EndTicks - 1,
            4 => random.Next(2) == 0 ? extent.EndTicks : extent.StartTicks - 1,
            _ => extent.StartTicks + random.NextInt64(0, extent.SpanTicks),
        };
    }

    private static string Describe(int seed, int index, TimeRange extent, int count, string failure) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"seed {seed}, case {index}: {failure}. extent [{extent.StartTicks}, {extent.EndTicks}), {count} columns");
}
