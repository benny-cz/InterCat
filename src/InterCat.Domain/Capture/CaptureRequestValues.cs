namespace InterCat.Domain;

/// <summary>
/// The content sources a person may ask for by name from any client: each one's content contract, process scope and
/// impact are proven (ADR-037). The capture adapter's catalog defines what each admits; a client names it without
/// reaching the adapter, and the broker refuses one it does not hold.
/// </summary>
public static class ContentSources
{
    /// <summary>WinINet's own capture of an HTTP exchange's buffers, for the processes a capture names (ADR-037).</summary>
    public const string WinInetCapture = "etw/manifest/Microsoft-Windows-WinINet-Capture";

    /// <summary>
    /// Whose messages a source keeps, as a review follows a mechanism's messages with it: "WinINet raises (its ID)" for
    /// WinINet's capture, "of" its ID for any other.
    /// </summary>
    public static string Of(string sourceId) => string.Equals(sourceId, WinInetCapture, StringComparison.Ordinal)
        ? $"WinINet raises ({WinInetCapture})"
        : $"of {sourceId}";
}

/// <summary>What happens when retained content reaches its session byte cap.</summary>
public enum ContentRetentionMode
{
    StopAtLimit = 1,
}

/// <summary>
/// Inspection is a separate consent from collection. Hex/text lets a person see a record's bytes when they ask, bounded
/// and inert; joining a part's buffers, copying and saving are each a deliberate action of theirs (content-v1 §4). It
/// does not authorize search, decoding, or active rendering.
/// </summary>
public enum ContentInspectionMode
{
    Disabled = 1,
    HexAndText = 2,
}

/// <summary>
/// A deliberately complete content request. PID values select the processes running when a capture starts, which it
/// holds open while it runs, so no other process can be given their IDs (ADR-037); channel values are selectors a capture
/// must bind to the resources it observes.
/// </summary>
public sealed record ContentCaptureRequest
{
    /// <summary>
    /// The channel selector that names every channel of the named processes. It stands alone, and it is how a request
    /// scopes a source that cannot select channels before anything is kept: the process scope is then the whole scope.
    /// </summary>
    public const string EveryChannel = "*";

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
