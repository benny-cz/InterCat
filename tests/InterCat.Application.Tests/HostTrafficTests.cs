using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// Each other host's traffic (§6.3, R21): exactly the connections the census counts as to it, with what their transfers
/// measured each way, ranked by records or by bytes - measured first, then those that stated no size, then those with none.
/// </summary>
public sealed class HostTrafficTests
{
    [Fact(DisplayName = "§6.3: each other host's traffic is its census's connections and what they measured, ranked by records or bytes, an unmeasured host never ranked as a zero")]
    public void EachHostsTrafficIsItsConnectionsAndWhatTheyMeasured()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        IReadOnlyList<HostLink> links = overview.HostLinks;

        // Ranked by records, the hosts and their counts are the census's own, in its order, ports and all.
        IReadOnlyList<HostTraffic> byRecords = SessionHostTraffic.Of(session.Store, links);
        Assert.Equal(OtherHost.Of(links), byRecords.Select(host => host.Host));
        Assert.Equal(["142.250.1.1", "9.9.9.9", "8.8.8.8", "20.42.65.1", "52.1.1.1", "60.0.0.1"], Addresses(byRecords));

        // Its bytes each way: the browser's measured send and receive, and the updater's two sends that stated no size.
        HostTraffic busiest = byRecords[0];
        Assert.Equal((3L, 500L, 2L, 1L, 2_000L, 0L), (busiest.Sends, busiest.SentBytes, busiest.UnmeasuredSends, busiest.Receives,
            busiest.ReceivedBytes, busiest.UnmeasuredReceives));
        Assert.Equal("500 B sent (2 sends unmeasured), 2,000 B received", busiest.Transfers(System.Globalization.CultureInfo.InvariantCulture));

        // Ranked by bytes, measured hosts come first, the most first; then the host whose sends stated no size, though its
        // address sorts later; then the one that sent nothing. Hosts that received nothing follow by records.
        Assert.Equal(["20.42.65.1", "142.250.1.1", "9.9.9.9", "8.8.8.8", "60.0.0.1", "52.1.1.1"],
            Addresses(SessionHostTraffic.Of(session.Store, links, RankingMetric.BytesSent)));
        Assert.Equal(["142.250.1.1", "52.1.1.1", "8.8.8.8", "9.9.9.9", "20.42.65.1", "60.0.0.1"],
            Addresses(SessionHostTraffic.Of(session.Store, links, RankingMetric.BytesReceived)));
        Assert.Equal(["20.42.65.1", "142.250.1.1", "52.1.1.1", "8.8.8.8", "9.9.9.9", "60.0.0.1"],
            Addresses(SessionHostTraffic.Of(session.Store, links, RankingMetric.EndpointBytes)));
        HostTraffic unmeasured = byRecords.Single(host => host.Host.Address == "60.0.0.1");
        Assert.Equal(new RankedValue(RankingMetric.BytesSent, null, 0, 1), unmeasured.RankedBy(RankingMetric.BytesSent));
        Assert.Equal(new RankedValue(RankingMetric.EndpointBytes, 2_500, 2, 2), busiest.RankedBy(RankingMetric.EndpointBytes));
        Assert.Equal(new RankedValue(RankingMetric.Records, 4, 4, 0), busiest.RankedBy(RankingMetric.Records));
        Assert.Null(busiest.RankedBy(RankingMetric.RpcCallsMade));

        // Within an interval, only the records in it, and only the hosts that made one there.
        IReadOnlyList<HostTraffic> within = SessionHostTraffic.Of(session.Store, links, interval: new TimeRange(15, 45));
        Assert.Equal(["9.9.9.9", "8.8.8.8", "142.250.1.1", "20.42.65.1"], Addresses(within));
        Assert.Equal((1L, 0L, 2_000L), (within[2].Host.Records, within[2].Sends, within[2].ReceivedBytes));

        // No link reads nothing; a metric hosts hold nothing of is refused.
        Assert.Empty(SessionHostTraffic.Of(session.Store, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionHostTraffic.Of(session.Store, links, RankingMetric.RpcCallsMade));
        Assert.True(SessionHostTraffic.Ranks(RankingMetric.EndpointBytes));
        Assert.False(SessionHostTraffic.Ranks(RankingMetric.ActivePeers));
    }

    private static string[] Addresses(IEnumerable<HostTraffic> hosts) => [.. hosts.Select(host => host.Host.Address)];

    /// <summary>
    /// browser.exe's TCP to 142.250.1.1, a 500 B send and a 2,000 B receive, to 20.42.65.1, a 9,000 B send, and to
    /// 9.9.9.9 over two connections, 50 B each, and its UDP to a resolver, 40 B out and 80 B back; updater.exe's two sends
    /// of no stated size to 142.250.1.1 and one to 60.0.0.1; and svc.exe's one 300 B receive from 52.1.1.1. Each row's
    /// session time is its reading in workspace ticks.
    /// </summary>
    private static ObservationRowV1[] Rows() =>
    [
        Timed(Lifecycle(1, ObservationKind.Create, 300, 1) with { ResourceName = @"C:\Apps\browser.exe" }),
        Timed(Lifecycle(2, ObservationKind.Create, 310, 2) with { ResourceName = @"C:\Apps\updater.exe" }),
        Timed(Lifecycle(3, ObservationKind.Create, 320, 3) with { ResourceName = @"C:\Windows\svc.exe" }),
        Timed(Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 500, 300, 10).Between("192.168.1.10:52000", "142.250.1.1:443")),
        Timed(Transfer(20, ObservationKind.Receive, AccountingSide.ReceiveSide, 2_000, 300, 11).Between("192.168.1.10:52000", "142.250.1.1:443")),
        Timed(Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 9_000, 300, 12).Between("192.168.1.10:52001", "20.42.65.1:443")),
        Timed(Transfer(33, ObservationKind.Send, AccountingSide.SendSide, 50, 300, 19).Between("192.168.1.10:52005", "9.9.9.9:443")),
        Timed(Transfer(36, ObservationKind.Send, AccountingSide.SendSide, 50, 300, 20).Between("192.168.1.10:52006", "9.9.9.9:443")),
        Timed(Transfer(40, ObservationKind.Send, AccountingSide.SendSide, 40, 300, 13).Between("192.168.1.10:61000", "8.8.8.8:53")
            with { Mechanism = Mechanism.Udp, EventId = 42 }),
        // A UDP receive names the datagram's sender first (ADR-019).
        Timed(Transfer(41, ObservationKind.Receive, AccountingSide.ReceiveSide, 80, 300, 14).Between("8.8.8.8:53", "192.168.1.10:61000")
            with { Mechanism = Mechanism.Udp, EventId = 43 }),
        Timed(Transfer(50, ObservationKind.Send, AccountingSide.SendSide, null, 310, 15).Between("192.168.1.10:53000", "142.250.1.1:8443")),
        Timed(Transfer(51, ObservationKind.Send, AccountingSide.SendSide, null, 310, 16).Between("192.168.1.10:53000", "142.250.1.1:8443")),
        Timed(Transfer(60, ObservationKind.Send, AccountingSide.SendSide, null, 310, 17).Between("192.168.1.10:53001", "60.0.0.1:443")),
        Timed(Transfer(70, ObservationKind.Receive, AccountingSide.ReceiveSide, 300, 320, 18).Between("192.168.1.10:53002", "52.1.1.1:443")),
    ];

    /// <summary>A row whose session time is its reading in workspace ticks.</summary>
    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };
}
