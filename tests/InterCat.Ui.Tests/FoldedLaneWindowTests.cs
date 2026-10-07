using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
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
/// §6.2's collapse groups in the window: a group past the lane bound draws its folded members as one row beneath its
/// lanes, whose card and cell say what it folds, and whose name selects no process, where the window drew no lane at all;
/// the pin control gives a folded member a lane of its own.
/// </summary>
public sealed class FoldedLaneWindowTests
{
    [AvaloniaFact(DisplayName = "§6.2: the window draws a large group's folded lane beneath its lanes, and a click on it chooses and explains its cell")]
    public async Task TheWindowDrawsAndChoosesTheFoldedLane()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Pool(230));
        var window = new MainWindow { Width = 1_400, Height = 900 };
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
        timeline.RequestDetailNow();
        await workspace.TimelineDetailReady;
        Dispatch();
        FoldedProcessLane folded = Assert.IsType<FoldedProcessLane>(workspace.FoldedLane);
        Assert.Equal((199, 31), (workspace.ProcessLaneDisplay.Count, folded.Processes.Count));

        // The folded row is the last, beneath every lane: scrolled to, a cell of it is where the pointer finds its bucket.
        ScrollViewer scroller = timeline.GetVisualAncestors().OfType<ScrollViewer>().First();
        scroller.Offset = new Vector(0, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height));
        Dispatch();
        _ = window.CaptureRenderedFrame();
        string rendered = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(rendered);
        window.CaptureRenderedFrame()!.Save(Path.Combine(rendered, "folded-lane-1400x900.png"));
        TimelineBucket cell = folded.Buckets.First(bucket => bucket.ObservationCount > 0
            && bucket.Interval.EndTicks > timeline.Viewport.StartTicks && bucket.Interval.StartTicks < timeline.Viewport.EndTicks);
        Point at = timeline.TranslatePoint(timeline.PointOfFolded(cell)!.Value, window)!.Value;
        window.MouseMove(at);
        Dispatch();
        Assert.Equal(cell, timeline.HoveredBucket);
        HoverCard card = Assert.IsType<HoverCard>(timeline.HoverCard);
        Assert.EndsWith(" · folded lane: 31 other members", card.Lines[0], StringComparison.Ordinal);

        // A click chooses the folded lane's cell, which the inspector explains as one.
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatch();
        Assert.Equal(cell.Interval, workspace.SelectedInterval);
        TextBlock explanation = window.GetControl<TextBlock>("CellExplanationText");
        Assert.True(explanation.IsEffectivelyVisible);
        Assert.Contains("bound to one of the 31 other members of this group", explanation.Text, StringComparison.Ordinal);

        // Its name stands for many processes, so a click on it selects none.
        workspace.SelectedProcess = null;
        window.MouseDown(new Point(timeline.TranslatePoint(new Point(12, 0), window)!.Value.X, at.Y), MouseButton.Left);
        window.MouseUp(new Point(timeline.TranslatePoint(new Point(12, 0), window)!.Value.X, at.Y), MouseButton.Left);
        Dispatch();
        Assert.Null(workspace.SelectedProcess);

        // A process folded with the least busy has no lane to select, but the pin control offers it one, and gives it.
        ProcessNode member = workspace.Snapshot.Processes.Single(process => process.Id == folded.Processes[0]);
        Button pin = window.GetControl<Button>("LanePinButton");
        Assert.False(pin.IsVisible);
        workspace.SelectProcess(member.Id);
        Dispatch();
        Assert.True(pin.IsVisible);
        Assert.Equal("Pin lane (P)", pin.Content);
        pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatch();
        await workspace.TimelineDetailReady;
        Dispatch();
        Assert.Equal(member.Id, workspace.ProcessLaneDisplay[0].ProcessId);
        Assert.DoesNotContain(member.Id, workspace.FoldedLane!.Processes);
        Assert.Equal("Unpin lane (P)", pin.Content);
        window.Close();
    }

    private static CaptureUiUpdate Update(TemporarySession session) => new(
        CaptureUiPhase.Recording, "Recording", "A published generation.", SessionPath: session.Path,
        Overview: SessionOverviewProjector.Project(session.Store));

    /// <summary><paramref name="count"/> instances of pool.exe, the one at index i sending i % 7 times after its creation.</summary>
    private static ObservationRowV1[] Pool(int count) =>
    [
        .. Enumerable.Range(0, count).SelectMany(index => new[]
        {
            Lifecycle(index + 1, ObservationKind.Create, 2_000 + index, (ulong)(index * 20)) with
            {
                ResourceName = @"C:\Tools\pool.exe", SessionRelativeTicks = (index + 1) * 100L,
            },
        }.Concat(Enumerable.Range(0, index % 7).Select(step =>
            Transfer(1_000 + (step * 1_000) + index, ObservationKind.Send, AccountingSide.SendSide, 8, 2_000 + index,
                (ulong)((index * 20) + step + 1)) with { SessionRelativeTicks = (1_000 + (step * 1_000) + index) * 100L }))),
    ];

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
