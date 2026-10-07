using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

public sealed class SessionTimelineTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [Fact(DisplayName = "§3.2: mechanism lanes partition the overview and zoomed timeline without another read")]
    public void MechanismLanesPartitionAndUseTheirOwnCoverage()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Timed(Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1)
                .Between(ClientEnd, ServerEnd)),
            Timed(Lifecycle(2_000, ObservationKind.Create, 100, 2)),
            Timed(Transfer(8_000, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 3)
                .Between(ClientEnd, ServerEnd)),
            Transfer(10_001, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 4)
                .Between(ClientEnd, ServerEnd),
        ], coverage: TwoEpochs());

        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        SessionTimelineDetail sameColumns = SessionTimelineQuery.Detail(session.Store, overview.Extent!.Value,
            SessionOverviewProjector.MaximumTimelineBuckets);
        Assert.Equal(overview.MechanismLanes.SelectMany(lane => lane.Buckets.Select(bucket => (lane.Mechanism, bucket))),
            sameColumns.MechanismLanes.SelectMany(lane => lane.Buckets.Select(bucket => (lane.Mechanism, bucket))));
        Assert.Same(overview.MechanismLanes, OverviewWorkspace.From(overview).MechanismLanes);
        Assert.Equal([Mechanism.ProcessLifecycle, Mechanism.Tcp], overview.MechanismLanes.Select(lane => lane.Mechanism));
        Assert.All(overview.Timeline.Select((bucket, index) => (bucket, index)), pair =>
            Assert.Equal(pair.bucket.ObservationCount,
                overview.MechanismLanes.Sum(lane => lane.Buckets[pair.index].ObservationCount)));
        Assert.Equal(3, overview.MechanismLanes.Sum(lane => lane.Buckets.Sum(bucket => bucket.ObservationCount)));

        SessionTimelineDetail zoomed = SessionTimelineQuery.Detail(session.Store, new TimeRange(0, 10_000), 10);
        Assert.All(zoomed.Buckets.Select((bucket, index) => (bucket, index)), pair =>
            Assert.Equal(pair.bucket.ObservationCount,
                zoomed.MechanismLanes.Sum(lane => lane.Buckets[pair.index].ObservationCount)));
        MechanismTimelineLane tcp = zoomed.MechanismLanes.Single(lane => lane.Mechanism == Mechanism.Tcp);
        MechanismTimelineLane lifecycle = zoomed.MechanismLanes.Single(lane => lane.Mechanism == Mechanism.ProcessLifecycle);
        int quiet = Enumerable.Range(0, tcp.Buckets.Count).Single(index => tcp.Buckets[index].Interval.Contains(3_000));
        Assert.Equal(0, tcp.Buckets[quiet].ObservationCount);
        Assert.Equal(CoverageState.Covered, tcp.Buckets[quiet].Coverage);
        Assert.NotEqual(CoverageState.Covered, lifecycle.Buckets[quiet].Coverage);
    }

    [Fact(DisplayName = "§6.2: a zoom keeps a lane for each mechanism a view draws, empty where none of its records falls in view and judged by that mechanism's coverage")]
    public void AZoomKeepsTheLanesAViewDraws()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Timed(Lifecycle(2_000, ObservationKind.Create, 100, 2)),
            Timed(Transfer(6_000, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 3).Between(ClientEnd, ServerEnd)),
            Timed(Transfer(8_000, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 4).Between(ClientEnd, ServerEnd)),
        ], coverage: TwoEpochs());

        // Past its first half the session holds TCP records and no lifecycle record: counted alone, a zoom there has a TCP
        // lane and no other, so a view drawing both lanes could draw neither from it.
        var later = new TimeRange(5_000, 10_000);
        SessionTimelineDetail alone = SessionTimelineQuery.Detail(session.Store, later, 5);
        Assert.Equal([Mechanism.Tcp], alone.MechanismLanes.Select(lane => lane.Mechanism));

        // Asked for the lanes a view draws, it keeps the lifecycle lane, empty in each of the zoom's columns, which the
        // capture, collecting TCP alone, never covered for it; its TCP lane is the one counted alone.
        SessionTimelineDetail drawn = SessionTimelineQuery.Detail(session.Store, later, 5,
            [Mechanism.ProcessLifecycle, Mechanism.Tcp]);
        Assert.Equal([Mechanism.ProcessLifecycle, Mechanism.Tcp], drawn.MechanismLanes.Select(lane => lane.Mechanism));
        MechanismTimelineLane lifecycle = drawn.MechanismLanes[0];
        Assert.Equal(drawn.Buckets.Select(bucket => bucket.Interval), lifecycle.Buckets.Select(bucket => bucket.Interval));
        Assert.All(lifecycle.Buckets, bucket =>
        {
            Assert.Equal(0, bucket.ObservationCount);
            Assert.NotEqual(CoverageState.Covered, bucket.Coverage);
        });
        Assert.Equal(alone.MechanismLanes[0].Buckets, drawn.MechanismLanes[1].Buckets);
        Assert.Contains(drawn.MechanismLanes[1].Buckets, bucket => bucket is { ObservationCount: 0, Coverage: CoverageState.PartialGap });
        Assert.Equal(alone.Buckets, drawn.Buckets);
    }

    [Fact(DisplayName = "R21: a viewport timeline counts what a scan counts, and at the overview's resolution it is the overview")]
    public void AViewportTimelineCountsWhatAScanCounts()
    {
        using var session = new TemporarySession();
        long[] ticks = [.. Enumerable.Range(0, 240).Select(index => (long)((index * 7_919) % 10_000)).Distinct().Order()];
        ObservationRowV1[] rows =
        [
            .. ticks.Select((tick, index) => Timed(
                Transfer(tick, ObservationKind.Send, AccountingSide.SendSide, 8, 100, (ulong)(index + 1)).Between(ClientEnd, ServerEnd))),

            // A record without a usable session time has no column in any interval.
            Transfer(10_001, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 9_999).Between(ClientEnd, ServerEnd),
        ];
        Publish(session.Store, rows);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        TimeRange extent = overview.Extent!.Value;

        SessionTimelineDetail whole = SessionTimelineQuery.Detail(session.Store, extent, SessionOverviewProjector.MaximumTimelineBuckets);
        Assert.Equal(overview.Timeline, whole.Buckets);
        Assert.Equal((overview.SessionId, overview.Generation), (whole.SessionId, whole.Generation));

        var interval = new TimeRange(2_500, 7_500);
        SessionTimelineDetail zoomed = SessionTimelineQuery.Detail(session.Store, interval, 10);
        Assert.Equal(10, zoomed.Buckets.Count);
        Assert.Equal(interval.StartTicks, zoomed.Buckets[0].Interval.StartTicks);
        Assert.Equal(interval.EndTicks, zoomed.Buckets[^1].Interval.EndTicks);
        Assert.All(zoomed.Buckets.Zip(zoomed.Buckets.Skip(1)), pair =>
            Assert.Equal(pair.First.Interval.EndTicks, pair.Second.Interval.StartTicks));
        Assert.All(zoomed.Buckets, bucket => Assert.Equal(
            ticks.Count(tick => bucket.Interval.Contains(tick)), bucket.ObservationCount));
        Assert.Equal(ticks.Count(interval.Contains), zoomed.Buckets.Sum(bucket => bucket.ObservationCount));

        // A span narrower than the columns asked for gets one column per tick, never an empty sliver.
        Assert.Equal(5, SessionTimelineQuery.Detail(session.Store, new TimeRange(100, 105), 50).Buckets.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionTimelineQuery.Detail(session.Store, interval, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SessionTimelineQuery.Detail(session.Store, interval, SessionTimelineQuery.MaximumColumns + 1));
    }

    [Fact(DisplayName = "§3.2: a focused timeline counts exactly the records its rung's evidence scope reads")]
    public void AFocusedTimelineCountsWhatItsEvidenceReads()
    {
        const int exchanges = 40;
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Timed(Lifecycle(1, ObservationKind.Create, 100, 1)),
            Timed(Lifecycle(2, ObservationKind.Create, 200, 2)),
            .. Enumerable.Range(0, exchanges).SelectMany(index => new[]
            {
                Timed(Transfer(10 + (50 * index), ObservationKind.Send, AccountingSide.SendSide, 64, 100,
                    (ulong)(100 + (2 * index))).Between(ClientEnd, ServerEnd)),
                Timed(Transfer(11 + (50 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200,
                    (ulong)(101 + (2 * index))).Between(ServerEnd, ClientEnd)),
            }),

            // A one-sided flow elsewhere: a record of the whole timeline that neither focus reads.
            Timed(Transfer(1_000, ObservationKind.Send, AccountingSide.SendSide, 3, 300, 8_000)
                .Between("127.0.0.1:50001", "127.0.0.1:9090")),
        ]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        TimeRange extent = overview.Extent!.Value;
        ProcessNode client = overview.Nodes.Single(node => node.ProcessId == 100);
        ProcessNode server = overview.Nodes.Single(node => node.ProcessId == 200);
        Channel channel = overview.Channels.Single();
        SessionTimelineDetail whole = SessionTimelineQuery.Detail(session.Store, extent, 16);

        // The client owns its creation and its sends; the channel holds both ends' transfers.
        foreach ((TimelineFocus focus, int expected) in new[]
        {
            (new TimelineFocus(null, [client.Id]), exchanges + 1),
            (new TimelineFocus(channel.Key, []), 2 * exchanges),
            (new TimelineFocus(null, [client.Id, server.Id]), 2 * exchanges + 2),
        })
        {
            SessionFocusedTimeline timeline = SessionTimelineQuery.Focused(session.Store, extent, 16, focus);

            // The focus leaves the whole timeline as it is, on the same columns, and never exceeds it.
            Assert.Equal(whole.Buckets, timeline.Whole.Buckets);
            Assert.Equal(whole.Buckets.Select(bucket => bucket.Interval), timeline.Focus.Select(bucket => bucket.Interval));
            Assert.All(whole.Buckets.Zip(timeline.Focus), pair =>
                Assert.InRange(pair.Second.ObservationCount, 0, pair.First.ObservationCount));

            // Each focus bucket holds exactly the records E lists for the scope inside that bucket.
            SessionEvidencePage read = SessionEvidenceQuery.ReadScope(session.Store, 10_000, focus.ChannelKey,
                ownerProcesses: focus.OwnerProcesses.Count == 0 ? null : focus.OwnerProcesses);
            long[] ticks = [.. read.Records.Select(record => record.Observation.SessionRelativeTicks!.Value / 100)];
            Assert.Equal(expected, ticks.Length);
            Assert.All(timeline.Focus, bucket => Assert.Equal(ticks.Count(bucket.Interval.Contains), bucket.ObservationCount));
            Assert.Equal(expected, timeline.Focus.Sum(bucket => bucket.ObservationCount));
            if (focus.OwnerProcesses.Count > 1)
            {
                Assert.Null(timeline.ProcessLaneProblem);
                Assert.Equal(focus.OwnerProcesses, timeline.ProcessLanes.Select(lane => lane.ProcessId));
                Assert.All(timeline.Focus.Select((bucket, index) => (bucket, index)), pair =>
                    Assert.Equal(pair.bucket.ObservationCount,
                        timeline.ProcessLanes.Sum(lane => lane.Buckets[pair.index].ObservationCount)));
                foreach (ProcessTimelineLane lane in timeline.ProcessLanes)
                {
                    SessionEvidencePage ownerRecords = SessionEvidenceQuery.ReadScope(session.Store, 10_000,
                        ownerProcesses: [lane.ProcessId]);
                    long[] ownerTicks = [.. ownerRecords.Records.Select(record =>
                        record.Observation.SessionRelativeTicks!.Value / 100)];
                    Assert.All(lane.Buckets, bucket =>
                        Assert.Equal(ownerTicks.Count(bucket.Interval.Contains), bucket.ObservationCount));
                }
            }
            else
            {
                Assert.Empty(timeline.ProcessLanes);
            }
        }

        // A focus this generation cannot resolve is refused with a reason, never counted as nothing.
        Assert.Throws<InvalidOperationException>(() => SessionTimelineQuery.Focused(session.Store, extent, 16,
            new TimelineFocus(null, [new ProcessInstanceId(Guid.Parse("0b1c2d3e-4f50-4617-8293-a4b5c6d7e8f9"))])));
        Assert.Throws<InvalidOperationException>(() => SessionTimelineQuery.Focused(session.Store, extent, 16,
            new TimelineFocus("tcp:no-such-channel", [])));
        Assert.Throws<ArgumentException>(() => new TimelineFocus(null, []));
        Assert.Equal(new TimelineFocus(null, [client.Id, client.Id]).Key, new TimelineFocus(null, [client.Id]).Key);
    }

    [Fact(DisplayName = "§3.2: a channel's two ends partition its records, each banded by its own source directions")]
    public void ChannelEndLanesPartitionTheChannelByEnd()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Timed(Lifecycle(1, ObservationKind.Create, 100, 1)),
            Timed(Lifecycle(2, ObservationKind.Create, 200, 2)),
            Timed(Lifecycle(3, ObservationKind.Create, 300, 3)),

            // The client sends for twenty exchanges, then receives for ten; then it disconnects, which has no direction.
            .. Enumerable.Range(0, 30).SelectMany(index =>
            {
                (int sender, int receiver, string from, string to) = index < 20
                    ? (100, 200, ClientEnd, ServerEnd)
                    : (200, 100, ServerEnd, ClientEnd);
                return new[]
                {
                    Timed(Transfer(10 + (20 * index), ObservationKind.Send, AccountingSide.SendSide, 64, sender,
                        (ulong)(100 + (2 * index))).Between(from, to)),
                    Timed(Transfer(11 + (20 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 64, receiver,
                        (ulong)(101 + (2 * index))).Between(to, from)),
                };
            }),
            Timed(Transfer(700, ObservationKind.Disconnect, AccountingSide.CanonicalOwner, null, 100, 900)
                .Between(ClientEnd, ServerEnd) with { Direction = Direction.DirectionNotApplicable }),

            // A process connected to itself over loopback, and a one-sided flow that no channel focus reads.
            Timed(Transfer(750, ObservationKind.Send, AccountingSide.SendSide, 8, 300, 901)
                .Between("127.0.0.1:50002", "127.0.0.1:9091")),
            Timed(Transfer(751, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 300, 902)
                .Between("127.0.0.1:9091", "127.0.0.1:50002")),
            Timed(Transfer(800, ObservationKind.Send, AccountingSide.SendSide, 3, 400, 903)
                .Between("127.0.0.1:50001", "127.0.0.1:9090")),
        ], coverage: TwoEpochs());
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        TimeRange extent = overview.Extent!.Value;
        ProcessNode client = overview.Nodes.Single(node => node.ProcessId == 100);
        ProcessNode server = overview.Nodes.Single(node => node.ProcessId == 200);
        ProcessNode looped = overview.Nodes.Single(node => node.ProcessId == 300);
        CommunicationEdge selfEdge = overview.Edges.Single(edge => edge.SourceId == edge.TargetId);
        Channel channel = overview.Channels.Single(candidate => candidate.EdgeKey != selfEdge.Key);
        SessionFocusedTimeline timeline = SessionTimelineQuery.Focused(session.Store, extent, 16,
            new TimelineFocus(channel.Key, []));

        // Two ends, first end first; each names its holder and its own endpoint, and together they are the channel.
        IReadOnlyList<ChannelEndTimelineLane> ends = timeline.ChannelEndLanes;
        Assert.Equal([0, 1], ends.Select(end => end.End));
        ChannelEndTimelineLane clientEnd = ends.Single(end => end.Holder == client.Id);
        ChannelEndTimelineLane serverEnd = ends.Single(end => end.Holder == server.Id);
        Assert.Equal(ClientEnd, clientEnd.Endpoint);
        Assert.Equal(ServerEnd, serverEnd.Endpoint);
        Assert.All(timeline.Focus.Select((bucket, index) => (bucket, index)), pair =>
            Assert.Equal(pair.bucket.ObservationCount, ends.Sum(end => end.Buckets[pair.index].ObservationCount)));
        Assert.Equal(61, timeline.Focus.Sum(bucket => bucket.ObservationCount));

        // Each end holds exactly the records E lists for the channel that its holder made.
        SessionEvidencePage read = SessionEvidenceQuery.ReadScope(session.Store, 10_000, channel.Key);
        foreach ((ChannelEndTimelineLane end, int pid) in new[] { (clientEnd, 100), (serverEnd, 200) })
        {
            long[] ticks = [.. read.Records.Where(record => record.Observation.OwnerProcessId == pid)
                .Select(record => record.Observation.SessionRelativeTicks!.Value / 100)];
            Assert.All(end.Buckets, bucket => Assert.Equal(ticks.Count(bucket.Interval.Contains), bucket.ObservationCount));
        }

        // The bands hold the end's own directions; the disconnect stays in its end's total and in neither band.
        Assert.Equal((31, 20, 10), Totals(clientEnd));
        Assert.Equal((30, 10, 20), Totals(serverEnd));
        Assert.All(ends, end => Assert.All(end.Buckets.Select((bucket, index) => (bucket, index)), pair =>
            Assert.InRange(end.Outbound[pair.index].ObservationCount + end.Inbound[pair.index].ObservationCount,
                0, pair.bucket.ObservationCount)));

        // An end can only hold the channel's mechanism, so a quiet interval the capture covered is observed-empty there,
        // and on the aggregate focus too, which judges its quiet interval by what the capture collected (revision 240).
        int quiet = Enumerable.Range(0, clientEnd.Buckets.Count).First(index => clientEnd.Buckets[index].ObservationCount == 0
            && timeline.Focus[index].ObservationCount == 0);
        Assert.Equal((CoverageState.Covered, Mechanism.Tcp),
            (clientEnd.Buckets[quiet].Coverage, clientEnd.Buckets[quiet].DominantMechanism));
        Assert.Equal(CoverageState.Covered, clientEnd.Inbound[quiet].Coverage);
        Assert.Equal(CoverageState.Covered, timeline.Focus[quiet].Coverage);

        // A process connected to itself still has two ends, told apart by endpoint rather than by holder.
        Channel loop = overview.Channels.Single(candidate => candidate.EdgeKey == selfEdge.Key);
        IReadOnlyList<ChannelEndTimelineLane> loopEnds = SessionTimelineQuery.Focused(session.Store, extent, 16,
            new TimelineFocus(loop.Key, [])).ChannelEndLanes;
        Assert.Equal(2, loopEnds.Count);
        Assert.All(loopEnds, end => Assert.Equal(looped.Id, end.Holder));
        Assert.Equal(["127.0.0.1:50002", "127.0.0.1:9091"], loopEnds.Select(end => end.Endpoint).Order(StringComparer.Ordinal));
        Assert.All(loopEnds, end => Assert.Equal(1, end.Buckets.Sum(bucket => bucket.ObservationCount)));

        // Owner focuses have no ends, and a whole timeline has no focus at all.
        Assert.Empty(SessionTimelineQuery.Focused(session.Store, extent, 16, new TimelineFocus(null, [client.Id])).ChannelEndLanes);

        static (int Total, int Outbound, int Inbound) Totals(ChannelEndTimelineLane end) => (
            end.Buckets.Sum(bucket => bucket.ObservationCount),
            end.Outbound.Sum(bucket => bucket.ObservationCount),
            end.Inbound.Sum(bucket => bucket.ObservationCount));
    }

    [Fact(DisplayName = "§3.2: one process's source-direction rows partition its exact focus without guessing unknown direction")]
    public void ProcessDirectionLanesPartitionOwnerEvidence()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Timed(Lifecycle(100, ObservationKind.Create, 100, 1)),
            Timed(Transfer(1_100, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 2)
                .Between(ClientEnd, ServerEnd)),
            Timed(Transfer(2_100, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 100, 3)
                .Between(ServerEnd, ClientEnd)),
            Timed(Transfer(3_100, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 4)
                .Between(ClientEnd, ServerEnd) with { Direction = Direction.UnknownDirection }),
            Timed(Transfer(4_100, ObservationKind.Send, AccountingSide.SendSide, 8, 200, 5)
                .Between(ClientEnd, ServerEnd)),
        ], coverage: TwoEpochs());
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        ProcessInstanceId owner = overview.Nodes.Single(node => node.ProcessId == 100).Id;
        TimeRange extent = overview.Extent!.Value;
        SessionFocusedTimeline result = SessionTimelineQuery.Focused(session.Store, extent, 5,
            new TimelineFocus(null, [owner]));

        Assert.Empty(result.ProcessLanes);
        Assert.Equal([Direction.Outbound, Direction.Inbound, Direction.Bidirectional,
                Direction.UnknownDirection, Direction.DirectionNotApplicable],
            result.DirectionLanes.Select(lane => lane.Direction));
        Assert.All(result.Focus.Select((bucket, index) => (bucket, index)), pair =>
            Assert.Equal(pair.bucket.ObservationCount,
                result.DirectionLanes.Sum(lane => lane.Buckets[pair.index].ObservationCount)));
        Assert.Equal(4, result.DirectionLanes.Sum(lane => lane.Buckets.Sum(bucket => bucket.ObservationCount)));
        Assert.Equal(1, result.DirectionLanes.Single(lane => lane.Direction == Direction.UnknownDirection)
            .Buckets.Sum(bucket => bucket.ObservationCount));

        // A row's quiet interval is judged by what the capture collected, as a process lane's is (R21): the rows split
        // one process's records, and it could have made any of them. The whole extent lies in the first, lossless epoch.
        Assert.All(result.DirectionLanes.SelectMany(lane => lane.Buckets)
            .Where(bucket => bucket.ObservationCount == 0),
            bucket => Assert.Equal(CoverageState.Covered, bucket.Coverage));

        // The process's own lane lists the focus's records, judged the same way: what `icat timeline --process` lists.
        Assert.Equal(result.Focus.Select(bucket => (bucket.Interval, bucket.ObservationCount, bucket.DominantMechanism)),
            result.OwnerLane.Select(bucket => (bucket.Interval, bucket.ObservationCount, bucket.DominantMechanism)));
        Assert.Equal(result.DirectionLanes[0].Buckets.Select(bucket => bucket.Coverage),
            result.OwnerLane.Select(bucket => bucket.Coverage));

        // Observed-empty where the capture covered what it collects, a gap where it lost records, and unknown past the
        // readings it delivered.
        SessionFocusedTimeline wider = SessionTimelineQuery.Focused(session.Store, new TimeRange(0, 12_000), 12,
            new TimelineFocus(null, [owner]));
        IReadOnlyList<TimelineBucket> inbound = wider.DirectionLanes.Single(lane => lane.Direction == Direction.Inbound).Buckets;
        Assert.Equal((1, CoverageState.Covered), (inbound[2].ObservationCount, inbound[2].Coverage));
        Assert.Equal((0, CoverageState.Covered), (inbound[3].ObservationCount, inbound[3].Coverage));
        Assert.Equal((0, CoverageState.PartialGap), (inbound[6].ObservationCount, inbound[6].Coverage));
        Assert.Equal((0, CoverageState.UnknownCoverage), (inbound[11].ObservationCount, inbound[11].Coverage));
        Assert.Equal((0, CoverageState.PartialGap), (wider.OwnerLane[6].ObservationCount, wider.OwnerLane[6].Coverage));
        Assert.Empty(SessionTimelineQuery.Focused(session.Store, extent, 5, new TimelineFocus(null,
            [owner, overview.Nodes.Single(node => node.ProcessId == 200).Id])).OwnerLane);
    }

    [Fact(DisplayName = "§6.2: a group's lanes past the cell budget are counted in fewer, wider columns, and still partition its records")]
    public void LanesPastTheCellBudgetAreCountedCoarser()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [.. Enumerable.Range(1, 64).SelectMany(index => new[]
        {
            Timed(Lifecycle(index * 10, ObservationKind.Create, 1_000 + index, (ulong)(index * 2))),
            Timed(Transfer((index * 10) + 5, ObservationKind.Send, AccountingSide.SendSide, 8, 1_000 + index,
                (ulong)((index * 2) + 1))),
        })]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        TimeRange extent = overview.Extent!.Value;
        TimelineFocus group = new(null, [.. overview.Nodes.Select(node => node.Id)]);

        // 64 lanes of 400 columns would be 25,600 cells: they are counted in the 312 columns 20,000 cells allow.
        SessionFocusedTimeline zoomed = SessionTimelineQuery.Focused(session.Store, extent, 400, group);
        Assert.Null(zoomed.ProcessLaneProblem);
        Assert.Equal(64, zoomed.ProcessLanes.Count);
        Assert.All(zoomed.ProcessLanes, lane => Assert.Equal(20_000 / 64, lane.Buckets.Count));
        Assert.Equal(400, zoomed.Focus.Count);
        Assert.Equal(64 * 2, zoomed.ProcessLanes.Sum(lane => lane.Buckets.Sum(bucket => bucket.ObservationCount)));

        // At that resolution the lanes partition exactly what the focus counts there, column by column.
        SessionFocusedTimeline coarse = SessionTimelineQuery.Focused(session.Store, extent, 20_000 / 64, group);
        Assert.All(coarse.Focus.Select((bucket, index) => (bucket, index)), pair =>
        {
            Assert.Equal(pair.bucket.Interval, zoomed.ProcessLanes[0].Buckets[pair.index].Interval);
            Assert.Equal(pair.bucket.ObservationCount,
                zoomed.ProcessLanes.Sum(lane => lane.Buckets[pair.index].ObservationCount));
        });
        Assert.All(zoomed.ProcessLanes.Zip(coarse.ProcessLanes), pair =>
            Assert.Equal(pair.First.Buckets.Select(bucket => (bucket.ObservationCount, bucket.Coverage)),
                pair.Second.Buckets.Select(bucket => (bucket.ObservationCount, bucket.Coverage))));
    }

    [Fact(DisplayName = "§6.2: past the lane bound, the lanes a view names are counted each on its own and the rest in one folded lane, exactly")]
    public void PastTheBoundTheRestFoldIntoOneLane()
    {
        // 250 processes, the one at index i sending i % 4 times after its creation.
        using var session = new TemporarySession();
        Publish(session.Store, [.. Enumerable.Range(1, 250).SelectMany(index => new[]
        {
            Timed(Lifecycle(index * 10, ObservationKind.Create, 1_000 + index, (ulong)(index * 10))),
        }.Concat(Enumerable.Range(1, index % 4).Select(step =>
            Timed(Transfer((index * 10) + step, ObservationKind.Send, AccountingSide.SendSide, 8, 1_000 + index,
                (ulong)((index * 10) + step))))))]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        TimeRange extent = overview.Extent!.Value;
        ProcessInstanceId[] owners = [.. overview.Nodes.Select(node => node.Id)];
        var focus = new TimelineFocus(null, owners);
        Dictionary<ProcessInstanceId, long> records = overview.Nodes.ToDictionary(node => node.Id, node => node.Records);

        // The view names 199 lanes, in its own order, and one stranger the group does not hold, which draws nothing.
        ProcessInstanceId[] named = [.. owners.OrderByDescending(owner => records[owner]).ThenBy(owner => owner.ToString(),
            StringComparer.Ordinal).Take(SessionTimelineQuery.MaximumProcessLanes - 1)];
        SessionFocusedTimeline folded = SessionTimelineQuery.Focused(session.Store, extent, 16, focus,
            lanes: [new ProcessInstanceId(Guid.NewGuid()), .. named]);
        Assert.Null(folded.ProcessLaneProblem);
        Assert.Equal(named, folded.ProcessLanes.Select(lane => lane.ProcessId));
        FoldedProcessLane rest = Assert.IsType<FoldedProcessLane>(folded.FoldedLane);
        Assert.Equal([.. focus.OwnerProcesses.Where(owner => !named.Contains(owner))], rest.Processes);
        Assert.Equal(51, rest.Processes.Count);

        // The lanes and the folded one partition the group's records, column by column, and the fold holds its members' own.
        Assert.All(folded.Focus.Select((bucket, column) => (bucket, column)), pair => Assert.Equal(pair.bucket.ObservationCount,
            folded.ProcessLanes.Sum(lane => lane.Buckets[pair.column].ObservationCount) + rest.Buckets[pair.column].ObservationCount));
        Assert.Equal(rest.Processes.Sum(owner => records[owner]), rest.Buckets.Sum(bucket => bucket.ObservationCount));
        Assert.Equal(folded.Focus.Select(bucket => bucket.Interval), rest.Buckets.Select(bucket => bucket.Interval));

        // A smaller group may fold too; a view that names as many lanes as the bound, with members left to fold, is refused.
        SessionFocusedTimeline few = SessionTimelineQuery.Focused(session.Store, extent, 16, new TimelineFocus(null, owners.Take(64).ToArray()),
            lanes: [.. owners.Take(10)]);
        Assert.Equal((10, 54), (few.ProcessLanes.Count, few.FoldedLane!.Processes.Count));
        Assert.Contains("at most 200", Assert.Throws<ArgumentException>(() => SessionTimelineQuery.Focused(session.Store, extent, 16,
            focus, lanes: [.. owners.Take(SessionTimelineQuery.MaximumProcessLanes)])).Message, StringComparison.Ordinal);

        // Naming every member folds none.
        SessionFocusedTimeline all = SessionTimelineQuery.Focused(session.Store, extent, 16, new TimelineFocus(null, owners.Take(64).ToArray()),
            lanes: [.. owners.Take(64)]);
        Assert.Equal(64, all.ProcessLanes.Count);
        Assert.Null(all.FoldedLane);
    }

    [Fact(DisplayName = "§6.2: process lane and cell caps report fallback without dropping focused records")]
    public void ProcessLaneBudgetFallsBackToAnExactAggregate()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [.. Enumerable.Range(1, 201).Select(index =>
            Timed(Lifecycle(index * 10, ObservationKind.Create, 1_000 + index, (ulong)index)))]);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        TimeRange extent = overview.Extent!.Value;
        ProcessInstanceId[] owners = [.. overview.Nodes.Select(node => node.Id)];
        Assert.Equal(201, owners.Length);

        SessionFocusedTimeline tooMany = SessionTimelineQuery.Focused(session.Store, extent, 2,
            new TimelineFocus(null, owners));
        Assert.Empty(tooMany.ProcessLanes);
        Assert.Contains("201 process lanes", tooMany.ProcessLaneProblem, StringComparison.Ordinal);
        Assert.Equal(201, tooMany.Focus.Sum(bucket => bucket.ObservationCount));

        // Past the cell budget the lanes are kept, counted coarser, rather than refused.
        TimelineFocus withinCount = new(null, owners.Take(64).ToArray());
        SessionFocusedTimeline tooManyCells = SessionTimelineQuery.Focused(session.Store, extent, 400, withinCount);
        Assert.Null(tooManyCells.ProcessLaneProblem);
        Assert.Equal(64, tooManyCells.ProcessLanes.Count);
        Assert.Equal(64, tooManyCells.Focus.Sum(bucket => bucket.ObservationCount));
        Assert.Equal(64, tooManyCells.ProcessLanes.Sum(lane => lane.Buckets.Sum(bucket => bucket.ObservationCount)));

        SessionFocusedTimeline bounded = SessionTimelineQuery.Focused(session.Store, extent, 300, withinCount);
        Assert.Null(bounded.ProcessLaneProblem);
        Assert.Equal(64, bounded.ProcessLanes.Count);
        Assert.All(bounded.Focus.Select((bucket, index) => (bucket, index)), pair =>
            Assert.Equal(pair.bucket.ObservationCount,
                bounded.ProcessLanes.Sum(lane => lane.Buckets[pair.index].ObservationCount)));
    }

    [Fact(DisplayName = "R21: a process lane's quiet interval is judged by what the capture collected, not left unknown")]
    public void AQuietProcessLaneIsJudgedByTheCapture()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Timed(Lifecycle(1, ObservationKind.Create, 100, 1)),
            Timed(Lifecycle(2, ObservationKind.Create, 200, 2)),
            .. Enumerable.Range(0, 10).Select(index => Timed(Transfer(100 + (index * 10), ObservationKind.Send,
                AccountingSide.SendSide, 8, 100, (ulong)(10 + index)).Between(ClientEnd, ServerEnd))),
            .. Enumerable.Range(0, 10).Select(index => Timed(Transfer(9_000 + (index * 10), ObservationKind.Send,
                AccountingSide.SendSide, 8, 200, (ulong)(30 + index)).Between(ServerEnd, ClientEnd))),
        ], coverage: TwoEpochs());
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        ProcessInstanceId first = overview.Nodes.Single(node => node.ProcessId == 100).Id;
        ProcessInstanceId second = overview.Nodes.Single(node => node.ProcessId == 200).Id;
        SessionFocusedTimeline timeline = SessionTimelineQuery.Focused(
            session.Store, new TimeRange(0, 12_000), 12, new TimelineFocus(null, [first, second]));
        IReadOnlyList<TimelineBucket> quiet = timeline.ProcessLanes.Single(lane => lane.ProcessId == first).Buckets;

        // The first process is quiet after its first second. That reads as observed-empty where the capture covered
        // what it collects, as a gap where the capture lost records, and as unknown past the readings it delivered.
        Assert.Equal((0, CoverageState.Covered), (quiet[3].ObservationCount, quiet[3].Coverage));
        Assert.Equal((0, CoverageState.PartialGap), (quiet[6].ObservationCount, quiet[6].Coverage));
        Assert.Equal((0, CoverageState.UnknownCoverage), (quiet[11].ObservationCount, quiet[11].Coverage));
        Assert.Equal(CoverageState.Covered, quiet[0].Coverage);

        // The whole timeline judges its quiet intervals the same way, since it could hold any record the capture collects;
        // left unknown, a quiet capture read as one long gap (revision 240).
        int[] quietColumns = [3, 6, 11];
        Assert.Equal(
            [(0, CoverageState.Covered), (0, CoverageState.PartialGap), (0, CoverageState.UnknownCoverage)],
            quietColumns.Select(index => (timeline.Whole.Buckets[index].ObservationCount, timeline.Whole.Buckets[index].Coverage)));
    }

    [Fact(DisplayName = "R21: the minimap counts every timed record and shows a capture gap even where nothing was observed")]
    public void TheMinimapShowsTheCapturesOwnCoverage()
    {
        using var session = new TemporarySession();
        long[] ticks = [.. Enumerable.Range(0, 30).Select(index => index * 100L), .. Enumerable.Range(60, 30).Select(index => index * 100L), 12_000];
        ObservationRowV1[] rows = [.. ticks.Select((tick, index) => Timed(
            Transfer(tick, ObservationKind.Send, AccountingSide.SendSide, 8, 100, (ulong)(index + 1)).Between(ClientEnd, ServerEnd)))];
        Publish(session.Store, rows, coverage: TwoEpochs());

        SessionMinimap minimap = SessionOverviewProjector.Project(session.Store).Minimap!;
        Assert.Equal(new TimeRange(0, 12_001), minimap.Extent);

        // The columns are whole multiples of the narrowest 1-2-5 width that spans the extent in at most 2,000 of them:
        // here 1,201 columns of 10 ticks, the last one clipped to the extent.
        Assert.Equal(new TimeRange(0, 12_010), minimap.ColumnSpan);
        Assert.Equal(1_201, minimap.Counts.Count);
        Assert.Equal(ticks.Length, minimap.Counts.Sum());
        Assert.Equal(0, minimap.IntervalOf(0).StartTicks);
        Assert.Equal(12_001, minimap.IntervalOf(minimap.Counts.Count - 1).EndTicks);

        // The first epoch delivered everything; the second reported loss, and so does every column it holds, including
        // the empty stretch the loss may explain. Past the delivered readings the capture's coverage is unknown.
        Assert.Equal(CoverageState.Covered, StateAt(minimap, 1_000));
        Assert.Equal(CoverageState.Covered, StateAt(minimap, 4_000));
        Assert.Equal(0, minimap.Counts[ColumnAt(minimap, 5_500)]);
        Assert.Equal(CoverageState.PartialGap, StateAt(minimap, 5_500));
        Assert.Equal(CoverageState.PartialGap, StateAt(minimap, 8_000));
        Assert.Equal(CoverageState.UnknownCoverage, StateAt(minimap, 11_000));

        using var legacy = new TemporarySession();
        Publish(legacy.Store, rows);
        Assert.All(SessionOverviewProjector.Project(legacy.Store).Minimap!.Capture,
            state => Assert.Equal(CoverageState.UnknownCoverage, state));
    }

    [Fact(DisplayName = "R21: a live capture's timeline is covered from its epoch to its stop, not only between its first and last record")]
    public void ALiveCaptureIsCoveredOverItsRecording()
    {
        // Records from 2,000 to 8,000 of a capture that recorded from its epoch, 0, to its stop at 10,000.
        ObservationRowV1[] rows =
        [
            Timed(Transfer(2_000, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1).Between(ClientEnd, ServerEnd)),
            Timed(Transfer(8_000, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 2).Between(ClientEnd, ServerEnd)),
        ];
        CoverageEpochV1 delivered = Epoch(1, 2_000, 8_000, lost: 0);

        // Bounded by what it delivered, it says nothing of its first two and last two columns.
        using var bounded = new TemporarySession();
        Publish(bounded.Store, rows, coverage: new CoverageLedgerV1 { Contract = CoverageLedgerV1.ContractName, Epochs = [delivered] });
        SessionTimelineDetail before = SessionTimelineQuery.Detail(bounded.Store, new TimeRange(0, 10_000), 10);
        Assert.Equal(
            [CoverageState.UnknownCoverage, CoverageState.UnknownCoverage],
            [before.Buckets[0].Coverage, before.Buckets[^1].Coverage]);

        // Stating what it recorded, it speaks for all of it: the quiet start and end were covered, and nothing happened.
        using var recorded = new TemporarySession();
        Publish(recorded.Store, rows, coverage: new CoverageLedgerV1
        {
            Contract = CoverageLedgerV1.ContractName,
            Epochs = [delivered with { RecordedFromNativeTicks = 0, RecordedToNativeTicks = 10_000 }],
        });
        SessionTimelineDetail after = SessionTimelineQuery.Detail(recorded.Store, new TimeRange(0, 10_000), 10);
        Assert.All(after.Buckets, bucket => Assert.Equal(CoverageState.Covered, bucket.Coverage));
    }

    [Fact(DisplayName = "I3: a fast clock's column wider than a tick count of its readings still says its coverage")]
    public void AFastClocksWideColumnSaysItsCoverage()
    {
        // A 3 GHz clock reads 300 times a presentation tick, so a column of some 130 years spans more of its readings than
        // a tick count holds. Its coverage is judged on the widest part a range holds, centred on the epoch as far as the
        // column reaches: here a capture that says it recorded every reading its clock can make, and lost nothing.
        var fast = new SourceClockDescriptor(TestSessions.Clock, HostId.Derive("fast-clock"), SourceClockKind.Monotonic,
            TimestampEncoding.Qpc, 3_000_000_000, 0, TimestampRounding.NearestEven, long.MaxValue);
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(6_000_000_000_000_000_000, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1).Between(ClientEnd, ServerEnd)
                with { SessionRelativeTicks = 2_000_000_000_000_000_000 },
        ], clock: fast, coverage: new CoverageLedgerV1
        {
            Contract = CoverageLedgerV1.ContractName,
            Epochs = [Epoch(1, 0, 9_999, lost: 0) with { RecordedFromNativeTicks = long.MinValue, RecordedToNativeTicks = long.MaxValue }],
        });

        // A column holding a record says its mechanism's coverage there, and so does the mechanism's lane.
        var holding = new TimeRange(-20_000_000_000_000_000, 25_000_000_000_000_000);
        SessionTimelineDetail detail = SessionTimelineQuery.Detail(session.Store, holding, 1);
        TimelineBucket column = Assert.Single(detail.Buckets);
        Assert.Equal((holding, 1, CoverageState.Covered), (column.Interval, column.ObservationCount, column.Coverage));
        Assert.Equal(CoverageState.Covered, Assert.Single(Assert.Single(detail.MechanismLanes).Buckets).Coverage);

        // An empty one says the capture's own.
        var quiet = new TimeRange(-30_000_000_000_000_000, 10_000_000_000_000_000);
        TimelineBucket empty = Assert.Single(SessionTimelineQuery.Detail(session.Store, quiet, 1).Buckets);
        Assert.Equal((0, CoverageState.Covered), (empty.ObservationCount, empty.Coverage));
    }

    private static int ColumnAt(SessionMinimap minimap, long tick) =>
        Enumerable.Range(0, minimap.Counts.Count).Single(column => minimap.IntervalOf(column).Contains(tick));

    private static CoverageState StateAt(SessionMinimap minimap, long tick) => minimap.Capture[ColumnAt(minimap, tick)];

    [Fact(DisplayName = "§12.1: a zoom opens only the segments its interval meets, and counts every row in it as a read of every segment does")]
    public void AZoomOpensOnlyTheSegmentsItMeets()
    {
        // Six segments of a hundred sends each, one every ten ticks: segment k holds ticks 1,000k to 1,000k + 990.
        using var session = new TemporarySession();
        ObservationRowV1[] rows =
        [
            Timed(Lifecycle(0, ObservationKind.Create, 100, 1)),
            .. Enumerable.Range(1, 599).Select(index => Timed(Transfer(index * 10L, ObservationKind.Send, AccountingSide.SendSide,
                8, 100, (ulong)(index + 1)).Between(ClientEnd, ServerEnd))),
        ];
        Publish(session.Store, rows, rowsPerSegment: 100);
        Assert.Equal(6, SessionSegments.Names(session.Store.Current!).Count);

        foreach ((TimeRange zoom, int opened) in new[]
        {
            (new TimeRange(2_050, 2_950), 1),
            (new TimeRange(1_950, 2_050), 2),

            // An interval ending where a segment's first row is leaves it shut; one starting at a segment's last row opens it.
            (new TimeRange(2_050, 3_000), 1),
            (new TimeRange(2_990, 3_010), 2),
            (new TimeRange(6_000, 9_000), 0),
            (new TimeRange(-500, 10_000), 6),
        })
        {
            // A reopened session holds no reader yet: the zoom opens what it meets and nothing else.
            SessionStore reopened = SessionStore.OpenExisting(LocalOwnedDirectory.Open(session.Path));
            SessionTimelineDetail detail = SessionTimelineQuery.Detail(reopened, zoom, 10);
            Assert.Equal(opened, reopened.SegmentReaderCache.Entries);
            Assert.Equal(rows.Count(row => zoom.Contains(row.SessionRelativeTicks!.Value / 100)),
                detail.Buckets.Sum(bucket => bucket.ObservationCount));

            // And it answers as one with every segment already open does, bucket for bucket.
            foreach (string name in SessionSegments.Names(reopened.Current!)) _ = SessionSegments.Open(reopened, reopened.Current!, name);
            Assert.Equal(detail.Buckets, SessionTimelineQuery.Detail(reopened, zoom, 10).Buckets);
            reopened.ReleaseSegmentReaders();
        }

        // A focus resolves its process over the whole session, and counts only what its interval meets, as without one.
        ProcessInstanceId client = SessionOverviewProjector.Project(session.Store).Nodes.Single().Id;
        SessionStore focused = SessionStore.OpenExisting(LocalOwnedDirectory.Open(session.Path));
        SessionFocusedTimeline narrow = SessionTimelineQuery.Focused(focused, new TimeRange(2_050, 2_950), 10,
            new TimelineFocus(null, [client]));
        Assert.Equal(90, narrow.Focus.Sum(bucket => bucket.ObservationCount));
        Assert.Equal(90, narrow.Whole.Buckets.Sum(bucket => bucket.ObservationCount));
        Assert.Equal([false, false, true, false, false, false], SessionSegments.Names(focused.Current!)
            .Select(name => SegmentTimeTiles.IsBuilt(SessionSegments.Open(focused, focused.Current!, name))));
        focused.ReleaseSegmentReaders();
    }

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };

    private static CoverageLedgerV1 TwoEpochs() => new()
    {
        Contract = CoverageLedgerV1.ContractName,
        Epochs = [Epoch(1, 0, 4_999, lost: 0), Epoch(2, 5_000, 9_999, lost: 3)],
    };

    private static CoverageEpochV1 Epoch(int number, long first, long last, long lost) => new()
    {
        Epoch = number,
        Acquisition = CoverageAcquisition.LiveCapture,
        FirstDeliveredNativeTicks = first,
        LastDeliveredNativeTicks = last,
        Collected =
        [
            new CoverageCollectedV1 { ProviderId = NetworkProvider, ProviderName = "network", EventId = 10, Version = 0, Mechanism = Mechanism.Tcp },
        ],
        Deliveries = [new CoverageDeliveryV1
        {
            ProviderId = NetworkProvider,
            EventId = 10,
            Version = 0,
            Delivered = 30,
            Admitted = 30,
            Omitted = 0,
        }],
        Losses =
        [
            new CoverageLossV1 { Layer = LossLayer.SourceSession, Lost = lost },
            new CoverageLossV1 { Layer = LossLayer.ConsumerBuffers, Lost = 0 },
            new CoverageLossV1 { Layer = LossLayer.CallbackQueue, Lost = 0 },
            new CoverageLossV1 { Layer = LossLayer.Storage, Lost = 0 },
        ],
    };
}
