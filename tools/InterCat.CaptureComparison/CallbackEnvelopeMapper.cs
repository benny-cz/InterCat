using System.Security.Cryptography;
using System.Text;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.CaptureComparison;

/// <summary>
/// Maps one admitted callback record into a journal-v1 envelope: the bounded field projection as an
/// approved-metadata body, the record's copied extended-data items, and the capture's own source clock.
/// Nothing here invents evidence — an item the callback could not copy stays absent and counted, an item
/// the metadata policy denies loses its bytes and is counted, and the clock is the one the session
/// recorded rather than a fresh identifier per run.
/// </summary>
internal sealed class CallbackEnvelopeMapper
{
    internal const string PolicyId = "metadata-only-admitted-projection-v1";
    private const uint ProjectionMagic = 0x31504149; // "IAP1" little-endian.

    private readonly JournalV1SchemaTable schemas = new();
    private readonly uint policyReference;
    private readonly ClockId clockId;

    public CallbackEnvelopeMapper(IReadOnlyList<SourceAdmissionPlan> sources, ClockId clockId)
    {
        ArgumentNullException.ThrowIfNull(sources);
        foreach (SourceAdmissionPlan source in sources)
        {
            foreach (AdmittedEventPlan plan in source.Events)
            {
                _ = schemas.Intern(
                    plan.ProviderGuid,
                    checked((ushort)plan.EventId),
                    checked((byte)plan.Version),
                    Fingerprint(plan));
            }
        }

        policyReference = schemas.InternPolicy(PolicyId);
        this.clockId = clockId;
    }

    /// <summary>The tables the journal writes before its first batch, so replay resolves every reference.</summary>
    public JournalV1SchemaTable Schemas => schemas;

    public RecordEnvelopeV1 ToEnvelope(in AdmittedEvent admitted, AdmittedEventPlan plan, CaptureId captureId)
    {
        ArgumentNullException.ThrowIfNull(plan);

        uint schemaReference = schemas.Intern(
            plan.ProviderGuid,
            checked((ushort)plan.EventId),
            checked((byte)plan.Version),
            Fingerprint(plan));
        return new()
        {
            CaptureId = captureId,
            StreamId = checked((uint)admitted.SourceIndex + 1),
            SourceEpoch = 1,
            RecordOrdinal = checked((ulong)admitted.RecordOrdinal),
            Header = new(
                plan.ProviderGuid,
                checked((ushort)admitted.EventId),
                checked((byte)admitted.Version),
                0,
                0,
                checked((byte)admitted.Opcode),
                0,
                0,
                0,
                0,
                admitted.HeaderProcessId,
                admitted.HeaderThreadId,
                admitted.ActivityId,
                admitted.RelatedActivityId),
            BufferContext = new(checked((ushort)admitted.ProcessorNumber), 0),
            ClockId = clockId,
            TimestampEncoding = TimestampEncoding.Qpc,
            NativeTicks = admitted.TimestampQpc,
            PointerSize = checked((byte)plan.PointerSize),
            SchemaReference = schemaReference,
            AdmissionPolicyReference = policyReference,
            ExtendedItems = ReadExtendedItems(admitted),
            OmittedExtendedItemCount = admitted.ExtendedItemsOmitted + DeniedItemCount(admitted),
            Body = new()
            {
                Classification = BodyClassificationV1.ApprovedMetadata,
                Disposition = BodyDispositionV1.Retained,
                OriginalLength = 0,
                Bytes = EncodeProjection(admitted),
            },
        };
    }

    /// <summary>
    /// Copies the record's permitted extended items out of its inline storage, bytes only, never a
    /// pointer. An item whose type the metadata policy denies loses its bytes here and is counted as an
    /// omission, so replay reproduces the refusal rather than the item (R17, I13).
    /// </summary>
    private static List<ExtendedItemV1> ReadExtendedItems(in AdmittedEvent admitted)
    {
        int count = admitted.ExtendedItemsCopied;
        if (count == 0)
        {
            return [];
        }

        var items = new List<ExtendedItemV1>(count);
        Span<byte> buffer = stackalloc byte[AdmittedEvent.MaximumExtendedItemBytes];
        for (int index = 0; index < count; index++)
        {
            AdmittedExtendedItem item = admitted.GetExtendedItem(index);
            if (!EtwExtendedDataTypes.PermittedMetadata.Contains(item.Type))
            {
                continue;
            }

            int length = admitted.CopyExtendedItemBytes(index, buffer);
            items.Add(new()
            {
                Type = item.Type,
                Flags = item.Linkage,
                OriginalLength = item.OriginalLength,
                Bytes = EnvelopeBuffer.CopyOf(buffer[..length]),
            });
        }

        return items;
    }

    private static int DeniedItemCount(in AdmittedEvent admitted)
    {
        int denied = 0;
        for (int index = 0; index < admitted.ExtendedItemsCopied; index++)
        {
            if (!EtwExtendedDataTypes.PermittedMetadata.Contains(admitted.GetExtendedItem(index).Type))
            {
                denied++;
            }
        }

        return denied;
    }

    /// <summary>
    /// Rebuilds the admitted record a journal-v1 envelope carries. It is the inverse of the projection
    /// body, and it refuses anything it cannot read rather than returning a partially filled record.
    /// </summary>
    public static AdmittedEvent FromEnvelope(RecordEnvelopeV1 envelope, AdmittedEventPlan plan)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(plan);
        if (envelope.Body.Disposition != BodyDispositionV1.Retained)
        {
            throw new InvalidDataException(
                $"The projection body of record {envelope.RecordOrdinal} was not retained: "
                + $"{envelope.Body.Disposition}.");
        }

        using var stream = new MemoryStream(envelope.Body.Bytes.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        if (reader.ReadUInt32() != ProjectionMagic)
        {
            throw new InvalidDataException("A journal-v1 projection body has the wrong marker.");
        }

        var admitted = new AdmittedEvent
        {
            SourceIndex = checked((int)envelope.StreamId - 1),
            EventId = envelope.Header.EventId,
            Version = envelope.Header.Version,
            Opcode = envelope.Header.Opcode,
            TimestampQpc = envelope.NativeTicks,
            TimestampUtcTicks = reader.ReadInt64(),
            HeaderProcessId = envelope.Header.ProcessId,
            HeaderThreadId = envelope.Header.ThreadId,
            ProcessorNumber = envelope.BufferContext.ProcessorNumber,
            RecordOrdinal = checked((long)envelope.RecordOrdinal),
            ActivityId = envelope.Header.ActivityId,
            RelatedActivityId = envelope.Header.RelatedActivityId,
        };

        byte knownMask = reader.ReadByte();
        for (int index = 0; index < AdmissionPlanCompiler.MaximumSlots; index++)
        {
            long value = reader.ReadInt64();
            if ((knownMask & (1 << index)) != 0)
            {
                admitted.SetSlot(index, value);
            }
        }

        if (reader.ReadBoolean())
        {
            admitted.SetIdentifier(new Guid(ReadExactly(reader, 16), bigEndian: true));
        }

        bool nameTruncated = reader.ReadBoolean();
        int nameByteLength = reader.ReadUInt16();
        if (nameByteLength > AdmittedEvent.MaximumNameLength * 4)
        {
            throw new InvalidDataException("A journal-v1 projection name exceeds its UTF-8 bound.");
        }

        string name = Encoding.UTF8.GetString(ReadExactly(reader, nameByteLength));
        if (name.Length > 0)
        {
            admitted.SetName(name, nameTruncated);
        }

        var availability = (ExtendedDataAvailability)reader.ReadByte();
        int present = reader.ReadUInt16();
        admitted.BeginExtendedData(availability, present);
        Span<byte> item = stackalloc byte[AdmittedEvent.MaximumExtendedItemBytes];
        foreach (ExtendedItemV1 extended in envelope.ExtendedItems)
        {
            int length = Math.Min(extended.Bytes.Length, item.Length);
            extended.Bytes.ReadOnlySpan[..length].CopyTo(item);
            if (!admitted.TryAppendExtendedItem(
                extended.Type,
                extended.Flags,
                item[..length],
                extended.OriginalLength))
            {
                throw new InvalidDataException(
                    "A journal-v1 envelope carries more extended items than one admitted record holds.");
            }
        }

        for (int index = 0; index < envelope.OmittedExtendedItemCount; index++)
        {
            admitted.RecordOmittedExtendedItem();
        }

        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("A journal-v1 projection has an unexplained trailing tail.");
        }

        return admitted.SourceIndex == plan.SourceIndex && envelope.PointerSize == plan.PointerSize
            ? admitted
            : throw new InvalidDataException(
                "A journal-v1 projection no longer matches its admitted descriptor plan.");
    }

    /// <summary>
    /// Fingerprints an envelope's identity and contents in a fixed order, so what a replay reads can be
    /// compared to what the capture wrote without comparing files byte for byte (I1).
    /// </summary>
    public static void AppendFingerprint(IncrementalHash hash, RecordEnvelopeV1 envelope)
    {
        ArgumentNullException.ThrowIfNull(hash);
        ArgumentNullException.ThrowIfNull(envelope);
        hash.AppendData(envelope.CaptureId.Value.ToByteArray(bigEndian: true));
        Append(hash, envelope.StreamId);
        Append(hash, envelope.SourceEpoch);
        Append(hash, envelope.RecordOrdinal);
        hash.AppendData(envelope.Header.ProviderId.ToByteArray(bigEndian: true));
        Append(hash, envelope.Header.EventId);
        Append(hash, envelope.Header.Version);
        Append(hash, envelope.Header.Opcode);
        Append(hash, envelope.Header.ProcessId);
        Append(hash, envelope.Header.ThreadId);
        hash.AppendData(envelope.Header.ActivityId.ToByteArray(bigEndian: true));
        hash.AppendData(envelope.Header.RelatedActivityId.ToByteArray(bigEndian: true));
        Append(hash, envelope.BufferContext.ProcessorNumber);
        hash.AppendData(envelope.ClockId.Value.ToByteArray(bigEndian: true));
        Append(hash, (int)envelope.TimestampEncoding);
        Append(hash, envelope.NativeTicks);
        Append(hash, envelope.PointerSize);
        Append(hash, envelope.SchemaReference ?? 0);
        Append(hash, envelope.AdmissionPolicyReference);
        Append(hash, (int)envelope.Body.Classification);
        Append(hash, (int)envelope.Body.Disposition);
        Append(hash, envelope.Body.OriginalLength);
        Append(hash, envelope.Body.RetainedLength);
        hash.AppendData(envelope.Body.Bytes.ReadOnlySpan);
        Append(hash, envelope.OmittedExtendedItemCount);
        Append(hash, envelope.ExtendedItems.Count);
        foreach (ExtendedItemV1 item in envelope.ExtendedItems)
        {
            Append(hash, item.Type);
            Append(hash, item.Flags);
            Append(hash, item.OriginalLength);
            Append(hash, item.Bytes.Length);
            hash.AppendData(item.Bytes.ReadOnlySpan);
        }
    }

    /// <summary>
    /// The bounded field projection, as an approved-metadata body. It is a transformed projection and is
    /// labelled as one: §18.2 forbids presenting a projection as original raw bytes.
    /// </summary>
    private static EnvelopeBuffer EncodeProjection(in AdmittedEvent admitted)
    {
        using var stream = new MemoryStream(512);
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(ProjectionMagic);
            writer.Write(admitted.TimestampUtcTicks);
            writer.Write(admitted.KnownSlotMask);
            for (int index = 0; index < AdmissionPlanCompiler.MaximumSlots; index++)
            {
                _ = admitted.TryGetSlot(index, out long value);
                writer.Write(value);
            }

            writer.Write(admitted.HasIdentifier);
            if (admitted.HasIdentifier)
            {
                writer.Write(admitted.Identifier.ToByteArray(bigEndian: true));
            }

            Span<char> chars = stackalloc char[AdmittedEvent.MaximumNameLength];
            int nameLength = admitted.CopyName(chars);
            byte[] name = Encoding.UTF8.GetBytes(new string(chars[..nameLength]));
            writer.Write(admitted.NameTruncated);
            writer.Write(checked((ushort)name.Length));
            writer.Write(name);

            // How extended data was handled travels with the record, so a replayed record repeats the
            // counts instead of inferring them from what survived the policy (I13).
            writer.Write((byte)admitted.ExtendedData);
            writer.Write(checked((ushort)admitted.ExtendedItemsPresent));
        }

        return EnvelopeBuffer.CopyOf(stream.GetBuffer().AsSpan(0, (int)stream.Length));
    }

    private static string Fingerprint(AdmittedEventPlan plan) =>
        $"{plan.ProviderGuid:N}:{plan.EventId}:{plan.Version}:{plan.PointerSize}:{plan.MinimumBodyLength}";

    private static byte[] ReadExactly(BinaryReader reader, int length)
    {
        byte[] value = reader.ReadBytes(length);
        return value.Length == length
            ? value
            : throw new EndOfStreamException("A journal-v1 projection ended inside a declared field.");
    }

    private static void Append(IncrementalHash hash, int value) => Append(hash, unchecked((ulong)(long)value));

    private static void Append(IncrementalHash hash, long value) => Append(hash, unchecked((ulong)value));

    private static void Append(IncrementalHash hash, uint value) => Append(hash, (ulong)value);

    private static void Append(IncrementalHash hash, ushort value) => Append(hash, (ulong)value);

    private static void Append(IncrementalHash hash, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }
}
