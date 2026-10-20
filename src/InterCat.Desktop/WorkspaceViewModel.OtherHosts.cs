using System.Globalization;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// The graph's Other hosts node and its edges (§6.3's unresolved resources, which remain visible): where the drawn
/// processes' connections ran with no process of the capture at their other end. Their cards and the node's description
/// say what they are drawn from, name the busiest of the addresses they ran to and say what is not observed: who is at
/// them (P7). A click on an edge chooses its process, and a double click opens it, whose rung lists each connection.
/// Enter or a double click on Other hosts opens it into its busiest hosts, each drawn on its own and named by its
/// address (§6.3's explicit expansion), and on any of them folds them back. Chosen, it lists in the inspector every host it
/// stands for, the busiest first, and Enter on one there finds the processes that reached it. A host chosen there, or
/// drawn on its own and chosen, has its connections' records highlighted, counted and listed by E
/// (WorkspaceViewModel.HostRecords.cs).
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

    /// <summary>
    /// The most hosts the inspector lists for Other hosts, the busiest; a search for its address finds the processes that
    /// reached any of the rest.
    /// </summary>
    internal const int HostsListed = 100;

    // The hosts the chosen Other hosts node stands for, and who reached each, worked out once for each drawing (R11).
    private (GraphDisplay Display, IReadOnlyList<OtherHost> Hosts, IReadOnlyDictionary<string, string> ReachedBy)? listedHosts;

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

        // The busiest hosts by the records their connections hold, a line each beneath what heads them, each saying who
        // reached it; one process's need not say whose they are.
        IReadOnlyList<OtherHost> hosts = OtherHost.Of(links);
        if (hosts.Count > 0)
        {
            IReadOnlyDictionary<string, string> reachedBy = ReachedByAddress(links);
            lines.Add(hosts.Count <= HostsOnCard
                ? CountText.Agree(hosts.Count, "Host:", "Hosts:")
                : string.Create(CultureInfo.CurrentCulture, $"Busiest {HostsOnCard} of {hosts.Count:N0} hosts:"));
            lines.AddRange(hosts.Take(HostsOnCard).Select(host => "  " + host.Address
                + (AddressScope.Of(host.Address) is { } scope ? " (" + scope + ")" : string.Empty) + " · "
                + ReachedFrom(host, hub.ConnectedProcesses > 1, reachedBy)
                + (HostLinkText.Accepted(host) is { Length: > 0 } accepted ? " · " + accepted : string.Empty)
                + " · " + CountText.Of(host.Records, "record")));
        }

        lines.Add(HostScope);
        lines.Add("Who is at these addresses is not observed; searching one (Ctrl+F) finds the processes that reached it");
        if (CandidatesProposed(hosts) is { } proposed)
        {
            lines.Add("Its investigation's other sessions propose candidate other ends for " + proposed
                + ": processes whose connections mirror these, never an established join; chosen, it lists them");
        }

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
    /// it, who opened them - its processes by a connect, or the hosts by one they accepted - and what a double click opens.
    /// </summary>
    private HoverCard DescribeHostEdgeHover(GraphDisplayEdge edge, GraphDisplay drawn)
    {
        HostLink[] links = LinksOn(edge, drawn);
        GraphDisplayNode? source = drawn.Node(edge.SourceKey);
        string target = drawn.Node(edge.TargetKey)?.Label ?? edge.TargetKey;
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
            DirectionLine(edge, source?.Title ?? edge.SourceKey, target),
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
            lines.Add("Click chooses its processes, which the inspector lists; double-click takes the keyboard there");
        }

        // Its ends joined as a relationship's are: who opened its connections is its direction line's to say.
        return new($"{source?.Title ?? edge.SourceKey} ↔ {target}", lines);
    }

    /// <summary>
    /// The Other hosts node, selected: whose connections it stands for, that the inspector lists their hosts beneath, and
    /// where each connection is listed.
    /// </summary>
    private string DescribeOtherHosts(GraphDisplayNode hub)
    {
        HostLink[] links = HubLinks(graphDisplay);
        IReadOnlyList<string> parts = HostLinkText.Parts(links);
        string reach = parts.Count == 0 ? string.Empty : ": " + WorkspaceLayout.Series(parts);
        IReadOnlyList<OtherHost> hosts = OtherHost.Of(links);
        string listed = hosts.Count switch
        {
            0 => string.Empty,
            1 => "Chosen, its host below has its records highlighted and listed by E; Enter on it finds who reached it. ",
            _ => "A host chosen below has its records highlighted and listed by E; Enter on one finds who reached it. ",
        };
        return "Where the connections of " + Counted(hub.ConnectedProcesses, "drawn process", "drawn processes")
            + " ran with no process of this capture at their other end, most often another machine" + reach + ". Who is at "
            + "these addresses is not observed. " + listed
            + "Its edges are connections, with no traffic value. "
            + (graphDisplay.OpenedHosts is { Count: > 0 }
                ? "Enter folds the hosts drawn on their own back into it."
                : "Enter draws its busiest hosts on their own.");
    }

    /// <summary>
    /// Which of the hosts Other hosts stands for a session opened from its investigation is proposed candidate other ends
    /// for: "its host", or "1 of its 2 hosts"; null where it is proposed none.
    /// </summary>
    private string? CandidatesProposed(IReadOnlyList<OtherHost> hosts)
    {
        int proposed = hosts.Count(host => CandidatesAt(host.Address) is not null);
        return proposed == 0 ? null
            : hosts.Count == 1 ? "its host"
            : string.Create(CultureInfo.CurrentCulture, $"{proposed:N0} of its {hosts.Count:N0} hosts");
    }

    /// <summary>
    /// Every host the chosen Other hosts node stands for, the busiest first - those its drawn processes' connections ran
    /// to, but for any drawn on its own - and none while another node, or none, is chosen.
    /// </summary>
    private IReadOnlyList<OtherHost> ListedHosts()
    {
        if (SelectedCluster is not { Kind: GraphNodeKind.OtherHosts })
        {
            return [];
        }

        if (listedHosts is { } known && ReferenceEquals(known.Display, graphDisplay))
        {
            return known.Hosts;
        }

        HostLink[] links = HubLinks(graphDisplay);
        IReadOnlyList<OtherHost> hosts = OtherHost.Of(links);
        listedHosts = (graphDisplay, hosts, ReachedByAddress(links));
        return hosts;
    }

    /// <summary>Who reached each host <see cref="ListedHosts"/> holds, by its address; none while it holds none.</summary>
    private IReadOnlyDictionary<string, string> ListedReachedBy() =>
        ListedHosts().Count > 0 && listedHosts is { } known ? known.ReachedBy : new Dictionary<string, string>();

    /// <summary>
    /// Who reached each host <paramref name="links"/> ran to, as `icat hosts` names them: the busiest process there and how
    /// many more (<see cref="HostLinkText.ReachedBy"/>), by address.
    /// </summary>
    private Dictionary<string, string> ReachedByAddress(IEnumerable<HostLink> links)
    {
        Dictionary<ProcessInstanceId, ProcessNode> processes = wholeSnapshot.Processes.ToDictionary(process => process.Id);
        return HostLinkText.Reachers(links).ToDictionary(
            host => host.Key,
            host => HostLinkText.ReachedBy(host.Value,
                process => processes.TryGetValue(process, out ProcessNode? node) ? node.NameWithPid : process.ToString()),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What reached a host, as Other hosts' list and card say it: its transports and, where several drawn processes'
    /// connections run to the other hosts, who reached this one - "5 UDP flows from msedge.exe · PID 3400 and 4 more".
    /// </summary>
    private static string ReachedFrom(OtherHost host, bool several, IReadOnlyDictionary<string, string> reachedBy) =>
        several && reachedBy.TryGetValue(host.Address, out string? by) && by.Length > 0
            ? HostLinkText.Reached(host, countsProcesses: false) + " from " + by
            : HostLinkText.Reached(host, several);

    /// <summary>
    /// The hosts the inspector lists while Other hosts is chosen (§6.3), the busiest first and at most
    /// <see cref="HostsListed"/>, each with what reached it and the records their connections hold, so every host it
    /// stands for can be read, and who reached one found by choosing it.
    /// </summary>
    public IReadOnlyList<HostRow> HostRows
    {
        get
        {
            bool several = SelectedCluster is { ConnectedProcesses: > 1 };
            bool updating = StandInHostTraffic is not null;
            IReadOnlyDictionary<string, string> reachedBy = ListedReachedBy();
            return [.. RankedHosts().Take(HostsListed).Select(entry => new HostRow(entry.Host.Address,
                (AddressScope.Of(entry.Host.Address) is { } scope ? scope + " · " : string.Empty)
                    + ReachedFrom(entry.Host, several, reachedBy)
                    + (entry.Host.Ports.Count == 0 ? string.Empty : " · " + HostLinkText.Ports(entry.Host.Ports))
                    + (HostLinkText.Accepted(entry.Host) is { Length: > 0 } accepted ? " · " + accepted : string.Empty)
                    + " · " + CountText.Of(entry.Host.Records, "record")
                    + (entry.Traffic is { } traffic ? " · " + traffic.Transfers(CultureInfo.CurrentCulture) : string.Empty)
                    + (CandidatesAt(entry.Host.Address) is { } candidates
                        ? " · candidate other ends: " + candidates.Words
                        : string.Empty))
            {
                BytesUpdating = updating && entry.Traffic is not null,
            })];
        }
    }

    /// <summary>Whether the inspector lists hosts: Other hosts is chosen, and stands for some.</summary>
    public bool ListsHosts => ListedHosts().Count > 0;

    /// <summary>
    /// The host list's heading: "Its 12 hosts, the busiest first", "Its busiest 100 of 1,234 hosts", or under a byte ranking
    /// "Its 12 hosts, the most bytes sent first".
    /// </summary>
    public string HostsHeading => ListedHosts() switch
    {
        { Count: 0 } => string.Empty,
        { Count: 1 } => "Its host",
        { Count: var count } => HostRankingWords(count),
    };

    /// <summary>
    /// What the host list leaves out, and how to reach it, why its bytes could not be read where they could not, and what
    /// the candidate other ends it names are, beneath the rows that name them; empty when it lists every host as ranked and
    /// names none.
    /// </summary>
    public string HostsNote => string.Join(" ", new[]
    {
        HostTrafficProblem,
        CandidatesProposed(ListedHosts()) is { } proposed
            ? "The candidate other ends named for " + proposed + " are processes of its investigation's other sessions whose "
                + "connections mirror these, never an established join."
            : string.Empty,
        ListedHosts() is { Count: > HostsListed } hosts
            ? string.Create(CultureInfo.CurrentCulture, $"{hosts.Count - HostsListed:N0} more, quieter. Searching an address ")
                + "(Ctrl+F) finds the processes that reached any of them."
            : string.Empty,
    }.Where(part => part.Length > 0));

    /// <summary>Whether the host list says what it leaves out, why its bytes could not be read, or what its candidates are.</summary>
    public bool HasHostsNote => HostsNote.Length > 0;

    /// <summary>
    /// Finds the processes that reached a host the inspector lists, as a search for its address does: the search box takes
    /// the address, the rail lists the processes and the graph marks them, while Other hosts stays chosen. False for a host
    /// the list does not hold.
    /// </summary>
    public bool FindHost(HostRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!HostRows.Any(listed => string.Equals(listed.Address, row.Address, StringComparison.Ordinal)))
        {
            return false;
        }

        SearchText = row.Address;
        return true;
    }

    /// <summary>
    /// Says the host list changed, after the choice or the drawing did. A list given new rows lets its choice go and writes
    /// that back as none chosen; the host chosen is kept by its address and chosen again among them.
    /// </summary>
    private void RaiseHostListChanged()
    {
        FollowHostTraffic();
        restatingHosts = true;
        try
        {
            OnPropertyChanged(nameof(HostRows));
        }
        finally
        {
            restatingHosts = false;
        }

        OnPropertyChanged(nameof(ChosenHostRow));
        OnPropertyChanged(nameof(ListsHosts));
        OnPropertyChanged(nameof(HostsHeading));
        OnPropertyChanged(nameof(HostsNote));
        OnPropertyChanged(nameof(HasHostsNote));
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

        if (OtherHost.Of(links) is [{ Ports.Count: > 0 } reached])
        {
            // Where at the address they ran, never what it serves there.
            string ports = HostLinkText.Ports(reached.Ports);
            lines.Add(char.ToUpperInvariant(ports[0]) + ports[1..]);
        }

        // Those the processes accepted from it, at their own ports, where the address's port was the other end's own.
        if (OtherHost.Of(links) is [{ } from] && HostLinkText.Accepted(from) is { Length: > 0 } accepted)
        {
            lines.Add(char.ToUpperInvariant(accepted[0]) + accepted[1..]);
        }

        if (AddressScope.Meaning(host.Label) is { } meaning)
        {
            lines.Add(meaning);
        }

        lines.Add("Reached by " + ReachedBy(links));
        if (CandidatesAt(host.Label) is { } candidates)
        {
            lines.Add("Candidate other ends: " + candidates.Words + ", never an established join");
        }

        lines.Add(HostScope);
        lines.Add("Who is at this address is not observed; searching it (Ctrl+F) marks the processes that reached it");
        lines.Add("Edges: connections, not relationships, and counted in no total; drawn at one thickness, with no traffic value");
        lines.Add("Size: fixed; a resource, not a process");
        lines.Add("Click highlights the records of every connection to it, whichever process held it, and E lists them");
        lines.Add("Double-click or Enter folds the hosts drawn on their own back into Other hosts");
        if (SearchedLine(host) is { } searched)
        {
            lines.Add(searched);
        }

        return new(host.Label, lines);
    }

    /// <summary>
    /// A host Other hosts was opened into, selected: whose connections ran to its address, what is not observed, and where
    /// its records are read.
    /// </summary>
    private string DescribeHost(GraphDisplayNode host)
    {
        HostLink[] links = HostLinksTo(host, graphDisplay);
        IReadOnlyList<OtherHost> hosts = OtherHost.Of(links);
        OtherHost? reached = hosts.Count > 0 ? hosts[0] : null;
        return "Where the connections of " + Counted(host.ConnectedProcesses, "drawn process", "drawn processes")
            + " ran with no process of this capture at their other end"
            + (reached is null ? string.Empty
                : ": " + HostLinkText.Reached(reached, host.ConnectedProcesses > 1) + ", " + CountText.Of(reached.Records, "record")
                    + (reached.Ports.Count == 0 ? string.Empty : ", at " + HostLinkText.Ports(reached.Ports))
                    + (HostLinkText.Accepted(reached) is { Length: > 0 } accepted ? ", " + accepted : string.Empty))
            + (AddressScope.Meaning(host.Label) is { } meaning ? ". " + meaning : string.Empty)
            + ". Reached by " + ReachedBy(links) + "."
            + (CandidatesAt(host.Label) is { } candidates
                ? " Its candidate other ends in its investigation, never an established join: " + candidates.Words + "."
                : string.Empty)
            + " Who is at it is not observed. Its records, whichever process held them, are "
            + "highlighted and listed by E; searching its address (Ctrl+F) marks who reached it. Enter folds the hosts back "
            + "into Other hosts.";
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
    /// The graph summary's note of the drawn processes whose connections run to the other hosts: " · 23 with connections
    /// to other hosts" - whichever end made them, as the edges' arrowheads say. Empty when the graph draws none.
    /// </summary>
    private string OtherHostsNote => DrawnHostLinks(graphDisplay).Select(link => link.Process).Distinct().Count() is > 0 and var connected
        ? string.Create(CultureInfo.CurrentCulture, $" · {connected:N0} with connections to other hosts")
        : string.Empty;
}
