using System.Globalization;
using InterCat.Application;
using InterCat.Desktop.Presentation;
using InterCat.Desktop.Theme;
using InterCat.Domain;

namespace InterCat.Desktop;

/// <summary>
/// The relationship table as the graph's whole table equivalent (R15). Since revision 542 the graph draws each process's
/// connections to other hosts as edges to Other hosts, and the table listed none of them - on a busy rung, most of what
/// the graph draws. Each such edge is a row after the relationships: its source as the graph names it; the host it
/// reached where it reached one, by its address and the port it ran to, else Other hosts and how many; who opened the
/// connections, as its arrowheads say; its transport, records and evidence. Chosen, a row chooses what a click on its edge
/// does - its source - and stays chosen while that is; Enter or a double click opens what a double click on it opens.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    // The table's rows, worked out once for each relationship list and drawing; the rows last restated in the table.
    private (IReadOnlyList<RelationshipRow> Relationships, GraphDisplay Display, IReadOnlyList<RelationshipRow> Rows)? tableRows;
    private IReadOnlyList<RelationshipRow>? restatedTableRows;

    // The edge to other hosts whose row was chosen, while its source stays the selection.
    private string? chosenHostEdge;

    /// <summary>The relationship table's rows: the relationships the graph draws at this rung, then its edges to other hosts.</summary>
    public IReadOnlyList<RelationshipRow> TableRows
    {
        get
        {
            if (tableRows is { } known && ReferenceEquals(known.Relationships, relationships)
                && ReferenceEquals(known.Display, graphDisplay))
            {
                return known.Rows;
            }

            IReadOnlyList<RelationshipRow> rows = [.. relationships, .. HostEdgeRows()];
            tableRows = (relationships, graphDisplay, rows);
            return rows;
        }
    }

    /// <summary>
    /// The row chosen in the table: a relationship chosen, in the graph or the table, or an edge to other hosts chosen in
    /// the table while its source stays the selection. Choosing a relationship chooses it; choosing an edge to other hosts
    /// chooses its source, as a click on the edge does. Rows restated with the same words are the same rows, so the table
    /// keeps an edge chosen through a restatement, and a relationship's choice is kept by its own.
    /// </summary>
    public RelationshipRow? SelectedTableRow
    {
        get => selectedRelationship ?? ChosenHostEdgeRow();
        set
        {
            if (value is not { ToHosts: true } host)
            {
                chosenHostEdge = null;
                SelectedRelationship = value;
                OnPropertyChanged();
                return;
            }

            // The rows are restated as bytes are read for a selection, so a row is known by its key.
            if (string.Equals(ChosenHostEdgeRow()?.Key, host.Key, StringComparison.Ordinal))
            {
                return;
            }

            SelectGraphNode(host.SourceNode);
            chosenHostEdge = host.Key;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedTableRowExplanation));
            OnPropertyChanged(nameof(HasSelectedTableRow));
        }
    }

    /// <summary>The row chosen in the table in words, beneath it: what its explanation, direction and Enter say.</summary>
    public string SelectedTableRowExplanation => SelectedTableRow is { ToHosts: true } host
        ? $"{host.Explanation} {host.DirectionLine}. " + (host.Opens.Length > 0
            ? $"Enter opens {host.Opens}."
            : "Enter opens nothing here; each process's rung lists its connections.")
        : SelectedRelationshipExplanation;

    public bool HasSelectedTableRow => SelectedTableRow is not null;

    /// <summary>
    /// Why the table lists nothing, in its place (R21): no relationship and no edge to other hosts. Null while it lists any
    /// row; where it lists only edges to other hosts, they say what is there.
    /// </summary>
    public string? TableRowsAbsent => TableRows.Count > 0 ? null : RelationshipsAbsent;

    /// <summary>Enter, or a double click, on the row chosen in the table: what a double click on its edge opens.</summary>
    public bool OpenSelectedTableRow() => SelectedTableRow is { ToHosts: true } host
        ? OpenGraphEdge(host.Key)
        : OpenSelectedRelationship();

    /// <summary>
    /// The edge to other hosts chosen in the table, while its source is still the selection: a selection made anywhere else,
    /// or another rung, lets it go. Null while none is.
    /// </summary>
    private RelationshipRow? ChosenHostEdgeRow() => chosenHostEdge is { } key
        ? TableRows.FirstOrDefault(row => row.ToHosts && string.Equals(row.Key, key, StringComparison.Ordinal))
        : null;

    /// <summary>
    /// Chooses <paramref name="row"/>'s edge to other hosts again where its source is the selection, as a newer generation
    /// or a way back restores it: what choosing it would select is selected already. False where its source is not.
    /// </summary>
    private bool ChooseHostEdgeAgain(RelationshipRow row)
    {
        if (!string.Equals(SelectedGraphNodeKey, row.SourceNode, StringComparison.Ordinal))
        {
            return false;
        }

        chosenHostEdge = row.Key;
        RaiseTableChoice();
        return true;
    }

    /// <summary>Lets go of the edge to other hosts chosen in the table, and says so.</summary>
    private void ForgetHostEdge()
    {
        chosenHostEdge = null;
        RaiseTableChoice();
    }

    /// <summary>Restates the table's rows where the relationships or the drawing gave it others, and then its choice.</summary>
    private void RestateTableRows()
    {
        if (ReferenceEquals(TableRows, restatedTableRows))
        {
            return;
        }

        restatedTableRows = TableRows;
        OnPropertyChanged(nameof(TableRows));
        OnPropertyChanged(nameof(TableRowsAbsent));
        RaiseTableChoice();
    }

    /// <summary>Says the table's choice may have changed: the row chosen, and the line beneath the table.</summary>
    private void RaiseTableChoice()
    {
        OnPropertyChanged(nameof(SelectedTableRow));
        OnPropertyChanged(nameof(SelectedTableRowExplanation));
        OnPropertyChanged(nameof(HasSelectedTableRow));
    }

    /// <summary>The graph's edges to other hosts at this rung as the table's rows, the busiest first.</summary>
    private RelationshipRow[] HostEdgeRows()
    {
        var rows = new List<RelationshipRow>();
        foreach (GraphDisplayEdge edge in graphDisplay.Edges
            .Where(edge => edge.ToHosts)
            .OrderByDescending(edge => edge.ObservationCount)
            .ThenBy(edge => edge.Key, StringComparer.Ordinal))
        {
            GraphDisplayNode? source = graphDisplay.Node(edge.SourceKey);
            HostLink[] links = LinksOn(edge, graphDisplay);
            IReadOnlyList<OtherHost> hosts = OtherHost.Of(links);
            string from = source?.Title ?? edge.SourceKey;

            // The host where it reached one, by the port it ran to; else its target and how many hosts, which a sentence
            // names by the target alone.
            string toward = hosts is [{ } only]
                ? only.Ports is [ushort port] ? WithPort(only.Address, port) : only.Address
                : graphDisplay.Node(edge.TargetKey)?.Label ?? edge.TargetKey;
            string to = hosts.Count == 1 ? toward : $"{toward} · {CountText.Of(hosts.Count, "host")}";
            FamilyTokens tokens = ThemePalette.TokensFor(ThemeResources.CurrentMode, ThemePalette.FamilyOf(edge.Mechanism));
            string reach = links is [{ } link] ? HostLinkText.Reach(link) : HostLinkText.Reach(edge.Mechanism, links);
            rows.Add(new RelationshipRow(
                source is { Members: [ProcessInstanceId member] } ? member : default,
                from,
                to,
                tokens.Label,
                tokens.Glyph,
                edge.ObservationCount.ToString("N0", CultureInfo.CurrentCulture),
                "not counted",
                DescribeStrength(edge.Strength))
            {
                ObservationCount = edge.ObservationCount,
                Explanation = (HostLinkText.Named(hosts, HostsOnEdgeCard) is { } named ? $"{reach}. {named}." : reach + ".")
                    + " Who is at " + (hosts.Count == 1 ? "it" : "them") + " is not observed.",
                Key = edge.Key,
                Opens = source is null ? string.Empty : HostEdgeOpens(source),
                Direction = DirectionText.Glyph(edge.MadeFromSource, edge.MadeFromTarget),
                DirectionLine = DirectionLine(edge, from, toward),
                SpokenDirection = DirectionText.Spoken(edge.MadeFromSource, edge.MadeFromTarget, from, toward),
                ToHosts = true,
                SourceNode = edge.SourceKey,
            });
        }

        return [.. rows];
    }

    /// <summary>What a double click on an edge from <paramref name="source"/> to other hosts opens; empty where it opens nothing.</summary>
    private string HostEdgeOpens(GraphDisplayNode source) =>
        source.Kind == GraphNodeKind.Group && ladder.Current.Level == DetailLevel.Machine ? "its group"
        : ListsProcesses(source) ? "its processes in the inspector"
        : OpensProcess(source) ? "its process's rung, which lists each connection"
        : string.Empty;
}
