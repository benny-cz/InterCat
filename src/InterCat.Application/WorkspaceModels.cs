using InterCat.Domain;

namespace InterCat.Application;

public sealed record ProcessNode(
    ProcessInstanceId Id,
    int ProcessId,
    string Name,
    string Role,
    double X,
    double Y,
    CoverageState Coverage);

public sealed record CommunicationEdge(
    ProcessInstanceId SourceId,
    ProcessInstanceId TargetId,
    Mechanism Mechanism,
    long ObservationCount,
    long? KnownBytes,
    RelationStrength Strength);

public sealed record TimelineBucket(
    TimeRange Interval,
    int ObservationCount,
    long? KnownBytes,
    Mechanism DominantMechanism,
    CoverageState Coverage);

public sealed record WorkspaceSnapshot(
    string Title,
    TimeRange Extent,
    IReadOnlyList<ProcessNode> Processes,
    IReadOnlyList<CommunicationEdge> Edges,
    IReadOnlyList<TimelineBucket> Timeline);
