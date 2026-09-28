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

    [Fact(DisplayName = "ADR-037: WinINet's capture events are HTTP messages: exchange, buffer order and ends kept, length measured")]
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

    [Fact(DisplayName = "ADR-036: a scoped content policy keeps the content of the sources it names, and of no other")]
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

    [Fact(DisplayName = "ADR-037: a content request for WinINet's capture compiles no policy until its channel scope and overhead are settled")]
    public void AContentRequestWaitsForItsAdmission()
    {
        ContentCaptureDecision decision = ContentCapturePolicyCompiler.Compile(new()
        {
            SourceId = WindowsSourceCatalog.WinInetCaptureSourceId,
            Mechanism = Mechanism.Http,
            ProcessIds = [4_242],
            ChannelSelectors = ["*"],
            MaximumRecordBytes = 4_096,
            MaximumSessionBytes = 16L * 1024 * 1024,
            Retention = ContentRetentionMode.StopAtLimit,
            Inspection = ContentInspectionMode.HexAndText,
        });

        Assert.Equal((true, false, false, false),
            (decision.SourceBodyContractAvailable, decision.ScopeEnforceable, decision.CaptureImpactMeasured, decision.AdmissionPolicyAvailable));
        Assert.Contains("process/channel scope is not proven enforceable", decision.AvailabilityReason, StringComparison.Ordinal);
        Assert.Contains("capture impact is unmeasured", decision.AvailabilityReason, StringComparison.Ordinal);
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
