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
/// §6.7's navigation history: back and forward restore a rung's filters, time, graph focus and lane grouping as one
/// state. Time is the analysis interval the user had on the rung when they left it, and it comes back through the one
/// selection source, so the brush, the ranking's counts and the timeline name the same interval.
/// </summary>
public sealed class NavigationHistoryTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";
    private const int Exchanges = 120;

    [Fact(DisplayName = "§6.7: an ascent brings back the interval its rung was left with, and the ranking counts that interval")]
    public void AnAscentBringsBackTheIntervalItsRungWasLeftWith() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);
        string whole = workspace.RungRows.Single().Observations;
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);

        // Brushed at the machine rung and narrowed on the group below it, Esc brings the machine's brush back, and
        // the ranking counts that brush rather than the group's. Before, the brush was dropped while the ranking kept
        // counting the group's narrower one.
        var machineBrush = new TimeRange(10, 50);
        workspace.SelectInterval(machineBrush);
        await workspace.IntervalReady;
        DescendTo(workspace, client.GroupKey);
        workspace.SelectInterval(new TimeRange(10, 30));
        await workspace.IntervalReady;
        Assert.True(workspace.IsRankedWithinInterval);

        Assert.True(workspace.Ascend());
        await workspace.IntervalReady;
        Assert.Equal(machineBrush, workspace.SelectedInterval);
        Assert.True(workspace.IsRankedWithinInterval);
        Assert.StartsWith("Ranked within", workspace.RankingScopeText, StringComparison.Ordinal);
        Assert.Equal("40", workspace.RungRows.Single().Observations);

        // A rung left without a brush comes back without one, by a crumb as by Esc, counting the whole session.
        workspace.ClearSelection();
        await workspace.IntervalReady;
        DescendTo(workspace, client.GroupKey);
        DescendTo(workspace, client.Id.ToString());
        workspace.SelectInterval(new TimeRange(10, 30));
        await workspace.IntervalReady;
        workspace.ReturnTo(0);
        await workspace.IntervalReady;
        Assert.Equal("L0 · MACHINE", workspace.LevelBadge);
        Assert.Null(workspace.SelectedInterval);
        Assert.False(workspace.IsRankedWithinInterval);
        Assert.Equal(string.Empty, workspace.RankingScopeText);
        Assert.Equal(whole, workspace.RungRows.Single().Observations);
    });

    [Fact(DisplayName = "§6.7: going forward re-enters the rung left, with its interval, its filters and its graph")]
    public void GoingForwardReentersTheRungLeft() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        Assert.False(workspace.CanGoForward);
        Assert.False(workspace.GoForward());
        Assert.DoesNotContain("Alt+Right", workspace.DescendHint, StringComparison.Ordinal);

        // At the process: a brush, and the group filter the descent implied taken off.
        DescendTo(workspace, client.GroupKey);
        DescendTo(workspace, client.Id.ToString());
        var brush = new TimeRange(10, 30);
        workspace.SelectInterval(brush);
        await workspace.IntervalReady;
        workspace.SelectedFilter = workspace.Filters.Single(filter => filter.Field == "group");
        Assert.True(workspace.RemoveSelectedFilter());
        string[] crumbs = [.. workspace.Crumbs.Select(crumb => crumb.Label)];
        string[] filters = [.. workspace.Filters.Select(filter => filter.Field)];
        string[] rows = [.. workspace.RungRows.Select(row => $"{row.Key} {row.Observations}")];
        string graph = workspace.GraphSummary;
        string caption = workspace.TimelineCaption;

        Assert.True(workspace.Ascend());
        await workspace.IntervalReady;
        Assert.Null(workspace.SelectedInterval);
        Assert.True(workspace.CanGoForward);
        Assert.Equal($"Forward to {crumbs[^1]} (Alt+Right)", workspace.ForwardLabel);
        Assert.EndsWith(" · Alt+Right goes forward", workspace.DescendHint, StringComparison.Ordinal);

        Assert.True(workspace.GoForward());
        await workspace.IntervalReady;
        Assert.Equal(crumbs, workspace.Crumbs.Select(crumb => crumb.Label));
        Assert.Equal(filters, workspace.Filters.Select(filter => filter.Field));
        Assert.Equal(brush, workspace.SelectedInterval);
        Assert.True(workspace.IsRankedWithinInterval);
        Assert.Equal(rows, workspace.RungRows.Select(row => $"{row.Key} {row.Observations}"));
        Assert.Equal(graph, workspace.GraphSummary);
        Assert.Equal(caption, workspace.TimelineCaption);
        Assert.False(workspace.CanGoForward);
        Assert.Equal("Nothing to go forward to (Alt+Right)", workspace.ForwardLabel);
    });

    [Fact(DisplayName = "§6.7: going forward re-enters a rung with the row chosen there when it was left - at once, or once the rung reads its rows - and with none where none was")]
    public void GoingForwardChoosesTheRowTheRungWasLeftWith() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, RowsReachingAnotherHost());
        using WorkspaceViewModel workspace = Open(session);
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        string process = client.Id.ToString();

        // At the group, its process chosen and left by Esc: forward chooses it again, and the process with it.
        DescendTo(workspace, client.GroupKey);
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == process);
        Assert.True(workspace.Ascend());
        Assert.True(workspace.GoForward());
        Assert.Equal((process, client), (workspace.SelectedRung?.Key, workspace.SelectedProcess));

        // At the process, a row it reads after it is shown - its connection to another host - is chosen, and E lists its
        // records; the breadcrumb up to the machine passes the group and the process by.
        DescendTo(workspace, process);
        string[] shown = [.. workspace.RungRows.Select(row => row.Key)];
        await workspace.ConnectionsReady;
        RungRow later = workspace.RungRows.First(row => !shown.Contains(row.Key));
        workspace.SelectedRung = later;
        Assert.Same(later, workspace.DescribedRow);
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        workspace.ReturnTo(0);

        // Forward comes to the group with the process its way down went through, to the process with the connection once it
        // reads its rows again, described in the inspector as it was, and to E.
        Assert.True(workspace.GoForward());
        Assert.Equal(process, workspace.SelectedRung?.Key);
        Assert.True(workspace.GoForward());
        Assert.Null(workspace.SelectedRung);
        await workspace.ConnectionsReady;
        Assert.Equal((later.Key, later.Key), (workspace.SelectedRung?.Key, workspace.DescribedRow?.Key));
        Assert.True(workspace.GoForward());
        await workspace.EvidenceReady;
        Assert.True(workspace.IsEvidenceRung);

        // Left again before it has read its rows, the process rung keeps the row it was still to choose.
        Assert.True(workspace.Ascend());
        Assert.Null(workspace.SelectedRung);
        Assert.True(workspace.Ascend());
        Assert.True(workspace.GoForward());
        await workspace.ConnectionsReady;
        Assert.Equal(later.Key, workspace.SelectedRung?.Key);

        // Left with no row chosen, the process rung is entered again by forward with none, its rows read or not.
        workspace.SelectedRung = null;
        Assert.True(workspace.Ascend());
        Assert.True(workspace.GoForward());
        await workspace.ConnectionsReady;
        Assert.Null(workspace.SelectedRung);

        // A relationship opened from the machine runs the ladder through its process's group and the process: each rung is
        // left with the row the way went through, which forward chooses once the breadcrumb has passed them by.
        workspace.ReturnTo(0);
        workspace.SelectedRelationship = Assert.Single(workspace.Relationships);
        Assert.True(workspace.OpenSelectedRelationship());
        string?[] path = [.. workspace.CaptureNavigation().Breadcrumb.Select(rung => rung.Focus?.Key)];
        workspace.ReturnTo(0);
        Assert.True(workspace.GoForward());
        Assert.Equal(path[2], workspace.SelectedRung?.Key);
        Assert.True(workspace.GoForward());
        await workspace.ConnectionsReady;
        Assert.Equal(path[3], workspace.SelectedRung?.Key);
    });

    [Fact(DisplayName = "§3.2: Esc back from E taken from a row its rung reads after it is shown - a connection to another host - chooses the row once the rung lists it again, unless another is chosen first")]
    public void TheWayBackWaitsForARowReadLater() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, RowsReachingAnotherHost());
        using WorkspaceViewModel workspace = Open(session);
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        DescendTo(workspace, client.GroupKey);
        DescendTo(workspace, client.Id.ToString());
        RungRow paired = Assert.Single(workspace.RungRows);
        await workspace.ConnectionsReady;
        RungRow later = Assert.Single(workspace.RungRows, row => row.Key != paired.Key);

        // E lists the connection's records; back from them the rung reads its rows again, and the connection is chosen
        // and described once it lists it, where the way back chose nothing.
        workspace.SelectedRung = later;
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.True(workspace.Ascend());
        Assert.Null(workspace.SelectedRung);
        await workspace.ConnectionsReady;
        Assert.Equal((later.Key, later.Key), (workspace.SelectedRung?.Key, workspace.DescribedRow?.Key));

        // A row chosen before then is the one chosen.
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.True(workspace.Ascend());
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == paired.Key);
        await workspace.ConnectionsReady;
        Assert.Equal(paired.Key, workspace.SelectedRung?.Key);
    });

    [Fact(DisplayName = "§3.2: the breadcrumb up lands where the way down from its rung began, as Esc does - on the row the way went through, once the rung lists it")]
    public void TheBreadcrumbUpChoosesTheRowTheWayWentThrough() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, RowsReachingAnotherHost());
        using WorkspaceViewModel workspace = Open(session);
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        DescendTo(workspace, client.GroupKey);
        DescendTo(workspace, client.Id.ToString());
        RungRow paired = Assert.Single(workspace.RungRows);
        await workspace.ConnectionsReady;
        RungRow later = Assert.Single(workspace.RungRows, row => row.Key != paired.Key);
        workspace.SelectedRung = later;
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;

        // The crumb to the process: its connection to another host, once the rung reads its rows again.
        workspace.ReturnTo(2);
        Assert.Null(workspace.SelectedRung);
        await workspace.ConnectionsReady;
        Assert.Equal(later.Key, workspace.SelectedRung?.Key);

        // The crumb to the machine: the group the way went through, as Esc twice from the process chooses it.
        workspace.ReturnTo(0);
        Assert.Equal((client.GroupKey, client.GroupKey), (workspace.SelectedRung?.Key, workspace.SelectedGroup?.Key));
        Assert.True(workspace.GoForward());
        Assert.True(workspace.GoForward());
        Assert.True(workspace.Ascend());
        Assert.Equal(client.Id.ToString(), workspace.SelectedRung?.Key);
        Assert.True(workspace.Ascend());
        Assert.Equal(client.GroupKey, workspace.SelectedRung?.Key);
    });

    [Fact(DisplayName = "§6.7: a descent elsewhere or a removed filter ends forward history; the next rung is always named")]
    public void ADescentElsewhereOrARemovedFilterEndsForwardHistory()
    {
        using var workspace = new WorkspaceViewModel(SyntheticWorkspace.Create(), "session:one:generation:1");
        Assert.True(workspace.RungRows.Count > 1);
        workspace.SelectedRung = workspace.RungRows[0];
        Assert.True(workspace.Descend());
        string group = workspace.Crumbs[^1].Label;
        workspace.SelectedRung = workspace.RungRows[0];
        Assert.True(workspace.Descend());
        string process = workspace.Crumbs[^1].Label;

        // A crumb leaves both rungs; each step names the next one down.
        workspace.ReturnTo(0);
        Assert.Equal($"Forward to {group} (Alt+Right)", workspace.ForwardLabel);
        Assert.True(workspace.GoForward());
        Assert.Equal($"Forward to {process} (Alt+Right)", workspace.ForwardLabel);

        // Taking a filter off here would come back on the process, so the way forward ends.
        workspace.SelectedFilter = workspace.Filters[0];
        Assert.True(workspace.RemoveSelectedFilter());
        Assert.False(workspace.CanGoForward);

        // Enter on the row forward names keeps the way; Enter on another row ends it.
        Assert.True(workspace.Ascend());
        Assert.Equal($"Forward to {group} (Alt+Right)", workspace.ForwardLabel);
        workspace.SelectedRung = workspace.RungRows[1];
        Assert.True(workspace.Descend());
        Assert.False(workspace.CanGoForward);
        Assert.False(workspace.GoForward());
    }

    [Fact(DisplayName = "§6.7: forward history survives a live publication as far as the new generation still has its rungs")]
    public void ForwardHistorySurvivesAPublication() => SingleThreadedContext.Run(() =>
    {
        WorkspaceSnapshot source = SyntheticWorkspace.Create();
        using var old = new WorkspaceViewModel(source, "session:one:generation:1");
        for (int depth = 0; depth < 3; depth++)
        {
            old.SelectedRung = old.RungRows[0];
            Assert.True(old.Descend());
        }

        string[] deepest = [.. old.Crumbs.Select(crumb => crumb.Label)];
        old.ReturnTo(0);
        WorkspaceNavigationMemento saved = old.CaptureNavigation();
        Assert.Equal(3, saved.Forward?.Count);

        // A later generation that still has every rung, over a longer extent: each step comes back.
        using (var refreshed = new WorkspaceViewModel(
            source with { Extent = new TimeRange(0, 30 * WorkspaceTime.TicksPerSecond) }, "session:one:generation:2"))
        {
            Assert.Null(refreshed.RestoreNavigation(saved));
            Assert.Equal(old.ForwardLabel, refreshed.ForwardLabel);
            while (refreshed.GoForward())
            {
            }

            Assert.Equal(deepest, refreshed.Crumbs.Select(crumb => crumb.Label));
        }

        // A generation without the process: the way forward stops at the group above it, and says nothing about a
        // rung nobody is looking at - nor about the process, the breadcrumb back having chosen the group the way went
        // through, as Esc does.
        string process = saved.Forward![1].Focus!.Value.Key;
        Assert.Equal(saved.Forward[0].Focus!.Value.Key, saved.SelectedRungKey);
        WorkspaceSnapshot without = source with
        {
            Processes = [.. source.Processes.Where(node => node.Id.ToString() != process)],
            Edges = [.. source.Edges.Where(edge => edge.SourceId.ToString() != process && edge.TargetId.ToString() != process)],
        };
        using var thinner = new WorkspaceViewModel(without, "session:one:generation:3");
        Assert.Null(thinner.RestoreNavigation(saved));
        Assert.Equal($"Forward to {deepest[1]} (Alt+Right)", thinner.ForwardLabel);
        Assert.True(thinner.GoForward());
        Assert.False(thinner.CanGoForward);
        Assert.Equal(deepest.Take(2), thinner.Crumbs.Select(crumb => crumb.Label));
        return Task.CompletedTask;
    });

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

    /// <summary>A client and a server exchanging over one paired connection, and one one-sided flow elsewhere.</summary>
    private static ObservationRowV1[] Rows() =>
    [
        Timed(Lifecycle(1, ObservationKind.Create, 100, 1)),
        Timed(Lifecycle(2, ObservationKind.Create, 200, 2)),
        .. Enumerable.Range(0, Exchanges).SelectMany(index => new[]
        {
            Timed(Transfer(10 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 64, 100,
                (ulong)(100 + (2 * index))).Between(ClientEnd, ServerEnd)),
            Timed(Transfer(11 + (2 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200,
                (ulong)(101 + (2 * index))).Between(ServerEnd, ClientEnd)),
        }),
        Timed(Transfer(4_000, ObservationKind.Send, AccountingSide.SendSide, 3, 300, 8_000)
            .Between("127.0.0.1:50001", "127.0.0.1:9090")),
    ];

    /// <summary>
    /// <see cref="Rows"/>, and the client's one send to another host, a connection its process rung reads after it is shown.
    /// </summary>
    private static ObservationRowV1[] RowsReachingAnotherHost() =>
    [
        .. Rows(),
        Timed(Transfer(4_100, ObservationKind.Send, AccountingSide.SendSide, 5, 100, 8_100)
            .Between("192.168.1.5:50002", "93.184.216.34:443")),
    ];

    /// <summary>A row whose session time is its reading in workspace ticks, so the timeline can place it.</summary>
    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };
}
