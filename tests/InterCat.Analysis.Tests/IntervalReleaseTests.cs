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
            Transfer(320, ObservationKind.Send, AccountingSide.SendSide, 5, 300, 18).Between(Server, "10.0.0.7:5555")),
            coverage: TransportLedger(tcp: true, udp: false));
        SessionManifestV1 before = session.Store.Current!;
        Assert.Null(SessionSegments.CoverageLedger(session.Store.Root, before)!.ReleasedBefore);
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

        // Its coverage ledger is read with the boundary, so no scope reaching before it reads as covered.
        Assert.Equal(new LedgerRelease(SourceClockMath.FirstNativeAtOrAfter(TestClock, new SessionTimestamp(6_501)), 6_501),
            SessionSegments.CoverageLedger(reopened.Root, reopened.Current!)!.ReleasedBefore);

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

    [Fact(DisplayName = "I20: a call and an exchange open across the boundary keep their records, and a client call what its other end was read from")]
    public void OperationsOpenAcrossTheBoundaryStayWhole()
    {
        using var session = new TemporarySession();
        Guid service = Guid.Parse("367abb81-9844-35f1-ad32-98f038001003");
        ObservationRowV1 clientStart = RpcCall(100, ObservationKind.RequestStart, Direction.Outbound, 400, 3, Activity(1), service);
        ObservationRowV1 send = Alpc(101, ObservationKind.Send, 400, 401, 4);
        ObservationRowV1 receive = Alpc(102, ObservationKind.Receive, 1_960, 1_961, 5);
        ObservationRowV1 serverStart = RpcCall(103, ObservationKind.RequestStart, Direction.Inbound, 1_960, 6, Activity(2), service);
        ObservationRowV1 head = Http(104, 2001, 7);
        Publish(session.Store, Timed(
            Lifecycle(1, ObservationKind.Create, 400, 1),
            Lifecycle(2, ObservationKind.Create, 1_960, 2),
            clientStart, send, receive, serverStart, head,
            RpcCall(105, ObservationKind.RequestStart, Direction.Outbound, 400, 8, Activity(3), service),
            RpcCall(106, ObservationKind.RequestEnd, Direction.Outbound, 400, 9, Activity(3), status: 0)),
            fields:
            [
                Field(clientStart, SourceField.RpcProcedureNumber, 7),
                Field(serverStart, SourceField.RpcProcedureNumber, 7),
                Field(send, SourceField.AlpcMessageId, 21),
                Field(receive, SourceField.AlpcMessageId, 21),
                .. HttpFields(head, 1, 0, 3),
            ]);
        ObservationRowV1 response = Http(201, 2003, 12);
        Publish(session.Store, Timed(
            RpcCall(200, ObservationKind.RequestEnd, Direction.Inbound, 1_960, 10, Activity(2), status: 0),
            RpcCall(202, ObservationKind.RequestEnd, Direction.Outbound, 400, 11, Activity(1), status: 0),
            response),
            fields: [.. HttpFields(response, 1, 0, 3)]);
        Publish(session.Store, Timed(RpcCall(300, ObservationKind.RequestStart, Direction.Outbound, 400, 13, Activity(4), service)));
        Operations recorded = OperationsOf(session.Store);

        // The first chunk is released but for the call, the server call and the exchange its later records belong to, and
        // the send and receive that link the two calls. The call whose records all read in it goes.
        IntervalReleasePreview preview = IntervalRelease.Preview(session.Store, 15_000);
        Assert.Equal((1, 9L, 2L, 7L), (preview.ReleasedUnits, preview.ReleasedRecords, preview.ReleasedRows, preview.KeptRows));
        _ = IntervalRelease.Release(session.Store, 15_000, "older than the retained window", Committed, Committed);
        SessionStore reopened = SessionStore.OpenExisting(LocalOwnedDirectory.Open(session.Path));
        Assert.Equal([1UL, 2, 3, 4, 5, 6, 7, 10, 11, 12, 13], FactsOf(reopened).Rows.Keys.Select(row => row.Ordinal).Order().ToArray());
        Operations retained = OperationsOf(reopened);
        Assert.Equal([3UL, 6, 7, 10, 11, 12, 13], retained.Rows.Keys.Select(row => row.Ordinal).Order().ToArray());
        Assert.All(retained.Rows, row => Assert.Equal(recorded.Rows[row.Key], row.Value));
        Assert.Contains(retained.Rows.Values, fact => fact.Contains(" Completed ", StringComparison.Ordinal) && fact.Contains("peer Served", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "I20: on random recordings, every call and exchange a release retains a record of pairs, times and links as before")]
    public void RandomReleasesKeepEveryRetainedOperationsFacts()
    {
        int released = 0;
        for (int seed = 0; seed < 80; seed++)
        {
            var random = new Random(seed);
            List<(ObservationRowV1[] Rows, SourceFieldRowV1[] Fields)> chunks = Delivered(RandomOperations(random));
            using var session = new TemporarySession();
            foreach ((ObservationRowV1[] rows, SourceFieldRowV1[] fields) in chunks)
            {
                Publish(session.Store, rows, rowsPerSegment: random.Next(1, rows.Length + 2), fields: fields);
            }

            Operations recorded = OperationsOf(session.Store);
            long[] times = [.. chunks.SelectMany(chunk => chunk.Rows).Select(row => row.SessionRelativeTicks).OfType<long>().Order()];
            long boundary = times[random.Next(times.Length)] + random.Next(0, 2);
            IntervalReleasePreview preview = IntervalRelease.Preview(session.Store, boundary);
            if (!preview.ReleasesAnything)
            {
                continue;
            }

            released++;
            _ = IntervalRelease.Release(session.Store, boundary, "rolling window", Committed, Committed);
            Operations retained = OperationsOf(SessionStore.OpenExisting(LocalOwnedDirectory.Open(session.Path)));
            Assert.All(retained.Rows, row => Assert.Equal(recorded.Rows[row.Key], row.Value));
        }

        Assert.InRange(released, 30, 80);
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
            "The earliest boundary that can release anything is " + (10_001 / 1_000_000_000m).ToString("0.000######", CultureInfo.CurrentCulture)
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

    [Fact(DisplayName = "I18: a release asked for past a pin plans to it, giving up only what was read before, and one the pin stops says so")]
    public void AReleaseStopsAtAPin()
    {
        // Three chunks: sends at 10 and 20 µs, at 30 µs, and at 40 µs, the newest, which a release keeps.
        using var session = new TemporarySession();
        Publish(session.Store, Timed(
            Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1).Between(Client, Server),
            Transfer(200, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 2).Between(Client, Server)));
        Publish(session.Store, Timed(Transfer(300, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 3).Between(Client, Server)));
        Publish(session.Store, Timed(Transfer(400, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 4).Between(Client, Server)));
        IntervalReleasePreview unpinned = IntervalRelease.Preview(session.Store, 35_000);
        Assert.Equal((2, 30_001L, (RetentionPin?)null), (unpinned.ReleasedUnits, unpinned.BoundaryNanoseconds!.Value, unpinned.HeldBy));

        // A pin from 25 µs keeps the second chunk: asked for the records before 35 µs, a release takes only the first, and
        // is held by the earliest pin before 35 µs, whichever was placed first.
        RetentionPin later = session.Store.Pin(27_000, long.MaxValue, "after it", Committed);
        RetentionPin pin = session.Store.Pin(25_000, long.MaxValue, "the second chunk", Committed);
        IntervalReleasePreview held = IntervalRelease.Preview(session.Store, 35_000);
        Assert.Equal((pin, 1, 20_001L, IntervalReleaseObstacle.None, 35_000L),
            (held.HeldBy, held.ReleasedUnits, held.BoundaryNanoseconds!.Value, held.Obstacle, held.RequestedNanoseconds));

        // A pin from the boundary asked for, or after it, holds nothing back.
        Assert.Null(IntervalRelease.Preview(session.Store, 25_000).HeldBy);
        IntervalReleaseResult released = IntervalRelease.Release(session.Store, 35_000, "older than the retained window", Committed, Committed);
        Assert.Equal((20_001L, pin), (released.Preview.BoundaryNanoseconds!.Value, released.Preview.HeldBy));
        Assert.Equal(20_001L, session.Store.Current!.LatestRelease(RetentionExtentKind.Interval)!.Record.Interval!.BoundaryNanoseconds);

        // Asked again, nothing read before the pin is left to release but the evidence the first release kept: the pin, not
        // the chunk, stops it, and says so.
        IntervalReleasePreview stopped = IntervalRelease.Preview(session.Store, 35_000);
        Assert.Equal((IntervalReleaseObstacle.Pinned, pin, false), (stopped.Obstacle, stopped.HeldBy, stopped.ReleasesAnything));
        string seconds = SessionTimeText.Seconds(25_000, CultureInfo.CurrentCulture);
        Assert.Equal($"A pin keeps every record read from {seconds} (the second chunk), and nothing read before it can be released, "
            + "so nothing is until the pin is removed.", IntervalRelease.Refusal(stopped));
        long generation = session.Store.Current.Generation;
        Assert.EndsWith("until the pin is removed. Nothing was published.", Assert.Throws<InvalidOperationException>(() =>
            IntervalRelease.Release(session.Store, 35_000, "again", Committed, Committed)).Message, StringComparison.Ordinal);
        Assert.Equal(generation, session.Store.Current!.Generation);

        // Where the chunk itself stops a release, the chunk is said: the pin did not stop what would not have gone anyway.
        IntervalReleasePreview reaching = IntervalRelease.Preview(session.Store, 30_000);
        Assert.Equal((IntervalReleaseObstacle.AllKept, pin), (reaching.Obstacle, reaching.HeldBy));

        // Removed, the pins keep nothing: the release goes on past their moments.
        Assert.Equal(pin, session.Store.Unpin(pin.Id));
        Assert.Equal(later, IntervalRelease.Preview(session.Store, 35_000).HeldBy);
        Assert.Equal(later, session.Store.Unpin(later.Id));
        IntervalReleasePreview free = IntervalRelease.Preview(session.Store, 35_000);
        Assert.Equal((1, 30_001L, (RetentionPin?)null), (free.ReleasedUnits, free.BoundaryNanoseconds!.Value, free.HeldBy));
    }

    /// <summary>
    /// A random capture of RPC calls and HTTP exchanges, cut into chunks with some delivered late: calls of two clients,
    /// many linked through ALPC to a call a service host's thread served - now and then with a second send, a lost
    /// receive, another procedure or a second client reaching the same call - activity ids now and then reused before their
    /// stop or missing, starts and stops lost, and a process's HTTP exchanges, their numbers now and then used again.
    /// </summary>
    private static List<(ObservationRowV1[] Rows, SourceFieldRowV1[] Fields)> RandomOperations(Random random)
    {
        Guid service = Guid.Parse("367abb81-9844-35f1-ad32-98f038001003");
        var rows = new List<ObservationRowV1>
        {
            Lifecycle(1, ObservationKind.Create, 400, 1),
            Lifecycle(2, ObservationKind.Create, 1_960, 2),
            Lifecycle(3, ObservationKind.Create, 4_242, 3),
        };
        var fields = new List<SourceFieldRowV1>();
        ulong ordinal = 3;
        long ticks = 10;
        int activity = 0;
        long message = 0;
        long exchange = 0;
        int count = random.Next(10, 40);
        for (int index = 0; index < count; index++)
        {
            ticks += random.Next(1, 30);
            int shape = random.Next(5);
            if (shape <= 1)
            {
                // A client call linked to the call that served it, its records spread over the next few readings.
                int client = random.Next(2) == 0 ? 400 : 401;
                Guid clientActivity = Activity(++activity);
                Guid serverActivity = Activity(++activity);
                long id = ++message;
                long start = ticks;
                ObservationRowV1 clientStart = RpcCall(start, ObservationKind.RequestStart, Direction.Outbound, client, ++ordinal, clientActivity, service);
                fields.Add(Field(clientStart, SourceField.RpcProcedureNumber, 7));
                rows.Add(clientStart);
                ObservationRowV1 sent = Alpc(start + 1, ObservationKind.Send, client, client + 1, ++ordinal);
                fields.Add(Field(sent, SourceField.AlpcMessageId, id));
                rows.Add(sent);
                if (random.Next(6) == 0)
                {
                    ObservationRowV1 second = Alpc(start + 2, ObservationKind.Send, client, client + 1, ++ordinal);
                    fields.Add(Field(second, SourceField.AlpcMessageId, ++message));
                    rows.Add(second);
                }

                if (random.Next(6) != 0)
                {
                    ObservationRowV1 received = Alpc(start + 3, ObservationKind.Receive, 1_960, 1_961, ++ordinal);
                    fields.Add(Field(received, SourceField.AlpcMessageId, id));
                    rows.Add(received);
                }

                if (random.Next(5) == 0)
                {
                    // The other client's call, whose message reaches the same thread first: the server call is neither's.
                    int other = client == 400 ? 401 : 400;
                    Guid otherActivity = Activity(++activity);
                    long otherId = ++message;
                    ObservationRowV1 otherStart = RpcCall(start - 2, ObservationKind.RequestStart, Direction.Outbound, other, ++ordinal, otherActivity, service);
                    fields.Add(Field(otherStart, SourceField.RpcProcedureNumber, 7));
                    ObservationRowV1 otherSent = Alpc(start - 1, ObservationKind.Send, other, other + 1, ++ordinal);
                    fields.Add(Field(otherSent, SourceField.AlpcMessageId, otherId));
                    ObservationRowV1 otherReceived = Alpc(start + 2, ObservationKind.Receive, 1_960, 1_961, ++ordinal);
                    fields.Add(Field(otherReceived, SourceField.AlpcMessageId, otherId));
                    rows.AddRange([
                        otherStart, otherSent, otherReceived,
                        RpcCall(start + 70 + random.Next(30), ObservationKind.RequestEnd, Direction.Outbound, other, ++ordinal, otherActivity, status: 0),
                    ]);
                }

                ObservationRowV1 serverStart = RpcCall(start + 4, ObservationKind.RequestStart, Direction.Inbound, 1_960, ++ordinal, serverActivity, service);
                fields.Add(Field(serverStart, SourceField.RpcProcedureNumber, random.Next(8) == 0 ? 9 : 7));
                rows.Add(serverStart);
                rows.Add(RpcCall(start + 5 + random.Next(40), ObservationKind.RequestEnd, Direction.Inbound, 1_960, ++ordinal, serverActivity, status: 0));
                rows.Add(RpcCall(start + 10 + random.Next(60), ObservationKind.RequestEnd, Direction.Outbound, client, ++ordinal, clientActivity, status: random.Next(4) == 0 ? 5 : 0));
            }
            else if (shape == 2)
            {
                // A call of no other end: paired, its id now and then reused before its stop, missing, or a record lost.
                int process = random.Next(3) switch { 0 => 400, 1 => 401, _ => 1_960 };
                Direction direction = process == 1_960 ? Direction.Inbound : Direction.Outbound;
                Guid? id = random.Next(8) == 0 ? null : random.Next(5) == 0 && activity > 0 ? Activity(activity) : Activity(++activity);
                if (random.Next(6) != 0)
                {
                    ObservationRowV1 start = RpcCall(ticks, ObservationKind.RequestStart, direction, process, ++ordinal, id, service);
                    fields.Add(Field(start, SourceField.RpcProcedureNumber, 3));
                    rows.Add(start);
                }

                if (random.Next(6) != 0)
                {
                    rows.Add(RpcCall(ticks + 1 + random.Next(80), ObservationKind.RequestEnd, direction, process, ++ordinal, id, status: 0));
                }
            }
            else
            {
                // An exchange of process 4242: its request head, and its response's head and body, now and then a number again.
                long number = random.Next(5) == 0 && exchange > 0 ? exchange : ++exchange;
                (ushort Event, long Flags)[] parts = [(2001, 3), (2003, 3), (2004, 1), (2004, 2)];
                long at = ticks;
                foreach ((ushort part, long flags) in parts.Take(random.Next(2, 5)))
                {
                    at += random.Next(0, 25);
                    ObservationRowV1 buffer = Http(at, part, ++ordinal);
                    fields.AddRange(HttpFields(buffer, number, part == 2004 && flags == 2 ? 1 : 0, flags));
                    rows.Add(buffer);
                }
            }
        }

        int chunkCount = random.Next(2, 6);
        long last = rows.Max(row => row.NativeTicks) + 1;
        var chunks = new List<(List<ObservationRowV1> Rows, List<SourceFieldRowV1> Fields)>();
        for (int chunk = 0; chunk < chunkCount; chunk++)
        {
            chunks.Add(([], []));
        }

        var placed = new Dictionary<ulong, int>();
        foreach (ObservationRowV1 row in rows.OrderBy(row => row.NativeTicks).ThenBy(row => row.RawRecordOrdinal))
        {
            int chunk = (int)(row.NativeTicks * chunkCount / last);
            chunk = random.Next(8) == 0 ? Math.Min(chunkCount - 1, chunk + 1) : chunk;
            placed[row.RawRecordOrdinal] = chunk;
            chunks[chunk].Rows.Add(row with { SessionRelativeTicks = row.NativeTicks * 100 });
        }

        foreach (SourceFieldRowV1 field in fields)
        {
            chunks[placed[field.RawRecordOrdinal]].Fields.Add(field);
        }

        return [.. chunks.Select(chunk => (chunk.Rows.ToArray(), chunk.Fields.ToArray()))];
    }

    /// <summary>
    /// What a generation's operations say of each record of one - its call's identity, state, duration, status, interface,
    /// procedure, process and other end, or its exchange's identity, parts, completeness, duration and process.
    /// </summary>
    private static Operations OperationsOf(SessionStore store)
    {
        SessionManifestV1 manifest = store.Current!;
        SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store.Root, manifest, name))];
        SegmentReaderV1[] fields = [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store.Root, manifest, name))];
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(segments, TestClock, fields);
        RpcCallIndex calls = RpcCallIndex.Derive(segments, fields, processes, TestClock);
        RpcPeerIndex peers = RpcPeerIndex.Derive(calls, segments, fields);
        HttpExchangeIndex exchanges = HttpExchangeIndex.Derive(segments, fields, processes);
        var rows = new Dictionary<(uint Stream, ulong Ordinal, FactKey Fact), string>();
        foreach (RpcCallGroup group in calls.Groups)
        {
            IReadOnlyList<RpcCall> listed = calls.CallsOf(group, segments, 0, int.MaxValue);
            for (int position = 0; position < listed.Count; position++)
            {
                RpcCall call = listed[position];
                (RpcPeerState state, RpcCall? other) = peers.PeerOf(group, position, segments);
                string fact = string.Create(CultureInfo.InvariantCulture,
                    $"{Name(call.Identity)} {call.State} {call.DurationNanoseconds} {call.Status} {call.Interface} {call.Procedure} {Bound(call.Process)} peer {state} {(other is null ? "-" : Name(other.Identity))}");
                foreach (RpcCallRecord record in new[] { call.Start, call.Stop }.OfType<RpcCallRecord>())
                {
                    rows.Add((record.Observation.RawRecordId.StreamId, record.Observation.RawRecordId.RecordOrdinal, record.Observation.FactKey), fact);
                }
            }
        }

        foreach (HttpExchangeGroup group in exchanges.Groups)
        {
            IReadOnlyList<HttpExchange> listed = exchanges.ExchangesOf(group);
            for (int position = 0; position < listed.Count; position++)
            {
                HttpExchange exchange = listed[position];
                string fact = string.Create(CultureInfo.InvariantCulture,
                    $"exchange {exchange.First} {exchange.Number} {exchange.RequestHead} {exchange.RequestBody} {exchange.ResponseHead} {exchange.ResponseBody} {exchange.Complete} {exchange.DurationNanoseconds} {Bound(exchange.Process)}");
                foreach ((int segment, int row) in exchanges.RecordsOf(group, position))
                {
                    ObservationRowV1 read = segments[segment].Row(row);
                    rows.Add((read.RawStreamId, read.RawRecordOrdinal, read.FactKey), fact);
                }
            }
        }

        return new(rows);

        string Bound(ProcessBinding binding) =>
            binding.IsBound ? $"{processes.Instances[binding.Instance].Id} {binding.Strength}" : $"unresolved {binding.Reason}";

        static string Name(ObservationId identity) => string.Create(CultureInfo.InvariantCulture,
            $"{identity.RawRecordId.StreamId}.{identity.RawRecordId.RecordOrdinal}.{identity.FactKey}");
    }

    private sealed record Operations(Dictionary<(uint Stream, ulong Ordinal, FactKey Fact), string> Rows);

    private static Guid Activity(int number) => new(number, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 1);

    /// <summary>A WinINet capture record of <paramref name="eventId"/>, raised by process 4242.</summary>
    private static ObservationRowV1 Http(long ticks, ushort eventId, ulong ordinal) =>
        Transfer(ticks, eventId <= 2002 ? ObservationKind.Send : ObservationKind.Receive,
            eventId <= 2002 ? AccountingSide.SendSide : AccountingSide.ReceiveSide, 64, null, ordinal) with
        {
            Mechanism = Mechanism.Http,
            Layer = ObservationLayer.Application,
            EventId = eventId,
            HeaderProcessId = 4_242,
            Direction = eventId <= 2002 ? Direction.Outbound : Direction.Inbound,
            ByteDomain = ByteDomain.ApplicationPayload,
        };

    private static SourceFieldRowV1[] HttpFields(ObservationRowV1 buffer, long number, long sequence, long flags) =>
    [
        Field(buffer, SourceField.HttpExchangeId, number),
        Field(buffer, SourceField.ContentBufferSequence, sequence),
        Field(buffer, SourceField.ContentBufferFlags, flags),
    ];

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
