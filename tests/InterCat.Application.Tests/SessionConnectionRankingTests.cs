using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// The ranked table's connection rankings (§5.2, `relations-v1` §3 and §5): each process's TCP connections made and
/// accepted, as the capture saw each open at either end, which a group's are the sum of and a process's card says over the
/// whole session; and unmeasured rather than none where the session records no connection's open (R21).
/// </summary>
public sealed class SessionConnectionRankingTests
{
    [Fact(DisplayName = "§5.2: each process's connections made and accepted are those the capture saw it make and accept in scope, beside its TCP records, and a group's are its members'")]
    public void ConnectionsAreEachProcesssOwnOpens()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        ProcessInstanceId Id(int pid) => overview.Nodes.Single(node => node.ProcessId == pid).Id;

        // The client made two, the server and each worker accepted, the helper opened nothing, and the name server's UDP
        // holds no TCP record at all.
        SessionConnectionMeasures whole = SessionConnectionRanking.Measure(session.Store, null);
        Assert.True(whole.OpensRecorded);
        Assert.Equal(
            [(100, 2L, 0L, 5L), (200, 0L, 2L, 4L), (300, 0L, 0L, 1L), (500, 0L, 1L, 2L), (600, 0L, 1L, 2L)],
            whole.ByProcess.Select(entry => (overview.Nodes.Single(node => node.Id == entry.Key).ProcessId, entry.Value.Made,
                entry.Value.Accepted, entry.Value.TcpRecords)).Order());
        Assert.Equal(new ConnectionCount(0, 0, 0, true), whole.Of(Id(400)));

        // A ranking reads the side it ranks by over the TCP records behind it; one with no TCP record holds nothing.
        Assert.Equal(new RankedValue(RankingMetric.ConnectionsMade, 2, 5, 0), whole.Of(Id(100)).Of(RankingMetric.ConnectionsMade));
        Assert.Equal(new RankedValue(RankingMetric.ConnectionsAccepted, 0, 5, 0), whole.Of(Id(100)).Of(RankingMetric.ConnectionsAccepted));
        Assert.Equal(new RankedValue(RankingMetric.ConnectionsAccepted, 0, 1, 0), whole.Of(Id(300)).Of(RankingMetric.ConnectionsAccepted));
        Assert.False(whole.Of(Id(400)).Of(RankingMetric.ConnectionsMade).Holds);
        Assert.Null(whole.Of(Id(400)).Of(RankingMetric.ConnectionsMade).Value);

        // A brush counts the opens inside it: the client's first connect and the server's first accept.
        SessionConnectionMeasures early = SessionConnectionRanking.Measure(session.Store, new TimeRange(0, 15));
        Assert.Equal([(100, 1L, 0L), (200, 0L, 1L)],
            early.ByProcess.Select(entry => (overview.Nodes.Single(node => node.Id == entry.Key).ProcessId, entry.Value.Made,
                entry.Value.Accepted)).Order());

        // The machine rung ranks by them: the server and the two workers' group accepted two each, the group's being its
        // members' together; the client accepted none.
        WorkspaceSnapshot snapshot = OverviewWorkspace.WithConnections(OverviewWorkspace.From(overview), whole);
        var ladder = new DetailLadder(SyntheticWorkspace.Root(snapshot));
        LadderView ranked = LadderProjection.Project(snapshot, ladder.Current, RankingMetric.ConnectionsAccepted);
        Assert.Equal(
            [("server.exe", 2L), ("worker.exe", 2L), ("client.exe", 0L), ("helper.exe", 0L)],
            ranked.Rows.Take(4).Select(row => (row.Label, row.Ranked!.Value!.Value)));
        Assert.False(ranked.Rows.Single(row => row.Label == "dns.exe").Ranked!.Holds);
    }

    [Fact(DisplayName = "§5.2: a connection counts for the end that made it and the one that accepted it wherever the capture saw it open, a process connected to itself both, a simultaneous open neither, and a brush takes it where its first record is")]
    public void AConnectionCountsForBothEndsWhereverItsOpenWasSeen()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Opens());
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);

        // The client made three connections to the server: one seen at the server's accept alone, one at the client's
        // connect alone, one at both. loop.exe connected to itself twice, once seen at both ends and once at its connect
        // alone, so it made and accepted both. Two peers' connects to each other say both opened it, which counts for
        // neither.
        SessionConnectionMeasures whole = SessionConnectionRanking.Measure(session.Store, null);
        (int, long, long, long)[] Counts(SessionConnectionMeasures measures) =>
            [.. measures.ByProcess.Select(entry => (overview.Nodes.Single(node => node.Id == entry.Key).ProcessId, entry.Value.Made,
                entry.Value.Accepted, entry.Value.TcpRecords)).Order()];
        Assert.Equal([(100, 3L, 0L, 3L), (200, 0L, 3L, 5L), (300, 2L, 2L, 4L), (400, 0L, 0L, 1L), (500, 0L, 0L, 1L)], Counts(whole));

        // Each process's card counts the same over the whole session.
        Assert.All(overview.Nodes, node => Assert.Equal((whole.Of(node.Id).Made, whole.Of(node.Id).Accepted),
            (node.ConnectionsMade, node.ConnectionsAccepted)));

        // A brush takes a connection whose first record is in it, for both ends, though the server's own accept is past it;
        // one that opened before it is not taken again for a record within it, nor one whose first record is at its end.
        Assert.Equal([(100, 1L, 0L, 1L), (200, 0L, 1L, 1L)], Counts(SessionConnectionRanking.Measure(session.Store, new TimeRange(58, 65))));
        Assert.Equal([(200, 0L, 0L, 2L)], Counts(SessionConnectionRanking.Measure(session.Store, new TimeRange(11, 20))));
    }

    [Fact(DisplayName = "§5.2: a connection whose binding the evidence policy does not admit counts for neither end, in the ranking or on a card, until a policy admitting it is asked for")]
    public void AConnectionThePolicyWithholdsCountsForNeither()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Reused());

        // The client's PID was reused, so its later holder's connects bind to it only as candidates: by default neither its
        // connection to the server nor the one to another host counts, the server's accept among them.
        foreach ((EvidencePolicy policy, (int, long, long, long)[] expected) in new[]
        {
            (EvidencePolicy.IncludeCorrelated, new[] { (200, 0L, 0L, 1L) }),
            (EvidencePolicy.IncludeCandidates, new[] { (100, 2L, 0L, 2L), (200, 0L, 1L, 1L) }),
        })
        {
            SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store, policy);
            SessionConnectionMeasures whole = SessionConnectionRanking.Measure(session.Store, null, policy);
            (int, long, long, long)[] counts = [.. whole.ByProcess.Select(entry => (overview.Nodes.Single(node => node.Id == entry.Key)
                .ProcessId, entry.Value.Made, entry.Value.Accepted, entry.Value.TcpRecords)).Order()];
            Assert.Equal(expected, counts);
            Assert.All(overview.Nodes, node => Assert.Equal((whole.Of(node.Id).Made, whole.Of(node.Id).Accepted),
                (node.ConnectionsMade, node.ConnectionsAccepted)));
        }
    }

    [Fact(DisplayName = "R21: a session that records no connection's open ranks no process by connections made or accepted, unmeasured rather than none")]
    public void ASessionWithNoOpensRanksNone()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" },
            Lifecycle(4, ObservationKind.Create, 300, 4) with { ResourceName = @"C:\Tools\idle.exe" },
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 2).Between("127.0.0.1:50000", "127.0.0.1:8080"),
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 3).Between("127.0.0.1:8080", "127.0.0.1:50000"),
        ]);
        SessionConnectionMeasures measured = SessionConnectionRanking.Measure(session.Store, null);
        Assert.False(measured.OpensRecorded);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        ProcessInstanceId client = overview.Nodes.Single(node => node.ProcessId == 100).Id;
        RankedValue made = measured.Of(client).Of(RankingMetric.ConnectionsMade);
        Assert.Equal((null, 0L, 1L, true), (made.Value, made.Measured, made.Unmeasured, made.Holds));

        // A process with no TCP record in such a session is no more known to have opened none.
        Assert.False(measured.Of(overview.Nodes.Single(node => node.ProcessId == 300).Id).OpensRecorded);

        // A sum of counts, as a group's, is unmeasured where any of them is.
        Assert.False(ConnectionCount.None.Plus(measured.Of(client)).OpensRecorded);
        Assert.Throws<ArgumentOutOfRangeException>(() => measured.Of(client).Of(RankingMetric.ActivePeers));
    }

    [Fact(DisplayName = "§5.2: a session whose only opens are of connections to other hosts records opens, so a process that made none made none")]
    public void OpensToOtherHostsAreRecorded()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" },
            Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\quiet.exe" },
            Transfer(10, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 10).Between("192.168.1.10:52000", "203.0.113.1:443"),
            Transfer(11, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 11).Between("192.168.1.10:52000", "203.0.113.1:443"),
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 8, 200, 20).Between("192.168.1.10:52001", "203.0.113.2:443"),
        ]);
        SessionConnectionMeasures measured = SessionConnectionRanking.Measure(session.Store, null);
        Assert.True(measured.OpensRecorded);
        ProcessInstanceId quiet = SessionOverviewProjector.Project(session.Store).Nodes.Single(node => node.ProcessId == 200).Id;
        Assert.Equal(new RankedValue(RankingMetric.ConnectionsMade, 0, 1, 0), measured.Of(quiet).Of(RankingMetric.ConnectionsMade));
        Assert.Equal(new RankedValue(RankingMetric.ConnectionsAccepted, 0, 1, 0), measured.Of(quiet).Of(RankingMetric.ConnectionsAccepted));
    }

    /// <summary>
    /// client.exe connects to server.exe's 8080, seen at the server's accept alone; to its 8081, seen at the client's connect
    /// alone; and to its 8082 at tick 58, seen at both, the server's accept at 66. loop.exe connects to itself twice, seen at
    /// both ends and then at its connect alone; and two peers each connect to the other's port.
    /// </summary>
    private static ObservationRowV1[] Opens() =>
    [
        Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" },
        Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe" },
        Lifecycle(3, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Tools\loop.exe" },
        Lifecycle(4, ObservationKind.Create, 400, 4) with { ResourceName = @"C:\Tools\peer.exe" },
        Lifecycle(5, ObservationKind.Create, 500, 5) with { ResourceName = @"C:\Tools\peer.exe" },
        Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 10).Between("127.0.0.1:50000", "127.0.0.1:8080"),
        Transfer(11, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 11).Between("127.0.0.1:8080", "127.0.0.1:50000"),
        Transfer(12, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 12).Between("127.0.0.1:8080", "127.0.0.1:50000"),
        Transfer(20, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 20).Between("127.0.0.1:50001", "127.0.0.1:8081"),
        Transfer(21, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 21).Between("127.0.0.1:8081", "127.0.0.1:50001"),
        Transfer(30, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 300, 30).Between("127.0.0.1:50002", "127.0.0.1:9000"),
        Transfer(31, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 300, 31).Between("127.0.0.1:9000", "127.0.0.1:50002"),
        Transfer(40, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 300, 40).Between("127.0.0.1:50003", "127.0.0.1:9001"),
        Transfer(41, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 300, 41).Between("127.0.0.1:9001", "127.0.0.1:50003"),
        Transfer(50, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 400, 50).Between("127.0.0.1:7000", "127.0.0.1:7001"),
        Transfer(51, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 500, 51).Between("127.0.0.1:7001", "127.0.0.1:7000"),
        Transfer(58, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 58).Between("127.0.0.1:50005", "127.0.0.1:8082"),
        Transfer(60, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 60).Between("127.0.0.1:8080", "127.0.0.1:50000"),
        Transfer(66, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 66).Between("127.0.0.1:8082", "127.0.0.1:50005"),
    ];

    /// <summary>
    /// client.exe starts and exits, and a second client.exe takes its PID, connects to server.exe, which accepts, and to
    /// another host.
    /// </summary>
    private static ObservationRowV1[] Reused() =>
    [
        Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" },
        Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe" },
        Lifecycle(3, ObservationKind.Exit, 100, 3),
        Lifecycle(4, ObservationKind.Create, 100, 4) with { ResourceName = @"C:\Tools\client.exe" },
        Transfer(10, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 10).Between("127.0.0.1:50000", "127.0.0.1:8080"),
        Transfer(11, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 11).Between("127.0.0.1:8080", "127.0.0.1:50000"),
        Transfer(20, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 20).Between("192.168.1.10:52000", "203.0.113.1:443"),
    ];

    /// <summary>
    /// client.exe connects to server.exe over loopback and to another host; server.exe accepts it and a connection from
    /// another host; helper.exe receives from the client on a connection seen opening at neither end; two worker.exe
    /// instances each accept a connection from another host; and dns.exe sends UDP.
    /// </summary>
    private static ObservationRowV1[] Rows() =>
    [
        Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" },
        Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe" },
        Lifecycle(3, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Tools\helper.exe" },
        Lifecycle(4, ObservationKind.Create, 400, 4) with { ResourceName = @"C:\Tools\dns.exe" },
        Lifecycle(5, ObservationKind.Create, 500, 5) with { ResourceName = @"C:\Tools\worker.exe" },
        Lifecycle(6, ObservationKind.Create, 600, 6) with { ResourceName = @"C:\Tools\worker.exe" },
        Transfer(10, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 10).Between("127.0.0.1:50000", "127.0.0.1:8080"),
        Transfer(11, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 11).Between("127.0.0.1:50000", "127.0.0.1:8080"),
        Transfer(12, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 12).Between("127.0.0.1:8080", "127.0.0.1:50000"),
        Transfer(13, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 13).Between("127.0.0.1:8080", "127.0.0.1:50000"),
        Transfer(20, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 20).Between("192.168.1.10:52000", "203.0.113.1:443"),
        Transfer(21, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 21).Between("192.168.1.10:52000", "203.0.113.1:443"),
        Transfer(30, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 30).Between("192.168.1.10:445", "10.0.0.6:50001"),
        Transfer(31, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 31).Between("192.168.1.10:445", "10.0.0.6:50001"),
        Transfer(40, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 40).Between("127.0.0.1:50001", "127.0.0.1:7070"),
        Transfer(41, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 300, 41).Between("127.0.0.1:7070", "127.0.0.1:50001"),
        Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 32, 400, 50).Between("192.168.1.10:53000", "10.0.0.53:53")
            with { Mechanism = Mechanism.Udp },
        Transfer(60, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 500, 60).Between("192.168.1.10:9000", "10.0.0.7:50002"),
        Transfer(61, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 500, 61).Between("192.168.1.10:9000", "10.0.0.7:50002"),
        Transfer(70, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 600, 70).Between("192.168.1.10:9001", "10.0.0.8:50003"),
        Transfer(71, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 600, 71).Between("192.168.1.10:9001", "10.0.0.8:50003"),
    ];
}
