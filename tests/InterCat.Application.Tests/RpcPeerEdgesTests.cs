using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// The graph's RPC edges: a process and the process that served its calls, joined by calls linked through ALPC, counted
/// by the call records at both ends and drawn only when the capture collected ALPC (`contracts/operations-v1.md` §5c).
/// </summary>
public sealed class RpcPeerEdgesTests
{
    private static readonly Guid ServiceControl = Guid.Parse("367abb81-9844-35f1-ad32-98f038001003");

    [Fact(DisplayName = "§7.4: the overview joins a caller and the process that served it by an RPC edge of the linked calls' records")]
    public void TheOverviewDrawsAnRpcEdge()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = LinkedCalls();
        Publish(session.Store, rows, fields: fields, coverage: RpcLedger(alpc: true));

        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        ProcessInstanceId client = overview.Nodes.Single(node => node.ProcessId == 400).Id;
        ProcessInstanceId host = overview.Nodes.Single(node => node.ProcessId == 1_960).Id;
        CommunicationEdge edge = Assert.Single(overview.Edges, candidate => candidate.Mechanism == Mechanism.Rpc);

        // Two linked calls, each a client start and stop and a server start and stop; the ALPC records are not counted.
        Assert.Equal((8L, (long?)null, RelationStrength.Correlated), (edge.ObservationCount, edge.KnownBytes, edge.Strength));
        Assert.Equal(new HashSet<ProcessInstanceId> { client, host }, [edge.SourceId, edge.TargetId]);
        Assert.Contains(overview.Caveats, caveat => caveat.StartsWith("RPC edges join a process", StringComparison.Ordinal));

        // Under a brush holding only the first call, the edge counts only that link's records.
        SessionIntervalCounts scoped = SessionIntervalQuery.Count(session.Store, new TimeRange(0, 150));
        Assert.Equal(4, scoped.EdgeRecords[edge.Key]);
    }

    [Fact(DisplayName = "§7.4: a capture whose ledger names no ALPC draws no RPC edge, whatever its records hold")]
    public void WithoutCollectedAlpcNoRpcEdgeIsDrawn()
    {
        using var withoutAlpc = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = LinkedCalls();
        Publish(withoutAlpc.Store, rows, fields: fields, coverage: RpcLedger(alpc: false));
        Assert.DoesNotContain(SessionOverviewProjector.Project(withoutAlpc.Store).Edges, edge => edge.Mechanism == Mechanism.Rpc);

        using var legacy = new TemporarySession();
        Publish(legacy.Store, rows, fields: fields);
        Assert.DoesNotContain(SessionOverviewProjector.Project(legacy.Store).Edges, edge => edge.Mechanism == Mechanism.Rpc);
        Assert.DoesNotContain(SessionIntervalQuery.Count(legacy.Store, new TimeRange(0, 150)).EdgeRecords.Keys,
            key => key.StartsWith(RpcPeerEdges.KeyPrefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// A caller's three calls to the service control manager and the three the host served, the first and third linked by
    /// an ALPC message sent on the caller's thread and received on the thread that began the served call.
    /// </summary>
    private static (ObservationRowV1[] Rows, SourceFieldRowV1[] Fields) LinkedCalls()
    {
        ObservationRowV1[] messages =
        [
            Alpc(102, ObservationKind.Send, 400, 401, 60),
            Alpc(103, ObservationKind.Receive, 1_960, 1_961, 61),
            Alpc(302, ObservationKind.Send, 400, 401, 62),
            Alpc(303, ObservationKind.Receive, 1_960, 1_961, 63),
        ];
        ObservationRowV1[] rows =
        [
            Lifecycle(1, ObservationKind.Create, 400, 1),
            Lifecycle(2, ObservationKind.Inventory, 1_960, 2),
            RpcCall(100, ObservationKind.RequestStart, Direction.Outbound, 400, 10, Activity(1), ServiceControl),
            RpcCall(120, ObservationKind.RequestEnd, Direction.Outbound, 400, 11, Activity(1), status: 0),
            RpcCall(200, ObservationKind.RequestStart, Direction.Outbound, 400, 20, Activity(2), ServiceControl),
            RpcCall(210, ObservationKind.RequestEnd, Direction.Outbound, 400, 21, Activity(2), status: 0),
            RpcCall(300, ObservationKind.RequestStart, Direction.Outbound, 400, 30, Activity(3), ServiceControl),
            RpcCall(340, ObservationKind.RequestEnd, Direction.Outbound, 400, 31, Activity(3), status: 5),
            RpcCall(105, ObservationKind.RequestStart, Direction.Inbound, 1_960, 50, Activity(11), ServiceControl),
            RpcCall(110, ObservationKind.RequestEnd, Direction.Inbound, 1_960, 51, Activity(11), status: 0),
            RpcCall(205, ObservationKind.RequestStart, Direction.Inbound, 1_960, 52, Activity(12), ServiceControl),
            RpcCall(207, ObservationKind.RequestEnd, Direction.Inbound, 1_960, 53, Activity(12), status: 0),
            RpcCall(305, ObservationKind.RequestStart, Direction.Inbound, 1_960, 54, Activity(13), ServiceControl),
            RpcCall(330, ObservationKind.RequestEnd, Direction.Inbound, 1_960, 55, Activity(13), status: 5),
            .. messages,
        ];
        return (
            rows,
            [
                .. rows.Where(row => row is { Mechanism: Mechanism.Rpc, Kind: ObservationKind.RequestStart })
                    .Select(start => Field(start, SourceField.RpcProcedureNumber, 7)),
                .. messages.Select((message, index) => Field(message, SourceField.AlpcMessageId, 21 + (index / 2))),
            ]);
    }

    private static Guid Activity(int number) => new(number, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 1);
}
