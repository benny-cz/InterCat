using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// Two processes' TCP and UDP in the window (§6.3): two edges between the same two nodes, drawn side by side rather than
/// one over the other, so a hover on either finds that one and its card counts its own transport's records.
/// </summary>
public sealed class PairedUdpWindowTests
{
    [AvaloniaFact(DisplayName = "§6.3: two processes' TCP and UDP run side by side in the window, and a hover on either finds that one")]
    public async Task TwoProcessesTcpAndUdpRunSideBySide()
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
            GraphDisplayEdge tcp = workspace.GraphDisplay.Edges.Single(edge => edge.Mechanism == Mechanism.Tcp);
            GraphDisplayEdge udp = workspace.GraphDisplay.Edges.Single(edge => edge.Mechanism == Mechanism.Udp);
            Assert.Equal((tcp.SourceKey, tcp.TargetKey), (udp.SourceKey, udp.TargetKey));
            Assert.Equal((-0.5, 0.5), (tcp.Lane, udp.Lane));
            Save(window, "graph-paired-udp.png");

            // Each is found where it is drawn, 8 px from the other, and its card counts its own transport's records.
            foreach ((GraphDisplayEdge edge, string counted) in new[] { (tcp, "Paired TCP observations: 2"), (udp, "Paired UDP observations: 4") })
            {
                window.MouseMove(At(graph, window, OnLane(graph, edge)));
                Dispatch();
                Assert.Equal(edge.Key, graph.HoveredKey);
                Assert.StartsWith(counted, Assert.IsType<HoverCard>(graph.HoverCard).Lines[4], StringComparison.Ordinal);
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The middle of a relationship's edge as it is drawn: beside the line between its nodes by its lane, 8 px apart.</summary>
    private static Point OnLane(GraphView graph, GraphDisplayEdge edge)
    {
        Point source = graph.PointOf(edge.SourceKey)!.Value;
        Point target = graph.PointOf(edge.TargetKey)!.Value;
        Vector along = target - source;
        double length = Math.Sqrt((along.X * along.X) + (along.Y * along.Y));
        Vector across = new(-along.Y * edge.Lane * 8 / length, along.X * edge.Lane * 8 / length);
        return new Point((source.X + target.X) / 2, (source.Y + target.Y) / 2) + across;
    }

    /// <summary>client.exe and resolver.exe: one TCP message, and a UDP question and its answer, sender first (ADR-019).</summary>
    private static ObservationRowV1[] Rows() =>
    [
        Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 100 },
        Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\resolver.exe", SessionRelativeTicks = 110 },
        Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 3)
            .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 200 },
        Transfer(21, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 4)
            .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 210 },
        Datagram(30, ObservationKind.Send, AccountingSide.SendSide, 40, 100, 5).Between("127.0.0.1:61000", "127.0.0.1:53"),
        Datagram(31, ObservationKind.Receive, AccountingSide.ReceiveSide, 40, 200, 6).Between("127.0.0.1:61000", "127.0.0.1:53"),
        Datagram(32, ObservationKind.Send, AccountingSide.SendSide, 120, 200, 7).Between("127.0.0.1:53", "127.0.0.1:61000"),
        Datagram(33, ObservationKind.Receive, AccountingSide.ReceiveSide, 120, 100, 8).Between("127.0.0.1:53", "127.0.0.1:61000"),
    ];

    private static ObservationRowV1 Datagram(long ticks, ObservationKind kind, AccountingSide side, long bytes, int owner, ulong ordinal) =>
        Transfer(ticks, kind, side, bytes, owner, ordinal) with
        {
            Mechanism = Mechanism.Udp, EventId = 42, SessionRelativeTicks = ticks * 10,
        };

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
