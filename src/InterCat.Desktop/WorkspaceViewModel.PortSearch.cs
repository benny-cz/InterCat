using System.Globalization;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// A bare number searched as a port (§6.7). "5353" finds the processes whose PID is the number or begins with it, and
/// ":5353" those whose connections ran to that port or accepted connections at it, with the channels at it, which a person
/// holding a port from a firewall's log or netstat has to know to type. Typed bare, a number whose port search finds
/// something offers that search among its hits, after a process whose PID the number is, else first; going to it searches
/// the port in the number's place.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    /// <summary>
    /// The rows of <paramref name="result"/>'s hits, with the search of the port its words name among them where that
    /// search finds something: after a process whose PID the number is, which is the likelier meaning, else first.
    /// </summary>
    private SearchRow[] WithPortRow(SearchResult result)
    {
        SearchRow[] rows = [.. result.Hits.Select(SearchRow.Of)];
        if (WorkspaceSearch.BarePort(result.Query) is not { } port)
        {
            return rows;
        }

        string query = ":" + port.ToString(CultureInfo.InvariantCulture);
        int matched = WorkspaceSearch.Find(wholeSnapshot, query).Matched;
        if (matched == 0)
        {
            return rows;
        }

        SearchRow offered = SearchRow.Of(new SearchHit(SearchHitKind.Port, "port:" + query, query,
            "Search the port instead: " + CountText.Of(matched, "match", "matches"), 0, []));
        int at = rows.Length > 0 && rows[0].Hit.Kind == SearchHitKind.Process
            && wholeSnapshot.Processes.FirstOrDefault(process => process.Id.ToString() == rows[0].Hit.Key)?.ProcessId == port
                ? 1
                : 0;
        return [.. rows[..at], offered, .. rows[at..]];
    }

    /// <summary>The port search the hits offer, ":5353", or null when they offer none.</summary>
    private string? OfferedPort => searchRows.FirstOrDefault(row => row.Hit.Kind == SearchHitKind.Port)?.Label;
}
