using System.Globalization;
using Avalonia;
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

        // §3.2 L1 draws the opened group within its neighbourhood; the other groups' 445 processes are counted as context,
        // which neither opens nor claims to be a group.
        GraphDisplayNode context = Assert.Single(viewModel.GraphDisplay.Nodes, node => node.Kind == GraphNodeKind.Context);
        Assert.Equal(445, context.Members.Count);
        Assert.False(viewModel.OpenGraphGroup(context.Key));
        // Its members talk only among themselves, so the focus claims no peers.
        Assert.Equal("Group 0: 89 processes drawn · 445 more in Rest of the machine", viewModel.GraphSummary);
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

        // The pair's first member also talks to the big group, so the opened group's neighbourhood draws it; its partner is
        // two hops away and is counted as context there (§3.2 L1).
        CommunicationEdge[] edges =
        [
            .. Enumerable.Range(0, members.Length - 1).Select(index => Edge($"chain{index:D2}", members[index], members[index + 1])),
            Edge("pair", pair[0], pair[1]),
            Edge("bridge", members[0], pair[0]),
        ];
        var layouts = new HeldLayouts();
        using var viewModel = new WorkspaceViewModel(Snapshot([big, small], [.. members, .. pair], edges),
            "session:test:generation:1", null, null, layouts.Scheduler);
        await viewModel.LayoutReady;
        GraphDisplayNode collapsed = Assert.Single(viewModel.GraphDisplay.Nodes, node => node.Kind == GraphNodeKind.Group);
        Dictionary<string, GraphPoint> machine = viewModel.DisplayPositions.ToDictionary(entry => entry.Key, entry => entry.Value);

        viewModel.SelectedRung = viewModel.RungRows.First(row => row.Key == big.Key);
        Assert.True(viewModel.Descend());
        await viewModel.LayoutReady;
        Assert.Null(viewModel.GraphDisplay.Node(collapsed.Key));
        Assert.Equal(GraphNodeKind.Process, viewModel.GraphDisplay.NodeOf(pair[0].Id)!.Kind);
        Assert.Equal(GraphNodeKind.Context, viewModel.GraphDisplay.NodeOf(pair[1].Id)!.Kind);
        Dictionary<string, GraphPoint> opened = viewModel.DisplayPositions.ToDictionary(entry => entry.Key, entry => entry.Value);

        layouts.Holding = true;
        Assert.True(viewModel.Ascend());

        // On screen in both drawings: stays where it is. Back from the group or from the context: returns to its place.
        Assert.Equal(opened[pair[0].Id.ToString()], viewModel.DisplayPositions[pair[0].Id.ToString()]);
        Assert.Equal(machine[collapsed.Key], viewModel.DisplayPositions[collapsed.Key]);
        Assert.Equal(machine[pair[1].Id.ToString()], viewModel.DisplayPositions[pair[1].Id.ToString()]);
        layouts.Release();
        await viewModel.LayoutReady;
        Assert.Null(viewModel.GraphLayoutProblem);
    }

    [Fact(DisplayName = "§6.3: a manual pin is a hard constraint through re-layout and a later publication")]
    public async Task ManualPinSurvivesRelayoutAndLaterPublication()
    {
        (WorkspaceSnapshot snapshot, _, ProcessNode[] processes) = Mixed();
        using var first = new WorkspaceViewModel(snapshot, "session:test:generation:1");
        await first.LayoutReady;
        string key = processes[0].Id.ToString();
        first.SelectGraphNode(key);
        var pin = new GraphPoint(0.17, 0.23);

        Assert.True(first.PinGraphNode(key, pin));
        await first.LayoutReady;
        Assert.Equal(pin, first.DisplayPositions[key]);
        Assert.Equal(pin, first.GraphPins[key]);
        Assert.Contains(key, first.PinnedGraphNodeKeys);
        Assert.Equal("Unpin node (P)", first.PinActionLabel);
        Assert.Contains(first.DescribeGraphHover(key)!.Lines,
            line => line.StartsWith("Pinned where it was placed", StringComparison.Ordinal));

        Assert.True(first.RelayoutGraph());
        await first.LayoutReady;
        Assert.Equal(pin, first.DisplayPositions[key]);

        using var second = new WorkspaceViewModel(snapshot, "session:test:generation:2",
            carriedLayout: first.LaidOutPositions, carriedPins: first.GraphPins);
        await second.LayoutReady;
        Assert.Equal(pin, second.DisplayPositions[key]);
        Assert.Equal(pin, second.GraphPins[key]);

        second.SelectGraphNode(key);
        Assert.True(second.TogglePinSelectedGraphNode());
        await second.LayoutReady;
        Assert.DoesNotContain(key, second.GraphPins.Keys);
        Assert.Equal("Pin node (P)", second.PinActionLabel);
    }

    [Fact(DisplayName = "§6.3: a hub label searches beyond four occupied sides instead of disappearing")]
    public void CrowdedHubStillGetsAClearLabelSlot()
    {
        var pane = new Rect(0, 0, 500, 300);
        var hub = new Point(250, 150);
        const double radius = 12;
        var points = new Dictionary<string, Point>(StringComparer.Ordinal)
        {
            ["hub"] = hub,
            ["below"] = new(250, 174),
            ["above"] = new(250, 126),
            ["right"] = new(282, 150),
            ["left"] = new(218, 150),
        };
        var radii = points.Keys.ToDictionary(key => key, _ => 9d, StringComparer.Ordinal);
        radii["hub"] = radius;

        Rect? label = GraphView.FindLabelBox(
            pane, hub, radius, 100, 18, "hub", points, radii, [], mustShow: false);

        Assert.True(label.HasValue);
        Rect box = label.GetValueOrDefault();
        Assert.True(pane.Contains(box));
        Assert.All(points.Where(entry => entry.Key != "hub"), entry =>
            Assert.False(box.Inflate(radii[entry.Key]).Contains(entry.Value),
                $"label covered {entry.Key} at {entry.Value}"));
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

    [Fact(DisplayName = "§6.3: a graph hover card states half-open scope, semantics, value, unknowns, coverage and scale")]
    public async Task HoverCardsStateTheContract()
    {
        (WorkspaceSnapshot snapshot, _, ProcessNode[] processes) = Mixed();
        using var viewModel = new WorkspaceViewModel(snapshot, "session:test:generation:1");
        await viewModel.LayoutReady;

        HoverCard process = Assert.IsType<HoverCard>(viewModel.DescribeGraphHover(processes[0].Id.ToString()));
        // A PID is an identifier, written as the canvas label and Task Manager write it: without a group separator.
        Assert.Equal("Process 1 · PID 2001", process.Title);
        Assert.Equal("Process instance · 1 relationship", process.Lines[0]);
        Assert.StartsWith("Scope: [", process.Lines[1], StringComparison.Ordinal);
        Assert.EndsWith(" · whole session", process.Lines[1], StringComparison.Ordinal);
        Assert.Contains("Basis: source observations", process.Lines[2], StringComparison.Ordinal);
        Assert.Contains("accounting: not applicable to a count", process.Lines[2], StringComparison.Ordinal);
        Assert.Equal("Paired TCP observations on its relationships: 10", process.Lines[3]);
        Assert.Equal("Bytes: unknown · 10 contributing observations are on relationships with no byte total", process.Lines[4]);
        Assert.Equal("Coverage: covered", process.Lines[5]);
        Assert.Equal("Size: log scale against the busiest drawn node, 10", process.Lines[6]);

        GraphDisplayNode quiet = Assert.Single(viewModel.GraphDisplay.Nodes, node => node.Kind == GraphNodeKind.Quiet);
        HoverCard aggregate = Assert.IsType<HoverCard>(viewModel.DescribeGraphHover(quiet.Key));
        Assert.Equal("No relationships", aggregate.Title);
        Assert.Equal("3 processes with no admitted relationship", aggregate.Lines[0]);
        Assert.Equal("Bytes: unknown · 0 contributing observations in this scope", aggregate.Lines[4]);

        HoverCard edge = Assert.IsType<HoverCard>(viewModel.DescribeGraphHover("pair"));
        Assert.Equal("Process 1 ↔ Process 2", edge.Title);
        Assert.Equal("TCP · direct evidence · 1 relationship · 0 channels", edge.Lines[0]);
        Assert.Equal("Paired TCP observations: 10 · from both ends", edge.Lines[3]);
        Assert.Contains("Direction: display order only, not who initiated or sent", edge.Lines);
        Assert.Equal("Double-click opens its source process", edge.Lines[^1]);
        Assert.Null(viewModel.DescribeGraphHover("no such mark"));
    }

    [Fact(DisplayName = "§3.2: each rung draws its own neighbourhood, and a double click on an edge opens its channel")]
    public async Task EachRungDrawsItsNeighbourhoodAndAnEdgeOpensItsChannel()
    {
        using var viewModel = new WorkspaceViewModel(SyntheticWorkspace.Create(), "generation-1");
        await viewModel.LayoutReady;
        Assert.DoesNotContain(viewModel.GraphDisplay.Nodes, node => node.Kind == GraphNodeKind.Context);
        ProcessInstanceId api = new(Guid.Parse("76447f1d-1cfc-4815-b775-cbdb8327f624"));
        ProcessInstanceId cache = new(Guid.Parse("25ecf72c-7d72-4421-943e-eb6d66cd2fe8"));
        ProcessInstanceId browser = new(Guid.Parse("5eb7465f-3dd6-4df8-8577-472fa43aab10"));

        // The hover card says what the double click will do.
        Assert.Equal("Double-click opens its channel", viewModel.DescribeGraphHover("edge.api-cache")!.Lines[^1]);
        Assert.Equal("Double-click opens its source process, whose rows list its 2 channels",
            viewModel.DescribeGraphHover("edge.browser-api")!.Lines[^1]);

        // One channel: machine, group, process, channel - every rung in the breadcrumb, the channel's two participants drawn.
        Assert.True(viewModel.OpenGraphEdge("edge.api-cache"));
        await viewModel.LayoutReady;
        Assert.Equal(4, viewModel.Crumbs.Count);
        Assert.StartsWith("Channel", viewModel.Crumbs[^1].Label, StringComparison.Ordinal);
        Assert.Equal(GraphNodeKind.Process, viewModel.GraphDisplay.NodeOf(api)!.Kind);
        Assert.Equal(GraphNodeKind.Process, viewModel.GraphDisplay.NodeOf(cache)!.Kind);
        Assert.Equal(3, Assert.Single(viewModel.GraphDisplay.Nodes, node => node.Kind == GraphNodeKind.Context).Members.Count);
        Assert.Equal("edge.api-cache", viewModel.HighlightedEdgeKey);
        Assert.Equal(@"Channel \\.\pipe\intercat-cache: 2 processes drawn · 3 more in Rest of the machine", viewModel.GraphSummary);

        // The evidence rung keeps the graph of the rung it was reached from.
        string[] channelDrawing = [.. viewModel.GraphDisplay.Nodes.Select(node => node.Key)];
        Assert.True(viewModel.ShowEvidence());
        Assert.Equal(channelDrawing, viewModel.GraphDisplay.Nodes.Select(node => node.Key));

        // Several channels: the double click stops at the source process, whose rows list them, drawn with its peers.
        viewModel.ReturnTo(0);
        await viewModel.LayoutReady;
        Assert.DoesNotContain(viewModel.GraphDisplay.Nodes, node => node.Kind == GraphNodeKind.Context);
        Assert.True(viewModel.OpenGraphEdge("edge.browser-api"));
        await viewModel.LayoutReady;
        Assert.Equal(3, viewModel.Crumbs.Count);
        Assert.Equal(GraphNodeKind.Process, viewModel.GraphDisplay.NodeOf(browser)!.Kind);
        Assert.Equal(GraphNodeKind.Process, viewModel.GraphDisplay.NodeOf(api)!.Kind);
        Assert.Equal(GraphNodeKind.Context, viewModel.GraphDisplay.NodeOf(cache)!.Kind);
        Assert.StartsWith("Browser · PID 8204 and its peers: 2 processes drawn", viewModel.GraphSummary, StringComparison.Ordinal);

        // The context node says which of its relationships are drawn into it and which stay folded among its processes.
        viewModel.SelectGraphNode(viewModel.GraphDisplay.NodeOf(cache)!.Key);
        Assert.Equal("Rest of the machine", viewModel.SelectionTitle);
        Assert.Equal("3 processes outside this focus, counted rather than drawn · 2 relationships with the drawn processes, "
            + "2 among themselves. Esc returns to the wider graph.", viewModel.SelectionSubtitle);
        Assert.Equal(3, viewModel.Crumbs.Count);

        // It is not sized on the focus's scale, and the keyboard reaches it only after every drawn process.
        GraphDisplayNode rest = viewModel.GraphDisplay.NodeOf(cache)!;
        Assert.Equal("Size: fixed; the rest of the machine is not read on this focus's scale",
            viewModel.DescribeGraphHover(rest.Key)!.Lines[^1]);
        Assert.Equal(rest.Key, GraphView.Traversal(viewModel.GraphDisplay)[^1].Key);
    }

    [Fact(DisplayName = "§6.7: a double click on an aggregate edge opens nothing, since it stands for several relationships")]
    public async Task AnAggregateEdgeOpensNothing()
    {
        ProcessGroup pool = new("pool", "worker.exe", LaneGrouping.Executable);
        ProcessGroup queue = new("queue", "queue.exe", LaneGrouping.Executable);
        ProcessNode hub = Process(100, queue.Key);
        ProcessNode[] workers = [.. Enumerable.Range(1, 26).Select(index => Process(index, pool.Key))];
        CommunicationEdge[] edges = [.. workers.Select(worker => Edge($"w{worker.ProcessId}", worker, hub))];
        using var viewModel = new WorkspaceViewModel(Snapshot([pool, queue], [hub, .. workers], edges), "session:test:generation:1");
        await viewModel.LayoutReady;

        GraphDisplayEdge aggregate = Assert.Single(viewModel.GraphDisplay.Edges);
        Assert.Equal(26, aggregate.Relationships.Count);
        Assert.DoesNotContain(viewModel.DescribeGraphHover(aggregate.Key)!.Lines,
            line => line.StartsWith("Double-click", StringComparison.Ordinal));
        Assert.False(viewModel.OpenGraphEdge(aggregate.Key));
        Assert.Single(viewModel.Crumbs);
    }

    [Fact(DisplayName = "§3.2: a focus claims peers only when it has some, and says when its processes are drawn as fewer nodes")]
    public async Task FocusSummaryClaimsOnlyThePeersItHas()
    {
        (WorkspaceSnapshot snapshot, ProcessGroup busy, ProcessNode[] processes) = Mixed();
        using var viewModel = new WorkspaceViewModel(snapshot, "session:test:generation:1");
        await viewModel.LayoutReady;

        // idle.exe's two processes have no relationship: no peers, and they are drawn together as the group's other members.
        viewModel.SelectedRung = viewModel.RungRows.Single(row => row.Key == "idle");
        Assert.True(viewModel.Descend());
        await viewModel.LayoutReady;
        Assert.Equal("idle.exe: 2 processes drawn as 1 node · 3 more in Rest of the machine", viewModel.GraphSummary);

        // busy.exe's pair talk only to each other, so the group has no peers either; the pair's first process does.
        viewModel.ReturnTo(0);
        viewModel.SelectedRung = viewModel.RungRows.Single(row => row.Key == busy.Key);
        Assert.True(viewModel.Descend());
        await viewModel.LayoutReady;
        Assert.Equal("busy.exe: 3 processes drawn · 2 more in Rest of the machine", viewModel.GraphSummary);
        viewModel.SelectedRung = viewModel.RungRows.Single(row => row.Key == processes[0].Id.ToString());
        Assert.True(viewModel.Descend());
        await viewModel.LayoutReady;
        Assert.Equal("Process 1 · PID 2001 and its peers: 2 processes drawn · 3 more in Rest of the machine",
            viewModel.GraphSummary);
    }

    [Fact(DisplayName = "R3: a process whose relationships measured no bytes reads bytes unknown, never 0 MB")]
    public async Task UnmeasuredBytesAreUnknownNotZero()
    {
        using var viewModel = new WorkspaceViewModel(SyntheticWorkspace.Create(), "generation-1");
        await viewModel.LayoutReady;

        // The tour's cache talks over a pipe and a shared section, neither of which measured a byte.
        viewModel.SelectProcess(new ProcessInstanceId(Guid.Parse("25ecf72c-7d72-4421-943e-eb6d66cd2fe8")));
        Assert.Equal("188 observations · bytes unknown", viewModel.EvidenceSummary);
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
