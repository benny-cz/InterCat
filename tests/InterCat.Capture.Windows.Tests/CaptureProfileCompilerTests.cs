using InterCat.Capture.Windows;
using InterCat.Domain;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

public sealed class CaptureProfileCompilerTests
{
    [Fact(DisplayName = "IC-012: the profile catalog exposes every intent without pretending unfinished modes are available")]
    public void CatalogExposesUnavailableProfiles()
    {
        Assert.Equal(5, CaptureProfileCatalog.All.Count);
        Assert.Equal(5, CaptureProfileCatalog.All.Select(profile => profile.Id).Distinct().Count());

        CaptureProfileDescriptor focused = CaptureProfileCatalog.Find("focused-transport")!;
        Assert.True(focused.CompilationAvailable);
        Assert.Equal(AdmissionMode.MetadataOnly, focused.Admission);

        CaptureProfileDescriptor content = CaptureProfileCatalog.Find("content")!;
        Assert.Equal(AdmissionMode.ScopedContent, content.Admission);
        Assert.False(content.CompilationAvailable);
        Assert.True(content.RequestPreviewAvailable);
        Assert.Contains("bounded request", content.UnavailableReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "IC-012: Content compiles complete boundaries without inventing an admission policy")]
    public void ContentRequestCompilesBoundariesButRemainsUnavailable()
    {
        EffectiveCapturePlan plan = CaptureProfileCompiler.Compile(
            new(CaptureProfileKind.Content, Content: ValidContentRequest()),
            new SourcePlanCompilation([], []));

        Assert.False(plan.CanStart);
        Assert.Null(plan.EffectiveProfileId);
        Assert.Null(plan.EffectiveAdmission);
        Assert.Null(plan.BodyPolicy);
        Assert.Empty(plan.Providers);
        ContentCaptureDecision content = Assert.IsType<ContentCaptureDecision>(plan.Content);
        Assert.Equal(WindowsSourceCatalog.RpcSourceId, content.SourceId);
        Assert.Equal(Mechanism.Rpc, content.Mechanism);
        Assert.Equal([84, 4242], content.ProcessIds);
        Assert.Equal(["rpc-interface:12345678-1234-1234-1234-123456789abc"], content.ChannelSelectors);
        Assert.Equal(4096, content.MaximumRecordBytes);
        Assert.Equal(64 * 1024 * 1024, content.MaximumSessionBytes);
        Assert.Equal(ContentRecordLimitBehavior.RetainPrefixAndRecordTruncation, content.RecordLimitBehavior);
        Assert.Equal(UnknownContentSchemaBehavior.OmitBodyAndKeepHeaderDiagnostic, content.UnknownSchemaBehavior);
        Assert.False(content.SourceBodyContractAvailable);
        Assert.False(content.ScopeEnforceable);
        Assert.False(content.CaptureImpactMeasured);
        Assert.False(content.SourceEvidenceComplete);
        Assert.False(content.AdmissionPolicyAvailable);
        Assert.Empty(content.ApprovedEventIds);
        Assert.Empty(content.ApprovedSourceFields);
        Assert.Contains("remain denied", content.AvailabilityReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Mechanism.Rpc, plan.Scope.RequestedMechanism);
        Assert.Equal([84, 4242], plan.Scope.RequestedProcessIds);
    }

    [Fact(DisplayName = "IC-012: Content refuses incomplete or internally inconsistent boundaries")]
    public void ContentRequestValidatesEveryBoundary()
    {
        EffectiveCapturePlan missing = CaptureProfileCompiler.Compile(
            new(CaptureProfileKind.Content),
            new SourcePlanCompilation([], []));
        EffectiveCapturePlan undersizedSession = CaptureProfileCompiler.Compile(
            new(
                CaptureProfileKind.Content,
                Content: ValidContentRequest() with
                {
                    MaximumRecordBytes = 4096,
                    MaximumSessionBytes = 1024,
                }),
            new SourcePlanCompilation([], []));

        Assert.False(missing.CanStart);
        Assert.Null(missing.Content);
        Assert.Contains("explicit source", Assert.Single(missing.Diagnostics), StringComparison.OrdinalIgnoreCase);
        Assert.False(undersizedSession.CanStart);
        Assert.Null(undersizedSession.Content);
        Assert.Contains("at least the per-record", Assert.Single(undersizedSession.Diagnostics), StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "IC-012: Content source and mechanism must describe the same catalog capability")]
    public void ContentRequestRejectsSourceMechanismMismatch()
    {
        EffectiveCapturePlan plan = CaptureProfileCompiler.Compile(
            new(
                CaptureProfileKind.Content,
                Content: ValidContentRequest() with { Mechanism = Mechanism.Tcp }),
            new SourcePlanCompilation([], []));

        Assert.False(plan.CanStart);
        Assert.Null(plan.Content);
        Assert.Contains("does not describe Tcp", Assert.Single(plan.Diagnostics), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "IC-012: Focused TCP compiles one mechanism and required lifecycle context")]
    public void FocusedTcpCompilesWithoutProcessRestriction()
    {
        EffectiveCapturePlan plan = CompileFocused();

        Assert.True(plan.CanStart);
        Assert.Equal("focused-transport", plan.EffectiveProfileId);
        Assert.Equal(Mechanism.Tcp, plan.Scope.RequestedMechanism);
        Assert.Equal(Mechanism.Tcp, plan.Scope.EffectiveMechanism);
        Assert.Empty(plan.Scope.RequestedProcessIds);
        Assert.False(plan.Scope.CapturesOutsideRequestedProcesses);
        Assert.All(
            plan.Scope.Sources,
            source => Assert.Equal(ProviderProcessScope.WholeMachineRequested, source.ProcessScope));
        Assert.All(plan.Providers, provider => Assert.Empty(provider.ProcessIdsToInclude));
    }

    [Fact(DisplayName = "IC-012: a focused capture admits only the transport it names from a source that serves two")]
    public void FocusAdmitsOnlyTheNamedTransport()
    {
        SourceAdmissionPlan process = BuildPlan(WindowsSourceCatalog.KernelProcessSourceId, 0, 1);
        SourceAdmissionPlan tcpOnly = BuildPlan(WindowsSourceCatalog.KernelNetworkSourceId, 1, 10);
        SourceAdmissionPlan network = tcpOnly with
        {
            Events = [tcpOnly.Events[0], tcpOnly.Events[0] with { EventId = 42, Mechanism = Mechanism.Udp }],
        };

        foreach ((Mechanism focus, int eventId) in new[] { (Mechanism.Tcp, 10), (Mechanism.Udp, 42) })
        {
            EffectiveCapturePlan plan = CaptureProfileCompiler.Compile(
                new(CaptureProfileKind.FocusedTransport, FocusedMechanism: focus),
                new SourcePlanCompilation([process, network], []));

            Assert.True(plan.CanStart);
            SourceAdmissionPlan admitted = plan.Sources.Single(source => source.SourceId == WindowsSourceCatalog.KernelNetworkSourceId);
            Assert.Equal(eventId, Assert.Single(admitted.Events).EventId);
            Assert.Single(plan.Sources, source => source.SourceId == WindowsSourceCatalog.KernelProcessSourceId);

            // The provider is enabled for the focused transport's events only, not for everything the source serves.
            ProviderEnablementRequest provider = plan.Providers.Single(request => request.SourceId == WindowsSourceCatalog.KernelNetworkSourceId);
            Assert.Equal([eventId], provider.EventIdsToEnable);
        }
    }

    [Fact(DisplayName = "IC-012: process focus blocks until unavoidable broader TCP collection is accepted")]
    public void FocusedProcessNeedsBroaderCaptureConsent()
    {
        EffectiveCapturePlan plan = CompileFocused(processIds: [4242]);

        Assert.False(plan.CanStart);
        Assert.Null(plan.EffectiveProfileId);
        Assert.True(plan.Scope.CapturesOutsideRequestedProcesses);
        Assert.True(plan.Scope.BroaderCaptureNeedsConsent);
        Assert.False(plan.Scope.BroaderCaptureAccepted);
        Assert.Equal([4242], plan.Scope.InitialViewProcessIds);
        Assert.Contains("needs consent", plan.Scope.Disclosure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            plan.Scope.Sources,
            source => source.SourceId == WindowsSourceCatalog.KernelNetworkSourceId
                && source.ProcessScope == ProviderProcessScope.WholeMachineFilterUnavailable);
        Assert.Contains(
            plan.Scope.Sources,
            source => source.SourceId == WindowsSourceCatalog.KernelProcessSourceId
                && source.ProcessScope == ProviderProcessScope.WholeMachineRequiredContext);
        Assert.All(plan.Providers, provider => Assert.Empty(provider.ProcessIdsToInclude));
    }

    [Fact(DisplayName = "IC-012: broader Focused TCP collection is effective only after explicit acknowledgement")]
    public void FocusedProcessCanAcceptBroaderCapture()
    {
        EffectiveCapturePlan plan = CompileFocused(processIds: [4242, 84], allowBroaderCapture: true);

        Assert.True(plan.CanStart);
        Assert.Equal("focused-transport", plan.EffectiveProfileId);
        Assert.True(plan.Scope.BroaderCaptureAccepted);
        Assert.Equal([84, 4242], plan.Scope.RequestedProcessIds);
        Assert.Contains("accepted", plan.Scope.Disclosure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "IC-012: Focused transport refuses an unmeasured mechanism without TCP fallback")]
    public void FocusedTransportDoesNotSubstituteMechanism()
    {
        EffectiveCapturePlan plan = CaptureProfileCompiler.Compile(
            new(CaptureProfileKind.FocusedTransport, FocusedMechanism: Mechanism.Rpc),
            new SourcePlanCompilation([], []));

        Assert.False(plan.CanStart);
        Assert.Null(plan.EffectiveProfileId);
        Assert.Equal(Mechanism.Rpc, plan.Scope.RequestedMechanism);
        Assert.Null(plan.Scope.EffectiveMechanism);
        Assert.Contains("TCP", Assert.Single(plan.Diagnostics), StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "IC-012: malformed Focused requests are refused before provider compilation")]
    public void FocusedTransportValidatesScopeInputs()
    {
        EffectiveCapturePlan duplicate = CaptureProfileCompiler.Compile(
            new(
                CaptureProfileKind.FocusedTransport,
                FocusedMechanism: Mechanism.Tcp,
                FocusedProcessIds: [4242, 4242]),
            new SourcePlanCompilation([], []));
        EffectiveCapturePlan meaninglessConsent = CaptureProfileCompiler.Compile(
            new(
                CaptureProfileKind.FocusedTransport,
                FocusedMechanism: Mechanism.Tcp,
                AllowBroaderCapture: true),
            new SourcePlanCompilation([], []));

        Assert.False(duplicate.CanStart);
        Assert.Contains("only once", Assert.Single(duplicate.Diagnostics), StringComparison.OrdinalIgnoreCase);
        Assert.False(meaninglessConsent.CanStart);
        Assert.Contains("unnecessary", Assert.Single(meaninglessConsent.Diagnostics), StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "IC-012: provider PID filters require a source contract that can enforce them")]
    public void ProviderProcessFilterRequiresCatalogSupport()
    {
        SourceAdmissionPlan rpc = BuildPlan(WindowsSourceCatalog.RpcSourceId, 0, 5);
        var rpcFilters = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
        {
            [rpc.SourceId] = [84, 4242],
        };

        ProviderEnablementRequest request = Assert.Single(
            ProviderEnablementCompiler.Compile([rpc], processFilters: rpcFilters));
        Assert.Equal([84, 4242], request.ProcessIdsToInclude);

        SourceAdmissionPlan network = BuildPlan(WindowsSourceCatalog.KernelNetworkSourceId, 0, 10);
        var networkFilters = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
        {
            [network.SourceId] = [4242],
        };
        Assert.Throws<InvalidOperationException>(
            () => ProviderEnablementCompiler.Compile([network], processFilters: networkFilters));
    }

    [Fact(DisplayName = "IC-012: Explore records requested and effective sources with measured optional omissions")]
    public void ExploreCompilesRequiredSourcesAndExplainsOmissions()
    {
        SourceAdmissionPlan process = BuildPlan(WindowsSourceCatalog.KernelProcessSourceId, 0, 1);
        SourceAdmissionPlan network = BuildPlan(WindowsSourceCatalog.KernelNetworkSourceId, 1, 10);

        EffectiveCapturePlan plan = CaptureProfileCompiler.Compile(
            new(CaptureProfileKind.Explore),
            new SourcePlanCompilation([process, network], []));

        Assert.True(plan.CanStart);
        Assert.Equal(CapabilityInventoryProbe.AdapterVersion, plan.AdapterVersion);
        Assert.False(string.IsNullOrWhiteSpace(plan.Environment.BuildId));
        Assert.Equal("explore", plan.RequestedProfileId);
        Assert.Equal("explore", plan.EffectiveProfileId);
        Assert.Equal(AdmissionMode.MetadataOnly, plan.EffectiveAdmission);
        Assert.Same(CaptureBodyAdmissionPolicies.MetadataOnly, plan.BodyPolicy);
        Assert.Equal(2, plan.Sources.Count);
        Assert.Equal(2, plan.Providers.Count);
        Assert.All(
            plan.SourceDecisions.Where(decision => decision.Required),
            decision =>
            {
                Assert.Equal(ProfileSourceDecisionState.Included, decision.State);
                // Whatever class a measurement gives, a required source is one whose capture impact was measured (§4.3).
                Assert.NotEqual(OverheadClass.Unmeasured, decision.Overhead);
                Assert.NotNull(decision.OverheadEvidence);
            });
        Assert.All(plan.Providers, provider => Assert.Equal(4, provider.Level));
        Assert.Contains(
            plan.SourceDecisions,
            decision => decision.SourceId == WindowsSourceCatalog.RpcSourceId
                && decision.State == ProfileSourceDecisionState.Omitted
                && decision.Reason.Contains("unmeasured", StringComparison.OrdinalIgnoreCase));
    }

    [Fact(DisplayName = "IC-012: a missing required source blocks Explore instead of silently weakening it")]
    public void RequiredSourceRefusalBlocksProfile()
    {
        SourceAdmissionPlan process = BuildPlan(WindowsSourceCatalog.KernelProcessSourceId, 0, 1);
        var compilation = new SourcePlanCompilation(
            [process],
            [
                new(
                    WindowsSourceCatalog.KernelNetworkSourceId,
                    SourcePlanIssueSeverity.Refusal,
                    "Saved schema is unavailable."),
            ]);

        EffectiveCapturePlan plan = CaptureProfileCompiler.Compile(
            new(CaptureProfileKind.Explore),
            compilation);

        Assert.False(plan.CanStart);
        Assert.Null(plan.EffectiveProfileId);
        ProfileSourceDecision network = Assert.Single(
            plan.SourceDecisions,
            decision => decision.SourceId == WindowsSourceCatalog.KernelNetworkSourceId);
        Assert.Equal(ProfileSourceDecisionState.Blocking, network.State);
        Assert.Contains("schema", network.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "IC-012: original diagnostic evidence remains separate and an unsupported request blocks capture")]
    public void OriginalEvidenceDoesNotFallBack()
    {
        SourceAdmissionPlan process = BuildPlan(WindowsSourceCatalog.KernelProcessSourceId, 0, 1);
        SourceAdmissionPlan network = BuildPlan(WindowsSourceCatalog.KernelNetworkSourceId, 1, 10);

        EffectiveCapturePlan plan = CaptureProfileCompiler.Compile(
            new(CaptureProfileKind.Explore, RequestOriginalDiagnosticEtl: true),
            new SourcePlanCompilation([process, network], []));

        Assert.False(plan.CanStart);
        Assert.True(plan.OriginalEvidence.Requested);
        Assert.False(plan.OriginalEvidence.Available);
        Assert.False(plan.OriginalEvidence.WillStart);
        Assert.Contains("separate", plan.OriginalEvidence.StorageBoundary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("payload", plan.OriginalEvidence.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "IC-012: admitted layout and body policy produce a strong schema identity")]
    public void FingerprintIncludesAdmittedLayout()
    {
        Guid provider = Guid.Parse("7dd42a49-5329-4832-8dfd-43d979153a88");
        AdmittedSlotPlan first = new("size", FieldRole.ByteCount, 0, 4, MeasurementUnit.Bytes, ByteDomain.TransportObserved, SlotTransform.None);
        AdmittedSlotPlan moved = first with { Offset = 4 };

        string left = AdmissionPlanCompiler.ComputeFingerprint(
            "provider-schema",
            provider,
            10,
            0,
            8,
            4,
            [first],
            CaptureBodyAdmissionPolicies.MetadataOnly);
        string right = AdmissionPlanCompiler.ComputeFingerprint(
            "provider-schema",
            provider,
            10,
            0,
            8,
            8,
            [moved],
            CaptureBodyAdmissionPolicies.MetadataOnly);

        Assert.StartsWith("sha256:", left, StringComparison.Ordinal);
        Assert.NotEqual(left, right);
    }

    [Fact(DisplayName = "IC-012: duplicate descriptor identities are refused before capture")]
    public void DuplicateDescriptorsAreRefused()
    {
        SourceAdmissionPlan first = BuildPlan(WindowsSourceCatalog.KernelProcessSourceId, 0, 1);
        SourceAdmissionPlan duplicate = first with { SourceId = WindowsSourceCatalog.KernelNetworkSourceId };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => new EventAdmissionTable([first, duplicate]));

        Assert.Contains("more than once", exception.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "IC-012: unknown descriptors and unknown versions have distinct health attribution")]
    public void UnknownDescriptorClassificationIsExplicit()
    {
        SourceAdmissionPlan source = BuildPlan(WindowsSourceCatalog.KernelNetworkSourceId, 0, 10);
        var table = new EventAdmissionTable([source]);

        DescriptorAdmissionResolution admitted = table.Resolve(source.ProviderGuid, 10, 0);
        DescriptorAdmissionResolution version = table.Resolve(source.ProviderGuid, 10, 99);
        DescriptorAdmissionResolution descriptor = table.Resolve(source.ProviderGuid, 999, 0);
        DescriptorAdmissionResolution provider = table.Resolve(Guid.NewGuid(), 10, 0);

        Assert.Equal(DescriptorAdmissionOutcome.Admitted, admitted.Outcome);
        Assert.NotNull(admitted.Plan);
        Assert.Equal(DescriptorAdmissionOutcome.UnknownDescriptorVersion, version.Outcome);
        Assert.Equal(DescriptorAdmissionOutcome.DescriptorNotAdmitted, descriptor.Outcome);
        Assert.Equal(DescriptorAdmissionOutcome.UnrequestedProvider, provider.Outcome);
        Assert.Null(version.Plan);
        Assert.Null(descriptor.Plan);
        Assert.Null(provider.Plan);
    }

    [Fact(DisplayName = "IC-012: unsupported body modes are refused rather than downgraded")]
    public void UnsupportedBodyPolicyIsRefused()
    {
        var unsupported = new CompiledBodyAdmissionPolicy
        {
            PolicyId = "test-scoped-content",
            Mode = AdmissionMode.ScopedContent,
            RetainedBody = RetainedBodyShape.ApprovedMetadataProjection,
            MaximumRetainedBodyBytes = 64,
            RetainsOriginalSourceBytes = true,
            PermittedExtendedDataTypes = [],
            Summary = "test",
        };

        Assert.Throws<NotSupportedException>(() => CaptureBodyAdmissionPolicies.EnsureSupported(unsupported));

        CompiledBodyAdmissionPolicy forgedMetadata = CaptureBodyAdmissionPolicies.MetadataOnly with
        {
            PermittedExtendedDataTypes =
            [
                .. CaptureBodyAdmissionPolicies.MetadataOnly.PermittedExtendedDataTypes,
                EtwExtendedDataTypes.Sid,
            ],
        };
        Assert.Throws<NotSupportedException>(() => CaptureBodyAdmissionPolicies.EnsureSupported(forgedMetadata));
    }

    private static SourceAdmissionPlan BuildPlan(string sourceId, int sourceIndex, int eventId)
    {
        WindowsSourceDefinition definition = WindowsSourceCatalog.Find(sourceId)!;
        Guid provider = sourceId == WindowsSourceCatalog.KernelProcessSourceId
            ? Guid.Parse("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716")
            : Guid.Parse("7dd42a49-5329-4832-8dfd-43d979153a88");
        var admitted = new AdmittedEventPlan
        {
            SourceIndex = sourceIndex,
            ProviderGuid = provider,
            EventId = eventId,
            Version = 0,
            Name = "fixture",
            Mechanism = definition.Mechanisms[0],
            Layer = ObservationLayer.Transport,
            Kind = ObservationKind.Discovery,
            Direction = Direction.DirectionNotApplicable,
            MinimumBodyLength = 0,
            PointerSize = 8,
            SchemaFingerprint = $"sha256:fixture-{sourceIndex}",
            BodyPolicy = CaptureBodyAdmissionPolicies.MetadataOnly,
            Slots = [],
            FieldReport = [],
        };
        return new()
        {
            SourceId = sourceId,
            ProviderGuid = provider,
            SourceIndex = sourceIndex,
            Events = [admitted],
            Diagnostics = [],
        };
    }

    private static EffectiveCapturePlan CompileFocused(
        IReadOnlyList<int>? processIds = null,
        bool allowBroaderCapture = false)
    {
        SourceAdmissionPlan process = BuildPlan(WindowsSourceCatalog.KernelProcessSourceId, 0, 1);
        SourceAdmissionPlan network = BuildPlan(WindowsSourceCatalog.KernelNetworkSourceId, 1, 10);
        return CaptureProfileCompiler.Compile(
            new(
                CaptureProfileKind.FocusedTransport,
                FocusedMechanism: Mechanism.Tcp,
                FocusedProcessIds: processIds,
                AllowBroaderCapture: allowBroaderCapture),
            new SourcePlanCompilation([process, network], []));
    }

    private static ContentCaptureRequest ValidContentRequest() => new()
    {
        SourceId = WindowsSourceCatalog.RpcSourceId,
        Mechanism = Mechanism.Rpc,
        ProcessIds = [4242, 84],
        ChannelSelectors = ["rpc-interface:12345678-1234-1234-1234-123456789abc"],
        MaximumRecordBytes = 4096,
        MaximumSessionBytes = 64 * 1024 * 1024,
        Retention = ContentRetentionMode.StopAtLimit,
        Inspection = ContentInspectionMode.HexAndText,
    };
}
