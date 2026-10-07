using System.Globalization;
using InterCat.Application;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// Lanes a search finds (§6.2: search lane names). While a search is typed at a group's rung, the lanes of the processes it
/// finds are marked and counted in the timeline's caption, and the lane of its selected hit - else the first lane it finds -
/// is scrolled into view, so a PID typed in a group of a hundred lanes is shown without scrolling for it. A process is
/// found by the rule its search hit is ranked by, so every lane the search finds is marked, not only those of the hits the
/// rail lists. Nothing is selected: the search's Enter still opens its hit, and a click on the lane's name selects it.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    private HashSet<ProcessInstanceId> searchedLanes = [];

    // How many of the members folded into one lane the search finds: they have no lane of their own to mark (§6.2).
    private int searchedFolded;

    /// <summary>Whether the search typed finds the process whose lane this is.</summary>
    public bool IsLaneSearched(ProcessInstanceId process) => searchedLanes.Contains(process);

    /// <summary>
    /// The lane the search shows: its selected hit's, when that hit is a process whose lane is drawn, else the first lane it
    /// finds, in the order drawn; null while no search finds a lane drawn.
    /// </summary>
    public ProcessInstanceId? SearchedLane { get; private set; }

    /// <summary>
    /// What the lanes' caption adds while a search is typed: how many of them it finds, and how many of the processes folded
    /// into one lane, which have no lane of their own to mark.
    /// </summary>
    private string SearchedLanesNote => !IsSearching || !ShowsProcessLanes ? string.Empty
        : searchedLanes.Count == 0 && searchedFolded == 0 ? ", none found by the search"
        : (searchedLanes.Count == 0 ? string.Empty
                : string.Create(CultureInfo.CurrentCulture, $", {searchedLanes.Count:N0} found by the search"))
            + (searchedFolded == 0 ? string.Empty
                : searchedLanes.Count == 0
                    ? string.Create(CultureInfo.CurrentCulture, $", {searchedFolded:N0} found by the search among the folded members")
                    : string.Create(CultureInfo.CurrentCulture, $" and {searchedFolded:N0} among the folded members"));

    /// <summary>Finds the lanes drawn that the search typed finds, and the one it shows, saying what changed.</summary>
    private void FindSearchedLanes()
    {
        HashSet<ProcessInstanceId> found = [];
        int foldedFound = 0;
        if (IsSearching && ShowsProcessLanes)
        {
            Dictionary<ProcessInstanceId, ProcessNode> processes = wholeSnapshot.Processes.ToDictionary(process => process.Id);
            foreach (ProcessTimelineLane lane in processLaneDisplay)
            {
                if (processes.TryGetValue(lane.ProcessId, out ProcessNode? process) && WorkspaceSearch.Finds(process, searchText))
                {
                    found.Add(lane.ProcessId);
                }
            }

            foldedFound = FoldedLane?.Processes.Count(member =>
                processes.TryGetValue(member, out ProcessNode? process) && WorkspaceSearch.Finds(process, searchText)) ?? 0;
        }

        ProcessInstanceId? shown = selectedSearchResult?.Hit is { Kind: SearchHitKind.Process } hit
            && found.FirstOrDefault(lane => lane.ToString() == hit.Key) is var named && named != default
                ? named
                : processLaneDisplay.Select(lane => lane.ProcessId).FirstOrDefault(found.Contains) is var first && first != default
                ? first
                : null;
        string said = SearchedLanesNote;
        searchedLanes = found;
        searchedFolded = foldedFound;
        if (said != SearchedLanesNote)
        {
            OnPropertyChanged(nameof(TimelineCaption));
        }

        if (shown != SearchedLane)
        {
            SearchedLane = shown;
            OnPropertyChanged(nameof(SearchedLane));
        }
    }
}
