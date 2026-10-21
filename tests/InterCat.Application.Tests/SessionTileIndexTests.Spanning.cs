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

    [Fact(DisplayName = "S4: a level spans the segments only where two or more hold a timed record and none's readings go backwards")]
    public void ALevelSpansOnlyOrderedSegments()
    {
        // One segment: its own levels are the session's.
        using (var one = Published(Shifts(2, 3_000)))
        {
            TileIndexFile file = FileOf(one);
            Assert.Equal((6, false), (file.Minor, file.Spans));
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
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
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
        int headerLength = 110 + (2 * spanning.Mechanisms.Length) + (12 * spanning.Levels);
        (long at, string why) = damage switch
        {
            0 => (offset + 5, "the header of the spanning section does not match its checksum"),
            1 => (offset + headerLength + 9, "the extents of the spanning section does not match its checksum"),
            2 => (spanning.BlockAt(spanning.Levels - 1, 0).Offset + 2, $"block 0 of level {spanning.Levels - 1} of the spanning section does not match its checksum"),
            3 => (spanning.OwnerTalliesAt + spanning.OwnerTalliesLength - 2, "of the spanning section does not match its checksum"),
            4 => (spanning.ChannelTalliesAt + spanning.ChannelTalliesLength - 2, "of the spanning section does not match its checksum"),
            5 => (spanning.SumsAt + spanning.SumsLength - 2, "the byte sums of block 0 of level 0 of the spanning section does not match its checksum"),
            _ => (offset + length - 2, "the byte tallies of block 0 of level 0 of the spanning section does not match its checksum"),
        };
        Assert.True(spanning.KeepsByteTallies);
        Flip(path, at);

        // Opening it checks only its directory; the query reading the damaged part refuses the index and counts from the
        // segments' rows what it would have counted: a brush, a zoom's byte lanes, which alone read the byte sums, or a
        // brush's bytes or a group's byte lanes, which alone read the byte tallies.
        TimeRange whole = Extent(rows);
        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        Bound bound = Bind(session.Store);
        if (damage < 5)
        {
            Assert.Equal(Counted(bound, whole, EvidencePolicy.DirectOnly), Stated(SessionIntervalQuery.Count(reopened, whole, EvidencePolicy.DirectOnly)));
        }
        else
        {
            using var twin = new TemporarySession();
            _ = Publish(twin.Store, rows, rowsPerSegment: 600);
            if (damage < 7)
            {
                Assert.Equal(LaneBytes(twin.Store, whole, 3), LaneBytes(reopened, whole, 3));
                Assert.Equal(Brushed(twin.Store, whole, EvidencePolicy.DirectOnly), Brushed(reopened, whole, EvidencePolicy.DirectOnly));
            }
            else
            {
                ProcessInstanceId[] group = [.. bound.Processes.Instances.Take(2).Select(instance => instance.Id)];
                Assert.Equal(
                    OwnerBytes(twin.Store, whole, 1, 1, group, EvidencePolicy.DirectOnly),
                    OwnerBytes(reopened, whole, 1, 1, group, EvidencePolicy.DirectOnly));
            }

            twin.Store.ReleaseSegmentReaders();
        }

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
        int headerLength = 110 + (2 * spanning.Mechanisms.Length) + (12 * spanning.Levels);
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
        int headerLength = 110 + (2 * spanning.Mechanisms.Length) + (12 * spanning.Levels);
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

    [Theory(DisplayName = "I14: a minor 3, 4 or 5 tile index, which keeps no byte tallies, before minor 5 no byte sums and at minor 3 no level spanning its segments, is counted from as before, what it does not keep read from the segments, and a checkpoint publishes it again with all of them")]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void AnEarlierTileIndexIsPublishedAgain(int minor)
    {
        ObservationRowV1[] rows = Shifts(3, 7_000 + minor);
        TimeRange whole = Extent(rows);
        TimeRange half = new(whole.StartTicks + (whole.SpanTicks / 4), whole.EndTicks - (whole.SpanTicks / 4));
        using var session = Published(rows, rowsPerSegment: 700);
        SessionStore store = session.Store;
        string path = PathOf(session);
        (TransportBytes[] lanes, TransportBytes[] interval) = (LaneBytes(store, whole, 70), IntervalBytes(store, whole, 70));
        string[] brushed = Brushed(store, half, EvidencePolicy.IncludeCorrelated);
        ProcessInstanceId[] group = [.. Bind(store).Processes.Instances.Take(2).Select(instance => instance.Id)];
        string[] owned = OwnerBytes(store, half, 70, 35, group, EvidencePolicy.IncludeCorrelated);

        // The same file as revision 460, 701 or 704 wrote it: no byte tallies, before minor 5 no byte sums or measures, and
        // at minor 3 a directory ending after the segments' sections.
        Republish(session, tiles: content => content.Write(Earlier(File.ReadAllBytes(path), (ushort)minor)));
        Assert.Equal((minor, minor >= 4), (FileOf(session).Minor, FileOf(session).Spans));
        store.ReleaseSegmentReaders();
        Bound bound = Bind(store);
        SessionStore viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        Assert.Equal(Counted(bound, whole, EvidencePolicy.DirectOnly), Stated(SessionIntervalQuery.Count(viewer, whole, EvidencePolicy.DirectOnly)));
        Assert.Equal(Expected(rows, whole, 90), Drawn(SessionTimelineQuery.Detail(viewer, whole, 90, Kinds)));
        Assert.Equal(0, viewer.SegmentReaderCache.Entries);
        Assert.Equal(lanes, LaneBytes(viewer, whole, 70));
        Assert.Equal(interval, IntervalBytes(viewer, whole, 70));
        Assert.Equal(minor >= 5, viewer.SegmentReaderCache.Entries == 0);
        Assert.Equal(owned, OwnerBytes(viewer, half, 70, 35, group, EvidencePolicy.IncludeCorrelated));
        Assert.NotEqual(0, viewer.SegmentReaderCache.Entries);
        Assert.Equal(brushed, Brushed(viewer, half, EvidencePolicy.IncludeCorrelated));
        Assert.NotEqual(0, viewer.SegmentReaderCache.Entries);
        Assert.Null(SessionDerivationCache.For(viewer.Current!).TilesProblem);
        viewer.ReleaseSegmentReaders();

        // Not current: the next checkpoint publishes it again, spanning its segments and keeping their bytes.
        CheckpointPublication again = SessionCheckpoints.Publish(store, Committed);
        Assert.True(again.Outcome == CheckpointOutcome.Published, again.Reason);
        Assert.Equal((6, true), (FileOf(session).Minor, FileOf(session).Spans));
        Assert.Equal(CheckpointOutcome.AlreadyCurrent, SessionCheckpoints.Publish(store, Committed).Outcome);
        store.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "S2: past its bound, the spanning level's finest tiles are made ten times wider until their tally entries fit, and where none do it is given up; a zoom, a brush, a focus and a zoom's byte lanes count the same either way")]
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
            foreach (int columns in new[] { 1, 3, 33 })
            {
                Assert.Equal(LaneBytes(twin.Store, whole, columns), LaneBytes(reopened, whole, columns));
            }

            Assert.Equal(Brushed(twin.Store, whole, EvidencePolicy.AllIncludingConflicting),
                Brushed(reopened, whole, EvidencePolicy.AllIncludingConflicting));
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

    /// <summary>
    /// A minor 6 tile index as an earlier <paramref name="minor"/> kept it: at minor 5 each section's header without where
    /// its byte tallies lie, its tiles without their link to them, its blocks' trailers without where they end, and no
    /// byte tallies; at minors 3 and 4 the same of its byte sums too, and its records without their measures; at minor 4
    /// its spanning section the same way, and at minor 3 none, its directory ending after the sections.
    /// </summary>
    private static byte[] Earlier(byte[] bytes, ushort minor) => Rewritten(bytes, minor, _ => false, unsumSpanning: false);

    /// <summary>
    /// A minor 6 tile index whose sections <paramref name="unsum"/> chooses, and its spanning section where
    /// <paramref name="unsumSpanning"/>, keep no bytes, as a section reading its rows refuses keeps none: no byte sums or
    /// byte tallies, each tile's links to them and each block's ends of them zero, and each record's measure none.
    /// </summary>
    private static byte[] Unsummed(byte[] bytes, Func<int, bool> unsum, bool unsumSpanning) =>
        Rewritten(bytes, 6, unsum, unsumSpanning);

    private static byte[] Rewritten(byte[] bytes, ushort minor, Func<int, bool> unsum, bool unsumSpanning)
    {
        ReadOnlySpan<byte> footer = bytes.AsSpan(bytes.Length - SessionTileIndex.FooterLength);
        int directory = (int)BinaryPrimitives.ReadInt64LittleEndian(footer);
        int sections = BinaryPrimitives.ReadInt32LittleEndian(footer[12..]);
        int at = directory;
        at += 1 + bytes[at];
        at += 1 + bytes[at];
        at += 4 + 4 + 32;
        var output = new MemoryStream();
        output.Write(new byte[SessionTileIndex.HeaderLength]);
        var listed = new MemoryStream();
        listed.Write(bytes.AsSpan(directory, at - directory));
        for (int section = 0; section < sections; section++)
        {
            int nameAt = at;
            at += 1 + bytes[at];
            at += 8;
            at += 1 + bytes[at];
            int offset = (int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at));
            int length = (int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at + 8));
            listed.Write(bytes.AsSpan(nameAt, at - nameAt));
            byte[] kept = bytes.AsSpan(offset, length).ToArray();
            byte[] made = minor < 6 || unsum(section) ? RewrittenSection(kept, spanning: false, minor, summed: !unsum(section)) : kept;
            WriteI64(listed, output.Length);
            WriteI64(listed, made.Length);
            output.Write(made);
            at += 16;
        }

        int spanningOffset = (int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at));
        int spanningLength = (int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at + 8));
        if (minor >= 4)
        {
            byte[]? kept = spanningLength == 0 ? null : bytes.AsSpan(spanningOffset, spanningLength).ToArray();
            byte[]? made = kept is null || (minor >= 6 && !unsumSpanning) ? kept
                : RewrittenSection(kept, spanning: true, minor, summed: !unsumSpanning);
            WriteI64(listed, made is null ? 0 : output.Length);
            WriteI64(listed, made?.Length ?? 0);
            output.Write(made ?? []);
        }

        byte[] list = listed.ToArray();
        long directoryAt = output.Length;
        output.Write(list);
        byte[] trailer = new byte[SessionTileIndex.FooterLength];
        BinaryPrimitives.WriteInt64LittleEndian(trailer, directoryAt);
        BinaryPrimitives.WriteInt32LittleEndian(trailer.AsSpan(8), list.Length);
        BinaryPrimitives.WriteInt32LittleEndian(trailer.AsSpan(12), sections);
        Crc32C.Write(trailer.AsSpan(16), Crc32C.Compute(list));
        Crc32C.Write(trailer.AsSpan(28), Crc32C.Compute(trailer.AsSpan(0, 28)));
        output.Write(trailer);
        byte[] file = output.ToArray();
        bytes.AsSpan(0, SessionTileIndex.HeaderLength).CopyTo(file);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(10), minor);
        Crc32C.Write(file.AsSpan(44), Crc32C.Compute(file.AsSpan(0, 44)));
        return file;
    }

    /// <summary>
    /// One minor 6 section, or the spanning section, as <paramref name="minor"/> lays it out, keeping its bytes where
    /// <paramref name="summed"/> and the minor keeps any: at minor 6 a section keeping none has its tiles' links and its
    /// blocks' ends of byte sums and byte tallies zero, and each record's measure none; minor 5 keeps no byte tallies, nor
    /// a place for them in its header, tiles and blocks; minors 3 and 4 keep no measures, byte sums or byte tallies, nor a
    /// place for them. See <see cref="Rewritten"/>.
    /// </summary>
    private static byte[] RewrittenSection(byte[] section, bool spanning, int minor, bool summed)
    {
        int at = spanning ? 4 + 4 + 8 + 8 + 8 + 8 : 4 + 4 + 1 + 8 + 8 + 8;
        int mechanisms = section[at];
        at += 1 + (2 * mechanisms);
        int levels = section[at++];
        int levelsAt = at;
        int[] counts = new int[levels];
        long[] offsets = new long[levels];
        for (int level = 0; level < levels; level++)
        {
            counts[level] = BinaryPrimitives.ReadInt32LittleEndian(section.AsSpan(at));
            offsets[level] = BinaryPrimitives.ReadInt64LittleEndian(section.AsSpan(at + 4));
            at += 12;
        }

        // A minor 6 header's parts: the records, where a segment's section keeps them, then the owner and channel tallies,
        // the byte sums and the byte tallies, each where it begins and how long it is.
        int regionsAt = at;
        long[] regions = [.. Enumerable.Range(0, spanning ? 8 : 10).Select(field => BinaryPrimitives.ReadInt64LittleEndian(section.AsSpan(at + (8 * field))))];
        int headerLength = at + (8 * regions.Length) + 4;
        int extentsLength = spanning ? (BinaryPrimitives.ReadInt32LittleEndian(section.AsSpan(4)) * 28) + 4 : 0;
        int tileBytes = 40 + (4 * mechanisms);
        int keptTileBytes = (minor >= 6 ? 40 : minor >= 5 ? 36 : 32) + (4 * mechanisms);
        int parts = spanning ? 0 : 2;
        bool keepsBytes = summed && minor >= 5;

        // The records, each with its measure where it keeps bytes, none where it keeps none at minor 5 or 6, and without one
        // before; tile by tile, each with its checksum, and where each tile's begin.
        var records = new MemoryStream();
        int[] links = [];
        if (!spanning && levels > 0)
        {
            int recordsAt = (int)regions[0];
            int recordsLength = (int)regions[1];
            int[] oldLinks = new int[counts[0]];
            for (int tile = 0; tile < counts[0]; tile++)
            {
                long tileAt = offsets[0] + ((long)(tile / SessionTileIndex.TilesPerBlock) * ((SessionTileIndex.TilesPerBlock * tileBytes) + 24))
                    + ((tile % SessionTileIndex.TilesPerBlock) * tileBytes);
                oldLinks[tile] = BinaryPrimitives.ReadInt32LittleEndian(section.AsSpan((int)tileAt + 16));
            }

            links = new int[counts[0]];
            for (int tile = 0; tile < counts[0]; tile++)
            {
                int read = recordsAt + oldLinks[tile];
                int to = recordsAt + (tile + 1 < counts[0] ? oldLinks[tile + 1] : recordsLength) - 4;
                links[tile] = (int)records.Length;
                int start = (int)records.Length;
                while (read < to)
                {
                    for (int field = 0; field < 4; field++)
                    {
                        WriteLeb(records, ReadLeb(section, ref read));
                    }

                    ulong measure = ReadLeb(section, ref read);
                    ulong measured = measure > 4 ? ReadLeb(section, ref read) : 0;
                    if (keepsBytes)
                    {
                        WriteLeb(records, measure);
                        if (measure > 4)
                        {
                            WriteLeb(records, measured);
                        }
                    }
                    else if (minor >= 5)
                    {
                        WriteLeb(records, 0);
                    }
                }

                byte[] checksum = new byte[4];
                Crc32C.Write(checksum, Crc32C.Compute(records.GetBuffer().AsSpan(start, (int)records.Length - start)));
                records.Write(checksum);
            }
        }

        // The levels: each tile with its links to its byte sums and byte tallies as the minor keeps them, and each block's
        // trailer with where they end the same way.
        var blocks = new MemoryStream();
        long[] newOffsets = new long[levels];
        int newHeaderLength = headerLength - (minor >= 6 ? 0 : minor >= 5 ? 16 : 32);
        for (int level = 0; level < levels; level++)
        {
            newOffsets[level] = newHeaderLength + extentsLength + blocks.Length;
            for (int first = 0; first < counts[level]; first += SessionTileIndex.TilesPerBlock)
            {
                int tiles = Math.Min(SessionTileIndex.TilesPerBlock, counts[level] - first);
                int blockAt = (int)offsets[level] + ((first / SessionTileIndex.TilesPerBlock) * ((SessionTileIndex.TilesPerBlock * tileBytes) + 24));
                var block = new MemoryStream();
                int recordsEnd = 0;
                for (int tile = 0; tile < tiles; tile++)
                {
                    byte[] kept = section.AsSpan(blockAt + (tile * tileBytes), keptTileBytes).ToArray();
                    if (!keepsBytes && minor >= 5)
                    {
                        kept.AsSpan(32 + (4 * mechanisms)).Clear();
                    }

                    if (level == 0 && !spanning)
                    {
                        BinaryPrimitives.WriteInt32LittleEndian(kept.AsSpan(16), links[first + tile]);
                        int next = first + tile + 1;
                        recordsEnd = next < counts[0] ? links[next] - 4 : (int)records.Length - 4;
                    }

                    block.Write(kept);
                }

                // The trailer's ends of byte tallies and byte sums, then its records', owner tallies' and channel tallies'.
                byte[] ends = section.AsSpan(blockAt + (tiles * tileBytes), 8).ToArray();
                byte[] words = section.AsSpan(blockAt + (tiles * tileBytes) + 8, 12).ToArray();
                if (level == 0 && !spanning)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(words, recordsEnd);
                }

                if (!keepsBytes)
                {
                    ends.AsSpan().Clear();
                }

                block.Write(ends.AsSpan(minor >= 6 ? 0 : minor >= 5 ? 4 : 8));
                block.Write(words);
                byte[] checksum = new byte[4];
                Crc32C.Write(checksum, Crc32C.Compute(block.GetBuffer().AsSpan(0, (int)block.Length)));
                block.Write(checksum);
                blocks.Write(block.GetBuffer().AsSpan(0, (int)block.Length));
            }
        }

        // The header, its levels and parts placed anew: its byte sums and byte tallies where it keeps them.
        long next0 = newHeaderLength + extentsLength + blocks.Length;
        long ownersLength = regions[parts + 1];
        long channelsLength = regions[parts + 3];
        long sumsLength = keepsBytes ? regions[parts + 5] : 0;
        long talliesLength = keepsBytes && minor >= 6 ? regions[parts + 7] : 0;
        int ownersAt = (int)regions[parts];
        byte[] header = section.AsSpan(0, newHeaderLength).ToArray();
        for (int level = 0; level < levels; level++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(levelsAt + (12 * level) + 4), newOffsets[level]);
        }

        var fields = new List<long>();
        if (!spanning)
        {
            fields.AddRange([next0, records.Length]);
        }

        long ownersStart = next0 + (spanning ? 0 : records.Length);
        fields.AddRange([ownersStart, ownersLength, ownersStart + ownersLength, channelsLength]);
        if (minor >= 5)
        {
            fields.AddRange([ownersStart + ownersLength + channelsLength, sumsLength]);
        }

        if (minor >= 6)
        {
            fields.AddRange([ownersStart + ownersLength + channelsLength + sumsLength, talliesLength]);
        }

        for (int field = 0; field < fields.Count; field++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(regionsAt + (8 * field)), fields[field]);
        }

        Crc32C.Write(header.AsSpan(newHeaderLength - 4), Crc32C.Compute(header.AsSpan(0, newHeaderLength - 4)));
        return [.. header, .. section.AsSpan(headerLength, extentsLength), .. blocks.ToArray(), .. records.ToArray(),
            .. section.AsSpan(ownersAt, (int)(ownersLength + channelsLength)),
            .. section.AsSpan((int)regions[parts + 4], (int)sumsLength),
            .. section.AsSpan((int)regions[parts + 6], (int)talliesLength)];
    }

    private static void WriteI64(Stream into, long value)
    {
        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        into.Write(bytes);
    }

    private static ulong ReadLeb(byte[] bytes, ref int at)
    {
        ulong value = 0;
        int shift = 0;
        byte next;
        do
        {
            next = bytes[at++];
            value |= (ulong)(next & 0x7F) << shift;
            shift += 7;
        }
        while ((next & 0x80) != 0);
        return value;
    }

    private static void WriteLeb(Stream into, ulong value)
    {
        while (value >= 0x80)
        {
            into.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        into.WriteByte((byte)value);
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
