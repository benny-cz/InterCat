using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// A generation's persisted tiles are §12.1 S4's deeper levels (`contracts/tile-index-v1.md`): a zoom of a reopened
/// session counts from them exactly what its rows count, opens no segment, and reads in proportion to what it draws;
/// a part of them that cannot be read is refused, never misread, and the zoom counts from the segments instead.
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
            Assert.Contains("the records of block 0 of the section", Refusal(session, first, 2), StringComparison.Ordinal);

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
        SessionManifestV1 published = store.Current!;
        long next = store.NextGeneration;
        var staged = new List<StoreStagingFile>();
        try
        {
            foreach (StoreDependency index in published.Dependencies.Where(dependency => dependency.Kind == StoreDependencyKind.Index
                && SessionTileIndex.NamedBy(published) != dependency))
            {
                StoreStagingFile file = store.Stage(index.Name.Replace(
                    published.Generation.ToString("D10", System.Globalization.CultureInfo.InvariantCulture),
                    next.ToString("D10", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal),
                    StoreDependencyKind.Index);
                staged.Add(file);
                file.Content.Write(File.ReadAllBytes(Path.Combine(session.Path, index.Name)));
                _ = file.Complete();
            }

            Assert.Equal(3, staged.Count);
            _ = store.CommitIndex(staged, published.Generation, Committed, next);
        }
        finally
        {
            staged.ForEach(file => file.Dispose());
        }

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
        long next = store.NextGeneration;
        var staged = new List<StoreStagingFile>();
        try
        {
            foreach (StoreDependency index in published.Dependencies.Where(dependency => dependency.Kind == StoreDependencyKind.Index))
            {
                StoreStagingFile file = store.Stage(index.Name.Replace(
                    published.Generation.ToString("D10", System.Globalization.CultureInfo.InvariantCulture),
                    next.ToString("D10", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal),
                    StoreDependencyKind.Index);
                staged.Add(file);
                if (index == SessionTileIndex.NamedBy(published))
                {
                    (StoreDependency, SegmentReaderV1)[] described =
                    [
                        .. segments.Select((segment, ordinal) => (ordinal == 1 ? segment with { Digest = "sha256:" + new string('0', 64) } : segment,
                            SessionSegments.Open(store, published, segment.Name))),
                    ];
                    _ = SessionTileIndex.Write(file.Content, published.SessionId, published.Generation, described, CancellationToken.None);
                }
                else
                {
                    file.Content.Write(File.ReadAllBytes(Path.Combine(session.Path, index.Name)));
                }

                _ = file.Complete();
            }

            _ = store.CommitIndex(staged, published.Generation, Committed, next);
        }
        finally
        {
            staged.ForEach(file => file.Dispose());
        }

        store.ReleaseSegmentReaders();
        SessionStore viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        TileIndexFile tiles = SessionTileIndex.Open(viewer.Root, SessionTileIndex.NamedBy(viewer.Current!)!, viewer.Current!.SessionId);
        Assert.Equal([true, false, true], SessionOverviewIndex.ObservationSegments(viewer.Current!).Select(tiles.Describes));
        Assert.Equal(Expected(rows, whole, 30), Drawn(SessionTimelineQuery.Detail(viewer, whole, 30, Kinds)));
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

        // A zoom reads the backward segment's rows, and opens no other.
        SessionStore reopened = SessionStore.OpenExisting(LocalOwnedDirectory.Open(session.Path));
        Assert.Equal(Expected(rows, all, 9), Drawn(SessionTimelineQuery.Detail(reopened, all, 9, Kinds)));
        Assert.Equal(1, reopened.SegmentReaderCache.Entries);
        reopened.ReleaseSegmentReaders();
    }

    /// <summary>
    /// A send at <paramref name="tick"/>: its native reading, and its session time in nanoseconds as the test clock places
    /// that reading, so a query passes over a segment by the readings it holds as it would a capture's.
    /// </summary>
    private static ObservationRowV1 Send(int index, long tick) => Transfer(tick, ObservationKind.Send, AccountingSide.SendSide, 8,
        100, (ulong)(index + 1)).Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = tick * 100 };

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
