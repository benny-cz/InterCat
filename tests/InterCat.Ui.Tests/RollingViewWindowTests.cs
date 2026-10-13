using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.CaptureBroker;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// The view across what replaces it (§6.7, ADR-045): a session shown afresh opens whole, and a rolling capture's newer
/// generation keeps the view inside the time the session keeps, saying once, in the window's words, what its release took.
/// </summary>
public sealed class RollingViewWindowTests
{
    [AvaloniaFact(DisplayName = "§6.7: a session opened after another was zoomed, or a capture's first view, opens whole, never at the other's time")]
    public async Task ASessionShownAfreshOpensWhole()
    {
        using var first = new TemporarySession();
        Publish(first.Store, Pair(0, 10, 100, 200, 50000));
        Publish(first.Store, Pair(100, 10, 300, 400, 50001));
        using var second = new TemporarySession();
        Publish(second.Store, Pair(5000, 100, 500, 600, 50002));
        using var captured = new TemporarySession();
        Publish(captured.Store, Pair(7000, 10, 700, 800, 50003));
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(Opened(first), forceOverview: true);
            WorkspaceViewModel workspace = await Shown(window);
            TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
            Zoom(window, timeline, workspace, 4);
            Assert.False(timeline.IsFit);

            // Opened again, it opens whole, as everything else of its view starts afresh.
            window.ApplyCaptureUpdate(Opened(first), forceOverview: true);
            workspace = await Shown(window);
            Assert.True(timeline.IsFit);
            Assert.Null(workspace.ScopeInterval);

            // Another session's time is not the first's, even where it is long enough to hold the zoom's span: it opens
            // whole, and its ranking counts all of it.
            Zoom(window, timeline, workspace, 4);
            window.ApplyCaptureUpdate(Opened(second), forceOverview: true);
            workspace = await Shown(window);
            Assert.True(timeline.IsFit);
            Assert.Equal(workspace.Snapshot.Extent, timeline.Viewport);
            Assert.Null(workspace.ScopeInterval);

            // A capture started while it is zoomed waits whole, and its first view opens whole.
            Zoom(window, timeline, workspace, 4);
            window.ForgetDisplayedSession();
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "Recording", "Recording."));
            Dispatch(window);
            Assert.True(Assert.IsType<WorkspaceViewModel>(window.DataContext).IsEmptyWorkspace);
            Assert.True(timeline.IsFit);
            window.ApplyCaptureUpdate(Generation(captured, 1));
            workspace = await Shown(window);
            Assert.True(timeline.IsFit);
            Assert.Equal(workspace.Snapshot.Extent, timeline.Viewport);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "ADR-045: a rolling capture's release that reaches the zoom moves it inside the time the session keeps, and says where the view went")]
    public async Task ARollingReleaseMovesTheZoomInside()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Pair(0, 10, 100, 200, 50000));
        Publish(session.Store, Pair(100, 10, 300, 400, 50001));
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(Generation(session, 1));
            WorkspaceViewModel first = await Shown(window);

            // The person opens the process whose records come first, and zooms into its time.
            ProcessNode early = first.Snapshot.Processes.Single(node => node.ProcessId == 100);
            foreach (string key in new[] { early.GroupKey, early.Id.ToString() })
            {
                first.SelectedRung = first.RungRows.Single(row => row.Key == key);
                Assert.True(first.Descend());
            }

            TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
            TimeRange extent = first.Snapshot.Extent;
            timeline.SetViewport(new TimeRange(extent.StartTicks, extent.StartTicks + 11));
            timeline.RequestDetailNow();
            Dispatch(window);
            Assert.Equal(new TimeRange(10, 21), first.ScopeInterval);

            // The capture keeps its newest stretch: its next generation released that process's records and the time in view.
            // The view goes back to the process's group, and its zoom keeps its span from where the session keeps every
            // record, which the ranking then counts; the card says both, once.
            _ = IntervalRelease.Release(session.Store, 10_000, "rolling retention keeps the last 10 minutes", DateTimeOffset.UtcNow);
            Publish(session.Store, Pair(110, 10, 300, 400, 50001));
            TextBlock detail = window.GetControl<TextBlock>("CaptureDetail");
            window.ApplyCaptureUpdate(Generation(session, 2));
            WorkspaceViewModel second = await Shown(window);
            Assert.Equal(new TimeRange(210, 250), second.Snapshot.Extent);
            Assert.Equal("Group: Executable not witnessed", second.Crumbs[^1].Label);
            Assert.Equal("Generation 2 · 2 published chunks. Process: PID 100 is no longer in the session, so the view went "
                + "back to Group: Executable not witnessed. The view showed time the session released, so it moved to the "
                + "earliest time the session keeps.", detail.Text);
            Assert.Equal((new TimeRange(210, 221), false), (timeline.Viewport, timeline.IsFit));
            Assert.Equal(new TimeRange(210, 221), second.ScopeInterval);

            // A generation that releases nothing keeps the zoom where it is, and says nothing of it.
            Publish(session.Store, Pair(120, 10, 300, 400, 50001));
            window.ApplyCaptureUpdate(Generation(session, 3));
            await Shown(window);
            Assert.Equal(new TimeRange(210, 221), timeline.Viewport);
            Assert.Equal("Generation 3 · 3 published chunks.", detail.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "ADR-045: a rolling capture's release that takes all the time in view shows all the session keeps, and says so with the selection it took")]
    public async Task ARollingReleaseOfAllInViewShowsAllTheSessionKeeps()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Pair(0, 10, 100, 200, 50000));
        Publish(session.Store, Pair(100, 10, 300, 400, 50001));
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(Generation(session, 1));
            WorkspaceViewModel first = await Shown(window);
            TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
            Zoom(window, timeline, first, 4);
            first.SelectedProcess = first.Snapshot.Processes.Single(node => node.ProcessId == 100);
            Dispatch(window);

            // The zoom is wider than all the session keeps after the release, so it is all of it; the process selected is
            // gone with its records, and nothing is selected.
            _ = IntervalRelease.Release(session.Store, 10_000, "rolling retention keeps the last 10 minutes", DateTimeOffset.UtcNow);
            Publish(session.Store, Pair(110, 10, 300, 400, 50001));
            window.ApplyCaptureUpdate(Generation(session, 2));
            WorkspaceViewModel second = await Shown(window);
            Assert.Equal("Generation 2 · 2 published chunks. The selected process is no longer in the session, so nothing "
                + "is selected. The view showed time the session released, so it shows all the time the session keeps.",
                window.GetControl<TextBlock>("CaptureDetail").Text);
            Assert.True(timeline.IsFit);
            Assert.Null(second.SelectedProcess);
            Assert.Null(second.ScopeInterval);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "ADR-045: a rolling capture's release says once what it took of the analysis interval and of the chosen mechanism's lane")]
    public async Task ARollingReleaseSaysOnceWhatItTook()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [.. Pair(0, 10, 100, 200, 50000), .. Datagrams(5)]);
        Publish(session.Store, Pair(100, 10, 300, 400, 50001));
        Publish(session.Store, Pair(200, 10, 300, 400, 50001));
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(Generation(session, 1));
            WorkspaceViewModel first = await Shown(window);

            // The person chooses the UDP lane and brushes from the first record into the second stretch, then opens the
            // group that holds every process.
            first.SelectTimelineLane(Mechanism.Udp);
            first.SelectInterval(new TimeRange(first.Snapshot.Extent.StartTicks, 220));
            await first.IntervalReady;
            first.SelectedRung = Assert.Single(first.RungRows);
            Assert.True(first.Descend());
            Dispatch(window);

            // The release took every UDP record and the brush's start: each is said once, the group's own time with the
            // brush that opened it.
            _ = IntervalRelease.Release(session.Store, 10_000, "rolling retention keeps the last 10 minutes", DateTimeOffset.UtcNow);
            Publish(session.Store, Pair(300, 10, 300, 400, 50001));
            window.ApplyCaptureUpdate(Generation(session, 2));
            WorkspaceViewModel second = await Shown(window);
            Assert.Equal("Generation 2 · 2 published chunks. The session no longer holds any UDP record, so the timeline "
                + "shows all mechanisms. Part of the analysis interval lay in time the session released, so it now covers the "
                + "rest.",
                window.GetControl<TextBlock>("CaptureDetail").Text);
            Assert.Equal(new TimeRange(210, 220), second.SelectedInterval);
            Assert.Null(second.SelectedTimelineMechanism);
            Assert.Equal("L1 · GROUP", second.LevelBadge);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§19.3: the live preview's card says how often the capture publishes, as its milestones say, and claims no rate they do not")]
    public void TheLivePreviewSaysHowOftenTheCapturePublishes()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Pair(0, 100, 100, 200, 50000));
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            // A capture keeping its last hour publishes every 5 s, not every 2 s, and its card says so.
            CaptureUiUpdate published = Generation(session, 1) with
            {
                OverviewChunks = 2,
                Milestones = new(PublicationInterval: TimeSpan.FromSeconds(5)),
            };
            window.ApplyCaptureUpdate(published);
            Dispatch(window);
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            var preview = new BrokerCapturePreview(20, 3, 1, 11, 11, 0,
                [new(2, 10, Mechanism.Tcp, 5), new(3, 11, Mechanism.Tcp, 4), new(3, 12, Mechanism.Tcp, 2)]);
            window.ApplyCaptureUpdate(published with { Overview = null, OverviewChunks = null, LivePreview = preview });
            Dispatch(window);
            Assert.Contains("Exact records replace this preview when the chunk holding them is published and derived here, "
                + "about every 5 s while recording", Hovered(window, workspace).Lines);

            // Milestones that do not say how often leave the card saying when, never a rate it was not told.
            window.ApplyCaptureUpdate(published with
            {
                Overview = null,
                OverviewChunks = null,
                LivePreview = preview,
                Milestones = new(),
            });
            Dispatch(window);
            Assert.Contains("Exact records replace this preview when the chunk holding them is published and derived here",
                Hovered(window, workspace).Lines);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The card of the live preview's first bin, hovered as a person hovers it.</summary>
    private static HoverCard Hovered(MainWindow window, WorkspaceViewModel workspace)
    {
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        LiveEdge edge = Assert.IsType<LiveEdge>(workspace.LiveEdge);
        window.MouseMove(new Point(1, 1));
        Dispatch(window);
        window.MouseMove(timeline.TranslatePoint(timeline.PointOfLive(edge.Bins[0])!.Value, window)!.Value);
        Dispatch(window);
        return Assert.IsType<HoverCard>(timeline.HoverCard);
    }

    /// <summary>Zooms the timeline to the first <paramref name="part"/>th of the session, as a settled gesture does.</summary>
    private static void Zoom(Window window, TimelineView timeline, WorkspaceViewModel workspace, int part)
    {
        TimeRange extent = workspace.Snapshot.Extent;
        timeline.SetViewport(new TimeRange(extent.StartTicks, extent.StartTicks + Math.Max(1, extent.SpanTicks / part)));
        timeline.RequestDetailNow();
        Dispatch(window);
    }

    private static async Task<WorkspaceViewModel> Shown(MainWindow window)
    {
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        Dispatch(window);
        return workspace;
    }

    private static CaptureUiUpdate Opened(TemporarySession session) => new(CaptureUiPhase.Complete, "Saved session open",
        "Saved.", SessionPath: session.Path, Overview: SessionOverviewProjector.Project(session.Store));

    /// <summary>A recording's newer generation, which restates its chunks.</summary>
    private static CaptureUiUpdate Generation(TemporarySession session, int generation) => new(CaptureUiPhase.Recording,
        $"{generation * 10} observed records", $"Generation {generation} · {generation} published chunks.",
        SessionPath: session.Path, Overview: SessionOverviewProjector.Project(session.Store));

    /// <summary>
    /// A client sending and a server receiving on one connection, <paramref name="count"/> times from
    /// <paramref name="first"/>: the records at ticks 10 + 2 × index and the one after it.
    /// </summary>
    private static ObservationRowV1[] Pair(int first, int count, int client, int server, int port) =>
    [
        .. Enumerable.Range(first, count).SelectMany(index => new[]
        {
            Transfer(10 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 64, client, (ulong)(100 + (2 * index)))
                .Between($"127.0.0.1:{port}", "127.0.0.1:8080") with { SessionRelativeTicks = (10 + (2 * index)) * 100L },
            Transfer(11 + (2 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 64, server,
                (ulong)(101 + (2 * index))).Between("127.0.0.1:8080", $"127.0.0.1:{port}")
                with { SessionRelativeTicks = (11 + (2 * index)) * 100L },
        }),
    ];

    /// <summary>The first process's UDP datagrams, among its first records.</summary>
    private static ObservationRowV1[] Datagrams(int count) =>
    [
        .. Enumerable.Range(0, count).Select(index =>
            Transfer(12 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 32, 100, (ulong)(120 + index))
                .Between("127.0.0.1:50100", "127.0.0.1:53") with
            {
                Mechanism = Mechanism.Udp,
                SessionRelativeTicks = (12 + (2 * index)) * 100L,
            }),
    ];

    private static void Dispatch(Window window)
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        _ = window.CaptureRenderedFrame();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }
}
