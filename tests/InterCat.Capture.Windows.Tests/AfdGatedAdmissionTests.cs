using InterCat.Capture.Journal;
using InterCat.Capture.Recording;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// Winsock AFD admitted through its gate (ADR-073, ADR-074): the calls the gate counts compile from the provider's registered
/// layout to fixed slots, none of them an address the kernel reuses or a buffer's; an admitted record carries the gate's
/// socket number and owner in place of the kernel addresses the source raised, and derives a Unix-domain socket row of that
/// owner; and every record of an admitted descriptor the gate does not admit is omitted with its reason, never admitted with
/// an address.
/// </summary>
public sealed class AfdGatedAdmissionTests
{
    // The provider's registered layout of the calls the gate counts, and its two source keywords, as TDH gave them on
    // Windows 10.0.26220 (ADR-072).
    private const string Manifest = """
        <instrumentationManifest xmlns="http://schemas.microsoft.com/win/2004/08/events">
         <instrumentation><events>
          <provider name="Microsoft-Windows-Winsock-AFD" guid="{e53c6823-7bb8-44bb-90dc-3f86090d48a6}">
           <keywords>
            <keyword name="AFD_KW_SOURCE_WINSOCK" mask="0x4" />
            <keyword name="AFD_KW_SOURCE_TRANSPORT" mask="0x8" />
           </keywords>
           <templates>
            <template tid="AfdCreateOpenArgs">
             <data name="EnterExit" inType="win:UInt32" /><data name="Location" inType="win:UInt32" />
             <data name="Process" inType="win:Pointer" /><data name="Endpoint" inType="win:Pointer" />
             <data name="AddressFamily" inType="win:UInt32" /><data name="SocketType" inType="win:UInt32" />
             <data name="Protocol" inType="win:UInt32" /><data name="ProcessId" inType="win:Pointer" />
             <data name="Status" inType="win:UInt32" />
            </template>
            <template tid="AfdCloseClosedArgs">
             <data name="EnterExit" inType="win:UInt32" /><data name="Location" inType="win:UInt32" />
             <data name="Process" inType="win:Pointer" /><data name="Endpoint" inType="win:Pointer" />
             <data name="Status" inType="win:UInt32" />
            </template>
            <template tid="AfdSendConnectedArgs">
             <data name="EnterExit" inType="win:UInt32" /><data name="Location" inType="win:UInt32" />
             <data name="Process" inType="win:Pointer" /><data name="Endpoint" inType="win:Pointer" />
             <data name="BufferCount" inType="win:UInt32" /><data name="Buffer" inType="win:Pointer" />
             <data name="BufferLength" inType="win:UInt32" /><data name="Status" inType="win:UInt32" />
            </template>
            <template tid="AfdAcceptWithAddressConnectedArgs">
             <data name="EnterExit" inType="win:UInt32" /><data name="Location" inType="win:UInt32" />
             <data name="Process" inType="win:Pointer" /><data name="Endpoint" inType="win:Pointer" />
             <data name="Buffer" inType="win:Pointer" /><data name="BufferLength" inType="win:UInt32" />
             <data name="Status" inType="win:UInt32" /><data name="AddressLen" inType="win:UInt32" />
             <data name="Address" inType="win:Binary" length="AddressLen" /><data name="AcceptEndpoint" inType="win:Pointer" />
             <data name="CurrentBacklog" inType="win:UInt32" />
            </template>
            <template tid="AfdBindWithAddressConnectedArgs">
             <data name="EnterExit" inType="win:UInt32" /><data name="Location" inType="win:UInt32" />
             <data name="Process" inType="win:Pointer" /><data name="Endpoint" inType="win:Pointer" />
             <data name="Status" inType="win:UInt32" /><data name="AddressLen" inType="win:UInt32" />
             <data name="Address" inType="win:Binary" length="AddressLen" />
            </template>
           </templates>
           <events>
            <event value="1000" version="0" level="win:Informational" keywords="AFD_KW_SOURCE_WINSOCK" template="AfdCreateOpenArgs" />
            <event value="1001" version="0" level="win:Informational" keywords="AFD_KW_SOURCE_WINSOCK" template="AfdCloseClosedArgs" />
            <event value="1003" version="0" level="win:Informational" keywords="AFD_KW_SOURCE_WINSOCK" template="AfdSendConnectedArgs" />
            <event value="1004" version="0" level="win:Informational" keywords="AFD_KW_SOURCE_WINSOCK" template="AfdSendConnectedArgs" />
            <event value="1017" version="0" level="win:Informational" keywords="AFD_KW_SOURCE_WINSOCK" template="AfdCloseClosedArgs" />
            <event value="1020" version="0" level="win:Informational" keywords="AFD_KW_SOURCE_WINSOCK" template="AfdCloseClosedArgs" />
            <event value="1023" version="0" level="win:Informational" keywords="AFD_KW_SOURCE_WINSOCK" template="AfdCloseClosedArgs" />
            <event value="1027" version="0" level="win:Informational" keywords="AFD_KW_SOURCE_WINSOCK" template="AfdAcceptWithAddressConnectedArgs" />
            <event value="1029" version="0" level="win:Informational" keywords="AFD_KW_SOURCE_WINSOCK" template="AfdCloseClosedArgs" />
            <event value="1030" version="0" level="win:Informational" keywords="AFD_KW_SOURCE_WINSOCK" template="AfdBindWithAddressConnectedArgs" />
           </events>
          </provider>
         </events></instrumentation>
        </instrumentationManifest>
        """;

    // Kernel addresses as the source raises them: a process's and two endpoints', one AF_UNIX and one another family's.
    private const long ProcessAddress = unchecked((long)0xFFFF_9A0B_1C2D_3000UL);
    private const long UnixEndpoint = unchecked((long)0xFFFF_9A0B_4E5F_6000UL);
    private const long OtherEndpoint = unchecked((long)0xFFFF_9A0B_7A8B_9000UL);

    private static WindowsSourceDefinition Source => WindowsSourceCatalog.Find(WindowsSourceCatalog.WinsockAfdSourceId)!;

    private static SourceAdmissionPlan Compiled(int sourceIndex = 0) =>
        AdmissionPlanCompiler.Compile(Source, ManifestParser.Parse(Manifest), sourceIndex);

    [Fact(DisplayName = "R22: Winsock AFD's admitted calls compile from its registered layout to fixed slots, none an address the kernel reuses or a buffer's")]
    public void TheCallsCompileToFixedSlots()
    {
        SourceAdmissionPlan plan = Compiled();
        Assert.Empty(plan.Diagnostics);
        Assert.Equal([1000, 1001, 1003, 1004, 1017, 1020, 1023, 1027, 1029, 1030], plan.Events.Select(descriptor => descriptor.EventId).Order());

        // Each is a socket call of AFD_KW_SOURCE_WINSOCK, the one keyword the source is requested for, so a capture enables
        // none of what the transport indicates.
        ProviderSchema schema = ManifestParser.Parse(Manifest);
        Assert.Equal(["AFD_KW_SOURCE_WINSOCK"], Source.RequestedKeywords);
        Assert.Equal(schema.Keywords["AFD_KW_SOURCE_WINSOCK"], Source.MatchAnyKeyword);
        Assert.All(plan.Events, descriptor => Assert.Equal(Source.MatchAnyKeyword, schema.FindEvent(descriptor.EventId)!.KeywordMask));

        (string, int, int)[] call = [("EnterExit", 0, 4), ("Location", 4, 4), ("Process", 8, 8), ("Endpoint", 16, 8)];
        foreach (AdmittedEventPlan descriptor in plan.Events)
        {
            Assert.Equal((Mechanism.UnixDomainSocket, ObservationLayer.Transport), (descriptor.Mechanism, descriptor.Layer));
            (string Field, int Offset, int Width)[] slots = [.. descriptor.Slots.Select(slot => (slot.FieldName, slot.Offset, slot.Width)).OrderBy(slot => slot.Offset)];
            (string, int, int)[] expected = descriptor.EventId switch
            {
                1000 => [.. call, ("AddressFamily", 24, 4), ("SocketType", 28, 4), ("ProcessId", 36, 8), ("Status", 44, 4)],
                1003 or 1004 => [.. call, ("BufferLength", 36, 4), ("Status", 40, 4)],
                1027 => [.. call, ("Status", 36, 4)],
                _ => [.. call, ("Status", 24, 4)],
            };
            Assert.Equal(expected, slots);

            // The socket is the gate's number and its process the gate's owner, carried as the source's correlation key
            // and attribution; a transfer's length is the bytes it moved.
            Assert.Contains(descriptor.Slots, slot => slot is { FieldName: "Endpoint", Role: FieldRole.CorrelationKey, SourceField: SourceField.ConnectionId });
            Assert.Contains(descriptor.Slots, slot => slot is { FieldName: "Process", Role: FieldRole.ProcessAttribution });
            Assert.Equal(
                descriptor.EventId is 1003 or 1004,
                descriptor.Slots.Any(slot => slot is { FieldName: "BufferLength", Role: FieldRole.ByteCount, ByteDomain: ByteDomain.CompletedIo }));
        }
    }

    [Fact(DisplayName = "R22: through its gate an AFD record is admitted with its socket's number and owner in place of the kernel's addresses, and any other is omitted with its reason")]
    public void TheGateAdmitsWithItsOwnValues()
    {
        // Only the source with a gate admits through one.
        var sample = new SourceAdmissionPlan
        {
            SourceId = "etw/manifest/Sample",
            ProviderGuid = Guid.Parse("7dd42a49-5329-4832-8dfd-43d979153a88"),
            SourceIndex = 0,
            Events = [],
            Diagnostics = [],
        };
        SourceAdmissionPlan plan = Compiled(sourceIndex: 1);
        IRecordGate?[] gates = RecordGates.For(new EventAdmissionTable([sample, plan]));
        Assert.Null(gates[0]);
        AfdRecordGate gate = Assert.IsType<AfdRecordGate>(gates[1]);

        // A socket created AF_UNIX for process 30, on a thread of process 31: admitted with the socket's number for its
        // endpoint and its create's process for its process, never the kernel's addresses.
        AdmittedEvent create = Record(plan, 1000, raisedBy: 31, ("EnterExit", 0), ("Location", 1006), ("Process", ProcessAddress),
            ("Endpoint", UnixEndpoint), ("AddressFamily", 1), ("SocketType", 1), ("ProcessId", 30), ("Status", 0));
        Assert.Null(gate.Read(ref create, Plan(plan, 1000)));
        Assert.Equal((1L, 30L, 30L), (Slot(plan, create, "Endpoint"), Slot(plan, create, "Process"), Slot(plan, create, "ProcessId")));

        // A send's own exit, raised on another process's thread, is the socket's, with the bytes it moved; its entry
        // counts nothing.
        AdmittedEvent sent = Record(plan, 1003, raisedBy: 40, ("EnterExit", 1), ("Location", 3051), ("Process", ProcessAddress),
            ("Endpoint", UnixEndpoint), ("BufferLength", 120), ("Status", 0));
        Assert.Null(gate.Read(ref sent, Plan(plan, 1003)));
        Assert.Equal((1L, 30L, 120L), (Slot(plan, sent, "Endpoint"), Slot(plan, sent, "Process"), Slot(plan, sent, "BufferLength")));
        AdmittedEvent entry = Record(plan, 1003, raisedBy: 30, ("EnterExit", 0), ("Location", 3047), ("Process", ProcessAddress),
            ("Endpoint", UnixEndpoint), ("BufferLength", 120), ("Status", 0));
        Assert.Equal(GateOmission.NotCounted, gate.Read(ref entry, Plan(plan, 1003)));

        // Another family's socket, and its calls, are of no socket the gate follows.
        AdmittedEvent other = Record(plan, 1000, raisedBy: 30, ("EnterExit", 0), ("Location", 1006), ("Process", ProcessAddress),
            ("Endpoint", OtherEndpoint), ("AddressFamily", 2), ("SocketType", 1), ("ProcessId", 30), ("Status", 0));
        Assert.Equal(GateOmission.NoFollowedResource, gate.Read(ref other, Plan(plan, 1000)));
        AdmittedEvent otherSent = Record(plan, 1003, raisedBy: 30, ("EnterExit", 1), ("Location", 3051), ("Process", ProcessAddress),
            ("Endpoint", OtherEndpoint), ("BufferLength", 64), ("Status", 0));
        Assert.Equal(GateOmission.NoFollowedResource, gate.Read(ref otherSent, Plan(plan, 1003)));

        // Its close is admitted as the socket's last call.
        AdmittedEvent closed = Record(plan, 1001, raisedBy: 30, ("EnterExit", 1), ("Location", 2001), ("Process", ProcessAddress),
            ("Endpoint", UnixEndpoint), ("Status", 0));
        Assert.Null(gate.Read(ref closed, Plan(plan, 1001)));
        Assert.Equal((1L, 30L), (Slot(plan, closed, "Endpoint"), Slot(plan, closed, "Process")));

        // A gate holding as many sockets as it may follows no more: the create it did not follow is beyond its bound.
        var full = new AfdRecordGate(capacity: 1);
        AdmittedEvent first = create;
        Assert.Null(full.Read(ref first, Plan(plan, 1000)));
        AdmittedEvent second = Record(plan, 1000, raisedBy: 30, ("EnterExit", 0), ("Location", 1006), ("Process", ProcessAddress),
            ("Endpoint", OtherEndpoint), ("AddressFamily", 1), ("SocketType", 1), ("ProcessId", 30), ("Status", 0));
        Assert.Equal(GateOmission.BeyondBound, full.Read(ref second, Plan(plan, 1000)));
    }

    [Fact(DisplayName = "R22: an AF_UNIX record admitted through its gate derives a Unix-domain socket row of its socket's owner, its socket its connection, with the bytes it moved")]
    public void AnAdmittedRecordDerivesASocketRow()
    {
        SourceAdmissionPlan plan = Compiled();
        var gate = new AfdRecordGate();
        AdmittedEvent create = Record(plan, 1000, raisedBy: 31, ("EnterExit", 0), ("Location", 1006), ("Process", ProcessAddress),
            ("Endpoint", UnixEndpoint), ("AddressFamily", 1), ("SocketType", 1), ("ProcessId", 30), ("Status", 0));
        Assert.Null(gate.Read(ref create, Plan(plan, 1000)));
        AdmittedEvent received = Record(plan, 1004, raisedBy: 40, ("EnterExit", 1), ("Location", 4123), ("Process", ProcessAddress),
            ("Endpoint", UnixEndpoint), ("BufferLength", 96), ("Status", 0));
        Assert.Null(gate.Read(ref received, Plan(plan, 1004)));

        SourceClockDescriptor clock = new(
            ClockId.Derive("afd-gated-admission-tests"),
            HostId.Derive("afd-gated-admission-tests"),
            SourceClockKind.Monotonic,
            TimestampEncoding.Qpc,
            10_000_000,
            0,
            TimestampRounding.NearestEven,
            SourceClockMath.SessionTicksPerSecond * 3_600);
        var mapper = new AdmittedEventEnvelopeMapper([plan], clock.Id);
        received.TimestampQpc = 5_000;
        received.RecordOrdinal = 2;
        using RecordEnvelopeV1 envelope = mapper.ToEnvelope(received, Plan(plan, 1004), CaptureId.New());
        ObservationRowV1 row = new ObservationNormalizerV1(clock).ToRow(envelope, Plan(plan, 1004));

        // A receive of the socket's owner, never of the thread that raised it, with the bytes it took and its status.
        Assert.Equal(
            (Mechanism.UnixDomainSocket, ObservationLayer.Transport, ObservationKind.Receive, Direction.Inbound),
            (row.Mechanism, row.Layer, row.Kind, row.Direction));
        Assert.Equal((30, 40, QualityLevel.Proven), (row.OwnerProcessId, row.HeaderProcessId, row.AttributionQuality));
        Assert.Equal(
            (96L, ByteDomain.CompletedIo, AccountingSide.ReceiveSide, FieldAvailability.Present),
            (row.ByteValue, row.ByteDomain, row.AccountingSide, row.ByteAvailability));
        Assert.Equal((0L, FieldAvailability.Present), (row.StatusCode, row.StatusAvailability));
        Assert.Null(row.Validate());

        // Its socket is its connection, numbered in the capture; no field of it is the kernel's address.
        SourceFieldRowV1 connection = Assert.Single(ObservationNormalizerV1.FieldRows(envelope, Plan(plan, 1004), row));
        Assert.Equal((SourceField.ConnectionId, (long?)1), (connection.Field, connection.Value));
        Assert.Equal("unix-socket-operation", ObservationNormalizerV1.DiscriminatorFor(Mechanism.UnixDomainSocket));
    }

    private static AdmittedEventPlan Plan(SourceAdmissionPlan plan, int eventId) =>
        plan.Events.Single(descriptor => descriptor.EventId == eventId);

    private static AdmittedEvent Record(SourceAdmissionPlan plan, int eventId, int raisedBy, params (string Field, long Value)[] fields)
    {
        AdmittedEventPlan descriptor = Plan(plan, eventId);
        var admitted = new AdmittedEvent { SourceIndex = descriptor.SourceIndex, EventId = eventId, HeaderProcessId = raisedBy };
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

        throw new KeyNotFoundException(field);
    }
}
