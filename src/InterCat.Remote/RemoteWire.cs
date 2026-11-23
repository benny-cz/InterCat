using System.Buffers.Binary;
using System.Text;

namespace InterCat.Remote;

/// <summary>The messages a version 1.0 agent answers (`remote-agent-v1` §4): nothing else, and nothing that runs a command.</summary>
public enum RemoteMessageType : ushort
{
    Hello = 1,
    Enroll = 2,
    Inventory = 3,
    Error = 255,
}

[Flags]
public enum RemoteFrameAttributes : ushort
{
    Request = 1,
    Response = 2,
    Error = 4,
}

/// <summary>Why an agent refused a request (`remote-agent-v1` §6).</summary>
public enum RemoteErrorCode : ushort
{
    /// <summary>The frame or a field of it is not one version 1 writes.</summary>
    Malformed = 1,

    /// <summary>A message type or protocol version the agent does not serve.</summary>
    Unsupported = 2,

    /// <summary>The controller's key is not enrolled.</summary>
    NotEnrolled = 3,

    /// <summary>The token's secret is unknown, used or expired: which, the agent does not say.</summary>
    TokenRefused = 4,

    /// <summary>The controller's key was revoked, and is enrolled again by no token.</summary>
    Revoked = 5,

    /// <summary>The controller was not granted what it asked for, or the agent's local policy forbids it.</summary>
    NotPermitted = 6,

    /// <summary>The agent serves as many connections or requests as it will.</summary>
    Busy = 7,
}

/// <summary>One bounded frame: a 32-byte header and a payload of fields (`remote-agent-v1` §3).</summary>
public sealed class RemoteFrame
{
    public RemoteFrame(RemoteMessageType type, RemoteFrameAttributes flags, Guid correlationId, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Type = type;
        Flags = flags;
        CorrelationId = correlationId;
        Payload = payload;
    }

    public RemoteMessageType Type { get; }
    public RemoteFrameAttributes Flags { get; }
    public Guid CorrelationId { get; }
    public byte[] Payload { get; }

    /// <summary>Whether the frame answers with a refusal.</summary>
    public bool IsError => (Flags & RemoteFrameAttributes.Error) != 0;
}

/// <summary>
/// Reads and writes frames over a stream already secured by TLS: a header of the magic <c>ICRA</c>, the protocol's major
/// and minor version, the message type, its flags, the payload's length and a correlation identifier, then the payload,
/// whose length is checked before anything is allocated for it.
/// </summary>
public static class RemoteFrameCodec
{
    public const ushort Major = 1;
    public const ushort Minor = 0;
    public const int HeaderSize = 32;
    public const int MaximumPayloadBytes = 64 * 1024;

    private static readonly byte[] Magic = "ICRA"u8.ToArray();

    /// <summary>The next frame, or null where the stream ended cleanly between frames.</summary>
    /// <exception cref="InvalidDataException">The frame is not one version 1 writes, or it ended part way.</exception>
    public static async ValueTask<RemoteFrame?> ReadAsync(Stream stream, CancellationToken cancellationToken)
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
            throw new InvalidDataException("A remote frame's header ended part way.", exception);
        }

        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("A remote frame begins with the wrong magic.");
        }

        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4, 2));
        var type = (RemoteMessageType)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8, 2));
        var flags = (RemoteFrameAttributes)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10, 2));
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4));
        var correlation = new Guid(header.AsSpan(16, 16), bigEndian: true);
        if (major != Major)
        {
            throw new InvalidDataException($"A remote frame is of protocol {major}, not {Major}.");
        }

        if (length > MaximumPayloadBytes)
        {
            throw new InvalidDataException($"A remote frame's payload of {length:N0} bytes is more than {MaximumPayloadBytes:N0}.");
        }

        if ((flags & ~(RemoteFrameAttributes.Request | RemoteFrameAttributes.Response | RemoteFrameAttributes.Error)) != 0
            || ((flags & RemoteFrameAttributes.Request) != 0) == ((flags & RemoteFrameAttributes.Response) != 0))
        {
            throw new InvalidDataException("A remote frame is a request or a response, and carries no other flag.");
        }

        byte[] payload = new byte[length];
        try
        {
            await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("A remote frame's payload ended part way.", exception);
        }

        return new(type, flags, correlation, payload);
    }

    public static async ValueTask WriteAsync(Stream stream, RemoteFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Payload.Length > MaximumPayloadBytes)
        {
            throw new InvalidOperationException($"A remote frame's payload is at most {MaximumPayloadBytes:N0} bytes.");
        }

        byte[] bytes = new byte[HeaderSize + frame.Payload.Length];
        Magic.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4, 2), Major);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6, 2), Minor);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), (ushort)frame.Type);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10, 2), (ushort)frame.Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), (uint)frame.Payload.Length);
        _ = frame.CorrelationId.TryWriteBytes(bytes.AsSpan(16, 16), bigEndian: true, out _);
        frame.Payload.CopyTo(bytes, HeaderSize);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// A payload's fields: each an identifier, a length and its bytes, at most 64 of them, each at most 16 KiB, no identifier
/// twice. A reader takes the fields it knows and refuses one it needs that is missing or of the wrong length; a field it
/// does not know it passes over, so a later minor version may add fields.
/// </summary>
public sealed class RemoteFields
{
    public const int MaximumFields = 64;
    public const int MaximumFieldBytes = 16 * 1024;

    private readonly Dictionary<ushort, byte[]> fields;

    private RemoteFields(Dictionary<ushort, byte[]> fields) => this.fields = fields;

    /// <summary>The fields of <paramref name="payload"/>.</summary>
    /// <exception cref="InvalidDataException">The payload is not a run of well-formed fields.</exception>
    public static RemoteFields Read(ReadOnlySpan<byte> payload)
    {
        var fields = new Dictionary<ushort, byte[]>();
        int at = 0;
        while (at < payload.Length)
        {
            if (payload.Length - at < 4)
            {
                throw new InvalidDataException("A remote field's header ends part way.");
            }

            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(payload[at..]);
            ushort length = BinaryPrimitives.ReadUInt16LittleEndian(payload[(at + 2)..]);
            at += 4;
            if (length > MaximumFieldBytes || payload.Length - at < length)
            {
                throw new InvalidDataException($"Remote field {id} claims {length:N0} bytes the payload does not hold.");
            }

            if (fields.Count == MaximumFields)
            {
                throw new InvalidDataException($"A remote payload holds at most {MaximumFields} fields.");
            }

            if (!fields.TryAdd(id, payload.Slice(at, length).ToArray()))
            {
                throw new InvalidDataException($"Remote field {id} appears twice.");
            }

            at += length;
        }

        return new(fields);
    }

    public byte[] ReadBytes(ushort id, int length) =>
        fields.TryGetValue(id, out byte[]? value) && value.Length == length
            ? value
            : throw new InvalidDataException($"Remote field {id} is missing, or is not {length} bytes.");

    public ushort ReadUInt16(ushort id) => BinaryPrimitives.ReadUInt16LittleEndian(ReadBytes(id, 2));

    public Guid ReadGuid(ushort id) => new(ReadBytes(id, 16), bigEndian: true);

    public bool ReadBoolean(ushort id) => ReadBytes(id, 1)[0] switch
    {
        0 => false,
        1 => true,
        _ => throw new InvalidDataException($"Remote field {id} is a flag, 0 or 1."),
    };

    public string ReadText(ushort id, int maximumBytes)
    {
        if (!fields.TryGetValue(id, out byte[]? value) || value.Length > maximumBytes)
        {
            throw new InvalidDataException($"Remote field {id} is missing, or is more than {maximumBytes:N0} bytes.");
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(value);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"Remote field {id} is not UTF-8.", exception);
        }
    }
}

/// <summary>Writes a payload's fields in the order given.</summary>
public sealed class RemoteFieldWriter
{
    private readonly List<byte> bytes = [];
    private readonly HashSet<ushort> written = [];

    public RemoteFieldWriter WriteBytes(ushort id, ReadOnlySpan<byte> value)
    {
        if (value.Length > RemoteFields.MaximumFieldBytes || !written.Add(id) || written.Count > RemoteFields.MaximumFields)
        {
            throw new InvalidOperationException($"Remote field {id} is written once, at most {RemoteFields.MaximumFieldBytes:N0} bytes.");
        }

        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(header, id);
        BinaryPrimitives.WriteUInt16LittleEndian(header[2..], (ushort)value.Length);
        bytes.AddRange(header);
        bytes.AddRange(value);
        return this;
    }

    public RemoteFieldWriter WriteUInt16(ushort id, ushort value)
    {
        Span<byte> encoded = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(encoded, value);
        return WriteBytes(id, encoded);
    }

    public RemoteFieldWriter WriteGuid(ushort id, Guid value)
    {
        Span<byte> encoded = stackalloc byte[16];
        _ = value.TryWriteBytes(encoded, bigEndian: true, out _);
        return WriteBytes(id, encoded);
    }

    public RemoteFieldWriter WriteBoolean(ushort id, bool value) => WriteBytes(id, [value ? (byte)1 : (byte)0]);

    public RemoteFieldWriter WriteText(ushort id, string value, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] encoded = Encoding.UTF8.GetBytes(value);
        if (encoded.Length > maximumBytes)
        {
            // A text the agent states is cut at a character boundary rather than refused: it is said, not obeyed.
            int cut = maximumBytes;
            while (cut > 0 && (encoded[cut] & 0xC0) == 0x80) cut--;
            encoded = encoded[..cut];
        }

        return WriteBytes(id, encoded);
    }

    public byte[] ToArray() => [.. bytes];
}
