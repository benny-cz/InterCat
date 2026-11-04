using InterCat.Capture.Journal;
using InterCat.Capture.Recording;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// COM-Perf's call events admitted (ADR-079's addendum): a synchronous call's starts and stops at both ends compile from the
/// provider's registered layout to fixed slots - its trace number, the process at its other end, its method and interface,
/// its HRESULT - and never to a thread, a pointer, an IPID, an OID or an address; an admitted record is a COM call row whose
/// payload names no owner, so the process that raised it owns it (`process-binding-v5`), the process it names at its other
/// end a source field and never its owner.
/// </summary>
public sealed class ComPerfAdmissionTests
{
    // The provider's registered layout of its synchronous call events, as TDH gave it on Windows 10.0.26220 (ADR-077,
    // bench/results/com-provider-inspection-20261104T141555Z).
    private const string Manifest = """
        <instrumentationManifest xmlns="http://schemas.microsoft.com/win/2004/08/events">
         <instrumentation><events>
          <provider name="Microsoft-Windows-COM-Perf" guid="{b8d6861b-d20f-4eec-bbae-87e0dd80602b}">
           <templates>
            <template tid="ClientSyncCallStart">
             <data name="CallTraceId" inType="win:UInt32" /><data name="TargetProcessId" inType="win:UInt32" />
             <data name="TargetThreadId" inType="win:UInt32" /><data name="CausalityId" inType="win:GUID" />
             <data name="TargetMethod" inType="win:UInt32" /><data name="TargetInterface" inType="win:GUID" />
             <data name="ProxyInterfacePointer" inType="win:Pointer" /><data name="ProxyManagerIUnknown" inType="win:Pointer" />
             <data name="IPID" inType="win:GUID" /><data name="OID" inType="win:UInt64" />
             <data name="CallerLogicalThreadId" inType="win:GUID" /><data name="CallerReturnAddress" inType="win:Pointer" />
             <data name="WinrtAsyncPatternMethodIndicator" inType="win:UInt32" /><data name="FakeSync" inType="win:Boolean" />
            </template>
            <template tid="ClientSyncCallStop">
             <data name="CallTraceId" inType="win:UInt32" /><data name="HRESULT" inType="win:Int32" />
             <data name="SourceOfHRESULT" inType="win:UInt32" />
            </template>
            <template tid="ServerSyncCallStart">
             <data name="CallTraceId" inType="win:UInt32" /><data name="SourceProcessId" inType="win:UInt32" />
             <data name="SourceThreadId" inType="win:UInt32" /><data name="CausalityId" inType="win:GUID" />
             <data name="TargetMethod" inType="win:UInt32" /><data name="TargetInterface" inType="win:GUID" />
             <data name="InterfacePointer" inType="win:Pointer" /><data name="MethodAddress" inType="win:Pointer" />
             <data name="ServerIUnknown" inType="win:Pointer" /><data name="IPID" inType="win:GUID" />
             <data name="OID" inType="win:UInt64" /><data name="WinrtAsyncPatternMethodIndicator" inType="win:UInt32" />
            </template>
            <template tid="ServerSyncCallStop">
             <data name="CallTraceId" inType="win:UInt32" /><data name="HRESULT" inType="win:Int32" />
             <data name="ServerException" inType="win:UInt32" />
            </template>
           </templates>
           <events>
            <event value="39" version="1" level="win:Informational" template="ClientSyncCallStart" />
            <event value="40" version="0" level="win:Informational" template="ClientSyncCallStop" />
            <event value="43" version="2" level="win:Informational" template="ServerSyncCallStart" />
            <event value="44" version="0" level="win:Informational" template="ServerSyncCallStop" />
           </events>
          </provider>
         </events></instrumentation>
        </instrumentationManifest>
        """;

    private static readonly Guid Dispatch = Guid.Parse("00020400-0000-0000-c000-000000000046");

    private static WindowsSourceDefinition Source => WindowsSourceCatalog.Find(WindowsSourceCatalog.ComPerfSourceId)!;

    private static SourceAdmissionPlan Compiled() => AdmissionPlanCompiler.Compile(Source, ManifestParser.Parse(Manifest), 0);

    [Fact(DisplayName = "R22: COM-Perf's call events compile from its registered layout to fixed slots - a call's trace number, the other end's process, its method and interface, its HRESULT - never a thread, a pointer, an IPID, an OID or an address")]
    public void TheCallEventsCompileToFixedSlots()
    {
        SourceAdmissionPlan plan = Compiled();
        Assert.Empty(plan.Diagnostics);
        Assert.Equal(
            [
                (39, ObservationKind.RequestStart, Direction.Outbound), (40, ObservationKind.RequestEnd, Direction.Outbound),
                (43, ObservationKind.RequestStart, Direction.Inbound), (44, ObservationKind.RequestEnd, Direction.Inbound),
            ],
            plan.Events.OrderBy(descriptor => descriptor.EventId).Select(descriptor => ((int)descriptor.EventId, descriptor.Kind, descriptor.Direction)));

        foreach (AdmittedEventPlan descriptor in plan.Events)
        {
            Assert.Equal((Mechanism.ComCall, ObservationLayer.Application), (descriptor.Mechanism, descriptor.Layer));
            (string Field, int Offset, int Width, SourceField? Meaning)[] slots =
                [.. descriptor.Slots.Select(slot => (slot.FieldName, slot.Offset, slot.Width, slot.SourceField)).OrderBy(slot => slot.Offset)];

            // A start keeps its trace number, the other end's process - the target at the client, the source at the server -
            // its method and, as its one identifier, its interface; the thread and causality between them are skipped.
            // A stop keeps its trace number and its HRESULT.
            (string, int, int, SourceField?)[] expected = descriptor.EventId switch
            {
                39 => [("CallTraceId", 0, 4, SourceField.ComCallTraceId), ("TargetProcessId", 4, 4, SourceField.PeerProcessId),
                    ("TargetMethod", 28, 4, SourceField.ComMethodNumber), ("TargetInterface", 32, 16, null)],
                43 => [("CallTraceId", 0, 4, SourceField.ComCallTraceId), ("SourceProcessId", 4, 4, SourceField.PeerProcessId),
                    ("TargetMethod", 28, 4, SourceField.ComMethodNumber), ("TargetInterface", 32, 16, null)],
                _ => [("CallTraceId", 0, 4, SourceField.ComCallTraceId), ("HRESULT", 4, 4, null)],
            };
            Assert.Equal(expected, slots);
            Assert.Equal(descriptor.Kind == ObservationKind.RequestStart,
                descriptor.Slots.Any(slot => slot is { FieldName: "TargetInterface", Kind: AdmittedSlotKind.Identifier }));
            Assert.Equal(descriptor.Kind == ObservationKind.RequestEnd, descriptor.Slots.Any(slot => slot is { FieldName: "HRESULT", Role: FieldRole.Status }));

            // The process a record names is its other end's, so no slot attributes a record to a process.
            Assert.DoesNotContain(descriptor.Slots, slot => slot.Role == FieldRole.ProcessAttribution);
        }
    }

    [Fact(DisplayName = "R22: an admitted COM call record is a COM call row whose payload names no owner, so the process that raised it owns it, the process it names at its other end a source field and never its owner")]
    public void AnAdmittedRecordIsAComCallRow()
    {
        SourceAdmissionPlan plan = Compiled();
        SourceClockDescriptor clock = new(
            ClockId.Derive("com-perf-admission-tests"),
            HostId.Derive("com-perf-admission-tests"),
            SourceClockKind.Monotonic,
            TimestampEncoding.Qpc,
            10_000_000,
            0,
            TimestampRounding.NearestEven,
            SourceClockMath.SessionTicksPerSecond * 3_600);
        var mapper = new AdmittedEventEnvelopeMapper([plan], clock.Id);
        var normalizer = new ObservationNormalizerV1(clock);
        Guid activity = Guid.Parse("0c0f1b5e-3a51-4c8e-9a07-51d5ab1c2e44");

        // A client's start, raised in process 400, naming process 700 at its other end, its activity its header's.
        AdmittedEvent start = Record(plan, 39, raisedBy: 400, ("CallTraceId", 7), ("TargetProcessId", 700), ("TargetMethod", 6));
        start.SetIdentifier(Dispatch);
        start.ActivityId = activity;
        start.TimestampQpc = 5_000;
        start.RecordOrdinal = 1;
        using RecordEnvelopeV1 started = mapper.ToEnvelope(start, Plan(plan, 39), CaptureId.New());
        ObservationRowV1 row = normalizer.ToRow(started, Plan(plan, 39));
        Assert.Equal(
            (Mechanism.ComCall, ObservationLayer.Application, ObservationKind.RequestStart, Direction.Outbound),
            (row.Mechanism, row.Layer, row.Kind, row.Direction));
        Assert.Equal(((int?)null, 400, QualityLevel.UnknownQuality), (row.OwnerProcessId, row.HeaderProcessId, row.AttributionQuality));
        Assert.Equal(400, RecordAttribution.OwnerOf(row.OwnerProcessId, row.Mechanism, row.HeaderProcessId));
        Assert.Equal(((Guid?)Dispatch, (Guid?)activity), (row.SourceIdentifier, row.ActivityId));
        Assert.Null(row.Validate());
        Assert.Equal(
            [(SourceField.ComCallTraceId, 7L), (SourceField.PeerProcessId, 700L), (SourceField.ComMethodNumber, 6L)],
            ObservationNormalizerV1.FieldRows(started, Plan(plan, 39), row).Select(field => (field.Field, field.Value!.Value)).OrderBy(field => field.Field));
        Assert.Equal("com-call", ObservationNormalizerV1.DiscriminatorFor(Mechanism.ComCall));

        // Its stop repeats its trace number and reports the method's HRESULT as the call's status.
        AdmittedEvent stop = Record(plan, 40, raisedBy: 400, ("CallTraceId", 7), ("HRESULT", 0x8000_4002));
        stop.ActivityId = activity;
        stop.TimestampQpc = 6_000;
        stop.RecordOrdinal = 2;
        using RecordEnvelopeV1 stopped = mapper.ToEnvelope(stop, Plan(plan, 40), CaptureId.New());
        ObservationRowV1 end = normalizer.ToRow(stopped, Plan(plan, 40));
        Assert.Equal((ObservationKind.RequestEnd, (long?)0x8000_4002, FieldAvailability.Present), (end.Kind, end.StatusCode, end.StatusAvailability));
        SourceFieldRowV1 trace = Assert.Single(ObservationNormalizerV1.FieldRows(stopped, Plan(plan, 40), end));
        Assert.Equal((SourceField.ComCallTraceId, (long?)7), (trace.Field, trace.Value));
        Assert.Null(end.Validate());
    }

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
}
