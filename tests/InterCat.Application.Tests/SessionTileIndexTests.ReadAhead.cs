using System.Buffers.Binary;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// A group's first focus, and its first byte lanes, read their members' lists ahead (ADR-113's addendum, revision 1088):
/// each section's directory and its members' lists - and their measures - in a read or two, on several workers, so that
/// the count walking the sections one after another reads none of them, and counts what reading each list alone counts;
/// a list that cannot be read is refused by the count, as it was.
/// </summary>
public sealed partial class SessionTileIndexTests
{
    [Fact(DisplayName = "S3: a group's lists are read ahead, each section's in one read of its directory and one of its members' lists and of their measures, so the count that follows reads no part and counts and measures what reading each list alone does")]
    public void AGroupsListsAreReadAheadInRuns()
    {
        // Eight pairs taking turns, their bytes varied, in eight segments; three of the sixteen processes, each in a lane.
        var random = new Random(1_088);
        ObservationRowV1[] rows = Varied(random, Interleaved(8, 24_000), timed: false);
        using var session = Published(rows, rowsPerSegment: 3_000);
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

        bool[] admitted = SessionTileIndex.AdmittedStrengths(EvidencePolicy.IncludeCorrelated);
        bool Member(TileSection _, uint key, int kind) => admitted[key & 7] && members[(int)(key >> 3) - 1];
        StoreDependency named = SessionTileIndex.NamedBy(manifest)!;
        TileIndexFile ahead = SessionTileIndex.Open(session.Store.Root, named, manifest.SessionId);
        TileIndexFile alone = SessionTileIndex.Open(session.Store.Root, named, manifest.SessionId);
        using TileReading reading = ahead.Read(session.Store.Root);
        using TileReading reference = alone.Read(session.Store.Root);
        TileSection[] sections = [.. SessionSegments.Names(manifest).Select(reading.Section)];
        Assert.Equal(8, sections.Length);
        Assert.All(sections, section => Assert.True(section.Posted && section.Measured));

        // Two parts a section, its directory and one run of its members' lists, and two more with their measures.
        int before = ahead.PartsRead;
        SessionTileIndex.ReadGroupAhead(ahead, session.Store.Root, sections, Member, measures: false, CancellationToken.None);
        Assert.Equal(2 * sections.Length, ahead.PartsRead - before);
        SessionTileIndex.ReadGroupAhead(ahead, session.Store.Root, sections, Member, measures: true, CancellationToken.None);
        Assert.Equal(4 * sections.Length, ahead.PartsRead - before);
        int lists = sections.Sum(section =>
        {
            TilePostingDirectory directory = ahead.Blocks.FindDecoded<TilePostingDirectory>(section, TilePart.OwnerPostings, 0)!;
            return Enumerable.Range(0, directory.Count).Count(list => Member(section, directory.KeyAt(list), directory.KindAt(list)));
        });
        Assert.InRange(lists, 2 * sections.Length, int.MaxValue);

        // Read ahead again, every directory is kept and nothing is read.
        int read = ahead.PartsRead;
        SessionTileIndex.ReadGroupAhead(ahead, session.Store.Root, sections, Member, measures: true, CancellationToken.None);
        Assert.Equal(read, ahead.PartsRead);

        // The count and the byte lanes that follow read no part, and are what reading each list alone counts and measures.
        TimeRange extent = Extent(rows);
        foreach (TimeRange interval in new TimeRange[] { extent, new(extent.StartTicks + (extent.SpanTicks / 3), extent.EndTicks - (extent.SpanTicks / 5)) })
        {
            var focused = new TimelineColumns(interval, 256, tallyMechanisms: true);
            TimelineColumns[] lanes = [.. Enumerable.Range(0, 3).Select(_ => new TimelineColumns(interval, 166, tallyMechanisms: true))];
            var focusedAlone = new TimelineColumns(interval, 256, tallyMechanisms: true);
            TimelineColumns[] lanesAlone = [.. Enumerable.Range(0, 3).Select(_ => new TimelineColumns(interval, 166, tallyMechanisms: true))];
            var laneBytes = new TransportByteTally((3 * 166) + 1);
            var laneBytesAlone = new TransportByteTally((3 * 166) + 1);
            foreach (TileSection section in sections)
            {
                Assert.True(reading.CountGroupPosted(section, focused, lanes, null, members, laneOf, null, null, EvidencePolicy.IncludeCorrelated, CancellationToken.None));
                Assert.True(reference.CountGroupPosted(section, focusedAlone, lanesAlone, null, members, laneOf, null, null, EvidencePolicy.IncludeCorrelated, CancellationToken.None));
                Assert.True(reading.MeasureGroupPosted(section, lanes[0], laneOf, admitted, laneBytes, CancellationToken.None));
                Assert.True(reference.MeasureGroupPosted(section, lanesAlone[0], laneOf, admitted, laneBytesAlone, CancellationToken.None));
            }

            Assert.Equal(Snapshot(focusedAlone), Snapshot(focused));
            Assert.All(Enumerable.Range(0, 3), lane => Assert.Equal(Snapshot(lanesAlone[lane]), Snapshot(lanes[lane])));
            Assert.Equal(Enumerable.Range(0, laneBytesAlone.Slots).Select(laneBytesAlone.Of), Enumerable.Range(0, laneBytes.Slots).Select(laneBytes.Of));
            Assert.True(focused.Counts.Sum() > 0 && laneBytes.Of(0).SentBytes + laneBytes.Of(0).ReceivedBytes > 0);
        }

        Assert.Equal(read, ahead.PartsRead);

        // Counting with nothing read ahead, each section's group was read by the count itself, part for part as ahead
        // (revision 1090): a directory and a run of lists, and the same of their measures.
        Assert.Equal(4 * sections.Length, alone.PartsRead);
        session.Store.ReleaseSegmentReaders();
    }

    [Theory(DisplayName = "S3: a group's count walks behind its lists read ahead, waiting for a section only until its parts are kept or reading it ahead itself where no worker has taken it, so every part is read once, the count reading none alone, and it counts and measures what reading each list alone does")]
    [InlineData(1)]
    [InlineData(4)]
    public void AGroupsCountWalksBehindItsReadAhead(int workers)
    {
        // Eight pairs taking turns, their bytes varied, in eight segments; three of the sixteen processes, each in a lane.
        ObservationRowV1[] rows = Varied(new Random(1_089), Interleaved(8, 24_000), timed: false);
        using var session = Published(rows, rowsPerSegment: 3_000);
        SessionManifestV1 manifest = session.Store.Current!;
        int instances = Bind(session.Store).Processes.Instances.Count;
        bool[] members = new bool[instances];
        int[] laneOf = [.. Enumerable.Repeat(-1, instances)];
        for (int member = 0; member < 3; member++)
        {
            members[1 + (member * 5)] = true;
            laneOf[1 + (member * 5)] = member;
        }

        bool[] admitted = SessionTileIndex.AdmittedStrengths(EvidencePolicy.IncludeCorrelated);
        bool Member(TileSection _, uint key, int kind) => admitted[key & 7] && members[(int)(key >> 3) - 1];
        StoreDependency named = SessionTileIndex.NamedBy(manifest)!;
        TileIndexFile ahead = SessionTileIndex.Open(session.Store.Root, named, manifest.SessionId);
        TileIndexFile alone = SessionTileIndex.Open(session.Store.Root, named, manifest.SessionId);
        using TileReading counting = ahead.Read(session.Store.Root);
        using TileReading reference = alone.Read(session.Store.Root);
        TileSection[] sections = [.. SessionSegments.Names(manifest).Select(counting.Section)];
        Assert.Equal(8, sections.Length);

        TimeRange extent = Extent(rows);
        var focused = new TimelineColumns(extent, 256, tallyMechanisms: true);
        var focusedAlone = new TimelineColumns(extent, 256, tallyMechanisms: true);
        TimelineColumns[] lanes = [.. Enumerable.Range(0, 3).Select(_ => new TimelineColumns(extent, 166, tallyMechanisms: true))];
        TimelineColumns[] lanesAlone = [.. Enumerable.Range(0, 3).Select(_ => new TimelineColumns(extent, 166, tallyMechanisms: true))];
        var laneBytes = new TransportByteTally((3 * 166) + 1);
        var laneBytesAlone = new TransportByteTally((3 * 166) + 1);
        SegmentPasses.Workers.Value = workers;
        try
        {
            // The count, then the measure, each walking behind its own read-ahead: once waited for, a section's directory
            // is kept, and a section is read once, by a worker or by the walk - a directory and a run of lists, and with
            // measures the same of theirs - where a walk reading its lists alone would read one part a list.
            int before = ahead.PartsRead;
            using (TileGroupReadAhead reading = TileGroupReadAhead.Start(ahead, session.Store.Root, sections, Member, measures: false, CancellationToken.None))
            {
                foreach (TileSection section in sections)
                {
                    reading.WaitFor(section, counting, CancellationToken.None);
                    Assert.False(ahead.WouldReadGroup(section, Member, measures: false));
                    Assert.True(counting.CountGroupPosted(section, focused, lanes, null, members, laneOf, null, null, EvidencePolicy.IncludeCorrelated, CancellationToken.None));
                }
            }

            Assert.Equal(2 * sections.Length, ahead.PartsRead - before);
            using (TileGroupReadAhead reading = TileGroupReadAhead.Start(ahead, session.Store.Root, sections, Member, measures: true, CancellationToken.None))
            {
                foreach (TileSection section in sections)
                {
                    reading.WaitFor(section, counting, CancellationToken.None);
                    Assert.False(ahead.WouldReadGroup(section, Member, measures: true));
                    Assert.True(counting.MeasureGroupPosted(section, lanes[0], laneOf, admitted, laneBytes, CancellationToken.None));
                }
            }

            Assert.Equal(4 * sections.Length, ahead.PartsRead - before);
        }
        finally
        {
            SegmentPasses.Workers.Value = null;
        }

        // What reading each list alone counts and measures.
        foreach (TileSection section in sections)
        {
            Assert.True(reference.CountGroupPosted(section, focusedAlone, lanesAlone, null, members, laneOf, null, null, EvidencePolicy.IncludeCorrelated, CancellationToken.None));
            Assert.True(reference.MeasureGroupPosted(section, lanesAlone[0], laneOf, admitted, laneBytesAlone, CancellationToken.None));
        }

        Assert.Equal(Snapshot(focusedAlone), Snapshot(focused));
        Assert.All(Enumerable.Range(0, 3), lane => Assert.Equal(Snapshot(lanesAlone[lane]), Snapshot(lanes[lane])));
        Assert.Equal(Enumerable.Range(0, laneBytesAlone.Slots).Select(laneBytesAlone.Of), Enumerable.Range(0, laneBytes.Slots).Select(laneBytes.Of));
        Assert.True(focused.Counts.Sum() > 0 && laneBytes.Of(0).SentBytes + laneBytes.Of(0).ReceivedBytes > 0);
        session.Store.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "S3: a group's lists are read on their own, not the lists of the processes between them: each section's in runs of lists lying close, into one buffer kept as one part, the bytes read its members' lists and measures and nothing between, and the count then reads nothing and counts what the section's tiles count")]
    public void AGroupsListsAreReadOnTheirOwn()
    {
        // Sixteen pairs taking turns in four segments: each process posts some 750 records a section, so between two of
        // the group's members - every fifth process - lie four others' lists and measures, kilobytes of them.
        ObservationRowV1[] rows = Interleaved(16, 96_000);
        using var session = Published(rows, rowsPerSegment: 24_000);
        SessionManifestV1 manifest = session.Store.Current!;
        int instances = Bind(session.Store).Processes.Instances.Count;
        bool[] members = new bool[instances];
        int[] laneOf = [.. Enumerable.Repeat(-1, instances)];
        for (int member = 0; member < 6; member++)
        {
            members[1 + (member * 5)] = true;
            laneOf[1 + (member * 5)] = member;
        }

        bool[] admitted = SessionTileIndex.AdmittedStrengths(EvidencePolicy.IncludeCorrelated);
        bool Member(TileSection _, uint key, int kind) => admitted[key & 7] && members[(int)(key >> 3) - 1];
        StoreDependency named = SessionTileIndex.NamedBy(manifest)!;
        TileIndexFile tiles = SessionTileIndex.Open(session.Store.Root, named, manifest.SessionId);
        using TileReading reading = tiles.Read(session.Store.Root);
        TileSection[] sections = [.. SessionSegments.Names(manifest).Select(reading.Section)];
        Assert.Equal(4, sections.Length);

        // The directories first, of the postings and of their measures, kept by a measure of no lane, which reads no list.
        TimeRange extent = Extent(rows);
        int[] noLane = [.. Enumerable.Repeat(-1, instances)];
        foreach (TileSection section in sections)
        {
            Assert.True(reading.MeasureGroupPosted(section, new TimelineColumns(extent, 128, tallyMechanisms: true), noLane, admitted,
                new TransportByteTally(129), CancellationToken.None));
        }

        // The group's lists and their measures read ahead: their bytes and none between, a run of each a member, whose
        // lists lie end to end, where more than a page lies between two members'.
        (long bytes, int parts) = (tiles.BytesRead, tiles.PartsRead);
        SessionTileIndex.ReadGroupAhead(tiles, session.Store.Root, sections, Member, measures: true, CancellationToken.None);
        long listed = 0;
        int runs = 0;
        foreach (TileSection section in sections)
        {
            TilePostingDirectory directory = tiles.Blocks.FindDecoded<TilePostingDirectory>(section, TilePart.OwnerPostings, 0)!;
            long[] measures = tiles.Blocks.FindDecoded<long[]>(section, TilePart.OwnerMeasures, 0)!;
            int[] lists = TileIndexFile.MembersOf(section, directory, Member);
            Assert.InRange(lists.Length, 6, int.MaxValue);
            for (int at = 0; at < lists.Length; at++)
            {
                int list = lists[at];
                listed += (directory.Width * directory.CountAt(list)) + 4 + (measures[list + 1] - measures[list]);
                if (at > 0 && lists[at - 1] == list - 1)
                {
                    continue;
                }

                runs += 2;
                if (at > 0)
                {
                    int previous = lists[at - 1];
                    Assert.True(directory.OffsetAt(list) - directory.OffsetAt(previous) - ((directory.Width * directory.CountAt(previous)) + 4) > TileIndexFile.GapBytes);
                    Assert.True(measures[list] - measures[previous + 1] > TileIndexFile.GapBytes);
                }
            }
        }

        Assert.Equal(listed, tiles.BytesRead - bytes);
        Assert.Equal(runs, tiles.PartsRead - parts);

        // Kept as one part a section: read ahead again, nothing is read, and nothing would be.
        int read = tiles.PartsRead;
        SessionTileIndex.ReadGroupAhead(tiles, session.Store.Root, sections, Member, measures: true, CancellationToken.None);
        Assert.Equal(read, tiles.PartsRead);
        Assert.All(sections, section => Assert.False(tiles.WouldReadGroup(section, Member, measures: true)));

        // The count and the byte lanes read nothing more; the count is what the section's tiles count, member by member.
        var focused = new TimelineColumns(extent, 256, tallyMechanisms: true);
        TimelineColumns[] lanes = [.. Enumerable.Range(0, 6).Select(_ => new TimelineColumns(extent, 128, tallyMechanisms: true))];
        var laneBytes = new TransportByteTally((6 * 128) + 1);
        foreach (TileSection section in sections)
        {
            Assert.True(reading.CountGroupPosted(section, focused, lanes, null, members, laneOf, null, null, EvidencePolicy.IncludeCorrelated, CancellationToken.None));
            Assert.True(reading.MeasureGroupPosted(section, lanes[0], laneOf, admitted, laneBytes, CancellationToken.None));
        }

        Assert.Equal(read, tiles.PartsRead);
        var focusedTiles = new TimelineColumns(extent, 256, tallyMechanisms: true);
        TimelineColumns[] lanesTiles = [.. Enumerable.Range(0, 6).Select(_ => new TimelineColumns(extent, 128, tallyMechanisms: true))];
        foreach (TileSection section in sections)
        {
            Assert.True(reading.CountGroup(section, focusedTiles, lanesTiles, null, members, laneOf, null, null, EvidencePolicy.IncludeCorrelated, CancellationToken.None));
        }

        Assert.Equal(Snapshot(focusedTiles), Snapshot(focused));
        Assert.All(Enumerable.Range(0, 6), lane => Assert.Equal(Snapshot(lanesTiles[lane]), Snapshot(lanes[lane])));
        Assert.True(focused.Counts.Sum() > 0 && Enumerable.Range(0, laneBytes.Slots).Any(slot => laneBytes.Of(slot).SentBytes > 0));
        session.Store.ReleaseSegmentReaders();
    }

    [Theory(DisplayName = "I4: a list read ahead that is damaged or goes backwards is refused by the count, as one read alone is: the group's focus counts from the segments' rows, the refusal naming the list")]
    [InlineData(0)]
    [InlineData(1)]
    public void ADamagedListReadAheadIsRefused(int damage)
    {
        // Each case's session differs from the other's, so the two share no manifest, nor what a generation's derivations keep.
        ObservationRowV1[] rows = Interleaved(8, 24_000 + damage);
        TimeRange extent = Extent(rows);
        using var session = Published(rows, rowsPerSegment: 4_000);
        using var twin = new TemporarySession();
        Publish(twin.Store, rows, rowsPerSegment: 4_000);
        Bound bound = Bind(twin.Store);
        SessionManifestV1 manifest = session.Store.Current!;
        StoreDependency named = SessionTileIndex.NamedBy(manifest)!;
        TileSection section;
        using (TileReading reading = SessionTileIndex.Open(session.Store.Root, named, manifest.SessionId).Read(session.Store.Root))
        {
            section = reading.Section(SessionSegments.Names(manifest)[2]);
        }

        // A list of a process's transfers in the third of the six sections, and where its readings begin.
        string path = Path.Combine(session.Path, named.Name);
        byte[] kept = File.ReadAllBytes(path);
        int ownersAt = (int)section.Entry.Offset + (int)section.Postings.OwnersOffset;
        (_, _, int directoryEnd, List<(ulong Key, ulong Kind, ulong Count, int KindAt)> lists) = PostingDirectory(kept, ownersAt);
        int chosen = lists.FindIndex(list => list.Count >= 100);
        int listAt = directoryEnd + 4;
        for (int list = 0; list < chosen; list++)
        {
            listAt += (4 * (int)lists[list].Count) + 4;
        }

        int count = (int)lists[chosen].Count;
        string name = section.Entry.Name;
        string why;
        if (damage == 0)
        {
            kept[listAt + 5] ^= 0x40;
            why = $"list {chosen} of the owners' postings of the section of '{name}' does not match its checksum.";
        }
        else
        {
            // Its second reading before its first: the second made 0, or where the first is 0, the first past the second.
            uint first = BinaryPrimitives.ReadUInt32LittleEndian(kept.AsSpan(listAt));
            uint second = BinaryPrimitives.ReadUInt32LittleEndian(kept.AsSpan(listAt + 4));
            if (first > 0)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(kept.AsSpan(listAt + 4), 0);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(kept.AsSpan(listAt), second + 1);
            }

            Crc32C.Write(kept.AsSpan(listAt + (4 * count)), Crc32C.Compute(kept.AsSpan(listAt, 4 * count)));
            why = $"list {chosen} of the owners' postings of the section of '{name}' go backwards or past the section's readings.";
        }

        File.WriteAllBytes(path, kept);

        // The damaged list's process and another, a group whose first focus reads its lists ahead: counted from the rows.
        int place = (int)(lists[chosen].Key >> 3) - 1;
        var focus = new TimelineFocus(null, [bound.Processes.Instances[place].Id, bound.Processes.Instances[(place + 3) % bound.Processes.Instances.Count].Id]);
        SessionStore viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        Assert.Equal(Focused(SessionTimelineQuery.Focused(twin.Store, extent, 41, focus, EvidencePolicy.AllIncludingConflicting)),
            Focused(SessionTimelineQuery.Focused(viewer, extent, 41, focus, EvidencePolicy.AllIncludingConflicting)));
        Assert.Equal("The persisted tile index is not readable: " + why, SessionDerivationCache.For(viewer.Current!).TilesProblem);
        viewer.ReleaseSegmentReaders();
        twin.Store.ReleaseSegmentReaders();
    }
}
