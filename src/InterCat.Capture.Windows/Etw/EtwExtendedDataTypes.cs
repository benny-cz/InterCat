namespace InterCat.Capture.Windows;

/// <summary>
/// The <c>EVENT_HEADER_EXT_TYPE_*</c> values InterCat knows, and which of them a metadata-only admission
/// may retain. The split is a policy decision, not a technical limit: every listed item is reachable, and
/// the denied ones are denied because of what they contain, with the refusal counted (section 18.2, R17).
/// </summary>
public static class EtwExtendedDataTypes
{
    public const ushort RelatedActivityId = 0x0001;
    public const ushort Sid = 0x0002;
    public const ushort TerminalServicesId = 0x0003;
    public const ushort InstanceInfo = 0x0004;
    public const ushort StackTrace32 = 0x0005;
    public const ushort StackTrace64 = 0x0006;
    public const ushort PebsIndex = 0x0007;
    public const ushort PmcCounters = 0x0008;
    public const ushort PsmKey = 0x0009;
    public const ushort EventKey = 0x000A;
    public const ushort TraceLoggingSchema = 0x000B;
    public const ushort ProviderTraits = 0x000C;
    public const ushort ProcessStartKey = 0x000D;
    public const ushort ControlGuid = 0x000E;
    public const ushort QpcDelta = 0x000F;
    public const ushort ContainerId = 0x0010;
    public const ushort StackKey32 = 0x0011;
    public const ushort StackKey64 = 0x0012;

    /// <summary>
    /// Correlation and lifecycle metadata a metadata-only admission retains. <c>ProcessStartKey</c> is
    /// here because ADR-005 gives a start key precedence over a reusable PID, and <c>QpcDelta</c> because
    /// ADR-006 keeps native clock evidence rather than a converted value.
    /// </summary>
    public static IReadOnlySet<ushort> PermittedMetadata { get; } = new HashSet<ushort>
    {
        RelatedActivityId,
        TerminalServicesId,
        InstanceInfo,
        StackTrace32,
        StackTrace64,
        PebsIndex,
        PmcCounters,
        PsmKey,
        EventKey,
        ProcessStartKey,
        ControlGuid,
        QpcDelta,
        ContainerId,
        StackKey32,
        StackKey64,
    };

    /// <summary>
    /// Items a metadata-only admission refuses. A SID identifies a person, and a TraceLogging schema item
    /// carries provider-authored field names that are not a category InterCat can vouch for. Each refusal
    /// is a counted policy omission, never an unexplained parser gap (section 11.1, I13).
    /// </summary>
    public static IReadOnlySet<ushort> DeniedByMetadataPolicy { get; } = new HashSet<ushort>
    {
        Sid,
        TraceLoggingSchema,
        ProviderTraits,
    };

    /// <summary>A readable name for evidence and diagnostics; an unknown type keeps its numeric identity.</summary>
    public static string Describe(ushort type) => type switch
    {
        RelatedActivityId => "related-activity-id",
        Sid => "sid",
        TerminalServicesId => "terminal-services-id",
        InstanceInfo => "instance-info",
        StackTrace32 => "stack-trace-32",
        StackTrace64 => "stack-trace-64",
        PebsIndex => "pebs-index",
        PmcCounters => "pmc-counters",
        PsmKey => "psm-key",
        EventKey => "event-key",
        TraceLoggingSchema => "tracelogging-schema",
        ProviderTraits => "provider-traits",
        ProcessStartKey => "process-start-key",
        ControlGuid => "control-guid",
        QpcDelta => "qpc-delta",
        ContainerId => "container-id",
        StackKey32 => "stack-key-32",
        StackKey64 => "stack-key-64",
        _ => $"unknown-0x{type:x4}",
    };
}
