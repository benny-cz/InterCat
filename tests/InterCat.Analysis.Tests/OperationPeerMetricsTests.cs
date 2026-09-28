using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Analysis.Tests;

/// <summary>
/// Peers of RPC calls on the logical-operations basis (`contracts/metrics-v1.md` §8a, revision 228): a call's other end is
/// the process of the call `rpc-call-peer-v1` links it to through ALPC, which answers peer(P,Q), grouping by peer,
/// participant(P), between(A,B) either way and a count of peers; a call no link reached is disclosed, never guessed.
/// </summary>
public sealed class OperationPeerMetricsTests
{
    private const int Caller = 400;
    private const int Other = 600;
    private const int Host = 500;
    private static readonly Guid ServiceControl = Guid.Parse("367abb81-9844-35f1-ad32-98f038001003");

    [Fact(DisplayName = "§8a: peer(P,Q) keeps the calls P made that Q served, and discloses the ones no link reached")]
    public void PeerKeepsTheCallsAnotherProcessServed()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows(), fields: Fields());
        (ProcessInstanceId caller, _, ProcessInstanceId host) = Instances(session.Store);

        MetricResult served = SessionMetrics.Evaluate(session.Store, Request(Metric.OperationsStarted) with { Owner = caller, Peer = host });
        Assert.Equal(2, served.Value);
        Assert.Equal(RpcPeerIndex.PeerRule, served.RelationRule);
        Assert.Equal(new Dictionary<ProcessBindingReason, long> { [ProcessBindingReason.CallNotLinked] = 1 }, served.UnresolvedCounterparts);
        Assert.Equal(new Dictionary<RpcPeerState, long> { [RpcPeerState.NoSend] = 1 }, served.UnlinkedCalls);
        Assert.Contains(served.Caveats, caveat => caveat.Contains("rpc-call-peer-v1", StringComparison.Ordinal));

        // The same filter measures those two calls' durations: a client call's, from its start to its stop.
        MetricResult durations = SessionMetrics.Evaluate(session.Store, Request(Metric.Duration) with
        {
            Owner = caller,
            Peer = host,
            DurationInterval = DurationInterval.ClientCall,
        });
        Assert.Equal(2, durations.Distribution!.Count);
    }

    [Fact(DisplayName = "§8a: grouped by peer, a process's calls rank by who is at their other end, and an unlinked call is unattributed with its reason")]
    public void GroupingByPeerRanksTheOtherEnds()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows(), fields: Fields());
        (ProcessInstanceId caller, ProcessInstanceId other, ProcessInstanceId host) = Instances(session.Store);

        // The caller's calls: two the host served, and one that sent no message.
        MetricResult calls = SessionMetrics.Evaluate(session.Store, Request(Metric.OperationsStarted) with
        {
            Owner = caller,
            Grouping = LaneGrouping.Peer,
        });
        Assert.Equal(3, calls.Value);
        MetricGroup hostGroup = Assert.Single(calls.Groups);
        Assert.Equal((host, (long?)2), (hostGroup.Process!.Id, hostGroup.Value));
        MetricGroup unlinked = Assert.Single(calls.Unattributed);
        Assert.Equal((ProcessBindingReason.CallNotLinked, (long?)1), (unlinked.Reason, unlinked.Value));
        Assert.Equal(new Dictionary<RpcPeerState, long> { [RpcPeerState.NoSend] = 1 }, calls.UnlinkedCalls);

        // The host's calls, by the client each served; the one no client call reached is not reached.
        MetricResult served = SessionMetrics.Evaluate(session.Store, Request(Metric.OperationsStarted) with
        {
            Owner = host,
            Grouping = LaneGrouping.Peer,
        });
        Assert.Equal(
            [(caller, 2L), (other, 1L)],
            served.Groups.Select(group => (group.Process!.Id, group.Value!.Value)));
        Assert.Equal(new Dictionary<RpcPeerState, long> { [RpcPeerState.NotReached] = 1 }, served.UnlinkedCalls);
    }

    [Fact(DisplayName = "§8a: participant(P) and between(A,B) keep the calls on either side of a link, and count a peer once")]
    public void ParticipantAndBetweenKeepBothSidesOfALink()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows(), fields: Fields());
        (ProcessInstanceId caller, ProcessInstanceId other, ProcessInstanceId host) = Instances(session.Store);

        // The host's four served calls, and the three client calls linked to them. The caller's unlinked call could be the
        // host's too, so it is disclosed.
        MetricResult participant = SessionMetrics.Evaluate(session.Store, Request(Metric.OperationsStarted) with { Participant = host });
        Assert.Equal(7, participant.Value);
        Assert.Equal(1, participant.UnresolvedCounterparts[ProcessBindingReason.CallNotLinked]);

        // Between the caller and the host, either way: the two linked calls on each side; each side's unlinked call is
        // disclosed.
        MetricResult between = SessionMetrics.Evaluate(session.Store, Request(Metric.OperationsStarted) with
        {
            Between = new([caller], [host], BetweenDirection.Either),
        });
        Assert.Equal(4, between.Value);
        Assert.Equal(
            new Dictionary<RpcPeerState, long> { [RpcPeerState.NoSend] = 1, [RpcPeerState.NotReached] = 1 },
            between.UnlinkedCalls);

        // The host served two processes, and one of its calls reached none: the count is a lower bound beside it.
        MetricResult peers = SessionMetrics.Evaluate(session.Store, Request(Metric.ActivePeers) with { Owner = host });
        Assert.Equal((2L, 3L, 1L), (peers.Value!.Value, peers.KnownContributions, peers.UnknownContributions));
        Assert.Equal(new Dictionary<RpcPeerState, long> { [RpcPeerState.NotReached] = 1 }, peers.UnlinkedCalls);
        Assert.Equal(1, SessionMetrics.Evaluate(session.Store, Request(Metric.ActivePeers) with { Owner = caller }).Value);
        _ = other;
    }

    private static MetricRequest Request(Metric metric) => new()
    {
        Basis = AnalysisBasis.LogicalOperations,
        Metric = metric,
    };

    /// <summary>
    /// The caller makes three calls to the host and the other client one. The caller's first and third and the other
    /// client's call send one ALPC message each on the calling thread, received on the thread that begins the host's
    /// call; the caller's second sends none, and the host serves one call on another thread that no client call reached.
    /// </summary>
    private static ObservationRowV1[] Rows()
    {
        ulong ordinal = 0;
        var rows = new List<ObservationRowV1>
        {
            Lifecycle(1, ObservationKind.Create, Caller, ++ordinal),
            Lifecycle(2, ObservationKind.Create, Other, ++ordinal),
            Lifecycle(3, ObservationKind.Create, Host, ++ordinal),
        };
        void Call(long start, long stop, Direction direction, int process, int thread, int activity) => rows.AddRange(
        [
            RpcCall(start, ObservationKind.RequestStart, direction, process, ++ordinal, Activity(activity), ServiceControl)
                with { HeaderThreadId = thread },
            RpcCall(stop, ObservationKind.RequestEnd, direction, process, ++ordinal, Activity(activity), status: 0)
                with { HeaderThreadId = thread },
        ]);
        void Message(long ticks, ObservationKind kind, int process, int thread) =>
            rows.Add(Alpc(ticks, kind, process, thread, ++ordinal));

        Call(100, 150, Direction.Outbound, Caller, Caller + 1, 1);
        Message(110, ObservationKind.Send, Caller, Caller + 1);
        Message(120, ObservationKind.Receive, Host, Host + 1);
        Call(130, 140, Direction.Inbound, Host, Host + 1, 11);

        Call(200, 250, Direction.Outbound, Caller, Caller + 1, 2);
        Call(230, 240, Direction.Inbound, Host, Host + 2, 12);

        Call(300, 350, Direction.Outbound, Caller, Caller + 1, 3);
        Message(310, ObservationKind.Send, Caller, Caller + 1);
        Message(320, ObservationKind.Receive, Host, Host + 1);
        Call(330, 340, Direction.Inbound, Host, Host + 1, 13);

        Call(400, 450, Direction.Outbound, Other, Other + 1, 4);
        Message(410, ObservationKind.Send, Other, Other + 1);
        Message(420, ObservationKind.Receive, Host, Host + 1);
        Call(430, 440, Direction.Inbound, Host, Host + 1, 14);
        return [.. rows.OrderBy(row => row.NativeTicks).ThenBy(row => row.RawRecordOrdinal)];
    }

    /// <summary>Every call start's procedure, and each message's id, one id per send and its receive.</summary>
    private static SourceFieldRowV1[] Fields()
    {
        ObservationRowV1[] rows = Rows();
        ObservationRowV1[] messages = [.. rows.Where(row => row.Mechanism == Mechanism.Alpc).OrderBy(row => row.NativeTicks)];
        return
        [
            .. rows.Where(row => row is { Mechanism: Mechanism.Rpc, Kind: ObservationKind.RequestStart })
                .Select(start => Field(start, SourceField.RpcProcedureNumber, 7)),
            .. messages.Select((message, index) => Field(message, SourceField.AlpcMessageId, 100 + (index / 2))),
        ];
    }

    private static (ProcessInstanceId Caller, ProcessInstanceId Other, ProcessInstanceId Host) Instances(SessionStore store)
    {
        MetricResult grouped = SessionMetrics.Evaluate(store, new MetricRequest
        {
            Basis = AnalysisBasis.SourceObservations,
            Metric = Metric.Observations,
            Grouping = LaneGrouping.InstanceOnly,
        });
        ProcessInstanceId Of(int pid) => grouped.Groups.Single(group => group.Process?.ProcessId == pid).Process!.Id;
        return (Of(Caller), Of(Other), Of(Host));
    }

    private static Guid Activity(int number) => new(number, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 3);
}
