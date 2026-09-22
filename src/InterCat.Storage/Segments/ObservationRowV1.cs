using InterCat.Domain;

namespace InterCat.Storage;

/// <summary>
/// One row of the `observation-v1` table: an immutable normalized fact, derived from exactly one admitted
/// record and carrying the source's own attribution. Every optional value here is a real option — a null
/// means the source exposed nothing and is paired with the availability reason that says so (R2, R3).
/// </summary>
/// <remarks>
/// A row is a transient shape. The writer encodes it into column buffers as it arrives and does not retain
/// it, and the reader materializes one on request; neither holds a collection of these, which is what keeps
/// a derivation's memory the size of its columns rather than the size of its rows (R8).
///
/// Nothing in a row is a resolved identity. A process instance, a channel, a relation or a derived metric
/// is a versioned derivation over these facts in its own dependency, because R1 forbids an observation
/// column a later revision could rewrite behind a reader.
/// </remarks>
public sealed record ObservationRowV1
{
    /// <summary>Which stream of the capture delivered the record behind this row.</summary>
    public required uint RawStreamId { get; init; }

    public required uint RawSourceEpoch { get; init; }

    /// <summary>The acquisition ordinal the journal assigned. Acquisition order is not display order (I7).</summary>
    public required ulong RawRecordOrdinal { get; init; }

    /// <summary>Where that record sits in the journal this row derives from, or null when there is none.</summary>
    public ulong? JournalRecordIndex { get; init; }

    /// <summary>The deterministic fact key of §7.2. One record may yield several rows, each with its own (I2).</summary>
    public required FactKey FactKey { get; init; }

    /// <summary>The provider of the descriptor this row was admitted against.</summary>
    public required Guid ProviderId { get; init; }

    public required ushort EventId { get; init; }

    /// <summary>The descriptor version. A descriptor's shape is versioned, so the version is part of it.</summary>
    public required byte DescriptorVersion { get; init; }

    /// <summary>
    /// The identity of the schema the recording machine saw. A segment publishes provider, event, version
    /// and fingerprint as one sorted dictionary entry, so the reading machine needs neither the recording
    /// machine's manifests nor its interning order (§18.3).
    /// </summary>
    public required string SchemaFingerprint { get; init; }

    public required byte Opcode { get; init; }

    /// <summary>The source clock reading in its original encoding (I8).</summary>
    public required long NativeTicks { get; init; }

    /// <summary>The same instant session-relative, or null when the conversion was quarantined (ADR-006).</summary>
    public long? SessionRelativeTicks { get; init; }

    /// <summary>The event header's process. Context only; it is never promoted into the owner (§4.1).</summary>
    public required int HeaderProcessId { get; init; }

    public required int HeaderThreadId { get; init; }

    public required ushort ProcessorNumber { get; init; }

    /// <summary>The header activity id, or null when the source carried none. An absent id is not zero.</summary>
    public Guid? ActivityId { get; init; }

    public Guid? RelatedActivityId { get; init; }

    public required Mechanism Mechanism { get; init; }

    public required ObservationLayer Layer { get; init; }

    public required ObservationKind Kind { get; init; }

    public required Direction Direction { get; init; }

    /// <summary>The owner the record's payload names, or null when the descriptor exposes none.</summary>
    public int? OwnerProcessId { get; init; }

    /// <summary>The bounded resource name the record carried, as delivered.</summary>
    public string? ResourceName { get; init; }

    /// <summary>A 16-byte identifier the descriptor carried, such as an RPC interface UUID.</summary>
    public Guid? SourceIdentifier { get; init; }

    /// <summary>4 for IPv4, 6 for IPv6, null when no address was admitted.</summary>
    public byte? EndpointAddressFamily { get; init; }

    /// <summary>The endpoint the source names as the origin, exactly as delivered after its documented transform.</summary>
    public uint? SourceEndpointAddress { get; init; }

    public ushort? SourceEndpointPort { get; init; }

    public uint? DestinationEndpointAddress { get; init; }

    public ushort? DestinationEndpointPort { get; init; }

    /// <summary>The byte measurement, or null when the descriptor exposes no size (R3).</summary>
    public long? ByteValue { get; init; }

    /// <summary>Exactly one byte domain. Two are never summed or ranked together (I6, P3).</summary>
    public ByteDomain? ByteDomain { get; init; }

    public AccountingSide? AccountingSide { get; init; }

    public MeasurementUnit? MeasurementUnit { get; init; }

    /// <summary>Why the byte measurement is absent when it is. `Present` when it is there (R2).</summary>
    public required FieldAvailability ByteAvailability { get; init; }

    public long? StatusCode { get; init; }

    public required FieldAvailability StatusAvailability { get; init; }

    /// <summary>Quality per dimension. The four are never collapsed into one number (P11).</summary>
    public required QualityLevel AttributionQuality { get; init; }

    public required QualityLevel CorrelationQuality { get; init; }

    public required QualityLevel MeasurementQuality { get; init; }

    public required QualityLevel TimingQuality { get; init; }

    /// <summary>What is true of this row beyond its values. See <see cref="SegmentRowMarkers"/>.</summary>
    public SegmentRowMarkers Markers { get; init; }

    /// <summary>The raw-record identity this row derives from, given the capture the segment names.</summary>
    public RawRecordId RawRecordIdIn(CaptureId captureId) =>
        new(captureId, RawStreamId, RawSourceEpoch, RawRecordOrdinal);

    /// <summary>The observation identity of §7.2, given the capture and derivation the segment names.</summary>
    public ObservationId ObservationIdIn(CaptureId captureId, NormalizerContractVersion derivation) =>
        new(RawRecordIdIn(captureId), derivation, FactKey);

    /// <summary>
    /// Returns the reason this row could not be written, or null when it can. The checks are the ones a
    /// reader would otherwise have to trust: that a present measurement is labelled, that an absent one has
    /// a reason, and that no enumeration carries a code §23 does not define.
    /// </summary>
    public string? Validate()
    {
        if (!Enum.IsDefined(Mechanism) || !Enum.IsDefined(Layer) || !Enum.IsDefined(Kind) || !Enum.IsDefined(Direction))
        {
            return "A row carries mechanism, layer, kind and direction codes that §23 defines.";
        }

        if (!Enum.IsDefined(ByteAvailability) || !Enum.IsDefined(StatusAvailability))
        {
            return "A row's availability reasons are §23 codes.";
        }

        if (!Enum.IsDefined(AttributionQuality)
            || !Enum.IsDefined(CorrelationQuality)
            || !Enum.IsDefined(MeasurementQuality)
            || !Enum.IsDefined(TimingQuality))
        {
            return "A row carries a quality level per dimension, each a §23 code.";
        }

        if (string.IsNullOrWhiteSpace(SchemaFingerprint))
        {
            return "A row names the schema identity it was admitted against.";
        }

        if ((Markers & ~SegmentRowMarkers.All) != 0)
        {
            return $"A row carries marker bits this format does not define: 0x{(ushort)Markers:x4}.";
        }

        // The measurement slot and the measurement are separate facts. A descriptor that declares a byte
        // field labels its domain, side and unit whether or not a given record supplied a value, because
        // that is what makes an unknown countable against a defined denominator (R2, R3, §5).
        bool slotDeclared = ByteDomain is not null || AccountingSide is not null || MeasurementUnit is not null;
        if (ByteValue is { } bytes)
        {
            if (bytes < 0)
            {
                return "A byte measurement is not negative. A source that reports one is undecodable, not a debt.";
            }

            if (ByteDomain is null || AccountingSide is null || MeasurementUnit is null)
            {
                return "A byte measurement carries its domain, accounting side and unit, or it is not a "
                    + "measurement (R2, §5.3).";
            }

            if (ByteAvailability != FieldAvailability.Present)
            {
                return $"A row carries a byte measurement and also reports it as {ByteAvailability}.";
            }
        }
        else if (ByteAvailability == FieldAvailability.Present)
        {
            return "A row with no byte measurement states why it has none; `Present` is not a reason (R3).";
        }
        else if (ByteAvailability == FieldAvailability.NotApplicable)
        {
            if (slotDeclared)
            {
                return "A row whose descriptor declares no byte field carries no domain, side or unit either; "
                    + "labelling a slot that does not exist would put it in a denominator (§5).";
            }
        }
        else if (ByteDomain is null || AccountingSide is null || MeasurementUnit is null)
        {
            return $"A row reports its byte measurement as {ByteAvailability}, which is an unknown value in a "
                + "declared slot, so it still carries that slot's domain, side and unit (R2, R3).";
        }

        if (ByteDomain is { } domain && !Enum.IsDefined(domain))
        {
            return "A byte domain is a §23 code.";
        }

        if (AccountingSide is { } side && !Enum.IsDefined(side))
        {
            return "An accounting side is a §23 code.";
        }

        if (MeasurementUnit is { } unit && !Enum.IsDefined(unit))
        {
            return "A measurement unit is a §23 code.";
        }

        if (StatusCode is not null && StatusAvailability != FieldAvailability.Present)
        {
            return $"A row carries a status code and also reports it as {StatusAvailability}.";
        }

        if (StatusCode is null && StatusAvailability == FieldAvailability.Present)
        {
            return "A row with no status states why it has none.";
        }

        if (ResourceName is { Length: > 0 } name)
        {
            int utf8 = System.Text.Encoding.UTF8.GetByteCount(name);
            if (utf8 > SegmentFormatV1.MaximumTextBytes)
            {
                return $"A resource name is at most {SegmentFormatV1.MaximumTextBytes} UTF-8 bytes; this one "
                    + $"is {utf8}.";
            }
        }
        else if (ResourceName is not null)
        {
            // An empty name and no name are different facts. The source either named nothing, which is a
            // null, or named the empty string, which no admitted descriptor produces; either way, storing
            // an empty string as if it were a name would make a nameless record look named.
            return "A resource name is null when the source named nothing, never the empty string.";
        }
        else if ((Markers & SegmentRowMarkers.ResourceNameTruncated) != 0)
        {
            return "A row with no resource name cannot report that name as truncated.";
        }

        if (EndpointAddressFamily is { } family && family is not (4 or 6))
        {
            return $"An endpoint address family is 4 or 6; this row declares {family}.";
        }

        return (SourceEndpointAddress is not null || DestinationEndpointAddress is not null)
            && EndpointAddressFamily is null
                ? "A row carrying an endpoint address states the family those 32 bits are in."
                : null;
    }
}
