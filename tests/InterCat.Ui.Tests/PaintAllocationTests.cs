using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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
/// R11 and §19.4's frame-loop rules: a repaint allocates nothing of its own - no query, pen, string or laid-out text
/// rebuilt per frame - in any pane and on any rung, so a long interaction accumulates no collection pauses. What the
/// drawing context itself costs is measured on an empty control of the same size and taken off.
/// </summary>
public sealed class PaintAllocationTests
{
    /// <summary>
    /// Averaged over the measured frames, so one lazily grown cache in the drawing stack is not a failure, while the least
    /// object allocated on every other frame is: the smallest an object can be is 24 bytes.
    /// </summary>
    private const long ToleratedBytesPerFrame = 8;

    private static readonly string[] Panes = ["TimelineSurface", "GraphSurface", "MinimapSurface", "HoverLayer"];

    [AvaloniaFact(DisplayName = "R11: a repaint of the timeline, graph, minimap and hover layer allocates nothing of its own, on every rung")]
    public async Task ARepaintAllocatesNothingOfItsOwn()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(60));
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
            Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await Settle(window, workspace);
        var report = new List<string>();

        // The machine rung, with its mechanism lanes, on one scale and each on its own (§6.2); then with a bar and its card
        // under the pointer.
        Measure(window, "machine", report);
        await MeasureEachLaneOnItsOwn(window, workspace, "machine", report);
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        TimelineBucket busiest = workspace.Snapshot.Timeline.MaxBy(bucket => bucket.ObservationCount)!;
        window.MouseMove(timeline.TranslatePoint(timeline.PointOf(busiest)!.Value, window)!.Value);
        await Settle(window, workspace);
        Measure(window, "machine, a bar hovered", report);

        // A node and its card under the pointer.
        GraphView graph = window.GetControl<GraphView>("GraphSurface");
        GraphDisplayNode node = workspace.GraphDisplay.Nodes.First(candidate => candidate.Kind == GraphNodeKind.Process);
        window.MouseMove(graph.TranslatePoint(graph.PointOf(node.Key)!.Value, window)!.Value);
        await Settle(window, workspace);
        Measure(window, "machine, a node hovered", report);
        window.MouseMove(new Point(1, 1));

        // Ranked by bytes sent, whose lanes plot each column's bytes and cross-hatch the sends that recorded no size.
        workspace.RankBy = RankingMetric.BytesSent;
        await workspace.TimelineBytesReady;
        await workspace.RankingReady;
        await Settle(window, workspace);
        Assert.NotNull(workspace.TimelineBytes);
        Assert.True(workspace.TimelineDrawsUnmeasured);
        Measure(window, "machine, plotting bytes sent", report);
        await MeasureEachLaneOnItsOwn(window, workspace, "machine, plotting bytes sent", report);
        workspace.RankBy = RankingMetric.Records;
        await workspace.RankingReady;

        // Down the ladder: a group's owner rows, a process's direction rows, a channel's end lanes.
        foreach (string rung in new[] { "group", "process", "channel" })
        {
            workspace.SelectedRung = workspace.RungRows[0];
            Assert.True(workspace.Descend(), rung);
            await Settle(window, workspace);
            Measure(window, rung, report);
            await MeasureEachLaneOnItsOwn(window, workspace, rung, report);
            if (rung is "group" or "process")
            {
                // The rung's rows and the machine row above them, plotting bytes sent.
                workspace.RankBy = RankingMetric.BytesSent;
                await workspace.TimelineBytesReady;
                await workspace.RankingReady;
                await Settle(window, workspace);
                Assert.True(rung == "group" ? workspace.ProcessLaneBytes is not null : workspace.DirectionLaneBytes is not null, rung);
                Measure(window, $"{rung}, plotting bytes sent", report);
                workspace.RankBy = RankingMetric.Records;
                await workspace.RankingReady;
                await Settle(window, workspace);
            }
        }

        window.Close();
        Assert.True(report.Count == 0, string.Join(Environment.NewLine, report));
    }

    [AvaloniaFact(DisplayName = "R11: a repaint of a call or exchange lane allocates nothing of its own, as bars or as density, a mark under the pointer or not")]
    public async Task AnOperationLaneRepaintAllocatesNothingOfItsOwn()
    {
        var report = new List<string>();
        foreach (bool dense in new[] { false, true })
        {
            string shape = dense ? "density" : "bars";

            // An RPC channel's calls: completed, failing and one open at capture end.
            using (var session = new TemporarySession())
            {
                Publish(session.Store, RpcCalls(dense ? SessionRpcCalls.MaximumSpans + 100 : 12));
                (MainWindow window, WorkspaceViewModel workspace, TimelineView timeline) = await OpenCalls(session);
                try
                {
                    Assert.True(workspace.ShowsRpcCallLane, workspace.TimelineCaption);
                    Assert.Equal(dense, workspace.RpcCallDensity is not null);
                    Point? mark = workspace.RpcCallDensity is { } density
                        ? timeline.PointOfDensityColumn(Occupied(density.Running))
                        : timeline.PointOf(workspace.RpcCallSpans!.First(call => call.Failed));
                    await MeasureLane(window, workspace, timeline, $"RPC calls as {shape}", mark, report);
                }
                finally
                {
                    window.Close();
                }
            }

            // A process's HTTP exchanges, some not recorded whole.
            using (var session = new TemporarySession())
            {
                SourceFieldRowV1[] fields;
                Publish(session.Store, dense
                    ? HttpExchangeLaneTests.ManyExchanges(SessionHttpExchanges.MaximumSpans + 100, out fields)
                    : HttpExchangeLaneTests.FiveExchanges(out fields), fields: fields);
                (MainWindow window, WorkspaceViewModel workspace, TimelineView timeline) = await HttpExchangeLaneTests.OpenExchanges(session);
                try
                {
                    Assert.True(workspace.ShowsHttpExchangeLane, workspace.TimelineCaption);
                    Assert.Equal(dense, workspace.HttpExchangeDensity is not null);
                    Point? mark = workspace.HttpExchangeDensity is { } density
                        ? timeline.PointOfDensityColumn(Occupied(density.Incomplete))
                        : timeline.PointOf(workspace.HttpExchangeSpans!.First(exchange => !exchange.Complete));
                    await MeasureLane(window, workspace, timeline, $"HTTP exchanges as {shape}", mark, report);
                }
                finally
                {
                    window.Close();
                }
            }
        }

        Assert.True(report.Count == 0, string.Join(Environment.NewLine, report));
    }

    /// <summary>Measures every pane with the lane drawn, then again with one of its marks and that mark's card under the pointer.</summary>
    private static async Task MeasureLane(
        Window window, WorkspaceViewModel workspace, TimelineView timeline, string lane, Point? mark, List<string> report)
    {
        await Settle(window, workspace);
        Measure(window, lane, report);
        window.MouseMove(timeline.TranslatePoint(Assert.NotNull(mark), window)!.Value);
        await Settle(window, workspace);
        Assert.NotNull(timeline.HoverCard);
        Measure(window, $"{lane}, one hovered", report);
    }

    /// <summary>The first density column that counts something.</summary>
    private static int Occupied(IReadOnlyList<long> columns) =>
        Enumerable.Range(0, columns.Count).First(column => columns[column] > 0);

    /// <summary>
    /// Measures the rung again with each timeline lane read against its own busiest bar (§6.2), then puts back the one
    /// scale every lane shares: each row's peak is found in an array kept from frame to frame, never a new one.
    /// </summary>
    private static async Task MeasureEachLaneOnItsOwn(Window window, WorkspaceViewModel workspace, string rung, List<string> report)
    {
        workspace.ScalesEachLane = true;
        await Settle(window, workspace);
        Measure(window, $"{rung}, each lane on its own scale", report);
        workspace.ScalesEachLane = false;
        await Settle(window, workspace);
    }

    private static void Measure(Window window, string rung, List<string> report)
    {
        foreach (string name in Panes)
        {
            long bytes = OwnAllocationPerFrame(window.GetControl<Control>(name));
            if (bytes > ToleratedBytesPerFrame)
            {
                report.Add($"{rung}: {name} allocated {bytes} B of its own per repaint");
            }
        }
    }

    /// <summary>
    /// What one repaint of <paramref name="control"/> allocates, in the least of up to three batches. Each batch draws
    /// every frame into one context, so the context's opacity, clip and transform stacks grow once, in the warm-up, and
    /// what a counted frame allocates is the pane's own. Drawn into a fresh context per frame, the stacks' growth had to be
    /// taken off as one estimate for every pane, and a lane's note formatted on every frame once hid inside it.
    /// </summary>
    private static long OwnAllocationPerFrame(Control control)
    {
        // The first frames build what later ones reuse (laid-out text, dash geometry, the runtime's own warm-up), so a
        // generous warm-up comes before the frames that are counted.
        const int frames = 32;
        var target = new RenderTargetBitmap(new PixelSize(
            Math.Max(1, (int)Math.Ceiling(control.Bounds.Width)), Math.Max(1, (int)Math.Ceiling(control.Bounds.Height))));

        // A batch can include a one-off allocation that no pane code makes. Under full-suite load one batch in a few runs
        // allocated 3.6-5.7 KB with no collection during it, and each of three batches measured right after allocated
        // nothing. So a batch over the tolerance is measured again, up to three times, and the least is taken. An
        // allocation on every frame shows in every batch, and still fails.
        long least = long.MaxValue;
        for (int batch = 0; batch < 3 && least > ToleratedBytesPerFrame; batch++)
        {
            using DrawingContext context = target.CreateDrawingContext();
            for (int warm = 0; warm < 32; warm++)
            {
                control.Render(context);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int frame = 0; frame < frames; frame++)
            {
                control.Render(context);
            }

            least = Math.Min(least, (GC.GetAllocatedBytesForCurrentThread() - before) / frames);
        }

        return least;
    }

    private static async Task Settle(Window window, WorkspaceViewModel workspace)
    {
        await workspace.LayoutReady;
        await workspace.TimelineDetailReady;
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
    }

    /// <summary>Opens the session in a window and descends to process 100's RPC channel, with its call lane read for the view.</summary>
    private static async Task<(MainWindow Window, WorkspaceViewModel Workspace, TimelineView Timeline)> OpenCalls(TemporarySession session)
    {
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(new(
            CaptureUiPhase.Recording, "Recording", "A published generation.", SessionPath: session.Path,
            Overview: SessionOverviewProjector.Project(session.Store)));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        ProcessNode node = workspace.Snapshot.Processes.Single(process => process.ProcessId == 100);
        foreach (string key in new[] { node.GroupKey, node.Id.ToString() })
        {
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == key);
            Assert.True(workspace.Descend());
        }

        await workspace.RpcReady;
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Source.Mechanism == Mechanism.Rpc);
        Assert.True(workspace.Descend());
        await workspace.RpcReady;
        timeline.RequestDetailNow();
        await workspace.RpcSpansReady;
        Dispatch();
        return (window, workspace, timeline);
    }

    /// <summary>
    /// <paramref name="count"/> calls process 100 made on one interface, one after another, every seventh failing, then one
    /// still open at capture end; beside a few transfers, so the machine's context lane has records to draw.
    /// </summary>
    private static ObservationRowV1[] RpcCalls(int count)
    {
        Guid serviceControl = Guid.Parse("367abb81-9844-35f1-ad32-98f038001003");
        long open = 10 + (3L * count);
        return
        [
            .. Exchange(4),
            .. Enumerable.Range(0, count).SelectMany(index =>
            {
                long start = 10 + (3L * index);
                var activity = new Guid(index + 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3);
                return new[]
                {
                    RpcCall(start, ObservationKind.RequestStart, Direction.Outbound, 100, (ulong)(100_000 + (2 * index)),
                        activity, serviceControl) with { SessionRelativeTicks = start * 100 },
                    RpcCall(start + 1, ObservationKind.RequestEnd, Direction.Outbound, 100, (ulong)(100_001 + (2 * index)),
                        activity, status: index % 7 == 5 ? 5 : 0) with { SessionRelativeTicks = (start + 1) * 100 },
                };
            }),
            RpcCall(open, ObservationKind.RequestStart, Direction.Outbound, 100, (ulong)(100_000 + (2 * count)),
                new Guid(count + 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3), serviceControl) with { SessionRelativeTicks = open * 100 },
        ];
    }

    /// <summary>A client sending and a server receiving, <paramref name="count"/> times, then sending once with no size.</summary>
    private static ObservationRowV1[] Exchange(int count) =>
    [
        .. Enumerable.Range(0, count).SelectMany(index => new[]
        {
            Transfer(10 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 64, 100, (ulong)(100 + (2 * index)))
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = (10 + (2 * index)) * 100L },
            Transfer(11 + (2 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200,
                (ulong)(101 + (2 * index))).Between("127.0.0.1:8080", "127.0.0.1:50000")
                with { SessionRelativeTicks = (11 + (2 * index)) * 100L },
        }),
        Transfer(10 + (2 * count), ObservationKind.Send, AccountingSide.SendSide, null, 100, (ulong)(100 + (2 * count)))
            .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = (10 + (2 * count)) * 100L },
    ];

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
