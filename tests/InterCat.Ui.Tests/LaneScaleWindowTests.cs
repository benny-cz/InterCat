using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
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
/// §6.2's normalization scope in the window: one scale for every timeline lane by default, so heights compare, and each
/// lane against its own busiest bar one click from the legend, so a quiet lane's shape shows beside a busy one - which the
/// legend, the axis and every card then say.
/// </summary>
public sealed class LaneScaleWindowTests
{
    private const string BusyEnd = "127.0.0.1:50000";
    private const string QuietEnd = "127.0.0.1:50001";
    private const string ServerEnd = "127.0.0.1:8080";

    [AvaloniaFact(DisplayName = "§6.2: each lane on its own scale is one click from the legend, at every rung that draws lanes, and the axis and every card say which scale a height reads")]
    public async Task EachLaneOnItsOwnScaleIsOneClickAway()
    {
        // Two instances of client.exe: one sends in three bursts of ten, the other twice, alone, to a server that receives
        // both, so one shared scale flattens the quiet one.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Timed(Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" }),
            Timed(Lifecycle(2, ObservationKind.Create, 101, 2) with { ResourceName = @"C:\Tools\client.exe" }),
            Timed(Lifecycle(3, ObservationKind.Create, 200, 3) with { ResourceName = @"C:\Tools\server.exe" }),
            .. Enumerable.Range(0, 30).Select(index => Timed(Transfer(10 + (index / 10), ObservationKind.Send,
                AccountingSide.SendSide, 64, 100, (ulong)(10 + index)).Between(BusyEnd, ServerEnd))),
            Timed(Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 64, 101, 50).Between(QuietEnd, ServerEnd)),
            Timed(Transfer(21, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 51).Between(ServerEnd, QuietEnd)),
            Timed(Transfer(35, ObservationKind.Send, AccountingSide.SendSide, 64, 101, 52).Between(QuietEnd, ServerEnd)),
            Timed(Transfer(36, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 53).Between(ServerEnd, QuietEnd)),
        ]);
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        StackPanel key = window.GetControl<StackPanel>("LaneScalePanel");
        TextBlock said = window.GetControl<TextBlock>("LaneScaleText");
        ToggleButton toggle = window.GetControl<ToggleButton>("LaneScaleToggle");

        // The machine's mechanism lanes share one scale, which the legend says beside the toggle that changes it; on it,
        // the lifecycle lane's three records never reach the top of their row.
        Assert.True(workspace.ShowsMechanismLanes);
        Render(window);
        Assert.True(key.IsEffectivelyVisible);
        Assert.Equal("records per second, one scale for every lane", said.Text);
        Assert.False(toggle.IsChecked);
        IReadOnlyList<MechanismTimelineLane> mechanisms = workspace.Snapshot.MechanismLanes;
        int lifecycle = mechanisms.ToList().FindIndex(lane => lane.Mechanism == Mechanism.ProcessLifecycle);
        Assert.All(Enumerable.Range(0, mechanisms.Count), row => Assert.Equal(timeline.RowScale(0), timeline.RowScale(row)));
        Assert.False(ReachesTop(window, timeline, lifecycle, timeline.PointOf(Busiest(mechanisms[lifecycle].Buckets))!.Value.X));

        // Each lane its own: every mechanism lane reads against its own busiest bar, and the lifecycle lane's reaches its top.
        toggle.IsChecked = true;
        Render(window);
        Assert.Equal("records per second, each lane to its own peak", said.Text);
        Assert.Equal("each lane to its own peak", timeline.AxisPeakText);
        Assert.All(Enumerable.Range(0, mechanisms.Count), row =>
            Assert.Equal(PeakPerSecond(mechanisms[row].Buckets, timeline.Viewport), timeline.RowScale(row)));
        Assert.True(ReachesTop(window, timeline, lifecycle, timeline.PointOf(Busiest(mechanisms[lifecycle].Buckets))!.Value.X));
        toggle.IsChecked = false;

        // At the group, its two processes' lanes are drawn beneath the machine's on one scale, which their cards name.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "client.exe");
        Assert.True(workspace.Descend());
        await Until(() => workspace.ShowsProcessLanes && workspace.ProcessLaneDisplay.Count == 2);
        int busy = 1 + Lane(workspace, 100);
        int quiet = 1 + Lane(workspace, 101);
        ProcessTimelineLane quietLane = workspace.ProcessLaneDisplay[quiet - 1];
        Render(window);
        Assert.True(key.IsEffectivelyVisible);
        Assert.Equal([timeline.RowScale(0), timeline.RowScale(0)], new[] { timeline.RowScale(busy), timeline.RowScale(quiet) });
        Assert.EndsWith("/s", timeline.AxisPeakText, StringComparison.Ordinal);
        double quietX = timeline.PointOf(quietLane.ProcessId, Busiest(quietLane.Buckets))!.Value.X;
        Assert.False(ReachesTop(window, timeline, quiet, quietX));
        Assert.Contains(Hover(window, timeline, quietLane).Lines, line =>
            line.Contains("height against the busiest visible lane including machine context", StringComparison.Ordinal)
            && line.EndsWith("(shared scale)", StringComparison.Ordinal));

        // One click reads each lane against its own busiest bar: the quiet lane's is its own, far under the busy one's, its
        // busiest bar reaches its row's top, and the axis, the legend and its card say so.
        toggle.IsChecked = true;
        Render(window);
        Assert.True(workspace.ScalesEachLane);
        Assert.Equal("records per second, each lane to its own peak", said.Text);
        Assert.Equal("each lane to its own peak", timeline.AxisPeakText);
        Assert.Equal(PeakPerSecond(workspace.Snapshot.Timeline, timeline.Viewport), timeline.RowScale(0));
        Assert.Equal(PeakPerSecond(quietLane.Buckets, timeline.Viewport), timeline.RowScale(quiet));
        Assert.Equal(PeakPerSecond(workspace.ProcessLaneDisplay[busy - 1].Buckets, timeline.Viewport), timeline.RowScale(busy));
        Assert.True(timeline.RowScale(quiet) < timeline.RowScale(busy));
        Assert.True(ReachesTop(window, timeline, quiet, quietX));
        string own = $"height against this lane's own busiest visible bar, {TimelineView.RateText(timeline.RowScale(quiet))} (each lane on its own scale";
        Assert.Contains(Hover(window, timeline, quietLane).Lines, line => line.Contains(own, StringComparison.Ordinal));

        // The machine row above them reads against its own busiest bar too, which its card says.
        window.MouseMove(timeline.TranslatePoint(new Point(quietX, timeline.RowBounds(0).Center.Y), window)!.Value);
        Dispatch();
        HoverCard machineCard = Assert.IsType<HoverCard>(timeline.HoverCard);
        Assert.DoesNotContain(machineCard.Lines, line => line.Contains("canonically owned", StringComparison.Ordinal));
        Assert.Contains(machineCard.Lines, line => line.Contains(
            $"height against this lane's own busiest visible bar, {TimelineView.RateText(timeline.RowScale(0))} (each lane on its own scale",
            StringComparison.Ordinal));
        Save(window, "lanes-each-on-its-own-scale-1456x939.png");
        (window.Width, window.Height) = (window.MinWidth, window.MinHeight);
        Assert.Empty(LegibleTextTests.CutOff(window, "each lane on its own scale, at the smallest window"));
        (window.Width, window.Height) = (1456, 939);

        // Ranked by bytes sent, the lanes plot bytes, each against its own busiest column, which the legend names.
        workspace.RankBy = RankingMetric.BytesSent;
        await Until(() => workspace.ProcessLaneBytes is not null);
        Render(window);
        Assert.Equal("bytes sent per second, each lane to its own peak", said.Text);
        SessionIntervalByteMeasures quietBytes = workspace.ProcessLaneBytes!.Measures.Of(quietLane.ProcessId)!;
        Assert.Equal(BytePeakPerSecond(quietBytes, timeline.Viewport), timeline.RowScale(quiet));
        workspace.RankBy = RankingMetric.Records;
        await Until(() => workspace.ProcessLaneBytes is null);

        // The quiet process's direction rows, and its channel's two ends, each read against their own busiest bar too.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == quietLane.ProcessId.ToString());
        Assert.True(workspace.Descend());
        await Until(() => workspace.ShowsDirectionLanes);
        Render(window);
        IReadOnlyList<DirectionTimelineLane> directions = workspace.TimelineDirectionLanes!;
        Assert.All(Enumerable.Range(0, directions.Count), index =>
            Assert.Equal(PeakPerSecond(directions[index].Buckets, timeline.Viewport), timeline.RowScale(index + 1)));
        int sent = directions.ToList().FindIndex(lane => lane.Buckets.Any(bucket => bucket.ObservationCount > 0));
        double sentX = timeline.PointOf(directions[sent].Direction, Busiest(directions[sent].Buckets))!.Value.X;
        Assert.True(ReachesTop(window, timeline, sent + 1, sentX));
        toggle.IsChecked = false;
        Render(window);
        Assert.False(ReachesTop(window, timeline, sent + 1, sentX));
        toggle.IsChecked = true;
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == workspace.Snapshot.Channels.Single().Key);
        Assert.True(workspace.Descend());
        await Until(() => workspace.ShowsChannelEndLanes);
        Render(window);
        IReadOnlyList<ChannelEndTimelineLane> ends = workspace.TimelineChannelEndLanes!;
        Assert.Equal(2, ends.Count);
        Assert.All(Enumerable.Range(0, ends.Count), index => Assert.Equal(
            Math.Max(PeakPerSecond(ends[index].Outbound, timeline.Viewport), PeakPerSecond(ends[index].Inbound, timeline.Viewport)),
            timeline.RowScale(index + 1)));

        // The client's end sends: its outbound band reaches the top of its half on its own scale, and on the machine's
        // shared one stays near its floor.
        int client = ends.ToList().FindIndex(end => end.Outbound.Any(bucket => bucket.ObservationCount > 0));
        int column = Enumerable.Range(0, ends[client].Outbound.Count)
            .MaxBy(index => (double)ends[client].Outbound[index].ObservationCount / ends[client].Outbound[index].Interval.SpanTicks);
        double endX = timeline.PointOfEnd(ends[client].End, ends[client].Buckets[column])!.Value.X;
        Assert.True(ReachesBandTop(window, timeline, client + 1, endX));
        toggle.IsChecked = false;
        Render(window);
        Assert.False(ReachesBandTop(window, timeline, client + 1, endX));
        toggle.IsChecked = true;
        Render(window);

        // A newer publication of the session keeps the choice, as it keeps the ranking.
        Publish(session.Store, [Timed(Transfer(45, ObservationKind.Send, AccountingSide.SendSide, 64, 101, 54).Between(QuietEnd, ServerEnd))]);
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var newer = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.NotSame(workspace, newer);
        Assert.True(newer.ScalesEachLane);
        Assert.True(toggle.IsChecked);

        // Its records are one series, not lanes, so the key goes; back among lanes, it comes back, and off, one scale again.
        Assert.True(newer.ShowEvidence());
        Dispatch();
        Assert.False(key.IsEffectivelyVisible);
        Assert.True(newer.Ascend());
        await Until(() => newer.ShowsChannelEndLanes);
        Assert.True(key.IsEffectivelyVisible);
        toggle.IsChecked = false;
        Render(window);
        Assert.False(newer.ScalesEachLane);
        Assert.Equal(timeline.RowScale(0), timeline.RowScale(1));
        window.Close();
    }

    private static CaptureUiUpdate Update(TemporarySession session) => new(CaptureUiPhase.Recording, "Recording",
        "A published generation.", SessionPath: session.Path, Overview: SessionOverviewProjector.Project(session.Store));

    /// <summary>Where the lane of PID <paramref name="pid"/> stands among the group's process lanes.</summary>
    private static int Lane(WorkspaceViewModel workspace, int pid) => workspace.ProcessLaneDisplay.ToList()
        .FindIndex(lane => workspace.Snapshot.Processes.Single(process => process.Id == lane.ProcessId).ProcessId == pid);

    /// <summary>The bucket with the highest rate, the first of equals.</summary>
    private static TimelineBucket Busiest(IReadOnlyList<TimelineBucket> buckets) =>
        buckets.MaxBy(bucket => (double)bucket.ObservationCount / bucket.Interval.SpanTicks)!;

    /// <summary>The busiest rate, per second, of the buckets in <paramref name="visible"/>, read as the timeline reads it.</summary>
    private static double PeakPerSecond(IReadOnlyList<TimelineBucket> buckets, TimeRange visible) => buckets
        .Where(bucket => Intersects(bucket.Interval, visible))
        .Select(bucket => (double)bucket.ObservationCount / bucket.Interval.SpanTicks)
        .DefaultIfEmpty(0)
        .Max() * WorkspaceTime.TicksPerSecond;

    /// <summary>The busiest measured byte rate, per second, of a lane's columns in <paramref name="visible"/>.</summary>
    private static double BytePeakPerSecond(SessionIntervalByteMeasures lane, TimeRange visible) => Enumerable
        .Range(0, lane.Columns.Count)
        .Where(column => Intersects(lane.IntervalOf(column), visible)
            && lane.Columns[column].ValueOf(RankingMetric.BytesSent).Value is > 0)
        .Select(column => (double)lane.Columns[column].ValueOf(RankingMetric.BytesSent).Value!.Value / lane.IntervalOf(column).SpanTicks)
        .DefaultIfEmpty(0)
        .Max() * WorkspaceTime.TicksPerSecond;

    private static bool Intersects(TimeRange left, TimeRange right) =>
        left.StartTicks < right.EndTicks && right.StartTicks < left.EndTicks;

    /// <summary>
    /// Whether the bar at <paramref name="x"/> in lane row <paramref name="row"/> is drawn up to the top of its row: the
    /// colour just under the row's top is the bar's own, read just above its baseline, where any bar of a record is drawn.
    /// </summary>
    private static bool ReachesTop(Window window, TimelineView timeline, int row, double x)
    {
        Rect bounds = timeline.RowBounds(row);
        return SameInk(window, timeline, new Point(x, bounds.Top + 6), new Point(x, bounds.Bottom - 7));
    }

    /// <summary>
    /// Whether an end's outbound band at <paramref name="x"/> in row <paramref name="row"/> reaches the top of its half:
    /// the colour just under the band's top is the band's own, read just above the midline it rises from.
    /// </summary>
    private static bool ReachesBandTop(Window window, TimelineView timeline, int row, double x)
    {
        Rect bounds = timeline.RowBounds(row);
        double bandTop = bounds.Top + 3;
        double middle = (bandTop + bounds.Bottom - 6) / 2;
        return SameInk(window, timeline, new Point(x, bandTop + 2), new Point(x, middle - 3));
    }

    /// <summary>Whether two points of the timeline were drawn in one colour.</summary>
    private static bool SameInk(Window window, TimelineView timeline, Point first, Point second)
    {
        Avalonia.Media.Imaging.WriteableBitmap frame = Frame(window);
        return RenderedPixels.At(frame, timeline.TranslatePoint(first, window)!.Value)
            == RenderedPixels.At(frame, timeline.TranslatePoint(second, window)!.Value);
    }

    /// <summary>The card the pointer gets resting on the busiest bucket of <paramref name="lane"/>.</summary>
    private static HoverCard Hover(Window window, TimelineView timeline, ProcessTimelineLane lane)
    {
        Point at = timeline.TranslatePoint(timeline.PointOf(lane.ProcessId, Busiest(lane.Buckets))!.Value, window)!.Value;
        window.MouseMove(at);
        Dispatch();
        return Assert.IsType<HoverCard>(timeline.HoverCard);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int wait = 0; wait < 500 && !condition(); wait++)
        {
            Dispatch();
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    /// <summary>Draws the window, so the timeline's scales are those of what it shows.</summary>
    private static void Render(Window window) => _ = Frame(window);

    private static Avalonia.Media.Imaging.WriteableBitmap Frame(Window window)
    {
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
        return window.CaptureRenderedFrame()!;
    }

    /// <summary>Keeps what the window drew beside the tests' other renders, for a person to look at.</summary>
    private static void Save(Window window, string name)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(directory);
        Frame(window).Save(Path.Combine(directory, name));
    }

    /// <summary>A row whose session time is its reading in workspace ticks.</summary>
    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
