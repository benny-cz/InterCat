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
/// A paired UDP flow in the window (§6.3): an edge of its own beside two processes' TCP, run side by side with it, said as
/// a flow wherever it is read - the graph's summary and cards, the evidence card, its process's rung, its own rung and its
/// two ends - and highlighted by a UDP lane's cell.
/// </summary>
public sealed class PairedUdpWorkspaceTests
{
    private const string Client = "127.0.0.1:61000";
    private const string Resolver = "127.0.0.1:53";

    [Fact(DisplayName = "§6.3: a UDP flow between two processes is an edge beside their TCP, said as a flow from the graph to its two ends")]
    public void AUdpFlowIsSaidAsAFlow() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        CommunicationEdge flow = workspace.Snapshot.Edges.Single(edge => edge.Mechanism == Mechanism.Udp);
        CommunicationEdge connection = workspace.Snapshot.Edges.Single(edge => edge.Mechanism == Mechanism.Tcp);

        // The graph says it draws paired TCP and UDP, and draws the two side by side.
        Assert.Equal("2 processes · 2 relationships · paired TCP and UDP only", workspace.GraphSummary);
        Assert.Equal(workspace.GraphSummary + ". The graph draws admitted paired TCP and UDP; a process's RPC calls are on its "
            + "rung, and all other observed activity is in the timeline.", workspace.GraphScope);
        GraphDisplayEdge drawnFlow = workspace.GraphDisplay.EdgeOf(flow.Key)!;
        GraphDisplayEdge drawnConnection = workspace.GraphDisplay.EdgeOf(connection.Key)!;
        Assert.Equal([-0.5, 0.5], new[] { drawnConnection.Lane, drawnFlow.Lane });

        // Its card counts its own transport's records, and a node's counts both.
        HoverCard card = workspace.DescribeGraphHover(drawnFlow.Key)!;
        Assert.Contains("domain: paired UDP transport evidence", card.Lines[3], StringComparison.Ordinal);
        Assert.Equal("Paired UDP observations: 4 · from both ends", card.Lines[4]);
        Assert.Equal("Paired TCP observations: 2 · from both ends",
            workspace.DescribeGraphHover(drawnConnection.Key)!.Lines[4]);
        Assert.Equal("Paired TCP and UDP observations on its relationships: 6",
            workspace.DescribeGraphHover(workspace.GraphDisplay.NodeOf(client.Id)!.Key)!.Lines[3]);

        // Chosen in the table, the card counts it as a flow; its process chosen, the card counts both transports.
        workspace.SelectedRelationship = workspace.Relationships.Single(row => row.Key == flow.Key);
        Assert.StartsWith("4 paired UDP observations · ", workspace.EvidenceSummary, StringComparison.Ordinal);
        workspace.SelectedRelationship = null;
        workspace.SelectGraphNode(workspace.GraphDisplay.NodeOf(client.Id)!.Key);
        Assert.Contains(" · 6 paired TCP and UDP observations · ", workspace.EvidenceSummary, StringComparison.Ordinal);

        // Its process's rung lists it as UDP beside the connection, and says it lists both.
        DescendTo(workspace, client.GroupKey);
        DescendTo(workspace, client.Id.ToString());
        await workspace.ConnectionsReady;
        Channel channel = workspace.Snapshot.Channels.Single(candidate => candidate.Mechanism == Mechanism.Udp);
        RungRow row = workspace.RungRows.Single(candidate => candidate.Key == channel.Key);
        Assert.StartsWith("UDP · ", row.Detail, StringComparison.Ordinal);
        Assert.EndsWith(" · admitted paired TCP and UDP only; not all session observations", workspace.LevelSummary,
            StringComparison.Ordinal);

        // Chosen there, it says how it was paired: a flow no record opens or closes.
        workspace.SelectedRung = row;
        Assert.Contains("A datagram flow, which no record opens or closes.", workspace.PairingExplanation, StringComparison.Ordinal);

        // On its own rung each end's card says whose end of the flow it is, and E names the flow it lists.
        workspace.RequestTimelineDetail(workspace.Snapshot.Extent, 40);
        DescendTo(workspace, channel.Key);
        await workspace.TimelineDetailReady;
        ChannelEndTimelineLane clientEnd = workspace.TimelineChannelEndLanes!.Single(end => end.Holder == client.Id);
        TimelineBucket sent = clientEnd.Buckets.First(bucket => bucket.ObservationCount > 0);
        Assert.Contains($"domain: records made at {Client}, the end of this paired UDP flow that {client.NameWithPid} holds",
            workspace.DescribeTimelineHover(sent, 1_000, endLane: clientEnd).Lines[1], StringComparison.Ordinal);
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.StartsWith($"Paired UDP flow {channel.Name}", workspace.EvidenceScopeText, StringComparison.Ordinal);
        Assert.Equal(4, workspace.RungRows.Count);
        Assert.All(workspace.RungRows, record => Assert.StartsWith("UDP ", record.Label, StringComparison.Ordinal));
    });

    [Fact(DisplayName = "§6.4: a UDP lane's chosen cell highlights the UDP edge holding its records, and a TCP lane's the TCP edge")]
    public void AUdpLanesCellHighlightsItsEdge() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);
        await workspace.LayoutReady;
        CommunicationEdge flow = workspace.Snapshot.Edges.Single(edge => edge.Mechanism == Mechanism.Udp);
        CommunicationEdge connection = workspace.Snapshot.Edges.Single(edge => edge.Mechanism == Mechanism.Tcp);
        foreach ((Mechanism mechanism, CommunicationEdge expected) in new[] { (Mechanism.Udp, flow), (Mechanism.Tcp, connection) })
        {
            TimelineBucket cell = (workspace.TimelineDetail?.MechanismLanes ?? workspace.Snapshot.MechanismLanes)
                .Single(lane => lane.Mechanism == mechanism).Buckets.First(bucket => bucket.ObservationCount > 0);
            workspace.ChooseTimelineCell(cell, mechanism);
            await workspace.IntervalReady;
            Assert.Equal([workspace.GraphDisplay.EdgeOf(expected.Key)!.Key], workspace.CellGraphEdges);
            Assert.EndsWith(" · the chosen cell's relationship highlighted", workspace.GraphSummary, StringComparison.Ordinal);
        }
    });

    [Fact(DisplayName = "§6.3: two edges between two nodes are drawn 8 px apart across the line between them, a node's edges to other hosts 4")]
    public void TwoEdgesBetweenTwoNodesAreDrawnApart()
    {
        // Along a line 100 px long, lanes -0.5 and 0.5: the relationships 4 px each side of it, the host edges 2.
        Avalonia.Point from = new(10, 20);
        Avalonia.Point to = new(110, 20);
        GraphDisplayEdge edge = new("e", "a", "b", Mechanism.Udp, 1, RelationStrength.Correlated, ["e"]) { Lane = 0.5 };
        Assert.Equal((new Avalonia.Point(10, 24), new Avalonia.Point(110, 24)), GraphView.InLane(from, to, edge));
        Assert.Equal((new Avalonia.Point(10, 16), new Avalonia.Point(110, 16)), GraphView.InLane(from, to, edge with { Lane = -0.5 }));
        Assert.Equal((new Avalonia.Point(10, 22), new Avalonia.Point(110, 22)),
            GraphView.InLane(from, to, edge with { Lane = 0.5, ToHosts = true }));

        // Alone on its line, or between two nodes in one place, an edge runs on the line.
        Assert.Equal((from, to), GraphView.InLane(from, to, edge with { Lane = 0 }));
        Assert.Equal((from, from), GraphView.InLane(from, from, edge));
    }

    private static WorkspaceViewModel Open(TemporarySession session)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        return new(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
    }

    private static void DescendTo(WorkspaceViewModel workspace, string key)
    {
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == key);
        Assert.True(workspace.Descend());
    }

    /// <summary>
    /// A client, PID 100, and its resolver, PID 200: one TCP message, then a UDP question and its answer, apart in time. A
    /// UDP receive names the datagram's sender first (ADR-019).
    /// </summary>
    private static ObservationRowV1[] Rows() =>
    [
        Timed(Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" }),
        Timed(Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\resolver.exe" }),
        Timed(Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 3).Between("127.0.0.1:50000", "127.0.0.1:8080")),
        Timed(Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 4).Between("127.0.0.1:8080", "127.0.0.1:50000")),
        Timed(Datagram(500, ObservationKind.Send, AccountingSide.SendSide, 40, 100, 5).Between(Client, Resolver)),
        Timed(Datagram(501, ObservationKind.Receive, AccountingSide.ReceiveSide, 40, 200, 6).Between(Client, Resolver)),
        Timed(Datagram(502, ObservationKind.Send, AccountingSide.SendSide, 120, 200, 7).Between(Resolver, Client)),
        Timed(Datagram(503, ObservationKind.Receive, AccountingSide.ReceiveSide, 120, 100, 8).Between(Resolver, Client)),
    ];

    private static ObservationRowV1 Datagram(long ticks, ObservationKind kind, AccountingSide side, long bytes, int owner, ulong ordinal) =>
        Transfer(ticks, kind, side, bytes, owner, ordinal) with { Mechanism = Mechanism.Udp, EventId = 42 };

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };
}
