using InterCat.Domain;
using Xunit;

namespace InterCat.Domain.Tests;

public sealed class LatencyHistogramTests
{
    [Fact(DisplayName = "R3: a quantile is reported as the interval it fell in, never as an invented value")]
    public void QuantilesAreReportedAsBounds()
    {
        var histogram = new LatencyHistogram();
        for (int index = 0; index < 98; index++)
        {
            histogram.Record(1_000);
        }

        histogram.Record(5_000_000);
        histogram.Record(5_000_000);

        LatencyHistogramSnapshot snapshot = histogram.Read();

        Assert.Equal(100, snapshot.Samples);
        Assert.Equal(1_000, snapshot.MinimumNanoseconds);
        Assert.Equal(5_000_000, snapshot.MaximumNanoseconds);
        LatencyBound median = snapshot.Median!.Value;
        Assert.True(median.LowerNanoseconds <= 1_000 && 1_000 < median.UpperNanoseconds);
        LatencyBound tail = snapshot.Percentile99!.Value;
        Assert.True(tail.LowerNanoseconds <= 5_000_000 && 5_000_000 < tail.UpperNanoseconds);
        Assert.True(tail.LowerNanoseconds > median.LowerNanoseconds);
    }

    [Fact(DisplayName = "R3: an empty latency histogram reports no quantile rather than zero")]
    public void EmptyHistogramHasNoQuantile()
    {
        LatencyHistogramSnapshot snapshot = new LatencyHistogram().Read();

        Assert.Equal(0, snapshot.Samples);
        Assert.Null(snapshot.Median);
        Assert.Null(snapshot.Percentile99);
        Assert.Null(snapshot.MinimumNanoseconds);
        Assert.Null(snapshot.MaximumNanoseconds);
        Assert.Null(snapshot.MeanNanoseconds);
        Assert.Empty(snapshot.Buckets);
    }

    [Fact(DisplayName = "I3: histogram buckets are half-open, contiguous and contain their samples")]
    public void BucketsAreHalfOpenAndContiguous()
    {
        var histogram = new LatencyHistogram();
        long[] samples = [0, 1, 2, 3, 7, 8, 1_023, 1_024, 1_000_000_000];
        foreach (long sample in samples)
        {
            histogram.Record(sample);
        }

        LatencyHistogramSnapshot snapshot = histogram.Read();

        Assert.Equal(samples.Length, snapshot.Samples);
        Assert.Equal(samples.Sum(), snapshot.TotalNanoseconds);
        foreach (long sample in samples)
        {
            LatencyBucket bucket = snapshot.Buckets.Single(
                candidate => candidate.LowerNanoseconds <= sample && sample < candidate.UpperNanoseconds);
            Assert.True(bucket.Samples > 0);
        }

        IReadOnlyList<LatencyBucket> ordered = [.. snapshot.Buckets.OrderBy(bucket => bucket.LowerNanoseconds)];
        for (int index = 1; index < ordered.Count; index++)
        {
            Assert.True(ordered[index - 1].UpperNanoseconds <= ordered[index].LowerNanoseconds);
        }
    }

    [Fact(DisplayName = "R3: a single sample answers every quantile from the bucket it fell in")]
    public void SingleSampleAnswersEveryQuantile()
    {
        var histogram = new LatencyHistogram();
        histogram.Record(4_096);

        LatencyHistogramSnapshot snapshot = histogram.Read();

        Assert.Equal(snapshot.Median, snapshot.Percentile99);
        Assert.Equal(4_096d, snapshot.MeanNanoseconds);
    }

    [Fact(DisplayName = "R3: a negative duration is a defect, never a clamped zero")]
    public void NegativeDurationIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new LatencyHistogram().Record(-1));
}
