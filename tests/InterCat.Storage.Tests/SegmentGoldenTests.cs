using System.Security.Cryptography;
using InterCat.Domain;
using Xunit;

namespace InterCat.Storage.Tests;

/// <summary>
/// Pinned bytes. A segment of IPv4 and other records is `observation-v1` exactly as it was before `observation-v2` existed
/// (revision 172), so a reader of an earlier build still opens it and re-deriving an earlier session publishes the files it
/// published. The digests were measured with the build before revision 172 and must not change.
/// </summary>
public sealed class SegmentGoldenTests
{
    private const string GoldenSegmentDigest =
        "sha256:0331308bab89904adaf420ee80c30430f7e9201857e19a7433b67036e4ab43d7";

    private static readonly string[] GoldenDictionaryDigests =
    [
        "sha256:bd7c13e8a3d3b4ad78c652a1813f4ed565f9b21b6e9f0e4bb306360a53c1b0af",
        "sha256:6f414ed0a95812444b598bafa99bbe496b73ef464d9484c8d4760b80333b4bd4",
    ];

    [Fact(DisplayName = "I15: a segment of IPv4 and other records keeps the bytes observation-v1 has always had")]
    public void AnObservationV1SegmentKeepsItsBytes()
    {
        var writer = new SegmentWriterV1(Identity, 3);
        foreach (ObservationRowV1 row in Rows())
        {
            writer.Add(row);
        }

        SegmentBuildResult built = writer.Build();

        Assert.Equal(GoldenSegmentDigest, Digest(built.Segment));
        Assert.Equal(GoldenDictionaryDigests, built.Dictionaries.Select(dictionary => Digest(dictionary.Encode())));
    }

    private static string Digest(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// Every kind of value an `observation-v1` row holds: IPv4 endpoints, a zero address and port, names, identifiers, an
    /// unknown measurement, a status, a quarantined reading and markers, over three descriptors and two streams.
    /// </summary>
    private static IEnumerable<ObservationRowV1> Rows()
    {
        yield return Row(1_000, 1) with
        {
            EndpointAddressFamily = 4,
            SourceEndpointAddress = 0x7F00_0001,
            SourceEndpointPort = 52_100,
            DestinationEndpointAddress = 0x7F00_0001,
            DestinationEndpointPort = 443,
            OwnerProcessId = 4_242,
        };
        yield return Row(1_000, 2) with
        {
            EndpointAddressFamily = 4,
            SourceEndpointAddress = 0xC0A8_010A,
            SourceEndpointPort = 0,
            DestinationEndpointAddress = 0,
            DestinationEndpointPort = 9_000,
            ByteValue = null,
            ByteAvailability = FieldAvailability.NotExposed,
            MeasurementQuality = QualityLevel.UnknownQuality,
        };
        yield return Row(900, 3) with
        {
            RawStreamId = 2,
            EventId = 5,
            Opcode = 0,
            Mechanism = Mechanism.NamedPipe,
            Layer = ObservationLayer.Application,
            ResourceName = @"\Device\NamedPipe\golden",
            ByteDomain = ByteDomain.RequestedIo,
            Markers = SegmentRowMarkers.ResourceNameTruncated,
            ActivityId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
        };
        yield return Row(1_200, 4) with
        {
            EventId = 1,
            DescriptorVersion = 4,
            SchemaFingerprint = "sha256:" + new string('b', 64),
            Mechanism = Mechanism.ProcessLifecycle,
            Layer = ObservationLayer.Lifecycle,
            Kind = ObservationKind.Exit,
            Direction = Direction.DirectionNotApplicable,
            ByteValue = null,
            ByteDomain = null,
            AccountingSide = null,
            MeasurementUnit = null,
            ByteAvailability = FieldAvailability.NotApplicable,
            MeasurementQuality = QualityLevel.UnknownQuality,
            StatusCode = 3,
            StatusAvailability = FieldAvailability.Present,
            ResourceName = @"C:\Tools\golden.exe",
            SessionRelativeTicks = null,
            SourceIdentifier = Guid.Parse("367abb81-9844-35f1-ad32-98f038001003"),
        };
    }

    private static ObservationRowV1 Row(long ticks, ulong ordinal) => new()
    {
        RawStreamId = 1,
        RawSourceEpoch = 1,
        RawRecordOrdinal = ordinal,
        JournalRecordIndex = ordinal - 1,
        FactKey = FactKey.Create("network-transfer"),
        ProviderId = Guid.Parse("7dd42a49-5329-4832-8dfd-43d979153a88"),
        EventId = 10,
        DescriptorVersion = 0,
        SchemaFingerprint = "sha256:" + new string('a', 64),
        Opcode = 10,
        NativeTicks = ticks,
        SessionRelativeTicks = ticks * 100,
        HeaderProcessId = 1_234,
        HeaderThreadId = 5_678,
        ProcessorNumber = 3,
        Mechanism = Mechanism.Tcp,
        Layer = ObservationLayer.Transport,
        Kind = ObservationKind.Send,
        Direction = Direction.Outbound,
        ByteValue = 64 * (long)ordinal,
        ByteDomain = ByteDomain.TransportObserved,
        AccountingSide = AccountingSide.SendSide,
        MeasurementUnit = MeasurementUnit.Bytes,
        ByteAvailability = FieldAvailability.Present,
        StatusAvailability = FieldAvailability.NotApplicable,
        AttributionQuality = QualityLevel.Proven,
        CorrelationQuality = QualityLevel.UnknownQuality,
        MeasurementQuality = QualityLevel.Proven,
        TimingQuality = QualityLevel.Qualified,
    };

    private static SegmentIdentityV1 Identity { get; } = new()
    {
        CaptureId = new(Guid.Parse("a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d")),
        ClockId = new(Guid.Parse("11112222-3333-4444-8555-666677778888")),
        TimestampEncoding = TimestampEncoding.Qpc,
        Derivation = NormalizerContractVersion.V1,
    };
}
