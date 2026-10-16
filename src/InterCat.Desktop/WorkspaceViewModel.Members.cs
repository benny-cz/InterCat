using System.Globalization;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// The processes an aggregate counts (§6.3): No relationships, Other hosts only, Other processes and an opened group's
/// Other members fold processes the graph does not draw on their own. Chosen, one lists them in the inspector beneath its
/// description, the busiest first, each with what it did, so every process it counts is read where it is counted; Enter
/// or a double click on one opens its rung, as Enter on a process drawn on its own does.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    /// <summary>The most processes the inspector lists for an aggregate, the busiest; the ranked table lists every one.</summary>
    internal const int MembersListed = 100;

    // The rows of the chosen aggregate's processes, worked out once for each drawing and scope (R11).
    private (GraphDisplayNode Node, WorkspaceSnapshot Scope, int Count, IReadOnlyList<MemberRow> Rows)? listedMembers;

    /// <summary>The chosen aggregate whose processes the inspector lists; null for any other selection.</summary>
    private GraphDisplayNode? ListingAggregate => SelectedCluster is
    {
        Kind: GraphNodeKind.Quiet or GraphNodeKind.OtherHostsOnly or GraphNodeKind.Remainder or GraphNodeKind.OtherMembers,
    } aggregate ? aggregate : null;

    /// <summary>
    /// The processes the inspector lists while an aggregate is chosen (§6.3), the busiest over the scope shown first and at
    /// most <see cref="MembersListed"/>, each with its records, its relationships and its connections to other hosts, the
    /// whole session's, which a brush does not re-count.
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

    /// <summary>
    /// Opens the rung of a process the chosen aggregate lists, as Enter on a process drawn on its own does: through its
    /// group, so the breadcrumb states every rung and Esc climbs each. False for a process the list does not hold.
    /// </summary>
    public bool OpenMember(MemberRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return MemberRows.Any(listed => listed.Process == row.Process)
            && Snapshot.Processes.FirstOrDefault(process => process.Id == row.Process) is { } process
            && DescendAlong([process.GroupKey, process.Id.ToString()], selectedInterval);
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
                links[process.Id].Any() ? HostLinkText.Line(links[process.Id]) + wholeOnly : string.Empty,
            }.Where(part => part.Length > 0))))];
        listedMembers = (aggregate, Snapshot, ranked.Length, rows);
        return (ranked.Length, rows);
    }

    /// <summary>Says the process list changed, after the choice, the drawing or the scope did.</summary>
    private void RaiseMemberListChanged()
    {
        OnPropertyChanged(nameof(MemberRows));
        OnPropertyChanged(nameof(ListsMembers));
        OnPropertyChanged(nameof(MembersHeading));
        OnPropertyChanged(nameof(MembersNote));
        OnPropertyChanged(nameof(HasMembersNote));
    }
}
