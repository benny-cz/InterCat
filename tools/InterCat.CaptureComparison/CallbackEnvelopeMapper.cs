using System.Security.Cryptography;
using System.Text;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.CaptureComparison;

/// <summary>
/// Maps one admitted callback record into the IC-009 envelope candidate: the bounded field projection,
/// the record's copied extended-data items, and the capture's own source clock. Nothing here invents
/// evidence — an item the callback could not copy stays absent and counted, and the clock is the one the
/// session recorded, not a fresh identifier per run (section 18.1, ADR-006).
/// </summary>
internal sealed class CallbackEnvelopeMapper
{
    internal const string PolicyId = "callback-envelope-candidate-v0";
    private const uint ProjectionMagic = 0x30504149; // "IAP0" little-endian.

    private readonly JournalProbePolicy policy;
    private readonly ClockId clockId;

    public CallbackEnvelopeMapper(IReadOnlyList<SourceAdmissionPlan> sources, ClockId clockId)
    {
        ArgumentNullException.ThrowIfNull(sources);
        JournalProbeDescriptor[] descriptors =
        [
            .. sources.SelectMany(source => source.Events).Select(plan => new JournalProbeDescriptor(
                plan.ProviderGuid,
                plan.EventId,
                plan.Version,
                Fingerprint(plan))),
        ];
        policy = new(
            PolicyId,
            JournalProbeAdmissionMode.MetadataOnly,
            maximumRetainedBodyBytes: 4_096,
            maximumExtendedItemBytes: AdmittedEvent.MaximumExtendedItemBytes,
            descriptors,
            EtwExtendedDataTypes.PermittedMetadata);
        this.clockId = clockId;
    }

    public JournalProbeEnvelope ToEnvelope(in AdmittedEvent admitted, AdmittedEventPlan plan, CaptureId captureId)
    {
        byte[] projection = EncodeProjection(admitted);
        var source = new JournalProbeSourceRecord
        {
            Id = new(
                captureId,
                checked((uint)admitted.SourceIndex + 1),
                1,
                checked((ulong)admitted.RecordOrdinal)),
            ProviderGuid = plan.ProviderGuid,
            EventId = admitted.EventId,
            Version = admitted.Version,
            Opcode = admitted.Opcode,
            NativeTimestamp = new(clockId, TimestampEncoding.Qpc, admitted.TimestampQpc),
            HeaderProcessId = admitted.HeaderProcessId,
            HeaderThreadId = admitted.HeaderThreadId,
            ProcessorNumber = admitted.ProcessorNumber,
            PointerSize = plan.PointerSize,
            ActivityId = admitted.ActivityId,
            RelatedActivityId = admitted.RelatedActivityId,
            SchemaFingerprint = Fingerprint(plan),
            BodyClassification = JournalProbeBodyClassification.ApprovedMetadata,
            Body = projection,
            ExtendedItems = ReadExtendedItems(admitted),
            ScopeMatched = true,
        };
        return JournalProbeAdmission.Admit(source, policy).Envelope;
    }

    /// <summary>Copies the record's extended items out of its inline storage, bytes only, never a pointer.</summary>
    private static List<JournalProbeExtendedItem> ReadExtendedItems(in AdmittedEvent admitted)
    {
        int count = admitted.ExtendedItemsCopied;
        if (count == 0)
        {
            return [];
        }


        var items = new List<JournalProbeExtendedItem>(count);
        Span<byte> buffer = stackalloc byte[AdmittedEvent.MaximumExtendedItemBytes];
        for (int index = 0; index < count; index++)
        {
            AdmittedExtendedItem item = admitted.GetExtendedItem(index);
            int length = admitted.CopyExtendedItemBytes(index, buffer);
            items.Add(new(item.Type, item.Linkage, buffer[..length].ToArray()));
        }

        return items;
    }

    public static AdmittedEvent FromEnvelope(JournalProbeEnvelope envelope, AdmittedEventPlan plan)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(plan);
        if (envelope.Body.Disposition != JournalProbeBodyDisposition.Retained)
        {
            throw new InvalidDataException(
                $"Projection body for ordinal {envelope.Id.RecordOrdinal} was not retained: {envelope.Body.Disposition}.");
        }

        using var stream = new MemoryStream(envelope.Body.RetainedBytes.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        if (reader.ReadUInt32() != ProjectionMagic)
        {
            throw new InvalidDataException("Journal probe projection body has the wrong marker.");
        }

        var admitted = new AdmittedEvent
        {
            SourceIndex = checked((int)envelope.Id.StreamId - 1),
            EventId = envelope.EventId,
            Version = envelope.Version,
            Opcode = envelope.Opcode,
            TimestampQpc = envelope.NativeTimestamp.Ticks,
            TimestampUtcTicks = reader.ReadInt64(),
            HeaderProcessId = envelope.HeaderProcessId,
            HeaderThreadId = envelope.HeaderThreadId,
            ProcessorNumber = envelope.ProcessorNumber,
            RecordOrdinal = checked((long)envelope.Id.RecordOrdinal),
            ActivityId = envelope.ActivityId,
            RelatedActivityId = envelope.RelatedActivityId,
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

        bool hasIdentifier = reader.ReadBoolean();
        if (hasIdentifier)
        {
            admitted.SetIdentifier(new Guid(ReadExactly(reader, 16), bigEndian: true));
        }

        bool nameTruncated = reader.ReadBoolean();
        int nameByteLength = reader.ReadUInt16();
        if (nameByteLength > AdmittedEvent.MaximumNameLength * 4)
        {
            throw new InvalidDataException("Journal probe projection name exceeds its UTF-8 bound.");
        }

        byte[] nameBytes = ReadExactly(reader, nameByteLength);
        string name = Encoding.UTF8.GetString(nameBytes);
        if (name.Length > 0)
        {
            admitted.SetName(name, nameTruncated);
        }

        var availability = (ExtendedDataAvailability)reader.ReadByte();
        int present = reader.ReadUInt16();
        int admissionOmitted = reader.ReadUInt16();
        int copied = reader.ReadByte();
        admitted.BeginExtendedData(availability, present);
        int consumed = 0;
        for (int index = 0; index < copied; index++)
        {
            ushort type = reader.ReadUInt16();
            ushort linkage = reader.ReadUInt16();
            int originalLength = reader.ReadUInt16();

            // A copied item whose type the persistence policy denies is absent from the envelope by
            // design. Replay counts it as an omission rather than inventing bytes for it (R17, I13).
            if (consumed >= envelope.ExtendedItems.Count || envelope.ExtendedItems[consumed].Type != type)
            {
                admitted.RecordOmittedExtendedItem();
                continue;
            }

            JournalProbeExtendedItem item = envelope.ExtendedItems[consumed++];
            if (!admitted.TryAppendExtendedItem(type, linkage, item.Bytes.Span, originalLength))
            {
                throw new InvalidDataException("A replayed envelope carries more extended items than one record holds.");
            }
        }

        if (consumed != envelope.ExtendedItems.Count)
        {
            throw new InvalidDataException("A replayed envelope has extended items its projection does not describe.");
        }

        for (int index = 0; index < admissionOmitted; index++)
        {
            admitted.RecordOmittedExtendedItem();
        }

        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("Journal probe projection has an unexplained trailing tail.");
        }

        if (admitted.SourceIndex != plan.SourceIndex || envelope.PointerSize != plan.PointerSize)
        {
            throw new InvalidDataException("Journal probe projection no longer matches its admitted descriptor plan.");
        }

        return admitted;
    }

    public static void AppendFingerprint(IncrementalHash hash, JournalProbeEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(hash);
        ArgumentNullException.ThrowIfNull(envelope);
        hash.AppendData(envelope.Id.CaptureId.Value.ToByteArray(bigEndian: true));
        Append(hash, envelope.Id.StreamId);
        Append(hash, envelope.Id.SourceEpoch);
        Append(hash, envelope.Id.RecordOrdinal);
        hash.AppendData(envelope.ProviderGuid.ToByteArray(bigEndian: true));
        Append(hash, envelope.EventId);
        Append(hash, envelope.Version);
        Append(hash, envelope.Opcode);
        hash.AppendData(envelope.NativeTimestamp.ClockId.Value.ToByteArray(bigEndian: true));
        Append(hash, (int)envelope.NativeTimestamp.Encoding);
        Append(hash, envelope.NativeTimestamp.Ticks);
        Append(hash, envelope.HeaderProcessId);
        Append(hash, envelope.HeaderThreadId);
        Append(hash, envelope.ProcessorNumber);
        Append(hash, envelope.PointerSize);
        hash.AppendData(envelope.ActivityId.ToByteArray(bigEndian: true));
        hash.AppendData(envelope.RelatedActivityId.ToByteArray(bigEndian: true));
        AppendText(hash, envelope.SchemaFingerprint);
        AppendText(hash, envelope.AdmissionPolicyId);
        Append(hash, (int)envelope.Body.Classification);
        Append(hash, (int)envelope.Body.Disposition);
        Append(hash, envelope.Body.OriginalLength);
        Append(hash, envelope.Body.RetainedBytes.Length);
        hash.AppendData(envelope.Body.RetainedBytes.Span);
        Append(hash, envelope.OmittedExtendedItemCount);
        Append(hash, envelope.ExtendedItems.Count);
        foreach (JournalProbeExtendedItem item in envelope.ExtendedItems)
        {
            Append(hash, item.Type);
            Append(hash, item.Flags);
            Append(hash, item.Bytes.Length);
            hash.AppendData(item.Bytes.Span);
        }
    }

    private static byte[] EncodeProjection(in AdmittedEvent admitted)
    {
        using var stream = new MemoryStream(512);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
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

        // The extended-data disposition travels with the record: how it was handled, how many items the
        // source declared, how many admission did not copy, and each copied item's type and original
        // length. A replayed record repeats those counts instead of inferring them, and an item the
        // persistence policy denied is visible as a type with no bytes rather than as a gap (I13).
        writer.Write((byte)admitted.ExtendedData);
        writer.Write(checked((ushort)admitted.ExtendedItemsPresent));
        writer.Write(checked((ushort)admitted.ExtendedItemsOmitted));
        writer.Write(checked((byte)admitted.ExtendedItemsCopied));
        for (int index = 0; index < admitted.ExtendedItemsCopied; index++)
        {
            AdmittedExtendedItem item = admitted.GetExtendedItem(index);
            writer.Write(item.Type);
            writer.Write(item.Linkage);
            writer.Write(checked((ushort)item.OriginalLength));
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static string Fingerprint(AdmittedEventPlan plan) =>
        $"{plan.ProviderGuid:N}:{plan.EventId}:{plan.Version}:{plan.PointerSize}:{plan.MinimumBodyLength}";

    private static byte[] ReadExactly(BinaryReader reader, int length)
    {
        byte[] value = reader.ReadBytes(length);
        return value.Length == length
            ? value
            : throw new EndOfStreamException("Journal probe projection ended inside a declared field.");
    }

    private static void Append(IncrementalHash hash, int value) => Append(hash, unchecked((ulong)(long)value));

    private static void Append(IncrementalHash hash, long value) => Append(hash, unchecked((ulong)value));

    private static void Append(IncrementalHash hash, uint value) => Append(hash, (ulong)value);

    private static void Append(IncrementalHash hash, ushort value) => Append(hash, (ulong)value);

    private static void AppendText(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Append(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }
}
