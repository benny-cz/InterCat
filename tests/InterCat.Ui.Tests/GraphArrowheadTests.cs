using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using InterCat.Analysis;
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
    private static readonly MechanismCoverage Covered = new(Mechanism.Tcp, CoverageState.Covered, "covered");

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

        // A short edge, 50 px between discs that reach 10 px along it, holds a lone head - its tip at 59, its base at 44.6 -
        // but not one at each end with a stroke between them, so where each end connected it draws neither.
        WriteableBitmap shortOneWay = Drawn(fromSource: 1, fromTarget: 0, to: 70);
        Assert.Equal(hue, Opaque(At(shortOneWay, new Point(46, 24))));
        WriteableBitmap shortBoth = Drawn(fromSource: 1, fromTarget: 1, to: 70);
        Assert.Equal(0, At(shortBoth, new Point(46, 24)).A);
        Assert.Equal(Colors.Red, Opaque(At(shortBoth, new Point(56, 22))));

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

    [AvaloniaFact(DisplayName = "§6.3: an edge to Other hosts draws its arrowhead at the square where its process connected to the hosts, and at the process's disc where it accepted from them")]
    public async Task AnEdgeToOtherHostsDrawsTheHeadWhereItsConnectionsRan()
    {
        using var session = new TemporarySession();
        Publish(session.Store, HostRows());
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
            string hub = display.OtherHosts!.Key;
            Color pane = At(frame, graph.TranslatePoint(new Point(3, 3), window)!.Value);
            foreach ((int processId, bool connected) in new[] { (100, true), (300, false) })
            {
                string process = ProcessKey(workspace, processId);
                GraphDisplayEdge edge = Assert.Single(display.Edges, drawn => drawn.SourceKey == process);
                Assert.Equal(connected ? (1, 0) : (0, 1), (edge.MadeFromSource, edge.MadeFromTarget));

                // The head toward the square stands a pixel off the side the edge meets, or the one toward the process's
                // disc a pixel off its rim: drawn exactly there.
                ((Point Tip, Point Left, Point Right) atSquare, (Point Tip, Point Left, Point Right) atDisc) = Heads(graph, display, edge);
                Assert.Contains(GraphView.HeadBox(connected ? atSquare : atDisc), graph.DrawnHeads);

                // Beside its base the head stands apart from the pane, and the pane shows at the other end.
                double thickness = GraphEncoding.EdgeThickness(edge, GraphEncoding.EdgeScale(display));
                Point squareSide = BesideTheBase(atSquare, thickness);
                Point discSide = BesideTheBase(atDisc, thickness);
                Assert.DoesNotContain(graph.PlacedLabels, label => label.Inflate(2).Contains(squareSide) || label.Inflate(2).Contains(discSide));
                Color head = At(frame, graph.TranslatePoint(connected ? squareSide : discSide, window)!.Value);
                Color none = At(frame, graph.TranslatePoint(connected ? discSide : squareSide, window)!.Value);
                Assert.True(Distance(head, pane) > 150, $"PID {processId}: {head} against the pane's {pane}");
                Assert.True(Distance(none, pane) < 80, $"PID {processId}: {none} against the pane's {pane}");
            }

            // Every head drawn is clear of every label.
            Assert.Equal(2, graph.DrawnHeads.Count);
            Assert.DoesNotContain(graph.PlacedLabels, label => graph.DrawnHeads.Any(head => head.Intersects(label)));

            // Opened, each host drawn on its own is a smaller square, and the head toward it stands off its side as well.
            Assert.True(workspace.OpenGraphNode(hub));
            await workspace.LayoutReady;
            _ = Settle(window);
            GraphDisplay opened = workspace.GraphDisplay;
            GraphDisplayEdge toHost = Assert.Single(opened.Edges, drawn => drawn.TargetKey == GraphProjection.HostKey("203.0.113.1"));
            GraphDisplayEdge fromHost = Assert.Single(opened.Edges, drawn => drawn.TargetKey == GraphProjection.HostKey("10.0.0.6"));
            Assert.Equal(2, graph.DrawnHeads.Count);
            Assert.Contains(GraphView.HeadBox(Heads(graph, opened, toHost).AtTarget), graph.DrawnHeads);
            Assert.Contains(GraphView.HeadBox(Heads(graph, opened, fromHost).AtSource), graph.DrawnHeads);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§6.3: a node's label keeps clear of the graph's arrowheads, taking another side of its node where the first is over a head")]
    public async Task TheGraphsLabelsKeepClearOfItsArrowheads()
    {
        using var session = new TemporarySession();
        Publish(session.Store, BusyRows());
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
                Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            GraphView graph = window.GetControl<GraphView>("GraphSurface");
            _ = Settle(window);

            // Six heads: three processes' toward Other hosts, two from it, and agent.exe's toward svchost.exe.
            Assert.Equal(6, graph.DrawnHeads.Count);
            Assert.DoesNotContain(graph.PlacedLabels, label => graph.DrawnHeads.Any(head => head.Intersects(label)));

            // The slot below svchost.exe's disc, which its label would take first, a gap of 3 px below it, is over the head
            // of agent.exe's connection to it: its label stands elsewhere.
            string svchost = ProcessKey(workspace, 300);
            Point point = graph.PointOf(svchost)!.Value;
            Rect label = graph.PlacedLabels.MinBy(box => Math.Abs(box.Center.X - point.X) + Math.Abs(box.Center.Y - point.Y));
            Rect below = new(point.X - (label.Width / 2), point.Y + graph.RadiusOf(svchost)!.Value + 3, label.Width, label.Height);
            Assert.Contains(graph.DrawnHeads, head => head.Intersects(below));
            Assert.NotEqual(below, label);
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
    /// Where the graph draws <paramref name="edge"/>'s heads, were it to carry one at each end: a pixel off the node at its
    /// target - a square's side or a disc's rim - and a pixel off the node at its source. The edge runs between their
    /// centres, alone between them.
    /// </summary>
    private static ((Point Tip, Point Left, Point Right) AtTarget, (Point Tip, Point Left, Point Right) AtSource) Heads(
        GraphView graph, GraphDisplay display, GraphDisplayEdge edge)
    {
        Point source = graph.PointOf(edge.SourceKey)!.Value;
        Point target = graph.PointOf(edge.TargetKey)!.Value;
        double fromSource = GraphView.Reach(display.Node(edge.SourceKey)!, graph.RadiusOf(edge.SourceKey)!.Value, source, source,
            target) + 1;
        double fromTarget = GraphView.Reach(display.Node(edge.TargetKey)!, graph.RadiusOf(edge.TargetKey)!.Value, target, target,
            source) + 1;

        // The pen's thickness is kept to a quarter pixel, and the head is as wide as the pen.
        double thickness = Math.Round(GraphEncoding.EdgeThickness(edge, GraphEncoding.EdgeScale(display)) * 4) / 4;
        return (GraphView.Arrowhead(source, target, fromSource, fromTarget, thickness)!.Value,
            GraphView.Arrowhead(target, source, fromTarget, fromSource, thickness)!.Value);
    }

    private static string ProcessKey(WorkspaceViewModel workspace, int processId) =>
        workspace.GraphDisplay.NodeOf(workspace.Snapshot.Processes.Single(node => node.ProcessId == processId).Id)!.Key;

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
    private static WriteableBitmap Drawn(int fromSource, int fromTarget, bool unmeasured = false, double thickness = 6, double to = 180)
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
            GraphView.DrawEdgeMark(context, edge, new Pen(Brushes.Red, thickness), new Point(20, 20), new Point(to, 20), null,
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

    /// <summary>
    /// app.exe connects to 203.0.113.1's port 443, the connect seen at its end, and sends once; svc.exe accepts a connection
    /// from 10.0.0.6 at its own port 445 and receives once.
    /// </summary>
    private static ObservationRowV1[] HostRows() =>
    [
        Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Apps\app.exe" },
        Lifecycle(2, ObservationKind.Create, 300, 2) with { ResourceName = @"C:\Apps\svc.exe" },
        Transfer(10, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 3).Between("192.168.1.10:52000", "203.0.113.1:443"),
        Transfer(11, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 4).Between("192.168.1.10:52000", "203.0.113.1:443"),
        Transfer(20, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 300, 5).Between("192.168.1.10:445", "10.0.0.6:50001"),
        Transfer(21, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 300, 6).Between("192.168.1.10:445", "10.0.0.6:50001"),
    ];

    /// <summary>
    /// browser.exe and updater.exe connect to other hosts; svchost.exe accepts from two; agent.exe connects to one and
    /// accepts from another, and connects to svchost.exe over loopback. Each open is seen at its own end.
    /// </summary>
    private static ObservationRowV1[] BusyRows()
    {
        ulong ordinal = 1;
        long tick = 10;
        var rows = new List<ObservationRowV1>
        {
            Lifecycle(1, ObservationKind.Create, 100, ordinal++) with { ResourceName = @"C:\Apps\browser.exe" },
            Lifecycle(2, ObservationKind.Create, 200, ordinal++) with { ResourceName = @"C:\Apps\updater.exe" },
            Lifecycle(3, ObservationKind.Create, 300, ordinal++) with { ResourceName = @"C:\Windows\System32\svchost.exe" },
            Lifecycle(4, ObservationKind.Create, 400, ordinal++) with { ResourceName = @"C:\Apps\agent.exe" },
        };

        void Open(ObservationKind kind, int process, string local, string remote, int transfers)
        {
            rows.Add(Transfer(tick++, kind, AccountingSide.EndpointActivity, 0, process, ordinal++).Between(local, remote));
            for (int index = 0; index < transfers; index++)
            {
                rows.Add(kind == ObservationKind.Connect
                    ? Transfer(tick++, ObservationKind.Send, AccountingSide.SendSide, 500, process, ordinal++).Between(local, remote)
                    : Transfer(tick++, ObservationKind.Receive, AccountingSide.ReceiveSide, 500, process, ordinal++).Between(local, remote));
            }
        }

        Open(ObservationKind.Connect, 100, "192.168.1.10:50000", "142.250.1.1:443", 6);
        Open(ObservationKind.Connect, 100, "192.168.1.10:50001", "20.42.65.1:443", 3);
        Open(ObservationKind.Connect, 200, "192.168.1.10:50002", "13.107.4.50:443", 2);
        Open(ObservationKind.Accept, 300, "192.168.1.10:445", "10.0.0.6:50001", 4);
        Open(ObservationKind.Accept, 300, "192.168.1.10:3389", "10.0.0.7:50002", 2);
        Open(ObservationKind.Connect, 400, "192.168.1.10:50003", "10.0.0.6:8443", 2);
        Open(ObservationKind.Accept, 400, "192.168.1.10:9000", "10.0.0.9:50004", 2);
        Open(ObservationKind.Connect, 400, "127.0.0.1:50010", "127.0.0.1:135", 1);
        rows.Add(Transfer(tick++, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 300, ordinal++)
            .Between("127.0.0.1:135", "127.0.0.1:50010"));
        return [.. rows];
    }

    [Fact(DisplayName = "R14: a comparison's mark sits on its edge's stroke, off a middle ring, staggered by its lane, and clear of the nodes")]
    public void AComparisonsMarkSitsOnItsEdge()
    {
        // A level edge 200 px long between discs reaching 20 px along it: the mark at its middle, off the middle ring of a
        // weak relation toward its target, and edges beside one another staggered along their strokes by their lanes.
        Point from = new(0, 0), to = new(200, 0);
        Assert.Equal(new Point(100, 0), GraphView.ComparisonMarkAt(from, to, 20, 20, 0, ringed: false));
        Assert.Equal(new Point(113.5, 0), GraphView.ComparisonMarkAt(from, to, 20, 20, 0, ringed: true));
        Assert.Equal(new Point(91, 0), GraphView.ComparisonMarkAt(from, to, 20, 20, -0.5, ringed: false));
        Assert.Equal(new Point(109, 0), GraphView.ComparisonMarkAt(from, to, 20, 20, 0.5, ringed: false));
        Assert.Equal(new Point(77.5, 0), GraphView.ComparisonMarkAt(from, to, 20, 20, -0.5, ringed: true));
        Assert.Equal(new Point(122.5, 0), GraphView.ComparisonMarkAt(from, to, 20, 20, 0.5, ringed: true));
        Assert.Equal(new Point(82, 0), GraphView.ComparisonMarkAt(from, to, 20, 20, -1, ringed: false));
        Assert.Equal(new Point(0, 50), GraphView.ComparisonMarkAt(from, new Point(0, 100), 10, 10, 0, ringed: false));

        // Kept off the nodes where the stroke shows enough of itself; at its middle where it does not, and at a point's own
        // place for an edge of no length.
        Assert.Equal(new Point(101.5, 0), GraphView.ComparisonMarkAt(from, to, 90, 90, 0, ringed: true));
        Assert.Equal(new Point(98.5, 0), GraphView.ComparisonMarkAt(from, to, 90, 90, -1, ringed: false));
        Assert.Equal(new Point(15, 0), GraphView.ComparisonMarkAt(from, new Point(30, 0), 20, 20, 1, ringed: true));
        Assert.Equal(new Point(5, 5), GraphView.ComparisonMarkAt(new Point(5, 5), new Point(5.5, 5), 0, 0, 0, ringed: false));
    }

    [AvaloniaFact(DisplayName = "R14: a comparison's mark is its glyph in a ringed disc, the ring broken where the mark is of more than its edge")]
    public void AComparisonsMarkIsItsGlyphInARingedDisc()
    {
        // The ring's band is inked all the way round where the mark is the edge's own, and only in dashes, the pane showing
        // between them, where it is of more than the edge. The glyph is inked at the disc's middle, over its fill.
        Color ink = ThemeResources.ToColor(ThemePalette.Surfaces(ThemeResources.CurrentMode).Ink);
        Color plot = ThemeResources.ToColor(ThemePalette.Surfaces(ThemeResources.CurrentMode).Plot);
        ComparedRelationship row = new(new(1, 1, 1, 0, Covered), new(2, 2, 2, 0, Covered), ComparisonChange.Increased, null);
        bool Inked(Color color) => color.A >= 128 && Distance(color, ink) < Distance(color, plot);
        int[] ringed = new int[2];
        var graph = new GraphView();
        foreach (bool shared in new[] { false, true })
        {
            var target = new RenderTargetBitmap(new PixelSize(40, 40));
            using (DrawingContext context = target.CreateDrawingContext())
            {
                graph.DrawComparisonMark(context, new Point(20, 20),
                    new GraphComparisonMarked(GraphComparisonMark.Increased, [row], shared));
            }

            using var stream = new MemoryStream();
            target.Save(stream);
            stream.Position = 0;
            WriteableBitmap drawn = WriteableBitmap.Decode(stream);
            Point[] pixels = [.. Enumerable.Range(0, 40).SelectMany(x => Enumerable.Range(0, 40).Select(y => new Point(x + 0.5, y + 0.5)))];
            double Off(Point pixel) => Math.Sqrt(Math.Pow(pixel.X - 20, 2) + Math.Pow(pixel.Y - 20, 2));
            ringed[shared ? 1 : 0] = pixels.Count(pixel => Off(pixel) is >= 6.9 and <= 8.1 && Inked(At(drawn, pixel)));
            Assert.Equal(plot, Opaque(At(drawn, new Point(14.5, 20.5))));
            Assert.Contains(pixels.Where(pixel => Off(pixel) <= 3), pixel => Inked(At(drawn, pixel)));
            Assert.Equal(0, At(drawn, new Point(20.5, 10.5)).A);
        }

        Assert.True(ringed[0] >= 30, $"{ringed[0]} pixels inked round an unbroken ring");
        Assert.True(ringed[1] < ringed[0] * 0.8, $"{ringed[1]} pixels inked round a broken ring, {ringed[0]} round an unbroken one");
    }

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
