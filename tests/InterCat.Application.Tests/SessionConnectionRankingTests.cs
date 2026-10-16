using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// The ranked table's connection rankings (§5.2, `relations-v1` §3 and §5): each process's TCP connections made and
/// accepted, its own connect and accept records in scope, which a group's are the sum of; and unmeasured rather than none
/// where the session records no connection's open (R21).
/// </summary>
public sealed class SessionConnectionRankingTests
{
    [Fact(DisplayName = "§5.2: each process's connections made and accepted are its own connects and accepts in scope, beside its TCP records, and a group's are its members'")]
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
    }

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
