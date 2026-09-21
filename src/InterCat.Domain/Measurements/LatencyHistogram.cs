using System.Numerics;

namespace InterCat.Domain;

/// <summary>
/// The half-open nanosecond interval a recorded quantile falls inside. A histogram knows the bucket, not
/// the sample, so the bound is reported instead of an interpolated value that was never measured (R3).
/// </summary>
public readonly record struct LatencyBound(long LowerNanoseconds, long UpperNanoseconds)
{
    public override string ToString() => $"[{LowerNanoseconds}, {UpperNanoseconds}) ns";
}

/// <summary>One populated bucket and the number of samples inside it.</summary>
public readonly record struct LatencyBucket(long LowerNanoseconds, long UpperNanoseconds, long Samples);

/// <summary>
/// An immutable reading of a <see cref="LatencyHistogram"/>. Every quantile is an interval, and an empty
/// histogram reports no quantile at all rather than zero.
/// </summary>
public sealed record LatencyHistogramSnapshot
{
    public required long Samples { get; init; }
    public required long TotalNanoseconds { get; init; }
    public required long? MinimumNanoseconds { get; init; }
    public required long? MaximumNanoseconds { get; init; }
    public required LatencyBound? Median { get; init; }
    public required LatencyBound? Percentile99 { get; init; }
    public required IReadOnlyList<LatencyBucket> Buckets { get; init; }

    /// <summary>The arithmetic mean, which is exact because the total is accumulated, not bucketed.</summary>
    public double? MeanNanoseconds => Samples == 0 ? null : (double)TotalNanoseconds / Samples;
}

/// <summary>
/// A bounded power-of-two latency histogram for stage overhead. Recording allocates nothing and takes no
/// lock, so it can run on a capture callback thread (R9). Bucket <c>i</c> covers
/// <c>[2^(i-1), 2^i)</c> nanoseconds, and bucket 0 holds the single value zero.
/// </summary>
public sealed class LatencyHistogram
{
    /// <summary>Buckets up to 2^46 ns, about 19.5 hours. Anything longer saturates the last bucket.</summary>
    public const int BucketCount = 48;

    private readonly long[] buckets = new long[BucketCount];
    private long samples;
    private long totalNanoseconds;
    private long minimumNanoseconds = long.MaxValue;
    private long maximumNanoseconds = long.MinValue;

    /// <summary>Records one non-negative duration. A negative duration is a defect, never a clamped zero.</summary>
    public void Record(long nanoseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nanoseconds);

        Interlocked.Increment(ref samples);
        Interlocked.Add(ref totalNanoseconds, nanoseconds);
        Interlocked.Increment(ref buckets[BucketIndex(nanoseconds)]);
        UpdateMinimum(nanoseconds);
        UpdateMaximum(nanoseconds);
    }

    /// <summary>Records one duration expressed in <see cref="System.Diagnostics.Stopwatch"/> ticks.</summary>
    public void RecordTicks(long stopwatchTicks, long ticksPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(stopwatchTicks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerSecond);
        Int128 nanoseconds = (Int128)stopwatchTicks * SourceClockMath.SessionTicksPerSecond / ticksPerSecond;
        Record(nanoseconds > long.MaxValue ? long.MaxValue : (long)nanoseconds);
    }

    public LatencyHistogramSnapshot Read()
    {
        long count = Interlocked.Read(ref samples);
        var populated = new List<LatencyBucket>();
        long[] counts = new long[BucketCount];
        for (int index = 0; index < BucketCount; index++)
        {
            counts[index] = Interlocked.Read(ref buckets[index]);
            if (counts[index] > 0)
            {
                populated.Add(new(LowerBound(index), UpperBound(index), counts[index]));
            }
        }

        return new()
        {
            Samples = count,
            TotalNanoseconds = Interlocked.Read(ref totalNanoseconds),
            MinimumNanoseconds = count == 0 ? null : Interlocked.Read(ref minimumNanoseconds),
            MaximumNanoseconds = count == 0 ? null : Interlocked.Read(ref maximumNanoseconds),
            Median = Quantile(counts, count, 0.50),
            Percentile99 = Quantile(counts, count, 0.99),
            Buckets = populated,
        };
    }

    internal static int BucketIndex(long nanoseconds)
    {
        if (nanoseconds == 0)
        {
            return 0;
        }

        int index = 64 - BitOperations.LeadingZeroCount((ulong)nanoseconds);
        return index >= BucketCount ? BucketCount - 1 : index;
    }

    internal static long LowerBound(int index) => index == 0 ? 0 : 1L << (index - 1);

    internal static long UpperBound(int index) =>
        index == BucketCount - 1 ? long.MaxValue : index == 0 ? 1 : 1L << index;

    private static LatencyBound? Quantile(long[] counts, long samples, double quantile)
    {
        if (samples == 0)
        {
            return null;
        }

        // The rank is the first sample at or past the quantile, so a single sample answers every quantile.
        long rank = (long)Math.Ceiling(quantile * samples);
        if (rank < 1)
        {
            rank = 1;
        }

        long cumulative = 0;
        for (int index = 0; index < counts.Length; index++)
        {
            cumulative += counts[index];
            if (cumulative >= rank)
            {
                return new(LowerBound(index), UpperBound(index));
            }
        }

        return new(LowerBound(counts.Length - 1), UpperBound(counts.Length - 1));
    }

    private void UpdateMinimum(long nanoseconds)
    {
        long current = Interlocked.Read(ref minimumNanoseconds);
        while (nanoseconds < current)
        {
            long seen = Interlocked.CompareExchange(ref minimumNanoseconds, nanoseconds, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }

    private void UpdateMaximum(long nanoseconds)
    {
        long current = Interlocked.Read(ref maximumNanoseconds);
        while (nanoseconds > current)
        {
            long seen = Interlocked.CompareExchange(ref maximumNanoseconds, nanoseconds, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }
}
