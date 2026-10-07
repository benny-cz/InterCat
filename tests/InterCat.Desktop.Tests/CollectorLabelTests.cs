using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>
/// §19.5 in the window: InterCat's own process is labelled wherever it is described - its ranked row, the inspector's
/// caption and explanation, and its graph node's card - in the words `icat processes` uses, and no other process is.
/// </summary>
public sealed class CollectorLabelTests
{
    [Fact(DisplayName = "§19.5: the window labels InterCat's broker in its row, the inspector and its graph card, and not the later holder of its PID")]
    public async Task TheBrokerIsLabelledWhereverItIsDescribed()
    {
        using var session = new TemporarySession();
        PublishCollected(session.Store);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        await workspace.LayoutReady;
        ProcessNode broker = workspace.Snapshot.Processes.Single(process => process.Collector == CollectorRole.Broker);
        const string Caption = "PID 4120 #1 · created during capture · InterCat's broker";

        // Its executable's rung lists it with the label beneath its name.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "intercat-broker.exe");
        Assert.True(workspace.Descend());
        Assert.Equal(Caption, Assert.Single(workspace.RungRows).Detail);
        Assert.True(workspace.Ascend());

        // Selected, the inspector says which of InterCat's processes it is, and that its records count as any process's do.
        workspace.SelectProcess(broker.Id);
        Assert.Equal(Caption, workspace.SelectionSubtitle);
        Assert.Contains("It is InterCat's broker, which recorded this capture", workspace.Explanation, StringComparison.Ordinal);

        // Its graph node's card names it first.
        GraphDisplayNode node = workspace.GraphDisplay.Nodes.Single(candidate => candidate.Members.Contains(broker.Id));
        Assert.StartsWith("Process instance · InterCat's broker · ", workspace.DescribeGraphHover(node.Key)!.Lines[0],
            StringComparison.Ordinal);

        // The later holder of its PID is described as any process is.
        ProcessNode later = workspace.Snapshot.Processes.Single(process => process.ProcessId == 4120 && process.PidHolder == 2);
        workspace.SelectProcess(later.Id);
        Assert.Equal("PID 4120 #2 · created during capture", workspace.SelectionSubtitle);
        Assert.DoesNotContain("InterCat", workspace.Explanation, StringComparison.Ordinal);
        GraphDisplayNode other = workspace.GraphDisplay.Nodes.Single(candidate => candidate.Members.Contains(later.Id));
        Assert.DoesNotContain("InterCat", workspace.DescribeGraphHover(other.Key)!.Lines[0], StringComparison.Ordinal);
    }
}
