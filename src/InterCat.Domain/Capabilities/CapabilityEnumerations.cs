namespace InterCat.Domain;

/// <summary>
/// How a source describes its events, which selects the decoding path (§18.3).
/// Adapter contract for capability reports; not a §23 durable wire code.
/// </summary>
public enum SourceKind
{
    ManifestProvider = 1,
    ClassicProvider = 2,
    KernelFlagGroup = 3,
    SelfDescribing = 4,
    ApiInventory = 5,
    UnknownSourceKind = 99,
}

/// <summary>
/// The four checks §18.2 requires to stay separate: a registered provider is not an enabled one,
/// an enabled provider is not an observed one, and an observed one is not a validated one.
/// </summary>
public enum CapabilityCheckKind
{
    Registration = 1,
    Enablement = 2,
    ObservedHealth = 3,
    SemanticCoverage = 4,
}

/// <summary>Result of one <see cref="CapabilityCheckKind"/>. Not attempted is never reported as failed.</summary>
public enum CheckOutcome
{
    NotAttempted = 1,
    Passed = 2,
    Failed = 3,
    Inconclusive = 4,
}

/// <summary>
/// What a decoded field can be used for. Drives which §5 metrics a source can ever supply
/// and which attribution a relationship may claim (R2, R4, R22).
/// </summary>
public enum FieldRole
{
    Unclassified = 1,
    ProcessAttribution = 2,
    ThreadAttribution = 3,

    /// <summary>The endpoint the source names as the origin of the record. Which side is local is derived
    /// from direction, never assumed from the field name.</summary>
    SourceEndpoint = 4,

    /// <summary>The endpoint the source names as the destination of the record.</summary>
    DestinationEndpoint = 5,
    ResourceName = 6,
    ResourceHandle = 7,
    CorrelationKey = 8,
    ByteCount = 9,
    Timing = 10,
    Status = 11,
    Content = 12,
}

/// <summary>Privilege a source requires before it can be enabled (§4.3, §9.2).</summary>
public enum PrivilegeRequirement
{
    None = 1,
    PerformanceLogUsers = 2,
    Administrator = 3,
    UnknownPrivilege = 99,
}

/// <summary>Measured overhead class (§4.3). <c>Unmeasured</c> until a benchmark exists (IC-010).</summary>
public enum OverheadClass
{
    Unmeasured = 1,
    Low = 2,
    Moderate = 3,
    High = 4,
}

/// <summary>
/// Whether a source has a documented public contract or is an experimental lead (§4.3).
/// </summary>
public enum SourceContractStatus
{
    Documented = 1,
    Experimental = 2,
    UndocumentedLead = 3,
}
