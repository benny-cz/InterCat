using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// The honest L0-L3 workspace for one published session generation. A session overview has no logical-operation
/// or exact-record projection yet: those rungs stay empty instead of borrowing the synthetic tour.
/// </summary>
public static class OverviewWorkspace
{
    /// <summary>What every view of a real session's overview must say about what it does and does not show.</summary>
    public const string SessionDisclosure =
        "Graph and channel rungs show admitted paired TCP only; the timeline includes every observed row. "
        + "TCP and UDP records are completed transfers, so this session has no operation rung: source "
        + "records are one step (E) from every rung, and Enter on a record opens its original journal "
        + "entry. Byte previews stay hidden until requested.";

    public static WorkspaceSnapshot Empty() => new(
        "Start exploring", new TimeRange(0, 1), [], [], [], [], [], [], []);

    public static WorkspaceSnapshot From(SessionOverviewBundle overview)
    {
        ArgumentNullException.ThrowIfNull(overview);
        return new WorkspaceSnapshot(
            overview.Redaction is null
                ? $"Session · generation {overview.Generation:N0}"
                : $"Redacted package · generation {overview.Generation:N0}",
            overview.Extent ?? new TimeRange(0, 1),
            overview.Groups,
            overview.Nodes,
            overview.Edges,
            overview.Channels, [], [],
            overview.Timeline,
            overview.ChannelProjectionProblem,
            overview.Minimap,
            overview.Redaction)
        {
            MechanismLanes = overview.MechanismLanes,
            Clock = overview.Clock,
        };
    }

    /// <summary>
    /// What every view of a redacted session package adds to <see cref="SessionDisclosure"/>: its values are pseudonyms
    /// and its records synthetic, so no name, id or address in it is the source machine's.
    /// </summary>
    public const string RedactedDisclosure =
        "This is a redacted session package: names, process and thread IDs, addresses, ports and identifiers are random "
        + "pseudonyms consistent only within it, and each record's original entry is a synthetic metadata record with "
        + "no payload. Times, sizes, counts and relationships are the source's.";

    /// <summary>
    /// The same workspace ranked within an analysis interval: every edge and channel keeps its identity and position and
    /// carries only the records inside the interval, so the ladder's totals and ranking follow the brush while the graph
    /// and timeline keep their shape (plan §6.4). An edge with nothing in the interval counts zero, not "absent".
    /// </summary>
    public static WorkspaceSnapshot WithinInterval(WorkspaceSnapshot snapshot, SessionIntervalCounts counts)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(counts);
        return snapshot with
        {
            Edges = [.. snapshot.Edges.Select(edge => edge with
            {
                ObservationCount = counts.EdgeRecords.GetValueOrDefault(edge.Key),
            })],
            Channels = [.. snapshot.Channels.Select(channel => channel with
            {
                ObservationCount = counts.ChannelRecords.GetValueOrDefault(channel.Key),
            })],
        };
    }
}
