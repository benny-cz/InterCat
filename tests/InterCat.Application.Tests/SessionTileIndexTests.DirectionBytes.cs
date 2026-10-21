using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// Since minor 7 a tile index keeps each record's source direction in its byte tallies (`contracts/tile-index-v1.md` §1),
/// so a process's direction rows under a byte ranking add up from them: a reopened session's rows are exactly what its
/// rows measure, under every policy, opening no segment; a tile's records are read only where a column boundary falls
/// among them; and a part of the index that cannot be read has the rows measured.
/// </summary>
public sealed partial class SessionTileIndexTests
{
    [Theory(DisplayName = "I4: a reopened session's process's direction rows under a byte ranking add up from its byte tallies exactly what its rows measure, under every policy, and open no segment")]
    [InlineData(9)]
    [InlineData(101)]
    [InlineData(20_261_032)]
    public void DirectionByteRowsMeasureWhatRowsMeasure(int seed)
    {
        var random = new Random(seed);
        for (int trial = 0; trial < 6; trial++)
        {
            // The same capture twice, its bytes varied and some of its records untimed: with its checkpoint's tiles, and
            // without, where the rows are read.
            using var tiled = new TemporarySession();
            using var twin = new TemporarySession();
            long stretch = trial % 3 == 0 ? 1 : trial % 3 == 1 ? 1_000 : 1_000_000;
            int rowsPerSegment = random.Next(5, 80);
            foreach ((ObservationRowV1[] rows, SourceFieldRowV1[] fields) in Stretched(random, stretch))
            {
                ObservationRowV1[] varied = Varied(random, rows, timed: trial % 2 == 0);
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
            long low = ticks.Min() - stretch;
            long high = ticks.Max() + 1 + stretch;

            SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(tiled.Path));
            foreach (EvidencePolicy policy in Enum.GetValues<EvidencePolicy>())
            {
                for (int query = 0; query < 4; query++)
                {
                    // One process, over the whole extent or anywhere.
                    ProcessInstanceId owner = instances[random.Next(instances.Length)];
                    long start = low + random.NextInt64(0, high - low);
                    TimeRange interval = query == 0 ? new(low, high) : new(start, start + 1 + random.NextInt64(0, high - start));
                    int columns = random.Next(3) == 0 ? random.Next(1, 6) : random.Next(1, 300);
                    Assert.Equal(
                        DirectionBytes(twin.Store, interval, columns, owner, policy),
                        DirectionBytes(reopened, interval, columns, owner, policy));
                }
            }

            Assert.Equal(0, reopened.SegmentReaderCache.Entries);
            Assert.Null(SessionDerivationCache.For(reopened.Current!).TilesProblem);
            reopened.ReleaseSegmentReaders();
            twin.Store.ReleaseSegmentReaders();
        }
    }

    [Fact(DisplayName = "S3: a process's byte direction rows add whole tiles' byte tallies, reading records only where a column boundary falls among them, a section's or the level spanning the segments first")]
    public void DirectionByteRowsReadRecordsOnlyAtBoundaries()
    {
        // 50,010 records of five pairs talking in shifts, in four segments, each tile of a hundred records or more keeping
        // its byte tallies: the first client's rows.
        ObservationRowV1[] rows = Shifts(5, 50_010);
        using var session = Published(rows, rowsPerSegment: 12_503);
        using var twin = new TemporarySession();
        Publish(twin.Store, rows, rowsPerSegment: 12_503);
        SessionManifestV1 manifest = session.Store.Current!;
        Bound bound = Bind(session.Store);
        TimeRange whole = Extent(rows);

        // Records 300 ticks apart, three or four to each finest tile a thousand ticks wide: each end falls among a tile's.
        var interval = new TimeRange(1_071_250, 14_285_750);
        Assert.True(whole.StartTicks < interval.StartTicks && interval.EndTicks < whole.EndTicks);
        ProcessInstanceId owner = bound.Processes.Instances[0].Id;
        var machine = new TimelineColumns(interval, 64, tallyMechanisms: false);
        string[] measured = DirectionBytes(twin.Store, interval, 64, owner, EvidencePolicy.IncludeCorrelated);
        TileIndexFile file = SessionTileIndex.Open(session.Store.Root, SessionTileIndex.NamedBy(manifest)!, manifest.SessionId);
        bool[] admitted = SessionTileIndex.AdmittedStrengths(EvidencePolicy.IncludeCorrelated);
        int rowCount = SessionTimelineQuery.LaneDirections.Count;

        // Section by section, as where no level spans the segments.
        using (TileReading reading = file.Read(session.Store.Root))
        {
            var into = new DirectionByteRows(machine, 0, admitted, new(65), new((rowCount * 64) + 1));
            foreach (string name in SessionSegments.Names(manifest))
            {
                Assert.True(reading.MeasureColumns(reading.Section(name), interval, into, CancellationToken.None));
            }

            // The records of the tiles its 65 boundaries fall among, and of their neighbours too narrow to keep byte
            // tallies, about ten each; every other tile is added whole.
            Assert.InRange(reading.TilesOfRecordsRead, 65, 1_000);
            Assert.InRange(reading.TilesOfTalliesRead, 64, 2_000);
            Assert.Equal(measured, Added(into));
        }

        // The spanning level first, and the sections beneath only the ranges it leaves.
        using (TileReading reading = file.Read(session.Store.Root))
        {
            Assert.True(reading.Spanning().KeepsByteTallies);
            var into = new DirectionByteRows(machine, 0, admitted, new(65), new((rowCount * 64) + 1));
            TileRemainders left = reading.SpanMeasureColumns(into, CancellationToken.None);
            Assert.InRange(left.Ranges.Count, 1, 66);
            foreach ((TileSection section, List<TimeRange> ranges) in reading.Beneath(left))
            {
                foreach (TimeRange range in ranges)
                {
                    Assert.True(reading.MeasureColumns(section, range, into, CancellationToken.None));
                }
            }

            Assert.Equal(measured, Added(into));
        }

        // The rows measure the same, and open no segment.
        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        Assert.Equal(measured, DirectionBytes(reopened, interval, 64, owner, EvidencePolicy.IncludeCorrelated));
        Assert.Equal(0, reopened.SegmentReaderCache.Entries);
        reopened.ReleaseSegmentReaders();
        twin.Store.ReleaseSegmentReaders();
        session.Store.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "I4: byte tallies that fail their checksum are refused, never misread: a process's byte direction rows are measured from the segments' rows instead")]
    public void DamagedByteTalliesLeaveDirectionRowsToTheirRows()
    {
        ObservationRowV1[] rows = Shifts(2, 3_223);
        TimeRange whole = Extent(rows);
        using var session = Published(rows);
        (TileSection section, string path) = TopOf(session);
        int top = section.Levels - 1;
        byte[] kept = File.ReadAllBytes(path);
        TileBlock block;
        using (FileStream stream = File.OpenRead(path))
        {
            block = FileOf(session).Block(stream, section, top, 0);
        }

        kept[(int)section.ByteTalliesAt + block.ByteLinks[0] + 1] ^= 0x40;
        File.WriteAllBytes(path, kept);
        using var twin = new TemporarySession();
        Publish(twin.Store, rows);
        ProcessInstanceId owner = Bind(twin.Store).Processes.Instances[1].Id;
        SessionStore viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));

        // One column, in which the top block's tiles are added whole.
        Assert.Equal(DirectionBytes(twin.Store, whole, 1, owner, EvidencePolicy.DirectOnly), DirectionBytes(viewer, whole, 1, owner, EvidencePolicy.DirectOnly));
        Assert.Equal(
            $"The persisted tile index is not readable: the byte tallies of block 0 of level {top} of the section of '{section.Entry.Name}' does not match its checksum.",
            SessionDerivationCache.For(viewer.Current!).TilesProblem);
        Assert.Equal(1, viewer.SegmentReaderCache.Entries);
        viewer.ReleaseSegmentReaders();
        twin.Store.ReleaseSegmentReaders();
    }

    /// <summary>
    /// A process's byte direction rows as the window plots them: the machine row's columns, then each direction's row's,
    /// each column's bytes in words.
    /// </summary>
    private static string[] DirectionBytes(SessionStore store, TimeRange interval, int columns, ProcessInstanceId owner, EvidencePolicy policy)
    {
        SessionDirectionByteMeasures measures = SessionIntervalByteQuery.MeasureByDirection(store, interval, columns, owner, policy);
        return
        [
            .. measures.Machine.Columns.Select((bytes, column) => $"machine {column} {bytes}"),
            .. measures.Lanes.SelectMany(row => row.Columns.Select((bytes, column) => $"{row.Scope.Direction} {column} {bytes}")),
        ];
    }

    /// <summary>What <paramref name="rows"/> added up, in <see cref="DirectionBytes"/>' words.</summary>
    private static string[] Added(DirectionByteRows rows)
    {
        int columns = rows.Machine.Counts.Count;
        return
        [
            .. Enumerable.Range(0, columns).Select(column => $"machine {column} {rows.MachineBytes.Of(column)}"),
            .. SessionTimelineQuery.LaneDirections.SelectMany((direction, row) => Enumerable.Range(0, columns)
                .Select(column => $"{direction} {column} {rows.LaneBytes.Of((row * columns) + column)}")),
        ];
    }
}
