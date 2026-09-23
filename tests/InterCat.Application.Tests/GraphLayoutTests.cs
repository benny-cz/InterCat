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

        GraphLayoutResult one = GraphLayout.Compute("one", Groups, [Nodes[0]], [],
            previous: new Dictionary<ProcessInstanceId, GraphPoint>
            {
                [Nodes[0].Id] = new(0.37, 0.46),
            });
        Assert.Equal(new GraphPoint(0.37, 0.46), one.Positions[Nodes[0].Id]);

        GraphLayoutResult regrouped = GraphLayout.Compute("regrouped", Groups, Nodes, Edges,
            previous: new Dictionary<ProcessInstanceId, GraphPoint>
            {
                [Nodes[2].Id] = new(0.37, 0.1),
            });
        Assert.InRange(regrouped.Positions[Nodes[2].Id].Y, 0.5, 1);
    }

    [Fact(DisplayName = "R12: groups occupy stable separate bands and no process is silently omitted")]
    public void GroupsHaveStableBands()
    {
        GraphLayoutResult result = GraphLayout.Compute("graph-v1", Groups, Nodes, Edges);
        Assert.Equal(Nodes.Length, result.Positions.Count);
        Assert.All(Nodes.Where(node => node.GroupKey == "clients"), node =>
            Assert.InRange(result.Positions[node.Id].Y, 0, 0.5));
        Assert.All(Nodes.Where(node => node.GroupKey == "services"), node =>
            Assert.InRange(result.Positions[node.Id].Y, 0.5, 1));
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
}
