using System.Globalization;
using InterCat.Domain;
using Xunit;

namespace InterCat.PropertyTests;

/// <summary>
/// Boundary and conservation properties for time bucketing, generated over adversarial extents. Records
/// at a boundary, at the extent's edges and at the final observation must all be conserved (I3, I4, I5).
/// </summary>
public sealed class BucketProperties
{
    private const int Cases = 300;

    public static TheoryData<int> Seeds => [3, 11, 97, 20_260_921];

    [Theory(DisplayName = "I4: every eligible point belongs to exactly one bucket")]
    [MemberData(nameof(Seeds))]
    public void EveryPointBelongsToExactlyOneBucket(int seed)
    {
        var random = new Random(seed);
        for (int index = 0; index < Cases; index++)
        {
            (TimeRange extent, TimeBuckets buckets) = Generate(random);
            for (int sample = 0; sample < 32; sample++)
            {
                long tick = SampleTick(random, extent, buckets);
                int? bucket = buckets.IndexOf(tick);
                int hits = 0;
                for (int candidate = 0; candidate < buckets.Count; candidate++)
                {
                    if (buckets.RangeOf(candidate).Contains(tick))
                    {
                        hits++;
                    }
                }

                Assert.True(
                    hits <= 1,
                    Describe(seed, index, extent, buckets, $"tick {tick} fell into {hits} buckets"));
                Assert.Equal(hits == 1, bucket is not null);
                if (bucket is not null)
                {
                    Assert.True(
                        buckets.RangeOf(bucket.Value).Contains(tick),
                        Describe(seed, index, extent, buckets, $"tick {tick} is not inside bucket {bucket}"));
                }
            }
        }
    }

    [Theory(DisplayName = "I3: bucket boundaries are half-open and contiguous")]
    [MemberData(nameof(Seeds))]
    public void BucketBoundariesAreHalfOpenAndContiguous(int seed)
    {
        var random = new Random(seed);
        for (int index = 0; index < Cases; index++)
        {
            (TimeRange extent, TimeBuckets buckets) = Generate(random);
            TimeRange previous = buckets.RangeOf(0);
            Assert.Equal(buckets.Extent.StartTicks, previous.StartTicks);

            for (int bucket = 1; bucket < Math.Min(buckets.Count, 64); bucket++)
            {
                TimeRange current = buckets.RangeOf(bucket);
                Assert.True(
                    current.StartTicks == previous.EndTicks,
                    Describe(seed, index, extent, buckets, $"bucket {bucket} starts at {current.StartTicks}, previous ended at {previous.EndTicks}"));
                Assert.False(
                    previous.Contains(current.StartTicks),
                    Describe(seed, index, extent, buckets, $"boundary tick {current.StartTicks} belongs to two buckets"));
                Assert.Equal(bucket, buckets.IndexOf(current.StartTicks));
                previous = current;
            }
        }
    }

    [Theory(DisplayName = "I4: the covered extent contains the requested one, including the final observation")]
    [MemberData(nameof(Seeds))]
    public void TheCoveredExtentContainsTheRequestedOne(int seed)
    {
        var random = new Random(seed);
        for (int index = 0; index < Cases; index++)
        {
            (TimeRange extent, TimeBuckets buckets) = Generate(random);

            Assert.Equal(extent.StartTicks, buckets.Extent.StartTicks);
            Assert.True(
                buckets.Extent.EndTicks >= extent.EndTicks,
                Describe(seed, index, extent, buckets, "the covered extent ends before the requested one"));
            Assert.True(
                buckets.IndexOf(extent.EndTicks - 1) is not null,
                Describe(seed, index, extent, buckets, "the final observation has no bucket"));
            Assert.Null(buckets.IndexOf(extent.StartTicks - 1));
            Assert.Null(buckets.IndexOf(buckets.Extent.EndTicks));
        }
    }

    [Theory(DisplayName = "I5: cell counts match the records those cells resolve to")]
    [MemberData(nameof(Seeds))]
    public void CountingBySampleMatchesCountingByRange(int seed)
    {
        var random = new Random(seed);
        for (int index = 0; index < Cases; index++)
        {
            (TimeRange extent, TimeBuckets buckets) = Generate(random);
            long[] ticks = new long[64];
            for (int sample = 0; sample < ticks.Length; sample++)
            {
                ticks[sample] = SampleTick(random, extent, buckets);
            }

            var byIndex = new Dictionary<int, int>();
            int outside = 0;
            foreach (long tick in ticks)
            {
                int? bucket = buckets.IndexOf(tick);
                if (bucket is null)
                {
                    outside++;
                    continue;
                }

                byIndex[bucket.Value] = byIndex.GetValueOrDefault(bucket.Value) + 1;
            }

            int total = outside;
            foreach (int count in byIndex.Values)
            {
                total += count;
            }

            // The cell counts plus the explicitly out-of-range records account for every sample: no record
            // is lost at an edge and none is counted twice (I5).
            Assert.Equal(ticks.Length, total);

            foreach ((int bucket, int count) in byIndex)
            {
                TimeRange range = buckets.RangeOf(bucket);
                int matches = 0;
                foreach (long tick in ticks)
                {
                    if (range.Contains(tick))
                    {
                        matches++;
                    }
                }

                Assert.Equal(count, matches);
            }
        }
    }

    private static (TimeRange Extent, TimeBuckets Buckets) Generate(Random random)
    {
        long start = random.NextInt64(-500_000_000L, 500_000_000L);
        long span = random.NextInt64(1, 5_000_000_000L);
        var extent = new TimeRange(start, checked(start + span));
        TimeBuckets buckets = random.Next(0, 2) == 0
            ? TimeBuckets.Create(extent, random.Next(1, 2_000))
            : TimeBuckets.CreateWithWidth(extent, Math.Max(1, span / Math.Max(1, random.Next(1, 500))));
        return (extent, buckets);
    }

    private static long SampleTick(Random random, TimeRange extent, TimeBuckets buckets)
    {
        return random.Next(0, 5) switch
        {
            0 => extent.StartTicks,
            1 => extent.EndTicks - 1,
            2 => buckets.Extent.EndTicks - 1,
            3 => checked(extent.StartTicks + (buckets.WidthTicks * random.Next(0, Math.Max(1, buckets.Count)))),
            _ => random.NextInt64(extent.StartTicks - 2, buckets.Extent.EndTicks + 2),
        };
    }

    private static string Describe(int seed, int index, TimeRange extent, TimeBuckets buckets, string failure) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"seed {seed}, case {index}: {failure}. extent [{extent.StartTicks}, {extent.EndTicks}), "
            + $"{buckets.Count} buckets of {buckets.WidthTicks} ticks");
}
