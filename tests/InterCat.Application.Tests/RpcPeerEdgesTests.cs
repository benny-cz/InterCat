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

        // It names the rule that linked its calls, and rests on its own key, which opens them.
        Assert.Equal((RelationRule.RpcCallPeer, RpcPeerIndex.PeerRule), (edge.Rule, edge.Rule.ToString()));
        Assert.Equal([edge.Key], edge.Evidence);
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

    /// <summary>The service control manager's calls, linked through ALPC (<see cref="TestSessions.LinkedRpcCalls"/>).</summary>
    internal static (ObservationRowV1[] Rows, SourceFieldRowV1[] Fields) LinkedCalls() => TestSessions.LinkedRpcCalls();
}
