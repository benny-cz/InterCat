using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Domain;

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
        ? WorkspaceRowBuilder.ExplainBinding(whole, wholeSnapshot.Collectors.Unfound)
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
    /// How the paired channel the inspector describes was paired: one chosen among a process's rows, or the one its own
    /// rung opened, which the inspector describes there as the card counts it. Empty for any other row and for a channel
    /// no rule paired, as the tour's.
    /// </summary>
    public string PairingExplanation => (DescribedRow?.Key ?? (DescribesOpened ? FocusedRealChannel?.Key : null)) is { } key
        && wholeSnapshot.Channels.FirstOrDefault(channel => string.Equals(channel.Key, key, StringComparison.Ordinal))
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

    /// <summary>
    /// How strongly a record must bind to a process for this workspace to count it as that process's: the policy its
    /// overview was projected under and every read of it binds by, correlated evidence unless a person chose otherwise.
    /// </summary>
    public EvidencePolicy EvidencePolicy => evidenceSource?.Policy ?? EvidencePolicy.IncludeCorrelated;

    /// <summary>Whether a reused PID's later holders' records count as theirs, as candidates.</summary>
    public bool CountsCandidates => EvidencePolicy >= EvidencePolicy.IncludeCandidates;

    /// <summary>
    /// Whether the inspector offers to count candidates: the explanation it states says records were left out of the
    /// selection's total, which counting candidates would put back (§6.8, one action from the explanation).
    /// </summary>
    public bool OffersCandidates => !CountsCandidates && (WithheldOfSelection() > 0);

    /// <summary>
    /// Whether the empty rung offers to count candidates beside its reason, which says its process's records were left out
    /// as candidates: a reused PID's later holder's rung lists no row while they are not counted (§6.8).
    /// </summary>
    public bool OffersRungCandidates => IsEmptyRung && WithheldRungProcess is not null;

    /// <summary>
    /// The process whose rung is shown, when the evidence policy left records bound to it out as candidates; null at any
    /// other rung, in the tour, and while candidates count, since then none is left out.
    /// </summary>
    private ProcessNode? WithheldRungProcess => realOverview
        && ladder.Current.Level == DetailLevel.ProcessInstance && ladder.Current.Focus is { } focus
        && wholeSnapshot.Processes.FirstOrDefault(process => process.Id.ToString() == focus.Key) is { WithheldRecords: > 0 } process
            ? process
            : null;

    /// <summary>
    /// What the rail says while candidates count, so the unusual setting is never invisible (§6.8): every count, ranking,
    /// relationship and record E lists binds a reused PID's later holder's records to it, as candidates.
    /// </summary>
    public string EvidencePolicyNote => CountsCandidates
        ? "Counting candidates: a reused PID's later holder also counts records naming its PID that may be an earlier "
            + "holder's"
        : string.Empty;

    /// <summary>What an export says of the evidence policy its rows or records were counted under, when not the default.</summary>
    private string? EvidencePolicyCaveat => CountsCandidates
        ? "Counted with candidates: a reused PID's later holder's records count as its own, as candidates (evidence "
            + "policy include-candidates); counted with correlated evidence only, they would be left out."
        : null;

    /// <summary>The records the explained process or group left out of its total, under the policy counted.</summary>
    private long WithheldOfSelection()
    {
        if (BindingExplanation.Length > 0 && selectedProcess is { } selected)
        {
            return wholeSnapshot.Processes.FirstOrDefault(process => process.Id == selected.Id)?.WithheldRecords ?? 0;
        }

        return GroupingExplanation.Length > 0 && SelectedGroup is { } group
            ? wholeSnapshot.Processes.Where(process => process.GroupKey == group.Key).Sum(process => process.WithheldRecords)
            : 0;
    }

    private void RaiseExplanationChanged()
    {
        OnPropertyChanged(nameof(BindingExplanation));
        OnPropertyChanged(nameof(GroupingExplanation));
        OnPropertyChanged(nameof(PairingExplanation));
        OnPropertyChanged(nameof(Explanation));
        OnPropertyChanged(nameof(ExplanationHeading));
        OnPropertyChanged(nameof(HasExplanation));
        OnPropertyChanged(nameof(OffersCandidates));
    }
}
