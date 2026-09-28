using System.Globalization;
using InterCat.Application;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// Who started the selected process, and whom it started (`entities-v1` §3's parent link). A parent is linked by its
/// start key or by its PID and the child's creation reading, and one named but not in the capture stays a PID, never a
/// guessed process. The inspector states both, and each is one action away: the parent as the selection, the children
/// as a multi-selection Enter turns into their records (§6.7).
/// </summary>
public sealed partial class WorkspaceViewModel
{
    /// <summary>At most this many children are named; the rest are counted.</summary>
    private const int NamedChildren = 3;

    /// <summary>Whether the inspector states the selected process's lineage: one process of a real session.</summary>
    public bool ShowsLineage => realOverview && selectedProcess is not null && !HasMultiSelection
        && SelectedGroup is null && SelectedCluster is null;

    /// <summary>The selected process's parent when the capture holds it.</summary>
    private ProcessNode? ParentOfSelected => selectedProcess?.Parent is { } parent
        ? wholeSnapshot.Processes.FirstOrDefault(process => process.Id == parent)
        : null;

    /// <summary>The processes the selected one started, as their records link them to it, in the snapshot's order.</summary>
    private IReadOnlyList<ProcessNode> ChildrenOfSelected => selectedProcess is { } selected
        ? [.. wholeSnapshot.Processes.Where(process => process.Parent == selected.Id)]
        : [];

    /// <summary>
    /// Who started the selected process: the parent by name and PID, with how the link rests; a named PID the capture
    /// never saw; or that no record of it named one.
    /// </summary>
    public string ParentText
    {
        get
        {
            if (selectedProcess is not { } process) return string.Empty;
            if (ParentOfSelected is { } parent)
            {
                return parent.NameWithPid + (process.ParentBinding == RelationStrength.Direct
                    ? " · linked by its start key"
                    : " · linked by its PID and this process's start time");
            }

            return process.ParentProcessId is { } named
                ? string.Create(CultureInfo.CurrentCulture, $"PID {named} · not in this capture")
                : "No record of this process names its parent";
        }
    }

    /// <summary>Whom the selected process started: named, the rest counted, or none the capture holds.</summary>
    public string ChildrenText
    {
        get
        {
            if (selectedProcess is null) return string.Empty;
            IReadOnlyList<ProcessNode> children = ChildrenOfSelected;
            if (children.Count == 0) return "None seen in this capture";
            string names = string.Join(", ", children.Take(NamedChildren).Select(child => child.NameWithPid));
            return Counted(children.Count, "process", "processes") + ": " + names
                + (children.Count > NamedChildren
                    ? string.Create(CultureInfo.CurrentCulture, $" and {children.Count - NamedChildren:N0} more")
                    : string.Empty);
        }
    }

    /// <summary>Whether the parent can be selected: the capture holds it.</summary>
    public bool CanSelectParent => ShowsLineage && ParentOfSelected is not null;

    /// <summary>Whether the children can be selected: the capture holds at least one.</summary>
    public bool CanSelectChildren => ShowsLineage && ChildrenOfSelected.Count > 0;

    /// <summary>What the children action does, by how many there are.</summary>
    public string SelectChildrenLabel => ChildrenOfSelected.Count == 1 ? "Go to its child" : "Select its children";

    /// <summary>Selects the selected process's parent, when the capture holds it.</summary>
    public bool SelectParent()
    {
        if (!CanSelectParent || ParentOfSelected is not { } parent) return false;
        SelectProcess(parent.Id);
        return true;
    }

    /// <summary>
    /// Selects the processes the selected one started: its one child as the selection, or several as a multi-selection,
    /// which Enter turns into their records (§6.7).
    /// </summary>
    public bool SelectChildren()
    {
        if (!CanSelectChildren) return false;
        ProcessInstanceId[] children = [.. ChildrenOfSelected.Select(child => child.Id)];
        if (children.Length == 1)
        {
            SelectProcess(children[0]);
            return true;
        }

        chosenProcesses.Clear();
        chosenProcesses.UnionWith(children);
        selectedClusterKey = null;
        selectedGroupKey = null;
        chosenChannelKey = null;
        graphSelection = null;
        selectedProcess = null;
        OnPropertyChanged(nameof(SelectedProcess));
        selection.SelectProcess(null);
        OnPropertyChanged(nameof(ChosenProcesses));
        OnPropertyChanged(nameof(HasMultiSelection));
        RaiseGraphSelectionChanged();
        return true;
    }

    private void RaiseLineageChanged()
    {
        OnPropertyChanged(nameof(ShowsLineage));
        OnPropertyChanged(nameof(ParentText));
        OnPropertyChanged(nameof(ChildrenText));
        OnPropertyChanged(nameof(CanSelectParent));
        OnPropertyChanged(nameof(CanSelectChildren));
        OnPropertyChanged(nameof(SelectChildrenLabel));
    }
}
