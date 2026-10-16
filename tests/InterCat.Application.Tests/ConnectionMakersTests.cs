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
    }

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
