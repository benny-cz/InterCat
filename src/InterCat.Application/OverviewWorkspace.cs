using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// The honest L0-L3 workspace for one published session generation. A session overview has no logical-operation
/// or exact-record projection yet: those rungs stay empty instead of borrowing the synthetic tour.
/// </summary>
public static class OverviewWorkspace
{
    public static WorkspaceSnapshot Empty() => new(
        "Start exploring", new TimeRange(0, 1), [], [], [], [], [], [], []);

    public static WorkspaceSnapshot From(SessionOverviewBundle overview)
    {
        ArgumentNullException.ThrowIfNull(overview);
        return new(
            $"Session · generation {overview.Generation:N0}",
            overview.Extent ?? new TimeRange(0, 1),
            overview.Groups,
            overview.Nodes,
            overview.Edges,
            overview.Channels, [], [],
            overview.Timeline,
            overview.ChannelProjectionProblem,
            overview.Minimap);
    }

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
