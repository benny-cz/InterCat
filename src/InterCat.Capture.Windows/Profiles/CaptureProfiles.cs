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
    public required bool RequestPreviewAvailable { get; init; }
    public string? UnavailableReason { get; init; }
    public required IReadOnlyList<ProfileSourceRequirement> Sources { get; init; }
    public required bool PreserveExtendedData { get; init; }
    public required bool RequestCallStacks { get; init; }
    public required string CollectionStatement { get; init; }

    /// <summary>
    /// The profile's own capture cost, measured through the product on a stated workload (ADR-035's fifth decision).
    /// Unmeasured when only its sources' costs are known, which its source decisions state one by one.
    /// </summary>
    public OverheadClass Overhead { get; init; } = OverheadClass.Unmeasured;

    /// <summary>What was measured and on which workload, in words a reader can check against the evidence.</summary>
    public string? OverheadStatement { get; init; }

    /// <summary>The repository file that holds the measurement.</summary>
    public string? OverheadEvidence { get; init; }
}

public static class CaptureProfileCatalog
{
    /// <summary>The most bytes of one fixture message the content fixture keeps.</summary>
    public const int ContentFixtureRecordLimit = 4_096;

    /// <summary>The content the fixture keeps before it stops the capture.</summary>
    public const long ContentFixtureSessionLimit = 16L * 1024 * 1024;

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
            RequestPreviewAvailable = true,
            Sources =
            [
                new(WindowsSourceCatalog.KernelProcessSourceId, true, true, "Process identity and PID-reuse-safe lifecycle context."),
                new(WindowsSourceCatalog.KernelNetworkSourceId, true, true, "Validated TCP and UDP endpoints, direction and transport-observed byte counts."),
                new(WindowsSourceCatalog.RpcSourceId, false, true, "Validated RPC call metadata when its capture impact is known."),

                new(WindowsSourceCatalog.KernelFileSourceId, false, true, "Named-pipe metadata when its whole-machine cost is acceptable."),
            ],
            PreserveExtendedData = true,
            RequestCallStacks = false,
            CollectionStatement =
                "Collects schema-approved lifecycle, endpoint, direction, byte-count and correlation metadata. "
                + "It does not retain event payload bytes, request call stacks, or enable payload-producing debug settings.",
        },
        new()
        {
            Kind = CaptureProfileKind.FocusedTransport,
            Id = "focused-transport",
            DisplayName = "Focused transport",
            Summary = "One validated transport with optional process focus and explicit wider-capture consent.",
            Admission = AdmissionMode.MetadataOnly,
            CompilationAvailable = true,
            RequestPreviewAvailable = true,
            Sources =
            [
                new(WindowsSourceCatalog.KernelProcessSourceId, true, true, "Lifecycle context required to identify selected and peer processes."),
                new(WindowsSourceCatalog.KernelNetworkSourceId, true, true, "Validated endpoints, direction and transport-observed byte counts of the focused transport."),
            ],
            PreserveExtendedData = true,
            RequestCallStacks = false,
            CollectionStatement =
                "Collects schema-approved metadata for one validated transport and the lifecycle context "
                + "needed to explain it. It retains no payload bytes and requests no call stacks.",
        },
        new()
        {
            Kind = CaptureProfileKind.RpcPeers,
            Id = "rpc-peers",
            DisplayName = "RPC peers",
            Summary = "Lifecycle, RPC calls and the kernel's ALPC messages, to find the process that served each local call. "
                + "Opt-in: its private system logger takes one of the eight a machine can run.",
            Admission = AdmissionMode.MetadataOnly,
            CompilationAvailable = true,
            RequestPreviewAvailable = true,
            Sources =
            [
                new(WindowsSourceCatalog.KernelProcessSourceId, true, true, "Process identity and PID-reuse-safe lifecycle context."),
                new(WindowsSourceCatalog.RpcSourceId, true, true, "The RPC calls whose other end ALPC resolves."),
                new(WindowsSourceCatalog.KernelAlpcSourceId, true, true, "ALPC sends and receives, whose message ids link a call to the thread that served it (ADR-034)."),
                new(WindowsSourceCatalog.KernelNetworkSourceId, false, true, "TCP and UDP context beside the calls."),
            ],
            PreserveExtendedData = true,
            RequestCallStacks = false,
            CollectionStatement =
                "Collects Explore's schema-approved metadata and, in a private system logger of its own, each ALPC message's "
                + "id and the thread that sent or received it. It retains no payload bytes and requests no call stacks.",
            Overhead = OverheadClass.Moderate,
            OverheadStatement =
                "its sources measured Moderate at most (Kernel-Process 1.75 CPU pp, ALPC's collection alone 1.86). Through "
                + "the product, on FX-RPC-001's paced RPC workload, it added a median 0.12 pp to Explore's cost (0.21 in a "
                + "first series) and its own process used 0.22 pp, while this workstation's trials without any capture "
                + "ranged from 1.9 to 3.8% busy: the whole profile keeps its sources' class rather than a finer one.",
            OverheadEvidence = "bench/results/rpc-peers-impact-20260928T052021Z/product-impact.json",
        },
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
            "A bounded request compiles only for a source whose content contract, process scope and impact are proven - "
                + "WinINet's HTTP exchanges (ADR-037) - and records through icat record, or through the broker from your own "
                + "processes alone (ADR-049).",
            requestPreviewAvailable: true),
        Unavailable(
            CaptureProfileKind.FlightRecorder,
            "flight-recorder",
            "Flight recorder",
            AdmissionMode.MetadataOnly,
            "Rolling retention, visible oldest-time, pin reservation and export-freeze guarantees are not implemented yet."),
    ];

    /// <summary>
    /// Profiles that record InterCat's own instruments rather than what Windows delivers: the content fixture (ADR-036).
    /// <see cref="Find(string)"/> resolves one, so icat record can start it, but <see cref="All"/> - what the broker
    /// offers and what a machine can be asked to capture - never lists one.
    /// </summary>
    public static IReadOnlyList<CaptureProfileDescriptor> Fixtures { get; } =
    [
        new()
        {
            Kind = CaptureProfileKind.ContentFixture,
            Id = "content-fixture",
            DisplayName = "Content fixture",
            Summary = "Lifecycle and InterCat's own content fixture, whose generated messages are kept as content: the "
                + "controlled path that carries content end to end (ADR-036). It keeps no other application's bytes.",
            Admission = AdmissionMode.ScopedContent,
            CompilationAvailable = true,
            RequestPreviewAvailable = true,
            Sources =
            [
                new(WindowsSourceCatalog.KernelProcessSourceId, true, true, "Process identity and PID-reuse-safe lifecycle context."),
                new(WindowsSourceCatalog.ContentFixtureSourceId, true, false,
                    "The fixture's messages: each one's length, and its bytes kept as content up to the policy's limits."),
            ],
            PreserveExtendedData = true,
            RequestCallStacks = false,
            CollectionStatement = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Collects lifecycle metadata and the content fixture's messages: each one's length, and up to {ContentFixtureRecordLimit:N0} of its bytes kept as restricted content beside the journal, until {ContentFixtureSessionLimit / (1024 * 1024):N0} MiB of content stops the capture. It keeps no other application's bytes and requests no call stacks."),
        },
    ];

    public static CaptureProfileDescriptor? Find(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        string normalized = id.Trim().Replace('_', '-').Replace(' ', '-').ToLowerInvariant();
        foreach (CaptureProfileDescriptor profile in All.Concat(Fixtures))
        {
            if (string.Equals(profile.Id, normalized, StringComparison.Ordinal))
            {
                return profile;
            }
        }

        return null;
    }

    public static CaptureProfileDescriptor Find(CaptureProfileKind kind) =>
        All.Concat(Fixtures).First(profile => profile.Kind == kind);

    private static CaptureProfileDescriptor Unavailable(
        CaptureProfileKind kind,
        string id,
        string displayName,
        AdmissionMode admission,
        string reason,
        bool requestPreviewAvailable = false) => new()
        {
            Kind = kind,
            Id = id,
            DisplayName = displayName,
            Summary = reason,
            Admission = admission,
            CompilationAvailable = false,
            RequestPreviewAvailable = requestPreviewAvailable,
            UnavailableReason = reason,
            Sources = [],
            PreserveExtendedData = false,
            RequestCallStacks = false,
            CollectionStatement = requestPreviewAvailable
                ? "Compiles bounded requested content rules for review, but starts no capture and retains no content because no source can enforce them yet."
                : "Starts no capture because this profile cannot yet enforce its stated guarantees.",
        };
}

public sealed record CaptureProfileRequest(
    CaptureProfileKind Profile,
    bool RequestOriginalDiagnosticEtl = false,
    Mechanism? FocusedMechanism = null,
    IReadOnlyList<int>? FocusedProcessIds = null,
    bool AllowBroaderCapture = false,
    ContentCaptureRequest? Content = null);

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

public sealed record ProviderScopeDecision(
    string SourceId,
    ProviderProcessScope ProcessScope,
    IReadOnlyList<int> AppliedProcessIds,
    bool CapturesOutsideRequestedProcesses,
    string Reason);

/// <summary>
/// Requested process focus and the scope the providers can really enforce. Initial view focus never
/// changes retention, so broader collection requires a separate recorded acknowledgement.
/// </summary>
public sealed record CaptureScopeDecision
{
    public Mechanism? RequestedMechanism { get; init; }
    public Mechanism? EffectiveMechanism { get; init; }
    public required IReadOnlyList<int> RequestedProcessIds { get; init; }
    public required IReadOnlyList<int> InitialViewProcessIds { get; init; }
    public required bool CapturesOutsideRequestedProcesses { get; init; }
    public required bool BroaderCaptureNeedsConsent { get; init; }
    public required bool BroaderCaptureAccepted { get; init; }
    public required IReadOnlyList<ProviderScopeDecision> Sources { get; init; }
    public required string Disclosure { get; init; }
}

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
    public required CaptureScopeDecision Scope { get; init; }
    public ContentCaptureDecision? Content { get; init; }
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
        string? requestRefusal = ValidateRequest(request);

        if (requestRefusal is not null)
        {
            return Refused(
                request,
                profile,
                originalEvidence,
                environment,
                compiledAtUtc,
                requestRefusal);
        }

        ContentCaptureDecision? contentDecision = request.Profile == CaptureProfileKind.Content
            ? ContentCapturePolicyCompiler.Compile(request.Content!)
            : null;

        // Content compiles from its request alone: a bounded request naming a source admitted for scoped content, whose
        // own sources are that source and the lifecycle that names its processes (ADR-037).
        bool contentAdmitted = contentDecision is { AdmissionPolicyAvailable: true };
        if (!profile.CompilationAvailable && !contentAdmitted)
        {
            return Refused(
                request,
                profile,
                originalEvidence,
                environment,
                compiledAtUtc,
                contentDecision?.AvailabilityReason ?? profile.UnavailableReason ?? "Profile is unavailable.",
                contentDecision);
        }

        CompiledBodyAdmissionPolicy bodyPolicy = (profile.Admission, profile.Kind) switch
        {
            (AdmissionMode.MetadataOnly, _) => CaptureBodyAdmissionPolicies.MetadataOnly,

            // The one scoped content compiler: the controlled fixture's, whose source keeps only its own messages (ADR-036).
            (AdmissionMode.ScopedContent, CaptureProfileKind.ContentFixture) => CaptureBodyAdmissionPolicies.ScopedContentFixture(
                CaptureProfileCatalog.ContentFixtureRecordLimit,
                CaptureProfileCatalog.ContentFixtureSessionLimit,
                ContentInspectionMode.HexAndText),
            (AdmissionMode.ScopedContent, CaptureProfileKind.Content) when contentAdmitted =>
                CaptureBodyAdmissionPolicies.ScopedContentRequest(contentDecision!),
            _ => throw new NotSupportedException(
                $"Profile '{profile.Id}' requests {profile.Admission}, which has no production admission compiler."),
        };

        IReadOnlyList<ProfileSourceRequirement> requirements = contentAdmitted
            ?
            [
                new(WindowsSourceCatalog.KernelProcessSourceId, true, true,
                    "Process identity and PID-reuse-safe lifecycle context, so the named processes keep their names and lifetimes."),
                new(contentDecision!.SourceId, true, true,
                    "The named processes' messages: each record's length, and its bytes kept as content up to the request's limits."),
            ]
            : profile.Sources;
        var decisions = new List<ProfileSourceDecision>(requirements.Count);
        var candidates = new List<string>(requirements.Count);
        foreach (ProfileSourceRequirement requirement in requirements)
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
        if (request.Profile == CaptureProfileKind.FocusedTransport && request.FocusedMechanism is { } focused)
        {
            // A focused capture admits the transport it names and the context that explains it. A source that serves
            // several focusable transports contributes only the focused one's descriptors, and enables only their events.
            compilation = compilation with
            {
                Plans =
                [
                    .. compilation.Plans.Select(plan => plan with
                    {
                        Events =
                        [
                            .. plan.Events.Where(descriptor =>
                                descriptor.Mechanism == focused || !FocusableTransports.Contains(descriptor.Mechanism)),
                        ],
                    }),
                ],
            };
        }

        Dictionary<string, SourceAdmissionPlan> plansBySource = compilation.Plans.ToDictionary(
            plan => plan.SourceId,
            StringComparer.Ordinal);
        foreach (ProfileSourceRequirement requirement in requirements)
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
        [.. requirements.Select(requirement => decisions.Single(decision =>
            string.Equals(decision.SourceId, requirement.SourceId, StringComparison.Ordinal)))];
        IReadOnlyList<SourceAdmissionPlan> included =
        [.. compilation.Plans.Where(plan => orderedDecisions.Any(decision =>
            decision.State == ProfileSourceDecisionState.Included
            && string.Equals(decision.SourceId, plan.SourceId, StringComparison.Ordinal)))];
        CaptureScopeDecision scope = CompileScope(request, included);
        bool sourceBlock = orderedDecisions.Any(decision => decision.State == ProfileSourceDecisionState.Blocking);
        bool scopeBlock = scope.BroaderCaptureNeedsConsent && !scope.BroaderCaptureAccepted;
        bool canStart = !sourceBlock && !scopeBlock && !request.RequestOriginalDiagnosticEtl;
        var diagnostics = compilation.Issues
            .Where(issue => issue.Severity == SourcePlanIssueSeverity.Warning)
            .Select(issue => $"{issue.SourceId}: {issue.Reason}")
            .ToList();
        if (request.RequestOriginalDiagnosticEtl)
        {
            diagnostics.Add(originalEvidence.Warning);
        }

        if (scopeBlock)
        {
            diagnostics.Add(scope.Disclosure);
        }

        Dictionary<string, IReadOnlyList<int>> processFilters = scope.Sources
            .Where(source => source.ProcessScope == ProviderProcessScope.ProcessFiltered)
            .ToDictionary(source => source.SourceId, source => source.AppliedProcessIds, StringComparer.Ordinal);

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
            Providers = ProviderEnablementCompiler.Compile(
                included,
                profile.RequestCallStacks,
                processFilters),
            SourceDecisions = orderedDecisions,
            Scope = scope,
            Content = contentDecision,
            OriginalEvidence = originalEvidence,
            PreserveExtendedData = profile.PreserveExtendedData,
            RequestCallStacks = profile.RequestCallStacks,
            CanStart = canStart,
            CollectionStatement = contentAdmitted
                ? $"{ContentCollectionStatement(contentDecision!)} {scope.Disclosure}"
                : $"{profile.CollectionStatement} {scope.Disclosure}",
            Diagnostics = diagnostics,
        };
    }

    /// <summary>
    /// The transports whose source semantics and capture impact are measured, so a focused capture may name one:
    /// TCP on FX-TCP-001 and UDP on FX-UDP-001.
    /// </summary>
    private static readonly Mechanism[] FocusableTransports = [Mechanism.Tcp, Mechanism.Udp];

    private static string? ValidateRequest(CaptureProfileRequest request)
    {
        IReadOnlyList<int> processIds = request.FocusedProcessIds ?? [];
        if (request.Profile != CaptureProfileKind.Content && request.Content is not null)
        {
            return "Content scope and budgets apply only to the Content profile.";
        }

        if (request.Profile == CaptureProfileKind.Content)
        {
            return request.FocusedMechanism is not null || processIds.Count > 0 || request.AllowBroaderCapture
                ? "Focused transport settings cannot be combined with a Content request."
                : ContentCapturePolicyCompiler.Validate(request.Content);
        }

        if (processIds.Count > 64)
        {
            return "A focused profile accepts at most 64 process IDs per capture request.";
        }

        if (processIds.Any(processId => processId <= 0))
        {
            return "Every focused process ID must be a positive integer.";
        }

        if (processIds.Distinct().Count() != processIds.Count)
        {
            return "A focused process ID may appear only once.";
        }

        if (request.Profile != CaptureProfileKind.FocusedTransport)
        {
            return request.FocusedMechanism is not null || processIds.Count > 0 || request.AllowBroaderCapture
                ? "Mechanism, process focus and broader-capture consent apply only to Focused transport."
                : null;
        }

        if (request.FocusedMechanism is null)
        {
            return "Focused transport requires an explicit mechanism. TCP and UDP are the validated options in this build.";
        }

        if (!FocusableTransports.Contains(request.FocusedMechanism.Value))
        {
            return $"{request.FocusedMechanism} is not available in Focused transport. TCP and UDP are the only mechanisms with measured source semantics and capture impact.";
        }

        return request.AllowBroaderCapture && processIds.Count == 0
            ? "Broader-capture consent is unnecessary without a process selection. Remove the consent flag."
            : null;
    }

    /// <summary>What an admitted Content request collects, in words, before its scope's own disclosure.</summary>
    private static string ContentCollectionStatement(ContentCaptureDecision content)
    {
        string source = WindowsSourceCatalog.Find(content.SourceId)?.DisplayName ?? content.SourceId;
        string inspection = content.Inspection == ContentInspectionMode.HexAndText
            ? "A person may see the bytes only when they ask, bounded and inert."
            : "The bytes are kept without consent to inspect them, so they are never shown.";
        string holds = WindowsSourceCatalog.Find(content.SourceId)?.ContentContract?.ContentStatement is { } statement
            ? " " + statement
            : string.Empty;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Collects lifecycle metadata and {source} of processes {string.Join(", ", content.ProcessIds)} only: each record's length, and up to {content.MaximumRecordBytes:N0} of its bytes kept as restricted content beside the journal, until {content.MaximumSessionBytes:N0} bytes of content stop the capture.{holds} {inspection}");
    }

    private static CaptureScopeDecision CompileScope(
        CaptureProfileRequest request,
        IReadOnlyList<SourceAdmissionPlan> included)
    {
        int[] requestedProcessIds = [.. (request.FocusedProcessIds ?? []).Order()];
        if (request.Profile == CaptureProfileKind.Content && request.Content is { } content)
        {
            // Content is held to its processes by the source's own process filter, before anything is kept; lifecycle stays
            // whole-machine metadata, so those processes keep their names, lifetimes and parents (ADR-037).
            int[] contentProcessIds = [.. content.ProcessIds.Order()];
            return new()
            {
                RequestedProcessIds = contentProcessIds,
                InitialViewProcessIds = contentProcessIds,
                CapturesOutsideRequestedProcesses = included.Any(source => source.SourceId == WindowsSourceCatalog.KernelProcessSourceId),
                BroaderCaptureNeedsConsent = false,
                BroaderCaptureAccepted = false,
                Sources =
                [
                    .. included.Select(source => source.SourceId == content.SourceId
                        ? new ProviderScopeDecision(source.SourceId, ProviderProcessScope.ProcessFiltered, contentProcessIds, false,
                            "Content is kept only from the named processes, by the session's process filter.")
                        : new ProviderScopeDecision(source.SourceId, ProviderProcessScope.WholeMachineRequiredContext, [], true,
                            "Lifecycle stays whole-machine metadata so the named processes keep their identity context.")),
                ],
                Disclosure = "Content is kept only from the named processes, by its provider's process filter; lifecycle "
                    + "metadata stays whole-machine.",
            };
        }

        if (request.Profile != CaptureProfileKind.FocusedTransport)
        {
            return new()
            {
                RequestedProcessIds = [],
                InitialViewProcessIds = [],
                CapturesOutsideRequestedProcesses = false,
                BroaderCaptureNeedsConsent = false,
                BroaderCaptureAccepted = false,
                Sources =
                [
                    .. included.Select(source => new ProviderScopeDecision(
                        source.SourceId,
                        ProviderProcessScope.WholeMachineRequested,
                        [],
                        false,
                        "This profile requests whole-machine metadata.")),
                ],
                Disclosure = "The profile requests whole-machine metadata; no narrower process scope was requested.",
            };
        }

        if (requestedProcessIds.Length == 0)
        {
            return new()
            {
                RequestedMechanism = request.FocusedMechanism,
                EffectiveMechanism = request.FocusedMechanism,
                RequestedProcessIds = [],
                InitialViewProcessIds = [],
                CapturesOutsideRequestedProcesses = false,
                BroaderCaptureNeedsConsent = false,
                BroaderCaptureAccepted = false,
                Sources =
                [
                    .. included.Select(source => new ProviderScopeDecision(
                        source.SourceId,
                        ProviderProcessScope.WholeMachineRequested,
                        [],
                        false,
                        "No process restriction was requested.")),
                ],
                Disclosure = $"The capture is focused on {request.FocusedMechanism} but includes all processes.",
            };
        }

        var sourceScopes = new List<ProviderScopeDecision>(included.Count);
        foreach (SourceAdmissionPlan source in included)
        {
            WindowsSourceDefinition definition = WindowsSourceCatalog.Find(source.SourceId)
                ?? throw new InvalidOperationException($"Source '{source.SourceId}' left the catalog during compilation.");
            if (source.SourceId == WindowsSourceCatalog.KernelProcessSourceId)
            {
                sourceScopes.Add(new(
                    source.SourceId,
                    ProviderProcessScope.WholeMachineRequiredContext,
                    [],
                    true,
                    "Lifecycle stays whole-machine so selected processes and newly observed peers retain identity context."));
            }
            else if (definition.SupportsCaptureSideProcessFilter)
            {
                sourceScopes.Add(new(
                    source.SourceId,
                    ProviderProcessScope.ProcessFiltered,
                    requestedProcessIds,
                    false,
                    "The provider can enforce the requested process IDs before delivery."));
            }
            else
            {
                sourceScopes.Add(new(
                    source.SourceId,
                    ProviderProcessScope.WholeMachineFilterUnavailable,
                    [],
                    true,
                    "The provider exposes no capture-side process filter; process focus can narrow the initial view only."));
            }
        }

        bool broader = sourceScopes.Any(source => source.CapturesOutsideRequestedProcesses);
        string disclosure = request.AllowBroaderCapture
            ? $"Broader capture accepted: {request.FocusedMechanism} metadata may be collected outside PID(s) {string.Join(", ", requestedProcessIds)}; the initial view remains focused on those processes."
            : $"Broader capture needs consent: {request.FocusedMechanism} cannot be retained only for PID(s) {string.Join(", ", requestedProcessIds)} while preserving required context. Review the source decisions, then explicitly allow broader capture if acceptable.";
        return new()
        {
            RequestedMechanism = request.FocusedMechanism,
            EffectiveMechanism = request.FocusedMechanism,
            RequestedProcessIds = requestedProcessIds,
            InitialViewProcessIds = requestedProcessIds,
            CapturesOutsideRequestedProcesses = broader,
            BroaderCaptureNeedsConsent = broader,
            BroaderCaptureAccepted = broader && request.AllowBroaderCapture,
            Sources = sourceScopes,
            Disclosure = disclosure,
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
        CaptureProfileRequest request,
        CaptureProfileDescriptor profile,
        OriginalEvidenceDecision originalEvidence,
        ProbeEnvironment environment,
        DateTimeOffset compiledAtUtc,
        string reason,
        ContentCaptureDecision? content = null) => new()
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
            Scope = new()
            {
                RequestedMechanism = request.FocusedMechanism ?? request.Content?.Mechanism,
                EffectiveMechanism = null,
                RequestedProcessIds = [.. (request.FocusedProcessIds ?? request.Content?.ProcessIds ?? []).Order()],
                InitialViewProcessIds = [],
                CapturesOutsideRequestedProcesses = false,
                BroaderCaptureNeedsConsent = false,
                BroaderCaptureAccepted = false,
                Sources = [],
                Disclosure = reason,
            },
            Content = content,
            OriginalEvidence = originalEvidence,
            PreserveExtendedData = false,
            RequestCallStacks = false,
            CanStart = false,
            CollectionStatement = profile.CollectionStatement,
            Diagnostics = [reason],
        };
}
