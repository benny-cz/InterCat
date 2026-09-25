using InterCat.Application;
using InterCat.Domain;
using Xunit;

namespace InterCat.Application.Tests;

public sealed class GraphProjectionTests
{
    [Fact(DisplayName = "§6.3: a 534-process overview opens clustered without omitting a process")]
    public void LargeOverviewFitsDisplayAndLayoutBudgets()
    {
        ProcessGroup[] groups = [.. Enumerable.Range(0, 6)
            .Select(index => new ProcessGroup($"g{index}", $"Group {index}", LaneGrouping.Executable))];
        ProcessNode[] processes = [.. Enumerable.Range(0, 534)
            .Select(index => Node(index + 1, groups[index % groups.Length].Key))];

        // Every process talks to the next member of its own group, so all of them are drawn rather than counted as quiet.
        CommunicationEdge[] edges = [.. Enumerable.Range(0, processes.Length - groups.Length)
            .Select(index => Edge($"e{index}", processes[index], processes[index + groups.Length], 1))];
        WorkspaceSnapshot snapshot = Snapshot(groups, processes, edges);

        GraphDisplay display = GraphProjection.Project(snapshot);

        Assert.InRange(display.Nodes.Count, 1, GraphDisplayBudget.Default.Nodes);
        Assert.InRange(display.Edges.Count, 0, GraphDisplayBudget.Default.Edges);
        Assert.Equal(534, display.Nodes.Sum(node => node.Members.Count));
        Assert.Equal(534, processes.Count(process => display.NodeOf(process.Id) is not null));
        Assert.All(display.Nodes, node => Assert.Equal(GraphNodeKind.Group, node.Kind));
        Assert.All(display.Nodes, node => Assert.Equal(89, node.Members.Count));
        Assert.All(edges, edge => Assert.NotNull(display.NodeOfRelationship(edge.Key)));

        GraphDisplayLayout layout = GraphLayout.ComputeDisplay("large-overview", display);
        Assert.Equal(display.Nodes.Count, layout.Positions.Count);
    }

    [Fact(DisplayName = "§6.3: processes with no relationship are counted in one node, not scattered as isolated circles")]
    public void ProcessesWithoutRelationshipsAreCountedInOneNode()
    {
        ProcessGroup busy = new("busy", "busy.exe", LaneGrouping.Executable);
        ProcessGroup idle = new("idle", "idle.exe", LaneGrouping.Executable);
        ProcessNode[] talking = [Node(1, busy.Key), Node(2, busy.Key)];
        ProcessNode[] silent = [.. Enumerable.Range(3, 530).Select(index => Node(index, index % 2 == 0 ? busy.Key : idle.Key))];
        CommunicationEdge edge = Edge("pair", talking[0], talking[1], 89);

        GraphDisplay display = GraphProjection.Project(Snapshot([busy, idle], [.. talking, .. silent], [edge]));

        Assert.Equal(3, display.Nodes.Count);
        Assert.All(talking, process => Assert.Equal(GraphNodeKind.Process, display.NodeOf(process.Id)!.Kind));
        GraphDisplayNode quiet = Assert.Single(display.Nodes, node => node.Kind == GraphNodeKind.Quiet);
        Assert.Equal(silent.Length, quiet.Members.Count);
        Assert.Equal((0, 0L), (quiet.Relationships, quiet.Observations));
        Assert.Null(quiet.GroupKey);
        Assert.DoesNotContain(display.Edges, drawn => drawn.SourceKey == quiet.Key || drawn.TargetKey == quiet.Key);
        Assert.Same(display.Edges[0], display.EdgeOf("pair"));
    }

    [Fact(DisplayName = "§6.3: the focus keeps its quiet members apart: the kept process stays drawn, the opened group's go to Other members")]
    public void QuietProcessesInTheFocusStayAddressable()
    {
        ProcessGroup opened = new("opened", "opened.exe", LaneGrouping.Executable);
        ProcessGroup other = new("other", "other.exe", LaneGrouping.Executable);
        ProcessNode[] members = [.. Enumerable.Range(1, 5).Select(index => Node(index, opened.Key))];
        ProcessNode[] outside = [.. Enumerable.Range(10, 4).Select(index => Node(index, other.Key))];
        CommunicationEdge edge = Edge("inside", members[0], members[1], 5);
        ProcessInstanceId kept = members[4].Id;

        GraphDisplay display = GraphProjection.Project(
            Snapshot([opened, other], [.. members, .. outside], [edge]), expandedGroup: opened.Key, kept: kept);

        Assert.Equal(GraphNodeKind.Process, display.NodeOf(kept)!.Kind);
        GraphDisplayNode otherMembers = Assert.Single(display.Nodes, node => node.Kind == GraphNodeKind.OtherMembers);
        Assert.Equal([members[2].Id, members[3].Id], otherMembers.Members);
        Assert.Equal(opened.Key, otherMembers.GroupKey);
        GraphDisplayNode quiet = Assert.Single(display.Nodes, node => node.Kind == GraphNodeKind.Quiet);
        Assert.Equal(Sorted(outside.Select(process => process.Id)), Sorted(quiet.Members));
    }

    [Fact(DisplayName = "§6.3: a lone quiet process is drawn as itself; a one-member aggregate would only hide its name")]
    public void ALoneQuietProcessIsNotFolded()
    {
        ProcessGroup group = new("g", "g.exe", LaneGrouping.Executable);
        ProcessNode[] processes = [Node(1, group.Key), Node(2, group.Key), Node(3, group.Key)];

        GraphDisplay display = GraphProjection.Project(
            Snapshot([group], processes, [Edge("pair", processes[0], processes[1], 1)]));

        Assert.All(display.Nodes, node => Assert.Equal(GraphNodeKind.Process, node.Kind));
        Assert.False(display.IsClustered);
    }

    [Fact(DisplayName = "§6.3: edge pressure collapses only the shortest lowest-metric prefix of groups that fits")]
    public void EdgePressureCollapsesOnlyWhatItMust()
    {
        ProcessGroup quiet = new("a-quiet", "Quiet", LaneGrouping.Executable);
        ProcessGroup middle = new("b-middle", "Middle", LaneGrouping.Executable);
        ProcessGroup loud = new("c-loud", "Loud", LaneGrouping.Executable);
        ProcessNode[] a = [Node(1, quiet.Key), Node(2, quiet.Key), Node(3, quiet.Key)];
        ProcessNode[] b = [Node(4, middle.Key), Node(5, middle.Key), Node(6, middle.Key)];
        ProcessNode[] c = [Node(7, loud.Key), Node(8, loud.Key)];

        // Three edges from each quiet member to the middle group, and a busy pair inside the loud group.
        CommunicationEdge[] edges =
        [
            .. a.SelectMany((source, i) => b.Select((target, j) => Edge($"ab-{i}-{j}", source, target, 1))),
            Edge("loud", c[0], c[1], 1_000),
        ];

        // Ten drawn edges; a budget of four is met by collapsing the quiet group alone (three edges, one per middle member).
        GraphDisplay display = GraphProjection.Project(
            Snapshot([quiet, middle, loud], [.. a, .. b, .. c], edges), budget: new GraphDisplayBudget(20, 4, 25));

        Assert.Equal(GraphNodeKind.Group, display.NodeOf(a[0].Id)!.Kind);
        Assert.All(b.Concat(c), process => Assert.Equal(GraphNodeKind.Process, display.NodeOf(process.Id)!.Kind));
        Assert.Equal(4, display.Edges.Count);
        Assert.All(edges, edge => Assert.NotNull(display.EdgeOf(edge.Key)));
    }

    [Fact(DisplayName = "§6.3: the cross-group remainder never absorbs the no-relationship node or the focused process")]
    public void RemainderKeepsQuietAndFocusApart()
    {
        ProcessGroup[] groups = [.. Enumerable.Range(0, 10)
            .Select(index => new ProcessGroup($"g{index:D2}", $"g{index}.exe", LaneGrouping.Executable))];
        ProcessNode[] pairs = [.. groups.SelectMany((group, index) => new[] { Node((index * 2) + 1, group.Key), Node((index * 2) + 2, group.Key) })];
        ProcessNode[] silent = [Node(100, groups[0].Key), Node(101, groups[1].Key)];

        // Each group's pair talks across to the next group, so no group collapse makes any edge internal.
        CommunicationEdge[] edges = [.. Enumerable.Range(0, pairs.Length - 1)
            .Select(index => Edge($"e{index:D2}", pairs[index], pairs[index + 1], index + 1))];
        ProcessInstanceId kept = pairs[0].Id;

        GraphDisplay display = GraphProjection.Project(
            Snapshot(groups, [.. pairs, .. silent], edges), kept: kept, budget: new GraphDisplayBudget(6, 50, 25));

        Assert.InRange(display.Nodes.Count, 1, 6);
        Assert.Equal(pairs.Length + silent.Length, display.Nodes.Sum(node => node.Members.Count));
        Assert.Equal(GraphNodeKind.Process, display.NodeOf(kept)!.Kind);
        GraphDisplayNode quiet = Assert.Single(display.Nodes, node => node.Kind == GraphNodeKind.Quiet);
        Assert.Equal(Sorted(silent.Select(process => process.Id)), Sorted(quiet.Members));
        Assert.Single(display.Nodes, node => node.Kind == GraphNodeKind.Remainder);
    }

    [Fact(DisplayName = "§6.3: when the node budget is tight, the lowest-metric group collapses first")]
    public void LowestMetricGroupCollapsesFirst()
    {
        ProcessGroup low = new("low", "Low", LaneGrouping.Executable);
        ProcessGroup medium = new("medium", "Medium", LaneGrouping.Executable);
        ProcessGroup high = new("high", "High", LaneGrouping.Executable);
        ProcessNode[] processes =
        [
            Node(1, low.Key), Node(2, low.Key),
            Node(3, medium.Key), Node(4, medium.Key),
            Node(5, high.Key), Node(6, high.Key),
        ];
        CommunicationEdge[] edges =
        [
            Edge("low-edge", processes[0], processes[1], 1),
            Edge("medium-edge", processes[2], processes[3], 10),
            Edge("high-edge", processes[4], processes[5], 100),
        ];

        GraphDisplay display = GraphProjection.Project(
            Snapshot([low, medium, high], processes, edges),
            budget: new GraphDisplayBudget(5, 20, 10));

        GraphDisplayNode lowNode = Assert.Single(display.Nodes, node => node.GroupKey == low.Key);
        Assert.Equal(GraphNodeKind.Group, lowNode.Kind);
        Assert.Equal(2, lowNode.Members.Count);
        Assert.All(processes.Where(process => process.GroupKey != low.Key),
            process => Assert.Equal(GraphNodeKind.Process, display.NodeOf(process.Id)!.Kind));
    }

    [Fact(DisplayName = "§6.3: an oversized opened group keeps busy members and folds the low end explicitly")]
    public void ExpandedGroupStaysBoundedWithoutTruncation()
    {
        ProcessGroup hot = new("hot", "Hot workers", LaneGrouping.Executable);
        ProcessGroup context = new("context", "Context", LaneGrouping.Executable);
        ProcessNode[] hotProcesses = [.. Enumerable.Range(1, 30).Select(index => Node(index, hot.Key))];
        ProcessNode[] contextProcesses = [Node(100, context.Key), Node(101, context.Key)];
        ProcessNode[] processes = [.. hotProcesses, .. contextProcesses];
        CommunicationEdge[] edges =
        [
            Edge("busy-1", hotProcesses[0], hotProcesses[1], 500),
            Edge("busy-2", hotProcesses[1], hotProcesses[2], 400),
            Edge("context", hotProcesses[0], contextProcesses[0], 50),
        ];

        GraphDisplay display = GraphProjection.Project(
            Snapshot([hot, context], processes, edges),
            expandedGroup: hot.Key,
            budget: new GraphDisplayBudget(10, 20, 5));

        Assert.True(display.Nodes.Count <= 10);
        Assert.Equal(processes.Length, display.Nodes.Sum(node => node.Members.Count));
        Assert.Equal(GraphNodeKind.Process, display.NodeOf(hotProcesses[0].Id)!.Kind);
        Assert.Equal(GraphNodeKind.Process, display.NodeOf(hotProcesses[1].Id)!.Kind);
        Assert.Equal(GraphNodeKind.Process, display.NodeOf(hotProcesses[2].Id)!.Kind);
        GraphDisplayNode folded = Assert.Single(display.Nodes, node => node.Kind == GraphNodeKind.OtherMembers);
        Assert.True(folded.Members.Count > 1);
        Assert.Equal(hot.Key, folded.GroupKey);
    }

    [Fact(DisplayName = "R13: collapsed edges preserve source identities and interval rescope changes counts, not membership")]
    public void AggregationAndRescopePreserveIdentity()
    {
        ProcessGroup left = new("left", "Left", LaneGrouping.Executable);
        ProcessGroup right = new("right", "Right", LaneGrouping.Executable);
        ProcessNode[] processes = [Node(1, left.Key), Node(2, left.Key), Node(3, right.Key), Node(4, right.Key)];
        CommunicationEdge[] edges =
        [
            Edge("e1", processes[0], processes[2], 5, RelationStrength.Direct),
            Edge("e2", processes[1], processes[3], 7, RelationStrength.Candidate),
            Edge("internal", processes[0], processes[1], 11, RelationStrength.Correlated),
        ];
        WorkspaceSnapshot snapshot = Snapshot([left, right], processes, edges);

        GraphDisplay display = GraphProjection.Project(snapshot, budget: new GraphDisplayBudget(2, 10, 10));

        GraphDisplayEdge drawn = Assert.Single(display.Edges);
        Assert.Equal(12, drawn.ObservationCount);
        Assert.Equal(RelationStrength.Candidate, drawn.Strength);
        Assert.Equal(["e1", "e2"], drawn.Relationships);
        Assert.Equal(drawn, display.EdgeOf("e1"));
        Assert.Equal(drawn, display.EdgeOf("e2"));
        Assert.Null(display.EdgeOf("internal"));
        Assert.NotNull(display.NodeOfRelationship("internal"));
        Assert.Contains("internal", display.RelationshipKeys);
        Assert.Contains(display.Nodes, node => node.InternalRelationships == 1 && node.Relationships == 3);

        WorkspaceSnapshot scoped = snapshot with
        {
            Edges =
            [
                edges[0] with { ObservationCount = 2 },
                edges[1] with { ObservationCount = 3 },
                edges[2] with { ObservationCount = 4 },
            ],
        };
        GraphDisplay recounted = GraphProjection.Rescope(display, snapshot, scoped);
        Assert.Equal(display.Nodes.Select(node => (node.Key, node.Members.Count)),
            recounted.Nodes.Select(node => (node.Key, node.Members.Count)));
        Assert.Equal(5, Assert.Single(recounted.Edges).ObservationCount);
        Assert.Contains(recounted.Nodes, node => node.InternalRelationships == 1 && node.Observations == 9);

        WorkspaceSnapshot changedStructure = scoped with
        {
            Edges =
            [
                scoped.Edges[0] with { SourceId = processes[1].Id },
                scoped.Edges[1],
                scoped.Edges[2],
            ],
        };
        Assert.Throws<ArgumentException>(() => GraphProjection.Rescope(display, snapshot, changedStructure));

        WorkspaceSnapshot changedStrength = scoped with
        {
            Edges =
            [
                scoped.Edges[0] with { Strength = RelationStrength.Correlated },
                scoped.Edges[1],
                scoped.Edges[2],
            ],
        };
        Assert.Throws<ArgumentException>(() => GraphProjection.Rescope(display, snapshot, changedStrength));
    }

    [Fact(DisplayName = "§6.3: singleton-heavy captures use an explicit remainder and preserve the focused process")]
    public void SingletonHeavyCaptureUsesRemainderWithoutOmission()
    {
        ProcessGroup[] groups = [.. Enumerable.Range(0, 12)
            .Select(index => new ProcessGroup($"g{index:D2}", $"Group {index}", LaneGrouping.Executable))];
        ProcessNode[] processes = [.. groups.Select((group, index) => Node(index + 1, group.Key))];
        ProcessInstanceId kept = processes[^1].Id;

        // A chain of relationships across the singleton groups: each process is drawn, and no group can collapse.
        CommunicationEdge[] chain = [.. Enumerable.Range(0, processes.Length - 1)
            .Select(index => Edge($"chain{index:D2}", processes[index], processes[index + 1], 1))];
        GraphDisplay display = GraphProjection.Project(
            Snapshot(groups, processes, chain),
            kept: kept,
            budget: new GraphDisplayBudget(5, 20, 25));

        Assert.Equal(processes.Length, display.Nodes.Sum(node => node.Members.Count));
        Assert.InRange(display.Nodes.Count, 1, 5);
        GraphDisplayNode remainder = Assert.Single(display.Nodes, node => node.Kind == GraphNodeKind.Remainder);
        Assert.True(remainder.Members.Count > 1);
        Assert.DoesNotContain(kept, remainder.Members);
        Assert.Equal(GraphNodeKind.Process, display.NodeOf(kept)!.Kind);
    }

    [Fact(DisplayName = "§6.3: edge pressure collapses groups until the drawn edge budget fits without losing identities")]
    public void EdgePressureCompactsRelationshipsWithoutLosingSourceKeys()
    {
        ProcessGroup left = new("left", "Left", LaneGrouping.Executable);
        ProcessGroup right = new("right", "Right", LaneGrouping.Executable);
        ProcessNode[] leftProcesses = [Node(1, left.Key), Node(2, left.Key), Node(3, left.Key)];
        ProcessNode[] rightProcesses = [Node(4, right.Key), Node(5, right.Key), Node(6, right.Key)];
        ProcessNode[] processes = [.. leftProcesses, .. rightProcesses];
        CommunicationEdge[] edges = [.. leftProcesses.SelectMany((source, sourceIndex) =>
            rightProcesses.Select((target, targetIndex) =>
                Edge($"e-{sourceIndex}-{targetIndex}", source, target, 1)))];

        GraphDisplay display = GraphProjection.Project(
            Snapshot([left, right], processes, edges),
            budget: new GraphDisplayBudget(10, 2, 10));

        Assert.InRange(display.Edges.Count, 1, 2);
        Assert.Equal(edges.Length, display.RelationshipKeys.Count);
        Assert.All(edges, edge => Assert.NotNull(display.EdgeOf(edge.Key)));
        Assert.Equal(processes.Length, display.Nodes.Sum(node => node.Members.Count));
    }

    [Fact(DisplayName = "R8: malformed graph identity or a display budget beyond layout safety is refused")]
    public void MalformedInputIsRefused()
    {
        ProcessGroup group = new("g", "Group", LaneGrouping.Executable);
        ProcessNode process = Node(1, group.Key);
        ProcessNode absent = Node(2, group.Key);
        WorkspaceSnapshot malformed = Snapshot([group], [process], [Edge("bad", process, absent, 1)]);

        Assert.Throws<ArgumentException>(() => GraphProjection.Project(malformed));
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphProjection.Project(
            Snapshot([group], [process], []),
            budget: new GraphDisplayBudget(GraphLayout.MaximumNodes + 1, 1, 2)));
    }

    [Fact(DisplayName = "§3.2: a focused rung draws its neighbourhood and counts everything else in one context node")]
    public void NeighbourhoodCountsTheRestAsContext()
    {
        ProcessGroup group = new("g", "g.exe", LaneGrouping.Executable);
        ProcessNode[] processes = [.. Enumerable.Range(1, 6).Select(index => Node(index, group.Key))];

        // 1-2 are the focus; 2-3 leads out of it; 4-5 talk among the outside; 6 is quiet and outside.
        CommunicationEdge[] edges =
        [
            Edge("focus", processes[0], processes[1], 5),
            Edge("out", processes[1], processes[2], 7),
            Edge("far", processes[3], processes[4], 9),
        ];
        HashSet<ProcessInstanceId> neighbourhood = [processes[0].Id, processes[1].Id];

        GraphDisplay display = GraphProjection.Project(Snapshot([group], processes, edges), neighborhood: neighbourhood);

        Assert.All(processes.Take(2), process => Assert.Equal(GraphNodeKind.Process, display.NodeOf(process.Id)!.Kind));
        GraphDisplayNode context = Assert.Single(display.Nodes, node => node.Kind == GraphNodeKind.Context);
        Assert.Equal(Sorted(processes.Skip(2).Select(process => process.Id)), Sorted(context.Members));
        Assert.Equal((2, 1), (context.Relationships, context.InternalRelationships));
        GraphDisplayEdge leadsOut = Assert.IsType<GraphDisplayEdge>(display.EdgeOf("out"));
        Assert.Contains(context.Key, new[] { leadsOut.SourceKey, leadsOut.TargetKey });
        Assert.Null(display.EdgeOf("far"));
        Assert.Equal(context.Key, display.NodeOfRelationship("far")!.Key);
        Assert.Equal(processes.Length, display.Nodes.Sum(node => node.Members.Count));

        // The quiet process outside the focus is context, not a separate no-relationship node.
        Assert.DoesNotContain(display.Nodes, node => node.Kind == GraphNodeKind.Quiet);

        // The rest of the machine holds the most here, yet sizes are read against the focus: the context node sets no
        // scale and is drawn at the aggregate floor, while the focus's busiest process is drawn at full size.
        Assert.Equal(16, context.Observations);
        long scale = GraphEncoding.NodeScale(display);
        Assert.Equal(12, scale);
        Assert.Equal(9, GraphEncoding.NodeRadius(context, scale));
        Assert.Equal(16, GraphEncoding.NodeRadius(display.NodeOf(processes[1].Id)!, scale));

        // The layout parks the context node: its edges are drawn, but they place nothing.
        GraphDisplayLayout layout = GraphLayout.ComputeDisplay("focus", display);
        Assert.True(layout.Positions[context.Key].Y > layout.Positions.Where(entry => entry.Key != context.Key).Max(entry => entry.Value.Y));

        Assert.Throws<ArgumentException>(() => GraphProjection.Project(
            Snapshot([group], processes, edges), kept: processes[5].Id, neighborhood: neighbourhood));
    }

    private static string[] Sorted(IEnumerable<ProcessInstanceId> ids) =>
        [.. ids.Select(id => id.ToString()).Order(StringComparer.Ordinal)];

    private static WorkspaceSnapshot Snapshot(
        IReadOnlyList<ProcessGroup> groups,
        IReadOnlyList<ProcessNode> processes,
        IReadOnlyList<CommunicationEdge> edges) =>
        new("Graph projection test", new TimeRange(0, 10), groups, processes, edges, [], [], [], []);

    private static ProcessNode Node(int index, string group) => new(
        new ProcessInstanceId(Guid.Parse($"00000000-0000-0000-0000-{index:D12}")),
        1_000 + index,
        $"Process {index}",
        "test",
        group,
        0.5,
        0.5,
        CoverageState.Covered);

    private static CommunicationEdge Edge(
        string key,
        ProcessNode source,
        ProcessNode target,
        long observations,
        RelationStrength strength = RelationStrength.Correlated) =>
        new(key, source.Id, target.Id, Mechanism.Tcp, observations, null, strength);
}
