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
/// same way, at its two ends, and a focus on owners or on a channel its records column by column. None opens a segment,
/// and each reads in proportion to its view rather than to the session (S3). Every byte it interprets carries a checksum
/// of its own, as a segment's do, so it is read by the block, never hashed whole first.
/// </summary>
internal static class SessionTileIndex
{
    /// <summary>How many tiles one checked block of a level holds.</summary>
    public const int TilesPerBlock = 32;

    /// <summary>The bytes before the first section: magic, version, session and the generation the tiles describe.</summary>
    public const int HeaderLength = 48;

    /// <summary>The bytes after the directory, which say where it is.</summary>
    public const int FooterLength = 32;

    /// <summary>The first minor whose tiles keep their records' bytes, and whose records keep each one's measure.</summary>
    internal const ushort SummedMinor = 5;

    /// <summary>
    /// The first minor whose tiles keep their records' bytes tallied by what each record binds to: its owner, its channel
    /// and its accounting side, so a brush measures its processes' and channel ends' bytes from whole tiles.
    /// </summary>
    internal const ushort ByteTalliedMinor = 6;

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

    /// <summary>
    /// The bytes of a section's header but its mechanisms and its levels: 87 before minor 5, whose header says where its
    /// byte sums lie, and 103 before minor 6, whose header says where its byte tallies lie.
    /// </summary>
    private const int SectionHeaderBytes = 119;

    /// <summary>The longest a section's header can be, with every mechanism and level a section may name.</summary>
    private const int MaximumSectionHeaderBytes = SectionHeaderBytes + (2 * 256) + (12 * MaximumLevels);

    /// <summary>
    /// The most a finest tile's records take apiece: the increase, the kind (its mechanism's slot, its end and its
    /// direction), the owner, the channel and, since minor 5, the measure and the value it measured.
    /// </summary>
    private const int MaximumRecordBytes = 10 + 2 + 4 + 4 + 1 + 10;

    private const string FilePrefix = "tiles-";
    private const string FileSuffix = ".bin";
    private const string What = "persisted tile index";
    private const ushort Major = 1;

    /// <summary>
    /// Minor 6 keeps each tile's transport-observed bytes tallied by owner, channel and accounting side; minor 5, revision
    /// 704's, keeps each tile's transport-observed bytes by mechanism and accounting side, and each record's measure; minor
    /// 4, revision 701's, keeps a level spanning the generation's segments after their sections; minor 3, revision 460's,
    /// keeps each record's end of its channel beside its direction, and each tile's owner and channel tallies apart, its
    /// channels by mechanism, direction and end; minor 2, revision 459's, kept each record's direction and tallied owners by
    /// it; minor 1, revision 457's, kept each tile's tallies and each record's owner and channel; minor 0, revision 456's,
    /// kept none of these. Minors 3 to 5 are still read, what they keep counted and summed from them and the rest read from
    /// the segments, and are published again.
    /// </summary>
    internal const ushort Minor = 6;

    /// <summary>The earliest minor read: its sections hold every binding, direction and end a query counts.</summary>
    private const ushort EarliestReadMinor = 3;

    /// <summary>The first minor whose directory says where a level spanning the segments lies.</summary>
    private const ushort SpanningMinor = 4;

    /// <summary>
    /// The most finest tiles a level spanning segments has: its width is the narrowest power of ten, no narrower than any
    /// segment's finest tiles, that spans the generation's timed readings in at most this many.
    /// </summary>
    internal const long MaximumSpanningTiles = 2_048;

    /// <summary>A level spanning segments has at most one finest tile for each this many of the timed records it spans.</summary>
    internal const long RecordsPerSpanningTile = 256;

    /// <summary>
    /// The most tally entries the finest tiles of a level spanning segments hold while it is written, about 24 MB: past it,
    /// its tiles are made ten times wider, which sums the entries of a process or a channel each of them holds, and where
    /// no width spanning more than one tile keeps them within it, the index keeps no such level. As many byte tally entries
    /// again, about 64 MB, are held beside them; past that the level keeps no byte tallies.
    /// </summary>
    internal const long MaximumSpanningEntries = 2_097_152;

    /// <summary>
    /// The bytes of a spanning section's header but its mechanisms and its levels: 78 at minor 4, before it said where its
    /// byte sums lie, and 94 at minor 5, before it said where its byte tallies lie.
    /// </summary>
    private const int SpanningHeaderBytes = 110;

    /// <summary>The bytes of one extent of a spanning section: the section's place, earliest and latest reading, records.</summary>
    private const int ExtentBytes = 28;

    /// <summary>The direction code a kind holds for an `EN-Direction` code of 7 or more, which no direction is.</summary>
    private const int DirectionPastCodes = 7;

    /// <summary>
    /// A tile keeps its owner tallies, and its channel tallies, only where each takes at most a third of the bytes of the
    /// records beneath it, so both together take at most two thirds of them.
    /// </summary>
    private const int TallyShare = 3;

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
    /// canonical order of tile-index-v1: its header, a section per segment by name, the section of the level spanning
    /// them where they keep one, the directory and the footer. <paramref name="stated"/> is what the directory says the
    /// bindings are, which only a test of a stale derivation states otherwise, and <paramref name="spanningEntries"/> the
    /// most tally entries the spanning level's finest tiles hold while they are summed, which only a test of that bound
    /// lowers.
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
        TileDerivation? stated = null,
        long spanningEntries = MaximumSpanningEntries)
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
        (StoreDependency Dependency, SegmentReaderV1 Reader)[] ordered =
            [.. segments.OrderBy(segment => segment.Dependency.Name, StringComparer.Ordinal)];
        SpanningLevel? spanning = SpanningLevel.Plan([.. ordered.Select(segment => segment.Reader)], spanningEntries,
            cancellationToken);
        foreach ((StoreDependency dependency, SegmentReaderV1 reader) in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long offset = writer.Written;
            WriteSection(writer, reader, processes, relations, spanning, cancellationToken);
            entries.Str8(dependency.Name);
            entries.I64(dependency.LengthBytes);
            entries.Str8(dependency.Digest);
            entries.I64(offset);
            entries.I64(writer.Written - offset);
        }

        // The level spanning the segments follows their sections, and the directory says where it lies, or that there is none.
        long spanningOffset = writer.Written;
        if (spanning is { Abandoned: false })
        {
            spanning.Write(writer, cancellationToken);
        }

        bool spans = writer.Written > spanningOffset;
        entries.I64(spans ? spanningOffset : 0);
        entries.I64(writer.Written - spanningOffset);
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
    /// block by block; and every tile's owner tallies, then every tile's channel tallies, each level by level and block by
    /// block. A segment whose timed readings go backwards, or that has none, keeps no tiles: a zoom or a brush reads the
    /// first's rows, and the second has none. Its tiles are summed into <paramref name="spanning"/>'s, where a level spans
    /// the segments.
    /// </summary>
    private static void WriteSection(
        IndexFileWriter writer,
        SegmentReaderV1 segment,
        ProcessInstanceIndex processes,
        TransportRelationIndex relations,
        SpanningLevel? spanning,
        CancellationToken cancellationToken)
    {
        SegmentTimeTiles tiles = SegmentTimeTiles.Of(segment, cancellationToken);
        bool timed = tiles.First is not null;
        bool usable = timed && tiles.Ordered && tiles.Count > 0;
        Mechanism[] mechanisms = usable ? tiles.Mechanisms.ToArray() : [];
        List<Level> levels = [];
        byte[] records = [];
        byte[] ownerTallies = [];
        byte[] channelTallies = [];
        byte[] byteSums = [];
        byte[] byteTallies = [];
        if (usable)
        {
            PackedOwners owners = SegmentBindings.OwnersOf(segment, processes);
            PackedChannels channels = SegmentBindings.ChannelsOf(segment, relations);

            // Which end of its channel each row was made at, as a channel focus reading the row finds it.
            using RentedRows<sbyte> ends = RentedRows<sbyte>.For(segment);
            TransportRelationIndex.EndsOf(segment, ends.Span);

            // Its records' bytes are kept where reading its rows measures them; where that refuses a row, they are not, and
            // a query reads the rows, which refuse as before.
            bool summed = Summable(segment, cancellationToken);
            levels = Levels(segment, tiles, mechanisms, owners, channels, ends.Buffer, summed, cancellationToken);
            records = Records(segment, tiles, mechanisms, levels[0], owners, channels, ends.Buffer, summed, cancellationToken);
            (ownerTallies, channelTallies, byteSums, byteTallies) = Tallies(levels, summed, summed, cancellationToken);
            spanning?.Add(levels, mechanisms, tiles.Width, summed, cancellationToken);
        }

        int headerBytes = SectionHeaderBytes + (2 * mechanisms.Length) + (12 * levels.Count);
        (long[] levelOffsets, long next) = LevelOffsets(levels, mechanisms.Length, headerBytes);
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
        at.I64(ownerTallies.Length);
        at.I64(next + records.Length + ownerTallies.Length);
        at.I64(channelTallies.Length);
        at.I64(next + records.Length + ownerTallies.Length + channelTallies.Length);
        at.I64(byteSums.Length);
        at.I64(next + records.Length + ownerTallies.Length + channelTallies.Length + byteSums.Length);
        at.I64(byteTallies.Length);
        Crc32C.Write(head.AsSpan(headerBytes - 4), Crc32C.Compute(head.AsSpan(0, headerBytes - 4)));
        writer.Raw(head);
        WriteBlocks(writer, levels, mechanisms.Length, cancellationToken);
        writer.Raw(records);
        writer.Raw(ownerTallies);
        writer.Raw(channelTallies);
        writer.Raw(byteSums);
        writer.Raw(byteTallies);
    }

    /// <summary>
    /// Where each of <paramref name="levels"/> begins when the first begins at <paramref name="first"/>, the finest first,
    /// each after the one before, and where the last ends.
    /// </summary>
    private static (long[] Offsets, long End) LevelOffsets(List<Level> levels, int mechanisms, long first)
    {
        int tileBytes = TileBytes(mechanisms, Minor);
        long[] offsets = new long[levels.Count];
        long next = first;
        for (int level = 0; level < levels.Count; level++)
        {
            offsets[level] = next;
            next += LevelBytes(levels[level].Count, tileBytes, TrailerBytes(Minor));
        }

        return (offsets, next);
    }

    /// <summary>
    /// A section's levels, from the finest up, each a block of <see cref="TilesPerBlock"/> tiles after another: each tile's
    /// readings, links, children, tally links, counts and byte sums' link, and each block's trailer and checksum.
    /// </summary>
    private static void WriteBlocks(IndexFileWriter writer, List<Level> levels, int mechanisms, CancellationToken cancellationToken)
    {
        int tileBytes = TileBytes(mechanisms, Minor);
        int trailerBytes = TrailerBytes(Minor);
        byte[] block = new byte[(TilesPerBlock * tileBytes) + trailerBytes];
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
                    into.I32(tilesOf.OwnerLinks[tile]);
                    into.I32(tilesOf.ChannelLinks[tile]);
                    for (int slot = 0; slot < mechanisms; slot++)
                    {
                        into.I32(tilesOf.Counts[(tile * mechanisms) + slot]);
                    }

                    into.I32(tilesOf.SumLinks[tile]);
                    into.I32(tilesOf.ByteLinks[tile]);
                }

                // A block says where its byte tallies, its byte sums, its records and its tallies end, so each is read and
                // checked without the next block.
                into.I32(tilesOf.ByteTalliesEnd[first / TilesPerBlock]);
                into.I32(tilesOf.SumsEnd[first / TilesPerBlock]);
                into.I32(level == 0 ? tilesOf.RecordsEnd[first / TilesPerBlock] : 0);
                into.I32(tilesOf.OwnerTalliesEnd[first / TilesPerBlock]);
                into.I32(tilesOf.ChannelTalliesEnd[first / TilesPerBlock]);
                int length = (count * tileBytes) + trailerBytes - 4;
                Crc32C.Write(block.AsSpan(length), Crc32C.Compute(block.AsSpan(0, length)));
                writer.Raw(block.AsSpan(0, length + 4));
            }
        }
    }

    /// <summary>
    /// A section's levels, from the finest up to the first that one block holds: each tile's readings and counts, what its
    /// records' owners and channels bind to, tallied, and where <paramref name="summed"/>, its records' bytes, summed.
    /// </summary>
    private static List<Level> Levels(
        SegmentReaderV1 segment,
        SegmentTimeTiles tiles,
        Mechanism[] mechanisms,
        PackedOwners owners,
        PackedChannels channels,
        sbyte[] ends,
        bool summed,
        CancellationToken cancellationToken)
    {
        SegmentColumnSlice times = segment.Slice(SegmentColumnId.SessionRelativeTicks);
        SegmentColumnSlice kinds = segment.Slice(SegmentColumnId.Mechanism);
        SegmentColumnSlice directions = segment.Slice(SegmentColumnId.Direction);
        SegmentColumnSlice domains = segment.Slice(SegmentColumnId.ByteDomain);
        SegmentColumnSlice sides = segment.Slice(SegmentColumnId.AccountingSide);
        SegmentColumnSlice values = segment.Slice(SegmentColumnId.ByteValue);
        int[] slotOf = SlotsOf(mechanisms);
        var finest = new Level(tiles.Count, mechanisms.Length);
        var ownerCounts = new Dictionary<(uint Owner, byte Slot, byte Direction), int>();
        var channelCounts = new Dictionary<(int Number, byte Slot, byte Kind), int>();
        var sums = new SumsBuilder(mechanisms.Length);
        var byteTallies = new ByteTalliesBuilder();
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
            // mechanism and direction, a known channel by its number and the record's mechanism, direction and end. Its sums
            // add up the bytes of the same records by mechanism and accounting side, and its byte tallies by what each binds
            // to: its owner, its channel and its side, none being one too.
            ownerCounts.Clear();
            channelCounts.Clear();
            sums.Clear();
            byteTallies.Clear();
            (int firstRow, int endRow) = tiles.RowsOf(tile);
            for (int row = firstRow; row < endRow; row++)
            {
                if (times.SignedAt(row) is null)
                {
                    continue;
                }

                byte slot = (byte)slotOf[(int)(Mechanism)kinds.UnsignedAt(row)!.Value];
                byte direction = DirectionCode(directions, row);
                if (OwnerKey(owners.Raw(row)) is not 0 and uint owner)
                {
                    var key = (owner, slot, direction);
                    ownerCounts[key] = ownerCounts.GetValueOrDefault(key) + 1;
                }

                if (ChannelKey(channels.Raw(row)) is not 0 and uint channel)
                {
                    var key = ((int)channel - 1, slot, Kind(direction, ends[row]));
                    channelCounts[key] = channelCounts.GetValueOrDefault(key) + 1;
                }

                if (summed && MeasureOf(domains, sides, values, row) is { Side: > 0 } measure)
                {
                    sums.Add(slot, measure.Side, measure.Value);
                    byteTallies.Add(OwnerKey(owners.Raw(row)), ChannelKey(channels.Raw(row)), measure.Side, measure.Value);
                }
            }

            finest.Tallies[tile] = TileTallies.Of(ownerCounts, channelCounts);
            finest.Sums[tile] = sums.Build();
            finest.ByteTallies[tile] = byteTallies.Build();
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
        var ownerCounts = new Dictionary<(uint Owner, byte Slot, byte Direction), int>();
        var channelCounts = new Dictionary<(int Number, byte Slot, byte Kind), int>();
        var sums = new SumsBuilder(mechanisms);
        var byteTallies = new ByteTalliesBuilder();
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
            sums.Clear();
            byteTallies.Clear();
            for (int child = first; child < end; child++)
            {
                sums.Add(below.Sums[child]);
                byteTallies.Add(below.ByteTallies[child]);
                for (int slot = 0; slot < mechanisms; slot++)
                {
                    level.Counts[(parent * mechanisms) + slot] = checked(
                        level.Counts[(parent * mechanisms) + slot] + below.Counts[(child * mechanisms) + slot]);
                }

                TileTallies tallies = below.Tallies[child];
                for (int entry = 0; entry < tallies.Owners.Length; entry++)
                {
                    var key = (tallies.Owners[entry], tallies.Slots[entry], tallies.Directions[entry]);
                    ownerCounts[key] = checked(ownerCounts.GetValueOrDefault(key) + tallies.OwnerCounts[entry]);
                }

                for (int entry = 0; entry < tallies.Channels.Length; entry++)
                {
                    var key = (tallies.Channels[entry], tallies.ChannelSlots[entry], tallies.ChannelKinds[entry]);
                    channelCounts[key] = checked(channelCounts.GetValueOrDefault(key) + tallies.ChannelCounts[entry]);
                }
            }

            level.Tallies[parent] = TileTallies.Of(ownerCounts, channelCounts);
            level.Sums[parent] = sums.Build();
            level.ByteTallies[parent] = byteTallies.Build();
        }

        return level;
    }

    /// <summary>
    /// The records of every finest tile, each tile's after the one before and followed by its checksum, so a column
    /// boundary or a brush's end reads only the tile it falls among: each record its reading's increase over the record
    /// before it in its tile - the first's over the tile's earliest, so none - then its mechanism's slot times 32 plus its
    /// kind (its end of its channel and its direction), its owner's key, its channel's and its measure (<see cref="Measure"/>),
    /// none for every record where its section keeps no bytes. The tiles' links, and where each block's last tile's records
    /// end, are set as they are written.
    /// </summary>
    private static byte[] Records(
        SegmentReaderV1 segment,
        SegmentTimeTiles tiles,
        Mechanism[] mechanisms,
        Level finest,
        PackedOwners owners,
        PackedChannels channels,
        sbyte[] ends,
        bool summed,
        CancellationToken cancellationToken)
    {
        SegmentColumnSlice times = segment.Slice(SegmentColumnId.SessionRelativeTicks);
        SegmentColumnSlice kinds = segment.Slice(SegmentColumnId.Mechanism);
        SegmentColumnSlice directions = segment.Slice(SegmentColumnId.Direction);
        SegmentColumnSlice domains = segment.Slice(SegmentColumnId.ByteDomain);
        SegmentColumnSlice sides = segment.Slice(SegmentColumnId.AccountingSide);
        SegmentColumnSlice values = segment.Slice(SegmentColumnId.ByteValue);
        int[] slotOf = SlotsOf(mechanisms);
        var bytes = new MemoryStream();
        Span<byte> encoded = stackalloc byte[MaximumRecordBytes];
        byte[] checksum = new byte[4];
        for (int tile = 0; tile < finest.Count; tile++)
        {
            if ((tile & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            finest.Links[tile] = checked((int)bytes.Length);
            int tileStart = checked((int)bytes.Length);
            int measures = 0;
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
                int slot = slotOf[(int)(Mechanism)kinds.UnsignedAt(row)!.Value];
                length += Unsigned(encoded[length..], ((ulong)slot << 5) | Kind(DirectionCode(directions, row), ends[row]));
                length += Unsigned(encoded[length..], OwnerKey(owners.Raw(row)));
                length += Unsigned(encoded[length..], ChannelKey(channels.Raw(row)));
                int measure = Measure(encoded[length..], summed ? MeasureOf(domains, sides, values, row) : (0, null));
                length += measure;
                measures += measure;
                bytes.Write(encoded[..length]);
                previous = tick;
            }

            // What a tile's tallies are weighed against is what a count reads of its records: their measures, which only a
            // sum of bytes needs, are left out, so tallies are kept where minor 4 kept them.
            int end = checked((int)bytes.Length);
            finest.RecordBytes[tile] = end - tileStart - measures;
            finest.RecordsEnd[tile / TilesPerBlock] = end;
            Crc32C.Write(checksum, Crc32C.Compute(bytes.GetBuffer().AsSpan(tileStart, end - tileStart)));
            bytes.Write(checksum);
        }

        return bytes.ToArray();
    }

    /// <summary>
    /// Every tile's owner tallies, every tile's channel tallies and, where <paramref name="summed"/>, every tile's byte sums,
    /// apart, each level by level from the finest and block by block, each block's followed by its checksum. A tile keeps
    /// either part of its tallies only where it takes at most a third of the bytes of the records beneath it
    /// (<see cref="TallyShare"/>), so a query that counts a tile from its children or records instead reads at most three
    /// times what the tallies it counts would have taken; tallies that are not kept are a single zero. Kept owner tallies
    /// are how many owners plus one, then for each, in order of key, mechanism and direction, its key's increase over the
    /// one before (the first's over none), its mechanism's slot times 8 plus its direction, and its count. Kept channel
    /// tallies are how many channels plus one, then for each, in order of number, mechanism and kind, its number's increase
    /// over the one before (the first's over none), its mechanism's slot times 32 plus its kind, and its count. A tile's byte
    /// sums, at most four entries a mechanism, are always kept where the section keeps any: how many entries, then for each,
    /// in order of mechanism and side, its mechanism's slot times 4 plus its accounting side less one, how many of its
    /// records measured their bytes, how many declared a size they did not record, and the bytes they measured. Where
    /// <paramref name="tallyBytes"/>, a tile's byte tallies are kept as its tallies are, where they take at most a third of
    /// the bytes of the records beneath it: how many entries plus one, then for each, in order of owner key, channel key and
    /// side, its owner key's increase over the one before (the first's over none), its channel key's increase over the one
    /// before where its owner key is the one before's and otherwise its channel key, times 4, plus its accounting side less
    /// one, and its three sums. The tiles' links and each block's ends are set as they are written.
    /// </summary>
    private static (byte[] Owners, byte[] Channels, byte[] Sums, byte[] Bytes) Tallies(
        List<Level> levels, bool summed, bool tallyBytes, CancellationToken cancellationToken)
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

        var owners = new MemoryStream();
        var channels = new MemoryStream();
        var sums = new MemoryStream();
        var bytes = new MemoryStream();
        var kept = new MemoryStream();
        Span<byte> encoded = stackalloc byte[40];
        foreach (Level level in levels)
        {
            int ownersStart = checked((int)owners.Length);
            int channelsStart = checked((int)channels.Length);
            int sumsStart = checked((int)sums.Length);
            int bytesStart = checked((int)bytes.Length);
            for (int tile = 0; tile < level.Count; tile++)
            {
                if ((tile & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                TileTallies tallies = level.Tallies[tile];
                level.OwnerLinks[tile] = checked((int)owners.Length);
                kept.SetLength(0);
                kept.Write(encoded[..Unsigned(encoded, (ulong)tallies.Owners.Length + 1)]);
                uint previousOwner = 0;
                for (int entry = 0; entry < tallies.Owners.Length; entry++)
                {
                    int length = Unsigned(encoded, tallies.Owners[entry] - previousOwner);
                    length += Unsigned(encoded[length..], ((ulong)tallies.Slots[entry] << 3) | tallies.Directions[entry]);
                    length += Unsigned(encoded[length..], (ulong)tallies.OwnerCounts[entry]);
                    kept.Write(encoded[..length]);
                    previousOwner = tallies.Owners[entry];
                }

                Keep(owners, kept, level.RecordBytes[tile]);
                level.ChannelLinks[tile] = checked((int)channels.Length);
                kept.SetLength(0);
                kept.Write(encoded[..Unsigned(encoded, (ulong)tallies.Channels.Length + 1)]);
                int previousChannel = 0;
                for (int entry = 0; entry < tallies.Channels.Length; entry++)
                {
                    int length = Unsigned(encoded, (ulong)(tallies.Channels[entry] - previousChannel));
                    length += Unsigned(encoded[length..], ((ulong)tallies.ChannelSlots[entry] << 5) | tallies.ChannelKinds[entry]);
                    length += Unsigned(encoded[length..], (ulong)tallies.ChannelCounts[entry]);
                    kept.Write(encoded[..length]);
                    previousChannel = tallies.Channels[entry];
                }

                Keep(channels, kept, level.RecordBytes[tile]);
                if (summed)
                {
                    TileSums tileSums = level.Sums[tile];
                    level.SumLinks[tile] = checked((int)sums.Length);
                    sums.Write(encoded[..Unsigned(encoded, (ulong)tileSums.Slots.Length)]);
                    for (int entry = 0; entry < tileSums.Slots.Length; entry++)
                    {
                        int length = Unsigned(encoded, (ulong)((tileSums.Slots[entry] << 2) + tileSums.Sides[entry] - 1));
                        length += Unsigned(encoded[length..], (ulong)tileSums.Measured[entry]);
                        length += Unsigned(encoded[length..], (ulong)tileSums.Unmeasured[entry]);
                        length += Unsigned(encoded[length..], (ulong)tileSums.Totals[entry]);
                        sums.Write(encoded[..length]);
                    }
                }

                if (tallyBytes)
                {
                    TileByteTallies tileBytes = level.ByteTallies[tile];
                    level.ByteLinks[tile] = checked((int)bytes.Length);
                    kept.SetLength(0);
                    kept.Write(encoded[..Unsigned(encoded, (ulong)tileBytes.Keys.Length + 1)]);
                    uint ownerBefore = 0;
                    uint channelBefore = 0;
                    for (int entry = 0; entry < tileBytes.Keys.Length; entry++)
                    {
                        ulong key = tileBytes.Keys[entry];
                        uint owner = TileByteTallies.OwnerOf(key);
                        uint channel = TileByteTallies.ChannelOf(key);
                        ulong placed = owner == ownerBefore ? channel - channelBefore : channel;
                        int length = Unsigned(encoded, owner - ownerBefore);
                        length += Unsigned(encoded[length..], (placed << 2) + (ulong)(TileByteTallies.SideOf(key) - 1));
                        length += Unsigned(encoded[length..], (ulong)tileBytes.Measured[entry]);
                        length += Unsigned(encoded[length..], (ulong)tileBytes.Unmeasured[entry]);
                        length += Unsigned(encoded[length..], (ulong)tileBytes.Totals[entry]);
                        kept.Write(encoded[..length]);
                        (ownerBefore, channelBefore) = (owner, channel);
                    }

                    Keep(bytes, kept, level.RecordBytes[tile]);
                }

                if (tile % TilesPerBlock == TilesPerBlock - 1 || tile == level.Count - 1)
                {
                    level.OwnerTalliesEnd[tile / TilesPerBlock] = Close(owners, ref ownersStart);
                    level.ChannelTalliesEnd[tile / TilesPerBlock] = Close(channels, ref channelsStart);
                    if (summed)
                    {
                        level.SumsEnd[tile / TilesPerBlock] = Close(sums, ref sumsStart);
                    }

                    if (tallyBytes)
                    {
                        level.ByteTalliesEnd[tile / TilesPerBlock] = Close(bytes, ref bytesStart);
                    }
                }
            }
        }

        return (owners.ToArray(), channels.ToArray(), sums.ToArray(), bytes.ToArray());

        // A tile's tallies where they take at most a third of the bytes beneath it, else a single zero.
        static void Keep(MemoryStream into, MemoryStream kept, long beneath)
        {
            if (kept.Length * TallyShare <= beneath)
            {
                into.Write(kept.GetBuffer().AsSpan(0, (int)kept.Length));
            }
            else
            {
                into.WriteByte(0);
            }
        }

        // A block's tallies end in their checksum; the next block's begin after it.
        static int Close(MemoryStream into, ref int start)
        {
            int end = checked((int)into.Length);
            byte[] checksum = new byte[4];
            Crc32C.Write(checksum, Crc32C.Compute(into.GetBuffer().AsSpan(start, end - start)));
            into.Write(checksum);
            start = checked((int)into.Length);
            return end;
        }
    }

    /// <summary>
    /// A row's source direction as a tile index keeps it: its `EN-Direction` code, or <see cref="DirectionPastCodes"/> for
    /// one of 7 or more, which no direction is and a count by direction refuses, as it refuses any code it does not define.
    /// </summary>
    private static byte DirectionCode(SegmentColumnSlice directions, int row) =>
        directions.UnsignedAt(row) is { } code && code < DirectionPastCodes ? (byte)code : (byte)DirectionPastCodes;

    /// <summary>
    /// A record's kind: its direction code in the low three bits, and above them its end of its channel plus one - 0 for a
    /// record with no end of its own (<see cref="TransportRelationIndex.EndsOf(SegmentReaderV1, Span{sbyte})"/>), 1 for
    /// the first end, 2 for the second.
    /// </summary>
    private static byte Kind(byte direction, sbyte end) => (byte)(direction | ((end + 1) << 3));

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

    /// <summary>
    /// Whether reading <paramref name="segment"/>'s rows measures their transport-observed bytes without refusing one
    /// (`SegmentMeasurement.MeasureDomainByGroup`): each such row stating an accounting side §23 defines and the segment's
    /// one unit, and a value of zero or more or why it has none, and no side's bytes past what a sum holds. Where reading
    /// would refuse a row, the section keeps no bytes, and a query reads its rows, which refuse it as before.
    /// </summary>
    private static bool Summable(SegmentReaderV1 segment, CancellationToken cancellationToken)
    {
        SegmentColumnSlice domains = segment.Slice(SegmentColumnId.ByteDomain);
        SegmentColumnSlice sides = segment.Slice(SegmentColumnId.AccountingSide);
        SegmentColumnSlice units = segment.Slice(SegmentColumnId.MeasurementUnit);
        SegmentColumnSlice values = segment.Slice(SegmentColumnId.ByteValue);
        SegmentColumnSlice availability = segment.Slice(SegmentColumnId.ByteAvailability);
        ulong? unit = null;
        long[] totals = new long[(int)AccountingSide.CanonicalOwner + 1];
        try
        {
            for (int row = 0; row < segment.RowCount; row++)
            {
                if ((row & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (domains.UnsignedAt(row) is not { } domain || (ByteDomain)domain != ByteDomain.TransportObserved)
                {
                    continue;
                }

                if (sides.UnsignedAt(row) is not { } side || side is 0 or > (ulong)AccountingSide.CanonicalOwner
                    || units.UnsignedAt(row) is not { } rowUnit || (unit is { } established && established != rowUnit))
                {
                    return false;
                }

                unit = rowUnit;
                if (values.SignedAt(row) is { } value)
                {
                    if (value < 0)
                    {
                        return false;
                    }

                    totals[side] = checked(totals[side] + value);
                }
                else if (availability.UnsignedAt(row) is null)
                {
                    return false;
                }
            }
        }
        catch (OverflowException)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// A row's transport-observed measure, as reading it measures it, in a segment whose rows are <see cref="Summable"/>:
    /// its accounting side, 0 where it states no transport-observed bytes, and the value it measured, null where it declared
    /// a size it did not record.
    /// </summary>
    private static (int Side, long? Value) MeasureOf(
        SegmentColumnSlice domains, SegmentColumnSlice sides, SegmentColumnSlice values, int row) =>
        domains.UnsignedAt(row) is { } domain && (ByteDomain)domain == ByteDomain.TransportObserved
            ? ((int)sides.UnsignedAt(row)!.Value, values.SignedAt(row))
            : (0, null);

    /// <summary>
    /// Writes a record's measure as unsigned LEB128: 0 for none; its accounting side for a size it declared and did not
    /// record; its side plus four, then the value, for bytes it measured. Returns how many bytes.
    /// </summary>
    private static int Measure(Span<byte> destination, (int Side, long? Value) measure)
    {
        if (measure.Side == 0)
        {
            return Unsigned(destination, 0);
        }

        if (measure.Value is not { } value)
        {
            return Unsigned(destination, (ulong)measure.Side);
        }

        int length = Unsigned(destination, (ulong)measure.Side + 4);
        return length + Unsigned(destination[length..], (ulong)value);
    }

    /// <summary>
    /// A tile's bytes at <paramref name="minor"/>: its readings, links, children, tally links and counts, since minor 5
    /// where its byte sums begin, and since minor 6 where its byte tallies begin.
    /// </summary>
    internal static int TileBytes(int mechanisms, int minor) =>
        (minor >= ByteTalliedMinor ? 40 : minor >= SummedMinor ? 36 : 32) + (4 * mechanisms);

    /// <summary>
    /// The fixed bytes of a block after its tiles at <paramref name="minor"/>: since minor 6, where its byte tallies end;
    /// since minor 5, where its byte sums end; where its records end, where its owner tallies end, where its channel tallies
    /// end; its checksum.
    /// </summary>
    internal static int TrailerBytes(int minor) => minor >= ByteTalliedMinor ? 24 : minor >= SummedMinor ? 20 : 16;

    private static long LevelBytes(int tiles, int tileBytes, int trailerBytes) =>
        ((long)tiles * tileBytes) + ((long)((tiles + TilesPerBlock - 1) / TilesPerBlock) * trailerBytes);

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

        if (minor < EarliestReadMinor)
        {
            throw Invalid(string.Create(CultureInfo.InvariantCulture,
                $"it is minor {minor}, written before a tile index kept its records' bindings, directions and ends; a checkpoint publishes it again."));
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

        var entries = new List<TileSectionEntry>();
        long end = HeaderLength;
        string? previous = null;
        for (int index = 0; index < sections; index++)
        {
            string name = reader.Str8();
            long segmentLength = reader.I64();
            string digest = reader.Str8();
            long offset = reader.I64();
            long length = reader.I64();
            if (name.Length == 0 || (previous is not null && string.CompareOrdinal(previous, name) >= 0))
            {
                throw Invalid("its directory does not list its segments once each, by name.");
            }

            if (offset != end || length < 4 || offset + length > directoryOffset)
            {
                throw Invalid($"its directory places the section of '{name}' outside its sections.");
            }

            entries.Add(new(name, segmentLength, digest, offset, length));
            end = offset + length;
            previous = name;
        }

        // Since minor 4 the directory ends saying where the level spanning the segments lies, after their sections, or
        // that there is none.
        TileSectionEntry? spanning = null;
        if (minor >= SpanningMinor)
        {
            long offset = reader.I64();
            long length = reader.I64();
            if (length == 0 ? offset != 0
                : offset != end || length < SpanningHeaderLength(minor) || offset + length > directoryOffset)
            {
                throw Invalid("its directory places the level spanning its segments outside its sections.");
            }

            if (length > 0)
            {
                spanning = new(string.Empty, 0, string.Empty, offset, length);
                end = offset + length;
            }
        }

        reader.RequireEnd();
        if (end != directoryOffset)
        {
            throw Invalid("bytes lie between its last section and its directory.");
        }

        return new TileIndexFile(dependency, derivedGeneration, minor,
            new TileDerivation(bindingRule, relationRule, instances, channels, fingerprint), entries, spanning);
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

    /// <summary>
    /// A section's header, read and checked once, as <paramref name="minor"/> lays it out: what its segment holds and where
    /// each of its parts lies.
    /// </summary>
    internal static TileSection ReadSection(FileStream stream, TileSectionEntry entry, int minor)
    {
        byte[] bytes = Read(stream, entry.Offset, (int)Math.Min(entry.Length, MaximumSectionHeaderBytes));
        var at = new SpanReader(bytes, entry.Describe);
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
        long ownersOffset = at.I64();
        long ownersLength = at.I64();
        long channelsOffset = at.I64();
        long channelsLength = at.I64();

        // Since minor 5 the byte sums follow the channel tallies, and since minor 6 the byte tallies follow them and end where
        // the section does: none where it keeps none.
        (long sumsOffset, long sumsLength) = minor >= SummedMinor ? (at.I64(), at.I64()) : (channelsOffset + channelsLength, 0);
        (long bytesOffset, long bytesLength) = minor >= ByteTalliedMinor ? (at.I64(), at.I64()) : (sumsOffset + sumsLength, 0);
        int headerLength = at.Position + 4;
        Check(bytes.AsSpan(0, headerLength), $"the header of the section of '{entry.Name}'");
        bool ordered = (flags & Ordered) != 0;
        bool timed = (flags & Timed) != 0;
        bool usable = ordered && timed;
        int tileBytes = TileBytes(mechanisms, minor);
        long next = headerLength;
        bool laidOut = (flags & ~(Ordered | Timed)) == 0 && rows >= 0 && untimed >= 0 && untimed <= rows
            && (!timed || first <= last) && IsPowerOfTen(width)
            && (usable
                ? levels > 0 && mechanisms > 0
                : levels == 0 && mechanisms == 0 && recordsLength == 0 && ownersLength == 0 && channelsLength == 0
                    && sumsLength == 0 && bytesLength == 0);
        for (int level = 0; laidOut && level < levels; level++)
        {
            laidOut = counts[level] > 0 && offsets[level] == next
                && (level > 0 || counts[level] <= SegmentTimeTiles.MaximumTilesPerSpan + 1)
                && (level == 0 || counts[level] <= counts[level - 1])
                && (level == levels - 1 ? counts[level] <= TopLevelTiles : counts[level] > TopLevelTiles);
            next += LevelBytes(counts[level], tileBytes, TrailerBytes(minor));
        }

        // Each timed row is one record of at most MaximumRecordBytes, and each finest tile's records end in a checksum.
        long mostRecords = ((long)(rows - untimed) * MaximumRecordBytes) + (levels == 0 ? 0 : 4L * counts[0]);
        if (!laidOut || recordsOffset != next || recordsLength < 0 || recordsLength > mostRecords
            || ownersOffset != recordsOffset + recordsLength || ownersLength < 0
            || channelsOffset != ownersOffset + ownersLength || channelsLength < 0
            || sumsOffset != channelsOffset + channelsLength || sumsLength < 0
            || bytesOffset != sumsOffset + sumsLength || bytesLength < 0 || bytesOffset + bytesLength != entry.Length

            // A segment's section tallies its bytes by binding where it keeps them, and only there.
            || (minor >= ByteTalliedMinor && (sumsLength > 0) != (bytesLength > 0)))
        {
            throw Invalid($"the header of the section of '{entry.Name}' does not lay out its section.");
        }

        return new TileSection(entry, minor, rows, untimed, ordered, timed ? first : null, timed ? last : null, width, kinds,
            counts, offsets, recordsOffset, ownersOffset, channelsOffset, sumsOffset, bytesOffset);
    }

    /// <summary>
    /// The spanning section's header and the extents of the sections it spans, read and checked once: what it spans, where
    /// each of its parts lies, and, for each of the directory's <paramref name="sections"/> sections holding a timed record,
    /// its place, earliest and latest reading and how many timed records it holds.
    /// </summary>
    internal static TileSection ReadSpanning(FileStream stream, TileSectionEntry entry, int sections, int minor)
    {
        byte[] bytes = Read(stream, entry.Offset, (int)Math.Min(entry.Length, SpanningHeaderLength(minor) + (2 * 256) + (12 * MaximumLevels)));
        var at = new SpanReader(bytes, entry.Describe);
        int spanned = at.I32();
        int timedSections = at.I32();
        long records = at.I64();
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
                throw Invalid("the spanning section names its mechanisms out of order or names one no build defines.");
            }
        }

        int levels = at.U8();
        if (levels > MaximumLevels)
        {
            throw Invalid("the spanning section has more levels than its tiles need.");
        }

        var counts = new int[levels];
        var offsets = new long[levels];
        for (int level = 0; level < levels; level++)
        {
            counts[level] = at.I32();
            offsets[level] = at.I64();
        }

        long ownersOffset = at.I64();
        long ownersLength = at.I64();
        long channelsOffset = at.I64();
        long channelsLength = at.I64();
        (long sumsOffset, long sumsLength) = minor >= SummedMinor ? (at.I64(), at.I64()) : (channelsOffset + channelsLength, 0);
        (long bytesOffset, long bytesLength) = minor >= ByteTalliedMinor ? (at.I64(), at.I64()) : (sumsOffset + sumsLength, 0);
        int headerLength = at.Position + 4;
        Check(bytes.AsSpan(0, headerLength), "the header of the spanning section");
        long extentsLength = ((long)timedSections * ExtentBytes) + 4;
        if (spanned != sections || timedSections < 2 || timedSections > sections || records < timedSections || first > last
            || !IsPowerOfTen(width) || mechanisms == 0 || levels == 0 || headerLength + extentsLength > entry.Length)
        {
            throw Invalid("the header of the spanning section does not lay out its section.");
        }

        // Each section holding a timed record, in the directory's order, within what the level spans and adding up to it.
        byte[] listed = Read(stream, entry.Offset + headerLength, (int)extentsLength);
        Check(listed, "the extents of the spanning section");
        var extents = new TileExtent[timedSections];
        long summed = 0;
        for (int index = 0; index < timedSections; index++)
        {
            ReadOnlySpan<byte> extent = listed.AsSpan(index * ExtentBytes);
            extents[index] = new(
                BinaryPrimitives.ReadInt32LittleEndian(extent),
                BinaryPrimitives.ReadInt64LittleEndian(extent[4..]),
                BinaryPrimitives.ReadInt64LittleEndian(extent[12..]),
                BinaryPrimitives.ReadInt64LittleEndian(extent[20..]));
            if (extents[index].Section < (index == 0 ? 0 : extents[index - 1].Section + 1) || extents[index].Section >= sections
                || extents[index].First > extents[index].Last || extents[index].First < first || extents[index].Last > last
                || extents[index].Records < 1 || extents[index].Records > records - summed)
            {
                throw Invalid("the spanning section states an extent of a section it does not span.");
            }

            summed += extents[index].Records;
        }

        if (summed != records || extents.Min(extent => extent.First) != first || extents.Max(extent => extent.Last) != last)
        {
            throw Invalid("the spanning section's extents do not add up to what it spans.");
        }

        long next = headerLength + extentsLength;
        int tileBytes = TileBytes(mechanisms, minor);
        bool laidOut = true;
        for (int level = 0; laidOut && level < levels; level++)
        {
            laidOut = counts[level] > 0 && offsets[level] == next
                && (level > 0 || counts[level] <= MaximumSpanningTiles + 1)
                && (level == 0 || counts[level] <= counts[level - 1])
                && (level == levels - 1 ? counts[level] <= TopLevelTiles : counts[level] > TopLevelTiles);
            next += LevelBytes(counts[level], tileBytes, TrailerBytes(minor));
        }

        // The level keeps byte tallies only where it keeps byte sums: it can keep sums alone, where too many entries or a sum
        // past what one holds would have taken its byte tallies.
        if (!laidOut || ownersOffset != next || ownersLength < 0 || channelsOffset != ownersOffset + ownersLength
            || channelsLength < 0 || sumsOffset != channelsOffset + channelsLength || sumsLength < 0
            || bytesOffset != sumsOffset + sumsLength || bytesLength < 0 || bytesOffset + bytesLength != entry.Length
            || (bytesLength > 0 && sumsLength == 0))
        {
            throw Invalid("the header of the spanning section does not lay out its section.");
        }

        return new TileSection(entry, minor, 0, 0, ordered: true, first, last, width, kinds, counts, offsets, ownersOffset,
            ownersOffset, channelsOffset, sumsOffset, bytesOffset, extents, records);
    }

    /// <summary>
    /// The bytes of a spanning section's header but its mechanisms and its levels, as <paramref name="minor"/> lays it out.
    /// </summary>
    private static int SpanningHeaderLength(int minor) =>
        minor >= ByteTalliedMinor ? SpanningHeaderBytes : minor >= SummedMinor ? SpanningHeaderBytes - 16 : SpanningHeaderBytes - 32;

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
    /// One tile's tallies, in key order: each bound owner's key (<see cref="OwnerKey"/>) with the mechanism and direction of
    /// the records it owns and how many; and each known channel's number with the mechanism and kind (<see cref="Kind"/>)
    /// of the records it binds and how many.
    /// </summary>
    private sealed record TileTallies(
        uint[] Owners,
        byte[] Slots,
        byte[] Directions,
        int[] OwnerCounts,
        int[] Channels,
        byte[] ChannelSlots,
        byte[] ChannelKinds,
        int[] ChannelCounts)
    {
        public static TileTallies Of(
            Dictionary<(uint Owner, byte Slot, byte Direction), int> owners, Dictionary<(int Number, byte Slot, byte Kind), int> channels)
        {
            (uint Owner, byte Slot, byte Direction)[] ownerKeys = [.. owners.Keys.Order()];
            (int Number, byte Slot, byte Kind)[] channelKeys = [.. channels.Keys.Order()];
            return new(
                [.. ownerKeys.Select(key => key.Owner)],
                [.. ownerKeys.Select(key => key.Slot)],
                [.. ownerKeys.Select(key => key.Direction)],
                [.. ownerKeys.Select(key => owners[key])],
                [.. channelKeys.Select(key => key.Number)],
                [.. channelKeys.Select(key => key.Slot)],
                [.. channelKeys.Select(key => key.Kind)],
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

        /// <summary>
        /// The bytes of the records beneath each tile but their measures: a finest tile's own, a wider tile's children's.
        /// </summary>
        public long[] RecordBytes { get; } = new long[count];

        /// <summary>Each tile's owner tallies, in bytes into the owner tallies.</summary>
        public int[] OwnerLinks { get; } = new int[count];

        /// <summary>Each tile's channel tallies, in bytes into the channel tallies.</summary>
        public int[] ChannelLinks { get; } = new int[count];

        /// <summary>For the finest level, where each block's records end, before their checksum.</summary>
        public int[] RecordsEnd { get; } = new int[(count + TilesPerBlock - 1) / TilesPerBlock];

        /// <summary>Where each block's owner tallies end, before their checksum.</summary>
        public int[] OwnerTalliesEnd { get; } = new int[(count + TilesPerBlock - 1) / TilesPerBlock];

        /// <summary>Where each block's channel tallies end, before their checksum.</summary>
        public int[] ChannelTalliesEnd { get; } = new int[(count + TilesPerBlock - 1) / TilesPerBlock];

        /// <summary>Each tile's records' bytes, summed by mechanism and accounting side; none where its section keeps none.</summary>
        public TileSums[] Sums { get; } = [.. Enumerable.Repeat(TileSums.None, count)];

        /// <summary>Each tile's byte sums, in bytes into the byte sums; 0 where its section keeps none.</summary>
        public int[] SumLinks { get; } = new int[count];

        /// <summary>Where each block's byte sums end, before their checksum; 0 where its section keeps none.</summary>
        public int[] SumsEnd { get; } = new int[(count + TilesPerBlock - 1) / TilesPerBlock];

        /// <summary>Each tile's records' bytes, tallied by owner, channel and accounting side; none where its section keeps none.</summary>
        public TileByteTallies[] ByteTallies { get; } = [.. Enumerable.Repeat(TileByteTallies.None, count)];

        /// <summary>Each tile's byte tallies, in bytes into the byte tallies; 0 where its section keeps none.</summary>
        public int[] ByteLinks { get; } = new int[count];

        /// <summary>Where each block's byte tallies end, before their checksum; 0 where its section keeps none.</summary>
        public int[] ByteTalliesEnd { get; } = new int[(count + TilesPerBlock - 1) / TilesPerBlock];
    }

    /// <summary>
    /// One tile's transport-observed bytes, in order of mechanism slot and accounting side: for each pair its records
    /// measured or declared a size of, the bytes they measured, how many measured them and how many did not.
    /// </summary>
    private sealed record TileSums(byte[] Slots, byte[] Sides, long[] Totals, long[] Measured, long[] Unmeasured)
    {
        public static TileSums None { get; } = new([], [], [], [], []);
    }

    /// <summary>A tile's byte sums while they are added up: for each mechanism slot and accounting side, its three sums.</summary>
    private sealed class SumsBuilder(int mechanisms)
    {
        private const int Sides = (int)AccountingSide.CanonicalOwner;
        private readonly long[] totals = new long[mechanisms * Sides];
        private readonly long[] measured = new long[mechanisms * Sides];
        private readonly long[] unmeasured = new long[mechanisms * Sides];

        public void Clear()
        {
            Array.Clear(totals);
            Array.Clear(measured);
            Array.Clear(unmeasured);
        }

        /// <summary>Adds one record's measure: the bytes it measured, or a size it declared and did not record.</summary>
        public void Add(int slot, int side, long? value)
        {
            int at = (slot * Sides) + side - 1;
            if (value is { } bytes)
            {
                totals[at] = checked(totals[at] + bytes);
                measured[at]++;
            }
            else
            {
                unmeasured[at]++;
            }
        }

        /// <summary>Adds a child's sums, whose slots are these.</summary>
        public void Add(TileSums sums)
        {
            for (int entry = 0; entry < sums.Slots.Length; entry++)
            {
                int at = (sums.Slots[entry] * Sides) + sums.Sides[entry] - 1;
                totals[at] = checked(totals[at] + sums.Totals[entry]);
                measured[at] += sums.Measured[entry];
                unmeasured[at] += sums.Unmeasured[entry];
            }
        }

        public TileSums Build()
        {
            int count = 0;
            for (int at = 0; at < totals.Length; at++)
            {
                count += measured[at] + unmeasured[at] > 0 ? 1 : 0;
            }

            if (count == 0)
            {
                return TileSums.None;
            }

            var built = new TileSums(new byte[count], new byte[count], new long[count], new long[count], new long[count]);
            int entry = 0;
            for (int at = 0; at < totals.Length; at++)
            {
                if (measured[at] + unmeasured[at] > 0)
                {
                    (built.Slots[entry], built.Sides[entry], built.Totals[entry], built.Measured[entry], built.Unmeasured[entry]) =
                        ((byte)(at / Sides), (byte)((at % Sides) + 1), totals[at], measured[at], unmeasured[at]);
                    entry++;
                }
            }

            return built;
        }
    }

    /// <summary>
    /// One tile's records' bytes tallied by what each binds to (tile-index-v1 §1, minor 6), in key order: for each owner key,
    /// channel key and accounting side its records state, how many measured their bytes, how many declared a size they did
    /// not record, and the bytes they measured. An owner key or a channel key of 0 is a record bound to none, so a tile's
    /// byte tallies hold every byte its byte sums do.
    /// </summary>
    internal sealed record TileByteTallies(ulong[] Keys, long[] Measured, long[] Unmeasured, long[] Totals)
    {
        public static TileByteTallies None { get; } = new([], [], [], []);

        /// <summary>An entry's key: the owner key, then the channel key, then the accounting side, in that order of weight.</summary>
        public static ulong Key(uint owner, uint channel, int side) => ((ulong)owner << 32) | ((ulong)channel << 3) | (uint)side;

        public static uint OwnerOf(ulong key) => (uint)(key >> 32);

        public static uint ChannelOf(ulong key) => (uint)(key >> 3) & ChannelMask;

        public static int SideOf(ulong key) => (int)(key & 7);
    }

    /// <summary>A tile's byte tallies while they are added up: its records' measures, or its children's tallies.</summary>
    private sealed class ByteTalliesBuilder
    {
        private readonly Dictionary<ulong, (long Measured, long Unmeasured, long Total)> entries = [];

        public void Clear() => entries.Clear();

        /// <summary>Adds one record's measure: the bytes it measured, or a size it declared and did not record.</summary>
        public void Add(uint owner, uint channel, int side, long? value)
        {
            ulong key = TileByteTallies.Key(owner, channel, side);
            (long measured, long unmeasured, long total) = entries.GetValueOrDefault(key);
            entries[key] = value is { } bytes ? (measured + 1, unmeasured, checked(total + bytes)) : (measured, unmeasured + 1, total);
        }

        /// <summary>Adds a child's byte tallies.</summary>
        public void Add(TileByteTallies tallies)
        {
            for (int entry = 0; entry < tallies.Keys.Length; entry++)
            {
                (long measured, long unmeasured, long total) = entries.GetValueOrDefault(tallies.Keys[entry]);
                entries[tallies.Keys[entry]] = (measured + tallies.Measured[entry], unmeasured + tallies.Unmeasured[entry],
                    checked(total + tallies.Totals[entry]));
            }
        }

        public TileByteTallies Build()
        {
            if (entries.Count == 0)
            {
                return TileByteTallies.None;
            }

            ulong[] keys = [.. entries.Keys.Order()];
            return new(keys, [.. keys.Select(key => entries[key].Measured)], [.. keys.Select(key => entries[key].Unmeasured)],
                [.. keys.Select(key => entries[key].Total)]);
        }
    }

    /// <summary>
    /// The level spanning a generation's segments while it is written (tile-index-v1 §1): its finest tiles, each the sum of
    /// every segment's tile as wide as it is that holds the same readings - its counts, the bytes of its records and its
    /// owner and channel tallies - and the extent of each section holding a timed record. Its width is planned before the
    /// sections are written, and made ten times wider while its finest tiles hold more tally entries than its bound.
    /// </summary>
    private sealed class SpanningLevel
    {
        private readonly long budget;
        private SortedDictionary<long, SpanTile> tiles = [];
        private long entries;
        private long byteEntries;

        private SpanningLevel(long width, long budget, int sections, (int Section, long First, long Last, long Records)[] extents)
        {
            Width = width;
            this.budget = budget;
            Sections = sections;
            Extents = extents;
            First = extents.Min(extent => extent.First);
            Last = extents.Max(extent => extent.Last);
            Records = extents.Sum(extent => extent.Records);
        }

        /// <summary>A finest tile's width in presentation ticks, a power of ten.</summary>
        public long Width { get; private set; }

        /// <summary>Whether no width kept its finest tiles' entries within the bound: the index then keeps no such level.</summary>
        public bool Abandoned { get; private set; }

        /// <summary>
        /// Whether its tiles keep their records' bytes: only where every section it spans keeps them, and no sum passes what
        /// it holds.
        /// </summary>
        public bool Summed { get; private set; } = true;

        /// <summary>
        /// Whether its tiles keep their records' bytes tallied by owner, channel and side: only where they keep their bytes,
        /// no sum passes what it holds, and its finest tiles hold no more byte tally entries than its bound, once they are as
        /// wide as their tallies make them. Past that they are not made wider for them: they keep none, and a brush measures
        /// the sections beneath.
        /// </summary>
        public bool ByteTallied { get; private set; } = true;

        /// <summary>How many sections the directory lists, every one of which the level spans.</summary>
        public int Sections { get; }

        /// <summary>Each section holding a timed record, by its place in the directory: its earliest and latest reading, and how many.</summary>
        public (int Section, long First, long Last, long Records)[] Extents { get; }

        /// <summary>The earliest timed reading of any section.</summary>
        public long First { get; }

        /// <summary>The latest timed reading of any section.</summary>
        public long Last { get; }

        /// <summary>How many timed records the sections hold.</summary>
        public long Records { get; }

        /// <summary>
        /// The level spanning <paramref name="ordered"/>, a generation's segments in the directory's order, before their
        /// tiles are summed into it; null when fewer than two of them hold a timed record, or when one's timed readings go
        /// backwards, which keeps no tiles.
        /// </summary>
        public static SpanningLevel? Plan(SegmentReaderV1[] ordered, long budget, CancellationToken cancellationToken)
        {
            var extents = new List<(int Section, long First, long Last, long Records)>();
            long width = 1;
            for (int section = 0; section < ordered.Length; section++)
            {
                SegmentTimeTiles tiles = SegmentTimeTiles.Of(ordered[section], cancellationToken);
                if (tiles.First is not { } first || tiles.Last is not { } last)
                {
                    continue;
                }

                if (!tiles.Ordered)
                {
                    return null;
                }

                extents.Add((section, first, last, tiles.Rows - tiles.Untimed));
                width = Math.Max(width, tiles.Width);
            }

            if (extents.Count < 2)
            {
                return null;
            }

            // The narrowest power of ten, no narrower than any segment's finest tiles, spanning the readings in at most
            // MaximumSpanningTiles tiles and one for each RecordsPerSpanningTile records.
            long most = Math.Clamp(extents.Sum(extent => extent.Records) / RecordsPerSpanningTile, 1, MaximumSpanningTiles);
            Int128 span = (Int128)extents.Max(extent => extent.Last) - extents.Min(extent => extent.First) + 1;
            try
            {
                while (span > (Int128)most * width)
                {
                    width = checked(width * 10);
                }
            }
            catch (OverflowException)
            {
                return null;
            }

            return new(width, budget, ordered.Length, [.. extents]);
        }

        /// <summary>
        /// Sums one segment's tiles into the finest: its level as wide as they are, built above its top where they are wider,
        /// each tile into the one holding the same readings, and its tiles' bytes where it keeps them, <paramref name="summed"/>.
        /// Past the bound, the finest are made ten times wider until their entries are within it; where no width spanning
        /// more than one tile keeps them within it, the level is given up.
        /// </summary>
        public void Add(List<Level> levels, Mechanism[] mechanisms, long finestWidth, bool summed, CancellationToken cancellationToken)
        {
            if (Abandoned)
            {
                return;
            }

            if (!summed)
            {
                Unsum();
            }

            try
            {
                Level level = levels[0];
                long width = finestWidth;
                for (int above = 1; width < Width; above++)
                {
                    level = above < levels.Count ? levels[above] : Widen(level, mechanisms.Length);
                    width = checked(width * 10);
                }

                for (int tile = 0; tile < level.Count; tile++)
                {
                    if ((tile & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                    if (!tiles.TryGetValue(level.Indexes[tile], out SpanTile? into))
                    {
                        tiles.Add(level.Indexes[tile], into = new());
                    }

                    entries -= into.Entries;
                    into.Add(level, tile, mechanisms);
                    entries += into.Entries;
                    if (Summed && !into.AddSums(level.Sums[tile], mechanisms))
                    {
                        Unsum();
                    }

                    if (ByteTallied)
                    {
                        byteEntries -= into.ByteEntries;
                        if (!into.AddByteTallies(level.ByteTallies[tile]))
                        {
                            UntallyBytes();
                        }

                        byteEntries += into.ByteEntries;
                    }
                }

                while (entries > budget)
                {
                    if ((Int128)Last - First + 1 <= Width)
                    {
                        Abandon();
                        return;
                    }

                    Coarsen();
                }

                // Past the bound its tiles keep no byte tallies, rather than being made wider for them.
                if (byteEntries > budget)
                {
                    UntallyBytes();
                }
            }
            catch (OverflowException)
            {
                // A count past what a tile's word holds: the index keeps no level spanning these segments.
                Abandon();
            }
        }

        /// <summary>
        /// The spanning section: its header and the sections' extents, each followed by its checksum; its levels, from its
        /// finest tiles up to the first that one block holds, block by block; and every tile's owner tallies, then every
        /// tile's channel tallies, then where it keeps them every tile's byte sums, as a segment's section keeps them. It keeps
        /// no records: a query counts a finest tile it cannot count whole from the sections beneath it. Nothing is written
        /// where a sum overflows what the file holds.
        /// </summary>
        public void Write(IndexFileWriter writer, CancellationToken cancellationToken)
        {
            Mechanism[] mechanisms;
            List<Level> levels;
            byte[] ownerTallies;
            byte[] channelTallies;
            byte[] byteSums;
            byte[] byteTallies;
            try
            {
                // Its mechanisms are those its tiles count, in code order.
                bool[] present = new bool[TimelineColumns.MechanismCodes];
                foreach (SpanTile tile in tiles.Values)
                {
                    for (int code = 0; code < present.Length; code++)
                    {
                        present[code] |= tile.Counts[code] > 0;
                    }
                }

                mechanisms = [.. Enumerable.Range(0, present.Length).Where(code => present[code]).Select(code => (Mechanism)code)];
                int[] slotOf = SlotsOf(mechanisms);
                var finest = new Level(tiles.Count, mechanisms.Length);
                int at = 0;
                foreach ((long index, SpanTile tile) in tiles)
                {
                    finest.Lows[at] = tile.Low;
                    finest.Highs[at] = tile.High;
                    finest.Indexes[at] = index;
                    finest.RecordBytes[at] = tile.RecordBytes;
                    for (int slot = 0; slot < mechanisms.Length; slot++)
                    {
                        finest.Counts[(at * mechanisms.Length) + slot] = tile.Counts[(int)mechanisms[slot]];
                    }

                    finest.Sums[at] = tile.Sums(slotOf);
                    finest.ByteTallies[at] = tile.ByteTallies();
                    finest.Tallies[at++] = tile.Tallies(slotOf);
                }

                levels = [finest];
                while (levels[^1].Count > TopLevelTiles && levels.Count < MaximumLevels)
                {
                    levels.Add(Above(levels[^1], mechanisms.Length));
                }

                (ownerTallies, channelTallies, byteSums, byteTallies) = Tallies(levels, Summed, Summed && ByteTallied, cancellationToken);
            }
            catch (OverflowException)
            {
                return;
            }

            int headerBytes = SpanningHeaderBytes + (2 * mechanisms.Length) + (12 * levels.Count);
            int extentsBytes = (Extents.Length * ExtentBytes) + 4;
            (long[] levelOffsets, long next) = LevelOffsets(levels, mechanisms.Length, headerBytes + extentsBytes);
            byte[] head = new byte[headerBytes];
            var into = new SpanWriter(head);
            into.I32(Sections);
            into.I32(Extents.Length);
            into.I64(Records);
            into.I64(First);
            into.I64(Last);
            into.I64(Width);
            into.U8((byte)mechanisms.Length);
            foreach (Mechanism mechanism in mechanisms)
            {
                into.U16((ushort)mechanism);
            }

            into.U8((byte)levels.Count);
            for (int level = 0; level < levels.Count; level++)
            {
                into.I32(levels[level].Count);
                into.I64(levelOffsets[level]);
            }

            into.I64(next);
            into.I64(ownerTallies.Length);
            into.I64(next + ownerTallies.Length);
            into.I64(channelTallies.Length);
            into.I64(next + ownerTallies.Length + channelTallies.Length);
            into.I64(byteSums.Length);
            into.I64(next + ownerTallies.Length + channelTallies.Length + byteSums.Length);
            into.I64(byteTallies.Length);
            Crc32C.Write(head.AsSpan(headerBytes - 4), Crc32C.Compute(head.AsSpan(0, headerBytes - 4)));
            writer.Raw(head);

            byte[] extents = new byte[extentsBytes];
            var listed = new SpanWriter(extents);
            foreach ((int section, long first, long last, long records) in Extents)
            {
                listed.I32(section);
                listed.I64(first);
                listed.I64(last);
                listed.I64(records);
            }

            Crc32C.Write(extents.AsSpan(extentsBytes - 4), Crc32C.Compute(extents.AsSpan(0, extentsBytes - 4)));
            writer.Raw(extents);
            WriteBlocks(writer, levels, mechanisms.Length, cancellationToken);
            writer.Raw(ownerTallies);
            writer.Raw(channelTallies);
            writer.Raw(byteSums);
            writer.Raw(byteTallies);
        }

        /// <summary>
        /// The level ten times wider than <paramref name="below"/>, a segment's top or one built above it, with the bytes of
        /// the records beneath each of its tiles.
        /// </summary>
        private static Level Widen(Level below, int mechanisms)
        {
            Level level = Above(below, mechanisms);
            for (int tile = 0; tile < level.Count; tile++)
            {
                for (int child = level.Links[tile]; child < level.Links[tile] + level.Children[tile]; child++)
                {
                    level.RecordBytes[tile] += below.RecordBytes[child];
                }
            }

            return level;
        }

        /// <summary>Makes the finest tiles ten times wider: each the sum of the ten it holds.</summary>
        private void Coarsen()
        {
            Width = checked(Width * 10);
            var wider = new SortedDictionary<long, SpanTile>();
            bool summed = Summed;
            bool tallied = ByteTallied;
            foreach ((long index, SpanTile tile) in tiles)
            {
                long parent = SegmentTimeTiles.FloorDivide(index, 10);
                if (wider.TryGetValue(parent, out SpanTile? into))
                {
                    into.Absorb(tile);
                    summed = summed && into.AbsorbSums(tile);
                    tallied = tallied && into.AbsorbByteTallies(tile);
                }
                else
                {
                    wider.Add(parent, tile);
                }
            }

            tiles = wider;
            entries = tiles.Values.Sum(tile => (long)tile.Entries);
            byteEntries = tiles.Values.Sum(tile => (long)tile.ByteEntries);
            if (!summed)
            {
                Unsum();
            }

            if (!tallied)
            {
                UntallyBytes();
            }
        }

        private void Abandon()
        {
            tiles.Clear();
            entries = 0;
            byteEntries = 0;
            Abandoned = true;
        }

        /// <summary>Keeps no bytes in its tiles: a section it spans keeps none, or a sum passed what it holds.</summary>
        private void Unsum()
        {
            if (Summed)
            {
                Summed = false;
                foreach (SpanTile tile in tiles.Values)
                {
                    tile.DropSums();
                }
            }

            UntallyBytes();
        }

        /// <summary>
        /// Keeps no byte tallies in its tiles: they keep no bytes, a sum passed what it holds, or their entries passed what it
        /// allows.
        /// </summary>
        private void UntallyBytes()
        {
            if (ByteTallied)
            {
                ByteTallied = false;
                byteEntries = 0;
                foreach (SpanTile tile in tiles.Values)
                {
                    tile.DropByteTallies();
                }
            }
        }
    }

    /// <summary>
    /// One finest tile of a level spanning segments while it is summed: its earliest and latest reading, its counts by
    /// mechanism code, the bytes of the records beneath it, and its owner and channel tallies, each a key - an owner's key or
    /// a channel's number, a mechanism's code and a direction or a kind - in increasing order, with a count.
    /// </summary>
    private sealed class SpanTile
    {
        private const int Sides = (int)AccountingSide.CanonicalOwner;
        private ulong[] ownerKeys = [];
        private int[] ownerCounts = [];
        private ulong[] channelKeys = [];
        private int[] channelCounts = [];

        // Its records' bytes by mechanism code and accounting side, while it keeps them and once a tile with bytes is added.
        private long[]? totals;
        private long[]? measured;
        private long[]? unmeasured;

        // Its records' bytes tallied by owner key, channel key and side, in key order, while its level keeps them.
        private TileByteTallies bytes = TileByteTallies.None;

        public long Low { get; private set; } = long.MaxValue;

        public long High { get; private set; } = long.MinValue;

        public long RecordBytes { get; private set; }

        public int[] Counts { get; } = new int[TimelineColumns.MechanismCodes];

        /// <summary>How many owner and channel entries it holds.</summary>
        public int Entries => ownerKeys.Length + channelKeys.Length;

        /// <summary>How many byte tally entries it holds.</summary>
        public int ByteEntries => bytes.Keys.Length;

        /// <summary>Adds one segment's tile of <paramref name="level"/>, whose slots are of <paramref name="mechanisms"/>.</summary>
        public void Add(Level level, int tile, Mechanism[] mechanisms)
        {
            (Low, High) = (Math.Min(Low, level.Lows[tile]), Math.Max(High, level.Highs[tile]));
            RecordBytes += level.RecordBytes[tile];
            for (int slot = 0; slot < mechanisms.Length; slot++)
            {
                int code = (int)mechanisms[slot];
                Counts[code] = checked(Counts[code] + level.Counts[(tile * mechanisms.Length) + slot]);
            }

            TileTallies tallies = level.Tallies[tile];
            ulong[] owners = new ulong[tallies.Owners.Length];
            for (int entry = 0; entry < owners.Length; entry++)
            {
                owners[entry] = Key(tallies.Owners[entry], (int)mechanisms[tallies.Slots[entry]], tallies.Directions[entry]);
            }

            ulong[] channels = new ulong[tallies.Channels.Length];
            for (int entry = 0; entry < channels.Length; entry++)
            {
                channels[entry] = Key((uint)tallies.Channels[entry], (int)mechanisms[tallies.ChannelSlots[entry]], tallies.ChannelKinds[entry]);
            }

            (ownerKeys, ownerCounts) = Merge(ownerKeys, ownerCounts, owners, tallies.OwnerCounts);
            (channelKeys, channelCounts) = Merge(channelKeys, channelCounts, channels, tallies.ChannelCounts);
        }

        /// <summary>Adds another finest tile, whose readings lie beside these: what making the tiles wider sums.</summary>
        public void Absorb(SpanTile other)
        {
            (Low, High) = (Math.Min(Low, other.Low), Math.Max(High, other.High));
            RecordBytes += other.RecordBytes;
            for (int code = 0; code < Counts.Length; code++)
            {
                Counts[code] = checked(Counts[code] + other.Counts[code]);
            }

            (ownerKeys, ownerCounts) = Merge(ownerKeys, ownerCounts, other.ownerKeys, other.ownerCounts);
            (channelKeys, channelCounts) = Merge(channelKeys, channelCounts, other.channelKeys, other.channelCounts);
        }

        /// <summary>
        /// Adds one segment's tile's byte sums, whose slots are of <paramref name="mechanisms"/>; false where a sum passes
        /// what it holds.
        /// </summary>
        public bool AddSums(TileSums sums, Mechanism[] mechanisms)
        {
            if (sums.Slots.Length == 0)
            {
                return true;
            }

            totals ??= new long[TimelineColumns.MechanismCodes * Sides];
            measured ??= new long[totals.Length];
            unmeasured ??= new long[totals.Length];
            try
            {
                for (int entry = 0; entry < sums.Slots.Length; entry++)
                {
                    int at = ((int)mechanisms[sums.Slots[entry]] * Sides) + sums.Sides[entry] - 1;
                    totals[at] = checked(totals[at] + sums.Totals[entry]);
                    measured[at] = checked(measured[at] + sums.Measured[entry]);
                    unmeasured[at] = checked(unmeasured[at] + sums.Unmeasured[entry]);
                }
            }
            catch (OverflowException)
            {
                return false;
            }

            return true;
        }

        /// <summary>Adds another finest tile's byte sums, as <see cref="Absorb"/> adds its counts; false where a sum overflows.</summary>
        public bool AbsorbSums(SpanTile other)
        {
            if (other.totals is null)
            {
                return true;
            }

            totals ??= new long[other.totals.Length];
            measured ??= new long[totals.Length];
            unmeasured ??= new long[totals.Length];
            try
            {
                for (int at = 0; at < totals.Length; at++)
                {
                    totals[at] = checked(totals[at] + other.totals[at]);
                    measured[at] = checked(measured[at] + other.measured![at]);
                    unmeasured[at] = checked(unmeasured[at] + other.unmeasured![at]);
                }
            }
            catch (OverflowException)
            {
                return false;
            }

            return true;
        }

        /// <summary>Keeps no byte sums.</summary>
        public void DropSums() => (totals, measured, unmeasured) = (null, null, null);

        /// <summary>Adds one segment's tile's byte tallies; false where a sum passes what it holds.</summary>
        public bool AddByteTallies(TileByteTallies added) => MergeBytes(added);

        /// <summary>Adds another finest tile's byte tallies, as <see cref="Absorb"/> adds its counts; false where a sum overflows.</summary>
        public bool AbsorbByteTallies(SpanTile other) => MergeBytes(other.bytes);

        /// <summary>Keeps no byte tallies.</summary>
        public void DropByteTallies() => bytes = TileByteTallies.None;

        /// <summary>Its byte tallies as a section's tile keeps them; their keys name no mechanism, so they need no slots.</summary>
        public TileByteTallies ByteTallies() => bytes;

        /// <summary>Two runs of byte tally entries in key order as one, the sums of a key both hold added; false where one overflows.</summary>
        private bool MergeBytes(TileByteTallies added)
        {
            if (added.Keys.Length == 0)
            {
                return true;
            }

            if (bytes.Keys.Length == 0)
            {
                bytes = added;
                return true;
            }

            int count = bytes.Keys.Length + added.Keys.Length;
            var merged = new TileByteTallies(new ulong[count], new long[count], new long[count], new long[count]);
            int a = 0;
            int b = 0;
            int at = 0;
            try
            {
                for (; a < bytes.Keys.Length || b < added.Keys.Length; at++)
                {
                    if (b == added.Keys.Length || (a < bytes.Keys.Length && bytes.Keys[a] < added.Keys[b]))
                    {
                        (merged.Keys[at], merged.Measured[at], merged.Unmeasured[at], merged.Totals[at]) =
                            (bytes.Keys[a], bytes.Measured[a], bytes.Unmeasured[a], bytes.Totals[a]);
                        a++;
                    }
                    else if (a == bytes.Keys.Length || added.Keys[b] < bytes.Keys[a])
                    {
                        (merged.Keys[at], merged.Measured[at], merged.Unmeasured[at], merged.Totals[at]) =
                            (added.Keys[b], added.Measured[b], added.Unmeasured[b], added.Totals[b]);
                        b++;
                    }
                    else
                    {
                        (merged.Keys[at], merged.Measured[at], merged.Unmeasured[at], merged.Totals[at]) = (bytes.Keys[a],
                            checked(bytes.Measured[a] + added.Measured[b]), checked(bytes.Unmeasured[a] + added.Unmeasured[b]),
                            checked(bytes.Totals[a] + added.Totals[b]));
                        a++;
                        b++;
                    }
                }
            }
            catch (OverflowException)
            {
                return false;
            }

            bytes = at == count ? merged : new(merged.Keys[..at], merged.Measured[..at], merged.Unmeasured[..at], merged.Totals[..at]);
            return true;
        }

        /// <summary>
        /// Its byte sums as a section's tile keeps them, each mechanism by its slot of <paramref name="slotOf"/>: in code
        /// order, which is the slots' order, then by side.
        /// </summary>
        public TileSums Sums(int[] slotOf)
        {
            if (totals is null)
            {
                return TileSums.None;
            }

            var entries = new List<int>();
            for (int at = 0; at < totals.Length; at++)
            {
                if (measured![at] + unmeasured![at] > 0)
                {
                    entries.Add(at);
                }
            }

            return entries.Count == 0 ? TileSums.None : new(
                [.. entries.Select(at => (byte)slotOf[at / Sides])],
                [.. entries.Select(at => (byte)((at % Sides) + 1))],
                [.. entries.Select(at => totals[at])],
                [.. entries.Select(at => measured![at])],
                [.. entries.Select(at => unmeasured![at])]);
        }

        /// <summary>Its tallies as a section's tile keeps them, each mechanism by its slot of <paramref name="slotOf"/>.</summary>
        public TileTallies Tallies(int[] slotOf) => new(
            [.. ownerKeys.Select(key => (uint)(key >> 16))],
            [.. ownerKeys.Select(key => (byte)slotOf[(int)(key >> 8) & 0xFF])],
            [.. ownerKeys.Select(key => (byte)(key & 0xFF))],
            ownerCounts,
            [.. channelKeys.Select(key => (int)(key >> 16))],
            [.. channelKeys.Select(key => (byte)slotOf[(int)(key >> 8) & 0xFF])],
            [.. channelKeys.Select(key => (byte)(key & 0xFF))],
            channelCounts);

        /// <summary>An entry's key: an owner's key or a channel's number, then a mechanism's code, then a direction or a kind.</summary>
        private static ulong Key(uint value, int code, byte kind) => ((ulong)value << 16) | ((ulong)code << 8) | kind;

        /// <summary>Two runs of entries in key order as one, the counts of a key both hold summed.</summary>
        private static (ulong[] Keys, int[] Counts) Merge(ulong[] keys, int[] counts, ulong[] added, int[] addedCounts)
        {
            if (added.Length == 0)
            {
                return (keys, counts);
            }

            if (keys.Length == 0)
            {
                return (added, addedCounts);
            }

            ulong[] mergedKeys = new ulong[keys.Length + added.Length];
            int[] mergedCounts = new int[mergedKeys.Length];
            int a = 0;
            int b = 0;
            int at = 0;
            for (; a < keys.Length || b < added.Length; at++)
            {
                if (b == added.Length || (a < keys.Length && keys[a] < added[b]))
                {
                    mergedKeys[at] = keys[a];
                    mergedCounts[at] = counts[a++];
                }
                else if (a == keys.Length || added[b] < keys[a])
                {
                    mergedKeys[at] = added[b];
                    mergedCounts[at] = addedCounts[b++];
                }
                else
                {
                    mergedKeys[at] = keys[a];
                    mergedCounts[at] = checked(counts[a++] + addedCounts[b++]);
                }
            }

            if (at < mergedKeys.Length)
            {
                Array.Resize(ref mergedKeys, at);
                Array.Resize(ref mergedCounts, at);
            }

            return (mergedKeys, mergedCounts);
        }
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
    internal ref struct SpanReader(ReadOnlySpan<byte> bytes, string what)
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
                throw Invalid($"{what} ends inside its header.");
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

/// <summary>
/// Where one segment's section lies, and the segment it describes, as the directory lists it; or, named by no segment,
/// where the level spanning them lies.
/// </summary>
internal sealed record TileSectionEntry(string Name, long SegmentLength, string SegmentDigest, long Offset, long Length)
{
    /// <summary>The section as a refusal names it.</summary>
    public string Describe => Name.Length == 0 ? "the spanning section" : $"the section of '{Name}'";

    /// <summary>Whether the section describes exactly <paramref name="segment"/>: its name, length and digest.</summary>
    public bool Describes(StoreDependency segment) =>
        string.Equals(segment.Name, Name, StringComparison.Ordinal)
        && segment.LengthBytes == SegmentLength
        && string.Equals(segment.Digest, SegmentDigest, StringComparison.Ordinal);
}

/// <summary>
/// A section a level spanning segments spans, as it states it: the section's place in the directory, its earliest and
/// latest timed reading, and how many timed records it holds.
/// </summary>
internal readonly record struct TileExtent(int Section, long First, long Last, long Records);

/// <summary>
/// One segment's tiles as its section's header states them, or the tiles of the level spanning every segment. Its levels
/// are read a block at a time, each block checked, its finest tiles' records only where a column boundary or a brush's
/// end falls among them, and a tile's tallies only where a brush counts it whole. The spanning section keeps no records:
/// a finest tile of it a query cannot count whole is counted from the sections its readings meet.
/// </summary>
internal sealed class TileSection(
    TileSectionEntry entry,
    int minor,
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
    long ownersOffset,
    long channelsOffset,
    long sumsOffset,
    long bytesOffset,
    TileExtent[]? extents = null,
    long spanned = 0)
{
    // The extents by earliest reading, and the latest reading among each and those before it: the sections a range meets
    // are found among those whose earliest reading is before its end, back to where none before reaches its start.
    private readonly (TileExtent[] ByFirst, long[] LatestBefore) sorted = Sorted(extents ?? []);

    public TileSectionEntry Entry { get; } = entry;

    /// <summary>Whether it is the level spanning every segment, which keeps no records.</summary>
    public bool Spanning => extents is not null;

    /// <summary>For the spanning section, each section it spans that holds a timed record, in the directory's order.</summary>
    public IReadOnlyList<TileExtent> Extents { get; } = extents ?? [];

    /// <summary>For the spanning section, how many timed records it spans.</summary>
    public long Spanned { get; } = spanned;

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

    /// <summary>The minor its file was written as, which lays out its tiles, blocks and records.</summary>
    internal int Minor { get; } = minor;

    internal int TileBytes => SessionTileIndex.TileBytes(Mechanisms.Length, Minor);

    internal int TrailerBytes => SessionTileIndex.TrailerBytes(Minor);

    /// <summary>
    /// Whether its tiles keep their records' transport-observed bytes, and its records each one's measure: since minor 5,
    /// where reading its segment's rows measures them without refusing one.
    /// </summary>
    internal bool KeepsBytes => Minor >= SessionTileIndex.SummedMinor && SumsLength > 0;

    /// <summary>
    /// Whether its tiles keep their records' bytes tallied by owner, channel and side, so a brush measures its processes'
    /// and channel ends' bytes from them: since minor 6, where it keeps its bytes, though the level spanning the segments can
    /// keep its sums alone.
    /// </summary>
    internal bool KeepsByteTallies => Minor >= SessionTileIndex.ByteTalliedMinor && ByteTalliesLength > 0;

    /// <summary>Whether its records each carry a measure, as minor 5's do, of none where its section keeps no bytes.</summary>
    internal bool RecordsMeasure => Minor >= SessionTileIndex.SummedMinor;

    /// <summary>Where block <paramref name="block"/> of a level lies in the file, and how long it is, its trailer included.</summary>
    internal (long Offset, int Length) BlockAt(int level, int block)
    {
        int tiles = Math.Min(SessionTileIndex.TilesPerBlock, counts[level] - (block * SessionTileIndex.TilesPerBlock));
        long within = offsets[level] + ((long)block * ((SessionTileIndex.TilesPerBlock * TileBytes) + TrailerBytes));
        return (Entry.Offset + within, (tiles * TileBytes) + TrailerBytes);
    }

    /// <summary>Where the finest tiles' records begin in the file.</summary>
    internal long RecordsAt => Entry.Offset + recordsOffset;

    /// <summary>The bytes of records the section holds, their checksums among them.</summary>
    internal long RecordsLength => ownersOffset - recordsOffset;

    /// <summary>Where the tiles' owner tallies begin in the file.</summary>
    internal long OwnerTalliesAt => Entry.Offset + ownersOffset;

    /// <summary>The bytes of owner tallies the section holds, their checksums among them.</summary>
    internal long OwnerTalliesLength => channelsOffset - ownersOffset;

    /// <summary>Where the tiles' channel tallies begin in the file.</summary>
    internal long ChannelTalliesAt => Entry.Offset + channelsOffset;

    /// <summary>The bytes of channel tallies the section holds, their checksums among them.</summary>
    internal long ChannelTalliesLength => sumsOffset - channelsOffset;

    /// <summary>Where the tiles' byte sums begin in the file.</summary>
    internal long SumsAt => Entry.Offset + sumsOffset;

    /// <summary>The bytes of byte sums the section holds, their checksums among them; none where it keeps no bytes.</summary>
    internal long SumsLength => bytesOffset - sumsOffset;

    /// <summary>Where the tiles' byte tallies begin in the file.</summary>
    internal long ByteTalliesAt => Entry.Offset + bytesOffset;

    /// <summary>The bytes of byte tallies the section holds, their checksums among them; none where it keeps none.</summary>
    internal long ByteTalliesLength => Entry.Length - bytesOffset;

    /// <summary>
    /// For the spanning section, the sections it spans that hold a timed reading in <paramref name="range"/>, in the
    /// directory's order.
    /// </summary>
    internal List<TileExtent> Meeting(TimeRange range)
    {
        (TileExtent[] byFirst, long[] latestBefore) = sorted;
        int low = 0;
        int high = byFirst.Length;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (byFirst[middle].First < range.EndTicks)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        var meeting = new List<TileExtent>();
        for (int index = low - 1; index >= 0 && latestBefore[index] >= range.StartTicks; index--)
        {
            if (byFirst[index].Last >= range.StartTicks)
            {
                meeting.Add(byFirst[index]);
            }
        }

        meeting.Sort((one, other) => one.Section.CompareTo(other.Section));
        return meeting;
    }

    private static (TileExtent[] ByFirst, long[] LatestBefore) Sorted(TileExtent[] extents)
    {
        TileExtent[] byFirst = [.. extents.OrderBy(extent => extent.First).ThenBy(extent => extent.Section)];
        long[] latestBefore = new long[byFirst.Length];
        for (int index = 0; index < byFirst.Length; index++)
        {
            latestBefore[index] = index == 0 ? byFirst[0].Last : Math.Max(latestBefore[index - 1], byFirst[index].Last);
        }

        return (byFirst, latestBefore);
    }
}

/// <summary>One checked block of a level: its tiles' readings, links, children, tally links and counts.</summary>
internal sealed class TileBlock
{
    public required long[] Lows { get; init; }

    public required long[] Highs { get; init; }

    public required int[] Links { get; init; }

    public required int[] Children { get; init; }

    /// <summary>Where each tile's owner tallies begin within the section's owner tallies.</summary>
    public required int[] OwnerLinks { get; init; }

    /// <summary>Where each tile's channel tallies begin within the section's channel tallies.</summary>
    public required int[] ChannelLinks { get; init; }

    public required int[] Counts { get; init; }

    /// <summary>For a finest block, where its last tile's records end within the section's records, before their checksum.</summary>
    public required int RecordsEnd { get; init; }

    /// <summary>Where its tiles' owner tallies end within the section's owner tallies, before their checksum.</summary>
    public required int OwnerTalliesEnd { get; init; }

    /// <summary>Where its tiles' channel tallies end within the section's channel tallies, before their checksum.</summary>
    public required int ChannelTalliesEnd { get; init; }

    /// <summary>Where each tile's byte sums begin within the section's byte sums; 0 where it keeps none.</summary>
    public required int[] SumLinks { get; init; }

    /// <summary>Where its tiles' byte sums end within the section's byte sums, before their checksum; 0 where it keeps none.</summary>
    public required int SumsEnd { get; init; }

    /// <summary>Where each tile's byte tallies begin within the section's byte tallies; 0 where it keeps none.</summary>
    public required int[] ByteLinks { get; init; }

    /// <summary>Where its tiles' byte tallies end within the section's byte tallies, before their checksum; 0 where it keeps none.</summary>
    public required int ByteTalliesEnd { get; init; }
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
    private readonly TileSectionEntry[] ordered;
    private readonly TileSectionEntry? spanningEntry;
    private readonly Dictionary<string, TileSection> sections = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Section, int Level, int Block), TileBlock> blocks = [];
    private readonly Lock gate = new();
    private TileSection? spanning;
    private int blocksDecoded;

    internal TileIndexFile(
        StoreDependency dependency,
        long derivedGeneration,
        int minor,
        TileDerivation derivation,
        IReadOnlyList<TileSectionEntry> entries,
        TileSectionEntry? spanning)
    {
        Dependency = dependency;
        DerivedGeneration = derivedGeneration;
        Minor = minor;
        Derivation = derivation;
        ordered = [.. entries];
        this.entries = entries.ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        spanningEntry = spanning;
    }

    public StoreDependency Dependency { get; }

    /// <summary>The generation whose segments it describes.</summary>
    public long DerivedGeneration { get; }

    /// <summary>The minor version it was written as: 3 keeps no level spanning its segments, and is published again.</summary>
    public int Minor { get; }

    /// <summary>What it says its records' bindings were made under: a brush counts its tallies only for that derivation.</summary>
    public TileDerivation Derivation { get; }

    /// <summary>The segments it holds a section of, by name.</summary>
    public IReadOnlyCollection<string> Segments => entries.Keys;

    /// <summary>Whether it keeps a level spanning its segments.</summary>
    public bool Spans => spanningEntry is not null;

    /// <summary>How many blocks were read from the file and checked, rather than found already read: what a zoom costs.</summary>
    internal int BlocksDecoded => Volatile.Read(ref blocksDecoded);

    /// <summary>Whether it holds a section describing exactly <paramref name="segment"/>.</summary>
    public bool Describes(StoreDependency segment) =>
        entries.TryGetValue(segment.Name, out TileSectionEntry? entry) && entry.Describes(segment);

    /// <summary>
    /// Whether it keeps a level spanning exactly <paramref name="segments"/>: a section describing each of them and of no
    /// other segment, so the level's tiles count their records and no others.
    /// </summary>
    public bool SpansExactly(IReadOnlyCollection<StoreDependency> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return spanningEntry is not null && entries.Count == segments.Count && segments.All(Describes);
    }

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

        TileSection read = SessionTileIndex.ReadSection(stream, entries[name], Minor);
        lock (gate)
        {
            return sections.TryAdd(name, read) ? read : sections[name];
        }
    }

    /// <summary>
    /// The section of the level spanning the segments, read and checked once: its header, its extents, and its top block,
    /// whose counts must add up to the records it spans.
    /// </summary>
    internal TileSection Spanning(FileStream stream)
    {
        lock (gate)
        {
            if (spanning is not null)
            {
                return spanning;
            }
        }

        TileSectionEntry entry = spanningEntry ?? throw new InvalidOperationException("The tile index keeps no level spanning its segments.");
        TileSection read = SessionTileIndex.ReadSpanning(stream, entry, ordered.Length, Minor);
        int top = read.Levels - 1;
        TileBlock block = Block(stream, read, top, 0);
        if (block.Counts.Sum(count => (long)count) != read.Spanned)
        {
            throw SessionTileIndex.Invalid("the spanning section's tiles do not count the records it spans.");
        }

        lock (gate)
        {
            return spanning ??= read;
        }
    }

    /// <summary>
    /// The section of the segment <paramref name="extent"/> names, which the spanning section spans: its timed readings,
    /// their extent and their count as the spanning section states them, and tiles it keeps.
    /// </summary>
    internal TileSection Spanned(FileStream stream, TileExtent extent)
    {
        TileSectionEntry entry = ordered[extent.Section];
        TileSection section = Section(stream, entry.Name);
        if (!section.Ordered || section.Levels == 0 || section.First != extent.First || section.Last != extent.Last
            || section.Rows - section.Untimed != extent.Records)
        {
            throw SessionTileIndex.Invalid($"the spanning section does not state the extent of {entry.Describe}.");
        }

        return section;
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
                $"block {block} of level {level} of {section.Entry.Describe}"));
        }

        int tiles = (length - section.TrailerBytes) / section.TileBytes;
        int mechanisms = section.Mechanisms.Length;
        bool measures = section.RecordsMeasure;
        bool tallied = section.Minor >= SessionTileIndex.ByteTalliedMinor;
        var decoded = new TileBlock
        {
            Lows = new long[tiles],
            Highs = new long[tiles],
            Links = new int[tiles],
            Children = new int[tiles],
            OwnerLinks = new int[tiles],
            ChannelLinks = new int[tiles],
            Counts = new int[tiles * mechanisms],
            SumLinks = new int[tiles],
            SumsEnd = measures ? BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(length - 20)) : 0,
            ByteLinks = new int[tiles],
            ByteTalliesEnd = tallied ? BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(length - 24)) : 0,
            RecordsEnd = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(length - 16)),
            OwnerTalliesEnd = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(length - 12)),
            ChannelTalliesEnd = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(length - 8)),
        };
        int lower = level == 0 ? 0 : section.TileCount(level - 1);
        for (int tile = 0; tile < tiles; tile++)
        {
            ReadOnlySpan<byte> at = bytes.AsSpan(tile * section.TileBytes);
            decoded.Lows[tile] = BinaryPrimitives.ReadInt64LittleEndian(at);
            decoded.Highs[tile] = BinaryPrimitives.ReadInt64LittleEndian(at[8..]);
            decoded.Links[tile] = BinaryPrimitives.ReadInt32LittleEndian(at[16..]);
            decoded.Children[tile] = BinaryPrimitives.ReadInt32LittleEndian(at[20..]);
            decoded.OwnerLinks[tile] = BinaryPrimitives.ReadInt32LittleEndian(at[24..]);
            decoded.ChannelLinks[tile] = BinaryPrimitives.ReadInt32LittleEndian(at[28..]);
            long sum = 0;
            for (int slot = 0; slot < mechanisms; slot++)
            {
                int count = BinaryPrimitives.ReadInt32LittleEndian(at[(32 + (4 * slot))..]);
                decoded.Counts[(tile * mechanisms) + slot] = count;
                sum += count >= 0 ? count : throw Bad("a count below zero");
            }

            decoded.SumLinks[tile] = measures ? BinaryPrimitives.ReadInt32LittleEndian(at[(32 + (4 * mechanisms))..]) : 0;
            decoded.ByteLinks[tile] = tallied ? BinaryPrimitives.ReadInt32LittleEndian(at[(36 + (4 * mechanisms))..]) : 0;

            // A tile holds a record, follows the one before it in time and, wider than the finest, the children after the
            // ones before it; a finest tile's records, and every tile's owner and channel tallies, follow the ones before it.
            // A finest tile of the spanning section keeps no records, and its readings lie within one tile of its width,
            // after the one before it's.
            bool valid = decoded.Lows[tile] <= decoded.Highs[tile] && sum > 0
                && decoded.OwnerLinks[tile] >= 0 && decoded.ChannelLinks[tile] >= 0
                && (tile == 0 || (decoded.Lows[tile] > decoded.Highs[tile - 1]
                    && decoded.OwnerLinks[tile] > decoded.OwnerLinks[tile - 1]
                    && decoded.ChannelLinks[tile] > decoded.ChannelLinks[tile - 1]))
                && (level > 0
                    ? decoded.Children[tile] is >= 1 and <= 10 && decoded.Links[tile] >= 0
                        && (long)decoded.Links[tile] + decoded.Children[tile] <= lower
                        && (tile == 0 || decoded.Links[tile] == decoded.Links[tile - 1] + decoded.Children[tile - 1])
                    : section.Spanning
                        ? decoded.Children[tile] == 0 && decoded.Links[tile] == 0
                            && SegmentTimeTiles.FloorDivide(decoded.Lows[tile], section.Width)
                                == SegmentTimeTiles.FloorDivide(decoded.Highs[tile], section.Width)
                            && (tile == 0 || SegmentTimeTiles.FloorDivide(decoded.Lows[tile], section.Width)
                                > SegmentTimeTiles.FloorDivide(decoded.Highs[tile - 1], section.Width))
                        : decoded.Children[tile] == 0 && decoded.Links[tile] >= 0
                            && (tile == 0 || decoded.Links[tile] >= decoded.Links[tile - 1] + 5));
            if (!valid)
            {
                throw Bad("a tile it does not lay out");
            }
        }

        if ((level == 0 && !section.Spanning && (decoded.RecordsEnd <= decoded.Links[^1] || decoded.RecordsEnd + 4L > section.RecordsLength))
            || ((level > 0 || section.Spanning) && decoded.RecordsEnd != 0))
        {
            throw Bad("records it places outside the section");
        }

        if (decoded.OwnerTalliesEnd <= decoded.OwnerLinks[^1] || decoded.OwnerTalliesEnd + 4L > section.OwnerTalliesLength
            || decoded.ChannelTalliesEnd <= decoded.ChannelLinks[^1] || decoded.ChannelTalliesEnd + 4L > section.ChannelTalliesLength)
        {
            throw Bad("tallies it places outside the section");
        }

        // A tile's byte sums, where its section keeps them, follow the ones before it's and end before the block's end; where
        // it keeps none, no tile's and no block's place any.
        bool sumsLaidOut = section.KeepsBytes
            ? decoded.SumLinks[0] >= 0 && decoded.SumsEnd > decoded.SumLinks[^1] && decoded.SumsEnd + 4L <= section.SumsLength
            : decoded.SumsEnd == 0 && decoded.SumLinks[0] == 0;
        for (int tile = 1; sumsLaidOut && tile < tiles; tile++)
        {
            sumsLaidOut = section.KeepsBytes ? decoded.SumLinks[tile] > decoded.SumLinks[tile - 1] : decoded.SumLinks[tile] == 0;
        }

        if (!sumsLaidOut)
        {
            throw Bad("byte sums it places outside the section");
        }

        // So are its byte tallies, where its section keeps them.
        bool talliesLaidOut = section.KeepsByteTallies
            ? decoded.ByteLinks[0] >= 0 && decoded.ByteTalliesEnd > decoded.ByteLinks[^1]
                && decoded.ByteTalliesEnd + 4L <= section.ByteTalliesLength
            : decoded.ByteTalliesEnd == 0 && decoded.ByteLinks[0] == 0;
        for (int tile = 1; talliesLaidOut && tile < tiles; tile++)
        {
            talliesLaidOut = section.KeepsByteTallies
                ? decoded.ByteLinks[tile] > decoded.ByteLinks[tile - 1]
                : decoded.ByteLinks[tile] == 0;
        }

        if (!talliesLaidOut)
        {
            throw Bad("byte tallies it places outside the section");
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
            $"block {block} of level {level} of {section.Entry.Describe} holds {what}."));
    }
}

/// <summary>
/// One query's reads of a tile index: the file held open while it counts, each read checked. It counts a segment's timed
/// records into columns, or a brush's records by process and channel, exactly as reading its rows would.
/// </summary>
internal sealed class TileReading(TileIndexFile file, FileStream stream) : IDisposable
{
    // For each level, the last block of owner tallies and of channel tallies read and checked, kept while a query descends
    // below it and comes back.
    private readonly TallyBlocks ownerBlocks = new();
    private readonly TallyBlocks channelBlocks = new();

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

    // The last tile's owner and channel tallies read and checked, in buffers each read reuses.
    private uint[] ownerKeys = new uint[64];
    private byte[] ownerSlots = new byte[64];
    private byte[] ownerDirections = new byte[64];
    private long[] ownerCounts = new long[64];
    private int ownerEntries;
    private int[] channelNumbers = new int[64];
    private byte[] channelSlots = new byte[64];
    private byte[] channelKinds = new byte[64];
    private long[] channelCounts = new long[64];
    private int channelEntries;

    // The last tile's byte sums read and checked, in buffers each read reuses, and the last block of them, per level.
    private readonly TallyBlocks sumBlocks = new();
    private byte[] sumSlots = new byte[16];
    private byte[] sumSides = new byte[16];
    private long[] sumTotals = new long[16];
    private long[] sumMeasured = new long[16];
    private long[] sumUnmeasured = new long[16];
    private int sumEntries;

    // The last tile's byte tallies read and checked, in buffers each read reuses, and the last block of them, per level.
    private readonly TallyBlocks byteBlocks = new();
    private uint[] byteOwners = new uint[64];
    private uint[] byteChannels = new uint[64];
    private byte[] byteSides = new byte[64];
    private long[] byteMeasured = new long[64];
    private long[] byteUnmeasured = new long[64];
    private long[] byteTotals = new long[64];
    private int byteEntries;

    // The column of a group's lanes the last tile counted whole fell in, kept as the focus's own is.
    private int lastLaneColumn = -1;
    private long lastLaneStart;
    private long lastLaneEnd;

    // The part of the columns' interval a zoom or a focus counts: all of it, or a range the spanning section left to a
    // segment's section.
    private TimeRange within;

    // While a query counts the spanning section, the ranges of its finest tiles it could not count whole; null while it
    // counts a segment's section.
    private TileRemainders? remainders;

    public void Dispose() => stream.Dispose();

    /// <summary>What the file says its records' owners and channels were bound under.</summary>
    internal TileDerivation Derivation => file.Derivation;

    /// <summary>How many finest tiles' records this reading decoded: one tile a boundary falls among apiece.</summary>
    internal int TilesOfRecordsRead { get; private set; }

    /// <summary>How many tiles this reading counted whole from their tallies.</summary>
    internal int TilesOfTalliesRead { get; private set; }

    /// <summary>
    /// How many tiles a query held whole that kept none of the tallies it counts - a brush both, a group its owners', a
    /// channel its channels' - and were counted from their children or records.
    /// </summary>
    internal int TilesUntallied { get; private set; }

    /// <summary>The section of the segment <paramref name="name"/>, which the file must hold.</summary>
    public TileSection Section(string name) => file.Section(stream, name);

    /// <summary>The section of the level spanning the segments, which the file must keep (<see cref="TileIndexFile.Spans"/>).</summary>
    public TileSection Spanning() => file.Spanning(stream);

    /// <summary>
    /// The sections beneath the ranges <paramref name="left"/> holds, which the spanning section could not count whole: each
    /// section holding a timed reading in one of them, in the directory's order, with the ranges it is to count, and each
    /// checked against what the spanning section states of it.
    /// </summary>
    public List<(TileSection Section, List<TimeRange> Ranges)> Beneath(TileRemainders left)
    {
        ArgumentNullException.ThrowIfNull(left);
        TileSection spanning = Spanning();
        var meeting = new SortedDictionary<int, (TileExtent Extent, List<TimeRange> Ranges)>();
        foreach (TimeRange range in left.Ranges)
        {
            foreach (TileExtent extent in spanning.Meeting(range))
            {
                if (!meeting.TryGetValue(extent.Section, out (TileExtent Extent, List<TimeRange> Ranges) known))
                {
                    meeting.Add(extent.Section, known = (extent, []));
                }

                known.Ranges.Add(range);
            }
        }

        return [.. meeting.Values.Select(met => (file.Spanned(stream, met.Extent), met.Ranges))];
    }

    /// <summary>
    /// Counts the timed records of <paramref name="section"/>'s segment inside <paramref name="columns"/>' interval into
    /// them: a tile whole wherever its records fall in one column, its children where a boundary falls among them, and a
    /// finest tile's records where one does. False, counting nothing, for a segment whose readings go backwards and meet
    /// the interval, which keeps no tiles: its rows are read instead.
    /// </summary>
    public bool CountInto(TileSection section, TimelineColumns columns, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(columns);
        return CountInto(section, columns, columns.Interval, cancellationToken);
    }

    /// <summary>
    /// Counts the timed records of <paramref name="section"/>'s segment inside <paramref name="range"/>, a part of
    /// <paramref name="columns"/>' interval, into them, as <see cref="CountInto(TileSection, TimelineColumns, CancellationToken)"/>
    /// counts the whole: what the spanning section left to the sections beneath it.
    /// </summary>
    public bool CountInto(TileSection section, TimelineColumns columns, TimeRange range, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(columns);
        if (section.First is not { } first || section.Last is not { } last
            || last < range.StartTicks || first >= range.EndTicks)
        {
            return true;
        }

        if (!section.Ordered)
        {
            return false;
        }

        (lastColumn, within, remainders) = (-1, range, null);
        int top = section.Levels - 1;
        Range(section, top, 0, section.TileCount(top), columns, cancellationToken);
        return true;
    }

    /// <summary>
    /// Counts the spanning section's tiles inside <paramref name="columns"/>' interval into them: a tile whole wherever its
    /// records fall in one column, and its children where a boundary falls among them. Returns the ranges of its finest
    /// tiles a boundary falls among, which the sections beneath them count (<see cref="Beneath"/>).
    /// </summary>
    public TileRemainders SpanInto(TimelineColumns columns, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(columns);
        TileSection spanning = Spanning();
        var left = new TileRemainders();
        if (spanning.Last < columns.Interval.StartTicks || spanning.First >= columns.Interval.EndTicks)
        {
            return left;
        }

        (lastColumn, within, remainders) = (-1, columns.Interval, left);
        int top = spanning.Levels - 1;
        Range(spanning, top, 0, spanning.TileCount(top), columns, cancellationToken);
        remainders = null;
        return left;
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

        remainders = null;
        int top = section.Levels - 1;
        var brush = new Brush(interval, SessionTileIndex.AdmittedStrengths(policy), slotOfChannel, tally);
        RangeInterval(section, top, 0, section.TileCount(top), brush, cancellationToken);
        return true;
    }

    /// <summary>
    /// Counts the spanning section's tiles inside <paramref name="interval"/> into <paramref name="tally"/> as
    /// <see cref="CountInterval"/> counts a segment's: a tile whole from its tallies wherever its records all lie in the
    /// interval, and its children where one of the interval's ends falls among them or it keeps no tallies. Returns the
    /// ranges of its finest tiles it could not count whole, which the sections beneath them count (<see cref="Beneath"/>).
    /// </summary>
    public TileRemainders SpanInterval(
        TimeRange interval, EvidencePolicy policy, int[] slotOfChannel, IntervalTally tally, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slotOfChannel);
        ArgumentNullException.ThrowIfNull(tally);
        TileSection spanning = Spanning();
        var left = new TileRemainders();
        if (spanning.Last < interval.StartTicks || spanning.First >= interval.EndTicks)
        {
            return left;
        }

        remainders = left;
        int top = spanning.Levels - 1;
        var brush = new Brush(interval, SessionTileIndex.AdmittedStrengths(policy), slotOfChannel, tally);
        RangeInterval(spanning, top, 0, spanning.TileCount(top), brush, cancellationToken);
        remainders = null;
        return left;
    }

    /// <summary>
    /// Measures the transport-observed bytes of <paramref name="section"/>'s timed records inside
    /// <paramref name="interval"/> into <paramref name="measuring"/>, as a ranking measures a brush's rows
    /// (<see cref="SessionByteRanking"/>): each record's to the instance its owner binds to where the policy admits it, or
    /// apart, and each of a drawn channel's to the end its owner holds - a tile whole from its byte tallies wherever its
    /// records all lie in the interval, and a finest tile's records where one of the interval's ends falls among them. The
    /// caller has checked that the file's bindings are its own. False, measuring nothing, for a segment whose readings go
    /// backwards and meet the interval, or whose section keeps no byte tallies: its rows are read instead.
    /// </summary>
    public bool MeasureInterval(TileSection section, TimeRange interval, ByteMeasuring measuring, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(measuring);
        if (section.First is not { } first || section.Last is not { } last
            || last < interval.StartTicks || first >= interval.EndTicks)
        {
            return true;
        }

        if (!section.Ordered || !section.KeepsByteTallies)
        {
            return false;
        }

        remainders = null;
        int top = section.Levels - 1;
        RangeMeasure(section, top, 0, section.TileCount(top), interval, measuring, cancellationToken);
        return true;
    }

    /// <summary>
    /// Measures the spanning section's tiles inside <paramref name="interval"/> into <paramref name="measuring"/>, as
    /// <see cref="MeasureInterval"/> measures a segment's: a tile whole from its byte tallies wherever its records all lie
    /// in the interval, and its children where one of the interval's ends falls among them or it keeps none. Returns the
    /// ranges of its finest tiles it could not measure whole, which the sections beneath them measure (<see cref="Beneath"/>).
    /// </summary>
    public TileRemainders SpanMeasure(TimeRange interval, ByteMeasuring measuring, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(measuring);
        TileSection spanning = Spanning();
        var left = new TileRemainders();
        if (spanning.Last < interval.StartTicks || spanning.First >= interval.EndTicks)
        {
            return left;
        }

        remainders = left;
        int top = spanning.Levels - 1;
        RangeMeasure(spanning, top, 0, spanning.TileCount(top), interval, measuring, cancellationToken);
        remainders = null;
        return left;
    }

    /// <summary>
    /// Counts the records of <paramref name="section"/>'s segment that an owner focus holds into its columns,
    /// <paramref name="focused"/>, each member's into its lane of <paramref name="lanes"/>, and each by its direction into
    /// its row of <paramref name="directions"/>, as a focus counts its rows (<see cref="FocusRows"/>): every timed record
    /// inside the columns' interval whose owner binds to a member, where the policy admits the binding, of
    /// <paramref name="mechanism"/> and in <paramref name="direction"/> where it names one. A tile counts whole from its
    /// tallies wherever its records fall in one column of the focus and one of the lanes', and a finest tile's records
    /// are read where a boundary of either falls among them. The caller has checked that the file's bindings are its own.
    /// False, counting nothing, for a segment whose readings go backwards and meet the interval: its rows are read instead.
    /// </summary>
    public bool CountGroup(
        TileSection section,
        TimelineColumns focused,
        TimelineColumns[]? lanes,
        TimelineColumns[]? directions,
        bool[] members,
        int[]? laneOf,
        Mechanism? mechanism,
        Direction? direction,
        EvidencePolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(focused);
        return CountGroup(section, focused.Interval, focused, lanes, directions, members, laneOf, mechanism, direction, policy,
            cancellationToken);
    }

    /// <summary>
    /// Counts the records of <paramref name="section"/>'s segment inside <paramref name="range"/>, a part of
    /// <paramref name="focused"/>'s interval, that an owner focus holds, as the overload without a range counts the whole:
    /// what the spanning section left to the sections beneath it.
    /// </summary>
    public bool CountGroup(
        TileSection section,
        TimeRange range,
        TimelineColumns focused,
        TimelineColumns[]? lanes,
        TimelineColumns[]? directions,
        bool[] members,
        int[]? laneOf,
        Mechanism? mechanism,
        Direction? direction,
        EvidencePolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(focused);
        ArgumentNullException.ThrowIfNull(members);
        if (section.First is not { } first || section.Last is not { } last
            || last < range.StartTicks || first >= range.EndTicks)
        {
            return true;
        }

        if (!section.Ordered)
        {
            return false;
        }

        (lastColumn, lastLaneColumn, within, remainders) = (-1, -1, range, null);
        int top = section.Levels - 1;
        RangeGroup(section, top, 0, section.TileCount(top),
            GroupOf(focused, lanes, directions, members, laneOf, mechanism, direction, policy), cancellationToken);
        return true;
    }

    /// <summary>
    /// Counts the spanning section's tiles inside <paramref name="focused"/>'s interval into an owner focus, as
    /// <see cref="CountGroup(TileSection, TimelineColumns, TimelineColumns[], TimelineColumns[], bool[], int[], Mechanism?, Direction?, EvidencePolicy, CancellationToken)"/>
    /// counts a segment's: a tile whole from its owner tallies wherever its records fall in one column of the focus and one
    /// of the lanes', and its children where a boundary falls among them or it keeps none. Returns the ranges of its finest
    /// tiles it could not count whole, which the sections beneath them count (<see cref="Beneath"/>).
    /// </summary>
    public TileRemainders SpanGroup(
        TimelineColumns focused,
        TimelineColumns[]? lanes,
        TimelineColumns[]? directions,
        bool[] members,
        int[]? laneOf,
        Mechanism? mechanism,
        Direction? direction,
        EvidencePolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(focused);
        ArgumentNullException.ThrowIfNull(members);
        TileSection spanning = Spanning();
        var left = new TileRemainders();
        if (spanning.Last < focused.Interval.StartTicks || spanning.First >= focused.Interval.EndTicks)
        {
            return left;
        }

        (lastColumn, lastLaneColumn, within, remainders) = (-1, -1, focused.Interval, left);
        int top = spanning.Levels - 1;
        RangeGroup(spanning, top, 0, spanning.TileCount(top),
            GroupOf(focused, lanes, directions, members, laneOf, mechanism, direction, policy), cancellationToken);
        remainders = null;
        return left;
    }

    /// <summary>
    /// Counts the records of <paramref name="section"/>'s segment that a channel focus holds into its columns,
    /// <paramref name="focused"/>, and each by the end of the channel it was made at into that end's lanes of
    /// <paramref name="ends"/>, as a focus counts its rows (<see cref="FocusRows"/>): every timed record inside the
    /// columns' interval bound to channel <paramref name="channel"/>, of <paramref name="mechanism"/>, in
    /// <paramref name="direction"/> and at <paramref name="end"/> where it names them. A tile counts whole from its tallies
    /// wherever its records fall in one column, and a finest tile's records are read where a boundary falls among them.
    /// The caller has checked that the file's bindings are its own. False, counting nothing, for a segment whose readings
    /// go backwards and meet the interval: its rows are read instead.
    /// </summary>
    public bool CountChannel(
        TileSection section,
        TimelineColumns focused,
        SessionTimelineQuery.ChannelEndColumns[]? ends,
        int channel,
        Mechanism? mechanism,
        Direction? direction,
        int? end,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(focused);
        return CountChannel(section, focused.Interval, focused, ends, channel, mechanism, direction, end, cancellationToken);
    }

    /// <summary>
    /// Counts the records of <paramref name="section"/>'s segment inside <paramref name="range"/>, a part of
    /// <paramref name="focused"/>'s interval, that a channel focus holds, as the overload without a range counts the whole:
    /// what the spanning section left to the sections beneath it.
    /// </summary>
    public bool CountChannel(
        TileSection section,
        TimeRange range,
        TimelineColumns focused,
        SessionTimelineQuery.ChannelEndColumns[]? ends,
        int channel,
        Mechanism? mechanism,
        Direction? direction,
        int? end,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(focused);
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        if (section.First is not { } first || section.Last is not { } last
            || last < range.StartTicks || first >= range.EndTicks)
        {
            return true;
        }

        if (!section.Ordered)
        {
            return false;
        }

        (lastColumn, within, remainders) = (-1, range, null);
        int top = section.Levels - 1;
        RangeChannel(section, top, 0, section.TileCount(top), new(focused, ends, channel, mechanism, direction, end), cancellationToken);
        return true;
    }

    /// <summary>
    /// Counts the spanning section's tiles inside <paramref name="focused"/>'s interval into a channel focus, as
    /// <see cref="CountChannel(TileSection, TimelineColumns, SessionTimelineQuery.ChannelEndColumns[], int, Mechanism?, Direction?, int?, CancellationToken)"/>
    /// counts a segment's: a tile whole from its channel tallies wherever its records fall in one column, and its children
    /// where a boundary falls among them or it keeps none. Returns the ranges of its finest tiles it could not count whole,
    /// which the sections beneath them count (<see cref="Beneath"/>).
    /// </summary>
    public TileRemainders SpanChannel(
        TimelineColumns focused,
        SessionTimelineQuery.ChannelEndColumns[]? ends,
        int channel,
        Mechanism? mechanism,
        Direction? direction,
        int? end,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(focused);
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        TileSection spanning = Spanning();
        var left = new TileRemainders();
        if (spanning.Last < focused.Interval.StartTicks || spanning.First >= focused.Interval.EndTicks)
        {
            return left;
        }

        (lastColumn, within, remainders) = (-1, focused.Interval, left);
        int top = spanning.Levels - 1;
        RangeChannel(spanning, top, 0, spanning.TileCount(top), new(focused, ends, channel, mechanism, direction, end), cancellationToken);
        remainders = null;
        return left;
    }

    /// <summary>
    /// Adds the transport-observed bytes of <paramref name="section"/>'s timed records inside <paramref name="range"/>, a
    /// part of <paramref name="lanes"/>' interval, to their columns, each record by its mechanism's lane: a tile's byte sums
    /// whole wherever its records fall in one column, its children where a boundary falls among them, and a finest tile's
    /// records where one does. False, adding nothing, for a segment whose readings go backwards and meet the range, which
    /// keeps no tiles, or whose section keeps no bytes: its rows are read instead.
    /// </summary>
    public bool SumInto(TileSection section, ByteLanes lanes, TimeRange range, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(lanes);
        if (section.First is not { } first || section.Last is not { } last
            || last < range.StartTicks || first >= range.EndTicks)
        {
            return true;
        }

        if (!section.Ordered || !section.KeepsBytes)
        {
            return false;
        }

        (lastColumn, within, remainders) = (-1, range, null);
        int top = section.Levels - 1;
        RangeSums(section, top, 0, section.TileCount(top), lanes, lanes.LanesOf(section), cancellationToken);
        return true;
    }

    /// <summary>
    /// Adds the bytes of the spanning section's tiles inside <paramref name="lanes"/>' interval to their columns, as
    /// <see cref="SumInto"/> adds a segment's: a tile's byte sums whole wherever its records fall in one column, and its
    /// children where a boundary falls among them. Returns the ranges of its finest tiles a boundary falls among, which the
    /// sections beneath them add (<see cref="Beneath"/>). The caller has checked that the section keeps bytes.
    /// </summary>
    public TileRemainders SpanSums(ByteLanes lanes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lanes);
        TileSection spanning = Spanning();
        var left = new TileRemainders();
        TimeRange interval = lanes.Columns.Interval;
        if (spanning.Last < interval.StartTicks || spanning.First >= interval.EndTicks)
        {
            return left;
        }

        (lastColumn, within, remainders) = (-1, interval, left);
        int top = spanning.Levels - 1;
        RangeSums(spanning, top, 0, spanning.TileCount(top), lanes, lanes.LanesOf(spanning), cancellationToken);
        remainders = null;
        return left;
    }

    /// <summary>What an owner focus counts and into what, its lanes sharing its columns where they are as many.</summary>
    private static Group GroupOf(
        TimelineColumns focused,
        TimelineColumns[]? lanes,
        TimelineColumns[]? directions,
        bool[] members,
        int[]? laneOf,
        Mechanism? mechanism,
        Direction? direction,
        EvidencePolicy policy) =>
        new(focused, lanes, lanes is null || lanes[0].Counts.Count == focused.Counts.Count, directions, members, laneOf, mechanism,
            direction, SessionTileIndex.AdmittedStrengths(policy));

    /// <summary>
    /// Counts the tiles of one level from <paramref name="from"/> to before <paramref name="end"/> into a group's focus:
    /// each whole from its tallies that falls in one column of the focus and of its lanes, the children of each that does
    /// not or keeps no tallies, and the records of a finest one - or, of the spanning section's, its range, left to the
    /// sections beneath it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void RangeGroup(TileSection section, int level, int from, int end, Group group, CancellationToken cancellationToken)
    {
        TimelineColumns focused = group.Focused;
        TimeRange interval = focused.Interval;
        TimeRange range = within;
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
            if (low >= range.EndTicks)
            {
                return;
            }

            if (high < range.StartTicks)
            {
                continue;
            }

            // A tile lying in one column of the view's and one of the lanes' counts there whole: from its tallies, or where
            // it keeps none from its children, or its records, without looking up each one's column.
            int column = -1;
            int laneColumn = -1;
            if (low >= range.StartTicks && high < range.EndTicks)
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
                        remainders?.Counted();
                        continue;
                    }
                }
            }

            if (level > 0)
            {
                RangeGroup(section, level - 1, block.Links[tile], block.Links[tile] + block.Children[tile], group, cancellationToken);
                continue;
            }

            if (section.Spanning)
            {
                remainders!.Add(Math.Max(range.StartTicks, low), Math.Min(range.EndTicks, high + 1));
                continue;
            }

            var records = new RecordCursor(this, section, block, position);
            while (records.Next(out long reading, out int slot, out int direction, out _, out uint owner, out _))
            {
                if (owner == 0 || !group.Admitted[owner & 7] || !group.Members[(owner >> 3) - 1]
                    || (column < 0 && !range.Contains(reading)))
                {
                    continue;
                }

                Mechanism mechanism = section.Mechanisms[slot];
                if ((group.Mechanism is { } only && mechanism != only) || (group.Direction is { } way && direction != (int)way))
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

                group.Directions?[DirectionRow(direction)].Add(at, mechanism);
            }
        }
    }

    /// <summary>
    /// Counts the tiles of one level from <paramref name="from"/> to before <paramref name="end"/> into a channel's focus:
    /// each whole from its tallies that falls in one column, the children of each that does not or keeps no tallies, and
    /// the records of a finest one - or, of the spanning section's, its range, left to the sections beneath it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void RangeChannel(TileSection section, int level, int from, int end, Channel focus, CancellationToken cancellationToken)
    {
        TimelineColumns focused = focus.Focused;
        TimeRange interval = focused.Interval;
        TimeRange range = within;
        uint key = (uint)focus.Number + 1;
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
            if (low >= range.EndTicks)
            {
                return;
            }

            if (high < range.StartTicks)
            {
                continue;
            }

            // A tile lying in one column counts there whole: from its tallies, or where it keeps none from its children, or
            // its records, without looking up each one's column.
            int column = -1;
            if (low >= range.StartTicks && high < range.EndTicks)
            {
                if (lastColumn < 0 || low < lastStart || low >= lastEnd)
                {
                    lastColumn = focused.ColumnOf(low)!.Value;
                    TimeRange bounds = TimelineColumns.IntervalOf(interval, focused.Counts.Count, lastColumn);
                    (lastStart, lastEnd) = (bounds.StartTicks, bounds.EndTicks);
                }

                if (high < lastEnd)
                {
                    column = lastColumn;
                    if (AddChannelTallies(section, level, block, position, focus, column))
                    {
                        remainders?.Counted();
                        continue;
                    }
                }
            }

            if (level > 0)
            {
                RangeChannel(section, level - 1, block.Links[tile], block.Links[tile] + block.Children[tile], focus, cancellationToken);
                continue;
            }

            if (section.Spanning)
            {
                remainders!.Add(Math.Max(range.StartTicks, low), Math.Min(range.EndTicks, high + 1));
                continue;
            }

            var records = new RecordCursor(this, section, block, position);
            while (records.Next(out long reading, out int slot, out int direction, out int side, out _, out uint channel))
            {
                if (channel == key && (column >= 0 || range.Contains(reading)))
                {
                    CountChannelRecords(focus, column >= 0 ? column : focused.ColumnOf(reading)!.Value, section.Mechanisms[slot],
                        direction, side, 1);
                }
            }
        }
    }

    /// <summary>
    /// Counts <paramref name="count"/> records of a channel's focus with one mechanism, direction and end in one column, as
    /// reading their rows counts them: none outside the mechanism, direction and end the focus names; each in the focus,
    /// and at its end of the channel, in its lanes. A record of a channel whose ends are counted that names no end of it
    /// refuses the index, as reading its row refuses it.
    /// </summary>
    private static void CountChannelRecords(Channel focus, int column, Mechanism mechanism, int direction, int end, int count)
    {
        if ((focus.Mechanism is { } only && mechanism != only) || (focus.Direction is { } way && direction != (int)way)
            || (focus.End is { } side && end != side))
        {
            return;
        }

        focus.Focused.Add(column, mechanism, count);
        if (focus.Ends is { } ends)
        {
            if (end is not (0 or 1))
            {
                throw SessionTileIndex.Invalid("a record of the focused channel names no end of it.");
            }

            ends[end].Add(column, mechanism, DirectionOf(direction), count);
        }
    }

    /// <summary>
    /// Counts the tiles of one level from <paramref name="from"/> to before <paramref name="end"/>, in time order: each
    /// whole that falls in one column, the children of each that does not, and the records of a finest one that does not -
    /// or, of the spanning section's, its range, left to the sections beneath it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Range(
        TileSection section, int level, int from, int end, TimelineColumns columns, CancellationToken cancellationToken)
    {
        TimeRange interval = columns.Interval;
        TimeRange range = within;
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
            if (low >= range.EndTicks)
            {
                return;
            }

            if (high < range.StartTicks)
            {
                continue;
            }

            if (low >= range.StartTicks && high < range.EndTicks)
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

                    remainders?.Counted();
                    continue;
                }
            }

            if (level > 0)
            {
                Range(section, level - 1, block.Links[tile], block.Links[tile] + block.Children[tile], columns, cancellationToken);
            }
            else if (section.Spanning)
            {
                remainders!.Add(Math.Max(range.StartTicks, low), Math.Min(range.EndTicks, high + 1));
            }
            else
            {
                var records = new RecordCursor(this, section, block, position);
                while (records.Next(out long reading, out int slot, out _, out _, out _, out _))
                {
                    if (range.Contains(reading))
                    {
                        columns.Add(columns.ColumnOf(reading)!.Value, section.Mechanisms[slot]);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Adds the tiles of one level from <paramref name="from"/> to before <paramref name="end"/> to the lanes' columns:
    /// each one's byte sums that falls in one column, the children of each a boundary falls among, and the records of a
    /// finest one a boundary falls among - or, of the spanning section's, its range, left to the sections beneath it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void RangeSums(
        TileSection section, int level, int from, int end, ByteLanes lanes, int[] laneOfSlot, CancellationToken cancellationToken)
    {
        TimelineColumns columns = lanes.Columns;
        TimeRange interval = columns.Interval;
        TimeRange range = within;
        int laneCount = lanes.Count;
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
            if (low >= range.EndTicks)
            {
                return;
            }

            if (high < range.StartTicks)
            {
                continue;
            }

            if (low >= range.StartTicks && high < range.EndTicks)
            {
                if (lastColumn < 0 || low < lastStart || low >= lastEnd)
                {
                    lastColumn = columns.ColumnOf(low)!.Value;
                    TimeRange bounds = TimelineColumns.IntervalOf(interval, columns.Counts.Count, lastColumn);
                    (lastStart, lastEnd) = (bounds.StartTicks, bounds.EndTicks);
                }

                if (high < lastEnd)
                {
                    ReadSums(section, level, block, position);
                    for (int entry = 0; entry < sumEntries; entry++)
                    {
                        if (laneOfSlot[sumSlots[entry]] is >= 0 and int lane)
                        {
                            lanes.Tally.Add((lastColumn * laneCount) + lane, sumSides[entry], sumTotals[entry], sumMeasured[entry],
                                sumUnmeasured[entry]);
                        }
                    }

                    remainders?.Counted();
                    continue;
                }
            }

            if (level > 0)
            {
                RangeSums(section, level - 1, block.Links[tile], block.Links[tile] + block.Children[tile], lanes, laneOfSlot,
                    cancellationToken);
            }
            else if (section.Spanning)
            {
                remainders!.Add(Math.Max(range.StartTicks, low), Math.Min(range.EndTicks, high + 1));
            }
            else
            {
                var records = new RecordCursor(this, section, block, position);
                while (records.Next(out long reading, out int slot, out _, out _, out _, out _))
                {
                    if (records.Side > 0 && range.Contains(reading) && laneOfSlot[slot] is >= 0 and int lane)
                    {
                        lanes.Tally.Add((columns.ColumnOf(reading)!.Value * laneCount) + lane, records.Side, records.Value);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Counts the tiles of one level from <paramref name="from"/> to before <paramref name="end"/> into a brush's tally:
    /// each whole from its tallies that lies in the interval, the children of each that one of its ends falls among, and
    /// the records of a finest one that one does - or, of the spanning section's, its range, left to the sections beneath
    /// it.
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
                remainders?.Counted();
                continue;
            }

            if (level > 0)
            {
                RangeInterval(section, level - 1, block.Links[tile], block.Links[tile] + block.Children[tile], brush,
                    cancellationToken);
                continue;
            }

            if (section.Spanning)
            {
                remainders!.Add(Math.Max(interval.StartTicks, low), Math.Min(interval.EndTicks, high + 1));
                continue;
            }

            var records = new RecordCursor(this, section, block, position);
            while (records.Next(out long reading, out int slot, out _, out _, out uint owner, out uint channel))
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
    /// Measures the tiles of one level from <paramref name="from"/> to before <paramref name="end"/> into a brush's bytes:
    /// each whole from its byte tallies that lies in the interval, the children of each that one of its ends falls among,
    /// and the records of a finest one that one does - or, of the spanning section's, its range, left to the sections
    /// beneath it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void RangeMeasure(
        TileSection section, int level, int from, int end, TimeRange interval, ByteMeasuring measuring, CancellationToken cancellationToken)
    {
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

            // A tile its interval holds whole is measured from its byte tallies, or, where it keeps none, as its children or
            // its records are.
            if (low >= interval.StartTicks && high < interval.EndTicks && AddByteTallies(section, level, block, position, measuring))
            {
                remainders?.Counted();
                continue;
            }

            if (level > 0)
            {
                RangeMeasure(section, level - 1, block.Links[tile], block.Links[tile] + block.Children[tile], interval, measuring,
                    cancellationToken);
                continue;
            }

            if (section.Spanning)
            {
                remainders!.Add(Math.Max(interval.StartTicks, low), Math.Min(interval.EndTicks, high + 1));
                continue;
            }

            var records = new RecordCursor(this, section, block, position);
            while (records.Next(out long reading, out _, out _, out _, out uint owner, out uint channel))
            {
                if (records.Side > 0 && interval.Contains(reading))
                {
                    measuring.Add(owner, channel, records.Side, records.Value);
                }
            }
        }
    }

    /// <summary>
    /// Adds one tile's records' bytes to a brush's whole, from its byte tallies. False, adding nothing, for a tile that keeps
    /// none, whose children or records are measured instead.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool AddByteTallies(TileSection section, int level, TileBlock block, int position, ByteMeasuring measuring)
    {
        if (!ReadByteTallies(section, level, block, position, RecordsIn(section, block, position)))
        {
            TilesUntallied++;
            return false;
        }

        TilesOfTalliesRead++;
        for (int entry = 0; entry < byteEntries; entry++)
        {
            measuring.Add(byteOwners[entry], byteChannels[entry], byteSides[entry], byteTotals[entry], byteMeasured[entry],
                byteUnmeasured[entry]);
        }

        return true;
    }

    /// <summary>
    /// Adds one tile's records to a brush's tally whole: all of them observed, each bound owner's the policy admits to its
    /// instance by mechanism, and each drawn channel's to it. False, adding nothing, for a tile that keeps no owner or no
    /// channel tallies, whose children or records are counted instead.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool AddTallies(TileSection section, int level, TileBlock block, int position, Brush brush)
    {
        long records = RecordsIn(section, block, position);
        if (!ReadOwnerTallies(section, level, block, position, records) || !ReadChannelTallies(section, level, block, position, records))
        {
            TilesUntallied++;
            return false;
        }

        TilesOfTalliesRead++;
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
    /// focus and to the member's lane. False, adding nothing, for a tile that keeps no owner tallies.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool AddGroupTallies(TileSection section, int level, TileBlock block, int position, Group group, int column, int laneColumn)
    {
        if (!ReadOwnerTallies(section, level, block, position, RecordsIn(section, block, position)))
        {
            TilesUntallied++;
            return false;
        }

        TilesOfTalliesRead++;
        for (int entry = 0; entry < ownerEntries; entry++)
        {
            uint owner = ownerKeys[entry];
            int instance = (int)(owner >> 3) - 1;
            Mechanism mechanism = section.Mechanisms[ownerSlots[entry]];
            if (!group.Admitted[owner & 7] || !group.Members[instance] || (group.Mechanism is { } only && mechanism != only)
                || (group.Direction is { } way && ownerDirections[entry] != (int)way))
            {
                continue;
            }

            int count = (int)ownerCounts[entry];
            group.Focused.Add(column, mechanism, count);
            if (group.LaneOf?[instance] is >= 0 and int lane)
            {
                group.Lanes![lane].Add(laneColumn, mechanism, count);
            }

            group.Directions?[DirectionRow(ownerDirections[entry])].Add(column, mechanism, count);
        }

        return true;
    }

    /// <summary>
    /// Adds one tile's records to a channel's focus whole, in <paramref name="column"/>: each entry of the focused channel,
    /// by its mechanism, direction and end. False, adding nothing, for a tile that keeps no channel tallies.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool AddChannelTallies(TileSection section, int level, TileBlock block, int position, Channel focus, int column)
    {
        if (!ReadChannelTallies(section, level, block, position, RecordsIn(section, block, position)))
        {
            TilesUntallied++;
            return false;
        }

        TilesOfTalliesRead++;
        for (int entry = 0; entry < channelEntries; entry++)
        {
            if (channelNumbers[entry] == focus.Number)
            {
                int kind = channelKinds[entry];
                CountChannelRecords(focus, column, section.Mechanisms[channelSlots[entry]], kind & 7, (kind >> 3) - 1,
                    (int)channelCounts[entry]);
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
    /// Reads one tile's byte tallies into the byte buffers, checked against the tile as they are read: in order of owner key,
    /// channel key and side, each key none or within the derivation's instances and channels; no entry measuring no record
    /// or more than the tile's <paramref name="records"/>, nor bytes where no record measured them, nor more records in all
    /// than the tile holds; nothing after the last. False, reading no entry, for a tile that keeps none. Its block's byte
    /// tallies are checked against their checksum once.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool ReadByteTallies(TileSection section, int level, TileBlock block, int position, long records)
    {
        int tile = position % SessionTileIndex.TilesPerBlock;
        int start = block.ByteLinks[0];
        byte[] bytes = TalliesOf(byteBlocks, section, level, position / SessionTileIndex.TilesPerBlock, section.ByteTalliesAt,
            start, block.ByteTalliesEnd, "byte tallies");
        var at = new VarintReader(bytes, block.ByteLinks[tile] - start,
            (tile + 1 < block.ByteLinks.Length ? block.ByteLinks[tile + 1] : block.ByteTalliesEnd) - start);
        ulong entries = at.Next();
        if (entries == 0)
        {
            return at.AtEnd ? false : throw Bad(section, level, position, "byte tallies");
        }

        ulong lastOwner = ((ulong)file.Derivation.Instances << 3) | 7;
        ulong lastChannel = (ulong)file.Derivation.Channels;

        // Each entry's owner key over the one before's; its channel key over the one before's under the same owner key, and
        // otherwise itself; and its side.
        byteEntries = 0;
        long counted = 0;
        ulong owner = 0;
        ulong channel = 0;
        ulong previous = 0;
        for (ulong entry = 0; entry < entries - 1; entry++)
        {
            ulong increase = at.Next();
            ulong placed = at.Next();
            ulong measured = at.Next();
            ulong unmeasured = at.Next();
            ulong total = at.Next();
            if (increase > lastOwner || (placed >> 2) > lastChannel)
            {
                throw Bad(section, level, position, "byte tallies");
            }

            owner += increase;
            channel = increase == 0 ? channel + (placed >> 2) : placed >> 2;
            ulong key = (owner << 32) | (channel << 3) | ((placed & 3) + 1);
            if (owner is > 0 and < 8 || owner > lastOwner || channel > lastChannel || (entry > 0 && key <= previous)
                || (measured == 0 && unmeasured == 0) || measured > (ulong)records || unmeasured > (ulong)records
                || (measured == 0 && total != 0) || total > long.MaxValue)
            {
                throw Bad(section, level, position, "byte tallies");
            }

            counted += (long)(measured + unmeasured);
            if (counted > records)
            {
                throw Bad(section, level, position, "byte tallies");
            }

            previous = key;
            if (byteEntries == byteOwners.Length)
            {
                Array.Resize(ref byteOwners, byteEntries * 2);
                Array.Resize(ref byteChannels, byteEntries * 2);
                Array.Resize(ref byteSides, byteEntries * 2);
                Array.Resize(ref byteMeasured, byteEntries * 2);
                Array.Resize(ref byteUnmeasured, byteEntries * 2);
                Array.Resize(ref byteTotals, byteEntries * 2);
            }

            (byteOwners[byteEntries], byteChannels[byteEntries], byteSides[byteEntries]) =
                ((uint)owner, (uint)channel, (byte)((placed & 3) + 1));
            (byteMeasured[byteEntries], byteUnmeasured[byteEntries], byteTotals[byteEntries]) =
                ((long)measured, (long)unmeasured, (long)total);
            byteEntries++;
        }

        return at.AtEnd ? true : throw Bad(section, level, position, "byte tallies");
    }

    /// <summary>
    /// Reads one tile's owner tallies into the owner buffers, checked against the tile as they are read: in order of key,
    /// mechanism and direction, within the derivation's instances and the section's mechanisms; no count of zero or above
    /// the tile's <paramref name="records"/>, nor a sum above them; nothing after the last. False, reading no entry, for a
    /// tile that keeps none. Its block's owner tallies are checked against their checksum once.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool ReadOwnerTallies(TileSection section, int level, TileBlock block, int position, long records)
    {
        int tile = position % SessionTileIndex.TilesPerBlock;
        int start = block.OwnerLinks[0];
        byte[] bytes = TalliesOf(ownerBlocks, section, level, position / SessionTileIndex.TilesPerBlock, section.OwnerTalliesAt,
            start, block.OwnerTalliesEnd, "owner tallies");
        var at = new VarintReader(bytes, block.OwnerLinks[tile] - start,
            (tile + 1 < block.OwnerLinks.Length ? block.OwnerLinks[tile + 1] : block.OwnerTalliesEnd) - start);
        ulong owners = at.Next();
        if (owners == 0)
        {
            return at.AtEnd ? false : throw Bad(section, level, position, "owner tallies");
        }

        int mechanisms = section.Mechanisms.Length;
        ulong lastOwner = ((ulong)file.Derivation.Instances << 3) | 7;

        // Each owner's key over the one before, or its mechanism and direction after the one before's under the same key.
        ownerEntries = 0;
        long owned = 0;
        ulong owner = 0;
        ulong previous = 0;
        ulong kinds = (ulong)mechanisms << 3;
        for (ulong entry = 0; entry < owners - 1; entry++)
        {
            ulong increase = at.Next();
            ulong kind = at.Next();
            ulong count = at.Next();
            if (increase > lastOwner || (entry > 0 && increase == 0 && kind <= previous))
            {
                throw Bad(section, level, position, "owner tallies");
            }

            owner += increase;
            if (owner < 8 || owner > lastOwner || kind >= kinds || count == 0 || count > (ulong)records)
            {
                throw Bad(section, level, position, "owner tallies");
            }

            owned += (long)count;
            previous = kind;
            if (ownerEntries == ownerKeys.Length)
            {
                Array.Resize(ref ownerKeys, ownerEntries * 2);
                Array.Resize(ref ownerSlots, ownerEntries * 2);
                Array.Resize(ref ownerDirections, ownerEntries * 2);
                Array.Resize(ref ownerCounts, ownerEntries * 2);
            }

            (ownerKeys[ownerEntries], ownerSlots[ownerEntries], ownerDirections[ownerEntries], ownerCounts[ownerEntries]) =
                ((uint)owner, (byte)(kind >> 3), (byte)(kind & 7), (long)count);
            ownerEntries++;
        }

        return at.AtEnd && owned <= records ? true : throw Bad(section, level, position, "owner tallies");
    }

    /// <summary>
    /// Reads one tile's channel tallies into the channel buffers, checked against the tile as they are read: in order of
    /// number, mechanism and kind, within the derivation's channels and the section's mechanisms, each kind one a record can
    /// have; no count of zero or above the tile's <paramref name="records"/>, nor a sum above them; nothing after the last.
    /// False, reading no entry, for a tile that keeps none. Its block's channel tallies are checked against their checksum
    /// once.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool ReadChannelTallies(TileSection section, int level, TileBlock block, int position, long records)
    {
        int tile = position % SessionTileIndex.TilesPerBlock;
        int start = block.ChannelLinks[0];
        byte[] bytes = TalliesOf(channelBlocks, section, level, position / SessionTileIndex.TilesPerBlock, section.ChannelTalliesAt,
            start, block.ChannelTalliesEnd, "channel tallies");
        var at = new VarintReader(bytes, block.ChannelLinks[tile] - start,
            (tile + 1 < block.ChannelLinks.Length ? block.ChannelLinks[tile + 1] : block.ChannelTalliesEnd) - start);
        ulong numbered = at.Next();
        if (numbered == 0)
        {
            return at.AtEnd ? false : throw Bad(section, level, position, "channel tallies");
        }

        int mechanisms = section.Mechanisms.Length;
        ulong channels = (ulong)file.Derivation.Channels;

        // Each channel's number over the one before, or its mechanism and kind after the one before's under the same number.
        channelEntries = 0;
        long bound = 0;
        ulong number = 0;
        ulong previous = 0;
        ulong kinds = (ulong)mechanisms << 5;
        for (ulong entry = 0; entry < numbered - 1; entry++)
        {
            ulong increase = at.Next();
            ulong kind = at.Next();
            ulong count = at.Next();
            if (increase >= channels || (entry > 0 && increase == 0 && kind <= previous))
            {
                throw Bad(section, level, position, "channel tallies");
            }

            number += increase;
            if (number >= channels || kind >= kinds || !IsKind((int)kind & 31) || count == 0 || count > (ulong)records)
            {
                throw Bad(section, level, position, "channel tallies");
            }

            bound += (long)count;
            previous = kind;
            if (channelEntries == channelNumbers.Length)
            {
                Array.Resize(ref channelNumbers, channelEntries * 2);
                Array.Resize(ref channelSlots, channelEntries * 2);
                Array.Resize(ref channelKinds, channelEntries * 2);
                Array.Resize(ref channelCounts, channelEntries * 2);
            }

            (channelNumbers[channelEntries], channelSlots[channelEntries], channelKinds[channelEntries], channelCounts[channelEntries]) =
                ((int)number, (byte)(kind >> 5), (byte)(kind & 31), (long)count);
            channelEntries++;
        }

        return at.AtEnd && bound <= records ? true : throw Bad(section, level, position, "channel tallies");
    }

    /// <summary>
    /// Reads one tile's byte sums into the sum buffers, checked against the tile as they are read: in order of mechanism and
    /// side, within the section's mechanisms and the sides §23 defines; each with a record that measured or declared it, no
    /// more such records of a mechanism than the tile holds of it, and no bytes where none measured them; nothing after the
    /// last. Its block's byte sums are checked against their checksum once.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ReadSums(TileSection section, int level, TileBlock block, int position)
    {
        int tile = position % SessionTileIndex.TilesPerBlock;
        int start = block.SumLinks[0];
        byte[] bytes = TalliesOf(sumBlocks, section, level, position / SessionTileIndex.TilesPerBlock, section.SumsAt,
            start, block.SumsEnd, "byte sums");
        var at = new VarintReader(bytes, block.SumLinks[tile] - start,
            (tile + 1 < block.SumLinks.Length ? block.SumLinks[tile + 1] : block.SumsEnd) - start);
        int mechanisms = section.Mechanisms.Length;
        ulong entries = at.Next();
        if (entries > (ulong)mechanisms * 4)
        {
            throw Bad(section, level, position, "byte sums");
        }

        // Each entry's mechanism and side after the one before's, and no mechanism with more contributions than records.
        sumEntries = 0;
        long previous = -1;
        int slotOfRun = -1;
        long ofSlot = 0;
        for (ulong entry = 0; entry < entries; entry++)
        {
            ulong key = at.Next();
            ulong measured = at.Next();
            ulong unmeasured = at.Next();
            ulong total = at.Next();
            if (key >= (ulong)mechanisms * 4 || (long)key <= previous)
            {
                throw Bad(section, level, position, "byte sums");
            }

            int slot = (int)(key >> 2);
            long records = block.Counts[(tile * mechanisms) + slot];
            if (measured > (ulong)records || unmeasured > (ulong)records || measured + unmeasured == 0
                || total > long.MaxValue || (measured == 0 && total != 0))
            {
                throw Bad(section, level, position, "byte sums");
            }

            ofSlot = (slot == slotOfRun ? ofSlot : 0) + (long)(measured + unmeasured);
            if (ofSlot > records)
            {
                throw Bad(section, level, position, "byte sums");
            }

            (previous, slotOfRun) = ((long)key, slot);
            if (sumEntries == sumSlots.Length)
            {
                Array.Resize(ref sumSlots, sumEntries * 2);
                Array.Resize(ref sumSides, sumEntries * 2);
                Array.Resize(ref sumTotals, sumEntries * 2);
                Array.Resize(ref sumMeasured, sumEntries * 2);
                Array.Resize(ref sumUnmeasured, sumEntries * 2);
            }

            (sumSlots[sumEntries], sumSides[sumEntries], sumTotals[sumEntries], sumMeasured[sumEntries], sumUnmeasured[sumEntries]) =
                ((byte)slot, (byte)((key & 3) + 1), (long)total, (long)measured, (long)unmeasured);
            sumEntries++;
        }

        if (!at.AtEnd)
        {
            throw Bad(section, level, position, "byte sums");
        }
    }

    /// <summary>The refusal of one tile's owner or channel tallies, <paramref name="what"/>, that are not the tile's.</summary>
    private static InvalidDataException Bad(TileSection section, int level, int position, string what) =>
        SessionTileIndex.Invalid(string.Create(CultureInfo.InvariantCulture,
            $"the {what} of tile {position} of level {level} of {section.Entry.Describe} are not the tile's."));

    /// <summary>
    /// The bytes of the owner or the channel tallies, <paramref name="what"/>, of block <paramref name="index"/> of a level:
    /// from <paramref name="start"/> to <paramref name="end"/> within the run beginning at <paramref name="at"/> in the
    /// file, read and checked once.
    /// </summary>
    private byte[] TalliesOf(TallyBlocks blocks, TileSection section, int level, int index, long at, int start, int end, string what)
    {
        if (!ReferenceEquals(blocks.Section, section))
        {
            blocks.Section = section;
            Array.Clear(blocks.Bytes);
        }

        if (blocks.Bytes[level] is { } known && blocks.Indexes[level] == index)
        {
            return known;
        }

        byte[] read = SessionTileIndex.Read(stream, at + start, end - start + 4);
        if (!SessionTileIndex.Matches(read))
        {
            throw SessionTileIndex.Mismatch(string.Create(CultureInfo.InvariantCulture,
                $"the {what} of block {index} of level {level} of {section.Entry.Describe}"));
        }

        (blocks.Bytes[level], blocks.Indexes[level]) = (read, index);
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
                $"the records of tile {position} of {section.Entry.Describe}"));
        }

        return end - start;
    }

    /// <summary>
    /// The row of a process's direction rows a record's direction code is counted in; a code no direction is refuses the
    /// index, as reading the record's row refuses it (<see cref="SessionTimelineQuery.DirectionSlot"/>).
    /// </summary>
    private static int DirectionRow(int code) => SessionTimelineQuery.DirectionSlot(DirectionOf(code));

    /// <summary>The direction a record's direction code is; a code no direction is refuses the index, as reading the row does.</summary>
    private static Direction DirectionOf(int code) => Enum.IsDefined((Direction)code)
        ? (Direction)code
        : throw SessionTileIndex.Invalid(string.Create(CultureInfo.InvariantCulture,
            $"a record names a direction no code is: {code}."));

    /// <summary>Whether a kind is one a record can have: a direction code, and no end, the first or the second above it.</summary>
    private static bool IsKind(int kind) => kind < 3 << 3;

    /// <summary>What one brush counts and into what: its interval, the strengths its policy admits, the channels it draws.</summary>
    private sealed record Brush(TimeRange Interval, bool[] Admitted, int[] SlotOfChannel, IntervalTally Tally);

    /// <summary>
    /// What one owner focus counts and into what: its columns, its members' lanes and whether they share the columns, its
    /// rows by direction, its members and their lanes by instance, the one mechanism and the one direction it narrows to,
    /// and the strengths its policy admits.
    /// </summary>
    private sealed record Group(
        TimelineColumns Focused,
        TimelineColumns[]? Lanes,
        bool LanesShareColumns,
        TimelineColumns[]? Directions,
        bool[] Members,
        int[]? LaneOf,
        Mechanism? Mechanism,
        Direction? Direction,
        bool[] Admitted);

    /// <summary>
    /// What one channel focus counts and into what: its columns, its ends' lanes where it counts them, the channel's number,
    /// and the one mechanism, direction and end it narrows to.
    /// </summary>
    private sealed record Channel(
        TimelineColumns Focused,
        SessionTimelineQuery.ChannelEndColumns[]? Ends,
        int Number,
        Mechanism? Mechanism,
        Direction? Direction,
        int? End);

    /// <summary>For each level, the last block of one kind of tallies read and checked, of the section they were read from.</summary>
    private sealed class TallyBlocks
    {
        public int[] Indexes { get; } = new int[SessionTileIndex.MaximumLevels];

        public byte[]?[] Bytes { get; } = new byte[]?[SessionTileIndex.MaximumLevels];

        public TileSection? Section { get; set; }
    }

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
        private readonly ulong kinds;
        private readonly bool measures;
        private readonly bool kept;
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
            kinds = (ulong)mechanisms << 5;
            measures = section.RecordsMeasure;
            kept = section.KeepsBytes;
        }

        /// <summary>The last record's accounting side for its transport-observed bytes; 0 where it states none.</summary>
        public int Side { get; private set; }

        /// <summary>The bytes the last record measured on its <see cref="Side"/>; -1 where it declared a size it did not record.</summary>
        public long Value { get; private set; }

        /// <summary>
        /// The next record: its reading, its mechanism's slot, its direction's code, its end of its channel (0 the first,
        /// 1 the second, -1 none), its owner's key (<see cref="SessionTileIndex.OwnerKey"/>) and its channel's number plus
        /// one, 0 for none. False past the last, which is then checked to end the tile.
        /// </summary>
        public bool Next(out long next, out int slot, out int direction, out int end, out uint owner, out uint channel)
        {
            if (at.AtEnd)
            {
                if (counted != expected || reading != block.Highs[tile])
                {
                    throw Bad();
                }

                (next, slot, direction, end, owner, channel) = (0, 0, 0, 0, 0, 0);
                return false;
            }

            ulong increase = at.Next();
            if ((counted == 0 && increase != 0) || increase > (ulong)(block.Highs[tile] - reading))
            {
                throw Bad();
            }

            reading += (long)increase;
            ulong kind = at.Next();
            ulong ownerKey = at.Next();
            ulong channelKey = at.Next();

            // Since minor 5 a record's measure follows: none, a side it declared a size on and did not record, or a side
            // and the bytes it measured; none for every record of a section keeping no bytes.
            ulong measure = measures ? at.Next() : 0;
            ulong measured = measure > 4 ? at.Next() : 0;
            if (kind >= kinds || !IsKind((int)kind & 31) || ownerKey is > 0 and < 8 || ownerKey > lastOwner || channelKey > lastChannel
                || measure > 8 || (!kept && measure != 0) || measured > long.MaxValue)
            {
                throw Bad();
            }

            (Side, Value) = measure switch
            {
                0 => (0, 0),
                <= 4 => ((int)measure, -1),
                _ => ((int)measure - 4, (long)measured),
            };

            counted++;
            (next, slot, direction, end, owner, channel) =
                (reading, (int)(kind >> 5), (int)kind & 7, (((int)kind >> 3) & 3) - 1, (uint)ownerKey, (uint)channelKey);
            return true;
        }

        private readonly InvalidDataException Bad() => SessionTileIndex.Invalid(string.Create(CultureInfo.InvariantCulture,
            $"the records of tile {position} of {section.Entry.Describe} are not the tile's."));
    }

    /// <summary>LEB128 values out of a run of bytes, refusing one that runs past it or overflows.</summary>
    private ref struct VarintReader(byte[] bytes, int from, int to)
    {
        private int at = from;

        public readonly bool AtEnd => at >= to;

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

/// <summary>
/// What a brush's bytes are measured into from a tile index (`contracts/tile-index-v1.md` minor 6), as a ranking measures
/// them from rows (<see cref="SessionByteRanking"/>): each record's bytes to the instance its owner binds to where the
/// policy admits the binding, or to the slot after the instances, as unattributed; and each record of a drawn channel to
/// the end of it its admitted owner holds, the first where one process holds both.
/// </summary>
internal sealed class ByteMeasuring
{
    private readonly bool[] admitted;
    private readonly int[] slotOfChannel;
    private readonly int[] first;
    private readonly int[] second;
    private readonly int instances;

    /// <param name="admitted">Whether the policy admits an owner bound with each strength an owner key can hold.</param>
    /// <param name="slotOfChannel">Each channel's drawn slot, -1 for one the policy does not draw.</param>
    /// <param name="first">For each drawn slot, the place of the instance holding its first end.</param>
    /// <param name="second">For each drawn slot, the place of the instance holding its second end.</param>
    /// <param name="processes">Each instance's bytes, then a slot of the unattributed.</param>
    /// <param name="ends">Each drawn slot's first end's bytes then its second's, then a slot nothing is added to.</param>
    public ByteMeasuring(
        bool[] admitted, int[] slotOfChannel, int[] first, int[] second, TransportByteTally processes, TransportByteTally ends)
    {
        ArgumentNullException.ThrowIfNull(admitted);
        ArgumentNullException.ThrowIfNull(slotOfChannel);
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(ends);
        this.admitted = admitted;
        this.slotOfChannel = slotOfChannel;
        this.first = first;
        this.second = second;
        instances = processes.Slots - 1;
        Processes = processes;
        Ends = ends;
    }

    public TransportByteTally Processes { get; }

    public TransportByteTally Ends { get; }

    /// <summary>Adds one record's measure: the bytes it measured, or -1 for a size it declared and did not record.</summary>
    public void Add(uint owner, uint channel, int side, long value)
    {
        if (value < 0)
        {
            Add(owner, channel, side, 0, 0, 1);
        }
        else
        {
            Add(owner, channel, side, value, 1, 0);
        }
    }

    /// <summary>
    /// Adds what the records of one owner key, channel key and accounting side measured: their bytes, how many measured
    /// them and how many declared a size they did not record.
    /// </summary>
    public void Add(uint owner, uint channel, int side, long total, long measured, long unmeasured)
    {
        bool bound = owner != 0 && admitted[owner & 7];
        int instance = bound ? (int)(owner >> 3) - 1 : instances;
        Processes.Add(instance, side, total, measured, unmeasured);
        if (bound && channel != 0 && channel <= (uint)slotOfChannel.Length && slotOfChannel[channel - 1] is >= 0 and int slot)
        {
            int end = instance == first[slot] ? slot * 2 : instance == second[slot] ? (slot * 2) + 1 : -1;
            if (end >= 0)
            {
                Ends.Add(end, side, total, measured, unmeasured);
            }
        }
    }
}

/// <summary>
/// The lanes a zoom's bytes are added to from a tile index (`contracts/tile-index-v1.md` minor 5): the columns of their
/// interval, each mechanism's lane, and one tally of every column's bytes in every lane, column by column, with one slot
/// after them that nothing is added to.
/// </summary>
internal sealed class ByteLanes
{
    private readonly int[] laneOfCode;

    /// <param name="columns">The columns of the interval the lanes are summed over.</param>
    /// <param name="laneOfCode">Each mechanism code's lane, -1 for one no lane sums.</param>
    /// <param name="count">How many lanes.</param>
    public ByteLanes(TimelineColumns columns, int[] laneOfCode, int count)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(laneOfCode);
        Columns = columns;
        this.laneOfCode = laneOfCode;
        Count = count;
        Tally = new((columns.Counts.Count * count) + 1);
    }

    public TimelineColumns Columns { get; }

    public int Count { get; }

    public TransportByteTally Tally { get; }

    /// <summary>Each of <paramref name="section"/>'s mechanism slots' lane, -1 for one no lane sums.</summary>
    public int[] LanesOf(TileSection section) =>
        [.. section.Mechanisms.Select(mechanism => (int)mechanism < laneOfCode.Length ? laneOfCode[(int)mechanism] : -1)];
}

/// <summary>
/// The ranges of a spanning section's finest tiles a query could not count whole - where a column boundary or one of its
/// interval's ends falls among their records, or they keep none of the tallies it counts - in time order, each to be
/// counted from the sections beneath it (<see cref="TileReading.Beneath"/>). Ranges with no tile counted whole between
/// them are one: the readings between two such tiles are none, since every record is in one tile.
/// </summary>
internal sealed class TileRemainders
{
    private readonly List<TimeRange> ranges = [];
    private bool adjoins;

    /// <summary>The ranges, in time order.</summary>
    public IReadOnlyList<TimeRange> Ranges => ranges;

    /// <summary>A tile was counted whole: the next range does not adjoin the last.</summary>
    public void Counted() => adjoins = false;

    /// <summary>Adds a finest tile's range, from <paramref name="start"/> to before <paramref name="end"/>.</summary>
    public void Add(long start, long end)
    {
        if (adjoins)
        {
            ranges[^1] = new(ranges[^1].StartTicks, end);
        }
        else
        {
            ranges.Add(new(start, end));
        }

        adjoins = true;
    }
}
