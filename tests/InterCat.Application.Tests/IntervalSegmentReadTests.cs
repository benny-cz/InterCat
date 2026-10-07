using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// §12.1 S3's reads of one interval: a brush's counts, its byte rows, a focus's timeline and a time scope's byte and peer
/// rankings open only the segments the interval meets, with the instances and channels the checkpoint or the derivation
/// already holds, and answer as a read of every segment does (P25). They clear the shared derivation cache, so they run
/// alone.
/// </summary>
[Collection(SharedDerivationCache.Name)]
public sealed class IntervalSegmentReadTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [Fact(DisplayName = "§12.1: a brush's counts, an interval's bytes, a focus and a time scope's byte and peer rankings open only the segments the interval meets, and answer as one segment of the same records does")]
    public void AnIntervalOpensOnlyTheSegmentsItMeets()
    {
        // Six hundred records ten ticks apart in six segments of a hundred, so segment k holds ticks 1,000k to 1,000k + 990;
        // its twin holds the same records in one segment, which every query reads.
        ObservationRowV1[] rows = Rows();
        using var split = new TemporarySession();
        using var twin = new TemporarySession();
        Publish(split.Store, rows, rowsPerSegment: 100);
        Publish(twin.Store, rows);
        Assert.Equal(6, SessionSegments.Names(split.Store.Current!).Count);
        Assert.Single(SessionSegments.Names(twin.Store.Current!));
        WorkspaceSnapshot whole = OverviewWorkspace.From(SessionOverviewProjector.Project(twin.Store));
        ProcessInstanceId client = whole.Processes.Single(node => node.ProcessId == 100).Id;
        ProcessInstanceId server = whole.Processes.Single(node => node.ProcessId == 200).Id;
        string channel = whole.Channels.Single().Key;

        // A session no checkpoint names derives its instances and channels from every segment once; a brush after that,
        // from a reader that has opened none, opens only what it meets.
        var brushed = new TimeRange(2_050, 2_950);
        SessionDerivationCache.Clear();
        SessionStore first = SessionStore.OpenExisting(LocalOwnedDirectory.Open(split.Path));
        Assert.Equal(Answers(twin.Store, brushed, client, server, channel), Answers(first, brushed, client, server, channel));
        Assert.Equal(6, first.SegmentReaderCache.Entries);
        SessionStore later = SessionStore.OpenExisting(LocalOwnedDirectory.Open(split.Path));
        Assert.Equal(Answers(twin.Store, brushed, client, server, channel), Answers(later, brushed, client, server, channel));
        Assert.Equal(1, later.SegmentReaderCache.Entries);
        first.ReleaseSegmentReaders();
        later.ReleaseSegmentReaders();

        // A finished session's checkpoint holds them, and a reopened viewer's every read of an interval opens only the
        // segments it meets.
        Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(split.Store, Committed).Outcome);
        foreach ((TimeRange brush, int opened) in new[]
        {
            (brushed, 1),
            (new TimeRange(1_950, 2_050), 2),

            // An interval ending where a segment's first record is leaves it shut; one starting at its last opens it.
            (new TimeRange(2_050, 3_000), 1),
            (new TimeRange(2_990, 3_010), 2),
            (new TimeRange(6_000, 9_000), 0),
            (new TimeRange(-500, 10_000), 6),
        })
        {
            // Whichever a reader asks for first, the channels or the instances, both come from the checkpoint.
            foreach (bool channelsFirst in new[] { true, false })
            {
                SessionDerivationCache.Clear();
                SessionStore reopened = SessionStore.OpenExisting(LocalOwnedDirectory.Open(split.Path));
                string answered = Answers(reopened, brush, client, server, channel, channelsFirst);
                Assert.Equal(opened, reopened.SegmentReaderCache.Entries);
                Assert.Equal(Answers(twin.Store, brush, client, server, channel), answered);
                reopened.ReleaseSegmentReaders();
            }
        }

        // The answers are the records': the brush holds 45 of the client's sends and 45 of the server's receives, 100 B each.
        SessionByteMeasures ranked = SessionByteRanking.Measure(twin.Store, brushed);
        Assert.Equal(new TransportBytes(4_500, 45, 0, 0, 0, 0), ranked.ByProcess[client]);
        Assert.Equal(new TransportBytes(0, 0, 0, 4_500, 45, 0), ranked.ByProcess[server]);
        SessionIntervalCounts counted = SessionIntervalQuery.Count(twin.Store, brushed);
        Assert.Equal((90, 90), (counted.ObservedRows, counted.ChannelRecords[channel]));
    }

    [Fact(DisplayName = "§12.1: a brush on a capture of RPC calls pairs them over every segment once, and a later brush opens only the segments it meets")]
    public void ABrushPairsRpcCallsOnce()
    {
        // The service control manager's linked calls in three segments of six records, cut as they arrived: the first spans
        // ticks 1 to 210, the second 105 to 340 and the third 102 to 330. Its twin holds them in one.
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = LinkedRpcCalls();
        using var split = new TemporarySession();
        using var twin = new TemporarySession();
        Publish(split.Store, rows, rowsPerSegment: 6, fields: fields, coverage: RpcLedger(alpc: true));
        Publish(twin.Store, rows, fields: fields, coverage: RpcLedger(alpc: true));
        Assert.Equal((3, 2), (SessionSegments.Names(split.Store.Current!).Count, SessionSegments.FieldNames(split.Store.Current!).Count));
        Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(split.Store, Committed).Outcome);
        string edge = Assert.Single(SessionOverviewProjector.Project(twin.Store).Edges, edge => edge.Mechanism == Mechanism.Rpc).Key;

        // A call's request and response can lie in different segments, so the first brush pairs the calls over all of
        // them and their source fields, even with the checkpoint's instances; a later one reads the pairs it made, and
        // only the segments it meets: the third call's, from tick 300, lie in the second and the third.
        var early = new TimeRange(0, 150);
        var late = new TimeRange(250, 400);
        SessionDerivationCache.Clear();
        SessionStore first = SessionStore.OpenExisting(LocalOwnedDirectory.Open(split.Path));
        Assert.Equal(4, SessionIntervalQuery.Count(first, early).EdgeRecords[edge]);
        Assert.Equal(5, first.SegmentReaderCache.Entries);
        SessionStore later = SessionStore.OpenExisting(LocalOwnedDirectory.Open(split.Path));
        SessionIntervalCounts counted = SessionIntervalQuery.Count(later, late);
        Assert.Equal(2, later.SegmentReaderCache.Entries);
        Assert.Equal(4, counted.EdgeRecords[edge]);
        Assert.Equal(Text(SessionIntervalQuery.Count(twin.Store, late).EdgeRecords), Text(counted.EdgeRecords));
        first.ReleaseSegmentReaders();
        later.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "§12.1: an evidence page opens only the segments it reads, none before its cursor or outside its time scope, reads no row past its scope, and pages as one segment of the same records does")]
    public void AnEvidencePageOpensOnlyTheSegmentsItReads()
    {
        ObservationRowV1[] rows = Rows();
        using var split = new TemporarySession();
        using var twin = new TemporarySession();
        Publish(split.Store, rows, rowsPerSegment: 100);
        Publish(twin.Store, rows);
        WorkspaceSnapshot whole = OverviewWorkspace.From(SessionOverviewProjector.Project(twin.Store));
        ProcessInstanceId client = whole.Processes.Single(node => node.ProcessId == 100).Id;
        string channel = whole.Channels.Single().Key;
        _ = SessionOverviewProjector.Project(split.Store);
        Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(split.Store, Committed).Outcome);

        // A first page of 150 records, each owner resolved from the checkpoint, opens the two segments holding them and
        // the 151st that says there is more; the next page opens neither the first segment, which ends before its
        // cursor, nor any after the one holding its 151st.
        SessionStore reopened = Reopened(split.Path);
        SessionEvidencePage first = SessionEvidenceQuery.Read(reopened, pageSize: 150, resolveOwners: true);
        Assert.Equal((2, 151L), (reopened.SegmentReaderCache.Entries, first.RowsRead));
        Assert.Equal(Records(SessionEvidenceQuery.Read(twin.Store, pageSize: 150, resolveOwners: true)), Records(first));
        reopened = Reopened(split.Path);
        SessionEvidencePage next = SessionEvidenceQuery.Read(reopened, pageSize: 150, cursor: first.NextCursor, resolveOwners: true);
        Assert.Equal(3, reopened.SegmentReaderCache.Entries);
        Assert.False(next.RestartRequired);
        Assert.Equal(rows[150..300].Select(row => row.NativeTicks), next.Records.Select(record => record.Observation.NativeTicks));
        Assert.All(next.Records, record => Assert.True(record.Owner!.AdmittedUnderPolicy));

        // Its identity, read from the segments' headers, is the one their open readers give, so its cursor holds.
        foreach (string name in SessionSegments.Names(reopened.Current!)) _ = SessionSegments.Open(reopened, reopened.Current!, name);
        Assert.Equal(first.QueryIdentity, SessionEvidenceQuery.Read(reopened, pageSize: 150, resolveOwners: true).QueryIdentity);
        reopened.ReleaseSegmentReaders();

        // A page of a time scope opens only the segments it meets, starts at its first reading and stops at the first past
        // it: of a hundredth of the session, 90 rows, whatever else its segment holds; across a boundary, two segments.
        foreach ((TimeRange brush, ProcessInstanceId? owner, string? key, int opened, long read, int listed) in
            new (TimeRange, ProcessInstanceId?, string?, int, long, int)[]
            {
                (new TimeRange(2_050, 2_950), null, null, 1, 90, 90),
                (new TimeRange(1_950, 2_050), null, null, 2, 10, 10),
                (new TimeRange(2_050, 2_950), client, null, 1, 90, 45),
                (new TimeRange(2_050, 2_950), null, channel, 1, 90, 90),
                (new TimeRange(6_000, 9_000), null, null, 0, 0, 0),
            })
        {
            reopened = Reopened(split.Path);
            SessionEvidencePage scoped = SessionEvidenceQuery.Read(reopened, key, brush, owner, pageSize: 200);
            Assert.Equal((opened, read), (reopened.SegmentReaderCache.Entries, scoped.RowsRead));
            Assert.Equal(listed, scoped.Records.Count);
            Assert.Null(scoped.NextCursor);
            Assert.Equal(Records(SessionEvidenceQuery.Read(twin.Store, key, brush, owner, pageSize: 200)), Records(scoped));
            reopened.ReleaseSegmentReaders();
        }
    }

    [Fact(DisplayName = "§12.1: a process's connections open only the segments their lifetimes meet, within its time scope when it has one, and list what one segment of the same records lists")]
    public void AProcessesConnectionsOpenOnlyTheSegmentsTheyLieIn()
    {
        // Three processes' connections to other hosts in six segments of a hundred records, segment k holding ticks
        // 1,000k to 1,000k + 990. The first's runs through the third segment and ends at the fourth's first record; the
        // second's starts at the fourth's last record and runs to the end; the third holds a short connection inside a
        // long one, which takes every other record up to the fourth segment.
        ObservationRowV1[] rows =
        [
            Timed(Lifecycle(0, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\first.exe" }),
            Timed(Lifecycle(10, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\second.exe" }),
            Timed(Lifecycle(20, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Tools\third.exe" }),
            .. Enumerable.Range(3, 597).Select(row => Timed(row switch
            {
                >= 200 and <= 300 => Transfer(row * 10L, ObservationKind.Send, AccountingSide.SendSide, 10, 100, (ulong)(row + 1))
                    .Between("127.0.0.1:50000", "10.0.0.5:443"),
                >= 399 => Transfer(row * 10L, ObservationKind.Send, AccountingSide.SendSide, 20, 200, (ulong)(row + 1))
                    .Between("127.0.0.1:50001", "10.0.0.6:443"),
                >= 10 and < 20 => Transfer(row * 10L, ObservationKind.Receive, AccountingSide.ReceiveSide, 40, 300, (ulong)(row + 1))
                    .Between("127.0.0.1:50003", "10.0.0.8:443"),
                _ => Transfer(row * 10L, ObservationKind.Receive, AccountingSide.ReceiveSide, 30, 300, (ulong)(row + 1))
                    .Between("127.0.0.1:50002", "10.0.0.7:443"),
            })),
        ];
        using var split = new TemporarySession();
        using var twin = new TemporarySession();
        Publish(split.Store, rows, rowsPerSegment: 100);
        Publish(twin.Store, rows);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(twin.Store);
        ProcessInstanceId Process(int id) => overview.Nodes.Single(node => node.ProcessId == id).Id;
        _ = SessionOverviewProjector.Project(split.Store);
        Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(split.Store, Committed).Outcome);

        foreach ((int process, TimeRange? scope, int opened, long records) in new (int, TimeRange?, int, long)[]
        {
            (100, null, 2, 101),
            (200, null, 3, 201),
            (300, null, 4, 295),
            (200, new TimeRange(4_500, 4_600), 1, 10),
            (100, new TimeRange(4_500, 4_600), 0, 0),
        })
        {
            SessionStore reopened = Reopened(split.Path);
            ConnectionList listed = SessionConnections.OneSided(reopened, Process(process), scope);
            Assert.Equal(opened, reopened.SegmentReaderCache.Entries);
            Assert.Equal(records, listed.Connections.Sum(connection => connection.Records));
            Assert.Equal(SessionConnections.OneSided(twin.Store, Process(process), scope).Connections, listed.Connections);
            reopened.ReleaseSegmentReaders();
        }

        // Every process's connections together meet every segment.
        SessionStore all = Reopened(split.Path);
        Assert.Equal(SessionConnections.All(twin.Store).Connections.Select(held => held.Summary),
            SessionConnections.All(all).Connections.Select(held => held.Summary));
        Assert.Equal(6, all.SegmentReaderCache.Entries);
        all.ReleaseSegmentReaders();
    }

    /// <summary>Every interval read of the session, as one comparable text: its counts, byte rows, focus and rankings.</summary>
    private static string Answers(
        SessionStore store,
        TimeRange interval,
        ProcessInstanceId client,
        ProcessInstanceId server,
        string channel,
        bool channelsFirst = true)
    {
        // A channel's end is measured before the counts, which ask for the instances first, or after them.
        IntervalByteScope channelEnd = new() { ChannelKey = channel, End = 1 };
        SessionIntervalByteMeasures? end = channelsFirst ? SessionIntervalByteQuery.Measure(store, interval, 10, channelEnd) : null;
        SessionIntervalCounts counts = SessionIntervalQuery.Count(store, interval);
        end ??= SessionIntervalByteQuery.Measure(store, interval, 10, channelEnd);
        SessionByteMeasures bytes = SessionByteRanking.Measure(store, interval);
        SessionPeerMeasures peers = SessionPeerRanking.Measure(store, interval);
        SessionOwnerByteMeasures lanes = SessionIntervalByteQuery.MeasureByOwner(store, interval, 10, 10, [client, server]);
        SessionDirectionByteMeasures directions = SessionIntervalByteQuery.MeasureByDirection(store, interval, 10, client);
        SessionMechanismByteMeasures mechanisms = SessionIntervalByteQuery.MeasureByMechanism(
            store, interval, 10, [Mechanism.Tcp, Mechanism.ProcessLifecycle]);
        SessionFocusedTimeline focused = SessionTimelineQuery.Focused(store, interval, 10, new TimelineFocus(null, [client]));
        string[] parts =
        [
            $"{counts.ObservedRows} {counts.GraphRows}",
            Text(counts.EdgeRecords),
            Text(counts.ChannelRecords),
            Text(counts.ProcessRecords.ToDictionary(pair => pair.Key, pair => string.Join(",", pair.Value))),
            Text(bytes.ByProcess),
            Text(bytes.ByChannelEnd),
            bytes.Unattributed.ToString(),
            Text(peers.ByProcess),
            Text(peers.ByGroup),
            $"{peers.WithPeers} {peers.Unattributed}",
            Columns(SessionIntervalByteQuery.Measure(store, interval, 10, IntervalByteScope.Whole)),
            Columns(SessionIntervalByteQuery.Measure(store, interval, 10, new() { Owner = server })),
            Columns(end),
            Columns(lanes.Machine),
            .. lanes.Lanes.Select(Columns),
            .. directions.Lanes.Select(Columns),
            .. mechanisms.Lanes.Select(Columns),
            string.Join(",", focused.Focus.Select(bucket => bucket.ObservationCount)),
            string.Join(",", focused.Whole.Buckets.Select(bucket => bucket.ObservationCount)),
        ];
        return string.Join(" | ", parts);
    }

    /// <summary>A viewer of the session that has derived nothing and opened no segment.</summary>
    private static SessionStore Reopened(string path)
    {
        SessionDerivationCache.Clear();
        return SessionStore.OpenExisting(LocalOwnedDirectory.Open(path));
    }

    /// <summary>A page's records by their identity, reading and owner, wherever they are stored.</summary>
    private static string[] Records(SessionEvidencePage page) =>
        [.. page.Records.Select(record => $"{record.ObservationId} {record.Observation.NativeTicks} {record.Owner}")];

    private static string Columns(SessionIntervalByteMeasures measures) => string.Join(",", measures.Columns);

    private static string Text<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> values)
        where TKey : notnull =>
        string.Join("; ", values.Select(pair => $"{pair.Key}={pair.Value}").Order(StringComparer.Ordinal));

    /// <summary>
    /// The client's and the server's lifecycle records, then sends of 100 B from the client at even rows and the
    /// server's receives of them at odd rows, all on one connection: row r at tick 10r.
    /// </summary>
    private static ObservationRowV1[] Rows() =>
    [
        Timed(Lifecycle(0, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" }),
        Timed(Lifecycle(10, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe" }),
        .. Enumerable.Range(2, 598).Select(row => Timed(row % 2 == 0
            ? Transfer(row * 10L, ObservationKind.Send, AccountingSide.SendSide, 100, 100, (ulong)(row + 1)).Between(ClientEnd, ServerEnd)
            : Transfer(row * 10L, ObservationKind.Receive, AccountingSide.ReceiveSide, 100, 200, (ulong)(row + 1)).Between(ServerEnd, ClientEnd))),
    ];

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };
}
