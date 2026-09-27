using System.Globalization;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// What an evidence rung reads from a published session: one paired channel, the rows canonically owned by a set of
/// process instances, an RPC channel's or one call's records, or every admitted row, optionally within a deliberate
/// time range. A scope that cannot be read states its problem rather than widening itself to the whole session.
/// </summary>
public sealed record EvidenceScope(
    string Description,
    string? ChannelKey,
    IReadOnlyList<ProcessInstanceId> OwnerProcesses,
    TimeRange? Interval,
    string? ContributingEdgeKey,
    string? Problem = null)
{
    /// <summary>The RPC channel or call whose records the scope reads (<see cref="RpcChannelKeys"/>); null otherwise.</summary>
    public string? RpcKey { get; init; }

    public bool IsWholeSession => ChannelKey is null && OwnerProcesses.Count == 0 && RpcKey is null;
}

/// <summary>
/// An explicit set of process instances as one filter: §6.7's multi-selection, which <c>Enter</c> turns into a filter.
/// Its key names every member, so the scope it reads is exactly the processes chosen and never a group's current
/// membership or a guessed PID.
/// </summary>
public static class ProcessSetFilter
{
    public const string Prefix = "processes:";

    /// <summary>The filter key for these members, in a stable order.</summary>
    public static string KeyOf(IEnumerable<ProcessInstanceId> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        return Prefix + string.Join(",", members.Select(member => member.Value.ToString("N")).Distinct().Order(StringComparer.Ordinal));
    }

    /// <summary>How the set reads in a breadcrumb or a caption.</summary>
    public static string Label(int count) => count == 1
        ? "1 selected process"
        : string.Create(CultureInfo.CurrentCulture, $"{count:N0} selected processes");

    /// <summary>The members a set's key names; false for a key that names no set.</summary>
    public static bool TryParse(string? key, out ProcessInstanceId[] members)
    {
        members = [];
        if (key is null || !key.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parsed = new List<ProcessInstanceId>();
        foreach (string part in key[Prefix.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Guid.TryParseExact(part, "N", out Guid id) || id == Guid.Empty)
            {
                return false;
            }

            parsed.Add(new(id));
        }

        members = [.. parsed];
        return members.Length > 0;
    }
}

/// <summary>
/// Resolves an evidence rung's scope from the filters its breadcrumb shows. The latest filter that names an entity
/// decides: the rung evidence was reached from, or - once the user removes that filter - the next one out. Removing
/// filters therefore widens the scope one visible step at a time, and a rung with none reads the whole session.
/// A group is its member instances in this snapshot, never a guessed PID.
/// </summary>
public static class EvidenceScopes
{
    public static EvidenceScope Resolve(WorkspaceSnapshot snapshot, NavigationState rung)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(rung);
        TimeRange? interval = rung.Viewport == snapshot.Extent ? null : rung.Viewport;
        string time = interval is { } range ? " · " + WorkspaceTime.FormatRange(range, CultureInfo.CurrentCulture) : string.Empty;

        for (int index = rung.Filters.Count - 1; index >= 0; index--)
        {
            ImpliedFilter filter = rung.Filters[index];
            if (ProcessSetFilter.TryParse(filter.Key, out ProcessInstanceId[] chosen))
            {
                // A multi-selection turned into a filter reads exactly its members that this generation still holds.
                ProcessInstanceId[] present = [.. snapshot.Processes.Where(node => chosen.Contains(node.Id)).Select(node => node.Id)];
                return present.Length == 0
                    ? Unreadable("None of the selected processes is in this generation.", interval)
                    : new($"Records owned by {ProcessSetFilter.Label(present.Length)}{time}", null, present, interval, null);
            }

            switch (filter.Level)
            {
                case DetailLevel.Machine:
                    return Whole(interval, time);
                case DetailLevel.Channel when RpcChannelKeys.IsRpc(filter.Key):
                    return new($"Records of {filter.Value}{time}", null, [], interval, null) { RpcKey = filter.Key };
                case DetailLevel.Operation when RpcChannelKeys.IsRpc(filter.Key):
                    // A call is one entity: its records are its start and stop wherever the view is zoomed.
                    return new($"Records of {filter.Value}", null, [], null, null) { RpcKey = filter.Key };
                case DetailLevel.Channel when filter.Key is { } channelKey:
                    Channel? channel = snapshot.Channels.FirstOrDefault(candidate => candidate.Key == channelKey);
                    return new($"Paired TCP channel {channel?.Name ?? filter.Value}{time}", channelKey, [], interval,
                        channel?.EdgeKey);
                case DetailLevel.ProcessInstance when filter.Key is { } processKey:
                    if (!Guid.TryParse(processKey, out Guid id) || id == Guid.Empty)
                        return Unreadable($"The process filter '{filter.Value}' names no process instance.", interval);
                    ProcessNode? process = snapshot.Processes.FirstOrDefault(node => node.Id.Value == id);
                    string name = process is null ? filter.Value : process.NameWithPid;
                    return new($"Records owned by {name}{time}", null, [new(id)], interval, null);
                case DetailLevel.Group when filter.Key is { } groupKey:
                    ProcessInstanceId[] members = [.. snapshot.Processes
                        .Where(node => string.Equals(node.GroupKey, groupKey, StringComparison.Ordinal))
                        .Select(node => node.Id)];
                    return members.Length == 0
                        ? Unreadable($"The group '{filter.Value}' has no process instance in this generation.", interval)
                        : new(string.Create(CultureInfo.CurrentCulture,
                                $"Records owned by the {members.Length:N0} "
                                + $"{(members.Length == 1 ? "instance" : "instances")} of {filter.Value}{time}"),
                            null, members, interval, null);
                case DetailLevel.Operation:
                    return Unreadable("This session projects no logical operations, so an operation cannot scope its "
                        + "records. Remove the operation filter to read the channel's records.", interval);
            }
        }

        return Whole(interval, time);
    }

    private static EvidenceScope Whole(TimeRange? interval, string time) =>
        new($"Every admitted record in this session{time}", null, [], interval, null);

    private static EvidenceScope Unreadable(string problem, TimeRange? interval) =>
        new("No readable scope", null, [], interval, null, problem);
}
