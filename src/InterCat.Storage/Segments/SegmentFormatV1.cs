using System.Globalization;

namespace InterCat.Storage;

/// <summary>
/// What a column's values are, on the wire. A logical type fixes the width of one value, so a reader can
/// map a fixed-width column without decoding it (§20.1). A code here is a contract: retiring one leaves a
/// gap and never reuses the number.
/// </summary>
public enum SegmentColumnType : byte
{
    Unsigned8 = 1,
    Unsigned16 = 2,
    Unsigned32 = 3,
    Unsigned64 = 4,
    Signed32 = 5,
    Signed64 = 6,

    /// <summary>A 16-byte identifier, stored big-endian so its bytes read as the identifier is printed.</summary>
    Guid16 = 7,

    /// <summary>
    /// Text. Its physical encoding is chosen per segment: a dictionary code when the segment's distinct
    /// values fit the dictionary budget, and a variable-chunk reference when they do not (§10.2).
    /// </summary>
    Text = 8,
}

/// <summary>
/// How a column's values are physically stored. A dictionary-coded column is still fixed width; what it
/// stores is a code into a published dictionary rather than the value.
/// </summary>
public enum SegmentColumnEncoding : byte
{
    /// <summary>The values themselves, little-endian, fixed width, directly mappable.</summary>
    Plain = 1,

    /// <summary>A <see cref="uint"/> code per row into the dictionary this column names.</summary>
    Dictionary = 2,

    /// <summary>A <see cref="uint"/> offset and a <see cref="uint"/> length per row into the variable chunk.</summary>
    VariableReference = 3,
}

/// <summary>
/// The columns of the `observation-v1` table. Every code is a contract (R5): a reader that meets an
/// unknown code in a required column refuses the segment, and one in an optional column keeps the raw
/// code rather than guessing what it meant.
///
/// There is deliberately no resolved-identity column here. A process instance, a channel, a relation or a
/// derived metric is a versioned derivation over these facts and lives in its own dependency, because R1
/// forbids a writable observation column that a later correlation revision could edit behind a reader.
/// </summary>
public enum SegmentColumnId : ushort
{
    /// <summary>The raw-record locator: which stream of the capture delivered this record.</summary>
    RawStreamId = 1,

    /// <summary>The raw-record locator: the source epoch that stream was in.</summary>
    RawSourceEpoch = 2,

    /// <summary>The raw-record locator: the acquisition ordinal the journal assigned (I1, I7).</summary>
    RawRecordOrdinal = 3,

    /// <summary>
    /// Where the record sits in the journal this generation derives from, in stored order. Null when the
    /// row derives from evidence this session holds no journal for. Together with the locator above it
    /// survives compaction, because it is carried in the row rather than implied by the row's position.
    /// </summary>
    JournalRecordIndex = 4,

    /// <summary>High half of the deterministic fact key of §7.2. One record may yield several rows (I2).</summary>
    FactKeyHigh = 5,

    /// <summary>Low half of the deterministic fact key.</summary>
    FactKeyLow = 6,

    /// <summary>A code into the segment's schema dictionary: provider, event id, version and fingerprint.</summary>
    SchemaCode = 7,

    /// <summary>The descriptor's opcode, as the source delivered it.</summary>
    Opcode = 8,

    /// <summary>The source clock reading in its original encoding. Never replaced by a conversion (I8).</summary>
    NativeTicks = 9,

    /// <summary>
    /// The same instant on the capture's session-relative scale, or null when conversion quarantined it.
    /// It is a derivation beside the native reading, never instead of it (I8, ADR-006).
    /// </summary>
    SessionRelativeTicks = 10,

    /// <summary>The process id the event header carried. It is context, never promoted into an owner (§4.1).</summary>
    HeaderProcessId = 11,

    HeaderThreadId = 12,

    /// <summary>Which processor delivered the record. Recorded content, not consumer state (§18.4).</summary>
    ProcessorNumber = 13,

    /// <summary>The header activity id, or null when the source carried none. An absent id is not zero (§7.2).</summary>
    ActivityId = 14,

    RelatedActivityId = 15,

    Mechanism = 16,
    Layer = 17,
    ObservationKind = 18,
    Direction = 19,

    /// <summary>The owner the record's own payload names, or null when the descriptor exposes none.</summary>
    OwnerProcessId = 20,

    /// <summary>The bounded resource name the record carried, by dictionary code or variable reference.</summary>
    ResourceName = 21,

    /// <summary>A 16-byte identifier the descriptor carried, such as an RPC interface UUID.</summary>
    SourceIdentifier = 22,

    /// <summary>What the address columns mean: 4 for IPv4, 6 for IPv6. Null when no address was admitted.</summary>
    EndpointAddressFamily = 23,

    /// <summary>The endpoint the source names as the origin. Which side is local is derived, never assumed.</summary>
    SourceEndpointAddress = 24,

    SourceEndpointPort = 25,
    DestinationEndpointAddress = 26,
    DestinationEndpointPort = 27,

    /// <summary>The byte measurement, or null when the descriptor exposes no size (R3).</summary>
    ByteValue = 28,

    /// <summary>The one byte domain this row's measurement is in. Two domains are never summed (I6).</summary>
    ByteDomain = 29,

    /// <summary>Which side of the exchange this measurement is accounted to (§5.3).</summary>
    AccountingSide = 30,

    /// <summary>The unit of <see cref="ByteValue"/>, carried rather than assumed (R2).</summary>
    MeasurementUnit = 31,

    /// <summary>Why <see cref="ByteValue"/> is absent when it is. Never null: a null value has a reason (R2, R3).</summary>
    ByteAvailability = 32,

    /// <summary>The status the source reported, or null when it reported none.</summary>
    StatusCode = 33,

    /// <summary>Why <see cref="StatusCode"/> is absent when it is.</summary>
    StatusAvailability = 34,

    /// <summary>Quality, per dimension. The four are never combined into one number (P11, R2).</summary>
    AttributionQuality = 35,

    CorrelationQuality = 36,
    MeasurementQuality = 37,
    TimingQuality = 38,

    /// <summary>Row markers. See <see cref="SegmentRowMarkers"/>; a bit this reader does not know is refused.</summary>
    Markers = 39,

    /// <summary>
    /// `source-fields-v1`: which source field a row carries, as an `EN-SourceField` code. Column codes are one space
    /// across tables, so a code means the same thing in every table that carries it.
    /// </summary>
    SourceField = 40,

    /// <summary>`source-fields-v1`: the field's value as the source delivered it, or null when it delivered none.</summary>
    FieldValue = 41,

    /// <summary>`source-fields-v1`: the field's text, for a field whose value is text.</summary>
    FieldText = 42,

    /// <summary>`source-fields-v1`: why the value is absent when it is.</summary>
    FieldAvailability = 43,
}

/// <summary>
/// The bits of <see cref="SegmentColumnId.Markers"/>. A bit outside this set is refused, so a segment written
/// by a later minor version cannot have a flag silently read as false.
/// </summary>
[Flags]
public enum SegmentRowMarkers : ushort
{
    None = 0,

    /// <summary>The resource name is a prefix; its original length was longer than the capture bound (I21).</summary>
    ResourceNameTruncated = 1 << 0,

    /// <summary>The capture requested extended data for this record's provider, so an absence is a real absence.</summary>
    ExtendedDataRequested = 1 << 1,

    /// <summary>At least one extended item the source carried is not in the evidence behind this row (I13).</summary>
    ExtendedItemsOmitted = 1 << 2,

    /// <summary>The record's body was admitted under a policy that retained less than the source carried.</summary>
    BodyReducedByPolicy = 1 << 3,

    All = ResourceNameTruncated | ExtendedDataRequested | ExtendedItemsOmitted | BodyReducedByPolicy,
}

/// <summary>What a published dictionary holds. Its kind decides how an entry's text is parsed.</summary>
public enum SegmentDictionaryKind : ushort
{
    /// <summary>Arbitrary UTF-8 values, such as bounded resource names.</summary>
    Utf8Text = 1,

    /// <summary>
    /// Descriptor schemas as `provider|eventId|version|fingerprint`. The table travels with the segment,
    /// because the machine reading it may not have the recording machine's manifests (§18.3).
    /// </summary>
    Schema = 2,
}

/// <summary>
/// The numbers `segment-v1` is frozen at. Every bound here is a refusal rather than a truncation (R8): a
/// segment that would pass one is not written, and one that declares a value past one is not read.
/// </summary>
public static class SegmentFormatV1
{
    /// <summary>"ICATSEG1" in little-endian byte order.</summary>
    public const ulong Magic = 0x3147455354414349;

    /// <summary>"ICATDIC1" in little-endian byte order.</summary>
    public const ulong DictionaryMagic = 0x3143494474414349;

    public const ushort FormatMajor = 1;

    public const ushort FormatMinor = 0;

    public const int HeaderLength = 128;

    public const int DictionaryHeaderLength = 64;

    public const int ColumnEntryLength = 48;

    public const int TimeBlockEntryLength = 32;

    /// <summary>The digest over everything before it, appended to a segment and to a dictionary.</summary>
    public const int TrailerLength = 32;

    /// <summary>§20.1's staged-row target. A writer flushes a segment when it reaches this.</summary>
    public const int DefaultRowsPerSegment = 250_000;

    /// <summary>The hard bound on one segment's rows, independent of the tunable target above.</summary>
    public const int MaximumRowsPerSegment = 4_000_000;

    /// <summary>The hard bound on one segment file. A segment that would pass it is split, never truncated.</summary>
    public const int MaximumSegmentBytes = 256 * 1024 * 1024;

    /// <summary>§10.2's time-block granularity: how many rows one min/max metadata entry covers.</summary>
    public const int RowsPerTimeBlock = 8_192;

    /// <summary>The bound on one segment's time-block directory, which follows from the two above.</summary>
    public const int MaximumTimeBlocks = 512;

    /// <summary>The dictionary budget of §10.2. A column whose segment exceeds it falls back to a chunk.</summary>
    public const int MaximumDictionaryEntries = 65_536;

    public const int MaximumDictionaryBytes = 8 * 1024 * 1024;

    /// <summary>The bound on one text value, in UTF-8 bytes, in a dictionary or in a variable chunk.</summary>
    public const int MaximumTextBytes = 4 * 1024;

    public const int MaximumVariableChunkBytes = 64 * 1024 * 1024;

    /// <summary>Every column of `observation-v1`, in the order a segment's directory records them.</summary>
    public static IReadOnlyList<SegmentColumnSpec> ObservationColumns { get; } =
    [
        new(SegmentColumnId.RawStreamId, SegmentColumnType.Unsigned32, Nullable: false),
        new(SegmentColumnId.RawSourceEpoch, SegmentColumnType.Unsigned32, Nullable: false),
        new(SegmentColumnId.RawRecordOrdinal, SegmentColumnType.Unsigned64, Nullable: false),
        new(SegmentColumnId.JournalRecordIndex, SegmentColumnType.Unsigned64, Nullable: true),
        new(SegmentColumnId.FactKeyHigh, SegmentColumnType.Unsigned64, Nullable: false),
        new(SegmentColumnId.FactKeyLow, SegmentColumnType.Unsigned64, Nullable: false),
        new(SegmentColumnId.SchemaCode, SegmentColumnType.Unsigned32, Nullable: false),
        new(SegmentColumnId.Opcode, SegmentColumnType.Unsigned8, Nullable: false),
        new(SegmentColumnId.NativeTicks, SegmentColumnType.Signed64, Nullable: false),
        new(SegmentColumnId.SessionRelativeTicks, SegmentColumnType.Signed64, Nullable: true),
        new(SegmentColumnId.HeaderProcessId, SegmentColumnType.Signed32, Nullable: false),
        new(SegmentColumnId.HeaderThreadId, SegmentColumnType.Signed32, Nullable: false),
        new(SegmentColumnId.ProcessorNumber, SegmentColumnType.Unsigned16, Nullable: false),
        new(SegmentColumnId.ActivityId, SegmentColumnType.Guid16, Nullable: true),
        new(SegmentColumnId.RelatedActivityId, SegmentColumnType.Guid16, Nullable: true),
        new(SegmentColumnId.Mechanism, SegmentColumnType.Unsigned8, Nullable: false),
        new(SegmentColumnId.Layer, SegmentColumnType.Unsigned8, Nullable: false),
        new(SegmentColumnId.ObservationKind, SegmentColumnType.Unsigned8, Nullable: false),
        new(SegmentColumnId.Direction, SegmentColumnType.Unsigned8, Nullable: false),
        new(SegmentColumnId.OwnerProcessId, SegmentColumnType.Signed32, Nullable: true),
        new(SegmentColumnId.ResourceName, SegmentColumnType.Text, Nullable: true),
        new(SegmentColumnId.SourceIdentifier, SegmentColumnType.Guid16, Nullable: true),
        new(SegmentColumnId.EndpointAddressFamily, SegmentColumnType.Unsigned8, Nullable: true),
        new(SegmentColumnId.SourceEndpointAddress, SegmentColumnType.Unsigned32, Nullable: true),
        new(SegmentColumnId.SourceEndpointPort, SegmentColumnType.Unsigned16, Nullable: true),
        new(SegmentColumnId.DestinationEndpointAddress, SegmentColumnType.Unsigned32, Nullable: true),
        new(SegmentColumnId.DestinationEndpointPort, SegmentColumnType.Unsigned16, Nullable: true),
        new(SegmentColumnId.ByteValue, SegmentColumnType.Signed64, Nullable: true),
        new(SegmentColumnId.ByteDomain, SegmentColumnType.Unsigned8, Nullable: true),
        new(SegmentColumnId.AccountingSide, SegmentColumnType.Unsigned8, Nullable: true),
        new(SegmentColumnId.MeasurementUnit, SegmentColumnType.Unsigned8, Nullable: true),
        new(SegmentColumnId.ByteAvailability, SegmentColumnType.Unsigned8, Nullable: false),
        new(SegmentColumnId.StatusCode, SegmentColumnType.Signed64, Nullable: true),
        new(SegmentColumnId.StatusAvailability, SegmentColumnType.Unsigned8, Nullable: false),
        new(SegmentColumnId.AttributionQuality, SegmentColumnType.Unsigned8, Nullable: false),
        new(SegmentColumnId.CorrelationQuality, SegmentColumnType.Unsigned8, Nullable: false),
        new(SegmentColumnId.MeasurementQuality, SegmentColumnType.Unsigned8, Nullable: false),
        new(SegmentColumnId.TimingQuality, SegmentColumnType.Unsigned8, Nullable: false),
        new(SegmentColumnId.Markers, SegmentColumnType.Unsigned16, Nullable: false),
    ];

    /// <summary>
    /// Every column of `source-fields-v1`, in directory order: one row per (observation, source field) for the fields
    /// §7.3 names as source correlation and object fields that `observation-v1` has no column for.
    /// </summary>
    public static IReadOnlyList<SegmentColumnSpec> SourceFieldColumns { get; } =
    [
        new(SegmentColumnId.RawStreamId, SegmentColumnType.Unsigned32, Nullable: false),
        new(SegmentColumnId.RawSourceEpoch, SegmentColumnType.Unsigned32, Nullable: false),
        new(SegmentColumnId.RawRecordOrdinal, SegmentColumnType.Unsigned64, Nullable: false),
        new(SegmentColumnId.FactKeyHigh, SegmentColumnType.Unsigned64, Nullable: false),
        new(SegmentColumnId.FactKeyLow, SegmentColumnType.Unsigned64, Nullable: false),
        new(SegmentColumnId.NativeTicks, SegmentColumnType.Signed64, Nullable: false),
        new(SegmentColumnId.SourceField, SegmentColumnType.Unsigned16, Nullable: false),
        new(SegmentColumnId.FieldValue, SegmentColumnType.Signed64, Nullable: true),
        new(SegmentColumnId.FieldText, SegmentColumnType.Text, Nullable: true),
        new(SegmentColumnId.FieldAvailability, SegmentColumnType.Unsigned8, Nullable: false),
    ];

    /// <summary>The frozen column set of one table, in the order its segments' directories record them.</summary>
    public static IReadOnlyList<SegmentColumnSpec> ColumnsOf(SegmentTableId table) => table switch
    {
        SegmentTableId.ObservationV1 => ObservationColumns,
        SegmentTableId.SourceFieldsV1 => SourceFieldColumns,
        _ => throw new ArgumentOutOfRangeException(nameof(table), table, "This reader implements no such table."),
    };

    /// <summary>How wide one value of a plain fixed-width column is.</summary>
    public static int WidthOf(SegmentColumnType type) => type switch
    {
        SegmentColumnType.Unsigned8 => 1,
        SegmentColumnType.Unsigned16 => 2,
        SegmentColumnType.Unsigned32 or SegmentColumnType.Signed32 => 4,
        SegmentColumnType.Unsigned64 or SegmentColumnType.Signed64 => 8,
        SegmentColumnType.Guid16 => 16,

        // Text has no plain width: it is a code or an (offset, length) pair, and which one is a per-segment
        // decision recorded in the column directory.
        SegmentColumnType.Text => 8,
        _ => throw new ArgumentOutOfRangeException(
            nameof(type),
            type,
            "A segment column type this reader does not implement has no width."),
    };

    /// <summary>The name a generation publishes one segment under.</summary>
    public static string SegmentFileName(long generation, int ordinal) =>
        generation is < 1 or > 9_999_999_999
            ? throw new ArgumentOutOfRangeException(nameof(generation), generation, "A generation is 1..9,999,999,999.")
            : ordinal is < 0 or > 9_999
                ? throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "A generation publishes at most 10,000 segments.")
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"seg-{generation:D10}-{ordinal:D4}.icats");

    /// <summary>The name a generation publishes one `source-fields-v1` segment under.</summary>
    public static string FieldSegmentFileName(long generation, int ordinal) =>
        generation is < 1 or > 9_999_999_999
            ? throw new ArgumentOutOfRangeException(nameof(generation), generation, "A generation is 1..9,999,999,999.")
            : ordinal is < 0 or > 9_999
                ? throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "A generation publishes at most 10,000 segments.")
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"fld-{generation:D10}-{ordinal:D4}.icats");

    /// <summary>The name a generation publishes one dictionary under.</summary>
    public static string DictionaryFileName(long generation, int ordinal) =>
        generation is < 1 or > 9_999_999_999
            ? throw new ArgumentOutOfRangeException(nameof(generation), generation, "A generation is 1..9,999,999,999.")
            : ordinal is < 0 or > 9_999
                ? throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "A generation publishes at most 10,000 dictionaries.")
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"dict-{generation:D10}-{ordinal:D4}.icatd");

    /// <summary>The name a generation publishes the journal it derives from under.</summary>
    public static string JournalFileName(long generation) =>
        generation is < 1 or > 9_999_999_999
            ? throw new ArgumentOutOfRangeException(nameof(generation), generation, "A generation is 1..9,999,999,999.")
            : string.Create(CultureInfo.InvariantCulture, $"journal-{generation:D10}.icatj");

    /// <summary>The canonical text one schema dictionary entry holds.</summary>
    public static string SchemaEntry(Guid providerId, ushort eventId, byte version, string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{providerId:D}|{eventId}|{version}|{fingerprint}");
    }

    /// <summary>Reads one schema dictionary entry, refusing anything it cannot read exactly.</summary>
    public static (Guid ProviderId, ushort EventId, byte Version, string Fingerprint) ParseSchemaEntry(string entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string[] parts = entry.Split('|');
        return parts.Length == 4
            && Guid.TryParseExact(parts[0], "D", out Guid provider)
            && ushort.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out ushort eventId)
            && byte.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out byte version)
            && parts[3].Length > 0
                ? (provider, eventId, version, parts[3])
                : throw new InvalidDataException(
                    $"'{entry}' is not a segment-v1 schema entry. A schema table is refused rather than read "
                    + "at a guessed shape.");
    }
}

/// <summary>One column of a table, as the frozen schema declares it before any segment is written.</summary>
public sealed record SegmentColumnSpec(SegmentColumnId Id, SegmentColumnType Type, bool Nullable);
