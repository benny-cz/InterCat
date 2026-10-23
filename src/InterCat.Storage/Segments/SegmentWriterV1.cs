using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using InterCat.Domain;

namespace InterCat.Storage;

/// <summary>Which table a segment holds. The container is shared; the column set is not.</summary>
public enum SegmentTableId : uint
{
    /// <summary>The normalized source observations of §7.3, as `observation-v1`.</summary>
    ObservationV1 = 1,

    /// <summary>§7.3's source correlation and object fields that `observation-v1` has no column for.</summary>
    SourceFieldsV1 = 2,

    /// <summary>
    /// `observation-v1` with two IPv6 endpoint address columns, for a segment one of whose rows has an IPv6 address
    /// (revision 172). Its rows are observations as `observation-v1`'s are.
    /// </summary>
    ObservationV2 = 3,
}

/// <summary>What a segment is a segment of: the capture, the clock its readings are on, and the derivation.</summary>
public sealed record SegmentIdentityV1
{
    public required CaptureId CaptureId { get; init; }

    /// <summary>The clock every <see cref="ObservationRowV1.NativeTicks"/> in the segment is on (I8).</summary>
    public required ClockId ClockId { get; init; }

    public required TimestampEncoding TimestampEncoding { get; init; }

    /// <summary>The normalizer contract version the rows were derived under (§24, R20).</summary>
    public required NormalizerContractVersion Derivation { get; init; }

    /// <summary>
    /// A deterministic segment identity. It is derived from the capture, the derivation and the segment's
    /// ordinal rather than minted, so rebuilding a generation from the same evidence names the same segment.
    /// </summary>
    public Guid SegmentIdFor(int ordinal, int rowCount, long minNativeTicks, long maxNativeTicks) =>
        StableSegmentId.Derive(string.Create(
            CultureInfo.InvariantCulture,
            $"InterCat.Segment.v1|{CaptureId}|{ClockId}|{Derivation}|{ordinal}|{rowCount}|{minNativeTicks}|{maxNativeTicks}"));

    /// <summary>
    /// The identity of a segment of a table other than `observation-v1`. The table is part of the derivation, so a
    /// fields segment and an observation segment with the same ordinal and extent never share an identity.
    /// </summary>
    public Guid SegmentIdFor(SegmentTableId table, int ordinal, int rowCount, long minNativeTicks, long maxNativeTicks) =>
        table == SegmentTableId.ObservationV1
            ? SegmentIdFor(ordinal, rowCount, minNativeTicks, maxNativeTicks)
            : StableSegmentId.Derive(string.Create(
                CultureInfo.InvariantCulture,
                $"InterCat.Segment.v1.table{(uint)table}|{CaptureId}|{ClockId}|{Derivation}|{ordinal}|{rowCount}|{minNativeTicks}|{maxNativeTicks}"));
}

/// <summary>One finished segment: its bytes, the dictionaries it references, and what it holds.</summary>
public sealed record SegmentBuildResult(
    byte[] Segment,
    IReadOnlyList<SegmentDictionaryV1> Dictionaries,
    Guid SegmentId,
    int RowCount,
    long MinNativeTicks,
    long MaxNativeTicks,
    IReadOnlyList<SegmentColumnDescriptor> Columns);

/// <summary>
/// Builds one immutable observation segment: `observation-v1`, or `observation-v2` when a row has an IPv6 address.
/// Rows are encoded into their columns as they arrive and are not retained, so a derivation's memory is the size of
/// its columns rather than the size of its rows (R8); the sort that makes the segment ordered is a permutation of those
/// columns at the end.
/// </summary>
public sealed class SegmentWriterV1
{
    private readonly SegmentTableBuilder builder;

    public SegmentWriterV1(SegmentIdentityV1 identity, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        builder = new(
            identity,
            ordinal,
            SegmentTableId.ObservationV2,
            SegmentFormatV1.ObservationV2Columns,
            textColumn: SegmentColumnId.ResourceName,
            schemaColumn: SegmentColumnId.SchemaCode,
            narrower: (SegmentTableId.ObservationV1, SegmentFormatV1.ObservationColumns.Count));
    }

    public int RowCount => builder.RowCount;

    /// <summary>How many bytes this segment's columns will occupy, so a caller can flush on §20.1's size.</summary>
    public long StagedBytes => builder.StagedBytes;

    /// <summary>Encodes one row into the columns. The row itself is not retained.</summary>
    public void Add(ObservationRowV1 row)
    {
        ArgumentNullException.ThrowIfNull(row);
        string? problem = row.Validate();
        if (problem is not null)
        {
            throw new ArgumentException(problem, nameof(row));
        }

        int at = builder.BeginRow();
        builder.SetUInt32(SegmentColumnId.RawStreamId, at, row.RawStreamId);
        builder.SetUInt32(SegmentColumnId.RawSourceEpoch, at, row.RawSourceEpoch);
        builder.SetUInt64(SegmentColumnId.RawRecordOrdinal, at, row.RawRecordOrdinal);
        builder.SetNullableUInt64(SegmentColumnId.JournalRecordIndex, at, row.JournalRecordIndex);
        builder.SetUInt64(SegmentColumnId.FactKeyHigh, at, row.FactKey.High);
        builder.SetUInt64(SegmentColumnId.FactKeyLow, at, row.FactKey.Low);
        builder.SetUInt32(
            SegmentColumnId.SchemaCode,
            at,
            builder.InternSchema(row.ProviderId, row.EventId, row.DescriptorVersion, row.SchemaFingerprint));
        builder.SetUInt8(SegmentColumnId.Opcode, at, row.Opcode);
        builder.SetInt64(SegmentColumnId.NativeTicks, at, row.NativeTicks);
        builder.SetNullableInt64(SegmentColumnId.SessionRelativeTicks, at, row.SessionRelativeTicks);
        builder.SetInt32(SegmentColumnId.HeaderProcessId, at, row.HeaderProcessId);
        builder.SetInt32(SegmentColumnId.HeaderThreadId, at, row.HeaderThreadId);
        builder.SetUInt16(SegmentColumnId.ProcessorNumber, at, row.ProcessorNumber);
        builder.SetNullableGuid(SegmentColumnId.ActivityId, at, row.ActivityId);
        builder.SetNullableGuid(SegmentColumnId.RelatedActivityId, at, row.RelatedActivityId);
        builder.SetUInt8(SegmentColumnId.Mechanism, at, checked((byte)row.Mechanism));
        builder.SetUInt8(SegmentColumnId.Layer, at, checked((byte)row.Layer));
        builder.SetUInt8(SegmentColumnId.ObservationKind, at, checked((byte)row.Kind));
        builder.SetUInt8(SegmentColumnId.Direction, at, checked((byte)row.Direction));
        builder.SetNullableInt32(SegmentColumnId.OwnerProcessId, at, row.OwnerProcessId);
        builder.SetText(SegmentColumnId.ResourceName, at, row.ResourceName);
        builder.SetNullableGuid(SegmentColumnId.SourceIdentifier, at, row.SourceIdentifier);
        builder.SetNullableUInt8(SegmentColumnId.EndpointAddressFamily, at, row.EndpointAddressFamily);
        builder.SetNullableUInt32(SegmentColumnId.SourceEndpointAddress, at, row.SourceEndpointAddress);
        builder.SetNullableUInt16(SegmentColumnId.SourceEndpointPort, at, row.SourceEndpointPort);
        builder.SetNullableUInt32(SegmentColumnId.DestinationEndpointAddress, at, row.DestinationEndpointAddress);
        builder.SetNullableUInt16(SegmentColumnId.DestinationEndpointPort, at, row.DestinationEndpointPort);
        builder.SetNullableInt64(SegmentColumnId.ByteValue, at, row.ByteValue);
        builder.SetNullableUInt8(SegmentColumnId.ByteDomain, at, row.ByteDomain is { } domain ? checked((byte)domain) : null);
        builder.SetNullableUInt8(SegmentColumnId.AccountingSide, at, row.AccountingSide is { } side ? checked((byte)side) : null);
        builder.SetNullableUInt8(SegmentColumnId.MeasurementUnit, at, row.MeasurementUnit is { } unit ? checked((byte)unit) : null);
        builder.SetUInt8(SegmentColumnId.ByteAvailability, at, checked((byte)row.ByteAvailability));
        builder.SetNullableInt64(SegmentColumnId.StatusCode, at, row.StatusCode);
        builder.SetUInt8(SegmentColumnId.StatusAvailability, at, checked((byte)row.StatusAvailability));
        builder.SetUInt8(SegmentColumnId.AttributionQuality, at, checked((byte)row.AttributionQuality));
        builder.SetUInt8(SegmentColumnId.CorrelationQuality, at, checked((byte)row.CorrelationQuality));
        builder.SetUInt8(SegmentColumnId.MeasurementQuality, at, checked((byte)row.MeasurementQuality));
        builder.SetUInt8(SegmentColumnId.TimingQuality, at, checked((byte)row.TimingQuality));
        builder.SetUInt16(SegmentColumnId.Markers, at, (ushort)row.Markers);
        builder.SetNullableAddress128(SegmentColumnId.SourceEndpointAddressV6, at, row.SourceEndpointAddressV6);
        builder.SetNullableAddress128(SegmentColumnId.DestinationEndpointAddressV6, at, row.DestinationEndpointAddressV6);
        builder.EndRow(new(row.NativeTicks, row.RawStreamId, row.RawSourceEpoch, row.RawRecordOrdinal, row.FactKey, 0));
    }

    /// <summary>
    /// Copies <paramref name="count"/> rows of a published observation segment, from <paramref name="first"/>, into the
    /// columns as <see cref="Add"/> stages the rows the segment's reader materializes, without materializing them: a
    /// compaction's copy (§20.1, R11). The segment is the same once built.
    /// </summary>
    public void AddRows(SegmentReaderV1 source, int first, int count) => builder.CopyRows(source, first, count, discriminator: null);

    /// <summary>
    /// How many rows of <paramref name="source"/> from <paramref name="first"/> can be added before this segment holds
    /// <paramref name="rowBound"/> rows or stages <paramref name="byteBound"/> bytes: at least one, at most
    /// <paramref name="available"/>.
    /// </summary>
    public int RowsUnder(SegmentReaderV1 source, int first, int available, int rowBound, long byteBound) =>
        builder.RowsUnder(source, first, available, rowBound, byteBound);

    /// <summary>
    /// Writes the segment. Rows are sorted into the canonical order — time, then the raw locator, then the
    /// fact key — which is total, so the file is a function of its row set rather than of arrival order.
    /// </summary>
    public SegmentBuildResult Build(ushort firstDictionaryId = 1) => builder.Build(firstDictionaryId);
}

/// <summary>
/// Builds one immutable `source-fields-v1` segment: the source correlation and object fields of the observations
/// its rows name, one row per (observation, field). The container, the order, the bitmaps and the refusals are the
/// ones every segment has; only the column set differs.
/// </summary>
public sealed class SourceFieldWriterV1
{
    private readonly SegmentTableBuilder builder;

    public SourceFieldWriterV1(SegmentIdentityV1 identity, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        builder = new(
            identity,
            ordinal,
            SegmentTableId.SourceFieldsV1,
            SegmentFormatV1.SourceFieldColumns,
            textColumn: SegmentColumnId.FieldText,
            schemaColumn: null);
    }

    public int RowCount => builder.RowCount;

    public long StagedBytes => builder.StagedBytes;

    public void Add(SourceFieldRowV1 row)
    {
        ArgumentNullException.ThrowIfNull(row);
        string? problem = row.Validate();
        if (problem is not null)
        {
            throw new ArgumentException(problem, nameof(row));
        }

        int at = builder.BeginRow();
        builder.SetUInt32(SegmentColumnId.RawStreamId, at, row.RawStreamId);
        builder.SetUInt32(SegmentColumnId.RawSourceEpoch, at, row.RawSourceEpoch);
        builder.SetUInt64(SegmentColumnId.RawRecordOrdinal, at, row.RawRecordOrdinal);
        builder.SetUInt64(SegmentColumnId.FactKeyHigh, at, row.FactKey.High);
        builder.SetUInt64(SegmentColumnId.FactKeyLow, at, row.FactKey.Low);
        builder.SetInt64(SegmentColumnId.NativeTicks, at, row.NativeTicks);
        builder.SetUInt16(SegmentColumnId.SourceField, at, (ushort)row.Field);
        builder.SetNullableInt64(SegmentColumnId.FieldValue, at, row.Value);
        builder.SetText(SegmentColumnId.FieldText, at, row.Text);
        builder.SetUInt8(SegmentColumnId.FieldAvailability, at, checked((byte)row.Availability));
        builder.EndRow(new(row.NativeTicks, row.RawStreamId, row.RawSourceEpoch, row.RawRecordOrdinal, row.FactKey, (ushort)row.Field));
    }

    /// <summary>
    /// Copies <paramref name="count"/> rows of a published `source-fields-v1` segment, from <paramref name="first"/>, as
    /// <see cref="Add"/> stages the rows its reader materializes, without materializing them (§20.1, R11).
    /// </summary>
    public void AddRows(SegmentReaderV1 source, int first, int count) =>
        builder.CopyRows(source, first, count, discriminator: SegmentColumnId.SourceField);

    /// <inheritdoc cref="SegmentWriterV1.RowsUnder"/>
    public int RowsUnder(SegmentReaderV1 source, int first, int available, int rowBound, long byteBound) =>
        builder.RowsUnder(source, first, available, rowBound, byteBound);

    /// <summary>
    /// Writes the segment in canonical order — time, raw locator, fact key, then field — which is total because one
    /// observation carries each field at most once.
    /// </summary>
    public SegmentBuildResult Build(ushort firstDictionaryId = 1) => builder.Build(firstDictionaryId);
}

/// <summary>
/// The container mechanics every table shares: column buffers that grow with the rows, one optional text column with
/// §10.2's dictionary budget and variable-chunk fallback, one optional schema column interned into a schema
/// dictionary, the canonical sort, time blocks, the directory, the header and the trailing digest.
/// </summary>
internal sealed class SegmentTableBuilder
{
    private readonly SegmentIdentityV1 identity;
    private readonly int ordinal;
    private SegmentTableId table;
    private IReadOnlyList<SegmentColumnSpec> columns;

    /// <summary>
    /// The table this one extends and how many of its leading columns are that table's: a segment none of whose rows
    /// used a column past them is written as that table, byte for byte (`observation-v2` over `observation-v1`).
    /// </summary>
    private readonly (SegmentTableId Table, int Columns)? narrower;
    private readonly SegmentColumnId? textColumn;
    private readonly SegmentColumnId? schemaColumn;
    private readonly Dictionary<SegmentColumnId, int> positions;
    private readonly byte[][] values;
    private readonly bool[][] presence;
    private readonly List<string?> texts = [];
    private readonly List<SortKey> keys = [];
    private readonly Dictionary<(Guid Provider, ushort EventId, byte Version), (string Fingerprint, uint Local)> schemas = [];
    private readonly List<string> schemaEntries = [];
    private readonly StagedLayout wideLayout;
    private readonly StagedLayout? narrowLayout;
    private long stagedTextBytes;
    private int rowCount;

    /// <summary>Whether a row has a value in a column the narrower table lacks, so the segment is this table's.</summary>
    private bool widened;

    public SegmentTableBuilder(
        SegmentIdentityV1 identity,
        int ordinal,
        SegmentTableId table,
        IReadOnlyList<SegmentColumnSpec> columns,
        SegmentColumnId? textColumn,
        SegmentColumnId? schemaColumn,
        (SegmentTableId Table, int Columns)? narrower = null)
    {
        this.identity = identity;
        this.ordinal = ordinal;
        this.table = table;
        this.columns = columns;
        this.textColumn = textColumn;
        this.schemaColumn = schemaColumn;
        this.narrower = narrower;
        positions = [];
        for (int index = 0; index < columns.Count; index++)
        {
            positions[columns[index].Id] = index;
        }

        values = new byte[columns.Count][];
        presence = new bool[columns.Count][];
        for (int index = 0; index < columns.Count; index++)
        {
            values[index] = [];
            presence[index] = [];
        }

        wideLayout = StagedLayout.Of(columns);
        narrowLayout = narrower is { } narrow ? StagedLayout.Of([.. columns.Take(narrow.Columns)]) : null;
    }

    public int RowCount => rowCount;

    /// <summary>
    /// The bytes the segment would take if it were built now. Until a row uses a column the narrower table lacks, that is
    /// the narrower table's layout, so a segment of IPv4 records flushes where it always did.
    /// </summary>
    public long StagedBytes => StagedBytesOf(rowCount, stagedTextBytes, widened);

    /// <summary>What <see cref="StagedBytes"/> would be with these rows, text bytes and widening staged.</summary>
    private long StagedBytesOf(int rows, long textBytes, bool wide)
    {
        StagedLayout layout = wide || narrowLayout is not { } narrow ? wideLayout : narrow;
        return layout.FixedBytes + ((long)rows * layout.WidthPerRow)
            + ((long)layout.NullableColumns * ((rows + 7) / 8)) + textBytes;
    }

    private int TimeBlockCount => Math.Max(1, (rowCount + SegmentFormatV1.RowsPerTimeBlock - 1) / SegmentFormatV1.RowsPerTimeBlock);

    /// <summary>Starts one row and returns its position. A full segment refuses rather than writing past its bound.</summary>
    public int BeginRow() =>
        rowCount == SegmentFormatV1.MaximumRowsPerSegment
            ? throw new InvalidOperationException(
                $"A segment holds at most {SegmentFormatV1.MaximumRowsPerSegment} rows. This one is full and "
                + "the derivation flushes it rather than writing past the bound.")
            : rowCount;

    /// <summary>Finishes the row begun last, with its place in canonical order.</summary>
    public void EndRow(SortKey key)
    {
        keys.Add(key);
        rowCount++;
    }

    /// <summary>
    /// Interns a descriptor and returns the local code the schema column stores until <c>Build</c> remaps it onto
    /// the published dictionary. A descriptor's canonical text is built once per distinct descriptor, never per row.
    /// </summary>
    public uint InternSchema(Guid providerId, ushort eventId, byte version, string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        (Guid, ushort, byte) key = (providerId, eventId, version);
        if (schemas.TryGetValue(key, out (string Fingerprint, uint Local) existing))
        {
            return existing.Fingerprint.Equals(fingerprint, StringComparison.Ordinal)
                ? existing.Local
                : throw new InvalidOperationException(
                    $"Descriptor {providerId:D}/{eventId}/v{version} already carries a different schema "
                    + "fingerprint in this segment. A descriptor cannot change shape inside one derivation without "
                    + "a new identity.");
        }

        var local = (uint)schemaEntries.Count;
        schemaEntries.Add(SegmentFormatV1.SchemaEntry(providerId, eventId, version, fingerprint));
        schemas.Add(key, (fingerprint, local));
        return local;
    }

    public SegmentBuildResult Build(ushort firstDictionaryId)
    {
        if (rowCount == 0)
        {
            throw new InvalidOperationException(
                "An empty segment is not published. Nothing observed is coverage, which the coverage ledger "
                + "states; a zero-row segment would let absence be read as data (R21, P1).");
        }

        // A table whose own columns no row used is written as the table it extends: its layout, its identity, its bytes.
        if (narrower is { } narrow && !widened)
        {
            (table, columns) = (narrow.Table, [.. columns.Take(narrow.Columns)]);
        }

        // The rows' places sorted with the rows: sorted[row] is the place of the row order[row] staged.
        int[] order = new int[rowCount];
        for (int index = 0; index < rowCount; index++)
        {
            order[index] = index;
        }

        SortKey[] sorted = [.. keys];
        sorted.AsSpan().Sort(order.AsSpan(), default(SortKeyOrder));
        for (int index = 1; index < rowCount; index++)
        {
            if (SortKey.Compare(sorted[index - 1], sorted[index]) == 0)
            {
                throw new InvalidOperationException(
                    table is SegmentTableId.ObservationV1 or SegmentTableId.ObservationV2
                        ? "Two rows of this segment share one observation identity. A segment is refused rather "
                            + "than published with a fact that would be counted twice (I2, I5)."
                        : "Two rows of this segment carry the same field of one observation. A segment is refused "
                            + "rather than published with a field that says two different things (I2).");
            }
        }

        ushort nextDictionary = firstDictionaryId;
        SegmentDictionaryV1? schemaDictionary = null;
        if (schemaColumn is not null)
        {
            schemaDictionary = SegmentDictionaryV1.Create(nextDictionary++, SegmentDictionaryKind.Schema, schemaEntries);
        }

        SegmentDictionaryV1? textDictionary = textColumn is null ? null : TryBuildTextDictionary(nextDictionary);
        List<SegmentDictionaryV1> dictionaries = [];
        if (schemaDictionary is not null)
        {
            dictionaries.Add(schemaDictionary);
        }

        if (textDictionary is not null)
        {
            dictionaries.Add(textDictionary);
        }

        var plan = new List<SegmentColumnDescriptor>(columns.Count);
        int nullBitmapBytes = (rowCount + 7) / 8;
        int cursor = Align(
            SegmentFormatV1.HeaderLength
            + (columns.Count * SegmentFormatV1.ColumnEntryLength)
            + (TimeBlockCount * SegmentFormatV1.TimeBlockEntryLength));

        foreach (SegmentColumnSpec column in columns)
        {
            SegmentColumnEncoding encoding = column.Id == textColumn
                ? textDictionary is null
                    ? SegmentColumnEncoding.VariableReference
                    : SegmentColumnEncoding.Dictionary
                : column.Id == schemaColumn
                    ? SegmentColumnEncoding.Dictionary
                    : SegmentColumnEncoding.Plain;
            ushort dictionaryId = column.Id == schemaColumn
                ? schemaDictionary!.DictionaryId
                : column.Id == textColumn && textDictionary is not null
                    ? textDictionary.DictionaryId
                    : (ushort)0;
            int width = encoding == SegmentColumnEncoding.VariableReference
                ? 2 * sizeof(uint)
                : encoding == SegmentColumnEncoding.Dictionary
                    ? sizeof(uint)
                    : SegmentFormatV1.WidthOf(column.Type);
            int valueLength = rowCount * width;
            int valueOffset = cursor;
            cursor = Align(cursor + valueLength);
            int bitmapOffset = 0;
            if (column.Nullable)
            {
                bitmapOffset = cursor;
                cursor = Align(cursor + nullBitmapBytes);
            }

            plan.Add(new(
                column.Id,
                column.Type,
                encoding,
                column.Nullable,
                dictionaryId,
                rowCount,
                valueOffset,
                valueLength,
                bitmapOffset,
                column.Nullable ? nullBitmapBytes : 0,
                0,
                0,
                0,
                0));
        }

        int chunkOffset = cursor;
        byte[] chunk = textColumn is not null && textDictionary is null ? BuildVariableChunk(order) : [];
        cursor = Align(chunkOffset + chunk.Length);
        int fileLength = cursor + SegmentFormatV1.TrailerLength;
        if (fileLength > SegmentFormatV1.MaximumSegmentBytes)
        {
            throw new InvalidOperationException(
                $"This segment would be {fileLength} bytes, past the {SegmentFormatV1.MaximumSegmentBytes} one "
                + "segment holds. The derivation flushes sooner rather than writing past the bound.");
        }

        byte[] file = new byte[fileLength];
        uint chunkCrc = Crc32C.Compute(chunk);
        for (int index = 0; index < plan.Count; index++)
        {
            plan[index] = WriteColumn(file, plan[index], order, schemaDictionary, textDictionary);
            if (plan[index].Encoding == SegmentColumnEncoding.VariableReference)
            {
                plan[index] = plan[index] with { VariableChunkCrc32C = chunkCrc };
            }
        }

        chunk.CopyTo(file.AsSpan(chunkOffset));
        long minTicks = sorted[0].NativeTicks;
        long maxTicks = sorted[rowCount - 1].NativeTicks;
        Guid segmentId = identity.SegmentIdFor(table, ordinal, rowCount, minTicks, maxTicks);
        WriteTimeBlocks(file, sorted);
        WriteDirectory(file, plan);
        WriteHeader(file, segmentId, minTicks, maxTicks, chunkOffset, chunk.Length, fileLength);
        SHA256.HashData(
            file.AsSpan(0, fileLength - SegmentFormatV1.TrailerLength),
            file.AsSpan(fileLength - SegmentFormatV1.TrailerLength));
        return new(file, dictionaries, segmentId, rowCount, minTicks, maxTicks, plan);
    }

    public void SetUInt8(SegmentColumnId id, int row, byte value) => Slot(id, row, 1)[0] = value;

    public void SetUInt16(SegmentColumnId id, int row, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(Slot(id, row, 2), value);

    public void SetUInt32(SegmentColumnId id, int row, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(Slot(id, row, 4), value);

    public void SetInt32(SegmentColumnId id, int row, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(Slot(id, row, 4), value);

    public void SetUInt64(SegmentColumnId id, int row, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(Slot(id, row, 8), value);

    public void SetInt64(SegmentColumnId id, int row, long value) =>
        BinaryPrimitives.WriteInt64LittleEndian(Slot(id, row, 8), value);

    public void SetNullableUInt8(SegmentColumnId id, int row, byte? value)
    {
        Slot(id, row, 1)[0] = value ?? 0;
        MarkPresent(id, row, value is not null);
    }

    public void SetNullableUInt16(SegmentColumnId id, int row, ushort? value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(Slot(id, row, 2), value ?? 0);
        MarkPresent(id, row, value is not null);
    }

    public void SetNullableUInt32(SegmentColumnId id, int row, uint? value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(Slot(id, row, 4), value ?? 0);
        MarkPresent(id, row, value is not null);
    }

    public void SetNullableInt32(SegmentColumnId id, int row, int? value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(Slot(id, row, 4), value ?? 0);
        MarkPresent(id, row, value is not null);
    }

    public void SetNullableUInt64(SegmentColumnId id, int row, ulong? value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(Slot(id, row, 8), value ?? 0);
        MarkPresent(id, row, value is not null);
    }

    public void SetNullableInt64(SegmentColumnId id, int row, long? value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(Slot(id, row, 8), value ?? 0);
        MarkPresent(id, row, value is not null);
    }

    /// <summary>
    /// A 128-bit address, its bytes in network order. An absent one writes nothing: a slot no row wrote is zero and absent
    /// when the column is built, so a segment of IPv4 records stages no IPv6 bytes.
    /// </summary>
    public void SetNullableAddress128(SegmentColumnId id, int row, UInt128? value)
    {
        if (value is not { } address) return;
        BinaryPrimitives.WriteUInt128BigEndian(Slot(id, row, 16), address);
        MarkPresent(id, row, true);
    }

    public void SetNullableGuid(SegmentColumnId id, int row, Guid? value)
    {
        Span<byte> slot = Slot(id, row, 16);
        if (value is { } identifier)
        {
            _ = identifier.TryWriteBytes(slot, bigEndian: true, out _);
        }
        else
        {
            slot.Clear();
        }

        MarkPresent(id, row, value is not null);
    }

    public void SetText(SegmentColumnId id, int row, string? value)
    {
        if (id != textColumn)
        {
            throw new InvalidOperationException($"Column {id} is not this table's text column.");
        }

        while (texts.Count <= row)
        {
            texts.Add(null);
        }

        if (texts[row] is { } previous)
        {
            stagedTextBytes -= Encoding.UTF8.GetByteCount(previous);
        }

        texts[row] = value;
        if (value is not null)
        {
            stagedTextBytes += Encoding.UTF8.GetByteCount(value);
        }

        MarkPresent(id, row, value is not null);
    }

    private static int Align(int offset) => (offset + 7) & ~7;

    private SegmentDictionaryV1? TryBuildTextDictionary(ushort dictionaryId)
    {
        // A column no row has a value in needs no dictionary. Publishing an empty one would make every
        // generation carry a file that resolves nothing, and a reader would have to load it to find that out.
        if (!texts.Any(text => text is not null))
        {
            return null;
        }

        try
        {
            return SegmentDictionaryV1.Create(
                dictionaryId,
                SegmentDictionaryKind.Utf8Text,
                texts.Where(text => text is not null).Select(text => text!));
        }
        catch (SegmentDictionaryBudgetException)
        {
            // §10.2's declared fallback: a column whose distinct values pass the dictionary budget is stored
            // in the variable chunk instead. The budget is a bound with an alternative, not a failure.
            return null;
        }
    }

    private byte[] BuildVariableChunk(int[] order)
    {
        var chunk = new List<byte>();
        foreach (int row in order)
        {
            string? text = row < texts.Count ? texts[row] : null;
            if (text is null)
            {
                continue;
            }

            chunk.AddRange(Encoding.UTF8.GetBytes(text));
            if (chunk.Count > SegmentFormatV1.MaximumVariableChunkBytes)
            {
                throw new InvalidOperationException(
                    $"This segment's variable chunk passes {SegmentFormatV1.MaximumVariableChunkBytes} bytes. "
                    + "The derivation flushes sooner rather than writing past the bound.");
            }
        }

        return [.. chunk];
    }

    private SegmentColumnDescriptor WriteColumn(
        byte[] file,
        SegmentColumnDescriptor descriptor,
        int[] order,
        SegmentDictionaryV1? schemaDictionary,
        SegmentDictionaryV1? textDictionary)
    {
        Span<byte> destination = file.AsSpan(descriptor.ValueOffset, descriptor.ValueLength);
        Span<byte> bitmap = descriptor.Nullable
            ? file.AsSpan(descriptor.NullBitmapOffset, descriptor.NullBitmapLength)
            : [];
        int position = positions[descriptor.Id];
        bool[] staged = presence[position];

        // A row has a value in a column that is not nullable, and in a nullable one where its staged presence says so.
        int known = rowCount;
        if (descriptor.Nullable)
        {
            known = 0;
            for (int row = 0; row < rowCount; row++)
            {
                int source = order[row];
                if (source < staged.Length && staged[source])
                {
                    bitmap[row >> 3] |= (byte)(1 << (row & 7));
                    known++;
                }
            }
        }

        switch (descriptor.Encoding)
        {
            case SegmentColumnEncoding.Dictionary when descriptor.Id == schemaColumn:
            {
                // Each staged code's published one, looked up once per descriptor rather than once per row.
                uint[] published = new uint[schemaEntries.Count];
                for (int local = 0; local < published.Length; local++)
                {
                    _ = schemaDictionary!.TryGetCode(schemaEntries[local], out published[local]);
                }

                ReadOnlySpan<byte> codes = values[position];
                for (int row = 0; row < rowCount; row++)
                {
                    uint local = BinaryPrimitives.ReadUInt32LittleEndian(codes[(order[row] * sizeof(uint))..]);
                    BinaryPrimitives.WriteUInt32LittleEndian(destination[(row * sizeof(uint))..], published[local]);
                }

                break;
            }

            case SegmentColumnEncoding.Dictionary:
            {
                for (int row = 0; row < rowCount; row++)
                {
                    int source = order[row];
                    uint code = 0;
                    if (source < staged.Length && staged[source] && textDictionary is not null)
                    {
                        _ = textDictionary.TryGetCode(texts[source]!, out code);
                    }

                    BinaryPrimitives.WriteUInt32LittleEndian(destination[(row * sizeof(uint))..], code);
                }

                break;
            }

            case SegmentColumnEncoding.VariableReference:
            {
                int chunkCursor = 0;
                for (int row = 0; row < rowCount; row++)
                {
                    int source = order[row];
                    bool present = source < staged.Length && staged[source];
                    int length = present ? Encoding.UTF8.GetByteCount(texts[source]!) : 0;
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        destination[(row * 2 * sizeof(uint))..],
                        present ? (uint)chunkCursor : 0);
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        destination[((row * 2 * sizeof(uint)) + sizeof(uint))..],
                        (uint)length);
                    chunkCursor += length;
                }

                break;
            }

            default:
                Gather(destination, values[position], order, SegmentFormatV1.WidthOf(descriptor.Type));
                break;
        }

        return descriptor with
        {
            KnownCount = known,
            UnknownCount = rowCount - known,
            ValueCrc32C = Crc32C.Compute(destination),
            NullBitmapCrc32C = descriptor.Nullable ? Crc32C.Compute(bitmap) : 0,
        };
    }

    /// <summary>
    /// A plain column's values in the rows' sorted order: the staged value of the row each place names, and zero for a row
    /// the column staged none for, an absent IPv6 address being one.
    /// </summary>
    private static void Gather(Span<byte> destination, byte[] staged, int[] order, int width)
    {
        switch (width)
        {
            case sizeof(byte):
                Gather<byte>(destination, staged, order);
                break;
            case sizeof(ushort):
                Gather<ushort>(destination, staged, order);
                break;
            case sizeof(uint):
                Gather<uint>(destination, staged, order);
                break;
            case sizeof(ulong):
                Gather<ulong>(destination, staged, order);
                break;
            default:
                Gather<UInt128>(destination, staged, order);
                break;
        }
    }

    private static void Gather<T>(Span<byte> destination, byte[] staged, int[] order)
        where T : unmanaged
    {
        Span<T> into = MemoryMarshal.Cast<byte, T>(destination);
        ReadOnlySpan<T> from = MemoryMarshal.Cast<byte, T>(staged.AsSpan());
        for (int row = 0; row < into.Length; row++)
        {
            int source = order[row];
            if (source < from.Length)
            {
                into[row] = from[source];
            }
        }
    }

    private void WriteTimeBlocks(byte[] file, SortKey[] sorted)
    {
        int offset = SegmentFormatV1.HeaderLength + (columns.Count * SegmentFormatV1.ColumnEntryLength);
        for (int block = 0; block < TimeBlockCount; block++)
        {
            int first = block * SegmentFormatV1.RowsPerTimeBlock;
            int count = Math.Min(SegmentFormatV1.RowsPerTimeBlock, rowCount - first);
            Span<byte> entry = file.AsSpan(offset + (block * SegmentFormatV1.TimeBlockEntryLength));
            BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)first);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], (uint)count);
            BinaryPrimitives.WriteInt64LittleEndian(entry[8..], sorted[first].NativeTicks);
            BinaryPrimitives.WriteInt64LittleEndian(entry[16..], sorted[first + count - 1].NativeTicks);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[24..], 0);
        }
    }

    private static void WriteDirectory(byte[] file, List<SegmentColumnDescriptor> plan)
    {
        for (int index = 0; index < plan.Count; index++)
        {
            SegmentColumnDescriptor column = plan[index];
            Span<byte> entry = file.AsSpan(
                SegmentFormatV1.HeaderLength + (index * SegmentFormatV1.ColumnEntryLength),
                SegmentFormatV1.ColumnEntryLength);
            BinaryPrimitives.WriteUInt16LittleEndian(entry, (ushort)column.Id);
            entry[2] = (byte)column.Type;
            entry[3] = (byte)column.Encoding;
            entry[4] = column.Nullable ? (byte)1 : (byte)0;
            entry[5] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], column.DictionaryId);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)column.RowCount);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)column.ValueOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[16..], (uint)column.ValueLength);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[20..], (uint)column.NullBitmapOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[24..], (uint)column.NullBitmapLength);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[28..], (uint)column.KnownCount);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[32..], (uint)column.UnknownCount);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[36..], column.ValueCrc32C);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[40..], column.NullBitmapCrc32C);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[44..], column.VariableChunkCrc32C);
        }
    }

    private void WriteHeader(
        byte[] file,
        Guid segmentId,
        long minTicks,
        long maxTicks,
        int chunkOffset,
        int chunkLength,
        int fileLength)
    {
        Span<byte> header = file.AsSpan(0, SegmentFormatV1.HeaderLength);
        BinaryPrimitives.WriteUInt64LittleEndian(header, SegmentFormatV1.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], SegmentFormatV1.FormatMajor);
        BinaryPrimitives.WriteUInt16LittleEndian(header[10..], SegmentFormatV1.FormatMinor);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], 0);
        _ = segmentId.TryWriteBytes(header[16..32], bigEndian: true, out _);
        _ = identity.CaptureId.Value.TryWriteBytes(header[32..48], bigEndian: true, out _);
        _ = identity.ClockId.Value.TryWriteBytes(header[48..64], bigEndian: true, out _);
        BinaryPrimitives.WriteUInt32LittleEndian(header[64..], identity.Derivation.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(header[68..], (uint)rowCount);
        BinaryPrimitives.WriteInt64LittleEndian(header[72..], minTicks);
        BinaryPrimitives.WriteInt64LittleEndian(header[80..], maxTicks);
        BinaryPrimitives.WriteUInt16LittleEndian(header[88..], (ushort)columns.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(header[90..], (ushort)TimeBlockCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header[92..], (uint)identity.TimestampEncoding);
        BinaryPrimitives.WriteUInt32LittleEndian(header[96..], SegmentFormatV1.HeaderLength);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[100..],
            (uint)(SegmentFormatV1.HeaderLength + (columns.Count * SegmentFormatV1.ColumnEntryLength)));
        BinaryPrimitives.WriteUInt32LittleEndian(header[104..], (uint)chunkOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header[108..], (uint)chunkLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header[112..], (uint)fileLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header[116..], (uint)table);
        BinaryPrimitives.WriteUInt32LittleEndian(header[120..], Crc32C.Compute(header[..120]));

        // The directories are written before the header, so their checksum covers their final bytes, the chunk
        // checksums in the column entries included.
        int directoriesEnd = SegmentFormatV1.HeaderLength
            + (columns.Count * SegmentFormatV1.ColumnEntryLength)
            + (TimeBlockCount * SegmentFormatV1.TimeBlockEntryLength);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[124..],
            Crc32C.Compute(file.AsSpan(SegmentFormatV1.HeaderLength, directoriesEnd - SegmentFormatV1.HeaderLength)));
    }

    private Span<byte> Slot(SegmentColumnId id, int row, int width)
    {
        if (!positions.TryGetValue(id, out int position))
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, $"This column is not part of this table's {table} layout.");
        }

        return Values(position, (row + 1) * width).AsSpan(row * width, width);
    }

    private void MarkPresent(SegmentColumnId id, int row, bool present)
    {
        int position = positions[id];
        Presence(position, row + 1)[row] = present;
        widened |= present && narrower is { } narrow && position >= narrow.Columns;
    }

    /// <summary>A column's staged bytes, grown to hold at least <paramref name="required"/>.</summary>
    private byte[] Values(int position, int required)
    {
        if (values[position].Length < required)
        {
            Array.Resize(ref values[position], Math.Max(required, Math.Max(64, values[position].Length * 2)));
        }

        return values[position];
    }

    /// <summary>A column's staged presence, grown to hold at least <paramref name="rows"/> rows.</summary>
    private bool[] Presence(int position, int rows)
    {
        if (presence[position].Length < rows)
        {
            Array.Resize(ref presence[position], Math.Max(rows, Math.Max(64, presence[position].Length * 2)));
        }

        return presence[position];
    }

    /// <summary>
    /// How many rows of <paramref name="source"/> from <paramref name="first"/> a copy can take before this segment holds
    /// <paramref name="rowBound"/> rows or stages <paramref name="byteBound"/> bytes, which a derivation checks before
    /// each row it adds and flushes at: at least one, the caller having checked before the first, and at most
    /// <paramref name="available"/>. Each row's text and IPv6 addresses count as they would were it added.
    /// </summary>
    public int RowsUnder(SegmentReaderV1 source, int first, int available, int rowBound, long byteBound)
    {
        RequireSource(source, first, available);
        SegmentColumnDescriptor? text = textColumn is { } textId ? source.Column(textId) : null;
        TextLengths? lengths = text is null ? null : new TextLengths(source, text);
        SegmentColumnPresence textPresence = text is null ? default : source.Presence(text.Id);
        SegmentColumnId[] widening = narrower is { } narrow
            ? [.. columns.Skip(narrow.Columns).Select(column => column.Id).Where(source.HasColumn)]
            : [];
        int rows = rowCount;
        long textBytes = stagedTextBytes;
        bool wide = widened;
        int taken = 0;
        while (taken < available && (taken == 0 || (rows < rowBound && StagedBytesOf(rows, textBytes, wide) < byteBound)))
        {
            int row = first + taken;
            if (lengths is not null && textPresence.HasValue(row))
            {
                textBytes += lengths.At(row);
            }

            for (int column = 0; !wide && column < widening.Length; column++)
            {
                wide = source.Presence(widening[column]).HasValue(row);
            }

            rows++;
            taken++;
        }

        return taken;
    }

    /// <summary>
    /// Copies <paramref name="count"/> rows of <paramref name="source"/> from <paramref name="first"/> - a published segment
    /// of this table, or of the table it extends - into the columns as the rows its reader materializes would be staged
    /// one by one, without materializing them (§20.1 compaction, R11): each plain column's bytes, which a segment stores as
    /// they are staged here, a null as zero; each row's presence in a nullable column; the schema and the text each row
    /// names, interned here in the rows' order; and each row's place in canonical order, its discriminator read from
    /// <paramref name="discriminator"/> where the table has one. The rows were checked when that segment was written, and
    /// are read through its checksums; a column the source's table lacks stays absent, as for a row without it.
    /// </summary>
    public void CopyRows(SegmentReaderV1 source, int first, int count, SegmentColumnId? discriminator)
    {
        RequireSource(source, first, count);
        if (count > SegmentFormatV1.MaximumRowsPerSegment - rowCount)
        {
            throw new InvalidOperationException(
                $"A segment holds at most {SegmentFormatV1.MaximumRowsPerSegment} rows. This one holds {rowCount}, and "
                + $"the derivation flushes it rather than writing {count} more past the bound.");
        }

        int at = rowCount;
        for (int position = 0; position < columns.Count; position++)
        {
            SegmentColumnSpec column = columns[position];
            if (source.Column(column.Id) is not { } from)
            {
                continue;
            }

            if (column.Id == schemaColumn)
            {
                CopySchemas(source, from, position, first, count, at);
                continue;
            }

            if (column.Id == textColumn)
            {
                CopyTexts(source, from, position, first, count, at);
                continue;
            }

            int width = SegmentFormatV1.WidthOf(column.Type);
            source.ValueBytes(column.Id).Slice(first * width, count * width)
                .CopyTo(Values(position, (at + count) * width).AsSpan(at * width));
            if (column.Nullable)
            {
                SegmentColumnPresence present = source.Presence(column.Id);
                bool[] into = Presence(position, at + count);
                bool any = false;
                for (int row = 0; row < count; row++)
                {
                    into[at + row] = present.HasValue(first + row);
                    any |= into[at + row];
                }

                widened |= any && narrower is { } narrow && position >= narrow.Columns;
            }
        }

        ReadOnlySpan<byte> ticks = source.ValueBytes(SegmentColumnId.NativeTicks);
        ReadOnlySpan<byte> streams = source.ValueBytes(SegmentColumnId.RawStreamId);
        ReadOnlySpan<byte> epochs = source.ValueBytes(SegmentColumnId.RawSourceEpoch);
        ReadOnlySpan<byte> ordinals = source.ValueBytes(SegmentColumnId.RawRecordOrdinal);
        ReadOnlySpan<byte> highs = source.ValueBytes(SegmentColumnId.FactKeyHigh);
        ReadOnlySpan<byte> lows = source.ValueBytes(SegmentColumnId.FactKeyLow);
        ReadOnlySpan<byte> discriminators = discriminator is { } id ? source.ValueBytes(id) : default;
        keys.EnsureCapacity(at + count);
        for (int row = first; row < first + count; row++)
        {
            keys.Add(new(
                BinaryPrimitives.ReadInt64LittleEndian(ticks[(row * sizeof(long))..]),
                BinaryPrimitives.ReadUInt32LittleEndian(streams[(row * sizeof(uint))..]),
                BinaryPrimitives.ReadUInt32LittleEndian(epochs[(row * sizeof(uint))..]),
                BinaryPrimitives.ReadUInt64LittleEndian(ordinals[(row * sizeof(ulong))..]),
                new(BinaryPrimitives.ReadUInt64LittleEndian(highs[(row * sizeof(ulong))..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(lows[(row * sizeof(ulong))..])),
                discriminator is null ? (ushort)0 : BinaryPrimitives.ReadUInt16LittleEndian(discriminators[(row * sizeof(ushort))..])));
        }

        rowCount += count;
    }

    /// <summary>The rows' schema codes, each distinct code's descriptor interned once, at its first row.</summary>
    private void CopySchemas(SegmentReaderV1 source, SegmentColumnDescriptor from, int position, int first, int count, int at)
    {
        SegmentDictionaryV1 dictionary = source.DictionaryOf(from);
        uint[] local = new uint[dictionary.Count];
        Array.Fill(local, uint.MaxValue);
        ReadOnlySpan<byte> codes = source.ValueBytes(from.Id);
        Span<byte> into = Values(position, (at + count) * sizeof(uint)).AsSpan(at * sizeof(uint), count * sizeof(uint));
        for (int row = 0; row < count; row++)
        {
            uint code = BinaryPrimitives.ReadUInt32LittleEndian(codes[((first + row) * sizeof(uint))..]);
            if (code >= (uint)local.Length || local[code] == uint.MaxValue)
            {
                (Guid providerId, ushort eventId, byte version, string fingerprint) =
                    SegmentFormatV1.ParseSchemaEntry(dictionary[code]);
                local[code] = InternSchema(providerId, eventId, version, fingerprint);
            }

            BinaryPrimitives.WriteUInt32LittleEndian(into[(row * sizeof(uint))..], local[code]);
        }
    }

    /// <summary>
    /// The rows' texts: a dictionary-coded column's values taken once each, a referenced one's read row by row. Every row
    /// staged stages its text, a null where it has none, so these follow the rows before them.
    /// </summary>
    private void CopyTexts(SegmentReaderV1 source, SegmentColumnDescriptor from, int position, int first, int count, int at)
    {
        SegmentColumnPresence present = source.Presence(from.Id);
        bool[] into = Presence(position, at + count);
        var lengths = new TextLengths(source, from);
        texts.EnsureCapacity(at + count);
        for (int row = 0; row < count; row++)
        {
            string? text = present.HasValue(first + row) ? lengths.Text(first + row) : null;
            texts.Add(text);
            if (text is not null)
            {
                stagedTextBytes += lengths.At(first + row);
            }

            into[at + row] = text is not null;
        }
    }

    private void RequireSource(SegmentReaderV1 source, int first, int count)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Table != table && (narrower is not { } narrow || source.Table != narrow.Table))
        {
            throw new ArgumentException($"A {source.Table} segment's rows are not copied into a {table} segment.", nameof(source));
        }

        if (first < 0 || count < 1 || first > source.RowCount - count)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, $"Rows {first} to {first + count} are not the segment's {source.RowCount}.");
        }
    }

    /// <summary>
    /// A text column's values and their UTF-8 lengths: a dictionary-coded column's per code, each taken once; a referenced
    /// one's length from its reference, which is the length of the bytes it names.
    /// </summary>
    private sealed class TextLengths
    {
        private readonly SegmentReaderV1 source;
        private readonly SegmentColumnDescriptor column;
        private readonly SegmentDictionaryV1? dictionary;
        private readonly int[] lengths;

        public TextLengths(SegmentReaderV1 source, SegmentColumnDescriptor column)
        {
            this.source = source;
            this.column = column;
            dictionary = column.Encoding == SegmentColumnEncoding.Dictionary ? source.DictionaryOf(column) : null;
            lengths = new int[dictionary?.Count ?? 0];
            Array.Fill(lengths, -1);
        }

        public string Text(int row) => dictionary is not null ? dictionary[Code(row)] : source.TextValue(column.Id, row)!;

        public int At(int row)
        {
            if (dictionary is null)
            {
                return (int)BinaryPrimitives.ReadUInt32LittleEndian(
                    source.ValueBytes(column.Id)[((row * 2 * sizeof(uint)) + sizeof(uint))..]);
            }

            // The dictionary refuses a code past its entries before one is looked up here.
            uint code = Code(row);
            string text = dictionary[code];
            if (lengths[code] < 0)
            {
                lengths[code] = Encoding.UTF8.GetByteCount(text);
            }

            return lengths[code];
        }

        private uint Code(int row) => BinaryPrimitives.ReadUInt32LittleEndian(source.ValueBytes(column.Id)[(row * sizeof(uint))..]);
    }

    /// <summary>What a table layout costs: its fixed header, directory and trailer, its bytes per row, its null bitmaps.</summary>
    private readonly record struct StagedLayout(long FixedBytes, int WidthPerRow, int NullableColumns)
    {
        public static StagedLayout Of(IReadOnlyList<SegmentColumnSpec> columns) => new(
            SegmentFormatV1.HeaderLength + (columns.Count * (long)SegmentFormatV1.ColumnEntryLength)
                + SegmentFormatV1.TrailerLength,
            columns.Sum(column => SegmentFormatV1.WidthOf(column.Type)),
            columns.Count(column => column.Nullable));
    }
}

/// <summary>
/// The canonical order of a segment: the instant, then the raw-record locator, then the fact key, then a table's own
/// discriminator. The locator is unique inside a capture (I1), so the order is total and the file is reproducible;
/// the tie breaks are not a claim about which of two records happened first.
/// </summary>
internal readonly record struct SortKey(
    long NativeTicks,
    uint StreamId,
    uint SourceEpoch,
    ulong RecordOrdinal,
    FactKey FactKey,
    ushort Discriminator)
{
    public static int Compare(SortKey left, SortKey right)
    {
        int order = left.NativeTicks.CompareTo(right.NativeTicks);
        order = order != 0 ? order : left.StreamId.CompareTo(right.StreamId);
        order = order != 0 ? order : left.SourceEpoch.CompareTo(right.SourceEpoch);
        order = order != 0 ? order : left.RecordOrdinal.CompareTo(right.RecordOrdinal);
        order = order != 0 ? order : left.FactKey.High.CompareTo(right.FactKey.High);
        order = order != 0 ? order : left.FactKey.Low.CompareTo(right.FactKey.Low);
        return order != 0 ? order : left.Discriminator.CompareTo(right.Discriminator);
    }
}

/// <summary>The canonical order as a comparer a sort calls without a delegate.</summary>
internal readonly struct SortKeyOrder : IComparer<SortKey>
{
    public int Compare(SortKey left, SortKey right) => SortKey.Compare(left, right);
}

/// <summary>One column as a segment's directory records it, including its availability counters (§10.2).</summary>
/// <param name="VariableChunkCrc32C">
/// A chunk-encoded column's CRC-32C over the segment's variable chunk, which its references point into. Zero for any
/// other column, and for every column of a minor-0 segment, which reserved the word.
/// </param>
public sealed record SegmentColumnDescriptor(
    SegmentColumnId Id,
    SegmentColumnType Type,
    SegmentColumnEncoding Encoding,
    bool Nullable,
    ushort DictionaryId,
    int RowCount,
    int ValueOffset,
    int ValueLength,
    int NullBitmapOffset,
    int NullBitmapLength,
    int KnownCount,
    int UnknownCount,
    uint ValueCrc32C,
    uint NullBitmapCrc32C,
    uint VariableChunkCrc32C = 0);

internal static class StableSegmentId
{
    /// <summary>A deterministic identity for derived evidence, in the same UUIDv8 shape §7.2 uses.</summary>
    public static Guid Derive(string canonicalForm)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalForm));
        Span<byte> uuid = stackalloc byte[16];
        hash.AsSpan(0, uuid.Length).CopyTo(uuid);
        uuid[6] = (byte)((uuid[6] & 0x0f) | 0x80);
        uuid[8] = (byte)((uuid[8] & 0x3f) | 0x80);
        return new Guid(uuid, bigEndian: true);
    }
}
