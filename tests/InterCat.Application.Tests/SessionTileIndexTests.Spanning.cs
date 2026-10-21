using System.Buffers.Binary;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// The level spanning a generation's segments (`contracts/tile-index-v1.md` §1): a zoom, a brush and a focus over many
/// segments count its tiles whole where they can and only the ranges it leaves from the segments' sections, exactly what
/// their rows count; it is kept only where it spans the generation's segments exactly, is refused when it cannot be read,
/// and stays within its bound while it is written.
/// </summary>
public sealed partial class SessionTileIndexTests
{
    [Theory(DisplayName = "I4: over many segments, a zoom, a brush, a group's lanes and a channel's ends count from the level spanning them exactly what their rows count, under every policy, and open no segment")]
    [InlineData(11)]
    [InlineData(29)]
    [InlineData(20_261_021)]
    public void ASpanningLevelCountsWhatRowsCount(int seed)
    {
        var random = new Random(seed);
        EvidencePolicy[] policies = Enum.GetValues<EvidencePolicy>();
        for (int trial = 0; trial < 3; trial++)
        {
            // Pairs talking a pair at a time, or taking turns record by record, in segments of a hundred records or two
            // thousand: tiles whose tallies are kept, and tiles whose are not.
            int pairs = random.Next(2, 12);
            int count = random.Next(8_000, 24_000);
            ObservationRowV1[] rows = trial % 2 == 0 ? Shifts(pairs, count) : Interleaved(pairs, count);
            int rowsPerSegment = random.Next(100, 2_000);
            using var tiled = new TemporarySession();
            using var twin = new TemporarySession();
            _ = Publish(tiled.Store, rows, rowsPerSegment: rowsPerSegment);
            _ = Publish(twin.Store, rows, rowsPerSegment: rowsPerSegment);
            Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(tiled.Store, Committed).Outcome);
            tiled.Store.ReleaseSegmentReaders();
            Assert.True(FileOf(tiled).Spans);
            Bound bound = Bind(twin.Store);
            ProcessInstanceId[] instances = [.. bound.Processes.Instances.Select(instance => instance.Id)];
            string[] keys = [.. bound.Relations.Relations.Where(relation => TransportRelationIndex.Relates(relation.Mechanism))
                .Select(relation => relation.StableKey)];
            TimeRange whole = Extent(rows);

            SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(tiled.Path));
            for (int query = 0; query < 10; query++)
            {
                // The whole extent, or anywhere in it or about it, in a column or a few or hundreds.
                long start = whole.StartTicks - 5 + random.NextInt64(0, whole.SpanTicks);
                TimeRange interval = query == 0 ? whole : new(start, start + 1 + random.NextInt64(0, whole.EndTicks + 5 - start));
                int columns = random.Next(3) == 0 ? random.Next(1, 6) : random.Next(1, 400);
                EvidencePolicy policy = policies[random.Next(policies.Length)];
                Assert.Equal(Expected(rows, interval, columns), Drawn(SessionTimelineQuery.Detail(reopened, interval, columns, Kinds)));
                Assert.Equal(Counted(bound, interval, policy), Stated(SessionIntervalQuery.Count(reopened, interval, policy)));

                // Some of the instances, or one, which counts its direction rows; a channel, perhaps at one end.
                ProcessInstanceId[] group = random.Next(3) == 0 ? [instances[random.Next(instances.Length)]]
                    : [.. instances.Where((_, index) => index < 2 || random.Next(3) != 0)];
                var owners = new TimelineFocus(null, group);
                Assert.Equal(
                    Focused(SessionTimelineQuery.Focused(twin.Store, interval, columns, owners, policy)),
                    Focused(SessionTimelineQuery.Focused(reopened, interval, columns, owners, policy)));
                var channel = new TimelineFocus(keys[random.Next(keys.Length)], [], end: random.Next(3) == 0 ? random.Next(2) : null);
                Assert.Equal(
                    Outcome(() => SessionTimelineQuery.Focused(twin.Store, interval, columns, channel, policy)),
                    Outcome(() => SessionTimelineQuery.Focused(reopened, interval, columns, channel, policy)));
            }

            Assert.Equal(0, reopened.SegmentReaderCache.Entries);
            Assert.Null(SessionDerivationCache.For(reopened.Current!).TilesProblem);
            reopened.ReleaseSegmentReaders();
            twin.Store.ReleaseSegmentReaders();
        }
    }

    [Fact(DisplayName = "S3: over a hundred segments, a brush counts the spanning level's tiles whole and reads a segment's section only at its ends, and a zoom only where a column boundary falls")]
    public void ABrushOfManySegmentsReadsTheSpanningLevel()
    {
        // 100,000 records 30 microseconds apart in a hundred segments, four pairs talking in turn: finest spanning tiles a
        // hundredth of a second wide, the narrowest power of ten no narrower than the segments' finest tiles spanning the
        // three seconds in at most one tile for each 256 records, and a top level of 31.
        ObservationRowV1[] rows = Shifts(4, 100_000);
        using var session = Published(rows, rowsPerSegment: 1_000);
        SessionManifestV1 manifest = session.Store.Current!;
        Assert.Equal(100, SessionSegments.Names(manifest).Count);
        TileIndexFile file = FileOf(session);
        Bound bound = Bind(session.Store);
        session.Store.ReleaseSegmentReaders();
        TimeRange whole = Extent(rows);
        using TileReading reading = file.Read(session.Store.Root);
        TileSection spanning = reading.Spanning();
        Assert.Equal((100_000L, 2, 301, 31), (spanning.Width, spanning.Levels, spanning.TileCount(0), spanning.TileCount(1)));
        Assert.Equal((1L, 30_000_700L, 100_000L), (spanning.First!.Value, spanning.Last!.Value, spanning.Spanned));
        Assert.Equal(Enumerable.Range(0, 100), spanning.Extents.Select(extent => extent.Section));
        Assert.All(spanning.Extents, extent => Assert.Equal(1_000, extent.Records));

        // The whole extent: its top level's tiles each from their tallies but the last, whose three records' tallies would
        // take more than a third of their bytes, as would its one child's; that child's range is left to the section
        // beneath it.
        (IntervalTally tally, TileRemainders left) = SpanBrush(reading, whole, EvidencePolicy.DirectOnly, bound);
        Assert.Equal((30, 2, 0), (reading.TilesOfTalliesRead, reading.TilesUntallied, reading.TilesOfRecordsRead));
        Assert.Equal(new TimeRange(30_000_100, 30_000_701), Assert.Single(left.Ranges));
        Assert.Equal(2, file.BlocksDecoded);
        CountBeneath(left);
        Assert.Equal(Counted(bound, whole, EvidencePolicy.DirectOnly), Stated(tally, bound));

        // About the middle third: whole tiles from their tallies, and at each end the range of the finest tile it falls
        // among, which the section beneath it counts.
        var third = new TimeRange(10_000_050, 20_050_050);
        int tallied = reading.TilesOfTalliesRead;
        (tally, left) = SpanBrush(reading, third, EvidencePolicy.DirectOnly, bound);
        Assert.Equal(18, reading.TilesOfTalliesRead - tallied);
        Assert.Equal([new TimeRange(10_000_050, 10_099_901), new TimeRange(20_000_200, 20_050_050)], left.Ranges);
        string[] names = [.. SessionSegments.Names(manifest).Order(StringComparer.Ordinal)];
        Assert.Equal([names[33], names[66]], reading.Beneath(left).Select(met => met.Section.Entry.Name));
        CountBeneath(left);
        Assert.Equal(Counted(bound, third, EvidencePolicy.DirectOnly), Stated(tally, bound));
        Assert.InRange(file.BlocksDecoded, 2, 30);

        // The whole extent in a hundred columns: whole tiles where a column holds them, and a range for each finest tile a
        // boundary falls among - not one in the gap between two tiles' records - which a section or two beneath it count.
        var columns = new TimelineColumns(whole, 100, tallyMechanisms: true);
        TileRemainders zoom = reading.SpanInto(columns, CancellationToken.None);
        Assert.InRange(zoom.Ranges.Count, 50, 99);
        List<(TileSection Section, List<TimeRange> Ranges)> zoomed = reading.Beneath(zoom);
        Assert.InRange(zoomed.Count, zoom.Ranges.Count, 2 * zoom.Ranges.Count);
        foreach ((TileSection section, List<TimeRange> ranges) in zoomed)
        {
            ranges.ForEach(range => Assert.True(reading.CountInto(section, columns, range, CancellationToken.None)));
        }

        Assert.Equal(Expected(rows, whole, 100), Snapshot(columns));

        // In 2,000 columns every finest tile but the last, whose three records fall in the last column, lies across a
        // boundary, and the ranges they leave, with no tile counted whole between them, are one, which each section counts
        // as before.
        var fine = new TimelineColumns(whole, 2_000, tallyMechanisms: true);
        TileRemainders across = reading.SpanInto(fine, CancellationToken.None);
        Assert.Equal(new TimeRange(1, 29_999_801), Assert.Single(across.Ranges));
        List<(TileSection Section, List<TimeRange> Ranges)> all = reading.Beneath(across);
        Assert.Equal(100, all.Count);
        all.ForEach(met => Assert.True(reading.CountInto(met.Section, fine, Assert.Single(met.Ranges), CancellationToken.None)));
        Assert.Equal(Expected(rows, whole, 2_000), Snapshot(fine));

        // The same through the queries a reopened window runs, which open no segment.
        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        Assert.Equal(Counted(bound, third, EvidencePolicy.AllIncludingConflicting),
            Stated(SessionIntervalQuery.Count(reopened, third, EvidencePolicy.AllIncludingConflicting)));
        Assert.Equal(Expected(rows, whole, 256), Drawn(SessionTimelineQuery.Detail(reopened, whole, 256, Kinds)));
        Assert.Equal(0, reopened.SegmentReaderCache.Entries);
        reopened.ReleaseSegmentReaders();

        // What the spanning level left to the sections beneath it, counted from them.
        void CountBeneath(TileRemainders remainders)
        {
            foreach ((TileSection section, List<TimeRange> ranges) in reading.Beneath(remainders))
            {
                ranges.ForEach(range => Assert.True(reading.CountInterval(section, range, EvidencePolicy.DirectOnly,
                    SlotsOfChannels(bound, EvidencePolicy.DirectOnly), tally, CancellationToken.None)));
            }
        }
    }

    [Fact(DisplayName = "I4: a section counts only its records in a range it is given within the columns' interval, and ranges that cut the interval count what it does")]
    public void ASectionCountsOnlyTheRangeItIsGiven()
    {
        // Three segments whose finest tiles hold three or four records each, cut among a finest tile's records.
        ObservationRowV1[] rows = Shifts(3, 90_000);
        using var session = Published(rows, rowsPerSegment: 30_000);
        SessionManifestV1 manifest = session.Store.Current!;
        Bound bound = Bind(session.Store);
        session.Store.ReleaseSegmentReaders();
        TimeRange whole = Extent(rows);
        TimeRange[] ranges = [new(whole.StartTicks, 123_457), new(123_457, 1_234_567), new(1_234_567, whole.EndTicks)];
        using TileReading reading = FileOf(session).Read(session.Store.Root);
        TileSection[] sections = [.. SessionSegments.Names(manifest).Select(reading.Section)];

        // A zoom: each range's records alone, in the interval's columns, and the ranges together the interval's.
        var cut = new TimelineColumns(whole, 37, tallyMechanisms: true);
        foreach (TimeRange range in ranges)
        {
            var alone = new TimelineColumns(whole, 37, tallyMechanisms: true);
            foreach (TileSection section in sections)
            {
                Assert.True(reading.CountInto(section, alone, range, CancellationToken.None));
                Assert.True(reading.CountInto(section, cut, range, CancellationToken.None));
            }

            Assert.Equal(rows.Count(row => row.SessionRelativeTicks is { } nanoseconds && range.Contains(nanoseconds / 100)), alone.Counts.Sum());
        }

        Assert.All(sections, section => Assert.Equal(1_000, section.Width));

        Assert.Equal(Expected(rows, whole, 37), Snapshot(cut));

        // An owner focus, under a policy admitting every binding, and a channel's ends, cut alike, count what they count over
        // the interval whole.
        bool[] members = [.. bound.Processes.Instances.Select((_, index) => index % 2 == 0)];
        TransportRelation relation = bound.Relations.Relations.First(candidate => TransportRelationIndex.Relates(candidate.Mechanism));
        var group = new TimelineColumns(whole, 37, tallyMechanisms: true);
        var channel = new TimelineColumns(whole, 37, tallyMechanisms: true);
        SessionTimelineQuery.ChannelEndColumns[] ends = Ends(relation, whole, 37);
        var groupWhole = new TimelineColumns(whole, 37, tallyMechanisms: true);
        var channelWhole = new TimelineColumns(whole, 37, tallyMechanisms: true);
        SessionTimelineQuery.ChannelEndColumns[] endsWhole = Ends(relation, whole, 37);
        foreach (TileSection section in sections)
        {
            foreach (TimeRange range in ranges)
            {
                Assert.True(reading.CountGroup(section, range, group, null, null, members, null, null, null,
                    EvidencePolicy.AllIncludingConflicting, CancellationToken.None));
                Assert.True(reading.CountChannel(section, range, channel, ends, relation.Channel, null, null, null, CancellationToken.None));
            }

            Assert.True(reading.CountGroup(section, groupWhole, null, null, members, null, null, null,
                EvidencePolicy.AllIncludingConflicting, CancellationToken.None));
            Assert.True(reading.CountChannel(section, channelWhole, endsWhole, relation.Channel, null, null, null, CancellationToken.None));
        }

        Assert.Equal(Snapshot(groupWhole), Snapshot(group));
        Assert.Equal(Snapshot(channelWhole), Snapshot(channel));
        Assert.Equal(Lanes(endsWhole), Lanes(ends));
        Assert.InRange(group.Counts.Sum(), rows.Length / 3, rows.Length - 1);
        Assert.InRange(channel.Counts.Sum(), 1, rows.Length - 1);
    }

    [Fact(DisplayName = "S4: a level spans the segments only where two or more hold a timed record and none's readings go backwards, and only a minor 4 index is current")]
    public void ALevelSpansOnlyOrderedSegments()
    {
        // One segment: its own levels are the session's.
        using (var one = Published(Shifts(2, 3_000)))
        {
            TileIndexFile file = FileOf(one);
            Assert.Equal((4, false), (file.Minor, file.Spans));
        }

        // Two segments, the second holding no timed record.
        ObservationRowV1[] made = Shifts(2, 4_000);
        ObservationRowV1[] untimed = [.. made[..3_000], .. made[3_000..].Select(row => row with { SessionRelativeTicks = null })];
        using (var half = Published(untimed, rowsPerSegment: 3_000))
        {
            Assert.Equal(2, SessionSegments.Names(half.Store.Current!).Count);
            Assert.False(FileOf(half).Spans);
        }

        // Segments some of whose readings go backwards: a zoom reads their rows, and counts the rest from their sections.
        var random = new Random(7);
        ObservationRowV1[] backwards = RandomRows(random, ordered: false);
        while (backwards.Length < 600)
        {
            backwards = RandomRows(random, ordered: false);
        }

        using var unordered = Published(backwards, rowsPerSegment: backwards.Length / 4);
        int goBack = Segments(unordered.Store).Count(segment => SegmentTimeTiles.Of(segment) is { First: not null, Ordered: false });
        unordered.Store.ReleaseSegmentReaders();
        Assert.InRange(goBack, 1, 5);
        Assert.False(FileOf(unordered).Spans);
        TimeRange whole = Extent(backwards);
        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(unordered.Path));
        Assert.Equal(Expected(backwards, whole, 77), Drawn(SessionTimelineQuery.Detail(reopened, whole, 77, Kinds)));
        Assert.Equal(goBack, reopened.SegmentReaderCache.Entries);
        reopened.ReleaseSegmentReaders();
    }

    [Theory(DisplayName = "I4: a damaged spanning section is refused, never misread: the query counts from the segments' rows instead")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ADamagedSpanningSectionIsRefused(int damage)
    {
        // Each case's session differs from the others', so no two share a manifest, nor what its derivations keep.
        ObservationRowV1[] rows = Shifts(3, 9_000 + damage);
        using var session = Published(rows, rowsPerSegment: 600);
        TileIndexFile file = FileOf(session);
        (long offset, long length) = SpanningOf(session);
        TileSection spanning;
        using (TileReading reading = file.Read(session.Store.Root))
        {
            spanning = reading.Spanning();
        }

        string path = PathOf(session);
        int headerLength = 78 + (2 * spanning.Mechanisms.Length) + (12 * spanning.Levels);
        (long at, string why) = damage switch
        {
            0 => (offset + 5, "the header of the spanning section does not match its checksum"),
            1 => (offset + headerLength + 9, "the extents of the spanning section does not match its checksum"),
            2 => (spanning.BlockAt(spanning.Levels - 1, 0).Offset + 2, $"block 0 of level {spanning.Levels - 1} of the spanning section does not match its checksum"),
            3 => (spanning.OwnerTalliesAt + spanning.OwnerTalliesLength - 2, "of the spanning section does not match its checksum"),
            _ => (offset + length - 2, "of the spanning section does not match its checksum"),
        };
        Flip(path, at);

        // Opening it checks only its directory; the query reading the damaged part refuses the index and counts from the
        // segments' rows what it would have counted.
        TimeRange whole = Extent(rows);
        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        Bound bound = Bind(session.Store);
        Assert.Equal(Counted(bound, whole, EvidencePolicy.DirectOnly), Stated(SessionIntervalQuery.Count(reopened, whole, EvidencePolicy.DirectOnly)));
        Assert.Contains(why, SessionDerivationCache.For(reopened.Current!).TilesProblem, StringComparison.Ordinal);
        Assert.NotEqual(0, reopened.SegmentReaderCache.Entries);
        reopened.ReleaseSegmentReaders();
        session.Store.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "I4: a spanning section whose extents are not its sections' is refused where a range it leaves reads one")]
    public void AnExtentNotASectionsIsRefused()
    {
        // The extent of the second section said to end a tick early, its checksum made again: the section's own header says
        // otherwise once a range the spanning section leaves reads it.
        ObservationRowV1[] rows = Shifts(3, 6_000);
        using var session = Published(rows, rowsPerSegment: 1_500);
        (long offset, _) = SpanningOf(session);
        TileSection spanning;
        using (TileReading reading = FileOf(session).Read(session.Store.Root))
        {
            spanning = reading.Spanning();
        }

        string path = PathOf(session);
        byte[] bytes = File.ReadAllBytes(path);
        int headerLength = 78 + (2 * spanning.Mechanisms.Length) + (12 * spanning.Levels);
        int extents = (int)offset + headerLength;
        int length = (spanning.Extents.Count * 28) + 4;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(extents + 28 + 12), spanning.Extents[1].Last - 1);
        Crc32C.Write(bytes.AsSpan(extents + length - 4), Crc32C.Compute(bytes.AsSpan(extents, length - 4)));
        File.WriteAllBytes(path, bytes);

        TimeRange second = new(spanning.Extents[1].First + 1, spanning.Extents[1].Last);
        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        Bound bound = Bind(session.Store);
        Assert.Equal(Counted(bound, second, EvidencePolicy.DirectOnly), Stated(SessionIntervalQuery.Count(reopened, second, EvidencePolicy.DirectOnly)));
        Assert.Equal("The persisted tile index is not readable: the spanning section does not state the extent of the section of "
            + $"'{SessionSegments.Names(session.Store.Current!).Order(StringComparer.Ordinal).ElementAt(1)}'.",
            SessionDerivationCache.For(reopened.Current!).TilesProblem);
        reopened.ReleaseSegmentReaders();
        session.Store.ReleaseSegmentReaders();
    }

    [Theory(DisplayName = "I4: a spanning section whose checksums hold but whose parts do not agree is refused: the query counts from the segments' rows instead")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AnInconsistentSpanningSectionIsRefused(int damage)
    {
        // A level of one block over 15 segments, each part's checksum made again after it is changed.
        ObservationRowV1[] rows = Shifts(3, 9_100 + damage);
        using var session = Published(rows, rowsPerSegment: 600);
        (long offset, long length) = SpanningOf(session);
        TileSection spanning;
        using (TileReading reading = FileOf(session).Read(session.Store.Root))
        {
            spanning = reading.Spanning();
        }

        Assert.Equal(1, spanning.Levels);
        string path = PathOf(session);
        byte[] bytes = File.ReadAllBytes(path);
        int header = (int)offset;
        int headerLength = 78 + (2 * spanning.Mechanisms.Length) + (12 * spanning.Levels);
        int extents = header + headerLength;
        int extentsLength = (spanning.Extents.Count * 28) + 4;
        string why;
        switch (damage)
        {
            case 0:
                // It says it spans a record more than its extents hold.
                Add(header + 8, 1);
                Seal(header, headerLength);
                why = "the spanning section's extents do not add up to what it spans.";
                break;
            case 1:
                // It and its first extent say so, and its tiles count one fewer.
                Add(header + 8, 1);
                Seal(header, headerLength);
                Add(extents + 20, 1);
                Seal(extents, extentsLength);
                why = "the spanning section's tiles do not count the records it spans.";
                break;
            case 2:
                // Its first finest tile names records it does not keep.
                (long block, int blockLength) = spanning.BlockAt(0, 0);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan((int)block + 16), 1);
                Seal((int)block, blockLength);
                why = "block 0 of level 0 of the spanning section holds a tile it does not lay out.";
                break;
            default:
                // The directory places it a byte later than the last segment's section ends.
                ReadOnlySpan<byte> footer = bytes.AsSpan(bytes.Length - SessionTileIndex.FooterLength);
                int directory = (int)BinaryPrimitives.ReadInt64LittleEndian(footer);
                int listed = BinaryPrimitives.ReadInt32LittleEndian(footer[8..]);
                Add(directory + listed - 16, 1);
                Crc32C.Write(bytes.AsSpan(bytes.Length - SessionTileIndex.FooterLength + 16), Crc32C.Compute(bytes.AsSpan(directory, listed)));
                Seal(bytes.Length - SessionTileIndex.FooterLength, SessionTileIndex.FooterLength);
                why = "its directory places the level spanning its segments outside its sections.";
                break;
        }

        File.WriteAllBytes(path, bytes);
        TimeRange whole = Extent(rows);
        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        Bound bound = Bind(session.Store);
        Assert.Equal(Counted(bound, whole, EvidencePolicy.DirectOnly), Stated(SessionIntervalQuery.Count(reopened, whole, EvidencePolicy.DirectOnly)));
        Assert.Equal("The persisted tile index is not readable: " + why, SessionDerivationCache.For(reopened.Current!).TilesProblem);
        Assert.NotEqual(0, reopened.SegmentReaderCache.Entries);
        reopened.ReleaseSegmentReaders();
        session.Store.ReleaseSegmentReaders();
        _ = length;

        void Add(int at, long increase) =>
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at)) + increase);

        void Seal(int at, int partLength) =>
            Crc32C.Write(bytes.AsSpan(at + partLength - 4), Crc32C.Compute(bytes.AsSpan(at, partLength - 4)));
    }

    [Fact(DisplayName = "I14: a level spanning a section of a segment the generation does not name is not counted: each query counts the segments' own sections")]
    public void ALevelSpanningAnotherSegmentIsNotCounted()
    {
        // The generation's three segments, and its first again under a name it does not have: four sections, and a level
        // spanning their records, the first segment's twice.
        ObservationRowV1[] rows = Shifts(3, 6_000);
        TimeRange whole = Extent(rows);
        using var session = Published(rows, rowsPerSegment: 2_000);
        SessionManifestV1 published = session.Store.Current!;
        Bound bound = Bind(session.Store);
        StoreDependency[] segments = SessionOverviewIndex.ObservationSegments(published);
        Republish(session, tiles: content => SessionTileIndex.Write(content, published.SessionId, published.Generation,
            [.. segments.Zip(bound.Segments), (segments[0] with { Name = "seg-9999999999-0000.icats" }, bound.Segments[0])],
            bound.Processes, bound.Relations, CancellationToken.None));
        TileIndexFile file = FileOf(session);
        Assert.True(file.Spans);
        Assert.Equal(4, file.Segments.Count);
        Assert.False(file.SpansExactly(SessionOverviewIndex.ObservationSegments(session.Store.Current!)));

        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        Assert.Equal(Counted(bound, whole, EvidencePolicy.DirectOnly), Stated(SessionIntervalQuery.Count(reopened, whole, EvidencePolicy.DirectOnly)));
        Assert.Equal(Expected(rows, whole, 64), Drawn(SessionTimelineQuery.Detail(reopened, whole, 64, Kinds)));
        var group = new TimelineFocus(null, [.. bound.Processes.Instances.Select(instance => instance.Id)]);
        using var twin = new TemporarySession();
        _ = Publish(twin.Store, rows, rowsPerSegment: 2_000);
        Assert.Equal(
            Focused(SessionTimelineQuery.Focused(twin.Store, whole, 64, group, EvidencePolicy.AllIncludingConflicting)),
            Focused(SessionTimelineQuery.Focused(reopened, whole, 64, group, EvidencePolicy.AllIncludingConflicting)));
        Assert.Equal(0, reopened.SegmentReaderCache.Entries);
        Assert.Null(SessionDerivationCache.For(reopened.Current!).TilesProblem);
        reopened.ReleaseSegmentReaders();
        twin.Store.ReleaseSegmentReaders();
        session.Store.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "I14: a minor 3 tile index, which keeps no level spanning its segments, is counted from its sections, and a checkpoint publishes it again with one")]
    public void AMinorThreeTileIndexIsPublishedAgain()
    {
        ObservationRowV1[] rows = Shifts(3, 7_000);
        TimeRange whole = Extent(rows);
        using var session = Published(rows, rowsPerSegment: 700);
        SessionStore store = session.Store;
        string path = PathOf(session);

        // The same file as revision 460 wrote it: its sections, and a directory ending after them.
        Republish(session, tiles: content => content.Write(MinorThree(File.ReadAllBytes(path))));
        Assert.Equal((3, false), (FileOf(session).Minor, FileOf(session).Spans));
        store.ReleaseSegmentReaders();
        Bound bound = Bind(store);
        SessionStore viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        Assert.Equal(Counted(bound, whole, EvidencePolicy.DirectOnly), Stated(SessionIntervalQuery.Count(viewer, whole, EvidencePolicy.DirectOnly)));
        Assert.Equal(Expected(rows, whole, 90), Drawn(SessionTimelineQuery.Detail(viewer, whole, 90, Kinds)));
        Assert.Equal(0, viewer.SegmentReaderCache.Entries);
        Assert.Null(SessionDerivationCache.For(viewer.Current!).TilesProblem);
        viewer.ReleaseSegmentReaders();

        // Not current: the next checkpoint publishes it again, spanning its segments.
        CheckpointPublication again = SessionCheckpoints.Publish(store, Committed);
        Assert.True(again.Outcome == CheckpointOutcome.Published, again.Reason);
        Assert.Equal((4, true), (FileOf(session).Minor, FileOf(session).Spans));
        Assert.Equal(CheckpointOutcome.AlreadyCurrent, SessionCheckpoints.Publish(store, Committed).Outcome);
        store.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "S2: past its bound, the spanning level's finest tiles are made ten times wider until their tally entries fit, and where none do it is given up; a zoom, a brush and a focus count the same either way")]
    public void TheSpanningLevelKeepsWithinItsBound()
    {
        // Eight pairs taking turns: every finest spanning tile holds every process and channel, 64 entries in all.
        ObservationRowV1[] rows = Interleaved(8, 40_000);
        TimeRange whole = Extent(rows);
        using var session = Published(rows, rowsPerSegment: 2_000);
        using var twin = new TemporarySession();
        _ = Publish(twin.Store, rows, rowsPerSegment: 2_000);
        SessionManifestV1 published = session.Store.Current!;
        long planned;
        using (TileReading reading = FileOf(session).Read(session.Store.Root))
        {
            planned = reading.Spanning().Width;
        }

        Bound bound = Bind(session.Store);
        foreach (long entries in new long[] { 5_000, 500, 0 })
        {
            Republish(session, tiles: content => SessionTileIndex.Write(content, published.SessionId, published.Generation,
                [.. SessionOverviewIndex.ObservationSegments(published).Zip(bound.Segments)], bound.Processes, bound.Relations,
                CancellationToken.None, spanningEntries: entries));
            TileIndexFile file = FileOf(session);
            if (entries == 0)
            {
                Assert.False(file.Spans);
            }
            else
            {
                using TileReading reading = file.Read(session.Store.Root);
                Assert.Equal(planned * (entries == 5_000 ? 10 : 100), reading.Spanning().Width);
            }

            SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
            Assert.Equal(Counted(bound, whole, EvidencePolicy.DirectOnly), Stated(SessionIntervalQuery.Count(reopened, whole, EvidencePolicy.DirectOnly)));
            Assert.Equal(Expected(rows, whole, 33), Drawn(SessionTimelineQuery.Detail(reopened, whole, 33, Kinds)));
            var group = new TimelineFocus(null, [.. bound.Processes.Instances.Take(5).Select(instance => instance.Id)]);
            Assert.Equal(
                Focused(SessionTimelineQuery.Focused(twin.Store, whole, 41, group, EvidencePolicy.AllIncludingConflicting)),
                Focused(SessionTimelineQuery.Focused(reopened, whole, 41, group, EvidencePolicy.AllIncludingConflicting)));
            Assert.Equal(0, reopened.SegmentReaderCache.Entries);
            reopened.ReleaseSegmentReaders();
        }

        session.Store.ReleaseSegmentReaders();
        twin.Store.ReleaseSegmentReaders();
    }

    /// <summary>The current generation's tile index, opened afresh.</summary>
    private static TileIndexFile FileOf(TemporarySession session)
    {
        SessionManifestV1 manifest = session.Store.Current!;
        return SessionTileIndex.Open(session.Store.Root, SessionTileIndex.NamedBy(manifest)!, manifest.SessionId);
    }

    private static string PathOf(TemporarySession session) =>
        Path.Combine(session.Path, SessionTileIndex.NamedBy(session.Store.Current!)!.Name);

    /// <summary>Where the current generation's tile index says its spanning section lies, from the end of its directory.</summary>
    private static (long Offset, long Length) SpanningOf(TemporarySession session)
    {
        byte[] bytes = File.ReadAllBytes(PathOf(session));
        ReadOnlySpan<byte> footer = bytes.AsSpan(bytes.Length - SessionTileIndex.FooterLength);
        long directory = BinaryPrimitives.ReadInt64LittleEndian(footer);
        int length = BinaryPrimitives.ReadInt32LittleEndian(footer[8..]);
        ReadOnlySpan<byte> trailer = bytes.AsSpan((int)(directory + length - 16), 16);
        return (BinaryPrimitives.ReadInt64LittleEndian(trailer), BinaryPrimitives.ReadInt64LittleEndian(trailer[8..]));
    }

    /// <summary>A minor 4 tile index as minor 3 kept it: its sections, and its directory ending after them.</summary>
    private static byte[] MinorThree(byte[] bytes)
    {
        ReadOnlySpan<byte> footer = bytes.AsSpan(bytes.Length - SessionTileIndex.FooterLength);
        long directory = BinaryPrimitives.ReadInt64LittleEndian(footer);
        int length = BinaryPrimitives.ReadInt32LittleEndian(footer[8..]);
        int sections = BinaryPrimitives.ReadInt32LittleEndian(footer[12..]);
        long spanning = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan((int)(directory + length - 16)));
        long end = spanning == 0 ? directory : spanning;
        byte[] listed = bytes.AsSpan((int)directory, length - 16).ToArray();
        byte[] header = bytes.AsSpan(0, SessionTileIndex.HeaderLength).ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), 3);
        Crc32C.Write(header.AsSpan(44), Crc32C.Compute(header.AsSpan(0, 44)));
        byte[] trailer = new byte[SessionTileIndex.FooterLength];
        BinaryPrimitives.WriteInt64LittleEndian(trailer, end);
        BinaryPrimitives.WriteInt32LittleEndian(trailer.AsSpan(8), listed.Length);
        BinaryPrimitives.WriteInt32LittleEndian(trailer.AsSpan(12), sections);
        Crc32C.Write(trailer.AsSpan(16), Crc32C.Compute(listed));
        Crc32C.Write(trailer.AsSpan(28), Crc32C.Compute(trailer.AsSpan(0, 28)));
        return [.. header, .. bytes.AsSpan(SessionTileIndex.HeaderLength, (int)end - SessionTileIndex.HeaderLength), .. listed, .. trailer];
    }

    /// <summary>Each channel a brush under <paramref name="policy"/> draws, by its place among those it draws.</summary>
    private static int[] SlotsOfChannels(Bound bound, EvidencePolicy policy)
    {
        int[] slotOfChannel = new int[bound.Relations.Channels];
        Array.Fill(slotOfChannel, -1);
        int drawn = 0;
        foreach (TransportRelation relation in bound.Relations.Relations)
        {
            if (TransportRelationIndex.Relates(relation.Mechanism) && SessionOverviewProjector.Admitted(relation.Strength, policy))
            {
                slotOfChannel[relation.Channel] = drawn++;
            }
        }

        return slotOfChannel;
    }

    /// <summary>A brush of the spanning section alone: what it counts whole, and the ranges it leaves.</summary>
    private static (IntervalTally Tally, TileRemainders Left) SpanBrush(TileReading reading, TimeRange interval, EvidencePolicy policy, Bound bound)
    {
        int[] slotOfChannel = SlotsOfChannels(bound, policy);
        var tally = new IntervalTally(bound.Processes.Instances.Count * TimelineColumns.SlotCount, slotOfChannel.Count(slot => slot >= 0));
        return (tally, reading.SpanInterval(interval, policy, slotOfChannel, tally, CancellationToken.None));
    }

    /// <summary>A tally counted from tiles under the direct-only policy, as <see cref="Stated(SessionIntervalCounts)"/> says a brush's counts.</summary>
    private static List<string> Stated(IntervalTally tally, Bound bound)
    {
        int[] slotOfChannel = SlotsOfChannels(bound, EvidencePolicy.DirectOnly);
        Dictionary<int, TransportRelation> byChannel = bound.Relations.Relations.ToDictionary(relation => relation.Channel);
        var edges = new Dictionary<string, long>(StringComparer.Ordinal);
        var channels = new Dictionary<string, long>(StringComparer.Ordinal);
        for (int channel = 0; channel < slotOfChannel.Length; channel++)
        {
            if (slotOfChannel[channel] is >= 0 and int slot && tally.Channels[slot] > 0)
            {
                TransportRelation relation = byChannel[channel];
                string edge = SessionOverviewProjector.EdgeKeyOf(relation);
                edges[edge] = edges.GetValueOrDefault(edge) + tally.Channels[slot];
                channels[relation.StableKey] = tally.Channels[slot];
            }
        }

        int slots = TimelineColumns.SlotCount;
        return
        [
            $"observed {tally.Observed}, graph {tally.Graph}",
            .. edges.Select(entry => $"edge {entry.Key}: {entry.Value}").Order(StringComparer.Ordinal),
            .. channels.Select(entry => $"channel {entry.Key}: {entry.Value}").Order(StringComparer.Ordinal),
            .. Enumerable.Range(0, bound.Processes.Instances.Count).SelectMany(instance => Enumerable.Range(0, slots)
                .Where(slot => tally.Processes[(instance * slots) + slot] > 0)
                .Select(slot => $"process {bound.Processes.Instances[instance].Id} {TimelineColumns.MechanismAt(slot)}: {tally.Processes[(instance * slots) + slot]}"))
                .Order(StringComparer.Ordinal),
        ];
    }
}
