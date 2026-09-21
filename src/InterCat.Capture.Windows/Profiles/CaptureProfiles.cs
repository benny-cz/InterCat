using InterCat.Domain;

namespace InterCat.Capture.Windows;

public sealed record ProfileSourceRequirement(
    string SourceId,
    bool Required,
    bool RequiresMeasuredOverhead,
    string Purpose);

/// <summary>A discoverable capture intent. Availability is explicit; selecting one never falls back to Explore.</summary>
public sealed record CaptureProfileDescriptor
{
    public required CaptureProfileKind Kind { get; init; }
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Summary { get; init; }
    public required AdmissionMode Admission { get; init; }
    public required bool CompilationAvailable { get; init; }
    public string? UnavailableReason { get; init; }
    public required IReadOnlyList<ProfileSourceRequirement> Sources { get; init; }
    public required bool PreserveExtendedData { get; init; }
    public required bool RequestCallStacks { get; init; }
    public required string CollectionStatement { get; init; }
}

public static class CaptureProfileCatalog
{
    public static IReadOnlyList<CaptureProfileDescriptor> All { get; } =
    [
        new()
        {
            Kind = CaptureProfileKind.Explore,
            Id = "explore",
            DisplayName = "Explore",
            Summary = "Process lifecycle and validated communication metadata for a safe first look.",
            Admission = AdmissionMode.MetadataOnly,
            CompilationAvailable = true,
            Sources =
            [
                new(WindowsSourceCatalog.KernelProcessSourceId, true, true, "Process identity and PID-reuse-safe lifecycle context."),
                new(WindowsSourceCatalog.KernelNetworkSourceId, true, true, "Validated TCP endpoints, direction and transport-observed byte counts."),
                new(WindowsSourceCatalog.RpcSourceId, false, true, "Validated RPC call metadata when its capture impact is known."),
                new(WindowsSourceCatalog.KernelAlpcSourceId, false, true, "ALPC metadata when a bounded adapter and capture impact are known."),
                new(WindowsSourceCatalog.KernelFileSourceId, false, true, "Named-pipe metadata when its whole-machine cost is acceptable."),
            ],
            PreserveExtendedData = true,
            RequestCallStacks = false,
            CollectionStatement =
                "Collects schema-approved lifecycle, endpoint, direction, byte-count and correlation metadata. "
                + "It does not retain event payload bytes, request call stacks, or enable payload-producing debug settings.",
        },
        Unavailable(
            CaptureProfileKind.FocusedTransport,
            "focused-transport",
            "Focused transport",
            AdmissionMode.MetadataOnly,
            "Process and mechanism scope guarantees are not compiled yet; a view filter must not be presented as capture-side filtering."),
        Unavailable(
            CaptureProfileKind.Timing,
            "timing",
            "Timing",
            AdmissionMode.MetadataOnly,
            "Thread scheduling and stack settings still need a measured overhead preview and bounded retention policy."),
        Unavailable(
            CaptureProfileKind.Content,
            "content",
            "Content",
            AdmissionMode.ScopedContent,
            "No payload-capable source has an approved scope, byte budget, retention and inspection contract yet."),
        Unavailable(
            CaptureProfileKind.FlightRecorder,
            "flight-recorder",
            "Flight recorder",
            AdmissionMode.MetadataOnly,
            "Rolling retention, visible oldest-time, pin reservation and export-freeze guarantees are not implemented yet."),
    ];

    public static CaptureProfileDescriptor? Find(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        string normalized = id.Trim().Replace('_', '-').Replace(' ', '-').ToLowerInvariant();
        foreach (CaptureProfileDescriptor profile in All)
        {
            if (string.Equals(profile.Id, normalized, StringComparison.Ordinal))
            {
                return profile;
            }
        }

        return null;
    }

    public static CaptureProfileDescriptor Find(CaptureProfileKind kind) =>
        All.First(profile => profile.Kind == kind);

    private static CaptureProfileDescriptor Unavailable(
        CaptureProfileKind kind,
        string id,
        string displayName,
        AdmissionMode admission,
        string reason) => new()
        {
            Kind = kind,
            Id = id,
            DisplayName = displayName,
            Summary = reason,
            Admission = admission,
            CompilationAvailable = false,
            UnavailableReason = reason,
            Sources = [],
            PreserveExtendedData = false,
            RequestCallStacks = false,
            CollectionStatement = "Starts no capture because this profile cannot yet enforce its stated guarantees.",
        };
}

public sealed record CaptureProfileRequest(
    CaptureProfileKind Profile,
    bool RequestOriginalDiagnosticEtl = false);

public enum ProfileSourceDecisionState
{
    Included = 1,
    Omitted = 2,
    Blocking = 3,
}

public sealed record ProfileSourceDecision(
    string SourceId,
    bool Required,
    ProfileSourceDecisionState State,
    string Reason,
    OverheadClass Overhead,
    string? OverheadEvidence);

/// <summary>
/// Original ETL is separate evidence, never the sanitized journal and never implied by a metadata mode.
/// </summary>
public sealed record OriginalEvidenceDecision(
    bool Requested,
    bool Available,
    bool WillStart,
    string StorageBoundary,
    string Warning);

/// <summary>The immutable requested/effective profile preview compiled before any session is started.</summary>
public sealed record EffectiveCapturePlan
{
    public required DateTimeOffset CompiledAtUtc { get; init; }
    public required ProbeEnvironment Environment { get; init; }
    public required string AdapterVersion { get; init; }
    public required string RequestedProfileId { get; init; }
    public string? EffectiveProfileId { get; init; }
    public required AdmissionMode RequestedAdmission { get; init; }
    public AdmissionMode? EffectiveAdmission { get; init; }
    public CompiledBodyAdmissionPolicy? BodyPolicy { get; init; }
    public required IReadOnlyList<SourceAdmissionPlan> Sources { get; init; }
    public required IReadOnlyList<ProviderEnablementRequest> Providers { get; init; }
    public required IReadOnlyList<ProfileSourceDecision> SourceDecisions { get; init; }
    public required OriginalEvidenceDecision OriginalEvidence { get; init; }
    public required bool PreserveExtendedData { get; init; }
    public required bool RequestCallStacks { get; init; }
    public required bool CanStart { get; init; }
    public required string CollectionStatement { get; init; }
    public required IReadOnlyList<string> Diagnostics { get; init; }
}

public static class CaptureProfileCompiler
{
    public static EffectiveCapturePlan Compile(
        CaptureProfileRequest request,
        CapabilityInventoryProbe inventory,
        ProbeEnvironment? environment = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return CompileCore(
            request,
            (sourceIds, policy) => inventory.CompilePlanSet(sourceIds, policy),
            environment ?? CapabilityInventoryProbe.DescribeEnvironment(),
            (clock ?? TimeProvider.System).GetUtcNow());
    }

    /// <summary>Deterministic entry point for saved-schema fixtures and offline validation.</summary>
    public static EffectiveCapturePlan Compile(
        CaptureProfileRequest request,
        SourcePlanCompilation compilation,
        ProbeEnvironment? environment = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        return CompileCore(
            request,
            (_, _) => compilation,
            environment ?? CapabilityInventoryProbe.DescribeEnvironment(isElevated: false),
            (clock ?? TimeProvider.System).GetUtcNow());
    }

    private static EffectiveCapturePlan CompileCore(
        CaptureProfileRequest request,
        Func<IReadOnlyList<string>, CompiledBodyAdmissionPolicy, SourcePlanCompilation> compileSources,
        ProbeEnvironment environment,
        DateTimeOffset compiledAtUtc)
    {
        CaptureProfileDescriptor profile = CaptureProfileCatalog.Find(request.Profile);
        OriginalEvidenceDecision originalEvidence = OriginalEvidence(request.RequestOriginalDiagnosticEtl);

        if (!profile.CompilationAvailable)
        {
            return Refused(
                profile,
                originalEvidence,
                environment,
                compiledAtUtc,
                profile.UnavailableReason ?? "Profile is unavailable.");
        }

        CompiledBodyAdmissionPolicy bodyPolicy = profile.Admission switch
        {
            AdmissionMode.MetadataOnly => CaptureBodyAdmissionPolicies.MetadataOnly,
            _ => throw new NotSupportedException(
                $"Profile '{profile.Id}' requests {profile.Admission}, which has no production admission compiler."),
        };

        var decisions = new List<ProfileSourceDecision>(profile.Sources.Count);
        var candidates = new List<string>(profile.Sources.Count);
        foreach (ProfileSourceRequirement requirement in profile.Sources)
        {
            WindowsSourceDefinition? definition = WindowsSourceCatalog.Find(requirement.SourceId);
            if (definition is null)
            {
                decisions.Add(Decision(requirement, null, "The source is absent from the adapter catalog."));
                continue;
            }

            if (requirement.RequiresMeasuredOverhead && definition.Overhead == OverheadClass.Unmeasured)
            {
                decisions.Add(Decision(
                    requirement,
                    definition,
                    "Capture impact is unmeasured, so this source is not enabled by the default profile."));
                continue;
            }

            candidates.Add(requirement.SourceId);
        }

        SourcePlanCompilation compilation = compileSources(candidates, bodyPolicy);
        Dictionary<string, SourceAdmissionPlan> plansBySource = compilation.Plans.ToDictionary(
            plan => plan.SourceId,
            StringComparer.Ordinal);
        foreach (ProfileSourceRequirement requirement in profile.Sources)
        {
            if (decisions.Any(decision => string.Equals(decision.SourceId, requirement.SourceId, StringComparison.Ordinal)))
            {
                continue;
            }

            WindowsSourceDefinition? definition = WindowsSourceCatalog.Find(requirement.SourceId);
            if (plansBySource.TryGetValue(requirement.SourceId, out SourceAdmissionPlan? plan)
                && plan.Events.Count > 0)
            {
                decisions.Add(new(
                    requirement.SourceId,
                    requirement.Required,
                    ProfileSourceDecisionState.Included,
                    $"Compiled {plan.Events.Count} admitted descriptor(s): {requirement.Purpose}",
                    definition?.Overhead ?? OverheadClass.Unmeasured,
                    definition?.OverheadEvidence));
                continue;
            }

            string reason = compilation.Issues
                .FirstOrDefault(issue =>
                    issue.Severity == SourcePlanIssueSeverity.Refusal
                    && string.Equals(issue.SourceId, requirement.SourceId, StringComparison.Ordinal))
                ?.Reason
                ?? "No admitted descriptor was compiled for this source.";
            decisions.Add(Decision(requirement, definition, reason));
        }

        IReadOnlyList<ProfileSourceDecision> orderedDecisions =
        [.. profile.Sources.Select(requirement => decisions.Single(decision =>
            string.Equals(decision.SourceId, requirement.SourceId, StringComparison.Ordinal)))];
        IReadOnlyList<SourceAdmissionPlan> included =
        [.. compilation.Plans.Where(plan => orderedDecisions.Any(decision =>
            decision.State == ProfileSourceDecisionState.Included
            && string.Equals(decision.SourceId, plan.SourceId, StringComparison.Ordinal)))];
        bool sourceBlock = orderedDecisions.Any(decision => decision.State == ProfileSourceDecisionState.Blocking);
        bool canStart = !sourceBlock && !request.RequestOriginalDiagnosticEtl;
        var diagnostics = compilation.Issues
            .Where(issue => issue.Severity == SourcePlanIssueSeverity.Warning)
            .Select(issue => $"{issue.SourceId}: {issue.Reason}")
            .ToList();
        if (request.RequestOriginalDiagnosticEtl)
        {
            diagnostics.Add(originalEvidence.Warning);
        }

        return new()
        {
            CompiledAtUtc = compiledAtUtc,
            Environment = environment,
            AdapterVersion = CapabilityInventoryProbe.AdapterVersion,
            RequestedProfileId = profile.Id,
            EffectiveProfileId = canStart ? profile.Id : null,
            RequestedAdmission = profile.Admission,
            EffectiveAdmission = canStart ? profile.Admission : null,
            BodyPolicy = bodyPolicy,
            Sources = included,
            Providers = ProviderEnablementCompiler.Compile(included, profile.RequestCallStacks),
            SourceDecisions = orderedDecisions,
            OriginalEvidence = originalEvidence,
            PreserveExtendedData = profile.PreserveExtendedData,
            RequestCallStacks = profile.RequestCallStacks,
            CanStart = canStart,
            CollectionStatement = profile.CollectionStatement,
            Diagnostics = diagnostics,
        };
    }

    private static ProfileSourceDecision Decision(
        ProfileSourceRequirement requirement,
        WindowsSourceDefinition? definition,
        string reason) => new(
            requirement.SourceId,
            requirement.Required,
            requirement.Required ? ProfileSourceDecisionState.Blocking : ProfileSourceDecisionState.Omitted,
            reason,
            definition?.Overhead ?? OverheadClass.Unmeasured,
            definition?.OverheadEvidence);

    private static OriginalEvidenceDecision OriginalEvidence(bool requested) => new(
        requested,
        Available: false,
        WillStart: false,
        StorageBoundary: "A separate diagnostic ETL file; never merged with or described as the sanitized journal.",
        Warning: requested
            ? "Original diagnostic ETL is not available in the product capture path yet. It may contain payload, identifiers and provider-authored content, so the request blocks capture instead of silently falling back."
            : "No original diagnostic ETL requested; this request asks for no original source-byte retention.");

    private static EffectiveCapturePlan Refused(
        CaptureProfileDescriptor profile,
        OriginalEvidenceDecision originalEvidence,
        ProbeEnvironment environment,
        DateTimeOffset compiledAtUtc,
        string reason) => new()
    {
        CompiledAtUtc = compiledAtUtc,
        Environment = environment,
        AdapterVersion = CapabilityInventoryProbe.AdapterVersion,
        RequestedProfileId = profile.Id,
            EffectiveProfileId = null,
            RequestedAdmission = profile.Admission,
            EffectiveAdmission = null,
            BodyPolicy = null,
            Sources = [],
            Providers = [],
            SourceDecisions = [],
            OriginalEvidence = originalEvidence,
            PreserveExtendedData = false,
            RequestCallStacks = false,
            CanStart = false,
            CollectionStatement = profile.CollectionStatement,
            Diagnostics = [reason],
        };
}
