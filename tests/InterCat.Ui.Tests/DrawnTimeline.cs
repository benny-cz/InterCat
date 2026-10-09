using Avalonia.Controls;
using Avalonia.Headless;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;

namespace InterCat.Ui.Tests;

/// <summary>
/// What a window's timeline draws. The whole session is counted a column per device pixel once the view is laid out, as a
/// zoomed view is (§6.2), so a test that points at a cell first waits for that count and then takes the cell from it.
/// </summary>
internal static class DrawnTimeline
{
    /// <summary>Lays the window out, asks at once for the count its view needs and waits for it.</summary>
    public static async Task Counted(Window window, WorkspaceViewModel workspace, TimelineView timeline)
    {
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
        timeline.RequestDetailNow();
        await workspace.TimelineDetailReady;
        Dispatch();
    }

    /// <summary>The machine row's cells as drawn: the view's own count once it has arrived, else the overview's.</summary>
    public static IReadOnlyList<TimelineBucket> Machine(WorkspaceViewModel workspace) =>
        workspace.TimelineDetail?.Buckets ?? workspace.Snapshot.Timeline;

    /// <summary>A mechanism lane's cells as drawn: the view's own count once it has arrived, else the overview's.</summary>
    public static IReadOnlyList<TimelineBucket> Lane(WorkspaceViewModel workspace, Mechanism mechanism) =>
        (workspace.TimelineDetail?.MechanismLanes ?? workspace.Snapshot.MechanismLanes)
            .Single(lane => lane.Mechanism == mechanism).Buckets;

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
