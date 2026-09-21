namespace InterCat.Domain;

/// <summary>One of the four separate §18.2 checks, with the reason it did not pass.</summary>
public sealed record CapabilityCheck(
    CapabilityCheckKind Kind,
    CheckOutcome Outcome,
    string Detail,
    string? UnavailableReason = null);

/// <summary>
/// One field a source can expose, with the semantics needed before it may back any metric (R2).
/// <paramref name="Availability"/> uses <c>EN-FieldAvailability</c>; absence is never a zero (R3, R21).
/// </summary>
public sealed record FieldCapability(
    string Name,
    FieldAvailability Availability,
    FieldRole Role,
    string? SchemaType = null,
    MeasurementUnit? Unit = null,
    ByteDomain? ByteDomain = null,
    string? Notes = null);

/// <summary>One event descriptor of a source, with the fields its saved schema declares.</summary>
public sealed record EventCapability(
    int EventId,
    int Version,
    int Opcode,
    string? TaskName,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<FieldCapability> Fields);

/// <summary>
/// The exact enablement a capture would request (§18.2). Keywords are hexadecimal strings so that
/// 64-bit masks survive JSON interchange unchanged (§20.5).
/// </summary>
public sealed record EnablementPlan(
    string Level,
    string MatchAnyKeyword,
    string MatchAllKeyword,
    IReadOnlyList<string> RequestedKeywords,
    bool SupportsCaptureSideProcessFilter,
    string FilteringNotes);

/// <summary>Identity of a source's saved schema. A differing fingerprint invalidates decode (§24).</summary>
public sealed record SchemaDescriptor(
    string ProviderName,
    string SchemaFingerprint,
    int DeclaredEventCount,
    IReadOnlyList<string> EventVersions);

/// <summary>
/// The §4.3 source capability descriptor. Every field an adapter cannot prove is stated as unknown
/// or unavailable with a reason rather than omitted (R21, P1).
/// </summary>
public sealed record SourceCapability
{
    public required string SourceId { get; init; }
    public required string DisplayName { get; init; }
    public required SourceKind Kind { get; init; }
    public string? ProviderGuid { get; init; }
    public required IReadOnlyList<Mechanism> Mechanisms { get; init; }
    public required CapabilityState State { get; init; }

    /// <summary>Required whenever <see cref="State"/> is not <see cref="CapabilityState.Available"/>.</summary>
    public string? UnavailableReason { get; init; }

    public required PrivilegeRequirement RequiredPrivilege { get; init; }
    public required AdmissionMode Admission { get; init; }
    public EnablementPlan? Enablement { get; init; }
    public SchemaDescriptor? Schema { get; init; }
    public IReadOnlyList<EventCapability> Events { get; init; } = [];
    public required IReadOnlyList<CapabilityCheck> Checks { get; init; }

    /// <summary>Startup, rundown and inventory behaviour, which decides what a mid-run start can miss (§18.5).</summary>
    public required string StartupBehaviour { get; init; }

    public required SourceContractStatus ContractStatus { get; init; }
    public OverheadClass Overhead { get; init; } = OverheadClass.Unmeasured;

    /// <summary>Repository-relative evidence that supports <see cref="Overhead"/>, or null when unmeasured.</summary>
    public string? OverheadEvidence { get; init; }

    public IReadOnlyList<string> FixtureIds { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];

    public CheckOutcome OutcomeOf(CapabilityCheckKind kind)
    {
        foreach (CapabilityCheck check in Checks)
        {
            if (check.Kind == kind)
            {
                return check.Outcome;
            }
        }

        return CheckOutcome.NotAttempted;
    }
}
