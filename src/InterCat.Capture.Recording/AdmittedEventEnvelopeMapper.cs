using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Capture.Recording;

/// <summary>
/// Maps one admitted callback record into a journal-v1 envelope: the bounded field projection as an
/// approved-metadata body, the record's copied extended-data items, and the capture's own source clock.
/// Nothing here invents evidence — an item the callback could not copy stays absent and counted, an item
/// the metadata policy denies loses its bytes and is counted, and the clock is the one the session
/// recorded rather than a fresh identifier per run.
/// </summary>
public sealed class AdmittedEventEnvelopeMapper
{
    public const string PolicyId = CaptureBodyAdmissionPolicies.MetadataOnlyPolicyId;
    private const uint ProjectionMagic = 0x31504149; // "IAP1" little-endian.

    /// <summary>
    /// "IAP2" little-endian: an IAP1 projection that also carries the record's 128-bit addresses after its identifier - a
    /// mask of which it holds, then each one's 16 bytes in network order (revision 174). A record with no address is
    /// written as IAP1, so a journal of IPv4 records keeps its bytes.
    /// </summary>
    private const uint AddressedProjectionMagic = 0x32504149;

    private readonly JournalV1SchemaTable schemas = new();
    private readonly ClockId clockId;

    public AdmittedEventEnvelopeMapper(IReadOnlyList<SourceAdmissionPlan> sources, ClockId clockId)
    {
        ArgumentNullException.ThrowIfNull(sources);
        foreach (SourceAdmissionPlan source in sources)
        {
            foreach (AdmittedEventPlan plan in source.Events)
            {
                CaptureBodyAdmissionPolicies.EnsureSupported(plan.BodyPolicy);
                _ = schemas.Intern(
                    plan.ProviderGuid,
                    checked((ushort)plan.EventId),
                    checked((byte)plan.Version),
                    plan.SchemaFingerprint);
                _ = schemas.InternPolicy(plan.BodyPolicy.PolicyId);
            }
        }

        this.clockId = clockId;
    }

    /// <summary>The tables the journal writes before its first batch, so replay resolves every reference.</summary>
    public JournalV1SchemaTable Schemas => schemas;

    public RecordEnvelopeV1 ToEnvelope(in AdmittedEvent admitted, AdmittedEventPlan plan, CaptureId captureId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        CaptureBodyAdmissionPolicies.EnsureSupported(plan.BodyPolicy);
        if (admitted.SourceIndex != plan.SourceIndex
            || admitted.EventId != plan.EventId
            || admitted.Version != plan.Version
            || (plan.Opcode is { } opcode && admitted.Opcode != opcode)
            || !WidthAdmitted(plan, RecordWidth(admitted, plan)))
        {
            throw new InvalidDataException(
                "An admitted callback record does not match the descriptor plan selected for persistence.");
        }

        uint schemaReference = schemas.Intern(
            plan.ProviderGuid,
            checked((ushort)plan.EventId),
            checked((byte)plan.Version),
            plan.SchemaFingerprint);
        uint policyReference = schemas.InternPolicy(plan.BodyPolicy.PolicyId);
        EnvelopeBuffer projection = EncodeProjection(admitted);
        if (projection.Length > plan.BodyPolicy.MaximumRetainedBodyBytes)
        {
            int actualLength = projection.Length;
            projection.Dispose();
            throw new InvalidDataException(
                $"The approved metadata projection is {actualLength} bytes, beyond policy "
                + $"'{plan.BodyPolicy.PolicyId}' limit of {plan.BodyPolicy.MaximumRetainedBodyBytes} bytes.");
        }

        List<ExtendedItemV1>? extendedItems = null;
        try
        {
            extendedItems = ReadExtendedItems(admitted, plan.BodyPolicy);
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
                PointerSize = checked((byte)RecordWidth(admitted, plan)),
                SchemaReference = schemaReference,
                AdmissionPolicyReference = policyReference,
                ExtendedItems = extendedItems,
                OmittedExtendedItemCount = admitted.ExtendedItemsOmitted + DeniedItemCount(admitted, plan.BodyPolicy),
                Body = new()
                {
                    Classification = BodyClassificationV1.ApprovedMetadata,
                    Disposition = BodyDispositionV1.Retained,
                    // This is the original length of the transformed projection, not the ETW user-data
                    // buffer. The policy explicitly forbids retaining or representing those source bytes.
                    OriginalLength = projection.Length,
                    Bytes = projection,
                },
            };
        }
        catch
        {
            projection.Dispose();
            foreach (ExtendedItemV1 item in extendedItems ?? [])
            {
                item.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// Copies the record's permitted extended items out of its inline storage, bytes only, never a
    /// pointer. An item whose type the metadata policy denies loses its bytes here and is counted as an
    /// omission, so replay reproduces the refusal rather than the item (R17, I13).
    /// </summary>
    private static List<ExtendedItemV1> ReadExtendedItems(
        in AdmittedEvent admitted,
        CompiledBodyAdmissionPolicy policy)
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
            if (!policy.PermitsExtendedDataType(item.Type))
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

    private static int DeniedItemCount(in AdmittedEvent admitted, CompiledBodyAdmissionPolicy policy)
    {
        int denied = 0;
        for (int index = 0; index < admitted.ExtendedItemsCopied; index++)
        {
            if (!policy.PermitsExtendedDataType(admitted.GetExtendedItem(index).Type))
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
        CaptureBodyAdmissionPolicies.EnsureSupported(plan.BodyPolicy);
        if (envelope.Body.Disposition != BodyDispositionV1.Retained)
        {
            throw new InvalidDataException(
                $"The projection body of record {envelope.RecordOrdinal} was not retained: "
                + $"{envelope.Body.Disposition}.");
        }

        if (envelope.Body.Classification != BodyClassificationV1.ApprovedMetadata
            || envelope.Body.OriginalLength != envelope.Body.RetainedLength
            || envelope.Body.RetainedLength > plan.BodyPolicy.MaximumRetainedBodyBytes)
        {
            throw new InvalidDataException(
                $"Record {envelope.RecordOrdinal} does not satisfy admission policy "
                + $"'{plan.BodyPolicy.PolicyId}'.");
        }

        using var stream = new MemoryStream(envelope.Body.Bytes.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        uint magic = reader.ReadUInt32();
        if (magic is not (ProjectionMagic or AddressedProjectionMagic))
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
            PointerSize = envelope.PointerSize,
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

        if (magic == AddressedProjectionMagic)
        {
            // IAP2 is written only for a record that holds an address, and a record holds at most two.
            byte addresses = reader.ReadByte();
            if (addresses == 0 || (addresses >> AdmittedEvent.MaximumAddresses) != 0)
            {
                throw new InvalidDataException("A journal-v1 projection declares addresses a record cannot hold.");
            }

            for (int ordinal = 0; ordinal < AdmittedEvent.MaximumAddresses; ordinal++)
            {
                if ((addresses & (1 << ordinal)) != 0)
                {
                    admitted.SetAddress(ordinal, BinaryPrimitives.ReadUInt128BigEndian(ReadExactly(reader, 16)));
                }
            }
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

        return admitted.SourceIndex == plan.SourceIndex
            && envelope.Header.ProviderId == plan.ProviderGuid
            && envelope.Header.EventId == plan.EventId
            && envelope.Header.Version == plan.Version
            && (plan.Opcode is not { } classicOpcode || envelope.Header.Opcode == classicOpcode)
            && WidthAdmitted(plan, envelope.PointerSize)
            ? admitted
            : throw new InvalidDataException(
                "A journal-v1 projection no longer matches its admitted descriptor plan.");
    }

    /// <summary>The pointer width a record was raised at: its own when the callback stated it, else the plan's.</summary>
    private static int RecordWidth(AdmittedEvent admitted, AdmittedEventPlan plan) =>
        admitted.PointerSize == 0 ? plan.PointerSize : admitted.PointerSize;

    /// <summary>
    /// Whether a plan admits a record of this pointer width: its own width always, and the other one only when every
    /// admitted field lies before any pointer-sized field, so the offsets do not move (normalizer-plan-v1).
    /// </summary>
    private static bool WidthAdmitted(AdmittedEventPlan plan, int width) =>
        width == plan.PointerSize || (plan.PointerWidthIndependent && width is 4 or 8);

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
            byte addresses = admitted.KnownAddressMask;
            writer.Write(addresses == 0 ? ProjectionMagic : AddressedProjectionMagic);
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

            if (addresses != 0)
            {
                writer.Write(addresses);
                Span<byte> address = stackalloc byte[16];
                for (int ordinal = 0; ordinal < AdmittedEvent.MaximumAddresses; ordinal++)
                {
                    if (admitted.TryGetAddress(ordinal, out UInt128 value))
                    {
                        BinaryPrimitives.WriteUInt128BigEndian(address, value);
                        writer.Write(address);
                    }
                }
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
