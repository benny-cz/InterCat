using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>
/// The window's Other hosts node (§6.3's unresolved resources, which remain visible): a process whose connections ran to
/// other hosts is drawn on its own, edged to one node that stands for those hosts, and wherever the graph is read it says
/// what that node and its edges are - a resource with no traffic value, whose other ends are not observed (P7) - and what
/// a click or a double click on them does.
/// </summary>
public sealed class OtherHostsWorkspaceTests
{
    [Fact(DisplayName = "§6.3: the window draws the processes reaching other hosts to Other hosts, and its summary and cards say what that node and its edges are")]
    public void TheWindowSaysWhatOtherHostsIs() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;
        GraphDisplay drawn = workspace.GraphDisplay;
        GraphDisplayNode hub = Assert.IsType<GraphDisplayNode>(drawn.OtherHosts);
        GraphDisplayNode browser = drawn.NodeOf(workspace.Snapshot.Processes.Single(node => node.ProcessId == 300).Id)!;
        GraphDisplayEdge tcp = drawn.Edges.Single(edge => edge.ToHosts && edge.Mechanism == Mechanism.Tcp);

        // The summary counts the drawn processes that reach other hosts after what the relationships are drawn from, and
        // the graph's scope says what is drawn to Other hosts - and, since svchost.exe's call is linked to nothing, how to
        // draw RPC calls.
        Assert.Equal("5 processes · 1 relationship among 2 of them · paired TCP and UDP only · 1 connects to other hosts",
            workspace.GraphSummary);
        Assert.Equal(workspace.GraphSummary + ". The graph draws admitted paired TCP and UDP, and to Other hosts each drawn process's "
            + "TCP and UDP with no process of the capture at their other end; all other observed activity is in the timeline. "
            + RelationshipCensusText.RpcGuidance(workspace.Snapshot.Census!), workspace.GraphScope);

        // The node's card: whose connections it stands for, transport by transport, the hosts they ran to - one process's,
        // so not said whose - over what, and what is not observed.
        HoverCard card = workspace.DescribeGraphHover(hub.Key)!;
        Assert.Equal("Other hosts", card.Title);
        Assert.Equal(
            [
                "Connections of 1 drawn process with no process of this capture at their other end, most often another machine's",
                "2 TCP connections from 1 process to 2 hosts · 3 records",
                "1 UDP flow from 1 process with 1 host · 1 record",
                "Hosts:",
                "  142.250.1.1 · 1 TCP connection · 2 records",
                "  20.42.65.1 · 1 TCP connection · 1 record",
                "  8.8.8.8 · 1 UDP flow · 1 record",
            ],
            card.Lines.Take(7));
        Assert.Matches(@"^Scope: \[.+\) \S+ · whole session$", card.Lines[7]);
        Assert.Equal(
            [
                "Who is at these addresses is not observed; searching one (Ctrl+F) finds the processes that reached it",
                "Edges: connections, not relationships, and counted in no total; drawn at one thickness, with no traffic value",
                "Size: fixed; a resource, not a process",
            ],
            card.Lines.Skip(8));

        // An edge's card: its process's connections over its transport, the hosts they ran to, how they bind, and what a
        // click or double click does.
        HoverCard edgeCard = workspace.DescribeGraphHover(tcp.Key)!;
        Assert.Equal("browser.exe · PID 300 → Other hosts", edgeCard.Title);
        Assert.Equal(["2 TCP connections to 2 hosts · correlated evidence", "Hosts: 142.250.1.1 and 20.42.65.1", "Records: 3"],
            edgeCard.Lines.Take(3));
        Assert.Matches(@"^Scope: \[.+\) \S+ · whole session$", edgeCard.Lines[3]);
        Assert.Equal(
            [
                "Who is at these addresses is not observed; its process's rung lists each connection",
                "Thickness: the same for every connection to other hosts, which carries no traffic value in the graph",
                "Click chooses its process; double-click opens it",
            ],
            edgeCard.Lines.Skip(4));

        // The process's own card says where its connections ran; one that reaches no other host says nothing of them.
        Assert.Equal("Other hosts: 2 TCP connections to 2 hosts · 1 UDP flow with 1 host",
            workspace.DescribeGraphHover(browser.Key)!.Lines[1]);
        GraphDisplayNode client = drawn.NodeOf(workspace.Snapshot.Processes.Single(node => node.ProcessId == 100).Id)!;
        Assert.DoesNotContain(workspace.DescribeGraphHover(client.Key)!.Lines,
            line => line.StartsWith("Other hosts", StringComparison.Ordinal));

        // The processes with neither a relationship nor a connection to another host are counted in one node, which says so.
        GraphDisplayNode quiet = drawn.Nodes.Single(node => node.Kind == GraphNodeKind.Quiet);
        Assert.Equal("2 processes with no admitted relationship and no connection to other hosts",
            workspace.DescribeGraphHover(quiet.Key)!.Lines[0]);

        // Under a brush the connections are still the whole session's, and their cards say the brush does not re-count them.
        workspace.SelectInterval(new TimeRange(workspace.Snapshot.Extent.StartTicks, workspace.Snapshot.Extent.StartTicks + 150));
        await workspace.IntervalReady;
        Assert.EndsWith("· whole session; the brushed interval does not re-count it", workspace.DescribeGraphHover(tcp.Key)!.Lines[3],
            StringComparison.Ordinal);
        Assert.Equal("Records: 3", workspace.DescribeGraphHover(tcp.Key)!.Lines[2]);
    });

    [Fact(DisplayName = "§6.7: Other hosts chosen describes itself and lists no evidence of its own; an edge to it chooses its process, and a double click opens that process")]
    public void ChoosingOtherHostsAndItsEdges() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;
        GraphDisplayNode hub = workspace.GraphDisplay.OtherHosts!;
        ProcessNode browser = workspace.Snapshot.Processes.Single(node => node.ProcessId == 300);
        GraphDisplayEdge udp = workspace.GraphDisplay.Edges.Single(edge => edge.ToHosts && edge.Mechanism == Mechanism.Udp);

        // Chosen, the node says whose connections it stands for; it stands for no process, so the evidence card goes on
        // listing the rung's own records, and nothing is ringed but the node itself.
        workspace.SelectGraphNode(hub.Key);
        Assert.Equal("Other hosts", workspace.SelectionTitle);
        Assert.Equal("Where the connections of 1 drawn process ran with no process of this capture at their other end, most "
            + "often another machine: 2 TCP connections to 2 hosts and 1 UDP flow with 1 host. Their hosts: 142.250.1.1 (1 TCP "
            + "connection, 2 records), 20.42.65.1 (1 TCP connection, 1 record) and 8.8.8.8 (1 UDP flow, 1 record). Who is at "
            + "these addresses is not observed. Searching one (Ctrl+F) finds the processes that reached it, and each process's "
            + "rung lists its own connections. Its edges are connections, not relationships, and carry no traffic value.",
            workspace.SelectionSubtitle);
        Assert.Equal("Records E lists", workspace.EvidenceHeading);
        Assert.Equal([hub.Key], workspace.SelectedGraphNodeKeys);
        Assert.Empty(workspace.PartlySelectedGraphNodeKeys);

        // It opens nothing, and Ctrl+click adds no process to a set.
        Assert.False(workspace.OpenGraphNode(hub.Key));
        workspace.ToggleGraphNodeInSelection(hub.Key);
        Assert.False(workspace.HasMultiSelection);

        // A click on an edge to it chooses the process its connections come from; a double click opens that process, whose
        // rung lists each connection.
        Assert.True(workspace.SelectGraphEdge(udp.Key));
        Assert.Equal(browser.Id, workspace.SelectedProcess?.Id);
        Assert.Equal(workspace.GraphDisplay.NodeOf(browser.Id)!.Key, workspace.SelectedGraphNodeKey);
        Assert.True(workspace.OpenGraphEdge(udp.Key));
        Assert.EndsWith("browser.exe · PID 300", workspace.Crumbs[^1].Label, StringComparison.Ordinal);

        // There the graph draws the browser's own connections to other hosts, and its edges open nothing more; the node
        // they meet at is no process the rung draws.
        GraphDisplayEdge own = workspace.GraphDisplay.Edges.Single(edge => edge.ToHosts && edge.Mechanism == Mechanism.Udp);
        Assert.Equal(1, workspace.GraphDisplay.OtherHosts!.ConnectedProcesses);
        Assert.Equal("browser.exe · PID 300: 1 process drawn · 4 more in Rest of the machine · paired TCP and UDP only · 1 connects "
            + "to other hosts", workspace.GraphSummary);
        Assert.DoesNotContain(workspace.DescribeGraphHover(own.Key)!.Lines, line => line.Contains("double-click", StringComparison.Ordinal));
        Assert.False(workspace.OpenGraphEdge(own.Key));

        // At the client's rung the browser is the rest of the machine, whose connections are left to its own rung: no
        // Other hosts node is drawn, and the context node's card says nothing of them.
        workspace.ReturnTo(0);
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        Assert.True(workspace.OpenGraphNode(workspace.GraphDisplay.NodeOf(client.Id)!.Key));
        Assert.Null(workspace.GraphDisplay.OtherHosts);
        GraphDisplayNode context = workspace.GraphDisplay.Nodes.Single(node => node.Kind == GraphNodeKind.Context);
        Assert.Contains(browser.Id, context.Members);
        Assert.DoesNotContain(workspace.DescribeGraphHover(context.Key)!.Lines,
            line => line.StartsWith("Other hosts", StringComparison.Ordinal));
        Assert.DoesNotContain("other hosts", workspace.GraphSummary, StringComparison.Ordinal);
    });

    [Fact(DisplayName = "§6.3: Other hosts names the busiest hosts the drawn processes reached, on its card, its edges' and its description, and on a process's rung that process's alone")]
    public void OtherHostsNamesTheBusiestHosts() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, BusyRows());
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;
        GraphDisplay drawn = workspace.GraphDisplay;
        GraphDisplayNode hub = drawn.OtherHosts!;
        GraphDisplayEdge Edge(int pid, Mechanism mechanism) => drawn.Edges.Single(edge => edge.ToHosts && edge.Mechanism == mechanism
            && edge.SourceKey == drawn.NodeOf(workspace.Snapshot.Processes.Single(node => node.ProcessId == pid).Id)!.Key);

        // A host is counted once whichever processes and transports reached it, the busiest by records first: the browser's
        // TCP and QUIC to one, the browser's and the updater's TCP to another.
        HoverCard card = workspace.DescribeGraphHover(hub.Key)!;
        Assert.Equal(
            [
                "Connections of 3 drawn processes with no process of this capture at their other end, most often another machine's",
                "7 TCP connections from 2 processes to 5 hosts · 10 records",
                "2 UDP flows from 2 processes with 2 hosts · 4 records",
                "Busiest 3 of 6 hosts:",
                "  142.250.1.1 · 2 TCP connections and 1 UDP flow from 1 process · 5 records",
                "  20.42.65.1 · 2 TCP connections from 2 processes · 4 records",
                "  8.8.8.8 · 1 UDP flow from 1 process · 2 records",
            ],
            card.Lines.Take(7));
        Assert.StartsWith("Scope: ", card.Lines[7], StringComparison.Ordinal);

        // Its edges name their own process's hosts, the busiest first.
        Assert.Equal("Busiest of 4 hosts: 142.250.1.1, 20.42.65.1 and 52.1.1.1", workspace.DescribeGraphHover(Edge(300, Mechanism.Tcp).Key)!.Lines[1]);
        Assert.Equal("Hosts: 20.42.65.1 and 13.107.4.50", workspace.DescribeGraphHover(Edge(310, Mechanism.Tcp).Key)!.Lines[1]);
        Assert.Equal("Host: 8.8.8.8", workspace.DescribeGraphHover(Edge(320, Mechanism.Udp).Key)!.Lines[1]);

        // Chosen, it names more of them, ties by address.
        workspace.SelectGraphNode(hub.Key);
        Assert.Contains("2 UDP flows from 2 processes with 2 hosts. The busiest of their 6 hosts: 142.250.1.1 (2 TCP connections "
            + "and 1 UDP flow from 1 process, 5 records), 20.42.65.1 (2 TCP connections from 2 processes, 4 records), 8.8.8.8 "
            + "(1 UDP flow from 1 process, 2 records), 13.107.4.50 (1 TCP connection from 1 process, 1 record) and 52.1.1.1 (1 "
            + "TCP connection from 1 process, 1 record). Who is at", workspace.SelectionSubtitle, StringComparison.Ordinal);

        // On the browser's rung Other hosts stands for the browser's connections alone: the updater's to the host they share
        // is left to the updater's rung.
        workspace.ReturnTo(0);
        Assert.True(workspace.OpenGraphNode(drawn.NodeOf(workspace.Snapshot.Processes.Single(node => node.ProcessId == 300).Id)!.Key));
        Assert.Equal(
            [
                "Busiest 3 of 4 hosts:",
                "  142.250.1.1 · 2 TCP connections and 1 UDP flow · 5 records",
                "  20.42.65.1 · 1 TCP connection · 1 record",
                "  52.1.1.1 · 1 TCP connection · 1 record",
            ],
            workspace.DescribeGraphHover(workspace.GraphDisplay.OtherHosts!.Key)!.Lines.Skip(3).Take(4));
    });

    [Fact(DisplayName = "§6.3: a graph's summary counts the nodes its processes are drawn as, never the Other hosts node")]
    public void TheSummaryCountsNoOtherHostsNode() => SingleThreadedContext.Run(async () =>
    {
        // Thirty workers, each with a connection to another host: drawn on their own they would pass the threshold, so
        // their group collapses to one node, edged to Other hosts.
        ProcessGroup pool = new("pool", "worker.exe", LaneGrouping.Executable);
        ProcessNode[] workers = [.. Enumerable.Range(1, 30).Select(index => new ProcessNode(
            new ProcessInstanceId(Guid.Parse($"00000000-0000-0000-0000-{index:D12}")), 1_000 + index, "worker.exe", "test",
            pool.Key, 0.5, 0.5, CoverageState.Covered))];
        WorkspaceSnapshot snapshot = new("Other hosts summary", new TimeRange(0, 10), [pool], workers, [], [], [], [], [])
        {
            HostLinks = [.. workers.Select(worker => new HostLink(worker.Id, Mechanism.Tcp, [new HostAddress("203.0.113.1", 1, 2)],
                RelationStrength.Correlated))],
        };
        using var workspace = new WorkspaceViewModel(snapshot, "other-hosts-summary");
        await workspace.LayoutReady;

        Assert.Equal(2, workspace.GraphDisplay.Nodes.Count);
        Assert.Equal("30 processes · no relationship drawn; observed records may still be in the timeline · drawn as 1 node · "
            + "30 connect to other hosts", workspace.GraphSummary);
    });

    [Fact(DisplayName = "§6.4: a chosen TCP cell highlights the relationships its records belong to, never an edge to the other hosts")]
    public void AChosenCellHighlightsNoEdgeToOtherHosts() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;
        GraphDisplayEdge relationship = workspace.GraphDisplay.Edges.Single(edge => !edge.ToHosts);

        // The first TCP cell holds the relationship's records and the browser's, sent as the client's were.
        TimelineBucket cell = workspace.Snapshot.MechanismLanes.Single(lane => lane.Mechanism == Mechanism.Tcp).Buckets
            .First(bucket => bucket.ObservationCount > 0);
        workspace.ChooseTimelineCell(cell, Mechanism.Tcp);
        await workspace.IntervalReady;
        Assert.Equal([relationship.Key], workspace.CellGraphEdges);
    });

    private static WorkspaceViewModel Open(TemporarySession session)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        return new(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
    }

    /// <summary>
    /// browser.exe's TCP to four hosts, two connections to the busiest, and its QUIC to that host too; updater.exe's TCP to
    /// one of the browser's hosts and to another; and svchost.exe's UDP to a resolver.
    /// </summary>
    private static ObservationRowV1[] BusyRows()
    {
        ulong ordinal = 1;
        ObservationRowV1 Send(int pid, string local, string remote, Mechanism mechanism = Mechanism.Tcp) =>
            Transfer(200 + (long)ordinal, ObservationKind.Send, AccountingSide.SendSide, 100, pid, ordinal++).Between(local, remote)
                with
                {
                    Mechanism = mechanism, EventId = (ushort)(mechanism == Mechanism.Udp ? 42 : 10), SessionRelativeTicks = 200 + (long)ordinal,
                };
        return
        [
            Lifecycle(1, ObservationKind.Create, 300, ordinal++) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 100 },
            Lifecycle(2, ObservationKind.Create, 310, ordinal++) with { ResourceName = @"C:\Apps\updater.exe", SessionRelativeTicks = 110 },
            Lifecycle(3, ObservationKind.Create, 320, ordinal++) with { ResourceName = @"C:\Windows\svchost.exe", SessionRelativeTicks = 120 },
            Send(300, "192.168.1.10:52000", "142.250.1.1:443"),
            Send(300, "192.168.1.10:52000", "142.250.1.1:443"),
            Send(300, "192.168.1.10:52001", "142.250.1.1:443"),
            Send(300, "192.168.1.10:61000", "142.250.1.1:443", Mechanism.Udp),
            Send(300, "192.168.1.10:61000", "142.250.1.1:443", Mechanism.Udp),
            Send(300, "192.168.1.10:52002", "20.42.65.1:443"),
            Send(300, "192.168.1.10:52003", "52.1.1.1:443"),
            Send(300, "192.168.1.10:52004", "52.1.1.2:443"),
            Send(310, "192.168.1.10:53000", "20.42.65.1:443"),
            Send(310, "192.168.1.10:53000", "20.42.65.1:443"),
            Send(310, "192.168.1.10:53000", "20.42.65.1:443"),
            Send(310, "192.168.1.10:53001", "13.107.4.50:443"),
            Send(320, "192.168.1.10:61001", "8.8.8.8:53", Mechanism.Udp),
            Send(320, "192.168.1.10:61001", "8.8.8.8:53", Mechanism.Udp),
        ];
    }

    /// <summary>
    /// client.exe and server.exe related over loopback; browser.exe's TCP to two other hosts and its UDP to a resolver, as
    /// the client's first sends go; svchost.exe's one RPC call; and idle.exe, which does nothing the graph draws.
    /// </summary>
    private static ObservationRowV1[] Rows() =>
    [
        Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 100 },
        Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe", SessionRelativeTicks = 110 },
        Lifecycle(3, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 120 },
        Lifecycle(4, ObservationKind.Create, 400, 4) with { ResourceName = @"C:\Windows\svchost.exe", SessionRelativeTicks = 130 },
        Lifecycle(5, ObservationKind.Create, 500, 5) with { ResourceName = @"C:\Tools\idle.exe", SessionRelativeTicks = 140 },
        Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 6)
            .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 200 },
        Transfer(21, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 7)
            .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 200 },
        Transfer(22, ObservationKind.Send, AccountingSide.SendSide, 500, 300, 8)
            .Between("192.168.1.10:52000", "142.250.1.1:443") with { SessionRelativeTicks = 200 },
        Transfer(23, ObservationKind.Receive, AccountingSide.ReceiveSide, 900, 300, 9)
            .Between("192.168.1.10:52000", "142.250.1.1:443") with { SessionRelativeTicks = 200 },
        Transfer(24, ObservationKind.Send, AccountingSide.SendSide, 500, 300, 10)
            .Between("192.168.1.10:52001", "20.42.65.1:443") with { SessionRelativeTicks = 300 },
        Transfer(25, ObservationKind.Send, AccountingSide.SendSide, 40, 300, 11)
            .Between("192.168.1.10:61000", "8.8.8.8:53") with { Mechanism = Mechanism.Udp, EventId = 42, SessionRelativeTicks = 300 },
        RpcCall(40, ObservationKind.RequestStart, Direction.Outbound, 400, 12, Guid.NewGuid(), ServiceControlInterface)
            with { SessionRelativeTicks = 400 },
    ];
}
