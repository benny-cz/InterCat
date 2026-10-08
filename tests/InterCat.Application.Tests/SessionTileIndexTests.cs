using System.Buffers.Binary;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// A generation's persisted tiles are §12.1 S4's deeper levels (`contracts/tile-index-v1.md`): a zoom or a brush of a
/// reopened session counts from them exactly what its rows count, opens no segment, and reads in proportion to what it
/// draws; a part of them that cannot be read is refused, never misread, and the zoom or brush counts from the segments
/// instead, as it does when the tiles' bindings are not the derivation's.
/// </summary>
public sealed class SessionTileIndexTests
{
    private static readonly Mechanism[] Kinds = [Mechanism.Tcp, Mechanism.Udp, Mechanism.NamedPipe, Mechanism.Rpc];

    [Theory(DisplayName = "I4: a reopened session's zooms count from its persisted tiles exactly what its rows count, and open no segment")]
    [InlineData(3)]
    [InlineData(41)]
    [InlineData(20_261_008)]
    public void PersistedTilesCountWhatRowsCount(int seed)
    {
        var random = new Random(seed);
        for (int trial = 0; trial < 10; trial++)
        {
            bool ordered = random.Next(4) != 0;
            ObservationRowV1[] rows = RandomRows(random, ordered);
            using var session = new TemporarySession();
            Publish(session.Store, rows, rowsPerSegment: random.Next(Math.Max(1, rows.Length / 20), rows.Length + 1));
            Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(session.Store, Committed).Outcome);
            session.Store.ReleaseSegmentReaders();
            long[] ticks = [.. rows.Where(row => row.SessionRelativeTicks is not null).Select(row => row.SessionRelativeTicks!.Value / 100)];
            long low = ticks.Min() - random.Next(0, 50);
            long high = ticks.Max() + random.Next(1, 50);

            SessionStore reopened = SessionStore.OpenExisting(LocalOwnedDirectory.Open(session.Path));
            Assert.NotNull(SessionTileIndex.NamedBy(reopened.Current!));
            for (int query = 0; query < 20; query++)
            {
                long start = random.Next(3) == 0 ? low : low + random.NextInt64(0, high - low);
                long end = random.Next(3) == 0 ? high : start + 1 + random.NextInt64(0, high - start);
                var interval = new TimeRange(start, end);
                int columns = random.Next(1, 300);
                SessionTimelineDetail detail = SessionTimelineQuery.Detail(reopened, interval, columns, Kinds);
                Assert.Equal(Expected(rows, interval, columns), Drawn(detail));
            }

            // A segment whose readings go backwards keeps no tiles and has its rows read; every other is never opened.
            if (ordered)
            {
                Assert.Equal(0, reopened.SegmentReaderCache.Entries);
            }

            Assert.Null(SessionDerivationCache.For(reopened.Current!).TilesProblem);
            reopened.ReleaseSegmentReaders();
        }
    }

    [Fact(DisplayName = "S3: a zoom counts from persisted tiles in proportion to its view: whole tiles where its columns hold them, records only where a column boundary falls among them")]
    public void AZoomReadsWhatItDraws()
    {
        // 200,000 sends a third of a millisecond apart, in four segments: each segment's finest tiles a millisecond wide, and
        // three levels above them.
        using var session = new TemporarySession();
        ObservationRowV1[] rows =
        [
            .. Enumerable.Range(0, 200_000).Select(index => Send(index, index * 333_333L / 100) with
            {
                Mechanism = index % 3 == 0 ? Mechanism.Udp : Mechanism.Tcp,
            }),
        ];
        Publish(session.Store, rows, rowsPerSegment: 50_000);
        Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(session.Store, Committed).Outcome);
        session.Store.ReleaseSegmentReaders();
        SessionManifestV1 manifest = session.Store.Current!;
        TileIndexFile file = SessionTileIndex.Open(session.Store.Root, SessionTileIndex.NamedBy(manifest)!, manifest.SessionId);
        Assert.Equal(4, file.Segments.Count);

        var extent = Extent(rows);
        using TileReading reading = file.Read(session.Store.Root);
        TileSection[] sections = [.. SessionSegments.Names(manifest).Select(reading.Section)];
        Assert.All(sections, section => Assert.Equal((10_000L, 4), (section.Width, section.Levels)));
        int blocks = sections.Sum(section => Enumerable.Range(0, section.Levels)
            .Sum(level => (section.TileCount(level) + SessionTileIndex.TilesPerBlock - 1) / SessionTileIndex.TilesPerBlock));

        // The whole session in a hundred columns: every column holds whole tiles but where one of its 101 boundaries, or a
        // segment's end, falls among a tile's records.
        var counted = new TimelineColumns(extent, 100, tallyMechanisms: true);
        Assert.All(sections, section => Assert.True(reading.CountInto(section, counted, CancellationToken.None)));
        Assert.Equal(Expected(rows, extent, 100), Snapshot(counted));
        Assert.InRange(reading.TilesOfRecordsRead, 1, 101);
        Assert.InRange(file.BlocksDecoded, 1, blocks / 3);

        // The same reading counts other columns afresh: nothing of the columns before carries over, not even the column the
        // last tile fell in, which these begin inside.
        var other = new TimelineColumns(new TimeRange(extent.EndTicks - (extent.SpanTicks / 200), extent.EndTicks), 37,
            tallyMechanisms: true);
        Assert.All(sections, section => Assert.True(reading.CountInto(section, other, CancellationToken.None)));
        Assert.Equal(Expected(rows, other.Interval, 37), Snapshot(other));

        // A tenth of a second in the middle reads only the blocks beneath it, and each tile a boundary falls among.
        TileIndexFile fresh = SessionTileIndex.Open(session.Store.Root, SessionTileIndex.NamedBy(manifest)!, manifest.SessionId);
        using TileReading narrow = fresh.Read(session.Store.Root);
        var tenth = new TimeRange(33_000_000, 34_000_000);
        var columns = new TimelineColumns(tenth, 50, tallyMechanisms: true);
        foreach (string name in SessionSegments.Names(manifest))
        {
            Assert.True(narrow.CountInto(narrow.Section(name), columns, CancellationToken.None));
        }

        Assert.Equal(Expected(rows, tenth, 50), Snapshot(columns));
        Assert.Equal(300, columns.Counts.Sum());
        Assert.InRange(narrow.TilesOfRecordsRead, 0, 51);
        Assert.InRange(fresh.BlocksDecoded, 1, 4 * 8);
    }

    [Fact(DisplayName = "I4: a damaged tile index is refused, never misread: the zoom counts from the segments' rows instead")]
    public void ADamagedTileIndexIsRefused()
    {
        // Each case's session differs from the others', so no two share a manifest, nor what a generation's derivations keep.
        static ObservationRowV1[] Rows(int count) => [.. Enumerable.Range(0, count).Select(index => Send(index, (long)(index * 700.01)))];
        ObservationRowV1[] rows = Rows(3_000);
        TimeRange whole = Extent(rows);

        // A byte of the top level's block, which every zoom of the segment reads: refused at that block, and the zoom
        // counts what the segment's rows count, from the segment. The window opens a session without hashing a file that
        // checks itself (store-v1 §6); the command line hashes every file, and opens the generation before instead.
        using (var session = Published(rows))
        {
            (TileSection section, string path) = TopOf(session);
            (long offset, _) = section.BlockAt(section.Levels - 1, 0);
            Flip(path, offset + 3);
            Assert.Contains($"block 0 of level {section.Levels - 1} of the section of '{section.Entry.Name}' does not match "
                + "its checksum", Refusal(session, whole, 64), StringComparison.Ordinal);
            Assert.Equal(1, SessionStore.OpenExisting(LocalOwnedDirectory.Open(session.Path)).Current!.Generation);
            SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
            Assert.Equal(2, reopened.Current!.Generation);
            Assert.Equal(Expected(rows, whole, 64), Drawn(SessionTimelineQuery.Detail(reopened, whole, 64, Kinds)));
            Assert.Equal(1, reopened.SegmentReaderCache.Entries);
            reopened.ReleaseSegmentReaders();
        }

        // A byte of the records a column boundary falls among: refused when they are read.
        rows = Rows(3_001);
        whole = Extent(rows);
        using (var session = Published(rows))
        {
            (TileSection section, string path) = TopOf(session);
            Flip(path, section.RecordsAt + 1);
            SegmentReaderV1 segment = Assert.Single(Segments(session.Store));
            (long low, long high) = SegmentTimeTiles.Of(segment).Readings(0);
            session.Store.ReleaseSegmentReaders();
            Assert.True(low < high);
            var first = new TimeRange(low, high + 1);
            Assert.Contains($"the records of tile 0 of the section of '{section.Entry.Name}' does not match its checksum",
                Refusal(session, first, 2), StringComparison.Ordinal);

            // A zoom whose boundaries fall among no tile's records reads no record, and counts from the tiles alone.
            SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
            var aligned = new TimeRange(0, 100_000);
            Assert.Equal(Expected(rows, aligned, 10), Drawn(SessionTimelineQuery.Detail(reopened, aligned, 10, Kinds)));
            Assert.Equal(0, reopened.SegmentReaderCache.Entries);
            Assert.Equal(Expected(rows, first, 2), Drawn(SessionTimelineQuery.Detail(reopened, first, 2, Kinds)));
            Assert.Equal(1, reopened.SegmentReaderCache.Entries);
            reopened.ReleaseSegmentReaders();
        }

        // Its directory: refused when it is opened, before a byte of any section is read.
        rows = Rows(3_002);
        whole = Extent(rows);
        using (var session = Published(rows))
        {
            StoreDependency named = SessionTileIndex.NamedBy(session.Store.Current!)!;
            string path = Path.Combine(session.Path, named.Name);
            Flip(path, new FileInfo(path).Length - SessionTileIndex.FooterLength - 2);
            InvalidDataException refused = Assert.Throws<InvalidDataException>(() =>
                SessionTileIndex.Open(session.Store.Root, named, session.Store.Current!.SessionId));
            Assert.Equal("The persisted tile index is not readable: its directory does not match its checksum.", refused.Message);
            SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
            Assert.Equal(Expected(rows, whole, 64), Drawn(SessionTimelineQuery.Detail(reopened, whole, 64, Kinds)));
            Assert.Equal(1, reopened.SegmentReaderCache.Entries);
            reopened.ReleaseSegmentReaders();
        }
    }

    [Fact(DisplayName = "I14: a generation published before tile indexes is counted from its segments, and gains one when its checkpoint is published")]
    public void AnEarlierCheckpointGainsATileIndex()
    {
        ObservationRowV1[] rows = [.. Enumerable.Range(0, 2_000).Select(index => Send(index, index * 37L))];
        TimeRange whole = Extent(rows);
        using var session = Published(rows);

        // As revision 455 published it: the checkpoint, the overview and the operation index, and no tile index.
        SessionStore store = session.Store;
        Republish(session, tiles: null);
        Assert.Null(SessionTileIndex.NamedBy(store.Current!));
        SessionStore earlier = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        Assert.Equal(Expected(rows, whole, 40), Drawn(SessionTimelineQuery.Detail(earlier, whole, 40, Kinds)));
        Assert.Equal(1, earlier.SegmentReaderCache.Entries);
        earlier.ReleaseSegmentReaders();

        // Publishing its checkpoint again publishes all four, the tile index among them, and a zoom then opens no segment.
        CheckpointPublication again = SessionCheckpoints.Publish(store, Committed);
        Assert.True(again.Outcome == CheckpointOutcome.Published, again.Reason);
        store.ReleaseSegmentReaders();
        Assert.NotNull(SessionTileIndex.NamedBy(store.Current!));
        SessionStore later = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        Assert.Equal(Expected(rows, whole, 40), Drawn(SessionTimelineQuery.Detail(later, whole, 40, Kinds)));
        Assert.Equal(0, later.SegmentReaderCache.Entries);
        Assert.Equal(CheckpointOutcome.AlreadyCurrent, SessionCheckpoints.Publish(store, Committed).Outcome);
        store.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "I14: a tile index is used only where it describes each of the generation's segments by name, length and digest")]
    public void ATileIndexIsUsedOnlyForTheSegmentsItDescribes()
    {
        ObservationRowV1[] rows = [.. Enumerable.Range(0, 2_400).Select(index => Send(index, index * 53L))];
        TimeRange whole = Extent(rows);
        using var session = Published(rows, rowsPerSegment: 1_000);
        SessionStore store = session.Store;
        SessionManifestV1 published = store.Current!;
        StoreDependency[] segments = SessionOverviewIndex.ObservationSegments(published);
        Assert.Equal(3, segments.Length);

        // The same indexes again, with a tile index whose section of the second segment names another digest.
        (ProcessInstanceIndex processes, TransportRelationIndex relations) = Derivations(store);
        Republish(session, tiles: content => SessionTileIndex.Write(content, published.SessionId, published.Generation,
        [
            .. segments.Select((segment, ordinal) => (ordinal == 1 ? segment with { Digest = "sha256:" + new string('0', 64) } : segment,
                SessionSegments.Open(store, published, segment.Name))),
        ], processes, relations, CancellationToken.None));
        store.ReleaseSegmentReaders();
        SessionStore viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        TileIndexFile tiles = SessionTileIndex.Open(viewer.Root, SessionTileIndex.NamedBy(viewer.Current!)!, viewer.Current!.SessionId);
        Assert.Equal([true, false, true], SessionOverviewIndex.ObservationSegments(viewer.Current!).Select(tiles.Describes));
        Assert.Equal(Expected(rows, whole, 30), Drawn(SessionTimelineQuery.Detail(viewer, whole, 30, Kinds)));
        Assert.Equal(3, viewer.SegmentReaderCache.Entries);
        viewer.ReleaseSegmentReaders();

        // A brush, likewise, counts from the segments it meets.
        Bound bound = Bind(store);
        Assert.Equal(Counted(bound, whole, EvidencePolicy.IncludeCorrelated), Stated(SessionIntervalQuery.Count(viewer, whole)));
        Assert.Equal(3, viewer.SegmentReaderCache.Entries);
        viewer.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "I4: a generation's tile index refused once is not read again for it, and says why")]
    public void ARefusedTileIndexIsNotReadAgain()
    {
        using var session = Published([.. Enumerable.Range(0, 50).Select(index => Send(index, index * 10L))]);
        var derivation = new SessionDerivation(session.Store.Current!);
        Assert.NotNull(derivation.PersistedTiles(session.Store.Root));
        Assert.Null(derivation.TilesProblem);
        derivation.RefuseTiles("The persisted tile index is not readable: a test said so.");
        Assert.Null(derivation.PersistedTiles(session.Store.Root));
        Assert.Equal("The persisted tile index is not readable: a test said so.", derivation.TilesProblem);
    }

    [Fact(DisplayName = "I14: a segment that keeps no tiles is described as such, its rows read, and one with no timed record counts nothing")]
    public void UntiledSegmentsAreDescribed()
    {
        // Three segments: readings that go backwards, none timed, and an ordinary one.
        ObservationRowV1[] rows =
        [
            .. Enumerable.Range(0, 10).Select(index => Send(index, index) with { SessionRelativeTicks = (10 - index) * 10_000L }),
            .. Enumerable.Range(10, 10).Select(index => Send(index, index) with { SessionRelativeTicks = null }),
            .. Enumerable.Range(20, 10).Select(index => Send(index, index * 100L)),
        ];
        using var session = Published(rows, rowsPerSegment: 10);
        SessionManifestV1 manifest = session.Store.Current!;
        TileIndexFile file = SessionTileIndex.Open(session.Store.Root, SessionTileIndex.NamedBy(manifest)!, manifest.SessionId);
        using TileReading reading = file.Read(session.Store.Root);
        TileSection[] sections = [.. SessionSegments.Names(manifest).Select(reading.Section)];
        Assert.Equal(
        [
            (10, 0, false, 100L, 1_000L, 0),
            (10, 10, true, (long?)null, (long?)null, 0),
            (10, 0, true, 2_000L, 2_900L, 1),
        ], sections.Select(section => (section.Rows, section.Untimed, section.Ordered, section.First, section.Last, section.Levels)));
        var all = new TimeRange(0, 3_000);
        var counted = new TimelineColumns(all, 9, tallyMechanisms: true);
        Assert.False(reading.CountInto(sections[0], counted, CancellationToken.None));
        Assert.True(reading.CountInto(sections[1], counted, CancellationToken.None));
        Assert.True(reading.CountInto(sections[2], counted, CancellationToken.None));
        Assert.Equal(10, counted.Counts.Sum());

        // A zoom or a brush reads the backward segment's rows, and opens no other.
        SessionStore reopened = SessionStore.OpenExisting(LocalOwnedDirectory.Open(session.Path));
        Assert.Equal(Expected(rows, all, 9), Drawn(SessionTimelineQuery.Detail(reopened, all, 9, Kinds)));
        Assert.Equal(1, reopened.SegmentReaderCache.Entries);
        reopened.ReleaseSegmentReaders();
        Bound bound = Bind(session.Store);
        Assert.Equal(Counted(bound, all, EvidencePolicy.IncludeCorrelated), Stated(SessionIntervalQuery.Count(reopened, all)));
        Assert.Equal(1, reopened.SegmentReaderCache.Entries);
        reopened.ReleaseSegmentReaders();
    }

    [Theory(DisplayName = "I4: a reopened session's brush counts from its tiles' tallies and records exactly what its rows count, under every policy, and opens no segment")]
    [InlineData(5)]
    [InlineData(77)]
    [InlineData(20_261_008)]
    public void PersistedTalliesCountWhatRowsCount(int seed)
    {
        var random = new Random(seed);
        for (int trial = 0; trial < 6; trial++)
        {
            // Readings a native tick apart, a thousand or a million: tiles of one reading each, or of several.
            using var session = new TemporarySession();
            long stretch = trial % 3 == 0 ? 1 : trial % 3 == 1 ? 1_000 : 1_000_000;
            foreach ((ObservationRowV1[] rows, SourceFieldRowV1[] fields) in Stretched(random, stretch))
            {
                _ = Publish(session.Store, rows, rowsPerSegment: random.Next(5, 80), fields: fields);
            }

            Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(session.Store, Committed).Outcome);
            session.Store.ReleaseSegmentReaders();
            Bound bound = Bind(session.Store);
            long[] ticks = [.. bound.Segments.SelectMany(segment => Enumerable.Range(0, segment.RowCount)
                .Select(row => segment.SignedValue(SegmentColumnId.SessionRelativeTicks, row))
                .Where(nanoseconds => nanoseconds is not null).Select(nanoseconds => nanoseconds!.Value / 100))];
            long low = ticks.Min() - (random.Next(0, 3) * stretch);
            long high = ticks.Max() + 1 + (random.Next(0, 3) * stretch);

            SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
            foreach (EvidencePolicy policy in Enum.GetValues<EvidencePolicy>())
            {
                for (int query = 0; query < 6; query++)
                {
                    // The whole extent, a reading to a reading, or anywhere.
                    TimeRange interval = query switch
                    {
                        0 => new(low, high),
                        1 => Ordered(ticks[random.Next(ticks.Length)], ticks[random.Next(ticks.Length)]),
                        _ => Ordered(low + random.NextInt64(0, high - low), low + random.NextInt64(0, high - low)),
                    };
                    Assert.Equal(Counted(bound, interval, policy), Stated(SessionIntervalQuery.Count(reopened, interval, policy)));
                }
            }

            Assert.Equal(0, reopened.SegmentReaderCache.Entries);
            Assert.Null(SessionDerivationCache.For(reopened.Current!).TilesProblem);
            reopened.ReleaseSegmentReaders();
        }

        static TimeRange Ordered(long one, long other) => new(Math.Min(one, other), Math.Max(one, other) + 1);
    }

    [Fact(DisplayName = "S3: a brush counts from persisted tiles in proportion to its ends: whole tiles from their tallies, records only where an end falls among them")]
    public void ABrushReadsInProportionToItsEnds()
    {
        // 80,000 exchanges a third of a millisecond apart between two processes, a send and its receipt, a third of the
        // sends followed by a datagram, half of them with no session time, among the timed records of their tiles; in four
        // segments, each with four levels of tiles.
        using var session = new TemporarySession();
        ObservationRowV1[] rows = Exchanges(80_000);
        Publish(session.Store, rows, rowsPerSegment: 50_000);
        Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(session.Store, Committed).Outcome);
        session.Store.ReleaseSegmentReaders();
        SessionManifestV1 manifest = session.Store.Current!;
        Bound bound = Bind(session.Store);
        TileIndexFile file = SessionTileIndex.Open(session.Store.Root, SessionTileIndex.NamedBy(manifest)!, manifest.SessionId);
        Assert.True(file.Derivation.Matches(bound.Processes, bound.Relations));
        Assert.Equal((2, 1), (bound.Processes.Instances.Count, bound.Relations.Relations.Count));

        // The whole extent: every segment's top tiles from their tallies, and no record.
        TimeRange extent = Extent(rows);
        using (TileReading whole = file.Read(session.Store.Root))
        {
            TileSection[] sections = [.. SessionSegments.Names(manifest).Select(whole.Section)];
            Assert.Equal(4, sections.Length);
            Assert.All(sections, section => Assert.Equal(4, section.Levels));
            IntervalTally counted = Tally(whole, sections, extent, EvidencePolicy.IncludeCorrelated, bound);
            Assert.Equal(rows.Count(row => row.SessionRelativeTicks is not null), counted.Observed);
            Assert.Equal(160_000, counted.Graph);
            Assert.Equal(0, whole.TilesOfRecordsRead);
            Assert.Equal(sections.Sum(section => section.TileCount(section.Levels - 1)), whole.TilesOfTalliesRead);
            Assert.Equal(0, whole.TilesUntallied);
        }

        // A tenth of a second inside one segment, each end between a send and its receipt: the blocks beneath its two
        // ends, the tallies of the tiles between, and the records of the two tiles its ends fall among.
        TileIndexFile fresh = SessionTileIndex.Open(session.Store.Root, SessionTileIndex.NamedBy(manifest)!, manifest.SessionId);
        var tenth = new TimeRange(33_001_034, 34_001_000);
        using (TileReading narrow = fresh.Read(session.Store.Root))
        {
            TileSection[] sections = [.. SessionSegments.Names(manifest).Select(narrow.Section)];
            IntervalTally counted = Tally(narrow, sections, tenth, EvidencePolicy.IncludeCorrelated, bound);
            Assert.Equal(Counted(bound, tenth, EvidencePolicy.IncludeCorrelated)[0],
                $"observed {counted.Observed}, graph {counted.Graph}");
            Assert.Equal(2, narrow.TilesOfRecordsRead);
            Assert.InRange(narrow.TilesOfTalliesRead, 1, 2 * 9 * 4);
            Assert.InRange(fresh.BlocksDecoded, 1, 4 * 8);
        }

        // The query counts the same as the rows do under every policy, and opens no segment: over the tenth, the whole
        // extent, six seconds of the first segment, whose ends descend into other blocks of the same levels, and eight
        // seconds across the first two segments.
        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        foreach (EvidencePolicy policy in Enum.GetValues<EvidencePolicy>())
        {
            foreach (TimeRange interval in new[] { tenth, extent, new(5_001_034, 66_601_000), new(60_001_034, 140_001_000) })
            {
                Assert.Equal(Counted(bound, interval, policy), Stated(SessionIntervalQuery.Count(reopened, interval, policy)));
            }
        }

        Assert.Equal(0, reopened.SegmentReaderCache.Entries);
        reopened.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "S3: a tile whose tallies would take more than half the bytes of its records keeps none, and a brush counts it from its children or records, exactly")]
    public void TalliesAreKeptOnlyWhereTheySpareReading()
    {
        // 60 connections between 120 processes, their records taking turns 30 microseconds apart: a finest tile's three
        // or so records, and a tile ten times wider's thirty, each have an owner and a channel of their own, so their
        // tallies would take as many bytes as their records; a tile a hundred times wider has ten records per entry.
        ObservationRowV1[] rows = Interleaved(60, 30_000);
        TimeRange extent = Extent(rows);
        using var session = Published(rows);
        SessionManifestV1 manifest = session.Store.Current!;
        Bound bound = Bind(session.Store);
        TileIndexFile file = SessionTileIndex.Open(session.Store.Root, SessionTileIndex.NamedBy(manifest)!, manifest.SessionId);
        using (TileReading whole = file.Read(session.Store.Root))
        {
            TileSection section = whole.Section(Assert.Single(SessionSegments.Names(manifest)));
            Assert.Equal((1_000L, 4), (section.Width, section.Levels));
            IntervalTally counted = Tally(whole, [section], extent, EvidencePolicy.IncludeCorrelated, bound);
            Assert.Equal(rows.Length, counted.Observed);

            // Every widest tile from its tallies but the last, whose three records keep none at any level and are read.
            Assert.Equal((section.TileCount(3) - 1, 4, 1), (whole.TilesOfTalliesRead, whole.TilesUntallied, whole.TilesOfRecordsRead));
        }

        // A brush of a few milliseconds counts the widest tiles its ends fall beneath from their tallies, and the narrower
        // ones, which keep none, from their records.
        var few = new TimeRange(1_000_150, 1_604_850);
        using (TileReading narrow = file.Read(session.Store.Root))
        {
            TileSection section = narrow.Section(SessionSegments.Names(manifest)[0]);
            IntervalTally counted = Tally(narrow, [section], few, EvidencePolicy.IncludeCorrelated, bound);
            Assert.Equal(Counted(bound, few, EvidencePolicy.IncludeCorrelated)[0], $"observed {counted.Observed}, graph {counted.Graph}");
            // At each end, at most nine tiles of each level beside the one it falls in, and each of the narrower ones'
            // ten children: a few hundred records in all.
            Assert.InRange(narrow.TilesOfTalliesRead, 1, 2 * 9);
            Assert.InRange(narrow.TilesUntallied, 2, 2 * (9 + 90 + 9));
            Assert.InRange(narrow.TilesOfRecordsRead, 3, 2 * (90 + 9 + 1));
        }

        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        foreach (EvidencePolicy policy in Enum.GetValues<EvidencePolicy>())
        {
            foreach (TimeRange interval in new[] { extent, few, new(150_150, 7_777_777) })
            {
                Assert.Equal(Counted(bound, interval, policy), Stated(SessionIntervalQuery.Count(reopened, interval, policy)));
            }
        }

        Assert.Equal(0, reopened.SegmentReaderCache.Entries);
        reopened.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "I4: a burst that fills one tile with hundreds of records is counted from them exactly, by a zoom and by a brush")]
    public void ABurstIsCountedFromItsTile()
    {
        // 600 sends a tick apart, then one every ten milliseconds for a minute: the burst's records fill one tile.
        ObservationRowV1[] rows =
        [
            .. Enumerable.Range(0, 600).Select(index => Send(index, 1_000 + index)),
            .. Enumerable.Range(600, 6_000).Select(index => Send(index, 1_000 + (index * 100_000L))),
        ];
        using var session = Published(rows);
        SessionManifestV1 manifest = session.Store.Current!;
        TileIndexFile file = SessionTileIndex.Open(session.Store.Root, SessionTileIndex.NamedBy(manifest)!, manifest.SessionId);
        using (TileReading reading = file.Read(session.Store.Root))
        {
            TileSection section = reading.Section(SessionSegments.Names(manifest)[0]);
            Assert.Equal(100_000L, section.Width);
            var burst = new TimelineColumns(new TimeRange(1_000, 1_600), 7, tallyMechanisms: true);
            Assert.True(reading.CountInto(section, burst, CancellationToken.None));
            Assert.Equal(Expected(rows, burst.Interval, 7), Snapshot(burst));
            Assert.Equal(1, reading.TilesOfRecordsRead);
        }

        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        var half = new TimeRange(1_000, 1_301);
        Assert.Equal(Expected(rows, half, 3), Drawn(SessionTimelineQuery.Detail(reopened, half, 3, Kinds)));
        Assert.Equal(Counted(Bind(session.Store), half, EvidencePolicy.IncludeCorrelated), Stated(SessionIntervalQuery.Count(reopened, half)));
        Assert.Equal(0, reopened.SegmentReaderCache.Entries);
        reopened.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "R22: a tile index's derivation is its rules, instances and channels: another instance, paired channel or unpaired channel is another derivation")]
    public void ADerivationIsItsInstancesAndChannels()
    {
        (ProcessInstanceIndex Processes, TransportRelationIndex Relations) exchanges = Of(Exchanges(10));
        TileDerivation derivation = SessionTileIndex.Derivation(exchanges.Processes, exchanges.Relations);
        Assert.Equal((2, 2, 1), (derivation.Instances, derivation.Channels, exchanges.Relations.Relations.Count));
        _ = Assert.Single(exchanges.Relations.OneSided);

        // The same capture again is the same derivation. Another PID's instance is another, as is a paired channel held by
        // it, and an unpaired channel that begins at another record.
        (ProcessInstanceIndex Processes, TransportRelationIndex Relations) again = Of(Exchanges(10));
        Assert.True(derivation.Matches(again.Processes, again.Relations));
        (ProcessInstanceIndex Processes, TransportRelationIndex Relations) other = Of(Exchanges(10, receiver: 300));
        Assert.False(derivation.Matches(other.Processes, exchanges.Relations));
        Assert.False(derivation.Matches(exchanges.Processes, other.Relations));
        Assert.False(derivation.Matches(exchanges.Processes, Of(Exchanges(10, firstDatagram: 1)).Relations));
        Assert.False((derivation with { RelationRule = "transport-endpoint-relation-v3" }).Matches(exchanges.Processes, exchanges.Relations));

        static (ProcessInstanceIndex Processes, TransportRelationIndex Relations) Of(ObservationRowV1[] rows)
        {
            using var session = new TemporarySession();
            Publish(session.Store, rows);
            Bound bound = Bind(session.Store);
            return (bound.Processes, bound.Relations);
        }
    }

    [Fact(DisplayName = "R22: a tile index whose bindings are not the derivation's is not counted by a brush, which reads its segments, and a checkpoint publishes it again")]
    public void TalliesOfAnotherDerivationAreNotCounted()
    {
        ObservationRowV1[] rows = Exchanges(1_500);
        TimeRange whole = Extent(rows);
        using var session = Published(rows, rowsPerSegment: 1_000);
        SessionStore store = session.Store;
        SessionManifestV1 published = store.Current!;
        (ProcessInstanceIndex processes, TransportRelationIndex relations) = Derivations(store);
        TileDerivation derivation = SessionTileIndex.Derivation(processes, relations);
        Assert.True(derivation.Matches(processes, relations));
        Assert.False((derivation with { Fingerprint = new byte[32] }).Matches(processes, relations));
        Assert.False((derivation with { BindingRule = "process-binding-v3" }).Matches(processes, relations));

        // The same indexes again, with tiles stating another derivation's bindings.
        Republish(session, tiles: content => SessionTileIndex.Write(content, published.SessionId, published.Generation,
        [
            .. SessionOverviewIndex.ObservationSegments(published).Select(segment => (segment, SessionSegments.Open(store, published, segment.Name))),
        ], processes, relations, CancellationToken.None, stated: derivation with { Fingerprint = new byte[32] }));
        store.ReleaseSegmentReaders();

        // A zoom still counts from the tiles; a brush counts from the segments, the same as their rows.
        SessionStore viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        Assert.Equal(Expected(rows, whole, 20), Drawn(SessionTimelineQuery.Detail(viewer, whole, 20, Kinds)));
        Assert.Equal(0, viewer.SegmentReaderCache.Entries);
        Bound bound = Bind(store);
        Assert.Equal(Counted(bound, whole, EvidencePolicy.IncludeCorrelated), Stated(SessionIntervalQuery.Count(viewer, whole)));
        Assert.Equal(SessionSegments.Names(published).Count, viewer.SegmentReaderCache.Entries);
        Assert.Null(SessionDerivationCache.For(viewer.Current!).TilesProblem);
        viewer.ReleaseSegmentReaders();

        // Its writer's next checkpoint publishes the four again, and a brush then opens no segment.
        CheckpointPublication again = SessionCheckpoints.Publish(store, Committed);
        Assert.True(again.Outcome == CheckpointOutcome.Published, again.Reason);
        store.ReleaseSegmentReaders();
        SessionStore later = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        Assert.Equal(Counted(bound, whole, EvidencePolicy.IncludeCorrelated), Stated(SessionIntervalQuery.Count(later, whole)));
        Assert.Equal(0, later.SegmentReaderCache.Entries);
        Assert.Equal(CheckpointOutcome.AlreadyCurrent, SessionCheckpoints.Publish(store, Committed).Outcome);
        store.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "I4: damaged tallies are refused, never counted: the brush counts from the segments' rows instead")]
    public void DamagedTalliesAreRefused()
    {
        ObservationRowV1[] rows = Exchanges(1_700);
        TimeRange whole = Extent(rows);
        using var session = Published(rows);
        (TileSection section, string path) = TopOf(session);
        SessionManifestV1 manifest = session.Store.Current!;
        TileIndexFile file = SessionTileIndex.Open(session.Store.Root, SessionTileIndex.NamedBy(manifest)!, manifest.SessionId);
        int top = section.Levels - 1;
        int link;
        using (FileStream stream = File.OpenRead(path))
        {
            link = file.Block(stream, section, top, 0).TallyLinks[1];
        }

        Flip(path, section.TalliesAt + link);

        // A brush over the whole extent counts the top tiles from their tallies, and so meets the damage.
        SessionStore viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        Bound bound = Bind(session.Store);
        Assert.Equal(Counted(bound, whole, EvidencePolicy.AllIncludingConflicting),
            Stated(SessionIntervalQuery.Count(viewer, whole, EvidencePolicy.AllIncludingConflicting)));
        Assert.Equal(1, viewer.SegmentReaderCache.Entries);
        Assert.Equal($"The persisted tile index is not readable: the tallies of block 0 of level {top} of the section of "
            + $"'{section.Entry.Name}' does not match its checksum.", SessionDerivationCache.For(viewer.Current!).TilesProblem);
        viewer.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "I14: a tile index written before tiles kept their bindings is refused, counted around, and published again")]
    public void AMinorZeroTileIndexIsPublishedAgain()
    {
        ObservationRowV1[] rows = Exchanges(1_300);
        TimeRange whole = Extent(rows);
        using var session = Published(rows);
        SessionStore store = session.Store;
        string path = Path.Combine(session.Path, SessionTileIndex.NamedBy(store.Current!)!.Name);

        // The same file, its header saying minor 0, as revision 456 wrote it.
        Republish(session, tiles: content =>
        {
            byte[] bytes = File.ReadAllBytes(path);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), 0);
            Crc32C.Write(bytes.AsSpan(44), Crc32C.Compute(bytes.AsSpan(0, 44)));
            content.Write(bytes);
        });
        store.ReleaseSegmentReaders();
        SessionStore viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        Bound bound = Bind(store);
        Assert.Equal(Counted(bound, whole, EvidencePolicy.DirectOnly), Stated(SessionIntervalQuery.Count(viewer, whole, EvidencePolicy.DirectOnly)));
        Assert.Equal(1, viewer.SegmentReaderCache.Entries);
        Assert.Equal("The persisted tile index is not readable: it is minor 0, written before a tile index kept its records' "
            + "bindings; a checkpoint publishes it again.", SessionDerivationCache.For(viewer.Current!).TilesProblem);
        viewer.ReleaseSegmentReaders();

        CheckpointPublication again = SessionCheckpoints.Publish(store, Committed);
        Assert.True(again.Outcome == CheckpointOutcome.Published, again.Reason);
        store.ReleaseSegmentReaders();
        SessionStore later = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        Assert.Equal(Counted(bound, whole, EvidencePolicy.DirectOnly), Stated(SessionIntervalQuery.Count(later, whole, EvidencePolicy.DirectOnly)));
        Assert.Equal(0, later.SegmentReaderCache.Entries);
        later.ReleaseSegmentReaders();
    }

    /// <summary>
    /// A send at <paramref name="tick"/>: its native reading, and its session time in nanoseconds as the test clock places
    /// that reading, so a query passes over a segment by the readings it holds as it would a capture's.
    /// </summary>
    private static ObservationRowV1 Send(int index, long tick) => Transfer(tick, ObservationKind.Send, AccountingSide.SendSide, 8,
        100, (ulong)(index + 1)).Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = tick * 100 };

    /// <summary>
    /// <paramref name="exchanges"/> sends a third of a millisecond apart from PID 100 to <paramref name="receiver"/> over
    /// one loopback connection, each received a sixth of a millisecond later, and from exchange
    /// <paramref name="firstDatagram"/> on every third followed by a datagram from PID 100, every other one with no
    /// session time; after both processes' creation.
    /// </summary>
    private static ObservationRowV1[] Exchanges(int exchanges, int receiver = 200, int firstDatagram = 0)
    {
        var rows = new List<ObservationRowV1>
        {
            Lifecycle(1, ObservationKind.Create, 100, 1) with { SessionRelativeTicks = 100 },
            Lifecycle(2, ObservationKind.Create, receiver, 2) with { SessionRelativeTicks = 200 },
        };
        ulong ordinal = 2;
        for (int exchange = 0; exchange < exchanges; exchange++)
        {
            long tick = 1_000 + (exchange * 3_333L);
            rows.Add(Transfer(tick, ObservationKind.Send, AccountingSide.SendSide, 64, 100, ++ordinal)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = tick * 100 });
            rows.Add(Transfer(tick + 1_666, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, receiver, ++ordinal)
                .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = (tick + 1_666) * 100 });
            if (exchange >= firstDatagram && (exchange - firstDatagram) % 3 == 0)
            {
                rows.Add(Transfer(tick + 2_000, ObservationKind.Send, AccountingSide.SendSide, 32, 100, ++ordinal)
                    .Between("127.0.0.1:50001", "127.0.0.1:53") with
                {
                    Mechanism = Mechanism.Udp,
                    SessionRelativeTicks = exchange % 2 == 0 ? (tick + 2_000) * 100 : null,
                });
            }
        }

        return [.. rows];
    }

    /// <summary>
    /// <paramref name="rows"/> records 30 microseconds apart over <paramref name="pairs"/> loopback connections, each
    /// between a client and a server process of its own, the connections taking turns record by record: each in turn
    /// sends from its client, receives at its server, sends from its server and receives at its client, a round of the
    /// connections at a time; after every process's creation.
    /// </summary>
    private static ObservationRowV1[] Interleaved(int pairs, int rows)
    {
        var made = new List<ObservationRowV1>();
        ulong ordinal = 0;
        for (int pair = 0; pair < pairs; pair++)
        {
            made.Add(Lifecycle(1 + pair, ObservationKind.Create, 10_000 + pair, ++ordinal) with { SessionRelativeTicks = (1 + pair) * 100L });
            made.Add(Lifecycle(1 + pair, ObservationKind.Create, 20_000 + pair, ++ordinal) with { SessionRelativeTicks = (1 + pair) * 100L });
        }

        for (int index = made.Count; index < rows; index++)
        {
            long tick = 1_000 + (index * 300L);
            int pair = index % pairs;
            string client = $"127.0.0.1:{40_000 + pair}";
            string server = $"127.0.0.1:{8_000 + pair}";
            made.Add(((index / pairs) % 4) switch
            {
                0 => Transfer(tick, ObservationKind.Send, AccountingSide.SendSide, 512, 10_000 + pair, ++ordinal).Between(client, server),
                1 => Transfer(tick, ObservationKind.Receive, AccountingSide.ReceiveSide, 512, 20_000 + pair, ++ordinal).Between(server, client),
                2 => Transfer(tick, ObservationKind.Send, AccountingSide.SendSide, 64, 20_000 + pair, ++ordinal).Between(server, client),
                _ => Transfer(tick, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 10_000 + pair, ++ordinal).Between(client, server),
            } with { SessionRelativeTicks = tick * 100 });
        }

        return [.. made];
    }

    /// <summary>
    /// A random capture's chunks (<see cref="RandomCaptures"/>), each reading <paramref name="stretch"/> times as far from
    /// the session's start and moved within that stretch by its ordinal, so a tile can hold records at several readings.
    /// </summary>
    private static List<(ObservationRowV1[] Rows, SourceFieldRowV1[] Fields)> Stretched(Random random, long stretch)
    {
        return [.. RandomCaptures.Chunks(random).Select(chunk => (
            chunk.Rows.Select(row => row with
            {
                NativeTicks = Moved(row.NativeTicks, row.RawRecordOrdinal),
                SessionRelativeTicks = row.SessionRelativeTicks is null ? null : Moved(row.NativeTicks, row.RawRecordOrdinal) * 100,
            }).ToArray(),
            chunk.Fields.Select(field => field with { NativeTicks = Moved(field.NativeTicks, field.RawRecordOrdinal) }).ToArray()))];

        long Moved(long ticks, ulong ordinal) => (ticks * stretch) + (long)(ordinal * 7_919 % (ulong)stretch);
    }

    /// <summary>The current generation's segments, read apart from the store's readers, and their derivations made afresh.</summary>
    private sealed record Bound(SegmentReaderV1[] Segments, ProcessInstanceIndex Processes, TransportRelationIndex Relations);

    private static Bound Bind(SessionStore store)
    {
        SessionManifestV1 manifest = store.Current!;
        SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store.Root, manifest, name))];
        SegmentReaderV1[] fields = [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store.Root, manifest, name))];
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(segments, TestClock, fields);
        return new(segments, processes, TransportRelationIndex.Derive(segments, processes));
    }

    /// <summary>The instances and channels a query of the current generation binds with.</summary>
    private static (ProcessInstanceIndex Processes, TransportRelationIndex Relations) Derivations(SessionStore store)
    {
        SessionManifestV1 manifest = store.Current!;
        var generation = new GenerationSegments(store, manifest, SessionSegments.SourceClock(store.Root, manifest)!.Value);
        return (generation.Processes(CancellationToken.None), generation.Relations(CancellationToken.None));
    }

    /// <summary>What a brush over <paramref name="interval"/> counts, counted from every row, as <see cref="Stated"/> says it.</summary>
    private static List<string> Counted(Bound bound, TimeRange interval, EvidencePolicy policy)
    {
        Dictionary<int, (string Edge, string Channel)> drawn = bound.Relations.Relations
            .Where(relation => relation.Mechanism == Mechanism.Tcp && SessionOverviewProjector.Admitted(relation.Strength, policy))
            .ToDictionary(relation => relation.Channel, relation => (SessionOverviewProjector.EdgeKeyOf(relation), relation.StableKey));
        long observed = 0;
        long graph = 0;
        var edges = new Dictionary<string, long>(StringComparer.Ordinal);
        var channels = new Dictionary<string, long>(StringComparer.Ordinal);
        var made = new Dictionary<(ProcessInstanceId Id, Mechanism Mechanism), long>();
        foreach (SegmentReaderV1 segment in bound.Segments)
        {
            ProcessBinding[] owners = bound.Processes.OwnersOf(segment);
            ChannelBinding[] bindings = bound.Relations.ChannelsOf(segment);
            for (int row = 0; row < segment.RowCount; row++)
            {
                if (segment.SignedValue(SegmentColumnId.SessionRelativeTicks, row) is not { } nanoseconds
                    || !interval.Contains(nanoseconds / 100))
                {
                    continue;
                }

                observed++;
                if (owners[row].IsAdmittedUnder(policy))
                {
                    var key = (bound.Processes.Instances[owners[row].Instance].Id,
                        (Mechanism)segment.UnsignedValue(SegmentColumnId.Mechanism, row)!.Value);
                    made[key] = made.GetValueOrDefault(key) + 1;
                }

                if (bindings[row].IsKnown && drawn.TryGetValue(bindings[row].Channel, out (string Edge, string Channel) keys))
                {
                    graph++;
                    edges[keys.Edge] = edges.GetValueOrDefault(keys.Edge) + 1;
                    channels[keys.Channel] = channels.GetValueOrDefault(keys.Channel) + 1;
                }
            }
        }

        return
        [
            $"observed {observed}, graph {graph}",
            .. edges.Select(entry => $"edge {entry.Key}: {entry.Value}").Order(StringComparer.Ordinal),
            .. channels.Select(entry => $"channel {entry.Key}: {entry.Value}").Order(StringComparer.Ordinal),
            .. made.Select(entry => $"process {entry.Key.Id} {entry.Key.Mechanism}: {entry.Value}").Order(StringComparer.Ordinal),
        ];
    }

    /// <summary>A brush's counts as the query states them: what it observed and drew, and each edge's, channel's and process's.</summary>
    private static List<string> Stated(SessionIntervalCounts counts) =>
    [
        $"observed {counts.ObservedRows}, graph {counts.GraphRows}",
        .. counts.EdgeRecords.Select(entry => $"edge {entry.Key}: {entry.Value}").Order(StringComparer.Ordinal),
        .. counts.ChannelRecords.Select(entry => $"channel {entry.Key}: {entry.Value}").Order(StringComparer.Ordinal),
        .. counts.ProcessRecords.SelectMany(entry => entry.Value.Select(count => $"process {entry.Key} {count.Mechanism}: {count.Records}"))
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>A brush's counts from a reading of a tile index alone, as the query binds them under <paramref name="policy"/>.</summary>
    private static IntervalTally Tally(TileReading reading, TileSection[] sections, TimeRange interval, EvidencePolicy policy, Bound bound)
    {
        int[] slotOfChannel = new int[bound.Relations.Channels];
        Array.Fill(slotOfChannel, -1);
        int drawn = 0;
        foreach (TransportRelation relation in bound.Relations.Relations)
        {
            if (relation.Mechanism == Mechanism.Tcp && SessionOverviewProjector.Admitted(relation.Strength, policy))
            {
                slotOfChannel[relation.Channel] = drawn++;
            }
        }

        var tally = new IntervalTally(bound.Processes.Instances.Count * TimelineColumns.SlotCount, drawn);
        Assert.All(sections, section =>
            Assert.True(reading.CountInterval(section, interval, policy, slotOfChannel, tally, CancellationToken.None)));
        return tally;
    }

    /// <summary>
    /// Publishes the current generation's indexes again as the next generation: each as it is, but the tile index, which
    /// <paramref name="tiles"/> writes instead, or which is left out when it is null.
    /// </summary>
    private static void Republish(TemporarySession session, Action<Stream>? tiles)
    {
        SessionStore store = session.Store;
        SessionManifestV1 published = store.Current!;
        StoreDependency? named = SessionTileIndex.NamedBy(published);
        long next = store.NextGeneration;
        var staged = new List<StoreStagingFile>();
        try
        {
            foreach (StoreDependency index in published.Dependencies.Where(dependency => dependency.Kind == StoreDependencyKind.Index
                && (dependency != named || tiles is not null)))
            {
                StoreStagingFile file = store.Stage(index.Name.Replace(
                    published.Generation.ToString("D10", System.Globalization.CultureInfo.InvariantCulture),
                    next.ToString("D10", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal),
                    StoreDependencyKind.Index);
                staged.Add(file);
                if (index == named)
                {
                    tiles!(file.Content);
                }
                else
                {
                    file.Content.Write(File.ReadAllBytes(Path.Combine(session.Path, index.Name)));
                }

                _ = file.Complete();
            }

            Assert.Equal(tiles is null ? 3 : 4, staged.Count);
            _ = store.CommitIndex(staged, published.Generation, Committed, next);
        }
        finally
        {
            staged.ForEach(file => file.Dispose());
        }
    }

    /// <summary>From the earliest timed record to just past the latest.</summary>
    private static TimeRange Extent(ObservationRowV1[] rows)
    {
        long[] ticks = [.. rows.Where(row => row.SessionRelativeTicks is not null).Select(row => row.SessionRelativeTicks!.Value / 100)];
        return new(ticks.Min(), ticks.Max() + 1);
    }

    private static TemporarySession Published(ObservationRowV1[] rows, int rowsPerSegment = 250_000)
    {
        var session = new TemporarySession();
        Publish(session.Store, rows, rowsPerSegment: rowsPerSegment);
        Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(session.Store, Committed).Outcome);
        session.Store.ReleaseSegmentReaders();
        return session;
    }

    /// <summary>Why a fresh read of the session's tile index refuses to count <paramref name="interval"/>.</summary>
    private static string Refusal(TemporarySession session, TimeRange interval, int columns)
    {
        SessionManifestV1 manifest = session.Store.Current!;
        TileIndexFile file = SessionTileIndex.Open(session.Store.Root, SessionTileIndex.NamedBy(manifest)!, manifest.SessionId);
        using TileReading reading = file.Read(session.Store.Root);
        var counted = new TimelineColumns(interval, columns, tallyMechanisms: true);
        return Assert.Throws<InvalidDataException>(() =>
        {
            foreach (string name in SessionSegments.Names(manifest))
            {
                _ = reading.CountInto(reading.Section(name), counted, CancellationToken.None);
            }
        }).Message;
    }

    /// <summary>The first segment's section and the tile index's path.</summary>
    private static (TileSection Section, string Path) TopOf(TemporarySession session)
    {
        SessionManifestV1 manifest = session.Store.Current!;
        StoreDependency named = SessionTileIndex.NamedBy(manifest)!;
        TileIndexFile file = SessionTileIndex.Open(session.Store.Root, named, manifest.SessionId);
        using TileReading reading = file.Read(session.Store.Root);
        return (reading.Section(SessionSegments.Names(manifest)[0]), Path.Combine(session.Path, named.Name));
    }

    private static void Flip(string path, long offset)
    {
        using FileStream stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        stream.Position = offset;
        int value = stream.ReadByte();
        stream.Position = offset;
        stream.WriteByte((byte)(value ^ 0x5A));
    }

    /// <summary>Every column's count and each mechanism's count in it, as counting the rows themselves gives them.</summary>
    private static List<(int Column, Mechanism? Mechanism, int Count)> Expected(ObservationRowV1[] rows, TimeRange interval, int columns)
    {
        var counted = new TimelineColumns(interval, columns, tallyMechanisms: true);
        foreach (ObservationRowV1 row in rows)
        {
            if (row.SessionRelativeTicks is { } nanoseconds && counted.ColumnOf(nanoseconds / 100) is { } column)
            {
                counted.Add(column, row.Mechanism);
            }
        }

        return Snapshot(counted);
    }

    private static List<(int Column, Mechanism? Mechanism, int Count)> Snapshot(TimelineColumns columns)
    {
        var values = new List<(int, Mechanism?, int)>();
        for (int column = 0; column < columns.Counts.Count; column++)
        {
            values.Add((column, null, columns.Counts[column]));
            values.AddRange(Kinds.Select(kind => (column, (Mechanism?)kind, columns.CountOf(column, kind))));
        }

        return values;
    }

    private static List<(int Column, Mechanism? Mechanism, int Count)> Drawn(SessionTimelineDetail detail)
    {
        var values = new List<(int, Mechanism?, int)>();
        for (int column = 0; column < detail.Buckets.Count; column++)
        {
            values.Add((column, null, detail.Buckets[column].ObservationCount));
            values.AddRange(Kinds.Select(kind => (column, (Mechanism?)kind,
                detail.MechanismLanes.Single(lane => lane.Mechanism == kind).Buckets[column].ObservationCount)));
        }

        return values;
    }

    /// <summary>
    /// Records in native order with session times that mostly advance in bursts, stand still, cross zero or are missing;
    /// with <paramref name="ordered"/> false, session times that jump backwards as well.
    /// </summary>
    private static ObservationRowV1[] RandomRows(Random random, bool ordered)
    {
        int count = random.Next(1, 1_500);
        long nanoseconds = random.NextInt64(-5_000_000, 5_000_000);
        var rows = new ObservationRowV1[count];
        for (int index = 0; index < count; index++)
        {
            nanoseconds += random.Next(5) switch
            {
                0 => 0,
                1 => random.Next(1, 100),
                2 => random.Next(100, 10_000),
                3 => random.Next(10_000, 5_000_000),
                _ => ordered ? random.Next(1, 1_000) : -random.Next(1, 100_000),
            };
            ObservationRowV1 row = Transfer(1_000 + index, ObservationKind.Send, AccountingSide.SendSide, 8, 100, (ulong)(index + 1))
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { Mechanism = Kinds[random.Next(Kinds.Length)] };
            rows[index] = random.Next(8) == 0 ? row : row with { SessionRelativeTicks = nanoseconds };
        }

        if (rows.All(row => row.SessionRelativeTicks is null))
        {
            rows[0] = rows[0] with { SessionRelativeTicks = nanoseconds };
        }

        return rows;
    }
}
