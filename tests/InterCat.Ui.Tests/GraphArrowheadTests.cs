using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.CaptureBroker;
using InterCat.Desktop;
using InterCat.Desktop.Theme;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;
using static InterCat.Ui.Tests.RenderedPixels;

namespace InterCat.Ui.Tests;

/// <summary>
/// Which process made a relationship's connections, as the graph draws it (§6.3, `contracts/relations-v1.md` §5): a filled
/// arrowhead at each end its connections ran to, in the mechanism's hue and wider than the stroke, and the stroke stopping
/// under it, so the head reads as one however thick the stroke is; and the direction key that says so.
/// </summary>
public sealed class GraphArrowheadTests
{
    [AvaloniaFact(DisplayName = "§6.3: an edge draws an arrowhead at each end its connections ran to, wider than its stroke, and its stroke stops under the head")]
    public void AnEdgeDrawsAnArrowheadWhereItsConnectionsRan()
    {
        // A level 6 px stroke from 20 to 180 between discs reaching 10 px along it. A head toward 180 has its tip at 169 and
        // its base at 154.6, 6 px either side of the line; one toward 20, its tip at 31 and its base at 45.4.
        Color hue = ThemeResources.FillOf(Mechanism.Tcp, ThemeResources.CurrentMode);
        WriteableBitmap oneWay = Drawn(fromSource: 1, fromTarget: 0);

        // Beside the stroke near the base, the head toward the end connected to; none at the other end.
        Assert.Equal(hue, Opaque(At(oneWay, new Point(156, 24))));
        Assert.Equal(hue, Opaque(At(oneWay, new Point(156, 15))));
        Assert.Equal(0, At(oneWay, new Point(43, 24)).A);

        // Near the tip, where the head is narrower than the stroke, the stroke has stopped: the edge of a stroke run on to
        // the disc would show here.
        Assert.Equal(0, At(oneWay, new Point(166, 22)).A);
        Assert.Equal(Colors.Red, Opaque(At(oneWay, new Point(100, 22))));

        // Both ways, a head at each end; none seen opening, no head at all, and the stroke runs on to the discs.
        WriteableBitmap both = Drawn(fromSource: 2, fromTarget: 1);
        Assert.Equal(hue, Opaque(At(both, new Point(156, 24))));
        Assert.Equal(hue, Opaque(At(both, new Point(43, 24))));
        Assert.Equal(0, At(both, new Point(34, 22)).A);
        WriteableBitmap unseen = Drawn(fromSource: 0, fromTarget: 0);
        Assert.Equal(0, At(unseen, new Point(156, 24)).A);
        Assert.Equal(0, At(unseen, new Point(43, 24)).A);
        Assert.Equal(Colors.Red, Opaque(At(unseen, new Point(166, 22))));
        Assert.Equal(Colors.Red, Opaque(At(unseen, new Point(34, 22))));

        // An unmeasured edge's band is as wide as the widest stroke, whatever pen it is given, so its head is as wide as
        // that stroke's; and the band stops under the head too, its side lines with it.
        WriteableBitmap band = Drawn(fromSource: 1, fromTarget: 0, unmeasured: true, thickness: 2);
        Assert.Equal(hue, Opaque(At(band, new Point(156, 24))));
        Assert.NotEqual(0, At(band, new Point(100, 23)).A);
        Assert.Equal(0, At(band, new Point(166, 23)).A);
    }

    [AvaloniaFact(DisplayName = "§6.3: the graph draws the arrowhead at the disc of the process connected to, just off its rim, and none at the disc of the process that connected")]
    public async Task TheGraphDrawsTheHeadAtTheDiscConnectedTo()
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
            WriteableBitmap frame = Settle(window);
            GraphDisplay display = workspace.GraphDisplay;
            GraphDisplayEdge edge = Assert.Single(display.Edges);
            string client = display.NodeOf(workspace.Snapshot.Processes.Single(process => process.ProcessId == 100).Id)!.Key;
            string server = display.NodeOf(workspace.Snapshot.Processes.Single(process => process.ProcessId == 200).Id)!.Key;
            Assert.Equal((1, 0), edge.SourceKey == client
                ? (edge.MadeFromSource, edge.MadeFromTarget)
                : (edge.MadeFromTarget, edge.MadeFromSource));

            // Where the head toward the server stands - a pixel off its disc's rim, as wide as the stroke and 3 px more
            // either side - and where one toward the client would.
            double thickness = GraphEncoding.EdgeThickness(edge, GraphEncoding.EdgeScale(display));
            Point from = graph.PointOf(client)!.Value;
            Point to = graph.PointOf(server)!.Value;
            double fromRadius = graph.RadiusOf(client)!.Value;
            double toRadius = graph.RadiusOf(server)!.Value;
            Point toward = BesideTheBase(GraphView.Arrowhead(from, to, fromRadius + 1, toRadius + 1, thickness)!.Value, thickness);
            Point back = BesideTheBase(GraphView.Arrowhead(to, from, toRadius + 1, fromRadius + 1, thickness)!.Value, thickness);
            Assert.DoesNotContain(graph.PlacedLabels, label => label.Inflate(2).Contains(toward) || label.Inflate(2).Contains(back));

            // Beside the stroke there, the head's hue stands apart from the pane at the server's end, and the pane shows at
            // the client's.
            Color pane = At(frame, graph.TranslatePoint(new Point(3, 3), window)!.Value);
            Color head = At(frame, graph.TranslatePoint(toward, window)!.Value);
            Color none = At(frame, graph.TranslatePoint(back, window)!.Value);
            Assert.True(Distance(head, pane) > 150, $"{head} against the pane's {pane}");
            Assert.True(Distance(none, pane) < 40, $"{none} against the pane's {pane}");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§6.6: the direction key draws the graph's own arrowhead in body ink, in every mode")]
    public async Task TheDirectionKeyDrawsTheGraphsArrowhead()
    {
        var viewModel = new WorkspaceViewModel(SyntheticWorkspace.Create(), "generation-1");
        var window = new MainWindow(viewModel) { Width = 1456, Height = 939 };
        window.Show();
        await viewModel.LayoutReady;
        Grid key = window.GetControl<Grid>("DirectionKey");
        key.BringIntoView();
        DirectionKeySample sample = Assert.Single(key.GetVisualDescendants().OfType<DirectionKeySample>());

        // Along the 36 px sample a 2 px stroke from a pixel in, and a head 8 px wide whose tip is a pixel short of the far
        // end: beside the stroke near its base the head is ink, and short of the base the ground shows.
        try
        {
            foreach (ThemeMode mode in EveryMode)
            {
                ThemeResources.Apply(Avalonia.Application.Current!, mode);
                WriteableBitmap frame = Settle(window);
                Color ink = ThemeResources.ToColor(ThemePalette.Surfaces(mode).Ink);
                Color ground = ThemeResources.ToColor(ThemePalette.Surfaces(mode).Elevated);
                foreach ((int x, int y, bool drawn) in new[] { (26, 9, true), (26, 4, true), (10, 7, true), (22, 9, false), (22, 4, false) })
                {
                    Color seen = At(frame, sample.TranslatePoint(new(x, y), window)!.Value);
                    Assert.True(seen == (drawn ? ink : ground),
                        $"{mode}: the direction sample at ({x}, {y}) drew {seen}, where it draws {(drawn ? "the ink " + ink : "nothing over the ground " + ground)}");
                }
            }
        }
        finally
        {
            ThemeResources.Apply(Avalonia.Application.Current!, ThemeMode.Dark);
            window.Close();
        }
    }

    /// <summary>
    /// A point inside <paramref name="head"/>, a pixel and a half on from its base and a pixel and a quarter beyond the side
    /// of the stroke of <paramref name="thickness"/> it rides: the head's where one is drawn, the pane's where none is.
    /// </summary>
    private static Point BesideTheBase((Point Tip, Point Left, Point Right) head, double thickness)
    {
        Point root = new((head.Left.X + head.Right.X) / 2, (head.Left.Y + head.Right.Y) / 2);
        Vector along = head.Tip - root;
        Vector aside = head.Left - root;
        return root + (along * (1.5 / along.Length)) + (aside * (((thickness / 2) + 1.25) / aside.Length));
    }

    /// <summary>An edge whose source made <paramref name="fromSource"/> of its connections and its target the rest seen.</summary>
    private static WriteableBitmap Drawn(int fromSource, int fromTarget, bool unmeasured = false, double thickness = 6)
    {
        GraphDisplayEdge edge = new("tcp:a", "a", "b", Mechanism.Tcp, 10, RelationStrength.Direct, ["tcp:a"])
        {
            MadeFromSource = fromSource,
            MadeFromTarget = fromTarget,
            Unmeasured = unmeasured,
        };
        var target = new RenderTargetBitmap(new PixelSize(200, 40));
        using (DrawingContext context = target.CreateDrawingContext())
        {
            GraphView.DrawEdgeMark(context, edge, new Pen(Brushes.Red, thickness), new Point(20, 20), new Point(180, 20), null,
                10, 10);
        }

        using var stream = new MemoryStream();
        target.Save(stream);
        stream.Position = 0;
        return WriteableBitmap.Decode(stream);
    }

    /// <summary>A pixel's colour as drawn over nothing, its alpha set aside: a filled shape's inside is wholly opaque.</summary>
    private static Color Opaque(Color color)
    {
        Assert.Equal(255, color.A);
        return color;
    }

    /// <summary>How far apart two colours are, channel by channel.</summary>
    private static int Distance(Color first, Color second) =>
        Math.Abs(first.R - second.R) + Math.Abs(first.G - second.G) + Math.Abs(first.B - second.B);

    /// <summary>Every theme mode, ending in the dark one the other tests expect to find.</summary>
    private static readonly ThemeMode[] EveryMode =
        [ThemeMode.Light, ThemeMode.HighContrastDark, ThemeMode.HighContrastLight, ThemeMode.Dark];

    private static WriteableBitmap Settle(Window window)
    {
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
        return window.CaptureRenderedFrame()!;
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    /// <summary>client.exe connects to server.exe's 8080 over loopback, the connect seen at its end, and sends once.</summary>
    private static ObservationRowV1[] Rows() =>
    [
        Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" },
        Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe" },
        Transfer(10, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 3).Between("127.0.0.1:50000", "127.0.0.1:8080"),
        Transfer(11, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 4).Between("127.0.0.1:50000", "127.0.0.1:8080"),
        Transfer(12, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 5).Between("127.0.0.1:8080", "127.0.0.1:50000"),
    ];
}
