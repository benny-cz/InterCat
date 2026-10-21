using System.Globalization;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// A hop made from a chosen process's relationships (§6.3): the process left, the one chosen at the relationship's other
/// end, and the depth of the rung it was chosen at, which Esc comes back along.
/// </summary>
public sealed record PeerHop(ProcessInstanceId From, ProcessInstanceId To, int Depth);

/// <summary>
/// A hop made from a chosen program's pairs of programs (§6.3): the program left and the one chosen at the pair's other
/// end, by their keys, and the depth of the rung it was chosen at, which Esc comes back along.
/// </summary>
public sealed record ProgramHop(string From, string To, int Depth);

/// <summary>
/// What the inspector says made the selection's numbers (§6.8: every visual claim is one action from its explanation):
/// for a process, the rule that bound its records to it and what the evidence policy left out; for an executable group,
/// how its members were grouped and what its total left out; for a paired channel, the rule that paired its two ends,
/// whether the capture saw it open and close, and the key its records are listed by; for a relationship, its evidence, the
/// rule that derived it and the keys of what it rests on, as beneath the table (R4).
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
    /// The TCP connections the selected process, or the selected group's processes together, made and accepted over the
    /// whole session, as their card counts them and a ranking of the whole session reads (<see cref="DirectionText.Opened"/>):
    /// "3 made · 12 accepted, as the capture saw them open", none the capture saw open, or not recorded where the session
    /// records no connection's open (R21). Under a brush it says it is the whole session's. The inspector states it where a
    /// keyboard and a screen reader reach it, which a card is not; empty for any other selection.
    /// </summary>
    public string SelectionConnections
    {
        get
        {
            HashSet<ProcessInstanceId>? members = ShowsLineage && selectedProcess is { } selected ? [selected.Id]
                : GroupingExplanation.Length > 0 && SelectedGroup is { } group
                    ? [.. wholeSnapshot.Processes.Where(process => process.GroupKey == group.Key).Select(process => process.Id)]
                    : null;
            if (members is null)
            {
                return string.Empty;
            }

            if (!wholeSnapshot.ConnectionOpensRecorded)
            {
                return "Not recorded: this session records no connection's open, so how many were made or accepted is unknown";
            }

            (long made, long accepted) = DirectionText.Opened(wholeSnapshot, members);
            return (DirectionText.OpenedCounts(made, accepted) ?? "None the capture saw open")
                + (appliedInterval is null ? string.Empty : " · whole session");
        }
    }

    /// <summary>Whether the inspector states the selection's connections.</summary>
    public bool ShowsConnections => SelectionConnections.Length > 0;

    /// <summary>The most pairs of programs the inspector names for a chosen program, the busiest.</summary>
    internal const int ProgramPairsNamed = 5;

    /// <summary>Every pair of programs the session's relationships join, summed once when first asked for.</summary>
    private IReadOnlyList<ProgramPair>? programPairs;

    /// <summary>
    /// The selected program's relationships summed by the program at each end, as `icat overview` sums them
    /// (<see cref="ProgramPairs"/>), the busiest first: each named first by the program whose processes made its
    /// connections - "svchost.exe → lsass.exe", "12 relationships · 72 records" - its own processes connecting to each
    /// other among them, over the whole session. Which programs a busy one's processes talk to is otherwise read off the
    /// graph edge by edge. Empty for any other selection, a group of processes by their session, and a program none of
    /// whose processes has a relationship.
    /// </summary>
    public IReadOnlyList<ProgramPairRow> SelectionPrograms => [.. ProgramsOfSelection().Take(ProgramPairsNamed).Select(pair =>
    {
        string counts = RelationshipCount(pair.Relationships) + " · " + Spoken.Count(pair.Records, "record");
        string other = string.Equals(pair.First.Key, selectedGroupKey, StringComparison.Ordinal) ? pair.Second.Key : pair.First.Key;
        return new ProgramPairRow(pair.Joined, counts, pair.Spoken + ": " + counts.Replace(" · ", ", ", StringComparison.Ordinal))
        {
            OtherKey = other,
            Itself = string.Equals(other, selectedGroupKey, StringComparison.Ordinal),
        };
    })];

    /// <summary>How many more programs the selected program's processes talked with than the inspector names.</summary>
    public string ProgramsMore => ProgramsOfSelection().Count - ProgramPairsNamed is > 0 and var more
        ? "and " + Spoken.Count(more, "more program", "more programs")
        : string.Empty;

    /// <summary>Whether more programs than the inspector names are said to be left out.</summary>
    public bool ShowsProgramsMore => ProgramsMore.Length > 0;

    /// <summary>The heading over them, saying under a brush that they are the whole session's.</summary>
    public string ProgramsHeading => "Relationships by program" + (appliedInterval is null ? string.Empty : " · whole session");

    /// <summary>
    /// The list as a screen reader names it: what it holds and what Enter on a row does. Each row says itself whole as the
    /// keyboard reaches it, who connected said in words.
    /// </summary>
    public string ProgramsListName => ProgramsHeading.Replace(" · ", ", over the ", StringComparison.Ordinal)
        + "; Enter on one chooses the program at its other end";

    /// <summary>Whether the inspector names the programs the selected program's processes talked with.</summary>
    public bool ShowsPrograms => SelectionPrograms.Count > 0;

    /// <summary>
    /// Chooses the program at the other end of one of the selected program's pairs, as choosing its ranked row does, so
    /// which programs talked with which can be walked a pair at a time: its own pairs are then listed, and
    /// <see cref="ComeBackFromProgram"/> chooses again the one it was chosen from. False for the program's own processes
    /// connecting to each other, which goes nowhere.
    /// </summary>
    public bool ChooseProgram(ProgramPairRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (selectedGroupKey is not { } from || string.Equals(from, row.OtherKey, StringComparison.Ordinal))
        {
            return false;
        }

        SelectGroup(row.OtherKey, syncRow: true);
        programHops.Add(new ProgramHop(from, row.OtherKey, ladder.Depth));
        return true;
    }

    /// <summary>The hops made by <see cref="ChooseProgram"/>, the latest last, kept for a newer generation.</summary>
    private readonly List<ProgramHop> programHops = [];

    /// <summary>
    /// Comes back along the latest hop made from the inspector's pairs of programs, as Esc does before it climbs the
    /// ladder: where the program the hop chose is still the one chosen, on the rung it was chosen at, the program it was
    /// chosen from is chosen again, and the key of the one left is returned, whose row in its list the keyboard goes back
    /// to. A hop the selection or the ladder has since moved away from, or one from a program a newer generation no longer
    /// holds, is forgotten, every earlier one with it, and null returned.
    /// </summary>
    public string? ComeBackFromProgram()
    {
        if (programHops is not [.., var latest])
        {
            return null;
        }

        if (!string.Equals(selectedGroupKey, latest.To, StringComparison.Ordinal) || ladder.Depth != latest.Depth
            || wholeSnapshot.Groups.All(group => !string.Equals(group.Key, latest.From, StringComparison.Ordinal)))
        {
            programHops.Clear();
            return null;
        }

        programHops.RemoveAt(programHops.Count - 1);
        SelectGroup(latest.From, syncRow: true);
        return latest.To;
    }

    /// <summary>The most relationships the inspector names for a chosen process, the busiest.</summary>
    internal const int PeersNamed = 5;

    /// <summary>
    /// The selected process's relationships over the whole session, the busiest first, each by the process at its other end,
    /// marked from the selected process's side as its own count of connections made and accepted reads them - "→" where it
    /// made a relationship's connections, "←" where the other end did, "⇄" where each did, none where the capture saw none
    /// open - with what it made and accepted, its mechanism and records: "← node.exe · PID 3808", "1 accepted · TCP · 10
    /// records". Of RPC calls, the calls it made and those it served. Which processes a busy one talks to, and who connected,
    /// is otherwise read off the graph edge by edge or the relationship table row by row. Empty for any other selection.
    /// </summary>
    public IReadOnlyList<PeerRow> SelectionPeers => selectedProcess is { } selected
        ? [.. PeersOfSelection().Take(PeersNamed).Select(edge => PeerRowOf(edge, selected.Id))]
        : [];

    /// <summary>The process at a relationship's other end from the selected one, as a caption names it.</summary>
    private string PeerName(CommunicationEdge edge, ProcessInstanceId selected)
    {
        ProcessInstanceId other = edge.SourceId == selected ? edge.TargetId : edge.SourceId;
        return (wholeSnapshot.Processes.FirstOrDefault(process => process.Id == other)?.NameWithPid ?? "a process")
            + (other == selected ? " (itself)" : string.Empty);
    }

    /// <summary>How many more relationships the selected process has than the inspector names.</summary>
    public string PeersMore => PeersOfSelection().Count - PeersNamed is > 0 and var more
        ? "and " + Spoken.Count(more, "more relationship", "more relationships")
        : string.Empty;

    /// <summary>Whether more relationships than the inspector names are said to be left out.</summary>
    public bool ShowsPeersMore => PeersMore.Length > 0;

    /// <summary>The heading over them, saying under a brush that they are the whole session's.</summary>
    public string PeersHeading => "Its relationships, the busiest first" + (appliedInterval is null ? string.Empty : " · whole session");

    /// <summary>
    /// The list as a screen reader names it: what it holds and what Enter on a row does. Each row says itself whole as the
    /// keyboard reaches it, who connected said in words.
    /// </summary>
    public string PeersListName => PeersHeading.Replace(" · ", ", over the ", StringComparison.Ordinal)
        + "; Enter on one chooses the process at its other end";

    /// <summary>
    /// Chooses the process at the other end of one of the selected process's relationships, as a click on its node does,
    /// so who connected to whom can be walked a relationship at a time: its own relationships are then listed, and
    /// <see cref="ComeBackFromPeer"/> chooses again the one it was chosen from. False for one connected to itself, which
    /// goes nowhere.
    /// </summary>
    public bool ChoosePeer(PeerRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (selectedProcess is not { } from || from.Id == row.PeerId)
        {
            return false;
        }

        SelectProcess(row.PeerId);
        peerHops.Add(new PeerHop(from.Id, row.PeerId, ladder.Depth));
        return true;
    }

    /// <summary>The hops made by <see cref="ChoosePeer"/>, the latest last, kept for a newer generation.</summary>
    private readonly List<PeerHop> peerHops = [];

    /// <summary>
    /// Comes back along the latest hop made from the inspector's relationships, as Esc does before it climbs the ladder:
    /// where the process the hop chose is still the one chosen, on the rung it was chosen at, the process it was chosen
    /// from is chosen again, and the one left is returned, whose row in its list the keyboard goes back to. A hop the
    /// selection or the ladder has since moved away from, or one from a process a newer generation no longer holds, is
    /// forgotten, every earlier one with it, and null returned.
    /// </summary>
    public ProcessInstanceId? ComeBackFromPeer()
    {
        if (peerHops is not [.., var latest])
        {
            return null;
        }

        if (selectedProcess?.Id != latest.To || ladder.Depth != latest.Depth
            || Snapshot.Processes.All(process => process.Id != latest.From))
        {
            peerHops.Clear();
            return null;
        }

        peerHops.RemoveAt(peerHops.Count - 1);
        SelectProcess(latest.From);
        return latest.To;
    }

    /// <summary>Whether the inspector names the processes the selected process talked with.</summary>
    public bool ShowsPeers => SelectionPeers.Count > 0;

    /// <summary>Its relationships, the most records first, ties by the other end's name, then by key.</summary>
    private IReadOnlyList<CommunicationEdge> PeersOfSelection() => ShowsLineage && selectedProcess is { } selected
        ? [.. wholeSnapshot.Edges
            .Where(edge => edge.SourceId == selected.Id || edge.TargetId == selected.Id)
            .OrderByDescending(edge => edge.ObservationCount)
            .ThenBy(edge => PeerName(edge, selected.Id), StringComparer.OrdinalIgnoreCase)
            .ThenBy(edge => edge.Key, StringComparer.Ordinal)]
        : [];

    /// <summary>One of the selected process's relationships from its side: the other end, who made what, and what it carried.</summary>
    private PeerRow PeerRowOf(CommunicationEdge edge, ProcessInstanceId selected)
    {
        bool first = edge.SourceId == selected;
        string peer = PeerName(edge, selected);
        long made = first ? edge.SourceConnected : edge.TargetConnected;
        long accepted = first ? edge.TargetConnected : edge.SourceConnected;
        bool calls = edge.Mechanism == Mechanism.Rpc;
        string detail = string.Join(" · ", new[]
        {
            made == 0 ? null
                : calls ? Spoken.Count(made, "call") + " made"
                : string.Create(CultureInfo.CurrentCulture, $"{made:N0} made"),
            accepted == 0 ? null
                : calls ? Spoken.Count(accepted, "call") + " served"
                : string.Create(CultureInfo.CurrentCulture, $"{accepted:N0} accepted"),
            MechanismText.Name(edge.Mechanism),
            Spoken.Count(edge.ObservationCount, "record"),
        }.OfType<string>());
        string verb = calls ? "called" : "connected to";
        string said = (made > 0, accepted > 0) switch
        {
            (true, false) => $"it {verb} {peer}",
            (false, true) => $"{peer} {verb} it",
            (true, true) => $"it and {peer} each {verb} the other",
            _ => peer,
        };
        string glyph = DirectionText.Glyph((int)Math.Min(made, int.MaxValue), (int)Math.Min(accepted, int.MaxValue));
        ProcessInstanceId other = first ? edge.TargetId : edge.SourceId;
        return new PeerRow((glyph.Length > 0 ? glyph + " " : string.Empty) + peer, detail,
            said + ": " + detail.Replace(" · ", ", ", StringComparison.Ordinal))
        {
            PeerId = other,
            Itself = other == selected,
        };
    }

    private IReadOnlyList<ProgramPair> ProgramsOfSelection()
    {
        if (GroupingExplanation.Length == 0 || SelectedGroup is not { Kind: LaneGrouping.Executable } program)
        {
            return [];
        }

        programPairs ??= ProgramPairs.Of(wholeSnapshot.Edges, wholeSnapshot.Processes, wholeSnapshot.Groups);
        return [.. programPairs.Where(pair => string.Equals(pair.First.Key, program.Key, StringComparison.Ordinal)
            || string.Equals(pair.Second.Key, program.Key, StringComparison.Ordinal))];
    }

    /// <summary>
    /// The selection's explanation, as the inspector states it: a relationship's derivation, a process's binding, a group's
    /// grouping or a channel's pairing, of which at most one applies to any selection.
    /// </summary>
    public string Explanation => selectedRelationship is { } relationship ? relationship.Explanation
        : BindingExplanation is { Length: > 0 } binding ? binding
        : GroupingExplanation is { Length: > 0 } grouping ? grouping
        : PairingExplanation;

    /// <summary>What the explanation answers, above it.</summary>
    public string ExplanationHeading => selectedRelationship is not null ? "How it was derived"
        : BindingExplanation.Length > 0 ? "How its records are counted"
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
        OnPropertyChanged(nameof(SelectionConnections));
        OnPropertyChanged(nameof(ShowsConnections));
        OnPropertyChanged(nameof(SelectionPrograms));
        OnPropertyChanged(nameof(ProgramsMore));
        OnPropertyChanged(nameof(ShowsProgramsMore));
        OnPropertyChanged(nameof(ShowsPrograms));
        OnPropertyChanged(nameof(SelectionPeers));
        OnPropertyChanged(nameof(PeersMore));
        OnPropertyChanged(nameof(ShowsPeersMore));
        OnPropertyChanged(nameof(ShowsPeers));
    }
}
