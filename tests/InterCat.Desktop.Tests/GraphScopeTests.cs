using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>
/// What a published session's graph draws its relationships from, said in its own header (R21): briefly after its summary
/// in the ranked table's words, and in full on hover - admitted paired TCP, and RPC calls linked through ALPC where the
/// session has them, never every observation, which the timeline holds.
/// </summary>
public sealed class GraphScopeTests
{
    [Fact(DisplayName = "R21: a published session's graph says what its relationships are drawn from, briefly and in full")]
    public void TheGraphSaysWhatItDraws() => SingleThreadedContext.Run(async () =>
    {
        // Calls linked through ALPC are drawn beside paired TCP, and the graph says both.
        using var calls = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = LinkedRpcCalls();
        Publish(calls.Store, rows, fields: fields, coverage: RpcLedger(alpc: true));
        using (WorkspaceViewModel workspace = await Open(calls))
        {
            Assert.Contains(workspace.Snapshot.Edges, edge => edge.Mechanism == Mechanism.Rpc);
            Assert.EndsWith(" · paired TCP and RPC calls", workspace.GraphSummary, StringComparison.Ordinal);
            Assert.Equal(workspace.GraphSummary + ". The graph draws admitted paired TCP and RPC calls linked through ALPC; a "
                + "process's RPC calls are on its rung, and all other observed activity is in the timeline.", workspace.GraphScope);

            // A rung the graph is focused on says it too, after what it draws of the machine.
            ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 400);
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == client.GroupKey);
            Assert.True(workspace.Descend());
            await workspace.LayoutReady;
            Assert.Contains("processes drawn", workspace.GraphSummary, StringComparison.Ordinal);
            Assert.EndsWith(" · paired TCP and RPC calls", workspace.GraphSummary, StringComparison.Ordinal);
        }

        // Without ALPC the same calls draw no edge, and a graph with no relationship says only what its summary says.
        using var plain = new TemporarySession();
        Publish(plain.Store, rows, fields: fields, coverage: RpcLedger(alpc: false));
        using (WorkspaceViewModel workspace = await Open(plain))
        {
            Assert.Empty(workspace.Snapshot.Edges);
            Assert.Equal(workspace.GraphSummary, workspace.GraphScope);
            Assert.DoesNotContain("paired TCP", workspace.GraphSummary, StringComparison.Ordinal);
        }

        // A workspace that is not a published session's says nothing of what a capture's graph draws.
        using var synthetic = new WorkspaceViewModel(SyntheticWorkspace.Create(), "synthetic-generation");
        await synthetic.LayoutReady;
        Assert.NotEmpty(synthetic.Snapshot.Edges);
        Assert.Equal(synthetic.GraphSummary, synthetic.GraphScope);
        Assert.DoesNotContain("paired TCP", synthetic.GraphSummary, StringComparison.Ordinal);
    });

    private static async Task<WorkspaceViewModel> Open(TemporarySession session)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        await workspace.LayoutReady;
        return workspace;
    }
}
