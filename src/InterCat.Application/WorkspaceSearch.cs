using System.Globalization;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>What a search hit is: an executable group, a process instance or a channel.</summary>
public enum SearchHitKind
{
    Group = 1,
    Process = 2,
    Channel = 3,
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
/// name or PID, and channels by endpoint. It reads no payload and no source field beyond what the ladder already
/// shows (§10, P16), and it is bounded: a result states how many matched beyond the hits it lists.
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
            int rank = RankOf(process, text, pid);
            if (rank < 0) continue;
            string groupName = groups.TryGetValue(process.GroupKey, out ProcessGroup? group) ? group.Name : process.GroupKey;
            candidates.Add((rank, new(SearchHitKind.Process, process.Id.ToString(), process.NameWithPid,
                $"Process instance of {groupName}",
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

            // The channel is reached through its relationship's source process, as a double click on its edge reaches it.
            string between = processes.TryGetValue(edge.TargetId, out ProcessNode? target)
                ? $"{source.NameWithPid} and {target.NameWithPid}"
                : source.NameWithPid;
            // Endpoint strings can be matched, but never copied into a search snippet (P16). The channel view
            // reveals the endpoint in context after the person opens this hit.
            candidates.Add((rank, new(SearchHitKind.Channel, channel.Key, "Channel", "Channel between " + between,
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
    /// Whether <paramref name="query"/> finds <paramref name="process"/>, by the rule <see cref="Find"/> ranks a process's
    /// hit by - its PID, or a PID's first digits, or its name - with no limit on how many it finds: a group's lanes are
    /// marked by it (§6.2: search lane names), however many of them a search's listed hits leave out.
    /// </summary>
    public static bool Finds(ProcessNode process, string? query)
    {
        ArgumentNullException.ThrowIfNull(process);
        string text = query?.Trim() ?? string.Empty;
        return text.Length > 0 && RankOf(process, text, PidIn(text)) >= 0;
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
