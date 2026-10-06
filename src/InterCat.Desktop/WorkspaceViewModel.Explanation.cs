using InterCat.Application;
using InterCat.Desktop.Presentation;

namespace InterCat.Desktop;

/// <summary>
/// What the inspector says made the selection's numbers (§6.8: every visual claim is one action from its explanation):
/// for a process, the rule that bound its records to it and what the evidence policy left out; for an executable group,
/// how its members were grouped and what its total left out; for a paired channel, the rule that paired its two ends,
/// whether the capture saw it open and close, and the key its records are listed by. A relationship explains itself
/// beneath the table and in the inspector's subtitle (R4).
/// </summary>
public sealed partial class WorkspaceViewModel
{
    /// <summary>
    /// How the selected process's records were bound to it over the session, stated beside its lineage: the rule and how
    /// strongly, what the evidence policy left out of its total, and the capture's coverage. A reused PID's later holder
    /// counts only its lifecycle records, and without this its row would read as a quiet process.
    /// </summary>
    public string BindingExplanation => ShowsLineage && selectedProcess is { } selected
        && wholeSnapshot.Processes.FirstOrDefault(process => process.Id == selected.Id) is { } whole
        ? WorkspaceRowBuilder.ExplainBinding(whole)
        : string.Empty;

    /// <summary>
    /// How the selected executable group was formed and counted: by which executable, the rule that bound its members'
    /// records, and what the evidence policy left out of its total. Empty unless one group of a real session is the
    /// selection; the tour's groups illustrate, and no rule formed them.
    /// </summary>
    public string GroupingExplanation => realOverview && SelectedGroup is { } group && !HasMultiSelection
        && SelectedCluster is null && DescribedRow is null && selectedRelationship is null
        ? WorkspaceRowBuilder.ExplainGrouping(group, [.. wholeSnapshot.Processes.Where(process => process.GroupKey == group.Key)])
        : string.Empty;

    /// <summary>
    /// How the paired channel the inspector describes was paired: one chosen among a process's rows. Empty for any other
    /// row and for a channel no rule paired, as the tour's. The explanation follows the inspector's selection, so on a
    /// channel's own rung, where the inspector still describes the process it was opened from, the process's is stated.
    /// </summary>
    public string PairingExplanation => DescribedRow is { } row
        && wholeSnapshot.Channels.FirstOrDefault(channel => string.Equals(channel.Key, row.Key, StringComparison.Ordinal))
            is { Rule: not null } paired
        ? WorkspaceRowBuilder.ExplainPairing(paired)
        : string.Empty;

    /// <summary>
    /// The selection's explanation, as the inspector states it: a process's binding, a group's grouping or a channel's
    /// pairing, of which at most one applies to any selection.
    /// </summary>
    public string Explanation => BindingExplanation is { Length: > 0 } binding ? binding
        : GroupingExplanation is { Length: > 0 } grouping ? grouping
        : PairingExplanation;

    /// <summary>What the explanation answers, above it.</summary>
    public string ExplanationHeading => BindingExplanation.Length > 0 ? "How its records are counted"
        : GroupingExplanation.Length > 0 ? "How its processes are grouped"
        : "How it was paired";

    public bool HasExplanation => Explanation.Length > 0;

    private void RaiseExplanationChanged()
    {
        OnPropertyChanged(nameof(BindingExplanation));
        OnPropertyChanged(nameof(GroupingExplanation));
        OnPropertyChanged(nameof(PairingExplanation));
        OnPropertyChanged(nameof(Explanation));
        OnPropertyChanged(nameof(ExplanationHeading));
        OnPropertyChanged(nameof(HasExplanation));
    }
}
