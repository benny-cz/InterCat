using InterCat.Analysis;
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
/// The evidence policy a window reads a session under (§6.8): every read its evidence source makes binds a record to a
/// process as strongly as that policy allows, so a ranking, a brushed count, the timeline's focus and lanes, a process's
/// rows and the records E lists never disagree with the overview about whose a reused PID's records are.
/// </summary>
public sealed class EvidencePolicyTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";
    private const string LocalEnd = "192.168.1.5:52000";
    private const string RemoteEnd = "10.0.0.9:443";

    [Fact(DisplayName = "§6.8: every read of an evidence source binds a reused PID's later holder's records as its policy says")]
    public void EveryReadBindsAsItsPolicySays() => SingleThreadedContext.Run(async () =>
    {
        // PID 100 exits and is created again, and its second holder sends twice, 8 bytes each: candidates that could be
        // late records of the first holder. The server receives one of them.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Timed(Lifecycle(5, ObservationKind.Create, 200, 1)),
            Timed(Lifecycle(10, ObservationKind.Create, 100, 2)),
            Timed(Lifecycle(30, ObservationKind.Exit, 100, 3)),
            Timed(Lifecycle(40, ObservationKind.Create, 100, 4)),
            Timed(Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 5).Between(ClientEnd, ServerEnd)),
            Timed(Transfer(55, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 6).Between(ServerEnd, ClientEnd)),
            Timed(Transfer(60, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 7).Between(ClientEnd, ServerEnd)),
        ]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        ProcessInstanceId later = overview.Nodes.Single(node => node.PidHolder == 2).Id;
        var scope = new EvidenceScope("Records owned by the later holder", null, [later], null, null);
        TimelineFocus focus = Assert.IsType<TimelineFocus>(TimelineFocus.Of(scope));
        TimeRange extent = overview.Extent!.Value;

        // Correlated evidence counts its creation alone and no send; candidates count its two sends and their bytes too.
        foreach ((EvidencePolicy policy, long records, long sent) in Expected)
        {
            var source = new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation, policy);
            Assert.Equal(policy, source.Policy);
            SessionIntervalCounts counts = await source.CountAsync(extent, CancellationToken.None);
            Assert.Equal(records, counts.ProcessRecords.GetValueOrDefault(later)?.Sum(count => count.Records) ?? 0);
            SessionByteMeasures bytes = await source.ByteMeasuresAsync(null, CancellationToken.None);
            Assert.Equal(sent, bytes.ByProcess.GetValueOrDefault(later)?.SentBytes ?? 0);
            SessionFocusedTimeline timeline = await source.FocusedTimelineAsync(extent, 8, focus, CancellationToken.None);
            Assert.Equal(records, timeline.Focus.Sum(bucket => bucket.ObservationCount));
            Assert.Equal(records, (await source.ReadAsync(scope, null, CancellationToken.None)).Records.Count);
            Assert.Equal(records, (await source.ReadScopeAsync(scope, 100, CancellationToken.None)).Records.Count);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation, (EvidencePolicy)0));
    });

    [Fact(DisplayName = "§6.8: a reused PID's later holder's RPC calls, HTTP exchanges, one-sided connections, call and peer rankings and lanes are its own only where candidates count")]
    public void ALaterHoldersRowsRankingsAndLanesFollowThePolicy() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        PublishLaterHolder(session);

        // The keys its rows are read by, as counting candidates lists them.
        SessionOverviewBundle counted = SessionOverviewProjector.Project(session.Store, EvidencePolicy.IncludeCandidates);
        ProcessInstanceId later = counted.Nodes.Single(node => node.PidHolder == 2).Id;
        string calls = RpcChannelKeys.Channel(later, RpcCallSide.Client, ServiceControlInterface);
        string exchanges = HttpExchangeKeys.Channel(later);
        var candidates = new SessionEvidenceSource(session.Path, counted.SessionId, counted.Generation, EvidencePolicy.IncludeCandidates);
        string call = Assert.Single((await candidates.RpcCallsAsync(calls, 0, null, CancellationToken.None)).Calls).Key;
        TimeRange extent = counted.Extent!.Value;

        foreach (EvidencePolicy policy in new[] { EvidencePolicy.IncludeCorrelated, EvidencePolicy.IncludeCandidates })
        {
            bool counts = policy == EvidencePolicy.IncludeCandidates;
            var source = new SessionEvidenceSource(session.Path, counted.SessionId, counted.Generation, policy);

            // Its RPC channel, its HTTP exchanges and its one-sided connection are listed as its own, each by its records...
            Assert.Equal(counts ? [(calls, 2L)] : [],
                (await source.RpcChannelsAsync(later, null, CancellationToken.None)).Channels.Select(channel => (channel.Key, channel.Records)));
            Assert.Equal(counts ? [(exchanges, 3L)] : [],
                (await source.HttpChannelsAsync(later, null, CancellationToken.None)).Channels.Select(channel => (channel.Key, channel.Records)));
            Assert.Equal(counts ? [("TCP to " + RemoteEnd, 3L, 100L)] : [],
                (await source.ConnectionsAsync(later, null, CancellationToken.None)).Connections
                    .Select(connection => (connection.Name, connection.Records, connection.SentBytes)));

            // ...and each opened by its key reads its calls or exchanges, drawn in the timeline too, or says it holds none.
            RpcCallPage page = await source.RpcCallsAsync(calls, 0, null, CancellationToken.None);
            RpcCallPage through = await source.RpcCallsThroughAsync(call, null, CancellationToken.None);
            RpcCallSpanPage callSpans = await source.RpcSpansAsync(calls, extent, 8, CancellationToken.None);
            HttpExchangePage exchangePage = await source.HttpExchangesAsync(exchanges, 0, null, CancellationToken.None);
            HttpExchangeSpanPage exchangeSpans = await source.HttpSpansAsync(exchanges, extent, 8, CancellationToken.None);
            Assert.Equal(counts ? (1, 1, 1L, 1, 1L) : (0, 0, 0L, 0, 0L),
                (page.Calls.Count, through.Calls.Count, callSpans.Total, exchangePage.Exchanges.Count, exchangeSpans.Total));
            Assert.Equal(!counts,
                page.Problem is not null && through.Problem is not null && callSpans.Problem is not null
                && exchangePage.Problem is not null && exchangeSpans.Problem is not null);
            Assert.Equal(counts,
                page.Problem is null && through.Problem is null && callSpans.Problem is null
                && exchangePage.Problem is null && exchangeSpans.Problem is null);

            // Its completed call ranks it, or belongs to no process the policy admits; its peer, the server, likewise.
            SessionCallMeasures made = await source.CallMeasuresAsync(null, CancellationToken.None);
            Assert.Equal(counts ? (1L, 0L) : (0L, 1L), (made.ByProcess.GetValueOrDefault(later)?.Made ?? 0, made.Unattributed.Made));
            SessionPeerMeasures peers = await source.PeerMeasuresAsync(null, CancellationToken.None);
            Assert.Equal(counts ? (1L, 0L) : (0L, 8L), (peers.ByProcess.GetValueOrDefault(later)?.Peers ?? 0, peers.Unattributed));

            // Its process lane, its outbound direction row and an interval's count of its own bytes hold its 116 bytes sent.
            SessionOwnerByteMeasures lanes = await source.OwnerBytesAsync(extent, 4, 4, [later], CancellationToken.None);
            SessionDirectionByteMeasures directions = await source.DirectionBytesAsync(extent, 4, later, CancellationToken.None);
            SessionIntervalByteMeasures own = await source.IntervalBytesAsync(
                extent, 4, new IntervalByteScope { Owner = later }, CancellationToken.None);
            long sent = counts ? 116 : 0;
            Assert.Equal((sent, sent, sent),
                (lanes.Of(later)!.Columns.Sum(column => column.SentBytes),
                    directions.Of(Direction.Outbound)!.Columns.Sum(column => column.SentBytes),
                    own.Columns.Sum(column => column.SentBytes)));

            // A mechanism's lane counts every record whoever holds it, so it reads alike under either policy.
            SessionMechanismByteMeasures mechanisms = await source.LaneBytesAsync(extent, 4, [Mechanism.Tcp], CancellationToken.None);
            Assert.Equal(116, mechanisms.Of(Mechanism.Tcp)!.Columns.Sum(column => column.SentBytes));
        }
    });

    [Fact(DisplayName = "§6.8: a reused PID's later holder's rung lists its rows where candidates count, and where none does says why beside the action that counts them")]
    public void ALaterHoldersRungSaysWhyItListsNoRow() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        PublishLaterHolder(session);

        // Counting correlated evidence only, its one call belongs to no process, which the call rankings say in the singular.
        using (WorkspaceViewModel workspace = Open(session, EvidencePolicy.IncludeCorrelated))
        {
            foreach (RankingMetric metric in new[] { RankingMetric.RpcCallsMade, RankingMetric.RpcCallTime })
            {
                workspace.RankBy = metric;
                await workspace.RankingReady;
                Assert.Contains(" 1 more completed call belongs to no process the evidence policy admits.",
                    workspace.RankingNoteDetail, StringComparison.Ordinal);
            }

            // The later holder's rung lists no row, and says its records were left out as candidates rather than that they
            // are one step away, beside the action that counts them. E lists its creation, which is all it counts.
            await DescendToLaterHolder(workspace);
            Assert.True(workspace.IsEmptyRung);
            Assert.Equal("This process lists no row: PID 100 was held by an earlier process in this capture, so the 10 records "
                + "bound to this one over the session are only candidates, which the evidence policy does not count. Its "
                + "lifecycle records are one step away.", workspace.EmptyReason);
            Assert.True(workspace.OffersRungCandidates);
            Assert.True(workspace.OffersEvidenceStep);
            Assert.True(workspace.ShowEvidence());
            await workspace.EvidenceReady;
            Assert.Single(workspace.RungRows);
            Assert.StartsWith("Records owned by later.exe · PID 100 #2", workspace.EvidenceScopeText, StringComparison.Ordinal);
            Assert.False(workspace.OffersRungCandidates);
            Assert.True(workspace.Ascend());
            Assert.True(workspace.OffersRungCandidates);

            // The PID's first holder left nothing out: its rung says what an empty process rung always says, offering E alone.
            workspace.ReturnTo(0);
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "first.exe");
            Assert.True(workspace.Descend());
            workspace.SelectedRung = Assert.Single(workspace.RungRows);
            Assert.True(workspace.Descend());
            Assert.True(workspace.IsEmptyRung);
            Assert.StartsWith("No admitted paired TCP channel belongs to this process", workspace.EmptyReason, StringComparison.Ordinal);
            Assert.False(workspace.OffersRungCandidates);
        }

        // Counting candidates, the later holder ranks by its call, and its rung lists its one-sided connection, its HTTP
        // exchanges, its paired channel and its RPC channel, with nothing left out to offer.
        using (WorkspaceViewModel workspace = Open(session, EvidencePolicy.IncludeCandidates))
        {
            workspace.RankBy = RankingMetric.RpcCallsMade;
            await workspace.RankingReady;
            Assert.Equal("1 call", workspace.RungRows.Single(row => row.Label == "later.exe").Figure);
            Assert.DoesNotContain("more completed call", workspace.RankingNoteDetail, StringComparison.Ordinal);
            await DescendToLaterHolder(workspace);
            Assert.False(workspace.IsEmptyRung);
            Assert.False(workspace.OffersRungCandidates);
            Assert.Equal(4, workspace.RungRows.Count);
            Assert.Single(workspace.RungRows, row => TransportConnection.IsKey(row.Key));
            Assert.Single(workspace.RungRows, row => HttpExchangeKeys.IsHttp(row.Key));
            Assert.Single(workspace.RungRows, row => RpcChannelKeys.IsRpc(row.Key));
            Assert.Single(workspace.RungRows, row => row.Label == "↔ server.exe · PID 200");
        }

        // One record left out is said so.
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        ProcessNode one = overview.Nodes.Single(node => node.PidHolder == 2) with { WithheldRecords = 1 };
        Assert.Contains(", so the 1 record bound to this one over the session is only a candidate, which ",
            WorkspaceRowBuilder.ExplainWithheldRung(one), StringComparison.Ordinal);
    });

    /// <summary>The later holder's group, then its own rung, once every read of its rows has answered.</summary>
    private static async Task DescendToLaterHolder(WorkspaceViewModel workspace)
    {
        workspace.ReturnTo(0);
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "later.exe");
        Assert.True(workspace.Descend());
        workspace.SelectedRung = Assert.Single(workspace.RungRows);
        Assert.True(workspace.Descend());
        await workspace.RpcReady;
        await workspace.HttpReady;
        await workspace.ConnectionsReady;
    }

    private static WorkspaceViewModel Open(TemporarySession session, EvidencePolicy policy)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store, policy);
        return new(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation, policy));
    }

    /// <summary>
    /// PID 100 runs first.exe, exits, and is created again running later.exe, whose every record but its creation is a
    /// candidate that could be a late record of first.exe: it sends server.exe 8 bytes twice over a paired channel, calls the
    /// service control manager once over RPC, makes one HTTP exchange, and sends 100 bytes over a TCP connection to another
    /// host, whose other end no record holds.
    /// </summary>
    private static void PublishLaterHolder(TemporarySession session)
    {
        Guid activity = new(1, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 1);
        ObservationRowV1[] http = [Http(80, 2001, 10, 181), Http(81, 2003, 11, 115), Http(82, 2004, 12, 7)];
        ObservationRowV1[] rows =
        [
            Timed(Lifecycle(5, ObservationKind.Create, 200, 1) with { ResourceName = @"C:\Tools\server.exe" }),
            Timed(Lifecycle(10, ObservationKind.Create, 100, 2) with { ResourceName = @"C:\Tools\first.exe" }),
            Timed(Lifecycle(30, ObservationKind.Exit, 100, 3)),
            Timed(Lifecycle(40, ObservationKind.Create, 100, 4) with { ResourceName = @"C:\Tools\later.exe" }),
            Timed(Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 5).Between(ClientEnd, ServerEnd)),
            Timed(Transfer(55, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 6).Between(ServerEnd, ClientEnd)),
            Timed(Transfer(60, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 7).Between(ClientEnd, ServerEnd)),
            Timed(RpcCall(70, ObservationKind.RequestStart, Direction.Outbound, 100, 8, activity, ServiceControlInterface)),
            Timed(RpcCall(72, ObservationKind.RequestEnd, Direction.Outbound, 100, 9, activity, status: 0)),
            .. http,
            Timed(Transfer(90, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 13).Between(LocalEnd, RemoteEnd)),
            Timed(Transfer(91, ObservationKind.Send, AccountingSide.SendSide, 100, 100, 14).Between(LocalEnd, RemoteEnd)),
            Timed(Transfer(92, ObservationKind.Disconnect, AccountingSide.EndpointActivity, 0, 100, 15).Between(LocalEnd, RemoteEnd)),
        ];
        Publish(session.Store, rows, coverage: RpcLedger(alpc: false), fields:
        [
            .. rows.Where(row => row is { Mechanism: Mechanism.Rpc, Kind: ObservationKind.RequestStart })
                .Select(start => Field(start, SourceField.RpcProcedureNumber, 7)),
            .. http.SelectMany(row => new[]
            {
                Field(row, SourceField.HttpExchangeId, 1),
                Field(row, SourceField.ContentBufferSequence, 0),
                Field(row, SourceField.ContentBufferFlags, 3),
            }),
        ]);
    }

    /// <summary>A WinINet record of <paramref name="eventId"/>, raised in PID 100, which binds it by that header (ADR-030).</summary>
    private static ObservationRowV1 Http(long ticks, ushort eventId, ulong ordinal, long bytes) =>
        Timed(Transfer(ticks, eventId <= 2002 ? ObservationKind.Send : ObservationKind.Receive,
            eventId <= 2002 ? AccountingSide.SendSide : AccountingSide.ReceiveSide, bytes, null, ordinal) with
        {
            Mechanism = Mechanism.Http,
            Layer = ObservationLayer.Application,
            EventId = eventId,
            HeaderProcessId = 100,
            Direction = eventId <= 2002 ? Direction.Outbound : Direction.Inbound,
            ByteDomain = ByteDomain.ApplicationPayload,
        });

    /// <summary>Each policy, with the later holder's records and bytes sent that it counts.</summary>
    private static readonly (EvidencePolicy Policy, long Records, long Sent)[] Expected =
    [
        (EvidencePolicy.IncludeCorrelated, 1, 0),
        (EvidencePolicy.IncludeCandidates, 3, 16),
    ];

    /// <summary>A row whose session time is its reading in workspace ticks.</summary>
    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };
}
