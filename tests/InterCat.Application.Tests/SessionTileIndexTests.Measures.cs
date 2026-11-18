using System.Buffers.Binary;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// Since minor 11 a section keeping bytes measures its owners' posted records (`contracts/tile-index-v1.md` §3, ADR-113's
/// addendum), and a group's byte lanes measure each member from its postings and measures - the machine row from the
/// tiles' byte sums - reading no byte tally: exactly what its rows measure, refusing measures it cannot read or that do not
/// add up.
/// </summary>
public sealed partial class SessionTileIndexTests
{
    [Fact(DisplayName = "S3: a group's byte lanes measure each member from its postings - a lane column the difference between what its records before the column's two boundaries measured - and the machine row from the tiles' byte sums, reading no byte tally")]
    public void AGroupMeasuresItsMembersFromTheirMeasures()
    {
        // Eight pairs taking turns, their bytes varied - sizes declared and not recorded, values past four gigabytes, sides
        // stated for their owner - over more than one block of 64 records a list.
        var random = new Random(1_065);
        ObservationRowV1[] rows = Varied(random, Interleaved(8, 12_000), timed: false);
        TimeRange extent = Extent(rows);
        using var session = Published(rows, rowsPerSegment: 5_000);
        using var twin = new TemporarySession();
        Publish(twin.Store, rows, rowsPerSegment: 5_000);
        Bound bound = Bind(twin.Store);
        ProcessInstanceId[] group = [.. bound.Processes.Instances.Where((_, place) => place % 3 != 0).Select(instance => instance.Id)];
        TimeRange part = new(extent.StartTicks + (extent.SpanTicks / 7) + 3, extent.EndTicks - (extent.SpanTicks / 9) - 5);
        SessionStore viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        foreach ((TimeRange interval, int columns, int laneColumns) in new[] { (extent, 211, 53), (part, 97, 97), (part, 7, 1), (extent, 1, 1) })
        {
            foreach (EvidencePolicy policy in new[] { EvidencePolicy.DirectOnly, EvidencePolicy.AllIncludingConflicting })
            {
                Assert.Equal(OwnerBytes(twin.Store, interval, columns, laneColumns, group, policy),
                    OwnerBytes(viewer, interval, columns, laneColumns, group, policy));
            }
        }

        Assert.Equal(0, viewer.SegmentReaderCache.Entries);
        Assert.Null(SessionDerivationCache.For(viewer.Current!).TilesProblem);

        // Every section measured its members from their measures, which the index's cache keeps, and read no byte tally.
        TileIndexFile file = SessionDerivationCache.For(viewer.Current!).PersistedTiles(viewer.Root)!;
        using (TileReading reading = file.Read(viewer.Root))
        {
            Assert.All(SessionSegments.Names(viewer.Current!), segment =>
            {
                TileSection section = reading.Section(segment);
                Assert.True(section.Measured);
                Assert.NotNull(file.Blocks.FindDecoded<long[]>(section, TilePart.OwnerMeasures, 0));
                Assert.All(Enumerable.Range(0, section.Levels), level => Assert.All(
                    Enumerable.Range(0, (section.TileCount(level) + SessionTileIndex.TilesPerBlock - 1) / SessionTileIndex.TilesPerBlock),
                    block => Assert.Null(file.Blocks.Find(section, TilePart.ByteTallies, level, block))));
            });
        }

        viewer.ReleaseSegmentReaders();
        twin.Store.ReleaseSegmentReaders();
    }

    [Theory(DisplayName = "I4: owners' measures that are damaged, measure other lists than the postings hold, state sums or places their records do not have or hold a measure no record has are refused, never counted: a group's byte lanes are measured from the segments' rows instead")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void DamagedMeasuresAreRefused(int damage)
    {
        // Each case's session differs from the others', so no two share a manifest, nor what a generation's derivations keep.
        ObservationRowV1[] rows = Interleaved(8, 6_000 + damage);
        TimeRange extent = Extent(rows);
        using var session = Published(rows);
        using var twin = new TemporarySession();
        Publish(twin.Store, rows);
        Bound bound = Bind(twin.Store);
        (TileSection section, string path) = TopOf(session);
        string name = section.Entry.Name;
        byte[] kept = File.ReadAllBytes(path);
        int sectionAt = (int)section.Entry.Offset;
        (_, _, _, List<(ulong Key, ulong Kind, ulong Count, int KindAt)> lists) = PostingDirectory(kept, sectionAt + (int)section.Postings.OwnersOffset);

        // The measures' directory, and a list's measures: its blocks, then its records' measures. The first list's - a
        // process's creation - or, to damage a block past the first, the first list holding one.
        int measuresAt = sectionAt + (int)section.Postings.MeasuresOffset;
        int at = measuresAt;
        Assert.Equal((ulong)lists.Count, ReadLeb(kept, ref at));
        int countAt = measuresAt;
        int[] lengths = new int[lists.Count];
        for (int list = 0; list < lists.Count; list++)
        {
            lengths[list] = (int)ReadLeb(kept, ref at);
        }

        int directoryEnd = at;
        int damaged = damage == 4 ? lists.FindIndex(list => list.Count >= SessionTileIndex.MeasuresPerBlock) : 0;
        Assert.True(damaged >= 0);
        int listAt = directoryEnd + 4 + lengths[..damaged].Sum();
        int blocks = ((int)lists[damaged].Count / SessionTileIndex.MeasuresPerBlock) + 1;
        int streamAt = listAt + (blocks * SessionTileIndex.MeasureBlockBytes);
        string why;
        switch (damage)
        {
            case 0:
                // A byte of its records' measures.
                kept[streamAt + 1] ^= 0x10;
                why = $"the measures of owner list 0 of the section of '{name}' does not match its checksum.";
                break;
            case 1:
                // Its first block says the records before it sent a byte.
                BinaryPrimitives.WriteInt64LittleEndian(kept.AsSpan(listAt), 1);
                Seal(listAt, lengths[damaged]);
                why = $"the measures of owner list 0 of the section of '{name}' state sums or places its records do not have.";
                break;
            case 2:
                // Its first record's measure a code no record's is.
                Assert.InRange(kept[streamAt], 0, 8);
                kept[streamAt] = 9;
                Seal(listAt, lengths[damaged]);
                why = $"the measures of owner list 0 of the section of '{name}' hold a measure no record can have.";
                break;
            case 3:
                // The directory measures one list more than the postings hold.
                Assert.InRange(kept[countAt], 1, 0x7E);
                kept[countAt]++;
                Seal(measuresAt, directoryEnd + 4 - measuresAt);
                why = $"the owners' measures of the section of '{name}' measure other lists than the owners' postings hold.";
                break;
            case 4:
                // Its second block says its records' measures begin a byte later than they do.
                int offsetAt = listAt + SessionTileIndex.MeasureBlockBytes + (3 * (8 + 4 + 4));
                BinaryPrimitives.WriteUInt32LittleEndian(kept.AsSpan(offsetAt), BinaryPrimitives.ReadUInt32LittleEndian(kept.AsSpan(offsetAt)) + 1);
                Seal(listAt, lengths[damaged]);
                why = $"the measures of owner list {damaged} of the section of '{name}' state sums or places its records do not have.";
                break;
            default:
                // The section's header makes its measures two bytes longer than the section.
                int headerAt = sectionAt + 35 + (2 * section.Mechanisms.Length) + (12 * section.Levels);
                int lengthAt = headerAt + (8 * 15);
                BinaryPrimitives.WriteInt64LittleEndian(kept.AsSpan(lengthAt), BinaryPrimitives.ReadInt64LittleEndian(kept.AsSpan(lengthAt)) + 2);
                Seal(sectionAt, headerAt + (8 * 16) + 4 - sectionAt);
                why = $"the header of the section of '{name}' does not lay out its section.";
                break;
        }

        File.WriteAllBytes(path, kept);

        // The damaged list's process and another, in their lanes: measured from the segment's rows.
        ProcessInstanceId first = bound.Processes.Instances[(int)(lists[damaged].Key >> 3) - 1].Id;
        ProcessInstanceId other = bound.Processes.Instances.First(instance => instance.Id != first).Id;
        SessionStore viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        Assert.Equal(OwnerBytes(twin.Store, extent, 37, 37, [first, other], EvidencePolicy.AllIncludingConflicting),
            OwnerBytes(viewer, extent, 37, 37, [first, other], EvidencePolicy.AllIncludingConflicting));
        Assert.Equal("The persisted tile index is not readable: " + why, SessionDerivationCache.For(viewer.Current!).TilesProblem);
        Assert.NotEqual(0, viewer.SegmentReaderCache.Entries);
        viewer.ReleaseSegmentReaders();
        twin.Store.ReleaseSegmentReaders();

        void Seal(int start, int length) => Crc32C.Write(kept.AsSpan(start + length - 4), Crc32C.Compute(kept.AsSpan(start, length - 4)));
    }

    [Fact(DisplayName = "I14: a tile index of minor 10, which measures nothing, measures a group's byte lanes from its byte tallies as it did, and a checkpoint publishes it again with its owners' measures")]
    public void AnIndexMeasuringNothingIsPublishedAgain()
    {
        var random = new Random(10_651);
        ObservationRowV1[] rows = Varied(random, Interleaved(8, 9_000), timed: false);
        TimeRange extent = Extent(rows);
        using var session = Published(rows, rowsPerSegment: 3_000);
        using var twin = new TemporarySession();
        Publish(twin.Store, rows, rowsPerSegment: 3_000);
        SessionStore store = session.Store;
        Bound bound = Bind(twin.Store);
        ProcessInstanceId[] group = [.. bound.Processes.Instances.Where((_, place) => place % 2 == 0).Select(instance => instance.Id)];
        string[] expected = OwnerBytes(twin.Store, extent, 61, 19, group, EvidencePolicy.AllIncludingConflicting);

        // As revision 1064 wrote it: postings, and no measures.
        Republish(session, tiles: content => content.Write(Earlier(File.ReadAllBytes(PathOf(session)), 10)));
        Assert.Equal(10, FileOf(session).Minor);
        store.ReleaseSegmentReaders();
        SessionStore viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        using (TileReading reading = FileOf(session).Read(store.Root))
        {
            Assert.All(SessionSegments.Names(store.Current!), segment =>
                Assert.Equal((true, false), (reading.Section(segment).Posted, reading.Section(segment).Measured)));
        }

        Assert.Equal(expected, OwnerBytes(viewer, extent, 61, 19, group, EvidencePolicy.AllIncludingConflicting));
        Assert.Equal(0, viewer.SegmentReaderCache.Entries);
        Assert.Null(SessionDerivationCache.For(viewer.Current!).TilesProblem);
        viewer.ReleaseSegmentReaders();

        // Not current: the next checkpoint publishes it again, every section measuring its owners' records.
        CheckpointPublication again = SessionCheckpoints.Publish(store, Committed);
        Assert.True(again.Outcome == CheckpointOutcome.Published, again.Reason);
        Assert.Equal(SessionTileIndex.Minor, FileOf(session).Minor);
        using (TileReading reading = FileOf(session).Read(store.Root))
        {
            Assert.All(SessionSegments.Names(store.Current!), segment => Assert.True(reading.Section(segment).Measured));
        }

        SessionStore after = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        Assert.Equal(expected, OwnerBytes(after, extent, 61, 19, group, EvidencePolicy.AllIncludingConflicting));
        Assert.Equal(0, after.SegmentReaderCache.Entries);
        after.ReleaseSegmentReaders();
        store.ReleaseSegmentReaders();
        twin.Store.ReleaseSegmentReaders();
    }
}
