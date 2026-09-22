using System.Buffers.Binary;
using System.Text;

namespace InterCat.CaptureBroker;

internal enum BrokerWireType : byte
{
    Int32 = 1,
    Int64 = 2,
    UInt64 = 3,
    Boolean = 4,
    Utf8 = 5,
    Guid = 6,
    Int32List = 7,
    Utf8List = 8,
}

[Flags]
internal enum BrokerWireFieldFlags : byte
{
    Required = 1,
}

internal sealed class BrokerWireFieldWriter : IDisposable
{
    public const int MaximumFieldBytes = 16 * 1024;

    private readonly MemoryStream stream = new();
    private readonly HashSet<ushort> fields = [];

    public void WriteInt32(ushort fieldId, int value, bool required = true)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        Write(fieldId, BrokerWireType.Int32, required, bytes);
    }

    public void WriteInt64(ushort fieldId, long value, bool required = true)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        Write(fieldId, BrokerWireType.Int64, required, bytes);
    }

    public void WriteUInt64(ushort fieldId, ulong value, bool required = true)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Write(fieldId, BrokerWireType.UInt64, required, bytes);
    }

    public void WriteBoolean(ushort fieldId, bool value, bool required = true)
    {
        Span<byte> bytes = stackalloc byte[1];
        bytes[0] = value ? (byte)1 : (byte)0;
        Write(fieldId, BrokerWireType.Boolean, required, bytes);
    }

    public void WriteString(ushort fieldId, string value, bool required = true)
    {
        ArgumentNullException.ThrowIfNull(value);
        Write(fieldId, BrokerWireType.Utf8, required, Encoding.UTF8.GetBytes(value));
    }

    public void WriteGuid(ushort fieldId, Guid value, bool required = true)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!value.TryWriteBytes(bytes, bigEndian: true, out int written) || written != bytes.Length)
        {
            throw new InvalidOperationException("The wire GUID could not be encoded.");
        }

        Write(fieldId, BrokerWireType.Guid, required, bytes);
    }

    public void WriteInt32List(ushort fieldId, IReadOnlyList<int> values, bool required = true)
    {
        ArgumentNullException.ThrowIfNull(values);
        byte[] bytes = new byte[checked(4 + values.Count * 4)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), values.Count);
        for (int index = 0; index < values.Count; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4 + index * 4, 4), values[index]);
        }

        Write(fieldId, BrokerWireType.Int32List, required, bytes);
    }

    public void WriteStringList(ushort fieldId, IReadOnlyList<string> values, bool required = true)
    {
        ArgumentNullException.ThrowIfNull(values);
        using var valueStream = new MemoryStream();
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(number, values.Count);
        valueStream.Write(number);
        foreach (string value in values)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteInt32LittleEndian(number, bytes.Length);
            valueStream.Write(number);
            valueStream.Write(bytes);
        }

        Write(fieldId, BrokerWireType.Utf8List, required, valueStream.GetBuffer().AsSpan(0, checked((int)valueStream.Length)));
    }

    public byte[] Build()
    {
        if (stream.Length > BrokerWireFrameCodec.MaximumPayloadBytes)
        {
            throw new InvalidDataException("The encoded broker payload exceeds the frame limit.");
        }

        return stream.ToArray();
    }

    public void Dispose() => stream.Dispose();

    private void Write(
        ushort fieldId,
        BrokerWireType type,
        bool required,
        ReadOnlySpan<byte> value)
    {
        if (fieldId == 0 || !fields.Add(fieldId))
        {
            throw new InvalidDataException("Wire field IDs must be nonzero and unique.");
        }

        if (value.Length > MaximumFieldBytes)
        {
            throw new InvalidDataException(
                $"Wire field {fieldId} exceeds the {MaximumFieldBytes}-byte field limit.");
        }

        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(header[..2], fieldId);
        header[2] = (byte)type;
        header[3] = required ? (byte)BrokerWireFieldFlags.Required : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], value.Length);
        stream.Write(header);
        stream.Write(value);
    }
}

internal sealed class BrokerWireFieldSet
{
    public const int MaximumFields = 64;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Dictionary<ushort, Field> fields;

    private BrokerWireFieldSet(Dictionary<ushort, Field> fields)
    {
        this.fields = fields;
    }

    public static BrokerWireFieldSet Parse(
        ReadOnlySpan<byte> payload,
        IReadOnlySet<ushort> knownFields)
    {
        var fields = new Dictionary<ushort, Field>();
        int offset = 0;
        int fieldCount = 0;
        while (offset < payload.Length)
        {
            if (payload.Length - offset < 8)
            {
                throw new InvalidDataException("A broker wire field header is truncated.");
            }

            ushort fieldId = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(offset, 2));
            var type = (BrokerWireType)payload[offset + 2];
            var flags = (BrokerWireFieldFlags)payload[offset + 3];
            int length = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset + 4, 4));
            offset += 8;
            fieldCount++;
            if (fieldId == 0
                || (flags & ~BrokerWireFieldFlags.Required) != 0
                || length is < 0 or > BrokerWireFieldWriter.MaximumFieldBytes
                || length > payload.Length - offset
                || fieldCount > MaximumFields)
            {
                throw new InvalidDataException("A broker wire field has an invalid ID, flags, or length.");
            }

            bool known = knownFields.Contains(fieldId);
            if (!known && (flags & BrokerWireFieldFlags.Required) != 0)
            {
                throw new InvalidDataException($"Required broker wire field {fieldId} is unknown.");
            }

            if (known)
            {
                if (!Enum.IsDefined(type) || !fields.TryAdd(
                    fieldId,
                    new(type, payload.Slice(offset, length).ToArray())))
                {
                    throw new InvalidDataException($"Broker wire field {fieldId} is duplicated or has an unknown type.");
                }

            }

            offset += length;
        }

        return new(fields);
    }

    public bool Contains(ushort fieldId) => fields.ContainsKey(fieldId);

    public int RequiredInt32(ushort fieldId) =>
        ReadFixed(fieldId, BrokerWireType.Int32, 4, value => BinaryPrimitives.ReadInt32LittleEndian(value));

    public int? OptionalInt32(ushort fieldId) => fields.ContainsKey(fieldId) ? RequiredInt32(fieldId) : null;

    public long RequiredInt64(ushort fieldId) =>
        ReadFixed(fieldId, BrokerWireType.Int64, 8, value => BinaryPrimitives.ReadInt64LittleEndian(value));

    public ulong RequiredUInt64(ushort fieldId) =>
        ReadFixed(fieldId, BrokerWireType.UInt64, 8, value => BinaryPrimitives.ReadUInt64LittleEndian(value));

    public bool RequiredBoolean(ushort fieldId)
    {
        Field field = Required(fieldId, BrokerWireType.Boolean);
        if (field.Value.Length != 1 || field.Value[0] > 1)
        {
            throw new InvalidDataException($"Boolean field {fieldId} is invalid.");
        }

        return field.Value[0] == 1;
    }

    public Guid RequiredGuid(ushort fieldId)
    {
        Field field = Required(fieldId, BrokerWireType.Guid);
        if (field.Value.Length != 16)
        {
            throw new InvalidDataException($"GUID field {fieldId} is invalid.");
        }

        return new(field.Value, bigEndian: true);
    }

    public string RequiredString(ushort fieldId, int maximumBytes, bool allowControls = false)
    {
        Field field = Required(fieldId, BrokerWireType.Utf8);
        return DecodeString(field.Value, fieldId, maximumBytes, allowControls);
    }

    public string? OptionalString(ushort fieldId, int maximumBytes, bool allowControls = false) =>
        fields.ContainsKey(fieldId) ? RequiredString(fieldId, maximumBytes, allowControls) : null;

    public IReadOnlyList<int> OptionalInt32List(ushort fieldId, int maximumCount)
    {
        if (!fields.TryGetValue(fieldId, out Field? field))
        {
            return [];
        }

        if (field.Type != BrokerWireType.Int32List || field.Value.Length < 4)
        {
            throw new InvalidDataException($"Integer-list field {fieldId} is invalid.");
        }

        int count = BinaryPrimitives.ReadInt32LittleEndian(field.Value.AsSpan(0, 4));
        if (count is < 0 || count > maximumCount || field.Value.Length != 4 + count * 4)
        {
            throw new InvalidDataException($"Integer-list field {fieldId} exceeds its count or byte bound.");
        }

        var values = new int[count];
        for (int index = 0; index < count; index++)
        {
            values[index] = BinaryPrimitives.ReadInt32LittleEndian(field.Value.AsSpan(4 + index * 4, 4));
        }

        return values;
    }

    public IReadOnlyList<string> OptionalStringList(
        ushort fieldId,
        int maximumCount,
        int maximumItemBytes)
    {
        if (!fields.TryGetValue(fieldId, out Field? field))
        {
            return [];
        }

        if (field.Type != BrokerWireType.Utf8List || field.Value.Length < 4)
        {
            throw new InvalidDataException($"String-list field {fieldId} is invalid.");
        }

        ReadOnlySpan<byte> bytes = field.Value;
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes[..4]);
        if (count is < 0 || count > maximumCount)
        {
            throw new InvalidDataException($"String-list field {fieldId} exceeds its item-count bound.");
        }

        int offset = 4;
        var values = new string[count];
        for (int index = 0; index < count; index++)
        {
            if (bytes.Length - offset < 4)
            {
                throw new InvalidDataException($"String-list field {fieldId} is truncated.");
            }

            int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, 4));
            offset += 4;
            if (length is < 0 || length > maximumItemBytes || length > bytes.Length - offset)
            {
                throw new InvalidDataException($"String-list field {fieldId} has an invalid item length.");
            }

            values[index] = DecodeString(bytes.Slice(offset, length).ToArray(), fieldId, maximumItemBytes, allowControls: false);
            offset += length;
        }

        if (offset != bytes.Length)
        {
            throw new InvalidDataException($"String-list field {fieldId} has trailing bytes.");
        }

        return values;
    }

    private T ReadFixed<T>(
        ushort fieldId,
        BrokerWireType expectedType,
        int expectedLength,
        Func<ReadOnlySpan<byte>, T> read)
    {
        Field field = Required(fieldId, expectedType);
        if (field.Value.Length != expectedLength)
        {
            throw new InvalidDataException($"Field {fieldId} has an invalid fixed width.");
        }

        return read(field.Value);
    }

    private Field Required(ushort fieldId, BrokerWireType expectedType)
    {
        if (!fields.TryGetValue(fieldId, out Field? field))
        {
            throw new InvalidDataException($"Required broker wire field {fieldId} is missing.");
        }

        if (field.Type != expectedType)
        {
            throw new InvalidDataException($"Broker wire field {fieldId} has the wrong type.");
        }

        return field;
    }

    private static string DecodeString(
        byte[] value,
        ushort fieldId,
        int maximumBytes,
        bool allowControls)
    {
        if (value.Length > maximumBytes)
        {
            throw new InvalidDataException($"String field {fieldId} exceeds its byte bound.");
        }

        string decoded;
        try
        {
            decoded = StrictUtf8.GetString(value);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"String field {fieldId} is not valid UTF-8.", exception);
        }

        if (!allowControls && decoded.Any(char.IsControl))
        {
            throw new InvalidDataException($"String field {fieldId} contains a control character.");
        }

        return decoded;
    }

    private sealed record Field(BrokerWireType Type, byte[] Value);
}
