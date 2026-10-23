using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// Since minor 6 a group's process lanes under a byte ranking add up from a tile index's byte tallies
/// (`contracts/tile-index-v1.md` §4): a reopened session's lanes are exactly what its rows measure, under every policy, in
/// the machine row's columns or fewer, opening no segment; a tile's records are read only where a boundary of either falls
/// among them; and a part of the index that cannot be read has the rows measured.
/// </summary>
public sealed partial class SessionTileIndexTests
{
    [Theory(DisplayName = "I4: a reopened session's group lanes under a byte ranking add up from its byte tallies exactly what its rows measure, under every policy, in the machine row's columns or fewer, and open no segment")]
    [InlineData(8)]
    [InlineData(97)]
    [InlineData(20_261_031)]
    public void GroupByteLanesMeasureWhatRowsMeasure(int seed)
    {
        var random = new Random(seed);
        for (int trial = 0; trial < 6; trial++)
        {
            // The same capture twice, its bytes varied and some of its records untimed: with its checkpoint's tiles, and
            // without, where the lanes read their rows.
            using var tiled = new TemporarySession();
            using var twin = new TemporarySession();
            long stretch = trial % 3 == 0 ? 1 : trial % 3 == 1 ? 1_000 : 1_000_000;
            int rowsPerSegment = random.Next(5, 80);
            foreach ((ObservationRowV1[] rows, SourceFieldRowV1[] fields) in Stretched(random, stretch))
            {
                ObservationRowV1[] varied = Varied(random, rows, timed: trial >= 3);
                _ = Publish(tiled.Store, varied, rowsPerSegment: rowsPerSegment, fields: fields);
                _ = Publish(twin.Store, varied, rowsPerSegment: rowsPerSegment, fields: fields);
            }

            Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(tiled.Store, Committed).Outcome);
            tiled.Store.ReleaseSegmentReaders();
            Bound bound = Bind(twin.Store);
            ProcessInstanceId[] instances = [.. bound.Processes.Instances.Select(instance => instance.Id)];
            long[] ticks = [.. bound.Segments.SelectMany(segment => Enumerable.Range(0, segment.RowCount)
                .Select(row => segment.SignedValue(SegmentColumnId.SessionRelativeTicks, row))
                .Where(nanoseconds => nanoseconds is not null).Select(nanoseconds => nanoseconds!.Value / 100))];
            Assert.True(instances.Length >= 2);
            long low = ticks.Min() - stretch;
            long high = ticks.Max() + 1 + stretch;

            SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(tiled.Path));
            foreach (EvidencePolicy policy in Enum.GetValues<EvidencePolicy>())
            {
                for (int query = 0; query < 4; query++)
                {
                    // Some of the instances, or one; over the whole extent or anywhere; in the machine row's columns, or in
                    // fewer, as the lanes are past their cell budget.
                    ProcessInstanceId[] group = random.Next(4) == 0 ? [instances[random.Next(instances.Length)]]
                        : [.. instances.Where((_, index) => index < 2 || random.Next(3) != 0)];
                    long start = low + random.NextInt64(0, high - low);
                    TimeRange interval = query == 0 ? new(low, high) : new(start, start + 1 + random.NextInt64(0, high - start));
                    int columns = random.Next(3) == 0 ? random.Next(1, 6) : random.Next(1, 300);
                    int laneColumns = random.Next(2) == 0 ? columns : random.Next(1, columns + 1);
                    Assert.Equal(
                        OwnerBytes(twin.Store, interval, columns, laneColumns, group, policy),
                        OwnerBytes(reopened, interval, columns, laneColumns, group, policy));
                }
            }

            Assert.Equal(0, reopened.SegmentReaderCache.Entries);
            Assert.Null(SessionDerivationCache.For(reopened.Current!).TilesProblem);
            reopened.ReleaseSegmentReaders();
            twin.Store.ReleaseSegmentReaders();
        }
    }

    [Fact(DisplayName = "S3: a group's byte lanes add whole tiles' byte tallies, reading records only where a boundary of the machine row's or the lanes' columns falls among them, a section's or the level spanning the segments first")]
    public void GroupByteLanesReadRecordsOnlyAtBoundaries()
    {
        // 50,000 records of five pairs talking in shifts, in four segments, each tile of a hundred records or more keeping
        // its byte tallies; three of the ten processes in lanes of their own.
        ObservationRowV1[] rows = Shifts(5, 50_000);
        using var session = Published(rows, rowsPerSegment: 12_500);
        using var twin = new TemporarySession();
        Publish(twin.Store, rows, rowsPerSegment: 12_500);
        SessionManifestV1 manifest = session.Store.Current!;
        Bound bound = Bind(session.Store);
        TimeRange whole = Extent(rows);
        // Records 300 ticks apart, three or four to each finest tile a thousand ticks wide: each end falls among a tile's.
        var interval = new TimeRange(2_142_550, 13_333_750);
        Assert.True(whole.StartTicks < interval.StartTicks && interval.EndTicks < whole.EndTicks);
        ProcessInstanceId[] group = [.. bound.Processes.Instances.Take(3).Select(instance => instance.Id)];
        int[] laneOf = [.. bound.Processes.Instances.Select((_, place) => place < 3 ? place : -1)];
        var machine = new TimelineColumns(interval, 64, tallyMechanisms: false);
        var lanes = new TimelineColumns(interval, 16, tallyMechanisms: false);
        string[] measured = OwnerBytes(twin.Store, interval, 64, 16, group, EvidencePolicy.IncludeCorrelated);
        TileIndexFile file = SessionTileIndex.Open(session.Store.Root, SessionTileIndex.NamedBy(manifest)!, manifest.SessionId);
        bool[] admitted = SessionTileIndex.AdmittedStrengths(EvidencePolicy.IncludeCorrelated);

        // Section by section, as where no level spans the segments.
        using (TileReading reading = file.Read(session.Store.Root))
        {
            var into = new GroupByteLanes(machine, lanes, laneOf, admitted, new(65), new((3 * 16) + 1));
            foreach (string name in SessionSegments.Names(manifest))
            {
                Assert.True(reading.MeasureColumns(reading.Section(name), interval, into, CancellationToken.None));
            }

            // The records of the tiles its 65 boundaries fall among, and of their neighbours too narrow to keep byte
            // tallies, about ten each; every other tile is added whole.
            Assert.InRange(reading.TilesOfRecordsRead, 65, 1_000);
            Assert.InRange(reading.TilesOfTalliesRead, 64, 2_000);
            Assert.Equal(measured, Added(into, group));
        }

        // The spanning level first, and the sections beneath only the ranges it leaves.
        using (TileReading reading = file.Read(session.Store.Root))
        {
            Assert.True(reading.Spanning().KeepsByteTallies);
            var into = new GroupByteLanes(machine, lanes, laneOf, admitted, new(65), new((3 * 16) + 1));
            TileRemainders left = reading.SpanMeasureColumns(into, CancellationToken.None);
            Assert.InRange(left.Ranges.Count, 1, 66);
            foreach ((TileSection section, List<TimeRange> ranges) in reading.Beneath(left))
            {
                foreach (TimeRange range in ranges)
                {
                    Assert.True(reading.MeasureColumns(section, range, into, CancellationToken.None));
                }
            }

            Assert.Equal(measured, Added(into, group));
        }

        // The lanes measure the same, and open no segment.
        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        Assert.Equal(measured, OwnerBytes(reopened, interval, 64, 16, group, EvidencePolicy.IncludeCorrelated));
        Assert.Equal(0, reopened.SegmentReaderCache.Entries);
        reopened.ReleaseSegmentReaders();
        twin.Store.ReleaseSegmentReaders();
        session.Store.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "I4: byte tallies that fail their checksum are refused, never misread: a group's byte lanes are measured from the segments' rows instead")]
    public void DamagedByteTalliesLeaveAGroupsLanesToItsRows()
    {
        ObservationRowV1[] rows = Shifts(2, 3_211);
        TimeRange whole = Extent(rows);
        using var session = Published(rows);
        (TileSection section, string path) = TopOf(session);
        int top = section.Levels - 1;
        byte[] kept = File.ReadAllBytes(path);
        TileBlock block;
        using (FileStream stream = File.OpenRead(path))
        {
            block = FileOf(session).Block(() => stream, section, top, 0);
        }

        kept[(int)section.ByteTalliesAt + block.ByteLinks[0] + 1] ^= 0x40;
        File.WriteAllBytes(path, kept);
        using var twin = new TemporarySession();
        Publish(twin.Store, rows);
        ProcessInstanceId[] group = [.. Bind(twin.Store).Processes.Instances.Take(2).Select(instance => instance.Id)];
        SessionStore viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        // One column, in which the top block's tiles are added whole.
        Assert.Equal(OwnerBytes(twin.Store, whole, 1, 1, group, EvidencePolicy.DirectOnly), OwnerBytes(viewer, whole, 1, 1, group, EvidencePolicy.DirectOnly));
        Assert.Equal(
            $"The persisted tile index is not readable: the byte tallies of block 0 of level {top} of the section of '{section.Entry.Name}' does not match its checksum.",
            SessionDerivationCache.For(viewer.Current!).TilesProblem);
        Assert.Equal(1, viewer.SegmentReaderCache.Entries);
        viewer.ReleaseSegmentReaders();
        twin.Store.ReleaseSegmentReaders();
    }

    /// <summary>
    /// A group's byte lanes as the window plots them: the machine row's columns, then each lane's, each column's bytes in
    /// words.
    /// </summary>
    private static string[] OwnerBytes(
        SessionStore store, TimeRange interval, int columns, int laneColumns, ProcessInstanceId[] owners, EvidencePolicy policy)
    {
        SessionOwnerByteMeasures measures = SessionIntervalByteQuery.MeasureByOwner(store, interval, columns, laneColumns, owners, policy);
        return
        [
            .. measures.Machine.Columns.Select((bytes, column) => $"machine {column} {bytes}"),
            .. measures.Lanes.SelectMany(lane => lane.Columns.Select((bytes, column) => $"{lane.Scope.Owner} {column} {bytes}")),
        ];
    }

    /// <summary>What <paramref name="lanes"/> added up, in <see cref="OwnerBytes"/>' words, its lanes those of <paramref name="owners"/>.</summary>
    private static string[] Added(GroupByteLanes lanes, ProcessInstanceId[] owners)
    {
        int machineColumns = lanes.Machine.Counts.Count;
        int laneColumns = lanes.Lanes.Counts.Count;
        return
        [
            .. Enumerable.Range(0, machineColumns).Select(column => $"machine {column} {lanes.MachineBytes.Of(column)}"),
            .. owners.SelectMany((owner, lane) => Enumerable.Range(0, laneColumns)
                .Select(column => $"{owner} {column} {lanes.LaneBytes.Of((lane * laneColumns) + column)}")),
        ];
    }
}
