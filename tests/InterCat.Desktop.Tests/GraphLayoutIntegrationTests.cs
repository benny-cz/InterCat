using System.Globalization;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using Xunit;

namespace InterCat.Desktop.Tests;

public sealed class GraphLayoutIntegrationTests
{
    [Fact(DisplayName = "R13: a non-tour workspace labels its own extent and uses its own graph identity")]
    public async Task NonTourWorkspaceUsesItsOwnExtentAndGraphIdentity()
    {
        WorkspaceSnapshot snapshot = SyntheticWorkspace.Create() with
        {
            Title = "Imported session",
            Extent = new TimeRange(0, 42 * WorkspaceTime.TicksPerSecond),
        };
        using var viewModel = new WorkspaceViewModel(snapshot, "generation-42");
        await viewModel.LayoutReady;

        Assert.Equal(snapshot, viewModel.Snapshot);
        Assert.Equal($"All {42m.ToString("N1", CultureInfo.CurrentCulture)} s", viewModel.IntervalLabel);
        // A fresh workspace seeds on the layout's own lattice (§19.4); the snapshot's coordinates are not positions anyone saw.
        GraphLayoutResult expected = GraphLayout.Compute(
            "generation-42", snapshot.Groups, snapshot.Processes, snapshot.Edges);
        foreach (ProcessNode node in snapshot.Processes)
        {
            Assert.Equal(expected.Positions[node.Id], viewModel.GraphPositions[node.Id]);
        }

        viewModel.SelectInterval(new TimeRange(10 * WorkspaceTime.TicksPerSecond, 12 * WorkspaceTime.TicksPerSecond));
        Assert.Equal(
            $"{10m.ToString("N3", CultureInfo.CurrentCulture)} – {12m.ToString("N3", CultureInfo.CurrentCulture)} s",
            viewModel.IntervalLabel);
    }

    [Fact(DisplayName = "R13: an empty session workspace opens without a fabricated selected process")]
    public async Task EmptyWorkspaceHasNoFabricatedSelection()
    {
        WorkspaceSnapshot empty = SyntheticWorkspace.Create() with
        {
            Title = "Empty session",
            Groups = [], Processes = [], Edges = [], Channels = [], Operations = [], Evidence = [], Timeline = [],
        };
        using var viewModel = new WorkspaceViewModel(empty, "empty-generation");
        await viewModel.LayoutReady;

        Assert.Null(viewModel.SelectedProcess);
        Assert.Equal("Nothing selected", viewModel.SelectionTitle);
        Assert.Empty(viewModel.GraphPositions);
    }

    [Fact(DisplayName = "R12: desktop graph reads stable layout coordinates instead of evidence-model positions")]
    public async Task DesktopUsesSeparateGraphLayout()
    {
        using var viewModel = new WorkspaceViewModel();
        await viewModel.LayoutReady;
        GraphLayoutResult expected = GraphLayout.Compute(
            "synthetic-tour-v1", viewModel.Snapshot.Groups, viewModel.Snapshot.Processes, viewModel.Snapshot.Edges);

        Assert.Equal(viewModel.Snapshot.Processes.Count, viewModel.GraphPositions.Count);
        foreach (ProcessNode node in viewModel.Snapshot.Processes)
        {
            Assert.Equal(expected.Positions[node.Id], viewModel.GraphPositions[node.Id]);
        }

        Assert.Contains(viewModel.Snapshot.Processes, node =>
            viewModel.GraphPositions[node.Id] != new GraphPoint(node.X, node.Y));
    }

    [Fact(DisplayName = "§6.3: desktop opens a 534-process session clustered and expands one group without losing members")]
    public async Task LargeSessionOpensClusteredAndGroupExpansionStaysBounded()
    {
        ProcessGroup[] groups = [.. Enumerable.Range(0, 6)
            .Select(index => new ProcessGroup($"g{index}", $"Group {index}", LaneGrouping.Executable))];
        ProcessNode[] processes = [.. Enumerable.Range(0, 534).Select(index => Process(index + 1, groups[index % groups.Length].Key))];

        // Every process talks to the next member of its own group, so each is drawn rather than counted as quiet.
        CommunicationEdge[] edges = [.. Enumerable.Range(0, processes.Length - groups.Length)
            .Select(index => Edge($"e{index}", processes[index], processes[index + groups.Length]))];
        WorkspaceSnapshot snapshot = Snapshot(groups, processes, edges);

        using var viewModel = new WorkspaceViewModel(snapshot, "session:test:generation:1");
        await viewModel.LayoutReady;

        Assert.Null(viewModel.GraphLayoutProblem);
        Assert.True(viewModel.GraphDisplay.IsClustered);
        Assert.Equal(534, viewModel.GraphDisplay.Nodes.Sum(node => node.Members.Count));
        Assert.True(viewModel.GraphDisplay.Nodes.Count <= GraphDisplayBudget.Default.Nodes);
        GraphDisplayNode group = Assert.Single(viewModel.GraphDisplay.Nodes, node => node.GroupKey == groups[0].Key);
        Assert.Equal(GraphNodeKind.Group, group.Kind);
        viewModel.SelectGraphNode(group.Key);
        Assert.Equal(groups[0], viewModel.SelectedGroup);
        Assert.Equal(groups[0].Key, viewModel.SelectedRung?.Key);
        Assert.Contains("89 processes", viewModel.SelectionSubtitle, StringComparison.Ordinal);
        Assert.Contains("Enter opens the group", viewModel.SelectionSubtitle, StringComparison.Ordinal);
        Assert.Equal([group.Key], viewModel.SelectedGraphNodeKeys);

        Assert.True(viewModel.OpenGraphGroup(group.Key));
        await viewModel.LayoutReady;

        Assert.True(viewModel.GraphDisplay.Nodes.Count <= GraphDisplayBudget.Default.Nodes);
        Assert.All(processes.Where(process => process.GroupKey == groups[0].Key),
            process => Assert.Equal(GraphNodeKind.Process, viewModel.GraphDisplay.NodeOf(process.Id)!.Kind));
        Assert.Equal(534, viewModel.GraphDisplay.Nodes.Sum(node => node.Members.Count));
        GraphDisplayNode contextGroup = Assert.Single(viewModel.GraphDisplay.Nodes, node =>
            node.GroupKey == groups[1].Key && node.Kind == GraphNodeKind.Group);
        Assert.False(viewModel.OpenGraphGroup(contextGroup.Key));
    }

    [Fact(DisplayName = "§6.4: a selected group row rings where its members are drawn and scopes E to the group")]
    public async Task GroupRowSelectionRingsWhereItsMembersAreDrawn()
    {
        (WorkspaceSnapshot snapshot, ProcessGroup busy, ProcessNode[] processes) = Mixed();
        using var viewModel = new WorkspaceViewModel(snapshot, "session:test:generation:1");
        await viewModel.LayoutReady;
        string quiet = Assert.Single(viewModel.GraphDisplay.Nodes, node => node.Kind == GraphNodeKind.Quiet).Key;

        viewModel.SelectedRung = viewModel.RungRows.First(row => row.Key == busy.Key);

        Assert.Equal(busy, viewModel.SelectedGroup);
        Assert.Null(viewModel.SelectedProcess);
        Assert.Equal(busy.Name, viewModel.SelectionTitle);
        Assert.Equal(
            $"3 processes · 2 drawn on their own · 1 without a relationship{Environment.NewLine}{busy.Detail}",
            viewModel.SelectionSubtitle);
        Assert.Equal([processes[0].Id.ToString(), processes[1].Id.ToString()], viewModel.SelectedGraphNodeKeys.Order());
        Assert.Equal([quiet], viewModel.PartlySelectedGraphNodeKeys);
        Assert.Equal("Selected group", viewModel.EvidenceHeading);
        Assert.Equal("10 paired TCP observations on 1 relationship · bytes unknown", viewModel.EvidenceSummary);

        Assert.True(viewModel.ShowEvidence());
        Assert.Contains(viewModel.Filters, filter => filter.Label == busy.Name
            && filter.Reason.Contains("executable group selected", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "§6.4: a selected quiet process marks its aggregate as holding part of the selection, not as it")]
    public async Task SelectingAQuietProcessMarksItsAggregateAsPartlySelected()
    {
        (WorkspaceSnapshot snapshot, _, ProcessNode[] processes) = Mixed();
        using var viewModel = new WorkspaceViewModel(snapshot, "session:test:generation:1");
        await viewModel.LayoutReady;
        GraphDisplayNode quiet = Assert.Single(viewModel.GraphDisplay.Nodes, node => node.Kind == GraphNodeKind.Quiet);

        viewModel.SelectProcess(processes[2].Id);
        Assert.Empty(viewModel.SelectedGraphNodeKeys);
        Assert.Equal([quiet.Key], viewModel.PartlySelectedGraphNodeKeys);
        Assert.Equal(processes[2].Name, viewModel.SelectionTitle);
        Assert.Equal("Selected process", viewModel.EvidenceHeading);

        viewModel.SelectGraphNode(quiet.Key);
        Assert.Equal([quiet.Key], viewModel.SelectedGraphNodeKeys);
        Assert.Empty(viewModel.PartlySelectedGraphNodeKeys);
        Assert.Equal("No relationships", viewModel.SelectionTitle);
        Assert.Equal("Selected aggregate", viewModel.EvidenceHeading);
        Assert.StartsWith("3 processes with no admitted relationship", viewModel.SelectionSubtitle, StringComparison.Ordinal);
        Assert.Equal("5 processes · 1 relationship among 2 of them", viewModel.GraphSummary);
    }

    [Fact(DisplayName = "§6.3: a later publication of the session keeps each node that is still drawn where it was")]
    public async Task ALaterPublicationKeepsNodesWhereTheyWere()
    {
        (WorkspaceSnapshot snapshot, _, ProcessNode[] processes) = Mixed();
        using var first = new WorkspaceViewModel(snapshot, "session:test:generation:1");
        await first.LayoutReady;
        IReadOnlyDictionary<string, GraphPoint> seen = first.DisplayPositions;

        // The next generation adds a talking process to a drawn group; its identity, and so its lattice seed, differs.
        ProcessNode newcomer = Process(50, "busy");
        WorkspaceSnapshot next = snapshot with
        {
            Processes = [.. snapshot.Processes, newcomer],
            Edges = [.. snapshot.Edges, Edge("late", processes[1], newcomer)],
        };
        var layouts = new HeldLayouts { Holding = true };
        using var second = new WorkspaceViewModel(
            next, "session:test:generation:2", null, first.LaidOutPositions, layouts.Scheduler);

        // The first frame shows every surviving node where the user last saw it.
        foreach (string key in seen.Keys.Where(key => second.GraphDisplay.Node(key) is not null))
        {
            Assert.InRange(Distance(seen[key], second.DisplayPositions[key]), 0, 1e-9);
        }

        layouts.Release();
        await second.LayoutReady;
        Assert.Contains(newcomer.Id.ToString(), second.DisplayPositions.Keys);
        Assert.All(seen.Keys.Where(key => second.GraphDisplay.Node(key) is not null), key =>
            Assert.InRange(Distance(seen[key], second.DisplayPositions[key]), 0, 0.2));
    }

    [Fact(DisplayName = "§6.3: on the way back up, nodes on screen stay put and a group that reappears returns to its place")]
    public async Task AscendingKeepsNodesOnScreenAndReturnsAReappearingGroup()
    {
        ProcessGroup big = new("big", "big.exe", LaneGrouping.Executable);
        ProcessGroup small = new("small", "small.exe", LaneGrouping.Executable);
        ProcessNode[] members = [.. Enumerable.Range(1, 30).Select(index => Process(index, big.Key))];
        ProcessNode[] pair = [Process(40, small.Key), Process(41, small.Key)];
        CommunicationEdge[] edges =
        [
            .. Enumerable.Range(0, members.Length - 1).Select(index => Edge($"chain{index:D2}", members[index], members[index + 1])),
            Edge("pair", pair[0], pair[1]),
        ];
        var layouts = new HeldLayouts();
        using var viewModel = new WorkspaceViewModel(Snapshot([big, small], [.. members, .. pair], edges),
            "session:test:generation:1", null, null, layouts.Scheduler);
        await viewModel.LayoutReady;
        GraphDisplayNode collapsed = Assert.Single(viewModel.GraphDisplay.Nodes, node => node.Kind == GraphNodeKind.Group);
        GraphPoint collapsedAt = viewModel.DisplayPositions[collapsed.Key];

        viewModel.SelectedRung = viewModel.RungRows.First(row => row.Key == big.Key);
        Assert.True(viewModel.Descend());
        await viewModel.LayoutReady;
        Assert.Null(viewModel.GraphDisplay.Node(collapsed.Key));
        Dictionary<string, GraphPoint> opened = viewModel.DisplayPositions.ToDictionary(entry => entry.Key, entry => entry.Value);

        layouts.Holding = true;
        Assert.True(viewModel.Ascend());
        Assert.Equal(collapsedAt, viewModel.DisplayPositions[collapsed.Key]);
        Assert.All(pair, process => Assert.Equal(opened[process.Id.ToString()], viewModel.DisplayPositions[process.Id.ToString()]));
        layouts.Release();
        await viewModel.LayoutReady;
        Assert.Null(viewModel.GraphLayoutProblem);
    }

    /// <summary>A layout scheduler whose drawn-graph layouts wait while <see cref="Holding"/>, so a test reads the first frame.</summary>
    private sealed class HeldLayouts
    {
        private readonly TaskCompletionSource open = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HeldLayouts() => Scheduler = new(
            (request, token) => Task.Run(() => GraphLayout.Compute(request.GraphIdentity, request.Groups, request.Nodes,
                request.Edges, request.Previous, request.Pins, token), token),
            async (request, token) =>
            {
                if (Holding)
                {
                    await open.Task.WaitAsync(token);
                }

                return GraphLayout.ComputeDisplay(request.GraphIdentity, request.Display, request.Previous, request.Pins, token);
            });

        public GraphLayoutScheduler Scheduler { get; }

        public bool Holding { get; set; }

        public void Release()
        {
            Holding = false;
            open.TrySetResult();
        }
    }

    [Fact(DisplayName = "R7: a selected aggregate is still selected after a later publication of the session")]
    public async Task ASelectedAggregateSurvivesALaterPublication()
    {
        (WorkspaceSnapshot snapshot, _, _) = Mixed();
        using var first = new WorkspaceViewModel(snapshot, "session:test:generation:1");
        await first.LayoutReady;
        string quiet = Assert.Single(first.GraphDisplay.Nodes, node => node.Kind == GraphNodeKind.Quiet).Key;
        first.SelectGraphNode(quiet);

        using var second = new WorkspaceViewModel(snapshot, "session:test:generation:2", carriedLayout: first.LaidOutPositions);
        Assert.Null(second.RestoreNavigation(first.CaptureNavigation()));

        Assert.Equal(quiet, second.SelectedCluster?.Key);
        Assert.Equal("No relationships", second.SelectionTitle);
    }

    /// <summary>
    /// Five processes: two members of <c>busy.exe</c> talk to each other, its third member and two <c>idle.exe</c> processes
    /// have no relationship.
    /// </summary>
    private static (WorkspaceSnapshot Snapshot, ProcessGroup Busy, ProcessNode[] Processes) Mixed()
    {
        ProcessGroup busy = new("busy", "busy.exe", LaneGrouping.Executable, @"C:\Tools\busy.exe");
        ProcessGroup idle = new("idle", "idle.exe", LaneGrouping.Executable, @"C:\Tools\idle.exe");
        ProcessNode[] processes =
        [
            Process(1, busy.Key), Process(2, busy.Key), Process(3, busy.Key), Process(4, idle.Key), Process(5, idle.Key),
        ];
        return (Snapshot([busy, idle], processes, [Edge("pair", processes[0], processes[1])]), busy, processes);
    }

    private static WorkspaceSnapshot Snapshot(
        IReadOnlyList<ProcessGroup> groups, IReadOnlyList<ProcessNode> processes, IReadOnlyList<CommunicationEdge> edges) =>
        new("Session", new TimeRange(0, WorkspaceTime.TicksPerSecond), groups, processes, edges, [], [], [], []);

    private static ProcessNode Process(int index, string group) => new(
        new ProcessInstanceId(Guid.Parse($"00000000-0000-0000-0000-{index:D12}")),
        2_000 + index,
        $"Process {index}",
        "test",
        group,
        0.5,
        0.5,
        CoverageState.Covered);

    private static CommunicationEdge Edge(string key, ProcessNode source, ProcessNode target) =>
        new(key, source.Id, target.Id, Mechanism.Tcp, 10, null, RelationStrength.Direct);

    private static double Distance(GraphPoint left, GraphPoint right) =>
        Math.Sqrt(Math.Pow(left.X - right.X, 2) + Math.Pow(left.Y - right.Y, 2));
}
