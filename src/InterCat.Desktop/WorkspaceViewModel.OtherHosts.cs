using System.Globalization;
using InterCat.Application;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// The graph's Other hosts node and its edges (§6.3's unresolved resources, which remain visible): where the drawn
/// processes' connections ran with no process of the capture at their other end. Their cards and the node's description
/// say what they are drawn from and what is not observed (P7); a click on an edge chooses its process, and a double click
/// opens it, whose rung lists each connection.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    /// <summary>The host links a drawn edge to the other hosts stands for, by their keys.</summary>
    private HostLink[] HostLinksOf(GraphDisplayEdge edge)
    {
        HashSet<string> keys = new(edge.Relationships, StringComparer.Ordinal);
        return [.. wholeSnapshot.HostLinks.Where(link => keys.Contains(link.Key))];
    }

    /// <summary>Every host link a drawing draws: those of its edges to the other hosts.</summary>
    private HostLink[] DrawnHostLinks(GraphDisplay drawn)
    {
        HashSet<string> keys = new(drawn.Edges.Where(edge => edge.ToHosts).SelectMany(edge => edge.Relationships),
            StringComparer.Ordinal);
        return [.. wholeSnapshot.HostLinks.Where(link => keys.Contains(link.Key))];
    }

    /// <summary>
    /// A drawn node's processes' connections to other hosts, for its card: "Other hosts: 42 TCP connections to 17 hosts".
    /// Null when none of them has any, and for a context node, whose processes' connections are left to their own rungs.
    /// </summary>
    private string? HostReachLine(GraphDisplayNode node)
    {
        if (node.Kind == GraphNodeKind.Context)
        {
            return null;
        }

        HashSet<ProcessInstanceId> members = [.. node.Members];
        HostLink[] links = [.. wholeSnapshot.HostLinks.Where(link => members.Contains(link.Process))];
        return links.Length == 0 ? null : "Other hosts: " + HostLinkText.Line(links);
    }

    /// <summary>
    /// The scope connections to other hosts are counted over: always the whole session, which a brush does not re-count,
    /// since they are drawn with no traffic value to re-count.
    /// </summary>
    private string HostScope => "Scope: " + TimeBase.HalfOpenRange(wholeSnapshot.Extent, CultureInfo.CurrentCulture)
        + " · whole session" + (appliedInterval is not null ? "; the brushed interval does not re-count it" : string.Empty);

    /// <summary>The Other hosts node's card: whose connections it stands for, transport by transport, and what is not observed.</summary>
    private HoverCard DescribeOtherHostsHover(GraphDisplayNode hub, GraphDisplay drawn)
    {
        HostLink[] links = DrawnHostLinks(drawn);
        var lines = new List<string>
        {
            "Connections of " + Counted(hub.ConnectedProcesses, "drawn process", "drawn processes")
                + " with no process of this capture at their other end, most often another machine's",
        };
        foreach (IGrouping<Mechanism, HostLink> transport in links.GroupBy(link => link.Mechanism).OrderBy(group => group.Key))
        {
            lines.Add(HostLinkText.Reach(transport.Key, [.. transport]) + " · "
                + CountText.Of(transport.Sum(link => link.Records), "record"));
        }

        lines.Add(HostScope);
        lines.Add("Who is at their other ends is not observed; each process's rung lists its connections and their addresses");
        lines.Add("Edges: connections, not relationships, and counted in no total; drawn at one thickness, with no traffic value");
        lines.Add("Size: fixed; a resource, not a process");
        return new(hub.Label, lines);
    }

    /// <summary>
    /// An edge to the other hosts' card: its source's connections over its transport, how strongly their records bind to
    /// it, and what a double click opens.
    /// </summary>
    private HoverCard DescribeHostEdgeHover(GraphDisplayEdge edge, GraphDisplay drawn)
    {
        HostLink[] links = HostLinksOf(edge);
        GraphDisplayNode? source = drawn.Node(edge.SourceKey);
        var lines = new List<string>
        {
            (links is [{ } only] ? HostLinkText.Reach(only) : HostLinkText.Reach(edge.Mechanism, links))
                + " · " + DescribeStrength(edge.Strength) + " evidence",
            string.Create(CultureInfo.CurrentCulture, $"Records: {edge.ObservationCount:N0}"),
            HostScope,
            "Who is at their other ends is not observed; " + (links.Length == 1
                ? "its process's rung lists each connection and its addresses"
                : "each process's rung lists its own"),
            "Thickness: the same for every connection to other hosts, which carries no traffic value in the graph",
        };

        // What OpenGraphEdge will do, so the gesture is discoverable where the edge is read.
        if (source is { Kind: GraphNodeKind.Group } && ladder.Current.Level == DetailLevel.Machine)
        {
            lines.Add("Click chooses the group; double-click opens it");
        }
        else if (source is not null && OpensProcess(source))
        {
            lines.Add("Click chooses its process; double-click opens it");
        }
        else if (source is { Kind: GraphNodeKind.OtherHostsOnly })
        {
            lines.Add("Click chooses its processes; the ranked table lists each, and each one's rung its connections");
        }

        return new($"{source?.Title ?? edge.SourceKey} → {drawn.Node(edge.TargetKey)?.Label ?? edge.TargetKey}", lines);
    }

    /// <summary>The Other hosts node, selected: whose connections it stands for and where each one is listed.</summary>
    private string DescribeOtherHosts(GraphDisplayNode hub)
    {
        IReadOnlyList<string> parts = HostLinkText.Parts(DrawnHostLinks(graphDisplay));
        string reach = parts.Count switch
        {
            0 => string.Empty,
            1 => ": " + parts[0],
            _ => ": " + string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
        };
        return "Where the connections of " + Counted(hub.ConnectedProcesses, "drawn process", "drawn processes")
            + " ran with no process of this capture at their other end, most often another machine" + reach + ". Who is "
            + "at their other ends is not observed; each process's rung lists its connections and their addresses. Its "
            + "edges are connections, not relationships, and carry no traffic value.";
    }

    /// <summary>
    /// What else the No relationships node's processes lack where the session's connections to other hosts are drawn: a
    /// process with one is drawn on its own, edged to Other hosts, so the node holds only those with neither.
    /// </summary>
    private string NoHostConnection => wholeSnapshot.HostLinks.Count > 0 ? " and no connection to other hosts" : string.Empty;

    /// <summary>
    /// The graph summary's note of the drawn processes whose connections run to the other hosts: " · 23 connect to other
    /// hosts". Empty when the graph draws none.
    /// </summary>
    private string OtherHostsNote => graphDisplay.OtherHosts is { ConnectedProcesses: > 0 and var connected }
        ? string.Create(CultureInfo.CurrentCulture,
            $" · {connected:N0} {CountText.Agree(connected, "connects", "connect")} to other hosts")
        : string.Empty;
}
