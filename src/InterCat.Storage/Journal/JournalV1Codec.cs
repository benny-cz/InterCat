using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using InterCat.Domain;

namespace InterCat.Storage;

/// <summary>
/// The journal-v1 wire format, frozen by IC-011. Every offset, order and width here is contract: see
/// `contracts/journal-v1.md`. A change to any of it is a new major version and an ADR, and the golden
/// corpus under `fixtures/FX-JOURNAL-002/` fails first if one is made by accident (§13.6).
/// </summary>
public static class JournalV1Codec
{
    /// <summary>"ICJ1" little-endian. A file that does not start with it is not a journal.</summary>
    public const uint FileMagic = 0x314A4349;

    public const ushort FormatMajor = 1;
    public const ushort FormatMinor = 0;

    /// <summary>Header bytes before the checksum: magic, versions, flags, capture id, creation time.</summary>
    public const int HeaderBodyLength = 4 + 2 + 2 + 8 + 16 + 8;

    public const int HeaderLength = HeaderBodyLength + 32;

    /// <summary>Longest text journal-v1 stores in a schema or policy entry.</summary>
    public const int MaximumTextBytes = 512;

    /// <summary>Largest frame payload a reader will accept, so a corrupt length cannot allocate a machine.</summary>
    public const int MaximumFrameBytes = 128 * 1024 * 1024;

    public const int MaximumRecordsPerBatch = 1_000_000;

    public const int MaximumExtendedItemsPerRecord = 64;

    /// <summary>
    /// Writes the file header. The capture identity lives here rather than in every record, because it
    /// is the same for all of them and a journal is one capture (§18.1).
    /// </summary>
    public static byte[] EncodeHeader(CaptureId captureId, DateTimeOffset createdUtc)
    {
        byte[] header = new byte[HeaderLength];
        Span<byte> span = header;
        BinaryPrimitives.WriteUInt32LittleEndian(span, FileMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], FormatMajor);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], FormatMinor);
        BinaryPrimitives.WriteUInt64LittleEndian(span[8..], 0);
        WriteGuid(span[16..], captureId.Value);
        BinaryPrimitives.WriteInt64LittleEndian(span[32..], createdUtc.UtcTicks);
        SHA256.HashData(span[..HeaderBodyLength], span[HeaderBodyLength..]);
        return header;
    }

    /// <summary>Reads and verifies the file header, refusing anything this reader cannot claim to read.</summary>
    public static (CaptureId CaptureId, DateTimeOffset CreatedUtc) DecodeHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderLength)
        {
            throw new InvalidDataException("A journal-v1 file ends inside its header.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != FileMagic)
        {
            throw new InvalidDataException("Not a journal-v1 file.");
        }

        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        if (major != FormatMajor)
        {
            throw new InvalidDataException(
                $"This journal is format major {major}; this reader implements {FormatMajor}. A major "
                + "version is refused rather than read at a guessed layout.");
        }

        ulong features = BinaryPrimitives.ReadUInt64LittleEndian(header[8..]);
        if (features != 0)
        {
            throw new InvalidDataException(
                $"This journal declares feature flags 0x{features:x16} that this reader does not implement.");
        }

        Span<byte> expected = stackalloc byte[32];
        SHA256.HashData(header[..HeaderBodyLength], expected);
        if (!CryptographicOperations.FixedTimeEquals(expected, header.Slice(HeaderBodyLength, 32)))
        {
            throw new InvalidDataException("The journal-v1 header checksum does not match its contents.");
        }

        return (
            new CaptureId(ReadGuid(header[16..])),
            new DateTimeOffset(BinaryPrimitives.ReadInt64LittleEndian(header[32..]), TimeSpan.Zero));
    }

    /// <summary>
    /// Frames a payload: kind, length, payload, then the payload's SHA-256. The length precedes the bytes
    /// so a reader can skip a frame it will not use, and the checksum follows them so a truncated write
    /// cannot produce a frame that verifies (§20.1).
    /// </summary>
    public static byte[] EncodeFrame(JournalFrameKind kind, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaximumFrameBytes)
        {
            throw new InvalidDataException("A journal-v1 frame exceeds its bounded payload size.");
        }

        byte[] frame = new byte[4 + 4 + payload.Length + 32];
        Span<byte> span = frame;
        BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)kind);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], payload.Length);
        payload.CopyTo(span[8..]);
        SHA256.HashData(payload, span[(8 + payload.Length)..]);
        return frame;
    }

    /// <summary>The terminal frame. A journal without one is an incomplete capture, not a short one.</summary>
    public static byte[] EncodeTerminal() => EncodeFrame(JournalFrameKind.Terminal, []);

    public static byte[] EncodeClock(SourceClockDescriptor clock)
    {
        var payload = new ArrayBufferWriter();
        payload.WriteGuid(clock.Id.Value);
        payload.WriteGuid(clock.HostId.Value);
        payload.WriteByte((byte)clock.Kind);
        payload.WriteByte((byte)clock.Encoding);
        payload.WriteInt64(clock.TicksPerSecond);
        payload.WriteInt64(clock.CaptureEpochNativeTicks);
        payload.WriteByte((byte)clock.Rounding);
        payload.WriteInt64(clock.MaximumAbsoluteSessionNanoseconds);
        return EncodeFrame(JournalFrameKind.SourceClock, payload.WrittenSpan);
    }

    public static SourceClockDescriptor DecodeClock(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        var clock = new SourceClockDescriptor(
            new ClockId(reader.ReadGuid()),
            new HostId(reader.ReadGuid()),
            (SourceClockKind)reader.ReadByte(),
            (TimestampEncoding)reader.ReadByte(),
            reader.ReadInt64(),
            reader.ReadInt64(),
            (TimestampRounding)reader.ReadByte(),
            reader.ReadInt64());
        reader.EnsureConsumed("source clock frame");
        return clock;
    }

    public static byte[] EncodeSchemaTable(JournalV1SchemaTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        var payload = new ArrayBufferWriter();
        payload.WriteInt32(table.Schemas.Count);
        foreach (JournalSchemaV1 schema in table.Schemas.OrderBy(entry => entry.Reference))
        {
            payload.WriteUInt32(schema.Reference);
            payload.WriteGuid(schema.ProviderId);
            payload.WriteUInt16(schema.EventId);
            payload.WriteByte(schema.Version);
            payload.WriteText(schema.Fingerprint);
        }

        payload.WriteInt32(table.Policies.Count);
        foreach (JournalPolicyV1 policy in table.Policies.OrderBy(entry => entry.Reference))
        {
            payload.WriteUInt32(policy.Reference);
            payload.WriteText(policy.PolicyId);
        }

        return EncodeFrame(JournalFrameKind.SchemaTable, payload.WrittenSpan);
    }

    public static JournalV1SchemaTable DecodeSchemaTable(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        int schemaCount = reader.ReadBoundedCount(ushort.MaxValue, "schema count");
        var schemas = new List<JournalSchemaV1>(schemaCount);
        for (int index = 0; index < schemaCount; index++)
        {
            schemas.Add(new(
                reader.ReadUInt32(),
                reader.ReadGuid(),
                reader.ReadUInt16(),
                reader.ReadByte(),
                reader.ReadText()));
        }

        int policyCount = reader.ReadBoundedCount(ushort.MaxValue, "policy count");
        var policies = new List<JournalPolicyV1>(policyCount);
        for (int index = 0; index < policyCount; index++)
        {
            policies.Add(new(reader.ReadUInt32(), reader.ReadText()));
        }

        reader.EnsureConsumed("schema table frame");
        return JournalV1SchemaTable.FromDecoded(schemas, policies);
    }

    public static byte[] EncodeBatch(IReadOnlyList<RecordEnvelopeV1> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count is 0 or > MaximumRecordsPerBatch)
        {
            throw new ArgumentOutOfRangeException(
                nameof(records),
                records.Count,
                "A journal-v1 batch holds between one and a million records.");
        }

        var payload = new ArrayBufferWriter();
        payload.WriteInt32(records.Count);
        WriteRawRecordId(payload, records[0].Id);
        WriteRawRecordId(payload, records[^1].Id);
        foreach (RecordEnvelopeV1 record in records)
        {
            WriteRecord(payload, record);
        }

        return EncodeFrame(JournalFrameKind.RecordBatch, payload.WrittenSpan);
    }

    /// <summary>The exact bytes one record adds to a batch payload, without encoding or retaining a copy.</summary>
    public static int EncodedRecordLength(RecordEnvelopeV1 record)
    {
        ArgumentNullException.ThrowIfNull(record);
        // Keep this layout in lockstep with WriteRecord below. The fixed part includes both byte-run
        // length prefixes and the per-record CRC. A quota must account for pending, unflushed records.
        long length = 147 + record.Body.Bytes.Length;
        foreach (ExtendedItemV1 item in record.ExtendedItems)
        {
            length += 10L + item.Bytes.Length;
        }

        return checked((int)length);
    }

    public static JournalBatchV1 DecodeBatch(ReadOnlySpan<byte> payload, CaptureId captureId)
    {
        var reader = new SpanReader(payload);
        int count = reader.ReadBoundedCount(MaximumRecordsPerBatch, "record count");
        RawRecordId first = ReadRawRecordId(ref reader, captureId);
        RawRecordId last = ReadRawRecordId(ref reader, captureId);
        var records = new List<RecordEnvelopeV1>(count);
        try
        {
            for (int index = 0; index < count; index++)
            {
                records.Add(ReadRecord(ref reader, captureId));
            }

            reader.EnsureConsumed("record batch frame");
            if (records[0].Id != first || records[^1].Id != last)
            {
                throw new InvalidDataException(
                    "A journal-v1 batch's declared first and last identities do not match its records.");
            }

            return new(first, last, records);
        }
        catch
        {
            foreach (RecordEnvelopeV1 record in records)
            {
                record.Dispose();
            }

            throw;
        }
    }

    private static void WriteRecord(ArrayBufferWriter payload, RecordEnvelopeV1 record)
    {
        int start = payload.WrittenCount;
        payload.WriteUInt32(record.StreamId);
        payload.WriteUInt32(record.SourceEpoch);
        payload.WriteUInt64(record.RecordOrdinal);

        EventHeaderFieldsV1 header = record.Header;
        payload.WriteGuid(header.ProviderId);
        payload.WriteUInt16(header.EventId);
        payload.WriteByte(header.Version);
        payload.WriteByte(header.Channel);
        payload.WriteByte(header.Level);
        payload.WriteByte(header.Opcode);
        payload.WriteUInt16(header.Task);
        payload.WriteUInt64(header.Keyword);
        payload.WriteUInt16(header.Flags);
        payload.WriteUInt16(header.EventProperty);
        payload.WriteInt32(header.ProcessId);
        payload.WriteInt32(header.ThreadId);
        payload.WriteGuid(header.ActivityId);
        payload.WriteGuid(header.RelatedActivityId);

        payload.WriteUInt16(record.BufferContext.ProcessorNumber);
        payload.WriteUInt16(record.BufferContext.LoggerId);

        payload.WriteGuid(record.ClockId.Value);
        payload.WriteByte((byte)record.TimestampEncoding);
        payload.WriteInt64(record.NativeTicks);
        payload.WriteByte(record.PointerSize);

        payload.WriteUInt32(record.SchemaReference ?? 0);
        payload.WriteUInt32(record.AdmissionPolicyReference);

        if (record.ExtendedItems.Count > MaximumExtendedItemsPerRecord)
        {
            throw new InvalidDataException("A journal-v1 record exceeds its bounded extended-item count.");
        }

        payload.WriteByte((byte)record.ExtendedItems.Count);
        payload.WriteUInt16(checked((ushort)record.OmittedExtendedItemCount));
        foreach (ExtendedItemV1 item in record.ExtendedItems)
        {
            payload.WriteUInt16(item.Type);
            payload.WriteUInt16(item.Flags);
            payload.WriteUInt16(checked((ushort)item.OriginalLength));
            payload.WriteBytes(item.Bytes.ReadOnlySpan);
        }

        payload.WriteByte((byte)record.Body.Classification);
        payload.WriteByte((byte)record.Body.Disposition);
        payload.WriteInt32(record.Body.OriginalLength);
        payload.WriteBytes(record.Body.Bytes.ReadOnlySpan);

        // The per-record check covers everything above, so a reader can quarantine one bad record and
        // keep the rest of the batch rather than losing all of them to one corrupted field (§18.1).
        payload.WriteUInt32(Crc32C.Compute(payload.WrittenSpan[start..]));
    }

    private static RecordEnvelopeV1 ReadRecord(ref SpanReader reader, CaptureId captureId)
    {
        int start = reader.Position;
        uint streamId = reader.ReadUInt32();
        uint sourceEpoch = reader.ReadUInt32();
        ulong ordinal = reader.ReadUInt64();
        var header = new EventHeaderFieldsV1(
            reader.ReadGuid(),
            reader.ReadUInt16(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadUInt16(),
            reader.ReadUInt64(),
            reader.ReadUInt16(),
            reader.ReadUInt16(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadGuid(),
            reader.ReadGuid());
        var buffer = new BufferContextFieldsV1(reader.ReadUInt16(), reader.ReadUInt16());
        var clockId = new ClockId(reader.ReadGuid());
        var encoding = (TimestampEncoding)reader.ReadByte();
        long nativeTicks = reader.ReadInt64();
        byte pointerSize = reader.ReadByte();
        uint schemaReference = reader.ReadUInt32();
        uint policyReference = reader.ReadUInt32();

        int itemCount = reader.ReadByte();
        if (itemCount > MaximumExtendedItemsPerRecord)
        {
            throw new InvalidDataException("A journal-v1 record declares more extended items than the bound.");
        }

        int omitted = reader.ReadUInt16();
        var items = new List<ExtendedItemV1>(itemCount);
        BodyV1? body = null;
        try
        {
            for (int index = 0; index < itemCount; index++)
            {
                ushort type = reader.ReadUInt16();
                ushort flags = reader.ReadUInt16();
                int originalLength = reader.ReadUInt16();
                items.Add(new()
                {
                    Type = type,
                    Flags = flags,
                    OriginalLength = originalLength,
                    Bytes = reader.ReadBuffer(),
                });
            }

            var classification = (BodyClassificationV1)reader.ReadByte();
            var disposition = (BodyDispositionV1)reader.ReadByte();
            int bodyOriginalLength = reader.ReadInt32();
            body = new()
            {
                Classification = classification,
                Disposition = disposition,
                OriginalLength = bodyOriginalLength,
                Bytes = reader.ReadBuffer(),
            };

            uint stored = reader.ReadUInt32();
            uint computed = Crc32C.Compute(reader.Slice(start, reader.Position - start - 4));
            if (stored != computed)
            {
                throw new InvalidDataException(
                    $"Journal-v1 record {ordinal} fails its integrity check: stored 0x{stored:x8}, "
                    + $"computed 0x{computed:x8}.");
            }

            return new()
            {
                CaptureId = captureId,
                StreamId = streamId,
                SourceEpoch = sourceEpoch,
                RecordOrdinal = ordinal,
                Header = header,
                BufferContext = buffer,
                ClockId = clockId,
                TimestampEncoding = encoding,
                NativeTicks = nativeTicks,
                PointerSize = pointerSize,
                SchemaReference = schemaReference == 0 ? null : schemaReference,
                AdmissionPolicyReference = policyReference,
                ExtendedItems = items,
                OmittedExtendedItemCount = omitted,
                Body = body,
            };
        }
        catch
        {
            body?.Dispose();
            foreach (ExtendedItemV1 item in items)
            {
                item.Dispose();
            }

            throw;
        }
    }

    private static void WriteRawRecordId(ArrayBufferWriter payload, RawRecordId id)
    {
        payload.WriteUInt32(id.StreamId);
        payload.WriteUInt32(id.SourceEpoch);
        payload.WriteUInt64(id.RecordOrdinal);
    }

    private static RawRecordId ReadRawRecordId(ref SpanReader reader, CaptureId captureId) =>
        new(captureId, reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt64());

    internal static void WriteGuid(Span<byte> destination, Guid value) =>
        value.TryWriteBytes(destination, bigEndian: true, out _);

    internal static Guid ReadGuid(ReadOnlySpan<byte> source) => new(source[..16], bigEndian: true);

    /// <summary>A growable little-endian writer. Journal-v1 is little-endian except for its GUIDs, which
    /// are big-endian so a hexadecimal dump reads in the order the identifier is written.</summary>
    internal sealed class ArrayBufferWriter
    {
        private byte[] buffer = new byte[1024];

        public int WrittenCount { get; private set; }

        public ReadOnlySpan<byte> WrittenSpan => buffer.AsSpan(0, WrittenCount);

        public void WriteByte(byte value) => Reserve(1)[0] = value;

        public void WriteUInt16(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Reserve(2), value);

        public void WriteInt32(int value) => BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), value);

        public void WriteUInt32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), value);

        public void WriteInt64(long value) => BinaryPrimitives.WriteInt64LittleEndian(Reserve(8), value);

        public void WriteUInt64(ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Reserve(8), value);

        public void WriteGuid(Guid value) => JournalV1Codec.WriteGuid(Reserve(16), value);

        public void WriteBytes(ReadOnlySpan<byte> value)
        {
            WriteInt32(value.Length);
            value.CopyTo(Reserve(value.Length));
        }

        public void WriteText(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > MaximumTextBytes)
            {
                throw new InvalidDataException($"Journal-v1 text exceeds its {MaximumTextBytes}-byte bound.");
            }

            WriteUInt16((ushort)bytes.Length);
            bytes.CopyTo(Reserve(bytes.Length));
        }

        private Span<byte> Reserve(int length)
        {
            if (WrittenCount + length > buffer.Length)
            {
                Array.Resize(ref buffer, Math.Max(buffer.Length * 2, WrittenCount + length));
            }

            Span<byte> span = buffer.AsSpan(WrittenCount, length);
            WrittenCount += length;
            return span;
        }
    }

    /// <summary>A bounded little-endian reader. Every read is checked, so a truncated frame raises at the
    /// field that ran out rather than returning a plausible value from the next one.</summary>
    internal ref struct SpanReader(ReadOnlySpan<byte> source)
    {
        private readonly ReadOnlySpan<byte> source = source;

        public int Position { get; private set; }

        public readonly ReadOnlySpan<byte> Slice(int start, int length) => source.Slice(start, length);

        public byte ReadByte() => Take(1)[0];

        public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

        public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

        public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

        public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

        public Guid ReadGuid() => JournalV1Codec.ReadGuid(Take(16));

        public string ReadText()
        {
            int length = ReadUInt16();
            return length > MaximumTextBytes
                ? throw new InvalidDataException("Journal-v1 text exceeds its bound.")
                : Encoding.UTF8.GetString(Take(length));
        }

        /// <summary>Reads a length-prefixed byte run into a pooled buffer the caller then owns.</summary>
        public EnvelopeBuffer ReadBuffer()
        {
            int length = ReadBoundedCount(MaximumFrameBytes, "byte run length");
            return EnvelopeBuffer.CopyOf(Take(length));
        }

        public int ReadBoundedCount(int maximum, string label)
        {
            int value = ReadInt32();
            return value >= 0 && value <= maximum
                ? value
                : throw new InvalidDataException($"Journal-v1 {label} {value} is outside its bound.");
        }

        public readonly void EnsureConsumed(string label)
        {
            if (Position != source.Length)
            {
                throw new InvalidDataException(
                    $"A journal-v1 {label} has {source.Length - Position} bytes outside its declared fields.");
            }
        }

        private ReadOnlySpan<byte> Take(int length)
        {
            if (Position + length > source.Length)
            {
                throw new InvalidDataException("A journal-v1 frame ends inside a declared field.");
            }

            ReadOnlySpan<byte> span = source.Slice(Position, length);
            Position += length;
            return span;
        }
    }
}
