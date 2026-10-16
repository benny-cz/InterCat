using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;
using static InterCat.Ui.Tests.RenderedPixels;

namespace InterCat.Ui.Tests;

/// <summary>
/// What a search finds, drawn in the graph (§6.7): a mark at a found node's upper right, filled where every process the
/// node holds is found and open where only some are, gone when the search is cleared.
/// </summary>
public sealed class GraphSearchMarkWindowTests
{
    [AvaloniaFact(DisplayName = "§6.7: the graph draws a search's mark on a node it finds, filled, on an aggregate it finds in part, open, and none once cleared")]
    public async Task TheGraphDrawsTheSearchMark()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
                Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            GraphView graph = window.GetControl<GraphView>("GraphSurface");
            WriteableBitmap plain = Settle(window);
            GraphDisplayNode browser = workspace.GraphDisplay.NodeOf(
                workspace.Snapshot.Processes.Single(process => process.ProcessId == 300).Id)!;
            GraphDisplayNode tools = workspace.GraphDisplay.Nodes.Single(node => node.Kind == GraphNodeKind.Quiet);

            // Where each node's mark is drawn - the middle of the browser's, the rim of the tools' - clear of every label.
            Point browserMark = Mark(graph, window, browser, 0);
            Point toolsRim = Mark(graph, window, tools, GraphView.SearchMarkRadius);
            Assert.DoesNotContain(graph.PlacedLabels, label => label.Inflate(2).Contains(window.TranslatePoint(browserMark, graph)!.Value)
                || label.Inflate(2).Contains(window.TranslatePoint(toolsRim, graph)!.Value));

            // The pane's own colour, where the graph draws nothing.
            Color pane = At(plain, graph.TranslatePoint(new Point(3, 3), window)!.Value);

            // A name finds the browser: its mark is filled in ink, which stands apart from the pane.
            workspace.SearchText = "browser";
            WriteableBitmap found = Settle(window);
            Assert.True(Distance(At(found, browserMark), pane) > 150, $"{At(found, browserMark)} against the pane's {pane}");

            // A PID finds one of the three tools: the aggregate's mark is open, its rim in ink and its middle the pane's.
            workspace.SearchText = "402";
            WriteableBitmap part = Settle(window);
            Point toolsMiddle = Mark(graph, window, tools, 0);
            Assert.True(Distance(At(part, toolsRim), pane) > 100, $"{At(part, toolsRim)} against the pane's {pane}");
            Assert.True(Distance(At(part, toolsMiddle), pane) < 40, $"{At(part, toolsMiddle)} against the pane's {pane}");
            Assert.True(Distance(At(part, browserMark), At(plain, browserMark)) < 10);

            // With the keyboard on a node, a screen reader is told what the mark shows, and the ring of the hit chosen.
            GraphDisplayNode server = workspace.GraphDisplay.NodeOf(
                workspace.Snapshot.Processes.Single(process => process.ProcessId == 200).Id)!;
            AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(graph);
            graph.Focus();
            workspace.SelectGraphNode(tools.Key);
            Dispatch();
            Assert.EndsWith(", partly found by the search, holding the hit chosen in the search", peer.GetName(), StringComparison.Ordinal);
            workspace.SearchText = "browser";
            workspace.SelectGraphNode(browser.Key);
            Dispatch();
            Assert.EndsWith(", found by the search, the hit chosen in the search", peer.GetName(), StringComparison.Ordinal);
            workspace.SelectGraphNode(server.Key);
            Dispatch();
            Assert.DoesNotContain("found by the search", peer.GetName(), StringComparison.Ordinal);

            // Cleared, no node carries a mark.
            workspace.SelectedProcess = null;
            workspace.SearchText = string.Empty;
            WriteableBitmap cleared = Settle(window);
            Assert.True(Distance(At(cleared, browserMark), At(plain, browserMark)) < 10);
            Assert.True(Distance(At(cleared, toolsRim), At(plain, toolsRim)) < 10);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§6.7: the graph rings the hit chosen among a search's hits, whole on its own node, broken on an aggregate holding it among others, and the ring follows the choice")]
    public async Task TheGraphRingsTheChosenHit()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
                Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            GraphView graph = window.GetControl<GraphView>("GraphSurface");
            WriteableBitmap plain = Settle(window);
            GraphDisplayNode browser = workspace.GraphDisplay.NodeOf(
                workspace.Snapshot.Processes.Single(process => process.ProcessId == 300).Id)!;
            GraphDisplayNode tools = workspace.GraphDisplay.Nodes.Single(node => node.Kind == GraphNodeKind.Quiet);

            // A name's first hit, browser.exe's group, is the browser's node: it is ringed whole, the tools' not at all.
            workspace.SearchText = "browser";
            WriteableBitmap group = Settle(window);
            Assert.InRange(Ringed(plain, group, graph, window, browser), 20, Bearings);
            Assert.InRange(Ringed(plain, group, graph, window, tools), 0, 2);

            // The first of tool.exe's hits is its group, every process the tools' aggregate holds: ringed whole.
            workspace.SearchText = "tool";
            WriteableBitmap tool = Settle(window);
            Assert.InRange(Ringed(plain, tool, graph, window, tools), 20, Bearings);
            Assert.InRange(Ringed(plain, tool, graph, window, browser), 0, 2);

            // One of the three chosen instead is held among others there: the ring is broken.
            workspace.SelectedSearchResult = workspace.SearchResults.Single(row => row.Label == "tool.exe · PID 402");
            WriteableBitmap one = Settle(window);
            Assert.InRange(Ringed(plain, one, graph, window, tools), 5, 17);

            // With the keyboard on that node, a screen reader is told what the ring shows.
            AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(graph);
            graph.Focus();
            workspace.SelectGraphNode(tools.Key);
            Dispatch();
            Assert.EndsWith(", found by the search, holding the hit chosen in the search", peer.GetName(), StringComparison.Ordinal);

            // Cleared, no node is ringed.
            workspace.ClearSelection();
            workspace.SearchText = string.Empty;
            WriteableBitmap cleared = Settle(window);
            Assert.InRange(Ringed(plain, cleared, graph, window, tools), 0, 2);
            Assert.InRange(Ringed(plain, cleared, graph, window, browser), 0, 2);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The bearings a chosen hit's ring is sampled at around a node.</summary>
    private const int Bearings = 24;

    /// <summary>
    /// At how many of <see cref="Bearings"/> points on the circle a chosen hit's ring is drawn on around <paramref name="node"/>
    /// the frame differs from the plain one: nearly all for a whole ring, about half for a broken one, none for none.
    /// </summary>
    private static int Ringed(WriteableBitmap plain, WriteableBitmap frame, GraphView graph, Window window, GraphDisplayNode node)
    {
        Point centre = graph.PointOf(node.Key)!.Value;
        double ring = graph.RadiusOf(node.Key)!.Value + GraphView.ChosenHitHalo;
        int differing = 0;
        for (int bearing = 0; bearing < Bearings; bearing++)
        {
            double angle = 2 * Math.PI * (bearing + 0.5) / Bearings;
            Point at = graph.TranslatePoint(new Point(centre.X + (ring * Math.Cos(angle)), centre.Y + (ring * Math.Sin(angle))), window)!.Value;
            if (Distance(At(frame, at), At(plain, at)) > 60)
            {
                differing++;
            }
        }

        return differing;
    }

    /// <summary>A point of the search mark a node carries, <paramref name="across"/> to the right of its middle, in the window.</summary>
    private static Point Mark(GraphView graph, Window window, GraphDisplayNode node, double across)
    {
        Point mark = GraphView.SearchMarkAt(graph.PointOf(node.Key)!.Value, graph.RadiusOf(node.Key)!.Value);
        return graph.TranslatePoint(new Point(mark.X + across - 0.5, mark.Y), window)!.Value;
    }

    /// <summary>How far apart two colours are, channel by channel.</summary>
    private static int Distance(Color first, Color second) =>
        Math.Abs(first.R - second.R) + Math.Abs(first.G - second.G) + Math.Abs(first.B - second.B);

    private static WriteableBitmap Settle(Window window)
    {
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
        return window.CaptureRenderedFrame()!;
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    /// <summary>
    /// client.exe and server.exe related over loopback; browser.exe's TCP to another host; and three instances of
    /// tool.exe that do nothing the graph draws.
    /// </summary>
    private static ObservationRowV1[] Rows() =>
    [
        Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 100 },
        Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe", SessionRelativeTicks = 110 },
        Lifecycle(3, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 120 },
        .. Enumerable.Range(0, 3).Select(index => Lifecycle(4 + index, ObservationKind.Create, 401 + index, (ulong)(4 + index))
            with { ResourceName = @"C:\Tools\tool.exe", SessionRelativeTicks = 130 + index }),
        Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 10)
            .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 200 },
        Transfer(21, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 11)
            .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 200 },
        Transfer(22, ObservationKind.Send, AccountingSide.SendSide, 500, 300, 12)
            .Between("192.168.1.10:52000", "142.250.1.1:443") with { SessionRelativeTicks = 210 },
    ];
}
