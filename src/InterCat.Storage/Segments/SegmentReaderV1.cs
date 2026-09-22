using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using InterCat.Domain;

namespace InterCat.Storage;

/// <summary>One time block's min/max metadata, so a viewport can skip a block without reading its rows (§10.2).</summary>
public sealed record SegmentTimeBlockV1(int FirstRow, int RowCount, long MinNativeTicks, long MaxNativeTicks);

/// <summary>
/// One column resolved once: its descriptor, its verified value bytes and its null bitmap. A caller that
/// reads a column for every row takes a slice instead of resolving and re-checking the column per row.
/// </summary>
public readonly ref struct SegmentColumnSlice
{
    private readonly ReadOnlySpan<byte> values;
    private readonly ReadOnlySpan<byte> nulls;
    private readonly int width;

    internal SegmentColumnSlice(
        SegmentColumnDescriptor descriptor,
        ReadOnlySpan<byte> values,
        ReadOnlySpan<byte> nulls,
        int width)
    {
        Descriptor = descriptor;
        this.values = values;
        this.nulls = nulls;
        this.width = width;
    }

    public SegmentColumnDescriptor Descriptor { get; }

    public bool HasValue(int row) =>
        !Descriptor.Nullable || (nulls[row >> 3] & (1 << (row & 7))) != 0;

    /// <summary>The unsigned value of a row, or null when the row has none.</summary>
    public ulong? UnsignedAt(int row)
    {
        if (!HasValue(row))
        {
            return null;
        }

        ReadOnlySpan<byte> value = values.Slice(row * width, width);
        return width switch
        {
            1 => value[0],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(value),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(value),
            8 => BinaryPrimitives.ReadUInt64LittleEndian(value),
            _ => throw new InvalidOperationException(
                $"Column {Descriptor.Id} is {width} bytes wide and is not read as a number."),
        };
    }

    /// <summary>The signed value of a row, or null when the row has none.</summary>
    public long? SignedAt(int row)
    {
        if (!HasValue(row))
        {
            return null;
        }

        ReadOnlySpan<byte> value = values.Slice(row * width, width);
        return width switch
        {
            1 => value[0],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(value),
            4 => BinaryPrimitives.ReadInt32LittleEndian(value),
            8 => BinaryPrimitives.ReadInt64LittleEndian(value),
            _ => throw new InvalidOperationException(
                $"Column {Descriptor.Id} is {width} bytes wide and is not read as a number."),
        };
    }
}

/// <summary>
/// Reads one immutable `observation-v1` segment. Everything a reader would otherwise have to trust is
/// checked before a row is served: the header's own checksum, the trailing digest over the whole file, every
/// declared extent against the file's real length, each column's checksum on first use, and the ordering and
/// time-block metadata against the time column itself.
/// </summary>
/// <remarks>
/// The bytes are held as given. Fixed-width plain columns are exposed as spans over them, so a caller reads
/// a column without decoding it; nothing here copies a column to serve one.
/// </remarks>
public sealed class SegmentReaderV1
{
    private readonly ReadOnlyMemory<byte> file;
    private readonly Dictionary<SegmentColumnId, SegmentColumnDescriptor> columns;
    private readonly HashSet<SegmentColumnId> verified = [];
    private readonly Dictionary<ushort, SegmentDictionaryV1> dictionaries;

    private SegmentReaderV1(
        ReadOnlyMemory<byte> file,
        Guid segmentId,
        CaptureId captureId,
        ClockId clockId,
        TimestampEncoding encoding,
        NormalizerContractVersion derivation,
        SegmentTableId table,
        int rowCount,
        long minNativeTicks,
        long maxNativeTicks,
        IReadOnlyList<SegmentColumnDescriptor> ordered,
        IReadOnlyList<SegmentTimeBlockV1> timeBlocks,
        int variableChunkOffset,
        int variableChunkLength,
        Dictionary<ushort, SegmentDictionaryV1> dictionaries)
    {
        this.file = file;
        SegmentId = segmentId;
        CaptureId = captureId;
        ClockId = clockId;
        TimestampEncoding = encoding;
        Derivation = derivation;
        Table = table;
        RowCount = rowCount;
        MinNativeTicks = minNativeTicks;
        MaxNativeTicks = maxNativeTicks;
        Columns = ordered;
        TimeBlocks = timeBlocks;
        VariableChunkOffset = variableChunkOffset;
        VariableChunkLength = variableChunkLength;
        this.dictionaries = dictionaries;
        columns = ordered.ToDictionary(column => column.Id);
    }

    public Guid SegmentId { get; }

    public CaptureId CaptureId { get; }

    /// <summary>The clock every native reading in this segment is on. It is never assumed (I8).</summary>
    public ClockId ClockId { get; }

    public TimestampEncoding TimestampEncoding { get; }

    public NormalizerContractVersion Derivation { get; }

    public SegmentTableId Table { get; }

    public int RowCount { get; }

    public long MinNativeTicks { get; }

    public long MaxNativeTicks { get; }

    public IReadOnlyList<SegmentColumnDescriptor> Columns { get; }

    public IReadOnlyList<SegmentTimeBlockV1> TimeBlocks { get; }

    internal int VariableChunkOffset { get; }

    internal int VariableChunkLength { get; }

    /// <summary>
    /// Opens a segment, with the dictionaries its columns reference. A column whose dictionary was not
    /// supplied is readable as a code and refuses to produce a value, because guessing one would invent a
    /// name that no evidence carries.
    /// </summary>
    public static SegmentReaderV1 Open(
        ReadOnlyMemory<byte> segment,
        IReadOnlyList<SegmentDictionaryV1>? dictionaries = null)
    {
        ReadOnlySpan<byte> file = segment.Span;
        if (file.Length < SegmentFormatV1.HeaderLength + SegmentFormatV1.TrailerLength)
        {
            throw new InvalidDataException("A segment-v1 file is shorter than its own header.");
        }

        ReadOnlySpan<byte> header = file[..SegmentFormatV1.HeaderLength];
        if (BinaryPrimitives.ReadUInt64LittleEndian(header) != SegmentFormatV1.Magic)
        {
            throw new InvalidDataException("This file is not a segment-v1 segment.");
        }

        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
        if (major != SegmentFormatV1.FormatMajor)
        {
            throw new InvalidDataException(
                $"This segment declares format major {major}; this reader implements "
                + $"{SegmentFormatV1.FormatMajor}. An unknown major version is refused, never read at a "
                + "guessed layout.");
        }

        uint required = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        if (required != 0)
        {
            throw new InvalidDataException(
                $"This segment requires features 0x{required:x8}, none of which this reader implements. A "
                + "required feature is refused rather than ignored (§20.1).");
        }

        if (Crc32C.Compute(header[..120]) != BinaryPrimitives.ReadUInt32LittleEndian(header[120..]))
        {
            throw new InvalidDataException("A segment-v1 header fails its checksum.");
        }

        var segmentId = new Guid(header[16..32], bigEndian: true);
        var captureId = new CaptureId(new Guid(header[32..48], bigEndian: true));
        var clockId = new ClockId(new Guid(header[48..64], bigEndian: true));
        uint derivation = BinaryPrimitives.ReadUInt32LittleEndian(header[64..]);
        uint rowCount = BinaryPrimitives.ReadUInt32LittleEndian(header[68..]);
        long minTicks = BinaryPrimitives.ReadInt64LittleEndian(header[72..]);
        long maxTicks = BinaryPrimitives.ReadInt64LittleEndian(header[80..]);
        ushort columnCount = BinaryPrimitives.ReadUInt16LittleEndian(header[88..]);
        ushort timeBlockCount = BinaryPrimitives.ReadUInt16LittleEndian(header[90..]);
        var encoding = (TimestampEncoding)BinaryPrimitives.ReadUInt32LittleEndian(header[92..]);
        uint directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[96..]);
        uint timeBlockOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[100..]);
        uint chunkOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[104..]);
        uint chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(header[108..]);
        uint fileLength = BinaryPrimitives.ReadUInt32LittleEndian(header[112..]);
        var table = (SegmentTableId)BinaryPrimitives.ReadUInt32LittleEndian(header[116..]);

        if (!Enum.IsDefined(table))
        {
            throw new InvalidDataException(
                $"This segment holds table {(uint)table}, which this reader does not implement.");
        }

        if (!Enum.IsDefined(encoding))
        {
            throw new InvalidDataException(
                $"This segment declares timestamp encoding {(uint)encoding}, which §23 does not define.");
        }

        if (derivation == 0)
        {
            throw new InvalidDataException("A segment names the normalizer contract version it was derived under.");
        }

        if (rowCount is 0 or > SegmentFormatV1.MaximumRowsPerSegment)
        {
            throw new InvalidDataException(
                $"This segment declares {rowCount} rows. An empty segment is never published and one past "
                + $"{SegmentFormatV1.MaximumRowsPerSegment} rows is refused.");
        }

        if (fileLength != file.Length || fileLength > SegmentFormatV1.MaximumSegmentBytes)
        {
            throw new InvalidDataException(
                $"This segment declares {fileLength} bytes and is {file.Length}.");
        }

        if (directoryOffset != SegmentFormatV1.HeaderLength
            || timeBlockOffset != directoryOffset + ((uint)columnCount * SegmentFormatV1.ColumnEntryLength)
            || timeBlockCount is 0 or > SegmentFormatV1.MaximumTimeBlocks
            || columnCount == 0)
        {
            throw new InvalidDataException("A segment-v1 directory is not where its header says it is.");
        }

        long dataStart = timeBlockOffset + ((long)timeBlockCount * SegmentFormatV1.TimeBlockEntryLength);
        if (dataStart > fileLength
            || chunkLength > SegmentFormatV1.MaximumVariableChunkBytes
            || chunkOffset < dataStart
            || (long)chunkOffset + chunkLength + SegmentFormatV1.TrailerLength > fileLength)
        {
            throw new InvalidDataException("A segment-v1 variable chunk does not fit the file that declares it.");
        }

        Span<byte> digest = stackalloc byte[SegmentFormatV1.TrailerLength];
        SHA256.HashData(file[..(int)(fileLength - SegmentFormatV1.TrailerLength)], digest);
        if (!CryptographicOperations.FixedTimeEquals(digest, file[(int)(fileLength - SegmentFormatV1.TrailerLength)..]))
        {
            throw new InvalidDataException(
                $"Segment {segmentId:D} fails its trailing digest: its bytes are not the bytes that were "
                + "published.");
        }

        var ordered = new List<SegmentColumnDescriptor>(columnCount);
        var seen = new HashSet<SegmentColumnId>();
        for (int index = 0; index < columnCount; index++)
        {
            SegmentColumnDescriptor column = ReadColumnEntry(
                file.Slice((int)directoryOffset + (index * SegmentFormatV1.ColumnEntryLength), SegmentFormatV1.ColumnEntryLength),
                (int)rowCount,
                (int)dataStart,
                (int)fileLength);
            if (!seen.Add(column.Id))
            {
                throw new InvalidDataException($"Column {(ushort)column.Id} appears twice in one segment.");
            }

            ordered.Add(column);
        }

        var timeBlocks = new List<SegmentTimeBlockV1>(timeBlockCount);
        int expectedFirst = 0;
        for (int index = 0; index < timeBlockCount; index++)
        {
            ReadOnlySpan<byte> entry = file.Slice(
                (int)timeBlockOffset + (index * SegmentFormatV1.TimeBlockEntryLength),
                SegmentFormatV1.TimeBlockEntryLength);
            int firstRow = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry);
            int blockRows = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            long blockMin = BinaryPrimitives.ReadInt64LittleEndian(entry[8..]);
            long blockMax = BinaryPrimitives.ReadInt64LittleEndian(entry[16..]);
            if (firstRow != expectedFirst || blockRows < 1 || firstRow + blockRows > rowCount || blockMin > blockMax)
            {
                throw new InvalidDataException(
                    $"Time block {index} covers rows [{firstRow}, {firstRow + blockRows}) of {rowCount}, which "
                    + "does not continue the block before it.");
            }

            expectedFirst = firstRow + blockRows;
            timeBlocks.Add(new(firstRow, blockRows, blockMin, blockMax));
        }

        if (expectedFirst != rowCount)
        {
            throw new InvalidDataException(
                $"This segment's time blocks cover {expectedFirst} of {rowCount} rows.");
        }

        var byId = new Dictionary<ushort, SegmentDictionaryV1>();
        foreach (SegmentDictionaryV1 dictionary in dictionaries ?? [])
        {
            if (!byId.TryAdd(dictionary.DictionaryId, dictionary))
            {
                throw new ArgumentException(
                    $"Two dictionaries were supplied with id {dictionary.DictionaryId}.",
                    nameof(dictionaries));
            }
        }

        var reader = new SegmentReaderV1(
            segment,
            segmentId,
            captureId,
            clockId,
            encoding,
            new(derivation),
            table,
            (int)rowCount,
            minTicks,
            maxTicks,
            ordered,
            timeBlocks,
            (int)chunkOffset,
            (int)chunkLength,
            byId);
        reader.VerifyOrderAndExtents();
        return reader;
    }

    /// <summary>
    /// The dictionary ids a segment's columns reference, read from its header and directory alone. It lets a
    /// caller load exactly the dictionaries a segment needs before opening it, rather than opening it twice.
    /// </summary>
    public static IReadOnlyList<ushort> ReferencedDictionaryIds(ReadOnlySpan<byte> segment)
    {
        if (segment.Length < SegmentFormatV1.HeaderLength
            || BinaryPrimitives.ReadUInt64LittleEndian(segment) != SegmentFormatV1.Magic
            || BinaryPrimitives.ReadUInt16LittleEndian(segment[8..]) != SegmentFormatV1.FormatMajor)
        {
            throw new InvalidDataException("This file is not a segment-v1 segment this reader implements.");
        }

        if (Crc32C.Compute(segment[..120]) != BinaryPrimitives.ReadUInt32LittleEndian(segment[120..]))
        {
            throw new InvalidDataException("A segment-v1 header fails its checksum.");
        }

        ushort columnCount = BinaryPrimitives.ReadUInt16LittleEndian(segment[88..]);
        uint directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(segment[96..]);
        if (directoryOffset != SegmentFormatV1.HeaderLength
            || (long)directoryOffset + ((long)columnCount * SegmentFormatV1.ColumnEntryLength) > segment.Length)
        {
            throw new InvalidDataException("A segment-v1 directory is not where its header says it is.");
        }

        var ids = new List<ushort>();
        for (int index = 0; index < columnCount; index++)
        {
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(
                segment[((int)directoryOffset + (index * SegmentFormatV1.ColumnEntryLength) + 6)..]);
            if (id != 0 && !ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        ids.Sort();
        return ids;
    }

    /// <summary>
    /// Resolves and verifies one column once, for a caller that reads it for every row. A variable-reference
    /// column has no numeric value and is refused here rather than read as its offset.
    /// </summary>
    public SegmentColumnSlice Slice(SegmentColumnId id)
    {
        SegmentColumnDescriptor column = Require(id);
        if (column.Encoding == SegmentColumnEncoding.VariableReference)
        {
            throw new InvalidOperationException(
                $"Column {column.Id} holds a variable reference, not a number. Read it as text.");
        }

        Verify(column);
        int width = column.Encoding == SegmentColumnEncoding.Dictionary
            ? sizeof(uint)
            : SegmentFormatV1.WidthOf(column.Type);
        return new(
            column,
            file.Span.Slice(column.ValueOffset, column.ValueLength),
            column.Nullable ? file.Span.Slice(column.NullBitmapOffset, column.NullBitmapLength) : [],
            width);
    }

    /// <summary>The descriptor of one column, or null when this segment does not carry it.</summary>
    public SegmentColumnDescriptor? Column(SegmentColumnId id) =>
        columns.TryGetValue(id, out SegmentColumnDescriptor? column) ? column : null;

    /// <summary>
    /// One column's raw value bytes. The checksum is verified the first time a column is read, so a caller
    /// that reads two columns pays for two rather than for the whole file.
    /// </summary>
    public ReadOnlySpan<byte> ValueBytes(SegmentColumnId id)
    {
        SegmentColumnDescriptor column = Require(id);
        Verify(column);
        return file.Span.Slice(column.ValueOffset, column.ValueLength);
    }

    /// <summary>Whether a row has a value in a column. A column that is not nullable always has one.</summary>
    public bool HasValue(SegmentColumnId id, int row)
    {
        SegmentColumnDescriptor column = Require(id);
        RequireRow(row);
        if (!column.Nullable)
        {
            return true;
        }

        Verify(column);
        return (file.Span[column.NullBitmapOffset + (row >> 3)] & (1 << (row & 7))) != 0;
    }

    /// <summary>The unsigned value of a row, or null when the row has none.</summary>
    public ulong? UnsignedValue(SegmentColumnId id, int row)
    {
        SegmentColumnDescriptor column = Require(id);
        if (!HasValue(id, row))
        {
            return null;
        }

        int width = column.Encoding switch
        {
            SegmentColumnEncoding.Dictionary => sizeof(uint),
            SegmentColumnEncoding.VariableReference => throw new InvalidOperationException(
                $"Column {column.Id} holds a variable reference, not a number. Read it as text."),
            _ => SegmentFormatV1.WidthOf(column.Type),
        };
        ReadOnlySpan<byte> value = ValueBytes(id).Slice(row * width, width);
        return width switch
        {
            1 => value[0],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(value),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(value),
            8 => BinaryPrimitives.ReadUInt64LittleEndian(value),
            _ => throw new InvalidOperationException(
                $"Column {column.Id} is {width} bytes wide and is not read as a number."),
        };
    }

    /// <summary>The signed value of a row, or null when the row has none.</summary>
    public long? SignedValue(SegmentColumnId id, int row)
    {
        SegmentColumnDescriptor column = Require(id);
        if (!HasValue(id, row))
        {
            return null;
        }

        int width = SegmentFormatV1.WidthOf(column.Type);
        ReadOnlySpan<byte> value = ValueBytes(id).Slice(row * width, width);
        return width switch
        {
            4 => BinaryPrimitives.ReadInt32LittleEndian(value),
            8 => BinaryPrimitives.ReadInt64LittleEndian(value),
            1 => value[0],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(value),
            _ => throw new InvalidOperationException(
                $"Column {column.Id} is {width} bytes wide and is not read as a number."),
        };
    }

    /// <summary>The 16-byte identifier of a row, or null when the row has none.</summary>
    public Guid? IdentifierValue(SegmentColumnId id, int row)
    {
        SegmentColumnDescriptor column = Require(id);
        if (column.Type != SegmentColumnType.Guid16)
        {
            throw new InvalidOperationException($"Column {column.Id} is not a 16-byte identifier.");
        }

        return HasValue(id, row) ? new Guid(ValueBytes(id).Slice(row * 16, 16), bigEndian: true) : null;
    }

    /// <summary>
    /// The text of a row, or null when the row has none. A dictionary-coded column whose dictionary was not
    /// supplied refuses rather than returning a code rendered as a name.
    /// </summary>
    public string? TextValue(SegmentColumnId id, int row)
    {
        SegmentColumnDescriptor column = Require(id);
        if (column.Type != SegmentColumnType.Text)
        {
            throw new InvalidOperationException($"Column {column.Id} is not text.");
        }

        if (!HasValue(id, row))
        {
            return null;
        }

        if (column.Encoding == SegmentColumnEncoding.Dictionary)
        {
            uint code = BinaryPrimitives.ReadUInt32LittleEndian(ValueBytes(id).Slice(row * sizeof(uint), sizeof(uint)));
            return dictionaries.TryGetValue(column.DictionaryId, out SegmentDictionaryV1? dictionary)
                ? dictionary[code]
                : throw new InvalidOperationException(
                    $"Column {column.Id} references dictionary {column.DictionaryId}, which was not supplied. "
                    + "A code is not rendered as a name.");
        }

        ReadOnlySpan<byte> reference = ValueBytes(id).Slice(row * 2 * sizeof(uint), 2 * sizeof(uint));
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(reference);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(reference[sizeof(uint)..]);
        return (long)offset + length > VariableChunkLength
            ? throw new InvalidDataException(
                $"Row {row} of column {column.Id} spans [{offset}, {offset + length}) of a "
                + $"{VariableChunkLength}-byte chunk.")
            : Encoding.UTF8.GetString(file.Span.Slice(VariableChunkOffset + (int)offset, (int)length));
    }

    /// <summary>The descriptor schema behind a row, resolved through the segment's schema dictionary.</summary>
    public (Guid ProviderId, ushort EventId, byte Version, string Fingerprint) SchemaOf(int row)
    {
        SegmentColumnDescriptor column = Require(SegmentColumnId.SchemaCode);
        uint code = BinaryPrimitives.ReadUInt32LittleEndian(
            ValueBytes(SegmentColumnId.SchemaCode).Slice(row * sizeof(uint), sizeof(uint)));
        return dictionaries.TryGetValue(column.DictionaryId, out SegmentDictionaryV1? dictionary)
            ? SegmentFormatV1.ParseSchemaEntry(dictionary[code])
            : throw new InvalidOperationException(
                $"This segment's schema dictionary {column.DictionaryId} was not supplied, so a row's "
                + "descriptor cannot be resolved.");
    }

    /// <summary>Materializes one row. It is the inspection path, not the aggregation path.</summary>
    public ObservationRowV1 Row(int row)
    {
        RequireRow(row);
        (Guid ProviderId, ushort EventId, byte Version, string Fingerprint) schema = SchemaOf(row);
        return new()
        {
            RawStreamId = (uint)UnsignedValue(SegmentColumnId.RawStreamId, row)!.Value,
            RawSourceEpoch = (uint)UnsignedValue(SegmentColumnId.RawSourceEpoch, row)!.Value,
            RawRecordOrdinal = UnsignedValue(SegmentColumnId.RawRecordOrdinal, row)!.Value,
            JournalRecordIndex = UnsignedValue(SegmentColumnId.JournalRecordIndex, row),
            FactKey = new(
                UnsignedValue(SegmentColumnId.FactKeyHigh, row)!.Value,
                UnsignedValue(SegmentColumnId.FactKeyLow, row)!.Value),
            ProviderId = schema.ProviderId,
            EventId = schema.EventId,
            DescriptorVersion = schema.Version,
            SchemaFingerprint = schema.Fingerprint,
            Opcode = (byte)UnsignedValue(SegmentColumnId.Opcode, row)!.Value,
            NativeTicks = SignedValue(SegmentColumnId.NativeTicks, row)!.Value,
            SessionRelativeTicks = SignedValue(SegmentColumnId.SessionRelativeTicks, row),
            HeaderProcessId = (int)SignedValue(SegmentColumnId.HeaderProcessId, row)!.Value,
            HeaderThreadId = (int)SignedValue(SegmentColumnId.HeaderThreadId, row)!.Value,
            ProcessorNumber = (ushort)UnsignedValue(SegmentColumnId.ProcessorNumber, row)!.Value,
            ActivityId = IdentifierValue(SegmentColumnId.ActivityId, row),
            RelatedActivityId = IdentifierValue(SegmentColumnId.RelatedActivityId, row),
            Mechanism = Code<Mechanism>(SegmentColumnId.Mechanism, row),
            Layer = Code<ObservationLayer>(SegmentColumnId.Layer, row),
            Kind = Code<ObservationKind>(SegmentColumnId.ObservationKind, row),
            Direction = Code<Direction>(SegmentColumnId.Direction, row),
            OwnerProcessId = SignedValue(SegmentColumnId.OwnerProcessId, row) is { } owner ? (int)owner : null,
            ResourceName = TextValue(SegmentColumnId.ResourceName, row),
            SourceIdentifier = IdentifierValue(SegmentColumnId.SourceIdentifier, row),
            EndpointAddressFamily = UnsignedValue(SegmentColumnId.EndpointAddressFamily, row) is { } family
                ? (byte)family
                : null,
            SourceEndpointAddress = UnsignedValue(SegmentColumnId.SourceEndpointAddress, row) is { } source
                ? (uint)source
                : null,
            SourceEndpointPort = UnsignedValue(SegmentColumnId.SourceEndpointPort, row) is { } sourcePort
                ? (ushort)sourcePort
                : null,
            DestinationEndpointAddress = UnsignedValue(SegmentColumnId.DestinationEndpointAddress, row) is { } peer
                ? (uint)peer
                : null,
            DestinationEndpointPort = UnsignedValue(SegmentColumnId.DestinationEndpointPort, row) is { } peerPort
                ? (ushort)peerPort
                : null,
            ByteValue = SignedValue(SegmentColumnId.ByteValue, row),
            ByteDomain = OptionalCode<ByteDomain>(SegmentColumnId.ByteDomain, row),
            AccountingSide = OptionalCode<AccountingSide>(SegmentColumnId.AccountingSide, row),
            MeasurementUnit = OptionalCode<MeasurementUnit>(SegmentColumnId.MeasurementUnit, row),
            ByteAvailability = Code<FieldAvailability>(SegmentColumnId.ByteAvailability, row),
            StatusCode = SignedValue(SegmentColumnId.StatusCode, row),
            StatusAvailability = Code<FieldAvailability>(SegmentColumnId.StatusAvailability, row),
            AttributionQuality = Code<QualityLevel>(SegmentColumnId.AttributionQuality, row),
            CorrelationQuality = Code<QualityLevel>(SegmentColumnId.CorrelationQuality, row),
            MeasurementQuality = Code<QualityLevel>(SegmentColumnId.MeasurementQuality, row),
            TimingQuality = Code<QualityLevel>(SegmentColumnId.TimingQuality, row),
            Markers = Markers(row),
        };
    }

    /// <summary>The observation identity of a row, from the capture and derivation the segment names.</summary>
    public ObservationId ObservationIdOf(int row) => Row(row).ObservationIdIn(CaptureId, Derivation);

    /// <summary>
    /// The rows whose native reading falls inside a half-open interval on this segment's clock, as the row range
    /// <c>[First, End)</c>. Rows are sorted by native reading and the reader has already verified that order, so
    /// the range is contiguous and is found by search rather than by a scan (I3, §10.2). An interval that misses
    /// the segment is the empty range, never an error.
    /// </summary>
    public (int First, int End) RowsWithin(TimeRange interval)
    {
        if (interval.EndTicks <= MinNativeTicks || interval.StartTicks > MaxNativeTicks)
        {
            return (0, 0);
        }

        SegmentColumnSlice ticks = Slice(SegmentColumnId.NativeTicks);
        return (FirstAtOrAfter(ticks, interval.StartTicks), FirstAtOrAfter(ticks, interval.EndTicks));
    }

    /// <summary>The first row whose native reading is at or after a reading, or the row count when none is.</summary>
    private int FirstAtOrAfter(SegmentColumnSlice ticks, long reading)
    {
        int low = 0;
        int high = RowCount;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (ticks.SignedAt(middle)!.Value < reading)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static SegmentColumnDescriptor ReadColumnEntry(
        ReadOnlySpan<byte> entry,
        int rowCount,
        int dataStart,
        int fileLength)
    {
        var id = (SegmentColumnId)BinaryPrimitives.ReadUInt16LittleEndian(entry);
        var type = (SegmentColumnType)entry[2];
        var encoding = (SegmentColumnEncoding)entry[3];
        bool nullable = entry[4] switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidDataException($"Column {(ushort)id} declares nullability {entry[4]}."),
        };

        if (!Enum.IsDefined(id))
        {
            throw new InvalidDataException(
                $"Column {(ushort)id} is required by this table's layout and is not a code §20.1 defines. The "
                + "segment is refused rather than read with a column of unknown meaning.");
        }

        if (!Enum.IsDefined(type) || !Enum.IsDefined(encoding))
        {
            throw new InvalidDataException(
                $"Column {id} declares type {(byte)type} and encoding {(byte)encoding}, which this reader does "
                + "not implement.");
        }

        int entryRows = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
        int valueOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
        int valueLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[16..]);
        int bitmapOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[20..]);
        int bitmapLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[24..]);
        int known = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[28..]);
        int unknown = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[32..]);
        uint valueCrc = BinaryPrimitives.ReadUInt32LittleEndian(entry[36..]);
        uint bitmapCrc = BinaryPrimitives.ReadUInt32LittleEndian(entry[40..]);

        int width = encoding switch
        {
            SegmentColumnEncoding.Dictionary => sizeof(uint),
            SegmentColumnEncoding.VariableReference => 2 * sizeof(uint),
            _ => SegmentFormatV1.WidthOf(type),
        };
        if (entryRows != rowCount || valueLength != rowCount * width)
        {
            throw new InvalidDataException(
                $"Column {id} declares {entryRows} rows over {valueLength} bytes where the segment has "
                + $"{rowCount} rows of {width} bytes.");
        }

        if (valueOffset < dataStart || (long)valueOffset + valueLength + SegmentFormatV1.TrailerLength > fileLength)
        {
            throw new InvalidDataException($"Column {id} spans bytes outside the file that declares it.");
        }

        int expectedBitmap = nullable ? (rowCount + 7) / 8 : 0;
        if (bitmapLength != expectedBitmap
            || (nullable
                && (bitmapOffset < dataStart
                    || (long)bitmapOffset + bitmapLength + SegmentFormatV1.TrailerLength > fileLength)))
        {
            throw new InvalidDataException($"Column {id}'s null bitmap does not fit the file that declares it.");
        }

        if (known < 0 || unknown < 0 || known + unknown != rowCount || (!nullable && unknown != 0))
        {
            throw new InvalidDataException(
                $"Column {id} reports {known} known and {unknown} unknown values over {rowCount} rows.");
        }

        if (encoding == SegmentColumnEncoding.Dictionary
            && BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]) == 0)
        {
            throw new InvalidDataException($"Column {id} is dictionary-coded and names no dictionary.");
        }

        return new(
            id,
            type,
            encoding,
            nullable,
            BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]),
            entryRows,
            valueOffset,
            valueLength,
            bitmapOffset,
            bitmapLength,
            known,
            unknown,
            valueCrc,
            bitmapCrc);
    }

    /// <summary>
    /// Checks the one column everything else rests on. The time column decides the segment's order, its
    /// min/max and its time blocks, so it is read at open and compared with what the metadata claims rather
    /// than trusted; every other column is verified when it is first read.
    /// </summary>
    private void VerifyOrderAndExtents()
    {
        SegmentColumnDescriptor time = Require(SegmentColumnId.NativeTicks);
        Verify(time);
        ReadOnlySpan<byte> ticks = file.Span.Slice(time.ValueOffset, time.ValueLength);
        long previous = long.MinValue;
        for (int row = 0; row < RowCount; row++)
        {
            long value = BinaryPrimitives.ReadInt64LittleEndian(ticks[(row * sizeof(long))..]);
            if (row > 0 && value < previous)
            {
                throw new InvalidDataException(
                    $"This segment's rows are not in time order: row {row} reads {value} after {previous}. A "
                    + "segment is sorted by contract, and reading an unsorted one would break the boundary "
                    + "arithmetic every viewport depends on.");
            }

            previous = value;
        }

        long first = BinaryPrimitives.ReadInt64LittleEndian(ticks);
        long last = BinaryPrimitives.ReadInt64LittleEndian(ticks[((RowCount - 1) * sizeof(long))..]);
        if (first != MinNativeTicks || last != MaxNativeTicks)
        {
            throw new InvalidDataException(
                $"This segment declares [{MinNativeTicks}, {MaxNativeTicks}] and its rows read "
                + $"[{first}, {last}].");
        }

        foreach (SegmentTimeBlockV1 block in TimeBlocks)
        {
            long blockFirst = BinaryPrimitives.ReadInt64LittleEndian(ticks[(block.FirstRow * sizeof(long))..]);
            long blockLast = BinaryPrimitives.ReadInt64LittleEndian(
                ticks[((block.FirstRow + block.RowCount - 1) * sizeof(long))..]);
            if (blockFirst != block.MinNativeTicks || blockLast != block.MaxNativeTicks)
            {
                throw new InvalidDataException(
                    $"Time block at row {block.FirstRow} declares [{block.MinNativeTicks}, "
                    + $"{block.MaxNativeTicks}] where its rows read [{blockFirst}, {blockLast}].");
            }
        }
    }

    private SegmentRowMarkers Markers(int row)
    {
        var markers = (SegmentRowMarkers)UnsignedValue(SegmentColumnId.Markers, row)!.Value;
        return (markers & ~SegmentRowMarkers.All) == 0
            ? markers
            : throw new InvalidDataException(
                $"Row {row} carries marker bits 0x{(ushort)(markers & ~SegmentRowMarkers.All):x4} this reader "
                + "does not define. They are refused rather than read as unset.");
    }

    private T Code<T>(SegmentColumnId id, int row)
        where T : struct, Enum
    {
        ulong value = UnsignedValue(id, row)
            ?? throw new InvalidDataException($"Row {row} has no value in required column {id}.");
        var code = (T)Enum.ToObject(typeof(T), (int)value);
        return Enum.IsDefined(code)
            ? code
            : throw new InvalidDataException(
                $"Row {row} of column {id} carries code {value}, which §23 does not define. A required field "
                + "with an unknown code refuses the artifact (§23).");
    }

    private T? OptionalCode<T>(SegmentColumnId id, int row)
        where T : struct, Enum
    {
        if (UnsignedValue(id, row) is not { } value)
        {
            return null;
        }

        var code = (T)Enum.ToObject(typeof(T), (int)value);
        return Enum.IsDefined(code)
            ? code
            : throw new InvalidDataException(
                $"Row {row} of column {id} carries code {value}, which §23 does not define.");
    }

    private SegmentColumnDescriptor Require(SegmentColumnId id) =>
        columns.TryGetValue(id, out SegmentColumnDescriptor? column)
            ? column
            : throw new InvalidDataException(
                $"This segment does not carry column {id}, which `observation-v1` requires.");

    private void RequireRow(int row)
    {
        if (row < 0 || row >= RowCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(row),
                row,
                $"This segment has {RowCount} rows.");
        }
    }

    private void Verify(SegmentColumnDescriptor column)
    {
        if (!verified.Add(column.Id))
        {
            return;
        }

        if (Crc32C.Compute(file.Span.Slice(column.ValueOffset, column.ValueLength)) != column.ValueCrc32C)
        {
            _ = verified.Remove(column.Id);
            throw new InvalidDataException($"Column {column.Id} fails its checksum.");
        }

        if (column.Nullable
            && Crc32C.Compute(file.Span.Slice(column.NullBitmapOffset, column.NullBitmapLength))
                != column.NullBitmapCrc32C)
        {
            _ = verified.Remove(column.Id);
            throw new InvalidDataException($"Column {column.Id}'s null bitmap fails its checksum.");
        }

        if (!column.Nullable)
        {
            return;
        }

        int known = 0;
        ReadOnlySpan<byte> bitmap = file.Span.Slice(column.NullBitmapOffset, column.NullBitmapLength);
        for (int row = 0; row < RowCount; row++)
        {
            if ((bitmap[row >> 3] & (1 << (row & 7))) != 0)
            {
                known++;
            }
        }

        if (known != column.KnownCount)
        {
            _ = verified.Remove(column.Id);
            throw new InvalidDataException(
                $"Column {column.Id} reports {column.KnownCount} known values and its bitmap sets {known}. An "
                + "availability counter that disagrees with the data would make a denominator a guess.");
        }

        // Bits past the row count have no row to describe. A set one would mean the bitmap describes rows
        // the segment does not have, which makes the column's extent ambiguous rather than harmless.
        for (int bit = RowCount; bit < column.NullBitmapLength * 8; bit++)
        {
            if ((bitmap[bit >> 3] & (1 << (bit & 7))) != 0)
            {
                _ = verified.Remove(column.Id);
                throw new InvalidDataException(
                    $"Column {column.Id}'s null bitmap sets bit {bit} past its {RowCount} rows.");
            }
        }
    }
}
