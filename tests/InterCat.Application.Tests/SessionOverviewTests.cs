using InterCat.Application;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

public sealed class SessionOverviewTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [Fact]
    public void ChannelBoundKeepsGraphAndTimelineAvailableWithoutOmittingSilently()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 1, 100, 1)
                .Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = 1_000 },
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 1, 200, 2)
                .Between(ServerEnd, ClientEnd) with { SessionRelativeTicks = 1_100 },
            Transfer(12, ObservationKind.Send, AccountingSide.SendSide, 1, 300, 3)
                .Between("127.0.0.1:50001", "127.0.0.1:9090") with { SessionRelativeTicks = 1_200 },
            Transfer(13, ObservationKind.Receive, AccountingSide.ReceiveSide, 1, 400, 4)
                .Between("127.0.0.1:9090", "127.0.0.1:50001") with { SessionRelativeTicks = 1_300 },
        ]);

        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store, maximumChannels: 1);
        WorkspaceSnapshot workspace = OverviewWorkspace.From(overview);
        Assert.Equal(2, overview.Edges.Count);
        Assert.Equal(4, overview.GraphEligibleRows);
        Assert.Equal(4, overview.Timeline.Sum(bucket => bucket.ObservationCount));
        Assert.Empty(overview.Channels);
        Assert.Contains("scoped query", overview.ChannelProjectionProblem, StringComparison.Ordinal);
        Assert.Equal(overview.ChannelProjectionProblem, workspace.ChannelProjectionProblem);
        Assert.Contains(overview.Caveats, caveat => caveat == overview.ChannelProjectionProblem);
    }

    [Fact(DisplayName = "R7: one leased generation yields a stable graph and exact graph-eligible timeline")]
    public void OneGenerationProjectsStableOverview()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 100, 100, 1)
                .Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = 1_000 },
            Transfer(15, ObservationKind.Send, AccountingSide.SendSide, 7, 300, 3)
                .Between("127.0.0.1:50001", "127.0.0.1:9090") with { SessionRelativeTicks = 1_500 },
            Transfer(20, ObservationKind.Receive, AccountingSide.ReceiveSide, 100, 200, 2)
                .Between(ServerEnd, ClientEnd) with { SessionRelativeTicks = 2_000 },
        ]);

        SessionOverviewBundle first = SessionOverviewProjector.Project(session.Store);
        SessionOverviewBundle again = SessionOverviewProjector.Project(session.Store);
        WorkspaceSnapshot workspace = OverviewWorkspace.From(first);
        Assert.Equal(first.GraphIdentity, again.GraphIdentity);
        Assert.Equal(first.Nodes, workspace.Processes);
        Assert.Equal(first.Edges, workspace.Edges);
        Assert.Equal(first.Timeline, workspace.Timeline);
        Channel channel = Assert.Single(workspace.Channels);
        Assert.Equal(first.Channels, workspace.Channels);
        Assert.Equal(first.Edges[0].Key, channel.EdgeKey);
        Assert.Equal(first.Edges[0].ObservationCount, channel.ObservationCount);
        Assert.Equal(Direction.UnknownDirection, channel.Direction);
        Assert.Null(channel.KnownBytes);
        Assert.Equal(CoverageState.UnknownCoverage, channel.Coverage);
        Assert.Contains(ClientEnd, channel.Name, StringComparison.Ordinal);
        Assert.Contains(ServerEnd, channel.Name, StringComparison.Ordinal);
        var ladder = new DetailLadder(SyntheticWorkspace.Root(workspace));
        ProcessNode endpoint = workspace.Processes.Single(node => node.Id == first.Edges[0].SourceId);
        LadderRow groupRow = LadderProjection.Project(workspace, ladder.Current).Rows.Single(
            row => row.Key == endpoint.GroupKey);
        Assert.True(ladder.TryDescend(LadderProjection.DescentFor(groupRow, ladder.Current, workspace.Extent), out _));
        LadderRow processRow = LadderProjection.Project(workspace, ladder.Current).Rows.Single(
            row => row.Key == endpoint.Id.ToString());
        Assert.True(ladder.TryDescend(LadderProjection.DescentFor(processRow, ladder.Current, workspace.Extent), out _));
        LadderRow channelRow = Assert.Single(LadderProjection.Project(workspace, ladder.Current).Rows);
        Assert.Equal(channel.Key, channelRow.Key);
        Assert.Equal(channel.ObservationCount, channelRow.ObservationCount);
        Assert.Contains("direction varies by observation", channelRow.Detail, StringComparison.Ordinal);
        Assert.Empty(workspace.Operations);
        Assert.Empty(workspace.Evidence);
        Assert.Equal((3L, 0L, 1L),
            (first.ObservationRows, first.RowsWithoutSessionTime, first.UnresolvedTcpRows));
        Assert.Equal(3, first.Nodes.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<ProcessNode>)first.Nodes).Clear());
        CommunicationEdge edge = Assert.Single(first.Edges);
        Assert.Equal(2, edge.ObservationCount);
        Assert.Null(edge.KnownBytes);
        Assert.Equal(RelationStrength.Correlated, edge.Strength);
        Assert.Equal((long?)10, first.Extent?.StartTicks);
        Assert.Equal((long?)21, first.Extent?.EndTicks);
        Assert.Equal(3, first.Timeline.Sum(bucket => bucket.ObservationCount));
        Assert.Equal(2, first.GraphEligibleTimeline.Sum(bucket => bucket.ObservationCount));
        Assert.Equal(edge.ObservationCount, first.GraphEligibleRows);
        Assert.All(first.Timeline, bucket =>
        {
            Assert.Equal(CoverageState.UnknownCoverage, bucket.Coverage);
            Assert.Null(bucket.KnownBytes);
        });
        Assert.Equal(first.Edges, again.Edges);
        Assert.Equal(first.Channels, again.Channels);
    }

    [Fact(DisplayName = "R21: an unresolved connection and a reading without session time are disclosed, not graphed")]
    public void OneSidedAndUntimedRowsStayVisibleAsLimitations()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 100, 100, 1)
                .Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = 1_000 },
            Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 7, 300, 2)
                .Between("127.0.0.1:50001", "127.0.0.1:9090"),
        ]);

        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        Assert.Equal(2, overview.ObservationRows);
        Assert.Equal(1, overview.RowsWithoutSessionTime);
        Assert.Equal(2, overview.UnresolvedTcpRows);
        Assert.Empty(overview.Edges);
        Assert.Empty(overview.Channels);
        Assert.Equal(0, overview.GraphEligibleRows);
        Assert.Equal(2, overview.Nodes.Count);
        Assert.Single(overview.Timeline);
        Assert.Equal(1, overview.Timeline[0].ObservationCount);
        Assert.Contains(overview.Caveats, caveat => caveat.Contains("1 row has no usable session time", StringComparison.Ordinal));
        Assert.Contains(overview.Caveats, caveat => caveat.Contains("not rendered as guessed edges", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "R22: candidate relations enter the overview only under an explicit candidate policy")]
    public void CandidateRelationNeedsOptIn()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(10, ObservationKind.Create, 200, 1),
            Lifecycle(20, ObservationKind.Exit, 200, 2),
            Lifecycle(30, ObservationKind.Create, 200, 3),
            Transfer(40, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 4)
                .Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = 4_000 },
            Transfer(41, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 5)
                .Between(ServerEnd, ClientEnd) with { SessionRelativeTicks = 4_100 },
        ]);

        SessionOverviewBundle conservative = SessionOverviewProjector.Project(session.Store);
        SessionOverviewBundle admitted = SessionOverviewProjector.Project(
            session.Store, EvidencePolicy.IncludeCandidates);
        Assert.Empty(conservative.Edges);
        Assert.Empty(conservative.Channels);
        Assert.Equal(0, conservative.GraphEligibleTimeline.Sum(bucket => bucket.ObservationCount));
        Assert.Equal(1, conservative.RelationshipsNotAdmitted);
        Assert.Equal(RelationStrength.Candidate, Assert.Single(admitted.Edges).Strength);
        Assert.Single(admitted.Channels);
        Assert.Equal(2, admitted.GraphEligibleTimeline.Sum(bucket => bucket.ObservationCount));
        Assert.NotEqual(conservative.GraphIdentity, admitted.GraphIdentity);
    }

    [Fact(DisplayName = "R21: an untimed relation remains a graph edge without an invented timeline extent")]
    public void UntimedRelationHasNoInventedTimeline()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1).Between(ClientEnd, ServerEnd),
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 2).Between(ServerEnd, ClientEnd),
        ]);

        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        Assert.Equal(2, Assert.Single(overview.Edges).ObservationCount);
        Assert.Equal((2L, 2L), (overview.GraphEligibleRows, overview.GraphRowsWithoutSessionTime));
        Assert.Null(overview.Extent);
        Assert.Empty(overview.Timeline);
        Assert.Empty(overview.GraphEligibleTimeline);
    }

    [Theory(DisplayName = "R21: a leased overview applies imported coverage only to observed mechanism buckets")]
    [InlineData(0L, CoverageState.Covered)]
    [InlineData(1L, CoverageState.PartialGap)]
    public void ImportedCoverageStaysBoundedToObservedReadings(long lost, CoverageState expected)
    {
        using var session = new TemporarySession();
        CoverageLedgerV1 ledger = TcpLedger(10, 20, lost);
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1)
                .Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = 1_000 },
            Transfer(20, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 2)
                .Between(ServerEnd, ClientEnd) with { SessionRelativeTicks = 2_000 },
        ], coverage: ledger);

        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        Assert.True(overview.CoverageLedgerPublished);
        Assert.Equal(expected, overview.Timeline[0].Coverage);
        Assert.Equal(expected, overview.Timeline[^1].Coverage);
        Assert.Equal(expected, overview.GraphEligibleTimeline[0].Coverage);
        Assert.Equal(expected, overview.GraphEligibleTimeline[^1].Coverage);
        Assert.All(overview.Timeline.Where(bucket => bucket.ObservationCount == 0),
            bucket => Assert.Equal(CoverageState.UnknownCoverage, bucket.Coverage));
        Assert.Equal(CoverageState.UnknownCoverage,
            Assert.Single(overview.MechanismCoverage, item => item.Mechanism == Mechanism.Rpc).State);
        Assert.Contains(overview.Caveats, caveat => caveat.Contains("Empty buckets stay unknown", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "I8: negative session ticks map to native coverage without rounding a bucket across zero")]
    public void NegativeSessionTimeCoverageUsesExactBoundaries()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(-1, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1)
                .Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = -100 },
            Transfer(0, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 2)
                .Between(ServerEnd, ClientEnd) with { SessionRelativeTicks = 0 },
        ], coverage: TcpLedger(-1, 0, 0));

        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        Assert.Equal((-1L, 1L), (overview.Extent!.Value.StartTicks, overview.Extent.Value.EndTicks));
        Assert.Equal([CoverageState.Covered, CoverageState.Covered], overview.Timeline.Select(bucket => bucket.Coverage));
    }

    private static CoverageLedgerV1 TcpLedger(long first, long last, long lost) => new()
    {
        Contract = CoverageLedgerV1.ContractName,
        Epochs = [new CoverageEpochV1
        {
            Epoch = 1,
            Acquisition = CoverageAcquisition.EtlImport,
            FirstDeliveredNativeTicks = first,
            LastDeliveredNativeTicks = last,
            Collected =
            [
                new CoverageCollectedV1 { ProviderId = NetworkProvider, ProviderName = "network", EventId = 10, Version = 0, Mechanism = Mechanism.Tcp },
                new CoverageCollectedV1 { ProviderId = ProcessProvider, ProviderName = "process", EventId = 99, Version = 0, Mechanism = Mechanism.Rpc },
            ],
            Deliveries = [new CoverageDeliveryV1
            {
                ProviderId = NetworkProvider,
                EventId = 10,
                Version = 0,
                Delivered = 2,
                Admitted = 2,
                Omitted = 0,
            }],
            Losses = [new CoverageLossV1 { Layer = LossLayer.SourceSession, Lost = lost }],
        }],
    };
}
