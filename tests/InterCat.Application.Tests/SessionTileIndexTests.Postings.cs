using System.Buffers.Binary;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// Since minor 10 each section posts its owners' and channels' records by the finest tile each lies in
/// (`contracts/tile-index-v1.md` §3, ADR-113), and an owner or a channel's focus counts its members' records at its
/// columns' boundaries from their postings, reading no tally and the records of only the tiles a boundary falls within:
/// exactly what its tiles and its rows count, refusing postings it cannot read or that a tile it reads disagrees with.
/// </summary>
public sealed partial class SessionTileIndexTests
{
    [Fact(DisplayName = "S3: a group's focus counts each member from its postings - a column the difference between its records before the column's two boundaries - reading no tally and no record")]
    public void AGroupCountsItsMembersFromTheirPostings()
    {
        // Eight pairs taking turns record by record, so every tile holds every process, and tiles a few records wide keep no
        // tallies: three of the sixteen processes, each in a lane of its own.
        ObservationRowV1[] rows = Interleaved(8, 24_000);
        using var session = Published(rows);
        SessionManifestV1 manifest = session.Store.Current!;
        Bound bound = Bind(session.Store);
        int instances = bound.Processes.Instances.Count;
        bool[] members = new bool[instances];
        int[] laneOf = [.. Enumerable.Repeat(-1, instances)];
        for (int member = 0; member < 3; member++)
        {
            members[1 + (member * 5)] = true;
            laneOf[1 + (member * 5)] = member;
        }

        TileIndexFile file = SessionTileIndex.Open(session.Store.Root, SessionTileIndex.NamedBy(manifest)!, manifest.SessionId);
        using TileReading posted = file.Read(session.Store.Root);
        using TileReading tallied = file.Read(session.Store.Root);
        TileSection section = posted.Section(Assert.Single(SessionSegments.Names(manifest)));
        Assert.True(section.Posted);
        TimeRange extent = Extent(rows);
        int boundaries = 0;
        foreach (TimeRange interval in new TimeRange[] { extent, new(extent.StartTicks + (extent.SpanTicks / 3), extent.EndTicks - (extent.SpanTicks / 5)) })
        {
            // The view's 256 columns and the lanes' 166, each counted from the postings and from the tiles, as their rows count.
            var focused = new TimelineColumns(interval, 256, tallyMechanisms: true);
            TimelineColumns[] lanes = [.. Enumerable.Range(0, 3).Select(_ => new TimelineColumns(interval, 166, tallyMechanisms: true))];
            var fromTiles = new TimelineColumns(interval, 256, tallyMechanisms: true);
            TimelineColumns[] tileLanes = [.. Enumerable.Range(0, 3).Select(_ => new TimelineColumns(interval, 166, tallyMechanisms: true))];
            Assert.True(posted.CountGroupPosted(section, focused, lanes, null, members, laneOf, null, null, EvidencePolicy.IncludeCorrelated, CancellationToken.None));
            Assert.True(tallied.CountGroup(section, fromTiles, tileLanes, null, members, laneOf, null, null, EvidencePolicy.IncludeCorrelated, CancellationToken.None));
            Assert.Equal(Snapshot(fromTiles), Snapshot(focused));
            Assert.All(Enumerable.Range(0, 3), lane => Assert.Equal(Snapshot(tileLanes[lane]), Snapshot(lanes[lane])));
            Assert.Equal(lanes.Sum(lane => lane.Counts.Sum()), focused.Counts.Sum());
            Assert.InRange(focused.Counts.Sum(), 1_500, 4_500);
            boundaries += 257 + 167;
        }

        // It read no tally and no record; the tiles, records wherever a boundary fell among tiles keeping no tallies.
        Assert.Equal((0, 0, 0), (posted.TilesOfTalliesRead, posted.TilesUntallied, posted.TilesOfRecordsRead));
        Assert.InRange(tallied.TilesOfRecordsRead, boundaries / 4, int.MaxValue);

        // Columns whose every boundary falls on a record - the records 300 ticks apart from 1,000 on, a column each - count
        // each record in the column it begins, as its row does.
        TimeRange aligned = new(1_000 + (300L * 4_000), 1_000 + (300L * (4_000 + 97)));
        var onRecords = new TimelineColumns(aligned, 97, tallyMechanisms: true);
        var onRecordsFromTiles = new TimelineColumns(aligned, 97, tallyMechanisms: true);
        bool[] everyone = [.. Enumerable.Repeat(true, instances)];
        Assert.True(posted.CountGroupPosted(section, onRecords, null, null, everyone, null, null, null, EvidencePolicy.AllIncludingConflicting, CancellationToken.None));
        Assert.True(tallied.CountGroup(section, onRecordsFromTiles, null, null, everyone, null, null, null, EvidencePolicy.AllIncludingConflicting, CancellationToken.None));
        Assert.Equal(Snapshot(onRecordsFromTiles), Snapshot(onRecords));
        Assert.All(onRecords.Counts, count => Assert.Equal(1, count));

        // A process's direction rows, and one mechanism of one direction, count the same both ways.
        int process = 1 + 5;
        bool[] alone = new bool[instances];
        alone[process] = true;
        foreach ((Mechanism? mechanism, Direction? direction) in new (Mechanism?, Direction?)[] { (null, null), (Mechanism.Tcp, Direction.Outbound) })
        {
            var focused = new TimelineColumns(extent, 97, tallyMechanisms: true);
            TimelineColumns[] rowsOf = [.. SessionTimelineQuery.LaneDirections.Select(_ => new TimelineColumns(extent, 97, tallyMechanisms: true))];
            var fromTiles = new TimelineColumns(extent, 97, tallyMechanisms: true);
            TimelineColumns[] tileRows = [.. SessionTimelineQuery.LaneDirections.Select(_ => new TimelineColumns(extent, 97, tallyMechanisms: true))];
            Assert.True(posted.CountGroupPosted(section, focused, null, rowsOf, alone, null, mechanism, direction, EvidencePolicy.AllIncludingConflicting, CancellationToken.None));
            Assert.True(tallied.CountGroup(section, fromTiles, null, tileRows, alone, null, mechanism, direction, EvidencePolicy.AllIncludingConflicting, CancellationToken.None));
            Assert.Equal(Snapshot(fromTiles), Snapshot(focused));
            Assert.All(Enumerable.Range(0, rowsOf.Length), row => Assert.Equal(Snapshot(tileRows[row]), Snapshot(rowsOf[row])));
            Assert.True(focused.Counts.Sum() > 0);
        }

        session.Store.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "S3: a channel's focus counts its ends from its postings, each end's records apart, reading no tally and no record")]
    public void AChannelCountsItsEndsFromItsPostings()
    {
        ObservationRowV1[] rows = Interleaved(8, 24_000);
        using var session = Published(rows);
        SessionManifestV1 manifest = session.Store.Current!;
        Bound bound = Bind(session.Store);
        TransportRelation relation = bound.Relations.Relations.Single(candidate =>
            candidate.FirstEndpoint.EndsWith(":8003", StringComparison.Ordinal) || candidate.SecondEndpoint.EndsWith(":8003", StringComparison.Ordinal));
        TileIndexFile file = SessionTileIndex.Open(session.Store.Root, SessionTileIndex.NamedBy(manifest)!, manifest.SessionId);
        using TileReading reading = file.Read(session.Store.Root);
        TileSection section = reading.Section(Assert.Single(SessionSegments.Names(manifest)));
        TimeRange extent = Extent(rows);
        TimeRange stretch = new(extent.StartTicks + (extent.SpanTicks * 7 / 60), extent.EndTicks - (extent.SpanTicks / 9));
        foreach ((TimeRange interval, int columns, Direction? direction, int? end) in new (TimeRange, int, Direction?, int?)[]
        {
            (extent, 256, null, null), (stretch, 19, Direction.Inbound, 1), (stretch, 19, Direction.Outbound, null), (stretch, 61, null, 0),
        })
        {
            var focused = new TimelineColumns(interval, columns, tallyMechanisms: true);
            SessionTimelineQuery.ChannelEndColumns[] ends = Ends(relation, interval, columns);
            Assert.True(reading.CountChannelPosted(section, focused, ends, relation.Channel, null, direction, end, CancellationToken.None));
            AssertChannel(OfChannel(bound, relation, interval, columns, direction, end), focused, ends);
            Assert.True(focused.Counts.Sum() > 0);
        }

        Assert.Equal((0, 0, 0), (reading.TilesOfTalliesRead, reading.TilesUntallied, reading.TilesOfRecordsRead));

        // Another mechanism's records of it are none.
        var none = new TimelineColumns(extent, 64, tallyMechanisms: true);
        Assert.True(reading.CountChannelPosted(section, none, null, relation.Channel, Mechanism.Udp, null, null, CancellationToken.None));
        Assert.Equal(0, none.Counts.Sum());
        session.Store.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "S3: a section whose readings span 2^32 ticks or more posts readings of eight bytes, and a group's or a channel's focus counts them exactly as its rows count")]
    public void AWideSectionPostsEightByteReadings()
    {
        // Eight pairs taking turns, their records six seconds apart: 2,000 of them span over three hours, past 2^32 ticks.
        ObservationRowV1[] rows = [.. Interleaved(8, 2_000).Select(row => row with
        {
            NativeTicks = row.NativeTicks * 200_000,
            SessionRelativeTicks = row.SessionRelativeTicks is { } nanoseconds ? nanoseconds * 200_000 : null,
        })];
        TimeRange extent = Extent(rows);
        using var session = Published(rows);
        using var twin = new TemporarySession();
        Publish(twin.Store, rows);
        Bound bound = Bind(twin.Store);
        (TileSection section, string path) = TopOf(session);
        Assert.True(section.Last!.Value - section.First!.Value > uint.MaxValue);
        Assert.Equal(8, PostingDirectory(File.ReadAllBytes(path), (int)(section.Entry.Offset + section.Postings.OwnersOffset)).Width);

        // A group's lanes and a channel's ends, over the whole extent and over a part beginning and ending among records.
        var group = new TimelineFocus(null, [.. bound.Processes.Instances.Where((_, place) => place % 4 == 1).Select(instance => instance.Id)]);
        var channel = new TimelineFocus(bound.Relations.Relations[3].StableKey, []);
        TimeRange part = new(extent.StartTicks + (extent.SpanTicks / 7) + 11, extent.EndTicks - (extent.SpanTicks / 5) - 13);
        SessionStore viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        foreach ((TimeRange interval, int columns) in new[] { (extent, 83), (part, 29) })
        {
            Assert.Equal(Focused(SessionTimelineQuery.Focused(twin.Store, interval, columns, group, EvidencePolicy.AllIncludingConflicting)),
                Focused(SessionTimelineQuery.Focused(viewer, interval, columns, group, EvidencePolicy.AllIncludingConflicting)));
            Assert.Equal(Focused(SessionTimelineQuery.Focused(twin.Store, interval, columns, channel)),
                Focused(SessionTimelineQuery.Focused(viewer, interval, columns, channel)));
        }

        Assert.Equal(0, viewer.SegmentReaderCache.Entries);
        Assert.Null(SessionDerivationCache.For(viewer.Current!).TilesProblem);
        viewer.ReleaseSegmentReaders();
        twin.Store.ReleaseSegmentReaders();
    }

    [Theory(DisplayName = "I4: postings that are damaged, list what no record has, go backwards, run past the section's readings or are of a width its span does not have are refused, never counted: the focus counts from the segments' rows instead")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void DamagedPostingsAreRefused(int damage)
    {
        // Each case's session differs from the others', so no two share a manifest, nor what a generation's derivations keep.
        ObservationRowV1[] rows = Interleaved(8, 24_000 + damage);
        TimeRange extent = Extent(rows);
        using var session = Published(rows);
        using var twin = new TemporarySession();
        Publish(twin.Store, rows);
        Bound bound = Bind(twin.Store);
        (TileSection section, string path) = TopOf(session);
        string name = section.Entry.Name;
        byte[] kept = File.ReadAllBytes(path);
        int sectionAt = (int)section.Entry.Offset;
        int ownersAt = sectionAt + (int)section.Postings.OwnersOffset;
        (int width, int widthAt, int directoryEnd, List<(ulong Key, ulong Kind, ulong Count, int KindAt)> lists) = PostingDirectory(kept, ownersAt);
        Assert.Equal(4, width);

        // The first list of a process's transfers - not its creation's one record - and where its readings begin.
        int chosen = lists.FindIndex(list => list.Count >= 100);
        int listAt = directoryEnd + 4;
        for (int list = 0; list < chosen; list++)
        {
            listAt += (4 * (int)lists[list].Count) + 4;
        }

        int count = (int)lists[chosen].Count;
        long span = section.Last!.Value - section.First!.Value;
        string why;
        switch (damage)
        {
            case 0:
                // A byte of its readings.
                kept[listAt + 5] ^= 0x40;
                why = $"list {chosen} of the owners' postings of the section of '{name}' does not match its checksum.";
                break;
            case 1:
                // Its second reading before its first.
                Assert.True(BinaryPrimitives.ReadUInt32LittleEndian(kept.AsSpan(listAt)) > 0);
                BinaryPrimitives.WriteUInt32LittleEndian(kept.AsSpan(listAt + 4), 0);
                Seal(listAt, (4 * count) + 4);
                why = $"list {chosen} of the owners' postings of the section of '{name}' go backwards or past the section's readings.";
                break;
            case 2:
                // Its kind names a mechanism the section has none of.
                Assert.InRange(kept[lists[chosen].KindAt], 0, 0x7F);
                kept[lists[chosen].KindAt] = (byte)(section.Mechanisms.Length << 3);
                Seal(ownersAt, directoryEnd + 4 - ownersAt);
                why = $"the owners' postings of the section of '{name}' list a key, kind or count no record could have, or out of order.";
                break;
            case 3:
                // Its last reading past the section's latest.
                BinaryPrimitives.WriteUInt32LittleEndian(kept.AsSpan(listAt + (4 * (count - 1))), checked((uint)(span + 1)));
                Seal(listAt, (4 * count) + 4);
                why = $"list {chosen} of the owners' postings of the section of '{name}' go backwards or past the section's readings.";
                break;
            case 4:
                // Its directory says its readings take eight bytes, where the section's span takes four.
                kept[widthAt] = 8;
                Seal(ownersAt, directoryEnd + 4 - ownersAt);
                why = $"the owners' postings of the section of '{name}' hold readings of a width the section's span does not have.";
                break;
            default:
                // The section's header makes its owners' postings two bytes longer than its channels' leave them.
                int headerAt = sectionAt + 35 + (2 * section.Mechanisms.Length) + (12 * section.Levels);
                int lengthAt = headerAt + (8 * 11);
                BinaryPrimitives.WriteInt64LittleEndian(kept.AsSpan(lengthAt), BinaryPrimitives.ReadInt64LittleEndian(kept.AsSpan(lengthAt)) + 2);
                Seal(sectionAt, headerAt + (8 * 16) + 4 - sectionAt);
                why = $"the header of the section of '{name}' does not lay out its section.";
                break;
        }

        File.WriteAllBytes(path, kept);

        // The chosen list's process alone: counted from the segment's rows.
        ProcessInstanceId member = bound.Processes.Instances[(int)(lists[chosen].Key >> 3) - 1].Id;
        var focus = new TimelineFocus(null, [member]);
        SessionStore viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        Assert.Equal(Focused(SessionTimelineQuery.Focused(twin.Store, extent, 41, focus, EvidencePolicy.AllIncludingConflicting)),
            Focused(SessionTimelineQuery.Focused(viewer, extent, 41, focus, EvidencePolicy.AllIncludingConflicting)));
        Assert.Equal("The persisted tile index is not readable: " + why, SessionDerivationCache.For(viewer.Current!).TilesProblem);
        Assert.Equal(1, viewer.SegmentReaderCache.Entries);
        viewer.ReleaseSegmentReaders();
        twin.Store.ReleaseSegmentReaders();

        void Seal(int at, int length) => Crc32C.Write(kept.AsSpan(at + length - 4), Crc32C.Compute(kept.AsSpan(at, length - 4)));
    }

    [Fact(DisplayName = "I14: a tile index of minor 9, which posts nothing, counts a focus from its tiles as it did, and a checkpoint publishes it again with every section's postings")]
    public void AnIndexPostingNothingIsPublishedAgain()
    {
        ObservationRowV1[] rows = Interleaved(8, 9_000);
        TimeRange extent = Extent(rows);
        using var session = Published(rows, rowsPerSegment: 3_000);
        using var twin = new TemporarySession();
        Publish(twin.Store, rows, rowsPerSegment: 3_000);
        SessionStore store = session.Store;
        Bound bound = Bind(store);
        var group = new TimelineFocus(null, [.. bound.Processes.Instances.Where((_, place) => place % 3 == 0).Select(instance => instance.Id)]);
        var channel = new TimelineFocus(bound.Relations.Relations[2].StableKey, []);
        List<string> expectedGroup = Focused(SessionTimelineQuery.Focused(twin.Store, extent, 77, group, EvidencePolicy.AllIncludingConflicting));
        List<string> expectedChannel = Focused(SessionTimelineQuery.Focused(twin.Store, extent, 77, channel));

        // As revision 745 wrote it: no postings.
        Republish(session, tiles: content => content.Write(Earlier(File.ReadAllBytes(PathOf(session)), 9)));
        Assert.Equal(9, FileOf(session).Minor);
        store.ReleaseSegmentReaders();
        SessionStore viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        using (TileReading reading = FileOf(session).Read(store.Root))
        {
            Assert.All(SessionSegments.Names(store.Current!), segment => Assert.False(reading.Section(segment).Posted));
        }

        Assert.Equal(expectedGroup, Focused(SessionTimelineQuery.Focused(viewer, extent, 77, group, EvidencePolicy.AllIncludingConflicting)));
        Assert.Equal(expectedChannel, Focused(SessionTimelineQuery.Focused(viewer, extent, 77, channel)));
        Assert.Equal(0, viewer.SegmentReaderCache.Entries);
        Assert.Null(SessionDerivationCache.For(viewer.Current!).TilesProblem);
        viewer.ReleaseSegmentReaders();

        // Not current: the next checkpoint publishes it again, every section posting its owners and channels.
        CheckpointPublication again = SessionCheckpoints.Publish(store, Committed);
        Assert.True(again.Outcome == CheckpointOutcome.Published, again.Reason);
        Assert.Equal(SessionTileIndex.Minor, FileOf(session).Minor);
        using (TileReading reading = FileOf(session).Read(store.Root))
        {
            Assert.All(SessionSegments.Names(store.Current!), segment => Assert.True(reading.Section(segment).Posted));
        }

        SessionStore after = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        Assert.Equal(expectedGroup, Focused(SessionTimelineQuery.Focused(after, extent, 77, group, EvidencePolicy.AllIncludingConflicting)));
        Assert.Equal(expectedChannel, Focused(SessionTimelineQuery.Focused(after, extent, 77, channel)));
        Assert.Equal(0, after.SegmentReaderCache.Entries);

        // Each focus counted every section from its postings, whose directories the index's cache keeps, and no tally.
        TileIndexFile counted = SessionDerivationCache.For(after.Current!).PersistedTiles(after.Root)!;
        using (TileReading reading = counted.Read(after.Root))
        {
            Assert.All(SessionSegments.Names(after.Current!), segment =>
            {
                TileSection section = reading.Section(segment);
                Assert.NotNull(counted.Blocks.FindDecoded<TilePostingDirectory>(section, TilePart.OwnerPostings, 0));
                Assert.NotNull(counted.Blocks.FindDecoded<TilePostingDirectory>(section, TilePart.ChannelPostings, 0));
                Assert.All(Enumerable.Range(0, section.Levels), level => Assert.All(
                    Enumerable.Range(0, (section.TileCount(level) + SessionTileIndex.TilesPerBlock - 1) / SessionTileIndex.TilesPerBlock),
                    block => Assert.Null(counted.Blocks.Find(section, TilePart.OwnerTallies, level, block))));
            });
        }

        after.ReleaseSegmentReaders();
        store.ReleaseSegmentReaders();
        twin.Store.ReleaseSegmentReaders();
    }

    /// <summary>
    /// The owners' postings' directory at <paramref name="at"/>: its readings' width and where it lies, where it ends,
    /// before its checksum, and each list's key, kind, count and where its kind lies.
    /// </summary>
    private static (int Width, int WidthAt, int End, List<(ulong Key, ulong Kind, ulong Count, int KindAt)> Lists) PostingDirectory(byte[] bytes, int at)
    {
        int widthAt = at;
        int width = (int)ReadLeb(bytes, ref at);
        ulong lists = ReadLeb(bytes, ref at);
        var listed = new List<(ulong Key, ulong Kind, ulong Count, int KindAt)>();
        ulong key = 0;
        for (ulong list = 0; list < lists; list++)
        {
            key += ReadLeb(bytes, ref at);
            int kindAt = at;
            ulong kind = ReadLeb(bytes, ref at);
            listed.Add((key, kind, ReadLeb(bytes, ref at), kindAt));
        }

        return (width, widthAt, at, listed);
    }
}
