using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Analysis.Tests;

/// <summary>
/// What became of a related mechanism's records (§6.3, §6.8): each counted once, in a relationship the policy admits or
/// withholds, in a connection to another host, in one on this machine whose other end went unseen, in one no single
/// process holds at an end, or naming no complete endpoint - so a graph with no relationship can say why.
/// </summary>
public sealed class TransportCensusTests
{
    [Fact(DisplayName = "§6.8: a census counts every TCP record once, by what became of it, and a candidate relationship only as the policy admits it")]
    public void ACensusCountsEveryRecordOnceByWhatBecameOfIt()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            // A loopback connection with one process at each end: one relationship of 4 records.
            Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1).Between("127.0.0.1:50000", "127.0.0.1:8080"),
            Transfer(101, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 2).Between("127.0.0.1:8080", "127.0.0.1:50000"),
            Transfer(102, ObservationKind.Send, AccountingSide.SendSide, 4, 200, 3).Between("127.0.0.1:8080", "127.0.0.1:50000"),
            Transfer(103, ObservationKind.Receive, AccountingSide.ReceiveSide, 4, 100, 4).Between("127.0.0.1:50000", "127.0.0.1:8080"),

            // One whose server end lies in a reused PID's later instance: a candidate relationship of 2 records.
            Lifecycle(10, ObservationKind.Create, 210, 5),
            Lifecycle(20, ObservationKind.Exit, 210, 6),
            Lifecycle(30, ObservationKind.Create, 210, 7),
            Transfer(110, ObservationKind.Send, AccountingSide.SendSide, 1, 100, 8).Between("127.0.0.1:50002", "127.0.0.1:8082"),
            Transfer(111, ObservationKind.Receive, AccountingSide.ReceiveSide, 1, 210, 9).Between("127.0.0.1:8082", "127.0.0.1:50002"),

            // Three connections to two other hosts: 4 records.
            Transfer(120, ObservationKind.Send, AccountingSide.SendSide, 500, 300, 10).Between("192.168.1.10:52000", "142.250.1.1:443"),
            Transfer(121, ObservationKind.Receive, AccountingSide.ReceiveSide, 900, 300, 11).Between("192.168.1.10:52000", "142.250.1.1:443"),
            Transfer(122, ObservationKind.Send, AccountingSide.SendSide, 500, 300, 12).Between("192.168.1.10:52001", "142.250.1.1:443"),
            Transfer(123, ObservationKind.Send, AccountingSide.SendSide, 500, 300, 13).Between("192.168.1.10:52002", "142.250.1.2:443"),

            // Three on this machine whose other end no record holds: over loopback, to another address of IPv4's loopback
            // network, and to the end's own address.
            Transfer(130, ObservationKind.Send, AccountingSide.SendSide, 2, 400, 14).Between("127.0.0.1:50005", "127.0.0.1:9999"),
            Transfer(131, ObservationKind.Send, AccountingSide.SendSide, 2, 401, 15).Between("192.168.1.10:50006", "192.168.1.10:7777"),
            Transfer(132, ObservationKind.Send, AccountingSide.SendSide, 2, 402, 21).Between("127.0.0.1:50007", "127.0.0.2:9998"),

            // An end whose records name two processes, and one whose record names none: 3 records no one holds.
            Transfer(140, ObservationKind.Send, AccountingSide.SendSide, 1, 500, 16).Between("127.0.0.1:50010", "127.0.0.1:8090"),
            Transfer(141, ObservationKind.Send, AccountingSide.SendSide, 1, 501, 17).Between("127.0.0.1:50010", "127.0.0.1:8090"),
            Transfer(142, ObservationKind.Send, AccountingSide.SendSide, 1, null, 18).Between("127.0.0.1:50011", "10.0.0.9:80"),

            // A record naming no endpoint, and a UDP datagram the TCP census never counts.
            Transfer(150, ObservationKind.Send, AccountingSide.SendSide, 1, 600, 19),
            Transfer(160, ObservationKind.Send, AccountingSide.SendSide, 1, 700, 20).Between("192.168.1.10:5353", "224.0.0.251:5353")
                with { Mechanism = Mechanism.Udp, EventId = 42 },
        ]);
        (_, TransportRelationIndex relations) = Derive(session.Store);

        TransportCensus census = relations.Census(Mechanism.Tcp, EvidencePolicy.IncludeCorrelated);
        Assert.Equal(new TransportCensus(Mechanism.Tcp,
            Related: 4, Relationships: 1, Withheld: 2, WithheldRelationships: 1,
            ToOtherHosts: 4, OtherHostConnections: 3, OtherHosts: 2,
            ToUnseenLocalEnds: 3, UnseenLocalConnections: 3,
            Unpaired: 3, WithoutEndpoints: 1), census);
        Assert.Equal(17, census.Records);

        // Counting candidates draws the reused PID's relationship; nothing else moves.
        Assert.Equal(census with { Related = 6, Relationships = 2, Withheld = 0, WithheldRelationships = 0 },
            relations.Census(Mechanism.Tcp, EvidencePolicy.IncludeCandidates));

        // The datagram is UDP's, to a multicast address: another host's, as the source names it.
        Assert.Equal(new TransportCensus(Mechanism.Udp, 0, 0, 0, 0, 1, 1, 1, 0, 0, 0, 0),
            relations.Census(Mechanism.Udp, EvidencePolicy.IncludeCorrelated));
        Assert.Throws<ArgumentOutOfRangeException>(() => relations.Census(Mechanism.Rpc, EvidencePolicy.IncludeCorrelated));
    }

    [Fact(DisplayName = "§6.8: an IPv6 end's other end is on this machine at ::1 or an IPv4-mapped loopback address, and elsewhere otherwise")]
    public void AnIpv6EndIsLocalOnlyAtLoopback()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 1, 100, 1).Between("[::1]:50000", "[::1]:8080"),
            Transfer(11, ObservationKind.Send, AccountingSide.SendSide, 1, 100, 2).Between("[::ffff:127.0.0.1]:50001", "[::ffff:127.0.0.2]:8081"),
            Transfer(12, ObservationKind.Send, AccountingSide.SendSide, 1, 100, 3).Between("[fe80::1]:50002", "[2001:db8::7]:443"),
            Transfer(13, ObservationKind.Send, AccountingSide.SendSide, 1, 100, 4).Between("[fe80::1]:50003", "[::ffff:10.0.0.1]:443"),
        ]);
        (_, TransportRelationIndex relations) = Derive(session.Store);

        TransportCensus census = relations.Census(Mechanism.Tcp, EvidencePolicy.IncludeCorrelated);
        Assert.Equal((2L, 2, 2L, 2, 2), (census.ToUnseenLocalEnds, census.UnseenLocalConnections, census.ToOtherHosts,
            census.OtherHostConnections, census.OtherHosts));
    }

    [Fact(DisplayName = "§6.8: a session with no transport record has an empty census, and a policy outside the enumeration is refused")]
    public void ASessionWithoutTransportHasAnEmptyCensus()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [Lifecycle(10, ObservationKind.Create, 100, 1)]);
        (_, TransportRelationIndex relations) = Derive(session.Store);

        Assert.Equal(TransportCensus.None(Mechanism.Tcp), relations.Census(Mechanism.Tcp, EvidencePolicy.IncludeCorrelated));
        Assert.Equal(0, TransportCensus.None(Mechanism.Udp).Records);
        Assert.Throws<ArgumentOutOfRangeException>(() => relations.Census(Mechanism.Tcp, (EvidencePolicy)0));
    }

    private static (ProcessInstanceIndex Processes, TransportRelationIndex Relations) Derive(SessionStore store)
    {
        IReadOnlyList<SegmentReaderV1> segments = Segments(store);
        ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(segments, TestClock);
        return (processes, TransportRelationIndex.Derive(segments, processes));
    }
}
