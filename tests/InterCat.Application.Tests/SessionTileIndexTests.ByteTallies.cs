using System.Buffers.Binary;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// Since minor 6 a tile index keeps each tile's transport-observed bytes tallied by owner, channel and accounting side
/// (`contracts/tile-index-v1.md` §1): a reopened session's brushed byte ranking measures from them exactly what its rows
/// measure, under every policy, opening no segment; it reads a tile's records only where one of the brush's ends falls
/// among them; and a part of them that cannot be read is refused, never misread, and the bytes are measured from the
/// segments.
/// </summary>
public sealed partial class SessionTileIndexTests
{
    [Theory(DisplayName = "I4: a reopened session's brushed byte ranking measures from its byte tallies exactly what its rows measure, under every policy, opening no segment but one holding an untimed row")]
    [InlineData(6)]
    [InlineData(61)]
    [InlineData(20_261_021)]
    public void PersistedByteTalliesMeasureWhatRowsMeasure(int seed)
    {
        var random = new Random(seed);
        for (int trial = 0; trial < 6; trial++)
        {
            // Readings a native tick apart, a thousand or a million; every record timed, or some untimed among them.
            using var session = new TemporarySession();
            using var twin = new TemporarySession();
            long stretch = trial % 3 == 0 ? 1 : trial % 3 == 1 ? 1_000 : 1_000_000;
            bool timed = trial < 3;
            foreach ((ObservationRowV1[] rows, SourceFieldRowV1[] fields) in Stretched(random, stretch))
            {
                ObservationRowV1[] varied = Varied(random, rows, timed);
                int perSegment = random.Next(5, 80);
                _ = Publish(session.Store, varied, rowsPerSegment: perSegment, fields: fields);
                _ = Publish(twin.Store, varied, rowsPerSegment: perSegment, fields: fields);
            }

            Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(session.Store, Committed).Outcome);
            session.Store.ReleaseSegmentReaders();
            twin.Store.ReleaseSegmentReaders();
            Bound bound = Bind(session.Store);
            long[] ticks = [.. bound.Segments.SelectMany(segment => Enumerable.Range(0, segment.RowCount)
                .Select(row => segment.SignedValue(SegmentColumnId.SessionRelativeTicks, row))
                .Where(nanoseconds => nanoseconds is not null).Select(nanoseconds => nanoseconds!.Value / 100))];
            int untimed = bound.Segments.Count(segment => Enumerable.Range(0, segment.RowCount)
                .Any(row => segment.SignedValue(SegmentColumnId.SessionRelativeTicks, row) is null));
            Assert.True(timed == (untimed == 0));
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
                    Assert.Equal(Brushed(twin.Store, interval, policy), Brushed(reopened, interval, policy));
                }
            }

            // A segment holding an untimed row has its rows read, for no tile holds that row; no other is opened.
            Assert.InRange(reopened.SegmentReaderCache.Entries, 0, untimed);
            Assert.Null(SessionDerivationCache.For(reopened.Current!).TilesProblem);
            reopened.ReleaseSegmentReaders();
            twin.Store.ReleaseSegmentReaders();
        }

        static TimeRange Ordered(long one, long other) => new(Math.Min(one, other), Math.Max(one, other) + 1);
    }

    [Fact(DisplayName = "S3: a brush's bytes are measured from whole tiles' byte tallies, and records read only where one of its ends falls among them")]
    public void ABrushMeasuresInProportionToItsEnds()
    {
        // 60,000 records of six pairs taking turns, in four segments: every tile wide enough to keep byte tallies holds every
        // process and channel.
        ObservationRowV1[] rows = Interleaved(6, 60_000);
        using var session = Published(rows, rowsPerSegment: 15_000);
        using var twin = new TemporarySession();
        Publish(twin.Store, rows, rowsPerSegment: 15_000);
        SessionManifestV1 manifest = session.Store.Current!;
        Bound bound = Bind(session.Store);
        TimeRange whole = Extent(rows);
        var brush = new TimeRange(whole.StartTicks + (whole.SpanTicks / 3) + 7, whole.EndTicks - (whole.SpanTicks / 5) - 13);
        int instances = bound.Processes.Instances.Count;
        int PlaceOf(ProcessInstanceId id) => bound.Processes.Instances.Select((instance, place) => (instance, place)).Single(entry => entry.instance.Id == id).place;

        foreach (EvidencePolicy policy in new[] { EvidencePolicy.DirectOnly, EvidencePolicy.IncludeCorrelated })
        {
            // Measured section by section from the tiles, as a ranking measures them where no level spans the segments.
            TileIndexFile file = SessionTileIndex.Open(session.Store.Root, SessionTileIndex.NamedBy(manifest)!, manifest.SessionId);
            using TileReading reading = file.Read(session.Store.Root);
            TransportRelation[] drawn = [.. bound.Relations.Relations.Where(relation =>
                TransportRelationIndex.Relates(relation.Mechanism) && SessionOverviewProjector.Admitted(relation.Strength, policy))];
            var processes = new TransportByteTally(instances + 1);
            var ends = new TransportByteTally((drawn.Length * 2) + 1);
            var measuring = new ByteMeasuring(SessionTileIndex.AdmittedStrengths(policy), SlotsOfChannels(bound, policy),
                [.. drawn.Select(relation => PlaceOf(relation.First.Id))], [.. drawn.Select(relation => PlaceOf(relation.Second.Id))],
                processes, ends);
            foreach (string name in SessionSegments.Names(manifest))
            {
                Assert.True(reading.MeasureInterval(reading.Section(name), brush, measuring, CancellationToken.None));
            }

            // Its ends' tiles' records, and the records of their neighbours too narrow to keep byte tallies; every other
            // tile is measured whole.
            Assert.InRange(reading.TilesOfRecordsRead, 2, 200);
            Assert.InRange(reading.TilesOfTalliesRead, 4, 400);
            Assert.InRange(file.BlocksDecoded, 1, 60);

            // What the rows measure.
            SessionByteMeasures measured = SessionByteRanking.Measure(twin.Store, brush, policy);
            for (int place = 0; place < instances; place++)
            {
                Assert.Equal(measured.ByProcess.GetValueOrDefault(bound.Processes.Instances[place].Id) ?? TransportBytes.None, processes.Of(place));
            }

            Assert.Equal(measured.Unattributed, processes.Of(instances));
            for (int slot = 0; slot < drawn.Length; slot++)
            {
                Assert.Equal(measured.ByChannelEnd.GetValueOrDefault(new(drawn[slot].StableKey, drawn[slot].First.Id)) ?? TransportBytes.None,
                    ends.Of(slot * 2));
                Assert.Equal(measured.ByChannelEnd.GetValueOrDefault(new(drawn[slot].StableKey, drawn[slot].Second.Id)) ?? TransportBytes.None,
                    ends.Of((slot * 2) + 1));
            }
        }

        // A ranking measures the same, and opens no segment.
        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        Assert.Equal(Brushed(twin.Store, brush, EvidencePolicy.IncludeCorrelated), Brushed(reopened, brush, EvidencePolicy.IncludeCorrelated));
        Assert.Equal(0, reopened.SegmentReaderCache.Entries);
        reopened.ReleaseSegmentReaders();
        twin.Store.ReleaseSegmentReaders();
    }

    [Theory(DisplayName = "I4: over many segments, a brush's bytes are measured from the spanning level's byte tallies whole where it can and from the sections beneath only the ranges it leaves, exactly what rows measure, opening no segment")]
    [InlineData(19)]
    [InlineData(20_261_029)]
    public void ASpanningLevelMeasuresABrushsBytes(int seed)
    {
        // Each case's session differs from the other's, so the two share no manifest, nor what its derivations keep.
        var random = new Random(seed);
        ObservationRowV1[] rows = [.. Shifts(4, 40_000 + (seed % 100)).Select((row, index) => row.ByteValue is null || index % 13 != 0
            ? row
            : row with { ByteValue = null, ByteAvailability = FieldAvailability.NotExposed })];
        using var session = Published(rows, rowsPerSegment: 1_000);
        using var twin = new TemporarySession();
        Publish(twin.Store, rows, rowsPerSegment: 1_000);
        TimeRange whole = Extent(rows);
        using (TileReading reading = FileOf(session).Read(session.Store.Root))
        {
            Assert.True(reading.Spanning().KeepsByteTallies);

            // The whole session's spanning tiles hold every byte, each whole from its byte tallies or by its finest tiles'
            // ranges, which the sections beneath are left to measure.
            var processes = new TransportByteTally(9);
            var measuring = new ByteMeasuring(SessionTileIndex.AdmittedStrengths(EvidencePolicy.IncludeCorrelated), [], [], [],
                processes, new(1));
            TileRemainders left = reading.SpanMeasure(whole, measuring, CancellationToken.None);
            Assert.InRange(reading.TilesOfTalliesRead, 1, 100);
            Assert.InRange(left.Ranges.Count, 0, 50);
            Assert.True(left.Ranges.Sum(range => range.SpanTicks) < whole.SpanTicks / 2);
        }

        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        for (int query = 0; query < 12; query++)
        {
            long start = whole.StartTicks + random.NextInt64(0, whole.SpanTicks);
            TimeRange interval = query == 0 ? whole : new(start, start + 1 + random.NextInt64(0, whole.EndTicks - start));
            EvidencePolicy policy = Enum.GetValues<EvidencePolicy>()[query % Enum.GetValues<EvidencePolicy>().Length];
            Assert.Equal(Brushed(twin.Store, interval, policy), Brushed(reopened, interval, policy));
        }

        Assert.Equal(0, reopened.SegmentReaderCache.Entries);
        Assert.Null(SessionDerivationCache.For(reopened.Current!).TilesProblem);
        reopened.ReleaseSegmentReaders();
        twin.Store.ReleaseSegmentReaders();
        session.Store.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "S2: a spanning level whose byte tally entries pass its bound keeps no byte tallies but its byte sums, and a brush measures its sections' instead")]
    public void ASpanningLevelPastItsBoundKeepsNoByteTallies()
    {
        // Sends and receipts no process owns on no channel, in three segments: the spanning level's ten finest tiles tally
        // nothing by owner or channel and keep two byte tally entries apiece, which a bound of twelve entries cannot hold.
        ObservationRowV1[] rows =
        [
            .. Enumerable.Range(0, 3_003).Select(index => Transfer(1_000 + (index * 300L), index % 2 == 0 ? ObservationKind.Send : ObservationKind.Receive,
                index % 2 == 0 ? AccountingSide.SendSide : AccountingSide.ReceiveSide, index % 1_400, owner: null, (ulong)(index + 1))
                with { SessionRelativeTicks = (1_000 + (index * 300L)) * 100 }),
        ];
        using var session = Published(rows, rowsPerSegment: 1_001);
        using var twin = new TemporarySession();
        Publish(twin.Store, rows, rowsPerSegment: 1_001);
        SessionManifestV1 published = session.Store.Current!;
        Bound bound = Bind(session.Store);
        Republish(session, tiles: content => SessionTileIndex.Write(content, published.SessionId, published.Generation,
            [.. SessionOverviewIndex.ObservationSegments(published).Zip(bound.Segments)], bound.Processes, bound.Relations,
            CancellationToken.None, spanningEntries: 12));
        using (TileReading reading = FileOf(session).Read(session.Store.Root))
        {
            TileSection spanning = reading.Spanning();
            Assert.Equal((true, false), (spanning.KeepsBytes, spanning.KeepsByteTallies));
            Assert.All(SessionSegments.Names(session.Store.Current!), name => Assert.True(reading.Section(name).KeepsByteTallies));
        }

        TimeRange whole = Extent(rows);
        TimeRange brush = new(whole.StartTicks + 77_777, whole.EndTicks - 33_333);
        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        Assert.Equal(Brushed(twin.Store, brush, EvidencePolicy.DirectOnly), Brushed(reopened, brush, EvidencePolicy.DirectOnly));
        Assert.Equal(LaneBytes(twin.Store, whole, 7), LaneBytes(reopened, whole, 7));
        Assert.Equal(0, reopened.SegmentReaderCache.Entries);
        reopened.ReleaseSegmentReaders();
        twin.Store.ReleaseSegmentReaders();
        session.Store.ReleaseSegmentReaders();
    }

    [Theory(DisplayName = "I4: byte tallies that are damaged or do not agree with their tile are refused, never misread: a brush's bytes are measured from the segments' rows instead")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void DamagedByteTalliesAreRefused(int damage)
    {
        // Each case's session differs from the others', so no two share a manifest, nor what a generation's derivations keep.
        ObservationRowV1[] rows = Shifts(2, 3_100 + damage);
        TimeRange whole = Extent(rows);
        using var session = Published(rows);
        (TileSection section, string path) = TopOf(session);
        Assert.True(section.KeepsByteTallies);
        int top = section.Levels - 1;
        byte[] kept = File.ReadAllBytes(path);
        TileBlock block;
        using (FileStream stream = File.OpenRead(path))
        {
            block = FileOf(session).Block(stream, section, top, 0);
        }

        string name = section.Entry.Name;
        int first = (int)section.ByteTalliesAt + block.ByteLinks[0];
        string why;
        switch (damage)
        {
            case 0:
                // A byte of the top block's byte tallies.
                kept[first + 1] ^= 0x40;
                why = $"the byte tallies of block 0 of level {top} of the section of '{name}' does not match its checksum.";
                break;
            case 1:
                // The top block's second tile's byte tallies begin where its first's do.
                (long at, int length) = section.BlockAt(top, 0);
                BinaryPrimitives.WriteInt32LittleEndian(kept.AsSpan((int)at + (2 * section.TileBytes) - 4), block.ByteLinks[0]);
                Seal((int)at, length);
                why = $"block 0 of level {top} of the section of '{name}' holds byte tallies it places outside the section.";
                break;
            case 2:
                // The top block's first tile says it keeps no entry, its entries after it as they were.
                Assert.InRange(kept[first], 2, 0x7F);
                kept[first] = 1;
                Seal(first, block.ByteTalliesEnd - block.ByteLinks[0] + 4);
                why = $"the byte tallies of tile 0 of level {top} of the section of '{name}' are not the tile's.";
                break;
            case 3:
                // Its first entry names an owner key no instance's binding is.
                Assert.InRange(kept[first + 1], 8, 0x7F);
                kept[first + 1] = 3;
                Seal(first, block.ByteTalliesEnd - block.ByteLinks[0] + 4);
                why = $"the byte tallies of tile 0 of level {top} of the section of '{name}' are not the tile's.";
                break;
            case 4:
                // Its second entry, its first's owner and channel received rather than sent, says sent: its first's key again.
                int second = first + 1;
                for (int field = 0; field < 5; field++)
                {
                    second = Skip(second);
                }

                Assert.Equal((0, 1), (kept[second], kept[second + 1]));
                kept[second + 1] = 0;
                Seal(first, block.ByteTalliesEnd - block.ByteLinks[0] + 4);
                why = $"the byte tallies of tile 0 of level {top} of the section of '{name}' are not the tile's.";
                break;
            default:
                // The section's header places its byte tallies a byte after its byte sums end.
                int header = (int)section.Entry.Offset;
                int talliesAt = header + 35 + (2 * section.Mechanisms.Length) + (12 * section.Levels) + 64;
                BinaryPrimitives.WriteInt64LittleEndian(kept.AsSpan(talliesAt), BinaryPrimitives.ReadInt64LittleEndian(kept.AsSpan(talliesAt)) + 1);
                BinaryPrimitives.WriteInt64LittleEndian(kept.AsSpan(talliesAt + 8), BinaryPrimitives.ReadInt64LittleEndian(kept.AsSpan(talliesAt + 8)) - 1);
                Seal(header, talliesAt + 20 - header);
                why = $"the header of the section of '{name}' does not lay out its section.";
                break;
        }

        File.WriteAllBytes(path, kept);

        // The brush measures from the segment's rows what it would have measured.
        using var twin = new TemporarySession();
        Publish(twin.Store, rows);
        SessionStore viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        Assert.Equal(Brushed(twin.Store, whole, EvidencePolicy.IncludeCorrelated), Brushed(viewer, whole, EvidencePolicy.IncludeCorrelated));
        Assert.Equal("The persisted tile index is not readable: " + why, SessionDerivationCache.For(viewer.Current!).TilesProblem);
        Assert.Equal(1, viewer.SegmentReaderCache.Entries);
        viewer.ReleaseSegmentReaders();
        twin.Store.ReleaseSegmentReaders();

        void Seal(int at, int length) => Crc32C.Write(kept.AsSpan(at + length - 4), Crc32C.Compute(kept.AsSpan(at, length - 4)));

        // Past one unsigned LEB128 number.
        int Skip(int at)
        {
            while ((kept[at] & 0x80) != 0)
            {
                at++;
            }

            return at + 1;
        }
    }

    /// <summary>
    /// A brush's measures, as the ranked table reads them: every process's bytes, every drawn channel end's, and what no
    /// process holds under <paramref name="policy"/>.
    /// </summary>
    [Fact(DisplayName = "I4: a brush from before the capture's epoch measures a segment holding a record before it from its rows, as its bounds' native readings place each record, and one from after it opens no segment")]
    public void ABrushFromBeforeTheEpochMeasuresWhatRowsMeasure()
    {
        // A clock reading nanoseconds whose epoch falls after the first thousand records: a record before it lies in the
        // tick its nanoseconds truncate toward zero, which a brush's bound, the first native reading at or after it, can
        // place on the other side of the record.
        var clock = new SourceClockDescriptor(ClockId.New(), HostId.Derive("byte-tallies-epoch"), SourceClockKind.Monotonic,
            TimestampEncoding.Qpc, 1_000_000_000, 50_000, TimestampRounding.NearestEven,
            SourceClockMath.SessionTicksPerSecond * 60);
        ObservationRowV1[] rows =
        [
            .. Interleaved(2, 3_000).Select((row, index) => row with
            {
                NativeTicks = 13_000 + (index * 37L),
                SessionRelativeTicks = 13_000 + (index * 37L) - 50_000,
            }),
        ];
        using var session = new TemporarySession();
        using var twin = new TemporarySession();
        _ = Publish(session.Store, rows, rowsPerSegment: 1_000, clock: clock);
        _ = Publish(twin.Store, rows, rowsPerSegment: 1_000, clock: clock);
        Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(session.Store, Committed).Outcome);
        session.Store.ReleaseSegmentReaders();
        twin.Store.ReleaseSegmentReaders();

        // After the epoch, every bound's tick holds the records its native reading does, and no segment is opened.
        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        foreach (TimeRange brush in new TimeRange[] { new(100, 600), new(1, 740) })
        {
            Assert.Equal(
                Brushed(twin.Store, brush, EvidencePolicy.IncludeCorrelated), Brushed(reopened, brush, EvidencePolicy.IncludeCorrelated));
        }

        // Before any record, the first segment, whose rows the brush would measure, is not opened.
        TimeRange before = new(-400, -380);
        Assert.Equal(Brushed(twin.Store, before, EvidencePolicy.DirectOnly), Brushed(reopened, before, EvidencePolicy.DirectOnly));
        Assert.Equal(0, reopened.SegmentReaderCache.Entries);

        // From the epoch or before it - in a tick holding records on both sides of a bound's native reading, from the first
        // record, or wholly before the epoch - only the first segment, holding the records before the epoch, is measured
        // from its rows; the second, whose first record is in the epoch's tick, from its tiles.
        TimeRange[] early = [new(-150, 400), new(0, 200), new(-370, 740), new(-300, -120), new(-1, 1)];
        foreach (TimeRange brush in early)
        {
            foreach (EvidencePolicy policy in Enum.GetValues<EvidencePolicy>())
            {
                Assert.Equal(Brushed(twin.Store, brush, policy), Brushed(reopened, brush, policy));
            }
        }

        Assert.Equal(1, reopened.SegmentReaderCache.Entries);
        Assert.Null(SessionDerivationCache.For(reopened.Current!).TilesProblem);
        reopened.ReleaseSegmentReaders();
        twin.Store.ReleaseSegmentReaders();
        session.Store.ReleaseSegmentReaders();
    }

    private static string[] Brushed(SessionStore store, TimeRange interval, EvidencePolicy policy)
    {
        SessionByteMeasures measures = SessionByteRanking.Measure(store, interval, policy);
        return
        [
            .. measures.ByProcess.Select(entry => $"{entry.Key} {entry.Value}").Order(StringComparer.Ordinal),
            .. measures.ByChannelEnd.Select(entry => $"{entry.Key} {entry.Value}").Order(StringComparer.Ordinal),
            $"unattributed {measures.Unattributed}",
        ];
    }

    /// <summary>
    /// <paramref name="rows"/> with their bytes varied as a busy capture's are: some declaring a size they did not record,
    /// some measuring more than four gigabytes, some stated for their owner; and, where <paramref name="timed"/>, every
    /// record at the session time the clock gives its reading, so no tile leaves one out.
    /// </summary>
    private static ObservationRowV1[] Varied(Random random, ObservationRowV1[] rows, bool timed) =>
    [
        .. rows.Select(row =>
        {
            ObservationRowV1 varied = row.ByteDomain != ByteDomain.TransportObserved || row.AccountingSide is null ? row : random.Next(8) switch
            {
                0 => row with { ByteValue = null, ByteAvailability = FieldAvailability.NotExposed, MeasurementQuality = QualityLevel.UnknownQuality },
                1 => row with { ByteValue = random.NextInt64(1L << 32, 1L << 40) },
                2 => row with { AccountingSide = AccountingSide.CanonicalOwner },
                _ => row with { ByteValue = random.Next(0, 65_536) },
            };
            return timed ? varied with { SessionRelativeTicks = varied.NativeTicks * 100 } : varied;
        }),
    ];
}
