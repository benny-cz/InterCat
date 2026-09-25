using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using InterCat.Application;
using InterCat.Desktop;
using Xunit;

namespace InterCat.Ui.Tests;

/// <summary>
/// The graph's hover (§6.2's hover contract, §6.3): the pointer over a node or an edge draws a card that states what the
/// mark stands for, its scope, value, unmeasured part and scale, and hover highlights only - it never selects.
/// </summary>
public sealed class GraphHoverTests
{
    [AvaloniaFact(DisplayName = "§6.2: hovering a node or an edge describes it, and hover never changes the selection")]
    public async Task HoverDescribesWithoutSelecting()
    {
        var viewModel = new WorkspaceViewModel(SyntheticWorkspace.Create(), "generation-1");
        var window = new MainWindow(viewModel) { Width = 1456, Height = 939 };
        window.Show();
        await viewModel.LayoutReady;
        Dispatch();
        GraphView graph = window.GetControl<GraphView>("GraphSurface");
        ProcessNode? selected = viewModel.SelectedProcess;

        GraphDisplayNode node = viewModel.GraphDisplay.Nodes.OrderByDescending(candidate => candidate.Observations).First();
        window.MouseMove(At(graph, window, graph.PointOf(node.Key)!.Value));
        Dispatch();
        Assert.Equal(node.Key, graph.HoveredKey);
        GraphHoverCard nodeCard = Assert.IsType<GraphHoverCard>(graph.HoverCard);
        Assert.StartsWith(node.Label, nodeCard.Title, StringComparison.Ordinal);
        // The scope is the exact half-open range the numbers answer (§6.2), here the whole session.
        Assert.Contains(nodeCard.Lines, line => line.StartsWith("Scope: [", StringComparison.Ordinal)
            && line.EndsWith(") s · whole session", StringComparison.Ordinal));
        Assert.Contains(nodeCard.Lines, line => line.StartsWith("Size: log scale", StringComparison.Ordinal));
        Assert.Same(selected, viewModel.SelectedProcess);
        Save(window, "graph-hover-node.png");

        GraphDisplayEdge edge = viewModel.GraphDisplay.Edges.OrderByDescending(candidate => candidate.ObservationCount).First();
        Point source = graph.PointOf(edge.SourceKey)!.Value;
        Point target = graph.PointOf(edge.TargetKey)!.Value;
        window.MouseMove(At(graph, window, new((source.X + target.X) / 2, (source.Y + target.Y) / 2)));
        Dispatch();
        Assert.Equal(edge.Key, graph.HoveredKey);
        GraphHoverCard edgeCard = Assert.IsType<GraphHoverCard>(graph.HoverCard);
        Assert.Contains(" ↔ ", edgeCard.Title, StringComparison.Ordinal);
        Assert.Contains(edgeCard.Lines, line => line.StartsWith("Thickness: log scale", StringComparison.Ordinal));
        Assert.Same(selected, viewModel.SelectedProcess);
        Save(window, "graph-hover-edge.png");

        window.MouseMove(At(graph, window, new(2, 2)));
        Dispatch();
        Assert.Null(graph.HoveredKey);
        Assert.Null(graph.HoverCard);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§6.3: dragging a node pins it where it is dropped, and a click only selects")]
    public async Task DraggingANodePinsItWhereItIsDropped()
    {
        var viewModel = new WorkspaceViewModel(SyntheticWorkspace.Create(), "generation-1");
        var window = new MainWindow(viewModel) { Width = 1456, Height = 939 };
        window.Show();
        await viewModel.LayoutReady;
        Dispatch();
        GraphView graph = window.GetControl<GraphView>("GraphSurface");
        GraphDisplayNode node = viewModel.GraphDisplay.Nodes.OrderBy(candidate => candidate.Key, StringComparer.Ordinal).First();
        Point from = graph.PointOf(node.Key)!.Value;

        // A press that does not travel past the drag threshold is a click: it selects and pins nothing.
        window.MouseDown(At(graph, window, from), MouseButton.Left);
        window.MouseMove(At(graph, window, new(from.X + 2, from.Y + 1)));
        window.MouseUp(At(graph, window, new(from.X + 2, from.Y + 1)), MouseButton.Left);
        Dispatch();
        Assert.Equal(node.Key, viewModel.SelectedGraphNodeKey);
        Assert.DoesNotContain(node.Key, viewModel.PinnedGraphNodeKeys);

        Point to = new(from.X + 60, from.Y + 30);
        window.MouseDown(At(graph, window, from), MouseButton.Left);
        window.MouseMove(At(graph, window, new(from.X + 20, from.Y + 10)));
        window.MouseMove(At(graph, window, to));
        window.MouseUp(At(graph, window, to), MouseButton.Left);
        Dispatch();
        await viewModel.LayoutReady;
        Dispatch();

        Assert.Contains(node.Key, viewModel.PinnedGraphNodeKeys);
        Point drawn = graph.PointOf(node.Key)!.Value;
        Assert.InRange(Math.Abs(drawn.X - to.X), 0, 1);
        Assert.InRange(Math.Abs(drawn.Y - to.Y), 0, 1);
        Assert.Equal("Unpin node (P)", window.GetControl<Button>("PinNodeButton").Content);
        Save(window, "graph-pinned.png");
        window.Close();
    }

    private static Point At(Control control, Window window, Point point) => control.TranslatePoint(point, window)!.Value;

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    private static void Save(Window window, string name)
    {
        Avalonia.Media.Imaging.WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        string directory = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(directory);
        frame.Save(Path.Combine(directory, name));
    }
}
