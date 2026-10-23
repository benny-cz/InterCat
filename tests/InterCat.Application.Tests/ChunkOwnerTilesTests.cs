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
/// tile-index-v1 §6 since minor 8: a live follow binds the owners of each chunk it tiles under the derivation of the
/// generation that tiles it, and certifies each, so a later generation's owner focus counts a chunk from its tiles wherever
/// its own instances bind the chunk's records alike, and from its rows wherever a record read later binds them otherwise
/// (§12.1 S4, P25). They clear the shared derivation cache, so they run alone.
/// </summary>
[Collection(SharedDerivationCache.Name)]
public sealed class ChunkOwnerTilesTests
{
    [Theory(DisplayName = "S4: a live generation's owner focus and byte lanes count a chunk from its tiles where its instances bind the chunk's records as the derivation that tiled it did, and from its rows where a lifecycle record read later binds them otherwise, as every segment's rows count them")]
    [InlineData(5)]
    [InlineData(29)]
    [InlineData(20_261_023)]
    public void ALiveOwnerFocusCountsAsRowsDo(int seed)
    {
        SessionDerivationCache.Clear();
        using var tiled = new TemporarySession();
        using var plain = new TemporarySession();
        var random = new Random(seed);
        ObservationRowV1[][] chunks = Churn(random, 6);
        int counted = 0;
        int refused = 0;
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
            SessionOverviewBundle bundle = SessionOverviewProjector.Project(live);
            TimeRange extent = bundle.Extent!.Value;
            long middle = extent.StartTicks + random.NextInt64(Math.Max(1, extent.SpanTicks));
            ProcessInstanceId[] instances = [.. bundle.Nodes.Select(node => node.Id)];
            foreach (TimeRange interval in new[] { extent, new TimeRange(middle, extent.EndTicks) })
            {
                foreach ((TimelineFocus focus, ProcessInstanceId[]? lanes) in Focuses(random, instances))
                {
                    Assert.Equal(
                        Text(SessionTimelineQuery.Focused(read, interval, 64, focus, lanes: lanes)),
                        Text(SessionTimelineQuery.Focused(live, interval, 64, focus, lanes: lanes)));
                }

                // A group's byte lanes, a process's byte direction rows and every mechanism's bytes, as a byte ranking
                // draws them, measure as the rows do.
                ProcessInstanceId[] group = [.. instances.OrderBy(_ => random.Next()).Take(1 + random.Next(Math.Min(4, instances.Length)))];
                ProcessInstanceId one = instances[random.Next(instances.Length)];
                Assert.Equal(
                    JsonSerializer.Serialize(SessionIntervalByteQuery.MeasureByOwner(read, interval, 32, 16, group)),
                    JsonSerializer.Serialize(SessionIntervalByteQuery.MeasureByOwner(live, interval, 32, 16, group)));
                Assert.Equal(
                    JsonSerializer.Serialize(SessionIntervalByteQuery.MeasureByDirection(read, interval, 32, one)),
                    JsonSerializer.Serialize(SessionIntervalByteQuery.MeasureByDirection(live, interval, 32, one)));
                Assert.Equal(
                    JsonSerializer.Serialize(SessionIntervalByteQuery.MeasureByMechanism(read, interval, 32, [Mechanism.Tcp, Mechanism.Udp])),
                    JsonSerializer.Serialize(SessionIntervalByteQuery.MeasureByMechanism(live, interval, 32, [Mechanism.Tcp, Mechanism.Udp])));
                Assert.Equal(
                    JsonSerializer.Serialize(SessionIntervalByteQuery.Measure(read, interval, 32, IntervalByteScope.Whole)),
                    JsonSerializer.Serialize(SessionIntervalByteQuery.Measure(live, interval, 32, IntervalByteScope.Whole)));
            }

            // What each chunk's tiles count for this generation's instances.
            SessionManifestV1 manifest = live.Current!;
            (SegmentReaderV1[] observations, SegmentReaderV1[] fields) = SegmentsOf(live);
            ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fields);
            foreach (TileIndexFile file in SessionDerivationCache.For(manifest).TiledChunks(live.Root))
            {
                if (SessionTileIndex.Rebind(file, processes) is null)
                {
                    refused++;
                }
                else
                {
                    counted++;
                }
            }

            live.ReleaseSegmentReaders();
            read.ReleaseSegmentReaders();
        }

        // PID 108, seen only by its activity until a creation read in the third chunk, is another instance from then on:
        // the chunks tiled before it are read from their rows, and those tiled after from their tiles.
        Assert.True(counted > 0, "No chunk's tiles counted an owner.");
        Assert.True(refused > 0, "No chunk's tiles were refused for a binding a record read later changed.");
    }

    [Fact(DisplayName = "S4: a live generation opened cold counts an owner focus, a group's byte lanes, a process's byte direction rows and every mechanism's bytes from its chunks' tiles, opening only its newest chunk's segments, as a session of the same chunks recorded without tiles counts them")]
    public void ALiveOwnerFocusOpensOnlyTheNewestChunk()
    {
        using var session = new TemporarySession();
        using var twin = new TemporarySession();
        // Segments of a hundred records, whose tiles of a few owners keep their tallies.
        foreach (ObservationRowV1[] chunk in Steady(4))
        {
            _ = Publish(session.Store, chunk, rowsPerSegment: 100, indexer: LiveCheckpoints.Instance);
            _ = Publish(twin.Store, chunk, rowsPerSegment: 100);
        }

        SessionDerivationCache.Clear();
        SessionStore viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        SessionOverviewBundle drawn = SessionOverviewProjector.Project(viewer);
        TimeRange extent = drawn.Extent!.Value;
        ProcessInstanceId[] group = [.. drawn.Nodes.Select(node => node.Id)];
        foreach (TimeRange interval in new[] { extent, new TimeRange(extent.StartTicks + (extent.SpanTicks / 3), extent.EndTicks) })
        {
            // At a column a few records wide, and at two, where a tile's records fall in one column and it counts whole from
            // its owner and byte tallies.
            int newest = ChunkTilesTests.Met(viewer, interval, viewer.Current!.Generation);
            foreach (int columns in new[] { 64, 2 })
            {
                foreach (Func<SessionStore, string> query in new Func<SessionStore, string>[]
                {
                    store => Text(SessionTimelineQuery.Focused(store, interval, columns, new TimelineFocus(null, group), lanes: group)),
                    store => Text(SessionTimelineQuery.Focused(store, interval, columns, new TimelineFocus(null, [group[^1]]))),
                    store => JsonSerializer.Serialize(SessionIntervalByteQuery.MeasureByOwner(store, interval, columns, 16, group)),
                    store => JsonSerializer.Serialize(SessionIntervalByteQuery.MeasureByDirection(store, interval, columns, group[^1])),
                    store => JsonSerializer.Serialize(SessionIntervalByteQuery.MeasureByMechanism(store, interval, columns, [Mechanism.Tcp, Mechanism.Udp])),
                    store => JsonSerializer.Serialize(SessionIntervalByteQuery.Measure(store, interval, columns, IntervalByteScope.Whole)),
                })
                {
                    viewer.ReleaseSegmentReaders();
                    string counted = query(viewer);
                    Assert.Equal(newest, viewer.SegmentReaderCache.Entries);
                    Assert.Equal(query(twin.Store), counted);
                }
            }
        }

        viewer.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "S4: a chunk's tiles bind its owners under the derivation that tiled it and certify each, once, in order: an instance by identity, PID and the native readings of its records, a lifecycle record's apart, bound directly, and a record bound to none by its PID")]
    public void AChunksTilesCertifyTheirOwners()
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        ObservationRowV1[][] chunks = Churn(new Random(11), 3);
        foreach (ObservationRowV1[] chunk in chunks)
        {
            _ = Publish(session.Store, chunk, rowsPerSegment: 7, indexer: LiveCheckpoints.Instance);
        }

        SessionManifestV1 manifest = session.Store.Current!;
        foreach (StoreDependency dependency in ChunkTiles.NamedBy(manifest))
        {
            TileIndexFile file = SessionTileIndex.Open(session.Store.Root, dependency, manifest.SessionId);
            long chunk = ChunkTileFiles.SegmentsGenerationOf(dependency.Name)!.Value;

            // The derivation that tiled the chunk: the generation's own, whose segments those of the chunk and the ones before.
            string[] names = [.. SessionSegments.Names(manifest).Where(name => SessionSegments.PublishingGeneration(name) <= chunk)];
            SegmentReaderV1[] observations = [.. names.Select(name => SessionSegments.Open(session.Store, manifest, name))];
            ProcessInstanceIndex tiling = ProcessInstanceIndex.Derive(observations, TestClock, []);
            Assert.Equal(
                (ProcessInstanceIndex.BindingRule, SessionTileIndex.ChunkRelationRule, tiling.Instances.Count, file.Ends.Count),
                (file.Derivation.BindingRule, file.Derivation.RelationRule, file.Derivation.Instances, file.Derivation.Channels));
            Assert.Equal(SessionTileIndex.ChunkDerivation(tiling, file.Ends).Fingerprint, file.Derivation.Fingerprint);
            Assert.NotEmpty(file.Certified);
            Assert.Equal(file.Certified.Order(), file.Certified);
            Assert.Equal(file.Certified.Count, file.Certified.Select(owner => (owner.Key, owner.ProcessId, owner.Lifecycle)).Distinct().Count());

            // What each certifies is what the chunk's own rows bind to.
            var expected = new Dictionary<(uint, int, bool), (long First, long Last)>();
            foreach (string name in names.Where(name => SessionSegments.PublishingGeneration(name) == chunk))
            {
                SegmentReaderV1 segment = SessionSegments.Open(session.Store, manifest, name);
                ProcessBinding[] owners = tiling.OwnersOf(segment);
                int[] processIds = new int[segment.RowCount];
                bool[] lifecycle = new bool[segment.RowCount];
                ProcessInstanceIndex.OwnerProcessesOf(segment, processIds, lifecycle);
                SegmentColumnSlice times = segment.Slice(SegmentColumnId.SessionRelativeTicks);
                SegmentColumnSlice readings = segment.Slice(SegmentColumnId.NativeTicks);
                for (int row = 0; row < segment.RowCount; row++)
                {
                    if (processIds[row] < 0 || times.SignedAt(row) is null)
                    {
                        continue;
                    }

                    uint key = owners[row].IsBound ? ((uint)(owners[row].Instance + 1) << 3) | (uint)owners[row].Strength : 0;
                    long reading = readings.SignedAt(row)!.Value;
                    (uint, int, bool) owner = (key, processIds[row], key != 0 && lifecycle[row]);
                    expected[owner] = expected.TryGetValue(owner, out (long First, long Last) seen)
                        ? (Math.Min(seen.First, reading), Math.Max(seen.Last, reading))
                        : (reading, reading);
                }
            }

            Assert.Equal(expected.Count, file.Certified.Count);
            foreach (OwnerCertificate owner in file.Certified)
            {
                (long first, long last) = expected[(owner.Key, owner.ProcessId, owner.Lifecycle)];
                if (owner.Key == 0)
                {
                    Assert.Equal((default(ProcessInstanceId), first, last), (owner.Instance, owner.First, owner.Last));
                    continue;
                }

                ProcessInstance instance = tiling.Instances[(int)(owner.Key >> 3) - 1];
                Assert.Equal((instance.Id, instance.ProcessId, first, last), (owner.Instance, owner.ProcessId, owner.First, owner.Last));
                Assert.True(!owner.Lifecycle || (RelationStrength)(owner.Key & 7) == RelationStrength.Direct);
            }
        }
    }

    [Fact(DisplayName = "S4: a chunk's certified owners count only for instances that bind its records alike: a record bound to none bound to none still, and an instance found by identity, of the same PID, binding every reading of its records as strongly, a lifecycle record's directly")]
    public void ACertificateHoldsOnlyWhereEveryOwnerBindsAlike()
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        foreach (ObservationRowV1[] chunk in Certifiable())
        {
            _ = Publish(session.Store, chunk, rowsPerSegment: 50, indexer: LiveCheckpoints.Instance);
        }

        SessionManifestV1 manifest = session.Store.Current!;
        TileIndexFile file = SessionTileIndex.Open(session.Store.Root, Assert.Single(ChunkTiles.NamedBy(manifest)), manifest.SessionId);
        (SegmentReaderV1[] observations, SegmentReaderV1[] fields) = SegmentsOf(session.Store);
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fields);
        int[] places = Assert.IsType<int[]>(SessionTileIndex.Rebind(file, processes));
        foreach (OwnerCertificate owner in file.Certified.Where(owner => owner.Key != 0))
        {
            Assert.Equal(owner.Instance, processes.Instances[places[(int)(owner.Key >> 3) - 1]].Id);
        }

        // Each part of the certificate a derivation must hold, broken alone, refuses the chunk's tiles.
        // An instance of a creation the chunk holds, whose lifetime starts there, and records bound to none.
        OwnerCertificate created = file.Certified.First(owner => owner.Lifecycle);
        OwnerCertificate bound = file.Certified.First(owner => !owner.Lifecycle && owner.Key == created.Key - (uint)RelationStrength.Direct + (uint)RelationStrength.Correlated);
        OwnerCertificate none = file.Certified.First(owner => owner.Key == 0);
        OwnerCertificate other = file.Certified.First(owner => owner.Key >> 3 != bound.Key >> 3 && owner.Key != 0);
        OwnerCertificate lone = Assert.Single(file.Certified, owner => owner.ProcessId == 105);
        foreach ((OwnerCertificate original, OwnerCertificate changed) in new[]
        {
            (bound, bound with { Instance = ProcessInstanceId.New() }),
            (bound, bound with { ProcessId = bound.ProcessId + 1 }),
            (bound, bound with { Key = (bound.Key & ~7u) | (uint)RelationStrength.Candidate }),
            (bound, bound with { First = long.MinValue }),
            (bound, bound with { First = bound.Last + 1 }),

            // Its key names one instance and its identity another, which binds its PID's records alike: the key's
            // instance is then two.
            (bound, bound with { Instance = other.Instance, ProcessId = other.ProcessId, First = other.First, Last = other.Last }),

            // Its identity names another PID's instance, which its records do not bind to.
            (lone, lone with { Instance = bound.Instance }),
            (created, created with { Instance = ProcessInstanceId.New() }),
            (created, created with { First = long.MinValue }),
            (created, created with { Key = (created.Key & ~7u) | (uint)RelationStrength.Correlated }),
            (none, none with { First = long.MinValue + 1, Last = long.MaxValue - 1 }),
            (none, none with { ProcessId = 99_999 }),
            (none, none with { ProcessId = bound.ProcessId }),
        })
        {
            Assert.Null(SessionTileIndex.Rebind(With(file, original, changed), processes));
        }

        // A file of another rule counts no owner for a derivation it does not state.
        Assert.Null(SessionTileIndex.Rebind(With(file, derivation: file.Derivation with { RelationRule = TransportRelationIndex.RelationRule }), processes));
        Assert.Null(SessionTileIndex.Rebind(With(file, derivation: file.Derivation with { BindingRule = "process-binding-v3" }), processes));

        // An owner key its directory does not certify is refused as it is read, whether a tally's or a record's.
        int[] uncertified = [.. places];
        uncertified[(int)(bound.Key >> 3) - 1] = -1;
        using TileReading reading = file.Read(session.Store.Root);
        reading.Rebound = uncertified;
        TileSection section = reading.Section(Assert.Single(SessionOverviewIndex.ObservationSegments(manifest), file.Describes).Name);
        var focused = new TimelineColumns(new TimeRange(section.First!.Value, section.Last!.Value + 1), 4, tallyMechanisms: true);
        bool[] members = [.. processes.Instances.Select(_ => true)];
        Assert.Contains("an owner its directory does not certify", Assert.Throws<InvalidDataException>(() => reading.CountGroup(
            section, focused, null, null, members, null, null, null, EvidencePolicy.IncludeCandidates, CancellationToken.None)).Message,
            StringComparison.Ordinal);
    }

    [Theory(DisplayName = "S4: a chunk's tiles whose directory certifies its owners out of order, twice, more or fewer than it holds, or as no derivation of its instances binds them are refused as data")]
    [InlineData(0, "its directory does not certify its owners once each, in order.")]
    [InlineData(1, "its directory does not certify its owners once each, in order.")]
    [InlineData(2, "its directory does not certify its owners once each, in order.")]
    [InlineData(3, "its directory does not certify its owners once each, in order.")]
    [InlineData(4, "its directory does not certify its owners once each, in order.")]
    [InlineData(5, "its directory does not certify its owners once each, in order.")]
    [InlineData(6, "its directory does not certify its owners once each, in order.")]
    [InlineData(7, "its directory does not certify its owners once each, in order.")]
    [InlineData(8, "its directory does not certify its owners once each, in order.")]
    [InlineData(9, "its directory states more certified owners than it holds.")]
    [InlineData(10, "its directory states more certified owners than it holds.")]
    public void AMalformedCertificateIsRefused(int damage, string why)
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        foreach (ObservationRowV1[] chunk in Certifiable())
        {
            _ = Publish(session.Store, chunk, rowsPerSegment: 50, indexer: LiveCheckpoints.Instance);
        }

        SessionManifestV1 manifest = session.Store.Current!;
        StoreDependency named = Assert.Single(ChunkTiles.NamedBy(manifest));
        TileIndexFile file = SessionTileIndex.Open(session.Store.Root, named, manifest.SessionId);
        string path = Path.Combine(session.Path, named.Name);
        byte[] bytes = File.ReadAllBytes(path);
        ReadOnlySpan<byte> footer = bytes.AsSpan(bytes.Length - SessionTileIndex.FooterLength);
        int directory = (int)BinaryPrimitives.ReadInt64LittleEndian(footer);
        int listed = BinaryPrimitives.ReadInt32LittleEndian(footer[8..]);

        // The certificates follow their count, and the ends' spans follow them: the first bound to none, the last an
        // instance's records.
        int count = file.Certified.Count;
        int at = directory + listed - 4 - (file.Ends.Count * SessionTileIndex.EndSpanBytes) - (count * OwnerCertificate.Bytes);
        int last = count - 1;
        Assert.Equal(count, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at - 4)));
        Assert.Equal(0u, file.Certified[0].Key);
        Assert.False(file.Certified[last].Lifecycle || file.Certified[last].Key == 0);
        Span<byte> Entry(int index) => bytes.AsSpan(at + (index * OwnerCertificate.Bytes), OwnerCertificate.Bytes);
        switch (damage)
        {
            case 0:
                // The first two out of order.
                byte[] first = Entry(0).ToArray();
                Entry(1).CopyTo(Entry(0));
                first.CopyTo(Entry(1));
                break;
            case 1:
                // The first twice.
                Entry(0).CopyTo(Entry(1));
                break;
            case 2:
                // A PID below 0.
                BinaryPrimitives.WriteInt32LittleEndian(Entry(0)[4..], -1);
                break;
            case 3:
                // A first reading after the last.
                BinaryPrimitives.WriteInt64LittleEndian(Entry(last)[25..], file.Certified[last].Last + 1);
                break;
            case 4:
                // Records bound to none, of an instance.
                Entry(last)[8..24].CopyTo(Entry(0)[8..24]);
                break;
            case 5:
                // Records bound to none, said to be lifecycle records.
                Entry(0)[24] = 1;
                break;
            case 6:
                // A key beyond the directory's instances.
                BinaryPrimitives.WriteUInt32LittleEndian(Entry(last), ((uint)file.Derivation.Instances + 1) << 3 | (uint)RelationStrength.Correlated);
                break;
            case 7:
                // A key of a strength §23 does not define.
                BinaryPrimitives.WriteUInt32LittleEndian(Entry(last), file.Certified[last].Key | 7);
                break;
            case 8:
                // Lifecycle records not bound directly.
                Entry(last)[24] = 1;
                break;
            case 9:
                // More than it holds, the ends' spans after them too.
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at - 4), count + 100);
                break;
            default:
                // Fewer than none.
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at - 4), -1);
                break;
        }

        Crc32C.Write(bytes.AsSpan(bytes.Length - SessionTileIndex.FooterLength + 16), Crc32C.Compute(bytes.AsSpan(directory, listed)));
        Crc32C.Write(bytes.AsSpan(bytes.Length - 4), Crc32C.Compute(bytes.AsSpan(bytes.Length - SessionTileIndex.FooterLength, SessionTileIndex.FooterLength - 4)));
        File.WriteAllBytes(path, bytes);
        Assert.EndsWith(why, Assert.Throws<InvalidDataException>(() => SessionTileIndex.Open(session.Store.Root, named, manifest.SessionId)).Message,
            StringComparison.Ordinal);
    }

    [Theory(DisplayName = "S4: a lifecycle record read late that ends an instance among a chunk's records refuses the chunk's tiles, to the reading: one ending it before the chunk's last record of it does, as does one whose PID's next instance begins there, and one ending it at that record does not")]
    [InlineData(-1, null, false)]
    [InlineData(0, null, true)]
    [InlineData(-111, -110, false)]
    public void ALateExitRefusesTheTilesOfTheRecordsItSplits(int exitFromLast, int? createdFromLast, bool holds)
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        ObservationRowV1 Send(long at, int owner, ulong number) =>
            Transfer(at, ObservationKind.Send, AccountingSide.SendSide, 100, owner, number).Between("10.0.0.1:40000", "10.0.0.5:443")
                with { SessionRelativeTicks = at * 100 };

        // PID 100 created and sending three times; its exit, and where one is the creation of its next instance, are read
        // only in the next chunk, at readings of the first.
        ObservationRowV1[] first =
        [
            Lifecycle(110, ObservationKind.Create, 100, 1) with { SessionRelativeTicks = 110 * 100 },
            Send(210, 100, 2), Send(310, 105, 3), Send(410, 100, 4), Send(510, 100, 5),
        ];
        ObservationRowV1[] second =
        [
            Lifecycle(510 + exitFromLast, ObservationKind.Exit, 100, 6) with { SessionRelativeTicks = (510 + exitFromLast) * 100 },
            .. createdFromLast is { } created
                ? [Lifecycle(510 + created, ObservationKind.Create, 100, 7) with { SessionRelativeTicks = (510 + created) * 100 }]
                : Array.Empty<ObservationRowV1>(),
            Send(710, 105, 8),
        ];
        _ = Publish(session.Store, first, rowsPerSegment: 50, indexer: LiveCheckpoints.Instance);
        _ = Publish(session.Store, second, rowsPerSegment: 50, indexer: LiveCheckpoints.Instance);

        SessionManifestV1 manifest = session.Store.Current!;
        TileIndexFile file = SessionTileIndex.Open(session.Store.Root, Assert.Single(ChunkTiles.NamedBy(manifest)), manifest.SessionId);
        (SegmentReaderV1[] observations, SegmentReaderV1[] fields) = SegmentsOf(session.Store);
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(observations, TestClock, fields);
        OwnerCertificate sent = Assert.Single(file.Certified, owner => owner.ProcessId == 100 && !owner.Lifecycle);
        Assert.Equal((210L, 510L), (sent.First, sent.Last));
        Assert.Equal(holds, SessionTileIndex.Rebind(file, processes) is not null);
    }

    /// <summary>
    /// Two chunks a later derivation binds alike: in the first, PID 100 created and sending, PID 101 sending once before its
    /// creation and after it, and PID 105 seen only by its records; in the second, each sending again.
    /// </summary>
    private static ObservationRowV1[][] Certifiable()
    {
        ulong ordinal = 0;
        long ticks = 10;
        ObservationRowV1 Row(Func<long, ulong, ObservationRowV1> make)
        {
            ticks += 100;
            ordinal++;
            return make(ticks, ordinal) with { SessionRelativeTicks = ticks * 100 };
        }

        ObservationRowV1 Send(int owner) => Row((at, number) =>
            Transfer(at, ObservationKind.Send, AccountingSide.SendSide, 100, owner, number).Between("10.0.0.1:40000", "10.0.0.5:443"));

        ObservationRowV1[] first =
        [
            Row((at, number) => Lifecycle(at, ObservationKind.Create, 100, number)), Send(100), Send(101), Send(105),
            Row((at, number) => Lifecycle(at, ObservationKind.Create, 101, number)), Send(101), Send(100), Send(105),
        ];
        return [first, [Send(100), Send(101), Send(105)]];
    }

    /// <summary>
    /// The chunks of a capture whose processes stay: PIDs 100 and 101 created in the first and 105 seen only by its records,
    /// each sending and receiving over TCP and UDP in bursts in every chunk, so every later derivation binds each chunk's
    /// records alike; and in each later chunk a PID below them all first seen there, which places every instance after it
    /// one later.
    /// </summary>
    private static ObservationRowV1[][] Steady(int count)
    {
        ulong ordinal = 0;
        long ticks = 10;
        ObservationRowV1 Row(Func<long, ulong, ObservationRowV1> make)
        {
            ticks += 100;
            ordinal++;
            return make(ticks, ordinal) with { SessionRelativeTicks = ticks * 100 };
        }

        var chunks = new ObservationRowV1[count][];
        for (int chunk = 0; chunk < count; chunk++)
        {
            var rows = new List<ObservationRowV1>();
            if (chunk == 0)
            {
                rows.Add(Row((at, number) => Lifecycle(at, ObservationKind.Create, 100, number)));
                rows.Add(Row((at, number) => Lifecycle(at, ObservationKind.Create, 101, number)));
            }

            // In bursts of one process's records over one mechanism one way, as a transfer's come, so a tile of a burst keeps
            // its tallies.
            for (int index = 0; index < 300; index++)
            {
                int burst = index / 25;
                int owner = (burst % 4) switch { 0 => 100, 1 => 101, 2 => 105, _ => 100 - chunk };
                bool sent = burst / 4 % 2 == 0;
                rows.Add(Row((at, number) => Transfer(at, sent ? ObservationKind.Send : ObservationKind.Receive,
                        sent ? AccountingSide.SendSide : AccountingSide.ReceiveSide, 100 + index, owner, number)
                    .Between("10.0.0.1:40000", "10.0.0.5:443") with { Mechanism = burst % 3 == 0 ? Mechanism.Udp : Mechanism.Tcp }));
            }

            chunks[chunk] = [.. rows];
        }

        return chunks;
    }

    /// <summary>
    /// <paramref name="file"/> as though its directory certified <paramref name="changed"/> in place of
    /// <paramref name="original"/>, or stated <paramref name="derivation"/>.
    /// </summary>
    private static TileIndexFile With(
        TileIndexFile file, OwnerCertificate? original = null, OwnerCertificate? changed = null, TileDerivation? derivation = null) =>
        new(file.Dependency, file.DerivedGeneration, file.Minor, derivation ?? file.Derivation, [], null)
        {
            Certified = [.. file.Certified.Select(owner => owner == original ? changed!.Value : owner)],
        };

    /// <summary>
    /// A group in lanes of its own, one process with its direction rows, one of its mechanisms alone and a group's outbound
    /// records alone, each of instances drawn from <paramref name="instances"/>.
    /// </summary>
    private static IEnumerable<(TimelineFocus Focus, ProcessInstanceId[]? Lanes)> Focuses(Random random, ProcessInstanceId[] instances)
    {
        ProcessInstanceId[] group = [.. instances.OrderBy(_ => random.Next()).Take(1 + random.Next(Math.Min(4, instances.Length)))];
        ProcessInstanceId one = instances[random.Next(instances.Length)];
        yield return (new TimelineFocus(null, group), group);
        yield return (new TimelineFocus(null, [one]), null);
        yield return (new TimelineFocus(null, [one], mechanism: Mechanism.Tcp), null);
        yield return (new TimelineFocus(null, group, direction: Direction.Outbound), null);
    }

    /// <summary>
    /// The chunks of a capture whose processes come and go: PIDs 100 to 104 created in the first, 105 to 109 seen only by
    /// their records; 102 exiting in the second and created again in the third, whose records between bind to no instance;
    /// and 108 created in the third, after records of it the chunks before held, which then bind to none. Transfers over TCP
    /// and UDP among them all, inbound and outbound, a few naming no process, a few with no session time, a few read late,
    /// and from the fourth chunk a few whose session time goes backwards, whose segments keep no tiles.
    /// </summary>
    private static ObservationRowV1[][] Churn(Random random, int count)
    {
        var chunks = new ObservationRowV1[count][];
        ulong ordinal = 0;
        long ticks = 10;
        for (int chunk = 0; chunk < count; chunk++)
        {
            var rows = new List<ObservationRowV1>();
            void Add(Func<long, ulong, ObservationRowV1> make, bool untimed = false, bool late = false, bool backwards = false)
            {
                ticks += 1 + random.Next(1_000);
                ordinal++;

                // A record read late comes after one read later than it, as a buffer delivered late brings it, and its chunk
                // holds readings of the one before; one whose session time goes backwards is placed before the record ahead
                // of it, and its segment keeps no tiles.
                long at = late ? ticks - 1_001 : ticks;
                rows.Add(make(at, ordinal) with { SessionRelativeTicks = untimed ? null : (backwards ? at - 2_000 : at) * 100 });
            }

            if (chunk == 0)
            {
                for (int pid = 100; pid < 105; pid++)
                {
                    int created = pid;
                    Add((at, number) => Lifecycle(at, ObservationKind.Create, created, number));
                }
            }

            int transfers = 12 + random.Next(30);
            for (int index = 0; index < transfers; index++)
            {
                if (chunk == 1 && index == transfers / 3)
                {
                    Add((at, number) => Lifecycle(at, ObservationKind.Exit, 102, number));
                }

                if (chunk == 2 && index == transfers / 2)
                {
                    Add((at, number) => Lifecycle(at, ObservationKind.Create, 102, number));
                    Add((at, number) => Lifecycle(at, ObservationKind.Create, 108, number));
                }

                int? owner = random.Next(20) == 0 ? null : 100 + random.Next(10);
                ObservationKind kind = random.Next(2) == 0 ? ObservationKind.Send : ObservationKind.Receive;
                Mechanism mechanism = random.Next(3) == 0 ? Mechanism.Udp : Mechanism.Tcp;
                Add((at, number) => Transfer(at, kind, kind == ObservationKind.Send ? AccountingSide.SendSide : AccountingSide.ReceiveSide,
                            random.Next(4) == 0 ? null : random.Next(10_000), owner, number)
                        .Between(
                            string.Create(CultureInfo.InvariantCulture, $"10.0.0.{1 + random.Next(4)}:{40_000 + random.Next(3)}"),
                            string.Create(CultureInfo.InvariantCulture, $"10.0.0.{5 + random.Next(3)}:443"))
                        with { Mechanism = mechanism },
                    untimed: random.Next(12) == 0, late: ticks > 2_000 && random.Next(15) == 0,
                    backwards: chunk >= 3 && random.Next(20) == 0);
            }

            chunks[chunk] = [.. rows];
        }

        return chunks;
    }

    private static (SegmentReaderV1[] Observations, SegmentReaderV1[] Fields) SegmentsOf(SessionStore store)
    {
        SessionManifestV1 manifest = store.Current!;
        return ([.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store, manifest, name))],
            [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store, manifest, name))]);
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
