using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>What a search hit is: an executable group, a process instance or a channel.</summary>
public enum SearchHitKind
{
    Group = 1,
    Process = 2,
    Channel = 3,

    /// <summary>A moment of the session the search's text names, which going to chooses the cell of (§6.2, §6.7).</summary>
    Moment = 4,

    /// <summary>The port a bare number names, which going to searches as a port, ":5353", in the number's place (§6.7).</summary>
    Port = 5,

    /// <summary>
    /// A program a search that found nothing was a letter or two from, "chrome.exe" for "chrme", which going to searches in
    /// the search's place (R18).
    /// </summary>
    NearProgram = 6,
}

/// <summary>
/// One entity a search found, and the ladder path that reaches it: the row keys to descend from the machine rung, the
/// same rows a person would choose one rung at a time (§3.2), so the breadcrumb states every rung and Esc climbs each.
/// </summary>
public sealed record SearchHit(
    SearchHitKind Kind,
    string Key,
    string Label,
    string Detail,
    long ObservationCount,
    IReadOnlyList<string> Path);

/// <summary>A search's hits in rank order, at most the limit asked for, and how many matched in all.</summary>
public sealed record SearchResult(string Query, IReadOnlyList<SearchHit> Hits, int Matched);

/// <summary>
/// §6.7's search (Ctrl+F) over one snapshot's metadata: executable groups by name or image path, process instances by
/// name, PID or an address their connections to other hosts ran to (§6.3) - with its port, or a port alone, which also
/// finds those that accepted connections at that port of their own, whoever connected - and channels by endpoint. It
/// reads no payload and no source field beyond what the ladder already shows (§10, P16), and it is bounded: a result
/// states how many matched beyond the hits it lists.
/// </summary>
public static class WorkspaceSearch
{
    public const int DefaultLimit = 50;

    public static SearchResult Find(WorkspaceSnapshot snapshot, string? query, int limit = DefaultLimit)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        string text = query?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return new(text, [], 0);
        }

        Dictionary<ProcessInstanceId, ProcessNode> processes = snapshot.Processes.ToDictionary(process => process.Id);
        Dictionary<string, ProcessGroup> groups = snapshot.Groups.ToDictionary(group => group.Key, StringComparer.Ordinal);
        Dictionary<string, int> memberCounts = snapshot.Processes.GroupBy(process => process.GroupKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        // Hits that match equally well are ordered by the records the ranked table counts for them, the busier first: a
        // process's own records and a group's members' (process-activity-v1).
        var groupObservations = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (ProcessNode process in snapshot.Processes)
        {
            groupObservations[process.GroupKey] = groupObservations.GetValueOrDefault(process.GroupKey) + process.Records;
        }

        int? pid = PidIn(text);
        ILookup<ProcessInstanceId, HostLink> reached = snapshot.HostLinks.ToLookup(link => link.Process);

        // How many relationships each process is an end of: what, with its connections, tells a program's many instances
        // apart where a name finds them all.
        Dictionary<ProcessInstanceId, int> related = [];
        foreach (CommunicationEdge edge in snapshot.Edges)
        {
            related[edge.SourceId] = related.GetValueOrDefault(edge.SourceId) + 1;
            if (edge.TargetId != edge.SourceId)
            {
                related[edge.TargetId] = related.GetValueOrDefault(edge.TargetId) + 1;
            }
        }
        var candidates = new List<(int Rank, SearchHit Hit)>();
        foreach (ProcessGroup group in snapshot.Groups)
        {
            int rank = Rank(text, group.Name, group.Detail);
            if (rank < 0) continue;
            int members = memberCounts.GetValueOrDefault(group.Key);
            candidates.Add((rank, new(SearchHitKind.Group, group.Key, group.Name,
                string.Create(CultureInfo.CurrentCulture, $"{KindOf(group.Kind)} · {members:N0} {(members == 1 ? "process" : "processes")}")
                    + (group.Detail is { } path ? " · " + path : string.Empty),
                groupObservations.GetValueOrDefault(group.Key), [group.Key])));
        }

        foreach (ProcessNode process in snapshot.Processes)
        {
            int byName = RankOf(process, text, pid);
            (int byAddress, string? reach) = AddressRankOf(reached[process.Id], text);
            int byListening = ListeningRankOf(process, text);
            if (byName < 0 && byAddress < 0 && byListening < 0) continue;
            string groupName = groups.TryGetValue(process.GroupKey, out ProcessGroup? group) ? group.Name : process.GroupKey;

            // Found by an address its connections ran to, a process says what reached it, never the address (P16): the
            // person typed it, and the process's rung lists each connection with its endpoints. Found by its name, it says
            // what tells it apart from its program's other instances. Its program is said only where its name does not
            // say it: two of one name in different folders, or one whose executable was never witnessed.
            // Found by a port of its own it accepted connections at, where no other host's connection names the port, it
            // says so: those connections may be its own machine's processes', which no host link holds. A port alone names no
            // process by its name or PID.
            bool byReach = byAddress >= 0 && (byName < 0 || byAddress < byName);
            bool byListen = byListening >= 0;
            string apart = byReach ? reach!
                : byListen ? ListenedText
                : Apart(process, related.GetValueOrDefault(process.Id), snapshot.ConnectionOpensRecorded);
            int rank = byReach ? byAddress : byListen ? byListening : byName;
            candidates.Add((rank, new(SearchHitKind.Process, process.Id.ToString(), process.NameWithPid,
                string.Equals(groupName, process.Name, StringComparison.OrdinalIgnoreCase)
                    ? char.ToUpperInvariant(apart[0]) + apart[1..]
                    : $"Process instance of {groupName} · {apart}",
                process.Records,
                [process.GroupKey, process.Id.ToString()])));
        }

        Dictionary<string, CommunicationEdge> edges = snapshot.Edges.ToDictionary(edge => edge.Key, StringComparer.Ordinal);
        foreach (Channel channel in snapshot.Channels)
        {
            int rank = Rank(text, channel.Name, null);
            if (rank < 0
                || !edges.TryGetValue(channel.EdgeKey, out CommunicationEdge? edge)
                || !processes.TryGetValue(edge.SourceId, out ProcessNode? source))
            {
                continue;
            }

            // The channel is reached through its relationship's source process, as a double click on its edge reaches it,
            // and named by the processes at its ends, who connected to whom, as the channel list names it - where it holds
            // them - else by its relationship's.
            string ends = channel.FirstHolder is not null && channel.SecondHolder is not null
                ? ChannelNames.Processes(channel, id => processes.GetValueOrDefault(id)?.NameWithPid)
                : processes.TryGetValue(edge.TargetId, out ProcessNode? target)
                    ? $"{source.NameWithPid} and {target.NameWithPid}"
                    : source.NameWithPid;
            // Endpoint strings can be matched, but never copied into a search snippet (P16). The channel view
            // reveals the endpoint in context after the person opens this hit.
            candidates.Add((rank, new(SearchHitKind.Channel, channel.Key, ends,
                MechanismText.Name(channel.Mechanism) + " channel whose endpoint the search names",
                channel.ObservationCount, [source.GroupKey, source.Id.ToString(), channel.Key])));
        }

        SearchHit[] ranked = [.. candidates
            .OrderBy(candidate => candidate.Rank)
            .ThenBy(candidate => candidate.Hit.Kind)
            .ThenByDescending(candidate => candidate.Hit.ObservationCount)
            .ThenBy(candidate => candidate.Hit.Label, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(candidate => candidate.Hit.Key, StringComparer.Ordinal)
            .Select(candidate => candidate.Hit)
            .Take(limit)];
        return new(text, ranked, candidates.Count);
    }

    /// <summary>
    /// What tells a process apart from its program's other instances in a hit: the relationships it is an end of, and the
    /// TCP connections it made and accepted where the session records any connection's open, each said where there are
    /// some - "6 relationships · 3 connections made, 2 accepted", "11 relationships · 11 connections accepted", "no
    /// relationship".
    /// </summary>
    private static string Apart(ProcessNode process, int relationships, bool opensRecorded)
    {
        string related = relationships == 0 ? "no relationship" : Count(relationships, "relationship", "relationships");
        (long made, long accepted) = opensRecorded ? (process.ConnectionsMade, process.ConnectionsAccepted) : (0, 0);
        string? connections = (made > 0, accepted > 0) switch
        {
            (true, true) => Count(made, "connection", "connections") + string.Create(CultureInfo.CurrentCulture, $" made, {accepted:N0} accepted"),
            (true, false) => Count(made, "connection", "connections") + " made",
            (false, true) => Count(accepted, "connection", "connections") + " accepted",
            _ => null,
        };
        return connections is null ? related : related + " · " + connections;

        static string Count(long count, string one, string many) =>
            string.Create(CultureInfo.CurrentCulture, $"{count:N0} {(count == 1 ? one : many)}");
    }

    /// <summary>
    /// Whether <paramref name="query"/> finds <paramref name="process"/>, by the rule <see cref="Find"/> ranks a process's
    /// hit by - its PID, or a PID's first digits, its name, an address its connections to other hosts,
    /// <paramref name="links"/>, ran to, or a port of its own it accepted connections at - with no limit on how many it
    /// finds: a group's lanes are marked by it (§6.2: search lane names), however many of them a search's listed hits leave
    /// out.
    /// </summary>
    public static bool Finds(ProcessNode process, IEnumerable<HostLink> links, string? query)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(links);
        string text = query?.Trim() ?? string.Empty;
        return text.Length > 0
            && (RankOf(process, text, PidIn(text)) >= 0 || AddressRankOf(links, text).Rank >= 0 || ListeningRankOf(process, text) >= 0);
    }

    /// <summary>What a process's hit says where a port of its own it accepted connections at found it.</summary>
    private const string ListenedText = "TCP connections accepted at the port searched";

    /// <summary>
    /// How well a process matches a port searched alone by the ports of its own it accepted connections at, whoever
    /// connected (<see cref="ProcessNode.AcceptedPorts"/>): 1, as broad as a port its connections to other hosts ran to,
    /// where it accepted some at that port; -1 otherwise, and for any query but a port alone, since an endpoint names the
    /// address at the other end.
    /// </summary>
    private static int ListeningRankOf(ProcessNode process, string text) =>
        HostQueryOf(text) is { Address: null, Port: { } port } && process.AcceptedPorts.Contains(port) ? 1 : -1;

    /// <summary>
    /// The programs the words of a search that found nothing were a letter or two from (R18): the names of the session's
    /// processes whose executable a record named, as <see cref="NameText.NearestPrograms"/> measures them, the busiest
    /// first; none where the search found something.
    /// </summary>
    public static string[] NearPrograms(WorkspaceSnapshot snapshot, SearchResult result)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(result);
        return result.Matched > 0 ? [] : NameText.NearestPrograms(result.Query, snapshot.Processes
            .Where(process => process.GroupKey != ProcessGroup.UnwitnessedExecutableKey)
            .OrderByDescending(process => process.Records)
            .Select(process => process.Name));
    }

    /// <summary>A query that is a number is also a PID.</summary>
    private static int? PidIn(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;

    /// <summary>
    /// How well a process matches: 0 for its exact PID, 1 for a PID that starts with the number, else as its name matches;
    /// -1 for no match.
    /// </summary>
    private static int RankOf(ProcessNode process, string text, int? pid) =>
        pid is { } number && process.ProcessId == number ? 0
        : pid is not null && process.ProcessId.ToString(CultureInfo.InvariantCulture).StartsWith(text, StringComparison.Ordinal) ? 1
        : Rank(text, process.Name, null);

    /// <summary>
    /// Whether <paramref name="query"/> names <paramref name="address"/>, an address connections ran to with the ports
    /// they ran to there, by the rule a process is found by an address it reached (<see cref="Find"/>): the address whole,
    /// or begun up to a dot or colon, ignoring case, and an IPv4-mapped address by its IPv4 address too; the address whole
    /// with a port it was reached at, "142.250.1.1:443" or "[2001:db8::1]:443"; or a port alone, ":443", which also names
    /// the address where connections the process accepted from it arrived at that port of its own. Never a query with
    /// neither dot nor colon, which names no address.
    /// </summary>
    public static bool NamesHost(HostAddress address, string? query)
    {
        ArgumentNullException.ThrowIfNull(address);
        return HostQueryOf(query?.Trim() ?? string.Empty) is { } host && HostRank(address, host) >= 0;
    }

    /// <summary>
    /// The port a bare number names: "5353", which a search reads as a PID or a PID's first digits, as ":5353" names a port.
    /// Null for any text but a number from 0 to 65535 of at most five digits.
    /// </summary>
    public static ushort? BarePort(string? query) => PortOf(query?.Trim() ?? string.Empty);

    /// <summary>Whether <paramref name="query"/> asks for a port: an endpoint or a port alone, rather than an address.</summary>
    public static bool NamesPort(string? query) => HostQueryOf(query?.Trim() ?? string.Empty) is { Port: not null };

    /// <summary>
    /// What a query asks of the addresses connections to other hosts ran to: <see cref="Address"/>, the address or its
    /// beginning, null for any; and <see cref="Port"/>, a port it was reached at, or alone also one connections accepted
    /// from it arrived at, null for any. An endpoint names its address whole.
    /// </summary>
    private readonly record struct HostQuery(string? Address, ushort? Port);

    /// <summary>
    /// The address and port a query names: ":443", a port at any address, since no IPv6 address begins with one colon
    /// and a number; "[2001:db8::1]:443", an IPv6 address with a port; "142.250.1.1:443", an IPv4 address with a port
    /// after its last dot and its only colon; else an address or its beginning, when it holds a dot or a colon. Null for
    /// a query that names no address.
    /// </summary>
    private static HostQuery? HostQueryOf(string text)
    {
        if (text.Length > 1 && text[0] == ':' && PortOf(text[1..]) is { } alone)
        {
            return new(null, alone);
        }

        if (text.StartsWith('[') && text.IndexOf("]:", StringComparison.Ordinal) is > 1 and var close
            && PortOf(text[(close + 2)..]) is { } v6)
        {
            return new(text[1..close], v6);
        }

        int colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && colon == text.LastIndexOf(':') && text.LastIndexOf('.') is >= 0 and var dot && dot < colon
            && PortOf(text[(colon + 1)..]) is { } v4)
        {
            return new(text[..colon], v4);
        }

        return text.Contains('.', StringComparison.Ordinal) || text.Contains(':', StringComparison.Ordinal) ? new(text, null) : null;
    }

    /// <summary>A port written as a number from 0 to 65535, with no sign or space.</summary>
    private static ushort? PortOf(string text) =>
        text.Length is > 0 and <= 5 && ushort.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ushort port)
            ? port
            : null;

    /// <summary>
    /// How well an address connections ran to matches a query, as <see cref="AddressRankOf"/> ranks it: an address or its
    /// beginning by <see cref="AddressRank"/>; an endpoint 0 where it names the address whole and one of its ports; a port
    /// alone 1, as broad as an address's beginning, where it is one of the address's ports or one of the process's own
    /// that connections it accepted from the address arrived at; -1 otherwise.
    /// </summary>
    private static int HostRank(HostAddress address, HostQuery query) => query switch
    {
        { Address: null, Port: { } port } => address.Ports.Contains(port) || address.AcceptedPorts.Contains(port) ? 1 : -1,
        { Address: { } whole, Port: { } port } => AddressRank(address.Address, whole) == 0 && address.Ports.Contains(port) ? 0 : -1,
        { Address: { } text } => AddressRank(address.Address, text),
        _ => -1,
    };

    /// <summary>
    /// Whether <paramref name="query"/> names an address <paramref name="links"/>, connections to other hosts, ran to, by
    /// the rule a process is found by one (<see cref="Find"/>): the Other hosts node that stands for them is found too.
    /// </summary>
    public static bool FindsAddress(IEnumerable<HostLink> links, string? query)
    {
        ArgumentNullException.ThrowIfNull(links);
        string text = query?.Trim() ?? string.Empty;
        return text.Length > 0 && AddressRankOf(links, text).Rank >= 0;
    }

    /// <summary>
    /// How well the addresses a process's connections to other hosts ran to match the query (§6.3): 0 when one is the
    /// query, 1 when one begins with it up to a '.' or ':' - "142.250" begins 142.250.1.1, "142.250.1.1" does not begin
    /// 142.250.1.10 - ignoring case, as IPv6 is written either way, and an IPv4-mapped address matching as its IPv4
    /// address too; 0 when the query is an endpoint, an address whole with a port its connections ran to there; 1 when it
    /// is a port alone that they ran to at any address; -1 when none does, and for a query with neither '.' nor ':', which
    /// names no address: a bare number is a PID, a word a name. With it, what reached the addresses that match best, for
    /// the hit's detail, which never repeats an address or a port (P16): "12 TCP connections and 1 UDP flow to 1 matching
    /// address", or, since an address's counts are not kept port by port, "TCP connections to the endpoint searched".
    /// </summary>
    private static (int Rank, string? Reached) AddressRankOf(IEnumerable<HostLink> links, string text)
    {
        if (HostQueryOf(text) is not { } query)
        {
            return (-1, null);
        }

        int best = -1;
        var matched = new List<(Mechanism Mechanism, HostAddress Address)>();
        foreach (HostLink link in links)
        {
            foreach (HostAddress address in link.Addresses)
            {
                int rank = HostRank(address, query);
                if (rank < 0 || (best >= 0 && rank > best))
                {
                    continue;
                }

                if (best < 0 || rank < best)
                {
                    best = rank;
                    matched.Clear();
                }

                matched.Add((link.Mechanism, address));
            }
        }

        if (best < 0)
        {
            return (-1, null);
        }

        int tcp = matched.Where(match => match.Mechanism != Mechanism.Udp).Sum(match => match.Address.Connections);
        int udp = matched.Where(match => match.Mechanism == Mechanism.Udp).Sum(match => match.Address.Connections);
        int addresses = matched.Select(match => match.Address.Address).Distinct(StringComparer.Ordinal).Count();
        if (query.Port is { } port)
        {
            // An address's connections are counted whatever port each ran to, so those at the port searched are not
            // counted: the hit says over which transports, and to how many addresses - or from how many, where the
            // process accepted them at that port of its own.
            string transports = string.Join(" and ", new[]
            {
                tcp > 0 ? "TCP connections" : null,
                udp > 0 ? "UDP flows" : null,
            }.OfType<string>());
            if (query.Address is not null)
            {
                return (best, transports + " to the endpoint searched");
            }

            string counted = CountText.Of(addresses, "address", "addresses");
            bool ranTo = matched.Any(match => match.Address.Ports.Contains(port));
            bool accepted = matched.Any(match => match.Address.AcceptedPorts.Contains(port));
            return (best, transports + (ranTo && accepted ? " at the port searched, to or from " + counted
                : accepted ? " accepted at the port searched, from " + counted
                : " to " + counted + " at the port searched"));
        }

        string connections = string.Join(" and ", new[]
        {
            tcp > 0 ? HostLinkText.Connections(Mechanism.Tcp, tcp) : null,
            udp > 0 ? HostLinkText.Connections(Mechanism.Udp, udp) : null,
        }.OfType<string>());
        return (best, connections + " to " + CountText.Of(addresses, "matching address", "matching addresses"));
    }

    /// <summary>How well one address matches the query, as <see cref="AddressRankOf"/> ranks it.</summary>
    private static int AddressRank(string address, string text)
    {
        const string Mapped = "::ffff:";
        int rank = Matches(address, text);
        if (rank != 0 && address.StartsWith(Mapped, StringComparison.OrdinalIgnoreCase) && address.Contains('.', StringComparison.Ordinal)
            && Matches(address[Mapped.Length..], text) is >= 0 and var ipv4 && (rank < 0 || ipv4 < rank))
        {
            rank = ipv4;
        }

        return rank;

        static int Matches(string address, string text) =>
            string.Equals(address, text, StringComparison.OrdinalIgnoreCase) ? 0
            : address.Length > text.Length && address.StartsWith(text, StringComparison.OrdinalIgnoreCase)
                && (text[^1] is '.' or ':' || address[text.Length] is '.' or ':') ? 1
            : -1;
    }

    /// <summary>What a group's members have in common, as its hit names it.</summary>
    private static string KindOf(LaneGrouping grouping) => grouping switch
    {
        LaneGrouping.Executable => "Executable",
        LaneGrouping.ServiceContainer => "Service container",
        LaneGrouping.UserSession => "Terminal session",
        LaneGrouping.Package => "Package",
        _ => "Group",
    };

    /// <summary>
    /// How well a name matches, ignoring case: 0 when it is the query, 1 when it starts with it, 2 when it contains it,
    /// 3 when only its detail (an image path) does; -1 for no match.
    /// </summary>
    private static int Rank(string query, string name, string? detail)
    {
        if (NamesExactly(name, query)) return 0;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) return 2;
        return detail is not null && detail.Contains(query, StringComparison.OrdinalIgnoreCase) ? 3 : -1;
    }

    /// <summary>
    /// Whether a name is the query, as its file is named: "chrome" and "chrome.exe" both name "chrome.exe" and the group
    /// labelled "chrome.exe (Google\Chrome\Application)" exactly, while "chrome" only begins "chrome-native-host.exe".
    /// A group's label adds its folders in parentheses when two executables share a file name.
    /// </summary>
    private static bool NamesExactly(string name, string query)
    {
        if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase)) return true;
        int folders = name.IndexOf(" (", StringComparison.Ordinal);
        ReadOnlySpan<char> file = folders < 0 ? name : name.AsSpan(0, folders);
        int extension = file.LastIndexOf('.');
        return file.Equals(query, StringComparison.OrdinalIgnoreCase)
            || (extension > 0 && file[..extension].Equals(query, StringComparison.OrdinalIgnoreCase));
    }
}
