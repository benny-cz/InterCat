using InterCat.Analysis;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// The rows a process's rung lists beside its paired channels (§6.1, §7.1): its one-sided connections, its RPC channels
/// and its HTTP exchanges as one channel. The window lists them and an export writes them through these same rows, so a
/// person's export and <c>icat export</c>'s hold the same rows in the same order (R18).
/// </summary>
public static class ProcessRungRows
{
    /// <summary>
    /// One connection whose other end no record holds: named by that end, then its own end, how the capture saw it open
    /// and close, and what its transfers measured. Under a byte ranking (<paramref name="byteRanking"/>) it ranks by its
    /// own sends, receives or both, as a channel ranks by its process's own records on it.
    /// </summary>
    public static LadderRow Connection(ConnectionSummary connection, RankingMetric? byteRanking, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(connection);
        string protocol = connection.Mechanism == Mechanism.Udp ? "UDP" : "TCP";
        long measured = connection.Sends + connection.Receives - connection.Unmeasured;
        return new(connection.Key, connection.Name,
            $"{protocol} {connection.OwnEnd} · {connection.Lifetime} · {connection.Transfers(culture)}", connection.Records,
            measured > 0 ? checked(connection.SentBytes + connection.ReceivedBytes) : null, connection.Mechanism,
            CoverageState.UnknownCoverage, DetailLevel.Evidence, AccountingSide.CanonicalOwner)
        {
            Ranked = byteRanking is { } metric ? connection.RankedBy(metric) : null,
        };
    }

    /// <summary>One RPC channel: which side's calls, who is at their other end where the capture followed them, and what they came to.</summary>
    public static LadderRow RpcChannel(RpcChannelSummary channel, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(channel);
        string? peers = channel.Peers?.Describe(channel.Side, channel.Counts.Calls, culture);
        string detail = (channel.Side == RpcCallSide.Client ? "RPC client · " : "RPC server · ")
            + (peers is null ? string.Empty : peers + " · ")
            + channel.Outcome(culture);
        return new(channel.Key, channel.Name, detail, channel.Records, null, Mechanism.Rpc, CoverageState.UnknownCoverage,
            DetailLevel.Channel, AccountingSide.CanonicalOwner);
    }

    /// <summary>A process's HTTP exchanges as one channel: how many, how many were recorded whole, and how long they took.</summary>
    public static LadderRow HttpChannel(HttpChannelSummary channel, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return new(channel.Key, HttpChannelSummary.Name, "HTTP client · " + channel.Outcome(culture), channel.Records, null,
            Mechanism.Http, CoverageState.UnknownCoverage, DetailLevel.Channel, AccountingSide.CanonicalOwner);
    }

    /// <summary>
    /// The rung's rows in the order it lists them: its paired channels as the ladder ranked them where it lists nothing
    /// else, or with its connections, RPC channels and HTTP exchanges ranked together as the ladder ranks rows - by value
    /// where a byte ranking gave every one a value, else by records, then by key (R13). RPC carries no size and HTTP's
    /// bytes are its messages', never transport bytes (P3), so under a byte ranking their rows hold no value and rank
    /// after every transport row that measured one.
    /// </summary>
    public static IReadOnlyList<LadderRow> Ranked(
        IReadOnlyList<LadderRow> channels,
        IEnumerable<LadderRow> connections,
        IEnumerable<LadderRow> rpcChannels,
        IEnumerable<LadderRow> httpChannels,
        RankingMetric? byteRanking)
    {
        ArgumentNullException.ThrowIfNull(channels);
        RankedValue? sizeless = byteRanking is { } metric ? new RankedValue(metric, null, 0, 0) : null;
        LadderRow[] others =
        [
            .. connections,
            .. rpcChannels.Select(row => sizeless is null ? row : row with { Ranked = sizeless }),
            .. httpChannels.Select(row => sizeless is null ? row : row with { Ranked = sizeless }),
        ];
        return others.Length == 0 ? channels : LadderProjection.Order([.. channels, .. others]);
    }
}
