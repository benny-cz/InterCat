using Avalonia;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>
/// Which process made a relationship's connections, on the graph (§6.8, §7.4): a relationship's card says who connected to
/// whom where the capture saw its connections open - a connect at one end, an accept at the other - counted where they
/// went both ways or some opened unseen, and display order only where it saw none open.
/// </summary>
public sealed class ConnectionMakersWorkspaceTests
{
    private const string Client = "client.exe · PID 100";
    private const string Server = "server.exe · PID 200";

    [Fact(DisplayName = "§6.8: a relationship's card says which process made its connections, counted where they went both ways or some opened unseen, and display order only where the capture saw none open")]
    public void ARelationshipsCardSaysWhoConnected() => SingleThreadedContext.Run(async () =>
    {
        // Both ways, and one the capture saw open neither way.
        Assert.Equal($"Direction: {Client} connected to {Server} in 2 connections, {Server} connected to {Client} in 1 connection; "
            + "the capture saw 1 connection open neither way", await DirectionAsync(Mixed(), clientFirst: true));
        Assert.Equal($"Direction: {Server} connected to {Client} in 1 connection, {Client} connected to {Server} in 2 connections; "
            + "the capture saw 1 connection open neither way", await DirectionAsync(Mixed(), clientFirst: false));

        // One way only, whichever end the edge is drawn from.
        Assert.Equal($"Direction: {Client} connected to {Server}, in all 2 of its connections",
            await DirectionAsync(OneWay(), clientFirst: true));
        Assert.Equal($"Direction: {Client} connected to {Server}, in all 2 of its connections",
            await DirectionAsync(OneWay(), clientFirst: false));
        Assert.Equal($"Direction: {Client} connected to {Server}", await DirectionAsync(OneWay()[..^3], clientFirst: true));

        // None seen opening: display order only.
        Assert.Equal("Direction: display order only, not who initiated or sent", await DirectionAsync(Unseen(), clientFirst: true));
    });

    [Fact(DisplayName = "§7.4: a process's channels lead with an arrow toward a peer it connected to, or from one that connected to it, and both ways where the capture cannot say")]
    public void AProcesssChannelsLeadWithWhoConnected() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Mixed());
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        await workspace.LayoutReady;
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "client.exe");
        Assert.True(workspace.Descend());
        workspace.SelectedRung = workspace.RungRows.Single();
        Assert.True(workspace.Descend());

        // The client made two of its connections to the server and the server one to it; the capture saw one open neither way.
        Assert.Equal(
            [
                ("← " + Server, "Connection from " + Server),
                ("→ " + Server, "Connection to " + Server),
                ("→ " + Server, "Connection to " + Server),
                ("↔ " + Server, "Channel with " + Server),
            ],
            workspace.RungRows.Select(row => (row.Label, row.SpokenLabel!)).Order());
    });

    [Fact(DisplayName = "§6.3: an arrowhead points at the disc its connections ran to, standing just off it, wider than its stroke by a margin either side, and is left out where the edge cannot hold one at each end")]
    public void AnArrowheadPointsAtTheDiscItsConnectionsRanTo()
    {
        // Along a level edge whose discs reach 10 px along it, under a 2 px stroke: its tip a pixel off the disc at 100, its
        // base 9.6 px back and 4 px aside - the stroke's half and 3 px more.
        (Point Tip, Point Left, Point Right) toward = GraphView.Arrowhead(new Point(0, 0), new Point(100, 0), 11, 11, 2)!.Value;
        Assert.Equal((89d, 0d), (toward.Tip.X, toward.Tip.Y));
        Assert.Equal((79.4, 4d, 79.4, -4d), (Math.Round(toward.Left.X, 6), Math.Round(toward.Left.Y, 6),
            Math.Round(toward.Right.X, 6), Math.Round(toward.Right.Y, 6)));

        // Under a 6 px stroke it is wider and longer, still 3 px beyond the stroke either side.
        (Point Tip, Point Left, Point Right) thick = GraphView.Arrowhead(new Point(0, 0), new Point(100, 0), 11, 11, 6)!.Value;
        Assert.Equal((89d, 74.6, 6d), (thick.Tip.X, Math.Round(thick.Left.X, 6), Math.Round(thick.Left.Y, 6)));

        // The other way, and down a vertical edge, it points as the edge runs, standing off its own end's disc.
        (Point Tip, Point Left, Point Right) back = GraphView.Arrowhead(new Point(100, 0), new Point(0, 0), 11, 5, 2)!.Value;
        Assert.Equal((5d, 0d, 14.6, -4d), (back.Tip.X, back.Tip.Y, Math.Round(back.Left.X, 6), Math.Round(back.Left.Y, 6)));
        (Point Tip, Point Left, Point Right) down = GraphView.Arrowhead(new Point(0, 0), new Point(0, 100), 11, 11, 2)!.Value;
        Assert.Equal((0d, 89d, -4d, 79.4), (Math.Round(down.Tip.X, 6), Math.Round(down.Tip.Y, 6), Math.Round(down.Left.X, 6),
            Math.Round(down.Left.Y, 6)));

        // An edge that cannot hold a head at each end, with 6 px of stroke between them, past both stand-offs draws none.
        Assert.Null(GraphView.Arrowhead(new Point(0, 0), new Point(47, 0), 11, 11, 2));
        Assert.NotNull(GraphView.Arrowhead(new Point(0, 0), new Point(48, 0), 11, 11, 2));
        Assert.Null(GraphView.Arrowhead(new Point(0, 0), new Point(48, 0), 11, 12, 2));

        // A disc reaches its radius along an edge through its centre, less along one drawn in a lane beside it, and nothing
        // along one that passes outside it.
        Assert.Equal(10, GraphView.DiscReach(10, new Point(5, 5), new Point(5, 5)));
        Assert.Equal(6, GraphView.DiscReach(10, new Point(0, 0), new Point(0, 8)), 9);
        Assert.Equal(8, GraphView.DiscReach(10, new Point(0, 0), new Point(-6, 0)), 9);
        Assert.Equal(0, GraphView.DiscReach(10, new Point(0, 0), new Point(0, 12)));
    }

    /// <summary>
    /// The direction line of the card of the one relationship drawn between client.exe and server.exe, as the edge is drawn
    /// from the client's node, or from the server's when <paramref name="clientFirst"/> is false: the card's own where the
    /// graph draws it so, else the same edge's read reversed.
    /// </summary>
    private static async Task<string> DirectionAsync(ObservationRowV1[] rows, bool clientFirst)
    {
        using var session = new TemporarySession();
        Publish(session.Store, rows);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        await workspace.LayoutReady;
        GraphDisplayEdge edge = Assert.Single(workspace.GraphDisplay.Edges, drawn => !drawn.ToHosts);
        string clientKey = workspace.GraphDisplay.NodeOf(overview.Nodes.Single(node => node.ProcessId == 100).Id)!.Key;
        string card = workspace.DescribeGraphHover(edge.Key)!.Lines.Single(line => line.StartsWith("Direction: ", StringComparison.Ordinal));
        if ((edge.SourceKey == clientKey) == clientFirst)
        {
            return card;
        }

        string source = workspace.GraphDisplay.Node(edge.SourceKey)!.Title;
        string target = workspace.GraphDisplay.Node(edge.TargetKey)!.Title;
        return WorkspaceViewModel.DirectionLine(edge with
        {
            SourceKey = edge.TargetKey,
            TargetKey = edge.SourceKey,
            MadeFromSource = edge.MadeFromTarget,
            MadeFromTarget = edge.MadeFromSource,
        }, target, source);
    }

    /// <summary>
    /// The client connects to the server's 8080 twice, seen at its end; the server to the client's 9000, seen at the
    /// client's accept; and one more connection to 8080 opens unseen.
    /// </summary>
    private static ObservationRowV1[] Mixed() =>
    [
        .. Processes(),
        Transfer(10, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 3).Between("127.0.0.1:50000", "127.0.0.1:8080"),
        Transfer(11, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 4).Between("127.0.0.1:50000", "127.0.0.1:8080"),
        Transfer(12, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 5).Between("127.0.0.1:8080", "127.0.0.1:50000"),
        Transfer(20, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 6).Between("127.0.0.1:50001", "127.0.0.1:8080"),
        Transfer(21, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 7).Between("127.0.0.1:50001", "127.0.0.1:8080"),
        Transfer(22, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 8).Between("127.0.0.1:8080", "127.0.0.1:50001"),
        Transfer(30, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 100, 9).Between("127.0.0.1:9000", "127.0.0.1:50002"),
        Transfer(31, ObservationKind.Send, AccountingSide.SendSide, 8, 200, 10).Between("127.0.0.1:50002", "127.0.0.1:9000"),
        Transfer(32, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 100, 11).Between("127.0.0.1:9000", "127.0.0.1:50002"),
        Transfer(40, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 12).Between("127.0.0.1:50003", "127.0.0.1:8080"),
        Transfer(41, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 13).Between("127.0.0.1:8080", "127.0.0.1:50003"),
    ];

    /// <summary>The client connects to the server's 8080 twice, the second time seen at the server's accept alone.</summary>
    private static ObservationRowV1[] OneWay() =>
    [
        .. Processes(),
        Transfer(10, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 3).Between("127.0.0.1:50000", "127.0.0.1:8080"),
        Transfer(11, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 4).Between("127.0.0.1:50000", "127.0.0.1:8080"),
        Transfer(12, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 5).Between("127.0.0.1:8080", "127.0.0.1:50000"),
        Transfer(20, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 6).Between("127.0.0.1:8080", "127.0.0.1:50001"),
        Transfer(21, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 7).Between("127.0.0.1:50001", "127.0.0.1:8080"),
        Transfer(22, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 8).Between("127.0.0.1:8080", "127.0.0.1:50001"),
    ];

    /// <summary>A TCP connection between them, seen opening at neither end.</summary>
    private static ObservationRowV1[] Unseen() =>
    [
        .. Processes(),
        Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 3).Between("127.0.0.1:50000", "127.0.0.1:8080"),
        Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 4).Between("127.0.0.1:8080", "127.0.0.1:50000"),
    ];

    private static ObservationRowV1[] Processes() =>
    [
        Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" },
        Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe" },
    ];
}
