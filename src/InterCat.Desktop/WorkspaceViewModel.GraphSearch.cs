using System.Globalization;
using InterCat.Application;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// What a search finds in the graph (§6.7). While a search is typed, the graph marks each node holding a process it finds -
/// by the rule its hits and the timeline's lanes are found by: a name, a PID or an address the process's connections to
/// other hosts reached - filled where every process the node holds is found and open where only some are, and marks the
/// Other hosts node when an address the drawn processes' connections ran to is found. Its summary counts them and a
/// found node's card says so, so the graph shows where a search's processes are without selecting any.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    private (GraphDisplay Display, WorkspaceSnapshot Snapshot, string Text, IReadOnlySet<string> Whole, IReadOnlySet<string> Part,
        int Found)? graphSearch;

    /// <summary>The drawn nodes every process of which the search typed finds, and the Other hosts node it finds; empty while none is typed.</summary>
    public IReadOnlySet<string> SearchedGraphNodeKeys => GraphSearch().Whole;

    /// <summary>The drawn aggregates some but not all of whose processes the search typed finds.</summary>
    public IReadOnlySet<string> PartlySearchedGraphNodeKeys => GraphSearch().Part;

    /// <summary>
    /// The graph summary's note of what the search typed finds: " · 3 found by the search", or that it finds none. Empty
    /// while no search is typed.
    /// </summary>
    private string SearchedGraphNote => !IsSearching ? string.Empty
        : GraphSearch().Found is var found and > 0
            ? string.Create(CultureInfo.CurrentCulture, $" · {found:N0} found by the search")
            : " · none found by the search";

    /// <summary>
    /// A drawn node's card line for the search typed: "Found by the search" for a process it finds, how many of an
    /// aggregate's it finds, and for the Other hosts node that an address its connections reached is found. Null for a
    /// node the search does not mark.
    /// </summary>
    private string? SearchedLine(GraphDisplayNode node)
    {
        (IReadOnlySet<string> whole, IReadOnlySet<string> part, _) = GraphSearch();
        if (!whole.Contains(node.Key) && !part.Contains(node.Key))
        {
            return null;
        }

        if (node.Kind == GraphNodeKind.OtherHosts)
        {
            return "Found by the search: an address these connections reached";
        }

        if (node.Kind == GraphNodeKind.Process)
        {
            return "Found by the search";
        }

        HashSet<ProcessInstanceId> found = FoundBySearch();
        int count = node.Members.Count(found.Contains);
        return string.Create(CultureInfo.CurrentCulture, $"Found by the search: {count:N0} of its {node.Members.Count:N0} processes");
    }

    /// <summary>
    /// The drawn nodes the search finds wholly and in part, and how many processes it finds. The graph reads them on every
    /// repaint, so they are worked out once for each drawing, snapshot and search (R11).
    /// </summary>
    private (IReadOnlySet<string> Whole, IReadOnlySet<string> Part, int Found) GraphSearch()
    {
        if (!IsSearching)
        {
            return (NoKeys, NoKeys, 0);
        }

        if (graphSearch is { } known && ReferenceEquals(known.Display, graphDisplay) && ReferenceEquals(known.Snapshot, wholeSnapshot)
            && string.Equals(known.Text, searchText, StringComparison.Ordinal))
        {
            return (known.Whole, known.Part, known.Found);
        }

        HashSet<ProcessInstanceId> found = FoundBySearch();
        var whole = new HashSet<string>(StringComparer.Ordinal);
        var part = new HashSet<string>(StringComparer.Ordinal);
        foreach (GraphDisplayNode node in graphDisplay.Nodes)
        {
            int count = node.Members.Count(found.Contains);
            if (count > 0)
            {
                (count == node.Members.Count ? whole : part).Add(node.Key);
            }
        }

        // The Other hosts node holds no process: it is found when an address its drawn connections ran to is.
        if (graphDisplay.OtherHosts is { } hub && WorkspaceSearch.FindsAddress(DrawnHostLinks(graphDisplay), searchText))
        {
            whole.Add(hub.Key);
        }

        graphSearch = (graphDisplay, wholeSnapshot, searchText, whole, part, found.Count);
        return (whole, part, found.Count);
    }

    /// <summary>Every process the search typed finds, by the rule its hits are ranked by.</summary>
    private HashSet<ProcessInstanceId> FoundBySearch()
    {
        ILookup<ProcessInstanceId, HostLink> reached = wholeSnapshot.HostLinks.ToLookup(link => link.Process);
        return [.. wholeSnapshot.Processes
            .Where(process => WorkspaceSearch.Finds(process, reached[process.Id], searchText))
            .Select(process => process.Id)];
    }
}
