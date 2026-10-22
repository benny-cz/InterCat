using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// §10.2, R2: bytes records state that their measurement refuses to sum - sizes adding up past what a sum holds, or a unit
/// other than bytes - are refused, with why, by every byte a view reads, live and finished: never wrapped into a smaller
/// total, summed in another unit, or blamed on an index. Everything else the session holds reads as before, and a finished
/// session publishes its checkpoint all the same, keeping no bytes of them. A tile holding more byte entries than a read
/// first keeps room for is read whole.
/// </summary>
public sealed partial class SessionTileIndexTests
{
    /// <summary>A size no capture records, two of which add up past what a sum holds.</summary>
    private const long Oversized = 5_000_000_000_000_000_000;

    [Fact(DisplayName = "§10.2: sizes adding up past what a sum holds are refused with why by every byte read, live and finished, the rest of the session read as before, and the checkpoint published keeping no bytes")]
    public void SizesPastASumAreRefusedByEveryByteRead()
    {
        ObservationRowV1[] rows = Oversize(Exchanges(40, firstDatagram: 1_000), [2, 80]);
        TimeRange whole = Extent(rows);

        // While it is recorded: every byte read refuses - one that adds both sends up in one total naming the row that took
        // it past a sum - and the processes, their relationship and their records' counts are as they are.
        using var live = new TemporarySession();
        Publish(live.Store, rows, rowsPerSegment: 1_000);
        foreach ((string read, Action measure) in ByteReads(live.Store, whole))
        {
            string refused = Unsummable(read, measure);
            Assert.Contains(read is "whole-session ranking" or "brush"
                ? "Row 80 measures 5000000000000000000 bytes, which takes its side's total past 9,223,372,036,854,775,807"
                : SegmentMeasurement.PastASum, refused, StringComparison.Ordinal);
        }

        SessionOverviewBundle shown = SessionOverviewProjector.Project(live.Store);
        Assert.Equal(2, shown.Nodes.Count);
        Assert.Single(shown.Edges);

        // Finished: the checkpoint is published, keeping no bytes in its overview or its segment's section, and a reopened
        // session refuses as the live one did, without setting its tile index aside.
        using var finished = new TemporarySession();
        Publish(finished.Store, rows, rowsPerSegment: 1_000);
        Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(finished.Store, Committed).Outcome);
        finished.Store.ReleaseSegmentReaders();
        SessionDerivationCache.Clear();
        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(finished.Path));
        SessionDerivation derivation = SessionDerivationCache.For(reopened.Current!);
        OverviewCounts kept = derivation.PersistedOverview(reopened.Root)!;
        Assert.Null(kept.LaneBytes);
        Assert.Null(kept.ProcessBytes);
        using (TileReading reading = FileOf(finished).Read(finished.Store.Root))
        {
            Assert.False(reading.Section(SessionSegments.Names(reopened.Current!)[0]).KeepsBytes);
        }

        foreach ((string read, Action measure) in ByteReads(reopened, whole))
        {
            Assert.Contains("more than a sum holds", Unsummable(read, measure), StringComparison.Ordinal);
        }

        Assert.Equal(2, SessionOverviewProjector.Project(reopened).Nodes.Count);
        Assert.Null(derivation.TilesProblem);
        Assert.Null(derivation.CheckpointProblem);
        reopened.ReleaseSegmentReaders();
        live.Store.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "§10.2: segments whose own sizes fit but add up past what a sum holds keep their sums, a level spanning them keeps none, and only a read across them is refused")]
    public void SizesPastASumOnlyAcrossSegmentsAreRefusedAcrossThem()
    {
        // Each of the two segments holds one oversized send, so each one's sides fit and only the two together do not.
        ObservationRowV1[] rows = Oversize(Exchanges(40, firstDatagram: 1_000), [2, 80]);
        TimeRange whole = Extent(rows);
        long middle = rows[42].SessionRelativeTicks!.Value / 100;
        var first = new TimeRange(whole.StartTicks, middle);
        using var twin = new TemporarySession();
        Publish(twin.Store, rows, rowsPerSegment: 41);
        using TemporarySession session = Published(rows, rowsPerSegment: 41);
        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        SessionDerivation derivation = SessionDerivationCache.For(reopened.Current!);
        Assert.Null(derivation.PersistedOverview(reopened.Root)!.ProcessBytes);
        using (TileReading reading = FileOf(session).Read(session.Store.Root))
        {
            Assert.Equal([true, true], SessionSegments.Names(reopened.Current!).Select(name => reading.Section(name).KeepsBytes));
            Assert.True(reading.Spanning().Spanning);
            Assert.False(reading.Spanning().KeepsBytes);
            Assert.False(reading.Spanning().KeepsByteTallies);
        }

        foreach (SessionStore store in new[] { twin.Store, reopened })
        {
            foreach ((string read, Action measure) in ByteReads(store, whole))
            {
                Assert.Equal(SegmentMeasurement.PastASum, Unsummable(read, measure));
            }
        }

        // Read in one column, each read adds both up into one total while it reads the index, and refuses as it does.
        foreach (SessionStore store in new[] { twin.Store, reopened })
        {
            ProcessInstanceId owner = Bind(store).Processes.Instances.Single(instance => instance.ProcessId == 100).Id;
            Assert.Equal(SegmentMeasurement.PastASum,
                Unsummable("mechanism lanes", () => SessionIntervalByteQuery.MeasureByMechanism(store, whole, 1, Kinds)));
            Assert.Equal(SegmentMeasurement.PastASum,
                Unsummable("process lanes", () => SessionIntervalByteQuery.MeasureByOwner(store, whole, 1, 1, [owner])));
            Assert.Equal(SegmentMeasurement.PastASum,
                Unsummable("direction rows", () => SessionIntervalByteQuery.MeasureByDirection(store, whole, 1, owner)));
        }

        // What lies in one segment fits, and measures the same from the tiles as from the rows.
        ProcessInstanceId sender = Bind(twin.Store).Processes.Instances.Single(instance => instance.ProcessId == 100).Id;
        Assert.Equal(Oversized + (19 * 64), SessionByteRanking.Measure(reopened, first).ByProcess[sender].SentBytes);
        Assert.Equal(Brushed(twin.Store, first, EvidencePolicy.IncludeCorrelated), Brushed(reopened, first, EvidencePolicy.IncludeCorrelated));
        Assert.Equal(LaneBytes(twin.Store, first, 16), LaneBytes(reopened, first, 16));
        Assert.Null(derivation.TilesProblem);
        reopened.ReleaseSegmentReaders();
        twin.Store.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "§10.2: sizes each process's own sums hold but the session's total does not are refused, so no sum a view takes of what a read answers passes it")]
    public void SizesPastASumOnlyAmongProcessesAreRefused()
    {
        // Process 100 sends one size no capture records and process 200 receives another: each process's sides fit, and so
        // does each column, but a view adding up the machine's processes, or its columns, would pass a sum.
        ObservationRowV1[] rows = Oversize(Exchanges(40, firstDatagram: 1_000), [2, 81]);
        TimeRange whole = Extent(rows);
        using var live = new TemporarySession();
        Publish(live.Store, rows, rowsPerSegment: 1_000);
        using TemporarySession finished = Published(rows, rowsPerSegment: 1_000);
        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(finished.Path));
        Assert.NotNull(SessionDerivationCache.For(reopened.Current!).PersistedOverview(reopened.Root)!.ProcessBytes);
        foreach (SessionStore store in new[] { live.Store, reopened })
        {
            foreach ((string read, Action measure) in ByteReads(store, whole))
            {
                Assert.Equal(SegmentMeasurement.PastASum, Unsummable(read, measure));
            }
        }

        Assert.Null(SessionDerivationCache.For(reopened.Current!).TilesProblem);
        reopened.ReleaseSegmentReaders();
        live.Store.ReleaseSegmentReaders();

        // Each process sends one to another host, which no channel's end holds: only the processes' total passes a sum.
        ObservationRowV1[] reaching =
        [
            Lifecycle(1, ObservationKind.Create, 100, 1) with { SessionRelativeTicks = 100 },
            Lifecycle(2, ObservationKind.Create, 200, 2) with { SessionRelativeTicks = 200 },
            Transfer(1_000, ObservationKind.Send, AccountingSide.SendSide, Oversized, 100, 3)
                .Between("192.168.1.5:52000", "10.0.0.9:443") with { SessionRelativeTicks = 100_000 },
            Transfer(2_000, ObservationKind.Send, AccountingSide.SendSide, Oversized, 200, 4)
                .Between("192.168.1.5:52001", "10.0.0.8:443") with { SessionRelativeTicks = 200_000 },
        ];
        using TemporarySession hosts = Published(reaching);
        SessionStore reached = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(hosts.Path));
        Assert.NotNull(SessionDerivationCache.For(reached.Current!).PersistedOverview(reached.Root)!.ProcessBytes);
        Assert.Equal(SegmentMeasurement.PastASum, Unsummable("whole-session ranking", () => SessionByteRanking.Measure(reached, null)));
        reached.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "§10.2: a level spanning the segments keeps the sums of finest tiles that each fit, and none once made wider than a sum holds; every read across them is refused")]
    public void SizesPastASumAcrossSpanningTilesAreRefused()
    {
        // Thirteen hundred exchanges over two segments, an oversized send in each: the level spanning them has finest tiles
        // a million ticks wide, the two sends in different ones, each of whose sums fit.
        ObservationRowV1[] rows = Oversize(Exchanges(1_300, firstDatagram: 100_000), [2, 2_600]);
        TimeRange whole = Extent(rows);
        using TemporarySession session = Published(rows, rowsPerSegment: 1_301);
        Assert.Equal((1_000_000L, true, true), Spanned(session));
        Refused(session);

        // Allowed fewer entries than the two segments' finest tiles hold together, though not than the first's alone, the
        // level makes them ten times wider once both are in, adding the two up: it then keeps none of their bytes, which
        // the sections beneath keep.
        SessionManifestV1 published = session.Store.Current!;
        Bound bound = Bind(session.Store);
        Republish(session, tiles: content => SessionTileIndex.Write(content, published.SessionId, published.Generation,
            [.. SessionOverviewIndex.ObservationSegments(published).Zip(bound.Segments)], bound.Processes, bound.Relations,
            CancellationToken.None, spanningEntries: 16));
        Assert.Equal((10_000_000L, false, false), Spanned(session));
        Refused(session);

        static (long Width, bool Bytes, bool Tallies) Spanned(TemporarySession session)
        {
            using TileReading reading = FileOf(session).Read(session.Store.Root);
            Assert.Equal([true, true], SessionSegments.Names(session.Store.Current!).Select(name => reading.Section(name).KeepsBytes));
            TileSection spanning = reading.Spanning();
            return (spanning.Width, spanning.KeepsBytes, spanning.KeepsByteTallies);
        }

        void Refused(TemporarySession session)
        {
            SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
            foreach ((string read, Action measure) in ByteReads(reopened, whole))
            {
                Assert.Equal(SegmentMeasurement.PastASum, Unsummable(read, measure));
            }

            Assert.Null(SessionDerivationCache.For(reopened.Current!).TilesProblem);
            reopened.ReleaseSegmentReaders();
        }
    }

    [Fact(DisplayName = "§10.2: transport bytes measured in another unit are summed by no byte view and kept by no section or overview, and the checkpoint is published all the same")]
    public void BytesInAnotherUnitAreRefused()
    {
        // Every transfer measured in counts: a byte view converts nothing into bytes.
        ObservationRowV1[] counted = [.. Exchanges(40).Select(row => row.ByteValue is null ? row : row with { MeasurementUnit = MeasurementUnit.Count })];
        TimeRange whole = Extent(counted);
        using var live = new TemporarySession();
        Publish(live.Store, counted, rowsPerSegment: 1_000);
        const string InCounts = "A segment measures its transport bytes in Count. A byte view sums bytes, and does not convert another "
            + "unit into them.";
        foreach ((string read, Action measure) in ByteReads(live.Store, whole))
        {
            Assert.Equal(InCounts, Unsummable(read, measure));
        }

        using TemporarySession finished = Published(counted, rowsPerSegment: 1_000);
        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(finished.Path));
        using (TileReading reading = FileOf(finished).Read(finished.Store.Root))
        {
            Assert.False(reading.Section(SessionSegments.Names(reopened.Current!)[0]).KeepsBytes);
        }

        Assert.Equal(InCounts, Unsummable("brush", () => SessionByteRanking.Measure(reopened, whole)));
        Assert.Null(SessionDerivationCache.For(reopened.Current!).TilesProblem);
        reopened.ReleaseSegmentReaders();

        // One transfer among the rest in counts: the checkpoint, which published nothing for it before, keeps no bytes.
        ObservationRowV1[] odd = [.. Exchanges(40).Select((row, index) => index == 7 ? row with { MeasurementUnit = MeasurementUnit.Count } : row)];
        using TemporarySession published = Published(odd, rowsPerSegment: 41);
        SessionStore later = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(published.Path));
        Assert.Null(SessionDerivationCache.For(later.Current!).PersistedOverview(later.Root)!.LaneBytes);
        Assert.Equal("Row 7 measures in Count where earlier rows measure in Bytes. Two units are not summed and are not converted "
            + "into one another here.", Unsummable("brush", () => SessionByteRanking.Measure(later, Extent(odd))));
        Assert.Null(SessionDerivationCache.For(later.Current!).TilesProblem);
        later.ReleaseSegmentReaders();
        live.Store.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "I4: a tile holding more byte tallies and sums than a read first keeps room for is read whole, as its rows measure")]
    public void ATileOfManyByteEntriesIsReadWhole()
    {
        // Forty pairs of processes exchanging a dozen times in one instant, each over a connection of its own, are eighty
        // byte tally entries in one tile, kept since they take far fewer bytes than its records; and five mechanisms on each
        // of the four accounting sides are twenty byte sums.
        var rows = new List<ObservationRowV1>();
        ulong ordinal = 0;
        for (int pair = 0; pair < 40; pair++)
        {
            rows.Add(Lifecycle(1 + pair, ObservationKind.Create, 10_000 + pair, ++ordinal) with { SessionRelativeTicks = (1 + pair) * 100L });
            rows.Add(Lifecycle(1 + pair, ObservationKind.Create, 20_000 + pair, ++ordinal) with { SessionRelativeTicks = (1 + pair) * 100L });
        }

        for (int exchange = 0; exchange < 12; exchange++)
        {
            for (int pair = 0; pair < 40; pair++)
            {
                string client = $"127.0.0.1:{40_000 + pair}";
                string server = $"127.0.0.1:{8_000 + pair}";
                rows.Add(Transfer(5_000, ObservationKind.Send, AccountingSide.SendSide, 100 + pair + exchange, 10_000 + pair, ++ordinal)
                    .Between(client, server) with { SessionRelativeTicks = 500_000 });
                rows.Add(Transfer(5_000, ObservationKind.Receive, AccountingSide.ReceiveSide, 100 + pair + exchange, 20_000 + pair, ++ordinal)
                    .Between(server, client) with { SessionRelativeTicks = 500_000 });
            }
        }

        Mechanism[] mechanisms = [Mechanism.Tcp, Mechanism.Udp, Mechanism.NamedPipe, Mechanism.Rpc, Mechanism.Alpc];
        foreach (Mechanism mechanism in mechanisms)
        {
            foreach (AccountingSide side in Enum.GetValues<AccountingSide>())
            {
                rows.Add(Transfer(5_000, ObservationKind.Send, side, 7, 10_000, ++ordinal)
                    .Between("127.0.0.1:40000", "127.0.0.1:8000") with { Mechanism = mechanism, SessionRelativeTicks = 500_000 });
            }
        }

        ObservationRowV1[] made = [.. rows];
        TimeRange whole = Extent(made);
        using var twin = new TemporarySession();
        Publish(twin.Store, made);
        using TemporarySession session = Published(made);

        // The instant's tile is measured whole from its byte tallies, as its rows measure.
        Bound bound = Bind(twin.Store);
        int instances = bound.Processes.Instances.Count;
        using (TileReading reading = FileOf(session).Read(session.Store.Root))
        {
            var processes = new TransportByteTally(instances + 1);
            var measuring = new ByteMeasuring(SessionTileIndex.AdmittedStrengths(EvidencePolicy.IncludeCorrelated), [], [], [],
                processes, new(1));
            Assert.True(reading.MeasureInterval(reading.Section(SessionSegments.Names(session.Store.Current!)[0]), whole, measuring,
                CancellationToken.None));
            Assert.InRange(reading.TilesOfTalliesRead, 1, 10);
            SessionByteMeasures measured = SessionByteRanking.Measure(twin.Store, whole);
            for (int place = 0; place < instances; place++)
            {
                Assert.Equal(measured.ByProcess.GetValueOrDefault(bound.Processes.Instances[place].Id) ?? TransportBytes.None,
                    processes.Of(place));
            }
        }

        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        Assert.Equal(Brushed(twin.Store, whole, EvidencePolicy.IncludeCorrelated), Brushed(reopened, whole, EvidencePolicy.IncludeCorrelated));
        Assert.Equal(
            Lanes(SessionIntervalByteQuery.MeasureByMechanism(twin.Store, whole, 3, mechanisms)),
            Lanes(SessionIntervalByteQuery.MeasureByMechanism(reopened, whole, 3, mechanisms)));
        Assert.Equal(0, reopened.SegmentReaderCache.Entries);
        Assert.Null(SessionDerivationCache.For(reopened.Current!).TilesProblem);
        reopened.ReleaseSegmentReaders();
        twin.Store.ReleaseSegmentReaders();

        static string[] Lanes(SessionMechanismByteMeasures measures) =>
            [.. measures.Lanes.SelectMany(lane => lane.Columns.Select((bytes, column) => $"{lane.Scope.Mechanism} {column} {bytes}"))];
    }

    /// <summary><paramref name="rows"/> with the sends at <paramref name="at"/> made <see cref="Oversized"/>.</summary>
    private static ObservationRowV1[] Oversize(ObservationRowV1[] rows, int[] at) =>
        [.. rows.Select((row, index) => at.Contains(index) ? row with { ByteValue = Oversized } : row)];

    /// <summary>Every read of a session's bytes a view makes, of <paramref name="interval"/>, each named.</summary>
    private static IEnumerable<(string Read, Action Measure)> ByteReads(SessionStore store, TimeRange interval)
    {
        ProcessInstanceId owner = Bind(store).Processes.Instances.Single(instance => instance.ProcessId == 100).Id;
        yield return ("whole-session ranking", () => SessionByteRanking.Measure(store, null));
        yield return ("brush", () => SessionByteRanking.Measure(store, interval));
        yield return ("interval bytes", () => SessionIntervalByteQuery.Measure(store, interval, 64, IntervalByteScope.Whole));
        yield return ("mechanism lanes", () => SessionIntervalByteQuery.MeasureByMechanism(store, interval, 64, Kinds));
        yield return ("process lanes", () => SessionIntervalByteQuery.MeasureByOwner(store, interval, 64, 16, [owner]));
        yield return ("direction rows", () => SessionIntervalByteQuery.MeasureByDirection(store, interval, 64, owner));
    }

    /// <summary>What <paramref name="measure"/> refused to sum, as unreadable input marked as such.</summary>
    private static string Unsummable(string read, Action measure)
    {
        InvalidDataException refusal = Assert.Throws<InvalidDataException>(measure);
        Assert.True(SegmentMeasurement.IsUnsummable(refusal), $"The {read} refused for another reason: {refusal.Message}");
        return refusal.Message;
    }
}
