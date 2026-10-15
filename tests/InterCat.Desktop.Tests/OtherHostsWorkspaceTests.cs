using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
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
                "Double-click or Enter draws its busiest hosts on their own",
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
            + "often another machine: 2 TCP connections to 2 hosts and 1 UDP flow with 1 host. Who is at these addresses is not "
            + "observed. Its hosts are listed below, the busiest first: the timeline highlights the records of every connection "
            + "to the one chosen and E lists them, and Enter on one finds the processes that reached it, as searching its "
            + "address (Ctrl+F) does. Each process's rung lists its own connections. Its edges are connections, not "
            + "relationships, and carry no traffic value. Enter draws its busiest hosts on their own.",
            workspace.SelectionSubtitle);
        Assert.Equal(["142.250.1.1 | 1 TCP connection · port 443 · 2 records", "20.42.65.1 | 1 TCP connection · port 443 · 1 record",
                "8.8.8.8 | 1 UDP flow · port 53 · 1 record"],
            workspace.HostRows.Select(row => row.Address + " | " + row.Detail));
        Assert.Equal("Records E lists", workspace.EvidenceHeading);
        Assert.Equal([hub.Key], workspace.SelectedGraphNodeKeys);
        Assert.Empty(workspace.PartlySelectedGraphNodeKeys);

        // Ctrl+click adds no process to a set: it holds none.
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

    [Fact(DisplayName = "§6.3: Other hosts names the busiest hosts the drawn processes reached, on its card and its edges', lists them all once chosen, and on a process's rung that process's alone")]
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

        // Chosen, it lists every one of them in the inspector, beneath a description that says so, ties by address.
        workspace.SelectGraphNode(hub.Key);
        Assert.Contains("2 UDP flows from 2 processes with 2 hosts. Who is at these addresses is not observed. Its hosts are "
            + "listed below, the busiest first:", workspace.SelectionSubtitle, StringComparison.Ordinal);
        Assert.Equal(
            [
                "142.250.1.1 | 2 TCP connections and 1 UDP flow from 1 process · port 443 · 5 records",
                "20.42.65.1 | 2 TCP connections from 2 processes · port 443 · 4 records",
                "8.8.8.8 | 1 UDP flow from 1 process · port 53 · 2 records",
                "13.107.4.50 | 1 TCP connection from 1 process · port 443 · 1 record",
                "52.1.1.1 | 1 TCP connection from 1 process · port 443 · 1 record",
                "52.1.1.2 | 1 TCP connection from 1 process · port 443 · 1 record",
            ],
            workspace.HostRows.Select(row => row.Address + " | " + row.Detail));

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

    [Fact(DisplayName = "§6.3: Enter on Other hosts draws its busiest hosts on their own, each named and described, the rest kept in it; Enter on a host folds them back")]
    public void OtherHostsOpensIntoItsBusiestHosts() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, TenHosts());
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;
        string[] folded = [.. workspace.GraphDisplay.Nodes.Select(node => node.Key)];
        string summary = workspace.GraphSummary;
        GraphDisplayNode hub = workspace.GraphDisplay.OtherHosts!;
        Assert.False(workspace.HostsOpened);
        Assert.Equal("Double-click or Enter draws its busiest hosts on their own", workspace.DescribeGraphHover(hub.Key)!.Lines[^1]);

        // Enter on Other hosts draws its eight busiest hosts on their own, by records; the two quietest stay in it.
        Assert.True(workspace.OpenGraphNode(hub.Key));
        await workspace.LayoutReady;
        Assert.True(workspace.HostsOpened);
        GraphDisplay opened = workspace.GraphDisplay;
        Assert.Equal([.. Enumerable.Range(1, 8).Select(index => $"52.0.0.{index}")],
            opened.Nodes.Where(node => node.Kind == GraphNodeKind.Host).Select(node => node.Label));
        Assert.Equal(summary, workspace.GraphSummary);
        HoverCard rest = workspace.DescribeGraphHover(hub.Key)!;
        Assert.Equal(["2 TCP connections from 1 process to 2 hosts · 3 records", "Hosts:", "  52.0.0.9 · 1 TCP connection · 2 records",
                "  52.0.0.10 · 1 TCP connection · 1 record"],
            rest.Lines.Skip(1).Take(4));
        Assert.Equal("Double-click or Enter folds the hosts drawn on their own back into it", rest.Lines[^1]);

        // A host's card says whose connections ran to it, from how many processes, and that Enter folds the hosts back.
        GraphDisplayNode shared = opened.Node(GraphProjection.HostKey("52.0.0.1"))!;
        HoverCard card = workspace.DescribeGraphHover(shared.Key)!;
        Assert.Equal("52.0.0.1", card.Title);
        Assert.Equal(
            [
                "Connections of 2 drawn processes to this address, with no process of this capture at their other end",
                "2 TCP connections from 2 processes · 11 records",
                "Port 443",
                "Reached by browser.exe · PID 300 and updater.exe · PID 310",
            ],
            card.Lines.Take(4));
        Assert.Equal("Double-click or Enter folds the hosts drawn on their own back into Other hosts", card.Lines[^1]);

        // An edge to a host counts its source's connections there alone.
        GraphDisplayEdge toShared = opened.Edges.Single(edge => edge.TargetKey == shared.Key
            && edge.SourceKey == opened.NodeOf(Process(workspace, 300).Id)!.Key);
        Assert.Equal(["1 TCP connection to 1 host · correlated evidence", "Host: 52.0.0.1", "Records: 10"],
            workspace.DescribeGraphHover(toShared.Key)!.Lines.Take(3));

        // Chosen, a host describes itself; the keyboard reaches the hosts after the processes, the busiest first, then Other
        // hosts.
        workspace.SelectGraphNode(shared.Key);
        Assert.Equal("52.0.0.1", workspace.SelectionTitle);
        Assert.StartsWith("52.0.0.1: where the connections of 2 drawn processes ran with no process of this capture at their other "
            + "end: 2 TCP connections from 2 processes, 11 records, at port 443. Reached by browser.exe · PID 300 and updater.exe · "
            + "PID 310.",
            workspace.SelectionSubtitle, StringComparison.Ordinal);
        Assert.Equal("Selected host", workspace.EvidenceHeading);
        Assert.Equal([GraphNodeKind.Process, GraphNodeKind.Process, .. Enumerable.Repeat(GraphNodeKind.Host, 8), GraphNodeKind.OtherHosts],
            GraphView.Traversal(opened).Select(node => node.Kind));
        Assert.Equal([.. Enumerable.Range(1, 8).Select(index => GraphProjection.HostKey($"52.0.0.{index}")), hub.Key],
            GraphView.Traversal(opened).Where(node => node.IsHost).Select(node => node.Key));

        // A search marks the host whose address it finds, and the processes that reached it, and its card says so.
        workspace.SearchText = "52.0.0.1";
        Assert.Contains(shared.Key, workspace.SearchedGraphNodeKeys);
        Assert.Equal("Found by the search: its address", workspace.DescribeGraphHover(shared.Key)!.Lines[^1]);
        Assert.DoesNotContain(hub.Key, workspace.SearchedGraphNodeKeys);
        workspace.SearchText = "52.0.0.9";
        Assert.Contains(hub.Key, workspace.SearchedGraphNodeKeys);
        workspace.SearchText = string.Empty;

        // Enter on a host folds them back: the drawing is the folded one again.
        Assert.True(workspace.OpenGraphNode(shared.Key));
        await workspace.LayoutReady;
        Assert.False(workspace.HostsOpened);
        Assert.Equal(folded, workspace.GraphDisplay.Nodes.Select(node => node.Key));
    });

    [Fact(DisplayName = "§26.3: a workspace made with Other hosts opened, as its investigation or an earlier publication left it, draws its first graph so and lays it out once, and the rail names it until it is folded")]
    public void AWorkspaceMadeWithOtherHostsOpenedDrawsItSo() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, TenHosts());
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation), hostsOpened: true);

        // Its first drawing is the opened one, and the layout begun for it is the one that arrives.
        Assert.True(workspace.HostsOpened);
        Assert.Equal(8, workspace.GraphDisplay.OpenedHosts!.Count);
        Task first = workspace.LayoutReady;
        await first;
        Assert.Same(first, workspace.LayoutReady);
        Assert.Equal(["its busiest other hosts drawn on their own"], workspace.ChangedSettings);

        // Folded by its node, the setting is at its default again, and the rail is told so.
        var changed = new List<string?>();
        workspace.PropertyChanged += (_, eventArgs) => changed.Add(eventArgs.PropertyName);
        Assert.True(workspace.OpenGraphNode(GraphProjection.OtherHostsKey));
        Assert.False(workspace.DiffersFromDefaults);
        Assert.Contains(nameof(WorkspaceViewModel.ChangedSettingsSummary), changed);
        Assert.Null(workspace.GraphDisplay.OpenedHosts);
    });

    [Fact(DisplayName = "§6.3: Other hosts chosen lists every host it stands for, the busiest first, and a host chosen there finds the processes that reached it")]
    public void OtherHostsListsItsHosts() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, TenHosts());
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;
        GraphDisplayNode hub = workspace.GraphDisplay.OtherHosts!;
        string browser = workspace.GraphDisplay.NodeOf(Process(workspace, 300).Id)!.Key;
        string updater = workspace.GraphDisplay.NodeOf(Process(workspace, 310).Id)!.Key;

        // Nothing is listed until Other hosts is chosen.
        Assert.False(workspace.ListsHosts);
        Assert.Empty(workspace.HostRows);

        // Chosen, it lists its ten hosts, the busiest first, each with what reached it and the records its connections
        // hold, and each said whole to a screen reader.
        workspace.SelectGraphNode(hub.Key);
        Assert.True(workspace.ListsHosts);
        Assert.Equal("Its 10 hosts, the busiest first", workspace.HostsHeading);
        Assert.Equal([.. Enumerable.Range(1, 10).Select(index => $"52.0.0.{index}")], workspace.HostRows.Select(row => row.Address));
        Assert.Equal("2 TCP connections from 2 processes · port 443 · 11 records", workspace.HostRows[0].Detail);
        Assert.Equal("1 TCP connection from 1 process · port 443 · 1 record", workspace.HostRows[^1].Detail);
        Assert.Equal("52.0.0.1, 2 TCP connections from 2 processes · port 443 · 11 records. Press E to list the records of its "
            + "connections, or Enter to find the processes that reached it.", workspace.HostRows[0].AccessibleName);
        Assert.False(workspace.HasHostsNote);
        Assert.Equal(string.Empty, workspace.HostsNote);

        // A host chosen there finds the processes that reached it, as a search for its address does, and Other hosts stays
        // chosen; a host it does not list finds nothing.
        Assert.True(workspace.FindHost(workspace.HostRows[0]));
        Assert.Equal("52.0.0.1", workspace.SearchText);
        Assert.Equal(hub.Key, workspace.SelectedGraphNodeKey);
        Assert.Equal(new[] { browser, updater, hub.Key }.Order(StringComparer.Ordinal),
            workspace.SearchedGraphNodeKeys.Order(StringComparer.Ordinal));
        Assert.False(workspace.FindHost(new HostRow("192.0.2.1", "listed nowhere")));
        Assert.Equal("52.0.0.1", workspace.SearchText);

        // Opened, it lists only the hosts it still stands for, those not drawn on their own, and says so as the drawing
        // changes.
        var changed = new List<string?>();
        workspace.PropertyChanged += (_, eventArgs) => changed.Add(eventArgs.PropertyName);
        Assert.True(workspace.OpenGraphNode(hub.Key));
        await workspace.LayoutReady;
        Assert.Contains(nameof(WorkspaceViewModel.HostRows), changed);
        Assert.Equal(hub.Key, workspace.SelectedGraphNodeKey);
        Assert.Equal(["52.0.0.9", "52.0.0.10"], workspace.HostRows.Select(row => row.Address));
        Assert.Equal("Its 2 hosts, the busiest first", workspace.HostsHeading);

        // Another node chosen lists none: a host drawn on its own, which its card describes, or a process.
        workspace.SelectGraphNode(GraphProjection.HostKey("52.0.0.1"));
        Assert.Equal("52.0.0.1", workspace.SelectionTitle);
        Assert.False(workspace.ListsHosts);
        workspace.SelectGraphNode(browser);
        Assert.False(workspace.ListsHosts);
        Assert.Empty(workspace.HostRows);
        Assert.Equal(string.Empty, workspace.HostsHeading);
    });

    [Fact(DisplayName = "§6.3: Other hosts standing for one host lists it as one, and its description says so")]
    public void OtherHostsListsItsOneHost() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, ManyHosts(1));
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;
        workspace.SelectGraphNode(workspace.GraphDisplay.OtherHosts!.Key);

        Assert.Equal("Its host", workspace.HostsHeading);
        Assert.Equal(["52.0.1.1 | 1 TCP connection · port 443 · 3 records"], workspace.HostRows.Select(row => row.Address + " | " + row.Detail));
        Assert.Contains(" Its host is listed below: chosen, the timeline highlights the records of every connection to it and E "
            + "lists them, and Enter on it finds the processes that reached it, as searching its address (Ctrl+F) does. ",
            workspace.SelectionSubtitle, StringComparison.Ordinal);
    });

    [Fact(DisplayName = "§6.3: Other hosts lists its busiest hundred hosts, and says how many more there are and how to find them")]
    public void OtherHostsListsItsBusiestHundred() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, ManyHosts(105));
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;
        workspace.SelectGraphNode(workspace.GraphDisplay.OtherHosts!.Key);

        Assert.Equal(WorkspaceViewModel.HostsListed, workspace.HostRows.Count);
        Assert.Equal("Its busiest 100 of 105 hosts", workspace.HostsHeading);
        Assert.True(workspace.HasHostsNote);
        Assert.Equal("5 more, quieter. Searching an address (Ctrl+F) finds the processes that reached any of them.",
            workspace.HostsNote);

        // The busiest come first: the host with the most records, then those with one, by address.
        Assert.Equal(("52.0.1.105", "1 TCP connection · port 443 · 3 records"), (workspace.HostRows[0].Address, workspace.HostRows[0].Detail));
        Assert.Equal("52.0.1.1", workspace.HostRows[1].Address);

        // The last by address of those with one record is past the hundredth, so it is not listed and choosing it finds
        // nothing; a search for its address finds it.
        const string Quietest = "52.0.1.99";
        Assert.DoesNotContain(workspace.HostRows, row => row.Address == Quietest);
        Assert.False(workspace.FindHost(new HostRow(Quietest, string.Empty)));
        workspace.SearchText = Quietest;
        Assert.Contains(workspace.GraphDisplay.OtherHosts!.Key, workspace.SearchedGraphNodeKeys);
    });

    [Fact(DisplayName = "§6.3: an other host's address scope is said where it is listed and drawn, a multicast group's as whoever joined it")]
    public void AnOtherHostsScopeIsSaid() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, ScopedRows());
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;
        GraphDisplayNode hub = workspace.GraphDisplay.OtherHosts!;

        // Listed, each scoped host says its scope first; a public address says none.
        workspace.SelectGraphNode(hub.Key);
        Assert.Equal(
            [
                "239.255.255.250 | multicast · 1 UDP flow from 1 process · port 1900 · 3 records",
                "192.168.1.20 | private · 1 TCP connection from 1 process · port 445 · 2 records",
                "142.250.1.1 | 1 TCP connection from 1 process · port 443 · 1 record",
            ],
            workspace.HostRows.Select(row => row.Address + " | " + row.Detail));

        // Other hosts' card says it beside the address of each host it names.
        Assert.Contains("  239.255.255.250 (multicast) · 1 UDP flow from 1 process · 3 records", workspace.DescribeGraphHover(hub.Key)!.Lines);
        Assert.Contains("  142.250.1.1 · 1 TCP connection from 1 process · 1 record", workspace.DescribeGraphHover(hub.Key)!.Lines);

        // Drawn on its own, a multicast group says so beneath its address, and its card and description what it means.
        Assert.True(workspace.OpenGraphNode(hub.Key));
        await workspace.LayoutReady;
        GraphDisplayNode group = workspace.GraphDisplay.Node(GraphProjection.HostKey("239.255.255.250"))!;
        Assert.Equal("multicast · from 1 process", GraphView.Detail(group));
        Assert.Equal("from 1 process", GraphView.Detail(workspace.GraphDisplay.Node(GraphProjection.HostKey("142.250.1.1"))!));
        const string Meaning = "A multicast group's address: whoever joined the group receives what is sent to it";
        Assert.Contains(Meaning, workspace.DescribeGraphHover(group.Key)!.Lines);
        workspace.SelectGraphNode(group.Key);
        Assert.Contains(Meaning + ". Reached by ", workspace.SelectionSubtitle, StringComparison.Ordinal);
        Assert.DoesNotContain(workspace.DescribeGraphHover(GraphProjection.HostKey("142.250.1.1"))!.Lines,
            line => line.Contains("address:", StringComparison.Ordinal) || line.StartsWith("A private", StringComparison.Ordinal));
    });

    [Fact(DisplayName = "§6.3: a group collapsed for its size keeps its edge to Other hosts, whose card says a click chooses the group and a double click opens it, as they do")]
    public void ACollapsedGroupsEdgeToOtherHostsChoosesAndOpensIt() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, GroupReachingHosts(26));
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;

        // Past the threshold its instances are drawn as one group, edged to Other hosts once.
        GraphDisplayNode group = workspace.GraphDisplay.Nodes.Single(node => node.Kind == GraphNodeKind.Group);
        Assert.Equal(26, group.Members.Count);
        GraphDisplayEdge edge = workspace.GraphDisplay.Edges.Single(drawn => drawn.ToHosts && drawn.SourceKey == group.Key);
        Assert.Equal(26, workspace.GraphDisplay.OtherHosts!.ConnectedProcesses);
        HoverCard card = workspace.DescribeGraphHover(edge.Key)!;
        Assert.StartsWith("svc.exe → Other hosts", card.Title, StringComparison.Ordinal);
        Assert.Equal("Click chooses the group; double-click opens it", card.Lines[^1]);

        // A click chooses the group; a double click opens it, whose rung lists its instances.
        Assert.True(workspace.SelectGraphEdge(edge.Key));
        Assert.Equal(group.Key, workspace.SelectedGraphNodeKey);
        Assert.NotNull(workspace.SelectedGroup);
        Assert.True(workspace.OpenGraphEdge(edge.Key));
        Assert.Equal((1, true), (workspace.Crumbs[^1].Depth, workspace.Crumbs[^1].IsCurrent));
        Assert.Equal("Group: svc.exe", workspace.Crumbs[^1].Label);
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

    [Fact(DisplayName = "§6.4: a host drawn on its own, chosen, is the records of every connection to it, whichever process held it: the card counts them over the scope the rows count, the timeline highlights them and E lists them")]
    public void AChosenHostIsItsConnectionsRecords() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, HostOverTime());
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;
        workspace.RequestTimelineDetail(workspace.Snapshot.Extent, 100);
        Assert.True(workspace.OpenGraphNode(workspace.GraphDisplay.OtherHosts!.Key));
        await workspace.LayoutReady;
        string host = GraphProjection.HostKey("52.0.0.1");

        // Chosen, its card counts the records of the browser's connection to it and the updater's two, and what they
        // measured, once read; the timeline highlights the same records, and its description and card say so.
        var said = new List<string?>();
        workspace.PropertyChanged += (_, changed) => said.Add(changed.PropertyName);
        workspace.SelectGraphNode(host);
        Assert.Equal(("Selected host", "Reading the records of its connections…"), (workspace.EvidenceHeading, workspace.EvidenceSummary));
        Assert.Equal("Counting the selection, connections to 52.0.0.1…", workspace.TimelineSelectionCaption);
        await workspace.HostRecordsReady;
        Assert.Equal("5 records of 2 TCP connections and 1 UDP flow from 2 processes · 420 B sent, 300 B received",
            workspace.EvidenceSummary);
        await workspace.HighlightReady;
        Assert.Equal(5, workspace.TimelineHighlightBuckets!.Sum(bucket => bucket.ObservationCount));
        Assert.EndsWith(" · selection highlighted: connections to 52.0.0.1", workspace.TimelineCaption, StringComparison.Ordinal);

        // The header shows the highlight apart from the rest of the caption, and says so each time either changes.
        Assert.Equal("Selection highlighted: connections to 52.0.0.1", workspace.TimelineSelectionCaption);
        Assert.Equal(workspace.TimelineRungCaption + " · selection highlighted: connections to 52.0.0.1", workspace.TimelineCaption);
        Assert.Contains(nameof(WorkspaceViewModel.TimelineSelectionCaption), said);
        Assert.Contains(nameof(WorkspaceViewModel.TimelineRungCaption), said);
        Assert.Contains("The timeline highlights the records of every connection to it, whichever process held it, and E lists "
            + "them; searching it (Ctrl+F) marks the processes that reached it.", workspace.SelectionSubtitle, StringComparison.Ordinal);
        Assert.Contains("Click highlights the records of every connection to it, whichever process held it, and E lists them",
            workspace.DescribeGraphHover(host)!.Lines);

        // E lists exactly them, its filter naming the host and why.
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.Equal(5, workspace.RungRows.Count);
        Assert.StartsWith("Records of the connections to 52.0.0.1", workspace.EvidenceScopeText, StringComparison.Ordinal);
        FilterRow scope = Assert.Single(workspace.Filters, filter => filter.Field == "scope");
        Assert.Equal("Records of: Connections to 52.0.0.1", scope.Chip);
        Assert.EndsWith("with the host 52.0.0.1 chosen: every connection to it, whichever process held it.", scope.Reason,
            StringComparison.Ordinal);
        Assert.True(workspace.Ascend());
        await workspace.LayoutReady;

        // Brushed, the card counts the records in the interval, and E lists those; where none was made, the card says so.
        workspace.SelectGraphNode(host);
        workspace.SelectInterval(new TimeRange(25, 45));
        await workspace.IntervalReady;
        await workspace.HostRecordsReady;
        Assert.Equal("2 records of 2 TCP connections from 2 processes · 50 B sent, 300 B received", workspace.EvidenceSummary);
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.Equal(2, workspace.RungRows.Count);
        Assert.True(workspace.Ascend());
        await workspace.LayoutReady;
        workspace.SelectGraphNode(host);
        workspace.SelectInterval(new TimeRange(12, 18));
        await workspace.IntervalReady;
        await workspace.HostRecordsReady;
        Assert.Equal("No record of a connection to it in this interval", workspace.EvidenceSummary);
    });

    [Fact(DisplayName = "§6.4: a host chosen in Other hosts' list is the records of every connection to it, as one drawn on its own is, kept as the list restates its rows and let go by any node chosen in the graph")]
    public void AHostChosenInOtherHostsListIsItsRecords() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, TenHosts());
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;
        workspace.RequestTimelineDetail(workspace.Snapshot.Extent, 100);
        GraphDisplayNode hub = workspace.GraphDisplay.OtherHosts!;
        string browser = workspace.GraphDisplay.NodeOf(Process(workspace, 300).Id)!.Key;

        // None is chosen until Other hosts is, nor a host it does not list.
        workspace.ChosenHostRow = new HostRow("52.0.0.9", string.Empty);
        Assert.Null(workspace.ChosenHostRow);
        workspace.SelectGraphNode(hub.Key);
        workspace.ChosenHostRow = new HostRow("192.0.2.1", "listed nowhere");
        Assert.Null(workspace.ChosenHostRow);
        Assert.Equal("Records E lists", workspace.EvidenceHeading);

        // Chosen there, a host the graph never draws on its own is its connections' records: the card counts them, and the
        // timeline highlights them.
        workspace.ChosenHostRow = workspace.HostRows.Single(row => row.Address == "52.0.0.9");
        Assert.Equal("52.0.0.9", workspace.ChosenHostRow?.Address);
        Assert.Equal(("Selected host", "Reading the records of its connections…"), (workspace.EvidenceHeading, workspace.EvidenceSummary));
        await workspace.HostRecordsReady;
        Assert.Equal("2 records of 1 TCP connection from 1 process · 200 B sent, no receive recorded", workspace.EvidenceSummary);
        await workspace.HighlightReady;
        Assert.Equal(2, workspace.TimelineHighlightBuckets!.Sum(bucket => bucket.ObservationCount));
        Assert.EndsWith(" · selection highlighted: connections to 52.0.0.9", workspace.TimelineCaption, StringComparison.Ordinal);

        // The list given new rows, as when Other hosts opens, writes back that none is chosen; the choice is kept.
        void LetGo(object? sender, System.ComponentModel.PropertyChangedEventArgs changed)
        {
            if (changed.PropertyName == nameof(WorkspaceViewModel.HostRows))
            {
                workspace.ChosenHostRow = null;
            }
        }

        workspace.PropertyChanged += LetGo;
        Assert.True(workspace.OpenGraphNode(hub.Key));
        await workspace.LayoutReady;
        workspace.PropertyChanged -= LetGo;
        Assert.Equal("52.0.0.9", workspace.ChosenHostRow?.Address);

        // E lists them.
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.Equal(2, workspace.RungRows.Count);
        Assert.StartsWith("Records of the connections to 52.0.0.9", workspace.EvidenceScopeText, StringComparison.Ordinal);
        Assert.True(workspace.Ascend());
        await workspace.LayoutReady;

        // Let go, the card counts the rung's records again; any node chosen in the graph, Other hosts included, lets it go.
        workspace.SelectGraphNode(hub.Key);
        workspace.ChosenHostRow = workspace.HostRows[0];
        workspace.ChosenHostRow = null;
        Assert.Equal("Records E lists", workspace.EvidenceHeading);
        workspace.ChosenHostRow = workspace.HostRows[0];
        workspace.SelectGraphNode(hub.Key);
        Assert.Null(workspace.ChosenHostRow);
        workspace.ChosenHostRow = workspace.HostRows[0];
        workspace.SelectGraphNode(browser);
        Assert.Equal("Selected process", workspace.EvidenceHeading);
        workspace.SelectGraphNode(hub.Key);
        Assert.Null(workspace.ChosenHostRow);
        Assert.Equal("Records E lists", workspace.EvidenceHeading);
    });

    [Fact(DisplayName = "§6.4: a chosen host whose records cannot be read says so, never that it holds none")]
    public void AChosenHostsUnreadableRecordsAreSaidSo() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, HostOverTime());
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;
        Assert.True(workspace.OpenGraphNode(workspace.GraphDisplay.OtherHosts!.Key));
        await workspace.LayoutReady;
        Directory.Delete(session.Path, recursive: true);
        workspace.SelectGraphNode(GraphProjection.HostKey("52.0.0.1"));
        await workspace.HostRecordsReady;
        Assert.StartsWith("Its records could not be read: ", workspace.EvidenceSummary, StringComparison.Ordinal);
    });

    private static WorkspaceViewModel Open(TemporarySession session)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        return new(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
    }

    /// <summary>
    /// browser.exe's TCP to ten hosts, 52.0.0.1 to 52.0.0.10, ten records to the first down to one to the last; and
    /// updater.exe's one record to the first.
    /// </summary>
    private static ObservationRowV1[] TenHosts()
    {
        ulong ordinal = 1;
        var rows = new List<ObservationRowV1>
        {
            Lifecycle(1, ObservationKind.Create, 300, ordinal++) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 100 },
            Lifecycle(2, ObservationKind.Create, 310, ordinal++) with { ResourceName = @"C:\Apps\updater.exe", SessionRelativeTicks = 110 },
        };
        for (int host = 1; host <= 10; host++)
        {
            for (int record = 0; record <= 10 - host; record++)
            {
                rows.Add(Transfer(200 + (long)ordinal, ObservationKind.Send, AccountingSide.SendSide, 100, 300, ordinal)
                    .Between($"192.168.1.10:{52_000 + host}", $"52.0.0.{host}:443") with { SessionRelativeTicks = 200 + (long)ordinal });
                ordinal++;
            }
        }

        rows.Add(Transfer(900, ObservationKind.Send, AccountingSide.SendSide, 100, 310, ordinal)
            .Between("192.168.1.10:53000", "52.0.0.1:443") with { SessionRelativeTicks = 900 });
        return [.. rows];
    }

    /// <summary>
    /// browser.exe's TCP to 52.0.0.1, two sends and a receive at 1, 2 and 3 µs, and its send to 52.0.0.2 at 1.5 µs; and
    /// updater.exe's TCP send to 52.0.0.1 at 4 µs and its UDP send there at 5 µs.
    /// </summary>
    private static ObservationRowV1[] HostOverTime() =>
    [
        Lifecycle(1, ObservationKind.Create, 300, 1) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 100 },
        Lifecycle(2, ObservationKind.Create, 310, 2) with { ResourceName = @"C:\Apps\updater.exe", SessionRelativeTicks = 200 },
        Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 100, 300, 3).Between("192.168.1.10:52000", "52.0.0.1:443")
            with { SessionRelativeTicks = 1_000 },
        Transfer(15, ObservationKind.Send, AccountingSide.SendSide, 10, 300, 4).Between("192.168.1.10:52001", "52.0.0.2:443")
            with { SessionRelativeTicks = 1_500 },
        Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 200, 300, 5).Between("192.168.1.10:52000", "52.0.0.1:443")
            with { SessionRelativeTicks = 2_000 },
        Transfer(30, ObservationKind.Receive, AccountingSide.ReceiveSide, 300, 300, 6).Between("192.168.1.10:52000", "52.0.0.1:443")
            with { SessionRelativeTicks = 3_000 },
        Transfer(40, ObservationKind.Send, AccountingSide.SendSide, 50, 310, 7).Between("192.168.1.10:53000", "52.0.0.1:443")
            with { SessionRelativeTicks = 4_000 },
        Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 70, 310, 8).Between("192.168.1.10:61000", "52.0.0.1:443")
            with { Mechanism = Mechanism.Udp, EventId = 42, SessionRelativeTicks = 5_000 },
    ];

    /// <summary>
    /// browser.exe's TCP to <paramref name="hosts"/> hosts, 52.0.1.1 upwards, a record each but for the last, which holds
    /// three.
    /// </summary>
    private static ObservationRowV1[] ManyHosts(int hosts)
    {
        ulong ordinal = 1;
        var rows = new List<ObservationRowV1>
        {
            Lifecycle(1, ObservationKind.Create, 300, ordinal++) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 100 },
        };
        for (int host = 1; host <= hosts; host++)
        {
            for (int record = 0; record < (host == hosts ? 3 : 1); record++)
            {
                rows.Add(Transfer(200 + (long)ordinal, ObservationKind.Send, AccountingSide.SendSide, 100, 300, ordinal)
                    .Between($"192.168.1.10:{50_000 + host}", $"52.0.1.{host}:443") with { SessionRelativeTicks = 200 + (long)ordinal });
                ordinal++;
            }
        }

        return [.. rows];
    }

    /// <summary>
    /// svchost.exe's SSDP to its multicast group, three records; a file share's TCP to a private network's address, two;
    /// and the browser's one record to a public address.
    /// </summary>
    private static ObservationRowV1[] ScopedRows() =>
    [
        Lifecycle(1, ObservationKind.Create, 300, 1) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 100 },
        Lifecycle(2, ObservationKind.Create, 320, 2) with { ResourceName = @"C:\Windows\svchost.exe", SessionRelativeTicks = 110 },
        Lifecycle(3, ObservationKind.Create, 330, 3) with { ResourceName = @"C:\Tools\share.exe", SessionRelativeTicks = 120 },
        .. Enumerable.Range(0, 3).Select(index => Transfer(20 + index, ObservationKind.Send, AccountingSide.SendSide, 100, 320,
            (ulong)(10 + index)).Between("192.168.1.10:61900", "239.255.255.250:1900")
            with { Mechanism = Mechanism.Udp, EventId = 42, SessionRelativeTicks = 200 + index }),
        .. Enumerable.Range(0, 2).Select(index => Transfer(30 + index, ObservationKind.Send, AccountingSide.SendSide, 100, 330,
            (ulong)(20 + index)).Between("192.168.1.10:52445", "192.168.1.20:445") with { SessionRelativeTicks = 300 + index }),
        Transfer(40, ObservationKind.Send, AccountingSide.SendSide, 100, 300, 30).Between("192.168.1.10:52000", "142.250.1.1:443")
            with { SessionRelativeTicks = 400 },
    ];

    /// <summary><paramref name="instances"/> instances of svc.exe, each sending one record to 52.0.0.1's port 443.</summary>
    private static ObservationRowV1[] GroupReachingHosts(int instances) =>
    [
        .. Enumerable.Range(0, instances).Select(index => Lifecycle(1 + index, ObservationKind.Create, 1_000 + index, (ulong)(1 + index))
            with { ResourceName = @"C:\Windows\svc.exe", SessionRelativeTicks = 100 + index }),
        .. Enumerable.Range(0, instances).Select(index => Transfer(500 + index, ObservationKind.Send, AccountingSide.SendSide, 100,
            1_000 + index, (ulong)(500 + index)).Between($"192.168.1.10:{52_000 + index}", "52.0.0.1:443")
            with { SessionRelativeTicks = 1_000 + index }),
    ];

    private static ProcessNode Process(WorkspaceViewModel workspace, int pid) =>
        workspace.Snapshot.Processes.Single(process => process.ProcessId == pid);

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
