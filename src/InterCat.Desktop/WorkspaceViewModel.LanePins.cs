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

    /// <summary>What the pin control does to the selected process's lane now, with its key.</summary>
    public string LanePinLabel => IsSelectedLanePinned ? "Unpin lane (P)" : "Pin lane (P)";

    /// <summary>
    /// Pins the selected process's lane at the top of its group's lanes, after any pinned before it, or unpins it; false
    /// when the timeline shows no lane of a selected process.
    /// </summary>
    public bool ToggleSelectedLanePin()
    {
        if (SelectedProcessLane is not { } lane)
        {
            return false;
        }

        if (!pinnedLanes.Remove(lane.ProcessId))
        {
            pinnedLanes.Add(lane.ProcessId);
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
        OnPropertyChanged(nameof(ProcessLaneDisplay));
        OnPropertyChanged(nameof(PinnedLanes));
        OnPropertyChanged(nameof(IsSelectedLanePinned));
        OnPropertyChanged(nameof(LanePinLabel));
        OnPropertyChanged(nameof(TimelineCaption));
    }
}
