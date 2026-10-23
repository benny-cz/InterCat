using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// What queries read of tile index blocks is kept against a byte budget (§12.1 S2, §19.3): a generation's tile index keeps
/// its own, and the tiles of every chunk a live session's generations name share one, so a focus on traffic whose every
/// record is another pair's than the one before reads each block, its tallies and its records from their files once
/// (S4, P25). The test of a live session clears the shared derivation cache, so they run alone.
/// </summary>
[Collection(SharedDerivationCache.Name)]
public sealed class TileBlockCacheTests
{
    private const int PartBytes = 100;

    [Fact(DisplayName = "S2: a tile block cache keeps a block's tiles and each of its parts apart and once, giving a query that read the same again the first kept, each charged its bytes")]
    public void ABlockIsKeptOnce()
    {
        var cache = new TileBlockCache(1 << 20);
        TileSection section = Section("a");
        TileSection other = Section("b");
        TileBlock first = Block(32);
        Assert.Same(first, cache.Keep(section, 1, 2, first));
        Assert.Same(first, cache.Keep(section, 1, 2, Block(32)));
        Assert.Same(first, cache.Find(section, 1, 2));
        Assert.Null(cache.Find(section, 1, 3));
        Assert.Null(cache.Find(section, 0, 2));
        Assert.Null(cache.Find(other, 1, 2));

        byte[] tallies = new byte[PartBytes];
        Assert.Same(tallies, cache.Keep(section, TilePart.OwnerTallies, 1, 2, tallies));
        Assert.Same(tallies, cache.Keep(section, TilePart.OwnerTallies, 1, 2, new byte[PartBytes]));
        Assert.Same(tallies, cache.Find(section, TilePart.OwnerTallies, 1, 2));
        foreach (TilePart part in new[] { TilePart.ChannelTallies, TilePart.Sums, TilePart.ByteTallies, TilePart.Records })
        {
            Assert.Null(cache.Find(section, part, 1, 2));
        }

        Assert.Null(cache.Find(other, TilePart.OwnerTallies, 1, 2));
        Assert.Equal(2, cache.Count);

        // A block of 32 tiles of one mechanism holds 1,736 bytes: two arrays of longs and seven of ints a tile, nine arrays'
        // headers and its own.
        Assert.Equal(1_736, first.Bytes);
        Assert.Equal(first.Bytes + PartBytes + (2 * TileBlockCache.EntryBytes), cache.Charged);

        // A block's tiles decoded are kept as tiles alone, never as bytes.
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => cache.Find(section, TilePart.Tiles, 1, 2));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => cache.Keep(section, TilePart.Tiles, 1, 2, tallies));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new TileBlockCache(0));
    }

    [Fact(DisplayName = "S2: past its budget a tile block cache gives up parts chosen at random until what it keeps fits, never every part, and keeps none that could never fit")]
    public void PastItsBudgetACacheGivesUpPartsAtRandom()
    {
        var cache = new TileBlockCache(10 * (PartBytes + TileBlockCache.EntryBytes));
        TileSection section = Section("a");
        byte[][] parts = [.. Enumerable.Range(0, 10).Select(_ => new byte[PartBytes])];
        for (int block = 0; block < parts.Length; block++)
        {
            _ = cache.Keep(section, TilePart.Records, 0, block, parts[block]);
        }

        Assert.Equal(10, cache.Count);
        Assert.Equal(cache.Capacity, cache.Charged);

        // One more takes the place of one of them, and every other stays.
        byte[] eleventh = new byte[PartBytes];
        Assert.Same(eleventh, cache.Keep(section, TilePart.Records, 0, 10, eleventh));
        Assert.Same(eleventh, cache.Find(section, TilePart.Records, 0, 10));
        Assert.Equal(10, cache.Count);
        Assert.Equal(cache.Capacity, cache.Charged);
        Assert.Equal(9, Enumerable.Range(0, 10).Count(block => ReferenceEquals(parts[block], cache.Find(section, TilePart.Records, 0, block))));

        // One the size of four takes the places of four.
        byte[] large = new byte[(4 * PartBytes) + (3 * TileBlockCache.EntryBytes)];
        Assert.Same(large, cache.Keep(section, TilePart.Records, 0, 11, large));
        Assert.Same(large, cache.Find(section, TilePart.Records, 0, 11));
        Assert.Equal(7, cache.Count);
        Assert.Equal(cache.Capacity, cache.Charged);

        // One larger than the budget is the reading query's alone, and gives up nothing.
        byte[] whole = new byte[cache.Capacity];
        Assert.Same(whole, cache.Keep(section, TilePart.Records, 0, 12, whole));
        Assert.Null(cache.Find(section, TilePart.Records, 0, 12));
        Assert.Equal(7, cache.Count);
        Assert.Same(large, cache.Find(section, TilePart.Records, 0, 11));
    }

    [Fact(DisplayName = "S2: a scan of more blocks than a tile block cache holds, repeated in the same order, finds many of them kept each time, where giving up every block once full or the least recently read would find none")]
    public void ARepeatedScanFindsManyKept()
    {
        var cache = new TileBlockCache(64 * (PartBytes + TileBlockCache.EntryBytes));
        TileSection section = Section("a");
        for (int pass = 0; pass < 8; pass++)
        {
            int found = 0;
            for (int block = 0; block < 96; block++)
            {
                if (cache.Find(section, TilePart.Records, 0, block) is not null)
                {
                    found++;
                }
                else
                {
                    _ = cache.Keep(section, TilePart.Records, 0, block, new byte[PartBytes]);
                }
            }

            Assert.InRange(found, pass == 0 ? 0 : 96 / 4, pass == 0 ? 0 : 64);
            Assert.Equal(64, cache.Count);
            Assert.Equal(64, Enumerable.Range(0, 96).Count(block => cache.Find(section, TilePart.Records, 0, block) is not null));
        }
    }

    [Fact(DisplayName = "S2: a query's reading of a tile index opens its file when a read first needs a part not kept, once whatever it reads, and a later query that finds every part it reads kept opens none")]
    public void AReadingOpensItsFileOnlyForWhatIsNotKept()
    {
        // 200,000 sends a third of a millisecond apart in four segments, each finest tile a millisecond wide, counted at a
        // hundred columns: whole tiles, and records where a column boundary falls among them.
        using var session = new TemporarySession();
        ObservationRowV1[] rows =
        [
            .. Enumerable.Range(0, 200_000).Select(index => index * 333_333L / 100).Select((tick, index) => Transfer(tick,
                ObservationKind.Send, AccountingSide.SendSide, 8, 100, (ulong)(index + 1)).Between("127.0.0.1:50000", "127.0.0.1:8080")
                with { SessionRelativeTicks = tick * 100 }),
        ];
        _ = Publish(session.Store, rows, rowsPerSegment: 50_000);
        Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(session.Store, Committed).Outcome);
        SessionManifestV1 manifest = session.Store.Current!;
        TileIndexFile file = SessionTileIndex.Open(session.Store.Root, SessionTileIndex.NamedBy(manifest)!, manifest.SessionId);
        string[] names = [.. SessionSegments.Names(manifest)];
        Assert.Equal(4, names.Length);
        var extent = new TimeRange(0, (199_999L * 333_333 / 100) + 1);
        string Count(TileReading reading)
        {
            var columns = new TimelineColumns(extent, 100, tallyMechanisms: true);
            foreach (string name in names)
            {
                Assert.True(reading.CountInto(reading.Section(name), columns, CancellationToken.None));
            }

            return string.Join(",", columns.Counts);
        }

        string counted;
        using (TileReading reading = file.Read(session.Store.Root))
        {
            Assert.Equal(0, file.TimesOpened);
            counted = Count(reading);
            Assert.Equal(1, file.TimesOpened);
            Assert.True(file.BlocksDecoded > names.Length, $"{file.BlocksDecoded} blocks decoded");
            Assert.True(file.PartsRead > 0, "No records read");
        }

        using (TileReading again = file.Read(session.Store.Root))
        {
            Assert.Equal(counted, Count(again));
            Assert.Equal(1, file.TimesOpened);
        }
    }

    [Fact(DisplayName = "S4: a live generation's focuses on traffic whose every record is another pair's than the one before read each block of its chunks' tiles, its tallies and its records from their files once, opening none again, more blocks a chunk than each chunk kept on its own, every chunk's kept in one budget a later generation shares, and count as the rows do")]
    public void InterleavedFocusesReadEachBlockOnce()
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        using var twin = new TemporarySession();
        for (int chunk = 0; chunk < 3; chunk++)
        {
            ObservationRowV1[] rows = Interleaved(chunk * 3_000L, 3_000);
            _ = Publish(session.Store, rows, indexer: LiveCheckpoints.Instance);
            _ = Publish(twin.Store, rows);
        }

        SessionDerivationCache.Clear();
        SessionStore viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        SessionOverviewBundle drawn = SessionOverviewProjector.Project(viewer);
        TimeRange extent = drawn.Extent!.Value;
        ProcessInstanceId[] instances = [.. drawn.Nodes.Where(node => node.Name.StartsWith("client", StringComparison.OrdinalIgnoreCase)).Select(node => node.Id)];
        ProcessInstanceId[] group = instances[..8];
        string channel = ChannelOf(viewer);
        TimeRange later = new(extent.StartTicks + (extent.SpanTicks * 3 / 7), extent.EndTicks);
        (TimeRange Interval, Func<SessionStore, string> Query)[] queries =
        [
            (extent, store => Text(SessionTimelineQuery.Focused(store, extent, 64, new TimelineFocus(null, [instances[0]])))),
            (extent, store => Text(SessionTimelineQuery.Focused(store, extent, 64, new TimelineFocus(null, group), lanes: group))),
            (extent, store => Text(SessionTimelineQuery.Focused(store, extent, 64, new TimelineFocus(channel, [])))),
            (extent, store => Text(SessionTimelineQuery.Focused(store, extent, 64, new TimelineFocus(channel, [], end: 1)))),
            (later, store => Text(SessionTimelineQuery.Focused(store, later, 50, new TimelineFocus(null, [instances[1]])))),
        ];
        AskAll();

        // The tiles of every chunk but the newest, each read past the 64 blocks a chunk kept on its own before, and each
        // keeping them where every other does.
        IReadOnlyList<TileIndexFile> chunks = SessionDerivationCache.For(viewer.Current!).TiledChunks(viewer.Root);
        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, file => Assert.Same(chunks[0].Blocks, file.Blocks));
        Assert.All(chunks, file => Assert.True(file.BlocksDecoded > 64, $"{file.BlocksDecoded} blocks decoded"));
        Assert.All(chunks, file => Assert.True(file.PartsRead > file.BlocksDecoded, $"{file.PartsRead} tallies and records read"));
        Assert.All(chunks, file => Assert.True(file.TimesOpened > 0, "Never opened"));
        int[] decoded = [.. chunks.Select(file => file.BlocksDecoded)];
        int[] read = [.. chunks.Select(file => file.PartsRead)];
        int[] opened = [.. chunks.Select(file => file.TimesOpened)];
        long charged = chunks[0].Blocks.Charged;
        Assert.InRange(charged, 1, ChunkTiles.CachedBytes);

        // Asked again, they read nothing from the files, and open none: no block decoded, and no tallies or records read.
        AskAll();
        Assert.Equal(decoded, chunks.Select(file => file.BlocksDecoded));
        Assert.Equal(read, chunks.Select(file => file.PartsRead));
        Assert.Equal(opened, chunks.Select(file => file.TimesOpened));
        Assert.Equal(charged, chunks[0].Blocks.Charged);
        viewer.ReleaseSegmentReaders();

        // A later generation keeps its new chunk's tiles where the earlier chunks' are kept.
        ObservationRowV1[] fourth = Interleaved(9_000, 3_000);
        _ = Publish(session.Store, fourth, indexer: LiveCheckpoints.Instance);
        _ = Publish(twin.Store, fourth);
        SessionStore next = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        IReadOnlyList<TileIndexFile> nextChunks = SessionDerivationCache.For(next.Current!).TiledChunks(next.Root);
        Assert.Equal(3, nextChunks.Count);
        Assert.All(nextChunks, file => Assert.Same(chunks[0].Blocks, file.Blocks));
        Assert.Equal(queries[1].Query(twin.Store), queries[1].Query(next));
        next.ReleaseSegmentReaders();

        // Each counts every chunk but the newest from its tiles, opening only the newest chunk's segments, as the rows do.
        void AskAll()
        {
            foreach ((TimeRange interval, Func<SessionStore, string> query) in queries)
            {
                int newest = ChunkTilesTests.Met(viewer, interval, viewer.Current!.Generation);
                viewer.ReleaseSegmentReaders();
                string counted = query(viewer);
                Assert.Equal(newest, viewer.SegmentReaderCache.Entries);
                Assert.Equal(query(twin.Store), counted);
            }
        }
    }

    private static TileSection Section(string name) => new(
        new TileSectionEntry(name, 0, string.Empty, 0, 0), SessionTileIndex.Minor, 0, 0, ordered: true, null, null, 1, [], [], [],
        0, 0, 0, 0, 0);

    private static TileBlock Block(int tiles) => new()
    {
        Lows = new long[tiles],
        Highs = new long[tiles],
        Links = new int[tiles],
        Children = new int[tiles],
        OwnerLinks = new int[tiles],
        ChannelLinks = new int[tiles],
        Counts = new int[tiles],
        RecordsEnd = 0,
        OwnerTalliesEnd = 0,
        ChannelTalliesEnd = 0,
        SumLinks = new int[tiles],
        SumsEnd = 0,
        ByteLinks = new int[tiles],
        ByteTalliesEnd = 0,
    };

    /// <summary>
    /// <paramref name="count"/> records from <paramref name="first"/> of forty client and server pairs taking turns, each
    /// record another pair's than the one before and a tile width apart, so a chunk of them holds a finest tile a record and
    /// no tile tallies its owners or its channels: the first eighty create the processes, and each pair's first transfer
    /// connects.
    /// </summary>
    private static ObservationRowV1[] Interleaved(long first, int count)
    {
        const int Pairs = 40;
        var rows = new ObservationRowV1[count];
        for (int offset = 0; offset < count; offset++)
        {
            long index = first + offset;
            long ticks = 10 + (index * 10);
            ulong ordinal = (ulong)index + 1;
            ObservationRowV1 row;
            if (index < 2 * Pairs)
            {
                bool client = index < Pairs;
                int pair = (int)(index % Pairs);
                row = Lifecycle(ticks, ObservationKind.Create, (client ? 10_000 : 20_000) + pair, ordinal) with
                {
                    ResourceName = string.Create(CultureInfo.InvariantCulture, $@"C:\Busy\{(client ? "client" : "server")}{pair % 8}.exe"),
                };
            }
            else
            {
                long turn = index - (2 * Pairs);
                int pair = (int)(turn % Pairs);
                int client = 10_000 + pair;
                int server = 20_000 + pair;
                string clientEnd = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{40_000 + pair}");
                string serverEnd = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{8_000 + pair}");
                row = ((turn / Pairs) % 4) switch
                {
                    0 => Transfer(ticks, turn < Pairs ? ObservationKind.Connect : ObservationKind.Send, AccountingSide.SendSide, 512, client, ordinal)
                        .Between(clientEnd, serverEnd),
                    1 => Transfer(ticks, ObservationKind.Receive, AccountingSide.ReceiveSide, 512, server, ordinal).Between(serverEnd, clientEnd),
                    2 => Transfer(ticks, ObservationKind.Send, AccountingSide.SendSide, 4_096, server, ordinal).Between(serverEnd, clientEnd),
                    _ => Transfer(ticks, ObservationKind.Receive, AccountingSide.ReceiveSide, 4_096, client, ordinal).Between(clientEnd, serverEnd),
                };
            }

            rows[offset] = row with { SessionRelativeTicks = ticks * 100 };
        }

        return rows;
    }

    /// <summary>The stable key of the first pair's channel.</summary>
    private static string ChannelOf(SessionStore store)
    {
        SessionManifestV1 manifest = store.Current!;
        SegmentReaderV1[] observations = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store, manifest, name))];
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, []);
        return TransportRelationIndex.Derive(observations, processes).Relations
            .First(relation => relation.FirstEndpoint == "127.0.0.1:40000" || relation.SecondEndpoint == "127.0.0.1:40000").StableKey;
    }

    private static string Text(SessionFocusedTimeline timeline) => JsonSerializer.Serialize(new
    {
        timeline.Focus,
        timeline.FocusLanes,
        ProcessLanes = timeline.ProcessLanes.Select(lane => new { lane.ProcessId, lane.Buckets }),
        timeline.FoldedLane,
        timeline.DirectionLanes,
        timeline.OwnerLane,
        timeline.ProcessLaneProblem,
    });
}
