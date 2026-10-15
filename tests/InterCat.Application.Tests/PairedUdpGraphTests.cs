using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// The graph relates processes by paired UDP flows as it relates them by TCP connections (§6.3, `relations-v1` §5e): each
/// pair of process instances an edge per transport, each flow a channel judged by UDP's own coverage, which a brush, a byte
/// ranking, the channel pages, the evidence pages and a channel's focus count as they count a connection; and two edges
/// between the same two nodes run side by side.
/// </summary>
public sealed class PairedUdpGraphTests
{
    private const string Client = "127.0.0.1:61000";
    private const string Resolver = "127.0.0.1:53";

    [Fact(DisplayName = "§6.3: a UDP flow between two processes is an edge of its own beside their TCP, its channel judged by UDP's coverage, whole and brushed")]
    public void AUdpFlowIsAnEdgeOfItsOwn()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows(), coverage: TcpOnly());
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        ProcessInstanceId client = overview.Nodes.Single(node => node.ProcessId == 100).Id;
        ProcessInstanceId resolver = overview.Nodes.Single(node => node.ProcessId == 200).Id;
        (ProcessInstanceId first, ProcessInstanceId second) = string.CompareOrdinal(client.ToString(), resolver.ToString()) <= 0
            ? (client, resolver)
            : (resolver, client);

        // An edge per transport between the two, each keyed by its transport and counting its own records from both ends.
        Assert.Equal(
            [($"tcp:{first}:{second}", Mechanism.Tcp, 2L), ($"udp:{first}:{second}", Mechanism.Udp, 4L)],
            overview.Edges.Select(edge => (edge.Key, edge.Mechanism, edge.ObservationCount)));
        Assert.All(overview.Edges, edge => Assert.Equal((first, second, RelationRule.TransportEndpoint), (edge.SourceId, edge.TargetId, edge.Rule)));
        Assert.Equal((1, 1), (overview.Census!.Tcp.Relationships, overview.Census.Udp.Relationships));
        Assert.Equal(6, overview.GraphEligibleRows);
        Assert.Contains(overview.Caveats, caveat => caveat.StartsWith(
            "Graph edges show paired TCP connection incarnations and UDP datagram flows", StringComparison.Ordinal));

        // Each flow is a channel of its edge, judged by its own transport's coverage: this capture collected TCP alone.
        Channel tcp = overview.Channels.Single(channel => channel.Mechanism == Mechanism.Tcp);
        Channel udp = overview.Channels.Single(channel => channel.Mechanism == Mechanism.Udp);
        Assert.Equal(($"udp:{first}:{second}", 4L, CoverageState.NotCollected), (udp.EdgeKey, udp.ObservationCount, udp.Coverage));
        Assert.Equal(($"tcp:{first}:{second}", 2L, CoverageState.Covered), (tcp.EdgeKey, tcp.ObservationCount, tcp.Coverage));
        Assert.Equal([udp.Key], overview.Edges.Single(edge => edge.Mechanism == Mechanism.Udp).Evidence);

        // A brush counts the flow's records on its edge and its channel, judging each by its transport there.
        SessionIntervalCounts brushed = SessionIntervalQuery.Count(session.Store, new TimeRange(0, 23));
        Assert.Equal((3L, 3L), (brushed.EdgeRecords[$"udp:{first}:{second}"], brushed.ChannelRecords[udp.Key]));
        Assert.Equal(2L, brushed.EdgeRecords[$"tcp:{first}:{second}"]);
        Assert.Equal(5L, brushed.GraphRows);
        Assert.Equal((CoverageState.Covered, CoverageState.NotCollected), (brushed.TcpCoverage, brushed.UdpCoverage));
        WorkspaceSnapshot within = OverviewWorkspace.WithinInterval(OverviewWorkspace.From(overview), brushed);
        Assert.Equal(
            [(Mechanism.Tcp, 2L, CoverageState.Covered), (Mechanism.Udp, 3L, CoverageState.NotCollected)],
            within.Channels.Select(channel => (channel.Mechanism, channel.ObservationCount, channel.Coverage)).OrderBy(channel => channel.Mechanism));

        // The channel pages list it beside the connection and state both transports' coverage.
        SessionChannelPage page = SessionChannelQuery.Read(session.Store);
        Assert.Equal(new[] { tcp.Key, udp.Key }.Order(StringComparer.Ordinal), page.Channels.Select(channel => channel.Key));
        Assert.Equal([(Mechanism.Tcp, CoverageState.Covered), (Mechanism.Udp, CoverageState.NotCollected)],
            page.Coverage.Select(coverage => (coverage.Mechanism, coverage.State)));
        Assert.Equal((Mechanism.Udp, CoverageState.NotCollected),
            page.Channels.Where(channel => channel.Key == udp.Key).Select(channel => (channel.Mechanism, channel.Coverage)).Single());
        Assert.Equal(CoverageState.Covered, page.Channels.Single(channel => channel.Key == tcp.Key).Coverage);
        Assert.StartsWith("Only paired TCP incarnations and UDP flows", page.Caveat, StringComparison.Ordinal);

        // Its evidence is its four datagrams at both ends, and its focus draws its two ends.
        SessionEvidencePage records = SessionEvidenceQuery.Read(session.Store, channelKey: udp.Key);
        Assert.Equal(4, records.Records.Count);
        Assert.All(records.Records, record => Assert.Equal(Mechanism.Udp, record.Observation.Mechanism));
        SessionFocusedTimeline focused = SessionTimelineQuery.Focused(session.Store, overview.Extent!.Value, 10, new TimelineFocus(udp.Key, []));
        Assert.Equal(2, focused.ChannelEndLanes.Count);
        Assert.All(focused.ChannelEndLanes, end => Assert.Equal(2, end.Buckets.Sum(bucket => bucket.ObservationCount)));
    }

    [Fact(DisplayName = "§5.2: a UDP flow's ends hold their own bytes, as a connection's do, and rank a process's channels")]
    public void AUdpFlowsEndsHoldTheirOwnBytes()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        WorkspaceSnapshot whole = OverviewWorkspace.From(SessionOverviewProjector.Project(session.Store));
        SessionByteMeasures measured = SessionByteRanking.Measure(session.Store, null);
        ProcessNode client = whole.Processes.Single(node => node.ProcessId == 100);
        ProcessNode resolver = whole.Processes.Single(node => node.ProcessId == 200);
        Channel udp = whole.Channels.Single(channel => channel.Mechanism == Mechanism.Udp);
        Channel tcp = whole.Channels.Single(channel => channel.Mechanism == Mechanism.Tcp);

        // The client sent the question and received the answer; the resolver the other way about.
        Assert.Equal(new TransportBytes(40, 1, 0, 120, 1, 0), measured.ByChannelEnd[new(udp.Key, client.Id)]);
        Assert.Equal(new TransportBytes(120, 1, 0, 40, 1, 0), measured.ByChannelEnd[new(udp.Key, resolver.Id)]);
        Assert.Equal(new TransportBytes(8, 1, 0, 0, 0, 0), measured.ByChannelEnd[new(tcp.Key, client.Id)]);

        // By the client's bytes received the flow leads its rung, and the connection, on which it received none, follows.
        WorkspaceSnapshot counted = OverviewWorkspace.WithBytes(whole, measured);
        var ladder = new DetailLadder(SyntheticWorkspace.Root(counted));
        foreach (string key in new[] { client.GroupKey, client.Id.ToString() })
        {
            LadderRow row = LadderProjection.Project(counted, ladder.Current).Rows.Single(candidate => candidate.Key == key);
            Assert.True(ladder.TryDescend(LadderProjection.DescentFor(row, ladder.Current, counted.Extent), out string? refusal), refusal);
        }

        LadderView received = LadderProjection.Project(counted, ladder.Current, RankingMetric.BytesReceived);
        Assert.Equal([(udp.Key, true), (tcp.Key, false)], received.Rows.Select(row => (row.Key, row.Ranked!.Holds)));
        Assert.Equal(Mechanism.Udp, received.Rows[0].Mechanism);
    }

    [Fact(DisplayName = "§6.3: the relationships between two drawn nodes run side by side, a lane each, whichever end each leaves from")]
    public void RelationshipsBetweenTwoNodesRunSideBySide()
    {
        ProcessGroup group = new("g", "g.exe", LaneGrouping.Executable);
        ProcessNode[] processes = [.. Enumerable.Range(1, 4).Select(index => Node(index, group.Key))];
        CommunicationEdge[] edges =
        [
            Edge("tcp", processes[0], processes[1], Mechanism.Tcp, 10),
            Edge("udp", processes[0], processes[1], Mechanism.Udp, 4),
            Edge("rpc", processes[0], processes[1], Mechanism.Rpc, 2),
            Edge("back", processes[3], processes[2], Mechanism.Tcp, 3),
            Edge("forth", processes[2], processes[3], Mechanism.Udp, 3),
            Edge("lone", processes[1], processes[2], Mechanism.Udp, 1),
        ];

        GraphDisplay display = GraphProjection.Project(Snapshot([group], processes, edges));

        // Three between the first two: TCP, UDP and RPC, in their mechanisms' order across the line between them.
        Assert.Equal([-1d, 0d, 1d], [display.EdgeOf("tcp")!.Lane, display.EdgeOf("udp")!.Lane, display.EdgeOf("rpc")!.Lane]);

        // Two met in opposite orders part to opposite sides of the line from the ordinally first node; a lone one keeps to it.
        double Across(GraphDisplayEdge edge) => string.CompareOrdinal(edge.SourceKey, edge.TargetKey) <= 0 ? edge.Lane : -edge.Lane;
        Assert.Equal([-0.5, 0.5], new List<double> { Across(display.EdgeOf("back")!), Across(display.EdgeOf("forth")!) }.Order());
        Assert.NotEqual(display.EdgeOf("back")!.SourceKey, display.EdgeOf("forth")!.SourceKey);
        Assert.Equal(0d, display.EdgeOf("lone")!.Lane);

        // A re-count keeps them where they run.
        GraphDisplay recounted = GraphProjection.Rescope(display, Snapshot([group], processes, edges), Snapshot([group], processes,
            [.. edges.Select(edge => edge with { ObservationCount = 1 })]));
        Assert.Equal(display.Edges.Select(edge => (edge.Key, edge.Lane)), recounted.Edges.Select(edge => (edge.Key, edge.Lane)));
    }

    /// <summary>
    /// A client, PID 100, and its resolver, PID 200: one TCP message and a UDP question and answer. A UDP receive names
    /// the datagram's sender first (ADR-019).
    /// </summary>
    private static ObservationRowV1[] Rows() =>
    [
        Timed(Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" }),
        Timed(Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\resolver.exe" }),
        Timed(Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 3).Between("127.0.0.1:50000", "127.0.0.1:8080")),
        Timed(Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 4).Between("127.0.0.1:8080", "127.0.0.1:50000")),
        Timed(Datagram(20, ObservationKind.Send, AccountingSide.SendSide, 40, 100, 5).Between(Client, Resolver)),
        Timed(Datagram(21, ObservationKind.Receive, AccountingSide.ReceiveSide, 40, 200, 6).Between(Client, Resolver)),
        Timed(Datagram(22, ObservationKind.Send, AccountingSide.SendSide, 120, 200, 7).Between(Resolver, Client)),
        Timed(Datagram(30, ObservationKind.Receive, AccountingSide.ReceiveSide, 120, 100, 8).Between(Resolver, Client)),
    ];

    private static ObservationRowV1 Datagram(long ticks, ObservationKind kind, AccountingSide side, long bytes, int owner, ulong ordinal) =>
        Transfer(ticks, kind, side, bytes, owner, ordinal) with { Mechanism = Mechanism.Udp, EventId = 42 };

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };

    /// <summary>A capture that collected TCP and lifecycle, and no UDP, over every reading the rows hold.</summary>
    private static CoverageLedgerV1 TcpOnly() => new()
    {
        Contract = CoverageLedgerV1.ContractName,
        Epochs =
        [
            new CoverageEpochV1
            {
                Epoch = 1,
                Acquisition = CoverageAcquisition.LiveCapture,
                FirstDeliveredNativeTicks = 0,
                LastDeliveredNativeTicks = 40,
                Collected =
                [
                    new CoverageCollectedV1 { ProviderId = NetworkProvider, ProviderName = "network", EventId = 10, Version = 0, Mechanism = Mechanism.Tcp },
                ],
                Deliveries = [new CoverageDeliveryV1 { ProviderId = NetworkProvider, EventId = 10, Version = 0, Delivered = 2, Admitted = 2, Omitted = 0 }],
                Losses =
                [
                    new CoverageLossV1 { Layer = LossLayer.SourceSession, Lost = 0 },
                    new CoverageLossV1 { Layer = LossLayer.ConsumerBuffers, Lost = 0 },
                    new CoverageLossV1 { Layer = LossLayer.CallbackQueue, Lost = 0 },
                    new CoverageLossV1 { Layer = LossLayer.Storage, Lost = 0 },
                ],
            },
        ],
    };

    private static WorkspaceSnapshot Snapshot(
        IReadOnlyList<ProcessGroup> groups, IReadOnlyList<ProcessNode> processes, IReadOnlyList<CommunicationEdge> edges) =>
        new("Paired UDP test", new TimeRange(0, 10), groups, processes, edges, [], [], [], []);

    private static ProcessNode Node(int index, string group) => new(
        new ProcessInstanceId(Guid.Parse($"00000000-0000-0000-0000-{index:D12}")),
        1_000 + index,
        $"Process {index}",
        "test",
        group,
        0.5,
        0.5,
        CoverageState.Covered);

    private static CommunicationEdge Edge(string key, ProcessNode source, ProcessNode target, Mechanism mechanism, long observations) =>
        new(key, source.Id, target.Id, mechanism, observations, null, RelationStrength.Correlated)
        {
            Rule = RelationRule.TransportEndpoint,
            Evidence = [key],
        };
}
