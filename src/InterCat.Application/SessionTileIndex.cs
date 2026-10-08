using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// The persisted timeline tiles of a generation's observation segments (`contracts/tile-index-v1.md`): §12.1 S4's deeper
/// levels, beneath the persisted overview. Each segment's tiles are kept at decimal levels - its finest, as
/// <see cref="SegmentTimeTiles"/> builds them, and each ten times wider above it - each with what its records' owners and
/// channels bind to, and the records of every finest tile beside them. A zoom counts a segment from what it draws: a tile
/// whole wherever its records fall in one column, a tile's children where a column boundary falls among its records, and
/// a finest tile's records only where one falls among them; a brush counts the records of its processes and channels the
/// same way, at its two ends. Neither opens a segment, and each reads in proportion to its view rather than to the
/// session (S3). Every byte it interprets carries a checksum of its own, as a segment's do, so it is read by the block,
/// never hashed whole first.
/// </summary>
internal static class SessionTileIndex
{
    /// <summary>How many tiles one checked block of a level holds.</summary>
    public const int TilesPerBlock = 32;

    /// <summary>The bytes before the first section: magic, version, session and the generation the tiles describe.</summary>
    public const int HeaderLength = 48;

    /// <summary>The bytes after the directory, which say where it is.</summary>
    public const int FooterLength = 32;

    /// <summary>The fixed bytes of a block after its tiles: where its records end, where its tallies end, its checksum.</summary>
    internal const int BlockTrailerBytes = 12;

    /// <summary>The most levels a section has: a finest tile one tick wide, and eighteen decades above it.</summary>
    internal const int MaximumLevels = 20;

    /// <summary>A packed owner's instance plus one, 0 for none (<see cref="SegmentBindings"/>).</summary>
    private const uint InstanceMask = (1u << 24) - 1;

    /// <summary>A packed channel's number plus one, 0 for none.</summary>
    private const uint ChannelMask = (1u << 27) - 1;

    /// <summary>A level of at most this many tiles is the top one: one block holds it.</summary>
    private const int TopLevelTiles = TilesPerBlock;

    /// <summary>The longest directory a reader reads: far beyond what any generation's segments need.</summary>
    private const int MaximumDirectoryBytes = 64 * 1024 * 1024;

    /// <summary>The longest a section's header can be, with every mechanism and level a section may name.</summary>
    private const int MaximumSectionHeaderBytes = 71 + (2 * 256) + (12 * MaximumLevels);

    /// <summary>The most a finest tile's records take apiece: the increase, the mechanism's slot, the owner and the channel.</summary>
    private const int MaximumRecordBytes = 10 + 1 + 4 + 4;

    private const string FilePrefix = "tiles-";
    private const string FileSuffix = ".bin";
    private const string What = "persisted tile index";
    private const ushort Major = 1;

    /// <summary>Minor 1 keeps each tile's tallies and each record's owner and channel; minor 0, revision 456's, kept neither.</summary>
    private const ushort Minor = 1;

    private const byte Ordered = 1;
    private const byte Timed = 2;

    private static readonly ConditionalWeakTable<TransportRelationIndex, Fingerprinted> Fingerprints = new();

    private static ReadOnlySpan<byte> Magic => "ICATTILE"u8;

    /// <summary>The name a tile index published by <paramref name="generation"/> takes.</summary>
    public static string FileNameFor(long generation)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);
        return string.Create(CultureInfo.InvariantCulture, $"{FilePrefix}{generation:D10}{FileSuffix}");
    }

    /// <summary>
    /// What a tile index keeps of a row's packed owner: the instance plus one, past three bits holding the strength - all an
    /// evidence policy's admission reads (<see cref="ProcessBinding.IsAdmittedUnder"/>) - or 0 for an owner bound to none.
    /// </summary>
    internal static uint OwnerKey(uint packed) =>
        (packed & InstanceMask) == 0 ? 0 : ((packed & InstanceMask) << 3) | ((packed >> 24) & 7);

    /// <summary>A row's packed channel as a tile index keeps it: the channel's number plus one, or 0 for none.</summary>
    internal static uint ChannelKey(uint packed) => packed & ChannelMask;

    /// <summary>Whether <paramref name="policy"/> admits an owner bound with each strength an owner key can hold.</summary>
    internal static bool[] AdmittedStrengths(EvidencePolicy policy)
    {
        bool[] admitted = new bool[8];
        for (int strength = 0; strength < admitted.Length; strength++)
        {
            admitted[strength] = new ProcessBinding(0, (RelationStrength)strength, ProcessBindingReason.Bound).IsAdmittedUnder(policy);
        }

        return admitted;
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
    /// What a derivation's bindings are as a tile index keeps them: its binding and relation rules, then its instances in
    /// the order a packed owner counts them, and its channels as the order a packed channel counts them names them. Two
    /// derivations with the same fingerprint bind every record alike, so tallies kept under one count for the other.
    /// </summary>
    internal static TileDerivation Derivation(ProcessInstanceIndex processes, TransportRelationIndex relations)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(relations);
        if (Fingerprints.TryGetValue(relations, out Fingerprinted? known) && ReferenceEquals(known.Processes, processes))
        {
            return known.Derivation;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Text(hash, ProcessInstanceIndex.BindingRule);
        Text(hash, TransportRelationIndex.RelationRule);
        Number(hash, processes.Instances.Count);
        Span<byte> identity = stackalloc byte[16];
        foreach (ProcessInstance instance in processes.Instances)
        {
            _ = instance.Id.Value.TryWriteBytes(identity);
            hash.AppendData(identity);
        }

        Number(hash, relations.Channels);
        Number(hash, relations.Relations.Count);
        foreach (TransportRelation relation in relations.Relations)
        {
            Number(hash, relation.Channel);
            Text(hash, relation.StableKey);
        }

        Number(hash, relations.OneSided.Count);
        foreach (TransportConnection connection in relations.OneSided)
        {
            Number(hash, connection.Channel);
            Text(hash, connection.StableKey);
        }

        var derivation = new TileDerivation(ProcessInstanceIndex.BindingRule, TransportRelationIndex.RelationRule,
            processes.Instances.Count, relations.Channels, hash.GetHashAndReset());
        Fingerprints.AddOrUpdate(relations, new(processes, derivation));
        return derivation;

        static void Text(IncrementalHash into, string value)
        {
            into.AppendData(Encoding.UTF8.GetBytes(value));
            into.AppendData([0]);
        }

        static void Number(IncrementalHash into, int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            into.AppendData(bytes);
        }
    }

    /// <summary>
    /// Writes the tiles of <paramref name="segments"/>, generation <paramref name="derivedGeneration"/>'s observation
    /// segments, with their records' bindings under <paramref name="processes"/> and <paramref name="relations"/>, in the
    /// canonical order of tile-index-v1: its header, a section per segment by name, the directory and the footer.
    /// <paramref name="stated"/> is what the directory says the bindings are, which only a test of a stale derivation
    /// states otherwise.
    /// </summary>
    /// <returns>How many bytes were written.</returns>
    public static long Write(
        Stream destination,
        Guid sessionId,
        long derivedGeneration,
        IReadOnlyList<(StoreDependency Dependency, SegmentReaderV1 Reader)> segments,
        ProcessInstanceIndex processes,
        TransportRelationIndex relations,
        CancellationToken cancellationToken,
        TileDerivation? stated = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(relations);
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

        TileDerivation derivation = stated ?? Derivation(processes, relations);
        var directory = new MemoryStream();
        var entries = new IndexFileWriter(directory, MaximumDirectoryBytes, What);
        entries.Str8(derivation.BindingRule);
        entries.Str8(derivation.RelationRule);
        entries.I32(derivation.Instances);
        entries.I32(derivation.Channels);
        entries.Raw(derivation.Fingerprint);
        foreach ((StoreDependency dependency, SegmentReaderV1 reader) in segments
            .OrderBy(segment => segment.Dependency.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            long offset = writer.Written;
            WriteSection(writer, reader, processes, relations, cancellationToken);
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
    /// One segment's section: its header; its levels from the finest up, block by block; the records of its finest tiles,
    /// block by block; and every tile's tallies, level by level and block by block. A segment whose timed readings go
    /// backwards, or that has none, keeps no tiles: a zoom or a brush reads the first's rows, and the second has none.
    /// </summary>
    private static void WriteSection(
        IndexFileWriter writer,
        SegmentReaderV1 segment,
        ProcessInstanceIndex processes,
        TransportRelationIndex relations,
        CancellationToken cancellationToken)
    {
        SegmentTimeTiles tiles = SegmentTimeTiles.Of(segment, cancellationToken);
        bool timed = tiles.First is not null;
        bool usable = timed && tiles.Ordered && tiles.Count > 0;
        Mechanism[] mechanisms = usable ? tiles.Mechanisms.ToArray() : [];
        List<Level> levels = [];
        byte[] records = [];
        byte[] tallies = [];
        if (usable)
        {
            PackedOwners owners = SegmentBindings.OwnersOf(segment, processes);
            PackedChannels channels = SegmentBindings.ChannelsOf(segment, relations);
            levels = Levels(segment, tiles, mechanisms, owners, channels, cancellationToken);
            records = Records(segment, tiles, mechanisms, levels[0], owners, channels, cancellationToken);
            tallies = Tallies(levels, cancellationToken);
        }

        int tileBytes = TileBytes(mechanisms.Length);
        int headerBytes = 71 + (2 * mechanisms.Length) + (12 * levels.Count);
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
        at.I64(next + records.Length);
        at.I64(tallies.Length);
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
                    into.I32(tilesOf.TallyLinks[tile]);
                    for (int slot = 0; slot < mechanisms.Length; slot++)
                    {
                        into.I32(tilesOf.Counts[(tile * mechanisms.Length) + slot]);
                    }
                }

                // A block says where its records and its tallies end, so each is read and checked without the next block.
                into.I32(level == 0 ? tilesOf.RecordsEnd[first / TilesPerBlock] : 0);
                into.I32(tilesOf.TalliesEnd[first / TilesPerBlock]);
                int length = (count * tileBytes) + 8;
                Crc32C.Write(block.AsSpan(length), Crc32C.Compute(block.AsSpan(0, length)));
                writer.Raw(block.AsSpan(0, length + 4));
            }
        }

        writer.Raw(records);
        writer.Raw(tallies);
    }

    /// <summary>
    /// A section's levels, from the finest up to the first that one block holds: each tile's readings and counts, and
    /// what its records' owners and channels bind to, tallied.
    /// </summary>
    private static List<Level> Levels(
        SegmentReaderV1 segment,
        SegmentTimeTiles tiles,
        Mechanism[] mechanisms,
        PackedOwners owners,
        PackedChannels channels,
        CancellationToken cancellationToken)
    {
        SegmentColumnSlice times = segment.Slice(SegmentColumnId.SessionRelativeTicks);
        SegmentColumnSlice kinds = segment.Slice(SegmentColumnId.Mechanism);
        int[] slotOf = SlotsOf(mechanisms);
        var finest = new Level(tiles.Count, mechanisms.Length);
        var ownerCounts = new Dictionary<(uint Owner, byte Slot), int>();
        var channelCounts = new Dictionary<int, int>();
        for (int tile = 0; tile < tiles.Count; tile++)
        {
            if ((tile & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            (long low, long high) = tiles.Readings(tile);
            finest.Lows[tile] = low;
            finest.Highs[tile] = high;
            finest.Indexes[tile] = tiles.IndexOf(tile);
            for (int slot = 0; slot < mechanisms.Length; slot++)
            {
                finest.Counts[(tile * mechanisms.Length) + slot] = tiles.CountAt(tile, slot);
            }

            // A tile's tallies count its timed records alone, as its counts do: a bound owner by its key and the record's
            // mechanism, a known channel by its number.
            ownerCounts.Clear();
            channelCounts.Clear();
            (int firstRow, int endRow) = tiles.RowsOf(tile);
            for (int row = firstRow; row < endRow; row++)
            {
                if (times.SignedAt(row) is null)
                {
                    continue;
                }

                if (OwnerKey(owners.Raw(row)) is not 0 and uint owner)
                {
                    var key = (owner, (byte)slotOf[(int)(Mechanism)kinds.UnsignedAt(row)!.Value]);
                    ownerCounts[key] = ownerCounts.GetValueOrDefault(key) + 1;
                }

                if (ChannelKey(channels.Raw(row)) is not 0 and uint channel)
                {
                    int number = (int)channel - 1;
                    channelCounts[number] = channelCounts.GetValueOrDefault(number) + 1;
                }
            }

            finest.Tallies[tile] = TileTallies.Of(ownerCounts, channelCounts);
        }

        var levels = new List<Level> { finest };
        while (levels[^1].Count > TopLevelTiles && levels.Count < MaximumLevels)
        {
            levels.Add(Above(levels[^1], mechanisms.Length));
        }

        return levels;
    }

    /// <summary>
    /// The level ten times wider than <paramref name="below"/>: each of its tiles the run of tiles it holds, its counts
    /// and tallies their sums.
    /// </summary>
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
        var ownerCounts = new Dictionary<(uint Owner, byte Slot), int>();
        var channelCounts = new Dictionary<int, int>();
        for (int parent = 0; parent < parents.Count; parent++)
        {
            int first = parents[parent];
            int end = parent + 1 < parents.Count ? parents[parent + 1] : below.Count;
            level.Lows[parent] = below.Lows[first];
            level.Highs[parent] = below.Highs[end - 1];
            level.Indexes[parent] = SegmentTimeTiles.FloorDivide(below.Indexes[first], 10);
            level.Links[parent] = first;
            level.Children[parent] = end - first;
            ownerCounts.Clear();
            channelCounts.Clear();
            for (int child = first; child < end; child++)
            {
                for (int slot = 0; slot < mechanisms; slot++)
                {
                    level.Counts[(parent * mechanisms) + slot] = checked(
                        level.Counts[(parent * mechanisms) + slot] + below.Counts[(child * mechanisms) + slot]);
                }

                TileTallies tallies = below.Tallies[child];
                for (int entry = 0; entry < tallies.Owners.Length; entry++)
                {
                    var key = (tallies.Owners[entry], tallies.Slots[entry]);
                    ownerCounts[key] = checked(ownerCounts.GetValueOrDefault(key) + tallies.OwnerCounts[entry]);
                }

                for (int entry = 0; entry < tallies.Channels.Length; entry++)
                {
                    int number = tallies.Channels[entry];
                    channelCounts[number] = checked(channelCounts.GetValueOrDefault(number) + tallies.ChannelCounts[entry]);
                }
            }

            level.Tallies[parent] = TileTallies.Of(ownerCounts, channelCounts);
        }

        return level;
    }

    /// <summary>
    /// The records of every finest tile, each tile's after the one before and followed by its checksum, so a column
    /// boundary or a brush's end reads only the tile it falls among: each record its reading's increase over the record
    /// before it in its tile - the first's over the tile's earliest, so none - then its mechanism's slot, its owner's key
    /// and its channel's. The tiles' links, and where each block's last tile's records end, are set as they are written.
    /// </summary>
    private static byte[] Records(
        SegmentReaderV1 segment,
        SegmentTimeTiles tiles,
        Mechanism[] mechanisms,
        Level finest,
        PackedOwners owners,
        PackedChannels channels,
        CancellationToken cancellationToken)
    {
        SegmentColumnSlice times = segment.Slice(SegmentColumnId.SessionRelativeTicks);
        SegmentColumnSlice kinds = segment.Slice(SegmentColumnId.Mechanism);
        int[] slotOf = SlotsOf(mechanisms);
        var bytes = new MemoryStream();
        Span<byte> encoded = stackalloc byte[MaximumRecordBytes];
        byte[] checksum = new byte[4];
        for (int tile = 0; tile < finest.Count; tile++)
        {
            if ((tile & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            finest.Links[tile] = checked((int)bytes.Length);
            int tileStart = checked((int)bytes.Length);
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
                length += Unsigned(encoded[length..], OwnerKey(owners.Raw(row)));
                length += Unsigned(encoded[length..], ChannelKey(channels.Raw(row)));
                bytes.Write(encoded[..length]);
                previous = tick;
            }

            int end = checked((int)bytes.Length);
            finest.RecordBytes[tile] = end - tileStart;
            finest.RecordsEnd[tile / TilesPerBlock] = end;
            Crc32C.Write(checksum, Crc32C.Compute(bytes.GetBuffer().AsSpan(tileStart, end - tileStart)));
            bytes.Write(checksum);
        }

        return bytes.ToArray();
    }

    /// <summary>
    /// Every tile's tallies, level by level from the finest and block by block, each block's followed by its checksum. A
    /// tile keeps its tallies only where they take at most half the bytes of the records beneath it, so a brush that
    /// counts a tile from its children or records instead reads at most twice what its tallies would have taken; a tile
    /// whose tallies are not kept holds a single zero. Kept tallies are its owners - how many plus one, then for each,
    /// in order of key and mechanism, its key's increase over the one before (the first's over none), its mechanism's
    /// slot and its count - and its channels - how many, then for each, in order, its number's increase over the one
    /// before (the first's over none) and its count. The tiles' tally links and each block's end are set as they are
    /// written.
    /// </summary>
    private static byte[] Tallies(List<Level> levels, CancellationToken cancellationToken)
    {
        // What lies beneath each wider tile: its children's records.
        for (int level = 1; level < levels.Count; level++)
        {
            Level below = levels[level - 1];
            Level tiles = levels[level];
            for (int tile = 0; tile < tiles.Count; tile++)
            {
                for (int child = tiles.Links[tile]; child < tiles.Links[tile] + tiles.Children[tile]; child++)
                {
                    tiles.RecordBytes[tile] += below.RecordBytes[child];
                }
            }
        }

        var bytes = new MemoryStream();
        var kept = new MemoryStream();
        Span<byte> encoded = stackalloc byte[16];
        foreach (Level level in levels)
        {
            int blockStart = checked((int)bytes.Length);
            for (int tile = 0; tile < level.Count; tile++)
            {
                if ((tile & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                level.TallyLinks[tile] = checked((int)bytes.Length);
                kept.SetLength(0);
                TileTallies tallies = level.Tallies[tile];
                kept.Write(encoded[..Unsigned(encoded, (ulong)tallies.Owners.Length + 1)]);
                uint previousOwner = 0;
                for (int entry = 0; entry < tallies.Owners.Length; entry++)
                {
                    int length = Unsigned(encoded, tallies.Owners[entry] - previousOwner);
                    encoded[length++] = tallies.Slots[entry];
                    length += Unsigned(encoded[length..], (ulong)tallies.OwnerCounts[entry]);
                    kept.Write(encoded[..length]);
                    previousOwner = tallies.Owners[entry];
                }

                kept.Write(encoded[..Unsigned(encoded, (ulong)tallies.Channels.Length)]);
                int previousChannel = 0;
                for (int entry = 0; entry < tallies.Channels.Length; entry++)
                {
                    int length = Unsigned(encoded, (ulong)(tallies.Channels[entry] - previousChannel));
                    length += Unsigned(encoded[length..], (ulong)tallies.ChannelCounts[entry]);
                    kept.Write(encoded[..length]);
                    previousChannel = tallies.Channels[entry];
                }

                if (kept.Length * 2 <= level.RecordBytes[tile])
                {
                    bytes.Write(kept.GetBuffer().AsSpan(0, (int)kept.Length));
                }
                else
                {
                    bytes.WriteByte(0);
                }

                if (tile % TilesPerBlock == TilesPerBlock - 1 || tile == level.Count - 1)
                {
                    int end = checked((int)bytes.Length);
                    level.TalliesEnd[tile / TilesPerBlock] = end;
                    byte[] checksum = new byte[4];
                    Crc32C.Write(checksum, Crc32C.Compute(bytes.GetBuffer().AsSpan(blockStart, end - blockStart)));
                    bytes.Write(checksum);
                    blockStart = checked((int)bytes.Length);
                }
            }
        }

        return bytes.ToArray();
    }

    /// <summary>Each mechanism code's place among <paramref name="mechanisms"/>, -1 for one not among them.</summary>
    private static int[] SlotsOf(Mechanism[] mechanisms)
    {
        int[] slotOf = new int[TimelineColumns.MechanismCodes];
        Array.Fill(slotOf, -1);
        for (int slot = 0; slot < mechanisms.Length; slot++)
        {
            slotOf[(int)mechanisms[slot]] = slot;
        }

        return slotOf;
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

    internal static int TileBytes(int mechanisms) => 28 + (4 * mechanisms);

    private static long LevelBytes(int tiles, int tileBytes) =>
        ((long)tiles * tileBytes) + ((long)((tiles + TilesPerBlock - 1) / TilesPerBlock) * BlockTrailerBytes);

    /// <summary>
    /// Opens the tile index <paramref name="dependency"/> names: its header, footer and directory, each checked. A section
    /// is read when a zoom or a brush first needs it, and a block when one reads it.
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
        ushort minor = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10));
        if (major != Major)
        {
            throw Invalid(string.Create(CultureInfo.InvariantCulture, $"it is version {major}, and this build reads version {Major}."));
        }

        if (minor < Minor)
        {
            throw Invalid(string.Create(CultureInfo.InvariantCulture,
                $"it is minor {minor}, written before a tile index kept its records' bindings; a checkpoint publishes it again."));
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
        string bindingRule = reader.Str8();
        string relationRule = reader.Str8();
        int instances = reader.I32();
        int channels = reader.I32();
        byte[] fingerprint = new byte[32];
        for (int index = 0; index < fingerprint.Length; index++)
        {
            fingerprint[index] = reader.U8();
        }

        if (instances < 0 || instances > (int)InstanceMask - 1 || channels < 0 || channels > (int)ChannelMask - 1)
        {
            throw Invalid("its directory names more instances or channels than a binding packs.");
        }

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

        return new TileIndexFile(dependency, derivedGeneration,
            new TileDerivation(bindingRule, relationRule, instances, channels, fingerprint), entries);
    }

    internal static byte[] Read(FileStream stream, long offset, int length)
    {
        byte[] bytes = new byte[length];
        ReadInto(stream, offset, bytes);
        return bytes;
    }

    /// <summary>Fills <paramref name="bytes"/> from <paramref name="offset"/>, refusing a file that ends first.</summary>
    internal static void ReadInto(FileStream stream, long offset, Span<byte> bytes)
    {
        int read = 0;
        while (read < bytes.Length)
        {
            int taken = RandomAccess.Read(stream.SafeFileHandle, bytes[read..], offset + read);
            if (taken == 0)
            {
                throw Invalid("it ends inside a part its directory names.");
            }

            read += taken;
        }
    }

    /// <summary>Refuses <paramref name="bytes"/> unless they end with the checksum of what is before it.</summary>
    internal static void Check(ReadOnlySpan<byte> bytes, string what)
    {
        if (!Matches(bytes))
        {
            throw Mismatch(what);
        }
    }

    /// <summary>Whether <paramref name="bytes"/> end with the checksum of what is before it.</summary>
    internal static bool Matches(ReadOnlySpan<byte> bytes) =>
        Crc32C.Compute(bytes[..^4]) == BinaryPrimitives.ReadUInt32LittleEndian(bytes[^4..]);

    /// <summary>The refusal of a part, named by <paramref name="what"/>, that does not match its checksum.</summary>
    internal static InvalidDataException Mismatch(string what) => Invalid($"{what} does not match its checksum.");

    internal static InvalidDataException Invalid(string reason) => new($"The {What} is not readable: {reason}");

    /// <summary>A section's header, read and checked once: what its segment holds and where each of its parts lies.</summary>
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
        long talliesOffset = at.I64();
        long talliesLength = at.I64();
        int headerLength = at.Position + 4;
        Check(bytes.AsSpan(0, headerLength), $"the header of the section of '{entry.Name}'");
        bool ordered = (flags & Ordered) != 0;
        bool timed = (flags & Timed) != 0;
        bool usable = ordered && timed;
        int tileBytes = TileBytes(mechanisms);
        long next = headerLength;
        bool laidOut = (flags & ~(Ordered | Timed)) == 0 && rows >= 0 && untimed >= 0 && untimed <= rows
            && (!timed || first <= last) && IsPowerOfTen(width)
            && (usable
                ? levels > 0 && mechanisms > 0
                : levels == 0 && mechanisms == 0 && recordsLength == 0 && talliesLength == 0);
        for (int level = 0; laidOut && level < levels; level++)
        {
            laidOut = counts[level] > 0 && offsets[level] == next
                && (level > 0 || counts[level] <= SegmentTimeTiles.MaximumTilesPerSpan + 1)
                && (level == 0 || counts[level] <= counts[level - 1])
                && (level == levels - 1 ? counts[level] <= TopLevelTiles : counts[level] > TopLevelTiles);
            next += LevelBytes(counts[level], tileBytes);
        }

        // Each timed row is one record of at most MaximumRecordBytes, and each finest tile's records end in a checksum.
        long mostRecords = ((long)(rows - untimed) * MaximumRecordBytes) + (levels == 0 ? 0 : 4L * counts[0]);
        if (!laidOut || recordsOffset != next || recordsLength < 0 || recordsLength > mostRecords
            || talliesOffset != recordsOffset + recordsLength || talliesLength < 0
            || talliesOffset + talliesLength != entry.Length)
        {
            throw Invalid($"the header of the section of '{entry.Name}' does not lay out its section.");
        }

        return new TileSection(entry, rows, untimed, ordered, timed ? first : null, timed ? last : null, width, kinds,
            counts, offsets, recordsOffset, talliesOffset);
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

    /// <summary>A derivation's fingerprint, kept with the relations it was taken of, for the processes it was taken with.</summary>
    private sealed record Fingerprinted(ProcessInstanceIndex Processes, TileDerivation Derivation);

    /// <summary>
    /// One tile's tallies, in key order: each bound owner's key (<see cref="OwnerKey"/>) with the mechanism of the records it
    /// owns and how many; and each known channel's number with how many.
    /// </summary>
    private sealed record TileTallies(uint[] Owners, byte[] Slots, int[] OwnerCounts, int[] Channels, int[] ChannelCounts)
    {
        public static TileTallies Of(Dictionary<(uint Owner, byte Slot), int> owners, Dictionary<int, int> channels)
        {
            (uint Owner, byte Slot)[] ownerKeys = [.. owners.Keys.Order()];
            int[] channelKeys = [.. channels.Keys.Order()];
            return new(
                [.. ownerKeys.Select(key => key.Owner)],
                [.. ownerKeys.Select(key => key.Slot)],
                [.. ownerKeys.Select(key => owners[key])],
                channelKeys,
                [.. channelKeys.Select(key => channels[key])]);
        }
    }

    /// <summary>One level's tiles, as they are written: each one's readings, links, children, counts and tallies.</summary>
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

        public TileTallies[] Tallies { get; } = new TileTallies[count];

        /// <summary>The bytes of the records beneath each tile: a finest tile's own, a wider tile's children's.</summary>
        public long[] RecordBytes { get; } = new long[count];

        /// <summary>Each tile's tallies, in bytes into the tallies.</summary>
        public int[] TallyLinks { get; } = new int[count];

        /// <summary>For the finest level, where each block's records end, before their checksum.</summary>
        public int[] RecordsEnd { get; } = new int[(count + TilesPerBlock - 1) / TilesPerBlock];

        /// <summary>Where each block's tallies end, before their checksum.</summary>
        public int[] TalliesEnd { get; } = new int[(count + TilesPerBlock - 1) / TilesPerBlock];
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

/// <summary>
/// What a tile index says its bindings are: the binding and relation rules they were made under, how many instances and
/// channels they number, and the fingerprint of those instances and channels in their order (<see cref="SessionTileIndex.Derivation"/>).
/// </summary>
internal sealed record TileDerivation(string BindingRule, string RelationRule, int Instances, int Channels, byte[] Fingerprint)
{
    /// <summary>
    /// Whether <paramref name="processes"/> and <paramref name="relations"/> are these: made under the same rules, of the
    /// same instances and channels in the same order, so they bind every record of the same segments as these did.
    /// </summary>
    public bool Matches(ProcessInstanceIndex processes, TransportRelationIndex relations)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(relations);
        return string.Equals(BindingRule, ProcessInstanceIndex.BindingRule, StringComparison.Ordinal)
            && string.Equals(RelationRule, TransportRelationIndex.RelationRule, StringComparison.Ordinal)
            && Instances == processes.Instances.Count && Channels == relations.Channels
            && Fingerprint.AsSpan().SequenceEqual(SessionTileIndex.Derivation(processes, relations).Fingerprint);
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
/// its finest tiles' records only where a column boundary or a brush's end falls among them, and a tile's tallies only
/// where a brush counts it whole.
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
    long recordsOffset,
    long talliesOffset)
{
    public TileSectionEntry Entry { get; } = entry;

    public int Rows { get; } = rows;

    public int Untimed { get; } = untimed;

    /// <summary>Whether its timed readings never go backwards: otherwise it keeps no tiles, and its rows are read.</summary>
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

    /// <summary>Each of <see cref="Mechanisms"/>' slot in a count kept per mechanism (<see cref="TimelineColumns"/>).</summary>
    internal int[] CountSlots { get; } = [.. mechanisms.Select(TimelineColumns.SlotOfMechanism)];

    internal int TileBytes => SessionTileIndex.TileBytes(Mechanisms.Length);

    /// <summary>Where block <paramref name="block"/> of a level lies in the file, and how long it is, its trailer included.</summary>
    internal (long Offset, int Length) BlockAt(int level, int block)
    {
        int tiles = Math.Min(SessionTileIndex.TilesPerBlock, counts[level] - (block * SessionTileIndex.TilesPerBlock));
        long within = offsets[level]
            + ((long)block * ((SessionTileIndex.TilesPerBlock * TileBytes) + SessionTileIndex.BlockTrailerBytes));
        return (Entry.Offset + within, (tiles * TileBytes) + SessionTileIndex.BlockTrailerBytes);
    }

    /// <summary>Where the finest tiles' records begin in the file.</summary>
    internal long RecordsAt => Entry.Offset + recordsOffset;

    /// <summary>The bytes of records the section holds, their checksums among them.</summary>
    internal long RecordsLength => talliesOffset - recordsOffset;

    /// <summary>Where the tiles' tallies begin in the file.</summary>
    internal long TalliesAt => Entry.Offset + talliesOffset;

    /// <summary>The bytes of tallies the section holds, their checksums among them.</summary>
    internal long TalliesLength => Entry.Length - talliesOffset;
}

/// <summary>One checked block of a level: its tiles' readings, links, children, tally links and counts.</summary>
internal sealed class TileBlock
{
    public required long[] Lows { get; init; }

    public required long[] Highs { get; init; }

    public required int[] Links { get; init; }

    public required int[] Children { get; init; }

    public required int[] TallyLinks { get; init; }

    public required int[] Counts { get; init; }

    /// <summary>For a finest block, where its last tile's records end within the section's records, before their checksum.</summary>
    public required int RecordsEnd { get; init; }

    /// <summary>Where its tiles' tallies end within the section's tallies, before their checksum.</summary>
    public required int TalliesEnd { get; init; }
}

/// <summary>
/// A generation's tile index, opened and checked once: its directory and the derivation it states, and each section's
/// header and blocks as zooms and brushes first read them, kept within a bound (S2).
/// </summary>
internal sealed class TileIndexFile
{
    /// <summary>
    /// The most blocks kept decoded for later zooms, brushes and groups: about 25 MB, whatever the session's size (S2), and
    /// every block of a 10M-record session's tiles, which a group's lanes at 2,000 columns read most of.
    /// </summary>
    private const int MaximumCachedBlocks = 16_384;

    private readonly Dictionary<string, TileSectionEntry> entries;
    private readonly Dictionary<string, TileSection> sections = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Section, int Level, int Block), TileBlock> blocks = [];
    private readonly Lock gate = new();
    private int blocksDecoded;

    internal TileIndexFile(
        StoreDependency dependency, long derivedGeneration, TileDerivation derivation, Dictionary<string, TileSectionEntry> entries)
    {
        Dependency = dependency;
        DerivedGeneration = derivedGeneration;
        Derivation = derivation;
        this.entries = entries;
    }

    public StoreDependency Dependency { get; }

    /// <summary>The generation whose segments it describes.</summary>
    public long DerivedGeneration { get; }

    /// <summary>What it says its records' bindings were made under: a brush counts its tallies only for that derivation.</summary>
    public TileDerivation Derivation { get; }

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
        if (!SessionTileIndex.Matches(bytes))
        {
            throw SessionTileIndex.Mismatch(string.Create(CultureInfo.InvariantCulture,
                $"block {block} of level {level} of the section of '{section.Entry.Name}'"));
        }

        int tiles = (length - SessionTileIndex.BlockTrailerBytes) / section.TileBytes;
        int mechanisms = section.Mechanisms.Length;
        var decoded = new TileBlock
        {
            Lows = new long[tiles],
            Highs = new long[tiles],
            Links = new int[tiles],
            Children = new int[tiles],
            TallyLinks = new int[tiles],
            Counts = new int[tiles * mechanisms],
            RecordsEnd = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(length - 12)),
            TalliesEnd = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(length - 8)),
        };
        int lower = level == 0 ? 0 : section.TileCount(level - 1);
        for (int tile = 0; tile < tiles; tile++)
        {
            ReadOnlySpan<byte> at = bytes.AsSpan(tile * section.TileBytes);
            decoded.Lows[tile] = BinaryPrimitives.ReadInt64LittleEndian(at);
            decoded.Highs[tile] = BinaryPrimitives.ReadInt64LittleEndian(at[8..]);
            decoded.Links[tile] = BinaryPrimitives.ReadInt32LittleEndian(at[16..]);
            decoded.Children[tile] = BinaryPrimitives.ReadInt32LittleEndian(at[20..]);
            decoded.TallyLinks[tile] = BinaryPrimitives.ReadInt32LittleEndian(at[24..]);
            long sum = 0;
            for (int slot = 0; slot < mechanisms; slot++)
            {
                int count = BinaryPrimitives.ReadInt32LittleEndian(at[(28 + (4 * slot))..]);
                decoded.Counts[(tile * mechanisms) + slot] = count;
                sum += count >= 0 ? count : throw Bad("a count below zero");
            }

            // A tile holds a record, follows the one before it in time and, wider than the finest, the children after the
            // ones before it; a finest tile's records, and every tile's tallies, follow the ones before it.
            bool valid = decoded.Lows[tile] <= decoded.Highs[tile] && sum > 0
                && decoded.TallyLinks[tile] >= 0
                && (tile == 0 || (decoded.Lows[tile] > decoded.Highs[tile - 1]
                    && decoded.TallyLinks[tile] > decoded.TallyLinks[tile - 1]))
                && (level == 0
                    ? decoded.Children[tile] == 0 && decoded.Links[tile] >= 0
                        && (tile == 0 || decoded.Links[tile] >= decoded.Links[tile - 1] + 5)
                    : decoded.Children[tile] is >= 1 and <= 10 && decoded.Links[tile] >= 0
                        && (long)decoded.Links[tile] + decoded.Children[tile] <= lower
                        && (tile == 0 || decoded.Links[tile] == decoded.Links[tile - 1] + decoded.Children[tile - 1]));
            if (!valid)
            {
                throw Bad("a tile it does not lay out");
            }
        }

        if ((level == 0 && (decoded.RecordsEnd <= decoded.Links[^1] || decoded.RecordsEnd + 4L > section.RecordsLength))
            || (level > 0 && decoded.RecordsEnd != 0))
        {
            throw Bad("records it places outside the section");
        }

        if (decoded.TalliesEnd <= decoded.TallyLinks[^1] || decoded.TalliesEnd + 4L > section.TalliesLength)
        {
            throw Bad("tallies it places outside the section");
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
/// records into columns, or a brush's records by process and channel, exactly as reading its rows would.
/// </summary>
internal sealed class TileReading(TileIndexFile file, FileStream stream) : IDisposable
{
    // For each level, the last block of tallies read and checked, kept while a brush descends below it and comes back.
    private readonly int[] talliesBlocks = new int[SessionTileIndex.MaximumLevels];
    private readonly byte[]?[] talliesBytes = new byte[]?[SessionTileIndex.MaximumLevels];
    private TileSection? talliesSection;

    // The column the last tile counted whole fell in, kept while tiles go on falling in it: tiles come in time order.
    private int lastColumn = -1;
    private long lastStart;
    private long lastEnd;

    // The records of the last finest tile read, and of the tiles after it in its block, read at once in a buffer each read
    // reuses: a dense count reads a block's records in one read, a boundary's barely more than its tile's.
    private byte[] recordsBytes = new byte[4_096];
    private TileSection? recordsSection;
    private int recordsBlock = -1;
    private int recordsFrom;

    // The last tile's tallies read and checked (ReadTallies), in buffers each read reuses.
    private uint[] ownerKeys = new uint[64];
    private byte[] ownerSlots = new byte[64];
    private long[] ownerCounts = new long[64];
    private int ownerEntries;
    private int[] channelNumbers = new int[64];
    private long[] channelCounts = new long[64];
    private int channelEntries;

    // The column of a group's lanes the last tile counted whole fell in, kept as the focus's own is.
    private int lastLaneColumn = -1;
    private long lastLaneStart;
    private long lastLaneEnd;

    public void Dispose() => stream.Dispose();

    /// <summary>What the file says its records' owners and channels were bound under.</summary>
    internal TileDerivation Derivation => file.Derivation;

    /// <summary>How many finest tiles' records this reading decoded: one tile a boundary falls among apiece.</summary>
    internal int TilesOfRecordsRead { get; private set; }

    /// <summary>How many tiles this reading counted whole from their tallies.</summary>
    internal int TilesOfTalliesRead { get; private set; }

    /// <summary>How many tiles a brush held whole that kept no tallies, and were counted from their children or records.</summary>
    internal int TilesUntallied { get; private set; }

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
    /// Counts the timed records of <paramref name="section"/>'s segment inside <paramref name="interval"/> into
    /// <paramref name="tally"/> as a brush counts them (<see cref="SessionIntervalQuery"/>): every one observed, each by the
    /// instance its owner binds to where <paramref name="policy"/> admits it, and each of a channel
    /// <paramref name="slotOfChannel"/> draws by that channel - a tile whole from its tallies wherever its records all lie
    /// in the interval, and a finest tile's records where one of the interval's ends falls among them. The caller has
    /// checked that the file's bindings are its own (<see cref="TileDerivation.Matches"/>). False, counting nothing, for a
    /// segment whose readings go backwards and meet the interval: its rows are read instead.
    /// </summary>
    public bool CountInterval(
        TileSection section,
        TimeRange interval,
        EvidencePolicy policy,
        int[] slotOfChannel,
        IntervalTally tally,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(slotOfChannel);
        ArgumentNullException.ThrowIfNull(tally);
        if (section.First is not { } first || section.Last is not { } last
            || last < interval.StartTicks || first >= interval.EndTicks)
        {
            return true;
        }

        if (!section.Ordered)
        {
            return false;
        }

        int top = section.Levels - 1;
        var brush = new Brush(interval, SessionTileIndex.AdmittedStrengths(policy), slotOfChannel, tally);
        RangeInterval(section, top, 0, section.TileCount(top), brush, cancellationToken);
        return true;
    }

    /// <summary>
    /// Counts the records of <paramref name="section"/>'s segment that a group's focus holds into its columns,
    /// <paramref name="focused"/>, and each member's into its lane of <paramref name="lanes"/>, as a focus counts its rows
    /// (<see cref="FocusRows"/>): every timed record inside the columns' interval whose owner binds to a member, where the
    /// policy admits the binding, and of <paramref name="mechanism"/> where it names one. A tile counts whole from its
    /// tallies wherever its records fall in one column of the focus and one of the lanes', and a finest tile's records
    /// are read where a boundary of either falls among them. The caller has checked that the file's bindings are its own.
    /// False, counting nothing, for a segment whose readings go backwards and meet the interval: its rows are read instead.
    /// </summary>
    public bool CountGroup(
        TileSection section,
        TimelineColumns focused,
        TimelineColumns[]? lanes,
        bool[] members,
        int[]? laneOf,
        Mechanism? mechanism,
        EvidencePolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(focused);
        ArgumentNullException.ThrowIfNull(members);
        TimeRange interval = focused.Interval;
        if (section.First is not { } first || section.Last is not { } last
            || last < interval.StartTicks || first >= interval.EndTicks)
        {
            return true;
        }

        if (!section.Ordered)
        {
            return false;
        }

        (lastColumn, lastLaneColumn) = (-1, -1);
        int top = section.Levels - 1;
        var group = new Group(focused, lanes, lanes is null || lanes[0].Counts.Count == focused.Counts.Count, members, laneOf,
            mechanism, SessionTileIndex.AdmittedStrengths(policy));
        RangeGroup(section, top, 0, section.TileCount(top), group, cancellationToken);
        return true;
    }

    /// <summary>
    /// Counts the tiles of one level from <paramref name="from"/> to before <paramref name="end"/> into a group's focus:
    /// each whole from its tallies that falls in one column of the focus and of its lanes, the children of each that does
    /// not or keeps no tallies, and the records of a finest one.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void RangeGroup(TileSection section, int level, int from, int end, Group group, CancellationToken cancellationToken)
    {
        TimelineColumns focused = group.Focused;
        TimeRange interval = focused.Interval;
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

            // A tile lying in one column of the view's and one of the lanes' counts there whole: from its tallies, or where
            // it keeps none from its children, or its records, without looking up each one's column.
            int column = -1;
            int laneColumn = -1;
            if (low >= interval.StartTicks && high < interval.EndTicks)
            {
                if (lastColumn < 0 || low < lastStart || low >= lastEnd)
                {
                    lastColumn = focused.ColumnOf(low)!.Value;
                    TimeRange bounds = TimelineColumns.IntervalOf(interval, focused.Counts.Count, lastColumn);
                    (lastStart, lastEnd) = (bounds.StartTicks, bounds.EndTicks);
                }

                // The lanes past the cell budget have columns of their own over the same interval, which a tile must fall
                // in one of too.
                bool whole = high < lastEnd;
                if (whole && !group.LanesShareColumns)
                {
                    TimelineColumns laneColumns = group.Lanes![0];
                    if (lastLaneColumn < 0 || low < lastLaneStart || low >= lastLaneEnd)
                    {
                        lastLaneColumn = laneColumns.ColumnOf(low)!.Value;
                        TimeRange bounds = TimelineColumns.IntervalOf(interval, laneColumns.Counts.Count, lastLaneColumn);
                        (lastLaneStart, lastLaneEnd) = (bounds.StartTicks, bounds.EndTicks);
                    }

                    whole = high < lastLaneEnd;
                }

                if (whole)
                {
                    (column, laneColumn) = (lastColumn, group.LanesShareColumns ? lastColumn : lastLaneColumn);
                    if (AddGroupTallies(section, level, block, position, group, column, laneColumn))
                    {
                        continue;
                    }
                }
            }

            if (level > 0)
            {
                RangeGroup(section, level - 1, block.Links[tile], block.Links[tile] + block.Children[tile], group, cancellationToken);
                continue;
            }

            var records = new RecordCursor(this, section, block, position);
            while (records.Next(out long reading, out int slot, out uint owner, out _))
            {
                if (owner == 0 || !group.Admitted[owner & 7] || !group.Members[(owner >> 3) - 1]
                    || (column < 0 && !interval.Contains(reading)))
                {
                    continue;
                }

                Mechanism mechanism = section.Mechanisms[slot];
                if (group.Mechanism is { } only && mechanism != only)
                {
                    continue;
                }

                int at = column >= 0 ? column : focused.ColumnOf(reading)!.Value;
                focused.Add(at, mechanism);
                if (group.LaneOf?[(owner >> 3) - 1] is >= 0 and int lane)
                {
                    TimelineColumns laneColumns = group.Lanes![lane];
                    laneColumns.Add(column >= 0 ? laneColumn : group.LanesShareColumns ? at : laneColumns.ColumnOf(reading)!.Value, mechanism);
                }
            }
        }
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
                var records = new RecordCursor(this, section, block, position);
                while (records.Next(out long reading, out int slot, out _, out _))
                {
                    if (columns.ColumnOf(reading) is { } column)
                    {
                        columns.Add(column, section.Mechanisms[slot]);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Counts the tiles of one level from <paramref name="from"/> to before <paramref name="end"/> into a brush's tally:
    /// each whole from its tallies that lies in the interval, the children of each that one of its ends falls among, and
    /// the records of a finest one that one does.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void RangeInterval(TileSection section, int level, int from, int end, Brush brush, CancellationToken cancellationToken)
    {
        TimeRange interval = brush.Interval;
        IntervalTally tally = brush.Tally;
        int slots = TimelineColumns.SlotCount;
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

            // A tile its interval holds whole is counted from its tallies, or, where it keeps none, as its children or its
            // records are.
            if (low >= interval.StartTicks && high < interval.EndTicks && AddTallies(section, level, block, position, brush))
            {
                continue;
            }

            if (level > 0)
            {
                RangeInterval(section, level - 1, block.Links[tile], block.Links[tile] + block.Children[tile], brush,
                    cancellationToken);
                continue;
            }

            var records = new RecordCursor(this, section, block, position);
            while (records.Next(out long reading, out int slot, out uint owner, out uint channel))
            {
                if (!interval.Contains(reading))
                {
                    continue;
                }

                tally.Observed++;
                if (owner != 0 && brush.Admitted[owner & 7])
                {
                    tally.Processes[((int)(owner >> 3) - 1) * slots + section.CountSlots[slot]]++;
                }

                if (channel != 0 && channel <= (uint)brush.SlotOfChannel.Length && brush.SlotOfChannel[channel - 1] is >= 0 and int drawn)
                {
                    tally.Graph++;
                    tally.Channels[drawn]++;
                }
            }
        }
    }

    /// <summary>
    /// Adds one tile's records to a brush's tally whole: all of them observed, each bound owner's the policy admits to its
    /// instance by mechanism, and each drawn channel's to it. False, adding nothing, for a tile that keeps no tallies,
    /// whose children or records are counted instead.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool AddTallies(TileSection section, int level, TileBlock block, int position, Brush brush)
    {
        long records = RecordsIn(section, block, position);
        if (!ReadTallies(section, level, block, position, records))
        {
            return false;
        }

        IntervalTally tally = brush.Tally;
        tally.Observed += records;
        int slots = TimelineColumns.SlotCount;
        for (int entry = 0; entry < ownerEntries; entry++)
        {
            uint owner = ownerKeys[entry];
            if (brush.Admitted[owner & 7])
            {
                tally.Processes[((int)(owner >> 3) - 1) * slots + section.CountSlots[ownerSlots[entry]]] += ownerCounts[entry];
            }
        }

        for (int entry = 0; entry < channelEntries; entry++)
        {
            int number = channelNumbers[entry];
            if (number < brush.SlotOfChannel.Length && brush.SlotOfChannel[number] is >= 0 and int drawn)
            {
                tally.Graph += channelCounts[entry];
                tally.Channels[drawn] += channelCounts[entry];
            }
        }

        return true;
    }

    /// <summary>
    /// Adds one tile's records to a group's focus whole, in <paramref name="column"/> of its columns and
    /// <paramref name="laneColumn"/> of its lanes': each bound owner's the policy admits, of a member, by mechanism, to the
    /// focus and to the member's lane. False, adding nothing, for a tile that keeps no tallies.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool AddGroupTallies(TileSection section, int level, TileBlock block, int position, Group group, int column, int laneColumn)
    {
        if (!ReadTallies(section, level, block, position, RecordsIn(section, block, position)))
        {
            return false;
        }

        for (int entry = 0; entry < ownerEntries; entry++)
        {
            uint owner = ownerKeys[entry];
            int instance = (int)(owner >> 3) - 1;
            Mechanism mechanism = section.Mechanisms[ownerSlots[entry]];
            if (!group.Admitted[owner & 7] || !group.Members[instance] || (group.Mechanism is { } only && mechanism != only))
            {
                continue;
            }

            int count = (int)ownerCounts[entry];
            group.Focused.Add(column, mechanism, count);
            if (group.LaneOf?[instance] is >= 0 and int lane)
            {
                group.Lanes![lane].Add(laneColumn, mechanism, count);
            }
        }

        return true;
    }

    /// <summary>How many timed records one tile holds: its counts' sum.</summary>
    private static long RecordsIn(TileSection section, TileBlock block, int position)
    {
        int tile = position % SessionTileIndex.TilesPerBlock;
        int mechanisms = section.Mechanisms.Length;
        long records = 0;
        for (int slot = 0; slot < mechanisms; slot++)
        {
            records += block.Counts[(tile * mechanisms) + slot];
        }

        return records;
    }

    /// <summary>
    /// Reads one tile's tallies into the entry buffers, checked against the tile as they are read: owners in order of key
    /// and mechanism, within the derivation's instances and the section's mechanisms; channels in order, within its
    /// channels; no count of zero or above the tile's <paramref name="records"/>, nor sums above them; nothing after the
    /// last. False, reading no entry, for a tile that keeps no tallies. Its block's tallies are checked against their
    /// checksum once.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool ReadTallies(TileSection section, int level, TileBlock block, int position, long records)
    {
        int tile = position % SessionTileIndex.TilesPerBlock;
        byte[] bytes = TalliesOf(section, level, block, position / SessionTileIndex.TilesPerBlock);
        int start = block.TallyLinks[0];
        var at = new VarintReader(bytes, block.TallyLinks[tile] - start,
            (tile + 1 < block.TallyLinks.Length ? block.TallyLinks[tile + 1] : block.TalliesEnd) - start);
        ulong owners = at.Next();
        if (owners == 0)
        {
            TilesUntallied++;
            return at.AtEnd ? false : throw Bad();
        }

        TilesOfTalliesRead++;
        int mechanisms = section.Mechanisms.Length;
        ulong lastOwner = ((ulong)file.Derivation.Instances << 3) | 7;
        ulong channels = (ulong)file.Derivation.Channels;

        // Each owner's key over the one before, or its mechanism after the one before under the same key.
        ownerEntries = 0;
        long owned = 0;
        ulong owner = 0;
        int previousSlot = -1;
        for (ulong entry = 0; entry < owners - 1; entry++)
        {
            ulong increase = at.Next();
            int slot = at.Byte();
            ulong count = at.Next();
            if (increase > lastOwner || (entry > 0 && increase == 0 && slot <= previousSlot))
            {
                throw Bad();
            }

            owner += increase;
            if (owner < 8 || owner > lastOwner || slot >= mechanisms || count == 0 || count > (ulong)records)
            {
                throw Bad();
            }

            owned += (long)count;
            previousSlot = slot;
            if (ownerEntries == ownerKeys.Length)
            {
                Array.Resize(ref ownerKeys, ownerEntries * 2);
                Array.Resize(ref ownerSlots, ownerEntries * 2);
                Array.Resize(ref ownerCounts, ownerEntries * 2);
            }

            (ownerKeys[ownerEntries], ownerSlots[ownerEntries], ownerCounts[ownerEntries]) = ((uint)owner, (byte)slot, (long)count);
            ownerEntries++;
        }

        // Each channel's number over the one before.
        channelEntries = 0;
        long bound = 0;
        ulong number = 0;
        ulong numbered = at.Next();
        for (ulong entry = 0; entry < numbered; entry++)
        {
            ulong increase = at.Next();
            ulong count = at.Next();
            if (increase >= channels || (entry > 0 && increase == 0))
            {
                throw Bad();
            }

            number += increase;
            if (number >= channels || count == 0 || count > (ulong)records)
            {
                throw Bad();
            }

            bound += (long)count;
            if (channelEntries == channelNumbers.Length)
            {
                Array.Resize(ref channelNumbers, channelEntries * 2);
                Array.Resize(ref channelCounts, channelEntries * 2);
            }

            (channelNumbers[channelEntries], channelCounts[channelEntries]) = ((int)number, (long)count);
            channelEntries++;
        }

        if (!at.AtEnd || owned > records || bound > records)
        {
            throw Bad();
        }

        return true;

        InvalidDataException Bad() => SessionTileIndex.Invalid(string.Create(CultureInfo.InvariantCulture,
            $"the tallies of tile {position} of level {level} of the section of '{section.Entry.Name}' are not the tile's."));
    }

    /// <summary>The bytes of the tallies of block <paramref name="index"/> of a level, read and checked once.</summary>
    private byte[] TalliesOf(TileSection section, int level, TileBlock block, int index)
    {
        if (!ReferenceEquals(talliesSection, section))
        {
            talliesSection = section;
            Array.Clear(talliesBytes);
        }

        if (talliesBytes[level] is { } known && talliesBlocks[level] == index)
        {
            return known;
        }

        int start = block.TallyLinks[0];
        byte[] read = SessionTileIndex.Read(stream, section.TalliesAt + start, block.TalliesEnd - start + 4);
        if (!SessionTileIndex.Matches(read))
        {
            throw SessionTileIndex.Mismatch(string.Create(CultureInfo.InvariantCulture,
                $"the tallies of block {index} of level {level} of the section of '{section.Entry.Name}'"));
        }

        (talliesBytes[level], talliesBlocks[level]) = (read, index);
        return read;
    }

    /// <summary>
    /// The records of the finest tile at <paramref name="position"/>, checked against their checksum: where they begin in
    /// the buffer every such read reuses, and how many bytes they take. They are read with the records of the tiles after
    /// them in their block, unless an earlier read of the block holds them already.
    /// </summary>
    private int RecordsOf(TileSection section, TileBlock block, int position, out int offset)
    {
        TilesOfRecordsRead++;
        int tile = position % SessionTileIndex.TilesPerBlock;
        int start = block.Links[tile];
        int end = tile + 1 < block.Links.Length ? block.Links[tile + 1] - 4 : block.RecordsEnd;
        int index = position / SessionTileIndex.TilesPerBlock;
        if (!ReferenceEquals(recordsSection, section) || recordsBlock != index || start < recordsFrom)
        {
            int rest = block.RecordsEnd + 4 - start;
            if (rest > recordsBytes.Length)
            {
                recordsBytes = new byte[Math.Max(rest, recordsBytes.Length * 2)];
            }

            SessionTileIndex.ReadInto(stream, section.RecordsAt + start, recordsBytes.AsSpan(0, rest));
            (recordsSection, recordsBlock, recordsFrom) = (section, index, start);
        }

        offset = start - recordsFrom;
        if (!SessionTileIndex.Matches(recordsBytes.AsSpan(offset, end - start + 4)))
        {
            throw SessionTileIndex.Mismatch(string.Create(CultureInfo.InvariantCulture,
                $"the records of tile {position} of the section of '{section.Entry.Name}'"));
        }

        return end - start;
    }

    /// <summary>What one brush counts and into what: its interval, the strengths its policy admits, the channels it draws.</summary>
    private sealed record Brush(TimeRange Interval, bool[] Admitted, int[] SlotOfChannel, IntervalTally Tally);

    /// <summary>
    /// What one group's focus counts and into what: its columns, its members' lanes and whether they share the columns, its
    /// members and their lanes by instance, the one mechanism it narrows to, and the strengths its policy admits.
    /// </summary>
    private sealed record Group(
        TimelineColumns Focused,
        TimelineColumns[]? Lanes,
        bool LanesShareColumns,
        bool[] Members,
        int[]? LaneOf,
        Mechanism? Mechanism,
        bool[] Admitted);

    /// <summary>
    /// One finest tile's records, read in row order, each checked against the tile: the first at its earliest reading,
    /// none past its latest, the last at it, their count its count, and each binding within the derivation's numbers.
    /// </summary>
    private ref struct RecordCursor
    {
        private readonly TileSection section;
        private readonly TileBlock block;
        private readonly int position;
        private readonly int tile;
        private readonly long expected;
        private readonly ulong lastOwner;
        private readonly ulong lastChannel;
        private VarintReader at;
        private long reading;
        private long counted;

        public RecordCursor(TileReading source, TileSection section, TileBlock block, int position)
        {
            this.section = section;
            this.block = block;
            this.position = position;
            tile = position % SessionTileIndex.TilesPerBlock;

            // Read first: the read can give the buffer a larger array.
            int length = source.RecordsOf(section, block, position, out int offset);
            at = new VarintReader(source.recordsBytes, offset, offset + length);
            int mechanisms = section.Mechanisms.Length;
            for (int slot = 0; slot < mechanisms; slot++)
            {
                expected += block.Counts[(tile * mechanisms) + slot];
            }

            reading = block.Lows[tile];
            lastOwner = ((ulong)source.Derivation.Instances << 3) | 7;
            lastChannel = (ulong)source.Derivation.Channels;
        }

        /// <summary>
        /// The next record: its reading, its mechanism's slot, its owner's key (<see cref="SessionTileIndex.OwnerKey"/>) and
        /// its channel's number plus one, 0 for none. False past the last, which is then checked to end the tile.
        /// </summary>
        public bool Next(out long next, out int slot, out uint owner, out uint channel)
        {
            if (at.AtEnd)
            {
                if (counted != expected || reading != block.Highs[tile])
                {
                    throw Bad();
                }

                (next, slot, owner, channel) = (0, 0, 0, 0);
                return false;
            }

            ulong increase = at.Next();
            if ((counted == 0 && increase != 0) || increase > (ulong)(block.Highs[tile] - reading))
            {
                throw Bad();
            }

            reading += (long)increase;
            slot = at.Byte();
            ulong ownerKey = at.Next();
            ulong channelKey = at.Next();
            if (slot >= section.Mechanisms.Length || ownerKey is > 0 and < 8 || ownerKey > lastOwner || channelKey > lastChannel)
            {
                throw Bad();
            }

            counted++;
            (next, owner, channel) = (reading, (uint)ownerKey, (uint)channelKey);
            return true;
        }

        private readonly InvalidDataException Bad() => SessionTileIndex.Invalid(string.Create(CultureInfo.InvariantCulture,
            $"the records of tile {position} of the section of '{section.Entry.Name}' are not the tile's."));
    }

    /// <summary>LEB128 values and single bytes out of a run of bytes, refusing one that runs past it or overflows.</summary>
    private ref struct VarintReader(byte[] bytes, int from, int to)
    {
        private int at = from;

        public readonly bool AtEnd => at >= to;

        public byte Byte() => at < to ? bytes[at++] : throw Short();

        public ulong Next()
        {
            // Most values take one byte.
            if (at < to && bytes[at] < 0x80)
            {
                return bytes[at++];
            }

            ulong value = 0;
            int shift = 0;
            byte next;
            do
            {
                if (at >= to || shift > 63)
                {
                    throw Short();
                }

                next = bytes[at++];
                value |= (ulong)(next & 0x7F) << shift;
                shift += 7;
            }
            while ((next & 0x80) != 0);

            return value;
        }

        private static InvalidDataException Short() =>
            SessionTileIndex.Invalid("a tile's records or tallies end inside a value.");
    }
}
