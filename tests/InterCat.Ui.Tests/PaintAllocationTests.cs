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
    /// <summary>Averaged over the measured frames, so one lazily grown cache in the drawing stack is not a failure.</summary>
    private const long ToleratedBytesPerFrame = 32;

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

        // The machine rung, with its mechanism lanes; then with a bar and its card under the pointer.
        Measure(window, "machine", report);
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

        // Down the ladder: a group's owner rows, a process's direction rows, a channel's end lanes.
        foreach (string rung in new[] { "group", "process", "channel" })
        {
            workspace.SelectedRung = workspace.RungRows[0];
            Assert.True(workspace.Descend(), rung);
            await Settle(window, workspace);
            Measure(window, rung, report);
        }

        window.Close();
        Assert.True(report.Count == 0, string.Join(Environment.NewLine, report));
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
    /// What one repaint of <paramref name="control"/> allocates beyond the drawing context's own cost, in the least of up to
    /// three batches.
    /// </summary>
    private static long OwnAllocationPerFrame(Control control)
    {
        // The first frames build what later ones reuse (laid-out text, dash geometry, the runtime's own warm-up), so a
        // generous warm-up comes before the frames that are counted.
        const int frames = 32;
        var target = new RenderTargetBitmap(new PixelSize(
            Math.Max(1, (int)Math.Ceiling(control.Bounds.Width)), Math.Max(1, (int)Math.Ceiling(control.Bounds.Height))));
        long Frames(Action<DrawingContext> render)
        {
            for (int warm = 0; warm < 32; warm++)
            {
                using DrawingContext context = target.CreateDrawingContext();
                render(context);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int frame = 0; frame < frames; frame++)
            {
                using DrawingContext context = target.CreateDrawingContext();
                render(context);
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        // A batch can include a one-off allocation that no pane code makes. Under full-suite load one batch in a few runs
        // allocated 3.6-5.7 KB with no collection during it, and each of three batches measured right after allocated
        // nothing. So a batch over the tolerance is measured again, up to three times, and the least is taken. An
        // allocation on every frame shows in every batch, and still fails.
        long least = long.MaxValue;
        for (int batch = 0; batch < 3 && least > ToleratedBytesPerFrame; batch++)
        {
            // The context grows its opacity and clip stacks on their first use in a frame; that is its cost, not the pane's.
            long empty = Frames(static context =>
            {
                using (context.PushOpacity(0.5))
                using (context.PushClip(new Rect(0, 0, 1, 1)))
                {
                }
            });
            long drawn = Frames(control.Render);
            least = Math.Min(least, Math.Max(0, drawn - empty) / frames);
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

    /// <summary>A client sending and a server receiving, <paramref name="count"/> times.</summary>
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
    ];

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
