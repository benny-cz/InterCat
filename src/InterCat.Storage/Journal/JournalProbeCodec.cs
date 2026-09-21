using System.Security.Cryptography;
using System.Text;
using InterCat.Domain;

namespace InterCat.Storage;

/// <summary>
/// Checksummed IC-009 probe framing. Magic and version intentionally identify a disposable lab format,
/// not the journal-v1 contract that IC-011 owns.
/// </summary>
public static class JournalProbeCodec
{
    public const int MaximumTextLength = 256;
    public const int MaximumExtendedItemCount = 16;
    public const int MaximumRecordCount = 1_000_000;
    public const int MaximumBatchBytes = 128 * 1024 * 1024;

    private const uint Magic = 0x30504a49; // "IJP0" in little-endian byte order.
    private const uint ClockMagic = 0x304b4349; // "ICK0" in little-endian byte order.
    private const ushort Version = 0;
    private const int HashLength = 32;

    /// <summary>True when a frame holds the capture's source clock rather than a batch of records.</summary>
    public static bool IsClockFrame(ReadOnlySpan<byte> encoded) =>
        encoded.Length >= sizeof(uint)
        && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(encoded) == ClockMagic;

    /// <summary>
    /// Encodes the capture's source clock descriptor. Replay converts native ticks from this frame alone,
    /// so an offline reader never needs the live session that produced the file (ADR-006, section 18.3).
    /// </summary>
    public static byte[] EncodeClock(SourceClockDescriptor clock)
    {
        using var payloadStream = new MemoryStream(96);
        using (var payload = new BinaryWriter(payloadStream, Encoding.UTF8, leaveOpen: true))
        {
            WriteGuid(payload, clock.Id.Value);
            WriteGuid(payload, clock.HostId.Value);
            payload.Write((int)clock.Kind);
            payload.Write((int)clock.Encoding);
            payload.Write(clock.TicksPerSecond);
            payload.Write(clock.CaptureEpochNativeTicks);
            payload.Write((int)clock.Rounding);
            payload.Write(clock.MaximumAbsoluteSessionNanoseconds);
        }

        byte[] payloadBytes = payloadStream.ToArray();
        using var outputStream = new MemoryStream(payloadBytes.Length + 48);
        using (var output = new BinaryWriter(outputStream, Encoding.UTF8, leaveOpen: true))
        {
            output.Write(ClockMagic);
            output.Write(Version);
            output.Write((ushort)0);
            output.Write(payloadBytes.Length);
            output.Write(SHA256.HashData(payloadBytes));
            output.Write(payloadBytes);
        }

        return outputStream.ToArray();
    }

    /// <summary>Decodes a clock frame, refusing a corrupted one instead of returning a partial descriptor.</summary>
    public static SourceClockDescriptor DecodeClock(ReadOnlyMemory<byte> encoded)
    {
        using var stream = new MemoryStream(encoded.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        if (reader.ReadUInt32() != ClockMagic || reader.ReadUInt16() != Version)
        {
            throw new InvalidDataException("Not a supported IC-009 source clock frame.");
        }

        if (reader.ReadUInt16() != 0)
        {
            throw new InvalidDataException("Journal probe clock reserved flags are non-zero.");
        }

        int payloadLength = ReadBoundedCount(reader, 1_024, "clock payload length");
        byte[] expectedHash = ReadExactly(reader, HashLength);
        byte[] payload = ReadExactly(reader, payloadLength);
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("Journal probe clock frame has an unexplained trailing tail.");
        }

        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), expectedHash))
        {
            throw new InvalidDataException("Journal probe clock frame checksum does not match its payload.");
        }

        using var payloadStream = new MemoryStream(payload, writable: false);
        using var payloadReader = new BinaryReader(payloadStream, Encoding.UTF8, leaveOpen: false);
        var descriptor = new SourceClockDescriptor(
            new ClockId(ReadGuid(payloadReader)),
            new HostId(ReadGuid(payloadReader)),
            (SourceClockKind)payloadReader.ReadInt32(),
            (TimestampEncoding)payloadReader.ReadInt32(),
            payloadReader.ReadInt64(),
            payloadReader.ReadInt64(),
            (TimestampRounding)payloadReader.ReadInt32(),
            payloadReader.ReadInt64());
        return payloadStream.Position == payloadStream.Length
            ? descriptor
            : throw new InvalidDataException("Journal probe clock payload has bytes outside its declared fields.");
    }

    public static byte[] EncodeBatch(IReadOnlyList<JournalProbeEnvelope> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count > MaximumRecordCount)
        {
            throw new ArgumentOutOfRangeException(nameof(records));
        }

        using var payloadStream = new MemoryStream();
        using (var payload = new BinaryWriter(payloadStream, Encoding.UTF8, leaveOpen: true))
        {
            foreach (JournalProbeEnvelope record in records)
            {
                WriteRecord(payload, record);
            }
        }

        if (payloadStream.Length > MaximumBatchBytes)
        {
            throw new InvalidDataException("Journal probe batch exceeds its bounded payload size.");
        }

        byte[] payloadBytes = payloadStream.ToArray();
        byte[] hash = SHA256.HashData(payloadBytes);
        using var outputStream = new MemoryStream(payloadBytes.Length + 48);
        using (var output = new BinaryWriter(outputStream, Encoding.UTF8, leaveOpen: true))
        {
            output.Write(Magic);
            output.Write(Version);
            output.Write((ushort)0);
            output.Write(records.Count);
            output.Write(payloadBytes.Length);
            output.Write(hash);
            output.Write(payloadBytes);
        }

        return outputStream.ToArray();
    }

    public static IReadOnlyList<JournalProbeEnvelope> DecodeBatch(ReadOnlyMemory<byte> encoded)
    {
        if (encoded.Length > MaximumBatchBytes + 48)
        {
            throw new InvalidDataException("Journal probe batch exceeds its bounded encoded size.");
        }

        using var stream = new MemoryStream(encoded.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        if (reader.ReadUInt32() != Magic || reader.ReadUInt16() != Version)
        {
            throw new InvalidDataException("Not a supported IC-009 journal probe batch.");
        }

        if (reader.ReadUInt16() != 0)
        {
            throw new InvalidDataException("Journal probe reserved flags are non-zero.");
        }

        int count = ReadBoundedCount(reader, MaximumRecordCount, "record count");
        int payloadLength = ReadBoundedCount(reader, MaximumBatchBytes, "payload length");
        byte[] expectedHash = ReadExactly(reader, HashLength);
        byte[] payload = ReadExactly(reader, payloadLength);
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("Journal probe batch has an unexplained trailing tail.");
        }

        byte[] actualHash = SHA256.HashData(payload);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
        {
            throw new InvalidDataException("Journal probe batch checksum does not match its payload.");
        }

        using var payloadStream = new MemoryStream(payload, writable: false);
        using var payloadReader = new BinaryReader(payloadStream, Encoding.UTF8, leaveOpen: false);
        var records = new List<JournalProbeEnvelope>(count);
        for (int index = 0; index < count; index++)
        {
            records.Add(ReadRecord(payloadReader));
        }

        if (payloadStream.Position != payloadStream.Length)
        {
            throw new InvalidDataException("Journal probe payload has bytes outside its declared records.");
        }

        return records;
    }

    private static void WriteRecord(BinaryWriter writer, JournalProbeEnvelope record)
    {
        WriteGuid(writer, record.Id.CaptureId.Value);
        writer.Write(record.Id.StreamId);
        writer.Write(record.Id.SourceEpoch);
        writer.Write(record.Id.RecordOrdinal);
        WriteGuid(writer, record.ProviderGuid);
        writer.Write(record.EventId);
        writer.Write(record.Version);
        writer.Write(record.Opcode);
        WriteGuid(writer, record.NativeTimestamp.ClockId.Value);
        writer.Write((int)record.NativeTimestamp.Encoding);
        writer.Write(record.NativeTimestamp.Ticks);
        writer.Write(record.HeaderProcessId);
        writer.Write(record.HeaderThreadId);
        writer.Write(record.ProcessorNumber);
        writer.Write(record.PointerSize);
        WriteGuid(writer, record.ActivityId);
        WriteGuid(writer, record.RelatedActivityId);
        WriteText(writer, record.SchemaFingerprint);
        WriteText(writer, record.AdmissionPolicyId);
        writer.Write((int)record.Body.Classification);
        writer.Write((int)record.Body.Disposition);
        writer.Write(record.Body.OriginalLength);
        WriteBytes(writer, record.Body.RetainedBytes.Span);
        writer.Write(record.OmittedExtendedItemCount);
        writer.Write(record.ExtendedItems.Count);
        foreach (JournalProbeExtendedItem item in record.ExtendedItems)
        {
            writer.Write(item.Type);
            writer.Write(item.Flags);
            WriteBytes(writer, item.Bytes.Span);
        }
    }

    private static JournalProbeEnvelope ReadRecord(BinaryReader reader)
    {
        var id = new RawRecordId(
            new CaptureId(ReadGuid(reader)),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt64());
        Guid provider = ReadGuid(reader);
        int eventId = reader.ReadInt32();
        int version = reader.ReadInt32();
        int opcode = reader.ReadInt32();
        var timestamp = new NativeTimestamp(
            new ClockId(ReadGuid(reader)),
            (TimestampEncoding)reader.ReadInt32(),
            reader.ReadInt64());
        int processId = reader.ReadInt32();
        int threadId = reader.ReadInt32();
        int processor = reader.ReadInt32();
        int pointerSize = reader.ReadInt32();
        Guid activityId = ReadGuid(reader);
        Guid relatedActivityId = ReadGuid(reader);
        string fingerprint = ReadText(reader);
        string policyId = ReadText(reader);
        var classification = (JournalProbeBodyClassification)reader.ReadInt32();
        var disposition = (JournalProbeBodyDisposition)reader.ReadInt32();
        int originalLength = ReadBoundedCount(reader, MaximumBatchBytes, "original body length");
        byte[] body = ReadLengthPrefixedBytes(reader, MaximumBatchBytes);
        int omittedExtended = ReadBoundedCount(reader, MaximumExtendedItemCount, "omitted extended item count");
        int extendedCount = ReadBoundedCount(reader, MaximumExtendedItemCount, "extended item count");
        var extended = new List<JournalProbeExtendedItem>(extendedCount);
        for (int index = 0; index < extendedCount; index++)
        {
            ushort type = reader.ReadUInt16();
            ushort flags = reader.ReadUInt16();
            extended.Add(new(type, flags, ReadLengthPrefixedBytes(reader, MaximumBatchBytes)));
        }

        return new()
        {
            Id = id,
            ProviderGuid = provider,
            EventId = eventId,
            Version = version,
            Opcode = opcode,
            NativeTimestamp = timestamp,
            HeaderProcessId = processId,
            HeaderThreadId = threadId,
            ProcessorNumber = processor,
            PointerSize = pointerSize,
            ActivityId = activityId,
            RelatedActivityId = relatedActivityId,
            SchemaFingerprint = fingerprint,
            AdmissionPolicyId = policyId,
            Body = new()
            {
                Classification = classification,
                Disposition = disposition,
                OriginalLength = originalLength,
                RetainedBytes = body,
            },
            ExtendedItems = extended,
            OmittedExtendedItemCount = omittedExtended,
        };
    }

    private static void WriteGuid(BinaryWriter writer, Guid value) => writer.Write(value.ToByteArray(bigEndian: true));

    private static Guid ReadGuid(BinaryReader reader) => new(ReadExactly(reader, 16), bigEndian: true);

    private static void WriteText(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > MaximumTextLength)
        {
            throw new InvalidDataException("Journal probe text exceeds its bound.");
        }

        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadText(BinaryReader reader) =>
        Encoding.UTF8.GetString(ReadLengthPrefixedBytes(reader, MaximumTextLength));

    private static void WriteBytes(BinaryWriter writer, ReadOnlySpan<byte> bytes)
    {
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static byte[] ReadLengthPrefixedBytes(BinaryReader reader, int maximum) =>
        ReadExactly(reader, ReadBoundedCount(reader, maximum, "byte length"));

    private static int ReadBoundedCount(BinaryReader reader, int maximum, string label)
    {
        int value = reader.ReadInt32();
        if (value < 0 || value > maximum)
        {
            throw new InvalidDataException($"Journal probe {label} is outside its bound.");
        }

        return value;
    }

    private static byte[] ReadExactly(BinaryReader reader, int length)
    {
        byte[] bytes = reader.ReadBytes(length);
        return bytes.Length == length
            ? bytes
            : throw new EndOfStreamException("Journal probe batch ended inside a declared field.");
    }
}
