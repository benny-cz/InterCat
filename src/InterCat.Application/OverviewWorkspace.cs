using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// The honest L0-L2 workspace for one published session generation. A session overview has no channel,
/// operation or record projection yet: those rungs stay empty instead of borrowing the synthetic tour.
/// </summary>
public static class OverviewWorkspace
{
    public static WorkspaceSnapshot Empty() => new(
        "Start exploring", new TimeRange(0, 1), [], [], [], [], [], [], []);

    public static WorkspaceSnapshot From(SessionOverviewBundle overview)
    {
        ArgumentNullException.ThrowIfNull(overview);
        return new(
            $"Live session · generation {overview.Generation:N0}",
            overview.Extent ?? new TimeRange(0, 1),
            overview.Groups,
            overview.Nodes,
            overview.Edges,
            [], [], [],
            overview.Timeline);
    }
}
