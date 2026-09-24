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

        SessionChannelPage first = SessionChannelQuery.Read(session.Store, pageSize: 1);
        SessionChannelPage second = SessionChannelQuery.Read(session.Store, pageSize: 1, cursor: first.NextCursor);
        Assert.Equal(overview.SessionId, first.SessionId);
        Assert.Equal(2, first.TotalChannels);
        Assert.Single(first.Channels);
        Assert.Single(second.Channels);
        Assert.Null(second.NextCursor);
        Assert.Equal(2, new[] { first.Channels[0].Key, second.Channels[0].Key }.Distinct().Count());
        Assert.Equal(4, first.Channels[0].ObservationCount + second.Channels[0].ObservationCount);
        ProcessInstanceId process = overview.Edges[0].SourceId;
        SessionChannelPage focused = SessionChannelQuery.Read(session.Store, processScope: process);
        Assert.Single(focused.Channels);
        Assert.Equal(1, focused.TotalChannels);
        Assert.True(SessionChannelQuery.Read(session.Store, processScope: process,
            cursor: first.NextCursor).RestartRequired);
        Publish(session.Store,
        [Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 1, 500, 5)
            .Between("127.0.0.1:50002", "127.0.0.1:7070")]);
        SessionChannelPage newer = SessionChannelQuery.Read(session.Store, cursor: first.NextCursor);
        Assert.True(newer.RestartRequired);
        Assert.Empty(newer.Channels);
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

    [Fact]
    public void EvidencePagesKeepEveryExactRowAndSourceIdentityAcrossSegments()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1).Between(ClientEnd, ServerEnd),
            Transfer(20, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 2).Between(ServerEnd, ClientEnd),
            Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 3, 300, 3)
                .Between("127.0.0.1:50001", "127.0.0.1:9090"),
        ], rowsPerSegment: 1);

        SessionEvidencePage first = SessionEvidenceQuery.Read(session.Store, pageSize: 1);
        SessionEvidencePage second = SessionEvidenceQuery.Read(session.Store, pageSize: 1, cursor: first.NextCursor);
        SessionEvidencePage third = SessionEvidenceQuery.Read(session.Store, pageSize: 1, cursor: second.NextCursor);

        Assert.False(first.RestartRequired);
        Assert.Equal(first.QueryIdentity, second.QueryIdentity);
        Assert.Equal(first.QueryIdentity, third.QueryIdentity);
        Assert.NotNull(first.NextCursor);
        Assert.NotNull(second.NextCursor);
        Assert.Null(third.NextCursor);
        SessionEvidenceRecord[] records = [first.Records.Single(), second.Records.Single(), third.Records.Single()];
        Assert.Equal(3, records.Select(record => record.ObservationId).Distinct().Count());
        Assert.Equal([10L, 20L, 30L], records.Select(record => record.Observation.NativeTicks));
        Assert.All(records, record =>
        {
            Assert.Equal(record.Observation.ProviderId, NetworkProvider);
            Assert.NotEqual(default, record.ObservationId.RawRecordId);
            Assert.Equal(1u, record.ObservationId.NormalizerContractVersion.Value);
        });
        Assert.Contains("not a replay of original payload", first.Caveat, StringComparison.Ordinal);
    }

    [Fact]
    public void ChannelEvidencePagesUseTheSameAdmittedIncarnationAboveOverviewCap()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1).Between(ClientEnd, ServerEnd),
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 2).Between(ServerEnd, ClientEnd),
            Transfer(12, ObservationKind.Send, AccountingSide.SendSide, 9, 300, 3)
                .Between("127.0.0.1:50001", "127.0.0.1:9090"),
            Transfer(13, ObservationKind.Receive, AccountingSide.ReceiveSide, 9, 400, 4)
                .Between("127.0.0.1:9090", "127.0.0.1:50001"),
        ]);

        SessionOverviewBundle complete = SessionOverviewProjector.Project(session.Store);
        SessionOverviewBundle capped = SessionOverviewProjector.Project(session.Store, maximumChannels: 1);
        Assert.Empty(capped.Channels);
        Assert.Equal(complete.Channels.Select(channel => channel.Key),
            SessionChannelQuery.Read(session.Store).Channels.Select(channel => channel.Key));
        string key = complete.Channels.Single(channel => channel.Name.Contains(ClientEnd, StringComparison.Ordinal)).Key;
        SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store, channelKey: key);
        Assert.Equal(2, page.Records.Count);
        Assert.Equal([10L, 11L], page.Records.Select(record => record.Observation.NativeTicks));
        Assert.Null(page.NextCursor);
        Assert.Throws<InvalidOperationException>(() => SessionEvidenceQuery.Read(session.Store, channelKey: "tcp:missing"));
    }

    [Fact]
    public void EvidenceCursorRejectsAnotherQueryAndMalformedCoordinates()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1).Between(ClientEnd, ServerEnd),
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 2).Between(ServerEnd, ClientEnd),
        ]);
        SessionEvidencePage first = SessionEvidenceQuery.Read(session.Store, pageSize: 1);
        SessionEvidencePage changed = SessionEvidenceQuery.Read(session.Store,
            policy: EvidencePolicy.IncludeCandidates, cursor: first.NextCursor);
        Assert.True(changed.RestartRequired);
        Assert.Empty(changed.Records);
        Assert.Null(changed.NextCursor);
        Assert.Contains("Restart", changed.RestartReason, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => SessionEvidenceQuery.Read(session.Store, cursor: "bad"));
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionEvidenceQuery.Read(session.Store, pageSize: 201));
    }

    [Fact]
    public void EvidenceCursorDoesNotShiftOntoAReplacementGeneration()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1).Between(ClientEnd, ServerEnd),
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 2).Between(ServerEnd, ClientEnd),
        ]);
        SessionEvidencePage first = SessionEvidenceQuery.Read(session.Store, pageSize: 1);
        Assert.NotNull(first.NextCursor);

        Publish(session.Store,
        [
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 4, 100, 3).Between(ClientEnd, ServerEnd),
        ]);
        SessionEvidencePage changed = SessionEvidenceQuery.Read(session.Store, cursor: first.NextCursor);
        Assert.True(changed.RestartRequired);
        Assert.Empty(changed.Records);
        Assert.True(changed.Generation > first.Generation);
    }

    [Fact]
    public void EvidenceTimeScopeIsHalfOpenAndPartOfTheCursorIdentity()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1)
                .Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = 1_000 },
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 2)
                .Between(ServerEnd, ClientEnd) with { SessionRelativeTicks = 1_100 },
            Transfer(12, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 3)
                .Between(ClientEnd, ServerEnd),
        ]);

        var wide = new TimeRange(10, 12);
        SessionEvidencePage first = SessionEvidenceQuery.Read(session.Store, interval: wide, pageSize: 1);
        SessionEvidencePage second = SessionEvidenceQuery.Read(session.Store,
            interval: wide, pageSize: 1, cursor: first.NextCursor);
        Assert.Equal(10L, first.Records.Single().Observation.SessionRelativeTicks!.Value / 100);
        Assert.Equal(11L, second.Records.Single().Observation.SessionRelativeTicks!.Value / 100);
        Assert.Null(second.NextCursor);
        Assert.Equal(wide, first.Interval);
        SessionEvidencePage changed = SessionEvidenceQuery.Read(session.Store,
            interval: new TimeRange(10, 11), cursor: first.NextCursor);
        Assert.True(changed.RestartRequired);
        Assert.Empty(changed.Records);
        Assert.Single(SessionEvidenceQuery.Read(session.Store, interval: new TimeRange(10, 11)).Records);
    }

    [Fact]
    public void OwnerScopedEvidenceKeepsOnlyCanonicalOwnerAndBindsCursorToScope()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1).Between(ClientEnd, ServerEnd),
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 2).Between(ServerEnd, ClientEnd),
            Transfer(12, ObservationKind.Send, AccountingSide.SendSide, 3, 100, 3)
                .Between("127.0.0.1:50001", "127.0.0.1:9090"),
        ], rowsPerSegment: 1);

        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        ProcessInstanceId client = overview.Nodes.Single(node => node.ProcessId == 100).Id;
        ProcessInstanceId server = overview.Nodes.Single(node => node.ProcessId == 200).Id;
        SessionEvidencePage first = SessionEvidenceQuery.Read(session.Store,
            ownerProcessScope: client, pageSize: 1);
        SessionEvidencePage second = SessionEvidenceQuery.Read(session.Store,
            ownerProcessScope: client, pageSize: 1, cursor: first.NextCursor);
        Assert.Equal(client, first.OwnerProcessScope);
        Assert.Equal([10L, 12L], new[] { first, second }
            .Select(page => page.Records.Single().Observation.NativeTicks));
        Assert.Null(second.NextCursor);
        Assert.Contains("not possible peer rows", first.Caveat, StringComparison.Ordinal);

        SessionEvidencePage changed = SessionEvidenceQuery.Read(session.Store,
            ownerProcessScope: server, cursor: first.NextCursor);
        Assert.True(changed.RestartRequired);
        Assert.Empty(changed.Records);
        Assert.Equal(11L, SessionEvidenceQuery.Read(session.Store,
            ownerProcessScope: server).Records.Single().Observation.NativeTicks);
        Assert.Empty(SessionEvidenceQuery.Read(session.Store,
            ownerProcessScope: client, policy: EvidencePolicy.DirectOnly).Records);
        string channel = Assert.Single(overview.Channels).Key;
        Assert.Equal(10L, SessionEvidenceQuery.Read(session.Store,
            channelKey: channel, ownerProcessScope: client).Records.Single().Observation.NativeTicks);
        Assert.Throws<InvalidOperationException>(() => SessionEvidenceQuery.Read(session.Store,
            ownerProcessScope: new ProcessInstanceId(Guid.NewGuid())));
    }

    [Fact]
    public void OriginalRecordLookupIsGenerationBoundAndRevealsOnlyAnExplicitBoundedPreview()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1)
            .Between(ClientEnd, ServerEnd)],
            bodyForRow: _ => new BodyV1
            {
                Classification = BodyClassificationV1.ApprovedMetadata,
                Disposition = BodyDispositionV1.Retained,
                OriginalLength = 5,
                Bytes = EnvelopeBuffer.CopyOf([1, 2, 3, 4, 5]),
            });
        SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store);
        SessionEvidenceRecord selected = Assert.Single(page.Records);

        SessionRawRecordDetail hidden = SessionRawRecordQuery.Read(session.Store,
            page.SessionId, page.Generation, selected);
        Assert.True(hidden.Available);
        Assert.Null(hidden.BodyPreview);
        Assert.Equal(selected.ObservationId, hidden.ObservationId);
        Assert.Equal(selected.Observation.ProviderId, hidden.Header!.Value.ProviderId);
        Assert.Equal(selected.Observation.SchemaFingerprint, hidden.SchemaFingerprint);
        Assert.Equal("metadata-only-admitted-projection-v1", hidden.AdmissionPolicyId);
        Assert.Equal(BodyDispositionV1.Retained, hidden.BodyDisposition);
        Assert.Equal(5, hidden.OriginalBodyLength);
        Assert.Equal(5, hidden.RetainedBodyLength);

        SessionRawRecordDetail revealed = SessionRawRecordQuery.Read(session.Store,
            page.SessionId, page.Generation, selected, revealBodyBytes: true, maximumPreviewBytes: 2);
        Assert.Equal([1, 2], revealed.BodyPreview);
        Assert.True(revealed.BodyPreviewTruncated);
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionRawRecordQuery.Read(session.Store,
            page.SessionId, page.Generation, selected, maximumPreviewBytes: 1025));
        Assert.Throws<InvalidDataException>(() => SessionRawRecordQuery.Read(session.Store,
            page.SessionId, page.Generation, selected with { SegmentRow = 99 }));

        Publish(session.Store,
        [Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 1, 100, 2)
            .Between(ClientEnd, ServerEnd)]);
        Assert.Throws<InvalidOperationException>(() => SessionRawRecordQuery.Read(session.Store,
            page.SessionId, page.Generation, selected));
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
