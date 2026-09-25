using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using Xunit;

namespace InterCat.Ui.Tests;

/// <summary>
/// The timeline's hover (§6.2's hover contract): the pointer over a bucket outlines it and draws a card stating its exact
/// half-open interval, value, meaning, unmeasured part, coverage and scale. The window's hover layer draws the card above
/// every pane, so the minimum window's short timeline pane does not clip it; hover never selects or brushes.
/// </summary>
public sealed class TimelineHoverTests
{
    [AvaloniaFact(DisplayName = "§6.2: hovering a timeline bucket describes it whole above every pane, and never selects it")]
    public async Task HoverDescribesABucketWithoutSelectingIt()
    {
        var viewModel = new WorkspaceViewModel(SyntheticWorkspace.Create(), "generation-1");
        var window = new MainWindow(viewModel) { Width = 1080, Height = 700 };
        window.Show();
        await viewModel.LayoutReady;
        Dispatch();
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        HoverOverlay layer = window.GetControl<HoverOverlay>("HoverLayer");
        TimelineBucket busiest = viewModel.Snapshot.Timeline.OrderByDescending(bucket => bucket.ObservationCount).First();
        TimeRange? interval = viewModel.SelectedInterval;

        window.MouseMove(At(timeline, window, timeline.PointOf(busiest)!.Value));
        Dispatch();
        Assert.Equal(busiest, timeline.HoveredBucket);
        HoverCard card = Assert.IsType<HoverCard>(timeline.HoverCard);
        Assert.Equal(WorkspaceTime.FormatHalfOpenRange(busiest.Interval, CultureInfo.CurrentCulture), card.Title);
        Assert.StartsWith(busiest.ObservationCount.ToString("N0", CultureInfo.CurrentCulture) + " observed records · mostly ",
            card.Lines[0], StringComparison.Ordinal);
        Assert.StartsWith("Basis: source observations · unit: records", card.Lines[1], StringComparison.Ordinal);
        Assert.Contains(card.Lines, line => line.StartsWith("Rate: ", StringComparison.Ordinal)
            && line.Contains("height against the busiest visible bar", StringComparison.Ordinal));
        Assert.Contains(card.Lines, line => line.StartsWith("Unmeasured: ", StringComparison.Ordinal));
        Assert.Contains(card.Lines, line => line.StartsWith("Coverage: ", StringComparison.Ordinal));
        Assert.Equal("Click makes it the analysis interval · Shift+drag brushes a range", card.Lines[^1]);
        Assert.Equal(interval, viewModel.SelectedInterval);

        // The window's layer draws the card, and all of it lies inside the window.
        Save(window, "timeline-hover.png");
        Assert.Equal(card.Title, layer.Card?.Title);
        Rect drawn = Assert.IsType<Rect>(layer.CardBounds);
        Assert.InRange(drawn.Left, 0, layer.Bounds.Width);
        Assert.InRange(drawn.Top, 0, layer.Bounds.Height);
        Assert.InRange(drawn.Right, 0, layer.Bounds.Width);
        Assert.InRange(drawn.Bottom, 0, layer.Bounds.Height);

        // The axis gutter is not the plot: nothing is hovered there, and the layer draws nothing.
        window.MouseMove(At(timeline, window, new(4, timeline.Bounds.Height / 2)));
        Dispatch();
        Assert.Null(timeline.HoverCard);
        _ = window.CaptureRenderedFrame();
        Assert.Null(layer.CardBounds);
        window.Close();
    }

    private static Point At(Control control, Window window, Point point) => control.TranslatePoint(point, window)!.Value;

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    private static void Save(Window window, string name)
    {
        Avalonia.Media.Imaging.WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        string directory = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(directory);
        frame.Save(Path.Combine(directory, name));
    }
}
