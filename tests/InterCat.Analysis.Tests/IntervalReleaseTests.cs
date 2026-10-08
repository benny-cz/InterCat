using System.Globalization;
using System.Text;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Analysis.Tests;

/// <summary>
/// A session's oldest interval released whole units at a time, keeping the rows later records rest on for their identities
/// (ADR-043, S6): every record retained or kept binds to the process it bound to, has the other end and channel it had,
/// and an instance a retained record names is the instance it was.
/// </summary>
public sealed class IntervalReleaseTests
{
    private const string Client = "127.0.0.1:50000";
    private const string Server = "127.0.0.1:8080";

    [Fact(DisplayName = "S6: a release before a boundary keeps the lifecycle and first records later records rest on, so each binds as before")]
    public void ARecordingsOldestChunkIsReleasedKeepingItsIdentityEvidence()
    {
        using var session = new TemporarySession();
        ObservationRowV1 created = Lifecycle(10, ObservationKind.Create, 100, 2) with { ResourceName = @"C:\Tools\client.exe" };
        ObservationRowV1 once = Lifecycle(20, ObservationKind.Create, 200, 4) with { ResourceName = @"C:\Tools\once.exe" };
        Publish(session.Store, Timed(
            Lifecycle(5, ObservationKind.Create, 50, 1) with { ResourceName = @"C:\Tools\launcher.exe" },
            created,
            Lifecycle(12, ObservationKind.Exit, 50, 3, exitCode: 0),
            once,
            Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 64, 200, 5).Between("127.0.0.1:50001", "10.0.0.9:443"),
            Lifecycle(40, ObservationKind.Exit, 200, 6, exitCode: 0),
            Transfer(50, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 7).Between(Client, Server),
            Transfer(55, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 300, 8).Between(Server, Client),
            Transfer(60, ObservationKind.Send, AccountingSide.SendSide, 100, 100, 9).Between(Client, Server),
            Transfer(65, ObservationKind.Receive, AccountingSide.ReceiveSide, 100, 300, 10).Between(Server, Client)),
            fields: [Field(created, SourceField.ParentProcessId, 50)]);
        Publish(session.Store, Timed(
            Transfer(200, ObservationKind.Send, AccountingSide.SendSide, 10, 100, 11).Between(Client, Server),
            Transfer(205, ObservationKind.Receive, AccountingSide.ReceiveSide, 10, 300, 12).Between(Server, Client),
            Transfer(210, ObservationKind.Send, AccountingSide.SendSide, 20, 300, 13).Between(Server, Client),
            Transfer(215, ObservationKind.Receive, AccountingSide.ReceiveSide, 20, 100, 14).Between(Client, Server)),
            fields: [Field(once, SourceField.ProcessStartSequence, 7_001)]);
        Publish(session.Store, Timed(
            Transfer(300, ObservationKind.Disconnect, AccountingSide.EndpointActivity, 0, 100, 15).Between(Client, Server),
            Transfer(305, ObservationKind.Disconnect, AccountingSide.EndpointActivity, 0, 300, 16).Between(Server, Client),
            Lifecycle(310, ObservationKind.Exit, 100, 17, exitCode: 0),
            Transfer(320, ObservationKind.Send, AccountingSide.SendSide, 5, 300, 18).Between(Server, "10.0.0.7:5555")));
        SessionManifestV1 before = session.Store.Current!;
        Facts recorded = FactsOf(session.Store);
        TransportRelation whole = Assert.Single(Relations(session.Store).Relations);

        // The oldest chunk is the only one whose records all read before 15 µs. Of its ten rows, five are kept: the
        // client's creation, which its instance is keyed and named by, the creation and exit of the launcher it names as its
        // parent, and the connect and accept that open the connection the later records travel - the accept also the first
        // record of the server, which no lifecycle record names.
        IntervalReleasePreview preview = IntervalRelease.Preview(session.Store, 15_000);
        Assert.Equal(IntervalReleaseObstacle.None, preview.Obstacle);
        string oldest = before.Dependencies.Where(dependency => dependency.Kind == StoreDependencyKind.Journal)
            .Select(dependency => dependency.Name).Order(StringComparer.Ordinal).First();
        Assert.Equal((true, 3, 1), (preview.Chunks, preview.Units, preview.ReleasedUnits));
        Assert.Equal([oldest], preview.ReleasedChunks);
        Assert.Equal((18L, 10L, 18L, 5L, 5L, 0L), (preview.Records, preview.ReleasedRecords, preview.Rows, preview.ReleasedRows, preview.KeptRows, preview.RowsWithoutRecords));
        Assert.Equal((6_501L, 6_501L, 21_501L), (preview.BoundaryNanoseconds!.Value, preview.EarliestReleasingNanoseconds!.Value, preview.MostReleasingNanoseconds!.Value));

        // A reader holding the earlier generation keeps its files until it lets go (I18).
        IntervalReleaseResult result;
        using (EvidenceLease reader = session.Store.AcquireLease())
        {
            result = IntervalRelease.Release(session.Store, 15_000, "older than the retained window", Committed, Committed);
            Assert.True(result.Retention.AwaitingRelease);
            Assert.All(result.Retention.HeldByLease, name => Assert.True(File.Exists(Path.Combine(session.Path, name))));
        }

        SessionManifestV1 after = result.Retention.Manifest;
        RetentionRecord record = after.Retention!;
        Assert.Equal((RetentionExtentKind.Interval, 10L, "older than the retained window"), (record.Kind, record.ReleasedRecords, record.Reason));
        Assert.Equal(new ReleasedInterval(6_501, 5, 5), record.Interval);
        Assert.Equal(oldest, record.ReleasedFiles[0]);
        Assert.Equal(
            [oldest, .. preview.ReplacedFiles],
            record.ReleasedFiles);
        Assert.Equal(before.Boundary, after.Boundary);
        Assert.DoesNotContain(after.Dependencies, dependency => dependency.Name == oldest);
        Assert.NotEmpty(session.Store.RemoveOrphans());
        Assert.All(record.ReleasedFiles, name => Assert.False(File.Exists(Path.Combine(session.Path, name))));

        // Reopened, the retained records and the kept ones bind as the whole capture bound them; the process that only the
        // released interval held is gone, and the others are the instances they were.
        SessionStore reopened = SessionStore.OpenExisting(LocalOwnedDirectory.Open(session.Path));
        Assert.Equal(after.Digest, reopened.Current!.Digest);
        Facts retained = FactsOf(reopened);
        Assert.Equal([1UL, 2, 3, 7, 8, 11, 12, 13, 14, 15, 16, 17, 18], retained.Rows.Keys.Select(row => row.Ordinal).Order().ToArray());
        Assert.All(retained.Rows, row => Assert.Equal(recorded.Rows[row.Key], row.Value));
        Assert.DoesNotContain(retained.Instances.Values, instance => instance.ProcessId == 200);
        Assert.All(retained.Instances, instance => Assert.Equal(recorded.Instances[instance.Key], instance.Value));
        ProcessInstance client = retained.Instances.Values.Single(instance => instance.ProcessId == 100);
        ProcessInstance launcher = retained.Instances.Values.Single(instance => instance.ProcessId == 50);
        Assert.Equal(("client.exe", ProcessWitness.Created, launcher.Id), (client.ImageName, client.Witness, client.Parent));
        Assert.Equal(1, FieldRowsOf(reopened).Count(field => field.Field == SourceField.ParentProcessId));

        // A field goes with its record's row, even one delivered with a later chunk that gives up no row of its own.
        Assert.All(FieldRowsOf(reopened), field => Assert.True(retained.Rows.ContainsKey((field.RawStreamId, field.RawRecordOrdinal, field.FactKey))));

        // The connection keeps its key, both its lifecycle ends and its first reading; it holds the records retained.
        TransportRelation connection = Assert.Single(Relations(reopened).Relations);
        Assert.Equal((whole.StableKey, true, true, 50L, 8L), (connection.StableKey, connection.OpenWitnessed, connection.CloseWitnessed, connection.FirstNativeTicks, connection.Records));

        // The recording goes on: its next generation states the release, as every later one does.
        Publish(reopened, Timed(Transfer(400, ObservationKind.Send, AccountingSide.SendSide, 5, 300, 19).Between(Server, "10.0.0.7:5555")));
        GenerationRelease carried = reopened.Current!.LatestRelease(RetentionExtentKind.Interval)!;
        Assert.Equal((after.Generation, record.Interval, record.SourceDigest), (carried.Generation, carried.Record.Interval, carried.Record.SourceDigest));
    }

    [Fact(DisplayName = "S6: on random recordings, every row a release retains or keeps has the process, other end and channel the capture gave it")]
    public void RandomReleasesKeepEveryRetainedRecordsFacts()
    {
        int released = 0;
        for (int seed = 0; seed < 120; seed++)
        {
            var random = new Random(seed);
            List<(ObservationRowV1[] Rows, SourceFieldRowV1[] Fields)> chunks = Delivered(RandomCaptures.Chunks(random));
            using var session = new TemporarySession();
            foreach ((ObservationRowV1[] rows, SourceFieldRowV1[] fields) in chunks)
            {
                Publish(session.Store, rows, rowsPerSegment: random.Next(1, rows.Length + 2), fields: fields);
            }

            Facts recorded = FactsOf(session.Store);
            long[] times = [.. recorded.Rows.Values.Select(row => row.Time).OfType<long>().Order()];
            long boundary = times.Length == 0 ? 0 : times[random.Next(times.Length)] + random.Next(0, 2);
            IntervalReleasePreview preview = IntervalRelease.Preview(session.Store, boundary);
            if (!preview.ReleasesAnything)
            {
                Assert.Throws<InvalidOperationException>(() =>
                    IntervalRelease.Release(session.Store, boundary, "rolling window", Committed, Committed));
                continue;
            }

            released++;
            IntervalReleaseResult result = IntervalRelease.Release(session.Store, boundary, "rolling window", Committed, Committed);
            Assert.Equal(
                (preview.BoundaryNanoseconds, preview.ReleasedUnits, preview.ReleasedRows, preview.KeptRows),
                (result.Preview.BoundaryNanoseconds, result.Preview.ReleasedUnits, result.Preview.ReleasedRows, result.Preview.KeptRows));
            long cut = preview.BoundaryNanoseconds!.Value;
            Assert.InRange(cut, long.MinValue, boundary);

            SessionStore reopened = SessionStore.OpenExisting(LocalOwnedDirectory.Open(session.Path));
            Facts retained = FactsOf(reopened);
            Assert.Equal(recorded.Rows.Count - preview.ReleasedRows, retained.Rows.Count);
            Assert.All(retained.Rows, row => Assert.Equal(recorded.Rows[row.Key], row.Value));
            Assert.All(retained.Instances, instance => Assert.Equal(recorded.Instances[instance.Key], instance.Value));

            // Every record read at or after the boundary the release achieved is retained, and every row it gave up read
            // before it or at no session time.
            Assert.All(recorded.Rows.Where(row => row.Value.Time >= cut), row => Assert.True(retained.Rows.ContainsKey(row.Key)));
            Assert.All(recorded.Rows.Where(row => !retained.Rows.ContainsKey(row.Key)), row => Assert.True(row.Value.Time is null || row.Value.Time < cut));
            Assert.Equal(new ReleasedInterval(cut, preview.ReleasedRows, preview.KeptRows), reopened.Current!.Retention!.Interval);

            // A source field goes with its record's row: none is left of a row given up.
            Assert.All(FieldRowsOf(reopened), field => Assert.True(retained.Rows.ContainsKey((field.RawStreamId, field.RawRecordOrdinal, field.FactKey))));
            Assert.Equal(preview.ReleasedChunks, reopened.Current.Retention.ReleasedFiles.Take(preview.ReleasedChunks.Count));
        }

        Assert.InRange(released, 40, 120);
    }

    [Fact(DisplayName = "S6: a single journal gives up its first batches, rewritten from the first one it keeps, and a second release goes on from it")]
    public void ASingleJournalGivesUpItsFirstBatches()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] rows = Timed(
        [
            Lifecycle(10, ObservationKind.Create, 100, 1),
            .. Enumerable.Range(2, 15).Select(ordinal =>
                Transfer(10 * ordinal, ObservationKind.Send, AccountingSide.SendSide, 8, 100, (ulong)ordinal).Between(Client, "10.0.0.9:443")),
        ]);
        _ = Publish(session.Store, rows, journalBatchRecords: 4);
        string journal = Assert.Single(session.Store.Current!.Dependencies, dependency => dependency.Kind == StoreDependencyKind.Journal).Name;

        // Record n reads at n µs, in batches of four. A boundary at 10.05 µs releases the first two batches, whose last
        // records read at 4 and 8 µs; the creation is kept, so the process keeps its instance, and so is the first record
        // of the one-sided connection, which its key is anchored on.
        IntervalReleasePreview preview = IntervalRelease.Preview(session.Store, 10_050);
        Assert.Equal((false, "batch", 4, 2, 8L, 16L), (preview.Chunks, preview.ReleaseUnit, preview.Units, preview.ReleasedUnits, preview.ReleasedRecords, preview.Records));
        Assert.Equal((6L, 2L, 8_001L), (preview.ReleasedRows, preview.KeptRows, preview.BoundaryNanoseconds!.Value));
        Facts recorded = FactsOf(session.Store);

        IntervalReleaseResult result = IntervalRelease.Release(session.Store, 10_050, "older than the retained window", Committed, Committed);
        SessionManifestV1 after = result.Retention.Manifest;
        StoreDependency retainedJournal = Assert.Single(after.Dependencies, dependency => dependency.Kind == StoreDependencyKind.Journal);
        Assert.NotEqual(journal, retainedJournal.Name);
        Assert.Equal((retainedJournal.Name, 8L), (after.Boundary.JournalName, after.Boundary.CommittedRecords));
        Assert.Equal(journal, after.Retention!.ReleasedFiles[0]);
        Facts retained = FactsOf(SessionStore.OpenExisting(LocalOwnedDirectory.Open(session.Path)));
        Assert.Equal([1UL, 2, 9, 10, 11, 12, 13, 14, 15, 16], retained.Rows.Keys.Select(row => row.Ordinal).Order().ToArray());
        Assert.All(retained.Rows, row => Assert.Equal(recorded.Rows[row.Key], row.Value));

        // The rows kept of released records are the oldest a later release can take; it keeps them while they are still
        // needed, and carries no earlier interval release, whose boundary its own passes.
        IntervalReleasePreview second = IntervalRelease.Preview(session.Store, 12_050);
        Assert.Equal((1, 4L, 4L, 2L, 2L), (second.ReleasedUnits, second.ReleasedRecords, second.ReleasedRows, second.KeptRows, second.RowsWithoutRecords));
        SessionManifestV1 again = IntervalRelease.Release(session.Store, 12_050, "older than the retained window", Committed, Committed).Retention.Manifest;
        Assert.Equal((12_001L, 4L), (again.Retention!.Interval!.BoundaryNanoseconds, again.Boundary.CommittedRecords));
        Assert.Null(again.EarlierReleases);

        // Once only kept rows lie before a boundary, nothing is left to release before it.
        Assert.Equal(IntervalReleaseObstacle.AllKept, IntervalRelease.Preview(session.Store, 3_000).Obstacle);
    }

    [Fact(DisplayName = "I15: an interval release gives up nothing when its oldest unit reads at or after the boundary, and never the newest unit")]
    public void AnIntervalReleaseRefusesWhatItCannotGiveUp()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Timed(Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1).Between(Client, Server)));
        Assert.Equal(IntervalReleaseObstacle.NoReleasableUnit, IntervalRelease.Preview(session.Store, 1_000_000).Obstacle);

        Publish(session.Store, Timed(Transfer(200, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 2).Between(Client, Server)));
        IntervalReleasePreview early = IntervalRelease.Preview(session.Store, 10_000);
        Assert.Equal((IntervalReleaseObstacle.NothingBefore, 10_001L, 10_001L), (early.Obstacle, early.EarliestReleasingNanoseconds!.Value, early.MostReleasingNanoseconds!.Value));
        long generation = session.Store.Current!.Generation;
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() =>
            IntervalRelease.Release(session.Store, 10_000, "older than the retained window", Committed, Committed));
        Assert.EndsWith(
            "The earliest boundary that releases anything is " + (10_001 / 1_000_000_000m).ToString("0.000######", CultureInfo.CurrentCulture)
                + " s. Nothing was published.",
            refused.Message,
            StringComparison.Ordinal);
        Assert.Equal(generation, session.Store.Current!.Generation);

        // However late the boundary, the newest chunk is kept.
        IntervalReleasePreview late = IntervalRelease.Preview(session.Store, long.MaxValue);
        Assert.Equal((1, 1L), (late.ReleasedUnits, late.ReleasedRecords));

        // Content kept beside a single journal is released on its own first: the journal's first batches cannot be rewritten
        // from under it.
        using var content = new TemporarySession();
        ObservationRowV1[] rows = Timed(
            Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1).Between(Client, Server),
            Transfer(200, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 2).Between(Client, Server));
        _ = Publish(content.Store, rows, journalBatchRecords: 1,
            content: (ContentHeader(), [Content(rows[0], Encoding.UTF8.GetBytes("hello"))]));
        Assert.Equal(IntervalReleaseObstacle.ContentBesideJournal, IntervalRelease.Preview(content.Store, 15_000).Obstacle);

        // Rows are told to a unit by their records' numbers, which a journal stored out of numbering order cannot answer.
        using var unordered = new TemporarySession();
        Publish(unordered.Store, Timed(Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 10).Between(Client, Server)));
        Publish(unordered.Store, Timed(Transfer(200, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 5).Between(Client, Server)));
        Assert.Contains("not stored in the order they were numbered", Assert.Throws<InvalidDataException>(() =>
            IntervalRelease.Preview(unordered.Store, 15_000)).Message, StringComparison.Ordinal);
    }

    /// <summary>A capture's chunks numbered as a recorder numbers them, in the order they were delivered, each chunk's after the one before.</summary>
    private static List<(ObservationRowV1[] Rows, SourceFieldRowV1[] Fields)> Delivered(List<(ObservationRowV1[] Rows, SourceFieldRowV1[] Fields)> chunks)
    {
        var numbers = new Dictionary<ulong, ulong>();
        ulong next = 0;
        foreach ((ObservationRowV1[] rows, SourceFieldRowV1[] _) in chunks)
        {
            foreach (ObservationRowV1 row in rows)
            {
                numbers[row.RawRecordOrdinal] = ++next;
            }
        }

        return
        [
            .. chunks.Select(chunk => (
                (ObservationRowV1[])[.. chunk.Rows.Select(row => row with { RawRecordOrdinal = numbers[row.RawRecordOrdinal] })],
                (SourceFieldRowV1[])[.. chunk.Fields.Select(field => field with { RawRecordOrdinal = numbers[field.RawRecordOrdinal] })])),
        ];
    }

    private static SourceFieldRowV1[] FieldRowsOf(SessionStore store) =>
    [
        .. SessionSegments.FieldNames(store.Current!)
            .Select(name => SessionSegments.Open(store.Root, store.Current!, name))
            .SelectMany(segment => Enumerable.Range(0, segment.RowCount).Select(segment.FieldRow)),
    ];

    private static ObservationRowV1[] Timed(params ObservationRowV1[] rows) =>
        [.. rows.Select(row => row with { SessionRelativeTicks = row.NativeTicks * 100 })];

    private static TransportRelationIndex Relations(SessionStore store)
    {
        SessionManifestV1 generation = store.Current!;
        SegmentReaderV1[] segments = [.. SessionSegments.Names(generation).Select(name => SessionSegments.Open(store.Root, generation, name))];
        SegmentReaderV1[] fields = [.. SessionSegments.FieldNames(generation).Select(name => SessionSegments.Open(store.Root, generation, name))];
        return TransportRelationIndex.Derive(segments, ProcessInstanceIndex.Derive(segments, TestClock, fields));
    }

    /// <summary>
    /// What a generation's derivations say of each row - the process it binds to and how, its other end and its channel's
    /// key - and every instance a row binds to, each by its identity rather than by a position in one derivation.
    /// </summary>
    private static Facts FactsOf(SessionStore store)
    {
        SessionManifestV1 manifest = store.Current!;
        SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store.Root, manifest, name))];
        SegmentReaderV1[] fields = [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store.Root, manifest, name))];
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(segments, TestClock, fields);
        TransportRelationIndex relations = TransportRelationIndex.Derive(segments, processes);
        var keys = new Dictionary<int, string>();
        foreach (TransportRelation relation in relations.Relations)
        {
            keys[relation.Channel] = relation.StableKey;
        }

        foreach (TransportConnection connection in relations.OneSided)
        {
            keys[connection.Channel] = connection.StableKey;
        }

        var rows = new Dictionary<(uint Stream, ulong Ordinal, FactKey Fact), RowFacts>();
        var instances = new Dictionary<ProcessInstanceId, ProcessInstance>();
        foreach (SegmentReaderV1 segment in segments)
        {
            ProcessBinding[] owners = processes.OwnersOf(segment);
            (ProcessBinding[] peers, ChannelBinding[] channels) = relations.BindingsOf(segment);
            for (int index = 0; index < segment.RowCount; index++)
            {
                ObservationRowV1 row = segment.Row(index);
                rows.Add((row.RawStreamId, row.RawRecordOrdinal, row.FactKey), new(
                    Name(owners[index]),
                    Name(peers[index]),
                    channels[index].IsKnown ? keys.GetValueOrDefault(channels[index].Channel, "a channel no relation or connection names") : "none: " + channels[index].Reason,
                    row.SessionRelativeTicks));
            }
        }

        return new(rows, instances);

        string Name(ProcessBinding binding)
        {
            if (!binding.IsBound)
            {
                return $"unresolved: {binding.Reason}";
            }

            ProcessInstance instance = processes.Instances[binding.Instance];
            instances[instance.Id] = instance;
            return $"{instance.Id} {binding.Strength}";
        }
    }

    private sealed record Facts(
        Dictionary<(uint Stream, ulong Ordinal, FactKey Fact), RowFacts> Rows,
        Dictionary<ProcessInstanceId, ProcessInstance> Instances);

    private sealed record RowFacts(string Owner, string Peer, string Channel, long? Time);
}
