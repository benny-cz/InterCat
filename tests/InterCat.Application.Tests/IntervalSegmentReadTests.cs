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

    [Fact(DisplayName = "§12.1: a finished capture of RPC calls keeps them, so a reopen's first brush opens only the segments it meets; a generation that keeps none pairs them over every segment once")]
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

        // The finished session's operation index keeps its calls and their other ends, so even a reopen's first brush
        // reads only the segments it meets, and no source field: all three for the early interval, and the second and
        // third for the late one, where the third call's records lie, from tick 300.
        var early = new TimeRange(0, 150);
        var late = new TimeRange(250, 400);
        SessionStore indexed = Reopened(split.Path);
        Assert.Equal(4, SessionIntervalQuery.Count(indexed, early).EdgeRecords[edge]);
        Assert.Equal(3, indexed.SegmentReaderCache.Entries);
        Assert.True(SessionDerivationCache.For(indexed.Current!).CallsFromIndex);
        indexed.ReleaseSegmentReaders();
        SessionStore lateFirst = Reopened(split.Path);
        SessionIntervalCounts counted = SessionIntervalQuery.Count(lateFirst, late);
        Assert.Equal(2, lateFirst.SegmentReaderCache.Entries);
        Assert.Equal(4, counted.EdgeRecords[edge]);
        Assert.Equal(Text(SessionIntervalQuery.Count(twin.Store, late).EdgeRecords), Text(counted.EdgeRecords));
        lateFirst.ReleaseSegmentReaders();

        // A generation that keeps no calls, as a live one does, pairs them over every segment and their source fields at
        // its first brush, even with the checkpoint's instances, since a call's request and its response can lie in
        // different segments; a later brush reads the pairs it made, and only the segments it meets.
        OperationIndexReopenTests.Republish(split.Store, operations: null);
        SessionStore first = Reopened(split.Path);
        Assert.Equal(4, SessionIntervalQuery.Count(first, early).EdgeRecords[edge]);
        Assert.Equal(5, first.SegmentReaderCache.Entries);
        Assert.False(SessionDerivationCache.For(first.Current!).CallsFromIndex);
        SessionStore later = SessionStore.OpenExisting(LocalOwnedDirectory.Open(split.Path));
        counted = SessionIntervalQuery.Count(later, late);
        Assert.Equal(2, later.SegmentReaderCache.Entries);
        Assert.Equal(4, counted.EdgeRecords[edge]);
        first.ReleaseSegmentReaders();
        later.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "§12.1: once a generation's RPC calls are paired, its call ranking, a process's RPC channels and their spans open no segment, and a page of calls only the segments holding its calls and their other ends")]
    public void PairedCallsAreReadWithoutTheirSegments()
    {
        // The service control manager's linked calls in three segments of six records, cut as they arrived, each at its
        // session time; its twin holds them in one. The client's third call, from tick 300 to 340, lies in the second, and
        // the call that served it, from 305 to 330, in the third.
        (ObservationRowV1[] linked, SourceFieldRowV1[] fields) = LinkedRpcCalls();
        ObservationRowV1[] rows = [.. linked.Select(Timed)];
        using var split = new TemporarySession();
        using var twin = new TemporarySession();
        Publish(split.Store, rows, rowsPerSegment: 6, fields: fields, coverage: RpcLedger(alpc: true));
        Publish(twin.Store, rows, fields: fields, coverage: RpcLedger(alpc: true));
        Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(split.Store, Committed).Outcome);
        ProcessInstanceId client = SessionOverviewProjector.Project(twin.Store).Nodes.Single(node => node.ProcessId == 400).Id;
        var late = new TimeRange(250, 400);

        // In a generation that keeps no calls, as a live one does, the first ranking pairs them over every segment and
        // their source fields, and the first listing follows their other ends over the same.
        OperationIndexReopenTests.Republish(split.Store, operations: null);
        SessionDerivationCache.Clear();
        SessionStore first = SessionStore.OpenExisting(LocalOwnedDirectory.Open(split.Path));
        _ = SessionCallRanking.Measure(first, null);
        Assert.Equal(5, first.SegmentReaderCache.Entries);
        string channel = Assert.Single(SessionRpcCalls.Channels(first, client).Channels).Key;
        first.ReleaseSegmentReaders();

        // Later, the ranking of a time scope, the process's channels and their calls' spans read the pairs and open none.
        SessionStore later = SessionStore.OpenExisting(LocalOwnedDirectory.Open(split.Path));
        SessionCallMeasures scoped = SessionCallRanking.Measure(later, late);
        RpcChannelList channels = SessionRpcCalls.Channels(later, client, late);
        RpcCallSpanPage spans = SessionRpcCalls.Spans(later, channel, late);
        Assert.Equal(0, later.SegmentReaderCache.Entries);
        Assert.Equal(Text(SessionCallRanking.Measure(twin.Store, late).ByProcess), Text(scoped.ByProcess));
        Assert.Equal(Assert.Single(SessionRpcCalls.Channels(twin.Store, client, late).Channels).Counts,
            Assert.Single(channels.Channels).Counts);
        Assert.Equal(1, spans.Total);

        // A page of the calls the scope holds opens the segment its one call lies in and the one its other end does.
        RpcCallPage page = SessionRpcCalls.Calls(later, channel, interval: late);
        Assert.Equal(2, later.SegmentReaderCache.Entries);
        RpcCallRow shown = Assert.Single(page.Calls);
        RpcCallRow expected = Assert.Single(SessionRpcCalls.Calls(twin.Store, channel, interval: late).Calls);
        Assert.Equal((expected.Key, expected.OtherEnd?.Process?.ProcessId), (shown.Key, shown.OtherEnd?.Process?.ProcessId));
        later.ReleaseSegmentReaders();

        // The call's evidence opens only the segment holding its two records, and a zoom of its channel only the segments
        // the zoom meets.
        SessionStore evidence = SessionStore.OpenExisting(LocalOwnedDirectory.Open(split.Path));
        SessionEvidencePage records = SessionEvidenceQuery.Read(evidence, operationKey: shown.Key);
        Assert.Equal((1, 2), (evidence.SegmentReaderCache.Entries, records.Records.Count));
        Assert.Equal(Records(SessionEvidenceQuery.Read(twin.Store, operationKey: shown.Key)), Records(records));
        evidence.ReleaseSegmentReaders();
        SessionStore zoom = SessionStore.OpenExisting(LocalOwnedDirectory.Open(split.Path));
        SessionFocusedTimeline zoomed = SessionTimelineQuery.Focused(zoom, late, 15, new TimelineFocus(null, [], channel));
        Assert.Equal(2, zoom.SegmentReaderCache.Entries);
        Assert.Equal(SessionTimelineQuery.Focused(twin.Store, late, 15, new TimelineFocus(null, [], channel)).Focus, zoomed.Focus);
        Assert.Equal(2, zoomed.Focus.Sum(bucket => bucket.ObservationCount));
        zoom.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "§12.1: an RPC channel's records are gathered once and kept for the generation's next page and zoom of them, and each reads only the channel's rows of the segments it opens")]
    public void AChannelsRecordsAreKeptAndReadAlone()
    {
        // Two processes' calls, alternating, in segments of ten records: the client's channel holds every other call, so
        // nearly half of each segment's rows are the other process's. Its twin holds them in one segment.
        ObservationRowV1[] rows = AlternatingCalls(30);
        using var split = new TemporarySession();
        using var twin = new TemporarySession();
        Publish(split.Store, rows, rowsPerSegment: 10, coverage: RpcLedger(alpc: false));
        Publish(twin.Store, rows, coverage: RpcLedger(alpc: false));
        Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(split.Store, Committed).Outcome);
        ProcessInstanceId client = SessionOverviewProjector.Project(twin.Store).Nodes.Single(node => node.ProcessId == 400).Id;
        SessionStore store = Reopened(split.Path);
        string channel = Assert.Single(SessionRpcCalls.Channels(store, client).Channels).Key;

        // A page of the channel's records reads its rows alone: ten, and the eleventh that says there is more, however
        // many of the other process's lie between them. Its next page continues from there, and the last ends the list.
        SessionEvidencePage first = SessionEvidenceQuery.Read(store, operationKey: channel, pageSize: 10);
        SessionEvidencePage next = SessionEvidenceQuery.Read(store, operationKey: channel, pageSize: 10, cursor: first.NextCursor);
        SessionEvidencePage last = SessionEvidenceQuery.Read(store, operationKey: channel, pageSize: 10, cursor: next.NextCursor);
        Assert.Equal((11L, 11L, 10L), (first.RowsRead, next.RowsRead, last.RowsRead));
        Assert.Null(last.NextCursor);
        string[] listed = Records(SessionEvidenceQuery.Read(twin.Store, operationKey: channel, pageSize: 100));
        Assert.Equal(30, listed.Length);
        string[] paged = [.. Records(first), .. Records(next), .. Records(last)];
        Assert.Equal(listed, paged);

        // The records were gathered once, for the first page, and the generation keeps them: a zoom of the channel counts
        // the kept ones, its own rows alone, as the twin's zoom counts them.
        SessionDerivation derivation = SessionDerivationCache.For(store.Current!);
        OperationRecords kept = derivation.Operation(channel, EvidencePolicy.IncludeCorrelated,
            () => throw new InvalidOperationException("The channel's records were gathered again."))!;
        Assert.Equal(30, kept.Count);
        var extent = new TimeRange(0, 200);
        SessionFocusedTimeline zoomed = SessionTimelineQuery.Focused(store, extent, 12, new TimelineFocus(null, [], channel));
        Assert.Equal(SessionTimelineQuery.Focused(twin.Store, extent, 12, new TimelineFocus(null, [], channel)).Focus, zoomed.Focus);
        Assert.Equal(30, zoomed.Focus.Sum(bucket => bucket.ObservationCount));
        Assert.Same(kept, derivation.Operation(channel, EvidencePolicy.IncludeCorrelated, () => null));

        // The generation keeps the records of the last four operations asked for, a policy's apart from another's: the
        // channel's, asked for again after three of its calls', outlast a fourth call's, and once its records under another
        // policy and three calls' more have been asked for, they are gathered anew.
        OperationRecords Kept(string key) => derivation.Operation(key, EvidencePolicy.IncludeCorrelated,
            () => throw new InvalidOperationException("The records were gathered again."))!;
        void Page(RpcCallRow call) => Assert.Equal(2, SessionEvidenceQuery.Read(store, operationKey: call.Key).Records.Count);
        RpcCallRow[] calls = [.. SessionRpcCalls.Calls(store, channel).Calls.Take(4)];
        foreach (RpcCallRow call in calls[..3]) Page(call);
        Assert.Same(kept, Kept(channel));
        Page(calls[3]);
        Assert.Same(kept, Kept(channel));
        OperationRecords direct = OperationRecords.Of([]);
        Assert.Same(direct, derivation.Operation(channel, EvidencePolicy.DirectOnly, () => direct));
        foreach (RpcCallRow call in calls[..3]) Page(call);
        Assert.Equal(2, Kept(calls[2].Key).Count);
        Assert.NotSame(kept, derivation.Operation(channel, EvidencePolicy.IncludeCorrelated, () => OperationRecords.Of([])));
        store.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "§12.1: an operation's records are held by segment, each once, its rows ascending")]
    public void AnOperationsRecordsAreHeldBySegment()
    {
        OperationRecords records = OperationRecords.Of([("b", 5), ("a", 3), ("b", 1), ("b", 5)]);
        Assert.Equal(3, records.Count);
        Assert.True(records.Holds("a"));
        Assert.False(records.Holds("c"));
        Assert.Equal("1 5", string.Join(' ', records.RowsIn("b").ToArray()));
        Assert.True(records.RowsIn("c").IsEmpty);
    }

    [Fact(DisplayName = "§12.1: a finished session's HTTP exchanges are read without grouping, and once a generation's are grouped, a process's exchanges and their spans open no segment, and an exchange's evidence only the segment holding its buffers")]
    public void GroupedExchangesAreReadWithoutTheirSegments()
    {
        // A process's WinINet buffers in four segments of six records, cut as they arrived; its twin holds them in one.
        // Exchange 1's second use, its buffers at ticks 100 to 102, lies in the third.
        ObservationRowV1[] rows = SessionHttpExchangesTests.Rows(out SourceFieldRowV1[] fields);
        using var split = new TemporarySession();
        using var twin = new TemporarySession();
        Publish(split.Store, rows, rowsPerSegment: 6, fields: fields);
        Publish(twin.Store, rows, fields: fields);
        Assert.Equal(4, SessionSegments.Names(split.Store.Current!).Count);
        Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(split.Store, Committed).Outcome);
        ProcessInstanceId client = SessionHttpExchangesTests.Client(twin.Store);
        string channel = HttpExchangeKeys.Channel(client);
        var late = new TimeRange(90, 200);

        // The finished session keeps its exchanges, so a reopen's first read opens no segment; in a generation that
        // keeps none, as a live one does, the first read groups them over every segment and their source fields.
        SessionStore indexed = Reopened(split.Path);
        HttpExchangePage kept = SessionHttpExchanges.Exchanges(indexed, channel);
        Assert.Equal(0, indexed.SegmentReaderCache.Entries);
        OperationIndexReopenTests.Republish(split.Store, operations: null);
        SessionDerivationCache.Clear();
        SessionStore first = SessionStore.OpenExisting(LocalOwnedDirectory.Open(split.Path));
        HttpExchangePage all = SessionHttpExchanges.Exchanges(first, channel);
        Assert.Equal(4 + SessionSegments.FieldNames(split.Store.Current!).Count, first.SegmentReaderCache.Entries);
        Assert.Equal(kept.Exchanges, all.Exchanges);
        first.ReleaseSegmentReaders();

        // Later, the process's exchanges, a time scope's and their spans read the groups and open none.
        SessionStore later = SessionStore.OpenExisting(LocalOwnedDirectory.Open(split.Path));
        HttpChannelSummary scoped = Assert.Single(SessionHttpExchanges.Channels(later, client, late).Channels);
        HttpExchangePage page = SessionHttpExchanges.Exchanges(later, channel, interval: late);
        HttpExchangeSpanPage spans = SessionHttpExchanges.Spans(later, channel, late);
        Assert.Equal(0, later.SegmentReaderCache.Entries);
        Assert.Equal(Assert.Single(SessionHttpExchanges.Channels(twin.Store, client, late).Channels), scoped);
        Assert.Equal((1L, 3L), (scoped.Exchanges, scoped.Records));
        Assert.Equal(all.Exchanges[3].Key, Assert.Single(page.Exchanges).Key);
        Assert.Equal(SessionHttpExchanges.Spans(twin.Store, channel, late).Total, spans.Total);
        later.ReleaseSegmentReaders();

        // The exchange's evidence opens the one segment holding its three buffers, which the generation keeps.
        SessionStore evidence = SessionStore.OpenExisting(LocalOwnedDirectory.Open(split.Path));
        SessionEvidencePage records = SessionEvidenceQuery.Read(evidence, operationKey: all.Exchanges[3].Key);
        Assert.Equal((1, 3), (evidence.SegmentReaderCache.Entries, records.Records.Count));
        Assert.Equal(3, SessionDerivationCache.For(evidence.Current!).Operation(all.Exchanges[3].Key,
            EvidencePolicy.IncludeCorrelated, () => throw new InvalidOperationException("The buffers were gathered again."))!.Count);
        Assert.Equal(Records(SessionEvidenceQuery.Read(twin.Store, operationKey: all.Exchanges[3].Key)), Records(records));
        evidence.ReleaseSegmentReaders();
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

    /// <summary>
    /// The client's and another process's lifecycle records, then calls to the service control manager four ticks apart,
    /// each a request and its response two ticks later: the client's the even calls, the other process's the odd.
    /// </summary>
    private static ObservationRowV1[] AlternatingCalls(int calls) =>
    [
        Timed(Lifecycle(1, ObservationKind.Create, 400, 1)),
        Timed(Lifecycle(2, ObservationKind.Create, 500, 2)),
        .. Enumerable.Range(0, calls).SelectMany(call => new[]
        {
            Timed(RpcCall(10 + (4L * call), ObservationKind.RequestStart, Direction.Outbound, call % 2 == 0 ? 400 : 500,
                (ulong)(10 + (2 * call)), new Guid(call + 1, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 1), ServiceControlInterface)),
            Timed(RpcCall(12 + (4L * call), ObservationKind.RequestEnd, Direction.Outbound, call % 2 == 0 ? 400 : 500,
                (ulong)(11 + (2 * call)), new Guid(call + 1, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 1), status: 0)),
        }),
    ];

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };
}
