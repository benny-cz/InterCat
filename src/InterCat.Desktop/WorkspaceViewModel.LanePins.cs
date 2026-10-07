using System.Globalization;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// Pinned lanes (§6.2: pin lanes, collapse groups, search lane names): a process lane a person pins is drawn at the top of
/// its group's lanes, in the order pinned, above the rest in the ranked table's order, and marked. A group of many
/// instances - one real capture's svchost.exe held 97 - keeps the few being compared together rather than scattered down
/// a long list. A later publication of the session keeps its pins, as it keeps the graph's.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    private readonly List<ProcessInstanceId> pinnedLanes = [];

    /// <summary>The process lanes pinned, the first pinned first.</summary>
    public IReadOnlyList<ProcessInstanceId> PinnedLanes => pinnedLanes.AsReadOnly();

    /// <summary>Whether the process's lane is pinned: drawn at the top of its group's lanes, and marked.</summary>
    public bool IsLanePinned(ProcessInstanceId process) => pinnedLanes.Contains(process);

    /// <summary>Whether the selected process's lane is pinned.</summary>
    public bool IsSelectedLanePinned => SelectedProcessLane is { } lane && pinnedLanes.Contains(lane.ProcessId);

    /// <summary>
    /// Whether the pin control acts on the selected process: its lane is drawn, or it is folded with the group's least busy
    /// past the lane bound, where a pin gives it a lane of its own (§6.2: collapse groups).
    /// </summary>
    public bool OffersLanePin => HasSelectedProcessLane || SelectedFoldedMember is not null;

    /// <summary>The selected process when the group's folded lane counts its records rather than a lane of its own.</summary>
    private ProcessInstanceId? SelectedFoldedMember => selectedProcess is { } process
        && FoldedLane is { } folded && folded.Processes.Contains(process.Id) ? process.Id : null;

    /// <summary>What the pin control does to the selected process's lane now, with its key.</summary>
    public string LanePinLabel => IsSelectedLanePinned ? "Unpin lane (P)" : "Pin lane (P)";

    /// <summary>
    /// Pins the selected process's lane at the top of its group's lanes, after any pinned before it, or unpins it; a process
    /// folded with the group's least busy is pinned into a lane of its own. False when the timeline shows no lane of a
    /// selected process and folds none of its records.
    /// </summary>
    public bool ToggleSelectedLanePin()
    {
        if ((SelectedProcessLane?.ProcessId ?? SelectedFoldedMember) is not { } process)
        {
            return false;
        }

        if (!pinnedLanes.Remove(process))
        {
            pinnedLanes.Add(process);
        }

        ReorderProcessLanes();
        return true;
    }

    /// <summary>Pins these lanes after any pinned already, as a later publication of the session keeps them.</summary>
    internal void PinLanes(IEnumerable<ProcessInstanceId> lanes)
    {
        ArgumentNullException.ThrowIfNull(lanes);
        foreach (ProcessInstanceId lane in lanes)
        {
            if (!pinnedLanes.Contains(lane))
            {
                pinnedLanes.Add(lane);
            }
        }

        ReorderProcessLanes();
    }

    /// <summary>How many of the lanes drawn are pinned, as the timeline's caption says it.</summary>
    private int PinnedLanesShown => processLaneDisplay.Count(lane => pinnedLanes.Contains(lane.ProcessId));

    /// <summary>What the lanes' caption adds when some of those drawn are pinned: how many, at the top.</summary>
    private string PinnedLanesNote => PinnedLanesShown is > 0 and var pinned
        ? string.Create(CultureInfo.CurrentCulture, $", {pinned:N0} pinned at the top")
        : string.Empty;

    private void ReorderProcessLanes()
    {
        processLaneDisplay = timelineProcessLanes is null ? [] : OrderProcessLanes(timelineProcessLanes);
        FindSearchedLanes();
        OnPropertyChanged(nameof(ProcessLaneDisplay));
        OnPropertyChanged(nameof(PinnedLanes));
        OnPropertyChanged(nameof(IsSelectedLanePinned));
        OnPropertyChanged(nameof(LanePinLabel));
        OnPropertyChanged(nameof(TimelineCaption));

        // Past the lane bound the pins choose which members have lanes of their own, so the lanes are counted again: one
        // pinned out of the folded lane is drawn at the top, and one unpinned folds if it is among the least busy.
        if (drawnTimeline is { } drawn && LanesToDraw(timelineFocus) is not null)
        {
            RequestTimelineDetail(drawn.Viewport, drawn.Columns);
        }
    }
}
