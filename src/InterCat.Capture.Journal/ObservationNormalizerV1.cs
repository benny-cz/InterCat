using System.Buffers.Binary;
using InterCat.Capture.Recording;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Capture.Journal;

/// <summary>
/// Turns one admitted journal record into the `observation-v1` row a segment publishes. It is derivation
/// number one of §24's normalizer axis: given the same journal, the same descriptor plan and this version, it
/// produces the same rows with the same identities (I2, I14, R20).
/// </summary>
/// <remarks>
/// It derives from the journal, not from the callback. That is what makes a generation rebuildable from
/// retained evidence alone: the input is the envelope the journal stored, so re-running the normalizer over a
/// published journal reproduces the segment without the capture that produced it (R20, §20.2).
///
/// Nothing here promotes a value the source did not state. The layer, mechanism, kind and direction come from
/// the descriptor's own source contract; the owner comes from the record's payload and never from the event
/// header; the byte measurement keeps its declared domain even when the value is unknown, because that is what
/// gives an unknown a denominator (R2, R3, §4.1).
/// </remarks>
public sealed class ObservationNormalizerV1
{
    /// <summary>The derivation this normalizer is. It is part of every row's observation identity (§7.2).</summary>
    public static NormalizerContractVersion ContractVersion => NormalizerContractVersion.V1;

    private readonly SourceClockDescriptor clock;

    public ObservationNormalizerV1(SourceClockDescriptor clock) => this.clock = clock;

    /// <summary>
    /// The fact discriminator for a descriptor's rows. It is a property of the mechanism family rather than of
    /// the record, so the same descriptor always produces the same fact key for the same raw record (§7.2).
    /// </summary>
    public static string DiscriminatorFor(Mechanism mechanism) => mechanism switch
    {
        Mechanism.ProcessLifecycle or Mechanism.ThreadLifecycle => "process-lifecycle",
        Mechanism.Tcp or Mechanism.Udp => "network-transfer",
        Mechanism.Rpc => "rpc-call",
        Mechanism.NamedPipe or Mechanism.AnonymousPipe => "pipe-operation",
        Mechanism.Alpc => "alpc-message",
        Mechanism.SharedSection => "section-mapping",
        _ => "source-observation",
    };

    /// <summary>
    /// Derives the row one admitted record yields. A record that yields several rows is a later normalizer's
    /// work; this version yields exactly one per record and its fact key says which fact that is.
    /// </summary>
    public ObservationRowV1 ToRow(
        RecordEnvelopeV1 envelope,
        AdmittedEventPlan plan,
        ulong? journalRecordIndex = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(plan);
        if (envelope.ClockId != clock.Id)
        {
            throw new InvalidDataException(
                $"Record {envelope.RecordOrdinal} names clock {envelope.ClockId} where this derivation reads "
                + $"{clock.Id}. A reading is never converted against a clock that did not produce it (I8).");
        }

        AdmittedEvent admitted = AdmittedEventEnvelopeMapper.FromEnvelope(envelope, plan);
        SlotValues slots = ReadSlots(in admitted, plan);
        ByteMeasurement measurement = ResolveMeasurement(in admitted, plan, slots);
        TimestampConversionResult session = SourceClockMath.ConvertToSession(
            clock,
            new(envelope.ClockId, envelope.TimestampEncoding, envelope.NativeTicks));

        return new()
        {
            RawStreamId = envelope.StreamId,
            RawSourceEpoch = envelope.SourceEpoch,
            RawRecordOrdinal = envelope.RecordOrdinal,
            JournalRecordIndex = journalRecordIndex,
            FactKey = FactKey.Create(DiscriminatorFor(plan.Mechanism)),
            ProviderId = plan.ProviderGuid,
            EventId = checked((ushort)plan.EventId),
            DescriptorVersion = checked((byte)plan.Version),
            SchemaFingerprint = plan.SchemaFingerprint,
            Opcode = envelope.Header.Opcode,
            NativeTicks = envelope.NativeTicks,
            SessionRelativeTicks = session.SessionTime?.Nanoseconds,
            HeaderProcessId = envelope.Header.ProcessId,
            HeaderThreadId = envelope.Header.ThreadId,
            ProcessorNumber = envelope.BufferContext.ProcessorNumber,

            // An absent activity id is absent, not zero. Collapsing "no id" onto the empty GUID would let
            // unrelated records look like they shared a correlation (§7.2).
            ActivityId = envelope.Header.ActivityId == Guid.Empty ? null : envelope.Header.ActivityId,
            RelatedActivityId = envelope.Header.RelatedActivityId == Guid.Empty
                ? null
                : envelope.Header.RelatedActivityId,
            Mechanism = plan.Mechanism,
            Layer = plan.Layer,
            Kind = plan.Kind,
            Direction = plan.Direction,
            OwnerProcessId = slots.OwnerProcessId,
            ResourceName = slots.ResourceName,
            SourceIdentifier = admitted.HasIdentifier ? admitted.Identifier : null,
            EndpointAddressFamily = slots.AddressFamily,
            SourceEndpointAddress = slots.SourceAddress,
            SourceEndpointPort = slots.SourcePort,
            DestinationEndpointAddress = slots.DestinationAddress,
            DestinationEndpointPort = slots.DestinationPort,
            ByteValue = measurement.Value,
            ByteDomain = measurement.Domain,
            AccountingSide = measurement.Side,
            MeasurementUnit = measurement.Unit,
            ByteAvailability = measurement.Availability,
            StatusCode = slots.StatusCode,

            // §7.3 names a status domain that §23 assigns no enumeration, so a status is stored as the code
            // the source reported and nothing claims which domain it is in. A source that exposes no status at
            // all reports `NotApplicable` rather than an unexplained null.
            StatusAvailability = slots.StatusCode is null
                ? AvailabilityOf(plan, FieldRole.Status) ?? FieldAvailability.NotApplicable
                : FieldAvailability.Present,

            // The payload names the owner for every currently validated source, so attribution is proven when
            // it is there and unknown when it is not. It is never inferred from the event header (§4.1).
            AttributionQuality = slots.OwnerProcessId is null ? QualityLevel.UnknownQuality : QualityLevel.Proven,

            // No correlator has run over these rows yet. An unrun correlation is unknown, not weak: claiming
            // a level would describe work that has not happened (R4, §7.4).
            CorrelationQuality = QualityLevel.UnknownQuality,
            MeasurementQuality = measurement.Value is null ? QualityLevel.UnknownQuality : QualityLevel.Proven,

            // The reading is the source's own on the clock the journal names. A quarantined conversion leaves
            // the native reading intact and says the derived instant is unknown (ADR-006, I8).
            TimingQuality = session.QuarantineReason == TimestampQuarantineReason.None
                ? QualityLevel.Proven
                : QualityLevel.UnknownQuality,
            Markers = Markers(in admitted, envelope, plan),
        };
    }

    /// <summary>
    /// The §7.3 source correlation and object fields one admitted record carries, which `observation-v1` has no column
    /// for: one row per admitted slot the catalog gives a meaning, keyed to the observation <paramref name="row"/>
    /// derived from the same record. A value is carried exactly as the source delivered it; a descriptor that does not
    /// admit a field yields no row for it, because the capability report already says why per descriptor.
    /// </summary>
    public static IReadOnlyList<SourceFieldRowV1> FieldRows(RecordEnvelopeV1 envelope, AdmittedEventPlan plan, ObservationRowV1 row)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(row);
        IReadOnlyList<AdmittedSlotPlan> slots = plan.Slots;
        List<SourceFieldRowV1>? fields = null;
        AdmittedEvent admitted = default;
        bool decoded = false;
        for (int index = 0; index < slots.Count; index++)
        {
            if (slots[index].SourceField is not { } field || slots[index].Kind != AdmittedSlotKind.Numeric)
            {
                continue;
            }

            if (!decoded)
            {
                admitted = AdmittedEventEnvelopeMapper.FromEnvelope(envelope, plan);
                decoded = true;
            }

            bool known = admitted.TryGetSlot(index, out long value);
            (fields ??= []).Add(new()
            {
                RawStreamId = row.RawStreamId,
                RawSourceEpoch = row.RawSourceEpoch,
                RawRecordOrdinal = row.RawRecordOrdinal,
                FactKey = row.FactKey,
                NativeTicks = row.NativeTicks,
                Field = field,
                Value = known ? value : null,
                Availability = known ? FieldAvailability.Present : FieldAvailability.EventLost,
            });
        }

        return (IReadOnlyList<SourceFieldRowV1>?)fields ?? [];
    }

    private static SegmentRowMarkers Markers(
        in AdmittedEvent admitted,
        RecordEnvelopeV1 envelope,
        AdmittedEventPlan plan)
    {
        SegmentRowMarkers markers = SegmentRowMarkers.None;
        if (admitted.NameTruncated && admitted.HasName)
        {
            markers |= SegmentRowMarkers.ResourceNameTruncated;
        }

        // The capture asked for extended items whenever the adapter walked the record or the record itself
        // declared none: both are answers to a request. A run that never asked reports nothing, so a zero
        // count describes the capture's configuration rather than the record (§18.1).
        if (admitted.ExtendedData is ExtendedDataAvailability.Captured or ExtendedDataAvailability.RecordCarriedNone)
        {
            markers |= SegmentRowMarkers.ExtendedDataRequested;
        }

        if (envelope.OmittedExtendedItemCount > 0)
        {
            markers |= SegmentRowMarkers.ExtendedItemsOmitted;
        }

        if (envelope.Body.Disposition != BodyDispositionV1.Retained
            || envelope.Body.RetainedLength < envelope.Body.OriginalLength)
        {
            markers |= SegmentRowMarkers.BodyReducedByPolicy;
        }

        _ = plan;
        return markers;
    }

    /// <summary>
    /// Resolves the byte measurement and its labels. The slot and the value are separate facts: a descriptor
    /// that declares a byte field labels its domain, side and unit whether or not this record carried a value,
    /// and a descriptor that declares none reports `NotApplicable` with no labels at all (R2, R3, §5).
    /// </summary>
    private static ByteMeasurement ResolveMeasurement(
        in AdmittedEvent admitted,
        AdmittedEventPlan plan,
        SlotValues slots)
    {
        AccountingSide? side = plan.Kind switch
        {
            ObservationKind.Send => AccountingSide.SendSide,
            ObservationKind.Receive => AccountingSide.ReceiveSide,

            // A descriptor whose kind names no side still measures something; which side owns the total is a
            // correlation decision, so until one is made the contribution is endpoint activity, which §5.1
            // labels as counting both sides by design rather than pretending to be a one-sided total.
            _ => AccountingSide.EndpointActivity,
        };

        if (slots.ByteSlot is { } slot)
        {
            return new(
                slots.ByteValue,
                slot.ByteDomain,
                side,
                slot.Unit,
                slots.ByteValue is null ? FieldAvailability.EventLost : FieldAvailability.Present);
        }

        // The descriptor declared no admitted byte field. The capability report says whether the source has one
        // it could not admit, which is an unknown in a declared slot, or none at all, which is inapplicable.
        FieldCapability? declared = plan.FieldReport.FirstOrDefault(field =>
            field.Role == FieldRole.ByteCount && field.ByteDomain is not null);
        _ = admitted;
        return declared is null
            ? new(null, null, null, null, FieldAvailability.NotApplicable)
            : new(
                null,
                declared.ByteDomain,
                side,
                declared.Unit ?? MeasurementUnit.Bytes,
                declared.Availability == FieldAvailability.Present
                    ? FieldAvailability.NotExposed
                    : declared.Availability);
    }

    private static FieldAvailability? AvailabilityOf(AdmittedEventPlan plan, FieldRole role) =>
        plan.FieldReport.FirstOrDefault(field => field.Role == role)?.Availability;

    private static SlotValues ReadSlots(in AdmittedEvent admitted, AdmittedEventPlan plan)
    {
        var values = new SlotValues();
        IReadOnlyList<AdmittedSlotPlan> slots = plan.Slots;
        for (int index = 0; index < slots.Count; index++)
        {
            AdmittedSlotPlan slot = slots[index];
            bool known = admitted.TryGetSlot(index, out long raw);
            switch (slot.Role)
            {
                case FieldRole.ProcessAttribution when known:
                    values.OwnerProcessId ??= (int)raw;
                    break;
                case FieldRole.ByteCount:
                    values.ByteSlot ??= slot;
                    if (known)
                    {
                        values.ByteValue ??= raw;
                    }

                    break;
                case FieldRole.Status when known:
                    values.StatusCode ??= raw;
                    break;
                case FieldRole.SourceEndpoint when slot.Width == 4:
                    values.AddressFamily ??= 4;
                    if (known)
                    {
                        values.SourceAddress ??= Address(raw, slot.Transform);
                    }

                    break;
                case FieldRole.SourceEndpoint when known:
                    values.SourcePort ??= Port(raw, slot.Transform);
                    break;
                case FieldRole.DestinationEndpoint when slot.Width == 4:
                    values.AddressFamily ??= 4;
                    if (known)
                    {
                        values.DestinationAddress ??= Address(raw, slot.Transform);
                    }

                    break;
                case FieldRole.DestinationEndpoint when known:
                    values.DestinationPort ??= Port(raw, slot.Transform);
                    break;
                default:
                    break;
            }
        }

        if (admitted.HasName)
        {
            Span<char> name = stackalloc char[AdmittedEvent.MaximumNameLength];
            int length = admitted.CopyName(name);
            values.ResourceName = length == 0 ? null : new string(name[..length]);
        }

        // An address with no port, or a port with no address, is still an endpoint the source named in part.
        // The family is declared whenever an address column carries a value, because 32 bits alone do not say
        // what they are.
        if (values.SourceAddress is null && values.DestinationAddress is null)
        {
            values.AddressFamily = null;
        }

        return values;
    }

    /// <summary>
    /// Applies the documented transform for a port. The admitted value itself is never rewritten; this is the
    /// derivation the source contract records for it (R1, measured on FX-TCP-001).
    /// </summary>
    private static ushort Port(long raw, SlotTransform transform) => transform switch
    {
        SlotTransform.NetworkOrderPort => BinaryPrimitives.ReverseEndianness((ushort)raw),
        _ => (ushort)raw,
    };

    private static uint Address(long raw, SlotTransform transform) => transform switch
    {
        SlotTransform.NetworkOrderIpv4Address => BinaryPrimitives.ReverseEndianness((uint)raw),
        _ => (uint)raw,
    };

    private sealed record ByteMeasurement(
        long? Value,
        ByteDomain? Domain,
        AccountingSide? Side,
        MeasurementUnit? Unit,
        FieldAvailability Availability);

    private sealed class SlotValues
    {
        public int? OwnerProcessId { get; set; }

        public AdmittedSlotPlan? ByteSlot { get; set; }

        public long? ByteValue { get; set; }

        public long? StatusCode { get; set; }

        public string? ResourceName { get; set; }

        public byte? AddressFamily { get; set; }

        public uint? SourceAddress { get; set; }

        public ushort? SourcePort { get; set; }

        public uint? DestinationAddress { get; set; }

        public ushort? DestinationPort { get; set; }
    }
}
