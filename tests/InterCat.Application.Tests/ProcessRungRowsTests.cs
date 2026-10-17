using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;
using Xunit;

namespace InterCat.Application.Tests;

/// <summary>
/// The rows a process's rung lists beside its paired channels, as the window lists them and an export writes them (§7.1,
/// R18): what each says, what it measured, and how they rank together.
/// </summary>
public sealed class ProcessRungRowsTests
{
    private static readonly ConnectionSummary Https = new("transport:https", Mechanism.Tcp, "192.168.1.10:52000",
        "142.250.1.1:443", 3, 2, 1_400, 0, 0, 0, 0, OpenWitnessed: true, CloseWitnessed: false)
    {
        Opening = ConnectionOpening.Connected,
    };

    [Fact(DisplayName = "§7.1: a connection's row names its other end, says what its records measured, and ranks by bytes only under a byte ranking")]
    public void AConnectionsRowSaysWhatItMeasured()
    {
        LadderRow row = ProcessRungRows.Connection(Https, null, CultureInfo.InvariantCulture);
        Assert.Equal(("transport:https", "TCP to 142.250.1.1:443",
            "TCP from 192.168.1.10:52000 · connected in the capture, still open at its end · 1,400 B sent, no receive recorded",
            3L, (long?)1_400, Mechanism.Tcp, DetailLevel.Evidence),
            (row.Key, row.Label, row.Detail, row.ObservationCount, row.KnownBytes, row.Mechanism, row.DescendsTo));
        Assert.Null(row.Ranked);
        Assert.Equal(new RankedValue(RankingMetric.BytesSent, 1_400, 2, 0),
            ProcessRungRows.Connection(Https, RankingMetric.BytesSent, CultureInfo.InvariantCulture).Ranked);

        // Nothing it transferred measured a size, so it knows no bytes, never zero; a datagram flow says so.
        ConnectionSummary blind = new("transport:dns", Mechanism.Udp, "192.168.1.10:53000", "8.8.8.8:53", 1, 1, 0, 1, 0, 0, 0,
            OpenWitnessed: false, CloseWitnessed: false);
        LadderRow udp = ProcessRungRows.Connection(blind, RankingMetric.BytesSent, CultureInfo.InvariantCulture);
        Assert.Equal(("UDP to 8.8.8.8:53", "UDP from 192.168.1.10:53000 · a datagram flow · 1 send unmeasured, no receive recorded",
            (long?)null), (udp.Label, udp.Detail, udp.KnownBytes));
        Assert.Equal(new RankedValue(RankingMetric.BytesSent, null, 0, 1), udp.Ranked);

        // What it received counts too.
        Assert.Equal(1_500, ProcessRungRows.Connection(Https with { Receives = 1, ReceivedBytes = 100 }, null,
            CultureInfo.InvariantCulture).KnownBytes);
    }

    [Fact(DisplayName = "§7.1: an RPC channel's row says which side's calls it holds and who is at their other end, and HTTP's that they are a client's")]
    public void RpcAndHttpRowsSayWhatTheyHold()
    {
        var counts = new RpcCallCounts
        {
            Calls = 3, Started = 3, Completed = 3, Failed = 1, OpenAtCaptureEnd = 0, StartNotObserved = 0, NoActivityId = 0, Ambiguous = 0,
        };
        var served = new RpcChannelSummary("rpc:served", default, RpcCallSide.Server, null, counts, null, 6)
        {
            Peers = new RpcChannelPeers(2, new Dictionary<RpcPeerState, long>(), [new RpcChannelPeer(4_200, null, "client.exe", null, 2)]),
        };
        LadderRow rpc = ProcessRungRows.RpcChannel(served, CultureInfo.InvariantCulture);
        Assert.Equal(("rpc:served", "RPC calls served on " + RpcInterfaceNames.Describe(null),
            "RPC server · called by client.exe · 4200 (2 of 3) · 3 calls · 1 failed", 6L, (long?)null, Mechanism.Rpc, DetailLevel.Channel),
            (rpc.Key, rpc.Label, rpc.Detail, rpc.ObservationCount, rpc.KnownBytes, rpc.Mechanism, rpc.DescendsTo));
        Assert.Equal("RPC client · 3 calls · 1 failed", ProcessRungRows.RpcChannel(served with { Side = RpcCallSide.Client, Peers = null },
            CultureInfo.InvariantCulture).Detail);

        LadderRow http = ProcessRungRows.HttpChannel(new HttpChannelSummary("http:one", default, 2, 1, 7, 300, 4_000, null),
            CultureInfo.InvariantCulture);
        Assert.Equal(("http:one", "HTTP exchanges through WinINet", "HTTP client · 2 exchanges · 1 not recorded whole", 7L,
            (long?)null, Mechanism.Http, DetailLevel.Channel),
            (http.Key, http.Label, http.Detail, http.ObservationCount, http.KnownBytes, http.Mechanism, http.DescendsTo));
    }

    [Fact(DisplayName = "§6.1: a process's rows rank together, RPC and HTTP after every transport row under a byte ranking, and its channels alone keep their order")]
    public void TheRowsRankTogether()
    {
        LadderRow channel = new("transport:paired", "127.0.0.1:50000 ↔ 127.0.0.1:8080", "TCP", 9, 800, Mechanism.Tcp,
            CoverageState.UnknownCoverage, DetailLevel.Channel, AccountingSide.CanonicalOwner)
        {
            Ranked = new RankedValue(RankingMetric.BytesSent, 800, 2, 0),
        };
        LadderRow rpc = new("rpc:calls", "RPC calls to svcctl", "RPC client · 1 call", 20, null, Mechanism.Rpc,
            CoverageState.UnknownCoverage, DetailLevel.Channel, AccountingSide.CanonicalOwner);
        LadderRow http = new("http:exchanges", "HTTP exchanges through WinINet", "HTTP client · 1 exchange", 12, null,
            Mechanism.Http, CoverageState.UnknownCoverage, DetailLevel.Channel, AccountingSide.CanonicalOwner);
        LadderRow connection = ProcessRungRows.Connection(Https, RankingMetric.BytesSent, CultureInfo.InvariantCulture);

        // By bytes: the connection's 1,400 B, the channel's 800 B, then the rows that carry no transport bytes.
        IReadOnlyList<LadderRow> ranked = ProcessRungRows.Ranked([channel], [connection], [rpc], [http], RankingMetric.BytesSent);
        Assert.Equal(["transport:https", "transport:paired", "http:exchanges", "rpc:calls"], ranked.Select(row => row.Key));
        Assert.Equal(new RankedValue(RankingMetric.BytesSent, null, 0, 0), ranked[^1].Ranked);

        // A channel the ranking gave no value ranks every row by records, none by a value.
        IReadOnlyList<LadderRow> unranked = ProcessRungRows.Ranked([channel with { Ranked = null }], [connection], [rpc], [http],
            RankingMetric.BytesSent);
        Assert.Equal(["rpc:calls", "http:exchanges", "transport:paired", "transport:https"], unranked.Select(row => row.Key));
        Assert.All(unranked, row => Assert.Null(row.Ranked));

        // By records, nothing ranks by a value; and channels with nothing beside them are the ladder's as they are.
        Assert.All(ProcessRungRows.Ranked([channel with { Ranked = null }], [], [rpc], [], null), row => Assert.Null(row.Ranked));
        IReadOnlyList<LadderRow> alone = [channel];
        Assert.Same(alone, ProcessRungRows.Ranked(alone, [], [], [], RankingMetric.BytesSent));
    }
}
