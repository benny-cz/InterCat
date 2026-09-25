using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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
