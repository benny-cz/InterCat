using InterCat.Application;
using InterCat.Domain;
using Xunit;

namespace InterCat.Application.Tests;

public sealed class GraphLayoutTests
{
    private static readonly ProcessGroup[] Groups =
    [
        new("clients", "Clients", LaneGrouping.Executable),
        new("services", "Services", LaneGrouping.Executable),
    ];

    private static readonly ProcessNode[] Nodes =
    [
        Node("11111111-1111-1111-1111-111111111111", "clients"),
        Node("22222222-2222-2222-2222-222222222222", "clients"),
        Node("33333333-3333-3333-3333-333333333333", "services"),
        Node("44444444-4444-4444-4444-444444444444", "services"),
    ];

    private static readonly CommunicationEdge[] Edges =
    [
        new("one", Nodes[0].Id, Nodes[2].Id, Mechanism.Tcp, 5, 10, RelationStrength.Correlated) { Rule = RelationRule.TransportEndpoint, Evidence = [] },
        new("two", Nodes[1].Id, Nodes[3].Id, Mechanism.Tcp, 8, 16, RelationStrength.Correlated) { Rule = RelationRule.TransportEndpoint, Evidence = [] },
    ];

    [Fact(DisplayName = "R12: graph layout is identical after node, group and edge enumeration is reversed")]
    public void LayoutDoesNotDependOnInputEnumeration()
    {
        GraphLayoutResult first = GraphLayout.Compute("graph-v1", Groups, Nodes, Edges);
        GraphLayoutResult reversed = GraphLayout.Compute(
            "graph-v1", [.. Groups.Reverse()], [.. Nodes.Reverse()], [.. Edges.Reverse()]);

        Assert.Equal(GraphLayout.RelaxationIterations, first.Iterations);
        Assert.Equal(first.Energy, reversed.Energy);
        foreach (ProcessNode node in Nodes)
        {
            Assert.Equal(first.Positions[node.Id], reversed.Positions[node.Id]);
            Assert.True(first.Positions[node.Id].IsValid);
        }
    }

    [Fact(DisplayName = "R12: a pinned node is a hard constraint, while an existing node starts at its prior position")]
    public void PinsAreHardAndPreviousPositionsSeedTheLayout()
    {
        var pin = new GraphPoint(0.91, 0.72);
        GraphLayoutResult result = GraphLayout.Compute("graph-v1", Groups, Nodes, Edges,
            pins: new Dictionary<ProcessInstanceId, GraphPoint> { [Nodes[0].Id] = pin });
        Assert.Equal(pin, result.Positions[Nodes[0].Id]);

        // Laid out again from where it was drawn, under a new identity and so a new seed, a graph stays where it was:
        // it may settle by a few pixels (0.03 of the pane's height is about 8 logical px), never rearrange.
        GraphLayoutResult first = GraphLayout.Compute("graph-v1", Groups, Nodes, Edges);
        GraphLayoutResult again = GraphLayout.Compute("graph-v2", Groups, Nodes, Edges,
            previous: first.Positions.ToDictionary(entry => entry.Key, entry => entry.Value));
        Assert.All(Nodes, node => Assert.InRange(Distance(first.Positions[node.Id], again.Positions[node.Id]), 0, 0.03));
    }

    [Fact(DisplayName = "R12: related processes are drawn together, separate components apart, and none is omitted")]
    public void RelationshipsDecidePlacement()
    {
        // A hub with four peers in one executable group, and an unrelated pair that shares the hub's group.
        ProcessNode hub = Node(GuidFrom(1), "services");
        ProcessNode[] peers = [.. Enumerable.Range(2, 4).Select(i => Node(GuidFrom(i), "clients"))];
        ProcessNode[] pair = [Node(GuidFrom(10), "services"), Node(GuidFrom(11), "clients")];
        CommunicationEdge[] edges =
        [
            .. peers.Select(peer => new CommunicationEdge($"hub-{peer.Id}", peer.Id, hub.Id, Mechanism.Tcp, 5, null,
                RelationStrength.Direct) { Rule = RelationRule.TransportEndpoint, Evidence = [] }),
            new("pair", pair[0].Id, pair[1].Id, Mechanism.Tcp, 5, null, RelationStrength.Direct) { Rule = RelationRule.TransportEndpoint, Evidence = [] },
        ];
        ProcessNode[] all = [hub, .. peers, .. pair];

        GraphLayoutResult result = GraphLayout.Compute("star", Groups, all, edges);

        Assert.Equal(all.Length, result.Positions.Count);
        double farthestPeer = peers.Max(peer => Distance(result.Positions[hub.Id], result.Positions[peer.Id]));
        double nearestStranger = pair.Min(other => Distance(result.Positions[hub.Id], result.Positions[other.Id]));
        Assert.True(farthestPeer < nearestStranger, $"hub peers up to {farthestPeer:F3} away, the pair {nearestStranger:F3}");
        Assert.True(Distance(result.Positions[pair[0].Id], result.Positions[pair[1].Id])
            < peers.Min(peer => Distance(result.Positions[pair[0].Id], result.Positions[peer.Id])));
    }

    [Fact(DisplayName = "§19.4: a node with no drawn edge is parked below the related nodes, never across an edge")]
    public void EdgelessNodesAreParkedApart()
    {
        ProcessNode[] quiet = [Node(GuidFrom(20), "clients"), Node(GuidFrom(21), "services")];
        GraphLayoutResult result = GraphLayout.Compute("parked", Groups, [.. Nodes, .. quiet], Edges);

        double lowestRelated = Nodes.Max(node => result.Positions[node.Id].Y);
        Assert.All(quiet, node => Assert.True(result.Positions[node.Id].Y > lowestRelated + 0.05));
        Assert.Equal(result.Positions[quiet[0].Id].Y, result.Positions[quiet[1].Id].Y);

        // With nothing related, the parked row sits across the middle of the pane.
        GraphLayoutResult alone = GraphLayout.Compute("alone", Groups, quiet, []);
        Assert.All(quiet, node => Assert.Equal(0.5, alone.Positions[node.Id].Y));
    }

    [Fact(DisplayName = "§19.4: a node that joins a drawn graph appears beside its peer while the rest keep their places")]
    public void AJoiningNodeSettlesBesideItsPeer()
    {
        GraphLayoutResult before = GraphLayout.Compute("graph-v1", Groups, Nodes, Edges);
        ProcessNode newcomer = Node(GuidFrom(30), "clients");
        CommunicationEdge joined = new("three", newcomer.Id, Nodes[3].Id, Mechanism.Tcp, 5, null, RelationStrength.Direct) { Rule = RelationRule.TransportEndpoint, Evidence = [] };

        GraphLayoutResult after = GraphLayout.Compute("graph-v2", Groups, [.. Nodes, newcomer], [.. Edges, joined],
            previous: before.Positions.ToDictionary(entry => entry.Key, entry => entry.Value));

        Assert.All(Nodes, node => Assert.InRange(Distance(before.Positions[node.Id], after.Positions[node.Id]), 0, 0.1));
        double beside = Distance(after.Positions[newcomer.Id], after.Positions[Nodes[3].Id]);
        Assert.True(Nodes.Where(node => node.Id != Nodes[3].Id)
            .All(node => Distance(after.Positions[newcomer.Id], after.Positions[node.Id]) > beside));
    }

    [Fact(DisplayName = "§6.3: a settled refresh moves the nodes drawn before no further than a nudge however many join them, where a new drawing of the same graph moves them further")]
    public void ASettledRefreshHoldsTheNodesDrawnBefore()
    {
        // A hub related to four processes; then twelve more related to them, three times as many nodes as were drawn.
        ProcessGroup tools = new("tools", "tool.exe", LaneGrouping.Executable);
        ProcessNode[] processes = [.. Enumerable.Range(1, 17).Select(index => new ProcessNode(
            new ProcessInstanceId(Guid.Parse($"00000000-0000-0000-0000-{index:D12}")), 2_000 + index, $"Process {index}", "test",
            tools.Key, 0.5, 0.5, CoverageState.Covered) { Activity = [new(Mechanism.Tcp, 5)] })];
        CommunicationEdge[] first = [.. Enumerable.Range(1, 4).Select(index => Related($"hub-{index}", processes[0], processes[index]))];
        CommunicationEdge[] grown = [.. first,
            .. Enumerable.Range(5, 12).Select(index => Related($"more-{index}", processes[index % 5], processes[index]))];
        GraphDisplayLayout seen = GraphLayout.ComputeDisplay("generation-1", GraphProjection.Project(Workspace([tools], processes[..5], first)));
        GraphDisplay larger = GraphProjection.Project(Workspace([tools], processes, grown));

        GraphDisplayLayout held = GraphLayout.ComputeDisplay("generation-2", larger, seen.Positions, settled: true);
        GraphDisplayLayout redrawn = GraphLayout.ComputeDisplay("generation-2", larger, seen.Positions);
        double Farthest(GraphDisplayLayout layout) => seen.Positions.Keys.Max(key => Math.Sqrt(
            Math.Pow(seen.Positions[key].X - layout.Positions[key].X, 2) + Math.Pow(seen.Positions[key].Y - layout.Positions[key].Y, 2)));
        Assert.InRange(Farthest(held), 0, 0.1);
        Assert.True(Farthest(redrawn) > 0.1, $"A new drawing moved the nodes drawn before at most {Farthest(redrawn):F3}.");
    }

    [Fact(DisplayName = "§6.3: a dense component's discs stand apart, each clear of every other by the clearance, where the pane has room")]
    public void ADenseComponentsDiscsStandApart()
    {
        GraphDisplay display = Dense(60);
        GraphDisplayLayout layout = GraphLayout.ComputeDisplay("dense", display);
        AssertApart(display, layout.Positions, Clearance);
    }

    [Fact(DisplayName = "§6.3: discs part round a pinned node, which keeps its place")]
    public void DiscsPartRoundAPin()
    {
        GraphDisplay display = Dense(60);
        var pin = new GraphPoint(0.5, 0.5);
        GraphDisplayLayout layout = GraphLayout.ComputeDisplay("dense", display,
            pins: new Dictionary<string, GraphPoint> { [display.Nodes[0].Key] = pin });
        Assert.Equal(pin, layout.Positions[display.Nodes[0].Key]);
        AssertApart(display, layout.Positions, Clearance);
    }

    [Fact(DisplayName = "§6.3: discs that join a settled drawing make room round the nodes drawn before, which give way only a little")]
    public void JoiningDiscsMakeRoomRoundTheNodesDrawnBefore()
    {
        // A third as many again join a dense drawing, each related to three of its nodes.
        GraphDisplay seen = Dense(60);
        GraphDisplayLayout before = GraphLayout.ComputeDisplay("generation-1", seen);
        GraphDisplay grown = Dense(80);
        GraphDisplayLayout after = GraphLayout.ComputeDisplay("generation-2", grown, before.Positions, settled: true);
        AssertApart(grown, after.Positions, 0);
        Assert.All(seen.Nodes, node => Assert.InRange(Distance(before.Positions[node.Key], after.Positions[node.Key]), 0, 0.3));

        // A new drawing of the same graph lets them give way as a new node does, and parts every disc by the clearance.
        AssertApart(grown, GraphLayout.ComputeDisplay("generation-2", grown, before.Positions).Positions, Clearance);
    }

    [Fact(DisplayName = "§19.4: the first frame is where the layout starts, the nodes drawn before at their places, neither relaxed nor parted")]
    public void TheFirstFrameIsNotParted()
    {
        GraphDisplay display = Dense(60);
        Dictionary<string, GraphPoint> previous = GraphLayout.ComputeDisplay("dense", display).Positions
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        previous[display.Nodes[1].Key] = previous[display.Nodes[0].Key];
        IReadOnlyDictionary<string, GraphPoint> first = GraphLayout.SeedDisplay("dense-2", display, previous);
        Assert.All(previous, entry =>
        {
            Assert.Equal(entry.Value.X, first[entry.Key].X, 12);
            Assert.Equal(entry.Value.Y, first[entry.Key].Y, 12);
        });
    }

    /// <summary>Space a layout keeps between two discs beyond their radii, in drawn units, TUNABLE.</summary>
    private const double Clearance = 0.03;

    private static readonly int[] RingSteps = [7, 17, 29];

    [Fact(DisplayName = "§19.4: a lone component is drawn about the pane's middle, not toward the side its seeding leaned")]
    public void ALoneComponentIsDrawnAboutTheMiddle()
    {
        foreach (int ring in (int[])[12, 20, 60])
        {
            GraphDisplay display = Dense(ring, ring);
            IReadOnlyDictionary<string, GraphPoint> positions = GraphLayout.ComputeDisplay($"ring-{ring}", display).Positions;
            Assert.InRange(display.Nodes.Average(node => positions[node.Key].X), 0.46, 0.54);
        }
    }

    [Fact(DisplayName = "§19.4: a component alone in the seeding grid's short last row starts about its middle")]
    public void AShortLastRowStartsAboutTheMiddle()
    {
        // Four rings of eight fill the grid's first row; a related pair, the smallest component, has the second to itself.
        GraphDisplay display = Rings(8, 8, 8, 8, 2);
        IReadOnlyDictionary<string, GraphPoint> positions = GraphLayout.ComputeDisplay("rings", display).Positions;
        ProcessInstanceId[] ends = [Numbered(232).Id, Numbered(233).Id];
        GraphDisplayNode[] pair = [.. display.Nodes.Where(node => node.Process is { } process && ends.Contains(process))];
        Assert.Equal(2, pair.Length);
        Assert.InRange(pair.Average(node => positions[node.Key].X), 0.35, 0.65);
    }

    private static readonly ProcessGroup[] Programs = [.. Enumerable.Range(0, 12)
        .Select(index => new ProcessGroup($"program-{index}", $"program{index}.exe", LaneGrouping.Executable))];

    private static ProcessNode Numbered(int index) => new(
        new ProcessInstanceId(Guid.Parse(GuidFrom(index))), 4_000 + (4 * index), $"Process {index}", "test",
        Programs[index % Programs.Length].Key, 0.5, 0.5, CoverageState.Covered) { Activity = [new(Mechanism.Tcp, 5)] };

    private static CommunicationEdge Weighted(string key, ProcessNode source, ProcessNode target, int weight) =>
        new(key, source.Id, target.Id, Mechanism.Tcp, weight, null, RelationStrength.Direct) { Rule = RelationRule.TransportEndpoint, Evidence = [] };

    /// <summary>
    /// Separate rings of the given sizes, each a component of its own: every process related to the next round its ring
    /// and to the one three on.
    /// </summary>
    private static GraphDisplay Rings(params int[] sizes)
    {
        var processes = new List<ProcessNode>();
        var edges = new List<CommunicationEdge>();
        foreach (int size in sizes)
        {
            int start = processes.Count;
            processes.AddRange(Enumerable.Range(start, size).Select(index => Numbered(200 + index)));
            edges.AddRange(Enumerable.Range(0, size).SelectMany(index => ((int[])[1, 3])
                .Where(step => (index + step) % size != index)
                .Select(step => Weighted($"ring-{start + index}-{step}", processes[start + index],
                    processes[start + ((index + step) % size)], 1 + ((start + index) * step % 13)))));
        }

        return GraphProjection.Project(Workspace(Programs, processes, edges));
    }

    /// <summary>
    /// <paramref name="count"/> processes of twelve programs, the first <paramref name="ring"/> each related to three others
    /// round their ring at varied weights, and any more each related to three of them: relaxation alone settles them
    /// nearer than their discs are wide.
    /// </summary>
    private static GraphDisplay Dense(int count, int ring = 60)
    {
        ProcessNode[] processes = [.. Enumerable.Range(0, count).Select(index => Numbered(100 + index))];
        CommunicationEdge[] edges = [.. Enumerable.Range(0, count).SelectMany(index => RingSteps.Select(step => Weighted(
            $"ring-{index}-{step}", processes[index], processes[(index + step) % ring], 1 + (index * step % 13))))];
        return GraphProjection.Project(Workspace(Programs, processes, edges));
    }

    /// <summary>
    /// No two of <paramref name="display"/>'s discs, at the radii the pane draws them - §6.3's logical pixels, 250 to a
    /// drawn unit at the design pane's height - nearer than both radii and <paramref name="clearance"/>.
    /// </summary>
    private static void AssertApart(GraphDisplay display, IReadOnlyDictionary<string, GraphPoint> positions, double clearance)
    {
        long scale = GraphEncoding.NodeScale(display);
        for (int i = 0; i < display.Nodes.Count; i++)
        {
            for (int j = i + 1; j < display.Nodes.Count; j++)
            {
                GraphDisplayNode one = display.Nodes[i];
                GraphDisplayNode other = display.Nodes[j];
                double apart = ((GraphEncoding.NodeRadius(one, scale) + GraphEncoding.NodeRadius(other, scale)) / 250) + clearance;
                double distance = Distance(positions[one.Key], positions[other.Key]);
                Assert.True(distance >= apart - 1e-3, $"{one.Key} and {other.Key} stand {distance:F4} apart of {apart:F4}");
            }
        }
    }

    private static CommunicationEdge Related(string key, ProcessNode source, ProcessNode target) =>
        new(key, source.Id, target.Id, Mechanism.Tcp, 10, null, RelationStrength.Direct) { Rule = RelationRule.TransportEndpoint, Evidence = [] };

    private static WorkspaceSnapshot Workspace(
        IReadOnlyList<ProcessGroup> groups, IReadOnlyList<ProcessNode> processes, IReadOnlyList<CommunicationEdge> edges) =>
        new("Session", new TimeRange(0, 10), groups, processes, edges, [], [], [], []);

    [Fact(DisplayName = "R8: malformed layout input or a graph over budget is refused, never partly drawn")]
    public void InvalidInputIsRefused()
    {
        Assert.Throws<ArgumentException>(() => GraphLayout.Compute("graph", Groups, [Nodes[0], Nodes[0]], []));
        Assert.Throws<ArgumentException>(() => GraphLayout.Compute("graph", Groups, [Nodes[0]], Edges));
        Assert.Throws<ArgumentException>(() => GraphLayout.Compute("graph", Groups, [Nodes[0]], [],
            pins: new Dictionary<ProcessInstanceId, GraphPoint> { [Nodes[0].Id] = new(double.NaN, 0) }));
        Assert.Throws<InvalidOperationException>(() => GraphLayout.Compute("graph", Groups,
            Enumerable.Range(0, GraphLayout.MaximumNodes + 1)
                .Select(i => Node(GuidFrom(i), "clients")).ToArray(), []));
    }

    [Fact(DisplayName = "R8: cancelled layouts do not publish a partial result")]
    public void CancelledLayoutDoesNotReturn()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => GraphLayout.Compute(
            "graph", Groups, Nodes, Edges, cancellationToken: cancellation.Token));
    }

    private static ProcessNode Node(string id, string group) =>
        new(new ProcessInstanceId(Guid.Parse(id)), 10, "process", "test", group, 0.5, 0.5, CoverageState.Covered);

    private static string GuidFrom(int value) => $"00000000-0000-0000-0000-{value:D12}";

    /// <summary>Distance as the pane draws it: graph X spans the design aspect's width, Y one height.</summary>
    private static double Distance(GraphPoint left, GraphPoint right) =>
        Math.Sqrt(Math.Pow((left.X - right.X) * GraphLayout.DesignAspect, 2) + Math.Pow(left.Y - right.Y, 2));
}
