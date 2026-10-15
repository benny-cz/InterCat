using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// Why a session's graph draws the relationships it draws, and none (§6.3, §6.8): what became of its TCP and UDP records
/// under the overview's evidence policy, how many RPC records its processes hold, and whether its capture collected the
/// ALPC that links a call to the process that served it. A real machine's traffic mostly runs to other hosts, so a graph
/// with no relationship is often right; this is what lets it say so rather than look broken.
/// </summary>
public sealed record RelationshipCensus(TransportCensus Tcp, TransportCensus Udp, long RpcRecords, bool RpcLinked);

/// <summary>A <see cref="RelationshipCensus"/> in a person's words: where the window's graph and icat overview draw none.</summary>
public static class RelationshipCensusText
{
    /// <summary>
    /// The reason in a few words, for the graph's one-line summary: "no relationship drawn · 37 TCP connections ran to
    /// other hosts", TCP's when the capture recorded any, else UDP's.
    /// </summary>
    public static string Brief(RelationshipCensus census)
    {
        ArgumentNullException.ThrowIfNull(census);
        return "no relationship drawn · " + (Reason(census.Tcp) ?? Reason(census.Udp) ?? "no TCP or UDP recorded");
    }

    /// <summary>
    /// The whole reason, where a person reads why the graph is empty: whether any TCP connection or UDP flow had both ends
    /// held by the session's processes, where each transport's records went instead, and what the graph does not draw at
    /// all - RPC calls a capture without ALPC cannot join to the process that served them.
    /// </summary>
    public static string Explain(RelationshipCensus census, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(census);
        ArgumentNullException.ThrowIfNull(culture);
        TransportCensus[] recorded = [.. new[] { census.Tcp, census.Udp }.Where(transport => transport.Records > 0)];
        var sentences = new List<string>();
        int withheld = census.Tcp.WithheldRelationships + census.Udp.WithheldRelationships;
        if (recorded.Length == 0)
        {
            sentences.Add("This capture recorded no TCP or UDP, so no relationship between two of its processes can be drawn.");
        }
        else if (withheld > 0)
        {
            sentences.Add(Series([.. recorded
                    .Where(transport => transport.WithheldRelationships > 0)
                    .Select(transport => CountText.Of(transport.WithheldRelationships, Name(transport) + " relationship"))])
                + " " + CountText.Agree(withheld, "rests", "rest")
                + " on a reused PID's later holder, so the evidence policy leaves "
                + CountText.Agree(withheld, "it", "them") + " out as "
                + CountText.Agree(withheld, "a candidate", "candidates")
                + "; counting candidates draws " + CountText.Agree(withheld, "it", "them") + ".");
        }
        else
        {
            sentences.Add("No " + string.Join(" or ", recorded.Select(Channel))
                + " was seen with a process of this session at each of its two ends.");
        }

        foreach (TransportCensus transport in recorded)
        {
            if (Parts(transport) is not { Count: > 0 } parts)
            {
                continue;
            }

            string name = Name(transport);
            sentences.Add(parts.Count == 1
                ? (transport.Records == 1
                    ? $"Its 1 {name} record "
                    : string.Create(culture, $"All {transport.Records:N0} of its {name} records "))
                    + parts[0].Belong + "."
                : "Of its " + CountText.Of(transport.Records, name + " record") + ", "
                    + Series([.. parts.Select(part => string.Create(culture, $"{part.Records:N0} {part.Belong}"))]) + ".");
        }

        if (recorded.Any(transport => transport.OtherHostConnections + transport.UnseenLocalConnections > 0))
        {
            sentences.Add("A process's connections are listed on its rung.");
        }

        if (RpcGuidance(census) is { } guidance)
        {
            sentences.Add(guidance);
        }

        return string.Join(" ", sentences);
    }

    /// <summary>
    /// Where a capture's RPC calls are and how to draw them, for a session that holds RPC calls no record links to the
    /// process that served them, whether or not its graph draws anything else: a capture without ALPC can link none, and
    /// the RPC peers capture collects it. Null when the session holds no RPC call, or its calls were linked.
    /// </summary>
    public static string? RpcGuidance(RelationshipCensus census)
    {
        ArgumentNullException.ThrowIfNull(census);
        return census.RpcRecords > 0 && !census.RpcLinked
            ? "RPC calls are on their processes' rungs: a call is drawn to the process that served it only from a capture "
                + "that collects ALPC - choose RPC peers in the window's Keep, or run icat capture --profile rpc-peers."
            : null;
    }

    /// <summary>Why a transport the capture recorded joins no two of its processes, in a few words; null when it recorded none.</summary>
    private static string? Reason(TransportCensus census) => census.Records == 0 ? null
        : census.WithheldRelationships > 0
            ? CountText.Of(census.WithheldRelationships, Name(census) + " relationship") + " left out as "
                + CountText.Agree(census.WithheldRelationships, "a candidate", "candidates")
        : census.OtherHostConnections > 0
            ? CountText.Of(census.OtherHostConnections, Channel(census)) + " ran to other hosts"
        : census.UnseenLocalConnections > 0
            ? CountText.Of(census.UnseenLocalConnections, Channel(census)) + " with an unseen other end"
        : census.Unpaired > 0 ? Name(census) + " ends could not be paired"
        : "no " + Name(census) + " record names both endpoints";

    /// <summary>A transport's name: "TCP", "UDP".</summary>
    private static string Name(TransportCensus census) => census.Mechanism == Mechanism.Udp ? "UDP" : "TCP";

    /// <summary>What a transport's channel is called: "TCP connection", "UDP flow".</summary>
    private static string Channel(TransportCensus census) => census.Mechanism == Mechanism.Udp ? "UDP flow" : "TCP connection";

    /// <summary>Where a census's records went, outside any drawn relationship, each part with its verb.</summary>
    private static List<(long Records, string Belong)> Parts(TransportCensus census)
    {
        bool udp = census.Mechanism == Mechanism.Udp;
        string channel = udp ? "flow" : "connection";
        var parts = new List<(long, string)>();
        if (census.Withheld > 0)
        {
            parts.Add((census.Withheld, (census.Withheld == 1 ? "belongs" : "belong") + " to the relationships left out"));
        }

        if (census.ToOtherHosts > 0)
        {
            parts.Add((census.ToOtherHosts, (census.ToOtherHosts == 1 ? "belongs" : "belong") + " to "
                + CountText.Of(census.OtherHostConnections, channel) + (udp ? " with " : " to ")
                + CountText.Of(census.OtherHosts, "other host")));
        }

        if (census.ToUnseenLocalEnds > 0)
        {
            parts.Add((census.ToUnseenLocalEnds, (census.ToUnseenLocalEnds == 1 ? "belongs" : "belong") + " to "
                + CountText.Of(census.UnseenLocalConnections, channel) + " on this machine whose other end no record holds"));
        }

        if (census.Unpaired > 0)
        {
            parts.Add((census.Unpaired, (census.Unpaired == 1 ? "belongs" : "belong")
                + " to " + channel + "s that could not be paired with one process at each end"));
        }

        if (census.WithoutEndpoints > 0)
        {
            parts.Add((census.WithoutEndpoints, (census.WithoutEndpoints == 1 ? "names" : "name") + " no complete endpoint"));
        }

        return parts;
    }

    /// <summary>A list as a sentence says it: "a", "a and b", "a, b and c".</summary>
    private static string Series(IReadOnlyList<string> items) => items.Count switch
    {
        0 => string.Empty,
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };
}
