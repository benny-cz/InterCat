using System.Globalization;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// What an evidence rung reads from a published session: one paired channel, the rows canonically owned by a set of
/// process instances, or every admitted row, optionally within a deliberate time range. A scope that cannot be read
/// states its problem rather than widening itself to the whole session.
/// </summary>
public sealed record EvidenceScope(
    string Description,
    string? ChannelKey,
    IReadOnlyList<ProcessInstanceId> OwnerProcesses,
    TimeRange? Interval,
    string? ContributingEdgeKey,
    string? Problem = null)
{
    public bool IsWholeSession => ChannelKey is null && OwnerProcesses.Count == 0;
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
        string time = interval is { } range
            ? string.Create(CultureInfo.CurrentCulture,
                $" · {range.StartTicks / (decimal)WorkspaceTime.TicksPerSecond:N3} s – "
                + $"{range.EndTicks / (decimal)WorkspaceTime.TicksPerSecond:N3} s")
            : string.Empty;

        for (int index = rung.Filters.Count - 1; index >= 0; index--)
        {
            ImpliedFilter filter = rung.Filters[index];
            switch (filter.Level)
            {
                case DetailLevel.Machine:
                    return Whole(interval, time);
                case DetailLevel.Channel when filter.Key is { } channelKey:
                    Channel? channel = snapshot.Channels.FirstOrDefault(candidate => candidate.Key == channelKey);
                    return new($"Paired TCP channel {channel?.Name ?? filter.Value}{time}", channelKey, [], interval,
                        channel?.EdgeKey);
                case DetailLevel.ProcessInstance when filter.Key is { } processKey:
                    if (!Guid.TryParse(processKey, out Guid id) || id == Guid.Empty)
                        return Unreadable($"The process filter '{filter.Value}' names no process instance.", interval);
                    ProcessNode? process = snapshot.Processes.FirstOrDefault(node => node.Id.Value == id);
                    string name = process is null
                        ? filter.Value
                        : string.Create(CultureInfo.CurrentCulture, $"{process.Name} · PID {process.ProcessId}");
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
