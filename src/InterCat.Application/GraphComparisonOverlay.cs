using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// How a drawn edge, or a relationship, changed in a comparison shown on the graph (`contracts/comparison-v1.md` §8e): its
/// rows' change where they agree, the direction they all went where they went one way, or that they went both ways or
/// cannot be compared. Each has a glyph, so no mark is read from colour alone (R14).
/// </summary>
public enum GraphComparisonMark
{
    /// <summary>Every row it draws is unchanged.</summary>
    Unchanged = 1,

    /// <summary>Each row it draws rose, appeared or stayed, and something of it was in the baseline: together they rose.</summary>
    Increased = 2,

    /// <summary>Each row it draws fell, disappeared or stayed, and something of it is in the compared interval: together they fell.</summary>
    Decreased = 3,

    /// <summary>Nothing of it was in the baseline, whose coverage judged each row; something is in the compared interval.</summary>
    Appeared = 4,

    /// <summary>Something of it was in the baseline; nothing is in the compared interval, whose coverage judged each row.</summary>
    Disappeared = 5,

    /// <summary>A row it draws cannot be compared, so neither can it: no change is said of it (`comparison-v1` §5).</summary>
    NotComparable = 6,

    /// <summary>Some of its rows rose or appeared and others fell or disappeared, so its change is said row by row.</summary>
    Mixed = 7,
}

/// <summary>A drawn edge's, or a relationship's, part in a comparison shown on the graph.</summary>
/// <param name="Mark">How what it draws changed.</param>
/// <param name="Rows">The comparison's rows it draws, the most changed first, as the comparison ranks them.</param>
/// <param name="Shared">
/// Whether a row it draws is drawn elsewhere too - in a comparison by executable, an executable's processes'
/// relationships together, of which it draws some - so its mark is of more than it.
/// </param>
public sealed record GraphComparisonMarked(GraphComparisonMark Mark, IReadOnlyList<ComparedRelationship> Rows, bool Shared);

/// <summary>
/// A comparison shown on the graph (`contracts/comparison-v1.md` §8e, plan M12): each drawn edge marked by how the rows it
/// draws changed, and each relationship the relationship table lists by its row's, as their table equivalent (R15). A row
/// is drawn where the window draws what it compares - a relationship in its edge, a process's connections to an address in
/// the edge to that host or to Other hosts - and, of a comparison by executable, wherever any of its processes'
/// relationships or connections is: a grouping, never an identity (P6). An edge drawing no row is quiet in both intervals
/// as far as the comparison read, and carries no mark; a row the graph does not draw as an edge is counted, never lost.
/// </summary>
public sealed class GraphComparisonOverlay
{
    private readonly IReadOnlyDictionary<string, GraphComparisonMarked> relationships;

    private GraphComparisonOverlay(IntervalComparison comparison, IReadOnlyDictionary<string, GraphComparisonMarked> edges,
        IReadOnlyDictionary<string, GraphComparisonMarked> relationships, int drawn, int inside, int unmarked)
    {
        Comparison = comparison;
        Edges = edges;
        this.relationships = relationships;
        Drawn = drawn;
        Inside = inside;
        Unmarked = unmarked;
    }

    /// <summary>The comparison shown.</summary>
    public IntervalComparison Comparison { get; }

    /// <summary>Each drawn edge that draws a row, by the edge's key, with its mark.</summary>
    public IReadOnlyDictionary<string, GraphComparisonMarked> Edges { get; }

    /// <summary>How many of the comparison's rows a drawn edge draws.</summary>
    public int Drawn { get; }

    /// <summary>How many of its rows no edge draws but a drawn node holds: relationships between processes drawn together.</summary>
    public int Inside { get; }

    /// <summary>How many of its rows the graph does not draw at all: of processes it does not show, or of hosts its graph lacks.</summary>
    public int Undrawn => Comparison.Relationships.Count - Drawn - Inside;

    /// <summary>How many drawn edges draw no row of it: they had no record in either interval, as far as it read.</summary>
    public int Unmarked { get; }

    /// <summary>
    /// The comparison's change of a relationship the relationship table lists, by its key - its row's, of a comparison by
    /// executable its executables' - or of an edge to other hosts the table lists, by the edge's key; null for one it
    /// compares in no row.
    /// </summary>
    public GraphComparisonMarked? Of(string key) =>
        relationships.TryGetValue(key, out GraphComparisonMarked? relationship) ? relationship : Edges.GetValueOrDefault(key);

    /// <summary>
    /// Marks <paramref name="display"/>'s edges by <paramref name="comparison"/>, its rows found among the window's
    /// <paramref name="overview"/> relationships and <paramref name="hostLinks"/> - the session's it shows, which a comparison
    /// of two captures compared as its compared interval's.
    /// </summary>
    public static GraphComparisonOverlay Of(IntervalComparison comparison, GraphDisplay display,
        IReadOnlyList<CommunicationEdge> overview, IReadOnlyList<HostLink> hostLinks)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(overview);
        ArgumentNullException.ThrowIfNull(hostLinks);

        // Each part of the session a row compares: a relationship by its key, a process's connections to an address by its
        // host link and the address. A row by process is one part; a row by executable is every part of its processes.
        var rowOf = new Dictionary<string, ComparedRelationship>(StringComparer.Ordinal);
        var hostRowOf = new Dictionary<(string Link, string Address), ComparedRelationship>();
        if (comparison.Grouping == ComparisonGrouping.Process)
        {
            foreach (ComparedRelationship row in comparison.Relationships)
            {
                if (row.Relationship is { } relationship) rowOf[relationship.Key] = row;
                else if (row.Host is { } host) hostRowOf[(host.Link.Key, host.Address.Address)] = row;
            }
        }
        else
        {
            // A process of no witnessed executable is in no row (§2a), so its group key finds none.
            Dictionary<string, ComparedRelationship> byKey = comparison.Relationships.ToDictionary(row => row.Key, StringComparer.Ordinal);
            string? ExecutableOf(ProcessInstanceId process) =>
                comparison.Processes.TryGetValue(process, out ProcessNode? node) ? node.GroupKey : null;
            foreach (CommunicationEdge edge in overview)
            {
                if (ExecutableOf(edge.SourceId) is { } source && ExecutableOf(edge.TargetId) is { } target
                    && byKey.TryGetValue(SessionComparison.ExecutablesKey(edge.Mechanism, source, target), out ComparedRelationship? row))
                {
                    rowOf[edge.Key] = row;
                }
            }

            foreach (HostLink link in hostLinks)
            {
                if (ExecutableOf(link.Process) is not { } executable) continue;
                foreach (HostAddress address in link.Addresses)
                {
                    if (byKey.TryGetValue(SessionComparison.ExecutableHostsKey(link.Mechanism, executable, address.Address),
                        out ComparedRelationship? row))
                    {
                        hostRowOf[(link.Key, address.Address)] = row;
                    }
                }
            }
        }

        // The rows each drawn edge draws: its relationships', or its host links' connections to the host it runs to - to
        // Other hosts, those to every address not drawn on its own.
        Dictionary<string, HostLink> links = hostLinks.ToDictionary(link => link.Key, StringComparer.Ordinal);
        var rank = new Dictionary<ComparedRelationship, int>(ReferenceEqualityComparer.Instance);
        for (int index = 0; index < comparison.Relationships.Count; index++)
        {
            rank[comparison.Relationships[index]] = index;
        }

        var drawnBy = new Dictionary<string, List<ComparedRelationship>>(StringComparer.Ordinal);
        var edgesOf = new Dictionary<ComparedRelationship, int>(ReferenceEqualityComparer.Instance);
        int unmarked = 0;
        foreach (GraphDisplayEdge edge in display.Edges)
        {
            var rows = new HashSet<ComparedRelationship>(ReferenceEqualityComparer.Instance);
            foreach (string part in edge.Relationships)
            {
                if (!edge.ToHosts)
                {
                    if (rowOf.TryGetValue(part, out ComparedRelationship? row)) rows.Add(row);
                    continue;
                }

                foreach (string address in AddressesDrawn(display, edge, links.GetValueOrDefault(part)))
                {
                    if (hostRowOf.TryGetValue((part, address), out ComparedRelationship? row)) rows.Add(row);
                }
            }

            if (rows.Count == 0)
            {
                unmarked++;
                continue;
            }

            drawnBy[edge.Key] = [.. rows.OrderBy(row => rank[row])];
            foreach (ComparedRelationship row in rows)
            {
                edgesOf[row] = edgesOf.GetValueOrDefault(row) + 1;
            }
        }

        // A row no edge draws may be inside a drawn node: a relationship between processes the graph draws together. Each
        // relationship's row is counted by the relationships it holds, so the table says which are of more than their own.
        var inside = new HashSet<ComparedRelationship>(ReferenceEqualityComparer.Instance);
        var parts = new Dictionary<ComparedRelationship, int>(ReferenceEqualityComparer.Instance);
        foreach ((string relationship, ComparedRelationship row) in rowOf)
        {
            parts[row] = parts.GetValueOrDefault(row) + 1;
            if (display.EdgeOf(relationship) is null && display.NodeOfRelationship(relationship) is not null) inside.Add(row);
        }

        // A row is shared where it is drawn in more than one place, or held inside a node as well as drawn.
        bool Shared(ComparedRelationship row) => edgesOf.GetValueOrDefault(row) + (inside.Contains(row) ? 1 : 0) > 1;
        Dictionary<string, GraphComparisonMarked> edges = drawnBy.ToDictionary(
            pair => pair.Key, pair => new GraphComparisonMarked(MarkOf(pair.Value), pair.Value, pair.Value.Any(Shared)),
            StringComparer.Ordinal);
        Dictionary<string, GraphComparisonMarked> marked = rowOf.ToDictionary(
            pair => pair.Key, pair => new GraphComparisonMarked(MarkOf([pair.Value]), [pair.Value], parts[pair.Value] > 1),
            StringComparer.Ordinal);
        inside.ExceptWith(edgesOf.Keys);
        return new(comparison, edges, marked, edgesOf.Count, inside.Count, unmarked);
    }

    /// <summary>
    /// How <paramref name="rows"/> changed together: their change where they agree, the direction they went where each went
    /// one way or stayed, that they cannot be compared where one cannot, and else that they went both ways.
    /// </summary>
    public static GraphComparisonMark MarkOf(IReadOnlyCollection<ComparedRelationship> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0) throw new ArgumentException("A mark is of at least one row.", nameof(rows));
        if (rows.Any(row => row.Change == ComparisonChange.NotComparable)) return GraphComparisonMark.NotComparable;
        if (rows.All(row => row.Change == ComparisonChange.Unchanged)) return GraphComparisonMark.Unchanged;
        if (rows.All(row => row.Change is ComparisonChange.Increased or ComparisonChange.Appeared or ComparisonChange.Unchanged))
        {
            return rows.All(row => row.Baseline.None) ? GraphComparisonMark.Appeared : GraphComparisonMark.Increased;
        }

        if (rows.All(row => row.Change is ComparisonChange.Decreased or ComparisonChange.Disappeared or ComparisonChange.Unchanged))
        {
            return rows.All(row => row.Compared.None) ? GraphComparisonMark.Disappeared : GraphComparisonMark.Decreased;
        }

        return GraphComparisonMark.Mixed;
    }

    /// <summary>The glyph a mark is drawn with: ▲ up, ▼ down, + appeared, − disappeared, = unchanged, ? not comparable, ± both ways.</summary>
    public static string Glyph(GraphComparisonMark mark) => mark switch
    {
        GraphComparisonMark.Unchanged => "=",
        GraphComparisonMark.Increased => "▲",
        GraphComparisonMark.Decreased => "▼",
        GraphComparisonMark.Appeared => "+",
        GraphComparisonMark.Disappeared => "−",
        GraphComparisonMark.NotComparable => "?",
        GraphComparisonMark.Mixed => "±",
        _ => throw new ArgumentOutOfRangeException(nameof(mark), mark, "No such mark."),
    };

    /// <summary>A mark in the words `icat compare` states a row's change in: "up", "down", "appeared", and so on.</summary>
    public static string Words(GraphComparisonMark mark) => mark switch
    {
        GraphComparisonMark.Unchanged => "unchanged",
        GraphComparisonMark.Increased => "up",
        GraphComparisonMark.Decreased => "down",
        GraphComparisonMark.Appeared => "appeared",
        GraphComparisonMark.Disappeared => "disappeared",
        GraphComparisonMark.NotComparable => "not comparable",
        GraphComparisonMark.Mixed => "up and down",
        _ => throw new ArgumentOutOfRangeException(nameof(mark), mark, "No such mark."),
    };

    /// <summary>
    /// A mark as an edge's card, the relationship table and the graph's keyboard say it: one row's change in `icat
    /// compare`'s words, with why where it cannot be compared - "▲ up 90/s (+300%)" - or several rows' together with how
    /// many went each way, and, where it is of more than what is marked, that it is of the executables' processes together.
    /// </summary>
    public string Describe(GraphComparisonMarked marked, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(marked);
        culture ??= CultureInfo.CurrentCulture;
        (string change, string after) = marked.Rows is [{ } only]
            ? (ComparisonText.Change(only, Comparison, culture),
                only is { Change: ComparisonChange.NotComparable, Reason: { } reason } ? ": " + reason : string.Empty)
            : (Words(marked.Mark), string.Create(culture, $" across its {marked.Rows.Count:N0} rows compared: ")
                + string.Join(", ", marked.Rows.GroupBy(row => row.Change).OrderBy(group => group.Key)
                    .Select(group => string.Create(culture, $"{group.Count():N0} ") + ChangeWords(group.Key))));
        return Glyph(marked.Mark) + " " + change + (marked.Shared ? ", of its executables' processes together" : string.Empty)
            + after;
    }

    /// <summary>
    /// What the graph marks, in words, for the line beneath its header and a screen reader (R15): the two intervals and
    /// what the rows are, how many drawn edges went each way, each with its glyph, the edges it leaves unmarked, and the rows
    /// no edge draws - so a mark is never the only place a change is stated, and a missing one never reads as no change.
    /// </summary>
    public string Summary(SessionClock? clock = null, IFormatProvider? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        IntervalComparison shown = Comparison;
        string baseline = shown.BaselineSessionId is { } other
            ? WorkspaceTime.FormatHalfOpenRange(shown.Baseline, culture) + " of capture " + InvestigationWorkspace.Short(other)
            : clock is null ? WorkspaceTime.FormatHalfOpenRange(shown.Baseline, culture) : clock.HalfOpenRange(shown.Baseline, culture);
        string compared = clock is null ? WorkspaceTime.FormatHalfOpenRange(shown.Compared, culture) : clock.HalfOpenRange(shown.Compared, culture);
        string[] counted = [.. Edges.Values.GroupBy(marked => marked.Mark).OrderBy(group => group.Key)
            .Select(group => string.Create(culture, $"{group.Count():N0} ") + Words(group.Key) + " " + Glyph(group.Key))];
        string said = "The graph marks each edge by how it changed from " + baseline + " to " + compared
            + (shown.Metric == ComparisonMetric.BytesSent ? ", by bytes sent across" : ", by records")
            + (shown.Grouping == ComparisonGrouping.Executable ? ", a row per executable." : ", a row per process.")
            + (counted.Length == 0
                ? " No edge drawn holds a row it compares."
                : string.Create(culture, $" {Edges.Count:N0} ") + CountText.Agree(Edges.Count, "edge is", "edges are")
                    + " marked: " + string.Join(", ", counted) + ".");
        if (Unmarked > 0)
        {
            said += string.Create(culture, $" {Unmarked:N0} drawn ") + CountText.Agree(Unmarked, "edge holds", "edges hold")
                + " no row of it, no record in either interval, and " + CountText.Agree(Unmarked, "carries", "carry") + " no mark.";
        }

        if (Edges.Values.Any(marked => marked.Shared))
        {
            said += " A mark drawn with a broken outline is of its executables' processes together, of which the edge draws some.";
        }

        if (Inside + Undrawn > 0)
        {
            said += string.Create(culture, $" Of its {shown.Relationships.Count:N0} ")
                + CountText.Agree(shown.Relationships.Count, "row", "rows") + ", "
                + string.Join(" and ", new[]
                {
                    Inside > 0 ? string.Create(culture, $"{Inside:N0} ") + CountText.Agree(Inside, "is", "are") + " inside a drawn node" : null,
                    Undrawn > 0 ? string.Create(culture, $"{Undrawn:N0} ") + CountText.Agree(Undrawn, "is", "are") + " not drawn" : null,
                }.OfType<string>())
                + "; the Compare window lists every row.";
        }

        return said;
    }

    /// <summary>A row's change as a count of rows names it: "up", "down", or its own word.</summary>
    private static string ChangeWords(ComparisonChange change) => change switch
    {
        ComparisonChange.Increased => "up",
        ComparisonChange.Decreased => "down",
        ComparisonChange.Unchanged => "unchanged",
        ComparisonChange.Appeared => "appeared",
        ComparisonChange.Disappeared => "disappeared",
        _ => "not comparable",
    };

    /// <summary>The addresses a drawn edge to other hosts draws of a host link: its host's, or, to Other hosts, every one not drawn on its own.</summary>
    private static IEnumerable<string> AddressesDrawn(GraphDisplay display, GraphDisplayEdge edge, HostLink? link)
    {
        if (edge.TargetKey.StartsWith(GraphProjection.HostKeyPrefix, StringComparison.Ordinal))
        {
            return [edge.TargetKey[GraphProjection.HostKeyPrefix.Length..]];
        }

        return link is null
            ? []
            : link.Addresses.Select(address => address.Address)
                .Where(address => display.Node(GraphProjection.HostKey(address)) is null);
    }
}
