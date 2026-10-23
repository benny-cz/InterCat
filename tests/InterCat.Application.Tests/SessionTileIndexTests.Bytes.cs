using System.Buffers.Binary;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// Since minor 5 a tile index keeps each tile's transport-observed bytes by mechanism and accounting side, and each
/// record's measure (`contracts/tile-index-v1.md` §1): a reopened session's byte lanes and interval bytes add them up
/// exactly as its rows measure them, open no segment, and read a tile's records only where a column boundary falls among
/// them; a part of them that cannot be read is refused, never misread, and the bytes are measured from the segments.
/// </summary>
public sealed partial class SessionTileIndexTests
{
    [Theory(DisplayName = "I4: a reopened session's byte lanes and interval bytes add up from its persisted tiles exactly what its rows measure, and open no segment")]
    [InlineData(5)]
    [InlineData(37)]
    [InlineData(20_261_021)]
    public void PersistedTilesSumWhatRowsMeasure(int seed)
    {
        var random = new Random(seed);
        for (int trial = 0; trial < 8; trial++)
        {
            bool ordered = random.Next(4) != 0;
            ObservationRowV1[] rows = MeasuredRows(random, ordered);
            int perSegment = random.Next(Math.Max(1, rows.Length / 20), rows.Length + 1);
            using var session = Published(rows, perSegment);
            using var twin = new TemporarySession();
            Publish(twin.Store, rows, rowsPerSegment: perSegment);
            long[] ticks = [.. rows.Where(row => row.SessionRelativeTicks is not null).Select(row => row.SessionRelativeTicks!.Value / 100)];
            long low = ticks.Min() - random.Next(0, 50);
            long high = ticks.Max() + random.Next(1, 50);

            SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
            for (int query = 0; query < 12; query++)
            {
                long start = random.Next(3) == 0 ? low : low + random.NextInt64(0, high - low);
                long end = random.Next(3) == 0 ? high : start + 1 + random.NextInt64(0, high - start);
                var interval = new TimeRange(start, end);
                int columns = random.Next(1, 300);
                Assert.Equal(LaneBytes(twin.Store, interval, columns), LaneBytes(reopened, interval, columns));
                Assert.Equal(IntervalBytes(twin.Store, interval, columns), IntervalBytes(reopened, interval, columns));
            }

            // A segment whose readings go backwards keeps no tiles and has its rows read; every other is never opened.
            if (ordered)
            {
                Assert.Equal(0, reopened.SegmentReaderCache.Entries);
            }

            Assert.Null(SessionDerivationCache.For(reopened.Current!).TilesProblem);
            reopened.ReleaseSegmentReaders();
            twin.Store.ReleaseSegmentReaders();
        }
    }

    [Fact(DisplayName = "S3: a zoom's byte lanes add up whole tiles' sums where its columns hold them, and read records only where a column boundary falls among them")]
    public void AZoomSumsWhatItDraws()
    {
        // 200,000 sends a third of a millisecond apart, in four segments, every seventh declaring a size it did not record:
        // each segment's finest tiles a millisecond wide, and three levels above them.
        ObservationRowV1[] rows =
        [
            .. Enumerable.Range(0, 200_000).Select(index => Send(index, index * 333_333L / 100) with
            {
                Mechanism = index % 3 == 0 ? Mechanism.Udp : Mechanism.Tcp,
                ByteValue = index % 7 == 0 ? null : index % 1_500,
                ByteAvailability = index % 7 == 0 ? FieldAvailability.NotExposed : FieldAvailability.Present,
            }),
        ];
        using var session = Published(rows, rowsPerSegment: 50_000);
        using var twin = new TemporarySession();
        Publish(twin.Store, rows, rowsPerSegment: 50_000);
        SessionManifestV1 manifest = session.Store.Current!;
        TileIndexFile file = SessionTileIndex.Open(session.Store.Root, SessionTileIndex.NamedBy(manifest)!, manifest.SessionId);
        TimeRange extent = Extent(rows);
        using TileReading reading = file.Read(session.Store.Root);
        TileSection[] sections = [.. SessionSegments.Names(manifest).Select(reading.Section)];
        Assert.All(sections, section => Assert.Equal((10_000L, 4, true), (section.Width, section.Levels, section.KeepsBytes)));
        int blocks = sections.Sum(section => Enumerable.Range(0, section.Levels)
            .Sum(level => (section.TileCount(level) + SessionTileIndex.TilesPerBlock - 1) / SessionTileIndex.TilesPerBlock));

        // The whole session in a hundred columns: every column's sums from whole tiles but where one of its 101 boundaries,
        // or a segment's end, falls among a tile's records.
        ByteLanes lanes = Lanes(new TimelineColumns(extent, 100, tallyMechanisms: false));
        Assert.All(sections, section => Assert.True(reading.SumInto(section, lanes, extent, CancellationToken.None)));
        Assert.Equal(LaneBytes(twin.Store, extent, 100), Summed(lanes));
        Assert.InRange(reading.TilesOfRecordsRead, 1, 101);
        Assert.InRange(file.BlocksDecoded, 1, blocks / 3);

        // A tenth of a second in the middle reads only the blocks beneath it, and each tile a boundary falls among.
        TileIndexFile fresh = SessionTileIndex.Open(session.Store.Root, SessionTileIndex.NamedBy(manifest)!, manifest.SessionId);
        using TileReading narrow = fresh.Read(session.Store.Root);
        var tenth = new TimeRange(33_000_000, 34_000_000);
        ByteLanes zoomed = Lanes(new TimelineColumns(tenth, 50, tallyMechanisms: false));
        foreach (string name in SessionSegments.Names(manifest))
        {
            Assert.True(narrow.SumInto(narrow.Section(name), zoomed, tenth, CancellationToken.None));
        }

        Assert.Equal(LaneBytes(twin.Store, tenth, 50), Summed(zoomed));
        Assert.InRange(narrow.TilesOfRecordsRead, 0, 51);
        Assert.InRange(fresh.BlocksDecoded, 1, 4 * 8);
        twin.Store.ReleaseSegmentReaders();
    }

    [Theory(DisplayName = "I4: over many segments, a zoom's byte lanes add up the spanning level's sums whole where they can and the sections beneath only the ranges it leaves, exactly what rows measure, opening no segment")]
    [InlineData(13)]
    [InlineData(20_261_021)]
    public void ASpanningLevelSumsWhatRowsMeasure(int seed)
    {
        // Each case's session differs from the other's, so the two share no manifest, nor what its derivations keep.
        var random = new Random(seed);
        ObservationRowV1[] rows = [.. Shifts(4, 40_000 + (seed % 100)).Select((row, index) => row.ByteValue is null || index % 11 != 0
            ? row
            : row with { ByteValue = null, ByteAvailability = FieldAvailability.NotExposed })];
        using var session = Published(rows, rowsPerSegment: 400);
        using var twin = new TemporarySession();
        Publish(twin.Store, rows, rowsPerSegment: 400);
        TileIndexFile file = FileOf(session);
        Assert.True(file.SpansExactly(SessionOverviewIndex.ObservationSegments(session.Store.Current!)));
        TimeRange whole = Extent(rows);
        using (TileReading reading = file.Read(session.Store.Root))
        {
            TileSection spanning = reading.Spanning();
            Assert.True(spanning.KeepsBytes);

            // The whole session in 30 columns from the level's tiles, which leave a range only where a boundary falls.
            ByteLanes lanes = Lanes(new TimelineColumns(whole, 30, tallyMechanisms: false));
            TileRemainders left = reading.SpanSums(lanes, CancellationToken.None);
            Assert.InRange(left.Ranges.Count, 1, 31);
            foreach ((TileSection section, List<TimeRange> ranges) in reading.Beneath(left))
            {
                ranges.ForEach(range => Assert.True(reading.SumInto(section, lanes, range, CancellationToken.None)));
            }

            Assert.Equal(LaneBytes(twin.Store, whole, 30), Summed(lanes));
        }

        // A reopen's zoom in three columns reads the level's blocks and those of the few sections beneath its boundaries,
        // never the hundred sections' every block.
        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        Assert.Equal(LaneBytes(twin.Store, whole, 3), LaneBytes(reopened, whole, 3));
        Assert.InRange(SessionDerivationCache.For(reopened.Current!).PersistedTiles(reopened.Root)!.BlocksDecoded, 1, 40);
        for (int query = 0; query < 20; query++)
        {
            long start = whole.StartTicks + random.NextInt64(0, whole.SpanTicks);
            var interval = new TimeRange(start, start + 1 + random.NextInt64(0, whole.EndTicks - start));
            int columns = random.Next(1, 2_001);
            Assert.Equal(LaneBytes(twin.Store, interval, columns), LaneBytes(reopened, interval, columns));
            Assert.Equal(IntervalBytes(twin.Store, interval, columns), IntervalBytes(reopened, interval, columns));
        }

        Assert.Equal(0, reopened.SegmentReaderCache.Entries);
        Assert.Null(SessionDerivationCache.For(reopened.Current!).TilesProblem);
        reopened.ReleaseSegmentReaders();
        twin.Store.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "I4: a segment whose rows a byte measurement refuses keeps no bytes in its section, nor does a level spanning it")]
    public void ASegmentTheMeasurementRefusesKeepsNoBytes()
    {
        // The same records, but one of the second segment's measured in another unit, which reading its bytes refuses.
        ObservationRowV1[] rows = Shifts(3, 6_030);
        ObservationRowV1[] odd = [.. rows.Select((row, index) => index == 3_001 ? row with { MeasurementUnit = MeasurementUnit.Count } : row)];
        TimeRange whole = Extent(rows);
        using var session = Published(rows, rowsPerSegment: 2_010);
        using var twin = new TemporarySession();
        Publish(twin.Store, odd, rowsPerSegment: 2_010);
        Assert.Contains("measures in Count where earlier rows measure in Bytes",
            Assert.Throws<InvalidDataException>(() => LaneBytes(twin.Store, whole, 64)).Message, StringComparison.Ordinal);

        // A tile index written from those segments: the second's section keeps no bytes, and no level spans their bytes.
        SessionManifestV1 published = session.Store.Current!;
        Bound bound = Bind(session.Store);
        Bound odds = Bind(twin.Store);
        Republish(session, tiles: content => SessionTileIndex.Write(content, published.SessionId, published.Generation,
            [.. SessionOverviewIndex.ObservationSegments(published).Zip(odds.Segments)], bound.Processes, bound.Relations,
            CancellationToken.None));
        using (TileReading reading = FileOf(session).Read(session.Store.Root))
        {
            Assert.Equal([true, false, true], SessionSegments.Names(session.Store.Current!).Select(name => reading.Section(name).KeepsBytes));
            Assert.True(reading.Spanning().Spanning);
            Assert.False(reading.Spanning().KeepsBytes);
            var lanes = Lanes(new TimelineColumns(whole, 64, tallyMechanisms: false));
            Assert.False(reading.SumInto(reading.Section(SessionSegments.Names(session.Store.Current!)[1]), lanes, whole, CancellationToken.None));
        }

        twin.Store.ReleaseSegmentReaders();
        session.Store.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "I4: a section keeping no bytes has its rows measured, and a level spanning it keeps none either: each other section adds up its own, opening no other segment")]
    public void ASectionKeepingNoBytesHasItsRowsMeasured()
    {
        ObservationRowV1[] rows = Shifts(3, 6_060);
        using var session = Published(rows, rowsPerSegment: 2_020);
        using var twin = new TemporarySession();
        Publish(twin.Store, rows, rowsPerSegment: 2_020);
        string path = PathOf(session);
        Republish(session, tiles: content => content.Write(Unsummed(File.ReadAllBytes(path), section => section == 1, unsumSpanning: true)));
        TileIndexFile file = FileOf(session);
        using (TileReading reading = file.Read(session.Store.Root))
        {
            Assert.Equal([true, false, true], SessionSegments.Names(session.Store.Current!).Select(name => reading.Section(name).KeepsBytes));
            Assert.Equal([true, false, true], SessionSegments.Names(session.Store.Current!).Select(name => reading.Section(name).KeepsByteTallies));
            Assert.False(reading.Spanning().KeepsBytes);
            Assert.False(reading.Spanning().KeepsByteTallies);
        }

        TimeRange whole = Extent(rows);
        TimeRange brush = new(whole.StartTicks + 3_001, whole.EndTicks - 2_999);
        Bound bound = Bind(twin.Store);
        ProcessInstanceId[] group = [.. bound.Processes.Instances.Take(3).Select(instance => instance.Id)];
        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        Assert.Equal(LaneBytes(twin.Store, whole, 64), LaneBytes(reopened, whole, 64));

        // Lanes at columns of their own, which no overview keeps, add up from the sections that keep bytes and the rows of
        // the one that does not.
        Assert.Equal(LaneBytes(twin.Store, whole, 63), LaneBytes(reopened, whole, 63));
        Assert.Equal(IntervalBytes(twin.Store, whole, 64), IntervalBytes(reopened, whole, 64));
        Assert.Equal(Brushed(twin.Store, brush, EvidencePolicy.IncludeCorrelated), Brushed(reopened, brush, EvidencePolicy.IncludeCorrelated));
        Assert.Equal(
            OwnerBytes(twin.Store, brush, 64, 16, group, EvidencePolicy.IncludeCorrelated),
            OwnerBytes(reopened, brush, 64, 16, group, EvidencePolicy.IncludeCorrelated));
        Assert.Equal(1, reopened.SegmentReaderCache.Entries);

        // Its records count as before: a zoom's counts open no segment, nor does a brush.
        reopened.ReleaseSegmentReaders();
        Assert.Equal(Expected(rows, whole, 64), Drawn(SessionTimelineQuery.Detail(reopened, whole, 64, Kinds)));
        Assert.Equal(Counted(bound, whole, EvidencePolicy.DirectOnly), Stated(SessionIntervalQuery.Count(reopened, whole, EvidencePolicy.DirectOnly)));
        Assert.Equal(0, reopened.SegmentReaderCache.Entries);
        Assert.Null(SessionDerivationCache.For(reopened.Current!).TilesProblem);
        reopened.ReleaseSegmentReaders();
        twin.Store.ReleaseSegmentReaders();
    }

    [Theory(DisplayName = "I4: byte sums or measures that are damaged or do not agree with their tile are refused, never misread: the bytes are measured from the segments' rows instead")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void DamagedByteSumsAreRefused(int damage)
    {
        // Each case's session differs from the others', so no two share a manifest, nor what a generation's derivations keep.
        // The last case's is a burst of sends 128 ticks apart, then one every ten milliseconds: its first finest tile holds
        // many of the burst's records, which a boundary can fall among.
        ObservationRowV1[] rows = damage < 4 ? Exchanges(1_400 + damage)
            : [.. Enumerable.Range(0, 1_201).Select(index => Send(index, 1_000 + (index * 128L))),
                .. Enumerable.Range(1_201, 601).Select(index => Send(index, 1_000 + (index * 100_000L)))];
        TimeRange whole = Extent(rows);
        using var session = Published(rows);
        (TileSection section, string path) = TopOf(session);
        int top = section.Levels - 1;
        byte[] kept = File.ReadAllBytes(path);
        TileBlock block;
        TileBlock finest;
        using (FileStream stream = File.OpenRead(path))
        {
            TileIndexFile file = FileOf(session);
            block = file.Block(() => stream, section, top, 0);
            finest = file.Block(() => stream, section, 0, 0);
        }

        string name = section.Entry.Name;
        string why;
        long damaged = -1;
        int damagedTile = 0;
        switch (damage)
        {
            case 0:
                // A byte of the top block's sums.
                kept[(int)section.SumsAt + block.SumLinks[0] + 1] ^= 0x40;
                why = $"the byte sums of block 0 of level {top} of the section of '{name}' does not match its checksum.";
                break;
            case 1:
                // The top block's second tile's sums begin where its first's do.
                (long at, int length) = section.BlockAt(top, 0);
                int link = section.TileBytes - 8;
                BinaryPrimitives.WriteInt32LittleEndian(kept.AsSpan((int)at + section.TileBytes + link), block.SumLinks[0]);
                Seal((int)at, length);
                why = $"block 0 of level {top} of the section of '{name}' holds byte sums it places outside the section.";
                break;
            case 2:
                // The top block's first tile says it holds more sums than four for each of its mechanisms.
                kept[(int)section.SumsAt + block.SumLinks[0]] = 0x7F;
                Seal((int)section.SumsAt + block.SumLinks[0], block.SumsEnd - block.SumLinks[0] + 4);
                why = $"the byte sums of tile 0 of level {top} of the section of '{name}' are not the tile's.";
                break;
            case 3:
                // The top block's first tile's first two sums, the second's mechanism and side said first.
                int sums = (int)section.SumsAt + block.SumLinks[0];
                int second = sums + 2;
                for (int field = 0; field < 3; field++)
                {
                    _ = ReadLeb(kept, ref second);
                }

                Assert.True(kept[sums] >= 2 && kept[sums + 1] < kept[second]);
                (kept[sums + 1], kept[second]) = (kept[second], kept[sums + 1]);
                Seal(sums, block.SumsEnd - block.SumLinks[0] + 4);
                why = $"the byte sums of tile 0 of level {top} of the section of '{name}' are not the tile's.";
                break;
            default:
                // The first record measuring bytes, among the first finest tiles', names a side no code is, the bytes after
                // it as they were.
                for (damagedTile = 0; damaged < 0; damagedTile++)
                {
                    int read = (int)section.RecordsAt + finest.Links[damagedTile];
                    int to = (int)section.RecordsAt
                        + (damagedTile + 1 < finest.Links.Length ? finest.Links[damagedTile + 1] - 4 : finest.RecordsEnd);
                    long reading = finest.Lows[damagedTile];
                    while (damaged < 0 && read < to)
                    {
                        reading += (long)ReadLeb(kept, ref read);
                        for (int field = 0; field < 3; field++)
                        {
                            _ = ReadLeb(kept, ref read);
                        }

                        int measure = read;
                        if (ReadLeb(kept, ref read) > 4)
                        {
                            kept[measure] = 9;
                            damaged = reading;
                            _ = ReadLeb(kept, ref read);
                        }
                    }

                    if (damaged >= 0)
                    {
                        Seal((int)section.RecordsAt + finest.Links[damagedTile], to + 4 - ((int)section.RecordsAt + finest.Links[damagedTile]));
                        break;
                    }
                }

                why = $"the records of tile {damagedTile} of the section of '{name}' are not the tile's.";
                break;
        }

        File.WriteAllBytes(path, kept);

        // The lanes measure from the segment's rows what they would have added up: in one column, which counts every top
        // tile whole from its sums, or over a range holding the damaged record that begins or ends among its tile's
        // records.
        using var twin = new TemporarySession();
        Publish(twin.Store, rows);
        SessionStore viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        TimeRange interval = damage < 4 ? whole
            : damaged == finest.Lows[damagedTile] ? new(damaged, damaged + 1)
            : new(finest.Lows[damagedTile] + 1, whole.EndTicks);
        Assert.True(damage < 4 || finest.Highs[damagedTile] > finest.Lows[damagedTile]);
        int columns = damage == 4 ? 7 : 1;
        Assert.Equal(LaneBytes(twin.Store, interval, columns), LaneBytes(viewer, interval, columns));
        Assert.Equal("The persisted tile index is not readable: " + why, SessionDerivationCache.For(viewer.Current!).TilesProblem);
        Assert.Equal(1, viewer.SegmentReaderCache.Entries);
        viewer.ReleaseSegmentReaders();
        twin.Store.ReleaseSegmentReaders();

        void Seal(int at, int length) => Crc32C.Write(kept.AsSpan(at + length - 4), Crc32C.Compute(kept.AsSpan(at, length - 4)));
    }

    /// <summary>
    /// Random records of four mechanisms, each a send or a receive measuring its bytes, declaring a size it did not record,
    /// stating them at its endpoint or for its owner, or measuring another domain's or none; some untimed, the rest at the
    /// session times the clock gives their readings, as every writer makes them (I3), going backwards where not
    /// <paramref name="ordered"/>.
    /// </summary>
    private static ObservationRowV1[] MeasuredRows(Random random, bool ordered)
    {
        int count = random.Next(1, 1_500);
        long native = random.NextInt64(1_000_000, 2_000_000);
        var rows = new ObservationRowV1[count];
        for (int index = 0; index < count; index++)
        {
            native += random.Next(5) switch
            {
                0 => 0,
                1 => random.Next(1, 3),
                2 => random.Next(3, 100),
                3 => random.Next(100, 50_000),
                _ => ordered ? random.Next(1, 10) : -random.Next(1, 1_000),
            };
            bool receive = random.Next(2) == 0;
            long? bytes = random.Next(6) switch
            {
                0 => null,
                1 => 0,
                2 => random.NextInt64(1L << 32, 1L << 40),
                _ => random.Next(1, 65_536),
            };
            AccountingSide side = random.Next(8) switch
            {
                0 => AccountingSide.EndpointActivity,
                1 => AccountingSide.CanonicalOwner,
                _ => receive ? AccountingSide.ReceiveSide : AccountingSide.SendSide,
            };
            ObservationRowV1 row = Transfer(native, receive ? ObservationKind.Receive : ObservationKind.Send, side, bytes, 8, (ulong)(index + 1))
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with
            {
                Mechanism = Kinds[random.Next(Kinds.Length)],
                SessionRelativeTicks = random.Next(8) == 0 && index > 0 ? null : native * 100,
            };
            rows[index] = random.Next(10) switch
            {
                0 => row with { ByteDomain = ByteDomain.ApplicationPayload },
                1 => row with
                {
                    ByteValue = null,
                    ByteDomain = null,
                    AccountingSide = null,
                    MeasurementUnit = null,
                    ByteAvailability = FieldAvailability.NotApplicable,
                    MeasurementQuality = QualityLevel.UnknownQuality,
                },
                _ => row,
            };
        }

        return rows;
    }

    /// <summary>Every lane's bytes, column by column, the four mechanisms each a lane, as a zoom ranked by bytes reads them.</summary>
    private static TransportBytes[] LaneBytes(SessionStore store, TimeRange interval, int columns) =>
        [.. SessionIntervalByteQuery.MeasureByMechanism(store, interval, columns, Kinds).Lanes.SelectMany(lane => lane.Columns)];

    /// <summary>Every record's bytes and each mechanism's, column by column, as the interval table reads them.</summary>
    private static TransportBytes[] IntervalBytes(SessionStore store, TimeRange interval, int columns) =>
        [.. new[] { IntervalByteScope.Whole }.Concat(Kinds.Select(kind => new IntervalByteScope { Mechanism = kind }))
            .SelectMany(scope => SessionIntervalByteQuery.Measure(store, interval, columns, scope).Columns)];

    /// <summary>Lanes of the four mechanisms over <paramref name="columns"/>, for a tile index to add bytes to.</summary>
    private static ByteLanes Lanes(TimelineColumns columns)
    {
        int[] laneOfCode = new int[TimelineColumns.MechanismCodes];
        Array.Fill(laneOfCode, -1);
        for (int lane = 0; lane < Kinds.Length; lane++)
        {
            laneOfCode[(int)Kinds[lane]] = lane;
        }

        return new(columns, laneOfCode, Kinds.Length);
    }

    /// <summary>What a tile index added to <paramref name="lanes"/>, lane by lane and column by column, as <see cref="LaneBytes"/> lists them.</summary>
    private static TransportBytes[] Summed(ByteLanes lanes)
    {
        int columns = lanes.Columns.Counts.Count;
        return [.. Enumerable.Range(0, lanes.Count).SelectMany(lane => Enumerable.Range(0, columns)
            .Select(column => lanes.Tally.Of((column * lanes.Count) + lane)))];
    }
}
