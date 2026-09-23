using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// An L1 rung: process instances grouped by executable, service container or session (<c>EN-Grouping</c>).
/// </summary>
public sealed record ProcessGroup(
    string Key,
    string Name,
    LaneGrouping Kind);

public sealed record ProcessNode(
    ProcessInstanceId Id,
    int ProcessId,
    string Name,
    string Role,
    string GroupKey,
    double X,
    double Y,
    CoverageState Coverage);

public sealed record CommunicationEdge(
    string Key,
    ProcessInstanceId SourceId,
    ProcessInstanceId TargetId,
    Mechanism Mechanism,
    long ObservationCount,
    long? KnownBytes,
    RelationStrength Strength);

/// <summary>
/// An L3 rung: one channel or endpoint of a relationship. A relationship can carry more than one, which
/// is why descending from an edge is a rung of its own rather than a detail of the edge.
/// </summary>
public sealed record Channel(
    string Key,
    string EdgeKey,
    string Name,
    Mechanism Mechanism,
    Direction Direction,
    long ObservationCount,
    long? KnownBytes,
    CoverageState Coverage);

/// <summary>
/// An L4 rung: one logical operation on a channel. Requested and completed sizes stay separate, and an
/// operation whose completion was never seen keeps its state rather than borrowing a size (P3, P8).
/// </summary>
public sealed record ChannelOperation(
    string Key,
    string ChannelKey,
    ObservationKind Kind,
    Direction Direction,
    TimeRange Interval,
    long? RequestedBytes,
    long? CompletedBytes,
    OperationState State);

/// <summary>
/// An L5 rung: one source observation. It names the provider and descriptor it came from, because the
/// question the product exists to answer is "show me the actual records" (section 3.2).
/// </summary>
public sealed record EvidenceMark(
    string Key,
    string OperationKey,
    long NativeTicks,
    string Provider,
    int EventId,
    string Summary);

public sealed record TimelineBucket(
    TimeRange Interval,
    int ObservationCount,
    long? KnownBytes,
    Mechanism DominantMechanism,
    CoverageState Coverage);

/// <summary>
/// Workspace viewport ticks are 100-nanosecond presentation ticks. A real-session projection converts source-native
/// and session-relative readings into this scale before they reach the graph or timeline; no UI assumes the source
/// clock itself has this frequency.
/// </summary>
public static class WorkspaceTime
{
    public const long TicksPerSecond = TimeSpan.TicksPerSecond;
}

/// <summary>
/// Everything one workspace shows, at every rung of the section 3.2 ladder. The levels are separate
/// collections rather than a nested tree so a projection can be written as a pure query at any rung.
/// </summary>
public sealed record WorkspaceSnapshot(
    string Title,
    TimeRange Extent,
    IReadOnlyList<ProcessGroup> Groups,
    IReadOnlyList<ProcessNode> Processes,
    IReadOnlyList<CommunicationEdge> Edges,
    IReadOnlyList<Channel> Channels,
    IReadOnlyList<ChannelOperation> Operations,
    IReadOnlyList<EvidenceMark> Evidence,
    IReadOnlyList<TimelineBucket> Timeline,
    string? ChannelProjectionProblem = null);
