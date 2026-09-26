using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
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
/// The evidence rung in a real window over a published session: the keys that reach it, load more and leave it, and
/// the hold that keeps records still while a live capture publishes a newer generation.
/// </summary>
public sealed class EvidenceRungWindowTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [AvaloniaFact(DisplayName = "R7: records hold the view still; a newer generation is offered, and applied on leaving")]
    public async Task TheEvidenceRungHoldsItsGenerationAndEscapeAppliesTheNewestOne()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(0, 150));
        var window = new MainWindow();
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var reading = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.True(window.GetControl<Button>("ShowRecordsButton").IsEnabled);

        window.GetControl<ListBox>("RungList").Focus();
        window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);
        Assert.True(reading.IsEvidenceRung);
        await reading.EvidenceReady;
        Dispatch();
        Assert.Equal(SessionEvidenceQuery.DefaultPageSize, reading.RungRows.Count);
        Assert.True(window.GetControl<ListBox>("RungList").IsVisible);
        Assert.False(window.GetControl<Button>("ShowRecordsButton").IsEnabled);

        window.KeyPressQwerty(PhysicalKey.M, RawInputModifiers.None);
        await reading.EvidenceReady;
        Dispatch();
        Assert.Equal(2 * SessionEvidenceQuery.DefaultPageSize, reading.RungRows.Count);
        Assert.True(reading.CanLoadMoreEvidence);

        // A live publication while records are read is held; the rows the user is reading stay where they are.
        Publish(session.Store, Exchange(150, 10));
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        Assert.Same(reading, window.DataContext);
        Assert.True(window.GetControl<Border>("HeldBanner").IsVisible);
        Assert.Contains("Generation 2", window.GetControl<TextBlock>("HeldBannerText").Text, StringComparison.Ordinal);

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatch();
        var updated = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.NotSame(reading, updated);
        Assert.False(updated.IsEvidenceRung);
        Assert.False(window.GetControl<Border>("HeldBanner").IsVisible);
        Assert.Contains("generation 2", updated.Snapshot.Title, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R7: F5 applies the held generation and keeps the evidence rung and its scope")]
    public async Task RefreshAppliesTheHeldGenerationWithoutLeavingTheRecords()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(0, 20));
        var window = new MainWindow();
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var reading = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        window.GetControl<ListBox>("RungList").Focus();
        window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);
        await reading.EvidenceReady;

        Publish(session.Store, Exchange(20, 5));
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        Assert.Same(reading, window.DataContext);

        window.KeyPressQwerty(PhysicalKey.F5, RawInputModifiers.None);
        Dispatch();
        var refreshed = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.NotSame(reading, refreshed);
        Assert.True(refreshed.IsEvidenceRung);
        await refreshed.EvidenceReady;
        Dispatch();
        Assert.Equal(2 * 25, refreshed.RungRows.Count);
        Assert.False(window.GetControl<Border>("HeldBanner").IsVisible);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§6.4: a zoomed timeline ranks what it shows once it settles, and Keep this range holds that scope")]
    public async Task AZoomedTimelineRanksWhatItShows()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(0, 100));
        var window = new MainWindow();
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        Dispatch();
        string whole = workspace.RungRows.Single().Observations;
        Button keep = window.GetControl<Button>("KeepRangeButton");
        TextBlock scope = window.GetControl<TextBlock>("RankingScope");
        Assert.False(keep.IsVisible);

        // A zoom to the extent's first quarter: once the viewport settles, the ranking counts only what is drawn.
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        TimeRange extent = workspace.Snapshot.Extent;
        var quarter = new TimeRange(extent.StartTicks, extent.StartTicks + (extent.SpanTicks / 4));
        timeline.SetViewport(quarter);
        timeline.RequestDetailNow();
        await workspace.IntervalReady;
        Dispatch();
        Assert.Equal(quarter, workspace.ScopeInterval);
        Assert.NotEqual(whole, workspace.RungRows.Single().Observations);
        Assert.True(scope.IsVisible);
        Assert.StartsWith("Ranked within the visible ", scope.Text, StringComparison.Ordinal);
        Assert.True(keep.IsVisible);

        // Keeping the range makes it the interval: panning on leaves the counts where they are.
        string kept = workspace.RungRows.Single().Observations;
        keep.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatch();
        Assert.Equal(quarter, workspace.SelectedInterval);
        Assert.False(keep.IsVisible);
        timeline.SetViewport(new TimeRange(quarter.EndTicks, quarter.EndTicks + quarter.SpanTicks));
        timeline.RequestDetailNow();
        await workspace.IntervalReady;
        Dispatch();
        Assert.Equal(kept, workspace.RungRows.Single().Observations);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R15: Shift and a drag across the timeline brushes a range and the ranking counts only that range")]
    public async Task DraggingTheTimelineBrushesARangeAndReRanks()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(0, 100));
        var window = new MainWindow();
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        Dispatch();
        string whole = workspace.RungRows.Single().Observations;

        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        double plot = timeline.PlotSpan;
        Avalonia.Point from = timeline.TranslatePoint(new(timeline.PlotStart + (0.10 * plot), timeline.Bounds.Height / 2), window)!.Value;
        Avalonia.Point to = timeline.TranslatePoint(new(timeline.PlotStart + (0.40 * plot), timeline.Bounds.Height / 2), window)!.Value;
        window.MouseDown(from, MouseButton.Left, RawInputModifiers.Shift);
        window.MouseMove(new(from.X + 20, from.Y), RawInputModifiers.Shift);
        window.MouseMove(to, RawInputModifiers.Shift);
        window.MouseUp(to, MouseButton.Left, RawInputModifiers.Shift);
        Dispatch();

        TimeRange brushed = Assert.IsType<TimeRange>(workspace.SelectedInterval);
        long span = workspace.Snapshot.Extent.EndTicks - workspace.Snapshot.Extent.StartTicks;
        Assert.InRange(brushed.EndTicks - brushed.StartTicks, span / 4, span / 2);
        await workspace.IntervalReady;
        Dispatch();
        Assert.True(workspace.IsRankedWithinInterval);
        Assert.NotEqual(whole, workspace.RungRows.Single().Observations);
        Assert.StartsWith("Ranked within", workspace.RankingScopeText, StringComparison.Ordinal);
        WriteableBitmapCheck(window, "brushed-interval.png");

        // A press without a drag still selects the single bucket under the pointer, as before.
        window.MouseDown(from, MouseButton.Left);
        window.MouseUp(from, MouseButton.Left);
        Dispatch();
        Assert.Contains(workspace.Snapshot.Timeline, bucket => bucket.Interval == workspace.SelectedInterval);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "6.2: the minimap brush follows the timeline and moves, resizes and fits its viewport")]
    public void TheMinimapBrushFollowsAndMovesTheViewport()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [.. Exchange(0, 100).Select(row => row with { SessionRelativeTicks = row.NativeTicks * 50_000_000L })]);
        var window = new MainWindow();
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        MinimapView minimap = window.GetControl<MinimapView>("MinimapSurface");
        SessionMinimap level = Assert.IsType<SessionMinimap>(workspace.Snapshot.Minimap);
        Assert.Equal(workspace.Snapshot.Extent, level.Extent);
        (double fitStart, double fitEnd) = minimap.Brush();

        // Zooming the timeline narrows the brush, never below the width that keeps it grabbable.
        timeline.Focus();
        for (int step = 0; step < 6; step++) window.KeyPressQwerty(PhysicalKey.Equal, RawInputModifiers.None);
        Dispatch();
        TimeRange zoomed = timeline.Viewport;
        (double start, double end) = minimap.Brush();
        Assert.True(end - start < fitEnd - fitStart);
        Assert.True(end - start >= MinimapView.MinimumBrushWidth);

        // Dragging the brush moves the viewport by time and keeps its span.
        double middle = minimap.Bounds.Height / 2;
        Point grab = minimap.TranslatePoint(new((start + end) / 2, middle), window)!.Value;
        window.MouseDown(grab, MouseButton.Left);
        window.MouseMove(grab + new Vector(-60, 0));
        window.MouseUp(grab + new Vector(-60, 0), MouseButton.Left);
        Dispatch();
        TimeRange moved = timeline.Viewport;
        Assert.Equal(zoomed.SpanTicks, moved.SpanTicks);
        Assert.True(moved.StartTicks < zoomed.StartTicks);

        // Dragging its right edge resizes it from that edge alone.
        (start, end) = minimap.Brush();
        Point edge = minimap.TranslatePoint(new(end, middle), window)!.Value;
        window.MouseDown(edge, MouseButton.Left);
        window.MouseMove(edge + new Vector(80, 0));
        window.MouseUp(edge + new Vector(80, 0), MouseButton.Left);
        Dispatch();
        Assert.Equal(moved.StartTicks, timeline.Viewport.StartTicks);
        Assert.True(timeline.Viewport.EndTicks > moved.EndTicks);

        // A press beside the brush centres the viewport there.
        TimeRange before = timeline.Viewport;
        Point beside = minimap.TranslatePoint(new(minimap.Bounds.Width - 20, middle), window)!.Value;
        window.MouseDown(beside, MouseButton.Left);
        window.MouseUp(beside, MouseButton.Left);
        Dispatch();
        Assert.Equal(before.SpanTicks, timeline.Viewport.SpanTicks);
        Assert.True(timeline.Viewport.StartTicks > before.StartTicks);
        WriteableBitmapCheck(window, "timeline-minimap.png");

        // 0 on the timeline fits again, and the brush spans the whole minimap.
        timeline.Focus();
        window.KeyPressQwerty(PhysicalKey.Digit0, RawInputModifiers.None);
        Dispatch();
        Assert.True(timeline.IsFit);
        Assert.Equal((fitStart, fitEnd), minimap.Brush());
        window.Close();
    }

    [AvaloniaFact(DisplayName = "6.2: the minimap wheel zooms at the pointer and its keyboard path shares the timeline")]
    public void MinimapWheelAndKeyboardNavigateOneViewport()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [.. Exchange(0, 100).Select(row => row with
        { SessionRelativeTicks = row.NativeTicks * 50_000_000L })]);
        var window = new MainWindow();
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        MinimapView minimap = window.GetControl<MinimapView>("MinimapSurface");
        TimeRange extent = timeline.Viewport;
        double middle = minimap.Bounds.Height / 2;

        Point center = minimap.TranslatePoint(new(minimap.Bounds.Width / 2, middle), window)!.Value;
        window.MouseWheel(center, new Vector(0, 1));
        Dispatch();
        TimeRange first = timeline.Viewport;
        Assert.True(first.SpanTicks < extent.SpanTicks);

        // Wheel beyond the current frame moves toward that part of the whole session before zooming.
        Point farRight = minimap.TranslatePoint(new(minimap.Bounds.Width - 20, middle), window)!.Value;
        window.MouseWheel(farRight, new Vector(0, 1));
        Dispatch();
        TimeRange second = timeline.Viewport;
        Assert.True(second.StartTicks > first.StartTicks);
        Assert.True(second.SpanTicks < first.SpanTicks);

        minimap.Focus();
        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        Dispatch();
        Assert.True(timeline.Viewport.StartTicks < second.StartTicks);
        window.KeyPressQwerty(PhysicalKey.Digit0, RawInputModifiers.None);
        Dispatch();
        Assert.Equal(extent, timeline.Viewport);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "6.2: a zoomed timeline draws its viewport's own buckets, and a press selects the finer one")]
    public async Task AZoomedTimelineDrawsItsOwnResolution()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [.. Exchange(0, 100).Select(row => row with { SessionRelativeTicks = row.NativeTicks * 50_000_000L })]);
        var window = new MainWindow();
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        timeline.Focus();
        for (int step = 0; step < 8; step++) window.KeyPressQwerty(PhysicalKey.Equal, RawInputModifiers.None);
        timeline.RequestDetailNow();
        await workspace.TimelineDetailReady;
        Dispatch();

        SessionTimelineDetail detail = Assert.IsType<SessionTimelineDetail>(workspace.TimelineDetail);
        Assert.True(timeline.Bounds.Width > 200 && timeline.Bounds.Height > 80,
            $"Timeline must be laid out inside its scroller: {timeline.Bounds}");
        Assert.Equal(timeline.Viewport, detail.Interval);
        Assert.Equal(workspace.DisplayedGeneration, detail.Generation);
        Assert.True(detail.Buckets.Count >= 16);

        // A press selects the bucket drawn under it, which is the viewport's own, finer than the overview's there.
        Point centre = timeline.TranslatePoint(new(timeline.PlotStart + (timeline.PlotSpan / 2), timeline.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Dispatch();
        TimeRange selected = Assert.IsType<TimeRange>(workspace.SelectedInterval);
        Assert.Contains(detail.Buckets, bucket => bucket.Interval == selected);
        TimelineBucket coarse = workspace.WholeSnapshot.Timeline.First(bucket => bucket.Interval.Contains(selected.StartTicks));
        Assert.True(selected.SpanTicks < coarse.Interval.SpanTicks);
        WriteableBitmapCheck(window, "zoomed-timeline.png");

        // Fitting again needs no detail: the overview answers the whole extent.
        timeline.SetViewport(null);
        timeline.RequestDetailNow();
        await workspace.TimelineDetailReady;
        Assert.Null(workspace.TimelineDetail);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§6.2: a zoomed mechanism lane hovers and selects its own fine bucket")]
    public async Task AZoomedMechanismLaneUsesItsOwnBucket()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [.. Exchange(0, 100).Select(row => row with
        { SessionRelativeTicks = row.NativeTicks * 50_000_000L })]);
        var window = new MainWindow();
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        Assert.True(workspace.ShowsMechanismLanes);
        TimeRange extent = timeline.Viewport;
        timeline.SetViewport(new TimeRange(extent.StartTicks + extent.SpanTicks / 4,
            extent.EndTicks - extent.SpanTicks / 4));
        timeline.RequestDetailNow();
        await workspace.TimelineDetailReady;
        Dispatch();

        SessionTimelineDetail detail = Assert.IsType<SessionTimelineDetail>(workspace.TimelineDetail);
        MechanismTimelineLane tcp = Assert.Single(detail.MechanismLanes, lane => lane.Mechanism == Mechanism.Tcp);
        TimelineBucket bucket = tcp.Buckets.First(candidate => candidate.ObservationCount > 0);
        workspace.SelectTimelineLane(Mechanism.Tcp);
        Assert.Equal(bucket.ObservationCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
            workspace.Intervals.Single(row => row.Interval == bucket.Interval).Observations);
        Assert.Contains("Zoomed view", workspace.IntervalTableScope, StringComparison.Ordinal);
        Point at = timeline.TranslatePoint(timeline.PointOf(bucket)!.Value, window)!.Value;
        window.MouseMove(at);
        Dispatch();
        Assert.Equal(bucket, timeline.HoveredBucket);
        HoverCard card = Assert.IsType<HoverCard>(timeline.HoverCard);
        Assert.Contains("TCP lane", card.Lines[0], StringComparison.Ordinal);
        Assert.Contains(card.Lines, line => line.Contains("this view's own count", StringComparison.Ordinal));
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatch();
        Assert.Equal(bucket.Interval, workspace.SelectedInterval);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§3.2/R15: a group's process lanes hover, select and name their canonical owners")]
    public async Task AGroupDrawsExactOwnerLanesWithMachineContext()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(0, 100));
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        workspace.SelectedRung = Assert.Single(workspace.RungRows);
        Assert.True(workspace.Descend());
        await workspace.TimelineDetailReady;
        Dispatch();

        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        Assert.True(workspace.ShowsProcessLanes, workspace.TimelineCaption);
        Assert.Equal(2, workspace.ProcessLaneDisplay.Count);
        Assert.Contains("machine context", workspace.TimelineCaption, StringComparison.Ordinal);
        Assert.Contains("2 process lanes", workspace.TimelineCaption, StringComparison.Ordinal);
        Assert.Equal(3 * 30 + 54, timeline.MinHeight);
        Assert.Equal(workspace.TimelineFocusBuckets!.Sum(bucket => bucket.ObservationCount),
            workspace.ProcessLaneDisplay.Sum(lane => lane.Buckets.Sum(bucket => bucket.ObservationCount)));

        foreach (ProcessTimelineLane lane in workspace.ProcessLaneDisplay)
        {
            TimelineBucket bucket = lane.Buckets.First(candidate => candidate.ObservationCount > 0);
            Point at = timeline.TranslatePoint(timeline.PointOf(lane.ProcessId, bucket)!.Value, window)!.Value;
            window.MouseMove(at);
            Dispatch();
            Assert.Equal(bucket, timeline.HoveredBucket);
            HoverCard card = Assert.IsType<HoverCard>(timeline.HoverCard);
            Assert.Contains("canonically owned", card.Lines[1], StringComparison.Ordinal);
            Assert.Contains(lane.ProcessId.ToString(), card.Lines[1], StringComparison.Ordinal);
            Assert.Contains(card.Lines, line => line.Contains("shared scale", StringComparison.Ordinal));
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Assert.Equal(bucket.Interval, workspace.SelectedInterval);
        }

        ProcessTimelineLane first = workspace.ProcessLaneDisplay[0];
        TimelineBucket firstBucket = first.Buckets.First(candidate => candidate.ObservationCount > 0);
        Point lanePoint = timeline.PointOf(first.ProcessId, firstBucket)!.Value;
        Point label = timeline.TranslatePoint(new(30, lanePoint.Y), window)!.Value;
        window.MouseDown(label, MouseButton.Left);
        window.MouseUp(label, MouseButton.Left);
        Dispatch();
        Assert.Equal(first.ProcessId, workspace.SelectedProcess?.Id);
        Assert.True(timeline.MinHeight > 0, "Selecting an owner must not collapse the L1 rows.");
        TimelineBucket nextOwnerBucket = first.Buckets.First(bucket =>
            bucket.Interval.StartTicks >= workspace.SelectedInterval!.Value.EndTicks && bucket.ObservationCount > 0);
        timeline.Focus();
        window.KeyPressQwerty(PhysicalKey.BracketRight, RawInputModifiers.None);
        Assert.Equal(nextOwnerBucket.Interval, workspace.SelectedInterval);
        workspace.ShowTables = true;
        Dispatch();
        Assert.True(workspace.HasSelectedProcessLane);
        Assert.Equal(firstBucket.ObservationCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
            workspace.Intervals.Single(row => row.Interval == firstBucket.Interval).Observations);
        Assert.Contains("owner records", workspace.IntervalTableScope, StringComparison.Ordinal);
        Button groupTotals = window.GetControl<Button>("GroupTotalsButton");
        Assert.True(groupTotals.IsVisible);
        Assert.True(groupTotals.Bounds.Width > 0 && groupTotals.Bounds.Height > 0);
        Point clear = groupTotals.TranslatePoint(
            new Point(groupTotals.Bounds.Width / 2, groupTotals.Bounds.Height / 2), window)!.Value;
        Assert.True(clear.X >= 0 && clear.X <= window.Bounds.Width,
            $"Group totals must be reachable in the {window.Bounds.Width:0}-pixel window, at {clear}");
        Assert.InRange(clear.Y, 0, window.Bounds.Height);
        window.MouseDown(clear, MouseButton.Left);
        window.MouseUp(clear, MouseButton.Left);
        Dispatch();
        Assert.False(workspace.HasSelectedProcessLane);
        Assert.Null(workspace.SelectedProcess);
        Assert.Contains("in focus", workspace.Intervals.Single(row => row.Interval == firstBucket.Interval).Observations,
            StringComparison.Ordinal);

        workspace.ShowTables = false;
        Point firstLabel = timeline.TranslatePoint(new(30, lanePoint.Y), window)!.Value;
        window.MouseDown(firstLabel, MouseButton.Left);
        window.MouseUp(firstLabel, MouseButton.Left);
        Assert.Equal(first.ProcessId, workspace.SelectedProcess?.Id);
        Point contextLabel = timeline.TranslatePoint(
            new(30, timeline.PointOf(workspace.Snapshot.Timeline[0])!.Value.Y), window)!.Value;
        window.MouseDown(contextLabel, MouseButton.Left);
        window.MouseUp(contextLabel, MouseButton.Left);
        Assert.Null(workspace.SelectedProcess);
        TimeRange extent = timeline.Viewport;
        timeline.SetViewport(new TimeRange(extent.StartTicks + extent.SpanTicks / 4,
            extent.EndTicks - extent.SpanTicks / 4));
        timeline.RequestDetailNow();
        await workspace.TimelineDetailReady;
        Dispatch();
        Assert.True(workspace.ShowsProcessLanes);
        Assert.Equal(2, workspace.ProcessLaneDisplay.Count);
        ProcessTimelineLane zoomedLane = workspace.ProcessLaneDisplay[0];
        TimelineBucket zoomedBucket = zoomedLane.Buckets.First(candidate => candidate.ObservationCount > 0);
        Point zoomedAt = timeline.TranslatePoint(timeline.PointOf(zoomedLane.ProcessId, zoomedBucket)!.Value, window)!.Value;
        window.MouseMove(zoomedAt);
        Dispatch();
        Assert.Equal(zoomedBucket, timeline.HoveredBucket);
        Assert.Contains(Assert.IsType<HoverCard>(timeline.HoverCard).Lines,
            line => line.Contains("this view's own count", StringComparison.Ordinal));
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§3.2/R15: an instance's source-direction lanes hover, select and step in the minimum window")]
    public async Task AnInstanceDrawsSourceDirectionLanesWithMachineContext()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Conversation(60));
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        foreach (string key in new[] { client.GroupKey, client.Id.ToString() })
        {
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == key);
            Assert.True(workspace.Descend());
            await workspace.TimelineDetailReady;
        }

        Dispatch();
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        ScrollViewer scroller = window.GetControl<ScrollViewer>("TimelineLaneScroller");
        Assert.True(workspace.ShowsDirectionLanes, workspace.TimelineCaption);
        Assert.Contains("by source direction", workspace.TimelineCaption, StringComparison.Ordinal);
        Assert.Equal(((SessionTimelineQuery.LaneDirections.Count + 1) * 30) + 54, timeline.MinHeight);
        DirectionTimelineLane outbound = Lane(Direction.Outbound);
        DirectionTimelineLane inbound = Lane(Direction.Inbound);
        Assert.Equal(30, outbound.Buckets.Sum(bucket => bucket.ObservationCount));
        Assert.Equal(30, inbound.Buckets.Sum(bucket => bucket.ObservationCount));

        // Each row has its own hit target and names itself; a click on a bar selects its interval.
        foreach (DirectionTimelineLane lane in new[] { outbound, inbound })
        {
            TimelineBucket bucket = lane.Buckets.First(candidate => candidate.ObservationCount > 0);
            Point at = Reveal(window, timeline, scroller, timeline.PointOf(lane.Direction, bucket)!.Value);
            window.MouseMove(at);
            Dispatch();
            Assert.Equal(bucket, timeline.HoveredBucket);
            HoverCard card = Assert.IsType<HoverCard>(timeline.HoverCard);
            string name = WorkspaceViewModel.DirectionLabel(lane.Direction);
            Assert.EndsWith($" · {name} lane", card.Lines[0], StringComparison.Ordinal);
            Assert.Contains($" marked {name.ToLowerInvariant()} · accounting:", card.Lines[1], StringComparison.Ordinal);
            Assert.StartsWith($"Direction: {name.ToLowerInvariant()}, as the source marks", card.Lines[2], StringComparison.Ordinal);
            Assert.Contains(card.Lines, line => line.EndsWith("(shared scale)", StringComparison.Ordinal));
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Assert.Equal(bucket.Interval, workspace.SelectedInterval);
        }

        // A row's name makes it the table and step focus: brackets then visit only its occupied buckets.
        TimelineBucket firstSent = outbound.Buckets.First(bucket => bucket.ObservationCount > 0);
        Point label = Reveal(window, timeline, scroller, new(30, timeline.PointOf(Direction.Outbound, firstSent)!.Value.Y));
        window.MouseDown(label, MouseButton.Left);
        window.MouseUp(label, MouseButton.Left);
        Dispatch();
        Assert.Equal(Direction.Outbound, workspace.SelectedTimelineDirection);
        TimeRange received = workspace.SelectedInterval!.Value;
        TimelineBucket lastSent = outbound.Buckets.Last(bucket => bucket.ObservationCount > 0
            && bucket.Interval.EndTicks <= received.StartTicks);
        timeline.Focus();
        window.KeyPressQwerty(PhysicalKey.BracketLeft, RawInputModifiers.None);
        Assert.Equal(lastSent.Interval, workspace.SelectedInterval);
        window.KeyPressQwerty(PhysicalKey.BracketRight, RawInputModifiers.None);
        Assert.Equal((outbound.Buckets.FirstOrDefault(bucket => bucket.ObservationCount > 0
            && bucket.Interval.StartTicks >= lastSent.Interval.EndTicks) ?? lastSent).Interval, workspace.SelectedInterval);

        // The same focus has a keyboard-reachable table equivalent inside the minimum window.
        workspace.ShowTables = true;
        Dispatch();
        ComboBox selector = window.GetControl<ComboBox>("DirectionLaneSelector");
        Assert.True(selector.IsVisible);
        Assert.False(window.GetControl<ComboBox>("TimelineLaneSelector").IsVisible);
        Assert.Equal(Direction.Outbound, Assert.IsType<DirectionLaneOption>(selector.SelectedItem).Direction);
        Point selectorCentre = selector.TranslatePoint(new(selector.Bounds.Width / 2, selector.Bounds.Height / 2), window)!.Value;
        Assert.InRange(selectorCentre.X, 0, window.Bounds.Width);
        Assert.InRange(selectorCentre.Y, 0, window.Bounds.Height);
        selector.SelectedItem = workspace.DirectionLaneOptions.Single(option => option.Direction == Direction.Inbound);
        Dispatch();
        Assert.Equal(Direction.Inbound, workspace.SelectedTimelineDirection);
        Assert.StartsWith("Inbound source-direction records", workspace.IntervalTableScope, StringComparison.Ordinal);
        TimelineBucket firstReceived = inbound.Buckets.First(bucket => bucket.ObservationCount > 0);
        Assert.Equal(firstReceived.ObservationCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
            workspace.Intervals.Single(row => row.Interval == firstReceived.Interval).Observations);
        workspace.ShowTables = false;
        Dispatch();

        // The machine row's name returns to all directions.
        Point machine = Reveal(window, timeline, scroller,
            new(30, timeline.PointOf(workspace.Snapshot.Timeline[0])!.Value.Y));
        window.MouseDown(machine, MouseButton.Left);
        window.MouseUp(machine, MouseButton.Left);
        Dispatch();
        Assert.Null(workspace.SelectedTimelineDirection);

        // Zoomed, every row is re-counted on the viewport's own columns.
        TimeRange extent = timeline.Viewport;
        timeline.SetViewport(new TimeRange(extent.StartTicks + (extent.SpanTicks / 4), extent.EndTicks - (extent.SpanTicks / 4)));
        timeline.RequestDetailNow();
        await workspace.TimelineDetailReady;
        Dispatch();
        Assert.True(workspace.ShowsDirectionLanes);
        DirectionTimelineLane zoomed = Lane(Direction.Inbound);
        TimelineBucket zoomedBucket = zoomed.Buckets.First(bucket => bucket.ObservationCount > 0);
        Point zoomedAt = Reveal(window, timeline, scroller, timeline.PointOf(Direction.Inbound, zoomedBucket)!.Value);
        window.MouseMove(zoomedAt);
        Dispatch();
        Assert.Equal(zoomedBucket, timeline.HoveredBucket);
        Assert.Contains(Assert.IsType<HoverCard>(timeline.HoverCard).Lines,
            line => line.Contains("this view's own count", StringComparison.Ordinal));
        Save(window.CaptureRenderedFrame()!, "l2-direction-lanes-1080x700.png");
        window.Close();

        DirectionTimelineLane Lane(Direction direction) =>
            workspace.TimelineDirectionLanes!.Single(lane => lane.Direction == direction);
    }

    [AvaloniaFact(DisplayName = "§3.2/R15: a channel's ends are lanes banded by direction that hover, select and step")]
    public async Task AChannelDrawsItsEndsBandedByDirection()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Conversation(60));
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        Channel channel = workspace.Snapshot.Channels.Single();
        foreach (string key in new[] { client.GroupKey, client.Id.ToString(), channel.Key })
        {
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == key);
            Assert.True(workspace.Descend());
            await workspace.TimelineDetailReady;
        }

        Dispatch();
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        ScrollViewer scroller = window.GetControl<ScrollViewer>("TimelineLaneScroller");
        Assert.True(workspace.ShowsChannelEndLanes, workspace.TimelineCaption);
        Assert.Equal((3 * 30) + 54, timeline.MinHeight);
        IReadOnlyList<ChannelEndTimelineLane> ends = workspace.TimelineChannelEndLanes!;
        Assert.Equal(2, ends.Count);
        ChannelEndTimelineLane clientEnd = ends.Single(end => end.Holder == client.Id);
        Assert.Equal((30, 30), (clientEnd.Outbound.Sum(bucket => bucket.ObservationCount),
            clientEnd.Inbound.Sum(bucket => bucket.ObservationCount)));
        Save(window.CaptureRenderedFrame()!, "l3-channel-ends-whole-1080x700.png");

        // Each end has its own hit target; its card names the end and splits the bucket by direction.
        foreach (ChannelEndTimelineLane end in ends)
        {
            TimelineBucket bucket = end.Buckets.First(candidate => candidate.ObservationCount > 0);
            Point at = Reveal(window, timeline, scroller, timeline.PointOfEnd(end.End, bucket)!.Value);
            window.MouseMove(at);
            Dispatch();
            Assert.Equal(bucket, timeline.HoveredBucket);
            HoverCard card = Assert.IsType<HoverCard>(timeline.HoverCard);
            Assert.EndsWith($" · the {end.Endpoint} end, held by {workspace.ChannelEndHolder(end)}", card.Lines[0],
                StringComparison.Ordinal);
            Assert.StartsWith("Direction: ", card.Lines[2], StringComparison.Ordinal);
            Assert.Contains(card.Lines, line => line.EndsWith("(shared scale)", StringComparison.Ordinal));
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Assert.Equal(bucket.Interval, workspace.SelectedInterval);
        }

        // An end's name makes it the table and step focus: brackets then visit only that end's occupied buckets.
        TimelineBucket first = clientEnd.Buckets.First(bucket => bucket.ObservationCount > 0);
        Point label = Reveal(window, timeline, scroller, new(30, timeline.PointOfEnd(clientEnd.End, first)!.Value.Y));
        window.MouseDown(label, MouseButton.Left);
        window.MouseUp(label, MouseButton.Left);
        Dispatch();
        Assert.Equal(clientEnd.End, workspace.SelectedChannelEnd);
        workspace.SelectInterval(first.Interval);
        timeline.Focus();
        window.KeyPressQwerty(PhysicalKey.BracketRight, RawInputModifiers.None);
        Assert.Equal(clientEnd.Buckets.First(bucket => bucket.ObservationCount > 0
            && bucket.Interval.StartTicks >= first.Interval.EndTicks).Interval, workspace.SelectedInterval);

        // The keyboard-reachable selector names both ends and fits the minimum window.
        workspace.ShowTables = true;
        Dispatch();
        ComboBox selector = window.GetControl<ComboBox>("ChannelEndSelector");
        Assert.True(selector.IsVisible);
        Assert.False(window.GetControl<ComboBox>("DirectionLaneSelector").IsVisible);
        Assert.Equal(clientEnd.End, Assert.IsType<ChannelEndOption>(selector.SelectedItem).End);
        Point selectorCentre = selector.TranslatePoint(new(selector.Bounds.Width / 2, selector.Bounds.Height / 2), window)!.Value;
        Assert.InRange(selectorCentre.X, 0, window.Bounds.Width);
        Assert.InRange(selectorCentre.Y, 0, window.Bounds.Height);
        ChannelEndTimelineLane serverEnd = ends.Single(end => end.End != clientEnd.End);
        selector.SelectedItem = workspace.ChannelEndOptions.Single(option => option.End == serverEnd.End);
        Dispatch();
        Assert.Equal(serverEnd.End, workspace.SelectedChannelEnd);
        Assert.StartsWith($"Records made at {workspace.ChannelEndLabel(serverEnd)}", workspace.IntervalTableScope,
            StringComparison.Ordinal);
        workspace.ShowTables = false;
        Dispatch();

        // The machine row's name returns to both ends.
        Point machine = Reveal(window, timeline, scroller, new(30, timeline.PointOf(workspace.Snapshot.Timeline[0])!.Value.Y));
        window.MouseDown(machine, MouseButton.Left);
        window.MouseUp(machine, MouseButton.Left);
        Dispatch();
        Assert.Null(workspace.SelectedChannelEnd);

        // Zoomed, both ends are re-counted on the viewport's own columns.
        TimeRange extent = timeline.Viewport;
        timeline.SetViewport(new TimeRange(extent.StartTicks + (extent.SpanTicks / 4), extent.EndTicks - (extent.SpanTicks / 4)));
        timeline.RequestDetailNow();
        await workspace.TimelineDetailReady;
        Dispatch();
        Assert.True(workspace.ShowsChannelEndLanes);
        ChannelEndTimelineLane zoomed = workspace.TimelineChannelEndLanes!.Single(end => end.End == clientEnd.End);
        TimelineBucket zoomedBucket = zoomed.Buckets.First(bucket => bucket.ObservationCount > 0);
        Point zoomedAt = Reveal(window, timeline, scroller, timeline.PointOfEnd(zoomed.End, zoomedBucket)!.Value);
        window.MouseMove(zoomedAt);
        Dispatch();
        Assert.Equal(zoomedBucket, timeline.HoveredBucket);
        Assert.Contains(Assert.IsType<HoverCard>(timeline.HoverCard).Lines,
            line => line.Contains("this view's own count", StringComparison.Ordinal));
        window.MouseMove(new Point(1, 1));
        Dispatch();
        Save(window.CaptureRenderedFrame()!, "l3-channel-ends-1080x700.png");
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§12/§19.3: a live preview continues the published timeline, labelled, inert and retired by publication")]
    public void ALivePreviewContinuesThePublishedTimeline()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(0, 100));
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        CaptureUiUpdate published = Update(session) with { OverviewChunks = 2 };
        window.ApplyCaptureUpdate(published);
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        double whole = timeline.PlotSpan;
        Assert.Null(workspace.LiveEdge);

        // The published extent ends at tick 210. Chunk 2 is shown; chunk 3 is not, and continues the timeline after it.
        var preview = new BrokerCapturePreview(20, 3, 1, 11, 11, 0,
            [new(2, 10, Mechanism.Tcp, 5), new(3, 11, Mechanism.Tcp, 4), new(3, 12, Mechanism.Tcp, 2)]);
        window.ApplyCaptureUpdate(published with { Overview = null, OverviewChunks = null, LivePreview = preview });
        Dispatch();
        LiveEdge edge = Assert.IsType<LiveEdge>(workspace.LiveEdge);
        Assert.Equal(new[] { new TimeRange(220, 240), new TimeRange(240, 260) }, edge.Bins.Select(bin => bin.Interval));
        Assert.True(timeline.PlotSpan < whole - 30, $"The live edge must take its own part of the plot: {timeline.PlotSpan} of {whole}.");

        // Hover explains the preview in its own words; a press on it selects nothing.
        Point at = timeline.TranslatePoint(timeline.PointOfLive(edge.Bins[0])!.Value, window)!.Value;
        window.MouseMove(at);
        Dispatch();
        Assert.Equal(edge.Bins[0], timeline.HoveredLiveBin);
        Assert.Null(timeline.HoveredBucket);
        HoverCard card = Assert.IsType<HoverCard>(timeline.HoverCard);
        Assert.EndsWith(" · live preview", card.Title, StringComparison.Ordinal);
        Assert.Equal("4 records journaled here and not yet published · TCP lane", card.Lines[0]);
        Assert.Contains(card.Lines, line => line.StartsWith("Not yet attributed to processes or channels", StringComparison.Ordinal));
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Assert.Null(workspace.SelectedInterval);
        Save(window.CaptureRenderedFrame()!, "live-edge-1080x700.png");
        window.MouseMove(new Point(1, 1));
        Dispatch();

        // A paused view shows an older generation, so it draws no edge; following again brings it back.
        window.GetControl<ListBox>("RungList").Focus();
        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.None);
        Assert.Null(workspace.LiveEdge);
        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.None);
        Assert.NotNull(workspace.LiveEdge);

        // Zoomed or panned, the view no longer follows the capture's end, and the published plot gets the width back.
        TimeRange extent = timeline.Viewport;
        timeline.SetViewport(new TimeRange(extent.StartTicks, extent.StartTicks + (extent.SpanTicks / 2)));
        Dispatch();
        Assert.Equal(whole, timeline.PlotSpan, 0.5);
        timeline.SetViewport(null);
        Dispatch();
        Assert.True(timeline.PlotSpan < whole - 30);

        // The publication that holds chunk 3 retires its preview; a finishing capture previews nothing.
        Publish(session.Store, Exchange(100, 20));
        window.ApplyCaptureUpdate(Update(session) with { OverviewChunks = 3, LivePreview = preview });
        Dispatch();
        var next = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.NotSame(workspace, next);
        Assert.Null(next.LiveEdge);
        window.ApplyCaptureUpdate(published with { Phase = CaptureUiPhase.Finishing, Overview = null, LivePreview = preview });
        Dispatch();
        Assert.Null(next.LiveEdge);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R15: a plain drag pans the timeline, and Home, End and 0 move it by keyboard")]
    public void APlainDragPansAndKeysMoveTheViewport()
    {
        using var session = new TemporarySession();
        // Exchanges 50 ms apart, so the session spans seconds and there is room to zoom and pan.
        Publish(session.Store, [.. Exchange(0, 100).Select(row => row with { SessionRelativeTicks = row.NativeTicks * 50_000_000L })]);
        var window = new MainWindow();
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        timeline.Focus();

        // Zoom in first so there is room to pan; a drag then moves the viewport and selects nothing.
        for (int step = 0; step < 4; step++) window.KeyPressQwerty(PhysicalKey.Equal, RawInputModifiers.None);
        TimeRange zoomed = timeline.Viewport;
        Assert.True(zoomed.SpanTicks < workspace.Snapshot.Extent.SpanTicks);
        window.KeyPressQwerty(PhysicalKey.Home, RawInputModifiers.None);
        Assert.Equal(workspace.Snapshot.Extent.StartTicks, timeline.Viewport.StartTicks);

        double plot = timeline.PlotSpan;
        Point from = timeline.TranslatePoint(new(timeline.PlotStart + (0.6 * plot), timeline.Bounds.Height / 2), window)!.Value;
        Point to = timeline.TranslatePoint(new(timeline.PlotStart + (0.3 * plot), timeline.Bounds.Height / 2), window)!.Value;
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);
        Dispatch();
        Assert.True(timeline.Viewport.StartTicks > workspace.Snapshot.Extent.StartTicks);
        Assert.Equal(zoomed.SpanTicks, timeline.Viewport.SpanTicks);
        Assert.Null(workspace.SelectedInterval);

        window.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.None);
        Assert.Equal(workspace.Snapshot.Extent.EndTicks, timeline.Viewport.EndTicks);
        window.KeyPressQwerty(PhysicalKey.Digit0, RawInputModifiers.None);
        Assert.Equal(workspace.Snapshot.Extent, timeline.Viewport);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§6.7: ] and [ step over empty buckets, + zooms around the interval, Shift+arrow pans a bucket, a sideways wheel pans")]
    public void TheTimelineStepsZoomsAroundTheIntervalAndPans()
    {
        using var session = new TemporarySession();
        // Two bursts far apart, so most of the session's buckets are empty between them.
        Publish(session.Store, [.. Exchange(0, 20).Concat(Exchange(400, 20))
            .Select(row => row with { SessionRelativeTicks = row.NativeTicks * 50_000_000L })]);
        var window = new MainWindow();
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        timeline.Focus();
        TimeRange extent = workspace.Snapshot.Extent;
        TimeRange[] held = [.. workspace.Snapshot.Timeline.Where(bucket => bucket.ObservationCount > 0).Select(bucket => bucket.Interval)];
        Assert.True(held.Length >= 2 && workspace.Snapshot.Timeline.Count > held.Length + 10);

        // ] walks the buckets that hold records in order, never an empty one, and stops after the last; [ walks back.
        foreach (TimeRange expected in held)
        {
            window.KeyPressQwerty(PhysicalKey.BracketRight, RawInputModifiers.None);
            Assert.Equal(expected, workspace.SelectedInterval);
        }

        window.KeyPressQwerty(PhysicalKey.BracketRight, RawInputModifiers.None);
        Assert.Equal(held[^1], workspace.SelectedInterval);
        window.KeyPressQwerty(PhysicalKey.BracketLeft, RawInputModifiers.None);
        TimeRange selected = held[^2];
        Assert.Equal(selected, workspace.SelectedInterval);

        // + zooms around the analysis interval, which keeps its place on screen, not around the viewport's centre.
        double plot = timeline.PlotSpan;
        long middle = selected.StartTicks + (selected.SpanTicks / 2);
        window.KeyPressQwerty(PhysicalKey.Equal, RawInputModifiers.None);
        Assert.Equal(ViewportMath.ZoomAtPixel(extent, ViewportMath.PixelAtTick(extent, middle, plot), plot, 1.25m, extent, 100_000),
            timeline.Viewport);

        // Shift with an arrow pans one drawn bucket; a plain arrow still pans a tenth of the span.
        window.KeyPressQwerty(PhysicalKey.Home, RawInputModifiers.None);
        TimeRange before = timeline.Viewport;
        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Shift);
        Assert.Equal(before.StartTicks + workspace.Snapshot.Timeline[0].Interval.SpanTicks, timeline.Viewport.StartTicks);
        Assert.Equal(before.SpanTicks, timeline.Viewport.SpanTicks);

        // A sideways wheel and Shift with the wheel pan a tenth of the span per notch, and never zoom.
        before = timeline.Viewport;
        Point onPlot = timeline.TranslatePoint(new(timeline.PlotStart + (plot / 2), timeline.Bounds.Height / 2), window)!.Value;
        window.MouseWheel(onPlot, new Vector(-1, 0));
        Assert.Equal(before.SpanTicks, timeline.Viewport.SpanTicks);
        Assert.Equal(before.StartTicks + (before.SpanTicks / 10), timeline.Viewport.StartTicks);
        before = timeline.Viewport;
        window.MouseWheel(onPlot, new Vector(0, -1), RawInputModifiers.Shift);
        Assert.Equal(before.SpanTicks, timeline.Viewport.SpanTicks);
        Assert.Equal(before.StartTicks + (before.SpanTicks / 10), timeline.Viewport.StartTicks);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§6.7: at the evidence rung ] and [ step record by record, and the timeline marks the one selected")]
    public async Task BracketsStepThroughTheRecords()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(0, 30));
        var window = new MainWindow();
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        window.GetControl<ListBox>("RungList").Focus();
        window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);
        await workspace.EvidenceReady;
        Dispatch();
        Assert.True(workspace.IsEvidenceRung);

        window.GetControl<TimelineView>("TimelineSurface").Focus();
        window.KeyPressQwerty(PhysicalKey.BracketRight, RawInputModifiers.None);
        Assert.Equal(workspace.RungRows[0].Key, workspace.SelectedRung?.Key);
        window.KeyPressQwerty(PhysicalKey.BracketRight, RawInputModifiers.None);
        Assert.Equal(workspace.RungRows[1].Key, workspace.SelectedRung?.Key);
        Assert.Equal(workspace.EvidenceMarkTicks[1], workspace.SelectedEvidenceTick);
        window.KeyPressQwerty(PhysicalKey.BracketLeft, RawInputModifiers.None);
        Assert.Equal(workspace.RungRows[0].Key, workspace.SelectedRung?.Key);

        // Before the first record there is nothing to step to; the selection stays.
        Assert.False(workspace.StepEvidence(-1));
        Assert.Equal(workspace.RungRows[0].Key, workspace.SelectedRung?.Key);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§6.7: a pinch zooms against the viewport it began from, so a long gesture does not drift")]
    public void APinchZoomsAgainstItsStartingViewport()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [.. Exchange(0, 100).Select(row => row with { SessionRelativeTicks = row.NativeTicks * 50_000_000L })]);
        var window = new MainWindow();
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        TimeRange fit = timeline.Viewport;
        double plot = timeline.PlotSpan;
        var centre = new Point(timeline.PlotStart + (0.4 * plot), timeline.Bounds.Height / 2);

        // Every scale of one gesture applies to the viewport it began from: 1.5 and then 2 is a zoom of 2, not of 3.
        timeline.RaiseEvent(new PinchEventArgs(1.5, centre));
        timeline.RaiseEvent(new PinchEventArgs(2.0, centre));
        Assert.Equal(ViewportMath.ZoomAtPixel(fit, 0.4 * plot, plot, 2.0m, fit, 100_000), timeline.Viewport);

        // The next gesture begins where this one left the viewport.
        timeline.RaiseEvent(new PinchEndedEventArgs());
        TimeRange after = timeline.Viewport;
        timeline.RaiseEvent(new PinchEventArgs(0.5, centre));
        Assert.Equal(ViewportMath.ZoomAtPixel(after, 0.4 * plot, plot, 0.5m, fit, 100_000), timeline.Viewport);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§6.7: a double click on the timeline zooms in by 2, keeping the instant under the pointer")]
    public void ADoubleClickZoomsInAtThePointer()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [.. Exchange(0, 100).Select(row => row with { SessionRelativeTicks = row.NativeTicks * 50_000_000L })]);
        var window = new MainWindow();
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        TimeRange fit = timeline.Viewport;
        double plot = timeline.PlotSpan;
        double pixel = 0.3 * plot;
        long under = ViewportMath.TickAtPixel(fit, pixel, plot);
        Point at = timeline.TranslatePoint(new(timeline.PlotStart + pixel, timeline.Bounds.Height / 2), window)!.Value;

        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatch();

        Assert.Equal(ViewportMath.ZoomAtPixel(fit, pixel, plot, 2.0m, fit, 100_000), timeline.Viewport);
        Assert.InRange(ViewportMath.TickAtPixel(timeline.Viewport, pixel, plot) - under, -timeline.Viewport.SpanTicks / 100,
            timeline.Viewport.SpanTicks / 100);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R7: pausing the live view holds its generation while recording continues, and F resumes it")]
    public void PausingTheLiveViewHoldsItAndFResumes()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(0, 10));
        var window = new MainWindow();
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var first = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.True(window.GetControl<Button>("FollowButton").IsVisible);
        Assert.Equal("Recording · following live", window.GetControl<TextBlock>("HealthStateText").Text);
        Assert.Equal("Loss is stated when recording stops", window.GetControl<TextBlock>("HealthLossText").Text);
        Assert.StartsWith("last publication", window.GetControl<TextBlock>("HealthFreshnessText").Text, StringComparison.Ordinal);

        window.GetControl<ListBox>("RungList").Focus();
        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.None);
        Assert.Equal("Recording · view paused", window.GetControl<TextBlock>("HealthStateText").Text);
        Assert.Equal("Follow live (F)", window.GetControl<Button>("FollowButton").Content);

        Publish(session.Store, Exchange(10, 5));
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        Assert.Same(first, window.DataContext);
        Assert.StartsWith("View paused at generation 1", window.GetControl<TextBlock>("HeldBannerText").Text,
            StringComparison.Ordinal);
        Assert.True(window.GetControl<Button>("HeldFollowButton").IsVisible);
        WriteableBitmapCheck(window, "paused-live-view.png");

        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.None);
        Dispatch();
        var resumed = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.NotSame(first, resumed);
        Assert.Contains("generation 2", resumed.Snapshot.Title, StringComparison.Ordinal);
        Assert.False(window.GetControl<Border>("HeldBanner").IsVisible);
        Assert.Equal("Recording · following live", window.GetControl<TextBlock>("HealthStateText").Text);

        // A saved session is not live: F does nothing and the strip says which loss statement applies.
        window.ApplyCaptureUpdate(Update(session) with { Phase = CaptureUiPhase.Complete }, forceOverview: true);
        Dispatch();
        Assert.False(window.GetControl<Button>("FollowButton").IsVisible);
        Assert.Equal("Saved session", window.GetControl<TextBlock>("HealthStateText").Text);
        Assert.Equal("No coverage ledger · loss unknown", window.GetControl<TextBlock>("HealthLossText").Text);
        Assert.Equal(string.Empty, window.GetControl<TextBlock>("HealthFreshnessText").Text);
        window.Close();
    }

    [AvaloniaTheory(DisplayName = "R21: live counters state each loss so far, and an unreadable counter as unreadable")]
    [InlineData(0L, 0L, 0L, "No loss reported yet · final coverage pending")]
    [InlineData(3L, 0L, 0L, "So far 3 records dropped by InterCat's queue · no ETW loss")]
    [InlineData(0L, 2L, 0L, "So far no drops · ETW lost 2 events")]
    [InlineData(0L, 0L, 1L, "So far no drops · ETW lost 1 buffer")]
    [InlineData(1L, 1L, 4L, "So far 1 record dropped by InterCat's queue · ETW lost 1 event and 4 buffers")]
    [InlineData(0L, null, null, "So far no drops · ETW loss unreadable")]
    [InlineData(5L, null, null, "So far 5 records dropped by InterCat's queue · ETW loss unreadable")]
    public void LiveCountersStateEachLossSoFar(long drops, long? events, long? buffers, string expected)
    {
        Assert.Equal("Loss is stated when recording stops", MainWindow.LiveLossStatement(null));
        Assert.Equal(expected, MainWindow.LiveLossStatement(new(40, 44, drops, events, buffers, 0, 64)));
    }

    [AvaloniaFact(DisplayName = "R21: live counters update the health strip while recording and leave the view where it is")]
    public void LiveCountersUpdateTheStripWithoutMovingTheView()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(0, 10));
        var window = new MainWindow();
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var shown = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        TextBlock loss = window.GetControl<TextBlock>("HealthLossText");
        TextBlock freshness = window.GetControl<TextBlock>("HealthFreshnessText");
        TextBlock detail = window.GetControl<TextBlock>("CaptureDetail");
        Assert.Equal("Loss is stated when recording stops", loss.Text);

        // The runner repeats its last recording update without an overview when only the counters changed: the strip
        // follows them, while the view and anything written to the detail line since stay as they are.
        detail.Text = "Exported 20 rows (complete) to view.json.";
        CaptureUiUpdate counters = Update(session) with
        {
            Overview = null,
            LiveHealth = new(420, 431, 3, 2, 0, 5, 65_536),
        };
        window.ApplyCaptureUpdate(counters);
        Dispatch();
        Assert.Same(shown, window.DataContext);
        Assert.Equal("So far 3 records dropped by InterCat's queue · ETW lost 2 events", loss.Text);
        Assert.StartsWith("420 admitted · last publication", freshness.Text, StringComparison.Ordinal);
        Assert.Equal("Exported 20 rows (complete) to view.json.", detail.Text);

        // A closed capture states loss from its ledger, never from the counters of a recording that ended.
        window.ApplyCaptureUpdate(counters with { Phase = CaptureUiPhase.Complete, Detail = "Session saved." }, forceOverview: true);
        Dispatch();
        Assert.Equal("No coverage ledger · loss unknown", loss.Text);
        Assert.Equal(string.Empty, freshness.Text);
        Assert.Equal("Session saved.", detail.Text);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "6.4: the window writes the applied view to the file the user chose, in either format")]
    public async Task TheWindowWritesTheAppliedViewToTheChosenFile()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(0, 12));
        var window = new MainWindow();
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        Assert.True(window.GetControl<Button>("ExportButton").IsVisible);

        string json = Path.Combine(session.Path, "view.json");
        Assert.Equal(1, (await window.WriteExportAsync(json, ExportFormat.Json)).Rows);
        using (var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(json)))
        {
            Assert.Equal("ranking", document.RootElement.GetProperty("kind").GetString());
            Assert.Equal("Machine", document.RootElement.GetProperty("context").GetProperty("rung").GetString());
        }

        string csv = Path.Combine(session.Path, "view.csv");
        Assert.Equal(1, (await window.WriteExportAsync(csv, ExportFormat.Csv)).Rows);
        Assert.Equal(2, (await File.ReadAllLinesAsync(csv)).Length);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "3.1: the capture card states how soon the first view arrived and how often it refreshes")]
    public void TheCaptureCardStatesFirstFeedback()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(0, 3));
        var window = new MainWindow();
        window.Show();
        TextBlock latency = window.GetControl<TextBlock>("CaptureLatency");

        window.ApplyCaptureUpdate(Update(session) with
        {
            Milestones = new(Started: TimeSpan.FromMilliseconds(650), PublicationInterval: TimeSpan.FromSeconds(2)),
        });
        Assert.Equal(string.Empty, latency.Text);

        window.ApplyCaptureUpdate(Update(session) with
        {
            Milestones = new(Started: TimeSpan.FromMilliseconds(650), FirstOverview: TimeSpan.FromMilliseconds(1_950),
                PublicationInterval: TimeSpan.FromSeconds(2)),
        });
        Assert.Contains($"{1.3:0.0} s after recording began", latency.Text, StringComparison.Ordinal);
        Assert.Contains("every 2 s", latency.Text, StringComparison.Ordinal);
        window.Close();
    }

    public static TheoryData<int, int> Sizes => new() { { 1080, 700 }, { 1456, 939 } };

    [AvaloniaTheory(DisplayName = "R15: a published session's channel and evidence rungs render at every supported size")]
    [MemberData(nameof(Sizes))]
    public async Task TheChannelAndEvidenceRungsRender(int width, int height)
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(0, 40));
        var window = new MainWindow { Width = width, Height = height };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        Channel channel = workspace.Snapshot.Channels.Single();
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        foreach (string key in new[] { client.GroupKey, client.Id.ToString(), channel.Key })
        {
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == key);
            Assert.True(workspace.Descend());
        }

        Dispatch();
        Assert.True(workspace.OffersEvidenceStep);
        Assert.NotNull(window.CaptureRenderedFrame());
        Dispatch();
        Save(window.CaptureRenderedFrame()!, $"real-channel-{width}x{height}.png");
        ScrollViewer crumbs = window.GetControl<ScrollViewer>("CrumbScroller");
        Assert.Equal(crumbs.Extent.Width - crumbs.Viewport.Width, crumbs.Offset.X, 1.0);

        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        workspace.SelectedRung = workspace.RungRows[3];
        Dispatch();
        WriteableBitmapCheck(window, $"real-evidence-{width}x{height}.png");
        window.Close();
    }

    [AvaloniaTheory(DisplayName = "§6.8: at every supported size rows, the header and every filter stay legible, and a running capture's card gives the list room")]
    [MemberData(nameof(Sizes))]
    public async Task RowsAndHeaderStayLegible(int width, int height)
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(0, 40));
        var window = new MainWindow { Width = width, Height = height };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        workspace.SelectedRung = workspace.RungRows[0];
        Assert.True(workspace.Descend());
        Assert.True(workspace.Ascend());
        Dispatch();
        _ = window.CaptureRenderedFrame();

        // A row's name has the rail's width less its count. The coverage words once shared the count's column and left
        // the name about 60 px, so "PID 200" read "PID …".
        ListBox list = window.GetControl<ListBox>("RungList");
        Control row = Assert.IsAssignableFrom<Control>(list.ContainerFromIndex(0));
        TextBlock name = row.GetVisualDescendants().OfType<TextBlock>()
            .First(text => text.Text == workspace.RungRows[0].Label);
        Assert.True(name.Bounds.Width >= 130, $"A ranked row's name has only {name.Bounds.Width:F0} px.");

        // Forward's face is short, so the title keeps its room beside the rung's buttons.
        Assert.True(window.GetControl<Button>("ForwardButton").IsVisible);
        TextBlock title = window.GetControl<TextBlock>("HeaderTitle");
        Assert.True(title.Bounds.Width >= 150, $"The header's title has only {title.Bounds.Width:F0} px.");

        // While the capture runs, its card holds only what can be done now; starting another and opening a saved
        // session wait for it to end, and their room goes to the ranked list.
        Assert.False(window.GetControl<Button>("StartExploringButton").IsVisible);
        Assert.False(window.GetControl<Button>("OpenSavedSessionButton").IsVisible);
        Assert.False(window.GetControl<TextBlock>("CaptureIntro").IsVisible);
        Assert.True(window.GetControl<Button>("StopCaptureButton").IsVisible);

        // At the evidence rung of a channel four filters apply; each chip names what it narrows, and all of them lie
        // inside the window, wrapping rather than running off its edge.
        Channel channel = workspace.Snapshot.Channels.Single();
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        foreach (string key in new[] { client.GroupKey, client.Id.ToString(), channel.Key })
        {
            workspace.SelectedRung = workspace.RungRows.Single(candidate => candidate.Key == key);
            Assert.True(workspace.Descend());
        }

        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Dispatch();
        _ = window.CaptureRenderedFrame();
        ListBox filters = window.GetControl<ListBox>("FilterList");
        Assert.Equal(["Group", "Process", "Channel", "Records of"],
            workspace.Filters.Select(filter => filter.Chip[..filter.Chip.IndexOf(':', StringComparison.Ordinal)]));
        for (int index = 0; index < workspace.Filters.Count; index++)
        {
            Control chip = Assert.IsAssignableFrom<Control>(filters.ContainerFromIndex(index));
            Point right = chip.TranslatePoint(new(chip.Bounds.Width, 0), window)!.Value;
            Assert.True(right.X <= window.Bounds.Width, $"Filter {index} ends at {right.X:F0} of {window.Bounds.Width:F0} px.");
        }

        window.Close();
    }

    private static void WriteableBitmapCheck(Window window, string name)
    {
        Avalonia.Media.Imaging.WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Save(frame!, name);
    }

    private static void Save(Avalonia.Media.Imaging.WriteableBitmap frame, string name)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(directory);
        frame.Save(Path.Combine(directory, name));
    }

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

    /// <summary>
    /// A client that sends for the first half of <paramref name="count"/> exchanges and receives for the second, so its
    /// instance's outbound and inbound rows are busy at different times.
    /// </summary>
    private static ObservationRowV1[] Conversation(int count) =>
    [
        .. Enumerable.Range(0, count).SelectMany(index =>
        {
            (int sender, int receiver) = index < count / 2 ? (100, 200) : (200, 100);
            (string from, string to) = sender == 100 ? (ClientEnd, ServerEnd) : (ServerEnd, ClientEnd);
            return new[]
            {
                Transfer(10 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 64, sender,
                    (ulong)(100 + (2 * index))).Between(from, to) with { SessionRelativeTicks = (10 + (2 * index)) * 100L },
                Transfer(11 + (2 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 64, receiver,
                    (ulong)(101 + (2 * index))).Between(to, from) with { SessionRelativeTicks = (11 + (2 * index)) * 100L },
            };
        }),
    ];

    /// <summary>Scrolls a timeline point into the lane scroller's view and returns it in window coordinates.</summary>
    private static Point Reveal(Window window, TimelineView timeline, ScrollViewer scroller, Point local)
    {
        scroller.Offset = new Vector(scroller.Offset.X, Math.Clamp(local.Y - (scroller.Bounds.Height / 2), 0,
            Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height)));
        Dispatch();
        return timeline.TranslatePoint(local, window)!.Value;
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
