using System.Globalization;
using System.Text;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>
/// The records a content search listed, as a table (`contracts/content-v1.md` §4): one line per record in the order
/// listed, each with the locator that shows its bytes, when it was raised, its process, mechanism and event, how often its
/// kept bytes hold the text and where first, and the text searched for and how. Only where previews were asked for does it
/// add the bytes around each first match, as the inert text the search shows them in, which is content. Its numbers are
/// invariant, and a text cell is quoted where it holds a comma, a quote or a line end and neutralized where a spreadsheet
/// would evaluate it, since a text searched for and a record's bytes come from people and the observed machine.
/// </summary>
public static class ContentSearchTable
{
    /// <summary>The columns every table has, in order.</summary>
    public static IReadOnlyList<string> Columns { get; } =
    [
        "session_id", "generation", "segment", "row", "seconds", "pid", "mechanism", "event", "matches", "first_at", "text",
        "ignore_case",
    ];

    /// <summary>The columns a table of previews adds after <see cref="Columns"/>, in order.</summary>
    public static IReadOnlyList<string> PreviewColumns { get; } = ["preview_before", "preview_match", "preview_after"];

    /// <summary>
    /// <paramref name="result"/>'s records as CSV, each time as seconds after the capture epoch on the clock of the
    /// generation searched (empty where none describes it), with <see cref="PreviewColumns"/> only where
    /// <paramref name="request"/> asked for previews.
    /// </summary>
    public static string Csv(ContentSearchResult result, ContentSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(request);
        var csv = new StringBuilder();
        WorkspaceExport.Line(csv, request.Previews ? [.. Columns, .. PreviewColumns] : Columns);
        foreach (ContentSearchHit hit in result.Hits)
        {
            ObservationRowV1 record = hit.Observation;
            List<string> cells =
            [
                result.SessionId.ToString("N"),
                Number(result.Generation),
                WorkspaceExport.Text(hit.Segment),
                Number(hit.Row),
                result.Clock is { } clock ? SessionTimeText.EpochSeconds(clock, record.NativeTicks) : string.Empty,
                Number(record.OwnerProcessId ?? record.HeaderProcessId),
                MechanismText.Name(record.Mechanism),
                Number(record.EventId),
                Number(hit.Matches),
                Number(hit.FirstAt),
                WorkspaceExport.Text(request.Text),
                request.IgnoreCase ? "true" : "false",
            ];
            if (request.Previews)
            {
                cells.AddRange(hit.Preview is { } preview
                    ? [WorkspaceExport.Text(preview.Before), WorkspaceExport.Text(preview.Match), WorkspaceExport.Text(preview.After)]
                    : [string.Empty, string.Empty, string.Empty]);
            }

            WorkspaceExport.Line(csv, cells);
        }

        return csv.ToString();
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
