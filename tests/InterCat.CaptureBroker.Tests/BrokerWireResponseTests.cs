using System.Buffers.Binary;
using InterCat.Capture.Windows;
using InterCat.Domain;
using Xunit;
using static InterCat.CaptureBroker.Tests.BrokerPlanFixture;

namespace InterCat.CaptureBroker.Tests;

public sealed class BrokerWireResponseTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 14, 0, 0, TimeSpan.Zero);

    public static IEnumerable<object[]> Responses()
    {
        CaptureId captureId = new(Guid.Parse("10000000-2000-3000-4000-500000000000"));
        BrokerStopMilestones milestones = new(true, true, true, false, false);
        yield return [new BrokerErrorResponse(BrokerErrorCode.HelloRequired, "Say Hello first.", false)];
        yield return [new BrokerHelloResponse(
            Guid.Parse("01000000-0200-0300-0400-050000000000"),
            1,
            0,
            BrokerHelloNegotiator.SupportedFeatures,
            "fixture-1")];
        yield return [Capabilities()];
        yield return [new BrokerPrepareCaptureResponse(
            true,
            null,
            null,
            new(new string('A', 43), Digest('a'), Now, Now.AddSeconds(30)),
            Summary())];
        yield return [new BrokerPrepareCaptureResponse(
            true,
            null,
            null,
            new(new string('B', 43), Digest('c'), Now, Now.AddSeconds(30)),
            Summary() with
            {
                Publication = BrokerJournalPublication.Live,
                PublicationIntervalMilliseconds = BrokerJournalPublicationPolicy.IntervalMilliseconds(
                    BrokerJournalPublication.Live, Quota.MaximumDurationSeconds),
            })];
        yield return [new BrokerPrepareCaptureResponse(
            false,
            BrokerPrepareRefusalCode.PlanNotStartable,
            "Review the effective scope before starting.",
            null,
            Summary() with { EffectiveProfileId = null, EffectiveAdmission = null })];
        yield return [new BrokerStartCaptureResponse(
            BrokerOperationCode.Started,
            captureId,
            CaptureLifecycle.Recording,
            Now.AddSeconds(30),
            null)];
        yield return [new BrokerCaptureStatusResponse(
            captureId,
            CaptureLifecycle.Recording,
            Digest('b'),
            Now,
            Now.AddSeconds(1),
            Now.AddSeconds(30),
            BrokerStopMilestones.None,
            null)];
        yield return [new BrokerCaptureStatusResponse(
            captureId,
            CaptureLifecycle.Recording,
            Digest('b'),
            Now,
            Now.AddSeconds(1),
            Now.AddSeconds(30),
            BrokerStopMilestones.None,
            null,
            @"C:\ProgramData\InterCat\capture-1",
            new BrokerCaptureHealth(1_234, 1_300, 2, 0, 1, 17, 65_536))];
        yield return [new BrokerCaptureStatusResponse(
            captureId,
            CaptureLifecycle.Recording,
            Digest('b'),
            Now,
            Now.AddSeconds(1),
            Now.AddSeconds(30),
            BrokerStopMilestones.None,
            null,
            Health: new BrokerCaptureHealth(10, 10, 0, null, null, 0, 1_024))];
        yield return [new BrokerStopCaptureResponse(
            BrokerOperationCode.StopPartial,
            captureId,
            CaptureLifecycle.Finalizing,
            milestones,
            "Analysis finalization is pending.")];
        yield return [new BrokerRenewOwnerLeaseResponse(
            BrokerOperationCode.LeaseRenewed,
            captureId,
            CaptureLifecycle.Recording,
            Now.AddSeconds(45),
            null)];
    }

    [Theory]
    [MemberData(nameof(Responses))]
    public async Task EveryResponseRoundTripsCanonically(BrokerWireResponse response)
    {
        Guid correlationId = Guid.NewGuid();
        BrokerWireFrame encoded = BrokerWireResponseCodec.Encode(response, correlationId);
        await using var stream = new MemoryStream();
        await BrokerWireFrameCodec.WriteAsync(stream, encoded);
        stream.Position = 0;

        BrokerWireFrame frame = Assert.IsType<BrokerWireFrame>(await BrokerWireFrameCodec.ReadAsync(stream));
        BrokerWireResponse decoded = BrokerWireResponseCodec.Decode(frame);
        BrokerWireFrame reencoded = BrokerWireResponseCodec.Encode(decoded, correlationId);

        Assert.Equal(response.GetType(), decoded.GetType());
        Assert.Equal(correlationId, frame.CorrelationId);
        Assert.Equal(encoded.Flags, reencoded.Flags);
        Assert.Equal(encoded.MessageType, reencoded.MessageType);
        Assert.Equal(encoded.Payload.ToArray(), reencoded.Payload.ToArray());
        Assert.Null(await BrokerWireFrameCodec.ReadAsync(stream));
    }

    [Fact]
    public void SummaryPublicationIntervalMustBeTheOneItsPolicyCompilesTo()
    {
        var inconsistent = new BrokerPrepareCaptureResponse(
            true,
            null,
            null,
            new(new string('A', 43), Digest('a'), Now, Now.AddSeconds(30)),
            Summary() with { Publication = BrokerJournalPublication.Live, PublicationIntervalMilliseconds = 50 });

        Assert.Throws<InvalidDataException>(() =>
            BrokerWireResponseCodec.Decode(BrokerWireResponseCodec.Encode(inconsistent, Guid.NewGuid())));
    }

    [Fact]
    public void PrepareGrantAndRefusalMustBeCompleteAndExclusive()
    {
        var missingGrant = new BrokerPrepareCaptureResponse(true, null, null, null, Summary());
        var mixed = new BrokerPrepareCaptureResponse(
            true,
            BrokerPrepareRefusalCode.PlanNotStartable,
            "No.",
            new(new string('A', 43), Digest('a'), Now, Now.AddSeconds(30)),
            Summary());

        Assert.Throws<InvalidDataException>(() => BrokerWireResponseCodec.Encode(missingGrant, Guid.NewGuid()));
        Assert.Throws<InvalidDataException>(() => BrokerWireResponseCodec.Encode(mixed, Guid.NewGuid()));
    }

    [Fact]
    public void UnknownOptionalResponseFieldIsSkippedAndRequiredOneIsRefused()
    {
        Guid correlationId = Guid.NewGuid();
        BrokerWireFrame valid = BrokerWireResponseCodec.Encode(
            new BrokerErrorResponse(BrokerErrorCode.InvalidRequest, "Invalid.", false),
            correlationId);
        byte[] optionalPayload = [.. valid.Payload.ToArray(), .. Field(99, required: false)];
        byte[] requiredPayload = [.. valid.Payload.ToArray(), .. Field(99, required: true)];

        BrokerWireResponse decoded = BrokerWireResponseCodec.Decode(BrokerWireFrame.CreateResponse(
            BrokerMessageType.Error,
            correlationId,
            optionalPayload,
            isError: true));

        Assert.IsType<BrokerErrorResponse>(decoded);
        Assert.Throws<InvalidDataException>(() => BrokerWireResponseCodec.Decode(BrokerWireFrame.CreateResponse(
            BrokerMessageType.Error,
            correlationId,
            requiredPayload,
            isError: true)));
    }

    [Fact]
    public void LiveCountersArriveWholeAndPossibleOrNotAtAll()
    {
        var status = new BrokerCaptureStatusResponse(
            new CaptureId(Guid.NewGuid()), CaptureLifecycle.Recording, Digest('b'), Now, Now.AddSeconds(1),
            Now.AddSeconds(30), BrokerStopMilestones.None, null);
        Guid correlationId = Guid.NewGuid();
        BrokerWireFrame plain = BrokerWireResponseCodec.Encode(status, correlationId);
        Assert.Null(Assert.IsType<BrokerCaptureStatusResponse>(BrokerWireResponseCodec.Decode(plain)).Health);

        // Loss that could not be read travels as absent, and reads back as unknown rather than zero.
        var unread = BrokerWireResponseCodec.Decode(BrokerWireResponseCodec.Encode(
            status with { Health = new BrokerCaptureHealth(5, 5, 0, null, null, 0, 16) }, correlationId));
        Assert.Null(Assert.IsType<BrokerCaptureStatusResponse>(unread).Health!.ProviderReportedLoss);

        // Part of the set, or impossible values, are refused rather than read as zeros.
        byte[] partial = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(partial.AsSpan(0, 2), 14);
        partial[2] = 2;
        BinaryPrimitives.WriteInt32LittleEndian(partial.AsSpan(4, 4), 8);
        BinaryPrimitives.WriteInt64LittleEndian(partial.AsSpan(8, 8), 99);
        Assert.Throws<InvalidDataException>(() => BrokerWireResponseCodec.Decode(BrokerWireFrame.CreateResponse(
            BrokerMessageType.GetStatus, correlationId, [.. plain.Payload.ToArray(), .. partial])));
        Assert.Throws<InvalidDataException>(() => BrokerWireResponseCodec.Encode(
            status with { Health = new BrokerCaptureHealth(-1, 0, 0, 0, 0, 0, 16) }, correlationId));
        Assert.Throws<InvalidDataException>(() => BrokerWireResponseCodec.Encode(
            status with { Health = new BrokerCaptureHealth(1, 1, 0, 0, 0, 17, 16) }, correlationId));
    }

    [Fact]
    public void StatusResponseHasNoRuntimeOwnershipCredential()
    {
        string[] publicProperties = typeof(BrokerCaptureStatusResponse)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain("Session", publicProperties);
        Assert.DoesNotContain("OwnershipToken", publicProperties);
        Assert.DoesNotContain("Owner", publicProperties);
    }

    private static BrokerCapabilitiesResponse Capabilities() => new(
        Now,
        "Windows fixture",
        "10.0.26200.0-x64",
        "X64",
        true,
        BuildSupportTier.Primary,
        true,
        CapabilityInventoryProbe.AdapterVersion,
        [new("explore", "Explore", AdmissionMode.MetadataOnly, true, true, "Safe metadata.", null)],
        [new(Mechanism.Tcp, CapabilityState.Available, CapabilityTier.TrafficVisualization, "Measured TCP.", null)]);

    private static BrokerEffectiveCaptureSummary Summary() => new(
        "focused-transport",
        "focused-transport",
        AdmissionMode.MetadataOnly,
        AdmissionMode.MetadataOnly,
        Mechanism.Tcp,
        Mechanism.Tcp,
        [84],
        [84],
        true,
        true,
        true,
        [
            new(
                WindowsSourceCatalog.KernelNetworkSourceId,
                ProviderProcessScope.WholeMachineFilterUnavailable,
                [],
                true,
                "The provider cannot filter before delivery."),
        ],
        "Broader capture accepted.",
        "Collects metadata only.",
        Quota,
        BrokerRetentionPolicy.StopAtLimit,
        ["A bounded diagnostic."]);

    private static string Digest(char character) => "sha256:" + new string(character, 64);

    private static byte[] Field(ushort id, bool required)
    {
        byte[] bytes = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0, 2), id);
        bytes[2] = 1;
        bytes[3] = required ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), 4);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8, 4), 1);
        return bytes;
    }
}
