using InterCat.Capture.Journal;
using InterCat.Capture.Recording;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Capture.Windows.WindowMessageGate;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// Win32k's message events admitted (ADR-087's addendum): a message's six ends compile from the provider's registered layout
/// to fixed slots - each end's queue message pointer, which the window message gate writes the message's number over, and a
/// send's receiving process - and never to a window, a thread, a message number or a parameter; through the gate each end is
/// admitted as its message's, and a record delivered out of the order raised is omitted, counted by its reason.
/// </summary>
public sealed class WindowMessageAdmissionTests
{
    // The provider's registered layout of its message pump events, as TDH gave it on Windows 10.0.26220 (ADR-086,
    // bench/results/ui-message-provider-inspection-20261105T163256Z); the first test holds it to this machine's.
    private const string Manifest = """
        <instrumentationManifest xmlns="http://schemas.microsoft.com/win/2004/08/events">
         <instrumentation><events>
          <provider name="Microsoft-Windows-Win32k" guid="{8c416c79-d49b-4f01-a467-e56d3aa8234c}">
           <keywords><keyword name="MessagePump" mask="0x400000" /></keywords>
           <templates>
            <template tid="QueuePostMessage">
             <data name="CallbackCount" inType="win:Int8" /><data name="pqmsg" inType="win:Pointer" />
             <data name="hwnd" inType="win:Pointer" /><data name="WParam" inType="win:Pointer" /><data name="LParam" inType="win:Pointer" />
             <data name="message" inType="win:UInt32" /><data name="inputReadyTimeMs" inType="win:UInt32" />
            </template>
            <template tid="SendMessageStart">
             <data name="CallbackCount" inType="win:Int8" /><data name="pqmsg" inType="win:Pointer" />
             <data name="hwnd" inType="win:Pointer" /><data name="WParam" inType="win:Pointer" /><data name="LParam" inType="win:Pointer" />
             <data name="message" inType="win:UInt32" /><data name="inputReadyTimeMs" inType="win:UInt32" />
             <data name="flags" inType="win:UInt32" /><data name="pidReceiver" inType="win:UInt32" /><data name="tidReceiver" inType="win:UInt32" />
            </template>
            <template tid="RetrievePostMessage">
             <data name="CallbackCount" inType="win:Int8" /><data name="pqmsg" inType="win:Pointer" />
             <data name="hwnd" inType="win:Pointer" /><data name="WParam" inType="win:Pointer" /><data name="LParam" inType="win:Pointer" />
             <data name="message" inType="win:UInt32" /><data name="inputReadyTimeMs" inType="win:UInt32" /><data name="flags" inType="win:UInt32" />
            </template>
            <template tid="RetrieveSendMessageStart">
             <data name="CallbackCount" inType="win:Int8" /><data name="pqmsg" inType="win:Pointer" />
             <data name="hwnd" inType="win:Pointer" /><data name="WParam" inType="win:Pointer" /><data name="LParam" inType="win:Pointer" />
             <data name="message" inType="win:UInt32" /><data name="inputReadyTimeMs" inType="win:UInt32" />
            </template>
            <template tid="SendMessageStop">
             <data name="CallbackCount" inType="win:Int8" /><data name="pqmsg" inType="win:Pointer" />
            </template>
            <template tid="RetrieveSendMessageStop">
             <data name="CallbackCount" inType="win:Int8" /><data name="pqmsg" inType="win:Pointer" />
            </template>
           </templates>
           <events>
            <event value="59" version="0" level="win:Informational" keywords="MessagePump" template="QueuePostMessage" />
            <event value="60" version="0" level="win:Informational" keywords="MessagePump" template="SendMessageStart" />
            <event value="61" version="0" level="win:Informational" keywords="MessagePump" template="RetrievePostMessage" />
            <event value="62" version="0" level="win:Informational" keywords="MessagePump" template="RetrieveSendMessageStart" />
            <event value="70" version="0" level="win:Informational" keywords="MessagePump" template="SendMessageStop" />
            <event value="83" version="0" level="win:Informational" keywords="MessagePump" template="RetrieveSendMessageStop" />
           </events>
          </provider>
         </events></instrumentation>
        </instrumentationManifest>
        """;

    private const long Pointer = unchecked((long)0xFFFF_B000_0000_1000);

    private static WindowsSourceDefinition Source => WindowsSourceCatalog.Find(WindowsSourceCatalog.Win32kSourceId)!;

    private static SourceAdmissionPlan Compiled(string manifest = Manifest) => AdmissionPlanCompiler.Compile(Source, ManifestParser.Parse(manifest), 0);

    [Fact(DisplayName = "§18.3: this machine registers Win32k's six message events with the layout the admission is compiled from - each end's callback count and queue message pointer first, a send's start naming its receiver after its window, message and parameters")]
    public void TheMachineRegistersTheMessageEventsAsCompiled()
    {
        var metadata = new TdhEtwMetadataSource();
        RegisteredProvider provider = Assert.IsType<RegisteredProvider>(metadata.TryResolveProvider("Microsoft-Windows-Win32k"));
        ManifestReadResult read = metadata.TryReadManifest(provider.ProviderGuid);
        ProviderSchema registered = ManifestParser.Parse(Assert.IsType<string>(read.ManifestXml));
        ProviderSchema compiled = ManifestParser.Parse(Manifest);
        Assert.Equal(compiled.ProviderGuid, registered.ProviderGuid);
        Assert.Equal(0x40_0000UL, registered.Keywords["MessagePump"]);
        foreach (int end in Ends)
        {
            ProviderSchemaEvent machine = Assert.IsType<ProviderSchemaEvent>(registered.FindEvent(end, 0));
            ProviderSchemaEvent expected = Assert.IsType<ProviderSchemaEvent>(compiled.FindEvent(end, 0));
            Assert.Equal(expected.Fields.Select(field => field.Name + " " + field.InType), machine.Fields.Select(field => field.Name + " " + field.InType));
            Assert.Equal(("win:Informational", true), (machine.Level, machine.Keywords.Contains("MessagePump")));
        }
    }

    [Fact(DisplayName = "R22: Win32k's message events compile from its registered layout to fixed slots - each end's queue message pointer, which the gate writes the message's number over, and a send's receiving process - never a window, a thread, a message number or a parameter")]
    public void TheMessageEventsCompileToFixedSlots()
    {
        SourceAdmissionPlan plan = Compiled();
        Assert.Empty(plan.Diagnostics);
        Assert.Equal(
            [
                (QueuePost, ObservationKind.Send, Direction.Outbound), (SendStart, ObservationKind.RequestStart, Direction.Outbound),
                (RetrievePost, ObservationKind.Receive, Direction.Inbound), (RetrieveSendStart, ObservationKind.RequestStart, Direction.Inbound),
                (SendStop, ObservationKind.RequestEnd, Direction.Outbound), (RetrieveSendStop, ObservationKind.RequestEnd, Direction.Inbound),
            ],
            plan.Events.OrderBy(descriptor => descriptor.EventId).Select(descriptor => ((int)descriptor.EventId, descriptor.Kind, descriptor.Direction)));

        foreach (AdmittedEventPlan descriptor in plan.Events)
        {
            Assert.Equal((Mechanism.WindowMessage, ObservationLayer.Application), (descriptor.Mechanism, descriptor.Layer));
            (string Field, int Offset, int Width, SourceField? Meaning)[] slots =
                [.. descriptor.Slots.Select(slot => (slot.FieldName, slot.Offset, slot.Width, slot.SourceField)).OrderBy(slot => slot.Offset)];

            // Every end keeps its pointer, after the one-byte callback count, for the gate to write the message's number over;
            // a send's start keeps the process that receives it, after its window, parameters, message, time and flags.
            (string, int, int, SourceField?)[] expected = descriptor.EventId == SendStart
                ? [("pqmsg", 1, 8, SourceField.WindowMessageNumber), ("pidReceiver", 45, 4, SourceField.PeerProcessId)]
                : [("pqmsg", 1, 8, SourceField.WindowMessageNumber)];
            Assert.Equal(expected, slots);

            // The process a send names is its other end's, so no slot attributes a record to a process.
            Assert.DoesNotContain(descriptor.Slots, slot => slot.Role == FieldRole.ProcessAttribution);
        }
    }

    [Fact(DisplayName = "R22: through its gate Win32k admits each of a message's ends with the message's number written over its pointer - a send's four ends one number, a post's two another, the pointer's next message a third - and omits a record delivered out of the order raised")]
    public void TheGateAdmitsWithTheMessagesNumber()
    {
        SourceAdmissionPlan plan = Compiled();
        Assert.IsType<WindowMessageRecordGate>(Assert.Single(RecordGates.For(new EventAdmissionTable([plan]))));
        var gate = new WindowMessageRecordGate();

        // A send from process 400 to process 700's window: its start and stop at the sender, its retrieval at the receiver,
        // each admitted with the message's number, never the pointer; the start keeps the process it names.
        long[] sent = [.. new (int Event, int RaisedBy, long Raised)[] { (SendStart, 400, 10), (RetrieveSendStart, 700, 11), (RetrieveSendStop, 700, 12), (SendStop, 400, 13) }
            .Select(end =>
            {
                AdmittedEvent record = end.Event == SendStart
                    ? Record(plan, end.Event, end.RaisedBy, end.Raised, ("pqmsg", Pointer), ("pidReceiver", 700))
                    : Record(plan, end.Event, end.RaisedBy, end.Raised, ("pqmsg", Pointer));
                Assert.Null(gate.Read(ref record, Plan(plan, end.Event)));
                if (end.Event == SendStart)
                {
                    Assert.Equal(700L, Slot(plan, record, "pidReceiver"));
                }

                return Slot(plan, record, "pqmsg");
            })];
        Assert.Equal([1L, 1L, 1L, 1L], sent);

        // A post at the same pointer once the send has ended is another message, from its queueing to its retrieval.
        AdmittedEvent queued = Record(plan, QueuePost, 400, 14, ("pqmsg", Pointer));
        Assert.Null(gate.Read(ref queued, Plan(plan, QueuePost)));
        AdmittedEvent retrieved = Record(plan, RetrievePost, 700, 15, ("pqmsg", Pointer));
        Assert.Null(gate.Read(ref retrieved, Plan(plan, RetrievePost)));
        Assert.Equal((2L, 2L), (Slot(plan, queued, "pqmsg"), Slot(plan, retrieved, "pqmsg")));

        // A record raised before one delivered ahead of it is omitted as out of order, its pointer left unwritten; the next
        // message at the pointer is a third.
        AdmittedEvent late = Record(plan, QueuePost, 400, 9, ("pqmsg", Pointer));
        Assert.Equal(GateOmission.OutOfOrder, gate.Read(ref late, Plan(plan, QueuePost)));
        Assert.Equal(Pointer, Slot(plan, late, "pqmsg"));
        AdmittedEvent next = Record(plan, QueuePost, 400, 16, ("pqmsg", Pointer));
        Assert.Null(gate.Read(ref next, Plan(plan, QueuePost)));
        Assert.Equal(4L, Slot(plan, next, "pqmsg"));
    }

    [Fact(DisplayName = "R22: an admitted window message record is a window message row of a family of its own, its message's number and a send's receiving process source fields, and the process that raised it its owner, never the receiver it names")]
    public void AnAdmittedRecordIsAWindowMessageRow()
    {
        SourceAdmissionPlan plan = Compiled();
        SourceClockDescriptor clock = new(
            ClockId.Derive("window-message-admission-tests"),
            HostId.Derive("window-message-admission-tests"),
            SourceClockKind.Monotonic,
            TimestampEncoding.Qpc,
            10_000_000,
            0,
            TimestampRounding.NearestEven,
            SourceClockMath.SessionTicksPerSecond * 3_600);
        var mapper = new AdmittedEventEnvelopeMapper([plan], clock.Id);
        var normalizer = new ObservationNormalizerV1(clock);
        var gate = new WindowMessageRecordGate();

        // A send's start raised in process 400, naming process 700 as its receiver, its pointer written over by the gate.
        AdmittedEvent start = Record(plan, SendStart, 400, 5_000, ("pqmsg", Pointer), ("pidReceiver", 700));
        Assert.Null(gate.Read(ref start, Plan(plan, SendStart)));
        start.RecordOrdinal = 1;
        using RecordEnvelopeV1 started = mapper.ToEnvelope(start, Plan(plan, SendStart), CaptureId.New());
        ObservationRowV1 row = normalizer.ToRow(started, Plan(plan, SendStart));
        Assert.Equal(
            (Mechanism.WindowMessage, ObservationLayer.Application, ObservationKind.RequestStart, Direction.Outbound),
            (row.Mechanism, row.Layer, row.Kind, row.Direction));
        // Its payload names no owner, so the process that raised it owns it (since `process-binding-v7`), the receiver it
        // names a source field and never its owner.
        Assert.Equal(((int?)null, 400), (row.OwnerProcessId, row.HeaderProcessId));
        Assert.Equal(400, RecordAttribution.OwnerOf(row.OwnerProcessId, row.Mechanism, row.Kind, row.HeaderProcessId));
        Assert.Equal(
            [(SourceField.PeerProcessId, 700L), (SourceField.WindowMessageNumber, 1L)],
            ObservationNormalizerV1.FieldRows(started, Plan(plan, SendStart), row).Select(field => (field.Field, field.Value!.Value)).OrderBy(field => field.Field));
        Assert.Equal("window-message", ObservationNormalizerV1.DiscriminatorFor(Mechanism.WindowMessage));
        Assert.Null(row.Validate());

        // The receiver's retrieval of it carries the same number and no other field.
        AdmittedEvent retrieval = Record(plan, RetrieveSendStart, 700, 6_000, ("pqmsg", Pointer));
        Assert.Null(gate.Read(ref retrieval, Plan(plan, RetrieveSendStart)));
        retrieval.RecordOrdinal = 2;
        using RecordEnvelopeV1 retrieved = mapper.ToEnvelope(retrieval, Plan(plan, RetrieveSendStart), CaptureId.New());
        ObservationRowV1 end = normalizer.ToRow(retrieved, Plan(plan, RetrieveSendStart));
        Assert.Equal((ObservationKind.RequestStart, Direction.Inbound, 700), (end.Kind, end.Direction, end.HeaderProcessId));
        Assert.Equal(700, RecordAttribution.OwnerOf(end.OwnerProcessId, end.Mechanism, end.Kind, end.HeaderProcessId));
        SourceFieldRowV1 number = Assert.Single(ObservationNormalizerV1.FieldRows(retrieved, Plan(plan, RetrieveSendStart), end));
        Assert.Equal((SourceField.WindowMessageNumber, (long?)1), (number.Field, number.Value));
        Assert.Null(end.Validate());
    }

    [Theory(DisplayName = "R3: a Win32k whose schema does not admit an end's queue message pointer, by which its gate tells which message the end is of, is refused, never admitted to number messages from a value never observed - a capture's compilation refuses it, and icat capabilities says its schema is unknown")]
    [InlineData("QueuePostMessage", QueuePost)]
    [InlineData("SendMessageStart", SendStart)]
    [InlineData("RetrievePostMessage", RetrievePost)]
    [InlineData("RetrieveSendMessageStart", RetrieveSendStart)]
    [InlineData("SendMessageStop", SendStop)]
    [InlineData("RetrieveSendMessageStop", RetrieveSendStop)]
    public void AWin32kWithoutAnEndsPointerIsRefused(string template, int eventId)
    {
        // The registered layout as another build might report it, one end's pointer gone from its template.
        string manifest = Without(Manifest, template, "pqmsg");
        string reason = Assert.IsType<string>(RecordGates.Undecidable(Compiled(manifest)));
        Assert.Contains($"no pqmsg for event {eventId} v0", reason, StringComparison.Ordinal);
        Assert.Contains("window message gate", reason, StringComparison.Ordinal);
        Assert.Null(RecordGates.Undecidable(Compiled()));

        // A capture's compilation refuses the source with that reason, and compiles no plan of it.
        var inventory = new CapabilityInventoryProbe(new Win32kMetadata(manifest));
        SourcePlanCompilation compilation = inventory.CompilePlanSet([WindowsSourceCatalog.Win32kSourceId]);
        Assert.Empty(compilation.Plans);
        SourcePlanIssue refusal = Assert.Single(compilation.Issues, issue => issue.Severity == SourcePlanIssueSeverity.Refusal);
        Assert.Equal((WindowsSourceCatalog.Win32kSourceId, reason), (refusal.SourceId, refusal.Reason));
        var whole = new CapabilityInventoryProbe(new Win32kMetadata(Manifest));
        Assert.Single(whole.CompilePlanSet([WindowsSourceCatalog.Win32kSourceId]).Plans);

        // And the capability report says its schema is unknown, for that reason, where the whole layout is experimental.
        ProbeEnvironment environment = CapabilityInventoryProbe.DescribeEnvironment(isElevated: false);
        SourceCapability unknown = inventory.Probe(environment).Sources.Single(source => source.SourceId == WindowsSourceCatalog.Win32kSourceId);
        Assert.Equal((CapabilityState.SchemaUnknown, reason), (unknown.State, unknown.UnavailableReason));
        Assert.Equal(CapabilityState.Experimental,
            whole.Probe(environment).Sources.Single(source => source.SourceId == WindowsSourceCatalog.Win32kSourceId).State);
    }

    [Fact(DisplayName = "R3: a Win32k whose schema lacks a send's receiving process, which no rule decides by, is admitted, the receiver then unknown")]
    public void AWin32kWithoutTheReceiverIsAdmitted()
    {
        SourceAdmissionPlan plan = Compiled(Without(Manifest, "SendMessageStart", "pidReceiver"));
        Assert.Null(RecordGates.Undecidable(plan));
        Assert.DoesNotContain(Plan(plan, SendStart).Slots, slot => slot.FieldName == "pidReceiver");
        Assert.Contains(Plan(plan, SendStart).Slots, slot => slot.FieldName == "pqmsg");
    }

    /// <summary>The manifest with one field of one template taken out.</summary>
    private static string Without(string manifest, string template, string field)
    {
        int start = manifest.IndexOf($"<template tid=\"{template}\">", StringComparison.Ordinal);
        int end = manifest.IndexOf("</template>", start, StringComparison.Ordinal);
        int at = manifest.IndexOf($"<data name=\"{field}\"", start, StringComparison.Ordinal);
        Assert.InRange(at, start, end);
        int close = manifest.IndexOf("/>", at, StringComparison.Ordinal) + 2;
        return manifest[..at] + manifest[close..];
    }

    private static AdmittedEventPlan Plan(SourceAdmissionPlan plan, int eventId) => plan.Events.Single(descriptor => descriptor.EventId == eventId);

    private static AdmittedEvent Record(SourceAdmissionPlan plan, int eventId, int raisedBy, long raised, params (string Field, long Value)[] fields)
    {
        AdmittedEventPlan descriptor = Plan(plan, eventId);
        var admitted = new AdmittedEvent
        {
            SourceIndex = descriptor.SourceIndex,
            EventId = eventId,
            Version = 0,
            PointerSize = 8,
            HeaderProcessId = raisedBy,
            TimestampQpc = raised,
        };
        foreach ((string field, long value) in fields)
        {
            admitted.SetSlot(Index(descriptor, field), value);
        }

        return admitted;
    }

    private static long Slot(SourceAdmissionPlan plan, AdmittedEvent admitted, string field)
    {
        Assert.True(admitted.TryGetSlot(Index(Plan(plan, admitted.EventId), field), out long value), field);
        return value;
    }

    private static int Index(AdmittedEventPlan descriptor, string field)
    {
        for (int index = 0; index < descriptor.Slots.Count; index++)
        {
            if (descriptor.Slots[index].FieldName == field)
            {
                return index;
            }
        }

        throw new InvalidOperationException($"{field} is not admitted by event {descriptor.EventId}.");
    }

    /// <summary>A machine that registers Win32k with the layout given, and nothing else.</summary>
    private sealed class Win32kMetadata(string manifest) : IEtwMetadataSource
    {
        private static readonly Guid Provider = Guid.Parse("8c416c79-d49b-4f01-a467-e56d3aa8234c");

        public bool IsAvailable => true;

        public string? UnavailableReason => null;

        public RegisteredProvider? TryResolveProvider(string providerName) =>
            string.Equals(providerName, "Microsoft-Windows-Win32k", StringComparison.Ordinal) ? new(providerName, Provider) : null;

        public int CountPublishedProviders() => 1;

        public ManifestReadResult TryReadManifest(Guid providerGuid) => providerGuid == Provider
            ? ManifestReadResult.Success(manifest)
            : ManifestReadResult.Failure("not registered in this test");

        public ProviderSchema? TryReadClassic(Guid classGuid, string className, int version, IReadOnlyList<int> opcodes) => null;
    }
}
