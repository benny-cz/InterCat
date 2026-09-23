namespace InterCat.Domain;

/// <summary>What happens when retained content reaches its session byte cap.</summary>
public enum ContentRetentionMode
{
    StopAtLimit = 1,
}

/// <summary>
/// Inspection is a separate consent from collection. Hex/text means inert, bounded previews only; it
/// does not authorize search, decoding, reassembly, export, or active rendering.
/// </summary>
public enum ContentInspectionMode
{
    Disabled = 1,
    HexAndText = 2,
}

/// <summary>
/// A deliberately complete content request. PID and channel values are selectors for a future start
/// attempt, not durable identities; a broker must bind them to observed lifecycle/resource epochs.
/// </summary>
public sealed record ContentCaptureRequest
{
    public required string SourceId { get; init; }
    public required Mechanism Mechanism { get; init; }
    public required IReadOnlyList<int> ProcessIds { get; init; }
    public required IReadOnlyList<string> ChannelSelectors { get; init; }
    public required int MaximumRecordBytes { get; init; }
    public required long MaximumSessionBytes { get; init; }
    public required ContentRetentionMode Retention { get; init; }
    public required ContentInspectionMode Inspection { get; init; }
}

public enum ProviderProcessScope
{
    WholeMachineRequested = 1,
    ProcessFiltered = 2,
    WholeMachineRequiredContext = 3,
    WholeMachineFilterUnavailable = 4,
}
