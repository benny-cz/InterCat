using System.Buffers.Binary;
using InterCat.Capture.Windows;
using InterCat.Domain;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// The M0 observation builder reads a transport record's own flow through its descriptor's measured orientation, and
/// keeps the endpoints exactly as the source delivered them (R1). Runs against a committed manifest excerpt (R19).
/// </summary>
public sealed class NetworkObservationBuilderTests
{
    private const string Manifest = """
        <instrumentationManifest xmlns="http://schemas.microsoft.com/win/2004/08/events">
         <instrumentation>
          <events>
           <provider name="Sample-Network" guid="{7dd42a49-5329-4832-8dfd-43d979153a88}">
            <templates>
             <template tid="TransferArgs">
              <data name="PID" inType="win:UInt32" />
              <data name="size" inType="win:UInt32" />
              <data name="daddr" inType="win:UInt32" />
              <data name="saddr" inType="win:UInt32" />
              <data name="dport" inType="win:UInt16" />
              <data name="sport" inType="win:UInt16" />
             </template>
            </templates>
            <events>
             <event value="11" version="0" level="win:Informational" template="TransferArgs" />
             <event value="43" version="0" level="win:Informational" template="TransferArgs" />
            </events>
           </provider>
          </events>
         </instrumentation>
        </instrumentationManifest>
        """;

    private const uint Loopback = 0x7F00_0001;

    [Fact(DisplayName = "R1: a UDP receive keeps its endpoints as delivered and is read as its owner's own flow")]
    public void ReceivesAreReadThroughTheirMeasuredOrientation()
    {
        SourceAdmissionPlan plan = AdmissionPlanCompiler.Compile(Definition(), ManifestParser.Parse(Manifest), 0);
        AdmittedEventPlan tcpReceive = plan.Events.Single(descriptor => descriptor.EventId == 11);
        AdmittedEventPlan udpReceive = plan.Events.Single(descriptor => descriptor.EventId == 43);

        // Both records name the sender's port 50000 first and the receiver's port 40000 second, as a UDP receive does.
        AdmittedEvent admitted = Record(sourcePort: 50_000, destinationPort: 40_000);
        var capture = new CaptureId(Guid.Parse("08ccf60c-4c45-4793-b646-b1cf26fe691a"));
        NetworkTransferObservation udp = NetworkObservationBuilder.TryBuild(admitted, udpReceive, capture, 1, 1)!;
        NetworkTransferObservation tcp = NetworkObservationBuilder.TryBuild(admitted, tcpReceive, capture, 1, 1)!;

        Assert.Equal((50_000, 40_000), (udp.SourcePort, udp.DestinationPort));
        Assert.Equal(new FlowKey(Loopback, 40_000, Loopback, 50_000), udp.Flow);
        Assert.Equal(EndpointOrientation.OriginFirst, TransportEndpoints.OrientationOf(Mechanism.Udp, ObservationKind.Receive));

        // A TCP receive names its owner first (FX-TCP-001), so the same values are its own flow unchanged.
        Assert.Equal(new FlowKey(Loopback, 50_000, Loopback, 40_000), tcp.Flow);
        Assert.Equal(EndpointOrientation.OwnerFirst, TransportEndpoints.OrientationOf(Mechanism.Udp, ObservationKind.Send));
        Assert.All(
            Enum.GetValues<ObservationKind>(),
            kind => Assert.Equal(EndpointOrientation.OwnerFirst, TransportEndpoints.OrientationOf(Mechanism.Tcp, kind)));
    }

    private static AdmittedEvent Record(int sourcePort, int destinationPort)
    {
        AdmittedEvent admitted = default;
        admitted.SetSlot(0, 42);
        admitted.SetSlot(1, 100);
        admitted.SetSlot(2, BinaryPrimitives.ReverseEndianness(Loopback));
        admitted.SetSlot(3, BinaryPrimitives.ReverseEndianness((ushort)sourcePort));
        admitted.SetSlot(4, BinaryPrimitives.ReverseEndianness(Loopback));
        admitted.SetSlot(5, BinaryPrimitives.ReverseEndianness((ushort)destinationPort));
        return admitted;
    }

    private static IReadOnlyList<AdmittedFieldIntent> Fields() =>
    [
        new("PID", FieldRole.ProcessAttribution),
        new("size", FieldRole.ByteCount, MeasurementUnit.Bytes, ByteDomain.TransportObserved),
        new("saddr", FieldRole.SourceEndpoint, Transform: SlotTransform.NetworkOrderIpv4Address),
        new("sport", FieldRole.SourceEndpoint, Transform: SlotTransform.NetworkOrderPort),
        new("daddr", FieldRole.DestinationEndpoint, Transform: SlotTransform.NetworkOrderIpv4Address),
        new("dport", FieldRole.DestinationEndpoint, Transform: SlotTransform.NetworkOrderPort),
    ];

    private static WindowsSourceDefinition Definition() => new()
    {
        SourceId = "etw/manifest/Sample-Network",
        DisplayName = "Sample network",
        Kind = SourceKind.ManifestProvider,
        ProviderName = "Sample-Network",
        Mechanisms = [Mechanism.Tcp, Mechanism.Udp],
        RequiredPrivilege = PrivilegeRequirement.Administrator,
        Level = "win:Informational",
        MatchAnyKeyword = 0x10,
        RequestedKeywords = ["SAMPLE_KEYWORD_IPV4"],
        FilteringNotes = "test",
        StartupBehaviour = "test",
        ContractStatus = SourceContractStatus.Documented,
        AdmittedEvents =
        [
            new(11, 0, "TCP received", Mechanism.Tcp, ObservationLayer.Transport, ObservationKind.Receive, Direction.Inbound, Fields()),
            new(43, 0, "UDP received", Mechanism.Udp, ObservationLayer.Transport, ObservationKind.Receive, Direction.Inbound, Fields()),
        ],
    };
}
