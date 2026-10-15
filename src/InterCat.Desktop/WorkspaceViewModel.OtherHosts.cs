using System.Globalization;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// The graph's Other hosts node and its edges (§6.3's unresolved resources, which remain visible): where the drawn
/// processes' connections ran with no process of the capture at their other end. Their cards and the node's description
/// say what they are drawn from, name the busiest of the addresses they ran to and say what is not observed: who is at
/// them (P7). A click on an edge chooses its process, and a double click opens it, whose rung lists each connection.
/// Enter or a double click on Other hosts opens it into its busiest hosts, each drawn on its own and named by its
/// address (§6.3's explicit expansion), and on any of them folds them back.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    private bool hostsOpened;

    /// <summary>
    /// Whether Other hosts is opened: its busiest hosts drawn on their own, as the graph's budget allows, and the rest kept
    /// in it. A newer publication of the session keeps it, and the investigation a session was opened from keeps it as a
    /// view setting (§26.3), which the rail names while the session's processes reached another host.
    /// </summary>
    public bool HostsOpened
    {
        get => hostsOpened;
        set
        {
            if (hostsOpened == value)
            {
                return;
            }

            hostsOpened = value;
            RefreshGraphDisplay(reproject: true);
            OnPropertyChanged();
            RaiseSettingsChanged();
        }
    }

    /// <summary>
    /// Whether Other hosts opened is a setting the view shows: it is, and the session's processes shown reached another
    /// host, so its graph draws Other hosts to open. Kept while none did, it stays kept, for a view that shows one again.
    /// </summary>
    private bool ShowsHostsOpened => hostsOpened && wholeSnapshot.HostLinks.Count > 0;

    /// <summary>
    /// The connections a drawn edge to the other hosts stands for, each with only the addresses it is drawn to: one host's
    /// for an edge to a host drawn on its own, and the rest for an edge to Other hosts.
    /// </summary>
    private HostLink[] LinksOn(GraphDisplayEdge edge, GraphDisplay drawn) =>
        [.. Restricted(HostLinksOf(edge), drawn, edge.TargetKey)];

    /// <summary>The connections of the drawn processes that Other hosts stands for: those to hosts not drawn on their own.</summary>
    private HostLink[] HubLinks(GraphDisplay drawn) => [.. Restricted(DrawnHostLinks(drawn), drawn, GraphProjection.OtherHostsKey)];

    /// <summary>The connections of the drawn processes that a host drawn on its own stands for.</summary>
    private HostLink[] HostLinksTo(GraphDisplayNode host, GraphDisplay drawn) => [.. Restricted(DrawnHostLinks(drawn), drawn, host.Key)];

    /// <summary>Each link with only the addresses drawn to <paramref name="target"/>, those with none left out.</summary>
    private static IEnumerable<HostLink> Restricted(IEnumerable<HostLink> links, GraphDisplay drawn, string target)
    {
        HashSet<string> opened = new(drawn.OpenedHosts ?? [], StringComparer.Ordinal);
        bool hub = string.Equals(target, GraphProjection.OtherHostsKey, StringComparison.Ordinal);
        foreach (HostLink link in links)
        {
            HostAddress[] addresses = [.. link.Addresses.Where(address => hub
                ? !opened.Contains(address.Address)
                : string.Equals(GraphProjection.HostKey(address.Address), target, StringComparison.Ordinal))];
            if (addresses.Length > 0)
            {
                yield return link with { Addresses = addresses };
            }
        }
    }

    /// <summary>How many hosts the Other hosts node's card names, a line each.</summary>
    private const int HostsOnCard = 3;

    /// <summary>How many hosts the Other hosts node's description names, chosen, where there is room to read more.</summary>
    private const int HostsDescribed = 5;

    /// <summary>How many hosts an edge to the other hosts names on its card, on one line.</summary>
    private const int HostsOnEdgeCard = 3;

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

    /// <summary>
    /// The Other hosts node's card: whose connections it stands for, transport by transport, the busiest hosts they ran to,
    /// a line each, and what is not observed.
    /// </summary>
    private HoverCard DescribeOtherHostsHover(GraphDisplayNode hub, GraphDisplay drawn)
    {
        HostLink[] links = HubLinks(drawn);
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

        // The busiest hosts by the records their connections hold, a line each beneath what heads them; one process's
        // need not say whose they are.
        IReadOnlyList<OtherHost> hosts = OtherHost.Of(links);
        if (hosts.Count > 0)
        {
            lines.Add(hosts.Count <= HostsOnCard
                ? CountText.Agree(hosts.Count, "Host:", "Hosts:")
                : string.Create(CultureInfo.CurrentCulture, $"Busiest {HostsOnCard} of {hosts.Count:N0} hosts:"));
            lines.AddRange(hosts.Take(HostsOnCard).Select(host => "  " + host.Address + " · "
                + HostLinkText.Reached(host, hub.ConnectedProcesses > 1) + " · " + CountText.Of(host.Records, "record")));
        }

        lines.Add(HostScope);
        lines.Add("Who is at these addresses is not observed; searching one (Ctrl+F) finds the processes that reached it");
        lines.Add("Edges: connections, not relationships, and counted in no total; drawn at one thickness, with no traffic value");
        lines.Add("Size: fixed; a resource, not a process");
        lines.Add(drawn.OpenedHosts is { Count: > 0 }
            ? "Double-click or Enter folds the hosts drawn on their own back into it"
            : drawn.OpenedHosts is not null
                ? "Opened, but no host fits the graph's budget on its own; double-click or Enter folds it again"
                : "Double-click or Enter draws its busiest hosts on their own");
        if (SearchedLine(hub) is { } searched)
        {
            lines.Add(searched);
        }

        return new(hub.Label, lines);
    }

    /// <summary>
    /// An edge to the other hosts' card: its source's connections over its transport, how strongly their records bind to
    /// it, and what a double click opens.
    /// </summary>
    private HoverCard DescribeHostEdgeHover(GraphDisplayEdge edge, GraphDisplay drawn)
    {
        HostLink[] links = LinksOn(edge, drawn);
        GraphDisplayNode? source = drawn.Node(edge.SourceKey);
        var lines = new List<string>
        {
            (links is [{ } only] ? HostLinkText.Reach(only) : HostLinkText.Reach(edge.Mechanism, links))
                + " · " + DescribeStrength(edge.Strength) + " evidence",
        };
        if (HostLinkText.Named(OtherHost.Of(links), HostsOnEdgeCard) is { } named)
        {
            lines.Add(named);
        }

        lines.AddRange(
        [
            string.Create(CultureInfo.CurrentCulture, $"Records: {edge.ObservationCount:N0}"),
            HostScope,
            "Who is at these addresses is not observed; " + (links.Length == 1
                ? "its process's rung lists each connection"
                : "each process's rung lists its own connections"),
            "Thickness: the same for every connection to other hosts, which carries no traffic value in the graph",
        ]);

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

    /// <summary>
    /// The Other hosts node, selected: whose connections it stands for, the busiest hosts they ran to and where each
    /// connection is listed.
    /// </summary>
    private string DescribeOtherHosts(GraphDisplayNode hub)
    {
        HostLink[] links = HubLinks(graphDisplay);
        IReadOnlyList<string> parts = HostLinkText.Parts(links);
        string reach = parts.Count == 0 ? string.Empty : ": " + WorkspaceLayout.Series(parts);
        IReadOnlyList<OtherHost> hosts = OtherHost.Of(links);
        string busiest = hosts.Count == 0 ? string.Empty
            : (hosts.Count <= HostsDescribed
                ? " " + CountText.Agree(hosts.Count, "Their host: ", "Their hosts: ")
                : string.Create(CultureInfo.CurrentCulture, $" The busiest of their {hosts.Count:N0} hosts: "))
                + WorkspaceLayout.Series([.. hosts.Take(HostsDescribed).Select(host => host.Address + " ("
                    + HostLinkText.Reached(host, hub.ConnectedProcesses > 1) + ", " + CountText.Of(host.Records, "record") + ")")])
                + ".";
        return "Where the connections of " + Counted(hub.ConnectedProcesses, "drawn process", "drawn processes")
            + " ran with no process of this capture at their other end, most often another machine" + reach + "."
            + busiest + " Who is at these addresses is not observed. Searching one (Ctrl+F) finds the processes that reached "
            + "it, and each process's rung lists its own connections. Its edges are connections, not relationships, and "
            + "carry no traffic value. " + (graphDisplay.OpenedHosts is { Count: > 0 }
                ? "Enter folds the hosts drawn on their own back into it."
                : "Enter draws its busiest hosts on their own.");
    }

    /// <summary>
    /// A host Other hosts was opened into, hovered: whose connections ran to its address, transport by transport, which
    /// processes they are, and what is not observed.
    /// </summary>
    private HoverCard DescribeHostHover(GraphDisplayNode host, GraphDisplay drawn)
    {
        HostLink[] links = HostLinksTo(host, drawn);
        var lines = new List<string>
        {
            "Connections of " + Counted(host.ConnectedProcesses, "drawn process", "drawn processes")
                + " to this address, with no process of this capture at their other end",
        };
        foreach (IGrouping<Mechanism, HostLink> transport in links.GroupBy(link => link.Mechanism).OrderBy(group => group.Key))
        {
            lines.Add(HostLinkText.Connections(transport.Key, transport.Sum(link => (long)link.Connections)) + " from "
                + CountText.Of(transport.Select(link => link.Process).Distinct().Count(), "process", "processes") + " · "
                + CountText.Of(transport.Sum(link => link.Records), "record"));
        }

        lines.Add("Reached by " + ReachedBy(links));
        lines.Add(HostScope);
        lines.Add("Who is at this address is not observed; searching it (Ctrl+F) marks the processes that reached it");
        lines.Add("Edges: connections, not relationships, and counted in no total; drawn at one thickness, with no traffic value");
        lines.Add("Size: fixed; a resource, not a process");
        lines.Add("Double-click or Enter folds the hosts drawn on their own back into Other hosts");
        if (SearchedLine(host) is { } searched)
        {
            lines.Add(searched);
        }

        return new(host.Label, lines);
    }

    /// <summary>A host Other hosts was opened into, selected: whose connections ran to its address and what is not observed.</summary>
    private string DescribeHost(GraphDisplayNode host)
    {
        HostLink[] links = HostLinksTo(host, graphDisplay);
        IReadOnlyList<OtherHost> hosts = OtherHost.Of(links);
        OtherHost? reached = hosts.Count > 0 ? hosts[0] : null;
        return host.Label + ": where the connections of " + Counted(host.ConnectedProcesses, "drawn process", "drawn processes")
            + " ran with no process of this capture at their other end"
            + (reached is null ? string.Empty
                : ": " + HostLinkText.Reached(reached, host.ConnectedProcesses > 1) + ", " + CountText.Of(reached.Records, "record"))
            + ". Reached by " + ReachedBy(links) + ". Who is at this address is not observed; searching it (Ctrl+F) marks the "
            + "processes that reached it, and each process's rung lists its own connections. Enter folds the hosts drawn on "
            + "their own back into Other hosts.";
    }

    /// <summary>The processes whose connections are <paramref name="links"/>, the busiest first: "browser.exe · PID 300 and 2 more".</summary>
    private string ReachedBy(IReadOnlyCollection<HostLink> links)
    {
        Dictionary<ProcessInstanceId, ProcessNode> processes = wholeSnapshot.Processes.ToDictionary(process => process.Id);
        string[] named = [.. links
            .GroupBy(link => link.Process)
            .OrderByDescending(process => process.Sum(link => link.Records))
            .ThenBy(process => process.Key.ToString(), StringComparer.Ordinal)
            .Select(process => processes.TryGetValue(process.Key, out ProcessNode? node) ? node.NameWithPid : process.Key.ToString())];
        return named.Length <= HostsOnEdgeCard
            ? WorkspaceLayout.Series(named)
            : string.Join(", ", named.Take(HostsOnEdgeCard))
                + string.Create(CultureInfo.CurrentCulture, $" and {named.Length - HostsOnEdgeCard:N0} more");
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
    private string OtherHostsNote => DrawnHostLinks(graphDisplay).Select(link => link.Process).Distinct().Count() is > 0 and var connected
        ? string.Create(CultureInfo.CurrentCulture,
            $" · {connected:N0} {CountText.Agree(connected, "connects", "connect")} to other hosts")
        : string.Empty;
}
