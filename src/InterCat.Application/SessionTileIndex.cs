using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// The persisted timeline tiles of a generation's observation segments (`contracts/tile-index-v1.md`): §12.1 S4's deeper
/// levels, beneath the persisted overview. Each segment's tiles are kept at decimal levels - its finest, as
/// <see cref="SegmentTimeTiles"/> builds them, and each ten times wider above it - with the records of every finest tile
/// beside them. A zoom counts a segment from what it draws: a tile whole wherever its records fall in one column, a tile's
/// children where a column boundary falls among its records, and a finest tile's records only where one falls among
/// them, so it opens no segment and reads in proportion to its view rather than to the session (S3). Every byte it
/// interprets carries a checksum of its own, as a segment's do, so it is read by the block, never hashed whole first.
/// </summary>
internal static class SessionTileIndex
{
    /// <summary>How many tiles one checked block of a level holds.</summary>
    public const int TilesPerBlock = 32;

    /// <summary>The bytes before the first section: magic, version, session and the generation the tiles describe.</summary>
    public const int HeaderLength = 48;

    /// <summary>The bytes after the directory, which say where it is.</summary>
    public const int FooterLength = 32;

    /// <summary>A level of at most this many tiles is the top one: one block holds it.</summary>
    private const int TopLevelTiles = TilesPerBlock;

    /// <summary>The most levels a section has: a finest tile one tick wide, and eighteen decades above it.</summary>
    private const int MaximumLevels = 20;

    /// <summary>The longest directory a reader reads: far beyond what any generation's segments need.</summary>
    private const int MaximumDirectoryBytes = 64 * 1024 * 1024;

    /// <summary>The longest a section's header can be, with every mechanism and level a section may name.</summary>
    private const int MaximumSectionHeaderBytes = 55 + (2 * 256) + (12 * MaximumLevels);

    /// <summary>The fixed bytes of a block after its tiles: the end of the records it covers, then its checksum.</summary>
    private const int BlockTrailerBytes = 8;

    /// <summary>The most a finest tile's records take apiece: a ten-byte increase and the mechanism's slot.</summary>
    private const int MaximumRecordBytes = 11;

    private const string FilePrefix = "tiles-";
    private const string FileSuffix = ".bin";
    private const string What = "persisted tile index";
    private const ushort Major = 1;
    private const ushort Minor = 0;
    private const byte Ordered = 1;
    private const byte Timed = 2;

    private static ReadOnlySpan<byte> Magic => "ICATTILE"u8;

    /// <summary>The name a tile index published by <paramref name="generation"/> takes.</summary>
    public static string FileNameFor(long generation)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);
        return string.Create(CultureInfo.InvariantCulture, $"{FilePrefix}{generation:D10}{FileSuffix}");
    }

    /// <summary>The tile index <paramref name="manifest"/> names, or null. A generation naming two is refused.</summary>
    public static StoreDependency? NamedBy(SessionManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        StoreDependency[] named = [.. manifest.Dependencies.Where(dependency =>
            dependency.Kind == StoreDependencyKind.Index
            && dependency.Name.Length == FilePrefix.Length + 10 + FileSuffix.Length
            && dependency.Name.StartsWith(FilePrefix, StringComparison.Ordinal)
            && dependency.Name.EndsWith(FileSuffix, StringComparison.Ordinal))];
        return named.Length switch
        {
            0 => null,
            1 => named[0],
            _ => throw new InvalidDataException(
                $"Generation {manifest.Generation} names {named.Length} tile indexes; a generation names one."),
        };
    }

    /// <summary>
    /// Writes the tiles of <paramref name="segments"/>, generation <paramref name="derivedGeneration"/>'s observation
    /// segments, in the canonical order of tile-index-v1: its header, a section per segment by name, the directory and the
    /// footer.
    /// </summary>
    /// <returns>How many bytes were written.</returns>
    public static long Write(
        Stream destination,
        Guid sessionId,
        long derivedGeneration,
        IReadOnlyList<(StoreDependency Dependency, SegmentReaderV1 Reader)> segments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentOutOfRangeException.ThrowIfLessThan(derivedGeneration, 1);
        var writer = new IndexFileWriter(destination, long.MaxValue, What);

        byte[] header = new byte[HeaderLength];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), Major);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), Minor);
        if (!sessionId.TryWriteBytes(header.AsSpan(16)))
        {
            throw new InvalidOperationException("A GUID did not fit its 16 bytes.");
        }

        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(32), derivedGeneration);
        Crc32C.Write(header.AsSpan(HeaderLength - 4), Crc32C.Compute(header.AsSpan(0, HeaderLength - 4)));
        writer.Raw(header);

        var directory = new MemoryStream();
        var entries = new IndexFileWriter(directory, MaximumDirectoryBytes, What);
        foreach ((StoreDependency dependency, SegmentReaderV1 reader) in segments
            .OrderBy(segment => segment.Dependency.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            long offset = writer.Written;
            WriteSection(writer, reader, cancellationToken);
            entries.Str8(dependency.Name);
            entries.I64(dependency.LengthBytes);
            entries.Str8(dependency.Digest);
            entries.I64(offset);
            entries.I64(writer.Written - offset);
        }

        entries.Flush();
        long directoryOffset = writer.Written;
        byte[] listed = directory.ToArray();
        writer.Raw(listed);

        byte[] footer = new byte[FooterLength];
        BinaryPrimitives.WriteInt64LittleEndian(footer, directoryOffset);
        BinaryPrimitives.WriteInt32LittleEndian(footer.AsSpan(8), listed.Length);
        BinaryPrimitives.WriteInt32LittleEndian(footer.AsSpan(12), segments.Count);
        Crc32C.Write(footer.AsSpan(16), Crc32C.Compute(listed));
        Crc32C.Write(footer.AsSpan(FooterLength - 4), Crc32C.Compute(footer.AsSpan(0, FooterLength - 4)));
        writer.Raw(footer);
        writer.Flush();
        return writer.Written;
    }

    /// <summary>
    /// One segment's section: its header, its levels from the finest up, block by block, and the records of its finest
    /// tiles, block by block. A segment whose timed readings go backwards, or that has none, keeps no tiles: a zoom reads
    /// the first's rows, and the second has none to count.
    /// </summary>
    private static void WriteSection(IndexFileWriter writer, SegmentReaderV1 segment, CancellationToken cancellationToken)
    {
        SegmentTimeTiles tiles = SegmentTimeTiles.Of(segment, cancellationToken);
        bool timed = tiles.First is not null;
        bool usable = timed && tiles.Ordered && tiles.Count > 0;
        Mechanism[] mechanisms = usable ? tiles.Mechanisms.ToArray() : [];
        List<Level> levels = usable ? Levels(tiles, mechanisms.Length) : [];
        byte[] records = usable ? Records(segment, tiles, mechanisms, levels[0], cancellationToken) : [];

        int tileBytes = TileBytes(mechanisms.Length);
        int headerBytes = 55 + (2 * mechanisms.Length) + (12 * levels.Count);
        long[] levelOffsets = new long[levels.Count];
        long next = headerBytes;
        for (int level = 0; level < levels.Count; level++)
        {
            levelOffsets[level] = next;
            next += LevelBytes(levels[level].Count, tileBytes);
        }

        byte[] head = new byte[headerBytes];
        var at = new SpanWriter(head);
        at.I32(tiles.Rows);
        at.I32(tiles.Untimed);
        at.U8((byte)((tiles.Ordered ? Ordered : 0) | (timed ? Timed : 0)));
        at.I64(tiles.First ?? 0);
        at.I64(tiles.Last ?? 0);
        at.I64(usable ? tiles.Width : 1);
        at.U8((byte)mechanisms.Length);
        foreach (Mechanism mechanism in mechanisms)
        {
            at.U16((ushort)mechanism);
        }

        at.U8((byte)levels.Count);
        for (int level = 0; level < levels.Count; level++)
        {
            at.I32(levels[level].Count);
            at.I64(levelOffsets[level]);
        }

        at.I64(next);
        at.I64(records.Length);
        Crc32C.Write(head.AsSpan(headerBytes - 4), Crc32C.Compute(head.AsSpan(0, headerBytes - 4)));
        writer.Raw(head);

        byte[] block = new byte[(TilesPerBlock * tileBytes) + BlockTrailerBytes];
        for (int level = 0; level < levels.Count; level++)
        {
            Level tilesOf = levels[level];
            for (int first = 0; first < tilesOf.Count; first += TilesPerBlock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = Math.Min(TilesPerBlock, tilesOf.Count - first);
                var into = new SpanWriter(block);
                for (int tile = first; tile < first + count; tile++)
                {
                    into.I64(tilesOf.Lows[tile]);
                    into.I64(tilesOf.Highs[tile]);
                    into.I32(tilesOf.Links[tile]);
                    into.I32(tilesOf.Children[tile]);
                    for (int slot = 0; slot < mechanisms.Length; slot++)
                    {
                        into.I32(tilesOf.Counts[(tile * mechanisms.Length) + slot]);
                    }
                }

                // A finest block says where its records end, so they are read and checked without the next block.
                into.I32(level == 0 ? tilesOf.RecordsEnd[first / TilesPerBlock] : 0);
                int length = (count * tileBytes) + 4;
                Crc32C.Write(block.AsSpan(length), Crc32C.Compute(block.AsSpan(0, length)));
                writer.Raw(block.AsSpan(0, length + 4));
            }
        }

        writer.Raw(records);
    }

    /// <summary>A section's levels, from the finest up to the first that one block holds.</summary>
    private static List<Level> Levels(SegmentTimeTiles tiles, int mechanisms)
    {
        var finest = new Level(tiles.Count, mechanisms);
        for (int tile = 0; tile < tiles.Count; tile++)
        {
            (long low, long high) = tiles.Readings(tile);
            finest.Lows[tile] = low;
            finest.Highs[tile] = high;
            finest.Indexes[tile] = tiles.IndexOf(tile);
            for (int slot = 0; slot < mechanisms; slot++)
            {
                finest.Counts[(tile * mechanisms) + slot] = tiles.CountAt(tile, slot);
            }
        }

        var levels = new List<Level> { finest };
        while (levels[^1].Count > TopLevelTiles && levels.Count < MaximumLevels)
        {
            levels.Add(Above(levels[^1], mechanisms));
        }

        return levels;
    }

    /// <summary>The level ten times wider than <paramref name="below"/>: each of its tiles the run of tiles it holds.</summary>
    private static Level Above(Level below, int mechanisms)
    {
        var parents = new List<int>();
        for (int tile = 0; tile < below.Count; tile++)
        {
            if (tile == 0 || SegmentTimeTiles.FloorDivide(below.Indexes[tile], 10) != SegmentTimeTiles.FloorDivide(below.Indexes[tile - 1], 10))
            {
                parents.Add(tile);
            }
        }

        var level = new Level(parents.Count, mechanisms);
        for (int parent = 0; parent < parents.Count; parent++)
        {
            int first = parents[parent];
            int end = parent + 1 < parents.Count ? parents[parent + 1] : below.Count;
            level.Lows[parent] = below.Lows[first];
            level.Highs[parent] = below.Highs[end - 1];
            level.Indexes[parent] = SegmentTimeTiles.FloorDivide(below.Indexes[first], 10);
            level.Links[parent] = first;
            level.Children[parent] = end - first;
            for (int child = first; child < end; child++)
            {
                for (int slot = 0; slot < mechanisms; slot++)
                {
                    level.Counts[(parent * mechanisms) + slot] = checked(
                        level.Counts[(parent * mechanisms) + slot] + below.Counts[(child * mechanisms) + slot]);
                }
            }
        }

        return level;
    }

    /// <summary>
    /// The records of every finest tile, block by block, each block's after the one before and followed by its checksum:
    /// each record its reading's increase over the record before it in its tile - the first's over the tile's earliest,
    /// so none - then its mechanism's slot. The tiles' links and each block's end are set as they are written.
    /// </summary>
    private static byte[] Records(
        SegmentReaderV1 segment, SegmentTimeTiles tiles, Mechanism[] mechanisms, Level finest, CancellationToken cancellationToken)
    {
        SegmentColumnSlice times = segment.Slice(SegmentColumnId.SessionRelativeTicks);
        SegmentColumnSlice kinds = segment.Slice(SegmentColumnId.Mechanism);
        Span<int> slotOf = stackalloc int[TimelineColumns.MechanismCodes];
        slotOf.Fill(-1);
        for (int slot = 0; slot < mechanisms.Length; slot++)
        {
            slotOf[(int)mechanisms[slot]] = slot;
        }

        var bytes = new MemoryStream();
        Span<byte> encoded = stackalloc byte[MaximumRecordBytes];
        int blockStart = 0;
        for (int tile = 0; tile < finest.Count; tile++)
        {
            if ((tile & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            finest.Links[tile] = checked((int)bytes.Length);
            long previous = finest.Lows[tile];
            (int firstRow, int endRow) = tiles.RowsOf(tile);
            for (int row = firstRow; row < endRow; row++)
            {
                if (times.SignedAt(row) is not { } nanoseconds)
                {
                    continue;
                }

                long tick = nanoseconds / 100;
                int length = Unsigned(encoded, (ulong)(tick - previous));
                encoded[length++] = (byte)slotOf[(int)(Mechanism)kinds.UnsignedAt(row)!.Value];
                bytes.Write(encoded[..length]);
                previous = tick;
            }

            if (tile % TilesPerBlock == TilesPerBlock - 1 || tile == finest.Count - 1)
            {
                int end = checked((int)bytes.Length);
                finest.RecordsEnd[tile / TilesPerBlock] = end;
                byte[] checksum = new byte[4];
                Crc32C.Write(checksum, Crc32C.Compute(bytes.GetBuffer().AsSpan(blockStart, end - blockStart)));
                bytes.Write(checksum);
                blockStart = checked((int)bytes.Length);
            }
        }

        return bytes.ToArray();
    }

    /// <summary>Writes <paramref name="value"/> as LEB128, seven bits a byte, lowest first; returns how many bytes.</summary>
    private static int Unsigned(Span<byte> destination, ulong value)
    {
        int length = 0;
        while (value >= 0x80)
        {
            destination[length++] = (byte)(value | 0x80);
            value >>= 7;
        }

        destination[length++] = (byte)value;
        return length;
    }

    private static int TileBytes(int mechanisms) => 24 + (4 * mechanisms);

    private static long LevelBytes(int tiles, int tileBytes) =>
        ((long)tiles * tileBytes) + ((long)((tiles + TilesPerBlock - 1) / TilesPerBlock) * BlockTrailerBytes);

    /// <summary>
    /// Opens the tile index <paramref name="dependency"/> names: its header, footer and directory, each checked. A section
    /// is read when a zoom first needs it, and a block when a zoom reads it.
    /// </summary>
    public static TileIndexFile Open(IOwnedDirectory directory, StoreDependency dependency, Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(dependency);
        using FileStream stream = directory.OpenOwnedFile(
            dependency.Name, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        if (stream.Length != dependency.LengthBytes)
        {
            throw new InvalidDataException($"{dependency.LengthMismatch(stream.Length)}.");
        }

        if (stream.Length < HeaderLength + FooterLength)
        {
            throw Invalid("it is shorter than its header and footer.");
        }

        byte[] header = Read(stream, 0, HeaderLength);
        if (!header.AsSpan(0, 8).SequenceEqual(Magic))
        {
            throw Invalid("it does not begin as one does.");
        }

        Check(header, "its header");
        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8));
        if (major != Major)
        {
            throw Invalid(string.Create(CultureInfo.InvariantCulture, $"it is version {major}, and this build reads version {Major}."));
        }

        if (new Guid(header.AsSpan(16, 16)) != sessionId)
        {
            throw Invalid("it describes another session.");
        }

        long derivedGeneration = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(32));
        byte[] footer = Read(stream, stream.Length - FooterLength, FooterLength);
        Check(footer, "its footer");
        long directoryOffset = BinaryPrimitives.ReadInt64LittleEndian(footer);
        int directoryLength = BinaryPrimitives.ReadInt32LittleEndian(footer.AsSpan(8));
        int sections = BinaryPrimitives.ReadInt32LittleEndian(footer.AsSpan(12));
        if (directoryLength is < 0 or > MaximumDirectoryBytes || sections < 0
            || directoryOffset < HeaderLength || directoryOffset + directoryLength != stream.Length - FooterLength)
        {
            throw Invalid("its footer places its directory outside it.");
        }

        byte[] listed = Read(stream, directoryOffset, directoryLength);
        if (Crc32C.Compute(listed) != BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(16)))
        {
            throw Invalid("its directory does not match its checksum.");
        }

        var reader = new IndexFileReader(listed, What);
        var entries = new Dictionary<string, TileSectionEntry>(StringComparer.Ordinal);
        long end = HeaderLength;
        string? previous = null;
        for (int index = 0; index < sections; index++)
        {
            string name = reader.Str8();
            long segmentLength = reader.I64();
            string digest = reader.Str8();
            long offset = reader.I64();
            long length = reader.I64();
            if (previous is not null && string.CompareOrdinal(previous, name) >= 0)
            {
                throw Invalid("its directory does not list its segments once each, by name.");
            }

            if (offset != end || length < 4 || offset + length > directoryOffset)
            {
                throw Invalid($"its directory places the section of '{name}' outside its sections.");
            }

            entries.Add(name, new(name, segmentLength, digest, offset, length));
            end = offset + length;
            previous = name;
        }

        reader.RequireEnd();
        if (end != directoryOffset)
        {
            throw Invalid("bytes lie between its last section and its directory.");
        }

        return new TileIndexFile(dependency, derivedGeneration, entries);
    }

    internal static byte[] Read(FileStream stream, long offset, int length)
    {
        byte[] bytes = new byte[length];
        int read = 0;
        while (read < length)
        {
            int taken = RandomAccess.Read(stream.SafeFileHandle, bytes.AsSpan(read), offset + read);
            if (taken == 0)
            {
                throw Invalid("it ends inside a part its directory names.");
            }

            read += taken;
        }

        return bytes;
    }

    /// <summary>Refuses <paramref name="bytes"/> unless they end with the checksum of what is before it.</summary>
    internal static void Check(ReadOnlySpan<byte> bytes, string what)
    {
        if (Crc32C.Compute(bytes[..^4]) != BinaryPrimitives.ReadUInt32LittleEndian(bytes[^4..]))
        {
            throw Invalid($"{what} does not match its checksum.");
        }
    }

    internal static InvalidDataException Invalid(string reason) => new($"The {What} is not readable: {reason}");

    /// <summary>A section's header, read and checked once: what its segment holds and where each of its levels lies.</summary>
    internal static TileSection ReadSection(FileStream stream, TileSectionEntry entry)
    {
        byte[] bytes = Read(stream, entry.Offset, (int)Math.Min(entry.Length, MaximumSectionHeaderBytes));
        var at = new SpanReader(bytes, entry.Name);
        int rows = at.I32();
        int untimed = at.I32();
        byte flags = at.U8();
        long first = at.I64();
        long last = at.I64();
        long width = at.I64();
        int mechanisms = at.U8();
        var kinds = new Mechanism[mechanisms];
        for (int slot = 0; slot < mechanisms; slot++)
        {
            kinds[slot] = (Mechanism)at.U16();
            if (!Enum.IsDefined(kinds[slot]) || (slot > 0 && kinds[slot] <= kinds[slot - 1]))
            {
                throw Invalid($"the section of '{entry.Name}' names its mechanisms out of order or names one no build defines.");
            }
        }

        int levels = at.U8();
        if (levels > MaximumLevels)
        {
            throw Invalid($"the section of '{entry.Name}' has more levels than a segment's tiles need.");
        }

        var counts = new int[levels];
        var offsets = new long[levels];
        for (int level = 0; level < levels; level++)
        {
            counts[level] = at.I32();
            offsets[level] = at.I64();
        }

        long recordsOffset = at.I64();
        long recordsLength = at.I64();
        int headerLength = at.Position + 4;
        Check(bytes.AsSpan(0, headerLength), $"the header of the section of '{entry.Name}'");
        bool ordered = (flags & Ordered) != 0;
        bool timed = (flags & Timed) != 0;
        bool usable = ordered && timed;
        int tileBytes = TileBytes(mechanisms);
        long next = headerLength;
        bool laidOut = (flags & ~(Ordered | Timed)) == 0 && rows >= 0 && untimed >= 0 && untimed <= rows
            && (!timed || first <= last) && IsPowerOfTen(width)
            && (usable ? levels > 0 && mechanisms > 0 : levels == 0 && mechanisms == 0 && recordsLength == 0);
        for (int level = 0; laidOut && level < levels; level++)
        {
            laidOut = counts[level] > 0 && offsets[level] == next
                && (level > 0 || counts[level] <= SegmentTimeTiles.MaximumTilesPerSpan + 1)
                && (level == 0 || counts[level] <= counts[level - 1])
                && (level == levels - 1 ? counts[level] <= TopLevelTiles : counts[level] > TopLevelTiles);
            next += LevelBytes(counts[level], tileBytes);
        }

        // Each timed row is one record of at most MaximumRecordBytes, and each finest block's records end in a checksum.
        long mostRecords = ((long)(rows - untimed) * MaximumRecordBytes)
            + (levels == 0 ? 0 : 4L * ((counts[0] + TilesPerBlock - 1) / TilesPerBlock));
        if (!laidOut || recordsOffset != next || recordsLength < 0 || recordsLength > mostRecords
            || recordsOffset + recordsLength != entry.Length)
        {
            throw Invalid($"the header of the section of '{entry.Name}' does not lay out its section.");
        }

        return new TileSection(entry, rows, untimed, ordered, timed ? first : null, timed ? last : null, width, kinds,
            counts, offsets, recordsOffset);
    }

    private static bool IsPowerOfTen(long value)
    {
        for (long power = 1; power > 0 && power <= value; power = power <= long.MaxValue / 10 ? power * 10 : -1)
        {
            if (power == value)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One level's tiles, as they are written: each one's readings, its link and children, and its counts.</summary>
    private sealed class Level(int count, int mechanisms)
    {
        public int Count { get; } = count;

        public long[] Lows { get; } = new long[count];

        public long[] Highs { get; } = new long[count];

        public long[] Indexes { get; } = new long[count];

        /// <summary>A finest tile's first record, in bytes into the records; a wider tile's first child.</summary>
        public int[] Links { get; } = new int[count];

        /// <summary>A wider tile's children; none for a finest tile.</summary>
        public int[] Children { get; } = new int[count];

        public int[] Counts { get; } = new int[count * mechanisms];

        /// <summary>For the finest level, where each block's records end, before their checksum.</summary>
        public int[] RecordsEnd { get; } = new int[(count + TilesPerBlock - 1) / TilesPerBlock];
    }

    /// <summary>Little-endian fields into a buffer.</summary>
    private ref struct SpanWriter(Span<byte> bytes)
    {
        private readonly Span<byte> bytes = bytes;
        private int position;

        public void U8(byte value) => bytes[position++] = value;

        public void U16(ushort value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[position..], value);
            position += 2;
        }

        public void I32(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes[position..], value);
            position += 4;
        }

        public void I64(long value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(bytes[position..], value);
            position += 8;
        }
    }

    /// <summary>Little-endian fields out of a buffer, refusing one that runs past it.</summary>
    internal ref struct SpanReader(ReadOnlySpan<byte> bytes, string section)
    {
        private readonly ReadOnlySpan<byte> bytes = bytes;

        public int Position { get; private set; }

        public byte U8() => Take(1)[0];

        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

        private ReadOnlySpan<byte> Take(int count)
        {
            if (Position + count > bytes.Length)
            {
                throw Invalid($"the section of '{section}' ends inside its header.");
            }

            ReadOnlySpan<byte> taken = bytes.Slice(Position, count);
            Position += count;
            return taken;
        }
    }
}

/// <summary>Where one segment's section lies, and the segment it describes, as the directory lists it.</summary>
internal sealed record TileSectionEntry(string Name, long SegmentLength, string SegmentDigest, long Offset, long Length)
{
    /// <summary>Whether the section describes exactly <paramref name="segment"/>: its name, length and digest.</summary>
    public bool Describes(StoreDependency segment) =>
        string.Equals(segment.Name, Name, StringComparison.Ordinal)
        && segment.LengthBytes == SegmentLength
        && string.Equals(segment.Digest, SegmentDigest, StringComparison.Ordinal);
}

/// <summary>
/// One segment's tiles as its section's header states them. Its levels are read a block at a time, each block checked,
/// and its finest tiles' records only where a zoom's column boundary falls among them.
/// </summary>
internal sealed class TileSection(
    TileSectionEntry entry,
    int rows,
    int untimed,
    bool ordered,
    long? first,
    long? last,
    long width,
    Mechanism[] mechanisms,
    int[] counts,
    long[] offsets,
    long recordsOffset)
{
    public TileSectionEntry Entry { get; } = entry;

    public int Rows { get; } = rows;

    public int Untimed { get; } = untimed;

    /// <summary>Whether its timed readings never go backwards: otherwise it keeps no tiles, and a zoom reads its rows.</summary>
    public bool Ordered { get; } = ordered;

    /// <summary>The earliest timed reading, in presentation ticks; null when no row is timed.</summary>
    public long? First { get; } = first;

    /// <summary>The latest timed reading.</summary>
    public long? Last { get; } = last;

    /// <summary>A finest tile's width in presentation ticks.</summary>
    public long Width { get; } = width;

    public int Levels => counts.Length;

    public int TileCount(int level) => counts[level];

    internal Mechanism[] Mechanisms { get; } = mechanisms;

    internal int TileBytes => 24 + (4 * Mechanisms.Length);

    /// <summary>Where block <paramref name="block"/> of a level lies in the file, and how long it is, checksum included.</summary>
    internal (long Offset, int Length) BlockAt(int level, int block)
    {
        int tiles = Math.Min(SessionTileIndex.TilesPerBlock, counts[level] - (block * SessionTileIndex.TilesPerBlock));
        long within = offsets[level] + ((long)block * ((SessionTileIndex.TilesPerBlock * TileBytes) + 8));
        return (Entry.Offset + within, (tiles * TileBytes) + 8);
    }

    /// <summary>Where the finest tiles' records begin in the file.</summary>
    internal long RecordsAt => Entry.Offset + recordsOffset;

    /// <summary>The bytes of records the section holds, their checksums among them.</summary>
    internal long RecordsLength => Entry.Length - recordsOffset;
}

/// <summary>One checked block of a level: its tiles' readings, links, children and counts.</summary>
internal sealed class TileBlock
{
    public required long[] Lows { get; init; }

    public required long[] Highs { get; init; }

    public required int[] Links { get; init; }

    public required int[] Children { get; init; }

    public required int[] Counts { get; init; }

    /// <summary>For a finest block, where its records end in the section's records, before their checksum.</summary>
    public required int RecordsEnd { get; init; }
}

/// <summary>
/// A generation's tile index, opened and checked once: its directory, and each section's header and blocks as zooms
/// first read them, kept within a bound (S2).
/// </summary>
internal sealed class TileIndexFile
{
    /// <summary>The most blocks kept decoded for later zooms: a few megabytes, whatever the session's size (S2).</summary>
    private const int MaximumCachedBlocks = 4_096;

    private readonly Dictionary<string, TileSectionEntry> entries;
    private readonly Dictionary<string, TileSection> sections = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Section, int Level, int Block), TileBlock> blocks = [];
    private readonly Lock gate = new();
    private int blocksDecoded;

    internal TileIndexFile(StoreDependency dependency, long derivedGeneration, Dictionary<string, TileSectionEntry> entries)
    {
        Dependency = dependency;
        DerivedGeneration = derivedGeneration;
        this.entries = entries;
    }

    public StoreDependency Dependency { get; }

    /// <summary>The generation whose segments it describes.</summary>
    public long DerivedGeneration { get; }

    /// <summary>The segments it holds a section of, by name.</summary>
    public IReadOnlyCollection<string> Segments => entries.Keys;

    /// <summary>How many blocks were read from the file and checked, rather than found already read: what a zoom costs.</summary>
    internal int BlocksDecoded => Volatile.Read(ref blocksDecoded);

    /// <summary>Whether it holds a section describing exactly <paramref name="segment"/>.</summary>
    public bool Describes(StoreDependency segment) =>
        entries.TryGetValue(segment.Name, out TileSectionEntry? entry) && entry.Describes(segment);

    /// <summary>Opens the file for one query's reads; every read it makes is checked.</summary>
    public TileReading Read(IOwnedDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        return new(this, directory.OpenOwnedFile(
            Dependency.Name, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess));
    }

    internal TileSection Section(FileStream stream, string name)
    {
        lock (gate)
        {
            if (sections.TryGetValue(name, out TileSection? known))
            {
                return known;
            }
        }

        TileSection read = SessionTileIndex.ReadSection(stream, entries[name]);
        lock (gate)
        {
            return sections.TryAdd(name, read) ? read : sections[name];
        }
    }

    internal TileBlock Block(FileStream stream, TileSection section, int level, int block)
    {
        (string, int, int) key = (section.Entry.Name, level, block);
        lock (gate)
        {
            if (blocks.TryGetValue(key, out TileBlock? known))
            {
                return known;
            }
        }

        (long offset, int length) = section.BlockAt(level, block);
        byte[] bytes = SessionTileIndex.Read(stream, offset, length);
        Interlocked.Increment(ref blocksDecoded);
        SessionTileIndex.Check(bytes, string.Create(CultureInfo.InvariantCulture,
            $"block {block} of level {level} of the section of '{section.Entry.Name}'"));
        int tiles = (length - 8) / section.TileBytes;
        int mechanisms = section.Mechanisms.Length;
        var decoded = new TileBlock
        {
            Lows = new long[tiles],
            Highs = new long[tiles],
            Links = new int[tiles],
            Children = new int[tiles],
            Counts = new int[tiles * mechanisms],
            RecordsEnd = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(length - 8)),
        };
        int lower = level == 0 ? 0 : section.TileCount(level - 1);
        for (int tile = 0; tile < tiles; tile++)
        {
            ReadOnlySpan<byte> at = bytes.AsSpan(tile * section.TileBytes);
            decoded.Lows[tile] = BinaryPrimitives.ReadInt64LittleEndian(at);
            decoded.Highs[tile] = BinaryPrimitives.ReadInt64LittleEndian(at[8..]);
            decoded.Links[tile] = BinaryPrimitives.ReadInt32LittleEndian(at[16..]);
            decoded.Children[tile] = BinaryPrimitives.ReadInt32LittleEndian(at[20..]);
            long sum = 0;
            for (int slot = 0; slot < mechanisms; slot++)
            {
                int count = BinaryPrimitives.ReadInt32LittleEndian(at[(24 + (4 * slot))..]);
                decoded.Counts[(tile * mechanisms) + slot] = count;
                sum += count >= 0 ? count : throw Bad("a count below zero");
            }

            // A tile holds a record, follows the one before it in time and, wider than the finest, the children after the
            // ones before it; a finest tile's records follow the ones before it.
            bool valid = decoded.Lows[tile] <= decoded.Highs[tile] && sum > 0
                && (tile == 0 || decoded.Lows[tile] > decoded.Highs[tile - 1])
                && (level == 0
                    ? decoded.Children[tile] == 0 && decoded.Links[tile] >= 0
                        && (tile == 0 || decoded.Links[tile] > decoded.Links[tile - 1])
                    : decoded.Children[tile] is >= 1 and <= 10 && decoded.Links[tile] >= 0
                        && (long)decoded.Links[tile] + decoded.Children[tile] <= lower
                        && (tile == 0 || decoded.Links[tile] == decoded.Links[tile - 1] + decoded.Children[tile - 1]));
            if (!valid)
            {
                throw Bad("a tile it does not lay out");
            }
        }

        if (level == 0 && (decoded.RecordsEnd <= decoded.Links[^1] || decoded.RecordsEnd + 4L > section.RecordsLength))
        {
            throw Bad("records it places outside the section");
        }

        lock (gate)
        {
            if (blocks.Count >= MaximumCachedBlocks)
            {
                blocks.Clear();
            }

            return blocks.TryAdd(key, decoded) ? decoded : blocks[key];
        }

        InvalidDataException Bad(string what) => SessionTileIndex.Invalid(string.Create(CultureInfo.InvariantCulture,
            $"block {block} of level {level} of the section of '{section.Entry.Name}' holds {what}."));
    }
}

/// <summary>
/// One query's reads of a tile index: the file held open while it counts, each read checked. It counts a segment's timed
/// records into columns exactly as reading its rows would.
/// </summary>
internal sealed class TileReading(TileIndexFile file, FileStream stream) : IDisposable
{
    public void Dispose() => stream.Dispose();

    /// <summary>How many finest tiles' records this reading decoded: one tile a column boundary falls among apiece.</summary>
    internal int TilesOfRecordsRead { get; private set; }

    // The column the last tile counted whole fell in, kept while tiles go on falling in it: tiles come in time order.
    private int lastColumn = -1;
    private long lastStart;
    private long lastEnd;

    // The last block of finest records read and checked, kept for the next boundary that falls among its tiles.
    private TileSection? recordsSection;
    private int recordsBlock = -1;
    private byte[] recordsBytes = [];

    /// <summary>The section of the segment <paramref name="name"/>, which the file must hold.</summary>
    public TileSection Section(string name) => file.Section(stream, name);

    /// <summary>
    /// Counts the timed records of <paramref name="section"/>'s segment inside <paramref name="columns"/>' interval into
    /// them: a tile whole wherever its records fall in one column, its children where a boundary falls among them, and a
    /// finest tile's records where one does. False, counting nothing, for a segment whose readings go backwards and meet
    /// the interval, which keeps no tiles: its rows are read instead.
    /// </summary>
    public bool CountInto(TileSection section, TimelineColumns columns, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(columns);
        TimeRange interval = columns.Interval;
        if (section.First is not { } first || section.Last is not { } last
            || last < interval.StartTicks || first >= interval.EndTicks)
        {
            return true;
        }

        if (!section.Ordered)
        {
            return false;
        }

        lastColumn = -1;
        int top = section.Levels - 1;
        Range(section, top, 0, section.TileCount(top), columns, cancellationToken);
        return true;
    }

    /// <summary>
    /// Counts the tiles of one level from <paramref name="from"/> to before <paramref name="end"/>, in time order: each
    /// whole that falls in one column, the children of each that does not, and the records of a finest one that does not.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Range(
        TileSection section, int level, int from, int end, TimelineColumns columns, CancellationToken cancellationToken)
    {
        TimeRange interval = columns.Interval;
        TileBlock? block = null;
        int blockIndex = -1;
        for (int position = from; position < end; position++)
        {
            if (position / SessionTileIndex.TilesPerBlock != blockIndex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                blockIndex = position / SessionTileIndex.TilesPerBlock;
                block = file.Block(stream, section, level, blockIndex);
            }

            int tile = position % SessionTileIndex.TilesPerBlock;
            long low = block!.Lows[tile];
            long high = block.Highs[tile];
            if (low >= interval.EndTicks)
            {
                return;
            }

            if (high < interval.StartTicks)
            {
                continue;
            }

            if (low >= interval.StartTicks && high < interval.EndTicks)
            {
                if (lastColumn < 0 || low < lastStart || low >= lastEnd)
                {
                    lastColumn = columns.ColumnOf(low)!.Value;
                    TimeRange bounds = TimelineColumns.IntervalOf(interval, columns.Counts.Count, lastColumn);
                    (lastStart, lastEnd) = (bounds.StartTicks, bounds.EndTicks);
                }

                if (high < lastEnd)
                {
                    int mechanisms = section.Mechanisms.Length;
                    for (int slot = 0; slot < mechanisms; slot++)
                    {
                        if (block.Counts[(tile * mechanisms) + slot] is > 0 and int count)
                        {
                            columns.Add(lastColumn, section.Mechanisms[slot], count);
                        }
                    }

                    continue;
                }
            }

            if (level > 0)
            {
                Range(section, level - 1, block.Links[tile], block.Links[tile] + block.Children[tile], columns, cancellationToken);
            }
            else
            {
                CountRecords(section, block, position, columns);
            }
        }
    }

    /// <summary>
    /// Counts one finest tile's records, each into the column holding its reading, after checking its block's records
    /// against their checksum and each record against the tile: its readings within the tile's, its count the tile's.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void CountRecords(TileSection section, TileBlock block, int position, TimelineColumns columns)
    {
        TilesOfRecordsRead++;
        int first = position - (position % SessionTileIndex.TilesPerBlock);
        int tile = position - first;
        int start = block.Links[0];
        int end = block.RecordsEnd;
        if (!ReferenceEquals(recordsSection, section) || recordsBlock != first / SessionTileIndex.TilesPerBlock)
        {
            byte[] read = SessionTileIndex.Read(stream, section.RecordsAt + start, end - start + 4);
            SessionTileIndex.Check(read, string.Create(CultureInfo.InvariantCulture,
                $"the records of block {first / SessionTileIndex.TilesPerBlock} of the section of '{section.Entry.Name}'"));
            (recordsSection, recordsBlock, recordsBytes) = (section, first / SessionTileIndex.TilesPerBlock, read);
        }

        byte[] bytes = recordsBytes;
        int from = block.Links[tile] - start;
        int to = (tile + 1 < block.Links.Length ? block.Links[tile + 1] : end) - start;
        int mechanisms = section.Mechanisms.Length;
        long expected = 0;
        for (int slot = 0; slot < mechanisms; slot++)
        {
            expected += block.Counts[(tile * mechanisms) + slot];
        }

        long reading = block.Lows[tile];
        long counted = 0;
        int at = from;
        while (at < to)
        {
            ulong increase = 0;
            int shift = 0;
            byte next;
            do
            {
                if (at >= to || shift > 63)
                {
                    throw Bad();
                }

                next = bytes[at++];
                increase |= (ulong)(next & 0x7F) << shift;
                shift += 7;
            }
            while ((next & 0x80) != 0);

            // The first record is the tile's earliest, and none passes its latest.
            if (at >= to || (counted == 0 && increase != 0) || increase > (ulong)(block.Highs[tile] - reading))
            {
                throw Bad();
            }

            reading += (long)increase;
            int slot = bytes[at++];
            if (slot >= mechanisms)
            {
                throw Bad();
            }

            counted++;
            if (columns.ColumnOf(reading) is { } column)
            {
                columns.Add(column, section.Mechanisms[slot]);
            }
        }

        if (counted != expected || reading != block.Highs[tile])
        {
            throw Bad();
        }

        InvalidDataException Bad() => SessionTileIndex.Invalid(string.Create(CultureInfo.InvariantCulture,
            $"the records of tile {position} of the section of '{section.Entry.Name}' are not the tile's."));
    }
}
