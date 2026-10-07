using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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
/// §6.2's virtualized rows: a group's timeline draws the lanes in view rather than every lane of a group of a hundred and
/// twenty, and a lane scrolled into view, or shown by a view grown taller, is drawn as it comes.
/// </summary>
public sealed class LaneVirtualizationWindowTests
{
    [AvaloniaFact(DisplayName = "§6.2: a large group's timeline draws only the lanes in view, and a lane scrolled or grown into view is drawn")]
    public async Task ALargeGroupDrawsTheLanesInView()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Pool(120));
        var window = new MainWindow { Width = 1_080, Height = 700 };
        window.Show();
        // A saved session: nothing live repaints the timeline, so a lane drawn after a scroll was drawn for the scroll.
        window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
            Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label.StartsWith("pool.exe", StringComparison.Ordinal));
        Assert.True(workspace.Descend());
        Dispatch();
        await workspace.TimelineDetailReady;
        WriteableBitmap frame = Settle(window);
        Assert.Equal(120, workspace.ProcessLaneDisplay.Count);

        // At the top, the machine row and the first lanes are drawn: those in view, not the rest of the hundred and twenty.
        ScrollViewer scroller = window.GetControl<ScrollViewer>("TimelineLaneScroller");
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        int shown = (int)Math.Ceiling(scroller.Viewport.Height / timeline.RowBounds(1).Height);
        Assert.Equal(0, timeline.DrawnRowRange.First);
        Assert.Equal(timeline.DrawnRowRange.Last + 1, timeline.RowsDrawn);
        Assert.InRange(timeline.RowsDrawn, shown - 1, shown + 1);
        Assert.True(Drawn(frame, timeline, window, workspace, 0), "The first lane's send is not drawn.");

        // Scrolled to the last lane, it is drawn there, and the first rows no longer are.
        scroller.Offset = new Vector(0, scroller.Extent.Height - scroller.Viewport.Height);
        frame = Settle(window);
        int last = workspace.ProcessLaneDisplay.Count;
        Assert.Equal(last, timeline.DrawnRowRange.Last);
        Assert.True(timeline.DrawnRowRange.First > 0, $"Row {timeline.DrawnRowRange.First} was drawn from the top.");
        Assert.InRange(timeline.RowsDrawn, shown - 1, shown + 1);
        Assert.True(Drawn(frame, timeline, window, workspace, last - 1), "The last lane's send is not drawn.");

        // Back at the top, the timeline filling the column shows more lanes without moving: they are drawn as it grows.
        scroller.Offset = new Vector(0, 0);
        frame = Settle(window);
        int before = timeline.DrawnRowRange.Last;
        window.GetControl<ToggleButton>("TimelineExpandToggle").IsChecked = true;
        frame = Settle(window);
        int taller = (int)Math.Ceiling(scroller.Viewport.Height / timeline.RowBounds(1).Height);
        Assert.True(taller > shown + 2, $"The view grew from {shown} rows to {taller}.");
        Assert.InRange(timeline.DrawnRowRange.Last, taller - 2, taller);
        Assert.True(timeline.DrawnRowRange.Last > before + 2, $"Rows drawn to {timeline.DrawnRowRange.Last}, as before at {before}.");
        Assert.True(Drawn(frame, timeline, window, workspace, timeline.DrawnRowRange.Last - 2),
            "A lane the taller view shows is not drawn.");
        window.Close();
    }

    /// <summary>
    /// Whether lane <paramref name="lane"/>'s send is drawn: its column holds the send's hue somewhere in the lane's row.
    /// </summary>
    private static bool Drawn(WriteableBitmap frame, TimelineView timeline, Window window, WorkspaceViewModel workspace, int lane)
    {
        ProcessTimelineLane drawn = workspace.ProcessLaneDisplay[lane];
        TimelineBucket bucket = drawn.Buckets.Single(candidate => candidate.DominantMechanism == Mechanism.Tcp);
        Color hue = ThemeResources.FillOf(Mechanism.Tcp, ThemeResources.CurrentMode);
        double x = timeline.PointOf(drawn.ProcessId, bucket)!.Value.X;
        Rect row = timeline.RowBounds(lane + 1);

        // A narrow bar's middle can fall on a pixel it only partly covers, so the pixels either side are read too.
        for (double y = row.Top + 1; y < row.Bottom - 1; y++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                if (At(frame, timeline.TranslatePoint(new Point(x + dx, y), window)!.Value) == hue)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static WriteableBitmap Settle(Window window)
    {
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
        return window.CaptureRenderedFrame()!;
    }

    /// <summary><paramref name="count"/> instances of pool.exe, each created and then sending once, a tick after another.</summary>
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
