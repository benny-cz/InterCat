using System.Globalization;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// Where a way down from an aggregate's list began (§3.2): the rung - its depth, level and focus - the aggregate, by its
/// stable key, and the process chosen in its list.
/// </summary>
public sealed record MemberListOrigin(int Depth, DetailLevel Level, string? Focus, string Aggregate, ProcessInstanceId Member);

/// <summary>
/// The processes an aggregate counts (§6.3): No relationships, Other hosts only, Other processes and an opened group's
/// Other members fold processes the graph does not draw on their own. Chosen, one lists them in the inspector beneath its
/// description, the busiest first, each with what it did, so every process it counts is read where it is counted. One
/// chosen there is its own records, as a process drawn on its own and chosen is: the timeline highlights them, the card
/// counts them and E lists them (§6.4). Enter or a double click on one opens its rung, as Enter on a process drawn on its
/// own does.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    /// <summary>The most processes the inspector lists for an aggregate, the busiest; the ranked table lists every one.</summary>
    internal const int MembersListed = 100;

    // The rows of the chosen aggregate's processes, worked out once for each drawing and scope (R11).
    private (GraphDisplayNode Node, WorkspaceSnapshot Scope, int Count, IReadOnlyList<MemberRow> Rows)? listedMembers;

    // The process chosen in an aggregate's list, while the aggregate stays chosen; a node chosen in the graph, the same
    // aggregate again included, lets it go.
    private ProcessInstanceId? chosenListedMember;

    // While an aggregate's list restates its rows it lets its choice go for a moment, which is not the person's.
    private bool restatingMembers;

    /// <summary>
    /// The process chosen in the list an aggregate gives the inspector: what the card counts, the timeline highlights and E
    /// lists, as for a process drawn on its own and chosen. Null while none is, no aggregate is chosen, or the list does not
    /// hold the process. When the list restates its rows, the process chosen is chosen again among them.
    /// </summary>
    public MemberRow? ChosenMemberRow
    {
        // While the list restates its rows a binding that reads the choice back finds none, as the list then holds none;
        // said changed once the rows are in place, the choice is then a change it applies.
        get => !restatingMembers && chosenListedMember is { } chosen ? MemberRows.FirstOrDefault(row => row.Process == chosen) : null;
        set
        {
            if (restatingMembers)
            {
                return;
            }

            ProcessInstanceId? process = value?.Process;
            if (process == chosenListedMember)
            {
                return;
            }

            chosenListedMember = process;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EvidenceHeading));
            OnPropertyChanged(nameof(EvidenceSummary));
            UpdateHighlight();
            FollowDescribedBytes();
        }
    }

    /// <summary>The process chosen in an aggregate's list while the list holds it; null for any other selection.</summary>
    private ProcessNode? ChosenMember => ChosenMemberRow is { } row
        ? Snapshot.Processes.FirstOrDefault(process => process.Id == row.Process)
        : null;

    /// <summary>The chosen aggregate whose processes the inspector lists; null for any other selection.</summary>
    private GraphDisplayNode? ListingAggregate => SelectedCluster is { } aggregate && ListsProcesses(aggregate) ? aggregate : null;

    /// <summary>Whether a drawn node is an aggregate whose processes the inspector lists once it is chosen.</summary>
    private static bool ListsProcesses(GraphDisplayNode node) =>
        node.Kind is GraphNodeKind.Quiet or GraphNodeKind.OtherHostsOnly or GraphNodeKind.Remainder or GraphNodeKind.OtherMembers;

    /// <summary>
    /// How many times Enter or a double click on such an aggregate in the graph has asked for the keyboard to go to its
    /// processes' list (§6.3, R15); the window gives it there each time this changes.
    /// </summary>
    public int MembersAsked { get; private set; }

    /// <summary>
    /// An aggregate of processes opened, as Enter or a double click opens a node: it opens nothing of its own, so it is
    /// chosen, if it is not, and the keyboard is asked to its processes' list, where Enter opens one. False for any other
    /// node.
    /// </summary>
    private bool OpenAggregate(string key)
    {
        if (graphDisplay.Node(key) is not { } node || !ListsProcesses(node))
        {
            return false;
        }

        if (!string.Equals(SelectedCluster?.Key, key, StringComparison.Ordinal))
        {
            SelectGraphNode(key);
        }

        MembersAsked++;
        OnPropertyChanged(nameof(MembersAsked));
        return true;
    }

    /// <summary>
    /// The processes the inspector lists while an aggregate is chosen (§6.3), the busiest over the scope shown first and at
    /// most <see cref="MembersListed"/>, each with its records, its relationships and its connections to other hosts - those
    /// it accepted said apart, at its own ports - the whole session's, which a brush does not re-count.
    /// </summary>
    public IReadOnlyList<MemberRow> MemberRows => ListedMembers().Rows;

    /// <summary>Whether the inspector lists an aggregate's processes: one is chosen.</summary>
    public bool ListsMembers => ListedMembers().Count > 0;

    /// <summary>The process list's heading: "Its 4 processes, the busiest first", "Its busiest 100 of 1,234 processes".</summary>
    public string MembersHeading => ListedMembers().Count switch
    {
        0 => string.Empty,
        <= MembersListed and var count =>
            string.Create(CultureInfo.CurrentCulture, $"Its {count:N0} processes, the busiest first"),
        var count => string.Create(CultureInfo.CurrentCulture, $"Its busiest {MembersListed:N0} of {count:N0} processes"),
    };

    /// <summary>What the process list leaves out, and where to read it; empty when it lists every one.</summary>
    public string MembersNote => ListedMembers().Count is > MembersListed and var count
        ? string.Create(CultureInfo.CurrentCulture, $"{count - MembersListed:N0} more, quieter: the ranked table lists ")
            + "every one, and a search (Ctrl+F) finds one by its name or PID."
        : string.Empty;

    /// <summary>Whether the process list says what it leaves out.</summary>
    public bool HasMembersNote => MembersNote.Length > 0;

    // Where the latest way down from an aggregate's list began, Enter or E on a process chosen there, so each way back to
    // that rung - forward and back again included - chooses them there again (§3.2). A way down that begins at that rung
    // or above it is a way of its own, and replaces it. A newer generation keeps it.
    private MemberListOrigin? memberOrigin;

    /// <summary>A way down beginning here from the chosen aggregate's list, <paramref name="member"/> chosen in it.</summary>
    private MemberListOrigin? MemberOriginHere(ProcessInstanceId member) => ListingAggregate is { } aggregate
        ? new(ladder.Depth, ladder.Current.Level, ladder.Current.Focus?.Key, aggregate.Key, member)
        : null;

    /// <summary>
    /// A newer generation's restoring: the process chosen in <paramref name="aggregate"/>'s list, or the host chosen in Other
    /// hosts', is chosen there again while the list holds it; one it no longer holds is said once, the aggregate chosen
    /// alone (R7).
    /// </summary>
    private void RestoreListChoice(WorkspaceNavigationMemento saved, GraphDisplayNode aggregate, List<string> notices)
    {
        if (saved.ChosenListedMember is { } member)
        {
            ChosenMemberRow = MemberRows.FirstOrDefault(row => row.Process == member);
            if (ChosenMemberRow is null)
            {
                string named = Snapshot.Processes.FirstOrDefault(process => process.Id == member)?.NameWithPid
                    ?? "The process chosen there";
                notices.Add($"{named} is no longer among the processes {aggregate.Label} lists, so {aggregate.Label} "
                    + "alone is chosen.");
            }
        }

        if (saved.ChosenListedHost is { } host)
        {
            ChosenHostRow = HostRows.FirstOrDefault(row => string.Equals(row.Address, host, StringComparison.Ordinal));
            if (ChosenHostRow is null)
            {
                notices.Add($"{host} is no longer among the hosts {aggregate.Label} lists, so {aggregate.Label} alone is chosen.");
            }
        }
    }

    /// <summary>
    /// Opens the rung of a process the chosen aggregate lists, as Enter on a process drawn on its own does: through its
    /// group, so the breadcrumb states every rung and Esc climbs each, back to the aggregate and the process chosen in its
    /// list, and to the aggregate's edge to other hosts where the table had it chosen, as E's way back does. False for a
    /// process the list does not hold.
    /// </summary>
    public bool OpenMember(MemberRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!MemberRows.Any(listed => listed.Process == row.Process)
            || Snapshot.Processes.FirstOrDefault(process => process.Id == row.Process) is not { } process)
        {
            return false;
        }

        MemberListOrigin? origin = MemberOriginHere(process.Id);
        RelationshipOrigin? related = SelectedTableRow is { } chosen ? OriginHere(chosen.Key) : null;
        if (!DescendAlong([process.GroupKey, process.Id.ToString()], selectedInterval))
        {
            return false;
        }

        memberOrigin = origin;
        if (related is not null)
        {
            KeepWayBack(related);
        }

        return true;
    }

    /// <summary>
    /// Back at the rung where a way down from an aggregate's list began, chooses the aggregate and the process in its list
    /// again and asks the keyboard to its row (§3.2), each time a way back arrives there; a climb past it keeps it for the
    /// way forward. That rung no longer drawing the aggregate, or the aggregate the process, lets it go.
    /// </summary>
    private void ResumeMemberOrigin()
    {
        if (memberOrigin is not { } origin || ladder.Depth != origin.Depth)
        {
            return;
        }

        // Only a way down that replaced it could stand another rung at its depth; the rung is checked all the same.
        if (ladder.Current.Level != origin.Level
            || !string.Equals(ladder.Current.Focus?.Key, origin.Focus, StringComparison.Ordinal)
            || graphDisplay.Node(origin.Aggregate) is not { } aggregate || !aggregate.Members.Contains(origin.Member))
        {
            memberOrigin = null;
            return;
        }

        SelectGraphNode(origin.Aggregate);
        ChosenMemberRow = MemberRows.FirstOrDefault(listed => listed.Process == origin.Member);
        MembersAsked++;
        OnPropertyChanged(nameof(MembersAsked));
    }

    /// <summary>
    /// The chosen aggregate's process rows and how many processes it counts, ranked once for each drawing and scope: the
    /// busiest first, then by name as a person reads it, whatever its case, then by PID.
    /// </summary>
    private (int Count, IReadOnlyList<MemberRow> Rows) ListedMembers()
    {
        if (ListingAggregate is not { } aggregate)
        {
            return (0, []);
        }

        if (listedMembers is { } known && ReferenceEquals(known.Node, aggregate) && ReferenceEquals(known.Scope, Snapshot))
        {
            return (known.Count, known.Rows);
        }

        HashSet<ProcessInstanceId> members = [.. aggregate.Members];
        ProcessNode[] ranked = [.. Snapshot.Processes
            .Where(process => members.Contains(process.Id))
            .OrderByDescending(process => process.Records)
            .ThenBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(process => process.ProcessId)
            .ThenBy(process => process.PidHolder)];
        HashSet<ProcessInstanceId> listed = [.. ranked.Take(MembersListed).Select(process => process.Id)];
        Dictionary<ProcessInstanceId, int> relationships = [];
        foreach (CommunicationEdge edge in Snapshot.Edges)
        {
            if (listed.Contains(edge.SourceId))
            {
                relationships[edge.SourceId] = relationships.GetValueOrDefault(edge.SourceId) + 1;
            }

            if (edge.TargetId != edge.SourceId && listed.Contains(edge.TargetId))
            {
                relationships[edge.TargetId] = relationships.GetValueOrDefault(edge.TargetId) + 1;
            }
        }

        ILookup<ProcessInstanceId, HostLink> links = wholeSnapshot.HostLinks
            .Where(link => listed.Contains(link.Process))
            .ToLookup(link => link.Process);
        string wholeOnly = appliedInterval is null ? string.Empty : " · whole session";
        MemberRow[] rows = [.. ranked.Take(MembersListed).Select(process => new MemberRow(process.Id, process.NameWithPid,
            string.Join(" · ", new[]
            {
                CountText.Of(process.Records, "record"),
                relationships.GetValueOrDefault(process.Id) is > 0 and var related
                    ? CountText.Of(related, "relationship")
                    : string.Empty,
                links[process.Id].Any() ? HostReach([.. links[process.Id]]) + wholeOnly : string.Empty,
            }.Where(part => part.Length > 0))))];
        listedMembers = (aggregate, Snapshot, ranked.Length, rows);
        return (ranked.Length, rows);
    }

    /// <summary>A process's connections to other hosts, transport by transport, and those it accepted at its own ports.</summary>
    private static string HostReach(IReadOnlyCollection<HostLink> links) =>
        HostLinkText.Accepted(links) is { Length: > 0 } accepted
            ? HostLinkText.Line(links) + " · " + accepted
            : HostLinkText.Line(links);

    /// <summary>
    /// Says the process list changed, after the choice, the drawing or the scope did. A list given new rows lets its choice
    /// go and writes that back as none chosen; the process chosen is kept and chosen again among them.
    /// </summary>
    private void RaiseMemberListChanged()
    {
        restatingMembers = true;
        try
        {
            OnPropertyChanged(nameof(MemberRows));
        }
        finally
        {
            restatingMembers = false;
        }

        OnPropertyChanged(nameof(ChosenMemberRow));
        OnPropertyChanged(nameof(ListsMembers));
        OnPropertyChanged(nameof(MembersHeading));
        OnPropertyChanged(nameof(MembersNote));
        OnPropertyChanged(nameof(HasMembersNote));
    }
}
