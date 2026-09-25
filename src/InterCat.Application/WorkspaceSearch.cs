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

        // Observations on each entity's relationships order hits that match equally well: the busier first.
        var processObservations = new Dictionary<ProcessInstanceId, long>();
        var groupObservations = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (CommunicationEdge edge in snapshot.Edges)
        {
            processObservations[edge.SourceId] = processObservations.GetValueOrDefault(edge.SourceId) + edge.ObservationCount;
            processObservations[edge.TargetId] = processObservations.GetValueOrDefault(edge.TargetId) + edge.ObservationCount;
            foreach (string group in new[] { edge.SourceId, edge.TargetId }
                .Select(id => processes.TryGetValue(id, out ProcessNode? node) ? node.GroupKey : null)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal))
            {
                groupObservations[group] = groupObservations.GetValueOrDefault(group) + edge.ObservationCount;
            }
        }

        // A number is also a PID: an exact PID ranks first, then PIDs that start with it.
        int? pid = int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
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
            string pidText = process.ProcessId.ToString(CultureInfo.InvariantCulture);
            int rank = pid is { } number && process.ProcessId == number ? 0
                : pid is not null && pidText.StartsWith(text, StringComparison.Ordinal) ? 1
                : Rank(text, process.Name, null);
            if (rank < 0) continue;
            string groupName = groups.TryGetValue(process.GroupKey, out ProcessGroup? group) ? group.Name : process.GroupKey;
            candidates.Add((rank, new(SearchHitKind.Process, process.Id.ToString(), process.NameWithPid,
                $"Process instance of {groupName}",
                processObservations.GetValueOrDefault(process.Id),
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

    /// <summary>What a group's members have in common, as its hit names it.</summary>
    private static string KindOf(LaneGrouping grouping) => grouping switch
    {
        LaneGrouping.Executable => "Executable",
        LaneGrouping.ServiceContainer => "Service container",
        LaneGrouping.UserSession => "User session",
        LaneGrouping.Package => "Package",
        _ => "Group",
    };

    /// <summary>
    /// How well a name matches, ignoring case: 0 when it is the query, 1 when it starts with it, 2 when it contains it,
    /// 3 when only its detail (an image path) does; -1 for no match.
    /// </summary>
    private static int Rank(string query, string name, string? detail)
    {
        if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) return 2;
        return detail is not null && detail.Contains(query, StringComparison.OrdinalIgnoreCase) ? 3 : -1;
    }
}
