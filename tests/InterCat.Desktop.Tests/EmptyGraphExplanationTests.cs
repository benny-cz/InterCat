using System.Globalization;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>
/// A real machine's graph is often empty: its TCP runs to other hosts, its UDP is not drawn, and its RPC calls are joined
/// to the process that served them only by a capture that collects ALPC. The window says so where the graph is empty -
/// in its header, beneath it, on its one node and in the relationship table - so an empty pane is never read as a
/// failure (§6.8).
/// </summary>
public sealed class EmptyGraphExplanationTests
{
    [Fact(DisplayName = "§6.8: a session whose TCP runs to other hosts says why its graph draws none, beneath it and wherever it is read")]
    public void AnEmptyGraphSaysWhy() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 300, 1) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 100 },
            Lifecycle(2, ObservationKind.Create, 400, 2) with { ResourceName = @"C:\Windows\svchost.exe", SessionRelativeTicks = 110 },
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 500, 300, 3)
                .Between("192.168.1.10:52000", "142.250.1.1:443") with { SessionRelativeTicks = 200 },
            Transfer(21, ObservationKind.Receive, AccountingSide.ReceiveSide, 900, 300, 4)
                .Between("192.168.1.10:52000", "142.250.1.1:443") with { SessionRelativeTicks = 210 },
            Transfer(22, ObservationKind.Send, AccountingSide.SendSide, 500, 300, 5)
                .Between("192.168.1.10:52001", "20.42.65.1:443") with { SessionRelativeTicks = 220 },
            RpcCall(40, ObservationKind.RequestStart, Direction.Outbound, 400, 6, Guid.NewGuid(), ServiceControlInterface)
                with { SessionRelativeTicks = 400 },
        ]);
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;

        string why = "No TCP connection was seen with a process of this session at each of its two ends. All 3 of its TCP "
            + "records belong to 2 connections to 2 other hosts. A process's connections are listed on its rung. RPC calls are "
            + "on their processes' rungs: a call is drawn to the process that served it only from a capture that collects "
            + "ALPC, as icat record --profile rpc-peers does.";
        Assert.Equal("2 processes · no relationship drawn · 2 TCP connections ran to other hosts", workspace.GraphSummary);
        Assert.Equal(workspace.GraphSummary + ". " + why, workspace.GraphScope);
        Assert.Equal(why, workspace.GraphEmptyExplanation);
        Assert.True(workspace.ShowsGraphEmptyExplanation);
        Assert.Equal("No relationship here. " + why, workspace.RelationshipsAbsent);

        // The one node the graph draws says the same of the processes it holds.
        GraphDisplayNode quiet = Assert.Single(workspace.GraphDisplay.Nodes);
        Assert.Equal(GraphNodeKind.Quiet, quiet.Kind);
        workspace.SelectGraphNode(quiet.Key);
        Assert.Equal("2 processes with no admitted relationship in this session, counted in one node rather than drawn one "
            + "by one. The ranked table lists each of them. " + why, workspace.SelectionSubtitle);

        // At a process's rung the graph draws that process, and its own rows list its connections: nothing is said beneath.
        ProcessNode browser = workspace.Snapshot.Processes.Single(node => node.ProcessId == 300);
        foreach (string key in new[] { browser.GroupKey, browser.Id.ToString() })
        {
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == key);
            Assert.True(workspace.Descend());
        }

        Assert.Null(workspace.GraphEmptyExplanation);
        Assert.False(workspace.ShowsGraphEmptyExplanation);
        while (workspace.CanAscend)
        {
            workspace.Ascend();
        }

        Assert.Equal(why, workspace.GraphEmptyExplanation);
    });

    [Fact(DisplayName = "§6.8: a graph that draws a relationship, or a workspace not projected from a session, says nothing beneath it")]
    public void AGraphWithARelationshipSaysNothingBeneathIt() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 200 },
            Transfer(21, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 2)
                .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 210 },
        ]);
        using WorkspaceViewModel paired = Open(session);
        await paired.LayoutReady;
        Assert.Null(paired.GraphEmptyExplanation);
        Assert.Null(paired.RelationshipsAbsent);
        Assert.Equal("2 processes · 1 relationship · paired TCP only", paired.GraphSummary);

        // A snapshot no session was projected for has no census to explain itself with, and keeps its own words.
        using var synthetic = new WorkspaceViewModel(SyntheticWorkspace.Create() with { Edges = [] }, "zero-edge-generation");
        await synthetic.LayoutReady;
        Assert.Null(synthetic.GraphEmptyExplanation);
        Assert.Equal("5 processes · no relationship drawn; observed records may still be in the timeline", synthetic.GraphSummary);
    });

    private static WorkspaceViewModel Open(TemporarySession session)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        return new(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
    }
}
