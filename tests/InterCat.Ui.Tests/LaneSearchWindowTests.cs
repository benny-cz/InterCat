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
/// §6.2's search lane names in the window: a PID typed in the search box at a group of a hundred and twenty lanes scrolls
/// the timeline to that process's lane and marks it, and nothing is selected until a hit is opened or a lane chosen.
/// </summary>
public sealed class LaneSearchWindowTests
{
    [AvaloniaFact(DisplayName = "§6.2: a PID typed at a large group scrolls the timeline to its lane and marks it")]
    public async Task APidTypedAtAGroupShowsItsLane()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Pool(120));
        var window = new MainWindow { Width = 1_080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
            Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label.StartsWith("pool.exe", StringComparison.Ordinal));
        Assert.True(workspace.Descend());
        Dispatch();
        await workspace.TimelineDetailReady;
        _ = Settle(window);
        ScrollViewer scroller = window.GetControl<ScrollViewer>("TimelineLaneScroller");
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        Assert.Equal(0, scroller.Offset.Y);

        // A lane far down the list: typed in the search box, its PID brings it into view, marked.
        ProcessNode wanted = workspace.Snapshot.Processes.Single(process => process.ProcessId == 5_110);
        int row = workspace.ProcessLaneDisplay.ToList().FindIndex(lane => lane.ProcessId == wanted.Id) + 1;
        Assert.True(timeline.RowBounds(row).Top > scroller.Viewport.Height, "The lane was in view before the search.");
        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
        window.GetControl<TextBox>("SearchBox").Text = "5110";
        WriteableBitmap frame = Settle(window);
        Rect bounds = timeline.RowBounds(row);
        Assert.InRange(bounds.Top - scroller.Offset.Y, 0, scroller.Viewport.Height - bounds.Height);
        Color ink = ThemeResources.ToColor(ThemePalette.Surfaces(ThemeResources.CurrentMode).Ink);
        Assert.Equal(ink, At(frame, Center(timeline, window, TimelineView.SearchMark(bounds))));
        Assert.NotEqual(ink, At(frame, Center(timeline, window, TimelineView.SearchMark(timeline.RowBounds(row - 1)))));
        Assert.Contains("1 found by the search", workspace.TimelineCaption, StringComparison.Ordinal);
        Assert.Null(workspace.SelectedProcess);

        // Escape clears the search and its marks; the view stays where the search took it.
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        frame = Settle(window);
        Assert.NotEqual(ink, At(frame, Center(timeline, window, TimelineView.SearchMark(timeline.RowBounds(row)))));
        Assert.DoesNotContain("by the search", workspace.TimelineCaption, StringComparison.Ordinal);
        window.Close();
    }

    private static Point Center(TimelineView timeline, Window window, Rect mark) =>
        timeline.TranslatePoint(new Point(mark.X + (mark.Width / 2), mark.Center.Y), window)!.Value;

    private static WriteableBitmap Settle(Window window)
    {
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
        return window.CaptureRenderedFrame()!;
    }

    /// <summary><paramref name="count"/> instances of pool.exe, PIDs from 5000, each created and then sending once.</summary>
    private static ObservationRowV1[] Pool(int count) =>
    [
        .. Enumerable.Range(0, count).SelectMany(index => new[]
        {
            Lifecycle(1 + index, ObservationKind.Create, 5_000 + index, (ulong)(1 + index)) with
            {
                ResourceName = @"C:\Tools\pool.exe", SessionRelativeTicks = 100L * (1 + index),
            },
            Transfer(1_000 + index, ObservationKind.Send, AccountingSide.SendSide, 8, 5_000 + index, (ulong)(1_000 + index)) with
            {
                SessionRelativeTicks = 100L * (1_000 + index),
            },
        }),
    ];

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
