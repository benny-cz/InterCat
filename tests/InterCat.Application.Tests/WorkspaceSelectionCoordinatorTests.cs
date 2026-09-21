using InterCat.Application;
using InterCat.Domain;
using Xunit;

namespace InterCat.Application.Tests;

public sealed class WorkspaceSelectionCoordinatorTests
{
    [Fact(DisplayName = "R6: linked panes observe one atomic selection state")]
    public void ProcessAndIntervalSelectionsShareOneRevisionedState()
    {
        var coordinator = new WorkspaceSelectionCoordinator();
        var processId = ProcessInstanceId.New();
        var interval = new TimeRange(10, 20);
        var revisions = new List<long>();
        coordinator.SelectionChanged += (_, selection) => revisions.Add(selection.Revision);

        coordinator.SelectProcess(processId);
        coordinator.SelectInterval(interval);

        Assert.Equal(processId, coordinator.Current.ProcessId);
        Assert.Equal(interval, coordinator.Current.Interval);
        Assert.Equal([1L, 2L], revisions);
    }

    [Fact(DisplayName = "I19: clearing view selection does not mutate source data")]
    public void ClearSelectionLeavesSnapshotUntouched()
    {
        WorkspaceSnapshot snapshot = SyntheticWorkspace.Create();
        var coordinator = new WorkspaceSelectionCoordinator();
        coordinator.SelectProcess(snapshot.Processes[0].Id);

        coordinator.Clear();

        Assert.Null(coordinator.Current.ProcessId);
        Assert.Null(coordinator.Current.Interval);
        Assert.Equal(5, snapshot.Processes.Count);
        Assert.Equal(5, snapshot.Edges.Count);
    }
}
