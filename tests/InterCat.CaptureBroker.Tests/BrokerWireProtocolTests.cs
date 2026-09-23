using System.Buffers.Binary;
using InterCat.Capture.Windows;
using InterCat.Domain;
using Xunit;

namespace InterCat.CaptureBroker.Tests;

public sealed class BrokerWireProtocolTests
{
    public static IEnumerable<object[]> Requests()
    {
        Guid capture = Guid.Parse("10000000-2000-3000-4000-500000000000");
        Guid request = Guid.Parse("60000000-7000-8000-9000-a00000000000");
        yield return [new BrokerHelloRequest(
            Guid.Parse("01000000-0200-0300-0400-050000000000"),
            1,
            1,
            BrokerHelloNegotiator.SupportedFeatures,
            BrokerProtocolFeature.PreparedPlanDigest)];
        yield return [new BrokerGetCapabilitiesRequest()];
        yield return [new BrokerPrepareCaptureRequest(
            "explore",
            null,
            [],
            false,
            false,
            ValidQuota(),
            BrokerRetentionPolicy.StopAtLimit,
            null)];
        yield return [new BrokerPrepareCaptureRequest(
            "focused-transport",
            Mechanism.Tcp,
            [84, 4242],
            true,
            false,
            ValidQuota(),
            BrokerRetentionPolicy.StopAtLimit,
            null)];
        yield return [new BrokerPrepareCaptureRequest(
            "focused-transport",
            Mechanism.Tcp,
            [],
            false,
            false,
            ValidQuota(),
            BrokerRetentionPolicy.StopAtLimit,
            null,
            BrokerJournalPublication.Live)];
        yield return [new BrokerPrepareCaptureRequest(
            "content",
            null,
            [],
            false,
            false,
            ValidQuota(),
            BrokerRetentionPolicy.StopAtLimit,
            new()
            {
                SourceId = WindowsSourceCatalog.RpcSourceId,
                Mechanism = Mechanism.Rpc,
                ProcessIds = [84, 4242],
                ChannelSelectors = ["rpc-interface:12345678-1234-1234-1234-123456789abc"],
                MaximumRecordBytes = 4096,
                MaximumSessionBytes = 64 * 1024 * 1024,
                Retention = ContentRetentionMode.StopAtLimit,
                Inspection = ContentInspectionMode.HexAndText,
            })];
        yield return [new BrokerStartCaptureRequest(new string('A', 43), request)];
        yield return [new BrokerGetStatusRequest(new(capture))];
        yield return [new BrokerStopCaptureRequest(new(capture), request)];
        yield return [new BrokerRenewOwnerLeaseRequest(new(capture))];
    }

    [Theory]
    [MemberData(nameof(Requests))]
    public async Task EveryCommandRoundTripsThroughFrameAndTypedPayload(BrokerWireRequest request)
    {
        Guid correlation = Guid.NewGuid();
        BrokerWireFrame encoded = BrokerWireRequestCodec.Encode(request, correlation);
        await using var stream = new MemoryStream();
        await BrokerWireFrameCodec.WriteAsync(stream, encoded);
        stream.Position = 0;

        BrokerWireFrame decodedFrame = Assert.IsType<BrokerWireFrame>(
            await BrokerWireFrameCodec.ReadAsync(stream));
        BrokerWireRequest decoded = BrokerWireRequestCodec.Decode(decodedFrame);

        Assert.Equal(correlation, decodedFrame.CorrelationId);
        Assert.Equal(request.MessageType, decoded.MessageType);
        AssertRequestEqual(request, decoded);
        Assert.Null(await BrokerWireFrameCodec.ReadAsync(stream));
    }

    [Fact]
    public async Task ArbitraryOneByteFragmentationStillReadsExactlyOneFrame()
    {
        BrokerWireFrame frame = BrokerWireRequestCodec.Encode(
            new BrokerStopCaptureRequest(new(Guid.NewGuid()), Guid.NewGuid()),
            Guid.NewGuid());
        byte[] bytes = await Serialize(frame);
        await using var stream = new FragmentedReadStream(bytes, maximumChunk: 1);

        BrokerWireFrame decoded = Assert.IsType<BrokerWireFrame>(
            await BrokerWireFrameCodec.ReadAsync(stream));

        Assert.IsType<BrokerStopCaptureRequest>(BrokerWireRequestCodec.Decode(decoded));
        Assert.Null(await BrokerWireFrameCodec.ReadAsync(stream));
    }

    [Fact]
    public async Task EveryTruncatedPrefixIsRefusedButCleanEofIsNotAnError()
    {
        BrokerWireFrame frame = BrokerWireRequestCodec.Encode(
            new BrokerStartCaptureRequest(new string('A', 43), Guid.NewGuid()),
            Guid.NewGuid());
        byte[] bytes = await Serialize(frame);

        await using (var empty = new MemoryStream())
        {
            Assert.Null(await BrokerWireFrameCodec.ReadAsync(empty));
        }

        for (int length = 1; length < bytes.Length; length++)
        {
            await using var truncated = new MemoryStream(bytes, 0, length, writable: false);
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await BrokerWireFrameCodec.ReadAsync(truncated));
        }
    }

    [Fact]
    public async Task OversizedPayloadLengthIsRejectedBeforePayloadRead()
    {
        BrokerWireFrame frame = BrokerWireRequestCodec.Encode(
            new BrokerGetCapabilitiesRequest(),
            Guid.NewGuid());
        byte[] bytes = await Serialize(frame);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(12, 4),
            BrokerWireFrameCodec.MaximumPayloadBytes + 1U);
        await using var stream = new MemoryStream(bytes, writable: false);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await BrokerWireFrameCodec.ReadAsync(stream));

        Assert.Equal(BrokerWireFrameCodec.HeaderSize, stream.Position);
    }

    [Fact]
    public void JournalPublicationIsANamedPolicyNeverAnArbitraryValue()
    {
        var request = new BrokerPrepareCaptureRequest(
            "explore", null, [], false, false, ValidQuota(), BrokerRetentionPolicy.StopAtLimit, null);
        Assert.Equal(BrokerJournalPublication.OnStop, request.Publication);

        var unknown = request with { Publication = (BrokerJournalPublication)1_000 };
        Assert.Throws<InvalidDataException>(() =>
            BrokerWireRequestCodec.Decode(BrokerWireRequestCodec.Encode(unknown, Guid.NewGuid())));
    }

    [Fact]
    public void LivePublicationIntervalIsBoundedForEveryAllowedDuration()
    {
        Assert.Null(BrokerJournalPublicationPolicy.Interval(BrokerJournalPublication.OnStop, 600));
        Assert.Equal(TimeSpan.FromSeconds(2), BrokerJournalPublicationPolicy.Interval(BrokerJournalPublication.Live, 600));
        Assert.Equal(TimeSpan.FromSeconds(85), BrokerJournalPublicationPolicy.Interval(BrokerJournalPublication.Live, 86_400));
        for (int seconds = 1; seconds <= 86_400; seconds++)
        {
            TimeSpan interval = BrokerJournalPublicationPolicy.Interval(BrokerJournalPublication.Live, seconds)!.Value;
            Assert.True(interval >= BrokerJournalPublicationPolicy.MinimumLiveInterval);
            Assert.True(
                Math.Ceiling(seconds / interval.TotalSeconds) <= BrokerJournalPublicationPolicy.MaximumLiveChunks,
                $"{seconds} s at {interval} exceeds the live chunk bound");
        }
    }

    [Fact]
    public void UnknownOptionalFieldIsSkippedButUnknownRequiredFieldIsRefused()
    {
        byte[] optionalPayload = Field(99, type: 1, required: false, [1, 0, 0, 0]);
        BrokerWireFrame optional = BrokerWireFrame.CreateRequest(
            BrokerMessageType.GetCapabilities,
            Guid.NewGuid(),
            optionalPayload);

        Assert.IsType<BrokerGetCapabilitiesRequest>(BrokerWireRequestCodec.Decode(optional));

        byte[] requiredPayload = Field(99, type: 1, required: true, [1, 0, 0, 0]);
        BrokerWireFrame required = BrokerWireFrame.CreateRequest(
            BrokerMessageType.GetCapabilities,
            Guid.NewGuid(),
            requiredPayload);
        Assert.Throws<InvalidDataException>(() => BrokerWireRequestCodec.Decode(required));
    }

    [Fact]
    public void DuplicateAndInvalidUtf8FieldsAreRefused()
    {
        Guid captureId = Guid.NewGuid();
        byte[] capture = new byte[16];
        captureId.TryWriteBytes(capture, bigEndian: true, out _);
        byte[] one = Field(1, type: 6, required: true, capture);
        byte[] duplicate = [.. one, .. one];
        BrokerWireFrame duplicateFrame = BrokerWireFrame.CreateRequest(
            BrokerMessageType.GetStatus,
            Guid.NewGuid(),
            duplicate);
        Assert.Throws<InvalidDataException>(() => BrokerWireRequestCodec.Decode(duplicateFrame));

        byte[] invalidUtf8 = Field(1, type: 5, required: true, [0xc3, 0x28]);
        BrokerWireFrame utf8Frame = BrokerWireFrame.CreateRequest(
            BrokerMessageType.StartCapture,
            Guid.NewGuid(),
            invalidUtf8);
        Assert.Throws<InvalidDataException>(() => BrokerWireRequestCodec.Decode(utf8Frame));
    }

    [Fact]
    public async Task InvalidFlagsAndUnsupportedCommandAreRefusedAtFrameBoundary()
    {
        BrokerWireFrame frame = BrokerWireRequestCodec.Encode(
            new BrokerGetCapabilitiesRequest(),
            Guid.NewGuid());
        byte[] invalidFlags = await Serialize(frame);
        BinaryPrimitives.WriteUInt16LittleEndian(invalidFlags.AsSpan(10, 2), 3);
        await using var flagsStream = new MemoryStream(invalidFlags, writable: false);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await BrokerWireFrameCodec.ReadAsync(flagsStream));

        byte[] invalidCommand = await Serialize(frame);
        BinaryPrimitives.WriteUInt16LittleEndian(invalidCommand.AsSpan(8, 2), 254);
        await using var commandStream = new MemoryStream(invalidCommand, writable: false);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await BrokerWireFrameCodec.ReadAsync(commandStream));
    }

    [Fact]
    public void HelloNegotiationRequiresCommonVersionAndRequiredFeatures()
    {
        Guid server = Guid.NewGuid();
        var accepted = new BrokerHelloRequest(
            Guid.NewGuid(),
            1,
            2,
            BrokerProtocolFeature.PreparedPlanDigest | BrokerProtocolFeature.OwnerLeases,
            BrokerProtocolFeature.PreparedPlanDigest);

        BrokerHelloNegotiation success = BrokerHelloNegotiator.Negotiate(accepted, server, "fixture");
        BrokerHelloNegotiation noVersion = BrokerHelloNegotiator.Negotiate(
            accepted with { MinimumMajor = 2, MaximumMajor = 3 },
            server,
            "fixture");
        var futureOptional = (BrokerProtocolFeature)(1UL << 63);
        BrokerHelloNegotiation optionalUnknown = BrokerHelloNegotiator.Negotiate(
            accepted with { RequestedFeatures = accepted.RequestedFeatures | futureOptional },
            server,
            "fixture");
        BrokerHelloNegotiation requiredUnknown = BrokerHelloNegotiator.Negotiate(
            accepted with
            {
                RequestedFeatures = accepted.RequestedFeatures | futureOptional,
                RequiredFeatures = accepted.RequiredFeatures | futureOptional,
            },
            server,
            "fixture");

        Assert.True(success.Accepted);
        Assert.Equal(BrokerWireFrameCodec.CurrentMajor, success.Response!.SelectedMajor);
        Assert.Equal(accepted.RequestedFeatures, success.Response.EnabledFeatures);
        Assert.False(noVersion.Accepted);
        Assert.Contains("No common", noVersion.Refusal, StringComparison.Ordinal);
        Assert.True(optionalUnknown.Accepted);
        Assert.Equal(accepted.RequestedFeatures, optionalUnknown.Response!.EnabledFeatures);
        Assert.False(requiredUnknown.Accepted);
        Assert.Contains("unavailable", requiredUnknown.Refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidPrepareCombinationsAreRejectedBeforeEncoding()
    {
        var invalid = new BrokerPrepareCaptureRequest(
            "explore",
            Mechanism.Tcp,
            [84],
            true,
            false,
            ValidQuota(),
            BrokerRetentionPolicy.StopAtLimit,
            null);

        Assert.Throws<InvalidDataException>(() =>
            BrokerWireRequestCodec.Encode(invalid, Guid.NewGuid()));
    }

    private static BrokerCaptureQuota ValidQuota() => new(
        MaximumDurationSeconds: 300,
        MaximumJournalBytes: 1024L * 1024 * 1024,
        MinimumFreeDiskBytes: 64L * 1024 * 1024);

    private static async Task<byte[]> Serialize(BrokerWireFrame frame)
    {
        await using var stream = new MemoryStream();
        await BrokerWireFrameCodec.WriteAsync(stream, frame);
        return stream.ToArray();
    }

    private static byte[] Field(ushort id, byte type, bool required, byte[] value)
    {
        byte[] field = new byte[8 + value.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(field.AsSpan(0, 2), id);
        field[2] = type;
        field[3] = required ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(field.AsSpan(4, 4), value.Length);
        value.CopyTo(field, 8);
        return field;
    }

    private static void AssertRequestEqual(BrokerWireRequest expected, BrokerWireRequest actual)
    {
        switch (expected, actual)
        {
            case (BrokerHelloRequest left, BrokerHelloRequest right):
                Assert.Equal(left, right);
                break;
            case (BrokerGetCapabilitiesRequest, BrokerGetCapabilitiesRequest):
                break;
            case (BrokerPrepareCaptureRequest left, BrokerPrepareCaptureRequest right):
                Assert.Equal(left.ProfileId, right.ProfileId);
                Assert.Equal(left.FocusedMechanism, right.FocusedMechanism);
                Assert.Equal(left.FocusedProcessIds, right.FocusedProcessIds);
                Assert.Equal(left.AllowBroaderCapture, right.AllowBroaderCapture);
                Assert.Equal(left.RequestOriginalDiagnosticEtl, right.RequestOriginalDiagnosticEtl);
                Assert.Equal(left.Quota, right.Quota);
                Assert.Equal(left.Retention, right.Retention);
                Assert.Equal(left.Publication, right.Publication);
                if (left.Content is null)
                {
                    Assert.Null(right.Content);
                }
                else
                {
                    Assert.NotNull(right.Content);
                    Assert.Equal(left.Content.SourceId, right.Content.SourceId);
                    Assert.Equal(left.Content.Mechanism, right.Content.Mechanism);
                    Assert.Equal(left.Content.ProcessIds, right.Content.ProcessIds);
                    Assert.Equal(left.Content.ChannelSelectors, right.Content.ChannelSelectors);
                    Assert.Equal(left.Content.MaximumRecordBytes, right.Content.MaximumRecordBytes);
                    Assert.Equal(left.Content.MaximumSessionBytes, right.Content.MaximumSessionBytes);
                    Assert.Equal(left.Content.Retention, right.Content.Retention);
                    Assert.Equal(left.Content.Inspection, right.Content.Inspection);
                }

                break;
            default:
                Assert.Equal(expected, actual);
                break;
        }
    }

    private sealed class FragmentedReadStream(byte[] bytes, int maximumChunk) : Stream
    {
        private readonly MemoryStream inner = new(bytes, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, Math.Min(count, maximumChunk));
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer[..Math.Min(buffer.Length, maximumChunk)], cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
