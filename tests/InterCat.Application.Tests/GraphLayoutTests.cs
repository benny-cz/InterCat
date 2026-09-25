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
        new("one", Nodes[0].Id, Nodes[2].Id, Mechanism.Tcp, 5, 10, RelationStrength.Correlated),
        new("two", Nodes[1].Id, Nodes[3].Id, Mechanism.Tcp, 8, 16, RelationStrength.Correlated),
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
                RelationStrength.Direct)),
            new("pair", pair[0].Id, pair[1].Id, Mechanism.Tcp, 5, null, RelationStrength.Direct),
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
        CommunicationEdge joined = new("three", newcomer.Id, Nodes[3].Id, Mechanism.Tcp, 5, null, RelationStrength.Direct);

        GraphLayoutResult after = GraphLayout.Compute("graph-v2", Groups, [.. Nodes, newcomer], [.. Edges, joined],
            previous: before.Positions.ToDictionary(entry => entry.Key, entry => entry.Value));

        Assert.All(Nodes, node => Assert.InRange(Distance(before.Positions[node.Id], after.Positions[node.Id]), 0, 0.1));
        double beside = Distance(after.Positions[newcomer.Id], after.Positions[Nodes[3].Id]);
        Assert.True(Nodes.Where(node => node.Id != Nodes[3].Id)
            .All(node => Distance(after.Positions[newcomer.Id], after.Positions[node.Id]) > beside));
    }

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
