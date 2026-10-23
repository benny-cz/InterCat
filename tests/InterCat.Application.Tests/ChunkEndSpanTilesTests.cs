using System.Buffers.Binary;
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
/// tile-index-v1 §6 since minor 9: a chunk's tiles key each record's channel by the span of the end it was made at - the
/// end, and the incarnation of it the record belongs to under the derivation that tiles it, with the first and last of
/// those records - so a later generation's brush finds each span's channel among its own and counts the chunk from its
/// tiles, and reads its rows where a lifecycle record read later divides a span (§12.1 S4, P25). They clear the shared
/// derivation cache, so they run alone.
/// </summary>
[Collection(SharedDerivationCache.Name)]
public sealed class ChunkEndSpanTilesTests
{
    private static readonly EvidencePolicy[] Policies = [EvidencePolicy.IncludeCorrelated, EvidencePolicy.IncludeCandidates];

    [Theory(DisplayName = "S4: a live generation's brush counts and measures a chunk from its tiles where its relations hold each span of the chunk's ends within one incarnation and its instances bind the chunk's owners alike, and from its rows where a disconnect read later divides a span or a creation read later splits an owner's records, as every segment's rows count and measure it")]
    [InlineData(3)]
    [InlineData(41)]
    [InlineData(20_261_023)]
    public void ALiveBrushCountsAsRowsDo(int seed)
    {
        SessionDerivationCache.Clear();
        using var tiled = new TemporarySession();
        using var plain = new TemporarySession();
        var random = new Random(seed);
        ObservationRowV1[][] chunks = Traffic(random, 6);
        int counted = 0;
        int refused = 0;
        int ownersRefused = 0;
        for (int chunk = 0; chunk < chunks.Length; chunk++)
        {
            int rowsPerSegment = 1 + random.Next(chunks[chunk].Length);
            _ = Publish(tiled.Store, chunks[chunk], rowsPerSegment: rowsPerSegment, indexer: LiveCheckpoints.Instance);
            _ = Publish(plain.Store, chunks[chunk], rowsPerSegment: rowsPerSegment);
            if (chunk == 0)
            {
                continue;
            }

            SessionDerivationCache.Clear();
            SessionStore live = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(tiled.Path));
            SessionStore read = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(plain.Path));
            TimeRange extent = SessionOverviewProjector.Project(live).Extent!.Value;
            long middle = extent.StartTicks + random.NextInt64(Math.Max(1, extent.SpanTicks));
            foreach (TimeRange interval in new[] { extent, new TimeRange(middle, extent.EndTicks), new TimeRange(extent.StartTicks, middle + 1) })
            {
                foreach (EvidencePolicy policy in Policies)
                {
                    Assert.Equal(Text(SessionIntervalQuery.Count(read, interval, policy)), Text(SessionIntervalQuery.Count(live, interval, policy)));
                    Assert.Equal(Text(SessionByteRanking.Measure(read, interval, policy)), Text(SessionByteRanking.Measure(live, interval, policy)));
                }
            }

            // Which chunks' spans this generation's relations hold, and of those whose owners its instances bind alike.
            SessionManifestV1 manifest = live.Current!;
            (SegmentReaderV1[] observations, SegmentReaderV1[] fields) = SegmentsOf(live);
            ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fields);
            TransportRelationIndex relations = TransportRelationIndex.Derive(observations, processes);
            foreach (TileIndexFile file in SessionDerivationCache.For(manifest).TiledChunks(live.Root))
            {
                if (SessionTileIndex.RebindChannels(file, relations) is null)
                {
                    refused++;
                }
                else if (SessionTileIndex.Rebind(file, processes) is null)
                {
                    ownersRefused++;
                }
                else
                {
                    counted++;
                }
            }

            live.ReleaseSegmentReaders();
            read.ReleaseSegmentReaders();
        }

        // The first connection's close, read a chunk late, divides the records the second chunk's tiles held of its end; the
        // creation of PID 107, read a chunk late, splits the third chunk's records of it, whose spans hold.
        Assert.True(counted > 0, "No chunk's spans held.");
        Assert.True(refused > 0, "No chunk's spans were divided by a disconnect read later.");
        Assert.True(ownersRefused > 0, "No chunk whose spans held had its owners split by a creation read later.");
    }

    [Fact(DisplayName = "S4: a live generation opened cold counts and measures a brush from its chunks' tiles, opening only its newest chunk's segments, as a session of the same chunks recorded without tiles counts and measures it")]
    public void ALiveBrushOpensOnlyTheNewestChunk()
    {
        using var session = new TemporarySession();
        using var twin = new TemporarySession();
        foreach (ObservationRowV1[] chunk in Steady(4))
        {
            _ = Publish(session.Store, chunk, rowsPerSegment: 100, indexer: LiveCheckpoints.Instance);
            _ = Publish(twin.Store, chunk, rowsPerSegment: 100);
        }

        SessionDerivationCache.Clear();
        SessionStore viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        TimeRange extent = SessionOverviewProjector.Project(viewer).Extent!.Value;
        foreach (TimeRange interval in new[] { extent, new TimeRange(extent.StartTicks + (extent.SpanTicks / 3), extent.EndTicks) })
        {
            int newest = ChunkTilesTests.Met(viewer, interval, viewer.Current!.Generation);
            foreach (EvidencePolicy policy in Policies)
            {
                viewer.ReleaseSegmentReaders();
                string counted = Text(SessionIntervalQuery.Count(viewer, interval, policy));
                Assert.Equal(newest, viewer.SegmentReaderCache.Entries);
                Assert.Equal(Text(SessionIntervalQuery.Count(twin.Store, interval, policy)), counted);
                viewer.ReleaseSegmentReaders();
                string measured = Text(SessionByteRanking.Measure(viewer, interval, policy));
                Assert.Equal(newest, viewer.SegmentReaderCache.Entries);
                Assert.Equal(Text(SessionByteRanking.Measure(twin.Store, interval, policy)), measured);
            }
        }

        viewer.ReleaseSegmentReaders();
    }

    [Theory(DisplayName = "S4: a chunk's tiles span each end's records within one incarnation, which a later derivation holds unless a disconnect read later falls among them, to the record: one before the last record of the end does, and one after it in the canonical order does not")]
    [InlineData(399, false)]
    [InlineData(400, true)]
    public void ASpanHoldsWhereOneIncarnationHoldsItsRecords(long disconnectedAt, bool holds)
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        const string client = "10.0.0.1:50000";
        const string server = "10.0.0.2:443";
        ObservationRowV1 Row(long at, Func<long, ulong, ObservationRowV1> make, ulong number) =>
            make(at, number) with { SessionRelativeTicks = at * 100 };

        // A connection opened, sending three times; its client's close, read only in the next chunk, at the reading of the
        // last send or just before it. Its records come four to a segment, the first meeting each end at its middle and the
        // next bringing its first and last: a span stretches over its records whichever segment holds them.
        ObservationRowV1[] first =
        [
            Row(10, (at, number) => Lifecycle(at, ObservationKind.Create, 100, number), 1),
            Row(11, (at, number) => Lifecycle(at, ObservationKind.Create, 200, number), 2),
            Row(200, Tcp(ObservationKind.Send, 100, client, server, 10), 5),
            Row(201, Tcp(ObservationKind.Receive, 200, server, client, 10), 6),
            Row(100, Tcp(ObservationKind.Connect, 100, client, server), 3),
            Row(101, Tcp(ObservationKind.Accept, 200, server, client), 4),
            Row(400, Tcp(ObservationKind.Send, 100, client, server, 30), 9),
            Row(401, Tcp(ObservationKind.Receive, 200, server, client, 30), 10),
            Row(300, Tcp(ObservationKind.Send, 100, client, server, 20), 7),
            Row(301, Tcp(ObservationKind.Receive, 200, server, client, 20), 8),

            // Another connection, closed and opened again on its port: two incarnations at each end, a span each; and a
            // send of the second that no session time places, which the tiles do not hold.
            Row(120, Tcp(ObservationKind.Connect, 100, Reused, server), 11),
            Row(121, Tcp(ObservationKind.Accept, 200, server, Reused), 12),
            Row(130, Tcp(ObservationKind.Send, 100, Reused, server, 5), 13),
            Row(140, Tcp(ObservationKind.Disconnect, 100, Reused, server), 14),
            Row(141, Tcp(ObservationKind.Disconnect, 200, server, Reused), 15),
            Row(150, Tcp(ObservationKind.Connect, 100, Reused, server), 16),
            Row(151, Tcp(ObservationKind.Accept, 200, server, Reused), 17),
            Row(160, Tcp(ObservationKind.Send, 100, Reused, server, 5), 18),
            Tcp(ObservationKind.Send, 100, Reused, server, 5)(170, 19) with { SessionRelativeTicks = null },
        ];
        ObservationRowV1[] second =
        [
            Row(disconnectedAt, Tcp(ObservationKind.Disconnect, 100, client, server), 20),
            Row(900, Tcp(ObservationKind.Disconnect, 200, server, client), 21),
        ];
        _ = Publish(session.Store, first, rowsPerSegment: 4, indexer: LiveCheckpoints.Instance);
        _ = Publish(session.Store, second, rowsPerSegment: 50, indexer: LiveCheckpoints.Instance);

        SessionManifestV1 manifest = session.Store.Current!;
        TileIndexFile file = SessionTileIndex.Open(session.Store.Root, Assert.Single(ChunkTiles.NamedBy(manifest)), manifest.SessionId);

        // The client's end first, its records from its connect to its last send; then the server's, from its accept; then
        // the other connection's two incarnations at each end, the second client's to its last send that is placed.
        Assert.Equal(6, file.Ends.Count);
        Assert.Equal(file.Ends.Count, file.Derivation.Channels);
        Assert.Equal([(120L, 140L), (121L, 141L), (150L, 160L), (151L, 151L)], file.Ends.Skip(2).Select(span => (span.First.Ticks, span.Last.Ticks)));
        Assert.Equal((Mechanism.Tcp, (byte)4, (ushort)50000, (ushort)443, 100L, 400L),
            (file.Ends[0].Protocol, file.Ends[0].Family, file.Ends[0].LocalPort, file.Ends[0].RemotePort, file.Ends[0].First.Ticks, file.Ends[0].Last.Ticks));
        Assert.Equal(((ushort)443, (ushort)50000, 101L, 401L),
            (file.Ends[1].LocalPort, file.Ends[1].RemotePort, file.Ends[1].First.Ticks, file.Ends[1].Last.Ticks));

        (SegmentReaderV1[] observations, SegmentReaderV1[] fields) = SegmentsOf(session.Store);
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fields);
        TransportRelationIndex relations = TransportRelationIndex.Derive(observations, processes);
        int[]? channels = SessionTileIndex.RebindChannels(file, relations);
        Assert.Equal(holds, channels is not null);

        // An end no record the derivation holds was made at has no channel.
        Assert.False(relations.TryChannelOf(file.Ends[0] with { RemotePort = 1 }, out int unknown));
        Assert.Equal(-1, unknown);
        if (channels is not null)
        {
            // Both ends' spans bind to the one channel their connection is.
            TransportRelation connection = Assert.Single(relations.Relations, relation => relation.FirstEndpoint == client || relation.SecondEndpoint == client);
            Assert.Equal([connection.Channel + 1, connection.Channel + 1], channels.Take(2));
        }

        // What a chunk's tiles state they bind under names their spans, in their order.
        Assert.NotEqual(SessionTileIndex.ChunkDerivation(processes, [.. file.Ends.Reverse()]).Fingerprint,
            SessionTileIndex.ChunkDerivation(processes, file.Ends).Fingerprint);

        // A file of another rule, or of a minor naming no ends, names no channel a later derivation could find.
        Assert.Null(SessionTileIndex.RebindChannels(With(file, file.Derivation with { RelationRule = TransportRelationIndex.RelationRule }), relations));
        Assert.Null(SessionTileIndex.RebindChannels(With(file, file.Derivation, minor: SessionTileIndex.CertifiedMinor), relations));
    }

    [Fact(DisplayName = "S4: a chunk's tiles counted for a derivation that binds one of their spans to no channel count its records for none, whole from a tile's tallies or one by one, and every other record as they bound it")]
    public void ASpanBoundToNoChannelCountsForNone()
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        foreach (ObservationRowV1[] chunk in Steady(2))
        {
            _ = Publish(session.Store, chunk, rowsPerSegment: 100, indexer: LiveCheckpoints.Instance);
        }

        SessionManifestV1 manifest = session.Store.Current!;
        TileIndexFile file = SessionTileIndex.Open(session.Store.Root, Assert.Single(ChunkTiles.NamedBy(manifest)), manifest.SessionId);
        (SegmentReaderV1[] observations, SegmentReaderV1[] fields) = SegmentsOf(session.Store);
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fields);
        TransportRelationIndex relations = TransportRelationIndex.Derive(observations, processes);
        int[] owners = Assert.IsType<int[]>(SessionTileIndex.Rebind(file, processes));
        int[] channels = Assert.IsType<int[]>(SessionTileIndex.RebindChannels(file, relations));
        int[] slotOfChannel = [.. Enumerable.Range(0, relations.Channels)];
        TimeRange whole = SessionOverviewProjector.Project(session.Store).Extent!.Value;
        IntervalTally Counted(int[] mapped)
        {
            var tally = new IntervalTally(processes.Instances.Count * TimelineColumns.SlotCount, relations.Channels);
            using TileReading reading = file.Read(session.Store.Root);
            reading.Rebound = owners;
            reading.ChannelsRebound = mapped;
            foreach (string segment in file.Segments)
            {
                Assert.True(reading.CountInterval(reading.Section(segment), whole, EvidencePolicy.IncludeCandidates, slotOfChannel, tally,
                    CancellationToken.None));
            }

            return tally;
        }

        IntervalTally bound = Counted(channels);
        int[] none = [.. channels];
        none[0] = 0;
        IntervalTally unbound = Counted(none);
        int channel = channels[0] - 1;
        Assert.Equal(bound.Observed, unbound.Observed);
        Assert.Equal(bound.Processes, unbound.Processes);
        Assert.True(bound.Channels[channel] > unbound.Channels[channel]);
        Assert.Equal(bound.Graph - unbound.Graph, bound.Channels[channel] - unbound.Channels[channel]);
    }

    [Theory(DisplayName = "S4: a chunk's tiles whose directory lists an end span no derivation gathers, more or fewer than it holds, or another number than the channels its derivation states, are refused as data")]
    [InlineData(0, "its directory lists an end span no derivation gathers.")]
    [InlineData(1, "its directory lists an end span no derivation gathers.")]
    [InlineData(2, "its directory lists an end span no derivation gathers.")]
    [InlineData(3, "its directory lists an end span no derivation gathers.")]
    [InlineData(4, "its directory lists an end span no derivation gathers.")]
    [InlineData(5, "its directory lists an end span no derivation gathers.")]
    [InlineData(6, "its directory states more end spans than it holds.")]
    [InlineData(7, "its directory states more end spans than it holds.")]
    [InlineData(8, "its directory lists another number of end spans than the channels its derivation states.")]
    [InlineData(9, "its directory lists an end span no derivation gathers.")]
    [InlineData(10, "its directory lists an end span no derivation gathers.")]
    [InlineData(11, "its directory lists an end span no derivation gathers.")]
    public void AMalformedEndSpanIsRefused(int damage, string why)
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        foreach (ObservationRowV1[] chunk in Steady(2))
        {
            _ = Publish(session.Store, chunk, rowsPerSegment: 100, indexer: LiveCheckpoints.Instance);
        }

        SessionManifestV1 manifest = session.Store.Current!;
        StoreDependency named = Assert.Single(ChunkTiles.NamedBy(manifest));
        TileIndexFile file = SessionTileIndex.Open(session.Store.Root, named, manifest.SessionId);
        string path = Path.Combine(session.Path, named.Name);
        byte[] bytes = File.ReadAllBytes(path);
        ReadOnlySpan<byte> footer = bytes.AsSpan(bytes.Length - SessionTileIndex.FooterLength);
        int directory = (int)BinaryPrimitives.ReadInt64LittleEndian(footer);
        int listed = BinaryPrimitives.ReadInt32LittleEndian(footer[8..]);

        // The spans end the directory, after their count: an end's protocol, family, own address and port, remote address
        // and port, then its first and last record's places.
        int count = file.Ends.Count;
        int at = directory + listed - (count * SessionTileIndex.EndSpanBytes);
        Assert.Equal(count, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at - 4)));
        Assert.True(count > 0);
        Span<byte> span = bytes.AsSpan(at, SessionTileIndex.EndSpanBytes);
        switch (damage)
        {
            case 0:
                // A protocol the rule does not relate.
                span[0] = (byte)Mechanism.Rpc;
                break;
            case 1:
                // A family neither IPv4 nor IPv6.
                span[1] = 5;
                break;
            case 2:
                // No own port.
                BinaryPrimitives.WriteUInt16LittleEndian(span[18..], 0);
                break;
            case 3:
                // No remote address.
                span.Slice(20, 16).Clear();
                break;
            case 4:
                // An IPv4 own address beyond 32 bits.
                span[2] = 1;
                break;
            case 5:
                // A first record after the last.
                BinaryPrimitives.WriteInt64LittleEndian(span[38..], BinaryPrimitives.ReadInt64LittleEndian(span[78..]) + 1);
                break;
            case 6:
                // More than it holds.
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at - 4), count + 1);
                break;
            case 7:
                // Fewer than none.
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at - 4), -1);
                break;
            case 8:
                // Its derivation states one channel more: after the binding rule, the empty relation rule and the instances.
                int channels = directory + 1 + bytes[directory] + 1 + 4;
                Assert.Equal(count, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(channels)));
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(channels), count + 1);
                break;
            case 9:
                // No remote port.
                BinaryPrimitives.WriteUInt16LittleEndian(span[36..], 0);
                break;
            case 10:
                // No own address.
                span.Slice(2, 16).Clear();
                break;
            default:
                // An IPv4 remote address beyond 32 bits.
                span[20] = 1;
                break;
        }

        Crc32C.Write(bytes.AsSpan(bytes.Length - SessionTileIndex.FooterLength + 16), Crc32C.Compute(bytes.AsSpan(directory, listed)));
        Crc32C.Write(bytes.AsSpan(bytes.Length - 4), Crc32C.Compute(bytes.AsSpan(bytes.Length - SessionTileIndex.FooterLength, SessionTileIndex.FooterLength - 4)));
        File.WriteAllBytes(path, bytes);
        Assert.EndsWith(why, Assert.Throws<InvalidDataException>(() => SessionTileIndex.Open(session.Store.Root, named, manifest.SessionId)).Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The chunks of a capture whose processes connect to each other over TCP and send each other datagrams over UDP, PIDs
    /// 100 to 105 created in the first, each chunk's records delivered in no order of their readings. Each connection is
    /// opened, carries a few transfers each way and is closed, and at times its server's records come a chunk late, so
    /// its client's end is one-sided until they come. The first is closed in the second chunk while its port carries on -
    /// a connection whose opening the capture lost - and its client's close comes in the third, dividing the records of
    /// that end the second chunk's tiles held. Later connections take ports below the earlier ones', so their ends sort
    /// first and the channels before them are numbered again. Some datagrams go to another host, which no record holds.
    /// </summary>
    private static ObservationRowV1[][] Traffic(Random random, int count)
    {
        const long Width = 10_000;
        var rows = new List<(int Chunk, long At, Func<long, ulong, ObservationRowV1> Make, bool Untimed)>();
        void At(long at, int late, Func<long, ulong, ObservationRowV1> make, bool untimed = false) =>
            rows.Add(((int)Math.Min(count - 1, (at / Width) + late), at, make, untimed));

        for (int pid = 100; pid < 106; pid++)
        {
            int created = pid;
            At(10 + pid, 0, (at, number) => Lifecycle(at, ObservationKind.Create, created, number));
        }

        // PID 106 sends to another host in the second chunk and is created in the third, after: the records the second
        // chunk's tiles hold of it bind to no instance once its creation is read. PID 99 is first seen in the fourth, and
        // every instance after it stands one place later from then on.
        At(Width + 500, 0, Udp(ObservationKind.Send, 106, "10.0.0.1:61000", "192.0.2.200:53", 64));
        At((2 * Width) + 500, 0, (at, number) => Lifecycle(at, ObservationKind.Create, 106, number));
        At((2 * Width) + 600, 0, Udp(ObservationKind.Send, 106, "10.0.0.1:61000", "192.0.2.200:53", 64));
        At((3 * Width) + 500, 0, Udp(ObservationKind.Send, 99, "10.0.0.1:61001", "192.0.2.201:53", 64));

        // PID 107 sends twice in the third chunk, and its creation between the two is read only in the fourth: the third
        // chunk's tiles bound both sends to the one instance the first began, which every later derivation splits.
        At((2 * Width) + 400, 0, Udp(ObservationKind.Send, 107, "10.0.0.1:61003", "192.0.2.203:53", 64));
        At((2 * Width) + 600, 1, (at, number) => Lifecycle(at, ObservationKind.Create, 107, number));
        At((2 * Width) + 800, 0, Udp(ObservationKind.Send, 107, "10.0.0.1:61003", "192.0.2.203:53", 64));

        // A send in the first chunk that no session time places, which its tiles do not hold and a brush's bytes count by
        // its native reading.
        At(700, 0, Udp(ObservationKind.Send, 101, "10.0.0.1:61004", "192.0.2.204:53", 77), untimed: true);

        int connections = 10 + random.Next(6);
        for (int index = 0; index < connections; index++)
        {
            int client = 100 + random.Next(6);
            int server = 100 + random.Next(6);
            // Every fourth over IPv6.
            bool six = index % 4 == 3;
            string near = string.Create(CultureInfo.InvariantCulture, $"{(six ? "[fd00::1]" : "10.0.0.1")}:{50_000 - (index * 7)}");
            string far = string.Create(CultureInfo.InvariantCulture, $"{(six ? "[fd00::2]" : "10.0.0.2")}:{8_000 + random.Next(3)}");
            bool first = index == 0;
            long open = first ? Width + 100 : 300 + random.NextInt64((count * Width) - 1_000);
            long close = first ? Width + 3_000 : open + 200 + random.NextInt64(Width * 2);
            bool lateServer = !first && random.Next(4) == 0;
            At(open, 0, Tcp(ObservationKind.Connect, client, near, far));
            At(open + 1, lateServer ? 1 : 0, Tcp(ObservationKind.Accept, server, far, near));
            int transfers = 2 + random.Next(5);
            for (int transfer = 0; transfer < transfers; transfer++)
            {
                long at = open + 2 + random.NextInt64(Math.Max(1, close - open - 4));
                long bytes = 1 + random.Next(5_000);
                At(at, 0, Tcp(ObservationKind.Send, client, near, far, bytes));
                At(at + 1, lateServer ? 1 : 0, Tcp(ObservationKind.Receive, server, far, near, bytes));
            }

            if (close >= count * Width)
            {
                continue;
            }

            At(close, first ? 1 : 0, Tcp(ObservationKind.Disconnect, client, near, far));
            At(close + 1, lateServer ? 1 : 0, Tcp(ObservationKind.Disconnect, server, far, near));
            if (first)
            {
                for (int transfer = 0; transfer < 3; transfer++)
                {
                    long at = close + 10 + (transfer * 50);
                    At(at, 0, Tcp(ObservationKind.Send, client, near, far, 100));
                    At(at + 1, 0, Tcp(ObservationKind.Receive, server, far, near, 100));
                }
            }
        }

        int flows = 4 + random.Next(4);
        for (int index = 0; index < flows; index++)
        {
            int sender = 100 + random.Next(6);
            int receiver = 100 + random.Next(6);
            string from = string.Create(CultureInfo.InvariantCulture, $"10.0.0.1:{60_000 - index}");
            bool local = random.Next(3) != 0;
            string to = local
                ? string.Create(CultureInfo.InvariantCulture, $"10.0.0.3:{5_000 + index}")
                : string.Create(CultureInfo.InvariantCulture, $"192.0.2.{1 + index}:53");
            long start = random.NextInt64((count * Width) - 500);
            int datagrams = 2 + random.Next(4);
            for (int datagram = 0; datagram < datagrams; datagram++)
            {
                long at = start + (datagram * 37);
                long bytes = 40 + random.Next(500);
                At(at, 0, Udp(ObservationKind.Send, sender, from, to, bytes));
                if (local)
                {
                    // A receive names the datagram's origin first.
                    At(at + 1, 0, Udp(ObservationKind.Receive, receiver, from, to, bytes));
                }
            }
        }

        // Numbered in the order of their readings, and delivered in another: a chunk's segments are cut as records come.
        // From the fourth chunk a few records' session time goes backwards, and their segments keep no tiles.
        ulong ordinal = 0;
        var numbered = rows.OrderBy(row => row.At)
            .Select(row => (row.Chunk, Row: row.Make(row.At, ++ordinal) with
            {
                SessionRelativeTicks = row.Untimed ? null : (row.Chunk >= 3 && random.Next(25) == 0 ? row.At - 2_000 : row.At) * 100,
            }))
            .ToList();
        var chunks = new ObservationRowV1[count][];
        for (int chunk = 0; chunk < count; chunk++)
        {
            chunks[chunk] = [.. numbered.Where(row => row.Chunk == chunk).Select(row => row.Row).OrderBy(_ => random.Next())];
        }

        return chunks;
    }

    /// <summary>
    /// The chunks of a capture whose connections stay open: PIDs 100 to 103 created in the first, and in each chunk another
    /// connection between two of them, on a port below the ones before, whose end sorts first and numbers the channels
    /// before it again; every open connection carries a burst of sends one way and their receives in every chunk, so a
    /// tile of a burst keeps its tallies.
    /// </summary>
    private static ObservationRowV1[][] Steady(int count)
    {
        ulong ordinal = 0;
        long ticks = 10;
        ObservationRowV1 Row(Func<long, ulong, ObservationRowV1> make)
        {
            ticks += 10;
            ordinal++;
            return make(ticks, ordinal) with { SessionRelativeTicks = ticks * 100 };
        }

        var open = new List<(int Client, int Server, string Near, string Far)>();
        var chunks = new ObservationRowV1[count][];
        for (int chunk = 0; chunk < count; chunk++)
        {
            var rows = new List<ObservationRowV1>();
            if (chunk == 0)
            {
                for (int pid = 100; pid < 104; pid++)
                {
                    int created = pid;
                    rows.Add(Row((at, number) => Lifecycle(at, ObservationKind.Create, created, number)));
                }
            }

            (int client, int server) = (100 + (chunk % 2), 102 + (chunk % 2));
            string near = string.Create(CultureInfo.InvariantCulture, $"10.0.0.1:{40_000 - (chunk * 10)}");
            string far = string.Create(CultureInfo.InvariantCulture, $"10.0.0.2:{8_000 + chunk}");
            rows.Add(Row(Tcp(ObservationKind.Connect, client, near, far)));
            rows.Add(Row(Tcp(ObservationKind.Accept, server, far, near)));
            open.Add((client, server, near, far));
            foreach ((int sender, int receiver, string from, string to) in open)
            {
                for (int index = 0; index < 25; index++)
                {
                    rows.Add(Row(Tcp(ObservationKind.Send, sender, from, to, 100 + index)));
                }

                for (int index = 0; index < 25; index++)
                {
                    rows.Add(Row(Tcp(ObservationKind.Receive, receiver, to, from, 100 + index)));
                }
            }

            chunks[chunk] = [.. rows];
        }

        return chunks;
    }

    /// <summary>The port a connection is closed and opened again on.</summary>
    private const string Reused = "10.0.0.1:50001";

    private static Func<long, ulong, ObservationRowV1> Tcp(ObservationKind kind, int owner, string local, string remote, long bytes = 0) =>
        (at, number) => Transfer(at, kind,
                kind switch
                {
                    ObservationKind.Send => AccountingSide.SendSide,
                    ObservationKind.Receive => AccountingSide.ReceiveSide,
                    _ => AccountingSide.EndpointActivity,
                },
                bytes, owner, number)
            .Between(local, remote);

    private static Func<long, ulong, ObservationRowV1> Udp(ObservationKind kind, int owner, string first, string second, long bytes) =>
        (at, number) => Transfer(at, kind, kind == ObservationKind.Send ? AccountingSide.SendSide : AccountingSide.ReceiveSide,
                bytes, owner, number)
            .Between(first, second) with { Mechanism = Mechanism.Udp };

    /// <summary><paramref name="file"/> as though its directory stated <paramref name="derivation"/>, at <paramref name="minor"/>.</summary>
    private static TileIndexFile With(TileIndexFile file, TileDerivation derivation, ushort? minor = null) =>
        new(file.Dependency, file.DerivedGeneration, minor ?? file.Minor, derivation, [], null)
        {
            Certified = file.Certified,
            Ends = file.Ends,
        };

    private static (SegmentReaderV1[] Observations, SegmentReaderV1[] Fields) SegmentsOf(SessionStore store)
    {
        SessionManifestV1 manifest = store.Current!;
        return ([.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store, manifest, name))],
            [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store, manifest, name))]);
    }

    private static string Text(SessionByteMeasures measures) => JsonSerializer.Serialize(new
    {
        measures.Interval,
        Processes = measures.ByProcess.OrderBy(entry => entry.Key.ToString(), StringComparer.Ordinal)
            .Select(entry => new { Process = entry.Key.ToString(), entry.Value }),
        measures.Unattributed,
        Ends = measures.ByChannelEnd.OrderBy(entry => entry.Key.ToString(), StringComparer.Ordinal)
            .Select(entry => new { End = entry.Key.ToString(), entry.Value }),
    });

    private static string Text(SessionIntervalCounts counts) => JsonSerializer.Serialize(new
    {
        counts.Interval,
        Edges = counts.EdgeRecords.OrderBy(entry => entry.Key, StringComparer.Ordinal),
        Channels = counts.ChannelRecords.OrderBy(entry => entry.Key, StringComparer.Ordinal),
        counts.ObservedRows,
        counts.GraphRows,
        Processes = counts.ProcessRecords.OrderBy(entry => entry.Key.ToString(), StringComparer.Ordinal)
            .Select(entry => new { Process = entry.Key.ToString(), entry.Value }),
        counts.CaptureCoverage,
        counts.TcpCoverage,
        counts.UdpCoverage,
        counts.MechanismCoverage,
    });
}
