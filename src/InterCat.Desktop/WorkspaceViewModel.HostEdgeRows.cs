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
/// The graph draws such an edge with no traffic value, yet its connections' records measured what they carried: once the
/// table lists it in a session whose bytes are read, its row says what crossed them, each transfer counted once at this
/// machine's end - its sends, and the other host's as its receives - and its explanation what it sent and received.
/// </summary>
public sealed partial class WorkspaceViewModel
{
    // The table's rows, worked out once for each relationship list and drawing; the rows last restated in the table.
    private (IReadOnlyList<RelationshipRow> Relationships, GraphDisplay Display, IReadOnlyList<RelationshipRow> Rows)? tableRows;
    private IReadOnlyList<RelationshipRow>? restatedTableRows;

    // While the table takes rows restated in other words, as when their bytes are read, its list lets its choice go for a
    // moment; the choice is kept by key instead.
    private bool restatingTable;

    // The edge to other hosts whose row was chosen, while its source stays the selection.
    private string? chosenHostEdge;

    // What each process's connections over each transport to each host carried, as read for this generation - a reach read
    // and found holding none carries none - and the read of the reaches drawn that are not read yet.
    private readonly Dictionary<string, TransportBytes> reachTraffic = new(StringComparer.OrdinalIgnoreCase);
    private HostEdgeTrafficLoad? hostEdgeTraffic;

    /// <summary>Completes when the latest read of what the table's edges to other hosts carried has applied or failed.</summary>
    public Task HostEdgeTrafficReady { get; private set; } = Task.CompletedTask;

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
    /// chooses its source, as a click on the edge does. Through a restatement of its rows, in the same words or in others as
    /// their bytes are read, the table keeps the row chosen by its key.
    /// </summary>
    public RelationshipRow? SelectedTableRow
    {
        // While the list restates its rows a binding that reads the choice back finds none, as the list then holds none; said
        // changed once the rows are in place, the choice is then a change it applies.
        get => restatingTable ? null : selectedRelationship ?? ChosenHostEdgeRow();
        set
        {
            if (restatingTable)
            {
                return;
            }

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
            RaiseTableChoice();
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
        FollowHostEdgeTraffic();
        if (ReferenceEquals(TableRows, restatedTableRows))
        {
            return;
        }

        restatedTableRows = TableRows;
        restatingTable = true;
        try
        {
            OnPropertyChanged(nameof(TableRows));
        }
        finally
        {
            restatingTable = false;
        }

        OnPropertyChanged(nameof(TableRowsAbsent));
        RaiseTableChoice();
    }

    /// <summary>
    /// Says the table's choice may have changed: the row chosen, the line beneath the table, and the records an edge to
    /// other hosts chosen there stands for - the inspector's count of them and the timeline's highlight.
    /// </summary>
    private void RaiseTableChoice()
    {
        OnPropertyChanged(nameof(SelectedTableRow));
        OnPropertyChanged(nameof(SelectedTableRowExplanation));
        OnPropertyChanged(nameof(HasSelectedTableRow));
        OnPropertyChanged(nameof(EvidenceHeading));
        OnPropertyChanged(nameof(EvidenceSummary));
        UpdateHighlight();
    }

    /// <summary>
    /// The records the edge to other hosts chosen in the table stands for (§6.4, I5): its processes' connections over its
    /// transport to the hosts it draws, as E lists them, the timeline highlights them and the inspector counts them; null
    /// while none is chosen.
    /// </summary>
    private (string Key, Mechanism Mechanism, IReadOnlyList<ProcessInstanceId> Holders, string Name)? ChosenHostEdgeRecords()
    {
        if (ChosenHostEdgeRow() is not { } row || graphDisplay.Edges.FirstOrDefault(edge => edge.Key == row.Key) is not { } edge)
        {
            return null;
        }

        HostLink[] links = LinksOn(edge, graphDisplay);
        ProcessInstanceId[] holders = [.. links.Select(link => link.Process).Distinct()];
        return (HostScopeKeys.Of(links.SelectMany(link => link.Addresses.Select(address => address.Address)), holders),
            edge.Mechanism, holders, row.Name);
    }

    /// <summary>E's step from an edge to other hosts chosen in the table: its records alone, its transport's a filter of its own.</summary>
    private LadderDescent? HostEdgeEvidenceDescent(TimeRange viewport, string rung)
    {
        if (ChosenHostEdgeRecords() is not { } toHosts)
        {
            return null;
        }

        LadderDescent descent = LadderProjection.EvidenceDescentFor(ladder.Current, viewport,
            new(DetailLevel.Channel, toHosts.Key, toHosts.Name),
            $"Evidence was reached from the {rung} rung with this edge to other hosts chosen: its processes' connections "
            + "to the hosts it draws.");
        return descent with
        {
            AddedFilters = [.. descent.AddedFilters, EvidenceScopes.MechanismFilter(toHosts.Mechanism,
                "An edge to other hosts draws one transport's connections.")],
        };
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
            TransportBytes? carried = Carried(links);
            rows.Add(new RelationshipRow(
                source is { Members: [ProcessInstanceId member] } ? member : default,
                from,
                to,
                tokens.Label,
                tokens.Glyph,
                edge.ObservationCount.ToString("N0", CultureInfo.CurrentCulture),
                !ReadsBytes ? "bytes unknown"
                : carried is { } sum ? new SentAcrossTally(checked(sum.SentBytes + sum.ReceivedBytes),
                    sum.SentMeasured + sum.ReceivedMeasured, sum.SentUnmeasured + sum.ReceivedUnmeasured).Phrase
                : hostEdgeTraffic is { Problem: not null } ? "bytes could not be read"
                : hostEdgeTraffic is not null ? "reading bytes…"
                : "bytes not read",
                DescribeStrength(edge.Strength))
            {
                ObservationCount = edge.ObservationCount,
                Explanation = (HostLinkText.Named(hosts, HostsOnEdgeCard) is { } named ? $"{reach}. {named}." : reach + ".")
                    + " Who is at " + (hosts.Count == 1 ? "it" : "them") + " is not observed."
                    + (carried is { } each
                        ? " At this machine's end: " + WorkspaceRowBuilder.Directional(each.SentBytes, each.SentMeasured,
                            each.SentUnmeasured, "sent", "send") + " · " + WorkspaceRowBuilder.Directional(each.ReceivedBytes,
                            each.ReceivedMeasured, each.ReceivedUnmeasured, "received", "receive") + "."
                        : string.Empty)
                    + (appliedInterval is null ? string.Empty
                        : carried is null ? " Its counts are the whole session's."
                        : " Its counts and bytes are the whole session's."),
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

    /// <summary>What <paramref name="links"/>' connections carried, each reach once, once each is read; null until then.</summary>
    private TransportBytes? Carried(IEnumerable<HostLink> links)
    {
        TransportBytes sum = TransportBytes.None;
        foreach (string reach in ReachKeys(links))
        {
            if (!reachTraffic.TryGetValue(reach, out TransportBytes? carried))
            {
                return null;
            }

            sum = sum.Plus(carried);
        }

        return sum;
    }

    /// <summary>The links of every edge to other hosts drawn at this rung, each with the addresses its edge draws.</summary>
    private HostLink[] DrawnHostEdgeLinks() =>
        [.. graphDisplay.Edges.Where(edge => edge.ToHosts).SelectMany(edge => LinksOn(edge, graphDisplay))];

    /// <summary>The reaches <paramref name="links"/> name: each process's connections over a transport to one address.</summary>
    private static IEnumerable<string> ReachKeys(IEnumerable<HostLink> links) =>
        links.SelectMany(link => link.Addresses.Select(address => ReachKey(link.Process, link.Mechanism, address.Address)));

    private static string ReachKey(ProcessInstanceId process, Mechanism mechanism, string address) =>
        string.Create(CultureInfo.InvariantCulture, $"{process}|{(int)mechanism}|{address}");

    /// <summary>
    /// Reads what the drawn edges to other hosts carried while the table lists them in a session whose bytes are read: the
    /// reaches drawn that are not read yet, once, each read kept for this generation, so a rung drawn before reads none
    /// again. Until they are in, the rows they are drawn on say they are being read.
    /// </summary>
    private void FollowHostEdgeTraffic()
    {
        if (disposed || !ReadsBytes || !showTables)
        {
            return;
        }

        HostLink[] unread = [.. DrawnHostEdgeLinks()
            .Select(link => link with
            {
                Addresses = [.. link.Addresses.Where(address =>
                    !reachTraffic.ContainsKey(ReachKey(link.Process, link.Mechanism, address.Address)))],
            })
            .Where(link => link.Addresses.Count > 0)];
        string[] reaches = [.. ReachKeys(unread).Order(StringComparer.OrdinalIgnoreCase)];
        if (reaches.Length == 0 || hostEdgeTraffic is { } reading && reading.Reaches.SequenceEqual(reaches, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        CancelHostEdgeTraffic();
        var load = new HostEdgeTrafficLoad(reaches);
        hostEdgeTraffic = load;
        tableRows = null;
        HostEdgeTrafficReady = LoadHostEdgeTrafficAsync(load, unread);
    }

    private async Task LoadHostEdgeTrafficAsync(HostEdgeTrafficLoad load, HostLink[] links)
    {
        try
        {
            IReadOnlyList<HostReachTraffic> read = await evidenceSource!.HostReachTrafficAsync(links, load.Cancellation.Token)
                .AnsweredLater();
            if (disposed || !ReferenceEquals(hostEdgeTraffic, load)) return;
            Dictionary<string, TransportBytes> found = read.ToDictionary(reach => ReachKey(reach.Process, reach.Mechanism,
                reach.Address), reach => reach.Bytes, StringComparer.OrdinalIgnoreCase);
            foreach (string reach in load.Reaches)
            {
                reachTraffic[reach] = found.GetValueOrDefault(reach) ?? TransportBytes.None;
            }

            hostEdgeTraffic = null;
            load.Cancellation.Dispose();
        }
        catch (OperationCanceledException) when (load.Cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException or ArgumentException)
        {
            if (disposed || !ReferenceEquals(hostEdgeTraffic, load)) return;
            load.Problem = exception.Message;
        }

        tableRows = null;
        RestateTableRows();
    }

    private void CancelHostEdgeTraffic()
    {
        if (hostEdgeTraffic is not { } load)
        {
            return;
        }

        load.Cancellation.Cancel();
        load.Cancellation.Dispose();
        hostEdgeTraffic = null;
    }

    /// <summary>What a double click on an edge from <paramref name="source"/> to other hosts opens; empty where it opens nothing.</summary>
    private string HostEdgeOpens(GraphDisplayNode source) =>
        source.Kind == GraphNodeKind.Group && ladder.Current.Level == DetailLevel.Machine ? "its group"
        : ListsProcesses(source) ? "its processes in the inspector"
        : OpensProcess(source) ? "its process's rung, which lists each connection"
        : string.Empty;

    private sealed class HostEdgeTrafficLoad(string[] reaches)
    {
        public string[] Reaches { get; } = reaches;

        public string? Problem { get; set; }

        public CancellationTokenSource Cancellation { get; } = new();
    }
}
