namespace InterCat.Domain;

/// <summary>Per-mechanism roll-up. A tier is present only when a measurement produced it (§14.2, P27).</summary>
public sealed record MechanismCapability
{
    public required Mechanism Mechanism { get; init; }
    public required CapabilityState State { get; init; }
    public required CapabilityTier Tier { get; init; }
    public required CoverageState Coverage { get; init; }
    public required string Summary { get; init; }
    public required IReadOnlyList<string> SourceIds { get; init; }
    public string? UnavailableReason { get; init; }
    public MechanismMeasurement? Measurement { get; init; }
    public TierAssessment? TierAssessment { get; init; }
    public IReadOnlyList<string> FixtureIds { get; init; } = [];
}

/// <summary>The environment a report was produced on, recorded so a claim can never outlive its build (§13.4).</summary>
public sealed record ProbeEnvironment(
    string OperatingSystem,
    string BuildId,
    string Architecture,
    bool IsSupportedBuild,
    bool IsElevated,
    string MachineScope);

/// <summary>
/// The machine-readable capability report of IC-002 and §21.2.1. <c>ProbeOnly</c> states whether any
/// capture was started; a report produced without capture never carries observed-health results.
/// </summary>
public sealed record CapabilityReport
{
    public required string ReportVersion { get; init; }
    public required DateTimeOffset ProbedAtUtc { get; init; }
    public required ProbeEnvironment Environment { get; init; }
    public required string AdapterVersion { get; init; }
    public required bool ProbeOnly { get; init; }
    public required IReadOnlyList<SourceCapability> Sources { get; init; }
    public required IReadOnlyList<MechanismCapability> Mechanisms { get; init; }
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}
