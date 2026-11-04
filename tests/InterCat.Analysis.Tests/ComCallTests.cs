using System.Globalization;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.IncrementalDerivationTests;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Analysis.Tests;

/// <summary>
/// One generation's COM calls under `com-call-link-v1` (`contracts/com-calls-v1.md`, ADR-079): COM-Perf's records paired by
/// process, side and trace number; each joined to the RPC call carrying it - at the client the one of its activity, at the
/// server the one around it on its thread; and each client call linked to the one server call carrying its activity, which
/// relates the client's RPC call to the RPC call that served it - every other shape stated with its reason.
/// </summary>
public sealed class ComCallTests
{
    private static readonly Guid Dispatch = Guid.Parse("00020400-0000-0000-c000-000000000046");

    [Fact(DisplayName = "P7: a client COM call is linked to the one server COM call carrying its activity, each naming the other's process and the served one inside it, and so its RPC call to the RPC call that served it")]
    public void AClientCallIsLinkedToTheCallThatServedIt()
    {
        var exchanges = new Exchanges();
        exchanges.Call(1_000, client: 400, server: 700);
        using var session = new TemporarySession();
        (ComCallIndex index, SegmentReaderV1[] segments) = exchanges.Derive(session.Store);

        int client = Position(index, ComCallSide.Client, 400);
        int server = Position(index, ComCallSide.Server, 700);
        ComCall made = index.Calls[client];
        ComCall served = index.Calls[server];
        Assert.Equal((ComCallState.Completed, ComCallState.Completed), (made.State, served.State));
        Assert.Equal(((int?)700, (int?)400), (made.PeerProcessId, served.PeerProcessId));
        Assert.Equal((ComLinkState.Linked, server, ComLinkState.Linked, client), (made.Link, made.OtherEnd, served.Link, served.OtherEnd));

        // The client's carrier is the RPC call of its activity, the server's the one around it on its thread, and through
        // the COM calls the first is related to the second: the RPC calls' own records share no id.
        Assert.Equal((ComCarrierState.Carried, ComCarrierState.Carried), (made.Carrier, served.Carrier));
        RpcCall caller = index.CarrierOf(client, segments)!;
        RpcCall callee = index.CarrierOf(server, segments)!;
        Assert.Equal((400, RpcCallSide.Client, 700, RpcCallSide.Server), (caller.ProcessId, caller.Side, callee.ProcessId, callee.Side));
        Assert.NotEqual(caller.ActivityId, callee.ActivityId);
        Assert.Equal(new ComRpcLink(client, server, made.CarrierCall, served.CarrierCall), Assert.Single(index.RpcLinks));
        Assert.Equal((0x8000_4002L, 0L), (made.Hresult!.Value, index.OtherComRecords));
    }

    [Fact(DisplayName = "P4: a COM call is carried by one RPC call - at the client the one of its activity, at the server the one around it on its thread - and one naming another procedure, none or two carry nothing, each with its reason")]
    public void ACallIsCarriedByOneRpcCall()
    {
        var exchanges = new Exchanges();
        exchanges.Call(1_000, client: 401, server: 701, clientProcedure: 5);
        exchanges.Call(2_000_000, client: 402, server: 702, serverRpc: false);
        exchanges.Call(4_000_000, client: 403, server: 703, serverRpcTwice: true);
        exchanges.Call(6_000_000, client: 404, server: 704, clientRpc: false);

        // Two server calls inside one RPC call on one thread are each carried by it, and so it relates neither client's RPC
        // call: which it served cannot be told.
        exchanges.Call(8_000_000, client: 405, server: 705, serverRpcStop: 8_001_700);
        exchanges.Call(8_001_000, client: 406, server: 705, serverRpc: false);
        using var session = new TemporarySession();
        (ComCallIndex index, _) = exchanges.Derive(session.Store);
        Assert.Equal(index.Calls[Position(index, ComCallSide.Server, 705)].CarrierCall,
            index.Calls.Last(call => call is { Side: ComCallSide.Server, ProcessId: 705 }).CarrierCall);

        // A carrier naming another procedure conflicts; none, or two around the call on its thread, carry nothing.
        Assert.Equal(ComCarrierState.Conflicting, index.Calls[Position(index, ComCallSide.Client, 401)].Carrier);
        Assert.Equal(ComCarrierState.Carried, index.Calls[Position(index, ComCallSide.Server, 701)].Carrier);
        Assert.Equal(ComCarrierState.NoCarrier, index.Calls[Position(index, ComCallSide.Server, 702)].Carrier);
        Assert.Equal(ComCarrierState.SeveralCarriers, index.Calls[Position(index, ComCallSide.Server, 703)].Carrier);
        Assert.Equal(ComCarrierState.NoCarrier, index.Calls[Position(index, ComCallSide.Client, 404)].Carrier);
        Assert.All(index.Calls.Where(call => call.Carrier != ComCarrierState.Carried), call => Assert.Equal(-1, call.CarrierCall));

        // Each pair of ends is still linked by the client's activity, but only the pairs carried at both ends relate RPC calls.
        Assert.All(index.Calls, call => Assert.Equal(ComLinkState.Linked, call.Link));
        Assert.Empty(index.RpcLinks);
    }

    [Fact(DisplayName = "P7: a COM call's ends are never linked by an activity alone - a server in the client's own process, two server calls of one activity, ends that do not name each other, or a served call outside the client's link nothing, each with its reason")]
    public void AnActivityAloneLinksNothing()
    {
        var exchanges = new Exchanges();
        exchanges.Call(1_000, client: 410, server: 410);
        exchanges.Call(2_000_000, client: 411, server: 711, secondServer: 811);
        exchanges.Call(4_000_000, client: 412, server: 712, clientNames: 999);
        exchanges.Call(6_000_000, client: 413, server: 713, servedAfter: true);
        exchanges.Call(8_000_000, client: 414, server: 714, serverActivity: true);
        exchanges.Call(10_000_000, client: 415, server: 715, clientTwice: true);
        exchanges.Call(12_000_000, client: 416, server: 716, serverNames: 998);
        using var session = new TemporarySession();
        (ComCallIndex index, _) = exchanges.Derive(session.Store);

        Assert.Equal(ComLinkState.WithinOneProcess, index.Calls.Single(call => call is { Side: ComCallSide.Client, ProcessId: 410 }).Link);
        Assert.Equal(ComLinkState.SeveralServedCalls, index.Calls[Position(index, ComCallSide.Client, 411)].Link);
        Assert.Equal(ComLinkState.PeerMismatch, index.Calls[Position(index, ComCallSide.Client, 412)].Link);
        Assert.Equal(ComLinkState.NotInside, index.Calls[Position(index, ComCallSide.Client, 413)].Link);
        Assert.Equal(ComLinkState.NoServedCall, index.Calls[Position(index, ComCallSide.Client, 414)].Link);
        Assert.Equal([ComLinkState.ServerCallShared, ComLinkState.ServerCallShared],
            index.Calls.Where(call => call is { Side: ComCallSide.Client, ProcessId: 415 }).Select(call => call.Link));
        Assert.Equal(ComLinkState.PeerMismatch, index.Calls[Position(index, ComCallSide.Client, 416)].Link);

        // A server call no client call is linked to is unreached; nothing relates any RPC call.
        Assert.All(index.Calls.Where(call => call.Side == ComCallSide.Server), call => Assert.Equal((ComLinkState.NotReached, -1), (call.Link, call.OtherEnd)));
        Assert.All(index.Calls.Where(call => call.Side == ComCallSide.Client), call => Assert.Equal(-1, call.OtherEnd));
        Assert.Empty(index.RpcLinks);
    }

    [Fact(DisplayName = "P8: a COM call's records pair by process, side and trace number - a stop without its start, a start without its stop, a record without a number and a number used again before its stop pair nothing")]
    public void RecordsPairByTheirTraceNumber()
    {
        var exchanges = new Exchanges();
        exchanges.Record(100, ObservationKind.RequestStart, Direction.Outbound, process: 420, trace: 1);
        exchanges.Record(200, ObservationKind.RequestEnd, Direction.Outbound, process: 420, trace: 1);
        exchanges.Record(300, ObservationKind.RequestEnd, Direction.Outbound, process: 421, trace: 2);
        exchanges.Record(400, ObservationKind.RequestStart, Direction.Outbound, process: 422, trace: 3);
        exchanges.Record(500, ObservationKind.RequestStart, Direction.Outbound, process: 423, trace: null);
        exchanges.Record(600, ObservationKind.RequestStart, Direction.Outbound, process: 424, trace: 4);
        exchanges.Record(700, ObservationKind.RequestStart, Direction.Outbound, process: 424, trace: 4);
        exchanges.Record(800, ObservationKind.RequestEnd, Direction.Outbound, process: 424, trace: 4);
        exchanges.Record(900, ObservationKind.RequestEnd, Direction.Outbound, process: 424, trace: 4);

        // The same number in another process, or at the other end, is another key.
        exchanges.Record(1_000, ObservationKind.RequestStart, Direction.Inbound, process: 420, trace: 1);
        exchanges.Record(1_100, ObservationKind.RequestEnd, Direction.Inbound, process: 420, trace: 1);
        using var session = new TemporarySession();
        (ComCallIndex index, _) = exchanges.Derive(session.Store);

        Assert.Equal(
            [
                (420, ComCallSide.Client, ComCallState.Completed), (421, ComCallSide.Client, ComCallState.StartNotObserved),
                (422, ComCallSide.Client, ComCallState.OpenAtCaptureEnd), (423, ComCallSide.Client, ComCallState.NoTraceNumber),
                (424, ComCallSide.Client, ComCallState.Ambiguous), (424, ComCallSide.Client, ComCallState.Ambiguous),
                (424, ComCallSide.Client, ComCallState.Ambiguous), (424, ComCallSide.Client, ComCallState.Ambiguous),
                (420, ComCallSide.Server, ComCallState.Completed),
            ],
            index.Calls.Select(call => (call.ProcessId, call.Side, call.State)));
        Assert.Equal((100L, 200L), (index.Calls[0].Start!.Value, index.Calls[0].Stop!.Value));
    }

    [Fact(DisplayName = "P7: FX-COM-001's layering run, its ledger made a session, has every call's two ends linked under com-call-link-v1 as measured, and every client RPC call carrying a COM call related to the RPC call that served it")]
    public void TheMeasuredRunLinksEveryCall()
    {
        using var session = new TemporarySession();
        (ComCallIndex index, SegmentReaderV1[] segments) = Ledger.Derive(session.Store);

        // Every call of the paced run at both ends, paired, carried and linked, as ADR-079 measured it.
        ComCall[] made = [.. index.Calls.Where(call => call.Side == ComCallSide.Client)];
        ComCall[] served = [.. index.Calls.Where(call => call.Side == ComCallSide.Server)];
        Assert.Equal((195, 195), (made.Length, served.Length));
        Assert.All(index.Calls, call => Assert.Equal((ComCallState.Completed, ComCarrierState.Carried, ComLinkState.Linked), (call.State, call.Carrier, call.Link)));
        Assert.Equal(64, made.Count(call => call.Interface == Dispatch && call.Method == 6 && call.PeerProcessId == Ledger.Server));

        // So each client RPC call carrying a COM call is related to the RPC call that served it, none reached twice, on one
        // interface and procedure; the client's other RPC calls, made before its first COM call, carry none.
        Assert.Equal(195, index.RpcLinks.Count);
        Assert.Equal(195, index.RpcLinks.Select(link => link.ServerRpcCall).Distinct().Count());
        Assert.All(index.RpcLinks, link =>
        {
            RpcCall caller = index.CarrierOf(link.ClientCall, segments)!;
            RpcCall callee = index.CarrierOf(link.ServerCall, segments)!;
            Assert.Equal((Ledger.Client, RpcCallSide.Client, Ledger.Server, RpcCallSide.Server), (caller.ProcessId, caller.Side, callee.ProcessId, callee.Side));
            Assert.Equal((caller.Interface, caller.Procedure), (callee.Interface, callee.Procedure));
        });
        Assert.Equal(195 + 16, index.RpcCalls.Groups.Where(group => group is { Side: RpcCallSide.Client, ProcessId: Ledger.Client }).Sum(group => group.Counts.Completed));
    }

    [Fact(DisplayName = "P7: under rpc-call-peer-v2 a client RPC call carrying a linked COM call is served by the RPC call carrying the server's, with no ALPC record, linked through COM - where ALPC serves it alike it is ALPC's, and where ALPC reaches another call it conflicts")]
    public void ACallCarryingALinkedComCallIsServed()
    {
        var exchanges = new Exchanges();
        exchanges.Call(1_000, client: 400, server: 700);
        using var session = new TemporarySession();
        (RpcPeerIndex peers, RpcCallIndex calls, SegmentReaderV1[] segments, SegmentReaderV1[] fieldSegments) = exchanges.Published(session.Store);

        // Without one ALPC record the call is served through the COM call it carries, at both ends; the generation's
        // other ends are never extended, being derived from every segment.
        Assert.Equal((false, true, true), (peers.CollectedAlpc, peers.HoldsComCalls, peers.FollowedOtherEnds));
        Assert.Null(peers.Extend(calls, segments, fieldSegments));

        // A metric reads the link as it reads ALPC's: the client's one peer is the server.
        ProcessInstanceId owner = calls.Processes.Instances.Single(instance => instance.ProcessId == 400).Id;
        MetricResult active = SessionMetrics.Evaluate(session.Store, new()
        {
            Basis = AnalysisBasis.LogicalOperations,
            Metric = Metric.ActivePeers,
            Owner = owner,
        });
        Assert.Equal((MetricUnavailableReason.None, (long?)1), (active.Unavailable, active.Value));
        RpcCallGroup client = Group(peers, 400, RpcCallSide.Client);
        RpcCallGroup server = Group(peers, 700, RpcCallSide.Server);
        (RpcPeerState state, RpcCall? other) = peers.PeerOf(client, 0, segments);
        Assert.Equal((RpcPeerState.Served, 700, RpcCallSide.Server), (state, other!.ProcessId, other.Side));
        Assert.Equal((true, true), (peers.LinkedThroughCom(client, 0), peers.LinkedThroughCom(server, 0)));
        Assert.Equal((1L, 700), (peers.CountsOf(client).Served, Assert.Single(peers.PeersOf(client)).ProcessId));
        Assert.Equal("rpc-call-peer-v2", RpcPeerIndex.PeerRule);

        // ALPC reaching the same server call keeps the call ALPC's; ALPC reaching another process's call conflicts with
        // the COM link, and links neither.
        var both = new Exchanges();
        both.Call(1_000, client: 401, server: 701, alpcTo: 701);
        both.Call(2_000_000, client: 402, server: 702, alpcTo: 802);

        // A client RPC call still open at capture end is served by nothing, as ALPC serves no call that is not completed,
        // though the COM calls it carries are linked.
        both.Call(4_000_000, client: 403, server: 703, clientRpcOpen: true);
        using var second = new TemporarySession();
        (RpcPeerIndex mixed, SegmentReaderV1[] mixedSegments) = both.DerivePeers(second.Store);
        Assert.True(mixed.CollectedAlpc);
        RpcCallGroup agreeing = Group(mixed, 401, RpcCallSide.Client);
        (RpcPeerState agreed, RpcCall? agreedWith) = mixed.PeerOf(agreeing, 0, mixedSegments);
        Assert.Equal((RpcPeerState.Served, 701, false), (agreed, agreedWith!.ProcessId, mixed.LinkedThroughCom(agreeing, 0)));
        RpcCallGroup conflicting = Group(mixed, 402, RpcCallSide.Client);
        Assert.Equal(RpcPeerState.Conflicting, mixed.PeerOf(conflicting, 0, mixedSegments).Item1);
        Assert.Equal(0L, mixed.CountsOf(Group(mixed, 702, RpcCallSide.Server)).Served);
        Assert.Equal(RpcPeerState.NotCompleted, mixed.PeerOf(Group(mixed, 403, RpcCallSide.Client), 0, mixedSegments).Item1);
        Assert.Equal(0L, mixed.CountsOf(Group(mixed, 703, RpcCallSide.Server)).Served);
    }

    [Fact(DisplayName = "I14: other ends followed in a generation holding no COM call record are not extended over an added segment holding one, which a full derivation answers")]
    public void AnAddedComCallIsNeverExtended()
    {
        var exchanges = new Exchanges();
        exchanges.RpcOnly(1_000, client: 400, server: 700);
        using var session = new TemporarySession();
        (RpcPeerIndex first, _, _, _) = exchanges.Published(session.Store);
        Assert.False(first.HoldsComCalls);

        exchanges.Call(2_000_000, client: 401, server: 701);
        (RpcPeerIndex full, RpcCallIndex calls, SegmentReaderV1[] segments, SegmentReaderV1[] fieldSegments) = exchanges.Published(session.Store);
        Assert.Null(first.Extend(calls, segments, fieldSegments));
        Assert.True(full.LinkedThroughCom(Group(full, 401, RpcCallSide.Client), 0));
    }

    [Fact(DisplayName = "P7: FX-COM-001's layering run, its ledger made a session, has every client RPC call carrying a COM call served under rpc-call-peer-v2 through COM, and the client's other RPC calls unresolved for want of ALPC")]
    public void TheMeasuredRunServesEveryCarriedCall()
    {
        using var session = new TemporarySession();
        (RpcPeerIndex peers, _) = Ledger.DerivePeers(session.Store);
        RpcCallGroup[] made = [.. peers.Calls.Groups.Where(group => group is { ProcessId: Ledger.Client, Side: RpcCallSide.Client })];
        RpcCallGroup[] served = [.. peers.Calls.Groups.Where(group => group is { ProcessId: Ledger.Server, Side: RpcCallSide.Server })];
        Assert.Equal((195L, 195L), (made.Sum(group => peers.CountsOf(group).Served), served.Sum(group => peers.CountsOf(group).Served)));
        Assert.All(made, group => Assert.All(peers.CountsOf(group).Unresolved.Keys, reason => Assert.Equal(RpcPeerState.NoAlpcEvidence, reason)));
        // The client's 16 other completed calls, and its thread's first, whose start and stop carry no activity id and are
        // each a call of its own.
        Assert.Equal(18L, made.Sum(group => peers.CountsOf(group).Unresolved.Values.Sum()));
        Assert.All(served.Concat(made), group => Assert.All(Enumerable.Range(0, (int)group.Counts.Calls), position =>
            Assert.Equal(peers.CountsOf(group, [position]).Served == 1, peers.LinkedThroughCom(group, position))));
    }

    [Fact(DisplayName = "I14: an operation index keeps that its generation holds COM call records and which calls are linked through one, with no ALPC record, and reads them back so")]
    public void AnOperationIndexKeepsLinksThroughCom()
    {
        var exchanges = new Exchanges();
        exchanges.Call(1_000, client: 400, server: 700);
        exchanges.Call(2_000_000, client: 403, server: 703, serverRpc: false);
        using var session = new TemporarySession();
        Indexed derived = exchanges.Index(session.Store);
        using var written = new MemoryStream();
        OperationIndex.Write(written, derived.SessionId, derived.Generation, derived.Segments, derived.FieldSegments,
            derived.Calls, derived.Peers, derived.Exchanges);
        OperationIndex read = OperationIndex.Read(written.ToArray(), derived.SessionId, TestClock, derived.Processes);
        RpcPeerIndex peers = read.Peers!;

        // Calls whose other ends are followed through COM call records are held whole, never streamed into an index.
        using var streamed = new MemoryStream();
        Assert.Null(OperationIndex.WriteFromSegments(streamed, derived.SessionId, derived.Generation, derived.Segments, derived.FieldSegments,
            derived.Observations, derived.Fields, TestClock, derived.Exchanges));
        Assert.Equal(0, streamed.Length);
        Assert.Equal((false, true), (peers.CollectedAlpc, peers.HoldsComCalls));
        foreach (RpcCallGroup group in derived.Peers.Calls.Groups)
        {
            RpcCallGroup again = peers.Calls.Groups.Single(other => other.ProcessId == group.ProcessId && other.Side == group.Side && other.Interface == group.Interface);
            for (int position = 0; position < group.Counts.Calls; position++)
            {
                Assert.Equal(derived.Peers.LinkedThroughCom(group, position), peers.LinkedThroughCom(again, position));
                Assert.Equal(derived.Peers.CountsOf(group, [position]).Served, peers.CountsOf(again, [position]).Served);
            }
        }

        Assert.True(peers.LinkedThroughCom(Group(peers, 400, RpcCallSide.Client), 0));
        Assert.Equal((RpcPeerState.NoAlpcEvidence, 1L), (Assert.Single(peers.CountsOf(Group(peers, 403, RpcCallSide.Client)).Unresolved).Key,
            peers.CountsOf(Group(peers, 403, RpcCallSide.Client)).Unresolved[RpcPeerState.NoAlpcEvidence]));
    }

    private static RpcCallGroup Group(RpcPeerIndex peers, int process, RpcCallSide side) =>
        peers.Calls.Groups.Single(group => group.ProcessId == process && group.Side == side);

    private static int Position(ComCallIndex index, ComCallSide side, int process)
    {
        int found = index.Calls.ToList().FindIndex(call => call.Side == side && call.ProcessId == process);
        Assert.True(found >= 0, $"no {side} call of process {process}");
        return found;
    }

    /// <summary>What an operation index is written from: a generation's calls, their other ends and its exchanges.</summary>
    private sealed record Indexed(
        Guid SessionId,
        long Generation,
        StoreDependency[] Segments,
        StoreDependency[] FieldSegments,
        SegmentReaderV1[] Observations,
        SegmentReaderV1[] Fields,
        ProcessInstanceIndex Processes,
        RpcCallIndex Calls,
        RpcPeerIndex Peers,
        HttpExchangeIndex Exchanges);

    /// <summary>A generation's COM calls and the RPC calls carrying them, with the fields each carries.</summary>
    private sealed class Exchanges
    {
        private readonly List<ObservationRowV1> rows = [];
        private readonly List<SourceFieldRowV1> fields = [];
        private ulong ordinal;
        private int activity;
        private long trace;

        /// <summary>
        /// One out-of-process call as FX-COM-001 measured it: the client's COM call and the RPC call of its activity starting
        /// inside it and stopping just after it; the server's RPC call, of its own activity, around the server's COM call on
        /// its thread, which carries the client's activity; each start naming the other end's process.
        /// </summary>
        public void Call(
            long start,
            int client,
            int server,
            long clientProcedure = 6,
            bool clientRpc = true,
            bool serverRpc = true,
            bool serverRpcTwice = false,
            int? secondServer = null,
            int? clientNames = null,
            int? serverNames = null,
            bool servedAfter = false,
            bool serverActivity = false,
            bool clientTwice = false,
            long? serverRpcStop = null,
            int? alpcTo = null,
            bool clientRpcOpen = false)
        {
            Guid called = Activity();
            long number = ++trace;
            Com(start, ObservationKind.RequestStart, Direction.Outbound, client, called, number, clientNames ?? server, client + 1);
            if (clientRpc)
            {
                Rpc(start + 5, clientRpcOpen ? null : start + 1_005, Direction.Outbound, client, client + 1, called, clientProcedure);
            }

            if (alpcTo is { } reached)
            {
                // The client RPC call's one ALPC send, received in the process named, whose thread then begins a server call
                // of the same interface and procedure: the chain ADR-034 follows.
                long message = 9_000 + number;
                Alpc(start + 10, ObservationKind.Send, client, client + 1, message);
                Alpc(start + 20, ObservationKind.Receive, reached, reached + 1, message);
                if (reached != server)
                {
                    Rpc(start + 30, start + 600, Direction.Inbound, reached, reached + 1, Activity(), clientProcedure);
                }
            }

            if (clientTwice)
            {
                // Another call of the same process carrying the same activity id, on another thread.
                long again = ++trace;
                Com(start + 1, ObservationKind.RequestStart, Direction.Outbound, client, called, again, server, client + 2);
                Com(start + 999, ObservationKind.RequestEnd, Direction.Outbound, client, called, again, null, client + 2);
            }

            long served = servedAfter ? start + 5_000 : start + 100;
            Guid carried = serverActivity ? Activity() : called;
            if (serverRpc)
            {
                Rpc(served - 10, serverRpcStop ?? served + 510, Direction.Inbound, server, server + 1, Activity(), 6);
            }

            if (serverRpcTwice)
            {
                Rpc(served - 20, served + 520, Direction.Inbound, server, server + 1, Activity(), 6);
            }

            Com(served, ObservationKind.RequestStart, Direction.Inbound, server, carried, number, serverNames ?? client, server + 1);
            Com(served + 500, ObservationKind.RequestEnd, Direction.Inbound, server, carried, number, null, server + 1);
            if (secondServer is { } another)
            {
                Com(served + 1, ObservationKind.RequestStart, Direction.Inbound, another, carried, number, client, another + 1);
                Com(served + 400, ObservationKind.RequestEnd, Direction.Inbound, another, carried, number, null, another + 1);
            }

            Com(start + 1_000, ObservationKind.RequestEnd, Direction.Outbound, client, called, number, null, client + 1, hresult: 0x8000_4002);
        }

        /// <summary>A client's RPC call and the server's that serves it, with no COM call record and no ALPC message.</summary>
        public void RpcOnly(long start, int client, int server)
        {
            Rpc(start, start + 1_000, Direction.Outbound, client, client + 1, Activity(), 6);
            Rpc(start + 100, start + 600, Direction.Inbound, server, server + 1, Activity(), 6);
        }

        /// <summary>One COM call record of its own, with a trace number or none.</summary>
        public void Record(long ticks, ObservationKind kind, Direction direction, int process, long? trace) =>
            Com(ticks, kind, direction, process, Activity(), trace, kind == ObservationKind.RequestStart ? process + 1_000 : null, process + 1);

        public (ComCallIndex Index, SegmentReaderV1[] Segments) Derive(SessionStore store)
        {
            (RpcCallIndex calls, SegmentReaderV1[] observations, SegmentReaderV1[] fieldSegments) = Calls(store);
            return (ComCallIndex.Derive(calls, observations, fieldSegments), observations);
        }

        public (RpcPeerIndex Peers, SegmentReaderV1[] Segments) DerivePeers(SessionStore store)
        {
            (RpcCallIndex calls, SegmentReaderV1[] observations, SegmentReaderV1[] fieldSegments) = Calls(store);
            return (RpcPeerIndex.Derive(calls, observations, fieldSegments), observations);
        }

        /// <summary>Publishes the calls added since the last publication as a generation of their own, and derives over every one so far.</summary>
        public (RpcPeerIndex Peers, RpcCallIndex Calls, SegmentReaderV1[] Segments, SegmentReaderV1[] Fields) Published(SessionStore store)
        {
            (RpcCallIndex calls, SegmentReaderV1[] observations, SegmentReaderV1[] fieldSegments) = Calls(store);
            return (RpcPeerIndex.Derive(calls, observations, fieldSegments), calls, observations, fieldSegments);
        }

        public Indexed Index(SessionStore store)
        {
            (RpcCallIndex calls, SegmentReaderV1[] observations, SegmentReaderV1[] fieldSegments) = Calls(store);
            return new(
                store.Current!.SessionId,
                store.Current.Generation,
                [.. observations.Select(segment => segment.Published!)],
                [.. fieldSegments.Select(segment => segment.Published!)],
                observations,
                fieldSegments,
                calls.Processes,
                calls,
                RpcPeerIndex.Derive(calls, observations, fieldSegments),
                HttpExchangeIndex.Derive(observations, fieldSegments, calls.Processes));
        }

        private (RpcCallIndex Calls, SegmentReaderV1[] Observations, SegmentReaderV1[] Fields) Calls(SessionStore store)
        {
            // Each publication holds what was added since the one before, in a generation of its own.
            Publish(store, [.. rows.OrderBy(row => row.NativeTicks).ThenBy(row => row.RawRecordOrdinal)], fields: fields);
            rows.Clear();
            fields.Clear();
            (SegmentReaderV1[] observations, SegmentReaderV1[] fieldSegments) = SegmentsOf(store);
            ProcessInstanceIndex instances = ProcessInstanceIndex.Derive(observations, TestClock, fieldSegments);
            return (RpcCallIndex.Derive(observations, fieldSegments, instances, TestClock), observations, fieldSegments);
        }

        private void Alpc(long ticks, ObservationKind kind, int process, int thread, long message)
        {
            ObservationRowV1 row = TestSessions.Alpc(ticks, kind, process, thread, ++ordinal);
            rows.Add(row);
            fields.Add(Field(row, SourceField.AlpcMessageId, message));
        }

        private Guid Activity() => new(++activity, 0x434f, 0x4d31, 0x80, 0, 0, 0, 0, 0, 0, 3);

        private void Com(long ticks, ObservationKind kind, Direction direction, int process, Guid activityId, long? number, int? peer, int thread, long hresult = 0)
        {
            bool start = kind == ObservationKind.RequestStart;
            ObservationRowV1 row = ComCall(ticks, kind, direction, process, ++ordinal, activityId, start ? Dispatch : null, start ? null : hresult, thread);
            rows.Add(row);
            if (number is { } traced)
            {
                fields.Add(Field(row, SourceField.ComCallTraceId, traced));
            }

            if (start)
            {
                if (peer is { } other)
                {
                    fields.Add(Field(row, SourceField.PeerProcessId, other));
                }

                fields.Add(Field(row, SourceField.ComMethodNumber, 6));
            }
        }

        private void Rpc(long start, long? stop, Direction direction, int process, int thread, Guid activityId, long procedure)
        {
            ObservationRowV1 opened = RpcCall(start, ObservationKind.RequestStart, direction, process, ++ordinal, activityId, Dispatch) with { HeaderThreadId = thread };
            rows.Add(opened);
            fields.Add(Field(opened, SourceField.RpcProcedureNumber, procedure));
            if (stop is { } stopped)
            {
                rows.Add(RpcCall(stopped, ObservationKind.RequestEnd, direction, process, ++ordinal, activityId, status: 0) with { HeaderThreadId = thread });
            }
        }
    }

    /// <summary>
    /// FX-COM-001's layering ledger made a session: each of the paced run's RPC call records and COM-Perf call records a row,
    /// its process by its role, its thread and activity by their ordinals, its reading on the test clock - whose tick is the
    /// ledger's 100 ns - and its trace number, other end, method and procedure as the fields the product's capture admits.
    /// COM-Perf's other events, which no capture admits, are left out.
    /// </summary>
    private static class Ledger
    {
        public const int Client = 400;
        public const int Server = 700;

        public static (ComCallIndex Index, SegmentReaderV1[] Segments) Derive(SessionStore store)
        {
            (RpcCallIndex calls, SegmentReaderV1[] observations, SegmentReaderV1[] fieldSegments) = Calls(store);
            return (ComCallIndex.Derive(calls, observations, fieldSegments), observations);
        }

        public static (RpcPeerIndex Peers, SegmentReaderV1[] Segments) DerivePeers(SessionStore store)
        {
            (RpcCallIndex calls, SegmentReaderV1[] observations, SegmentReaderV1[] fieldSegments) = Calls(store);
            return (RpcPeerIndex.Derive(calls, observations, fieldSegments), observations);
        }

        private static (RpcCallIndex Calls, SegmentReaderV1[] Observations, SegmentReaderV1[] Fields) Calls(SessionStore store)
        {
            string path = Path.Combine(RepositoryRoot(), "fixtures", "FX-COM-001", "evidence", "layering", "ledger.csv");
            string[][] lines = [.. File.ReadLines(path).Skip(1).Where(line => line.Length > 0).Select(line => line.Split(','))];
            long first = long.Parse(lines[0][0], CultureInfo.InvariantCulture);
            var rows = new List<ObservationRowV1>();
            var fields = new List<SourceFieldRowV1>();
            ulong ordinal = 0;
            foreach (string[] field in lines)
            {
                int process = field[1] == "client" ? Client : Server;
                int @event = int.Parse(field[3], CultureInfo.InvariantCulture);
                bool rpc = field[2] == "rpc";
                if (!rpc && @event is not (39 or 40 or 43 or 44))
                {
                    continue;
                }

                long ticks = long.Parse(field[0], CultureInfo.InvariantCulture) - first + 1_000;
                int thread = (process * 100) + int.Parse(field[4], CultureInfo.InvariantCulture);
                Guid? activity = field[5].Length == 0 ? null : new Guid(int.Parse(field[5], CultureInfo.InvariantCulture), 0x464c, 0x4430, 0x80, 0, 0, 0, 0, 0, 0, 1);
                bool start = rpc ? @event is 5 or 6 : @event is 39 or 43;
                Direction direction = rpc ? (@event is 5 or 7 ? Direction.Outbound : Direction.Inbound) : (@event is 39 or 40 ? Direction.Outbound : Direction.Inbound);
                ObservationKind kind = start ? ObservationKind.RequestStart : ObservationKind.RequestEnd;
                Guid? iid = start ? Guid.Parse(field[8]) : null;
                long? status = start ? null : long.Parse(field[11].AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                ObservationRowV1 row = rpc
                    ? RpcCall(ticks, kind, direction, process, ++ordinal, activity, iid, status) with { HeaderThreadId = thread }
                    : ComCall(ticks, kind, direction, process, ++ordinal, activity, iid, status, thread);
                rows.Add(row);
                if (rpc && start)
                {
                    fields.Add(Field(row, SourceField.RpcProcedureNumber, long.Parse(field[9], CultureInfo.InvariantCulture)));
                    fields.Add(Field(row, SourceField.RpcProtocolSequence, long.Parse(field[10], CultureInfo.InvariantCulture)));
                }
                else if (!rpc)
                {
                    fields.Add(Field(row, SourceField.ComCallTraceId, long.Parse(field[6], CultureInfo.InvariantCulture)));
                    if (start)
                    {
                        fields.Add(Field(row, SourceField.PeerProcessId, field[7] == "client" ? Client : Server));
                        fields.Add(Field(row, SourceField.ComMethodNumber, long.Parse(field[9], CultureInfo.InvariantCulture)));
                    }
                }
            }

            Publish(store, rows, fields: fields);
            (SegmentReaderV1[] observations, SegmentReaderV1[] fieldSegments) = SegmentsOf(store);
            ProcessInstanceIndex instances = ProcessInstanceIndex.Derive(observations, TestClock, fieldSegments);
            return (RpcCallIndex.Derive(observations, fieldSegments, instances, TestClock), observations, fieldSegments);
        }

        private static string RepositoryRoot()
        {
            DirectoryInfo? current = new(AppContext.BaseDirectory);
            while (current is not null && !File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
            {
                current = current.Parent;
            }

            return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the InterCat repository root.");
        }
    }
}
