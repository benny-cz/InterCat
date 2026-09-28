using System.Diagnostics.Tracing;
using InterCat.Domain;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// The content fixture's admission (ADR-036): its layout read from the type that raises it, its message a content slot
/// sized by the count before it, and only under the one reviewed scoped content policy; every other source of the same
/// capture keeps metadata only.
/// </summary>
public sealed class ContentFixtureAdmissionTests
{
    private static readonly CompiledBodyAdmissionPolicy Scoped =
        CaptureBodyAdmissionPolicies.ScopedContentFixture(4_096, 16L * 1024 * 1024, ContentInspectionMode.HexAndText);

    private static WindowsSourceDefinition Fixture => WindowsSourceCatalog.Find(WindowsSourceCatalog.ContentFixtureSourceId)!;

    [Fact(DisplayName = "§11.2: the content fixture's layout is read from the type that raises it, its message sized by the count before it")]
    public void TheFixtureLayoutComesFromItsType()
    {
        // An instrument of InterCat's own, not a capability of Windows: found, but never in the machine's report.
        Assert.Contains(WindowsSourceCatalog.Fixtures, source => source.SourceId == WindowsSourceCatalog.ContentFixtureSourceId);
        Assert.DoesNotContain(WindowsSourceCatalog.All, source => source.SourceId == WindowsSourceCatalog.ContentFixtureSourceId);

        ProviderSchema schema = ManifestParser.Parse(Fixture.EmbeddedManifest!);
        Assert.Equal(EventSource.GetGuid(typeof(ContentFixtureEventSource)), schema.ProviderGuid);
        ProviderSchemaEvent sent = schema.FindEvent(ContentFixtureEventSource.MessageSentId, ContentFixtureEventSource.EventVersion)!;
        Assert.Equal(
            [
                ("processId", FieldWidthKind.Fixed, (string?)null), ("conversation", FieldWidthKind.Fixed, null),
                ("messageSize", FieldWidthKind.Fixed, null), ("message", FieldWidthKind.Variable, "messageSize"),
            ],
            sent.Fields.Select(field => (field.Name, field.WidthKind, field.LengthField)));

        SourceAdmissionPlan plan = AdmissionPlanCompiler.Compile(Fixture, schema, 0, bodyPolicy: Scoped);
        Assert.Equal(2, plan.Events.Count);
        AdmittedEventPlan message = plan.Events.Single(descriptor => descriptor.EventId == ContentFixtureEventSource.MessageSentId);
        Assert.Equal((Mechanism.ApplicationSdk, ObservationLayer.Application, ObservationKind.Send, Direction.Outbound),
            (message.Mechanism, message.Layer, message.Kind, message.Direction));
        AdmittedSlotPlan content = Assert.Single(message.Slots, slot => slot.Kind == AdmittedSlotKind.Content);
        Assert.Equal((16, 12, 4, ContentEvidenceClassification.ApplicationPayload, ContentFieldEncoding.Binary),
            (content.Offset, content.LengthOffset!.Value, content.LengthWidth!.Value, content.ContentClassification!.Value,
                content.ContentEncoding!.Value));
        Assert.Contains(message.Slots, slot => slot is { FieldName: "messageSize", Role: FieldRole.ByteCount, Width: 4 });

        // The owner is the process the payload names, so no application provider's header is read as one (ADR-030).
        Assert.Contains(message.Slots, slot => slot is { FieldName: "processId", Role: FieldRole.ProcessAttribution, Offset: 0, Width: 4 });
        Assert.False(RecordAttribution.RaisedByTheirProcess(Mechanism.ApplicationSdk));
        Assert.Equal(16, message.MinimumBodyLength);
        Assert.True(message.PointerWidthIndependent);

        // Under the metadata policy the message is not admitted, and says why; its length still is.
        SourceAdmissionPlan metadata = AdmissionPlanCompiler.Compile(Fixture, schema, 0);
        AdmittedEventPlan withoutContent = metadata.Events.Single(descriptor => descriptor.EventId == ContentFixtureEventSource.MessageSentId);
        Assert.DoesNotContain(withoutContent.Slots, slot => slot.Kind == AdmittedSlotKind.Content);
        Assert.Contains(withoutContent.FieldReport, field => field is { Name: "message", Availability: FieldAvailability.ProfileDisabled });
        Assert.NotEqual(message.SchemaFingerprint, withoutContent.SchemaFingerprint);
    }

    [Fact(DisplayName = "ADR-036: only the reviewed scoped content policy, inside its bounds, is enforceable")]
    public void OnlyTheReviewedContentPolicyIsEnforceable()
    {
        CaptureBodyAdmissionPolicies.EnsureSupported(CaptureBodyAdmissionPolicies.MetadataOnly);
        CaptureBodyAdmissionPolicies.EnsureSupported(Scoped);
        Assert.True(Scoped.KeepsContent);
        Assert.False(CaptureBodyAdmissionPolicies.MetadataOnly.KeepsContent);

        Assert.Throws<NotSupportedException>(() => CaptureBodyAdmissionPolicies.ScopedContentFixture(0, 1_024, ContentInspectionMode.HexAndText));
        Assert.Throws<NotSupportedException>(() => CaptureBodyAdmissionPolicies.ScopedContentFixture(
            CaptureBodyAdmissionPolicies.MaximumContentRecordLimit + 1, long.MaxValue, ContentInspectionMode.HexAndText));
        Assert.Throws<NotSupportedException>(() => CaptureBodyAdmissionPolicies.ScopedContentFixture(4_096, 1_024, ContentInspectionMode.HexAndText));
        Assert.Throws<NotSupportedException>(() => CaptureBodyAdmissionPolicies.EnsureSupported(Scoped with { PolicyId = "scoped-content-other-v1" }));
        Assert.Throws<NotSupportedException>(() => CaptureBodyAdmissionPolicies.EnsureSupported(Scoped with { ContentInspection = null }));
        Assert.Throws<NotSupportedException>(() => CaptureBodyAdmissionPolicies.EnsureSupported(
            CaptureBodyAdmissionPolicies.MetadataOnly with { ContentRecordLimit = 64 }));
    }

    [Fact(DisplayName = "ADR-036: a scoped content policy reaches only the fixture; every other source of the capture keeps metadata")]
    public void OnlyTheFixtureKeepsContent()
    {
        var inventory = new CapabilityInventoryProbe(new ClassicOnlyMetadata());
        SourcePlanCompilation compilation = inventory.CompilePlanSet(
            [WindowsSourceCatalog.ContentFixtureSourceId, WindowsSourceCatalog.KernelAlpcSourceId], Scoped);

        Assert.DoesNotContain(compilation.Issues, issue => issue.Severity == SourcePlanIssueSeverity.Refusal);
        SourceAdmissionPlan fixture = compilation.Plans.Single(plan => plan.SourceId == WindowsSourceCatalog.ContentFixtureSourceId);
        SourceAdmissionPlan alpc = compilation.Plans.Single(plan => plan.SourceId == WindowsSourceCatalog.KernelAlpcSourceId);
        Assert.All(fixture.Events, descriptor => Assert.Equal(CaptureBodyAdmissionPolicies.ScopedContentFixturePolicyId, descriptor.BodyPolicy.PolicyId));
        Assert.All(alpc.Events, descriptor => Assert.Equal(CaptureBodyAdmissionPolicies.MetadataOnlyPolicyId, descriptor.BodyPolicy.PolicyId));

        // The profile that asks for it is found by name but never offered with the machine's profiles, which the broker
        // advertises; it compiles the scoped policy, and the Content request profile stays unavailable.
        Assert.Same(Assert.Single(CaptureProfileCatalog.Fixtures), CaptureProfileCatalog.Find("content-fixture"));
        Assert.DoesNotContain(CaptureProfileCatalog.All, profile => profile.Kind == CaptureProfileKind.ContentFixture);
        EffectiveCapturePlan plan = CaptureProfileCompiler.Compile(new(CaptureProfileKind.ContentFixture), compilation with
        {
            Plans = [.. compilation.Plans.Where(candidate => candidate.SourceId == WindowsSourceCatalog.ContentFixtureSourceId)],
        });
        Assert.Equal(CaptureBodyAdmissionPolicies.ScopedContentFixturePolicyId, plan.BodyPolicy!.PolicyId);
        Assert.Contains(plan.SourceDecisions, decision => decision.SourceId == WindowsSourceCatalog.ContentFixtureSourceId
            && decision.State == ProfileSourceDecisionState.Included);
        Assert.False(CaptureProfileCatalog.Find(CaptureProfileKind.Content).CompilationAvailable);
    }

    /// <summary>A machine that registers no provider and describes only the ALPC class: the fixture needs neither.</summary>
    private sealed class ClassicOnlyMetadata : IEtwMetadataSource
    {
        public bool IsAvailable => true;

        public string? UnavailableReason => null;

        public RegisteredProvider? TryResolveProvider(string providerName) => null;

        public int CountPublishedProviders() => 0;

        public ManifestReadResult TryReadManifest(Guid providerGuid) => ManifestReadResult.Failure("not registered in this test");

        public ProviderSchema? TryReadClassic(Guid classGuid, string className, int version, IReadOnlyList<int> opcodes) =>
            new(className, classGuid, "alpc-test-schema", new Dictionary<string, ulong>(),
            [
                .. opcodes.Select(opcode => new ProviderSchemaEvent(0, version, null, null, null, null, opcode, null, [], 0, null,
                    [ProviderSchemaField.Create("MessageID", "win:UInt32")])),
            ]);
    }
}
