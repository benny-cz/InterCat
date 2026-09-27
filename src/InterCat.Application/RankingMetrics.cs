using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>What the ranked table ranks its groups and processes by: §6.1's metric selector, over §5.2's metrics.</summary>
public enum RankingMetric
{
    /// <summary>Each process's own records, as the ladder has always ranked (`process-activity-v1`).</summary>
    Records = 1,

    /// <summary>Transport-observed bytes each process's own send records measured: sender-accounted (`metrics-v1` §4, §6).</summary>
    BytesSent = 2,

    /// <summary>Transport-observed bytes each process's own receive records measured: receiver-accounted.</summary>
    BytesReceived = 3,

    /// <summary>
    /// The RPC client calls each process completed, counted by their stop (`metrics-v1` §8a): the logical-operations basis,
    /// on the calling side.
    /// </summary>
    RpcCallsMade = 4,

    /// <summary>The RPC server calls each process completed, counted by their stop: the serving side.</summary>
    RpcCallsServed = 5,
}

/// <summary>Which kind of measure a ranking reads, and so which read answers it.</summary>
public enum RankingFamily
{
    /// <summary>The records the overview already counts; nothing more is read.</summary>
    Records = 1,

    /// <summary>Transport bytes, read by <see cref="SessionByteRanking"/>.</summary>
    Bytes = 2,

    /// <summary>RPC calls, read by <see cref="SessionCallRanking"/>.</summary>
    Calls = 3,
}

/// <summary>A ranking's family and names.</summary>
public static class RankingMetrics
{
    /// <summary>Which read answers <paramref name="metric"/>.</summary>
    public static RankingFamily FamilyOf(RankingMetric metric) => metric switch
    {
        RankingMetric.Records => RankingFamily.Records,
        RankingMetric.BytesSent or RankingMetric.BytesReceived => RankingFamily.Bytes,
        RankingMetric.RpcCallsMade or RankingMetric.RpcCallsServed => RankingFamily.Calls,
        _ => throw new ArgumentOutOfRangeException(nameof(metric)),
    };
}

/// <summary>
/// A row's value under a ranking other than records: null when nothing it holds measured the metric, whether its records
/// declared a measurement that carried no value (<see cref="Unmeasured"/>) or it made no record the metric takes at all.
/// Under an RPC call ranking the value is the calls completed, <see cref="Unmeasured"/> the stops paired with no start,
/// which are stated and never counted, and <see cref="Failed"/> the completed calls whose stop reported a failure.
/// </summary>
public sealed record RankedValue(RankingMetric Metric, long? Value, long Measured, long Unmeasured)
{
    /// <summary>Under an RPC call ranking, the completed calls whose stop reported a status other than 0; null otherwise.</summary>
    public long? Failed { get; init; }

    /// <summary>Whether the row holds any record the metric takes, measured or not.</summary>
    public bool Holds => Measured + Unmeasured > 0;
}

/// <summary>What every ranking read answers: which session and generation, over which scope.</summary>
public interface IRankingMeasures
{
    Guid SessionId { get; }

    long Generation { get; }

    /// <summary>The presentation interval the measures answer; null for the whole retained capture.</summary>
    TimeRange? Interval { get; }
}

/// <summary>How a ranking read places a presentation interval on the source clock.</summary>
internal static class RankingScope
{
    /// <summary>
    /// A presentation interval as native readings, each bound's first reading at or after it, as the command line converts
    /// a time (metrics-v1 §5); null when no reading can fall between the two.
    /// </summary>
    public static TimeRange? NativeInterval(SourceClockDescriptor clock, TimeRange presentation)
    {
        long first = SourceClockMath.FirstNativeAtOrAfter(clock, new SessionTimestamp(checked(presentation.StartTicks * 100)));
        long end = SourceClockMath.FirstNativeAtOrAfter(clock, new SessionTimestamp(checked(presentation.EndTicks * 100)));
        return end > first ? new TimeRange(first, end) : null;
    }
}
