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
            SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store, pageSize: 4, cursor: cursor, resolveOwners: true, rpcKey: channel);
            Assert.False(page.RestartRequired);
            Assert.Equal(channel, page.RpcKey);
            Assert.All(page.Records, record => Assert.Equal(client, record.Owner!.Instance));
            seen.AddRange(page.Records.Select(record => record.Observation.RawRecordOrdinal));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal([10UL, 11UL, 20UL, 21UL, 30UL, 31UL], seen);

        RpcCallRow second = SessionRpcCalls.Calls(session.Store, channel).Calls[1];
        Assert.Equal([20UL, 21UL], SessionEvidenceQuery.Read(session.Store, rpcKey: second.Key)
            .Records.Select(record => record.Observation.RawRecordOrdinal));

        _ = Assert.Throws<ArgumentException>(() => SessionEvidenceQuery.Read(session.Store, channelKey: "tcp:x", rpcKey: channel));
        _ = Assert.Throws<ArgumentException>(() => SessionEvidenceQuery.Read(session.Store, rpcKey: "rpc:nonsense"));
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
        Assert.Equal((call, (TimeRange?)null, "Records of call at +1 s"), (scope.RpcKey, scope.Interval, scope.Description));
        Assert.False(scope.IsWholeSession);
        EvidenceScope wider = EvidenceScopes.Resolve(snapshot, rung with { Filters = [rung.Filters[0]] });
        Assert.Equal(channel, wider.RpcKey);
        Assert.StartsWith("Records of calls to svcctl", wider.Description, StringComparison.Ordinal);
    }

    /// <summary>
    /// A client, PID 400, calls the Service Control Manager three times - one call failing - and an unnamed interface once,
    /// never answered; the service host, PID 1960, raised the served side of the first three.
    /// </summary>
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
