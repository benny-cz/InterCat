using System.Buffers.Binary;

namespace InterCat.CaptureBroker;

public enum BrokerMessageType : ushort
{
    Hello = 1,
    GetCapabilities = 2,
    PrepareCapture = 3,
    StartCapture = 4,
    GetStatus = 5,
    StopCapture = 6,
    RenewOwnerLease = 7,
    Error = 255,
}

[Flags]
public enum BrokerFrameAttributes : ushort
{
    Request = 1,
    Response = 2,
    Error = 4,
}

/// <summary>One bounded control frame. Payload memory is always owned by this instance.</summary>
public sealed class BrokerWireFrame
{
    internal BrokerWireFrame(
        ushort protocolMajor,
        ushort protocolMinor,
        BrokerMessageType messageType,
        BrokerFrameAttributes flags,
        Guid correlationId,
        byte[] payload,
        bool takeOwnership)
    {
        ProtocolMajor = protocolMajor;
        ProtocolMinor = protocolMinor;
        MessageType = messageType;
        Flags = flags;
        CorrelationId = correlationId;
        Payload = takeOwnership ? payload : [.. payload];
    }

    public ushort ProtocolMajor { get; }
    public ushort ProtocolMinor { get; }
    public BrokerMessageType MessageType { get; }
    public BrokerFrameAttributes Flags { get; }
    public Guid CorrelationId { get; }
    public ReadOnlyMemory<byte> Payload { get; }

    public static BrokerWireFrame CreateRequest(
        BrokerMessageType messageType,
        Guid correlationId,
        ReadOnlySpan<byte> payload,
        ushort protocolMajor = BrokerWireFrameCodec.CurrentMajor,
        ushort protocolMinor = BrokerWireFrameCodec.CurrentMinor) =>
        new(
            protocolMajor,
            protocolMinor,
            messageType,
            BrokerFrameAttributes.Request,
            correlationId,
            payload.ToArray(),
            takeOwnership: true);
}

/// <summary>
/// Fixed 32-byte header plus a payload capped before allocation. Reads tolerate arbitrary stream
/// fragmentation and distinguish a clean EOF from a truncated frame.
/// </summary>
public static class BrokerWireFrameCodec
{
    public const ushort CurrentMajor = 1;
    public const ushort CurrentMinor = 0;
    public const int HeaderSize = 32;
    public const int MaximumPayloadBytes = 64 * 1024;

    private static readonly byte[] Magic = "ICBP"u8.ToArray();

    public static async ValueTask<BrokerWireFrame?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] header = new byte[HeaderSize];
        int first = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (first == 0)
        {
            return null;
        }

        try
        {
            await stream.ReadExactlyAsync(header.AsMemory(1), cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("The broker frame header is truncated.", exception);
        }

        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("The broker frame magic is invalid.");
        }

        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4, 2));
        ushort minor = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6, 2));
        var messageType = (BrokerMessageType)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8, 2));
        var flags = (BrokerFrameAttributes)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10, 2));
        uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4));
        var correlationId = new Guid(header.AsSpan(16, 16), bigEndian: true);
        ValidateHeader(major, messageType, flags, payloadLength, correlationId);

        byte[] payload = new byte[payloadLength];
        if (payload.Length > 0)
        {
            try
            {
                await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException exception)
            {
                throw new InvalidDataException("The broker frame payload is truncated.", exception);
            }
        }

        return new(major, minor, messageType, flags, correlationId, payload, takeOwnership: true);
    }

    public static async ValueTask WriteAsync(
        Stream stream,
        BrokerWireFrame frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(frame);
        ValidateHeader(
            frame.ProtocolMajor,
            frame.MessageType,
            frame.Flags,
            checked((uint)frame.Payload.Length),
            frame.CorrelationId);

        byte[] header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4, 2), frame.ProtocolMajor);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6, 2), frame.ProtocolMinor);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8, 2), (ushort)frame.MessageType);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10, 2), (ushort)frame.Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12, 4), checked((uint)frame.Payload.Length));
        if (!frame.CorrelationId.TryWriteBytes(
            header.AsSpan(16, 16),
            bigEndian: true,
            out int bytesWritten)
            || bytesWritten != 16)
        {
            throw new InvalidOperationException("The frame correlation ID could not be encoded.");
        }

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (!frame.Payload.IsEmpty)
        {
            await stream.WriteAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidateHeader(
        ushort major,
        BrokerMessageType messageType,
        BrokerFrameAttributes flags,
        uint payloadLength,
        Guid correlationId)
    {
        if (major == 0)
        {
            throw new InvalidDataException("Broker protocol major version zero is invalid.");
        }

        if (!Enum.IsDefined(messageType))
        {
            throw new InvalidDataException($"Broker message type {(ushort)messageType} is unsupported.");
        }

        bool request = (flags & BrokerFrameAttributes.Request) != 0;
        bool response = (flags & BrokerFrameAttributes.Response) != 0;
        bool invalidFlags = request == response
            || (flags & ~(BrokerFrameAttributes.Request | BrokerFrameAttributes.Response | BrokerFrameAttributes.Error)) != 0
            || ((flags & BrokerFrameAttributes.Error) != 0 && !response);
        if (invalidFlags)
        {
            throw new InvalidDataException("Broker frame flags must identify exactly one request/response direction; error is response-only.");
        }

        if (payloadLength > MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                $"Broker frame payload {payloadLength} exceeds the {MaximumPayloadBytes}-byte limit.");
        }

        if (correlationId == Guid.Empty)
        {
            throw new InvalidDataException("A broker frame requires a non-empty correlation ID.");
        }
    }
}
