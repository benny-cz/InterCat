using System.Globalization;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Domain;
using Xunit;

namespace InterCat.Application.Tests;

/// <summary>
/// A comparison shown on the graph (`contracts/comparison-v1.md` §8e): each drawn edge marked by how the rows it draws
/// changed, the relationship table's rows by theirs, and every row no edge draws counted rather than lost.
/// </summary>
public sealed class GraphComparisonOverlayTests
{
    private const long Second = 10_000_000;
    private static readonly MechanismCoverage Covered = new(Mechanism.Tcp, CoverageState.Covered, "covered");
    private static readonly ProcessInstanceId Client = Id(1);
    private static readonly ProcessInstanceId Server = Id(2);
    private static readonly ProcessInstanceId Worker = Id(3);
    private static readonly ProcessInstanceId Helper = Id(4);
    private static readonly ProcessInstanceId Quiet = Id(5);
    private static readonly TimeRange Before = new(0, 10 * Second);
    private static readonly TimeRange After = new(10 * Second, 20 * Second);
    private static readonly HostLink ClientHosts = new(Client, Mechanism.Tcp,
        [new HostAddress("10.0.0.5", 1, 4), new HostAddress("10.0.0.9", 1, 2)], RelationStrength.Direct);

    [Fact(DisplayName = "R14: an edge's mark is its rows' change where they agree, their direction where each went one way, and none where one cannot be compared")]
    public void AnEdgesMarkIsItsRowsChangeTogether()
    {
        static GraphComparisonMark Of(params ComparedRelationship[] rows) => GraphComparisonOverlay.MarkOf(rows);
        Assert.Equal(GraphComparisonMark.Unchanged, Of(Row(2, 2), Row(1, 1)));
        Assert.Equal(GraphComparisonMark.Increased, Of(Row(1, 2)));
        Assert.Equal(GraphComparisonMark.Decreased, Of(Row(2, 1)));
        Assert.Equal(GraphComparisonMark.Appeared, Of(Row(0, 1), Row(0, 3)));
        Assert.Equal(GraphComparisonMark.Disappeared, Of(Row(1, 0), Row(3, 0)));

        // Rows that went one way or stayed went that way together; from nothing, or to nothing, they appeared or went.
        Assert.Equal(GraphComparisonMark.Increased, Of(Row(1, 2), Row(0, 1), Row(1, 1)));
        Assert.Equal(GraphComparisonMark.Increased, Of(Row(0, 1), Row(1, 1)));
        Assert.Equal(GraphComparisonMark.Decreased, Of(Row(2, 1), Row(1, 0), Row(1, 1)));
        Assert.Equal(GraphComparisonMark.Decreased, Of(Row(1, 0), Row(1, 1)));

        // By bytes, a row whose records sent nothing in either interval is unchanged at nothing, and adds nothing before.
        ComparedRelationship nothingSent = new(new(3, 0, 0, 0, Covered), new(2, 0, 0, 0, Covered), ComparisonChange.Unchanged, null);
        Assert.Equal(GraphComparisonMark.Appeared, Of(Row(0, 1), nothingSent));
        Assert.Equal(GraphComparisonMark.Disappeared, Of(Row(1, 0), nothingSent));

        // Rows that went both ways are marked so; one that cannot be compared makes the whole not comparable.
        Assert.Equal(GraphComparisonMark.Mixed, Of(Row(1, 2), Row(2, 1)));
        Assert.Equal(GraphComparisonMark.Mixed, Of(Row(0, 1), Row(1, 0)));
        Assert.Equal(GraphComparisonMark.NotComparable, Of(Row(1, 2), NotComparable("A.")));
        Assert.Throws<ArgumentException>(() => Of());

        // Every mark has a glyph and words of its own, so none is read from colour alone.
        GraphComparisonMark[] marks = Enum.GetValues<GraphComparisonMark>();
        Assert.Equal(["=", "▲", "▼", "+", "−", "?", "±"], marks.Select(GraphComparisonOverlay.Glyph));
        Assert.Equal(["unchanged", "up", "down", "appeared", "disappeared", "not comparable", "up and down"],
            marks.Select(GraphComparisonOverlay.Words));
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphComparisonOverlay.Glyph((GraphComparisonMark)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphComparisonOverlay.Words((GraphComparisonMark)99));
    }

    [Fact(DisplayName = "R15: each drawn edge is marked by the rows it draws, the table's rows by theirs, and every other row counted")]
    public void EachDrawnEdgeIsMarkedByTheRowsItDraws()
    {
        CultureInfo culture = CultureInfo.InvariantCulture;
        ComparedRelationship toServer = Row(10, 20, Pair("tcp:1:2", Client, Server));
        ComparedRelationship toWorker = Row(1, 2, Pair("tcp:1:3", Client, Worker));
        ComparedRelationship toHelper = Row(2, 1, Pair("tcp:1:4", Client, Helper));
        ComparedRelationship within = Row(0, 3, Pair("tcp:3:4", Worker, Helper));
        ComparedRelationship opened = Row(4, 0) with { Host = new OtherHostReach(ClientHosts, ClientHosts.Addresses[0]) };
        ComparedRelationship folded = Row(2, 2) with { Host = new OtherHostReach(ClientHosts, ClientHosts.Addresses[1]) };
        ComparedRelationship gone = Row(1, 2, Pair("tcp:9:9", Server, Server));
        IntervalComparison comparison = Comparison(ComparisonGrouping.Process, toServer, toWorker, toHelper, within, opened, folded, gone);

        GraphComparisonOverlay overlay = GraphComparisonOverlay.Of(comparison, Display(), Overview(), [ClientHosts]);

        // A relationship's edge carries its row's change; an edge drawing two rows that went both ways says so; a host's edge
        // carries the connections to it, and Other hosts' those to every address not drawn on its own.
        Assert.Equal(
            [
                ("cluster-edge:group", GraphComparisonMark.Mixed, 2),
                ("hosts-edge:host", GraphComparisonMark.Disappeared, 1),
                ("hosts-edge:other", GraphComparisonMark.Unchanged, 1),
                ("tcp:1:2", GraphComparisonMark.Increased, 1),
            ],
            overlay.Edges.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => (pair.Key, pair.Value.Mark, pair.Value.Rows.Count)));
        Assert.All(overlay.Edges.Values, marked => Assert.False(marked.Shared));
        Assert.Equal([toWorker, toHelper], overlay.Edges["cluster-edge:group"].Rows);

        // A row between processes drawn together is inside their node; one of a relationship the graph lacks is not drawn;
        // an edge that draws no row carries no mark.
        Assert.Equal((5, 1, 1, 1), (overlay.Drawn, overlay.Inside, overlay.Undrawn, overlay.Unmarked));
        Assert.False(overlay.Edges.ContainsKey("tcp:1:5"));

        // The table's rows: each relationship by its own row, an edge to other hosts by its edge's, and one compared in no row
        // by nothing.
        Assert.Equal(GraphComparisonMark.Increased, overlay.Of("tcp:1:3")!.Mark);
        Assert.Equal(GraphComparisonMark.Appeared, overlay.Of("tcp:3:4")!.Mark);
        Assert.Same(overlay.Edges["hosts-edge:host"], overlay.Of("hosts-edge:host"));
        Assert.Null(overlay.Of("tcp:1:5"));

        // In words: one row's change as `icat compare` says it, and several rows' together, with how many went each way.
        Assert.Equal("▲ up 1.00/s (+100%)", overlay.Describe(overlay.Edges["tcp:1:2"], culture));
        Assert.Equal("± up and down across its 2 rows compared: 1 up, 1 down", overlay.Describe(overlay.Edges["cluster-edge:group"], culture));
        Assert.Equal("− disappeared", overlay.Describe(overlay.Edges["hosts-edge:host"], culture));
        IntervalComparison unsure = Comparison(ComparisonGrouping.Process, NotComparable("Not covered.") with { Relationship = toServer.Relationship });
        GraphComparisonOverlay doubted = GraphComparisonOverlay.Of(unsure, Display(), Overview(), [ClientHosts]);
        Assert.Equal("? not comparable: Not covered.", doubted.Describe(doubted.Edges["tcp:1:2"], culture));
        Assert.Equal((1, 0, 0, 4), (doubted.Drawn, doubted.Inside, doubted.Undrawn, doubted.Unmarked));

        // Several rows' changes are each counted in their own words, whichever they are.
        GraphComparisonOverlay grew = GraphComparisonOverlay.Of(Comparison(ComparisonGrouping.Process,
            Row(1, 1, Pair("tcp:1:3", Client, Worker)), Row(0, 2, Pair("tcp:1:4", Client, Helper))), Display(), Overview(), [ClientHosts]);
        Assert.Equal("▲ up across its 2 rows compared: 1 unchanged, 1 appeared", grew.Describe(grew.Edges["cluster-edge:group"], culture));
        GraphComparisonOverlay doubtful = GraphComparisonOverlay.Of(Comparison(ComparisonGrouping.Process,
            Row(2, 0, Pair("tcp:1:3", Client, Worker)), NotComparable("Not covered.") with { Relationship = Pair("tcp:1:4", Client, Helper) }),
            Display(), Overview(), [ClientHosts]);
        Assert.Equal("? not comparable across its 2 rows compared: 1 disappeared, 1 not comparable",
            doubtful.Describe(doubtful.Edges["cluster-edge:group"], culture));

        // The line beneath the graph's header says it all, the marks with their glyphs, so a missing mark is never read as no change.
        string before = WorkspaceTime.FormatHalfOpenRange(Before, culture), after = WorkspaceTime.FormatHalfOpenRange(After, culture);
        Assert.Equal($"The graph marks each edge by how it changed from {before} to {after}, by records, a row per process. 4 edges "
            + "are marked: 1 unchanged =, 1 up ▲, 1 disappeared −, 1 up and down ±. 1 drawn edge holds no row of it, no record in "
            + "either interval, and carries no mark. Of its 7 rows, 1 is inside a drawn node and 1 is not drawn; the Compare "
            + "window lists every row.", overlay.Summary(null, culture));
        Assert.Equal($"The graph marks each edge by how it changed from {before} to {after}, by records, a row per process. 1 edge "
            + "is marked: 1 not comparable ?. 4 drawn edges hold no row of it, no record in either interval, and carry no mark.",
            doubted.Summary(null, culture));
        IntervalComparison none = Comparison(ComparisonGrouping.Process, gone) with { Metric = ComparisonMetric.BytesSent };
        Assert.Equal($"The graph marks each edge by how it changed from {before} to {after}, by bytes sent across, a row per process. "
            + "No edge drawn holds a row it compares. 5 drawn edges hold no row of it, no record in either interval, and carry no "
            + "mark. Of its 1 row, 1 is not drawn; the Compare window lists every row.",
            GraphComparisonOverlay.Of(none, Display(), Overview(), [ClientHosts]).Summary(null, culture));
    }

    [Fact(DisplayName = "P6: a comparison by executable marks every edge of its processes by their executables' row, and says the mark is of more than the edge")]
    public void AnExecutablesRowMarksEachEdgeOfItsProcesses()
    {
        CultureInfo culture = CultureInfo.InvariantCulture;
        const string Tools = "executable:C:\\TOOLS\\CLIENT.EXE", Servers = "executable:C:\\TOOLS\\SERVER.EXE";
        ComparedRelationship pair = Row(3, 6) with
        {
            Executables = new ExecutableRelationship(SessionComparison.ExecutablesKey(Mechanism.Tcp, Servers, Tools), Tools, Servers,
                Mechanism.Tcp),
        };
        ComparedRelationship hosts = Row(6, 2) with
        {
            ExecutableHost = new ExecutableReach(SessionComparison.ExecutableHostsKey(Mechanism.Tcp, Tools, "10.0.0.9"), Tools,
                Mechanism.Tcp, "10.0.0.9"),
        };
        IntervalComparison comparison = Comparison(ComparisonGrouping.Executable, pair, hosts) with
        {
            Processes = new Dictionary<ProcessInstanceId, ProcessNode>
            {
                [Client] = Node(Client, Tools),
                [Worker] = Node(Worker, Tools),
                [Server] = Node(Server, Servers),
                [Helper] = Node(Helper, Servers),
                [Quiet] = Node(Quiet, ProcessGroup.UnwitnessedExecutableKey),
            },
            BaselineSessionId = Guid.Parse("0c0c0c0c-0000-4000-8000-000000000762"),
        };

        GraphComparisonOverlay overlay = GraphComparisonOverlay.Of(comparison, Display(), Overview(), [ClientHosts]);

        // Client's relationships with the server and with the helper are both of the pair; the one with its own worker is of
        // no row compared, and the one with a process of no witnessed executable of none.
        Assert.Equal(
            [
                ("cluster-edge:group", GraphComparisonMark.Increased, true),
                ("hosts-edge:other", GraphComparisonMark.Decreased, false),
                ("tcp:1:2", GraphComparisonMark.Increased, true),
            ],
            overlay.Edges.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value.Mark, pair.Value.Shared)));
        Assert.Equal((2, 0, 0, 2), (overlay.Drawn, overlay.Inside, overlay.Undrawn, overlay.Unmarked));
        Assert.True(overlay.Of("tcp:1:2")!.Shared);
        Assert.True(overlay.Of("tcp:3:4")!.Shared);
        Assert.Null(overlay.Of("tcp:1:3"));
        Assert.Null(overlay.Of("tcp:1:5"));
        Assert.Equal("▲ up 0.30/s (+100%), of its executables' processes together", overlay.Describe(overlay.Edges["tcp:1:2"], culture));

        // Drawn apart, the pair's three relationships are three edges, each marked by the pair, and so of more than itself.
        GraphComparisonOverlay apart = GraphComparisonOverlay.Of(comparison, Apart(), Overview(), [ClientHosts]);
        Assert.Equal(
            [
                ("hosts-edge:other", GraphComparisonMark.Decreased, false),
                ("tcp:1:2", GraphComparisonMark.Increased, true),
                ("tcp:1:4", GraphComparisonMark.Increased, true),
                ("tcp:3:4", GraphComparisonMark.Increased, true),
            ],
            apart.Edges.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value.Mark, pair.Value.Shared)));
        Assert.Equal((2, 0, 0, 2), (apart.Drawn, apart.Inside, apart.Undrawn, apart.Unmarked));

        // With the server another program's, the pair is the client's and the worker's with the helper: drawn in one edge
        // and inside the group the worker and the helper are drawn as, so its mark is still of more than the edge.
        IntervalComparison otherServer = comparison with
        {
            Processes = new Dictionary<ProcessInstanceId, ProcessNode>(comparison.Processes)
            {
                [Server] = Node(Server, "executable:C:\\TOOLS\\OTHER.EXE"),
            },
        };
        GraphComparisonOverlay folded = GraphComparisonOverlay.Of(otherServer, Display(), Overview(), [ClientHosts]);
        Assert.True(folded.Edges["cluster-edge:group"].Shared);
        Assert.False(folded.Edges.ContainsKey("tcp:1:2"));
        Assert.Equal((2, 0, 0), (folded.Drawn, folded.Inside, folded.Undrawn));

        // Its baseline another capture's, named by its session; a mark of more than its edge drawn with a broken outline.
        string after = WorkspaceTime.FormatHalfOpenRange(After, culture);
        Assert.Equal($"The graph marks each edge by how it changed from {WorkspaceTime.FormatHalfOpenRange(Before, culture)} of capture "
            + $"0c0c0c0c to {after}, by records, a row per executable. 3 edges are marked: 2 up ▲, 1 down ▼. 2 drawn edges hold no "
            + "row of it, no record in either interval, and carry no mark. A mark drawn with a broken outline is of its "
            + "executables' processes together, of which the edge draws some.", overlay.Summary(null, culture));
    }

    /// <summary>
    /// The window's drawing: client.exe and server.exe on their own, the worker and the helper drawn together as one group,
    /// a quiet process, Other hosts and the one host it was opened into.
    /// </summary>
    private static GraphDisplay Display()
    {
        string clientHosts = ClientHosts.Key;
        GraphDisplayNode[] nodes =
        [
            Process("process:1", Client),
            Process("process:2", Server),
            new("group:svc", "svc", GraphNodeKind.Group, "group:svc", null, null, [Worker, Helper], 3, 2, 1),
            Process("process:5", Quiet),
            new(GraphProjection.OtherHostsKey, "Other hosts", GraphNodeKind.OtherHosts, null, null, null, [], 0, 0, 0),
            new(GraphProjection.HostKey("10.0.0.5"), "10.0.0.5", GraphNodeKind.Host, null, null, null, [], 0, 0, 0),
        ];
        GraphDisplayEdge[] edges =
        [
            Edge("tcp:1:2", "process:1", "process:2", "tcp:1:2"),
            Edge("cluster-edge:group", "process:1", "group:svc", "tcp:1:3", "tcp:1:4"),
            Edge("tcp:1:5", "process:1", "process:5", "tcp:1:5"),
            Edge("hosts-edge:other", "process:1", GraphProjection.OtherHostsKey, clientHosts) with { ToHosts = true },
            Edge("hosts-edge:host", "process:1", GraphProjection.HostKey("10.0.0.5"), clientHosts) with { ToHosts = true },
        ];
        return new GraphDisplay(nodes, edges, 5, null, null, 5, ["tcp:1:2", "tcp:1:3", "tcp:1:4", "tcp:1:5", "tcp:3:4"],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["tcp:3:4"] = "group:svc" }, ["10.0.0.5"]);
    }

    /// <summary>The same processes each drawn on its own, and Other hosts opened into none.</summary>
    private static GraphDisplay Apart()
    {
        GraphDisplayNode[] nodes =
        [
            Process("process:1", Client),
            Process("process:2", Server),
            Process("process:3", Worker),
            Process("process:4", Helper),
            Process("process:5", Quiet),
            new(GraphProjection.OtherHostsKey, "Other hosts", GraphNodeKind.OtherHosts, null, null, null, [], 0, 0, 0),
        ];
        GraphDisplayEdge[] edges =
        [
            Edge("tcp:1:2", "process:1", "process:2", "tcp:1:2"),
            Edge("tcp:1:3", "process:1", "process:3", "tcp:1:3"),
            Edge("tcp:1:4", "process:1", "process:4", "tcp:1:4"),
            Edge("tcp:1:5", "process:1", "process:5", "tcp:1:5"),
            Edge("tcp:3:4", "process:3", "process:4", "tcp:3:4"),
            Edge("hosts-edge:other", "process:1", GraphProjection.OtherHostsKey, ClientHosts.Key) with { ToHosts = true },
        ];
        return new GraphDisplay(nodes, edges, 5, null, null);
    }

    /// <summary>The relationships the window's session holds, which the drawing draws.</summary>
    private static CommunicationEdge[] Overview() =>
    [
        Pair("tcp:1:2", Client, Server),
        Pair("tcp:1:3", Client, Worker),
        Pair("tcp:1:4", Client, Helper),
        Pair("tcp:1:5", Client, Quiet),
        Pair("tcp:3:4", Worker, Helper),
    ];

    private static IntervalComparison Comparison(ComparisonGrouping grouping, params ComparedRelationship[] rows) =>
        new(Guid.NewGuid(), 3, ComparisonMetric.Records, EvidencePolicy.IncludeCorrelated, Before, After, [Covered], [Covered], rows)
        {
            Processes = new Dictionary<ProcessInstanceId, ProcessNode>(),
            Grouping = grouping,
        };

    /// <summary>A row of records over two equal intervals, judged as the comparison judges it.</summary>
    private static ComparedRelationship Row(long before, long after, CommunicationEdge? relationship = null) =>
        new(Measure(before), Measure(after), (before, after) switch
        {
            (0, > 0) => ComparisonChange.Appeared,
            (> 0, 0) => ComparisonChange.Disappeared,
            _ when after > before => ComparisonChange.Increased,
            _ when after < before => ComparisonChange.Decreased,
            _ => ComparisonChange.Unchanged,
        }, null)
        {
            Relationship = relationship,
        };

    private static ComparedRelationship NotComparable(string reason) =>
        new(Measure(1), Measure(2), ComparisonChange.NotComparable, reason) { Relationship = Pair("tcp:1:2", Client, Server) };

    private static ComparedMeasure Measure(long records) => new(records, records, records, 0, Covered);

    private static CommunicationEdge Pair(string key, ProcessInstanceId source, ProcessInstanceId target) =>
        new(key, source, target, Mechanism.Tcp, 1, null, RelationStrength.Direct) { Rule = RelationRule.TransportEndpoint, Evidence = [key] };

    private static GraphDisplayNode Process(string key, ProcessInstanceId process) =>
        new(key, key, GraphNodeKind.Process, null, process, 100, [process], 1, 1, 0);

    private static GraphDisplayEdge Edge(string key, string source, string target, params string[] relationships) =>
        new(key, source, target, Mechanism.Tcp, 1, RelationStrength.Direct, relationships);

    private static ProcessNode Node(ProcessInstanceId id, string group) =>
        new(id, 100, "process.exe", "test", group, 0.5, 0.5, CoverageState.Covered);

    private static ProcessInstanceId Id(int value) => new(Guid.Parse($"00000000-0000-0000-0000-{value:D12}"));
}
