using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// An RPC channel's calls and a process's HTTP exchanges as their rungs' exports write them (§6.4, R18): every one of the
/// scope up to a limit, read in one pass, each at its session time with how it went, and an export that says what the limit
/// left out.
/// </summary>
public sealed class ChannelRungRowsTests
{
    private static readonly DateTimeOffset Exported = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "§6.4: a channel's calls and a process's exchanges are read whole up to a limit, each at its session time with how it went")]
    public void AChannelsCallsAndExchangesAreReadUpToALimit()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = LinkedRpcCalls();
        Publish(session.Store, [.. rows.Select(row => row with { SessionRelativeTicks = row.NativeTicks * 100 })], fields: fields,
            coverage: RpcLedger(alpc: true));
        ProcessNode client = SessionOverviewProjector.Project(session.Store).Nodes.Single(node => node.ProcessId == 400);
        string channel = Assert.Single(SessionRpcCalls.Channels(session.Store, client.Id).Channels).Key;

        ChannelRungRead all = ChannelRungRows.Read(session.Store, channel, null, 3);
        Assert.True(all.Complete);
        Assert.Equal(
        [
            ("RPC call at +0.000010 s", "2.0 µs · procedure 7 · succeeded · served by PID 1960"),
            ("RPC call at +0.000020 s", "1.0 µs · procedure 7 · succeeded · other end unresolved: no ALPC send on its thread during the call"),
            ("RPC call at +0.000030 s", "4.0 µs · procedure 7 · failed, status 5 · served by PID 1960"),
        ], all.Rows.Select(row => (row.Label, row.Detail)));
        Assert.All(all.Rows, row => Assert.Equal((2L, (long?)null, Mechanism.Rpc, DetailLevel.Evidence),
            (row.ObservationCount, row.KnownBytes, row.Mechanism, row.DescendsTo)));

        // The limit cuts them short and says so, as icat export --limit does.
        ChannelRungRead cut = ChannelRungRows.Read(session.Store, channel, null, 2);
        Assert.Equal((false, 2), (cut.Complete, cut.Rows.Count));
        string[] path = [client.GroupKey, client.Id.ToString(), channel];
        SessionExportResult limited = SessionExport.Build(session.Store, new(path, null, false, ExportFormat.Json, EvidenceLimit: 2), Exported);
        Assert.Equal((2, false), (limited.Rows, limited.Context.Complete));
        Assert.Contains("The export holds this scope's first 2 calls, leaving the rest out; raise --limit for them.", limited.Context.Caveats);
        SessionExportResult whole = SessionExport.Build(session.Store, new(path, null, false, ExportFormat.Json), Exported);
        Assert.Equal((3, true), (whole.Rows, whole.Context.Complete));
        Assert.DoesNotContain(whole.Context.Caveats, caveat => caveat.StartsWith("The export holds", StringComparison.Ordinal));
        // A call whose stop the capture never saw is open, with no status; an exchange whose response never ended says so.
        using var open = new TemporarySession();
        ObservationRowV1 start = RpcCall(400, ObservationKind.RequestStart, Direction.Outbound, 400, 40, new Guid(4, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 1),
            ServiceControlInterface) with { SessionRelativeTicks = 40_000 };
        Publish(open.Store, [.. rows.Select(row => row with { SessionRelativeTicks = row.NativeTicks * 100 }), start], fields: fields,
            coverage: RpcLedger(alpc: true));
        LadderRow unended = ChannelRungRows.Read(open.Store, channel, null, 10).Rows[^1];
        Assert.Equal(("RPC call at +0.000040 s", "open at capture end · no status · other end unresolved: not completed, so no window to follow", 1L),
            (unended.Label, unended.Detail, unended.ObservationCount));
        using var web = new TemporarySession();
        (ObservationRowV1[] buffers, SourceFieldRowV1[] parts) = HttpExchangeRows();
        Publish(web.Store, [.. buffers.Select(row => row with { SessionRelativeTicks = row.NativeTicks * 100 })], fields: parts);
        ProcessNode fetcher = SessionOverviewProjector.Project(web.Store).Nodes.Single(node => node.ProcessId == 4242);
        string exchanges = Assert.Single(SessionHttpExchanges.Channels(web.Store, fetcher.Id).Channels).Key;
        ChannelRungRead read = ChannelRungRows.Read(web.Store, exchanges, null, 10);
        Assert.True(read.Complete);
        Assert.Equal(
        [
            ("HTTP exchange at +0.000001 s", "2.0 µs · exchange 1 · request 181 B + 100 B · response 115 B + 1,000 B", 6L),
            ("HTTP exchange at +0.000002 s", "1.1 µs · exchange 2 · request 181 B · response 115 B + 0 B", 3L),
            ("HTTP exchange at +0.000004 s",
                "response end not recorded · exchange 3 · request 181 B · response 115 B + 50 B · not recorded whole: response body", 3L),
            ("HTTP exchange at +0.000010 s", "200 ns · exchange 1 · request 181 B · response 115 B + 7 B", 3L),
            ("HTTP exchange at +0.000020 s", "100 ns · exchange 3 · request 0 B · response 115 B + 9 B · not recorded whole: request head", 2L),
        ], read.Rows.Select(row => (row.Label, row.Detail, row.ObservationCount)));
        Assert.All(read.Rows, row => Assert.Equal(((long?)null, Mechanism.Http, DetailLevel.Evidence), (row.KnownBytes, row.Mechanism, row.DescendsTo)));
        Assert.Equal((false, 2), (ChannelRungRows.Read(web.Store, exchanges, null, 2).Complete, ChannelRungRows.Read(web.Store, exchanges, null, 2).Rows.Count));

        Assert.Equal(("1 call", "2 calls", "1 exchange", "1,500 exchanges"),
            (ChannelRungRows.Counted(channel, 1), ChannelRungRows.Counted(channel, 2), ChannelRungRows.Counted("http:x", 1),
                ChannelRungRows.Counted("http:x", 1_500)));
    }
}
