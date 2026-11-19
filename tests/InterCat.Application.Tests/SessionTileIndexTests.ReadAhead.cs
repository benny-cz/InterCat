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

        // Reading each list alone read one part a list and one a list's measures.
        Assert.InRange(alone.PartsRead, (2 * lists) + sections.Length, int.MaxValue);
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
