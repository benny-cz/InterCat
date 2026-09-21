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

        CaptureProfileDescriptor content = CaptureProfileCatalog.Find("content")!;
        Assert.Equal(AdmissionMode.ScopedContent, content.Admission);
        Assert.False(content.CompilationAvailable);
        Assert.Contains("scope", content.UnavailableReason, StringComparison.OrdinalIgnoreCase);
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
                Assert.Equal(OverheadClass.Low, decision.Overhead);
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
}
