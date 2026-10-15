using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// The graph's Other hosts node (§6.3's unresolved resources, which remain visible): each process's connections whose
/// other end no record of the capture holds, at an address neither loopback nor the connection's own, drawn to one node
/// as a resource is - a transport an edge, with no traffic value - and never naming who is at their other ends (P7).
/// </summary>
public sealed class OtherHostsGraphTests
{
    [Fact(DisplayName = "§6.3: a session's connections to other hosts are its host links, a process and transport each, as its evidence policy admits their binding")]
    public void ASessionsConnectionsToOtherHostsAreItsHostLinks()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            // A PID reused: its later instance's connection binds to it only as a candidate.
            Lifecycle(10, ObservationKind.Create, 210, 1),
            Lifecycle(20, ObservationKind.Exit, 210, 2),
            Lifecycle(30, ObservationKind.Create, 210, 3),

            // The browser's two TCP connections to two hosts and its one UDP flow; then the reused PID's connection, a
            // relationship over loopback and a connection to an unseen end on this machine, none of them to another host.
            Transfer(120, ObservationKind.Send, AccountingSide.SendSide, 500, 300, 10).Between("192.168.1.10:52000", "142.250.1.1:443"),
            Transfer(121, ObservationKind.Receive, AccountingSide.ReceiveSide, 900, 300, 11).Between("192.168.1.10:52000", "142.250.1.1:443"),
            Transfer(122, ObservationKind.Send, AccountingSide.SendSide, 500, 300, 12).Between("192.168.1.10:52001", "20.42.65.1:443"),
            Transfer(123, ObservationKind.Send, AccountingSide.SendSide, 40, 300, 13).Between("192.168.1.10:61000", "8.8.8.8:53")
                with { Mechanism = Mechanism.Udp, EventId = 42 },
            Transfer(124, ObservationKind.Send, AccountingSide.SendSide, 1, 210, 14).Between("192.168.1.10:52003", "142.250.1.3:443"),
            Transfer(130, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 15).Between("127.0.0.1:50000", "127.0.0.1:8080"),
            Transfer(131, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 16).Between("127.0.0.1:8080", "127.0.0.1:50000"),
            Transfer(140, ObservationKind.Send, AccountingSide.SendSide, 2, 400, 17).Between("127.0.0.1:50005", "127.0.0.1:9999"),
        ]);

        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        ProcessInstanceId browser = overview.Nodes.Single(node => node.ProcessId == 300).Id;
        // A transfer binds to its PID's first instance as correlated (entities-v1), and so do the browser's connections,
        // each link counting them address by address as the relation index does (relations-v1 §5d).
        Assert.Equal([(browser, Mechanism.Tcp, RelationStrength.Correlated), (browser, Mechanism.Udp, RelationStrength.Correlated)],
            overview.HostLinks.Select(link => (link.Process, link.Mechanism, link.Strength)));
        Assert.Equal([new HostAddress("142.250.1.1", 1, 2) { Ports = [443] }, new HostAddress("20.42.65.1", 1, 1) { Ports = [443] }],
            overview.HostLinks[0].Addresses);
        Assert.Equal((2, 2, 3L), (overview.HostLinks[0].Connections, overview.HostLinks[0].Hosts, overview.HostLinks[0].Records));
        Assert.Equal([new HostAddress("8.8.8.8", 1, 1) { Ports = [53] }], overview.HostLinks[1].Addresses);
        Assert.Equal([$"hosts:tcp:{browser}", $"hosts:udp:{browser}"], overview.HostLinks.Select(link => link.Key));
        Assert.All(overview.HostLinks, link => Assert.True(HostLink.IsKey(link.Key)));
        Assert.False(HostLink.IsKey(overview.Edges.Single().Key));
        Assert.Same(overview.HostLinks, OverviewWorkspace.From(overview).HostLinks);

        // Counting candidates draws the reused PID's later instance's connection too, as it draws its relationships.
        SessionOverviewBundle candidates = SessionOverviewProjector.Project(session.Store, EvidencePolicy.IncludeCandidates);
        HostLink reused = Assert.Single(candidates.HostLinks, link => link.Process != browser);
        Assert.Equal((210, Mechanism.Tcp, 1, 1, 1L, RelationStrength.Candidate),
            (candidates.Nodes.Single(node => node.Id == reused.Process).ProcessId, reused.Mechanism, reused.Connections,
                reused.Hosts, reused.Records, reused.Strength));
    }

    [Fact(DisplayName = "§6.3: the graph draws each process's connections to other hosts to one Other hosts node, a transport an edge, side by side")]
    public void TheGraphDrawsConnectionsToOneOtherHostsNode()
    {
        ProcessGroup group = new("g", "g.exe", LaneGrouping.Executable);
        ProcessNode[] processes = [.. Enumerable.Range(1, 6).Select(index => Node(index, group.Key))];
        CommunicationEdge pair = Edge("pair", processes[2], processes[3], 10);
        HostLink[] links =
        [
            Link(processes[0], Mechanism.Tcp, 12, 5, 300),
            Link(processes[0], Mechanism.Udp, 3, 2, 40, RelationStrength.Correlated),
            Link(processes[1], Mechanism.Tcp, 1, 1, 7),
        ];

        GraphDisplay display = GraphProjection.Project(Snapshot([group], processes, [pair], links));

        // A process whose connections run to other hosts is drawn on its own; the two with neither are No relationships.
        Assert.All(processes[..4], process => Assert.Equal(GraphNodeKind.Process, display.NodeOf(process.Id)!.Kind));
        GraphDisplayNode quiet = Assert.Single(display.Nodes, node => node.Kind == GraphNodeKind.Quiet);
        Assert.Equal(Sorted([processes[4].Id, processes[5].Id]), Sorted(quiet.Members));

        // One node stands for the other hosts: no process, no relationship, the records of the connections drawn to it.
        GraphDisplayNode hub = Assert.IsType<GraphDisplayNode>(display.OtherHosts);
        Assert.Equal((GraphProjection.OtherHostsKey, "Other hosts", GraphNodeKind.OtherHosts), (hub.Key, hub.Label, hub.Kind));
        Assert.Empty(hub.Members);
        Assert.Equal((347L, 0, 0, 2), (hub.Observations, hub.Relationships, hub.InternalRelationships, hub.ConnectedProcesses));
        Assert.Same(hub, display.Nodes[^1]);
        Assert.Same(hub, display.Node(GraphProjection.OtherHostsKey));
        Assert.All(processes, process => Assert.NotSame(hub, display.NodeOf(process.Id)));

        // A transport an edge, the first process's two side by side; each names its host link, and none is a relationship.
        string first = display.NodeOf(processes[0].Id)!.Key;
        string second = display.NodeOf(processes[1].Id)!.Key;
        Assert.Equal(
            [
                (first, Mechanism.Tcp, 300L, RelationStrength.Direct, -0.5),
                (first, Mechanism.Udp, 40L, RelationStrength.Correlated, 0.5),
                (second, Mechanism.Tcp, 7L, RelationStrength.Direct, 0d),
            ],
            display.Edges.Where(edge => edge.ToHosts)
                .Select(edge => (edge.SourceKey, edge.Mechanism, edge.ObservationCount, edge.Strength, edge.Lane))
                .OrderBy(edge => edge.SourceKey == first ? 0 : 1)
                .ThenBy(edge => edge.Mechanism));
        Assert.All(display.Edges.Where(edge => edge.ToHosts), edge => Assert.Equal(hub.Key, edge.TargetKey));
        foreach (HostLink link in links)
        {
            GraphDisplayEdge drawn = Assert.Single(display.EdgesOfHostLink(link.Key));
            Assert.Equal([link.Key], drawn.Relationships);
            Assert.Null(display.EdgeOf(link.Key));
        }

        GraphDisplayEdge relationship = Assert.Single(display.Edges, edge => !edge.ToHosts);
        Assert.Equal((false, 0d), (relationship.ToHosts, relationship.Lane));
        Assert.Same(relationship, display.EdgeOf("pair"));
        Assert.Empty(display.EdgesOfHostLink("pair"));
        Assert.Equal(1, display.TotalRelationships);
        Assert.Equal(["pair"], display.RelationshipKeys);

        // The layout places the node the edges meet at, as it places any related node.
        GraphDisplayLayout layout = GraphLayout.ComputeDisplay("other-hosts", display);
        Assert.True(layout.Positions[hub.Key].IsValid);

        // A snapshot with no connection to other hosts draws no such node.
        GraphDisplay none = GraphProjection.Project(Snapshot([group], processes, [pair]));
        Assert.Null(none.OtherHosts);
        Assert.DoesNotContain(none.Edges, edge => edge.ToHosts);
    }

    [Fact(DisplayName = "§6.3: a collapsed group's connections to other hosts are one edge per transport from its node, and a focused rung leaves the rest of the machine's to their own rungs")]
    public void FoldedAndFocusedConnectionsToOtherHosts()
    {
        ProcessGroup pool = new("pool", "worker.exe", LaneGrouping.Executable);
        ProcessGroup lone = new("lone", "lone.exe", LaneGrouping.Executable);
        ProcessNode[] workers = [.. Enumerable.Range(1, 30).Select(index => Node(index, pool.Key))];
        ProcessNode client = Node(40, lone.Key);
        ProcessNode server = Node(41, lone.Key);
        CommunicationEdge pair = Edge("pair", client, server, 4);
        HostLink[] links =
        [
            .. workers.Select((worker, index) => Link(worker, Mechanism.Tcp, 1, 1, 2,
                index == 7 ? RelationStrength.Correlated : RelationStrength.Direct)),
            Link(client, Mechanism.Udp, 2, 1, 5),
        ];
        WorkspaceSnapshot snapshot = Snapshot([pool, lone], [.. workers, client, server], [pair], links);

        // Thirty workers drawn on their own would pass the threshold, so their group collapses, and its one edge to the
        // other hosts names every member's connections, as weak as the weakest of them.
        GraphDisplay display = GraphProjection.Project(snapshot);
        GraphDisplayNode collapsed = Assert.Single(display.Nodes, node => node.Kind == GraphNodeKind.Group);
        Assert.Equal(30, collapsed.Members.Count);
        GraphDisplayEdge fromPool = Assert.Single(display.Edges, edge => edge.SourceKey == collapsed.Key);
        Assert.True(fromPool.ToHosts);
        Assert.Equal((Mechanism.Tcp, 60L, RelationStrength.Correlated, 30, 0d),
            (fromPool.Mechanism, fromPool.ObservationCount, fromPool.Strength, fromPool.Relationships.Count, fromPool.Lane));
        Assert.Equal(links[..30].Select(link => link.Key).Order(StringComparer.Ordinal), fromPool.Relationships);
        Assert.Equal(31, display.OtherHosts!.ConnectedProcesses);

        // A focus on the client draws its own connections and leaves the workers', outside it, to their own rungs.
        GraphDisplay focused = GraphProjection.Project(snapshot, kept: client.Id,
            neighborhood: new HashSet<ProcessInstanceId> { client.Id, server.Id });
        Assert.Equal(1, focused.OtherHosts!.ConnectedProcesses);
        GraphDisplayEdge own = Assert.Single(focused.Edges, edge => edge.ToHosts);
        Assert.Equal((focused.NodeOf(client.Id)!.Key, Mechanism.Udp, 5L), (own.SourceKey, own.Mechanism, own.ObservationCount));
        Assert.All(links[..30], link => Assert.Empty(focused.EdgesOfHostLink(link.Key)));

        // A focus none of whose processes reaches another host draws no Other hosts node.
        GraphDisplay quiet = GraphProjection.Project(snapshot, kept: server.Id,
            neighborhood: new HashSet<ProcessInstanceId> { server.Id });
        Assert.Null(quiet.OtherHosts);
        Assert.DoesNotContain(quiet.Edges, edge => edge.ToHosts);
    }

    [Fact(DisplayName = "§6.3: past the budget's count, the less busy of the processes that only reach other hosts are counted in one Other hosts only node, edged to Other hosts once per transport")]
    public void TheLessBusyProcessesThatOnlyReachOtherHostsFoldTogether()
    {
        // Fourteen processes reach other hosts and relate to nothing, the busier the later by TCP, two of the quieter by UDP
        // too - enough, added, to make the second of them one of the busiest; a client and a server relate to each other
        // and reach other hosts as well.
        ProcessGroup group = new("g", "g.exe", LaneGrouping.Executable);
        ProcessNode[] reaching = [.. Enumerable.Range(1, 14).Select(index => Node(index, group.Key))];
        ProcessNode client = Node(20, group.Key);
        ProcessNode server = Node(21, group.Key);
        CommunicationEdge pair = Edge("pair", client, server, 6);
        HostLink[] links =
        [
            .. reaching.Select((process, index) => Link(process, Mechanism.Tcp, 1, 1, 10 + index)),
            Link(reaching[0], Mechanism.Udp, 1, 1, 3, RelationStrength.Correlated),
            Link(reaching[1], Mechanism.Udp, 2, 1, 9),
            Link(client, Mechanism.Tcp, 1, 1, 1),
        ];
        WorkspaceSnapshot snapshot = Snapshot([group], [.. reaching, client, server], [pair], links);
        GraphDisplay display = GraphProjection.Project(snapshot);

        // The eight busiest by all their connections' records stay drawn; the six less busy are one node, and the related
        // pair, however few its records to other hosts, stays drawn as itself.
        GraphDisplayNode folded = Assert.Single(display.Nodes, node => node.Kind == GraphNodeKind.OtherHostsOnly);
        Assert.Equal(("Other hosts only", 0, 0L), (folded.Label, folded.Relationships, folded.Observations));
        ProcessNode[] lessBusy = [reaching[0], .. reaching[2..7]];
        Assert.Equal(Sorted([.. lessBusy.Select(process => process.Id)]), Sorted(folded.Members));
        Assert.All(reaching.Except(lessBusy), process => Assert.Equal(GraphNodeKind.Process, display.NodeOf(process.Id)!.Kind));
        Assert.All(new[] { client, server }, process => Assert.Equal(GraphNodeKind.Process, display.NodeOf(process.Id)!.Kind));
        Assert.Equal(12, display.Nodes.Count);
        Assert.Equal(15, display.OtherHosts!.ConnectedProcesses);

        // Its edges to the other hosts are a transport each, side by side, naming every folded process's connections, as
        // weak as the weakest of them.
        Assert.Equal(
            [(Mechanism.Tcp, 80L, RelationStrength.Direct, 6, -0.5), (Mechanism.Udp, 3L, RelationStrength.Correlated, 1, 0.5)],
            display.Edges.Where(edge => edge.SourceKey == folded.Key).OrderBy(edge => edge.Mechanism)
                .Select(edge => (edge.Mechanism, edge.ObservationCount, edge.Strength, edge.Relationships.Count, edge.Lane)));
        Assert.All(display.Edges.Where(edge => edge.SourceKey == folded.Key), edge => Assert.True(edge.ToHosts));

        // A brush re-counts the drawing and keeps who is folded.
        GraphDisplay brushed = GraphProjection.Rescope(display, snapshot, snapshot with
        {
            Edges = [.. snapshot.Edges.Select(edge => edge with { ObservationCount = 1 })],
        });
        Assert.Equal(folded.Members, brushed.Nodes.Single(node => node.Kind == GraphNodeKind.OtherHostsOnly).Members);

        // A budget drawing thirteen of them folds none - the one less busy process left would be a one-member aggregate -
        // and a budget drawing none folds every one.
        GraphDisplay nine = GraphProjection.Project(snapshot, budget: GraphDisplayBudget.Default with { HostOnlyDrawn = 13 });
        Assert.DoesNotContain(nine.Nodes, node => node.Kind == GraphNodeKind.OtherHostsOnly);
        GraphDisplay none = GraphProjection.Project(snapshot, budget: GraphDisplayBudget.Default with { HostOnlyDrawn = 0 });
        Assert.Equal(14, none.Nodes.Single(node => node.Kind == GraphNodeKind.OtherHostsOnly).Members.Count);
        Assert.Equal(nameof(GraphDisplayBudget.HostOnlyDrawn), Assert.Throws<ArgumentOutOfRangeException>(
            () => GraphProjection.Project(snapshot, budget: GraphDisplayBudget.Default with { HostOnlyDrawn = -1 })).ParamName);

        // The process kept in focus stays drawn, and an opened group's members are its focus: none of them is folded.
        GraphDisplay kept = GraphProjection.Project(snapshot, kept: reaching[0].Id);
        Assert.Equal(GraphNodeKind.Process, kept.NodeOf(reaching[0].Id)!.Kind);
        Assert.Equal(5, kept.Nodes.Single(node => node.Kind == GraphNodeKind.OtherHostsOnly).Members.Count);
        Assert.DoesNotContain(reaching[0].Id, kept.Nodes.Single(node => node.Kind == GraphNodeKind.OtherHostsOnly).Members);
        GraphDisplay opened = GraphProjection.Project(snapshot, expandedGroup: group.Key);
        Assert.DoesNotContain(opened.Nodes, node => node.Kind == GraphNodeKind.OtherHostsOnly);
    }

    [Fact(DisplayName = "§6.3: a collapsed group keeps its members that only reach other hosts, and only the processes drawn on their own are folded")]
    public void ACollapsedGroupKeepsItsMembers()
    {
        // Thirty workers of one executable collapse into their group; ten other processes reach other hosts on their own.
        ProcessGroup pool = new("pool", "worker.exe", LaneGrouping.Executable);
        ProcessGroup others = new("others", "other.exe", LaneGrouping.Executable);
        ProcessNode[] workers = [.. Enumerable.Range(1, 30).Select(index => Node(index, pool.Key))];
        ProcessNode[] reaching = [.. Enumerable.Range(40, 10).Select(index => Node(index, others.Key))];
        HostLink[] links =
        [
            .. workers.Select(worker => Link(worker, Mechanism.Tcp, 1, 1, 100)),
            .. reaching.Select((process, index) => Link(process, Mechanism.Tcp, 1, 1, 1 + index)),
        ];
        GraphDisplay display = GraphProjection.Project(Snapshot([pool, others], [.. workers, .. reaching], [], links));
        Assert.Equal(30, Assert.Single(display.Nodes, node => node.Kind == GraphNodeKind.Group).Members.Count);
        Assert.Equal(Sorted([reaching[0].Id, reaching[1].Id]),
            Sorted(Assert.Single(display.Nodes, node => node.Kind == GraphNodeKind.OtherHostsOnly).Members));
    }

    [Fact(DisplayName = "§6.3: connections to other hosts carry no traffic value: a brush does not re-count them, a byte ranking does not size them, and they set no scale")]
    public void ConnectionsToOtherHostsCarryNoTrafficValue()
    {
        ProcessGroup group = new("g", "g.exe", LaneGrouping.Executable);
        ProcessNode[] processes = [.. Enumerable.Range(1, 3).Select(index => Node(index, group.Key))];
        CommunicationEdge pair = Edge("pair", processes[0], processes[1], 10);
        HostLink busy = Link(processes[2], Mechanism.Tcp, 400, 90, 1_000_000);
        WorkspaceSnapshot snapshot = Snapshot([group], processes, [pair], [busy]);
        GraphDisplay display = GraphProjection.Project(snapshot);
        GraphDisplayEdge toHosts = Assert.Single(display.EdgesOfHostLink(busy.Key));
        GraphDisplayNode hub = display.OtherHosts!;

        // Every process is drawn as itself: the node the connections run to is no aggregate of them.
        Assert.False(display.IsClustered);

        // A brush re-counts the relationship and leaves the connections as the whole session counted them.
        GraphDisplay brushed = GraphProjection.Rescope(display, snapshot, snapshot with { Edges = [pair with { ObservationCount = 3 }] });
        Assert.Equal(3, brushed.EdgeOf("pair")!.ObservationCount);
        Assert.Equal(Drawn(toHosts), Drawn(Assert.Single(brushed.EdgesOfHostLink(busy.Key))));
        Assert.Equal(Drawn(hub), Drawn(brushed.OtherHosts!));

        // A byte ranking sizes the relationship and the processes, never the connections or the node they run to.
        GraphDisplay weighed = display.WithMagnitudes(_ => 5_000, _ => 7_000);
        Assert.Equal(5_000, weighed.EdgeOf("pair")!.Magnitude);
        Assert.Equal(7_000, weighed.NodeOf(processes[2].Id)!.Magnitude);
        Assert.Same(toHosts, Assert.Single(weighed.EdgesOfHostLink(busy.Key)));
        Assert.Same(hub, weighed.OtherHosts);
        GraphDisplay unmeasured = display.WithMagnitudes(_ => null, _ => null);
        Assert.Same(toHosts, Assert.Single(unmeasured.EdgesOfHostLink(busy.Key)));
        Assert.Same(hub, unmeasured.OtherHosts);

        // Neither sets a scale nor is read against one: the busiest edge and node are the relationship's.
        Assert.Equal(10, GraphEncoding.EdgeScale(display));
        Assert.Equal(10, GraphEncoding.NodeScale(display));
        Assert.Equal(1.25, GraphEncoding.EdgeThickness(toHosts, 1));
        Assert.Equal(1.25, GraphEncoding.EdgeThickness(toHosts with { Unmeasured = true }, 10));
        Assert.Equal(16, GraphEncoding.NodeRadius(hub, 10));
        Assert.Equal(16, GraphEncoding.NodeRadius(hub, 1_000_000_000));
    }

    [Fact(DisplayName = "§6.3: a host link naming no process of its snapshot, twice, or counting the impossible is refused, and so is a drawing that is no honest Other hosts node")]
    public void ImpossibleConnectionsToOtherHostsAreRefused()
    {
        ProcessGroup group = new("g", "g.exe", LaneGrouping.Executable);
        ProcessNode[] processes = [Node(1, group.Key), Node(2, group.Key)];
        HostLink link = Link(processes[0], Mechanism.Tcp, 2, 1, 4);
        WorkspaceSnapshot Of(params HostLink[] links) => Snapshot([group], processes, [], links);

        Assert.Throws<ArgumentException>(() => GraphProjection.Project(Of(link, link)));
        Assert.Throws<ArgumentException>(() => GraphProjection.Project(Of(Link(Node(9, group.Key), Mechanism.Tcp, 1, 1, 1))));
        // At least one address, each named once, each reached by a connection and each connection holding a record.
        Assert.Throws<ArgumentException>(() => GraphProjection.Project(Of(link with { Addresses = [] })));
        Assert.Throws<ArgumentException>(() => GraphProjection.Project(Of(link with { Addresses = [new("198.51.100.1", 0, 4)] })));
        Assert.Throws<ArgumentException>(() => GraphProjection.Project(Of(link with { Addresses = [new("198.51.100.1", 2, 1)] })));
        Assert.Throws<ArgumentException>(() => GraphProjection.Project(Of(link with { Addresses = [new("", 2, 4)] })));
        Assert.Throws<ArgumentException>(() => GraphProjection.Project(Of(link with
        {
            Addresses = [new("198.51.100.1", 1, 2), new("198.51.100.1", 1, 2)],
        })));
        _ = GraphProjection.Project(Of(link with { Addresses = [new("198.51.100.1", 1, 1), new("198.51.100.2", 1, 3)] }));

        GraphDisplay display = GraphProjection.Project(Of(link));
        GraphDisplayNode hub = display.OtherHosts!;
        GraphDisplayEdge toHosts = Assert.Single(display.EdgesOfHostLink(link.Key));
        GraphDisplayNode[] processNodes = [.. display.Nodes.Where(node => node.Kind != GraphNodeKind.OtherHosts)];
        GraphDisplay Draw(IReadOnlyList<GraphDisplayNode> nodes, IReadOnlyList<GraphDisplayEdge> edges) =>
            new(nodes, edges, display.TotalProcesses, null, null);
        _ = Draw(display.Nodes, display.Edges);

        // The node stands for no process, and there is one of it.
        Assert.Throws<ArgumentException>(() => Draw(
            [.. processNodes.Where(node => !node.Members.Contains(processes[1].Id)), hub with { Members = [processes[1].Id] }], []));
        Assert.Throws<ArgumentException>(() => Draw([.. display.Nodes, hub with { Key = "resource:more-hosts" }], display.Edges));

        // Its edges come from a drawn node and name host links, each once; no other edge runs to it or from it.
        Assert.Throws<ArgumentException>(() => Draw(display.Nodes, [toHosts with { ToHosts = false }]));
        Assert.Throws<ArgumentException>(() => Draw(display.Nodes, [toHosts with { SourceKey = hub.Key, TargetKey = processNodes[0].Key }]));
        Assert.Throws<ArgumentException>(() => Draw(display.Nodes, [toHosts with { TargetKey = processNodes[^1].Key }]));
        Assert.Throws<ArgumentException>(() => Draw(display.Nodes, [toHosts with { Relationships = ["pair"] }]));
        Assert.Throws<ArgumentException>(() => Draw(display.Nodes, [toHosts, toHosts with { Key = "again" }]));
    }

    [Fact(DisplayName = "§6.3: Other hosts opened draws its busiest hosts on their own, edged from the processes that reached them, and keeps the rest")]
    public void OtherHostsOpenedDrawsItsBusiestHosts()
    {
        ProcessGroup group = new("g", "g.exe", LaneGrouping.Executable);
        ProcessNode browser = Node(1, group.Key);
        ProcessNode updater = Node(2, group.Key);
        ProcessNode idle = Node(3, group.Key);
        HostLink browserTcp = new(browser.Id, Mechanism.Tcp, [new("142.250.1.1", 3, 50), new("20.42.65.1", 1, 4)], RelationStrength.Direct);
        HostLink browserUdp = new(browser.Id, Mechanism.Udp, [new("142.250.1.1", 1, 9)], RelationStrength.Correlated);
        HostLink updaterTcp = new(updater.Id, Mechanism.Tcp, [new("20.42.65.1", 2, 30), new("13.107.4.50", 1, 2)], RelationStrength.Direct);
        WorkspaceSnapshot snapshot = Snapshot([group], [browser, updater, idle], [], [browserTcp, browserUdp, updaterTcp]);
        GraphDisplayBudget two = GraphDisplayBudget.Default with { HostsDrawn = 2 };

        // Folded, as ever: one Other hosts node, and nothing opened.
        GraphDisplay folded = GraphProjection.Project(snapshot, budget: two);
        Assert.Null(folded.OpenedHosts);
        Assert.Equal(["resource:other-hosts"], folded.Nodes.Where(node => node.IsHost).Select(node => node.Key));

        // Opened, the two busiest hosts by their records - 142.250.1.1's 59 and 20.42.65.1's 34 - are drawn on their own,
        // and 13.107.4.50 stays in Other hosts; no process moves.
        GraphDisplay opened = GraphProjection.Project(snapshot, budget: two, openHosts: true);
        Assert.Equal(["142.250.1.1", "20.42.65.1"], opened.OpenedHosts);
        string first = GraphProjection.HostKey("142.250.1.1");
        string second = GraphProjection.HostKey("20.42.65.1");
        Assert.Equal(
            [(first, "142.250.1.1", 1, 59L), (second, "20.42.65.1", 2, 34L), (GraphProjection.OtherHostsKey, "Other hosts", 1, 2L)],
            opened.Nodes.Where(node => node.IsHost).Select(node => (node.Key, node.Label, node.ConnectedProcesses, node.Observations)));
        Assert.Equal(GraphNodeKind.Host, opened.Node(first)!.Kind);
        Assert.Equal(folded.Nodes.Where(node => !node.IsHost).Select(node => node.Key), opened.Nodes.Where(node => !node.IsHost).Select(node => node.Key));

        // Each drawn node's connections run to each host they reached, a transport an edge, side by side to one host, and to
        // Other hosts for the rest; a connection to two hosts is drawn to each.
        string browserNode = opened.NodeOf(browser.Id)!.Key;
        string updaterNode = opened.NodeOf(updater.Id)!.Key;
        Assert.Equal(
            [
                (browserNode, first, Mechanism.Tcp, 50L, -0.5), (browserNode, first, Mechanism.Udp, 9L, 0.5),
                (browserNode, second, Mechanism.Tcp, 4L, 0d), (updaterNode, second, Mechanism.Tcp, 30L, 0d),
                (updaterNode, GraphProjection.OtherHostsKey, Mechanism.Tcp, 2L, 0d),
            ],
            opened.Edges.OrderBy(edge => edge.SourceKey, StringComparer.Ordinal).ThenBy(edge => edge.TargetKey == first ? 0
                    : edge.TargetKey == second ? 1 : 2).ThenBy(edge => edge.Mechanism)
                .Select(edge => (edge.SourceKey, edge.TargetKey, edge.Mechanism, edge.ObservationCount, edge.Lane)));
        Assert.All(opened.Edges, edge => Assert.True(edge.ToHosts));
        Assert.Equal([first, second], opened.EdgesOfHostLink(browserTcp.Key).Select(edge => edge.TargetKey).Order(StringComparer.Ordinal));
        Assert.Equal([second, GraphProjection.OtherHostsKey], opened.EdgesOfHostLink(updaterTcp.Key).Select(edge => edge.TargetKey)
            .Order(StringComparer.Ordinal));
        Assert.Equal(RelationStrength.Correlated, opened.Edges.Single(edge => edge.Mechanism == Mechanism.Udp).Strength);

        // Every host opened leaves Other hosts undrawn; a re-count keeps what was opened.
        GraphDisplay all = GraphProjection.Project(snapshot, budget: GraphDisplayBudget.Default, openHosts: true);
        Assert.Equal(["142.250.1.1", "20.42.65.1", "13.107.4.50"], all.OpenedHosts);
        Assert.Null(all.OtherHosts);
        GraphDisplay recounted = GraphProjection.Rescope(opened, snapshot, snapshot);
        Assert.Equal(opened.OpenedHosts, recounted.OpenedHosts);
        Assert.Equal(opened.Edges.Select(edge => (edge.Key, edge.ObservationCount)), recounted.Edges.Select(edge => (edge.Key, edge.ObservationCount)));

        // Only as many hosts are drawn as the node and edge budgets allow: opening folds no process to make room.
        GraphDisplay tight = GraphProjection.Project(snapshot, budget: two with { Nodes = folded.Nodes.Count + 1 }, openHosts: true);
        Assert.Equal(["142.250.1.1"], tight.OpenedHosts);
        GraphDisplay none = GraphProjection.Project(snapshot, budget: two with { Nodes = folded.Nodes.Count, Edges = folded.Edges.Count },
            openHosts: true);
        Assert.Equal([], none.OpenedHosts!);
        Assert.Equal(folded.Nodes.Select(node => node.Key), none.Nodes.Select(node => node.Key));
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphProjection.Project(snapshot, budget: two with { HostsDrawn = -1 }));
    }

    [Fact(DisplayName = "§19.5: a process set aside takes its connections to other hosts with it")]
    public void AProcessSetAsideTakesItsConnectionsToOtherHosts()
    {
        ProcessGroup group = new("g", "g.exe", LaneGrouping.Executable);
        ProcessNode broker = Node(1, group.Key) with { Collector = CollectorRole.Broker };
        ProcessNode browser = Node(2, group.Key);
        WorkspaceSnapshot snapshot = Snapshot([group], [broker, browser], [],
            [Link(broker, Mechanism.Tcp, 1, 1, 1), Link(browser, Mechanism.Tcp, 2, 2, 2), Link(browser, Mechanism.Udp, 1, 1, 1)]);

        WorkspaceSnapshot kept = WorkspaceCollectors.SetAside(snapshot);
        Assert.Equal([browser.Id, browser.Id], kept.HostLinks.Select(link => link.Process));
        Assert.Equal(1, GraphProjection.Project(kept).OtherHosts!.ConnectedProcesses);
    }

    [Fact(DisplayName = "§6.3: connections to other hosts are said as a process's rung lists them: TCP connections to hosts and UDP flows with them")]
    public void ConnectionsToOtherHostsAreSaidAsTheRungListsThem()
    {
        ProcessNode one = Node(1, "g");
        ProcessNode two = Node(2, "g");
        Assert.Equal("1 TCP connection to 1 host", HostLinkText.Reach(Link(one, Mechanism.Tcp, 1, 1, 1)));
        Assert.Equal("42 TCP connections to 17 hosts", HostLinkText.Reach(Link(one, Mechanism.Tcp, 42, 17, 90)));
        Assert.Equal("1 UDP flow with 1 host", HostLinkText.Reach(Link(one, Mechanism.Udp, 1, 1, 1)));
        Assert.Equal("5 UDP flows with 3 hosts", HostLinkText.Reach(Link(one, Mechanism.Udp, 5, 3, 9)));
        Assert.Equal("1,200 TCP connections", HostLinkText.Connections(Mechanism.Tcp, 1_200));

        // Several processes' are counted by connection, process and host: a host two of them reached is one host.
        Assert.Equal("7 TCP connections from 2 processes to 4 hosts",
            HostLinkText.Reach(Mechanism.Tcp, [Link(one, Mechanism.Tcp, 3, 2, 4), Link(two, Mechanism.Tcp, 4, 4, 4)]));
        Assert.Equal("2 UDP flows from 1 process with 1 host", HostLinkText.Reach(Mechanism.Udp, [Link(one, Mechanism.Udp, 2, 1, 4)]));

        // A line says TCP before UDP, one process's with its hosts and several processes' by how many made them.
        Assert.Equal("3 TCP connections to 2 hosts · 5 UDP flows with 3 hosts",
            HostLinkText.Line([Link(one, Mechanism.Udp, 5, 3, 9), Link(one, Mechanism.Tcp, 3, 2, 4)]));
        Assert.Equal(["7 TCP connections from 2 processes to 4 hosts", "5 UDP flows from 1 process with 3 hosts"],
            HostLinkText.Parts([Link(one, Mechanism.Udp, 5, 3, 9), Link(one, Mechanism.Tcp, 3, 2, 4), Link(two, Mechanism.Tcp, 4, 4, 4)]));
        Assert.Equal(string.Empty, HostLinkText.Line([]));
    }

    [Fact(DisplayName = "§6.3: the hosts a set of processes reached are each counted once over both transports, the busiest first, and named so")]
    public void TheHostsProcessesReachedAreCountedOnceTheBusiestFirst()
    {
        ProcessInstanceId browser = Node(1, "g").Id;
        ProcessInstanceId updater = Node(2, "g").Id;
        ProcessInstanceId resolver = Node(3, "g").Id;
        HostLink[] links =
        [
            new(browser, Mechanism.Tcp, [new("142.250.1.1", 3, 40) { Ports = [443] }, new("20.42.65.1", 1, 5) { Ports = [443] }],
                RelationStrength.Direct),
            // The browser's QUIC to the host it also reached over TCP, at the same port.
            new(browser, Mechanism.Udp, [new("142.250.1.1", 1, 7) { Ports = [443] }], RelationStrength.Direct),
            new(updater, Mechanism.Tcp, [new("20.42.65.1", 2, 35) { Ports = [8443, 443] }, new("13.107.4.50", 1, 9) { Ports = [80] }],
                RelationStrength.Correlated),
            new(resolver, Mechanism.Udp, [new("9.9.9.9", 4, 9) { Ports = [53] }, new("8.8.8.8", 4, 9) { Ports = [53] }],
                RelationStrength.Direct),
        ];

        // The most records first, then the most connections over both transports, then by address; each at every port any
        // of them ran to there, once, the lowest first.
        IReadOnlyList<OtherHost> hosts = OtherHost.Of(links);
        Assert.Equal(
            [
                new OtherHost("142.250.1.1", 3, 1, 1, 47) { Ports = [443] },
                new OtherHost("20.42.65.1", 3, 0, 2, 40) { Ports = [443, 8443] },
                new OtherHost("8.8.8.8", 0, 4, 1, 9) { Ports = [53] },
                new OtherHost("9.9.9.9", 0, 4, 1, 9) { Ports = [53] },
                new OtherHost("13.107.4.50", 1, 0, 1, 9) { Ports = [80] },
            ],
            hosts);
        Assert.NotEqual(new OtherHost("13.107.4.50", 1, 0, 1, 9), hosts[^1]);
        Assert.Empty(OtherHost.Of([]));

        // Its ports, said: numbers alone in a table's cell, the lowest first, past three counted; never what they serve.
        Assert.Equal(["port 443", "ports 443 and 8443", "port 53"], hosts.Take(3).Select(host => HostLinkText.Ports(host.Ports)));
        Assert.Equal("443 and 8443", HostLinkText.PortNumbers(hosts[1].Ports));
        Assert.Equal("ports 22, 80, 443 and 2 more", HostLinkText.Ports([22, 80, 443, 3389, 8080]));
        Assert.Equal("22, 80 and 443", HostLinkText.PortNumbers([22, 80, 443]));
        Assert.Equal(string.Empty, HostLinkText.Ports([]));

        // What reached each, transport by transport and from how many processes.
        Assert.Equal("3 TCP connections and 1 UDP flow from 1 process", HostLinkText.Reached(hosts[0]));
        Assert.Equal("3 TCP connections from 2 processes", HostLinkText.Reached(hosts[1]));
        Assert.Equal("4 UDP flows from 1 process", HostLinkText.Reached(hosts[2]));

        // Named, the busiest first: all of them where they fit, else the busiest of how many.
        Assert.Equal("Busiest of 5 hosts: 142.250.1.1, 20.42.65.1 and 8.8.8.8", HostLinkText.Named(hosts, 3));
        Assert.Equal("Hosts: 142.250.1.1, 20.42.65.1 and 8.8.8.8", HostLinkText.Named([.. hosts.Take(3)], 3));
        Assert.Equal("Hosts: 142.250.1.1 and 20.42.65.1", HostLinkText.Named([.. hosts.Take(2)], 3));
        Assert.Equal("Host: 13.107.4.50", HostLinkText.Named([hosts[^1]], 3));
        Assert.Null(HostLinkText.Named([], 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => HostLinkText.Named(hosts, 0));

        // A transport's hosts are counted once whichever processes reached them.
        Assert.Equal("7 TCP connections from 2 processes to 3 hosts",
            HostLinkText.Reach(Mechanism.Tcp, [.. links.Where(link => link.Mechanism == Mechanism.Tcp)]));
        Assert.Equal("9 UDP flows from 2 processes with 3 hosts",
            HostLinkText.Reach(Mechanism.Udp, [.. links.Where(link => link.Mechanism == Mechanism.Udp)]));
    }

    private static object Drawn(GraphDisplayEdge edge) => (edge.Key, edge.SourceKey, edge.TargetKey, edge.Mechanism,
        edge.ObservationCount, edge.Strength, edge.Lane, edge.ToHosts, edge.Magnitude, edge.Unmeasured,
        string.Join(",", edge.Relationships));

    private static object Drawn(GraphDisplayNode node) => (node.Key, node.Label, node.Kind, node.Observations,
        node.ConnectedProcesses, node.Magnitude, node.Unmeasured, node.Members.Count);

    private static string[] Sorted(IEnumerable<ProcessInstanceId> ids) =>
        [.. ids.Select(id => id.ToString()).Order(StringComparer.Ordinal)];

    private static WorkspaceSnapshot Snapshot(
        IReadOnlyList<ProcessGroup> groups,
        IReadOnlyList<ProcessNode> processes,
        IReadOnlyList<CommunicationEdge> edges,
        IReadOnlyList<HostLink>? links = null) =>
        new("Other hosts test", new TimeRange(0, 10), groups, processes, edges, [], [], [], []) { HostLinks = links ?? [] };

    private static ProcessNode Node(int index, string group) => new(
        new ProcessInstanceId(Guid.Parse($"00000000-0000-0000-0000-{index:D12}")),
        1_000 + index,
        $"Process {index}",
        "test",
        group,
        0.5,
        0.5,
        CoverageState.Covered);

    private static CommunicationEdge Edge(string key, ProcessNode source, ProcessNode target, long observations) =>
        new(key, source.Id, target.Id, Mechanism.Tcp, observations, null, RelationStrength.Correlated)
        {
            Rule = RelationRule.TransportEndpoint,
            Evidence = [],
        };

    private static HostLink Link(ProcessNode process, Mechanism mechanism, int connections, int hosts, long records,
        RelationStrength strength = RelationStrength.Direct) => new(process.Id, mechanism, Addresses(connections, hosts, records),
        strength);

    /// <summary>
    /// <paramref name="hosts"/> addresses from 198.51.100.1: the first holding the connections and records the others' one
    /// each leave.
    /// </summary>
    private static HostAddress[] Addresses(int connections, int hosts, long records) =>
        [.. Enumerable.Range(1, hosts).Select(index => index == 1
            ? new HostAddress("198.51.100.1", connections - hosts + 1, records - hosts + 1)
            : new HostAddress($"198.51.100.{index}", 1, 1))];
}
