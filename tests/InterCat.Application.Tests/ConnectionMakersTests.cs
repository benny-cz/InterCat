using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// Which process made a relationship's connections (§7.4, R18): from a connect at one end or an accept at the other, a
/// relationship counts the connections each side made, a channel names its maker and the endpoint it connected from, and
/// a connection the capture saw open neither way names none.
/// </summary>
public sealed class ConnectionMakersTests
{
    [Fact(DisplayName = "§7.4: a relationship counts the connections each side made, and each channel names its maker and the endpoint it connected from")]
    public void ARelationshipCountsTheConnectionsEachSideMade()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        ProcessInstanceId client = overview.Nodes.Single(node => node.ProcessId == 100).Id;
        ProcessInstanceId server = overview.Nodes.Single(node => node.ProcessId == 200).Id;

        // The client made two of their four connections and the server one; the capture saw the fourth open neither way.
        CommunicationEdge edge = Assert.Single(overview.Edges);
        Assert.Equal((2, 1, 4), edge.SourceId == client
            ? (edge.SourceConnected, edge.TargetConnected, edge.Evidence.Count)
            : (edge.TargetConnected, edge.SourceConnected, edge.Evidence.Count));
        Assert.NotEqual(edge with { SourceConnected = edge.SourceConnected + 1 }, edge);
        Assert.NotEqual(edge with { TargetConnected = edge.TargetConnected + 1 }, edge);

        // Each channel names its maker and the endpoint it connected from, its own.
        string Maker(ProcessInstanceId? process) => process == client ? "client" : process == server ? "server" : "-";
        Assert.Equal(
            [("-", "-"), ("client", "127.0.0.1:50000"), ("client", "127.0.0.1:50001"), ("server", "127.0.0.1:50002")],
            overview.Channels.Select(channel => (Maker(channel.ConnectedBy), channel.ConnectedFrom ?? "-")).Order());

        // Its explanation says so, and a relationship's channel rows say where each was made from.
        Channel made = overview.Channels.Single(channel => channel.ConnectedFrom == "127.0.0.1:50000");
        Assert.Contains(". Opened and closed in the capture, by a connect from 127.0.0.1:50000. Coverage", ChannelPairingText.Explain(made),
            StringComparison.Ordinal);
        Channel unseen = overview.Channels.Single(channel => channel.ConnectedBy is null);
        Assert.DoesNotContain("by a connect", ChannelPairingText.Explain(unseen), StringComparison.Ordinal);
        WorkspaceSnapshot snapshot = OverviewWorkspace.From(overview);
        var ladder = new DetailLadder(SyntheticWorkspace.Root(snapshot));
        ProcessNode clientNode = snapshot.Processes.Single(process => process.Id == client);
        foreach (string key in new[] { clientNode.GroupKey, client.ToString() })
        {
            LadderRow row = LadderProjection.Project(snapshot, ladder.Current).Rows.Single(candidate => candidate.Key == key);
            Assert.True(ladder.TryDescend(LadderProjection.DescentFor(row, ladder.Current, snapshot.Extent), out _));
        }

        Assert.Equal(
            [
                "TCP · paired endpoints; direction varies by observation",
                "TCP · paired endpoints; direction varies by observation · made from 127.0.0.1:50000",
                "TCP · paired endpoints; direction varies by observation · made from 127.0.0.1:50001",
                "TCP · paired endpoints; direction varies by observation · made from 127.0.0.1:50002",
            ],
            LadderProjection.Project(snapshot, ladder.Current).Rows.Select(row => row.Detail).Order(StringComparer.Ordinal));

        // The graph's edge between them carries the same, oriented as it is drawn.
        GraphDisplay display = GraphProjection.Project(snapshot);
        GraphDisplayEdge drawn = Assert.Single(display.Edges, candidate => !candidate.ToHosts);
        bool fromClient = drawn.SourceKey == display.NodeOf(client)!.Key;
        Assert.Equal((2, 1, 4), fromClient
            ? (drawn.MadeFromSource, drawn.MadeFromTarget, drawn.Connections)
            : (drawn.MadeFromTarget, drawn.MadeFromSource, drawn.Connections));
    }

    [Fact(DisplayName = "§6.3: a drawn edge sums who made its connections over the relationships it draws, each oriented as it is, and an RPC edge holds no connection")]
    public void ADrawnEdgeSumsWhoMadeItsConnections()
    {
        ProcessGroup group = new("tools", "tool.exe", LaneGrouping.Executable);
        ProcessNode client = Node(1, 100, group.Key);
        ProcessNode server = Node(2, 200, group.Key);
        ProcessNode helper = Node(3, 300, group.Key);
        CommunicationEdge Edge(string key, ProcessNode source, ProcessNode target, Mechanism mechanism, int fromSource,
            int fromTarget, int connections) => new(key, source.Id, target.Id, mechanism, 10, null, RelationStrength.Correlated)
        {
            Rule = RelationRule.TransportEndpoint,
            Evidence = [.. Enumerable.Range(0, connections).Select(index => $"{key}:{index}")],
            SourceConnected = fromSource,
            TargetConnected = fromTarget,
        };

        WorkspaceSnapshot snapshot = new("Makers", new TimeRange(0, 10), [group], [client, server, helper],
            [
                Edge("tcp:a", client, server, Mechanism.Tcp, 2, 1, 4),
                Edge("udp:a", client, server, Mechanism.Udp, 0, 0, 1),
                Edge("tcp:b", server, helper, Mechanism.Tcp, 0, 3, 3),
                Edge("tcp:c", client, helper, Mechanism.Tcp, 1, 0, 2),
                Edge("rpc:a", client, server, Mechanism.Rpc, 0, 0, 3),
            ], [], [], [], []);
        GraphDisplay display = GraphProjection.Project(snapshot);
        Assert.Equal(
            [("rpc:a", 0, 0, 0), ("tcp:a", 2, 1, 4), ("tcp:b", 0, 3, 3), ("tcp:c", 1, 0, 2), ("udp:a", 0, 0, 1)],
            display.Edges.Select(edge => (edge.Key, edge.MadeFromSource, edge.MadeFromTarget, edge.Connections)).Order());

        // Focused on the client, the server and the helper are the rest of the machine, one node: its TCP edge to them sums
        // the two relationships it draws.
        GraphDisplay focused = GraphProjection.Project(snapshot, neighborhood: new HashSet<ProcessInstanceId> { client.Id });
        GraphDisplayEdge rest = Assert.Single(focused.Edges, edge => edge.Mechanism == Mechanism.Tcp);
        Assert.Equal((2, 3, 1, 6), (rest.Relationships.Count, rest.MadeFromSource, rest.MadeFromTarget, rest.Connections));
    }

    [Fact(DisplayName = "§6.3: an edge to the other hosts counts the connections its process made to them by a connect and accepted from them, and a host drawn on its own counts its own")]
    public void AnEdgeToTheOtherHostsCountsWhoOpenedItsConnections()
    {
        ProcessGroup group = new("tools", "tool.exe", LaneGrouping.Executable);
        ProcessNode client = Node(1, 100, group.Key);
        ProcessNode server = Node(2, 200, group.Key);
        HostLink Link(ProcessNode process, Mechanism mechanism, params HostAddress[] addresses) =>
            new(process.Id, mechanism, addresses, RelationStrength.Correlated);
        WorkspaceSnapshot snapshot = new("Hosts", new TimeRange(0, 10), [group], [client, server], [], [], [], [], [])
        {
            HostLinks =
            [
                Link(client, Mechanism.Tcp,
                    new HostAddress("203.0.113.1", 3, 9) { Connected = 2 },
                    new HostAddress("203.0.113.2", 2, 4) { Connected = 1, Accepted = 1 }),
                Link(client, Mechanism.Udp, new HostAddress("198.51.100.1", 1, 1)),
                Link(server, Mechanism.Tcp, new HostAddress("203.0.113.2", 4, 8) { Accepted = 3 }),
            ],
        };

        // Each process's edge to Other hosts, by transport: the client connected to them in three of its five TCP
        // connections and accepted one; the server accepted three of its four; nothing opens a UDP flow.
        string Who(GraphDisplay display, GraphDisplayEdge edge) =>
            (edge.SourceKey == display.NodeOf(client.Id)!.Key ? "client" : "server") + " to "
            + (edge.TargetKey == GraphProjection.OtherHostsKey ? "others" : display.Node(edge.TargetKey)!.Label);
        GraphDisplay display = GraphProjection.Project(snapshot);
        Assert.Equal(
            [
                ("client to others", Mechanism.Tcp, 3, 1, 5),
                ("client to others", Mechanism.Udp, 0, 0, 1),
                ("server to others", Mechanism.Tcp, 0, 3, 4),
            ],
            display.Edges.Select(edge => (Who(display, edge), edge.Mechanism, edge.MadeFromSource, edge.MadeFromTarget,
                edge.Connections)).Order());

        // Opened, each host drawn on its own counts its own connections, and Other hosts any left.
        GraphDisplay opened = GraphProjection.Project(snapshot, openHosts: true);
        Assert.Equal(
            [
                ("client to 198.51.100.1", Mechanism.Udp, 0, 0, 1),
                ("client to 203.0.113.1", Mechanism.Tcp, 2, 0, 3),
                ("client to 203.0.113.2", Mechanism.Tcp, 1, 1, 2),
                ("server to 203.0.113.2", Mechanism.Tcp, 0, 3, 4),
            ],
            opened.Edges.Select(edge => (Who(opened, edge), edge.Mechanism, edge.MadeFromSource, edge.MadeFromTarget,
                edge.Connections)).Order());
    }

    private static ProcessNode Node(int index, int processId, string group) =>
        new(new ProcessInstanceId(Guid.Parse($"00000000-0000-0000-0000-{index:D12}")), processId, $"p{index}.exe", "test", group, 0.5,
            0.5, CoverageState.Covered)
        {
            Activity = [new MechanismCount(Mechanism.Tcp, 10)],
        };

    /// <summary>
    /// client.exe (100) and server.exe (200) over loopback: the client connects to the server's 8080 twice - the first
    /// connect seen at both ends, the second at the client's alone - and once more with no open seen; and the server
    /// connects to the client's 9000, the client's accept seen.
    /// </summary>
    internal static ObservationRowV1[] Rows() =>
    [
        Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" },
        Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe" },

        Transfer(10, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 3).Between("127.0.0.1:50000", "127.0.0.1:8080"),
        Transfer(11, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 4).Between("127.0.0.1:8080", "127.0.0.1:50000"),
        Transfer(12, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 5).Between("127.0.0.1:50000", "127.0.0.1:8080"),
        Transfer(13, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 6).Between("127.0.0.1:8080", "127.0.0.1:50000"),
        Transfer(14, ObservationKind.Disconnect, AccountingSide.EndpointActivity, 0, 100, 7).Between("127.0.0.1:50000", "127.0.0.1:8080"),
        Transfer(15, ObservationKind.Disconnect, AccountingSide.EndpointActivity, 0, 200, 8).Between("127.0.0.1:8080", "127.0.0.1:50000"),

        Transfer(20, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 9).Between("127.0.0.1:50001", "127.0.0.1:8080"),
        Transfer(21, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 10).Between("127.0.0.1:50001", "127.0.0.1:8080"),
        Transfer(22, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 11).Between("127.0.0.1:8080", "127.0.0.1:50001"),

        Transfer(30, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 200, 12).Between("127.0.0.1:50002", "127.0.0.1:9000"),
        Transfer(31, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 100, 13).Between("127.0.0.1:9000", "127.0.0.1:50002"),
        Transfer(32, ObservationKind.Send, AccountingSide.SendSide, 8, 200, 14).Between("127.0.0.1:50002", "127.0.0.1:9000"),
        Transfer(33, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 100, 15).Between("127.0.0.1:9000", "127.0.0.1:50002"),

        Transfer(40, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 16).Between("127.0.0.1:50003", "127.0.0.1:8080"),
        Transfer(41, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 17).Between("127.0.0.1:8080", "127.0.0.1:50003"),
    ];
}
