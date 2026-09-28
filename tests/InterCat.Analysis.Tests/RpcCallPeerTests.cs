using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.IncrementalDerivationTests;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Analysis.Tests;

/// <summary>
/// A client call's other end: the server call its one ALPC send's one receive began on the receiving thread, checked by
/// interface and procedure, and every other shape stated with its reason (`contracts/operations-v1.md` §5c, ADR-034).
/// </summary>
public sealed class RpcCallPeerTests
{

    private static readonly Guid ServiceControl = Guid.Parse("367abb81-9844-35f1-ad32-98f038001003");

    /// <summary>The client processes of the unresolved shapes, in the order they are built.</summary>
    private static readonly int[] ReasonProcesses = [401, 402, 404, 406, 409, 411, 413, 415, 417, 419];

    /// <summary>5 ms on the test clock, whose tick is 100 ns.</summary>
    private const long Window = 50_000;

    [Fact(DisplayName = "§7.4: a client call is served by the call its one send's one receive began on the receiving thread, checked by interface and procedure")]
    public void AClientCallIsServedThroughItsOneMessage()
    {
        var chain = new Chain();
        chain.Served(start: 1_000, client: 400, server: 500, message: 77);

        // A reused id elsewhere is no key: the same id sent by another process, and received in the client's own
        // process and after the call stopped, changes nothing (ADR-034's second decision).
        chain.Message(1_050, ObservationKind.Send, process: 700, thread: 701, message: 77);
        chain.Message(1_060, ObservationKind.Receive, process: 400, thread: 402, message: 77);
        chain.Message(900_000, ObservationKind.Receive, process: 800, thread: 801, message: 77);

        using var session = new TemporarySession();
        (RpcPeerIndex peers, SegmentReaderV1[] segments) = chain.Derive(session.Store);

        RpcCallGroup client = ClientGroup(peers, 400);
        RpcCallGroup server = peers.Calls.Groups.Single(group => group.ProcessId == 500 && group.Side == RpcCallSide.Server);
        Assert.Equal((1L, 1L), (peers.CountsOf(client).Calls, peers.CountsOf(client).Served));
        Assert.Empty(peers.CountsOf(client).Unresolved);
        RpcPeerTally servedBy = Assert.Single(peers.PeersOf(client));
        Assert.Equal((500, (Guid?)ServiceControl, 1L), (servedBy.ProcessId, servedBy.Interface, servedBy.Calls));
        Assert.True(servedBy.Process.IsBound);
        RpcPeerTally served = Assert.Single(peers.PeersOf(server));
        Assert.Equal((400, 1L), (served.ProcessId, served.Calls));

        (RpcPeerState state, RpcCall? other) = peers.PeerOf(client, 0, segments);
        Assert.Equal(RpcPeerState.Served, state);
        Assert.Equal((500, RpcCallSide.Server, (long?)7), (other!.ProcessId, other.Side, other.Procedure));
        Assert.Equal((2L, 3L), (peers.AlpcSends, peers.AlpcReceives));
    }

    [Fact(DisplayName = "§7.4: every other shape leaves a call's other end unresolved with its reason, and a server call on another thread is never a candidate")]
    public void EveryOtherShapeIsStatedWithItsReason()
    {
        var chain = new Chain();

        // Each call runs on a thread and with a message id of its own, a million ticks after the one before.
        chain.Client(1_000_000, 100_000, process: 401, procedure: 7);
        chain.Client(2_000_000, 100_000, process: 402, procedure: 7);
        chain.Message(2_000_010, ObservationKind.Send, 402, 403, 2);
        chain.Message(2_000_020, ObservationKind.Send, 402, 403, 3);
        chain.Client(3_000_000, 100_000, process: 404, procedure: 7);
        chain.Message(3_000_010, ObservationKind.Send, 404, 405, message: null);
        chain.Client(4_000_000, 100_000, process: 406, procedure: 7);
        chain.Message(4_000_010, ObservationKind.Send, 406, 407, 4);
        chain.Message(4_000_020, ObservationKind.Receive, 406, 408, 4);
        chain.Client(5_000_000, 100_000, process: 409, procedure: 7);
        chain.Message(5_000_010, ObservationKind.Send, 409, 410, 5);
        chain.Message(5_000_020, ObservationKind.Receive, 500, 501, 5);
        chain.Message(5_000_030, ObservationKind.Receive, 600, 601, 5);

        // The receive reached a thread that began nothing; the server call right after it was on another thread.
        chain.Client(6_000_000, 100_000, process: 411, procedure: 7);
        chain.Message(6_000_010, ObservationKind.Send, 411, 412, 6);
        chain.Message(6_000_020, ObservationKind.Receive, 500, 502, 6);
        chain.Server(6_000_021, 6_000_040, process: 500, thread: 503, procedure: 7);

        // The receiving thread began a call, but more than 5 ms after the receive.
        chain.Client(7_000_000, 100_000, process: 413, procedure: 7);
        chain.Message(7_000_010, ObservationKind.Send, 413, 414, 7);
        chain.Message(7_000_020, ObservationKind.Receive, 500, 504, 7);
        chain.Server(7_000_021 + Window, 7_000_021 + Window + 10, process: 500, thread: 504, procedure: 7);

        // The server call it reached names no procedure, and one names another.
        chain.Client(8_000_000, 100_000, process: 415, procedure: 7);
        chain.Message(8_000_010, ObservationKind.Send, 415, 416, 8);
        chain.Message(8_000_020, ObservationKind.Receive, 500, 505, 8);
        chain.Server(8_000_030, 8_000_040, process: 500, thread: 505, procedure: null);
        chain.Client(9_000_000, 100_000, process: 417, procedure: 7);
        chain.Message(9_000_010, ObservationKind.Send, 417, 418, 9);
        chain.Message(9_000_020, ObservationKind.Receive, 500, 506, 9);
        chain.Server(9_000_030, 9_000_040, process: 500, thread: 506, procedure: 8);

        // A call with no stop has no window at all.
        chain.Client(10_000_000, stopAfter: null, process: 419, procedure: 7);
        chain.Message(10_000_010, ObservationKind.Send, 419, 420, 10);

        using var session = new TemporarySession();
        (RpcPeerIndex peers, SegmentReaderV1[] segments) = chain.Derive(session.Store);

        RpcPeerState StateOf(int process) => peers.PeerOf(ClientGroup(peers, process), 0, segments).State;
        Assert.Equal(
            [
                RpcPeerState.NoSend, RpcPeerState.SeveralSends, RpcPeerState.NoMessageId, RpcPeerState.NoReceive,
                RpcPeerState.SeveralReceives, RpcPeerState.NoServerCall, RpcPeerState.NoServerCall,
                RpcPeerState.CannotCheck, RpcPeerState.Conflicting, RpcPeerState.NotCompleted,
            ],
            ReasonProcesses.Select(StateOf));
        Assert.All(
            peers.Calls.Groups.Where(group => group.Side == RpcCallSide.Server),
            group => Assert.Empty(peers.PeersOf(group)));
    }

    [Fact(DisplayName = "§7.4: a server call two client calls reach is neither's")]
    public void AServerCallReachedTwiceLinksNeither()
    {
        var chain = new Chain();
        chain.Client(1_000, 100_000, process: 400, procedure: 7);
        chain.Message(1_010, ObservationKind.Send, 400, 401, 1);
        chain.Client(1_000, 100_000, process: 600, procedure: 7);
        chain.Message(1_011, ObservationKind.Send, 600, 601, 2);
        chain.Message(1_020, ObservationKind.Receive, 500, 501, 1);
        chain.Message(1_021, ObservationKind.Receive, 500, 501, 2);
        chain.Server(1_030, 1_040, process: 500, thread: 501, procedure: 7);

        using var session = new TemporarySession();
        (RpcPeerIndex peers, SegmentReaderV1[] segments) = chain.Derive(session.Store);

        Assert.All([400, 600], process => Assert.Equal(
            RpcPeerState.ServerCallShared,
            peers.PeerOf(ClientGroup(peers, process), 0, segments).State));
        Assert.Empty(peers.PeersOf(peers.Calls.Groups.Single(group => group.Side == RpcCallSide.Server)));
    }

    [Fact(DisplayName = "§7.4: a capture that collected no ALPC resolves no call's other end, and says so of each")]
    public void WithoutAlpcNoOtherEndIsResolved()
    {
        var chain = new Chain();
        chain.Client(1_000, 100_000, process: 400, procedure: 7);
        chain.Server(1_030, 1_040, process: 500, thread: 501, procedure: 7);

        using var session = new TemporarySession();
        (RpcPeerIndex peers, _) = chain.Derive(session.Store);

        RpcPeerCounts counts = peers.CountsOf(ClientGroup(peers, 400));
        Assert.Equal((0L, 1L), (counts.Served, counts.Unresolved[RpcPeerState.NoAlpcEvidence]));
        Assert.Equal(0, peers.AlpcSends + peers.AlpcReceives);
    }

    private static RpcCallGroup ClientGroup(RpcPeerIndex peers, int process) =>
        peers.Calls.Groups.Single(group => group.ProcessId == process && group.Side == RpcCallSide.Client);

    /// <summary>A generation's RPC calls and ALPC messages, with the fields each carries.</summary>
    private sealed class Chain
    {
        private readonly List<ObservationRowV1> rows = [];
        private readonly List<SourceFieldRowV1> fields = [];
        private readonly HashSet<int> processes = [];
        private ulong ordinal;
        private int activity;

        /// <summary>
        /// One served call: the client's start, its send, the server's receive, the server call and its stop, and the
        /// client's stop, each well inside the others.
        /// </summary>
        public void Served(long start, int client, int server, long message)
        {
            Client(start, 100_000, client, procedure: 7);
            Message(start + 10, ObservationKind.Send, client, client + 1, message);
            Message(start + 20, ObservationKind.Receive, server, server + 1, message);
            Server(start + 30, start + 40, server, server + 1, procedure: 7);
        }

        /// <summary>A client call on the process's thread <c>process + 1</c>, and its stop <paramref name="stopAfter"/> later.</summary>
        public void Client(long start, long? stopAfter, int process, long? procedure) =>
            Call(start, stopAfter, process, process + 1, Direction.Outbound, procedure);

        public void Server(long start, long stop, int process, int thread, long? procedure) =>
            Call(start, stop - start, process, thread, Direction.Inbound, procedure);

        public void Message(long ticks, ObservationKind kind, int process, int thread, long? message)
        {
            Exists(process);
            ObservationRowV1 row = Alpc(ticks, kind, process, thread, ++ordinal);
            rows.Add(row);
            if (message is { } id)
            {
                fields.Add(Field(row, SourceField.AlpcMessageId, id));
            }
        }

        public (RpcPeerIndex Peers, SegmentReaderV1[] Segments) Derive(SessionStore store)
        {
            Publish(store, [.. rows.OrderBy(row => row.NativeTicks).ThenBy(row => row.RawRecordOrdinal)], fields: fields);
            (SegmentReaderV1[] observations, SegmentReaderV1[] fieldSegments) = SegmentsOf(store);
            ProcessInstanceIndex instances = ProcessInstanceIndex.Derive(observations, TestClock, fieldSegments);
            RpcCallIndex calls = RpcCallIndex.Derive(observations, fieldSegments, instances, TestClock);
            return (RpcPeerIndex.Derive(calls, observations, fieldSegments), observations);
        }

        private void Call(long start, long? duration, int process, int thread, Direction direction, long? procedure)
        {
            Exists(process);
            Guid id = new(++activity, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 2);
            ObservationRowV1 opened = RpcCall(start, ObservationKind.RequestStart, direction, process, ++ordinal, id, ServiceControl)
                with { HeaderThreadId = thread };
            rows.Add(opened);
            if (procedure is { } number)
            {
                fields.Add(Field(opened, SourceField.RpcProcedureNumber, number));
            }

            if (duration is { } after)
            {
                rows.Add(RpcCall(start + after, ObservationKind.RequestEnd, direction, process, ++ordinal, id, status: 0)
                    with { HeaderThreadId = thread });
            }
        }

        /// <summary>Every process a record names is created first, so every call binds to an instance.</summary>
        private void Exists(int process)
        {
            if (processes.Add(process))
            {
                rows.Add(Lifecycle(1, ObservationKind.Create, process, ++ordinal));
            }
        }
    }
}
