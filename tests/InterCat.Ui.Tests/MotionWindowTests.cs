using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.CaptureBroker;
using InterCat.Desktop;
using InterCat.Desktop.Theme;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// §6.8's motion in the window: a level change carries the graph's nodes to their new places over the level change's
/// duration, drawn and hit where they are on the way, and a new selection's ring settles onto its node over the
/// selection's. Reduced motion draws both where they end at once, and neither changes a value, a place or a selection.
/// </summary>
public sealed class MotionWindowTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [AvaloniaFact(DisplayName = "§6.8: a view coming back to the newest generation carries the timeline there from the time it showed, its scope the newest at once; a publication followed live is drawn at once")]
    public async Task AViewComingBackToTheNewestIsCarriedThere()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(0, 10));
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(Update(session));
            Settle(window);
            TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
            var clock = new HandClock();
            timeline.Clock = clock;
            ReducedMotion.Pinned = false;

            // Followed live, a publication's newer extent is drawn at once: it comes several times a second, and moving
            // with each would keep the timeline in motion through the whole capture.
            Publish(session.Store, Exchange(10, 5));
            window.ApplyCaptureUpdate(Update(session));
            Settle(window);
            TimeRange followed = timeline.Viewport;
            Assert.True(timeline.IsFit);
            Assert.Equal(followed, timeline.DrawnViewport);

            // Paused while recording goes on, and followed again: the view's scope is the newest extent at once, and the
            // timeline is drawn from the time it showed.
            Pause(window, session, Exchange(15, 45));
            Assert.Equal(followed, timeline.Viewport);
            window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.None);
            Settle(window);
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            TimeRange newest = timeline.Viewport;
            Assert.Equal(workspace.Snapshot.Extent, newest);
            Assert.True(newest.EndTicks > followed.EndTicks + (2 * followed.SpanTicks), "The paused stretch was too short to see.");
            Assert.Equal(followed, timeline.DrawnViewport);

            // Early on its way, a record is drawn well right of where the newest range puts it, and the pointer resting
            // where it is drawn answers for the cell holding it, not for the later time the newest range puts there (R13).
            clock.Advance(ThemePalette.Motion.LevelChange / 10);
            Settle(window);
            TimelineBucket busy = workspace.Snapshot.Timeline.First(bucket => bucket.ObservationCount > 0
                && bucket.Interval.StartTicks > followed.StartTicks + (followed.SpanTicks / 3)
                && bucket.Interval.EndTicks < followed.EndTicks);
            long record = (busy.Interval.StartTicks + busy.Interval.EndTicks) / 2;
            double drawn = ViewportMath.PixelAtTick(timeline.DrawnViewport, record, timeline.PlotSpan);
            double settled = ViewportMath.PixelAtTick(newest, record, timeline.PlotSpan);
            Assert.True(drawn - settled > 20, $"The record is drawn {drawn - settled:0.0} px from where the newest range puts it.");
            window.MouseMove(timeline.TranslatePoint(new(timeline.PlotStart + drawn, timeline.Bounds.Height / 2), window)!.Value);
            Dispatch();
            TimelineBucket hovered = Assert.IsType<TimelineBucket>(timeline.HoveredBucket);
            Assert.True(hovered.Interval.StartTicks <= record && record < hovered.Interval.EndTicks,
                $"The pointer on the record drawn at {record} answered for {hovered.Interval}.");
            Assert.False(hovered.Interval.StartTicks <= ViewportMath.TickAtPixel(newest, drawn, timeline.PlotSpan)
                && ViewportMath.TickAtPixel(newest, drawn, timeline.PlotSpan) < hovered.Interval.EndTicks);

            // Each frame on the way tells the hover layer the drawing moved under the resting pointer.
            int nudges = 0;
            timeline.HoverChanged += (_, _) => nudges++;
            clock.Advance(ThemePalette.Motion.LevelChange / 10);
            Settle(window);
            Assert.True(nudges > 0, "The advance asked for no frame, so the drawing under the pointer moved unsaid.");

            // Halfway through the level change's duration an eased advance has covered seven eighths of its way, and at the
            // duration it is drawn exactly at the newest range.
            clock.Advance((ThemePalette.Motion.LevelChange / 2) - (ThemePalette.Motion.LevelChange / 5));
            Assert.Equal(Between(followed, newest, 0.875), timeline.DrawnViewport);
            clock.Advance(ThemePalette.Motion.LevelChange / 2);
            Assert.Equal(newest, timeline.DrawnViewport);
            Settle(window);
            Assert.Equal(newest, timeline.DrawnViewport);

            // What the timeline draws travels too: drawn at the start, on its way and at rest, with every count arrived, it
            // differs between the first two and not between the last two.
            window.MouseMove(new Point(1, 1));
            Pause(window, session, Exchange(60, 30));
            window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.None);
            await Assert.IsType<WorkspaceViewModel>(window.DataContext).TimelineDetailReady;
            Settle(window);
            byte[] first = Drawn(timeline);
            clock.Advance(ThemePalette.Motion.LevelChange);
            byte[] last = Drawn(timeline);
            Assert.NotEqual(first, last);
            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.Equal(last, Drawn(timeline));

            // A press while the timeline is on its way ends the advance, so the gesture starts from where the view ends.
            Pause(window, session, Exchange(90, 30));
            window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.None);
            Settle(window);
            clock.Advance(ThemePalette.Motion.LevelChange / 4);
            Assert.NotEqual(timeline.Viewport, timeline.DrawnViewport);
            Point press = timeline.TranslatePoint(new(timeline.PlotStart + (timeline.PlotSpan / 2), timeline.Bounds.Height / 2), window)!.Value;
            window.MouseDown(press, MouseButton.Left);
            Assert.Equal(timeline.Viewport, timeline.DrawnViewport);
            window.MouseUp(press, MouseButton.Left);
            Dispatch();

            // Under reduced motion a view coming back to the newest is drawn there at once.
            ReducedMotion.Pinned = true;
            Pause(window, session, Exchange(120, 30));
            window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.None);
            Settle(window);
            Assert.Equal(Assert.IsType<WorkspaceViewModel>(window.DataContext).Snapshot.Extent, timeline.Viewport);
            Assert.Equal(timeline.Viewport, timeline.DrawnViewport);
        }
        finally
        {
            ReducedMotion.Pinned = true;
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§6.8: opening a group carries its members from where it was drawn to their laid-out places, drawn and hit where they are on the way")]
    public async Task OpeningAGroupCarriesItsMembersToTheirPlaces()
    {
        (MainWindow window, WorkspaceViewModel viewModel, GraphView graph, HandClock clock) = await Open(Clustered());
        try
        {
            GraphDisplayNode group = viewModel.GraphDisplay.Nodes
                .Where(node => node.Kind == GraphNodeKind.Group && node.Members.Count > 1)
                .OrderByDescending(node => node.Observations)
                .First();
            GraphPoint groupPlace = viewModel.DisplayPositions[group.Key];
            HashSet<string> before = [.. viewModel.GraphDisplay.Nodes.Select(node => node.Key)];

            // Opened, the group's members are drawn where it was until their layout arrives: each is new to the drawing,
            // and appears where the eye last saw the processes it stands for. (Places are fractions of the pane, which the
            // opened rung's other panes may have resized, so where the group was is read on the pane as it is now.)
            Assert.True(viewModel.OpenGraphNode(group.Key));
            GraphDisplayNode[] members = [.. viewModel.GraphDisplay.Nodes
                .Where(node => !before.Contains(node.Key) && node.Members.Any(group.Members.Contains))];
            Assert.True(members.Length > 1, "The opened group drew fewer than two members.");
            Assert.All(members, member => Assert.True(Distance(graph.PlaceOf(groupPlace), graph.PointOf(member.Key)!.Value) < 0.5));
            await viewModel.LayoutReady;
            Settle(window);

            // The view model holds the laid-out places at once; the drawing carries each member there from the group's.
            Point start = graph.PlaceOf(groupPlace);
            GraphDisplayNode moving = members.MaxBy(member => Distance(start, Place(graph, viewModel, member.Key)))!;
            Point final = Place(graph, viewModel, moving.Key);
            double way = Distance(start, final);
            Assert.True(way > 20, $"The member's place is only {way:0.0} px from the group's, too near to see it move.");
            Assert.True(Distance(start, graph.PointOf(moving.Key)!.Value) < 0.5);

            // Halfway through the level change's duration an eased move has covered seven eighths of its way.
            clock.Advance(ThemePalette.Motion.LevelChange / 2);
            Settle(window);
            Point midway = graph.PointOf(moving.Key)!.Value;
            Assert.InRange(Distance(midway, final), way * 0.12, way * 0.13);
            Assert.Equal(final, Place(graph, viewModel, moving.Key));

            // A click where the member is drawn now chooses it, though its place is elsewhere (R13).
            window.MouseMove(At(graph, window, midway));
            window.MouseDown(At(graph, window, midway), MouseButton.Left);
            window.MouseUp(At(graph, window, midway), MouseButton.Left);
            Dispatch();
            Assert.Equal(moving.Key, viewModel.SelectedGraphNodeKey);

            // At the duration every member is drawn exactly at its place, and stays there.
            clock.Advance(ThemePalette.Motion.LevelChange / 2);
            Settle(window);
            Assert.All(members, member => Assert.Equal(Place(graph, viewModel, member.Key), graph.PointOf(member.Key)));
            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.Equal(final, graph.PointOf(moving.Key));

            // A node pinned into a corner from the inspector travels there too, while the view model holds it there at once.
            GraphDisplayNode[] others = [.. members.Where(member => member.Key != moving.Key).Take(2)];
            var corner = new GraphPoint(0.05, 0.95);
            Point wasAt = graph.PointOf(others[0].Key)!.Value;
            Assert.True(viewModel.PinGraphNode(others[0].Key, corner));
            Assert.Equal(corner, viewModel.DisplayPositions[others[0].Key]);
            Assert.Equal(wasAt, graph.PointOf(others[0].Key));
            await viewModel.LayoutReady;
            Settle(window);
            clock.Advance(ThemePalette.Motion.LevelChange);
            Assert.Equal(graph.PlaceOf(corner), graph.PointOf(others[0].Key));

            // Under reduced motion the same change is drawn where it ends at once, with no time passing: the node pinned and
            // every node the layout then settles around it.
            ReducedMotion.Pinned = true;
            var other = new GraphPoint(0.95, 0.05);
            Assert.True(viewModel.PinGraphNode(others[1].Key, other));
            Assert.Equal(graph.PlaceOf(other), graph.PointOf(others[1].Key));
            await viewModel.LayoutReady;
            Settle(window);
            Assert.All(viewModel.GraphDisplay.Nodes,
                node => Assert.Equal(Place(graph, viewModel, node.Key), graph.PointOf(node.Key)));
            Assert.Equal(moving.Key, viewModel.SelectedGraphNodeKey);
        }
        finally
        {
            ReducedMotion.Pinned = true;
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§6.8: a node dropped by a drag stays where it was dropped, and one whose drag is cancelled is carried back to its place")]
    public async Task ADroppedNodeStaysAndACancelledOneGoesBack()
    {
        (MainWindow window, WorkspaceViewModel viewModel, GraphView graph, HandClock clock) = await Open();
        try
        {
            GraphDisplayNode node = viewModel.GraphDisplay.Nodes.OrderBy(candidate => candidate.Key, StringComparer.Ordinal).First();
            Point from = graph.PointOf(node.Key)!.Value;

            // Dropped, the node is drawn where the pointer left it at once; it never travels back to slide there again.
            Point to = Inward(graph, from, 60, 30);
            window.MouseDown(At(graph, window, from), MouseButton.Left);
            window.MouseMove(At(graph, window, new((from.X + to.X) / 2, (from.Y + to.Y) / 2)));
            window.MouseMove(At(graph, window, to));
            window.MouseUp(At(graph, window, to), MouseButton.Left);
            Dispatch();
            Assert.True(Distance(to, graph.PointOf(node.Key)!.Value) < 1);
            await viewModel.LayoutReady;
            Settle(window);
            Assert.True(Distance(to, graph.PointOf(node.Key)!.Value) < 1);
            Point pinned = graph.PointOf(node.Key)!.Value;

            // A drag Esc cancels carries the node back from where the pointer left it, over the level change's duration.
            Point away = Inward(graph, pinned, 80, 40);
            window.MouseDown(At(graph, window, pinned), MouseButton.Left);
            window.MouseMove(At(graph, window, new((pinned.X + away.X) / 2, (pinned.Y + away.Y) / 2)));
            window.MouseMove(At(graph, window, away));
            Assert.True(graph.CancelDrag());
            Assert.True(Distance(away, graph.PointOf(node.Key)!.Value) < 1);
            clock.Advance(ThemePalette.Motion.LevelChange / 2);
            double back = Distance(pinned, graph.PointOf(node.Key)!.Value);
            Assert.InRange(back, Distance(pinned, away) * 0.12, Distance(pinned, away) * 0.13);
            clock.Advance(ThemePalette.Motion.LevelChange / 2);
            Assert.Equal(pinned, graph.PointOf(node.Key));
            window.MouseUp(At(graph, window, away), MouseButton.Left);
            Dispatch();
            Assert.Equal(pinned, graph.PointOf(node.Key));
        }
        finally
        {
            ReducedMotion.Pinned = true;
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§6.8: a node newly selected is ringed from wider and the ring settles onto it in the selection's duration; reduced motion rings it in place at once")]
    public async Task ANewSelectionsRingSettlesOntoItsNode()
    {
        (MainWindow window, WorkspaceViewModel viewModel, GraphView graph, HandClock clock) = await Open();
        try
        {
            GraphDisplayNode[] processes = [.. viewModel.GraphDisplay.Nodes
                .Where(node => node.Kind == GraphNodeKind.Process && !viewModel.SelectedGraphNodeKeys.Contains(node.Key))
                .Take(2)];
            Assert.Equal(2, processes.Length);
            viewModel.SelectGraphNode(processes[0].Key);
            Settle(window);
            double ring = graph.RadiusOf(processes[0].Key)!.Value + GraphView.SelectionHalo;
            Assert.Equal(ring + GraphView.RingTravel, graph.RingOf(processes[0].Key));

            // Halfway, an eased ring has travelled seven eighths of its way in; at the duration it is exactly in place.
            clock.Advance(ThemePalette.Motion.Selection / 2);
            Assert.Equal(ring + (GraphView.RingTravel / 8), graph.RingOf(processes[0].Key)!.Value, 9);
            clock.Advance(ThemePalette.Motion.Selection / 2);
            Assert.Equal(ring, graph.RingOf(processes[0].Key));

            // Choosing the node again - a second click - rings nothing again.
            viewModel.SelectGraphNode(processes[0].Key);
            Settle(window);
            Assert.Equal(ring, graph.RingOf(processes[0].Key));

            // Under reduced motion the next selection is ringed in place at once; the one it replaced loses its ring.
            ReducedMotion.Pinned = true;
            viewModel.SelectGraphNode(processes[1].Key);
            Settle(window);
            Assert.Null(graph.RingOf(processes[0].Key));
            Assert.Equal(graph.RadiusOf(processes[1].Key)!.Value + GraphView.SelectionHalo, graph.RingOf(processes[1].Key));
            Assert.Equal(processes[1].Key, viewModel.SelectedGraphNodeKey);

            // A node chosen again once it has left the selection joins it again, and is ringed from wider again.
            ReducedMotion.Pinned = false;
            viewModel.SelectGraphNode(processes[0].Key);
            Settle(window);
            Assert.Equal(ring + GraphView.RingTravel, graph.RingOf(processes[0].Key));
        }
        finally
        {
            ReducedMotion.Pinned = true;
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "P22: a node moving away from a resting pointer takes its card with it as it goes, not once it has arrived")]
    public async Task AMovingNodeTakesItsCardWithItAsItGoes()
    {
        (MainWindow window, WorkspaceViewModel viewModel, GraphView graph, HandClock clock) = await Open(Clustered());
        try
        {
            GraphDisplayNode node = viewModel.GraphDisplay.Nodes.First(candidate => candidate.Kind == GraphNodeKind.Process);
            Point resting = graph.PointOf(node.Key)!.Value;
            window.MouseMove(At(graph, window, resting));
            Dispatch();
            Assert.Equal(node.Key, graph.HoveredKey);

            // Pinned into the far corner, the node is still under the pointer when the move begins, so its card stays.
            var corner = new GraphPoint(resting.X < graph.Bounds.Width / 2 ? 0.95 : 0.05, 0.95);
            Assert.True(viewModel.PinGraphNode(node.Key, corner));
            Settle(window);
            Assert.Equal(node.Key, graph.HoveredKey);

            // Halfway through, it is far from the pointer, and its card has gone with it, though the pointer never moved.
            clock.Advance(ThemePalette.Motion.LevelChange / 2);
            Settle(window);
            Settle(window);
            Assert.True(Distance(resting, graph.PointOf(node.Key)!.Value) > 40, "The node did not move away from the pointer.");
            Assert.NotEqual(node.Key, graph.HoveredKey);
        }
        finally
        {
            ReducedMotion.Pinned = true;
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R11: a repaint while the graph's nodes move and a ring settles allocates nothing of its own")]
    public async Task ARepaintWhileNodesMoveAllocatesNothing()
    {
        (MainWindow window, WorkspaceViewModel viewModel, GraphView graph, HandClock clock) = await Open(Clustered());
        try
        {
            GraphDisplayNode[] processes = [.. viewModel.GraphDisplay.Nodes
                .Where(node => node.Kind == GraphNodeKind.Process && !viewModel.SelectedGraphNodeKeys.Contains(node.Key))
                .Take(2)];
            Assert.True(viewModel.PinGraphNode(processes[0].Key, new GraphPoint(0.05, 0.95)));
            viewModel.SelectGraphNode(processes[1].Key);
            await viewModel.LayoutReady;
            Settle(window);
            clock.Advance(ThemePalette.Motion.Selection / 2);
            Assert.NotEqual(Place(graph, viewModel, processes[0].Key), graph.PointOf(processes[0].Key));
            Assert.NotEqual(graph.RadiusOf(processes[1].Key)!.Value + GraphView.SelectionHalo, graph.RingOf(processes[1].Key));

            long bytes = PaintAllocationTests.OwnAllocationPerFrame(graph);
            Assert.True(bytes <= PaintAllocationTests.ToleratedBytesPerFrame,
                $"A repaint of the moving graph allocated {bytes} B of its own.");
        }
        finally
        {
            ReducedMotion.Pinned = true;
            window.Close();
        }
    }

    /// <summary>
    /// The synthetic workspace laid out and drawn while motion is reduced, so its first places are still, then motion
    /// turned on and read by a clock the test moves.
    /// </summary>
    private static async Task<(MainWindow Window, WorkspaceViewModel ViewModel, GraphView Graph, HandClock Clock)> Open(
        WorkspaceSnapshot? snapshot = null)
    {
        var viewModel = new WorkspaceViewModel(snapshot ?? SyntheticWorkspace.Create(), "generation-1");
        var window = new MainWindow(viewModel) { Width = 1456, Height = 939 };
        window.Show();
        await viewModel.LayoutReady;
        Settle(window);
        GraphView graph = window.GetControl<GraphView>("GraphSurface");
        var clock = new HandClock();
        graph.Clock = clock;
        ReducedMotion.Pinned = false;
        return (window, viewModel, graph, clock);
    }

    /// <summary>
    /// Two executables: one of 30 processes that each talk to the next, drawn as one collapsed group until it is opened
    /// (§6.3's threshold is 25 drawn members), and one of three that each talk to one of them.
    /// </summary>
    private static WorkspaceSnapshot Clustered()
    {
        ProcessGroup big = new("big", "Big", LaneGrouping.Executable);
        ProcessGroup small = new("small", "Small", LaneGrouping.Executable);
        ProcessNode[] bigs = [.. Enumerable.Range(1, 30).Select(index => Process(index, big.Key))];
        ProcessNode[] smalls = [.. Enumerable.Range(31, 3).Select(index => Process(index, small.Key))];
        CommunicationEdge[] edges =
        [
            .. Enumerable.Range(0, bigs.Length - 1).Select(index => Edge($"b{index}", bigs[index], bigs[index + 1])),
            .. smalls.Select((process, index) => Edge($"s{index}", process, bigs[index * 10])),
        ];
        return new("Session", new TimeRange(0, WorkspaceTime.TicksPerSecond), [big, small], [.. bigs, .. smalls], edges,
            [], [], [], []);
    }

    private static ProcessNode Process(int index, string group) => new(
        new ProcessInstanceId(Guid.Parse($"00000000-0000-0000-0000-{index:D12}")),
        2_000 + index,
        $"Process {index}",
        "test",
        group,
        0.5,
        0.5,
        CoverageState.Covered);

    private static CommunicationEdge Edge(string key, ProcessNode source, ProcessNode target) =>
        new(key, source.Id, target.Id, Mechanism.Tcp, 10, null, RelationStrength.Direct)
        {
            Rule = RelationRule.TransportEndpoint,
            Evidence = [],
        };

    /// <summary>What a control draws of its own now, as an image's bytes.</summary>
    private static byte[] Drawn(Control control)
    {
        var target = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(
            Math.Max(1, (int)Math.Ceiling(control.Bounds.Width)), Math.Max(1, (int)Math.Ceiling(control.Bounds.Height))));
        using (Avalonia.Media.DrawingContext context = target.CreateDrawingContext())
        {
            control.Render(context);
        }

        using var stream = new MemoryStream();
        target.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Pauses the view, then publishes <paramref name="records"/> while it is paused, so the newer generation is held.</summary>
    private static void Pause(MainWindow window, TemporarySession session, ObservationRowV1[] records)
    {
        window.GetControl<ListBox>("RungList").Focus();
        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.None);
        Publish(session.Store, records);
        window.ApplyCaptureUpdate(Update(session));
        Settle(window);
        Assert.True(window.GetControl<Border>("HeldBanner").IsVisible, "The newer generation was not held.");
    }

    /// <summary>The range a fit view is drawn at <paramref name="eased"/> of the way from one range to another.</summary>
    private static TimeRange Between(TimeRange from, TimeRange to, double eased) => new(
        from.StartTicks + (long)Math.Round((to.StartTicks - from.StartTicks) * eased),
        from.EndTicks + (long)Math.Round((to.EndTicks - from.EndTicks) * eased));

    private static CaptureUiUpdate Update(TemporarySession session) => new(
        CaptureUiPhase.Recording, "Recording", "A published generation.", SessionPath: session.Path,
        Overview: SessionOverviewProjector.Project(session.Store));

    /// <summary>A client sending and a server receiving, <paramref name="count"/> times from <paramref name="first"/>.</summary>
    private static ObservationRowV1[] Exchange(int first, int count) =>
    [
        .. Enumerable.Range(first, count).SelectMany(index => new[]
        {
            Transfer(10 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 64, 100, (ulong)(100 + (2 * index)))
                .Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = (10 + (2 * index)) * 100L },
            Transfer(11 + (2 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200,
                (ulong)(101 + (2 * index))).Between(ServerEnd, ClientEnd) with { SessionRelativeTicks = (11 + (2 * index)) * 100L },
        }),
    ];

    /// <summary>A point that far from <paramref name="from"/> toward the middle of the pane, so a drag stays inside it.</summary>
    private static Point Inward(GraphView graph, Point from, double across, double down) => new(
        from.X + (from.X < graph.Bounds.Width / 2 ? across : -across),
        from.Y + (from.Y < graph.Bounds.Height / 2 ? down : -down));

    private static Point Place(GraphView graph, WorkspaceViewModel viewModel, string key) =>
        graph.PlaceOf(viewModel.DisplayPositions[key]);

    private static double Distance(Point from, Point to) => Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2));

    private static Point At(Control control, Window window, Point point) => control.TranslatePoint(point, window)!.Value;

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    private static void Settle(Window window)
    {
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
    }

    /// <summary>A clock the test moves by hand, so a motion is read at the moments it names.</summary>
    private sealed class HandClock : TimeProvider
    {
        private long ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => ticks;

        public void Advance(TimeSpan by) => ticks += by.Ticks;
    }
}
