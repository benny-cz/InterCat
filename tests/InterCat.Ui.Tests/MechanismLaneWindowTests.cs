using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using Xunit;

namespace InterCat.Ui.Tests;

public sealed class MechanismLaneWindowTests
{
    [AvaloniaFact(DisplayName = "§3.2: mechanism lanes have separate hit targets, exact hover and a shared time selection")]
    public async Task LanesHoverAndSelectTheirOwnBuckets()
    {
        WorkspaceSnapshot snapshot = WithLanes([Mechanism.Tcp, Mechanism.Udp]);
        using var workspace = new WorkspaceViewModel(snapshot, "synthetic-lanes");
        var window = new MainWindow(workspace) { Width = 1080, Height = 700 };
        window.Show();
        await workspace.LayoutReady;
        Dispatch();
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        Assert.True(workspace.ShowsMechanismLanes);
        Assert.Contains("2 lanes", workspace.TimelineCaption, StringComparison.Ordinal);

        foreach (MechanismTimelineLane lane in snapshot.MechanismLanes)
        {
            TimelineBucket bucket = lane.Buckets[5];
            Point point = Assert.IsType<Point>(timeline.PointOf(bucket));
            window.MouseMove(timeline.TranslatePoint(point, window)!.Value);
            Dispatch();
            Assert.Equal(bucket, timeline.HoveredBucket);
            HoverCard card = Assert.IsType<HoverCard>(timeline.HoverCard);
            Assert.Contains(EvidenceRowText.MechanismName(lane.Mechanism) + " lane", card.Lines[0],
                StringComparison.Ordinal);
            Assert.Contains("domain: " + EvidenceRowText.MechanismName(lane.Mechanism), card.Lines[1],
                StringComparison.Ordinal);
            Assert.Contains(card.Lines, line => line.Contains("shared scale", StringComparison.Ordinal));
            Assert.Contains(card.Lines, line => line.StartsWith("Coverage: ", StringComparison.Ordinal));
            Assert.Null(workspace.SelectedInterval);
        }

        TimelineBucket selected = snapshot.MechanismLanes[1].Buckets[5];
        Point at = timeline.TranslatePoint(timeline.PointOf(selected)!.Value, window)!.Value;
        window.MouseDown(at, Avalonia.Input.MouseButton.Left);
        window.MouseUp(at, Avalonia.Input.MouseButton.Left);
        Dispatch();
        Assert.Equal(selected.Interval, workspace.SelectedInterval);
        Assert.NotNull(window.CaptureRenderedFrame());
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§6.2: a long mechanism list scrolls by its label gutter without zooming time")]
    public async Task MoreLanesThanFitCanBeScrolled()
    {
        WorkspaceSnapshot snapshot = WithLanes([.. Enum.GetValues<Mechanism>().Take(12)]);
        using var workspace = new WorkspaceViewModel(snapshot, "synthetic-many-lanes");
        var window = new MainWindow(workspace) { Width = 1080, Height = 700 };
        window.Show();
        await workspace.LayoutReady;
        Dispatch();
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        ScrollViewer scroller = window.GetControl<ScrollViewer>("TimelineLaneScroller");
        Assert.True(timeline.Bounds.Height > scroller.Bounds.Height,
            $"Lane content {timeline.Bounds.Height:0.#} must exceed the {scroller.Bounds.Height:0.#} viewport.");
        TimeRange original = timeline.Viewport;

        Point labelGutter = timeline.TranslatePoint(new(30, 50), window)!.Value;
        window.MouseWheel(labelGutter, new Vector(0, -1));
        Dispatch();
        Assert.True(scroller.Offset.Y > 0, "The lane-name gutter must scroll to later lanes.");
        Assert.Equal(original, timeline.Viewport);
        timeline.Focus();
        double afterWheel = scroller.Offset.Y;
        window.KeyPressQwerty(Avalonia.Input.PhysicalKey.ArrowDown, Avalonia.Input.RawInputModifiers.None);
        Dispatch();
        Assert.True(scroller.Offset.Y > afterWheel, "Down must reach the next lane from the keyboard.");
        double afterArrow = scroller.Offset.Y;
        window.KeyPressQwerty(Avalonia.Input.PhysicalKey.PageDown, Avalonia.Input.RawInputModifiers.None);
        Dispatch();
        Assert.True(scroller.Offset.Y > afterArrow, "Page Down must reach later lanes without changing time.");
        Assert.Equal(original, timeline.Viewport);
        workspace.SelectedRung = workspace.RungRows[0];
        Assert.True(workspace.Descend());
        Dispatch();
        Assert.False(workspace.ShowsMechanismLanes);
        Assert.Equal(0, timeline.MinHeight);
        Assert.True(workspace.Ascend());
        Dispatch();
        Assert.True(workspace.ShowsMechanismLanes);
        Assert.True(timeline.MinHeight > scroller.Bounds.Height);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R15/§6.7: L0 lane focus has a keyboard table equivalent and survives a publication")]
    public async Task LaneFocusNarrowsTheTableAndBracketStep()
    {
        WorkspaceSnapshot snapshot = WithLanes([Mechanism.Tcp, Mechanism.Udp, Mechanism.NamedPipe, Mechanism.Rpc]);
        using var workspace = new WorkspaceViewModel(snapshot, "synthetic-lanes");
        var window = new MainWindow(workspace) { Width = 1080, Height = 700 };
        window.Show();
        await workspace.LayoutReady;
        Dispatch();
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        workspace.ShowTables = true;
        ComboBox selector = window.GetControl<ComboBox>("TimelineLaneSelector");
        Assert.True(selector.IsVisible);
        Assert.Equal(5, workspace.TimelineLaneOptions.Count);
        selector.SelectedItem = workspace.TimelineLaneOptions.Single(option => option.Mechanism == Mechanism.Rpc);
        Dispatch();
        Assert.Equal(Mechanism.Rpc, workspace.SelectedTimelineMechanism);
        Assert.Equal("0", workspace.Intervals[0].Observations);
        Assert.Contains("RPC lane", workspace.IntervalTableScope, StringComparison.Ordinal);

        timeline.Focus();
        window.KeyPressQwerty(PhysicalKey.BracketRight, RawInputModifiers.None);
        Dispatch();
        Assert.Equal(snapshot.MechanismLanes.Single(lane => lane.Mechanism == Mechanism.Rpc).Buckets[1].Interval,
            workspace.SelectedInterval);

        WorkspaceNavigationMemento saved = workspace.CaptureNavigation();
        using var resumed = new WorkspaceViewModel(snapshot, "synthetic-lanes-next");
        Assert.Null(resumed.RestoreNavigation(saved));
        Assert.Equal(Mechanism.Rpc, resumed.SelectedTimelineMechanism);
        Assert.Equal("0", resumed.Intervals[0].Observations);
        using var changed = new WorkspaceViewModel(WithLanes([Mechanism.Tcp]), "synthetic-lanes-gone");
        Assert.Contains("selected mechanism lane is not observed", changed.RestoreNavigation(saved),
            StringComparison.Ordinal);
        Assert.Null(changed.SelectedTimelineMechanism);

        // The canvas's label gutter is a second path to the same focus, without selecting a time bucket.
        workspace.ShowTables = false;
        Dispatch();
        TimelineBucket tcp = snapshot.MechanismLanes[0].Buckets[1];
        Point label = timeline.TranslatePoint(new(30, timeline.PointOf(tcp)!.Value.Y), window)!.Value;
        window.MouseDown(label, MouseButton.Left);
        window.MouseUp(label, MouseButton.Left);
        Dispatch();
        Assert.Equal(Mechanism.Tcp, workspace.SelectedTimelineMechanism);
        Assert.Equal(tcp.Interval, workspace.SelectedInterval);
        workspace.SelectTimelineLane(null);
        workspace.ClearSelection();
        timeline.Focus();
        window.KeyPressQwerty(PhysicalKey.BracketRight, RawInputModifiers.None);
        Assert.Equal(snapshot.Timeline[0].Interval, workspace.SelectedInterval);
        window.Close();
    }

    private static WorkspaceSnapshot WithLanes(Mechanism[] mechanisms)
    {
        WorkspaceSnapshot baseSnapshot = SyntheticWorkspace.Create();
        MechanismTimelineLane[] lanes = [.. mechanisms.Select((mechanism, index) =>
            new MechanismTimelineLane(mechanism, Array.AsReadOnly([.. baseSnapshot.Timeline.Select(bucket =>
                bucket with
                {
                    ObservationCount = (bucket.ObservationCount / mechanisms.Length)
                        + (index < bucket.ObservationCount % mechanisms.Length ? 1 : 0),
                    KnownBytes = null,
                    DominantMechanism = mechanism,
                    Coverage = index == 0 ? CoverageState.UnknownCoverage : CoverageState.Covered,
                })])))];
        return baseSnapshot with { MechanismLanes = Array.AsReadOnly(lanes) };
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
