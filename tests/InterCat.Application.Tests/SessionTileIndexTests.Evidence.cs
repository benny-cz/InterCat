using System.Buffers.Binary;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// An owner's or a channel's evidence page reads the rows its postings place (ADR-114): a segment holding none of its
/// records is not opened, and in one holding some the page reads and binds those rows alone, its filters deciding each as
/// before - page after page exactly what reading every row lists; a section posting nothing, holding an untimed record, or
/// whose postings cannot be read, has its rows read.
/// </summary>
public sealed partial class SessionTileIndexTests
{
    [Theory(DisplayName = "S3: an owner's or a channel's evidence pages read only the rows its postings place, each bound alone - page after page exactly what reading every row lists, under every policy, time scope, narrowing and page size")]
    [InlineData(8)]
    [InlineData(1_066)]
    [InlineData(20_261_118)]
    public void PostedEvidencePagesListWhatRowsList(int seed)
    {
        var random = new Random(seed);
        int fewer = 0;
        for (int trial = 0; trial < 4; trial++)
        {
            // The same capture twice, its records timed where the trial says: with its checkpoint's tiles, and without,
            // where every page reads every row.
            using var tiled = new TemporarySession();
            using var twin = new TemporarySession();
            long stretch = trial % 2 == 0 ? 1 : 1_000;
            int rowsPerSegment = random.Next(5, 80);
            foreach ((ObservationRowV1[] rows, SourceFieldRowV1[] fields) in Stretched(random, stretch))
            {
                ObservationRowV1[] varied = Varied(random, rows, timed: trial < 3);
                _ = Publish(tiled.Store, varied, rowsPerSegment: rowsPerSegment, fields: fields);
                _ = Publish(twin.Store, varied, rowsPerSegment: rowsPerSegment, fields: fields);
            }

            Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(tiled.Store, Committed).Outcome);
            tiled.Store.ReleaseSegmentReaders();
            Bound bound = Bind(twin.Store);

            // A row bound alone binds as its segment's rows bound together do.
            foreach (SegmentReaderV1 segment in bound.Segments)
            {
                PackedOwners owners = SegmentBindings.OwnersOf(segment, bound.Processes);
                PackedChannels channels = SegmentBindings.ChannelsOf(segment, bound.Relations);
                sbyte[] ends = TransportRelationIndex.EndsOf(segment);
                for (int row = 0; row < segment.RowCount; row++)
                {
                    Assert.Equal(owners[row], SegmentBindings.OwnerOf(segment, row, bound.Processes));
                    Assert.Equal(channels[row], SegmentBindings.ChannelOf(segment, row, bound.Relations));
                    Assert.Equal(ends[row], TransportRelationIndex.EndOf(segment, row));
                }
            }

            long[] ticks = [.. bound.Segments.SelectMany(segment => Enumerable.Range(0, segment.RowCount)
                .Select(row => segment.SignedValue(SegmentColumnId.SessionRelativeTicks, row))
                .Where(nanoseconds => nanoseconds is not null).Select(nanoseconds => nanoseconds!.Value / 100))];
            SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(tiled.Path));
            int asked = 0;
            for (int query = 0; query < 24; query++)
            {
                EvidencePolicy policy = Enum.GetValues<EvidencePolicy>()[random.Next(Enum.GetValues<EvidencePolicy>().Length)];
                if (Asked(random, bound, policy) is not { } scope)
                {
                    continue;
                }

                TimeRange? interval = ticks.Length == 0 || random.Next(3) == 0 ? null : Part(random, ticks);
                var ask = scope with
                {
                    Interval = interval,
                    PageSize = random.Next(1, 60),
                    Mechanism = random.Next(4) == 0 ? (random.Next(2) == 0 ? Mechanism.Tcp : Mechanism.Udp) : null,
                    Direction = random.Next(5) == 0 ? (random.Next(2) == 0 ? Direction.Outbound : Direction.Inbound) : null,
                    End = scope.Paired && random.Next(3) == 0 ? random.Next(2) : null,
                };

                (string[] read, long rowsRead) = Pages(twin.Store, ask);
                (string[] posted, long postedRowsRead) = Pages(reopened, ask);
                Assert.Equal(read, posted);
                Assert.True(postedRowsRead <= rowsRead, $"the posted pages read {postedRowsRead} rows, the rows' {rowsRead}");
                fewer += postedRowsRead < rowsRead ? 1 : 0;
                asked++;
            }

            Assert.True(asked > 0);
            Assert.Null(SessionDerivationCache.For(reopened.Current!).TilesProblem);
            reopened.ReleaseSegmentReaders();
            twin.Store.ReleaseSegmentReaders();
        }

        // Most scopes' pages read only the rows their postings place, fewer than every row.
        Assert.True(fewer > 10, $"only {fewer} scopes' pages read fewer rows from their postings");
    }

    [Fact(DisplayName = "S3: a process's or a channel's first evidence page opens only the segments holding its records, reading its own rows: a pair talking last reads one segment of twenty past the first, not every row before it")]
    public void AFirstEvidencePageOpensOnlyTheSegmentsHoldingItsRecords()
    {
        // Five pairs talking in shifts over twenty segments of a thousand records, each pair's processes created in the
        // first: the last pair's records lie in the first segment and the last four.
        ObservationRowV1[] rows = Shifts(5, 20_000);
        using var session = Published(rows, rowsPerSegment: 1_000);
        using var twin = new TemporarySession();
        Publish(twin.Store, rows, rowsPerSegment: 1_000);
        Bound bound = Bind(twin.Store);
        ProcessInstanceId client = bound.Processes.Instances.Single(instance => instance.ProcessId == 10_004).Id;
        string channel = bound.Relations.Relations.Single(relation => relation.First.ProcessId == 10_004 || relation.Second.ProcessId == 10_004).StableKey;

        // Its first page: the client's creation in the first segment - beside its server's, made at the same reading, which
        // the page reads and passes over - then 99 of its transfers in the seventeenth, and the 100th saying there is more.
        SessionStore viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        SessionEvidencePage page = SessionEvidenceQuery.Read(viewer, ownerProcessScope: client);
        Assert.Equal(Listed(SessionEvidenceQuery.Read(twin.Store, ownerProcessScope: client)), Listed(page));
        Assert.Equal((2, 102L), (viewer.SegmentReaderCache.Entries, page.RowsRead));
        Assert.Null(SessionDerivationCache.For(viewer.Current!).TilesProblem);
        viewer.ReleaseSegmentReaders();

        // The channel's first page: the seventeenth segment alone, and the 101st record saying there is more.
        viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        page = SessionEvidenceQuery.Read(viewer, channelKey: channel);
        Assert.Equal(Listed(SessionEvidenceQuery.Read(twin.Store, channelKey: channel)), Listed(page));
        Assert.Equal((1, 101L), (viewer.SegmentReaderCache.Entries, page.RowsRead));
        viewer.ReleaseSegmentReaders();

        // Its next page reads on from its cursor, in the same segment.
        viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        SessionEvidencePage next = SessionEvidenceQuery.Read(viewer, channelKey: channel, cursor: page.NextCursor);
        Assert.Equal(
            Listed(SessionEvidenceQuery.Read(twin.Store, channelKey: channel, cursor: SessionEvidenceQuery.Read(twin.Store, channelKey: channel).NextCursor)),
            Listed(next));
        Assert.Equal(1, viewer.SegmentReaderCache.Entries);
        viewer.ReleaseSegmentReaders();
        twin.Store.ReleaseSegmentReaders();
    }

    [Theory(DisplayName = "I4: postings an evidence page cannot read, or that place a reading none of a segment's rows holds, are refused and said once: the page reads the segments' rows and lists the same records")]
    [InlineData(0)]
    [InlineData(1)]
    public void EvidenceFromDamagedPostingsIsRefused(int damage)
    {
        // Each case's session differs from the other's, so no two share a manifest, nor what a generation's derivations keep.
        ObservationRowV1[] rows = Shifts(2, 3_000 + damage);
        using var session = Published(rows);
        using var twin = new TemporarySession();
        Publish(twin.Store, rows);
        Bound bound = Bind(twin.Store);
        (TileSection section, string path) = TopOf(session);
        string name = section.Entry.Name;
        byte[] kept = File.ReadAllBytes(path);
        int sectionAt = (int)section.Entry.Offset;
        int ownersAt = sectionAt + (int)section.Postings.OwnersOffset;
        (int width, _, int directoryEnd, List<(ulong Key, ulong Kind, ulong Count, int KindAt)> lists) = PostingDirectory(kept, ownersAt);
        Assert.Equal(4, width);

        // The first list of a process's transfers, and where its readings begin.
        int chosen = lists.FindIndex(list => list.Count >= 100);
        int listAt = directoryEnd + 4;
        for (int list = 0; list < chosen; list++)
        {
            listAt += (4 * (int)lists[list].Count) + 4;
        }

        int count = (int)lists[chosen].Count;
        string why;
        if (damage == 0)
        {
            // A byte of its readings.
            kept[listAt + 5] ^= 0x40;
            why = $"list {chosen} of the owners' postings of the section of '{name}' does not match its checksum.";
        }
        else
        {
            // Its first reading a tick later, which no row is: the records lie 300 ticks apart.
            uint reading = BinaryPrimitives.ReadUInt32LittleEndian(kept.AsSpan(listAt));
            Assert.True(BinaryPrimitives.ReadUInt32LittleEndian(kept.AsSpan(listAt + 4)) > reading + 1);
            BinaryPrimitives.WriteUInt32LittleEndian(kept.AsSpan(listAt), reading + 1);
            Crc32C.Write(kept.AsSpan(listAt + (4 * count)), Crc32C.Compute(kept.AsSpan(listAt, 4 * count)));
            why = $"the postings of the section of '{name}' place a reading none of its segment's rows holds.";
        }

        File.WriteAllBytes(path, kept);
        ProcessInstanceId owner = bound.Processes.Instances[(int)(lists[chosen].Key >> 3) - 1].Id;
        SessionStore viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        var ask = new EvidenceAsk([owner], null, false, EvidencePolicy.IncludeCorrelated, null, 37, null, null, null);
        Assert.Equal(Pages(twin.Store, ask).Records, Pages(viewer, ask).Records);
        Assert.Equal("The persisted tile index is not readable: " + why, SessionDerivationCache.For(viewer.Current!).TilesProblem);
        viewer.ReleaseSegmentReaders();
        twin.Store.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "I14: an index of minor 9, which posts nothing, and a section holding an untimed record, which no list posts, have their segments' rows read by an evidence page: the same records")]
    public void UnpostedRowsAreReadByAnEvidencePage()
    {
        // Two pairs in shifts, some records untimed: their sections post the rest, and a page reads their segments whole.
        var random = new Random(9_066);
        ObservationRowV1[] rows = [.. Shifts(2, 4_000).Select((row, index) => index > 10 && index % 97 == 0 ? row with { SessionRelativeTicks = null } : row)];
        using var session = Published(rows, rowsPerSegment: 1_000);
        using var twin = new TemporarySession();
        Publish(twin.Store, rows, rowsPerSegment: 1_000);
        Bound bound = Bind(twin.Store);
        ProcessInstanceId[] owners = [.. bound.Processes.Instances.Select(instance => instance.Id)];
        string channel = bound.Relations.Relations[0].StableKey;
        using (TileReading reading = FileOf(session).Read(session.Store.Root))
        {
            Assert.All(SessionSegments.Names(session.Store.Current!), name =>
                Assert.Equal((true, true), (reading.Section(name).Posted, reading.Section(name).Untimed > 0)));
        }

        foreach (ProcessInstanceId owner in owners)
        {
            var ask = new EvidenceAsk([owner], null, false, EvidencePolicy.IncludeCorrelated, null, 61, null, null, null);
            SessionStore viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
            (string[] read, long rowsRead) = Pages(twin.Store, ask);
            (string[] listed, long listedRowsRead) = Pages(viewer, ask);
            Assert.Equal(read, listed);
            Assert.Equal(rowsRead, listedRowsRead);
            viewer.ReleaseSegmentReaders();
        }

        // As revision 1063 wrote it: no postings, and every scope's pages read the rows.
        Republish(session, tiles: content => content.Write(Earlier(File.ReadAllBytes(PathOf(session)), 9)));
        Assert.Equal(9, FileOf(session).Minor);
        session.Store.ReleaseSegmentReaders();
        foreach (EvidenceAsk ask in new[]
        {
            new EvidenceAsk([owners[^1]], null, false, EvidencePolicy.DirectOnly, null, 29, null, null, null),
            new EvidenceAsk(null, channel, true, EvidencePolicy.IncludeCorrelated, null, 43, null, null, random.Next(2)),
        })
        {
            SessionStore viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
            (string[] read, long rowsRead) = Pages(twin.Store, ask);
            (string[] listed, long listedRowsRead) = Pages(viewer, ask);
            Assert.Equal(read, listed);
            Assert.Equal(rowsRead, listedRowsRead);
            Assert.Null(SessionDerivationCache.For(viewer.Current!).TilesProblem);
            viewer.ReleaseSegmentReaders();
        }

        twin.Store.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "R22: an evidence page reads no postings of a tile index stating another derivation's bindings: it reads every row, listing what they list")]
    public void EvidenceReadsNoPostingsOfAnotherDerivation()
    {
        // Two pairs in shifts, every record timed, under an index whose postings state another derivation.
        ObservationRowV1[] rows = Shifts(2, 4_000);
        using var session = Published(rows, rowsPerSegment: 1_000);
        using var twin = new TemporarySession();
        Publish(twin.Store, rows, rowsPerSegment: 1_000);
        SessionStore store = session.Store;
        SessionManifestV1 published = store.Current!;
        (ProcessInstanceIndex processes, TransportRelationIndex relations) = Derivations(store);
        TileDerivation derivation = SessionTileIndex.Derivation(processes, relations);
        Republish(session, tiles: content => SessionTileIndex.Write(content, published.SessionId, published.Generation,
        [
            .. SessionOverviewIndex.ObservationSegments(published).Select(segment => (segment, SessionSegments.Open(store, published, segment.Name))),
        ], processes, relations, CancellationToken.None, stated: derivation with { Fingerprint = new byte[32] }));
        store.ReleaseSegmentReaders();

        // A process's pages and a channel's read every row, as without postings.
        Bound bound = Bind(twin.Store);
        foreach (EvidenceAsk ask in new[]
        {
            new EvidenceAsk([bound.Processes.Instances[^1].Id], null, false, EvidencePolicy.IncludeCorrelated, null, 97, null, null, null),
            new EvidenceAsk(null, bound.Relations.Relations[0].StableKey, true, EvidencePolicy.IncludeCorrelated, null, 97, null, null, null),
        })
        {
            SessionStore viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
            (string[] read, long rowsRead) = Pages(twin.Store, ask);
            (string[] listed, long listedRowsRead) = Pages(viewer, ask);
            Assert.Equal(read, listed);
            Assert.Equal(rowsRead, listedRowsRead);
            Assert.Null(SessionDerivationCache.For(viewer.Current!).TilesProblem);
            viewer.ReleaseSegmentReaders();
        }

        twin.Store.ReleaseSegmentReaders();
    }

    /// <summary>One evidence scope, as a page asks for it: owners or a channel, and what narrows it.</summary>
    private sealed record EvidenceAsk(
        ProcessInstanceId[]? Owners,
        string? Channel,
        bool Paired,
        EvidencePolicy Policy,
        TimeRange? Interval,
        int PageSize,
        Mechanism? Mechanism,
        Direction? Direction,
        int? End);

    /// <summary>
    /// A scope a page could ask for under <paramref name="policy"/>: a process, a group, a paired channel, a one-sided
    /// connection or a host, each one the policy admits; null where the capture holds none of the kind drawn.
    /// </summary>
    private static EvidenceAsk? Asked(Random random, Bound bound, EvidencePolicy policy)
    {
        IReadOnlyList<ProcessInstance> instances = bound.Processes.Instances;
        TransportRelation[] paired = [.. bound.Relations.Relations.Where(relation => TransportRelationIndex.Relates(relation.Mechanism)
            && SessionOverviewProjector.Admitted(relation.Strength, policy))];
        paired = [.. paired.Where(relation => paired.Count(other => other.StableKey == relation.StableKey) == 1)];
        TransportConnection[] oneSided = [.. bound.Relations.OneSided.Where(connection => SessionOverviewProjector.Admitted(connection.Strength, policy))];
        oneSided = [.. oneSided.Where(connection => oneSided.Count(other => other.StableKey == connection.StableKey) == 1)];
        switch (random.Next(5))
        {
            case 0 when instances.Count > 0:
                return new([instances[random.Next(instances.Count)].Id], null, false, policy, null, 1, null, null, null);
            case 1 when instances.Count > 1:
                return new([.. instances.Where(_ => random.Next(3) == 0).Append(instances[random.Next(instances.Count)]).Select(instance => instance.Id).Distinct()],
                    null, false, policy, null, 1, null, null, null);
            case 2 when paired.Length > 0:
                return new(null, paired[random.Next(paired.Length)].StableKey, true, policy, null, 1, null, null, null);
            case 3 when oneSided.Length > 0:
                return new(null, oneSided[random.Next(oneSided.Length)].StableKey, false, policy, null, 1, null, null, null);
            case 4 when oneSided.Length > 0:
                return new(null, HostScopeKeys.Of(HostScopeKeys.EndpointAddress(oneSided[random.Next(oneSided.Length)].RemoteEndpoint)),
                    false, policy, null, 1, null, null, null);
            default:
                return null;
        }
    }

    /// <summary>Every page of one scope, cursor after cursor: its records in the evidence tests' words, and the rows read.</summary>
    private static (string[] Records, long RowsRead) Pages(SessionStore store, EvidenceAsk ask)
    {
        var records = new List<string>();
        long read = 0;
        string? cursor = null;
        for (int pages = 0; pages < 2_000; pages++)
        {
            SessionEvidencePage page = SessionEvidenceQuery.Read(store, ask.Channel, ask.Interval, policy: ask.Policy,
                pageSize: ask.PageSize, cursor: cursor, ownerProcesses: ask.Owners, resolveOwners: true, mechanism: ask.Mechanism,
                direction: ask.Direction, end: ask.End);
            Assert.False(page.RestartRequired, page.RestartReason);
            records.AddRange(Listed(page));
            read += page.RowsRead;
            if (page.NextCursor is null)
            {
                return ([.. records], read);
            }

            cursor = page.NextCursor;
        }

        throw new InvalidOperationException("A scope's pages did not end.");
    }

    private static string[] Listed(SessionEvidencePage page) =>
        [.. page.Records.Select(record => $"{record.ObservationId} {record.Observation.NativeTicks} {record.Owner}")];

    /// <summary>A part of the session's readings, from one of them to past another.</summary>
    private static TimeRange Part(Random random, long[] ticks)
    {
        long low = ticks[random.Next(ticks.Length)];
        long high = ticks[random.Next(ticks.Length)];
        return new(Math.Min(low, high), Math.Max(low, high) + 1 + random.Next(3));
    }
}
