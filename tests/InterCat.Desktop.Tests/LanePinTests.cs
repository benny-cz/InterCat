using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>
/// §6.2's pinned lanes: a selected process's lane, pinned, is drawn at the top of its group's lanes, in the order pinned,
/// above the rest in the ranked table's order, until it is unpinned. The table keeps its own order, a pin elsewhere is not
/// counted in another group's caption, and moving a lane reads nothing anew.
/// </summary>
public sealed class LanePinTests
{
    [Fact(DisplayName = "§6.2: a pinned lane is drawn at the top of its group's lanes, in the order pinned, and unpinned goes back to its row's place")]
    public void APinnedLaneIsDrawnAtTheTopInTheOrderPinned() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Pool());
        using WorkspaceViewModel workspace = Open(session);
        ProcessNode[] pool = Pooled(workspace);
        ProcessNode tool = Process(workspace, 600);
        workspace.RequestTimelineDetail(workspace.WholeSnapshot.Extent, 64);

        // At the machine rung no process's lane is drawn, so a selected process has none to pin.
        workspace.SelectProcess(pool[3].Id);
        Assert.False(workspace.HasSelectedProcessLane);
        Assert.False(workspace.ToggleSelectedLanePin());
        Assert.Empty(workspace.PinnedLanes);

        DescendTo(workspace, pool[0].GroupKey);
        await workspace.TimelineDetailReady;
        Assert.True(workspace.ShowsProcessLanes);
        Assert.Equal(Ids(pool[0], pool[1], pool[2], pool[3]), Lanes(workspace));
        Assert.Contains(" · 4 process lanes · machine context above", workspace.TimelineCaption, StringComparison.Ordinal);

        workspace.SelectProcess(pool[3].Id);
        Assert.True(workspace.HasSelectedProcessLane);
        Assert.False(workspace.IsSelectedLanePinned);
        Assert.Equal("Pin lane (P)", workspace.LanePinLabel);
        List<string?> changed = [];
        workspace.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        string[] table = [.. workspace.RungRows.Select(row => row.Key)];

        // The least busy instance's lane, pinned, is drawn first; the ranked table keeps its order.
        Assert.True(workspace.ToggleSelectedLanePin());
        Assert.Equal(Ids(pool[3], pool[0], pool[1], pool[2]), Lanes(workspace));
        Assert.Equal([pool[3].Id], workspace.PinnedLanes);
        Assert.True(workspace.IsLanePinned(pool[3].Id));
        Assert.False(workspace.IsLanePinned(pool[0].Id));
        Assert.True(workspace.IsSelectedLanePinned);
        Assert.Equal("Unpin lane (P)", workspace.LanePinLabel);
        Assert.Contains(" · 4 process lanes, 1 pinned at the top · machine context above", workspace.TimelineCaption,
            StringComparison.Ordinal);
        Assert.Equal(table, workspace.RungRows.Select(row => row.Key));
        Assert.Contains(nameof(WorkspaceViewModel.ProcessLaneDisplay), changed);
        Assert.Contains(nameof(WorkspaceViewModel.PinnedLanes), changed);
        Assert.Contains(nameof(WorkspaceViewModel.LanePinLabel), changed);
        Assert.Contains(nameof(WorkspaceViewModel.TimelineCaption), changed);

        // Another selection's control says what it would do to that lane, and a second pin goes beneath the first.
        changed.Clear();
        workspace.SelectProcess(pool[1].Id);
        Assert.Contains(nameof(WorkspaceViewModel.LanePinLabel), changed);
        Assert.False(workspace.IsSelectedLanePinned);
        Assert.Equal("Pin lane (P)", workspace.LanePinLabel);
        Assert.True(workspace.ToggleSelectedLanePin());
        Assert.Equal(Ids(pool[3], pool[1], pool[0], pool[2]), Lanes(workspace));
        Assert.Contains(" · 4 process lanes, 2 pinned at the top · ", workspace.TimelineCaption, StringComparison.Ordinal);

        // Unpinned, a lane goes back to its row's place.
        workspace.SelectProcess(pool[3].Id);
        Assert.Equal("Unpin lane (P)", workspace.LanePinLabel);
        Assert.True(workspace.ToggleSelectedLanePin());
        Assert.Equal(Ids(pool[1], pool[0], pool[2], pool[3]), Lanes(workspace));
        Assert.Equal([pool[1].Id], workspace.PinnedLanes);
        Assert.Equal("Pin lane (P)", workspace.LanePinLabel);
        Assert.Contains(" · 4 process lanes, 1 pinned at the top · ", workspace.TimelineCaption, StringComparison.Ordinal);

        // Another group's caption counts only its own lanes pinned; the pin stays for when the group is opened again.
        Assert.True(workspace.Ascend());
        DescendTo(workspace, tool.GroupKey);
        await workspace.TimelineDetailReady;
        Assert.True(workspace.ShowsProcessLanes);
        Assert.Contains(" · 2 process lanes · machine context above", workspace.TimelineCaption, StringComparison.Ordinal);
        Assert.Equal([pool[1].Id], workspace.PinnedLanes);
        Assert.True(workspace.Ascend());
        DescendTo(workspace, pool[0].GroupKey);
        await workspace.TimelineDetailReady;
        Assert.Equal(Ids(pool[1], pool[0], pool[2], pool[3]), Lanes(workspace));
    });

    [Fact(DisplayName = "§6.2: a later publication's lanes keep the pins, which come before its lanes arrive")]
    public void ALaterPublicationKeepsThePins() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Pool());
        using WorkspaceViewModel workspace = Open(session);
        ProcessNode[] pool = Pooled(workspace);
        workspace.RequestTimelineDetail(workspace.WholeSnapshot.Extent, 64);
        DescendTo(workspace, pool[0].GroupKey);
        await workspace.TimelineDetailReady;
        workspace.SelectProcess(pool[2].Id);
        Assert.True(workspace.ToggleSelectedLanePin());

        // The next generation's workspace, as the window builds it: the pins, then the lanes it counts anew.
        using WorkspaceViewModel replacement = Open(session);
        _ = replacement.RestoreNavigation(workspace.CaptureNavigation());
        replacement.PinLanes([.. workspace.PinnedLanes, pool[2].Id]);
        Assert.Equal([pool[2].Id], replacement.PinnedLanes);
        replacement.AdoptTimeline(workspace.CarryTimeline());
        Assert.Equal(Ids(pool[2], pool[0], pool[1], pool[3]), Lanes(replacement));
        replacement.RequestTimelineDetail(replacement.WholeSnapshot.Extent, 64);
        await replacement.TimelineDetailReady;
        Assert.True(replacement.ShowsProcessLanes);
        Assert.Equal(Ids(pool[2], pool[0], pool[1], pool[3]), Lanes(replacement));
        Assert.Equal("Unpin lane (P)", replacement.LanePinLabel);
    });

    [Fact(DisplayName = "§6.2: pinning a lane moves it without reading the group's bytes again")]
    public void APinMovesALaneWithoutReadingItsBytesAgain() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Pool());
        using WorkspaceViewModel workspace = Open(session);
        ProcessNode[] pool = Pooled(workspace);
        TimeRange extent = workspace.WholeSnapshot.Extent;
        workspace.RequestTimelineDetail(extent, 64);
        workspace.RankBy = RankingMetric.BytesSent;
        await workspace.TimelineBytesReady;
        await workspace.RankingReady;
        DescendTo(workspace, pool[0].GroupKey);
        await workspace.TimelineDetailReady;
        await workspace.TimelineBytesReady;
        await workspace.RankingReady;
        ProcessLaneByteLayer plotted = Assert.IsType<ProcessLaneByteLayer>(workspace.ProcessLaneBytes);

        workspace.SelectProcess(pool[3].Id);
        Assert.True(workspace.ToggleSelectedLanePin());
        Assert.Equal(pool[3].Id, workspace.ProcessLaneDisplay[0].ProcessId);

        // The view draws the same columns again, as a repaint or a resize asks: the bytes already read still answer them.
        workspace.RequestTimelineDetail(extent, 64);
        await workspace.TimelineDetailReady;
        await workspace.TimelineBytesReady;
        Assert.Same(plotted.Measures, workspace.ProcessLaneBytes?.Measures);
        Assert.Equal(400, Sent(workspace.ProcessLaneBytes!, pool[0]));
        Assert.Equal(100, Sent(workspace.ProcessLaneBytes!, pool[3]));
        Assert.Contains(" · 4 process lanes, 1 pinned at the top · bytes sent per second", workspace.TimelineCaption,
            StringComparison.Ordinal);
    });

    /// <summary>The bytes a lane's process sent over the session, as its lane's columns add them up.</summary>
    private static long Sent(ProcessLaneByteLayer layer, ProcessNode process) =>
        layer.Measures.Of(process.Id)!.Columns.Sum(column => column.ValueOf(RankingMetric.BytesSent).Value ?? 0);

    /// <summary>pool.exe's four instances, busiest first, as the ranked table lists them.</summary>
    private static ProcessNode[] Pooled(WorkspaceViewModel workspace) =>
        [Process(workspace, 500), Process(workspace, 501), Process(workspace, 502), Process(workspace, 503)];

    private static ProcessNode Process(WorkspaceViewModel workspace, int pid) =>
        workspace.Snapshot.Processes.Single(process => process.ProcessId == pid);

    private static ProcessInstanceId[] Ids(params ProcessNode[] processes) => [.. processes.Select(process => process.Id)];

    private static ProcessInstanceId[] Lanes(WorkspaceViewModel workspace) =>
        [.. workspace.ProcessLaneDisplay.Select(lane => lane.ProcessId)];

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

    /// <summary>
    /// Four instances of pool.exe sending four, three, two and one 100-byte messages, so the ranked table and the lanes
    /// beneath it list them busiest first, and two of tool.exe, a group of their own.
    /// </summary>
    private static ObservationRowV1[] Pool() =>
    [
        .. new[] { (Pid: 500, Sends: 4), (Pid: 501, Sends: 3), (Pid: 502, Sends: 2), (Pid: 503, Sends: 1), (Pid: 600, Sends: 2), (Pid: 601, Sends: 1) }
            .SelectMany((instance, index) => new[]
            {
                Timed(Lifecycle(1 + index, ObservationKind.Create, instance.Pid, (ulong)(1 + index)) with
                {
                    ResourceName = instance.Pid < 600 ? @"C:\Tools\pool.exe" : @"C:\Tools\tool.exe",
                }),
            }.Concat(Enumerable.Range(0, instance.Sends).Select(send => Timed(Transfer(10 + (index * 10) + send,
                ObservationKind.Send, AccountingSide.SendSide, 100, instance.Pid, (ulong)(10 + (index * 10) + send)))))),
    ];

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };
}
