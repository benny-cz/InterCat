using System.Globalization;
using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// Why a session's graph draws no relationship, said where it would otherwise look broken (§6.3, §6.8): a real machine's
/// TCP and UDP mostly run to other hosts, and its RPC calls are joined to the process that served them only in a capture
/// that collects ALPC.
/// </summary>
public sealed class RelationshipCensusTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    [Fact(DisplayName = "§6.8: a session whose TCP and UDP all run to other hosts says so of each, with its RPC, and its graph draws none")]
    public void ASessionWhoseTcpRunsToOtherHostsSaysSo()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(10, ObservationKind.Inventory, 300, 1) with { SessionRelativeTicks = 100 },
            Lifecycle(11, ObservationKind.Inventory, 400, 2) with { SessionRelativeTicks = 110 },
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 500, 300, 3)
                .Between("192.168.1.10:52000", "142.250.1.1:443") with { SessionRelativeTicks = 200 },
            Transfer(21, ObservationKind.Receive, AccountingSide.ReceiveSide, 900, 300, 4)
                .Between("192.168.1.10:52000", "142.250.1.1:443") with { SessionRelativeTicks = 210 },
            Transfer(22, ObservationKind.Send, AccountingSide.SendSide, 500, 300, 5)
                .Between("192.168.1.10:52001", "20.42.65.1:443") with { SessionRelativeTicks = 220 },
            Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 40, 400, 6)
                .Between("192.168.1.10:5353", "224.0.0.251:5353") with { Mechanism = Mechanism.Udp, EventId = 42, SessionRelativeTicks = 300 },
            RpcCall(40, ObservationKind.RequestStart, Direction.Outbound, 400, 7, Guid.NewGuid(), ServiceControlInterface)
                with { SessionRelativeTicks = 400 },
        ]);

        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        Assert.Empty(overview.Edges);
        RelationshipCensus census = Assert.IsType<RelationshipCensus>(overview.Census);
        Assert.Equal(new TransportCensus(Mechanism.Tcp, 0, 0, 0, 0, 3, 2, 2, 0, 0, 0, 0), census.Tcp);
        Assert.Equal(new TransportCensus(Mechanism.Udp, 0, 0, 0, 0, 1, 1, 1, 0, 0, 0, 0), census.Udp);
        Assert.Equal((1L, false), (census.RpcRecords, census.RpcLinked));
        Assert.Same(census, OverviewWorkspace.From(overview).Census);

        Assert.Equal("no relationship drawn · 2 TCP connections ran to other hosts", RelationshipCensusText.Brief(census));
        Assert.Equal(
            "No TCP connection or UDP flow was seen with a process of this session at each of its two ends. All 3 of its TCP "
            + "records belong to 2 connections to 2 other hosts. Its 1 UDP record belongs to 1 flow with 1 other host. A "
            + "process's connections are listed on its rung. RPC calls are on their processes' rungs: a call is drawn to the "
            + "process that served it only from a capture that collects ALPC - choose RPC peers in the window's Keep, or run "
            + "icat capture --profile rpc-peers.",
            RelationshipCensusText.Explain(census, Culture));
    }

    [Fact(DisplayName = "§6.8: a session whose TCP pairs two of its processes has relationships, and its census counts them")]
    public void ASessionWithARelationshipCountsIt()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 200 },
            Transfer(21, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 2)
                .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 210 },
        ]);

        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        Assert.Single(overview.Edges);
        Assert.Equal((2L, 1, 2L), (overview.Census!.Tcp.Related, overview.Census.Tcp.Relationships, overview.Census.Tcp.Records));
        Assert.Equal((0L, false), (overview.Census.RpcRecords, overview.Census.RpcLinked));
    }

    [Fact(DisplayName = "§6.8: each reason a graph draws none is said in its own words, and a count of one in the singular")]
    public void EachReasonIsSaidInItsOwnWords()
    {
        TransportCensus none = TransportCensus.None(Mechanism.Tcp);
        TransportCensus noUdp = TransportCensus.None(Mechanism.Udp);
        RelationshipCensus Census(TransportCensus tcp, long rpc = 0, bool linked = false, TransportCensus? udp = null) =>
            new(tcp, udp ?? noUdp, rpc, linked);

        // Nothing recorded: no TCP or UDP at all, and no RPC to speak of.
        Assert.Equal("no relationship drawn · no TCP or UDP recorded", RelationshipCensusText.Brief(Census(none)));
        Assert.Equal("This capture recorded no TCP or UDP, so no relationship between two of its processes can be drawn.",
            RelationshipCensusText.Explain(Census(none), Culture));

        // A relationship the policy leaves out says how to draw it.
        TransportCensus withheld = none with { Withheld = 2, WithheldRelationships = 1 };
        Assert.Equal("no relationship drawn · 1 TCP relationship left out as a candidate", RelationshipCensusText.Brief(Census(withheld)));
        Assert.Equal(
            "1 TCP relationship rests on a reused PID's later holder, so the evidence policy leaves it out as a candidate; "
            + "counting candidates draws it. All 2 of its TCP records belong to the relationships left out.",
            RelationshipCensusText.Explain(Census(withheld), Culture));

        // One record, one connection on this machine whose other end went unseen.
        TransportCensus unseen = none with { ToUnseenLocalEnds = 1, UnseenLocalConnections = 1 };
        Assert.Equal("no relationship drawn · 1 TCP connection with an unseen other end", RelationshipCensusText.Brief(Census(unseen)));
        Assert.Equal(
            "No TCP connection was seen with a process of this session at each of its two ends. Its 1 TCP record belongs to "
            + "1 connection on this machine whose other end no record holds. A process's connections are listed on its rung.",
            RelationshipCensusText.Explain(Census(unseen), Culture));

        // Every way a record can miss a relationship, together, in the order they are said.
        TransportCensus mixed = none with
        {
            ToOtherHosts = 1_200, OtherHostConnections = 37, OtherHosts = 12,
            ToUnseenLocalEnds = 30, UnseenLocalConnections = 2, Unpaired = 14, WithoutEndpoints = 1,
        };
        Assert.Equal("no relationship drawn · 37 TCP connections ran to other hosts", RelationshipCensusText.Brief(Census(mixed)));
        Assert.Equal(
            "No TCP connection was seen with a process of this session at each of its two ends. Of its 1,245 TCP records, "
            + "1,200 belong to 37 connections to 12 other hosts, 30 belong to 2 connections on this machine whose other end "
            + "no record holds, 14 belong to connections that could not be paired with one process at each end and 1 names "
            + "no complete endpoint. A process's connections are listed on its rung.",
            RelationshipCensusText.Explain(Census(mixed), Culture));

        // Ends no single process holds, and records naming no endpoint, are the last two reasons a summary gives.
        Assert.Equal("no relationship drawn · TCP ends could not be paired",
            RelationshipCensusText.Brief(Census(none with { Unpaired = 3 })));
        Assert.Equal("no relationship drawn · no TCP record names both endpoints",
            RelationshipCensusText.Brief(Census(none with { WithoutEndpoints = 3 })));

        // UDP alone is said in its own words: flows, with other hosts.
        TransportCensus datagrams = noUdp with { ToOtherHosts = 4, OtherHostConnections = 2, OtherHosts = 1, Unpaired = 1 };
        Assert.Equal("no relationship drawn · 2 UDP flows ran to other hosts", RelationshipCensusText.Brief(Census(none, udp: datagrams)));
        Assert.Equal(
            "No UDP flow was seen with a process of this session at each of its two ends. Of its 5 UDP records, 4 belong to "
            + "2 flows with 1 other host and 1 belongs to flows that could not be paired with one process at each end. A "
            + "process's connections are listed on its rung.",
            RelationshipCensusText.Explain(Census(none, udp: datagrams), Culture));
        Assert.Equal("no relationship drawn · 1 UDP flow with an unseen other end",
            RelationshipCensusText.Brief(Census(none, udp: noUdp with { ToUnseenLocalEnds = 2, UnseenLocalConnections = 1 })));
        Assert.Equal("no relationship drawn · UDP ends could not be paired",
            RelationshipCensusText.Brief(Census(none, udp: noUdp with { Unpaired = 3 })));

        // Both transports' relationships left out are said together; TCP's reason leads the summary.
        TransportCensus withheldUdp = noUdp with { Withheld = 3, WithheldRelationships = 2 };
        Assert.Equal("no relationship drawn · 1 TCP relationship left out as a candidate",
            RelationshipCensusText.Brief(Census(withheld, udp: withheldUdp)));
        Assert.Equal(
            "1 TCP relationship and 2 UDP relationships rest on a reused PID's later holder, so the evidence policy leaves "
            + "them out as candidates; counting candidates draws them. All 2 of its TCP records belong to the relationships "
            + "left out. All 3 of its UDP records belong to the relationships left out.",
            RelationshipCensusText.Explain(Census(withheld, udp: withheldUdp), Culture));
        Assert.Equal("no relationship drawn · 2 UDP relationships left out as candidates",
            RelationshipCensusText.Brief(Census(none, udp: withheldUdp)));

        // A capture that collected ALPC can join RPC calls, so nothing is said of them.
        Assert.DoesNotContain("ALPC", RelationshipCensusText.Explain(Census(none, rpc: 4, linked: true), Culture), StringComparison.Ordinal);
        Assert.Contains("ALPC", RelationshipCensusText.Explain(Census(none, rpc: 4), Culture), StringComparison.Ordinal);
    }
}
