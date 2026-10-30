using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// Words a search finds nothing by, a letter or two from a program of the session, offer that program's name (R18), as a
/// bare number offers its port: "chrme" offers chrome.exe, and going to it searches the name in the words' place. Each
/// program as near as the nearest is offered, the busiest first.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    /// <summary>The rows offering the programs <paramref name="result"/>'s words were a letter or two from, where it found nothing.</summary>
    private SearchRow[] NearProgramRows(SearchResult result) =>
    [
        .. WorkspaceSearch.NearPrograms(wholeSnapshot, result).Select(program => SearchRow.Of(new SearchHit(
            SearchHitKind.NearProgram, "program:" + program, program,
            "Search it instead: " + CountText.Of(WorkspaceSearch.Find(wholeSnapshot, program).Matched, "match", "matches"), 0, []))),
    ];

    /// <summary>The program search the rows offer first, "chrome.exe", or null when they offer none.</summary>
    private string? OfferedProgram => searchRows.FirstOrDefault(row => row.Hit.Kind == SearchHitKind.NearProgram)?.Label;
}
