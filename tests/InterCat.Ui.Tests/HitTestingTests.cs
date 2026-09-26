using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using Xunit;

namespace InterCat.Ui.Tests;

/// <summary>
/// R13 and P22 (§6.2): a hit test answers through data space - the instant under the pointer and the bucket holding it,
/// or the mark drawn there now - never through the ink drawn nearest, and never through geometry that has since moved.
/// </summary>
public sealed class HitTestingTests
{
    [AvaloniaFact(DisplayName = "R13: a timeline hit resolves by time to the bucket holding the instant under the pointer, not to the bar drawn nearest")]
    public async Task ATimelineHitResolvesByTime()
    {
        (MainWindow window, WorkspaceViewModel viewModel, TimelineView timeline) = await Open(1080);
        IReadOnlyList<TimelineBucket> buckets = viewModel.Snapshot.Timeline;
        int index = Enumerable.Range(1, buckets.Count - 1).First(candidate =>
            buckets[candidate - 1].ObservationCount > 0 && buckets[candidate].ObservationCount > 0
            && buckets[candidate - 1].Interval.EndTicks == buckets[candidate].Interval.StartTicks);
        TimelineBucket earlier = buckets[index - 1];
        TimelineBucket later = buckets[index];
        double boundary = timeline.PlotStart
            + ViewportMath.PixelAtTick(timeline.Viewport, later.Interval.StartTicks, timeline.PlotSpan);
        double y = timeline.PointOf(earlier)!.Value.Y;

        // A bar stops 2 px short of its bucket's end. That unpainted gap is still the earlier bucket's time, and the card
        // states the bucket's own half-open interval rather than anything read off pixels.
        Point gap = At(timeline, window, new(boundary - 1, y));
        window.MouseMove(gap);
        Dispatch();
        Assert.Equal(earlier, timeline.HoveredBucket);
        Assert.Equal(WorkspaceTime.FormatHalfOpenRange(earlier.Interval, CultureInfo.CurrentCulture), timeline.HoverCard?.Title);

        window.MouseMove(At(timeline, window, new(boundary + 1, y)));
        Dispatch();
        Assert.Equal(later, timeline.HoveredBucket);

        // A click in the gap selects the earlier bucket's exact interval: its records, not a pixel's worth of time.
        window.MouseMove(gap);
        window.MouseDown(gap, MouseButton.Left);
        window.MouseUp(gap, MouseButton.Left);
        Dispatch();
        Assert.Equal(earlier.Interval, viewModel.SelectedInterval);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "P22: a zoom, a resize or a newer generation under a resting pointer re-answers the timeline's hover for what is there now")]
    public async Task TheTimelineHoverFollowsTheDrawingUnderARestingPointer()
    {
        (MainWindow window, WorkspaceViewModel viewModel, TimelineView timeline) = await Open(1080);
        WorkspaceSnapshot source = viewModel.Snapshot;
        TimelineBucket first = source.Timeline.First(bucket => bucket.ObservationCount > 0
            && bucket.Interval.StartTicks > source.Extent.StartTicks + (source.Extent.SpanTicks / 8));
        window.MouseMove(At(timeline, window, timeline.PointOf(first)!.Value));
        Dispatch();
        Assert.Equal(first, timeline.HoveredBucket);
        int changes = 0;
        timeline.HoverChanged += (_, _) => changes++;

        // A zoom onto the extent's second half moves that bucket out from under the pointer.
        timeline.SetViewport(new TimeRange(source.Extent.StartTicks + (source.Extent.SpanTicks / 2), source.Extent.EndTicks));
        Settle(window);
        Assert.NotEqual(first, timeline.HoveredBucket);
        AnswersForTheInstantUnderThePointer(timeline);
        Assert.True(changes > 0, "The card's layer was not told that the drawing moved under the pointer.");

        // A wider window stretches the whole-extent plot under the same point.
        timeline.SetViewport(null);
        Settle(window);
        TimelineBucket? before = timeline.HoveredBucket;
        window.Width = 1456;
        Settle(window);
        AnswersForTheInstantUnderThePointer(timeline);
        Assert.NotEqual(before, timeline.HoveredBucket);

        // A newer generation whose extent is twice as long draws the same instant half as far along.
        using var longer = new WorkspaceViewModel(
            source with { Extent = new TimeRange(source.Extent.StartTicks, source.Extent.EndTicks + source.Extent.SpanTicks) },
            "generation-2");
        before = timeline.HoveredBucket;
        window.DataContext = longer;
        Settle(window);
        AnswersForTheInstantUnderThePointer(timeline);
        Assert.NotEqual(before, timeline.HoveredBucket);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "P22: a pin or a resize that moves graph nodes under a resting pointer re-answers its hover for the mark there now")]
    public async Task TheGraphHoverFollowsTheDrawingUnderARestingPointer()
    {
        var viewModel = new WorkspaceViewModel(SyntheticWorkspace.Create(), "generation-1");
        var window = new MainWindow(viewModel) { Width = 1456, Height = 939 };
        window.Show();
        await viewModel.LayoutReady;
        Settle(window);
        GraphView graph = window.GetControl<GraphView>("GraphSurface");

        // A node the pointer can rest on, far enough right that a narrower window moves it well away.
        GraphDisplayNode node = viewModel.GraphDisplay.Nodes
            .Where(candidate => graph.PointOf(candidate.Key) is { X: > 500 })
            .OrderByDescending(candidate => candidate.Observations)
            .First();
        GraphDisplayNode other = viewModel.GraphDisplay.Nodes.First(candidate => candidate.Key != node.Key
            && !candidate.Label.StartsWith(node.Label, StringComparison.Ordinal));
        GraphPoint place = viewModel.DisplayPositions[node.Key];
        window.MouseMove(At(graph, window, graph.PointOf(node.Key)!.Value));
        Dispatch();
        Assert.Equal(node.Key, graph.HoveredKey);
        Assert.StartsWith(node.Label, graph.HoverCard!.Title, StringComparison.Ordinal);

        // Another node pinned to its place and the node itself pinned into a far corner: the pointer, which never moved,
        // now rests on the other node, and the card describes that one. The ink under the pointer never went away, so
        // nothing but the pane itself can notice that the mark it stands for changed.
        Assert.True(viewModel.PinGraphNode(other.Key, place));
        Assert.True(viewModel.PinGraphNode(node.Key, new GraphPoint(0.02, 0.98)));
        Settle(window);
        Assert.Equal(other.Key, graph.HoveredKey);
        Assert.StartsWith(other.Label, graph.HoverCard!.Title, StringComparison.Ordinal);
        Assert.True(viewModel.UnpinGraphNode(other.Key));

        // Released and hovered again, a narrower window carries it away just the same.
        Assert.True(viewModel.UnpinGraphNode(node.Key));
        Assert.True(viewModel.RelayoutGraph());
        await viewModel.LayoutReady;
        Settle(window);
        Point resting = graph.PointOf(node.Key)!.Value;
        window.MouseMove(At(graph, window, resting));
        Dispatch();
        Assert.Equal(node.Key, graph.HoveredKey);
        window.Width = 1000;
        Settle(window);
        Point moved = graph.PointOf(node.Key)!.Value;
        Assert.True(Distance(moved, graph.HoverPoint) > 40, "The resize did not move the node away from the pointer.");
        Assert.NotEqual(node.Key, graph.HoveredKey);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§6.3: a press on empty graph space gives the graph its keyboard focus, and selects nothing")]
    public async Task APressOnEmptyGraphSpaceFocusesTheGraph()
    {
        var viewModel = new WorkspaceViewModel(SyntheticWorkspace.Create(), "generation-1");
        var window = new MainWindow(viewModel) { Width = 1456, Height = 939 };
        window.Show();
        await viewModel.LayoutReady;
        Settle(window);
        GraphView graph = window.GetControl<GraphView>("GraphSurface");
        window.GetControl<ListBox>("RungList").Focus();
        ProcessNode? selected = viewModel.SelectedProcess;

        // The pane's corner holds no mark; pressing it still reaches the graph, whose arrow keys, P and L then work.
        Point empty = At(graph, window, new(2, 2));
        window.MouseMove(empty);
        Dispatch();
        Assert.Null(graph.HoveredKey);
        window.MouseDown(empty, MouseButton.Left);
        window.MouseUp(empty, MouseButton.Left);
        Settle(window);
        Assert.True(graph.IsFocused, "A press on empty graph space left the keyboard focus elsewhere.");
        Assert.Same(selected, viewModel.SelectedProcess);
        window.Close();
    }

    private static async Task<(MainWindow Window, WorkspaceViewModel ViewModel, TimelineView Timeline)> Open(double width)
    {
        var viewModel = new WorkspaceViewModel(SyntheticWorkspace.Create(), "generation-1");
        var window = new MainWindow(viewModel) { Width = width, Height = 700 };
        window.Show();
        await viewModel.LayoutReady;
        Settle(window);
        return (window, viewModel, window.GetControl<TimelineView>("TimelineSurface"));
    }

    /// <summary>The hover names the bucket holding the instant the pointer rests on now, and the card states it.</summary>
    private static void AnswersForTheInstantUnderThePointer(TimelineView timeline)
    {
        long tick = ViewportMath.TickAtPixel(timeline.Viewport,
            Math.Clamp(timeline.HoverPoint.X - timeline.PlotStart, 0, timeline.PlotSpan), timeline.PlotSpan);
        TimelineBucket hovered = Assert.IsType<TimelineBucket>(timeline.HoveredBucket);
        Assert.True(hovered.Interval.Contains(tick),
            string.Create(CultureInfo.InvariantCulture, $"The hover names [{hovered.Interval.StartTicks}, "
                + $"{hovered.Interval.EndTicks}) while the pointer rests on tick {tick}."));
        Assert.Equal(WorkspaceTime.FormatHalfOpenRange(hovered.Interval, CultureInfo.CurrentCulture), timeline.HoverCard?.Title);
    }

    private static double Distance(Point first, Point second) =>
        Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));

    private static Point At(Control control, Window window, Point point) => control.TranslatePoint(point, window)!.Value;

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    /// <summary>Runs the bindings and a layout pass, so what the tests read is what is drawn.</summary>
    private static void Settle(Window window)
    {
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
    }
}
