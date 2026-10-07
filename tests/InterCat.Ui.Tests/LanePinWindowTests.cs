using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
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
/// §6.2's pinned lanes in the window: P with the timeline focused, or the timeline's own button, pins the selected
/// process's lane at the top of its group's lanes, where a pin's head marks it as the graph marks a pinned node, and a
/// later publication of the session keeps it pinned.
/// </summary>
public sealed class LanePinWindowTests
{
    [AvaloniaFact(DisplayName = "§6.2: P with the timeline focused, or its button, pins the selected process's lane at the top, marked")]
    public async Task PPinsTheSelectedLaneAtTheTop()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Pool());
        var window = new MainWindow { Width = 1_400, Height = 900 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        WorkspaceViewModel workspace = await OpenPool(window);
        ProcessNode busiest = Process(workspace, 500);
        ProcessNode quiet = Process(workspace, 503);
        Assert.Equal(busiest.Id, workspace.ProcessLaneDisplay[0].ProcessId);

        // The control shows only while a process's lane is selected, and says what it would do to it.
        Button pin = window.GetControl<Button>("LanePinButton");
        Assert.False(pin.IsVisible);
        workspace.SelectProcess(quiet.Id);
        Dispatch();
        Assert.True(pin.IsVisible);
        Assert.Equal("Pin lane (P)", pin.Content);
        Assert.Equal("Pin lane (P)", AutomationProperties.GetName(pin));

        // P with the keyboard in the timeline pins the lane, not the selected graph node.
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        timeline.Focus();
        window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.None);
        Dispatch();
        Assert.Equal([quiet.Id], workspace.PinnedLanes);
        Assert.Empty(workspace.PinnedGraphNodeKeys);
        Assert.Equal(quiet.Id, workspace.ProcessLaneDisplay[0].ProcessId);
        Assert.Equal("Unpin lane (P)", pin.Content);
        Assert.Equal("Unpin lane (P)", AutomationProperties.GetName(pin));
        Assert.Contains("4 process lanes, 1 pinned at the top", workspace.TimelineCaption, StringComparison.Ordinal);

        // A pin's head marks the pinned lane after its name, in the accent the graph marks a pinned node with.
        Color accent = ThemeResources.ToColor(ThemePalette.Surfaces(ThemeResources.CurrentMode).Accent);
        WriteableBitmap frame = Settle(window);
        Save(frame, "lane-pinned-1400x900.png");
        Assert.Equal(accent, At(frame, PinHeadOf(timeline, window, row: 1)));
        Assert.NotEqual(accent, At(frame, PinHeadOf(timeline, window, row: 2)));

        // The button unpins it, and the lane goes back beneath the busier ones.
        pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatch();
        Assert.Empty(workspace.PinnedLanes);
        Assert.Equal(busiest.Id, workspace.ProcessLaneDisplay[0].ProcessId);
        Assert.Equal(quiet.Id, workspace.ProcessLaneDisplay[^1].ProcessId);
        Assert.Equal("Pin lane (P)", pin.Content);
        frame = Settle(window);
        Assert.NotEqual(accent, At(frame, PinHeadOf(timeline, window, row: 1)));

        // With the keyboard elsewhere, P is the graph's: the lane stays where it is.
        window.GetControl<GraphView>("GraphSurface").Focus();
        window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.None);
        Dispatch();
        Assert.Empty(workspace.PinnedLanes);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§6.2: a later publication of the session keeps a pinned lane at the top")]
    public async Task ALaterPublicationKeepsAPinnedLane()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Pool());
        var window = new MainWindow { Width = 1_400, Height = 900 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        WorkspaceViewModel workspace = await OpenPool(window);
        ProcessNode quiet = Process(workspace, 503);
        workspace.SelectProcess(quiet.Id);
        Assert.True(workspace.ToggleSelectedLanePin());

        // The next generation: every instance has sent as much again, and the workspace is built anew.
        Publish(session.Store, Round(1));
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        WorkspaceViewModel next = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.NotSame(workspace, next);
        Assert.Equal([quiet.Id], next.PinnedLanes);
        await next.TimelineDetailReady;
        Dispatch();
        await next.TimelineDetailReady;
        Assert.True(next.ShowsProcessLanes);
        Assert.Equal(quiet.Id, next.ProcessLaneDisplay[0].ProcessId);
        Assert.Equal("Unpin lane (P)", window.GetControl<Button>("LanePinButton").Content);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§6.2: a lane pinned from far down a long group moves to the top and stays in view")]
    public async Task ALanePinnedFromFarDownStaysInView()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Many(30));
        var window = new MainWindow { Width = 1_080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label.StartsWith("many.exe", StringComparison.Ordinal));
        Assert.True(workspace.Descend());
        Dispatch();
        await workspace.TimelineDetailReady;
        Dispatch();
        Assert.Equal(30, workspace.ProcessLaneDisplay.Count);

        // The last lane, selected, is scrolled into view far down the list.
        ScrollViewer scroller = window.GetControl<ScrollViewer>("TimelineLaneScroller");
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        ProcessInstanceId last = workspace.ProcessLaneDisplay[^1].ProcessId;
        workspace.SelectProcess(last);
        Dispatch();
        Dispatch();
        Assert.True(scroller.Offset.Y > timeline.RowBounds(1).Bottom, "The last lane was not scrolled into view.");

        // Pinned, it is drawn first, and the list scrolls back to it there.
        Assert.True(workspace.ToggleSelectedLanePin());
        Dispatch();
        Dispatch();
        Assert.Equal(last, workspace.ProcessLaneDisplay[0].ProcessId);
        Rect row = timeline.RowBounds(1);
        Assert.InRange(row.Top - scroller.Offset.Y, 0, scroller.Viewport.Height - row.Height);
        window.Close();
    }

    /// <summary>Opens pool.exe's group, its four lanes counted.</summary>
    private static async Task<WorkspaceViewModel> OpenPool(Window window)
    {
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label.StartsWith("pool.exe", StringComparison.Ordinal));
        Assert.True(workspace.Descend());
        Dispatch();
        await workspace.TimelineDetailReady;
        Dispatch();
        Assert.True(workspace.ShowsProcessLanes);
        Assert.Equal(4, workspace.ProcessLaneDisplay.Count);
        return workspace;
    }

    /// <summary>Where row <paramref name="row"/>'s pin head is drawn, in the window: row 0 is the machine context.</summary>
    private static Point PinHeadOf(TimelineView timeline, Window window, int row) =>
        timeline.TranslatePoint(timeline.LanePinHead(row), window)!.Value;

    private static ProcessNode Process(WorkspaceViewModel workspace, int pid) =>
        workspace.Snapshot.Processes.Single(process => process.ProcessId == pid);

    /// <summary>Keeps a frame beside the tests' other renders, for a person to look at.</summary>
    private static void Save(WriteableBitmap frame, string name)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(directory);
        frame.Save(Path.Combine(directory, name));
    }

    private static WriteableBitmap Settle(Window window)
    {
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
        return window.CaptureRenderedFrame()!;
    }

    private static CaptureUiUpdate Update(TemporarySession session) => new(
        CaptureUiPhase.Recording, "Recording", "A published generation.", SessionPath: session.Path,
        Overview: SessionOverviewProjector.Project(session.Store));

    /// <summary>
    /// Four instances of pool.exe and a first round of their messages, the first sending four times as many as the last,
    /// so the busiest instance's lane is drawn first until another is pinned.
    /// </summary>
    private static ObservationRowV1[] Pool() =>
    [
        .. Enumerable.Range(0, 4).Select(index => Timed(Lifecycle(1 + index, ObservationKind.Create, 500 + index, (ulong)(1 + index))
            with { ResourceName = @"C:\Tools\pool.exe" })),
        .. Round(0),
    ];

    /// <summary>A round of pool.exe's messages: four from the first instance, down to one from the last.</summary>
    private static ObservationRowV1[] Round(int round) =>
    [
        .. Enumerable.Range(0, 4).SelectMany(index => Enumerable.Range(0, 4 - index).Select(send => Timed(Transfer(
            100 + (round * 100) + (index * 10) + send, ObservationKind.Send, AccountingSide.SendSide, 100, 500 + index,
            (ulong)(100 + (round * 100) + (index * 10) + send))))),
    ];

    /// <summary><paramref name="count"/> instances of many.exe, each created and then sending once.</summary>
    private static ObservationRowV1[] Many(int count) =>
    [
        .. Enumerable.Range(0, count).SelectMany(index => new[]
        {
            Timed(Lifecycle(1 + index, ObservationKind.Create, 700 + index, (ulong)(1 + index)) with
            {
                ResourceName = @"C:\Tools\many.exe",
            }),
            Timed(Transfer(100 + index, ObservationKind.Send, AccountingSide.SendSide, 100, 700 + index, (ulong)(100 + index))),
        }),
    ];

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
