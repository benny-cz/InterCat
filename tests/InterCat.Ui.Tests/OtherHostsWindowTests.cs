using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// The window's Other hosts node (§6.3): drawn and named where a process's connections run to other hosts, its edges a
/// transport each, side by side, hovered and clicked as any mark is, and reached by the keyboard after every process.
/// </summary>
public sealed class OtherHostsWindowTests
{
    [AvaloniaFact(DisplayName = "§6.3: the window draws Other hosts, named, its edges a transport each that a hover describes, a click chooses the process of and a double click opens")]
    public async Task TheWindowDrawsOtherHosts()
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
            Dispatch();
            _ = window.CaptureRenderedFrame();
            GraphView graph = window.GetControl<GraphView>("GraphSurface");
            GraphDisplayNode hub = workspace.GraphDisplay.OtherHosts!;
            Point at = graph.PointOf(hub.Key)!.Value;

            // It is named where it is drawn, by what it stands for and how many drawn processes reach it.
            Assert.Equal("from 1 process", GraphView.Detail(hub));
            Assert.Contains(graph.PlacedLabels, box => Math.Abs(box.Center.X - at.X) < 80 && Math.Abs(box.Center.Y - at.Y) < 80);
            Save(window, "graph-other-hosts.png");

            // A hover over it describes it, and selects nothing.
            ProcessNode? selected = workspace.SelectedProcess;
            window.MouseMove(At(graph, window, at));
            Dispatch();
            Assert.Equal(hub.Key, graph.HoveredKey);
            Assert.Equal("Other hosts", Assert.IsType<HoverCard>(graph.HoverCard).Title);
            Assert.Same(selected, workspace.SelectedProcess);

            // The browser's TCP and UDP run side by side; a hover on either finds that one.
            GraphDisplayEdge tcp = workspace.GraphDisplay.Edges.Single(edge => edge.ToHosts && edge.Mechanism == Mechanism.Tcp);
            GraphDisplayEdge udp = workspace.GraphDisplay.Edges.Single(edge => edge.ToHosts && edge.Mechanism == Mechanism.Udp);
            Assert.Equal((-0.5, 0.5), (tcp.Lane, udp.Lane));
            foreach (GraphDisplayEdge edge in new[] { tcp, udp })
            {
                window.MouseMove(At(graph, window, OnLane(graph, edge)));
                Dispatch();
                Assert.Equal(edge.Key, graph.HoveredKey);
                Assert.Equal("browser.exe · PID 300 → Other hosts", Assert.IsType<HoverCard>(graph.HoverCard).Title);
            }

            // A click on one chooses the browser; a double click opens it, whose rung lists each connection.
            Point onUdp = At(graph, window, OnLane(graph, udp));
            window.MouseDown(onUdp, MouseButton.Left);
            window.MouseUp(onUdp, MouseButton.Left);
            Dispatch();
            Assert.Equal(300, workspace.SelectedProcess?.ProcessId);
            window.MouseDown(onUdp, MouseButton.Left);
            window.MouseUp(onUdp, MouseButton.Left);
            Dispatch();
            Assert.EndsWith("browser.exe · PID 300", workspace.Crumbs[^1].Label, StringComparison.Ordinal);

            // There the keyboard reaches the browser, then Other hosts, then the rest of the machine, and says each.
            await workspace.LayoutReady;
            Dispatch();
            Assert.Equal([GraphNodeKind.Process, GraphNodeKind.OtherHosts, GraphNodeKind.Context],
                GraphView.Traversal(workspace.GraphDisplay).Select(node => node.Kind));
            Assert.True(graph.Focus());
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Control);
            Dispatch();
            AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(graph);
            Assert.Equal($"{GraphView.SpokenName}: Other hosts, Connections of 1 drawn process with no process of this capture "
                + "at their other end, most often another machine's", peer.GetName());
            Save(window, "graph-other-hosts-rung.png");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The middle of an edge as it is drawn: beside the line between its nodes by its lane.</summary>
    private static Point OnLane(GraphView graph, GraphDisplayEdge edge)
    {
        Point source = graph.PointOf(edge.SourceKey)!.Value;
        Point target = graph.PointOf(edge.TargetKey)!.Value;
        Vector along = target - source;
        double length = Math.Sqrt((along.X * along.X) + (along.Y * along.Y));
        Vector across = new(-along.Y * edge.Lane * 4 / length, along.X * edge.Lane * 4 / length);
        return new Point((source.X + target.X) / 2, (source.Y + target.Y) / 2) + across;
    }

    /// <summary>
    /// client.exe and server.exe related over loopback; browser.exe's TCP to another host and its UDP to a resolver; and
    /// idle.exe, which does nothing the graph draws.
    /// </summary>
    private static ObservationRowV1[] Rows() =>
    [
        Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 100 },
        Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe", SessionRelativeTicks = 110 },
        Lifecycle(3, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 120 },
        Lifecycle(5, ObservationKind.Create, 500, 5) with { ResourceName = @"C:\Tools\idle.exe", SessionRelativeTicks = 140 },
        Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 6)
            .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 200 },
        Transfer(21, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 7)
            .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 210 },
        Transfer(22, ObservationKind.Send, AccountingSide.SendSide, 500, 300, 8)
            .Between("192.168.1.10:52000", "142.250.1.1:443") with { SessionRelativeTicks = 220 },
        Transfer(25, ObservationKind.Send, AccountingSide.SendSide, 40, 300, 11)
            .Between("192.168.1.10:61000", "8.8.8.8:53") with { Mechanism = Mechanism.Udp, EventId = 42, SessionRelativeTicks = 300 },
    ];

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
