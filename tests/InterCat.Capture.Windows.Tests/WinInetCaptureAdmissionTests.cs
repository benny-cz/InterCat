using InterCat.Domain;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// WinINet's capture provider in the catalog (ADR-037): its four events admitted as HTTP messages whose exchange, buffer
/// order and ends are kept as source fields and whose length is measured; its bytes only under a scoped content policy
/// that names it; enabled only for the processes a capture names. No profile captures it yet.
/// </summary>
public sealed class WinInetCaptureAdmissionTests
{
    // The provider's registered layout, as TDH gave it on Windows 10.0.26220 (ADR-037).
    private const string Manifest = """
        <instrumentationManifest xmlns="http://schemas.microsoft.com/win/2004/08/events">
         <instrumentation><events>
          <provider name="Microsoft-Windows-WinINet-Capture" guid="{a70ff94f-570b-4979-ba5c-e59c9feab61b}">
           <templates>
            <template tid="Capture">
             <data name="SessionId" inType="win:UInt32" />
             <data name="SequenceNumber" inType="win:UInt32" />
             <data name="Flags" inType="win:UInt32" />
             <data name="PayloadByteLength" inType="win:UInt32" />
             <data name="Payload" inType="win:Binary" length="PayloadByteLength" />
            </template>
           </templates>
           <events>
            <event value="2001" version="0" level="win:Informational" template="Capture" />
            <event value="2002" version="0" level="win:Informational" template="Capture" />
            <event value="2003" version="0" level="win:Informational" template="Capture" />
            <event value="2004" version="0" level="win:Informational" template="Capture" />
           </events>
          </provider>
         </events></instrumentation>
        </instrumentationManifest>
        """;

    private static readonly CompiledBodyAdmissionPolicy FixturePolicy =
        CaptureBodyAdmissionPolicies.ScopedContentFixture(4_096, 16L * 1024 * 1024, ContentInspectionMode.HexAndText);

    private static WindowsSourceDefinition Source => WindowsSourceCatalog.Find(WindowsSourceCatalog.WinInetCaptureSourceId)!;

    [Fact(DisplayName = "R17: WinINet's capture events are HTTP messages, and a buffer's bytes are admitted only under a policy that names them")]
    public void TheCaptureEventsAreHttpMessages()
    {
        // A Windows capability, reported with the machine's sources, and scoped to the processes a capture names.
        Assert.Contains(WindowsSourceCatalog.All, source => source.SourceId == WindowsSourceCatalog.WinInetCaptureSourceId);
        Assert.Equal([Mechanism.Http], Source.Mechanisms);
        Assert.True(Source.SupportsCaptureSideProcessFilter);
        Assert.Equal(0x0000_0603_0000_0000UL, Source.MatchAnyKeyword);
        ValidatedContentSourceContract contract = Source.ContentContract!;
        Assert.Equal([2001, 2002, 2003, 2004], contract.EventIds);
        Assert.Equal((true, false), (contract.EnforcesProcessScopeBeforePersistence, contract.EnforcesChannelScopeBeforePersistence));

        SourceAdmissionPlan plan = AdmissionPlanCompiler.Compile(Source, ManifestParser.Parse(Manifest), 0);
        Assert.Equal(
            [
                (2001, ObservationKind.Send, Direction.Outbound), (2002, ObservationKind.Send, Direction.Outbound),
                (2003, ObservationKind.Receive, Direction.Inbound), (2004, ObservationKind.Receive, Direction.Inbound),
            ],
            plan.Events.OrderBy(descriptor => descriptor.EventId).Select(descriptor => ((int)descriptor.EventId, descriptor.Kind, descriptor.Direction)));
        foreach (AdmittedEventPlan descriptor in plan.Events)
        {
            Assert.Equal((Mechanism.Http, ObservationLayer.Application), (descriptor.Mechanism, descriptor.Layer));
            Assert.Equal(
                [
                    ("SessionId", 0, (SourceField?)SourceField.HttpExchangeId), ("SequenceNumber", 4, SourceField.ContentBufferSequence),
                    ("Flags", 8, SourceField.ContentBufferFlags), ("PayloadByteLength", 12, null),
                ],
                descriptor.Slots.Select(slot => (slot.FieldName, slot.Offset, slot.SourceField)));
            Assert.Contains(descriptor.Slots, slot => slot is { FieldName: "PayloadByteLength", Role: FieldRole.ByteCount, Width: 4 }
                && slot.ByteDomain == ByteDomain.ApplicationPayload);

            // Under a metadata policy the buffer's bytes are not admitted, and the report says so; its length is.
            Assert.Contains(descriptor.FieldReport, field => field is { Name: "Payload", Availability: FieldAvailability.ProfileDisabled });
        }

        // Its records bind to the client process that raised them (ADR-030, process-binding-v4).
        Assert.True(RecordAttribution.RaisedByTheirProcess(Mechanism.Http));
    }

    [Fact(DisplayName = "P15: a scoped content policy keeps the content of the sources it names, and of no other")]
    public void AContentPolicyKeepsOnlyTheSourcesItNames()
    {
        // The fixture's policy names the fixture: compiled for WinINet's source, it keeps that source's metadata only.
        Assert.Equal([WindowsSourceCatalog.ContentFixtureSourceId], FixturePolicy.ContentSourceIds);
        Assert.False(FixturePolicy.KeepsContentOf(WindowsSourceCatalog.WinInetCaptureSourceId));
        SourceAdmissionPlan direct = AdmissionPlanCompiler.Compile(Source, ManifestParser.Parse(Manifest), 0, bodyPolicy: FixturePolicy);
        Assert.All(direct.Events, descriptor =>
        {
            Assert.DoesNotContain(descriptor.Slots, slot => slot.Kind == AdmittedSlotKind.Content);
            Assert.Contains(descriptor.FieldReport, field => field.Name == "Payload"
                && field.Notes!.Contains("keeps no content of this source", StringComparison.Ordinal));
        });

        // In one capture with the fixture, the fixture keeps content and WinINet's source keeps metadata.
        SourcePlanCompilation compilation = new CapabilityInventoryProbe(new WinInetMetadata()).CompilePlanSet(
            [WindowsSourceCatalog.ContentFixtureSourceId, WindowsSourceCatalog.WinInetCaptureSourceId], FixturePolicy);
        Assert.DoesNotContain(compilation.Issues, issue => issue.Severity == SourcePlanIssueSeverity.Refusal);
        Assert.All(compilation.Plans.Single(plan => plan.SourceId == WindowsSourceCatalog.WinInetCaptureSourceId).Events,
            descriptor => Assert.Equal(CaptureBodyAdmissionPolicies.MetadataOnlyPolicyId, descriptor.BodyPolicy.PolicyId));
        Assert.All(compilation.Plans.Single(plan => plan.SourceId == WindowsSourceCatalog.ContentFixtureSourceId).Events,
            descriptor => Assert.Equal(CaptureBodyAdmissionPolicies.ScopedContentFixturePolicyId, descriptor.BodyPolicy.PolicyId));

        // The reviewed fixture policy covers its fixture alone; widened, it is refused rather than enforced.
        Assert.Throws<NotSupportedException>(() => CaptureBodyAdmissionPolicies.EnsureSupported(
            FixturePolicy with { ContentSourceIds = [WindowsSourceCatalog.WinInetCaptureSourceId] }));
        Assert.Throws<NotSupportedException>(() => CaptureBodyAdmissionPolicies.EnsureSupported(
            CaptureBodyAdmissionPolicies.MetadataOnly with { ContentSourceIds = [WindowsSourceCatalog.ContentFixtureSourceId] }));
    }

    [Fact(DisplayName = "R17: a content request is admitted only with a scope that holds before persistence: its processes, every channel named")]
    public void AContentRequestIsAdmittedOnlyWithItsScope()
    {
        ContentCaptureDecision decision = ContentCapturePolicyCompiler.Compile(Request(["*"]));
        Assert.Equal((true, true, true, true),
            (decision.SourceBodyContractAvailable, decision.ScopeEnforceable, decision.CaptureImpactMeasured, decision.AdmissionPolicyAvailable));
        Assert.Contains("kept only from processes 4242", decision.AvailabilityReason, StringComparison.Ordinal);
        Assert.Contains("every channel of theirs", decision.AvailabilityReason, StringComparison.Ordinal);

        CompiledBodyAdmissionPolicy policy = CaptureBodyAdmissionPolicies.ScopedContentRequest(decision);
        Assert.Equal((CaptureBodyAdmissionPolicies.ScopedContentRequestPolicyId, 4_096, 16L * 1024 * 1024, ContentInspectionMode.HexAndText),
            (policy.PolicyId, policy.ContentRecordLimit, policy.ContentSessionLimit, policy.ContentInspection!.Value));
        Assert.Equal([WindowsSourceCatalog.WinInetCaptureSourceId], policy.ContentSourceIds);

        // A channel the source cannot select before anything is kept is refused, not trusted; "*" stands alone.
        ContentCaptureDecision named = ContentCapturePolicyCompiler.Compile(Request(["host:example.test"]));
        Assert.False(named.AdmissionPolicyAvailable);
        Assert.Contains("cannot select channels", named.AvailabilityReason, StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => CaptureBodyAdmissionPolicies.ScopedContentRequest(named));
        Assert.Contains("stands alone", ContentCapturePolicyCompiler.Validate(Request(["*", "host:example.test"])), StringComparison.Ordinal);

        // Only a source admitted for requests may be named by a request's policy.
        Assert.False(CaptureBodyAdmissionPolicies.AdmitsContentRequests(WindowsSourceCatalog.RpcSourceId));
        Assert.Throws<NotSupportedException>(() => CaptureBodyAdmissionPolicies.EnsureSupported(
            policy with { ContentSourceIds = [WindowsSourceCatalog.RpcSourceId] }));
    }

    [Fact(DisplayName = "P28: WinINet's payload-producing capture is enabled by an explicit content request only, for its named processes alone")]
    public void AnAdmittedRequestCompilesAScopedCapture()
    {
        // No profile of the catalog enables the source by default: only a request that names it and its processes does.
        Assert.DoesNotContain(CaptureProfileCatalog.All.Concat(CaptureProfileCatalog.Fixtures), profile =>
            profile.Sources.Any(source => source.SourceId == WindowsSourceCatalog.WinInetCaptureSourceId));

        ContentCaptureRequest request = Request(["*"]);
        CompiledBodyAdmissionPolicy policy = CaptureBodyAdmissionPolicies.ScopedContentRequest(ContentCapturePolicyCompiler.Compile(request));
        SourceAdmissionPlan http = AdmissionPlanCompiler.Compile(Source, ManifestParser.Parse(Manifest), 1, bodyPolicy: policy);
        Assert.All(http.Events, descriptor => Assert.Single(descriptor.Slots, slot => slot.Kind == AdmittedSlotKind.Content));

        EffectiveCapturePlan plan = CaptureProfileCompiler.Compile(
            new(CaptureProfileKind.Content, Content: request),
            new SourcePlanCompilation([Lifecycle(), http], []));
        Assert.True(plan.CanStart);
        Assert.Equal(("content", CaptureBodyAdmissionPolicies.ScopedContentRequestPolicyId), (plan.EffectiveProfileId, plan.BodyPolicy!.PolicyId));
        Assert.True(plan.Content!.AdmissionPolicyAvailable);

        // WinINet's provider is enabled for the named process alone; lifecycle stays whole-machine metadata.
        Assert.Equal([4_242], plan.Providers.Single(provider => provider.SourceId == WindowsSourceCatalog.WinInetCaptureSourceId).ProcessIdsToInclude);
        Assert.Empty(plan.Providers.Single(provider => provider.SourceId == WindowsSourceCatalog.KernelProcessSourceId).ProcessIdsToInclude);
        Assert.Equal(ProviderProcessScope.ProcessFiltered,
            plan.Scope.Sources.Single(source => source.SourceId == WindowsSourceCatalog.WinInetCaptureSourceId).ProcessScope);
        Assert.Contains("of processes 4242 only", plan.CollectionStatement, StringComparison.Ordinal);
    }

    private static ContentCaptureRequest Request(IReadOnlyList<string> channels) => new()
    {
        SourceId = WindowsSourceCatalog.WinInetCaptureSourceId,
        Mechanism = Mechanism.Http,
        ProcessIds = [4_242],
        ChannelSelectors = channels,
        MaximumRecordBytes = 4_096,
        MaximumSessionBytes = 16L * 1024 * 1024,
        Retention = ContentRetentionMode.StopAtLimit,
        Inspection = ContentInspectionMode.HexAndText,
    };

    /// <summary>A lifecycle plan of one admitted descriptor, which is all the profile compiler needs of it here.</summary>
    private static SourceAdmissionPlan Lifecycle()
    {
        Guid provider = Guid.Parse("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716");
        return new()
        {
            SourceId = WindowsSourceCatalog.KernelProcessSourceId,
            ProviderGuid = provider,
            SourceIndex = 0,
            Events =
            [
                new AdmittedEventPlan
                {
                    SourceIndex = 0,
                    ProviderGuid = provider,
                    EventId = 1,
                    Version = 0,
                    Name = "fixture",
                    Mechanism = Mechanism.ProcessLifecycle,
                    Layer = ObservationLayer.Lifecycle,
                    Kind = ObservationKind.Create,
                    Direction = Direction.DirectionNotApplicable,
                    MinimumBodyLength = 0,
                    PointerSize = 8,
                    SchemaFingerprint = "sha256:fixture-lifecycle",
                    BodyPolicy = CaptureBodyAdmissionPolicies.MetadataOnly,
                    Slots = [],
                    FieldReport = [],
                },
            ],
            Diagnostics = [],
        };
    }
    /// <summary>A machine that registers WinINet's capture provider with the layout ADR-037 measured, and nothing else.</summary>
    private sealed class WinInetMetadata : IEtwMetadataSource
    {
        private static readonly Guid Provider = Guid.Parse("a70ff94f-570b-4979-ba5c-e59c9feab61b");

        public bool IsAvailable => true;

        public string? UnavailableReason => null;

        public RegisteredProvider? TryResolveProvider(string providerName) =>
            string.Equals(providerName, "Microsoft-Windows-WinINet-Capture", StringComparison.Ordinal) ? new(providerName, Provider) : null;

        public int CountPublishedProviders() => 1;

        public ManifestReadResult TryReadManifest(Guid providerGuid) => providerGuid == Provider
            ? ManifestReadResult.Success(Manifest)
            : ManifestReadResult.Failure("not registered in this test");

        public ProviderSchema? TryReadClassic(Guid classGuid, string className, int version, IReadOnlyList<int> opcodes) => null;
    }
}
