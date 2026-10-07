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
/// §6.2's search lane names: a search typed at a group's rung marks the lanes of the processes it finds, however many the
/// rail's listed hits leave out, counts them in the timeline's caption, and names the lane to show - its selected hit's,
/// else the first it finds - without selecting anything.
/// </summary>
public sealed class LaneSearchTests
{
    [Fact(DisplayName = "§6.2: a search at a group marks the lanes it finds, past the hits it lists, and shows its selected hit's")]
    public void ASearchMarksTheLanesItFinds() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Pool());
        using WorkspaceViewModel workspace = Open(session);
        workspace.RequestTimelineDetail(workspace.WholeSnapshot.Extent, 64);
        ProcessNode wanted = Process(workspace, 5_042);
        ProcessNode tool = Process(workspace, 600);

        // At the machine rung no process lane is drawn, so a search marks none and names none to show.
        workspace.SearchText = "5042";
        Assert.False(workspace.IsLaneSearched(wanted.Id));
        Assert.Null(workspace.SearchedLane);
        workspace.SearchText = string.Empty;

        DescendTo(workspace, wanted.GroupKey);
        await workspace.TimelineDetailReady;
        Assert.Equal(60, workspace.ProcessLaneDisplay.Count);
        Assert.DoesNotContain("found by the search", workspace.TimelineCaption, StringComparison.Ordinal);
        List<string?> changed = [];
        workspace.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // A PID finds its lane, which is the one to show; the selection is the search's to make, not the timeline's.
        workspace.SearchText = "5042";
        Assert.True(workspace.IsLaneSearched(wanted.Id));
        Assert.Single(workspace.ProcessLaneDisplay, lane => workspace.IsLaneSearched(lane.ProcessId));
        Assert.Equal(wanted.Id, workspace.SearchedLane);
        Assert.Contains(" · 60 process lanes, 1 found by the search · ", workspace.TimelineCaption, StringComparison.Ordinal);
        Assert.Contains(nameof(WorkspaceViewModel.SearchedLane), changed);
        Assert.Contains(nameof(WorkspaceViewModel.TimelineCaption), changed);
        Assert.Null(workspace.SelectedProcess);

        // The executable's name finds every lane, though the rail lists 50 of its 61 hits.
        workspace.SearchText = "pool";
        Assert.Equal(WorkspaceSearch.DefaultLimit, workspace.SearchResults.Count);
        Assert.All(workspace.ProcessLaneDisplay, lane => Assert.True(workspace.IsLaneSearched(lane.ProcessId)));
        Assert.Contains(" · 60 process lanes, 60 found by the search · ", workspace.TimelineCaption, StringComparison.Ordinal);

        // Its first hit is the group, which has no lane: the first lane found is the one to show; a hit chosen in the rail
        // that is a lane is shown instead.
        Assert.Equal(SearchHitKind.Group, workspace.SelectedSearchResult!.Hit.Kind);
        Assert.Equal(workspace.ProcessLaneDisplay[0].ProcessId, workspace.SearchedLane);
        SearchRow later = workspace.SearchResults.Last(row => row.Hit.Kind == SearchHitKind.Process);
        workspace.SelectedSearchResult = later;
        Assert.Equal(later.Hit.Key, workspace.SearchedLane.ToString());

        // A process of another group is no lane here, and a search that finds none here says so.
        workspace.SearchText = "tool";
        Assert.False(workspace.IsLaneSearched(tool.Id));
        Assert.Null(workspace.SearchedLane);
        Assert.Contains(" · 60 process lanes, none found by the search · ", workspace.TimelineCaption, StringComparison.Ordinal);

        // Cleared, the search marks nothing and the caption says nothing of it.
        changed.Clear();
        workspace.SearchText = string.Empty;
        Assert.DoesNotContain(workspace.ProcessLaneDisplay, lane => workspace.IsLaneSearched(lane.ProcessId));
        Assert.DoesNotContain("by the search", workspace.TimelineCaption, StringComparison.Ordinal);
        Assert.Contains(nameof(WorkspaceViewModel.TimelineCaption), changed);
    });

    [Fact(DisplayName = "§6.2: a search typed before a group's lanes are counted marks them once they are")]
    public void ASearchMarksLanesThatArriveAfterIt() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Pool());
        using WorkspaceViewModel workspace = Open(session);
        workspace.RequestTimelineDetail(workspace.WholeSnapshot.Extent, 64);
        ProcessNode wanted = Process(workspace, 5_017);
        DescendTo(workspace, wanted.GroupKey);
        workspace.SearchText = "5017";
        await workspace.TimelineDetailReady;
        Assert.True(workspace.IsLaneSearched(wanted.Id));
        Assert.Equal(wanted.Id, workspace.SearchedLane);
    });

    private static ProcessNode Process(WorkspaceViewModel workspace, int pid) =>
        workspace.Snapshot.Processes.Single(process => process.ProcessId == pid);

    private static WorkspaceViewModel Open(TemporarySession session)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        return new(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
    }

    private static void DescendTo(WorkspaceViewModel workspace, string key)
    {
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == key);
        Assert.True(workspace.Descend());
    }

    /// <summary>Sixty instances of pool.exe, PIDs 5000 to 5059, each created and sending once, and one of tool.exe.</summary>
    private static ObservationRowV1[] Pool() =>
    [
        .. Enumerable.Range(0, 60).SelectMany(index => new[]
        {
            Timed(Lifecycle(1 + index, ObservationKind.Create, 5_000 + index, (ulong)(1 + index)) with
            {
                ResourceName = @"C:\Tools\pool.exe",
            }),
            Timed(Transfer(1_000 + index, ObservationKind.Send, AccountingSide.SendSide, 8, 5_000 + index, (ulong)(1_000 + index))),
        }),
        Timed(Lifecycle(100, ObservationKind.Create, 600, 100) with { ResourceName = @"C:\Tools\tool.exe" }),
    ];

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };
}
