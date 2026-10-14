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
    /// other hosts".
    /// </summary>
    public static string Brief(RelationshipCensus census)
    {
        ArgumentNullException.ThrowIfNull(census);
        TransportCensus tcp = census.Tcp;
        string reason = tcp.Records == 0 ? "no TCP recorded"
            : tcp.WithheldRelationships > 0
                ? CountText.Of(tcp.WithheldRelationships, "TCP relationship") + " left out as "
                    + CountText.Agree(tcp.WithheldRelationships, "a candidate", "candidates")
            : tcp.OtherHostConnections > 0
                ? CountText.Of(tcp.OtherHostConnections, "TCP connection") + " ran to other hosts"
            : tcp.UnseenLocalConnections > 0
                ? CountText.Of(tcp.UnseenLocalConnections, "TCP connection") + " with an unseen other end"
            : tcp.Unpaired > 0 ? "TCP ends could not be paired"
            : "no TCP record names both endpoints";
        return "no relationship drawn · " + reason;
    }

    /// <summary>
    /// The whole reason, where a person reads why the graph is empty: whether any TCP connection had both ends held by the
    /// session's processes, where its TCP records went instead, and what the graph does not draw at all - UDP, and RPC
    /// calls a capture without ALPC cannot join to the process that served them.
    /// </summary>
    public static string Explain(RelationshipCensus census, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(census);
        ArgumentNullException.ThrowIfNull(culture);
        TransportCensus tcp = census.Tcp;
        var sentences = new List<string>();
        if (tcp.Records == 0)
        {
            sentences.Add("This capture recorded no TCP, so no relationship between two of its processes can be drawn.");
        }
        else if (tcp.WithheldRelationships > 0)
        {
            sentences.Add(CountText.Of(tcp.WithheldRelationships, "TCP relationship") + " "
                + CountText.Agree(tcp.WithheldRelationships, "rests", "rest")
                + " on a reused PID's later holder, so the evidence policy leaves "
                + CountText.Agree(tcp.WithheldRelationships, "it", "them") + " out as "
                + CountText.Agree(tcp.WithheldRelationships, "a candidate", "candidates")
                + "; counting candidates draws " + CountText.Agree(tcp.WithheldRelationships, "it", "them") + ".");
        }
        else
        {
            sentences.Add("No TCP connection was seen with a process of this session at each of its two ends.");
        }

        if (tcp.Records > 0 && Parts(tcp) is { Count: > 0 } parts)
        {
            sentences.Add(parts.Count == 1
                ? (tcp.Records == 1 ? "Its 1 TCP record " : string.Create(culture, $"All {tcp.Records:N0} of its TCP records "))
                    + parts[0].Belong + "."
                : "Of its " + CountText.Of(tcp.Records, "TCP record") + ", "
                    + Series([.. parts.Select(part => string.Create(culture, $"{part.Records:N0} {part.Belong}"))]) + ".");
        }

        if (tcp.OtherHostConnections + tcp.UnseenLocalConnections > 0)
        {
            sentences.Add("A process's connections are listed on its rung.");
        }

        if (census.Udp.Records > 0)
        {
            sentences.Add("UDP datagrams are not drawn as relationships; they are in the timeline and on their processes' rungs.");
        }

        if (census.RpcRecords > 0 && !census.RpcLinked)
        {
            sentences.Add("RPC calls are on their processes' rungs: a call is drawn to the process that served it only from a "
                + "capture that collects ALPC, as icat record --profile rpc-peers does.");
        }

        return string.Join(" ", sentences);
    }

    /// <summary>Where a census's TCP records went, outside any drawn relationship, each part with its verb.</summary>
    private static List<(long Records, string Belong)> Parts(TransportCensus tcp)
    {
        var parts = new List<(long, string)>();
        if (tcp.Withheld > 0)
        {
            parts.Add((tcp.Withheld, (tcp.Withheld == 1 ? "belongs" : "belong") + " to the relationships left out"));
        }

        if (tcp.ToOtherHosts > 0)
        {
            parts.Add((tcp.ToOtherHosts, (tcp.ToOtherHosts == 1 ? "belongs" : "belong") + " to "
                + CountText.Of(tcp.OtherHostConnections, "connection") + " to "
                + CountText.Of(tcp.OtherHosts, "other host")));
        }

        if (tcp.ToUnseenLocalEnds > 0)
        {
            parts.Add((tcp.ToUnseenLocalEnds, (tcp.ToUnseenLocalEnds == 1 ? "belongs" : "belong") + " to "
                + CountText.Of(tcp.UnseenLocalConnections, "connection") + " on this machine whose other end no record holds"));
        }

        if (tcp.Unpaired > 0)
        {
            parts.Add((tcp.Unpaired, (tcp.Unpaired == 1 ? "belongs" : "belong")
                + " to connections that could not be paired with one process at each end"));
        }

        if (tcp.WithoutEndpoints > 0)
        {
            parts.Add((tcp.WithoutEndpoints, (tcp.WithoutEndpoints == 1 ? "names" : "name") + " no complete endpoint"));
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
