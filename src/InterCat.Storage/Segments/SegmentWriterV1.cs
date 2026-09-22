using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using InterCat.Domain;

namespace InterCat.Storage;

/// <summary>Which table a segment holds. The container is shared; the column set is not.</summary>
public enum SegmentTableId : uint
{
    /// <summary>The normalized source observations of §7.3, as `observation-v1`.</summary>
    ObservationV1 = 1,
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
/// Builds one immutable `observation-v1` segment. Rows are encoded into their columns as they arrive and
/// are not retained, so a derivation's memory is the size of its columns rather than the size of its rows
/// (R8); the sort that makes the segment ordered is a permutation of those columns at the end.
/// </summary>
public sealed class SegmentWriterV1
{
    private readonly SegmentIdentityV1 identity;
    private readonly int ordinal;
    private readonly byte[][] values;
    private readonly bool[][] presence;
    private readonly List<string?> resourceNames = [];
    private readonly List<SortKey> keys = [];
    private readonly Dictionary<(Guid Provider, ushort EventId, byte Version), (string Entry, uint Local)> schemas = [];
    private readonly List<string> schemaEntries = [];
    private int rowCount;

    public SegmentWriterV1(SegmentIdentityV1 identity, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        this.identity = identity;
        this.ordinal = ordinal;
        int columns = SegmentFormatV1.ObservationColumns.Count;
        values = new byte[columns][];
        presence = new bool[columns][];
        for (int index = 0; index < columns; index++)
        {
            values[index] = [];
            presence[index] = [];
        }
    }

    public int RowCount => rowCount;

    /// <summary>
    /// How many bytes this segment's columns will occupy, so a caller can flush on §20.1's size. It is read
    /// once per row, so the per-row and fixed parts are computed from the frozen column set rather than by
    /// walking it every time.
    /// </summary>
    public long StagedBytes =>
        FixedBytes + ((long)rowCount * FixedWidthPerRow) + ((long)NullableColumnCount * ((rowCount + 7) / 8));

    private static long FixedBytes { get; } =
        SegmentFormatV1.HeaderLength
        + (SegmentFormatV1.ObservationColumns.Count * (long)SegmentFormatV1.ColumnEntryLength)
        + SegmentFormatV1.TrailerLength;

    private static int FixedWidthPerRow { get; } =
        SegmentFormatV1.ObservationColumns.Sum(column => SegmentFormatV1.WidthOf(column.Type));

    private static int NullableColumnCount { get; } =
        SegmentFormatV1.ObservationColumns.Count(column => column.Nullable);

    /// <summary>Encodes one row into the columns. The row itself is not retained.</summary>
    public void Add(ObservationRowV1 row)
    {
        ArgumentNullException.ThrowIfNull(row);
        string? problem = row.Validate();
        if (problem is not null)
        {
            throw new ArgumentException(problem, nameof(row));
        }

        if (rowCount == SegmentFormatV1.MaximumRowsPerSegment)
        {
            throw new InvalidOperationException(
                $"A segment holds at most {SegmentFormatV1.MaximumRowsPerSegment} rows. This one is full and "
                + "the derivation flushes it rather than writing past the bound.");
        }

        int at = rowCount;
        SetUInt32(SegmentColumnId.RawStreamId, at, row.RawStreamId);
        SetUInt32(SegmentColumnId.RawSourceEpoch, at, row.RawSourceEpoch);
        SetUInt64(SegmentColumnId.RawRecordOrdinal, at, row.RawRecordOrdinal);
        SetNullableUInt64(SegmentColumnId.JournalRecordIndex, at, row.JournalRecordIndex);
        SetUInt64(SegmentColumnId.FactKeyHigh, at, row.FactKey.High);
        SetUInt64(SegmentColumnId.FactKeyLow, at, row.FactKey.Low);
        SetUInt32(SegmentColumnId.SchemaCode, at, InternSchema(row));
        SetUInt8(SegmentColumnId.Opcode, at, row.Opcode);
        SetInt64(SegmentColumnId.NativeTicks, at, row.NativeTicks);
        SetNullableInt64(SegmentColumnId.SessionRelativeTicks, at, row.SessionRelativeTicks);
        SetInt32(SegmentColumnId.HeaderProcessId, at, row.HeaderProcessId);
        SetInt32(SegmentColumnId.HeaderThreadId, at, row.HeaderThreadId);
        SetUInt16(SegmentColumnId.ProcessorNumber, at, row.ProcessorNumber);
        SetNullableGuid(SegmentColumnId.ActivityId, at, row.ActivityId);
        SetNullableGuid(SegmentColumnId.RelatedActivityId, at, row.RelatedActivityId);
        SetUInt8(SegmentColumnId.Mechanism, at, checked((byte)row.Mechanism));
        SetUInt8(SegmentColumnId.Layer, at, checked((byte)row.Layer));
        SetUInt8(SegmentColumnId.ObservationKind, at, checked((byte)row.Kind));
        SetUInt8(SegmentColumnId.Direction, at, checked((byte)row.Direction));
        SetNullableInt32(SegmentColumnId.OwnerProcessId, at, row.OwnerProcessId);
        SetText(SegmentColumnId.ResourceName, at, row.ResourceName);
        SetNullableGuid(SegmentColumnId.SourceIdentifier, at, row.SourceIdentifier);
        SetNullableUInt8(SegmentColumnId.EndpointAddressFamily, at, row.EndpointAddressFamily);
        SetNullableUInt32(SegmentColumnId.SourceEndpointAddress, at, row.SourceEndpointAddress);
        SetNullableUInt16(SegmentColumnId.SourceEndpointPort, at, row.SourceEndpointPort);
        SetNullableUInt32(SegmentColumnId.DestinationEndpointAddress, at, row.DestinationEndpointAddress);
        SetNullableUInt16(SegmentColumnId.DestinationEndpointPort, at, row.DestinationEndpointPort);
        SetNullableInt64(SegmentColumnId.ByteValue, at, row.ByteValue);
        SetNullableUInt8(SegmentColumnId.ByteDomain, at, row.ByteDomain is { } domain ? checked((byte)domain) : null);
        SetNullableUInt8(SegmentColumnId.AccountingSide, at, row.AccountingSide is { } side ? checked((byte)side) : null);
        SetNullableUInt8(SegmentColumnId.MeasurementUnit, at, row.MeasurementUnit is { } unit ? checked((byte)unit) : null);
        SetUInt8(SegmentColumnId.ByteAvailability, at, checked((byte)row.ByteAvailability));
        SetNullableInt64(SegmentColumnId.StatusCode, at, row.StatusCode);
        SetUInt8(SegmentColumnId.StatusAvailability, at, checked((byte)row.StatusAvailability));
        SetUInt8(SegmentColumnId.AttributionQuality, at, checked((byte)row.AttributionQuality));
        SetUInt8(SegmentColumnId.CorrelationQuality, at, checked((byte)row.CorrelationQuality));
        SetUInt8(SegmentColumnId.MeasurementQuality, at, checked((byte)row.MeasurementQuality));
        SetUInt8(SegmentColumnId.TimingQuality, at, checked((byte)row.TimingQuality));
        SetUInt16(SegmentColumnId.Markers, at, (ushort)row.Markers);

        keys.Add(new(row.NativeTicks, row.RawStreamId, row.RawSourceEpoch, row.RawRecordOrdinal, row.FactKey));
        rowCount++;
    }

    /// <summary>
    /// Writes the segment. Rows are sorted into the canonical order — time, then the raw locator, then the
    /// fact key — which is total, so the file is a function of its row set rather than of arrival order.
    /// </summary>
    public SegmentBuildResult Build(ushort firstDictionaryId = 1)
    {
        if (rowCount == 0)
        {
            throw new InvalidOperationException(
                "An empty segment is not published. Nothing observed is coverage, which the coverage ledger "
                + "states; a zero-row segment would let absence be read as data (R21, P1).");
        }

        int[] order = new int[rowCount];
        for (int index = 0; index < rowCount; index++)
        {
            order[index] = index;
        }

        Array.Sort(order, (left, right) => SortKey.Compare(keys[left], keys[right]));
        for (int index = 1; index < rowCount; index++)
        {
            if (SortKey.Compare(keys[order[index - 1]], keys[order[index]]) == 0)
            {
                throw new InvalidOperationException(
                    "Two rows of this segment share one observation identity. A segment is refused rather "
                    + "than published with a fact that would be counted twice (I2, I5).");
            }
        }

        SegmentDictionaryV1 schemaDictionary = SegmentDictionaryV1.Create(
            firstDictionaryId,
            SegmentDictionaryKind.Schema,
            schemaEntries);
        SegmentDictionaryV1? nameDictionary = TryBuildNameDictionary((ushort)(firstDictionaryId + 1));
        List<SegmentDictionaryV1> dictionaries = nameDictionary is null
            ? [schemaDictionary]
            : [schemaDictionary, nameDictionary];

        var plan = new List<SegmentColumnDescriptor>(SegmentFormatV1.ObservationColumns.Count);
        int nullBitmapBytes = (rowCount + 7) / 8;
        int cursor = Align(
            SegmentFormatV1.HeaderLength
            + (SegmentFormatV1.ObservationColumns.Count * SegmentFormatV1.ColumnEntryLength)
            + (TimeBlockCount * SegmentFormatV1.TimeBlockEntryLength));

        foreach (SegmentColumnSpec column in SegmentFormatV1.ObservationColumns)
        {
            SegmentColumnEncoding encoding = column.Id == SegmentColumnId.ResourceName
                ? nameDictionary is null
                    ? SegmentColumnEncoding.VariableReference
                    : SegmentColumnEncoding.Dictionary
                : column.Id == SegmentColumnId.SchemaCode
                    ? SegmentColumnEncoding.Dictionary
                    : SegmentColumnEncoding.Plain;
            ushort dictionaryId = column.Id switch
            {
                SegmentColumnId.SchemaCode => schemaDictionary.DictionaryId,
                SegmentColumnId.ResourceName when nameDictionary is not null => nameDictionary.DictionaryId,
                _ => 0,
            };
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
        byte[] chunk = nameDictionary is null ? BuildVariableChunk(order) : [];
        cursor = Align(chunkOffset + chunk.Length);
        int fileLength = cursor + SegmentFormatV1.TrailerLength;
        if (fileLength > SegmentFormatV1.MaximumSegmentBytes)
        {
            throw new InvalidOperationException(
                $"This segment would be {fileLength} bytes, past the {SegmentFormatV1.MaximumSegmentBytes} one "
                + "segment holds. The derivation flushes sooner rather than writing past the bound.");
        }

        byte[] file = new byte[fileLength];
        for (int index = 0; index < plan.Count; index++)
        {
            plan[index] = WriteColumn(file, plan[index], order, schemaDictionary, nameDictionary);
        }

        chunk.CopyTo(file.AsSpan(chunkOffset));
        long minTicks = keys[order[0]].NativeTicks;
        long maxTicks = keys[order[rowCount - 1]].NativeTicks;
        Guid segmentId = identity.SegmentIdFor(ordinal, rowCount, minTicks, maxTicks);
        WriteTimeBlocks(file, order);
        WriteDirectory(file, plan);
        WriteHeader(file, segmentId, minTicks, maxTicks, chunkOffset, chunk.Length, fileLength);
        SHA256.HashData(
            file.AsSpan(0, fileLength - SegmentFormatV1.TrailerLength),
            file.AsSpan(fileLength - SegmentFormatV1.TrailerLength));
        return new(file, dictionaries, segmentId, rowCount, minTicks, maxTicks, plan);
    }

    private int TimeBlockCount => Math.Max(1, (rowCount + SegmentFormatV1.RowsPerTimeBlock - 1) / SegmentFormatV1.RowsPerTimeBlock);

    private static int Align(int offset) => (offset + 7) & ~7;

    /// <summary>
    /// Interns a row's descriptor and returns the local code the column stores until <c>Build</c> remaps it
    /// onto the published dictionary. A descriptor's canonical text is built once per distinct descriptor,
    /// never once per row.
    /// </summary>
    private uint InternSchema(ObservationRowV1 row)
    {
        (Guid, ushort, byte) key = (row.ProviderId, row.EventId, row.DescriptorVersion);
        if (schemas.TryGetValue(key, out (string Entry, uint Local) existing))
        {
            string expected = SegmentFormatV1.SchemaEntry(
                row.ProviderId,
                row.EventId,
                row.DescriptorVersion,
                row.SchemaFingerprint);
            return existing.Entry.Equals(expected, StringComparison.Ordinal)
                ? existing.Local
                : throw new InvalidOperationException(
                    $"Descriptor {row.ProviderId:D}/{row.EventId}/v{row.DescriptorVersion} already carries a "
                    + "different schema fingerprint in this segment. A descriptor cannot change shape inside "
                    + "one derivation without a new identity.");
        }

        string entry = SegmentFormatV1.SchemaEntry(
            row.ProviderId,
            row.EventId,
            row.DescriptorVersion,
            row.SchemaFingerprint);
        var local = (uint)schemaEntries.Count;
        schemaEntries.Add(entry);
        schemas.Add(key, (entry, local));
        return local;
    }

    private SegmentDictionaryV1? TryBuildNameDictionary(ushort dictionaryId)
    {
        // A column no row has a value in needs no dictionary. Publishing an empty one would make every
        // generation carry a file that resolves nothing, and a reader would have to load it to find that out.
        if (!resourceNames.Any(name => name is not null))
        {
            return null;
        }

        try
        {
            return SegmentDictionaryV1.Create(
                dictionaryId,
                SegmentDictionaryKind.Utf8Text,
                resourceNames.Where(name => name is not null).Select(name => name!));
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
            string? name = resourceNames[row];
            if (name is null)
            {
                continue;
            }

            chunk.AddRange(Encoding.UTF8.GetBytes(name));
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
        SegmentDictionaryV1 schemaDictionary,
        SegmentDictionaryV1? nameDictionary)
    {
        Span<byte> destination = file.AsSpan(descriptor.ValueOffset, descriptor.ValueLength);
        Span<byte> bitmap = descriptor.Nullable
            ? file.AsSpan(descriptor.NullBitmapOffset, descriptor.NullBitmapLength)
            : [];
        int position = IndexOf(descriptor.Id);
        int known = 0;
        int chunkCursor = 0;

        for (int row = 0; row < rowCount; row++)
        {
            int source = order[row];
            bool present = !descriptor.Nullable || presence[position][source];
            if (present && descriptor.Nullable)
            {
                bitmap[row >> 3] |= (byte)(1 << (row & 7));
            }

            if (present)
            {
                known++;
            }

            switch (descriptor.Encoding)
            {
                case SegmentColumnEncoding.Dictionary when descriptor.Id == SegmentColumnId.SchemaCode:
                {
                    uint local = BinaryPrimitives.ReadUInt32LittleEndian(
                        values[position].AsSpan(source * sizeof(uint)));
                    _ = schemaDictionary.TryGetCode(schemaEntries[(int)local], out uint code);
                    BinaryPrimitives.WriteUInt32LittleEndian(destination[(row * sizeof(uint))..], code);
                    break;
                }

                case SegmentColumnEncoding.Dictionary:
                {
                    uint code = 0;
                    if (present && nameDictionary is not null)
                    {
                        _ = nameDictionary.TryGetCode(resourceNames[source]!, out code);
                    }

                    BinaryPrimitives.WriteUInt32LittleEndian(destination[(row * sizeof(uint))..], code);
                    break;
                }

                case SegmentColumnEncoding.VariableReference:
                {
                    int length = present ? Encoding.UTF8.GetByteCount(resourceNames[source]!) : 0;
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        destination[(row * 2 * sizeof(uint))..],
                        present ? (uint)chunkCursor : 0);
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        destination[((row * 2 * sizeof(uint)) + sizeof(uint))..],
                        (uint)length);
                    chunkCursor += length;
                    break;
                }

                default:
                {
                    int width = SegmentFormatV1.WidthOf(descriptor.Type);
                    values[position].AsSpan(source * width, width).CopyTo(destination[(row * width)..]);
                    break;
                }
            }
        }

        return descriptor with
        {
            KnownCount = known,
            UnknownCount = rowCount - known,
            ValueCrc32C = Crc32C.Compute(destination),
            NullBitmapCrc32C = descriptor.Nullable ? Crc32C.Compute(bitmap) : 0,
        };
    }

    private void WriteTimeBlocks(byte[] file, int[] order)
    {
        int offset = SegmentFormatV1.HeaderLength
            + (SegmentFormatV1.ObservationColumns.Count * SegmentFormatV1.ColumnEntryLength);
        for (int block = 0; block < TimeBlockCount; block++)
        {
            int first = block * SegmentFormatV1.RowsPerTimeBlock;
            int count = Math.Min(SegmentFormatV1.RowsPerTimeBlock, rowCount - first);
            Span<byte> entry = file.AsSpan(offset + (block * SegmentFormatV1.TimeBlockEntryLength));
            BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)first);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], (uint)count);
            BinaryPrimitives.WriteInt64LittleEndian(entry[8..], keys[order[first]].NativeTicks);
            BinaryPrimitives.WriteInt64LittleEndian(entry[16..], keys[order[first + count - 1]].NativeTicks);
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
            BinaryPrimitives.WriteUInt32LittleEndian(entry[44..], 0);
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
        BinaryPrimitives.WriteUInt16LittleEndian(header[88..], (ushort)SegmentFormatV1.ObservationColumns.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(header[90..], (ushort)TimeBlockCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header[92..], (uint)identity.TimestampEncoding);
        BinaryPrimitives.WriteUInt32LittleEndian(header[96..], SegmentFormatV1.HeaderLength);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[100..],
            (uint)(SegmentFormatV1.HeaderLength
                + (SegmentFormatV1.ObservationColumns.Count * SegmentFormatV1.ColumnEntryLength)));
        BinaryPrimitives.WriteUInt32LittleEndian(header[104..], (uint)chunkOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header[108..], (uint)chunkLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header[112..], (uint)fileLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header[116..], (uint)SegmentTableId.ObservationV1);
        BinaryPrimitives.WriteUInt32LittleEndian(header[120..], Crc32C.Compute(header[..120]));
        BinaryPrimitives.WriteUInt32LittleEndian(header[124..], 0);
    }

    private static int IndexOf(SegmentColumnId id)
    {
        for (int index = 0; index < SegmentFormatV1.ObservationColumns.Count; index++)
        {
            if (SegmentFormatV1.ObservationColumns[index].Id == id)
            {
                return index;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(id), id, "This column is not part of `observation-v1`.");
    }

    private Span<byte> Slot(SegmentColumnId id, int row, int width)
    {
        int position = IndexOf(id);
        int required = (row + 1) * width;
        if (values[position].Length < required)
        {
            Array.Resize(ref values[position], Math.Max(required, Math.Max(64, values[position].Length * 2)));
        }

        return values[position].AsSpan(row * width, width);
    }

    private void MarkPresent(SegmentColumnId id, int row, bool present)
    {
        int position = IndexOf(id);
        if (presence[position].Length <= row)
        {
            Array.Resize(ref presence[position], Math.Max(row + 1, Math.Max(64, presence[position].Length * 2)));
        }

        presence[position][row] = present;
    }

    private void SetUInt8(SegmentColumnId id, int row, byte value) => Slot(id, row, 1)[0] = value;

    private void SetUInt16(SegmentColumnId id, int row, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(Slot(id, row, 2), value);

    private void SetUInt32(SegmentColumnId id, int row, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(Slot(id, row, 4), value);

    private void SetInt32(SegmentColumnId id, int row, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(Slot(id, row, 4), value);

    private void SetUInt64(SegmentColumnId id, int row, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(Slot(id, row, 8), value);

    private void SetInt64(SegmentColumnId id, int row, long value) =>
        BinaryPrimitives.WriteInt64LittleEndian(Slot(id, row, 8), value);

    private void SetNullableUInt8(SegmentColumnId id, int row, byte? value)
    {
        Slot(id, row, 1)[0] = value ?? 0;
        MarkPresent(id, row, value is not null);
    }

    private void SetNullableUInt16(SegmentColumnId id, int row, ushort? value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(Slot(id, row, 2), value ?? 0);
        MarkPresent(id, row, value is not null);
    }

    private void SetNullableUInt32(SegmentColumnId id, int row, uint? value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(Slot(id, row, 4), value ?? 0);
        MarkPresent(id, row, value is not null);
    }

    private void SetNullableInt32(SegmentColumnId id, int row, int? value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(Slot(id, row, 4), value ?? 0);
        MarkPresent(id, row, value is not null);
    }

    private void SetNullableUInt64(SegmentColumnId id, int row, ulong? value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(Slot(id, row, 8), value ?? 0);
        MarkPresent(id, row, value is not null);
    }

    private void SetNullableInt64(SegmentColumnId id, int row, long? value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(Slot(id, row, 8), value ?? 0);
        MarkPresent(id, row, value is not null);
    }

    private void SetNullableGuid(SegmentColumnId id, int row, Guid? value)
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

    private void SetText(SegmentColumnId id, int row, string? value)
    {
        while (resourceNames.Count <= row)
        {
            resourceNames.Add(null);
        }

        resourceNames[row] = value;
        MarkPresent(id, row, value is not null);
    }

    /// <summary>
    /// The canonical order of a segment: the instant, then the raw-record locator, then the fact key. The
    /// locator is unique inside a capture (I1), so the order is total and the file is reproducible; the tie
    /// breaks are not a claim about which of two records happened first.
    /// </summary>
    private readonly record struct SortKey(
        long NativeTicks,
        uint StreamId,
        uint SourceEpoch,
        ulong RecordOrdinal,
        FactKey FactKey)
    {
        public static int Compare(SortKey left, SortKey right)
        {
            int order = left.NativeTicks.CompareTo(right.NativeTicks);
            if (order != 0)
            {
                return order;
            }

            order = left.StreamId.CompareTo(right.StreamId);
            if (order != 0)
            {
                return order;
            }

            order = left.SourceEpoch.CompareTo(right.SourceEpoch);
            if (order != 0)
            {
                return order;
            }

            order = left.RecordOrdinal.CompareTo(right.RecordOrdinal);
            if (order != 0)
            {
                return order;
            }

            order = left.FactKey.High.CompareTo(right.FactKey.High);
            return order != 0 ? order : left.FactKey.Low.CompareTo(right.FactKey.Low);
        }
    }
}

/// <summary>One column as a segment's directory records it, including its availability counters (§10.2).</summary>
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
    uint NullBitmapCrc32C);

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
