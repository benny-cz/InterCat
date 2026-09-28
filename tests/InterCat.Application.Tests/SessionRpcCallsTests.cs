using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// A session's RPC calls as the ladder reads them: a process's channels, a channel's calls a page at a time, and the
/// records of a channel or of one call (`contracts/operations-v1.md` §5).
/// </summary>
public sealed class SessionRpcCallsTests
{
    private static readonly Guid ServiceControl = Guid.Parse("367abb81-9844-35f1-ad32-98f038001003");
    private static readonly Guid Unnamed = Guid.Parse("0a74ef1c-41a4-4e06-83ae-dc74fb1cdd53");

    [Fact(DisplayName = "R22: a process's RPC channels are its own calls by side and interface, keyed by instance")]
    public void AProcessHasItsOwnChannels()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Calls());
        (ProcessInstanceId client, ProcessInstanceId host) = Instances(session.Store);

        RpcChannelList channels = SessionRpcCalls.Channels(session.Store, client);
        Assert.Equal(
            [
                RpcChannelKeys.Channel(client, RpcCallSide.Client, ServiceControl),
                RpcChannelKeys.Channel(client, RpcCallSide.Client, Unnamed),
            ],
            channels.Channels.Select(channel => channel.Key));
        RpcChannelSummary scm = channels.Channels[0];
        Assert.Equal((3L, 3L, 1L, 6L), (scm.Counts.Calls, scm.Counts.Completed, scm.Counts.Failed, scm.Records));
        Assert.Equal("RPC calls to svcctl (Service Control Manager)", scm.Name);
        Assert.Equal("3 calls · 1 failed · median 2.0 µs", scm.Outcome(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("RPC calls to " + Unnamed, channels.Channels[1].Name);
        Assert.Equal("1 call · 1 open at capture end", channels.Channels[1].Outcome(System.Globalization.CultureInfo.InvariantCulture));

        RpcChannelSummary served = Assert.Single(SessionRpcCalls.Channels(session.Store, host).Channels);
        Assert.Equal(("RPC calls served on svcctl (Service Control Manager)", 3L), (served.Name, served.Counts.Calls));
    }

    [Fact(DisplayName = "P8: a channel's calls page in reading order, and each call's key finds it again")]
    public void ChannelCallsPageInReadingOrder()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Calls());
        (ProcessInstanceId client, _) = Instances(session.Store);
        string channel = RpcChannelKeys.Channel(client, RpcCallSide.Client, ServiceControl);

        RpcCallPage first = SessionRpcCalls.Calls(session.Store, channel, 0, 2);
        Assert.Null(first.Problem);
        Assert.True(first.More);
        Assert.Equal([100L, 200L], first.Calls.Select(row => row.Call.Start!.NativeTicks));
        RpcCallPage rest = SessionRpcCalls.Calls(session.Store, channel, 2, 2);
        Assert.False(rest.More);
        Assert.Equal([300L], rest.Calls.Select(row => row.Call.Start!.NativeTicks));
        Assert.Equal(5L, rest.Calls[0].Call.Status);

        Assert.True(RpcChannelKeys.TryParseCall(first.Calls[1].Key, out string parsed, out var located));
        Assert.Equal(channel, parsed);
        Assert.Equal(first.Calls[1].Call.Identity.RawRecordId.RecordOrdinal, located.Ordinal);

        // A channel this generation does not hold is stated, not an empty page.
        RpcCallPage missing = SessionRpcCalls.Calls(session.Store, RpcChannelKeys.Channel(client, RpcCallSide.Server, ServiceControl));
        Assert.NotNull(missing.Problem);
        Assert.Empty(missing.Calls);
    }

    [Fact(DisplayName = "§3.2: a channel read through one of its calls lists every call up to the end of the page that holds it")]
    public void ChannelCallsReadThroughACall()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Calls());
        (ProcessInstanceId client, _) = Instances(session.Store);
        string channel = RpcChannelKeys.Channel(client, RpcCallSide.Client, ServiceControl);
        string[] keys = [.. SessionRpcCalls.Calls(session.Store, channel).Calls.Select(row => row.Key)];
        static IEnumerable<long> Starts(RpcCallPage page) => page.Calls.Select(row => row.Call.Start!.NativeTicks);

        // Pages of one: through the second call, then the third, which ends the channel.
        RpcCallPage second = SessionRpcCalls.CallsThrough(session.Store, keys[1], pageSize: 1);
        Assert.Equal([100L, 200L], Starts(second));
        Assert.Equal((true, 0), (second.More, second.Offset));
        RpcCallPage third = SessionRpcCalls.CallsThrough(session.Store, keys[2], pageSize: 1);
        Assert.Equal([100L, 200L, 300L], Starts(third));
        Assert.False(third.More);

        // Pages of two: the second call's page ends with it, the third's would hold a fourth the channel lacks.
        Assert.Equal([100L, 200L], Starts(SessionRpcCalls.CallsThrough(session.Store, keys[1], pageSize: 2)));
        Assert.Equal([100L, 200L, 300L], Starts(SessionRpcCalls.CallsThrough(session.Store, keys[2], pageSize: 2)));

        // A call past the most it lists reads the first page alone, as a plain read does.
        RpcCallPage far = SessionRpcCalls.CallsThrough(session.Store, keys[2], pageSize: 1, maximum: 2);
        Assert.Equal([100L], Starts(far));
        Assert.True(far.More);

        // Within [110, 250) the channel lists its first two calls: the second is listed there, the third is not.
        var early = new TimeRange(110, 250);
        Assert.Equal([100L, 200L], Starts(SessionRpcCalls.CallsThrough(session.Store, keys[1], pageSize: 1, interval: early)));
        Assert.Equal([100L], Starts(SessionRpcCalls.CallsThrough(session.Store, keys[2], pageSize: 1, interval: early)));

        _ = Assert.Throws<ArgumentException>(() => SessionRpcCalls.CallsThrough(session.Store, channel));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => SessionRpcCalls.CallsThrough(
            session.Store, keys[0], maximum: SessionRpcCalls.MaximumListedThrough + 1));
    }

    [Fact(DisplayName = "P8: the evidence of an RPC channel is its calls' records, and of one call its start and stop")]
    public void EvidenceOfAChannelAndACallIsTheirRecords()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Calls(), rowsPerSegment: 3);
        (ProcessInstanceId client, _) = Instances(session.Store);
        string channel = RpcChannelKeys.Channel(client, RpcCallSide.Client, ServiceControl);

        // The channel's six records, in canonical order across segments, a page at a time.
        var seen = new List<ulong>();
        string? cursor = null;
        do
        {
            SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store, pageSize: 4, cursor: cursor, resolveOwners: true, operationKey: channel);
            Assert.False(page.RestartRequired);
            Assert.Equal(channel, page.OperationKey);
            Assert.All(page.Records, record => Assert.Equal(client, record.Owner!.Instance));
            seen.AddRange(page.Records.Select(record => record.Observation.RawRecordOrdinal));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal([10UL, 11UL, 20UL, 21UL, 30UL, 31UL], seen);

        RpcCallRow second = SessionRpcCalls.Calls(session.Store, channel).Calls[1];
        Assert.Equal([20UL, 21UL], SessionEvidenceQuery.Read(session.Store, operationKey: second.Key)
            .Records.Select(record => record.Observation.RawRecordOrdinal));

        _ = Assert.Throws<ArgumentException>(() => SessionEvidenceQuery.Read(session.Store, channelKey: "tcp:x", operationKey: channel));
        _ = Assert.Throws<ArgumentException>(() => SessionEvidenceQuery.Read(session.Store, operationKey: "rpc:nonsense"));
    }

    [Fact(DisplayName = "P8: the calls a timeline draws are those running in its interval, and a denser one says how many it left out")]
    public void SpansAreTheCallsRunningInAnInterval()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [.. Calls().Select(row => row with { SessionRelativeTicks = row.NativeTicks * 100 })]);
        (ProcessInstanceId client, _) = Instances(session.Store);
        string channel = RpcChannelKeys.Channel(client, RpcCallSide.Client, ServiceControl);
        string unnamed = RpcChannelKeys.Channel(client, RpcCallSide.Client, Unnamed);

        // Session nanoseconds are 100 native ticks each here, so a call's workspace ticks are its native ones.
        RpcCallSpanPage all = SessionRpcCalls.Spans(session.Store, channel, new TimeRange(0, 1_000));
        Assert.Equal([(100L, 120L), (200L, 210L), (300L, 340L)],
            all.Calls.Select(call => (call.StartTicks!.Value, call.EndTicks!.Value)));
        Assert.Equal([false, false, true], all.Calls.Select(call => call.Failed));
        Assert.Equal(3, all.Total);

        // A call runs from its start to its stop, and only the calls within an interval are drawn for it.
        RpcCallSpanPage middle = SessionRpcCalls.Spans(session.Store, channel, new TimeRange(205, 300));
        Assert.Equal([200L], middle.Calls.Select(call => call.StartTicks!.Value));

        // A call open at capture end runs to the end of the session.
        RpcCallSpanPage open = SessionRpcCalls.Spans(session.Store, unnamed, new TimeRange(5_000, 6_000));
        RpcCallSpanView still = Assert.Single(open.Calls);
        Assert.Equal((RpcCallState.OpenAtCaptureEnd, (long?)null), (still.State, still.EndTicks));

        // More calls than the budget: none of them one by one, and all of them as density columns instead, each counting
        // the calls running in it, never dropping one (§6.2, R21). The failing call keeps its caution share.
        RpcCallSpanPage budgeted = SessionRpcCalls.Spans(session.Store, channel, new TimeRange(0, 1_000), budget: 2, columns: 10);
        Assert.Equal((0, 3L), (budgeted.Calls.Count, budgeted.Total));
        RpcCallDensity density = budgeted.Density!;
        Assert.Equal([0L, 1L, 1L, 1L, 0L, 0L, 0L, 0L, 0L, 0L], density.Running);
        Assert.Equal([0L, 0L, 0L, 1L, 0L, 0L, 0L, 0L, 0L, 0L], density.Failed);
        Assert.Equal((1L, new TimeRange(300, 400)), (density.Maximum, density.ColumnInterval(3)));
        Assert.Null(all.Density);

        // A call counts in every column from its start's to its stop's: 100-120 in three columns of ten ticks, 200-210 in
        // two, and the failing 300-340 in five.
        RpcCallDensity fine = SessionRpcCalls.Spans(session.Store, channel, new TimeRange(0, 1_000), budget: 2, columns: 100).Density!;
        Assert.Equal([1L, 1L, 1L, 0L], fine.Running.Skip(10).Take(4));
        Assert.Equal([1L, 1L, 0L], fine.Running.Skip(20).Take(3));
        Assert.Equal((10L, 5L), (fine.Running.Sum(), fine.Failed.Sum()));

        // Two calls running at once count twice, and a call still open at capture end runs to the interval's end.
        using var overlapping = new TemporarySession();
        Publish(overlapping.Store,
        [
            .. new[]
            {
                Lifecycle(1, ObservationKind.Create, 400, 1),
                RpcCall(100, ObservationKind.RequestStart, Direction.Outbound, 400, 10, Activity(1), ServiceControl),
                RpcCall(120, ObservationKind.RequestStart, Direction.Outbound, 400, 11, Activity(2), ServiceControl),
                RpcCall(130, ObservationKind.RequestEnd, Direction.Outbound, 400, 12, Activity(2), status: 0),
                RpcCall(150, ObservationKind.RequestEnd, Direction.Outbound, 400, 13, Activity(1), status: 0),
                RpcCall(300, ObservationKind.RequestStart, Direction.Outbound, 400, 14, Activity(3), ServiceControl),
            }.Select(row => row with { SessionRelativeTicks = row.NativeTicks * 100 }),
        ]);
        ProcessInstanceId caller = SessionOverviewProjector.Project(overlapping.Store).Nodes.Single(node => node.ProcessId == 400).Id;
        RpcCallDensity overlap = SessionRpcCalls.Spans(
            overlapping.Store, RpcChannelKeys.Channel(caller, RpcCallSide.Client, ServiceControl), new TimeRange(0, 1_000),
            budget: 1, columns: 10).Density!;
        Assert.Equal([0L, 2L, 0L, 1L, 1L, 1L, 1L, 1L, 1L, 1L], overlap.Running);
        Assert.Equal(2, overlap.Maximum);
    }

    [Fact(DisplayName = "§6.2: a density column's interval holds exactly the ticks placed in it, whatever the interval and columns")]
    public void DensityColumnsHoldTheirTicks()
    {
        var random = new Random(7);
        for (int trial = 0; trial < 200; trial++)
        {
            long start = random.Next(-50, 50);
            var interval = new TimeRange(start, start + random.Next(1, 300));
            int columns = (int)Math.Min(interval.SpanTicks, random.Next(1, 40));
            var density = new RpcCallDensity(interval, new long[columns], new long[columns]);
            Assert.Equal(interval.StartTicks, density.ColumnInterval(0).StartTicks);
            Assert.Equal(interval.EndTicks, density.ColumnInterval(columns - 1).EndTicks);
            for (long tick = interval.StartTicks; tick < interval.EndTicks; tick++)
            {
                int column = RpcCallDensity.ColumnOf(interval, columns, tick);
                Assert.True(density.ColumnInterval(column).Contains(tick), $"{interval} in {columns}: tick {tick} in {column}");
            }
        }
    }

    [Fact]
    public void KeysRoundTripAndRefuseWhatTheyDoNotName()
    {
        var instance = new ProcessInstanceId(Guid.Parse("5b6a1c2e-4d3f-4a5b-8c7d-9e0f1a2b3c4d"));
        string channel = RpcChannelKeys.Channel(instance, RpcCallSide.Server, null);
        Assert.Equal("rpc:5b6a1c2e4d3f4a5b8c7d9e0f1a2b3c4d:server:none", channel);
        Assert.True(RpcChannelKeys.TryParseChannel(channel, out ProcessInstanceId parsed, out RpcCallSide side, out Guid? rpcInterface));
        Assert.Equal((instance, RpcCallSide.Server, (Guid?)null), (parsed, side, rpcInterface));

        string call = RpcChannelKeys.Call(channel, 1, 2, 3, new FactKey(0xabc, 0xdef));
        Assert.True(RpcChannelKeys.TryParseCall(call, out string callChannel, out var first));
        Assert.Equal((channel, 1u, 2u, 3UL, new FactKey(0xabc, 0xdef)), (callChannel, first.Stream, first.Epoch, first.Ordinal, first.FactKey));
        Assert.False(RpcChannelKeys.TryParseChannel(call, out _, out _, out _));
        Assert.False(RpcChannelKeys.TryParseCall(channel, out _, out _));
        Assert.False(RpcChannelKeys.TryParseChannel("rpc:5b6a1c2e4d3f4a5b8c7d9e0f1a2b3c4d:sideways:none", out _, out _, out _));
        Assert.False(RpcChannelKeys.TryParseChannel("tcp:whatever", out _, out _, out _));
        Assert.Equal("an interface no start named", RpcInterfaceNames.Describe(null));
        Assert.Equal("epmapper (RPC Endpoint Mapper)", RpcInterfaceNames.Describe(Guid.Parse("e1af8308-5d1f-11c9-91a4-08002b14a0fa")));
    }

    [Fact]
    public void AnRpcFilterScopesEvidenceToItsRecords()
    {
        var snapshot = OverviewWorkspace.Empty();
        string channel = RpcChannelKeys.Channel(new ProcessInstanceId(Guid.NewGuid()), RpcCallSide.Client, ServiceControl);
        string call = RpcChannelKeys.Call(channel, 1, 1, 7, new FactKey(1, 2));
        var rung = new NavigationState
        {
            Level = DetailLevel.Evidence,
            Focus = new(DetailLevel.Evidence, call, "Call"),
            Viewport = new TimeRange(5, 9),
            Lanes = LaneGrouping.Endpoint,
            GraphFocusKey = null,
            Filters =
            [
                new("channel", "calls to svcctl", "test") { Key = channel, Level = DetailLevel.Channel },
                new("scope", "call at +1 s", "test") { Key = call, Level = DetailLevel.Operation },
            ],
        };

        EvidenceScope scope = EvidenceScopes.Resolve(snapshot, rung);
        Assert.Equal((call, (TimeRange?)null, "Records of call at +1 s"), (scope.OperationKey, scope.Interval, scope.Description));
        Assert.False(scope.IsWholeSession);
        EvidenceScope wider = EvidenceScopes.Resolve(snapshot, rung with { Filters = [rung.Filters[0]] });
        Assert.Equal(channel, wider.OperationKey);
        Assert.StartsWith("Records of calls to svcctl", wider.Description, StringComparison.Ordinal);
    }

    /// <summary>
    /// A client, PID 400, calls the Service Control Manager three times - one call failing - and an unnamed interface once,
    /// never answered; the service host, PID 1960, raised the served side of the first three.
    /// </summary>
    [Fact(DisplayName = "§6.4: within an interval a process's RPC channels count the calls it holds by the record that counts each")]
    public void ChannelsCountTheCallsAnIntervalHolds()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Calls());
        (ProcessInstanceId client, ProcessInstanceId host) = Instances(session.Store);
        string scm = RpcChannelKeys.Channel(client, RpcCallSide.Client, ServiceControl);
        string unnamed = RpcChannelKeys.Channel(client, RpcCallSide.Client, Unnamed);

        // [110, 250) holds the first two calls' stops, so both complete in it; the first call's start is before it, so
        // three of the channel's call records are read in it. The open call's start is later: its channel counts none.
        var early = new TimeRange(110, 250);
        Dictionary<string, RpcChannelSummary> within = SessionRpcCalls.Channels(session.Store, client, early).Channels
            .ToDictionary(channel => channel.Key);
        Assert.Equal((2L, 2L, 0L, 3L), (within[scm].Counts.Calls, within[scm].Counts.Completed, within[scm].Counts.Failed, within[scm].Records));
        Assert.Equal("2 calls · median 1.0 µs", within[scm].Outcome(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal((0L, 0L), (within[unnamed].Counts.Calls, within[unnamed].Records));

        // Its page lists exactly those calls, in reading order.
        RpcCallPage page = SessionRpcCalls.Calls(session.Store, scm, interval: early);
        Assert.Equal([100L, 200L], page.Calls.Select(row => row.Call.Start!.NativeTicks));
        Assert.False(page.More);
        Assert.Equal(2L, page.Channel!.Counts.Calls);
        RpcCallPage paged = SessionRpcCalls.Calls(session.Store, scm, 0, 1, early);
        Assert.True(paged.More);
        Assert.Equal([200L], SessionRpcCalls.Calls(session.Store, scm, 1, 1, early).Calls.Select(row => row.Call.Start!.NativeTicks));

        // [250, 500) holds the failed call's stop and the open call's start.
        Dictionary<string, RpcChannelSummary> late = SessionRpcCalls.Channels(session.Store, client, new TimeRange(250, 500)).Channels
            .ToDictionary(channel => channel.Key);
        Assert.Equal((1L, 1L), (late[scm].Counts.Calls, late[scm].Counts.Failed));
        Assert.Equal((1L, 1L), (late[unnamed].Counts.Calls, late[unnamed].Counts.OpenAtCaptureEnd));

        // The operations metric counts the same completed calls for the same interval (§8a).
        MetricResult completed = SessionMetrics.Evaluate(session.Store, new MetricRequest
        {
            Basis = AnalysisBasis.LogicalOperations,
            Metric = Metric.OperationsCompleted,
            Owner = client,
            Interval = early,
        });
        Assert.Equal(completed.Value, within.Values.Sum(channel => channel.Counts.Completed));

        // An interval holding every reading is the whole session, channel by channel, for either process.
        foreach (ProcessInstanceId instance in new[] { client, host })
        {
            Assert.Equal(
                SessionRpcCalls.Channels(session.Store, instance).Channels,
                SessionRpcCalls.Channels(session.Store, instance, new TimeRange(0, 10_000)).Channels);
        }
    }

    [Fact(DisplayName = "§7.4: a channel names who served its calls, and a call its other end and the key of the call there")]
    public void ChannelsAndCallsNameTheirOtherEnd()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] messages =
        [
            Alpc(102, ObservationKind.Send, 400, 401, 60),
            Alpc(103, ObservationKind.Receive, 1_960, 1_961, 61),
            Alpc(302, ObservationKind.Send, 400, 401, 62),
            Alpc(303, ObservationKind.Receive, 1_960, 1_961, 63),
        ];
        ObservationRowV1[] rows = [.. Calls(), .. messages];
        Publish(session.Store, rows, fields:
        [
            .. rows.Where(row => row is { Mechanism: Mechanism.Rpc, Kind: ObservationKind.RequestStart })
                .Select(start => Field(start, SourceField.RpcProcedureNumber, 7)),
            .. messages.Select((message, index) => Field(message, SourceField.AlpcMessageId, 21 + (index / 2))),
        ]);
        (ProcessInstanceId client, ProcessInstanceId host) = Instances(session.Store);
        var culture = System.Globalization.CultureInfo.InvariantCulture;

        // The first and third calls reached the host's; the second sent no message; the call that never ended has no window.
        RpcChannelList channels = SessionRpcCalls.Channels(session.Store, client);
        RpcChannelPeers scm = channels.Channels[0].Peers!;
        Assert.Equal((2L, 1L), (scm.Linked, scm.Unresolved[RpcPeerState.NoSend]));
        Assert.Equal((1_960, (ProcessInstanceId?)host, 2L), (scm.Processes[0].ProcessId, scm.Processes[0].Instance, scm.Processes[0].Calls));
        Assert.Equal("served by PID 1960 (2 of 3)", scm.Describe(RpcCallSide.Client, 3, culture));
        Assert.Equal(
            "other end unresolved: not completed, so no window to follow",
            channels.Channels[1].Peers!.Describe(RpcCallSide.Client, 1, culture));
        RpcChannelSummary served = Assert.Single(SessionRpcCalls.Channels(session.Store, host).Channels);
        Assert.Equal("called by PID 400 (2 of 3)", served.Peers!.Describe(RpcCallSide.Server, 3, culture));

        // A call's row names its other end and the key of the call there, which opens that call on its own channel.
        string channel = RpcChannelKeys.Channel(client, RpcCallSide.Client, ServiceControl);
        RpcCallPage page = SessionRpcCalls.Calls(session.Store, channel);
        Assert.Equal([1_960, (int?)null, 1_960], page.Calls.Select(row => row.OtherEnd?.Process?.ProcessId));
        Assert.Equal(RpcPeerState.NoSend, page.Calls[1].OtherEnd!.Unresolved);
        Assert.True(RpcChannelKeys.TryParseCall(page.Calls[0].OtherEnd!.CallKey!, out string otherChannel, out var otherCall));
        Assert.Equal((RpcChannelKeys.Channel(host, RpcCallSide.Server, ServiceControl), 50UL), (otherChannel, otherCall.Ordinal));
        RpcCallRow reached = Assert.Single(
            SessionRpcCalls.Calls(session.Store, otherChannel).Calls,
            row => row.Key == page.Calls[0].OtherEnd!.CallKey);
        Assert.Equal((400, page.Calls[0].Key), (reached.OtherEnd!.Process!.ProcessId, reached.OtherEnd.CallKey));

        // Under a brush holding only the first call, the channel counts only its link.
        RpcChannelPeers scoped = SessionRpcCalls.Channels(session.Store, client, new TimeRange(0, 150)).Channels
            .Single(summary => summary.Interface == ServiceControl).Peers!;
        Assert.Equal((1L, 0), (scoped.Linked, scoped.Unresolved.Count));

        // A capture that collected no ALPC names no other end at all, rather than an unresolved one for every call.
        using var plain = new TemporarySession();
        Publish(plain.Store, Calls());
        (ProcessInstanceId plainClient, _) = Instances(plain.Store);
        Assert.All(SessionRpcCalls.Channels(plain.Store, plainClient).Channels, summary => Assert.Null(summary.Peers));
    }

    private static ObservationRowV1[] Calls() =>
    [
        Lifecycle(1, ObservationKind.Create, 400, 1),
        Lifecycle(2, ObservationKind.Inventory, 1_960, 2),
        RpcCall(100, ObservationKind.RequestStart, Direction.Outbound, 400, 10, Activity(1), ServiceControl),
        RpcCall(120, ObservationKind.RequestEnd, Direction.Outbound, 400, 11, Activity(1), status: 0),
        RpcCall(200, ObservationKind.RequestStart, Direction.Outbound, 400, 20, Activity(2), ServiceControl),
        RpcCall(210, ObservationKind.RequestEnd, Direction.Outbound, 400, 21, Activity(2), status: 0),
        RpcCall(300, ObservationKind.RequestStart, Direction.Outbound, 400, 30, Activity(3), ServiceControl),
        RpcCall(340, ObservationKind.RequestEnd, Direction.Outbound, 400, 31, Activity(3), status: 5),
        RpcCall(400, ObservationKind.RequestStart, Direction.Outbound, 400, 40, Activity(4), Unnamed),
        RpcCall(105, ObservationKind.RequestStart, Direction.Inbound, 1_960, 50, Activity(11), ServiceControl),
        RpcCall(110, ObservationKind.RequestEnd, Direction.Inbound, 1_960, 51, Activity(11), status: 0),
        RpcCall(205, ObservationKind.RequestStart, Direction.Inbound, 1_960, 52, Activity(12), ServiceControl),
        RpcCall(207, ObservationKind.RequestEnd, Direction.Inbound, 1_960, 53, Activity(12), status: 0),
        RpcCall(305, ObservationKind.RequestStart, Direction.Inbound, 1_960, 54, Activity(13), ServiceControl),
        RpcCall(330, ObservationKind.RequestEnd, Direction.Inbound, 1_960, 55, Activity(13), status: 5),
    ];

    private static (ProcessInstanceId Client, ProcessInstanceId Host) Instances(SessionStore store)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(store);
        return (overview.Nodes.Single(node => node.ProcessId == 400).Id, overview.Nodes.Single(node => node.ProcessId == 1_960).Id);
    }

    private static Guid Activity(int number) => new(number, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 1);
}
