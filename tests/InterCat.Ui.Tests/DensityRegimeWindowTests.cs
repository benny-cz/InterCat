using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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
/// §6.2's density regime in the window: a zoomed view is counted in a column per device pixel and drawn as density, where
/// a lone record would be a one-pixel hairline. Its mark is drawn 5 px wide so it can be pointed at, a point beside it
/// snaps to it, a point further off answers with the empty interval under it, and a click chooses the record's own
/// one-pixel interval, never the width its mark is drawn at (R13).
/// </summary>
public sealed class DensityRegimeWindowTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    /// <summary>The two instances of client.exe.</summary>
    private static readonly int[] Clients = [100, 101];

    /// <summary>Where the lone records lie, in session ticks: 1.2 s and 1.5 s.</summary>
    private static readonly long[] Lone = [12_000_000, 15_000_000];

    [AvaloniaFact(DisplayName = "§6.2: a zoomed view is counted a column per device pixel, and a lone record's mark is drawn 5 px wide")]
    public async Task AZoomIsCountedPerPixelAndALoneRecordIsWidened()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        (MainWindow window, WorkspaceViewModel workspace, TimelineView timeline) = await OpenZoomed(session);
        try
        {
            // One column per device pixel across the plot (§10.3's W), where it was one per 10 px.
            SessionTimelineDetail detail = Assert.IsType<SessionTimelineDetail>(workspace.TimelineDetail);
            Assert.Equal(timeline.DeviceColumns, detail.Buckets.Count);
            Assert.Equal((int)Math.Round(timeline.PlotSpan), detail.Buckets.Count);

            // The lone record's column is a pixel wide; its mark, 5 px wide around it, and nothing either side.
            (TimelineBucket lone, Point mark) = LoneMark(workspace, timeline, window, 0);
            Assert.Equal(1, lone.ObservationCount);
            WriteableBitmap frame = Settle(window);
            Color hue = ThemeResources.FillOf(Mechanism.Tcp, ThemeResources.CurrentMode);
            foreach (double offset in new[] { -1.5, 0.0, 1.5 })
            {
                Assert.Equal(hue, At(frame, mark + new Point(offset, 0)));
            }

            foreach (double offset in new[] { -4.5, 4.5 })
            {
                Assert.NotEqual(hue, At(frame, mark + new Point(offset, 0)));
            }

            // The burst's columns hold many records each: drawn edge to edge at their own columns, its middle in the hue.
            TimelineBucket busiest = TcpLane(workspace).MaxBy(bucket => bucket.ObservationCount)!;
            Assert.True(busiest.ObservationCount > 10, $"The burst's busiest column holds {busiest.ObservationCount}.");
            Point burst = timeline.TranslatePoint(timeline.PointOf(busiest)!.Value, window)!.Value;
            Assert.Equal(hue, At(frame, burst));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§6.2: a point beside a lone record's mark snaps to it, a point further off answers with the empty interval under it, and a click chooses the record's own interval")]
    public async Task APointBesideAMarkSnapsToIt()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        (MainWindow window, WorkspaceViewModel workspace, TimelineView timeline) = await OpenZoomed(session);
        try
        {
            (TimelineBucket lone, Point mark) = LoneMark(workspace, timeline, window, 0);

            // On the mark, and 5 px from its middle, where no ink is: both name the record's cell and state its own interval.
            foreach (double offset in new[] { 0.0, 2.0, 5.0, -5.0, 7.0 })
            {
                window.MouseMove(mark + new Point(offset, 0));
                Dispatch();
                Assert.Same(lone, timeline.HoveredBucket);
                Assert.Equal(WorkspaceTime.FormatHalfOpenRange(lone.Interval, CultureInfo.CurrentCulture),
                    Assert.IsType<HoverCard>(timeline.HoverCard).Title);
            }

            // Past the reach of the mark the pointer is on genuine emptiness, which is an answer of its own.
            Point empty = mark + new Point(9, 0);
            window.MouseMove(empty);
            Dispatch();
            TimelineBucket nothing = Assert.IsType<TimelineBucket>(timeline.HoveredBucket);
            Assert.Equal(0, nothing.ObservationCount);
            Point local = window.TranslatePoint(empty, timeline)!.Value;
            long under = ViewportMath.TickAtPixel(timeline.Viewport, local.X - timeline.PlotStart, timeline.PlotSpan);
            Assert.True(nothing.Interval.Contains(under), $"{nothing.Interval} does not hold the instant {under} under the pointer.");
            Assert.Equal(WorkspaceTime.FormatHalfOpenRange(nothing.Interval, CultureInfo.CurrentCulture),
                Assert.IsType<HoverCard>(timeline.HoverCard).Title);

            // A click beside the mark chooses the record's own one-pixel interval, not the width the mark is drawn at.
            Point beside = mark + new Point(5, 0);
            window.MouseMove(beside);
            window.MouseDown(beside, MouseButton.Left);
            window.MouseUp(beside, MouseButton.Left);
            Dispatch();
            Assert.Equal(lone.Interval, workspace.SelectedInterval);
            Assert.True(lone.Interval.SpanTicks <= (timeline.Viewport.SpanTicks / timeline.DeviceColumns) + 1,
                $"The chosen interval spans {lone.Interval.SpanTicks} ticks, wider than a column.");

            // A click on emptiness chooses that empty interval.
            window.MouseDown(empty, MouseButton.Left);
            window.MouseUp(empty, MouseButton.Left);
            Dispatch();
            Assert.Equal(nothing.Interval, workspace.SelectedInterval);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§6.2: a group's lanes are drawn as density too, a lone record's mark snapping as on the machine's lanes")]
    public async Task AGroupsLanesSnapToALoneRecord()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        (MainWindow window, WorkspaceViewModel workspace, TimelineView timeline) = await OpenZoomed(session);
        try
        {
            TimeRange zoomed = timeline.Viewport;
            workspace.SelectedRung = workspace.RungRows[0];
            Assert.True(workspace.Descend());
            timeline.SetViewport(zoomed);
            timeline.RequestDetailNow();
            await workspace.TimelineDetailReady;
            Settle(window);
            Assert.True(workspace.ShowsProcessLanes, workspace.TimelineCaption);
            ProcessTimelineLane lane = workspace.ProcessLaneDisplay.Single(candidate =>
                workspace.Snapshot.Processes.Single(process => process.Id == candidate.ProcessId).ProcessId == 100);
            Assert.Equal(workspace.TimelineDetail!.Buckets.Count, lane.Buckets.Count);
            TimelineBucket lone = lane.Buckets.Single(bucket => bucket.Interval.Contains(Presentation(workspace, Lone[1])));
            Assert.Equal(1, lone.ObservationCount);
            Point mark = timeline.TranslatePoint(timeline.PointOf(lane.ProcessId, lone)!.Value, window)!.Value;
            window.MouseMove(mark + new Point(6, 0));
            Dispatch();
            Assert.Same(lone, timeline.HoveredBucket);
            window.MouseMove(mark + new Point(10, 0));
            Dispatch();
            Assert.Equal(0, timeline.HoveredBucket!.ObservationCount);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Opens the session at its machine rung, zoomed to all but its first and last tenths, with that view's count.</summary>
    private static async Task<(MainWindow Window, WorkspaceViewModel Workspace, TimelineView Timeline)> OpenZoomed(
        TemporarySession session)
    {
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "Recording", "A published generation.", SessionPath: session.Path,
            Overview: SessionOverviewProjector.Project(session.Store)));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        TimeRange extent = workspace.Snapshot.Extent;
        timeline.SetViewport(new TimeRange(extent.StartTicks + (extent.SpanTicks / 10), extent.EndTicks - (extent.SpanTicks / 10)));
        timeline.RequestDetailNow();
        await workspace.TimelineDetailReady;
        Settle(window);
        Assert.True(workspace.ShowsMechanismLanes);
        return (window, workspace, timeline);
    }

    /// <summary>The zoomed TCP lane's cells, which the machine rung's one lane draws.</summary>
    private static IReadOnlyList<TimelineBucket> TcpLane(WorkspaceViewModel workspace) =>
        workspace.TimelineDetail!.MechanismLanes.Single(lane => lane.Mechanism == Mechanism.Tcp).Buckets;

    /// <summary>The cell holding lone record <paramref name="which"/>, and the middle of its mark in window coordinates.</summary>
    private static (TimelineBucket Bucket, Point Mark) LoneMark(WorkspaceViewModel workspace, TimelineView timeline, Window window,
        int which)
    {
        long at = Presentation(workspace, Lone[which]);
        TimelineBucket lone = TcpLane(workspace).Single(bucket => bucket.Interval.Contains(at));
        return (lone, timeline.TranslatePoint(timeline.PointOf(lone)!.Value, window)!.Value);
    }

    /// <summary>
    /// Where a record placed at <paramref name="native"/> hundredths of its row's tick lies on the timeline, which counts
    /// its source clock's own ticks from the first record's.
    /// </summary>
    private static long Presentation(WorkspaceViewModel workspace, long native) =>
        workspace.Snapshot.Extent.StartTicks + ((native - 1_000_000) / 100);

    /// <summary>
    /// Two instances of client.exe, both created as the capture begins. The first sends at 0.1 s, a burst of two hundred
    /// over 20 ms at 0.5 s, and once alone at each of 1.2 s and 1.5 s; the second once, at 2.1 s.
    /// </summary>
    internal static ObservationRowV1[] Rows()
    {
        long[] ticks = [1_000_000, .. Enumerable.Range(0, 200).Select(index => 5_000_000 + (index * 1_000L)), .. Lone];
        return
        [
            .. Clients.Select((process, index) => Lifecycle(10_000, ObservationKind.Create, process, (ulong)(index + 1))
                with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 1_000_000 }),
            .. ticks.Select((tick, index) => Transfer(tick / 100, ObservationKind.Send, AccountingSide.SendSide, 64, 100,
                (ulong)(100 + index)).Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = tick }),
            Transfer(210_000, ObservationKind.Send, AccountingSide.SendSide, 64, 101, 1_000).Between("127.0.0.1:50001", ServerEnd)
                with { SessionRelativeTicks = 21_000_000 },
        ];
    }

    private static WriteableBitmap Settle(Window window)
    {
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
        return window.CaptureRenderedFrame()!;
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
