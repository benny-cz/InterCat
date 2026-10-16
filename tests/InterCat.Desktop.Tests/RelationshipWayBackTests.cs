using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>
/// The way back to a chosen relationship (§3.2, R15): E on it lists its records, and Enter or a double click opens it; Esc
/// or the breadcrumb back to the rung that way began chooses it there again and asks for the keyboard to its row, as the
/// way back to an aggregate's list chooses the process chosen there.
/// </summary>
public sealed class RelationshipWayBackTests
{
    [Fact(DisplayName = "§3.2: the way back from a relationship opened at the machine rung chooses it there again, its row asked for the keyboard - by Esc, by the breadcrumb, and forward and back")]
    public void TheWayBackFromAnOpenedRelationshipChoosesItAgain() => SingleThreadedContext.Run(async () =>
    {
        using var workspace = new WorkspaceViewModel(SyntheticWorkspace.Create(), "generation-1");
        await workspace.LayoutReady;
        List<int> asked = Asked(workspace);

        // Opened from its row to its channel, three rungs down: up from the channel is its process and the next its group,
        // neither choosing it, and the next the machine rung, where it is chosen again, its edge haloed and the inspector on
        // it.
        workspace.SelectedRelationship = Row(workspace, "edge.api-cache");
        Assert.True(workspace.OpenSelectedRelationship());
        Assert.Equal(4, workspace.Crumbs.Count);
        Assert.True(workspace.Ascend());
        Assert.True(workspace.Ascend());
        Assert.Null(workspace.SelectedRelationship);
        Assert.Empty(asked);
        Assert.True(workspace.Ascend());
        Assert.Equal("edge.api-cache", workspace.SelectedRelationship?.Key);
        Assert.Equal("edge.api-cache", workspace.HighlightedEdgeKey);
        Assert.Equal(workspace.SelectedRelationship!.Name, workspace.SelectionTitle);
        Assert.Equal([1], asked);

        // A double click on an edge opens it as its row does, chosen or not, and the breadcrumb's way back chooses it too.
        Assert.True(workspace.OpenGraphEdge("edge.browser-api"));
        Assert.Equal(3, workspace.Crumbs.Count);
        workspace.ReturnTo(0);
        Assert.Equal("edge.browser-api", workspace.SelectedRelationship?.Key);
        Assert.Equal([1, 2], asked);

        // Forward to its process again and back, it is chosen again, as back and forward retrace one way.
        Assert.True(workspace.GoForward());
        Assert.True(workspace.GoForward());
        Assert.Equal(3, workspace.Crumbs.Count);
        workspace.ReturnTo(0);
        Assert.Equal("edge.browser-api", workspace.SelectedRelationship?.Key);
        Assert.Equal([1, 2, 3], asked);

        // The crumb of the rung shown, chosen again, is no way back: what was chosen there since stays chosen.
        string process = workspace.GraphDisplay.NodeOf(Process(workspace, 9132).Id)!.Key;
        workspace.SelectGraphNode(process);
        workspace.ReturnTo(0);
        Assert.Equal((null, process), (workspace.SelectedRelationship, workspace.SelectedGraphNodeKey));
        Assert.Equal([1, 2, 3], asked);

        // A way down of its own replaces it: back from a group opened by its row, the group's row is chosen, as ever.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == "group.platform");
        Assert.True(workspace.Descend());
        Assert.True(workspace.Ascend());
        Assert.Equal((null, "group.platform"), (workspace.SelectedRelationship, workspace.SelectedRung?.Key));
        Assert.Equal([1, 2, 3], asked);
    });

    [Fact(DisplayName = "§3.2: E on a chosen relationship is a way down too: back from its records it is chosen there again, at the machine rung or a group's, and a way further down keeps the way back")]
    public void TheWayBackFromARelationshipsRecordsChoosesItAgain() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;
        List<int> asked = Asked(workspace);
        string paired = Between(workspace, 100, 200).Key;

        // E lists its channel's records; Esc comes back to it, and so does the breadcrumb.
        workspace.SelectedRelationship = Row(workspace, paired);
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.StartsWith("Paired TCP channel", workspace.EvidenceScopeText, StringComparison.Ordinal);
        Assert.True(workspace.Ascend());
        Assert.Equal(paired, workspace.SelectedRelationship?.Key);
        Assert.Equal([1], asked);
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        workspace.ReturnTo(0);
        Assert.Equal(paired, workspace.SelectedRelationship?.Key);
        Assert.Equal([1, 2], asked);

        // Opened to its channel, chosen again there and its records listed, a way further down: each rung keeps its own way
        // back, so it is chosen again at the channel's rung and again at the machine rung.
        Assert.True(workspace.OpenSelectedRelationship());
        Assert.Equal(4, workspace.Crumbs.Count);
        workspace.SelectedRelationship = Row(workspace, paired);
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.Equal(5, workspace.Crumbs.Count);
        Assert.True(workspace.Ascend());
        Assert.Equal((4, paired), (workspace.Crumbs.Count, workspace.SelectedRelationship?.Key));
        Assert.Equal([1, 2, 3], asked);
        workspace.ReturnTo(0);
        Assert.Equal(paired, workspace.SelectedRelationship?.Key);
        Assert.Equal([1, 2, 3, 4], asked);

        // At svc.exe's rung, chosen in its table: back from its records it is chosen there, and the machine rung above,
        // where that way did not begin, chooses the group's row it was opened from, as ever.
        string own = Between(workspace, 300, 301).Key;
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "svc.exe");
        Assert.True(workspace.Descend());
        workspace.SelectedRelationship = Row(workspace, own);
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.True(workspace.Ascend());
        Assert.Equal(own, workspace.SelectedRelationship?.Key);
        Assert.Equal([1, 2, 3, 4, 5], asked);
        Assert.True(workspace.Ascend());
        Assert.Equal((null, "svc.exe"), (workspace.SelectedRelationship, workspace.SelectedRung?.Label));
        Assert.Equal([1, 2, 3, 4, 5], asked);
    });

    [Fact(DisplayName = "§3.2: a relationship opened at a group's rung is chosen again there where the way back comes to that rung, and the way back is let go where the way down replaced it")]
    public void TheWayBackToAGroupsRungChoosesItWhereThatRungStands() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;
        List<int> asked = Asked(workspace);

        // svc.exe's two processes related: opened from svc.exe's rung, the ladder runs from the machine through svc.exe's
        // rung again, so the way back chooses it there.
        string own = Between(workspace, 300, 301).Key;
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "svc.exe");
        Assert.True(workspace.Descend());
        workspace.SelectedRelationship = Row(workspace, own);
        Assert.True(workspace.OpenSelectedRelationship());
        Assert.Equal(4, workspace.Crumbs.Count);
        Assert.True(workspace.Ascend());
        Assert.True(workspace.Ascend());
        Assert.Equal((2, own), (workspace.Crumbs.Count, workspace.SelectedRelationship?.Key));
        Assert.Equal([1], asked);
        Assert.True(workspace.Ascend());
        Assert.Null(workspace.SelectedRelationship);

        // Opened from the rung of its other end's group, the ladder runs through its source's group instead: that rung is
        // no way back to it, so it is chosen at neither that rung nor the machine's.
        CommunicationEdge paired = Between(workspace, 100, 200);
        ProcessNode target = workspace.Snapshot.Processes.Single(node => node.Id == paired.TargetId);
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == target.GroupKey);
        Assert.True(workspace.Descend());
        workspace.SelectedRelationship = Row(workspace, paired.Key);
        Assert.True(workspace.OpenSelectedRelationship());
        Assert.True(workspace.Ascend());
        Assert.True(workspace.Ascend());
        Assert.NotEqual(target.GroupKey, workspace.SelectedRung?.Key);
        Assert.Null(workspace.SelectedRelationship);
        Assert.Null(workspace.CaptureNavigation().RelationshipOrigins);
        Assert.True(workspace.Ascend());
        Assert.Null(workspace.SelectedRelationship);
        Assert.Equal([1], asked);
    });

    [Fact(DisplayName = "R7: a newer generation keeps the way back to a relationship opened, and lets it go where the session no longer holds the relationship")]
    public void ANewerGenerationKeepsTheWayBackToARelationship() => SingleThreadedContext.Run(async () =>
    {
        WorkspaceSnapshot source = SyntheticWorkspace.Create();
        using var old = new WorkspaceViewModel(source, "session:one:generation:1");
        await old.LayoutReady;
        old.SelectedRelationship = Row(old, "edge.api-cache");
        Assert.True(old.OpenSelectedRelationship());
        WorkspaceNavigationMemento saved = old.CaptureNavigation();
        Assert.Equal(new RelationshipOrigin(0, DetailLevel.Machine, null, "edge.api-cache"), Assert.Single(saved.RelationshipOrigins!));

        using var refreshed = new WorkspaceViewModel(source, "session:one:generation:2");
        await refreshed.LayoutReady;
        Assert.Null(refreshed.RestoreNavigation(saved));
        Assert.Equal(4, refreshed.Crumbs.Count);
        refreshed.ReturnTo(0);
        Assert.Equal("edge.api-cache", refreshed.SelectedRelationship?.Key);

        // Where it is gone, the way back chooses nothing in its place.
        using var changed = new WorkspaceViewModel(
            source with { Edges = [.. source.Edges.Where(edge => edge.Key != "edge.api-cache")] }, "session:one:generation:3");
        await changed.LayoutReady;
        _ = changed.RestoreNavigation(saved);
        List<int> asked = Asked(changed);
        changed.ReturnTo(0);
        Assert.Null(changed.SelectedRelationship);
        Assert.Empty(asked);
    });

    /// <summary>Each count of the times a way back asked for the keyboard to go to a relationship's row, in order.</summary>
    private static List<int> Asked(WorkspaceViewModel workspace)
    {
        var asked = new List<int>();
        workspace.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(WorkspaceViewModel.RelationshipAsked)) asked.Add(workspace.RelationshipAsked);
        };
        return asked;
    }

    private static RelationshipRow Row(WorkspaceViewModel workspace, string key) =>
        workspace.Relationships.Single(row => row.Key == key);

    private static ProcessNode Process(WorkspaceViewModel workspace, int pid) =>
        workspace.Snapshot.Processes.Single(process => process.ProcessId == pid);

    /// <summary>The relationship between the processes whose PIDs are <paramref name="one"/> and <paramref name="other"/>.</summary>
    private static CommunicationEdge Between(WorkspaceViewModel workspace, int one, int other)
    {
        HashSet<ProcessInstanceId> ends = [Process(workspace, one).Id, Process(workspace, other).Id];
        return workspace.Snapshot.Edges.Single(edge => ends.Contains(edge.SourceId) && ends.Contains(edge.TargetId));
    }

    private static WorkspaceViewModel Open(TemporarySession session)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        return new(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
    }

    /// <summary>
    /// client.exe connecting to server.exe's 8080 and sending to it, and svc.exe's two processes, PIDs 300 and 301, the
    /// first connecting to the second's 9000 and sending to it: two relationships, each over one channel.
    /// </summary>
    private static ObservationRowV1[] Rows() =>
    [
        Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 100 },
        Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe", SessionRelativeTicks = 110 },
        Lifecycle(3, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Tools\svc.exe", SessionRelativeTicks = 120 },
        Lifecycle(4, ObservationKind.Create, 301, 4) with { ResourceName = @"C:\Tools\svc.exe", SessionRelativeTicks = 130 },
        Transfer(10, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 10)
            .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 200 },
        Transfer(11, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 11)
            .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 210 },
        Transfer(12, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 12)
            .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 220 },
        Transfer(20, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 300, 20)
            .Between("127.0.0.1:51000", "127.0.0.1:9000") with { SessionRelativeTicks = 300 },
        Transfer(21, ObservationKind.Send, AccountingSide.SendSide, 32, 300, 21)
            .Between("127.0.0.1:51000", "127.0.0.1:9000") with { SessionRelativeTicks = 310 },
        Transfer(22, ObservationKind.Receive, AccountingSide.ReceiveSide, 32, 301, 22)
            .Between("127.0.0.1:9000", "127.0.0.1:51000") with { SessionRelativeTicks = 320 },
    ];
}
