using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
/// §6.2 in the window: zoomed, a group whose lanes would exceed the cell budget keeps them, counted in fewer, wider
/// columns, and the timeline draws each at its own resolution beside the machine row rather than dropping them.
/// </summary>
public sealed class CoarserLanesWindowTests
{
    [AvaloniaFact(DisplayName = "§6.2: a zoomed group past the cell budget draws its process lanes at their own coarser columns")]
    public async Task AZoomedLargeGroupDrawsItsCoarserLanes()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Pool(100));
        // As wide as a 4K screen: the view then asks its widest 2,000 columns, and a hundred lanes of them would not fit.
        var window = new MainWindow { Width = 3_600, Height = 1_400 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label.StartsWith("pool.exe", StringComparison.Ordinal));
        Assert.True(workspace.Descend());
        Dispatch();
        await workspace.TimelineDetailReady;
        Dispatch();

        // Zoomed to the middle half, the window asks for more columns than a hundred lanes can each have.
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        TimeRange extent = workspace.Snapshot.Extent;
        timeline.SetViewport(new TimeRange(extent.StartTicks + (extent.SpanTicks / 4), extent.EndTicks - (extent.SpanTicks / 4)));
        timeline.RequestDetailNow();
        await workspace.TimelineDetailReady;
        Dispatch();
        Assert.True(workspace.ShowsProcessLanes);
        Assert.Null(workspace.ProcessLaneProblem);
        Assert.Equal(100, workspace.ProcessLaneDisplay.Count);
        int machineColumns = workspace.TimelineDetail!.Buckets.Count;
        Assert.All(workspace.ProcessLaneDisplay, lane => Assert.Equal(
            Math.Min(machineColumns, SessionTimelineQuery.MaximumProcessLaneCells / 100), lane.Buckets.Count));
        Assert.True(workspace.ProcessLaneDisplay[0].Buckets.Count < machineColumns,
            $"The window asked {machineColumns} columns; a hundred lanes of them would fit the cell budget.");
        Assert.Contains("coarser than the view", workspace.TimelineCaption, StringComparison.Ordinal);

        // Each lane is drawn as a row of its own, at its own columns, where the machine row keeps the view's.
        ProcessTimelineLane lane = workspace.ProcessLaneDisplay.First(candidate => candidate.Buckets.Any(bucket =>
            bucket.ObservationCount > 0 && bucket.Interval.EndTicks > timeline.Viewport.StartTicks
            && bucket.Interval.StartTicks < timeline.Viewport.EndTicks));
        TimelineBucket drawn = lane.Buckets.First(bucket => bucket.ObservationCount > 0
            && bucket.Interval.EndTicks > timeline.Viewport.StartTicks && bucket.Interval.StartTicks < timeline.Viewport.EndTicks);
        Assert.NotNull(timeline.PointOf(lane.ProcessId, drawn));
        Save(window, "coarser-lanes-3600x1400.png");

        // Each lane on its own scale (§6.2), a hundred and one rows of them: the last lane reads against its own peak.
        workspace.ScalesEachLane = true;
        Dispatch();
        _ = window.CaptureRenderedFrame();
        ProcessTimelineLane last = workspace.ProcessLaneDisplay[^1];
        double ownPeak = last.Buckets
            .Where(bucket => bucket.Interval.EndTicks > timeline.Viewport.StartTicks && bucket.Interval.StartTicks < timeline.Viewport.EndTicks)
            .Select(bucket => (double)bucket.ObservationCount / bucket.Interval.SpanTicks)
            .DefaultIfEmpty(0)
            .Max() * WorkspaceTime.TicksPerSecond;
        Assert.Equal(ownPeak, timeline.RowScale(workspace.ProcessLaneDisplay.Count));
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§6.8/R13: a click on a coarser lane's cell makes that cell the analysis interval, and the inspector says how it was counted")]
    public async Task AClickOnACoarserLanesCellChoosesAndExplainsIt()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Pool(100));
        var window = new MainWindow { Width = 3_600, Height = 1_400 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label.StartsWith("pool.exe", StringComparison.Ordinal));
        Assert.True(workspace.Descend());
        Dispatch();
        await workspace.TimelineDetailReady;
        Dispatch();
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        TimeRange extent = workspace.Snapshot.Extent;
        timeline.SetViewport(new TimeRange(extent.StartTicks + (extent.SpanTicks / 4), extent.EndTicks - (extent.SpanTicks / 4)));
        timeline.RequestDetailNow();
        await workspace.TimelineDetailReady;
        Dispatch();
        Assert.True(workspace.ProcessLaneDisplay[0].Buckets.Count < workspace.TimelineDetail!.Buckets.Count);

        // A lane cell with records that no machine column matches: its hover names it, and a click chooses it whole, where
        // the machine column beneath the pointer was chosen before.
        (ProcessTimelineLane lane, TimelineBucket cell) = workspace.ProcessLaneDisplay
            .SelectMany(candidate => candidate.Buckets.Select(bucket => (candidate, bucket)))
            .First(pair => pair.bucket.ObservationCount > 0
                && pair.bucket.Interval.StartTicks >= timeline.Viewport.StartTicks && pair.bucket.Interval.EndTicks <= timeline.Viewport.EndTicks
                && !workspace.TimelineDetail.Buckets.Any(machine => machine.Interval == pair.bucket.Interval));
        Point at = timeline.TranslatePoint(timeline.PointOf(lane.ProcessId, cell)!.Value, window)!.Value;
        window.MouseMove(at);
        Dispatch();
        Assert.Equal(cell, timeline.HoveredBucket);
        Assert.Equal("Click makes it the analysis interval, explained in the inspector · Shift+drag brushes a range",
            Assert.IsType<HoverCard>(timeline.HoverCard).Lines[^1]);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatch();
        Assert.Equal(cell.Interval, workspace.SelectedInterval);

        // A press puts the card away; the pointer moved within the cell brings it back, saying it is chosen.
        window.MouseMove(new Point(at.X + 1, at.Y));
        Dispatch();
        Assert.Equal(cell, timeline.HoveredBucket);
        Assert.Equal("This bucket is the analysis interval", Assert.IsType<HoverCard>(timeline.HoverCard).Lines[^1]);

        // The inspector says, beneath the time scope, whose records the cell counts and the columns they were counted in.
        ProcessNode owner = workspace.Snapshot.Processes.Single(node => node.Id == lane.ProcessId);
        TextBlock explanation = window.GetControl<TextBlock>("CellExplanationText");
        Assert.True(explanation.IsEffectivelyVisible);
        Assert.Contains($"bound to {owner.NameWithPid}, ", explanation.Text, StringComparison.Ordinal);
        Assert.Contains("one of the lanes' own", explanation.Text, StringComparison.Ordinal);
        Assert.Equal(workspace.CellExplanation, explanation.Text);

        // A brushed range is no cell, and the explanation goes.
        workspace.SelectInterval(new TimeRange(cell.Interval.StartTicks, cell.Interval.EndTicks + 1));
        Dispatch();
        Assert.False(explanation.IsEffectivelyVisible);
        window.Close();
    }

    /// <summary>Keeps what the window drew beside the tests' other renders, for a person to look at.</summary>
    private static void Save(Window window, string name)
    {
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
        string directory = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(directory);
        window.CaptureRenderedFrame()!.Save(Path.Combine(directory, name));
    }

    private static CaptureUiUpdate Update(TemporarySession session) => new(
        CaptureUiPhase.Recording, "Recording", "A published generation.", SessionPath: session.Path,
        Overview: SessionOverviewProjector.Project(session.Store));

    /// <summary>
    /// <paramref name="count"/> instances of pool.exe, each created and then sending ten times across the capture, one
    /// instance a tick after another.
    /// </summary>
    private static ObservationRowV1[] Pool(int count) =>
    [
        .. Enumerable.Range(0, count).SelectMany(index => new[]
        {
            Lifecycle(index + 1, ObservationKind.Create, 5_000 + index, (ulong)(index * 20)) with
            {
                ResourceName = @"C:\Tools\pool.exe", SessionRelativeTicks = (index + 1) * 100L,
            },
        }.Concat(Enumerable.Range(0, 10).Select(step =>
            Transfer(1_000 + (step * 1_000) + (index * 7), ObservationKind.Send, AccountingSide.SendSide, 8, 5_000 + index,
                (ulong)((index * 20) + step + 1)) with { SessionRelativeTicks = (1_000 + (step * 1_000) + (index * 7)) * 100L }))),
    ];

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
