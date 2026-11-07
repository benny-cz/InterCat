using System.Diagnostics.Tracing;
using System.Globalization;
using System.Xml.Linq;
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

    /// <summary>The events a later minor version adds a field to here: one of fixed fields, one ending in text, one completion.</summary>
    private static readonly string[] LaterMinorTemplates = ["MessagePublishedArgs", "ChannelOpenedArgs", "OperationCompletedArgs"];

    private static WindowsSourceDefinition Sdk => WindowsSourceCatalog.Find(WindowsSourceCatalog.CooperativeSdkSourceId)!;

    [Fact(DisplayName = "§15: the SDK's layout is read from the type that raises it, each of its nine events an application record at a fixed offset, owned by the process its payload names")]
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
                (7, ObservationKind.RequestEnd, Direction.DirectionNotApplicable), (8, ObservationKind.Send, Direction.Outbound),
                (9, ObservationKind.Receive, Direction.Inbound),
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

        // A batch: its first sequence the message sequence, its count a source field of its own, its messages' lengths
        // together the record's application payload, and its reader where it is a consumption (ADR-106).
        AdmittedEventPlan batch = Plan(plan, InterCatSdkEventSource.MessagesPublishedId);
        Assert.Equal(
            [
                ("processId", FieldRole.ProcessAttribution, AdmittedSlotKind.Numeric, 0, (SourceField?)null),
                ("channel", FieldRole.CorrelationKey, AdmittedSlotKind.Identifier, 4, null),
                ("epoch", FieldRole.CorrelationKey, AdmittedSlotKind.Numeric, 20, SourceField.ChannelEpoch),
                ("firstSequence", FieldRole.CorrelationKey, AdmittedSlotKind.Numeric, 28, SourceField.MessageSequence),
                ("count", FieldRole.Unclassified, AdmittedSlotKind.Numeric, 36, SourceField.MessageCount),
                ("logicalLength", FieldRole.ByteCount, AdmittedSlotKind.Numeric, 44, null),
                ("copiedLength", FieldRole.Unclassified, AdmittedSlotKind.Numeric, 52, SourceField.CopiedLength),
                ("sourceTicks", FieldRole.Timing, AdmittedSlotKind.Numeric, 60, SourceField.SourceTicks),
            ],
            batch.Slots.Select(slot => (slot.FieldName, slot.Role, slot.Kind, slot.Offset, slot.SourceField)));
        Assert.Equal(68, batch.MinimumBodyLength);
        Assert.Equal(
            [("firstSequence", SourceField.MessageSequence), ("count", SourceField.MessageCount), ("consumer", SourceField.ConsumerNumber)],
            Plan(plan, InterCatSdkEventSource.MessagesConsumedId).Slots.Where(slot => slot.FieldName is "firstSequence" or "count" or "consumer")
                .Select(slot => (slot.FieldName, slot.SourceField!.Value)));

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

        // It asks the SDK's provider for EventSource's own keywords, without which it hears none of the SDK's events, and every
        // keyword the SDK may define but content's; and it enables no content event, so no application builds a message's
        // content for it (ADR-107).
        ProviderEnablementRequest sdk = Assert.Single(compiled.Providers, provider => provider.SourceId == WindowsSourceCatalog.CooperativeSdkSourceId);
        Assert.Equal((0x0000_F000_FFFF_FFFEUL, 0UL), (sdk.MatchAnyKeyword, sdk.MatchAnyKeyword & (ulong)InterCatSdkEventSource.Keywords.Content));
        Assert.Equal(Enumerable.Range(1, 9), sdk.EventIdsToEnable);

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

        // A batch's events, and the most messages one says, as the SDK raises and bounds them (ADR-106); a message's content
        // (ADR-107).
        Assert.Equal((InterCatSdkEventSource.MessagesPublishedId, InterCatSdkEventSource.MessagesConsumedId, (long)InterCatChannel.MaximumBatch),
            ((int)CooperativeSdk.MessagesPublished, (int)CooperativeSdk.MessagesConsumed, CooperativeSdk.MaximumBatch));
        Assert.Equal(InterCatSdkEventSource.MessageContentId, (int)CooperativeSdk.MessageContent);
    }

    [Fact(DisplayName = "§24: a capture keeps a later minor version's added field unread and safe - each event's known fields at the offsets it knows, a longer record read no further - and holds no descriptor of a later major version, whose records stay undecodable, never misread")]
    public void ALaterSchemaIsNeverMisread()
    {
        SourceAdmissionPlan known = Compiled();

        // A later minor adds a field at an event's end, after its text where it has one (`contracts/sdk-v1.md` §5).
        XDocument minor = XDocument.Parse(Sdk.EmbeddedManifest!);
        XNamespace events = "http://schemas.microsoft.com/win/2004/08/events";
        foreach (string template in LaterMinorTemplates)
        {
            minor.Descendants(events + "template").Single(element => (string?)element.Attribute("tid") == template)
                .Add(new XElement(events + "data", new XAttribute("name", "addedLater"), new XAttribute("inType", "win:Int64")));
        }

        SourceAdmissionPlan later = AdmissionPlanCompiler.Compile(Sdk, ManifestParser.Parse(minor.ToString()), 0);
        Assert.Empty(later.Diagnostics);
        Assert.Equal(
            known.Events.Select(Layout),
            later.Events.Select(Layout));
        Assert.DoesNotContain(later.Events.SelectMany(descriptor => descriptor.Slots), slot => slot.FieldName == "addedLater");

        // The capture's schema is the SDK's own, every event at the schema's major: a record of a later major names a version
        // no descriptor of its plan has, which the capture counts as undecodable rather than read by another version's layout.
        Assert.All(known.Events, descriptor => Assert.Equal(InterCatSdkEventSource.EventVersion, descriptor.Version));

        static string Layout(AdmittedEventPlan descriptor) => string.Join(", ",
            [string.Create(CultureInfo.InvariantCulture, $"{descriptor.EventId} v{descriptor.Version} from {descriptor.MinimumBodyLength}"),
                .. descriptor.Slots.Select(slot => string.Create(CultureInfo.InvariantCulture, $"{slot.FieldName}@{slot.Offset}:{slot.Kind}"))]);
    }

    [Fact(DisplayName = "§15: the native library's header declares InterCat.Sdk's provider and every event as the SDK's manifest does - identity, version, level, opcode, task and keyword - and the schema's version, so a capture admits a native application's records alike")]
    public void TheNativeHeaderIsTheSdksSchema()
    {
        string header = File.ReadAllText(Path.Combine(RepositoryRoot(), "sdk", "native", "include", "intercat_sdk.h"));

        // The provider's identity, as the header spells the GUID EventSource derives from the name.
        System.Text.RegularExpressions.Match guid = System.Text.RegularExpressions.Regex.Match(header,
            @"intercat_sdk_provider = \{ 0x(?<a>[0-9a-f]{8}), 0x(?<b>[0-9a-f]{4}), 0x(?<c>[0-9a-f]{4}), \{ (?<d>(0x[0-9a-f]{2}(, )?){8}) \} \}");
        Assert.True(guid.Success, "The header no longer spells its provider's identity.");
        byte[] tail = [.. guid.Groups["d"].Value.Split(", ").Select(octet => Convert.ToByte(octet[2..], 16))];
        var declared = new Guid(Convert.ToInt32(guid.Groups["a"].Value, 16), Convert.ToInt16(guid.Groups["b"].Value, 16),
            Convert.ToInt16(guid.Groups["c"].Value, 16), tail);
        Assert.Equal((EventSource.GetGuid(typeof(InterCatSdkEventSource)), CooperativeSdk.ProviderId), (declared, declared));

        // Each event's descriptor: id, version, channel, level, opcode, task, keyword - as the manifest declares each.
        XDocument manifest = XDocument.Parse(Sdk.EmbeddedManifest!);
        XNamespace events = "http://schemas.microsoft.com/win/2004/08/events";
        Dictionary<string, int> tasks = manifest.Descendants(events + "task")
            .ToDictionary(task => (string)task.Attribute("name")!, task => (int)task.Attribute("value")!);
        Dictionary<string, ulong> keywords = manifest.Descendants(events + "keyword")
            .ToDictionary(keyword => (string)keyword.Attribute("name")!, keyword => Convert.ToUInt64((string)keyword.Attribute("mask")!, 16));
        var opcodes = new Dictionary<string, int> { ["win:Start"] = 1, ["win:Stop"] = 2, ["win:Send"] = 9, ["win:Receive"] = 240 };
        string[] expected =
        [
            .. manifest.Descendants(events + "event").Where(element => (int)element.Attribute("value")! != 0)
                .OrderBy(element => (int)element.Attribute("value")!)
                .Select(element => string.Join(", ",
                    (int)element.Attribute("value")!, (int)element.Attribute("version")!, 0,
                    (string)element.Attribute("level")! == "win:Informational" ? 4 : -1,
                    element.Attribute("opcode") is { } opcode ? opcodes[opcode.Value] : 0,
                    element.Attribute("task") is { } task ? tasks[task.Value] : 0,
                    element.Attribute("keywords") is { } named
                        ? named.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Aggregate(0UL, (mask, name) => mask | keywords[name])
                        : 0UL)),
        ];
        string[] said =
        [
            .. System.Text.RegularExpressions.Regex.Matches(header, @"(?m)^#define INTERCAT_SDK_EVENT_[A-Z_]+ (?<descriptor>[0-9, ]+)\r?$")
                .Select(match => match.Groups["descriptor"].Value),
        ];
        Assert.Equal(expected, said);

        // The schema's version, as the header announces it, and the bounds and keyword its calls share with InterCat.Sdk's.
        Assert.Contains($"#define INTERCAT_SDK_SCHEMA_MAJOR {InterCatSdkEventSource.SchemaMajor}", header, StringComparison.Ordinal);
        Assert.Contains($"#define INTERCAT_SDK_SCHEMA_MINOR {InterCatSdkEventSource.SchemaMinor}", header, StringComparison.Ordinal);
        Assert.Contains($"#define INTERCAT_SDK_MAXIMUM_BATCH {InterCatChannel.MaximumBatch}", header, StringComparison.Ordinal);
        Assert.Contains($"#define INTERCAT_SDK_MAXIMUM_CONTENT {InterCatChannel.MaximumContent}", header, StringComparison.Ordinal);
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"#define INTERCAT_SDK_KEYWORD_CONTENT 0x{(ulong)InterCatSdkEventSource.Keywords.Content:x}"),
            header, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "InterCat.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
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
