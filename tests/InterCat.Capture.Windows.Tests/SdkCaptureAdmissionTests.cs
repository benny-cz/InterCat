using System.Diagnostics.Tracing;
using InterCat.Capture.Journal;
using InterCat.Capture.Recording;
using InterCat.Domain;
using InterCat.Sdk;
using InterCat.Storage;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// A capture's admission of the cooperative SDK (ADR-103): its layout read from the type that raises it, every field at a
/// fixed offset, each record owned by the process its payload names, a channel or an operation by the identity the
/// application chose, a message by its channel's epoch and sequence as source fields, an operation's parent its related
/// activity - and the sdk profile that asks for it beside lifecycle.
/// </summary>
public sealed class SdkCaptureAdmissionTests
{
    private static readonly Guid Frames = Guid.Parse("6f1c8f4e-2b8e-4a40-9f7c-0d6a7f3e5a11");

    private static WindowsSourceDefinition Sdk => WindowsSourceCatalog.Find(WindowsSourceCatalog.CooperativeSdkSourceId)!;

    [Fact(DisplayName = "§15: the SDK's layout is read from the type that raises it, each of its seven events an application record at a fixed offset, owned by the process its payload names")]
    public void TheLayoutComesFromTheSdk()
    {
        // What an application cooperating says, not a capability of Windows: found, but never in the machine's report.
        Assert.Contains(WindowsSourceCatalog.Cooperative, source => source.SourceId == WindowsSourceCatalog.CooperativeSdkSourceId);
        Assert.DoesNotContain(WindowsSourceCatalog.All, source => source.SourceId == WindowsSourceCatalog.CooperativeSdkSourceId);
        Assert.DoesNotContain(WindowsSourceCatalog.Fixtures, source => source.SourceId == WindowsSourceCatalog.CooperativeSdkSourceId);

        ProviderSchema schema = ManifestParser.Parse(Sdk.EmbeddedManifest!);
        Assert.Equal(EventSource.GetGuid(typeof(InterCatSdkEventSource)), schema.ProviderGuid);
        SourceAdmissionPlan plan = Compiled();
        Assert.Empty(plan.Diagnostics);
        Assert.Equal(
            [
                (1, ObservationKind.Inventory, Direction.DirectionNotApplicable), (2, ObservationKind.Open, Direction.DirectionNotApplicable),
                (3, ObservationKind.Close, Direction.DirectionNotApplicable), (4, ObservationKind.Send, Direction.Outbound),
                (5, ObservationKind.Receive, Direction.Inbound), (6, ObservationKind.RequestStart, Direction.DirectionNotApplicable),
                (7, ObservationKind.RequestEnd, Direction.DirectionNotApplicable),
            ],
            plan.Events.Select(descriptor => (descriptor.EventId, descriptor.Kind, descriptor.Direction)).OrderBy(descriptor => descriptor.EventId));
        Assert.All(plan.Events, descriptor =>
        {
            Assert.Equal((Mechanism.ApplicationSdk, ObservationLayer.Application, InterCatSdkEventSource.EventVersion),
                (descriptor.Mechanism, descriptor.Layer, (byte)descriptor.Version));

            // Every field the catalog asks for is admitted, the owner first, at the payload's start (ADR-030).
            Assert.All(descriptor.FieldReport, field => Assert.Equal(FieldAvailability.Present, field.Availability));
            Assert.Contains(descriptor.Slots, slot => slot is { FieldName: "processId", Role: FieldRole.ProcessAttribution, Offset: 0, Width: 4 });
            Assert.True(descriptor.PointerWidthIndependent);
        });
        Assert.False(RecordAttribution.RaisedByTheirProcess(Mechanism.ApplicationSdk));

        // A publication: its channel copied whole as the record's identifier, its epoch, sequence, copied length and clock
        // reading source fields, its logical length the record's application payload.
        AdmittedEventPlan published = Plan(plan, InterCatSdkEventSource.MessagePublishedId);
        Assert.Equal(
            [
                ("processId", FieldRole.ProcessAttribution, AdmittedSlotKind.Numeric, 0, (SourceField?)null),
                ("channel", FieldRole.CorrelationKey, AdmittedSlotKind.Identifier, 4, null),
                ("epoch", FieldRole.CorrelationKey, AdmittedSlotKind.Numeric, 20, SourceField.ChannelEpoch),
                ("sequence", FieldRole.CorrelationKey, AdmittedSlotKind.Numeric, 28, SourceField.MessageSequence),
                ("logicalLength", FieldRole.ByteCount, AdmittedSlotKind.Numeric, 36, null),
                ("copiedLength", FieldRole.Unclassified, AdmittedSlotKind.Numeric, 44, SourceField.CopiedLength),
                ("sourceTicks", FieldRole.Timing, AdmittedSlotKind.Numeric, 52, SourceField.SourceTicks),
            ],
            published.Slots.Select(slot => (slot.FieldName, slot.Role, slot.Kind, slot.Offset, slot.SourceField)));
        Assert.Contains(published.Slots, slot => slot is
        {
            FieldName: "logicalLength", Role: FieldRole.ByteCount, Unit: MeasurementUnit.Bytes, ByteDomain: ByteDomain.ApplicationPayload,
        });
        Assert.Equal(60, published.MinimumBodyLength);
        Assert.Equal(SourceField.ConsumerNumber,
            Plan(plan, InterCatSdkEventSource.MessageConsumedId).Slots.Single(slot => slot.FieldName == "consumer").SourceField);

        // The announcement's minor version and clock frequency, a channel's kind, and each text - the library's version, a
        // channel's or an operation's name - read as the record's name after every fixed field.
        Assert.Equal(
            [("schemaMinor", 8, SourceField.SdkSchemaMinor), ("ticksPerSecond", 12, SourceField.SourceClockFrequency)],
            Plan(plan, InterCatSdkEventSource.SdkStartedId).Slots.Where(slot => slot.SourceField is not null)
                .Select(slot => (slot.FieldName, slot.Offset, slot.SourceField!.Value)));
        Assert.Equal(
            [(InterCatSdkEventSource.SdkStartedId, "library", 20), (InterCatSdkEventSource.ChannelOpenedId, "name", 32),
                (InterCatSdkEventSource.OperationStartedId, "name", 28)],
            plan.Events.SelectMany(descriptor => descriptor.Slots.Where(slot => slot.Kind == AdmittedSlotKind.ResourceName)
                    .Select(slot => (descriptor.EventId, slot.FieldName, slot.Offset)))
                .OrderBy(text => text.EventId));
        Assert.Equal((28, SourceField.ChannelKind),
            Plan(plan, InterCatSdkEventSource.ChannelOpenedId).Slots.Where(slot => slot.FieldName == "kind").Select(slot => (slot.Offset, slot.SourceField!.Value)).Single());

        // An operation by its identity, its completion's status the record's status; no field names its parent, which is
        // the record's related activity.
        Assert.All(new[] { InterCatSdkEventSource.OperationStartedId, InterCatSdkEventSource.OperationCompletedId }, id =>
            Assert.Contains(Plan(plan, id).Slots, slot => slot is { FieldName: "operation", Kind: AdmittedSlotKind.Identifier, Offset: 4 }));
        Assert.Contains(Plan(plan, InterCatSdkEventSource.OperationCompletedId).Slots, slot => slot is { FieldName: "status", Role: FieldRole.Status, Offset: 20 });
    }

    [Fact(DisplayName = "§15: an admitted publication is an application row owned by the process it names, its channel the row's identifier, its length its payload, and its epoch, sequence, copy and clock source fields; an operation's start keeps its parent as its related activity")]
    public void AnAdmittedRecordIsAnApplicationRow()
    {
        SourceAdmissionPlan plan = Compiled();
        SourceClockDescriptor clock = new(
            ClockId.Derive("sdk-admission-tests"),
            HostId.Derive("sdk-admission-tests"),
            SourceClockKind.Monotonic,
            TimestampEncoding.Qpc,
            10_000_000,
            0,
            TimestampRounding.NearestEven,
            SourceClockMath.SessionTicksPerSecond * 3_600);
        var mapper = new AdmittedEventEnvelopeMapper([plan], clock.Id);
        var normalizer = new ObservationNormalizerV1(clock);

        // A publication raised in process 4242, its header naming another: the payload's process owns it.
        AdmittedEvent publication = Record(plan, InterCatSdkEventSource.MessagePublishedId, raisedBy: 7,
            ("processId", 4_242), ("epoch", 2), ("sequence", 7), ("logicalLength", 4_096), ("copiedLength", -1), ("sourceTicks", 9_000));
        publication.SetIdentifier(Frames);
        publication.TimestampQpc = 5_000;
        publication.RecordOrdinal = 1;
        AdmittedEventPlan published = Plan(plan, InterCatSdkEventSource.MessagePublishedId);
        using RecordEnvelopeV1 envelope = mapper.ToEnvelope(publication, published, CaptureId.New());
        ObservationRowV1 row = normalizer.ToRow(envelope, published);
        Assert.Equal(
            (Mechanism.ApplicationSdk, ObservationLayer.Application, ObservationKind.Send, Direction.Outbound),
            (row.Mechanism, row.Layer, row.Kind, row.Direction));
        Assert.Equal(((int?)4_242, 7, QualityLevel.Proven), (row.OwnerProcessId, row.HeaderProcessId, row.AttributionQuality));
        Assert.Equal((Guid?)Frames, row.SourceIdentifier);
        Assert.Equal(((long?)4_096, (ByteDomain?)ByteDomain.ApplicationPayload, (AccountingSide?)AccountingSide.SendSide),
            (row.ByteValue, row.ByteDomain, row.AccountingSide));
        Assert.Null(row.Validate());

        // The copy the publisher does not know is kept as it said it, -1, beside the length, never in place of it.
        Assert.Equal(
            [(SourceField.ChannelEpoch, 2L), (SourceField.MessageSequence, 7L), (SourceField.CopiedLength, -1L), (SourceField.SourceTicks, 9_000L)],
            ObservationNormalizerV1.FieldRows(envelope, published, row).Select(field => (field.Field, field.Value!.Value)).OrderBy(field => field.Field));

        // An operation's start under a parent: its identity the row's identifier, its parent the row's related activity.
        var parent = Guid.Parse("0b4f1d25-5e63-4b77-8f0e-9c1d2a3b4c5d");
        var render = Guid.Parse("3c9e2a71-8d44-4f0b-b6a5-2e7f9d1c0a83");
        AdmittedEvent start = Record(plan, InterCatSdkEventSource.OperationStartedId, raisedBy: 4_242, ("processId", 4_242), ("sourceTicks", 9_500));
        start.SetIdentifier(render);
        start.SetName("render", truncated: false);
        start.RelatedActivityId = parent;
        start.TimestampQpc = 5_500;
        start.RecordOrdinal = 2;
        AdmittedEventPlan started = Plan(plan, InterCatSdkEventSource.OperationStartedId);
        using RecordEnvelopeV1 begun = mapper.ToEnvelope(start, started, CaptureId.New());
        ObservationRowV1 operation = normalizer.ToRow(begun, started);
        Assert.Equal((ObservationKind.RequestStart, (Guid?)render, (Guid?)parent, "render"),
            (operation.Kind, operation.SourceIdentifier, operation.RelatedActivityId, operation.ResourceName));
        Assert.Equal((SourceField.SourceTicks, (long?)9_500), ObservationNormalizerV1.FieldRows(begun, started, operation).Select(field => (field.Field, field.Value)).Single());
        Assert.Null(operation.Validate());
    }

    [Fact(DisplayName = "§15: the sdk profile asks for lifecycle and the SDK's source, admitting metadata alone, and is offered where every opt-in profile is")]
    public void TheProfileAsksForTheSdk()
    {
        CaptureProfileDescriptor profile = Assert.Single(CaptureProfileCatalog.All, candidate => candidate.Kind == CaptureProfileKind.CooperativeSdk);
        Assert.Same(profile, CaptureProfileCatalog.Find("sdk"));
        Assert.Equal((AdmissionMode.MetadataOnly, true, true), (profile.Admission, profile.CompilationAvailable, profile.RequestPreviewAvailable));
        Assert.Equal(
            [(WindowsSourceCatalog.KernelProcessSourceId, true, true), (WindowsSourceCatalog.CooperativeSdkSourceId, true, false)],
            profile.Sources.Select(source => (source.SourceId, source.Required, source.RequiresMeasuredOverhead)));
        Assert.Equal(OverheadClass.Unmeasured, profile.Overhead);

        // Its source compiles from the manifest it carries, on a machine that registers nothing, and is included.
        var inventory = new CapabilityInventoryProbe(new NothingRegistered());
        SourcePlanCompilation compilation = inventory.CompilePlanSet([WindowsSourceCatalog.CooperativeSdkSourceId]);
        Assert.DoesNotContain(compilation.Issues, issue => issue.Severity == SourcePlanIssueSeverity.Refusal);
        EffectiveCapturePlan compiled = CaptureProfileCompiler.Compile(new(CaptureProfileKind.CooperativeSdk), compilation);
        Assert.Same(CaptureBodyAdmissionPolicies.MetadataOnly, compiled.BodyPolicy);
        Assert.Contains(compiled.SourceDecisions, decision => decision.SourceId == WindowsSourceCatalog.CooperativeSdkSourceId
            && decision.State == ProfileSourceDecisionState.Included);

        // The capability catalog names it as the source that records what an application says of itself.
        Assert.Equal([WindowsSourceCatalog.CooperativeSdkSourceId],
            MechanismCatalog.Entries.Single(entry => entry.Mechanism == Mechanism.ApplicationSdk).SourceIds);
    }

    [Fact(DisplayName = "§15: the domain names the SDK's provider and events as the SDK's own type declares them, so a derivation reads its records without the library an application ships")]
    public void TheDomainNamesTheSdkAsItDeclaresItself()
    {
        Assert.Equal((InterCatSdkEventSource.ProviderName, EventSource.GetGuid(typeof(InterCatSdkEventSource))),
            (CooperativeSdk.ProviderName, CooperativeSdk.ProviderId));
        Assert.Equal(
            (InterCatSdkEventSource.SdkStartedId, InterCatSdkEventSource.ChannelOpenedId, InterCatSdkEventSource.ChannelClosedId,
                InterCatSdkEventSource.MessagePublishedId, InterCatSdkEventSource.MessageConsumedId,
                InterCatSdkEventSource.OperationStartedId, InterCatSdkEventSource.OperationCompletedId),
            ((int)CooperativeSdk.SdkStarted, (int)CooperativeSdk.ChannelOpened, (int)CooperativeSdk.ChannelClosed,
                (int)CooperativeSdk.MessagePublished, (int)CooperativeSdk.MessageConsumed, (int)CooperativeSdk.OperationStarted,
                (int)CooperativeSdk.OperationCompleted));
    }

    private static SourceAdmissionPlan Compiled() => AdmissionPlanCompiler.Compile(Sdk, ManifestParser.Parse(Sdk.EmbeddedManifest!), 0);

    private static AdmittedEventPlan Plan(SourceAdmissionPlan plan, int eventId) =>
        plan.Events.Single(descriptor => descriptor.EventId == eventId);

    private static AdmittedEvent Record(SourceAdmissionPlan plan, int eventId, int raisedBy, params (string Field, long Value)[] fields)
    {
        AdmittedEventPlan descriptor = Plan(plan, eventId);
        var admitted = new AdmittedEvent
        {
            SourceIndex = descriptor.SourceIndex,
            EventId = eventId,
            Version = descriptor.Version,
            HeaderProcessId = raisedBy,
        };
        foreach ((string field, long value) in fields)
        {
            int index = descriptor.Slots.ToList().FindIndex(slot => slot.FieldName == field);
            Assert.True(index >= 0, field);
            admitted.SetSlot(index, value);
        }

        return admitted;
    }

    /// <summary>A machine that registers no provider: the SDK's source needs none.</summary>
    private sealed class NothingRegistered : IEtwMetadataSource
    {
        public bool IsAvailable => true;

        public string? UnavailableReason => null;

        public RegisteredProvider? TryResolveProvider(string providerName) => null;

        public int CountPublishedProviders() => 0;

        public ManifestReadResult TryReadManifest(Guid providerGuid) => ManifestReadResult.Failure("not registered in this test");

        public ProviderSchema? TryReadClassic(Guid classGuid, string className, int version, IReadOnlyList<int> opcodes) => null;
    }
}
