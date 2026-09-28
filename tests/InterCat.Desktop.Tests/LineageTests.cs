using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>
/// Who started a process, and whom it started (`entities-v1` §3's parent link), stated by the inspector and one action
/// away: the parent as the selection, the children as a multi-selection.
/// </summary>
public sealed class LineageTests
{
    [Fact(DisplayName = "§3.2: the inspector names a process's parent and children, and selects either")]
    public void TheInspectorStatesAndSelectsAProcesssLineage()
    {
        using var session = new TemporarySession();
        ObservationRowV1 launcher = Timed(Lifecycle(10, ObservationKind.Create, 400, 1) with { ResourceName = @"C:\Tools\launcher.exe" });
        ObservationRowV1 keyed = Timed(Lifecycle(20, ObservationKind.Create, 401, 2) with { ResourceName = @"C:\Tools\worker.exe" });
        ObservationRowV1 timed = Timed(Lifecycle(30, ObservationKind.Create, 402, 3) with { ResourceName = @"C:\Tools\worker.exe" });
        ObservationRowV1 orphan = Timed(Lifecycle(40, ObservationKind.Create, 500, 4) with { ResourceName = @"C:\Tools\orphan.exe" });
        ObservationRowV1 running = Timed(Lifecycle(5, ObservationKind.Inventory, 600, 5) with { ResourceName = @"C:\Tools\service.exe" });
        Publish(session.Store, [launcher, keyed, timed, orphan, running], fields:
        [
            Field(launcher, SourceField.ProcessStartSequence, 800),
            Field(keyed, SourceField.ProcessStartSequence, 801),
            Field(keyed, SourceField.ParentProcessId, 400),
            Field(keyed, SourceField.ParentStartSequence, 800),
            Field(timed, SourceField.ProcessStartSequence, 802),
            Field(timed, SourceField.ParentProcessId, 400),
            Field(orphan, SourceField.ProcessStartSequence, 803),
            Field(orphan, SourceField.ParentProcessId, 9),
        ]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        ProcessNode Pid(int pid) => workspace.Snapshot.Processes.Single(node => node.ProcessId == pid);

        // Nothing is selected: no lineage to state.
        Assert.False(workspace.ShowsLineage);

        // The launcher names no parent of its own, and started both workers.
        workspace.SelectProcess(Pid(400).Id);
        Assert.True(workspace.ShowsLineage);
        Assert.Equal("No record of this process names its parent", workspace.ParentText);
        Assert.Equal("2 processes: worker.exe · PID 401, worker.exe · PID 402", workspace.ChildrenText);
        Assert.False(workspace.CanSelectParent);
        Assert.True(workspace.CanSelectChildren);
        Assert.Equal("Select its children", workspace.SelectChildrenLabel);

        // A worker's parent is linked by its start key, or by its PID and the child's start time, and is selected in one step.
        workspace.SelectProcess(Pid(401).Id);
        Assert.Equal("launcher.exe · PID 400 · linked by its start key", workspace.ParentText);
        Assert.Equal("None seen in this capture", workspace.ChildrenText);
        workspace.SelectProcess(Pid(402).Id);
        Assert.Equal("launcher.exe · PID 400 · linked by its PID and this process's start time", workspace.ParentText);
        Assert.True(workspace.SelectParent());
        Assert.Equal(400, workspace.SelectedProcess?.ProcessId);

        // A parent the capture never saw stays a PID, and nothing selects it; a rundown that names none says so.
        workspace.SelectProcess(Pid(500).Id);
        Assert.Equal("PID 9 · not in this capture", workspace.ParentText);
        Assert.False(workspace.CanSelectParent);
        Assert.False(workspace.SelectParent());
        workspace.SelectProcess(Pid(600).Id);
        Assert.Equal("No record of this process names its parent", workspace.ParentText);

        // The children become a multi-selection, which Enter would turn into their records.
        workspace.SelectProcess(Pid(400).Id);
        Assert.True(workspace.SelectChildren());
        Assert.True(workspace.HasMultiSelection);
        Assert.Equal([401, 402], workspace.ChosenProcesses.Select(process => process.ProcessId).Order());
        Assert.Null(workspace.SelectedProcess);
        Assert.False(workspace.ShowsLineage);
    }

    [Fact(DisplayName = "§3.2: a single child is selected as the selection, and the synthetic tour states no lineage")]
    public void ASingleChildIsTheSelection()
    {
        using var session = new TemporarySession();
        ObservationRowV1 parent = Timed(Lifecycle(10, ObservationKind.Create, 400, 1) with { ResourceName = @"C:\Tools\shell.exe" });
        ObservationRowV1 child = Timed(Lifecycle(20, ObservationKind.Create, 401, 2) with { ResourceName = @"C:\Tools\tool.exe" });
        Publish(session.Store, [parent, child], fields:
        [
            Field(parent, SourceField.ProcessStartSequence, 800),
            Field(child, SourceField.ProcessStartSequence, 801),
            Field(child, SourceField.ParentProcessId, 400),
            Field(child, SourceField.ParentStartSequence, 800),
        ]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        workspace.SelectProcess(workspace.Snapshot.Processes.Single(node => node.ProcessId == 400).Id);
        Assert.Equal("Go to its child", workspace.SelectChildrenLabel);
        Assert.True(workspace.SelectChildren());
        Assert.Equal(401, workspace.SelectedProcess?.ProcessId);
        Assert.False(workspace.HasMultiSelection);

        using var tour = new WorkspaceViewModel(SyntheticWorkspace.Create(), "synthetic-tour-v1");
        tour.SelectProcess(tour.Snapshot.Processes[0].Id);
        Assert.False(tour.ShowsLineage);
    }

    /// <summary>A row whose session time is its reading in workspace ticks.</summary>
    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };
}
