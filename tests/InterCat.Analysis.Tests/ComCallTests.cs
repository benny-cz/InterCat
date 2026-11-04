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

    private static int Position(ComCallIndex index, ComCallSide side, int process)
    {
        int found = index.Calls.ToList().FindIndex(call => call.Side == side && call.ProcessId == process);
        Assert.True(found >= 0, $"no {side} call of process {process}");
        return found;
    }

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
            long? serverRpcStop = null)
        {
            Guid called = Activity();
            long number = ++trace;
            Com(start, ObservationKind.RequestStart, Direction.Outbound, client, called, number, clientNames ?? server, client + 1);
            if (clientRpc)
            {
                Rpc(start + 5, start + 1_005, Direction.Outbound, client, client + 1, called, clientProcedure);
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

        /// <summary>One COM call record of its own, with a trace number or none.</summary>
        public void Record(long ticks, ObservationKind kind, Direction direction, int process, long? trace) =>
            Com(ticks, kind, direction, process, Activity(), trace, kind == ObservationKind.RequestStart ? process + 1_000 : null, process + 1);

        public (ComCallIndex Index, SegmentReaderV1[] Segments) Derive(SessionStore store)
        {
            Publish(store, [.. rows.OrderBy(row => row.NativeTicks).ThenBy(row => row.RawRecordOrdinal)], fields: fields);
            (SegmentReaderV1[] observations, SegmentReaderV1[] fieldSegments) = SegmentsOf(store);
            ProcessInstanceIndex instances = ProcessInstanceIndex.Derive(observations, TestClock, fieldSegments);
            RpcCallIndex calls = RpcCallIndex.Derive(observations, fieldSegments, instances, TestClock);
            return (ComCallIndex.Derive(calls, observations, fieldSegments), observations);
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

        private void Rpc(long start, long stop, Direction direction, int process, int thread, Guid activityId, long procedure)
        {
            ObservationRowV1 opened = RpcCall(start, ObservationKind.RequestStart, direction, process, ++ordinal, activityId, Dispatch) with { HeaderThreadId = thread };
            rows.Add(opened);
            fields.Add(Field(opened, SourceField.RpcProcedureNumber, procedure));
            rows.Add(RpcCall(stop, ObservationKind.RequestEnd, direction, process, ++ordinal, activityId, status: 0) with { HeaderThreadId = thread });
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
            RpcCallIndex calls = RpcCallIndex.Derive(observations, fieldSegments, instances, TestClock);
            return (ComCallIndex.Derive(calls, observations, fieldSegments), observations);
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
