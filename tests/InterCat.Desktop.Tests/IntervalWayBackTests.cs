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
/// The way back to a row the interval table lists (§3.2, R15): Enter on it, or E with the keyboard there, lists its
/// moment's records in E; Esc or the breadcrumb back to the rung that way began asks for the keyboard on the row again.
/// </summary>
public sealed class IntervalWayBackTests
{
    [Fact(DisplayName = "§3.2: the way back from a moment the interval table lists asks for the keyboard on its row, by Esc, forward and back, or the breadcrumb, where the tables are shown")]
    public void TheWayBackFromAnIntervalRowAsksForIt() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, TimelineCellTests.Rows());
        using WorkspaceViewModel workspace = TimelineCellTests.Open(session);
        workspace.RequestTimelineDetail(workspace.Snapshot.Extent, 80);
        await workspace.TimelineDetailReady;
        workspace.ShowTables = true;
        IntervalRow row = workspace.Intervals.First(candidate => candidate.ObservationCount > 0);
        workspace.SelectedIntervalRow = row;

        // Its moment's records listed in E, Esc asks for the keyboard on the row again, and so does forward to them and
        // back, and the breadcrumb.
        Assert.True(workspace.ShowIntervalRecords());
        await workspace.EvidenceReady;
        IntervalRowOrigin origin = new(0, DetailLevel.Machine, null, row.Interval);
        Assert.Equal(origin, workspace.CaptureNavigation().IntervalRowOrigin);
        Assert.True(workspace.Ascend());
        Assert.Equal((1, row.Interval), (workspace.IntervalRowAsked, workspace.SelectedIntervalRow?.Interval));
        Assert.Equal(origin, workspace.CaptureNavigation().IntervalRowOrigin);

        // Asked once: the rows listed again for a lane chosen there since, the row still the moment's, ask nothing more.
        workspace.SelectedTimelineLane = workspace.TimelineLaneOptions.First(option => option.Mechanism is not null);
        Assert.Equal((1, row.Interval), (workspace.IntervalRowAsked, workspace.SelectedIntervalRow?.Interval));
        workspace.SelectedTimelineLane = workspace.TimelineLaneOptions[0];
        Assert.True(workspace.GoForward());
        await workspace.EvidenceReady;
        Assert.True(workspace.Ascend());
        Assert.Equal(2, workspace.IntervalRowAsked);
        Assert.True(workspace.ShowIntervalRecords());
        await workspace.EvidenceReady;
        workspace.ReturnTo(0);
        Assert.Equal(3, workspace.IntervalRowAsked);

        // E from elsewhere is no way down from the row; with the tables hidden on the way back, the row asks for nothing,
        // even shown again, but forward and back with them shown asks.
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.True(workspace.Ascend());
        Assert.Equal(3, workspace.IntervalRowAsked);
        Assert.Null(workspace.CaptureNavigation().IntervalRowOrigin);
        Assert.True(workspace.ShowIntervalRecords());
        await workspace.EvidenceReady;
        workspace.ShowTables = false;
        Assert.True(workspace.Ascend());
        Assert.Equal(3, workspace.IntervalRowAsked);
        workspace.ShowTables = true;
        Assert.Equal(3, workspace.IntervalRowAsked);
        Assert.True(workspace.GoForward());
        await workspace.EvidenceReady;
        Assert.True(workspace.Ascend());
        Assert.Equal(4, workspace.IntervalRowAsked);
    });

    [Fact(DisplayName = "§3.2: a newer generation keeps the way back to an interval table's row; another rung at its depth, another interval chosen before the row is listed again, or a way down of its own from above its rung, lets it go")]
    public void AWayOfItsOwnLetsTheRowGo() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, TimelineCellTests.Rows());
        using WorkspaceViewModel workspace = TimelineCellTests.Open(session);
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        workspace.RequestTimelineDetail(workspace.Snapshot.Extent, 80);
        TimelineCellTests.DescendTo(workspace, client.GroupKey);
        TimelineCellTests.DescendTo(workspace, client.Id.ToString());
        await workspace.TimelineDetailReady;
        workspace.ShowTables = true;
        IntervalRow row = workspace.Intervals.First(candidate => candidate.ObservationCount > 0);
        workspace.SelectedIntervalRow = row;
        Assert.True(workspace.ShowIntervalRecords());
        await workspace.EvidenceReady;

        // Restored at E in a newer generation, Esc asks for the row.
        WorkspaceNavigationMemento saved = workspace.CaptureNavigation();
        using (WorkspaceViewModel next = TimelineCellTests.Open(session))
        {
            next.RequestTimelineDetail(next.Snapshot.Extent, 80);
            Assert.Null(next.RestoreNavigation(saved));
            Assert.True(next.Ascend());
            await next.TimelineDetailReady;
            Assert.Equal(1, next.IntervalRowAsked);
        }

        // A way back kept for another rung at its depth - a process that rung no longer stands for - is let go there.
        using (WorkspaceViewModel next = TimelineCellTests.Open(session))
        {
            next.RequestTimelineDetail(next.Snapshot.Extent, 80);
            Assert.Null(next.RestoreNavigation(saved with { IntervalRowOrigin = saved.IntervalRowOrigin! with { Focus = "elsewhere" } }));
            Assert.True(next.Ascend());
            await next.TimelineDetailReady;
            Assert.Equal((0, row.Interval), (next.IntervalRowAsked, next.SelectedIntervalRow?.Interval));
            Assert.Null(next.CaptureNavigation().IntervalRowOrigin);
        }

        // Another interval chosen before the rows list it lets it go, even with the moment chosen again; so do the tables
        // hidden while the rows are listed, shown again or not, and E taken again.
        using (WorkspaceViewModel next = TimelineCellTests.Open(session))
        {
            next.RequestTimelineDetail(next.Snapshot.Extent, 80);
            Assert.Null(next.RestoreNavigation(saved));
            Assert.True(next.Ascend());
            next.SelectInterval(next.Snapshot.Extent);
            next.SelectInterval(row.Interval);
            await next.TimelineDetailReady;
            Assert.Equal((0, row.Interval), (next.IntervalRowAsked, next.SelectedIntervalRow?.Interval));
        }

        using (WorkspaceViewModel next = TimelineCellTests.Open(session))
        {
            next.RequestTimelineDetail(next.Snapshot.Extent, 80);
            Assert.Null(next.RestoreNavigation(saved));
            Assert.True(next.Ascend());
            next.ShowTables = false;
            await next.TimelineDetailReady;
            next.ShowTables = true;
            Assert.Equal((0, row.Interval), (next.IntervalRowAsked, next.SelectedIntervalRow?.Interval));
        }

        using (WorkspaceViewModel next = TimelineCellTests.Open(session))
        {
            next.RequestTimelineDetail(next.Snapshot.Extent, 80);
            Assert.Null(next.RestoreNavigation(saved));
            Assert.True(next.Ascend());
            Assert.True(next.ShowEvidence());
            await next.EvidenceReady;
            await next.TimelineDetailReady;
            Assert.Equal(0, next.IntervalRowAsked);
        }

        // The breadcrumb up past its rung keeps it for that rung; a way down of its own from above, over the same moment,
        // lets it go, so the way back from E there asks for nothing.
        workspace.ReturnTo(0);
        workspace.SelectInterval(row.Interval);
        TimelineCellTests.DescendTo(workspace, client.GroupKey);
        TimelineCellTests.DescendTo(workspace, client.Id.ToString());
        await workspace.TimelineDetailReady;
        Assert.Equal(row.Interval, workspace.SelectedIntervalRow?.Interval);
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.True(workspace.Ascend());
        await workspace.TimelineDetailReady;
        Assert.Equal((0, row.Interval), (workspace.IntervalRowAsked, workspace.SelectedIntervalRow?.Interval));
    });
}
