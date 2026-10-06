using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
/// §6.7's Esc on a drag in progress: the gesture is cancelled where it began - a pan or a move of the minimap's brush
/// gives the view back, a brush being drawn or a node being dragged is dropped - and the rung stays. With no drag, Esc
/// ascends as the ladder's back does.
/// </summary>
public sealed class DragCancelWindowTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [AvaloniaFact(DisplayName = "§6.7: Esc cancels a drag in progress before it ascends: a pan or a minimap move returns, a brush or a node's drag is dropped")]
    public void EscCancelsADragInProgress()
    {
        // A client and a server exchanging every 100 ms for 20 s, opened at their group, so an Esc that ascended would show.
        using var session = new TemporarySession();
        (MainWindow window, WorkspaceViewModel workspace, TimelineView timeline) = OpenZoomedGroup(session);
        int depth = workspace.Crumbs.Count;
        TimeRange zoomed = timeline.Viewport;
        // Each gesture begins at a point of its own, so that none is the second press of a double click.
        Point At(double fraction) => PlotPoint(window, timeline, fraction);
        Point from = At(0.6);
        Point to = At(0.3);

        // A pan cancelled mid-drag gives back the view it began from; the rung stays, and the release selects nothing.
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(to);
        Assert.NotEqual(zoomed, timeline.Viewport);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.Equal(zoomed, timeline.Viewport);
        Assert.Equal(depth, workspace.Crumbs.Count);
        window.MouseMove(from);
        window.MouseUp(from, MouseButton.Left);
        Assert.Equal(zoomed, timeline.Viewport);
        Assert.Null(workspace.SelectedInterval);

        // A brush cancelled mid-drag selects no interval, and a press cancelled before its release selects no bucket.
        window.MouseDown(At(0.8), MouseButton.Left, RawInputModifiers.Shift);
        window.MouseMove(At(0.5), RawInputModifiers.Shift);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.MouseUp(At(0.5), MouseButton.Left, RawInputModifiers.Shift);
        Assert.Null(workspace.SelectedInterval);
        window.MouseDown(At(0.1), MouseButton.Left);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.MouseUp(At(0.1), MouseButton.Left);
        Assert.Null(workspace.SelectedInterval);
        Assert.Equal((zoomed, depth), (timeline.Viewport, workspace.Crumbs.Count));

        // The minimap's brush dragged, then cancelled, gives the view back; so does a press beside it, which centres the
        // view there.
        MinimapView minimap = window.GetControl<MinimapView>("MinimapSurface");
        (double start, double end) = minimap.Brush();
        double middle = minimap.Bounds.Height / 2;
        Point grab = minimap.TranslatePoint(new((start + end) / 2, middle), window)!.Value;
        window.MouseDown(grab, MouseButton.Left);
        window.MouseMove(grab + new Point(-60, 0));
        Assert.NotEqual(zoomed, timeline.Viewport);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.MouseUp(grab + new Point(-60, 0), MouseButton.Left);
        Assert.Equal((zoomed, depth), (timeline.Viewport, workspace.Crumbs.Count));
        Point beside = minimap.TranslatePoint(new(minimap.Bounds.Width - 20, middle), window)!.Value;
        window.MouseDown(beside, MouseButton.Left);
        Assert.NotEqual(zoomed, timeline.Viewport);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.MouseUp(beside, MouseButton.Left);
        Assert.Equal((zoomed, depth), (timeline.Viewport, workspace.Crumbs.Count));

        // A node's drag, cancelled, pins nothing; the press's selection stands.
        GraphView graph = window.GetControl<GraphView>("GraphSurface");
        string key = workspace.GraphDisplay.Nodes.First(node => node.Process is not null).Key;
        Point node = graph.TranslatePoint(graph.PointOf(key)!.Value, window)!.Value;
        window.MouseDown(node, MouseButton.Left);
        window.MouseMove(node + new Point(60, 40));
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.MouseUp(node + new Point(60, 40), MouseButton.Left);
        Assert.False(workspace.IsGraphNodePinned(key));
        Assert.Equal(key, workspace.SelectedGraphNodeKey);
        Assert.Equal(depth, workspace.Crumbs.Count);

        // With no drag in progress, Esc ascends as before.
        timeline.Focus();
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.Equal(depth - 1, workspace.Crumbs.Count);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§6.7: a double click follows only a plain click: a press where a pan, a brush or a cancelled drag began zooms or opens nothing")]
    public void ADoubleClickFollowsOnlyAPlainClick()
    {
        using var session = new TemporarySession();
        (MainWindow window, WorkspaceViewModel workspace, TimelineView timeline) = OpenZoomedGroup(session);
        int depth = workspace.Crumbs.Count;

        // Two plain clicks at one place still zoom in there.
        TimeRange before = timeline.Viewport;
        Point twice = PlotPoint(window, timeline, 0.5);
        Click(window, twice);
        Click(window, twice);
        Assert.True(timeline.Viewport.SpanTicks < before.SpanTicks);

        // A pan, then a press where it began: two gestures, of which the second is a click that zooms nothing.
        Point from = PlotPoint(window, timeline, 0.7);
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(PlotPoint(window, timeline, 0.4));
        window.MouseUp(PlotPoint(window, timeline, 0.4), MouseButton.Left);
        TimeRange panned = timeline.Viewport;
        Click(window, from);
        Assert.Equal(panned, timeline.Viewport);

        // So too after a brush, and after a press Esc cancelled.
        Point brushed = PlotPoint(window, timeline, 0.2);
        window.MouseDown(brushed, MouseButton.Left, RawInputModifiers.Shift);
        window.MouseMove(PlotPoint(window, timeline, 0.3), RawInputModifiers.Shift);
        window.MouseUp(PlotPoint(window, timeline, 0.3), MouseButton.Left, RawInputModifiers.Shift);
        Click(window, brushed);
        Assert.Equal(panned, timeline.Viewport);
        Point cancelled = PlotPoint(window, timeline, 0.9);
        window.MouseDown(cancelled, MouseButton.Left);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.MouseUp(cancelled, MouseButton.Left);
        Click(window, cancelled);
        Assert.Equal(panned, timeline.Viewport);
        Assert.Equal(depth, workspace.Crumbs.Count);

        // In the graph, a node's drag given up with Esc and begun again where it began opens nothing; a double click does.
        GraphView graph = window.GetControl<GraphView>("GraphSurface");
        string key = workspace.GraphDisplay.Nodes.First(node => node.Process is not null).Key;
        Point node = graph.TranslatePoint(graph.PointOf(key)!.Value, window)!.Value;
        window.MouseDown(node, MouseButton.Left);
        window.MouseMove(node + new Point(60, 40));
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.MouseUp(node + new Point(60, 40), MouseButton.Left);
        Click(window, node);
        Assert.Equal(depth, workspace.Crumbs.Count);

        // A click elsewhere first, so the two that follow are a double click of their own rather than a third press.
        Click(window, graph.TranslatePoint(new Point(8, 8), window)!.Value);
        Click(window, node);
        Click(window, node);
        Assert.Equal(depth + 1, workspace.Crumbs.Count);
        window.Close();
    }

    /// <summary>The client's and server's group, with the timeline zoomed in four steps so a pan has room both ways.</summary>
    private static (MainWindow Window, WorkspaceViewModel Workspace, TimelineView Timeline) OpenZoomedGroup(TemporarySession session)
    {
        Publish(session.Store,
        [
            .. new[]
            {
                Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\peer.exe" },
                Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\peer.exe" },
            }.Concat(Enumerable.Range(0, 200).SelectMany(index => new[]
            {
                Transfer(10 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 64, 100, (ulong)(100 + (2 * index)))
                    .Between(ClientEnd, ServerEnd),
                Transfer(11 + (2 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, (ulong)(101 + (2 * index)))
                    .Between(ServerEnd, ClientEnd),
            })).Select(row => row with { SessionRelativeTicks = row.NativeTicks * 50_000_000L }),
        ]);
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        window.ApplyCaptureUpdate(new CaptureUiUpdate(CaptureUiPhase.Complete, "Saved session open", "Saved.",
            SessionPath: session.Path, Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        workspace.SelectedRung = Assert.Single(workspace.RungRows);
        Assert.True(workspace.Descend());
        Dispatch();
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        timeline.Focus();
        for (int step = 0; step < 4; step++) window.KeyPressQwerty(PhysicalKey.Equal, RawInputModifiers.None);
        Render(window);
        Assert.True(timeline.Viewport.SpanTicks < workspace.Snapshot.Extent.SpanTicks);
        return (window, workspace, timeline);
    }

    /// <summary>A point at <paramref name="fraction"/> across the timeline's plot, halfway down, in the window.</summary>
    private static Point PlotPoint(Window window, TimelineView timeline, double fraction) =>
        timeline.TranslatePoint(new(timeline.PlotStart + (fraction * timeline.PlotSpan), timeline.Bounds.Height / 2), window)!.Value;

    private static void Click(Window window, Point point)
    {
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatch();
    }

    private static void Render(Window window)
    {
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
