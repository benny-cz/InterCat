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
/// The evidence rung of a published session: records read from the session in pages, scoped by the filter bar,
/// held still while inspected, and replayed with their scope when the workspace moves to a newer generation.
/// </summary>
public sealed class EvidenceRungTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";
    private const int Exchanges = 120;

    [Fact]
    public async Task TheWholeSessionLoadsInPagesAndTheViewHoldsItsGenerationWhileRead()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] rows = Rows();
        Publish(session.Store, rows);
        using WorkspaceViewModel workspace = Open(session);

        Assert.False(workspace.HoldsGeneration);
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;

        Assert.True(workspace.IsEvidenceRung);
        Assert.True(workspace.HoldsGeneration);
        Assert.Equal(SessionEvidenceQuery.DefaultPageSize, workspace.RungRows.Count);
        Assert.True(workspace.CanLoadMoreEvidence);
        Assert.Contains("more available", workspace.EvidenceStatus, StringComparison.Ordinal);
        Assert.StartsWith("Every admitted record", workspace.EvidenceScopeText, StringComparison.Ordinal);
        Assert.Contains(workspace.Filters, filter => filter.Field == "scope");

        await workspace.LoadMoreEvidenceAsync();
        await workspace.LoadMoreEvidenceAsync();
        Assert.Equal(rows.Length, workspace.RungRows.Count);
        Assert.False(workspace.CanLoadMoreEvidence);
        Assert.Contains("end of scope", workspace.EvidenceStatus, StringComparison.Ordinal);
        Assert.Equal(rows.Length, workspace.EvidenceMarkTicks.Count);
        Assert.Equal([.. workspace.EvidenceMarkTicks.Order()], workspace.EvidenceMarkTicks);

        workspace.SelectedRung = workspace.RungRows[5];
        Assert.True(workspace.HasSelectedEvidence);
        Assert.Equal(workspace.EvidenceMarkTicks[5], workspace.SelectedEvidenceTick);
        Assert.Contains(workspace.SelectedEvidenceFields, field => field.Label == "When");
        Assert.Contains(workspace.SelectedEvidenceFields, field => field.Label == "Owner");

        // A transfer's record says its bytes were never recorded, and why (§3.7).
        Assert.Equal(RecordContent.Of(workspace.SelectedEvidence!.Observation).Describe(),
            workspace.SelectedEvidenceFields.Single(field => field.Label == "Content").Value);
        Assert.Contains("Press Enter to open the original record", workspace.RungRows[5].AccessibleName,
            StringComparison.Ordinal);

        Assert.True(workspace.Ascend());
        Assert.False(workspace.IsEvidenceRung);
        Assert.False(workspace.HoldsGeneration);
        Assert.False(workspace.HasSelectedEvidence);
        Assert.Empty(workspace.EvidenceMarkTicks);
    }

    [Fact]
    public async Task AChannelOffersItsRecordsAndRemovingFiltersWidensTheScopeOneStepAtATime()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        Channel channel = workspace.Snapshot.Channels.Single();

        DescendTo(workspace, client.GroupKey);
        DescendTo(workspace, client.Id.ToString());
        DescendTo(workspace, channel.Key);
        Assert.True(workspace.IsEmptyRung);
        Assert.True(workspace.OffersEvidenceStep);
        Assert.Contains("no operation rung", workspace.EmptyReason, StringComparison.Ordinal);
        Assert.Equal(channel.EdgeKey, workspace.HighlightedEdgeKey);
        Assert.StartsWith($"{2 * Exchanges:N0} records on this channel", workspace.LevelSummaryShort, StringComparison.Ordinal);

        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.Equal(channel.EdgeKey, workspace.HighlightedEdgeKey);
        Assert.StartsWith("Paired TCP channel", workspace.EvidenceScopeText, StringComparison.Ordinal);
        Assert.Equal(SessionEvidenceQuery.DefaultPageSize, workspace.RungRows.Count);
        Assert.All(workspace.RungRows, row => Assert.StartsWith("TCP ", row.Label, StringComparison.Ordinal));

        // The step's own scope filter names the same channel, so removing it changes nothing.
        RemoveFilter(workspace, "scope");
        await workspace.EvidenceReady;
        Assert.StartsWith("Paired TCP channel", workspace.EvidenceScopeText, StringComparison.Ordinal);

        RemoveFilter(workspace, "channel");
        await workspace.EvidenceReady;
        Assert.StartsWith("Records owned by", workspace.EvidenceScopeText, StringComparison.Ordinal);
        Assert.Null(workspace.HighlightedEdgeKey);
        await workspace.LoadMoreEvidenceAsync();
        Assert.Equal(Exchanges + 1, workspace.RungRows.Count);
        Assert.All(workspace.RungRows, row => Assert.EndsWith("· PID 100", row.Detail, StringComparison.Ordinal));

        RemoveFilter(workspace, "process");
        RemoveFilter(workspace, "group");
        await workspace.EvidenceReady;
        Assert.StartsWith("Every admitted record", workspace.EvidenceScopeText, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§3.2: a rung's timeline counts the records E reads from it, over every record in the session")]
    public async Task EachRungsTimelineCountsWhatItsEvidenceReads()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        Channel channel = workspace.Snapshot.Channels.Single();
        TimeRange extent = workspace.Snapshot.Extent;

        // The view asks for the whole extent. The machine rung has no focus, and the overview's buckets need no count.
        workspace.RequestTimelineDetail(extent, 80);
        await workspace.TimelineDetailReady;
        Assert.False(workspace.TimelineShowsFocus);
        Assert.Null(workspace.TimelineFocusBuckets);
        Assert.StartsWith("Observed records", workspace.TimelineCaption, StringComparison.Ordinal);

        // A group counts the records its members own, on the overview's own columns, beside the whole timeline.
        DescendTo(workspace, client.GroupKey);
        Assert.True(workspace.TimelineShowsFocus);
        Assert.StartsWith("Counting records owned by", workspace.TimelineCaption, StringComparison.Ordinal);
        await workspace.TimelineDetailReady;
        ProcessInstanceId[] members = [.. workspace.Snapshot.Processes
            .Where(node => node.GroupKey == client.GroupKey).Select(node => node.Id)];
        Assert.Equal(SessionEvidenceQuery.ReadScope(session.Store, 10_000, ownerProcesses: members).Records.Count,
            FocusTotal(workspace));
        Assert.Equal(workspace.Snapshot.Timeline.Select(bucket => bucket.Interval),
            workspace.TimelineFocusBuckets!.Select(bucket => bucket.Interval));
        Assert.Null(workspace.TimelineDetail);
        Assert.StartsWith("Records owned by", workspace.TimelineCaption, StringComparison.Ordinal);
        Assert.True(workspace.ShowsProcessLanes);
        Assert.Equal(members.Length, workspace.ProcessLaneDisplay.Count);
        Assert.Contains("process lanes · machine context above", workspace.TimelineCaption, StringComparison.Ordinal);
        Assert.Equal(FocusTotal(workspace), workspace.ProcessLaneDisplay
            .Sum(lane => lane.Buckets.Sum(bucket => bucket.ObservationCount)));
        Assert.Contains(workspace.Intervals, row => row.Observations.EndsWith(" in focus", StringComparison.Ordinal));

        // An instance counts its own records, and a channel both ends' transfers.
        DescendTo(workspace, client.Id.ToString());
        await workspace.TimelineDetailReady;
        Assert.Equal(Exchanges + 1, FocusTotal(workspace));
        Assert.NotNull(workspace.TimelineDirectionLanes);
        Assert.Equal(FocusTotal(workspace), workspace.TimelineDirectionLanes!
            .Sum(lane => lane.Buckets.Sum(bucket => bucket.ObservationCount)));
        DescendTo(workspace, channel.Key);
        await workspace.TimelineDetailReady;
        Assert.Equal(2 * Exchanges, FocusTotal(workspace));
        Assert.Empty(workspace.TimelineDirectionLanes!);

        // Zoomed, the whole timeline and the focus are counted on the viewport's own columns.
        workspace.RequestTimelineDetail(new TimeRange(extent.StartTicks, extent.StartTicks + (extent.SpanTicks / 2)), 20);
        await workspace.TimelineDetailReady;
        SessionTimelineDetail detail = Assert.IsType<SessionTimelineDetail>(workspace.TimelineDetail);
        Assert.Equal(detail.Buckets.Select(bucket => bucket.Interval),
            workspace.TimelineFocusBuckets!.Select(bucket => bucket.Interval));
        Assert.All(detail.Buckets.Zip(workspace.TimelineFocusBuckets!), pair =>
            Assert.InRange(pair.Second.ObservationCount, 0, pair.First.ObservationCount));

        // The evidence rung reads the channel it was reached from, so its timeline keeps that count without a new one.
        IReadOnlyList<TimelineBucket> channelFocus = workspace.TimelineFocusBuckets!;
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        await workspace.TimelineDetailReady;
        Assert.Same(channelFocus, workspace.TimelineFocusBuckets);
        Assert.StartsWith("Paired TCP channel", workspace.TimelineCaption, StringComparison.Ordinal);

        // The machine rung has no focus and draws every record in its hue again.
        workspace.ReturnTo(0);
        await workspace.TimelineDetailReady;
        Assert.False(workspace.TimelineShowsFocus);
        Assert.Null(workspace.TimelineFocusBuckets);
        Assert.NotNull(workspace.TimelineDetail);
        Assert.StartsWith("Observed records", workspace.TimelineCaption, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§3.2: a focus the session cannot count says why and falls back to every record")]
    public async Task AFocusThatCannotBeCountedSaysWhy()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);

        // The records are read from a session that does not hold the process this view focuses.
        using var other = new TemporarySession();
        Publish(other.Store, [Timed(Lifecycle(1, ObservationKind.Create, 900, 1))]);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(other.Path, overview.SessionId, overview.Generation));
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        workspace.RequestTimelineDetail(workspace.Snapshot.Extent, 80);
        DescendTo(workspace, client.GroupKey);
        DescendTo(workspace, client.Id.ToString());
        await workspace.TimelineDetailReady;

        Assert.False(workspace.TimelineShowsFocus);
        Assert.Null(workspace.TimelineFocusBuckets);
        Assert.StartsWith("Records owned by", workspace.TimelineCaption, StringComparison.Ordinal);
        Assert.Contains("could not be counted: The focused process instance is not in this generation.",
            workspace.TimelineCaption, StringComparison.Ordinal);
        Assert.EndsWith("Every observed record is shown.", workspace.TimelineCaption, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§3.2: a live refresh keeps the rung's timeline counts until its own arrive, and only for the same focus")]
    public async Task ALiveRefreshKeepsTheTimelineFocusUntilItsOwnCountArrives()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel first = Open(session);
        ProcessNode client = first.Snapshot.Processes.Single(node => node.ProcessId == 100);
        first.RequestTimelineDetail(first.Snapshot.Extent, 80);
        DescendTo(first, client.GroupKey);
        DescendTo(first, client.Id.ToString());
        await first.TimelineDetailReady;
        IReadOnlyList<TimelineBucket> counted = first.TimelineFocusBuckets!;
        IReadOnlyList<DirectionTimelineLane> directions = first.TimelineDirectionLanes!;

        // The next publication restores the rung, shows the earlier counts at once, and says nothing is missing.
        using WorkspaceViewModel next = Open(session);
        Assert.Null(next.RestoreNavigation(first.CaptureNavigation()));
        next.AdoptTimeline(first.CarryTimeline());
        Assert.Same(counted, next.TimelineFocusBuckets);
        Assert.Same(directions, next.TimelineDirectionLanes);
        next.RequestTimelineDetail(next.Snapshot.Extent, 80);
        Assert.DoesNotContain("Counting", next.TimelineCaption, StringComparison.Ordinal);
        await next.TimelineDetailReady;
        Assert.NotSame(counted, next.TimelineFocusBuckets);
        Assert.NotSame(directions, next.TimelineDirectionLanes);
        Assert.Equal(Exchanges + 1, FocusTotal(next));

        // A publication shown at another rung does not adopt counts that describe other records.
        using WorkspaceViewModel machine = Open(session);
        machine.AdoptTimeline(first.CarryTimeline());
        Assert.Null(machine.TimelineFocusBuckets);
        Assert.Null(machine.TimelineDirectionLanes);
        Assert.False(machine.TimelineShowsFocus);
    }

    [Fact(DisplayName = "§6.2: a timeline bucket's hover states its interval, value, the focus's share, unmeasured part, coverage and scale")]
    public async Task ATimelineBucketsHoverStatesTheContract()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        workspace.RequestTimelineDetail(workspace.Snapshot.Extent, 80);
        DescendTo(workspace, client.GroupKey);
        DescendTo(workspace, client.Id.ToString());
        await workspace.TimelineDetailReady;

        TimelineBucket bucket = workspace.Snapshot.Timeline.OrderByDescending(candidate => candidate.ObservationCount).First();
        TimelineBucket focused = workspace.TimelineFocusBuckets!.Single(candidate => candidate.Interval == bucket.Interval);
        HoverCard card = workspace.DescribeTimelineHover(bucket, 1_000);
        Assert.Equal(WorkspaceTime.FormatHalfOpenRange(bucket.Interval, System.Globalization.CultureInfo.CurrentCulture), card.Title);
        Assert.Equal($"{bucket.ObservationCount:N0} observed records · mostly TCP", card.Lines[0]);
        Assert.Equal("Basis: source observations · unit: records · domain: every admitted record with a session time, all "
            + "mechanisms · accounting: not applicable to a count", card.Lines[1]);
        Assert.StartsWith("Records owned by", card.Lines[2], StringComparison.Ordinal);
        Assert.EndsWith($": {focused.ObservationCount:N0} {(focused.ObservationCount == 1 ? "record" : "records")} of them",
            card.Lines[2], StringComparison.Ordinal);
        Assert.StartsWith("Rate: ", card.Lines[3], StringComparison.Ordinal);
        Assert.EndsWith("height against the busiest visible bar, " + TimelineView.RateText(1_000), card.Lines[3],
            StringComparison.Ordinal);
        Assert.Equal("Unmeasured: none in this bucket; a record without a usable session time is placed in no bucket", card.Lines[4]);
        Assert.Equal("Bytes: not summed by the timeline · the interval table (T) reads those of what it lists", card.Lines[5]);
        Assert.StartsWith("Coverage: ", card.Lines[6], StringComparison.Ordinal);
        Assert.Equal($"Resolution: the overview's {workspace.Snapshot.Timeline.Count:N0} buckets over the whole session", card.Lines[7]);

        // A bucket that is the analysis interval says so instead of offering the click that would make it one.
        workspace.SelectInterval(bucket.Interval);
        Assert.Equal("This bucket is the analysis interval", workspace.DescribeTimelineHover(bucket, 1_000).Lines[^1]);
    }

    [Fact(DisplayName = "§3.2/§6.2: an instance's source-direction rows state their domain and scope the table, and survive a refresh")]
    public async Task AnInstancesSourceDirectionRowsStateTheirDomainAndScopeTheTable()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        workspace.RequestTimelineDetail(workspace.Snapshot.Extent, 80);
        DescendTo(workspace, client.GroupKey);
        DescendTo(workspace, client.Id.ToString());
        await workspace.TimelineDetailReady;

        // Every direction code has a row, in one order; the client's sends and its creation land in their own rows.
        Assert.True(workspace.ShowsDirectionLanes);
        IReadOnlyList<DirectionTimelineLane> lanes = workspace.TimelineDirectionLanes!;
        Assert.Equal(SessionTimelineQuery.LaneDirections, lanes.Select(lane => lane.Direction));
        Assert.Equal(Exchanges, Total(Direction.Outbound));
        Assert.Equal(1, Total(Direction.DirectionNotApplicable));
        Assert.Equal(0, Total(Direction.Inbound) + Total(Direction.Bidirectional) + Total(Direction.UnknownDirection));
        Assert.Contains("by source direction · machine context above · click a lane name", workspace.TimelineCaption,
            StringComparison.Ordinal);

        // Hover names the row, what the source's direction means for it, and the instance's count across every row.
        TimelineBucket sent = lanes[0].Buckets.First(bucket => bucket.ObservationCount > 0);
        TimelineBucket focused = workspace.TimelineFocusBuckets!.Single(bucket => bucket.Interval == sent.Interval);
        HoverCard card = workspace.DescribeTimelineHover(sent, 1_000, directionLane: Direction.Outbound);
        Assert.EndsWith(" · Outbound lane", card.Lines[0], StringComparison.Ordinal);
        Assert.Equal($"Basis: source observations · unit: records · domain: records owned by {client.NameWithPid} "
            + "marked outbound · accounting: one owner and one source direction per record", card.Lines[1]);
        Assert.Equal("Direction: outbound, as the source marks sends, connection attempts and client calls · it does "
            + "not say who initiated the conversation", card.Lines[2]);
        Assert.EndsWith($": {focused.ObservationCount:N0} {(focused.ObservationCount == 1 ? "record" : "records")} "
            + "in this interval across all directions", card.Lines[3], StringComparison.Ordinal);
        Assert.EndsWith("(shared scale)", card.Lines[4], StringComparison.Ordinal);
        TimelineBucket created = lanes[^1].Buckets.First(bucket => bucket.ObservationCount > 0);
        HoverCard creation = workspace.DescribeTimelineHover(created, 1_000, directionLane: Direction.DirectionNotApplicable);
        Assert.EndsWith(" with no data direction · accounting: one owner and one source direction per record",
            creation.Lines[1], StringComparison.Ordinal);
        Assert.StartsWith("Direction: none · process lifecycle records", creation.Lines[2], StringComparison.Ordinal);

        // A chosen row scopes the interval table and the caption; the graph and ranking are untouched.
        string[] ranked = [.. workspace.RungRows.Select(row => row.Key)];
        workspace.SelectDirectionLane(Direction.Outbound);
        Assert.Equal(Direction.Outbound, workspace.SelectedTimelineDirection);
        Assert.StartsWith("Outbound source-direction records", workspace.IntervalTableScope, StringComparison.Ordinal);
        Assert.Equal(lanes[0].Buckets.Select(bucket => bucket.Interval), workspace.Intervals.Select(row => row.Interval));
        Assert.Equal(sent.ObservationCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
            workspace.Intervals.Single(row => row.Interval == sent.Interval).Observations);
        Assert.Contains("Outbound table/step focus", workspace.TimelineCaption, StringComparison.Ordinal);
        Assert.Equal(ranked, workspace.RungRows.Select(row => row.Key));

        // The choice is navigation state: a same-session publication restores it with the rung.
        WorkspaceNavigationMemento saved = workspace.CaptureNavigation();
        Assert.Equal(Direction.Outbound, saved.SelectedTimelineDirection);
        using (WorkspaceViewModel next = Open(session))
        {
            Assert.Null(next.RestoreNavigation(saved));
            Assert.Equal(Direction.Outbound, next.SelectedTimelineDirection);
            next.RequestTimelineDetail(next.Snapshot.Extent, 80);
            await next.TimelineDetailReady;
            Assert.StartsWith("Outbound source-direction records", next.IntervalTableScope, StringComparison.Ordinal);
        }

        // All directions returns the instance's own counts beside the machine's.
        workspace.SelectDirectionLane(null);
        Assert.StartsWith("Whole session in", workspace.IntervalTableScope, StringComparison.Ordinal);
        Assert.Contains(workspace.Intervals, row => row.Observations.EndsWith(" in focus", StringComparison.Ordinal));

        // The rows belong to the instance rung; the group above draws its own.
        workspace.SelectDirectionLane(Direction.Outbound);
        Assert.True(workspace.Ascend());
        await workspace.TimelineDetailReady;
        Assert.False(workspace.ShowsDirectionLanes);
        Assert.DoesNotContain("source-direction", workspace.IntervalTableScope, StringComparison.Ordinal);

        int Total(Direction direction) => lanes.Single(lane => lane.Direction == direction).Buckets
            .Sum(bucket => bucket.ObservationCount);
    }

    [Fact(DisplayName = "§3.2: a channel's two ends are lanes with their own table focus, kept by a refresh and reset by another focus")]
    public async Task AChannelsEndsScopeTheTableAndSurviveARefresh()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        ProcessNode server = workspace.Snapshot.Processes.Single(node => node.ProcessId == 200);
        Channel channel = workspace.Snapshot.Channels.Single();
        workspace.RequestTimelineDetail(workspace.Snapshot.Extent, 80);
        DescendTo(workspace, client.GroupKey);
        DescendTo(workspace, client.Id.ToString());
        DescendTo(workspace, channel.Key);
        await workspace.TimelineDetailReady;

        // Each end is named by who holds it and its own endpoint; the client's sends and the server's receives.
        Assert.True(workspace.ShowsChannelEndLanes);
        IReadOnlyList<ChannelEndTimelineLane> ends = workspace.TimelineChannelEndLanes!;
        ChannelEndTimelineLane clientEnd = ends.Single(end => end.Holder == client.Id);
        ChannelEndTimelineLane serverEnd = ends.Single(end => end.Holder == server.Id);
        Assert.Equal([null, 0, 1], workspace.ChannelEndOptions.Select(option => option.End));
        Assert.Equal($"{client.NameWithPid} · {ClientEnd}", workspace.ChannelEndLabel(clientEnd));
        Assert.Equal((Exchanges, 0), (Sum(clientEnd.Outbound), Sum(clientEnd.Inbound)));
        Assert.Equal((0, Exchanges), (Sum(serverEnd.Outbound), Sum(serverEnd.Inbound)));
        Assert.Contains("one lane per end: outbound above its midline, inbound below", workspace.TimelineCaption,
            StringComparison.Ordinal);

        // Hover names the end and splits its bucket by direction; the channel's count spans both ends.
        TimelineBucket sent = clientEnd.Buckets.First(bucket => bucket.ObservationCount > 0);
        HoverCard card = workspace.DescribeTimelineHover(sent, 1_000, endLane: clientEnd);
        Assert.EndsWith($" · the {ClientEnd} end, held by {client.NameWithPid}", card.Lines[0], StringComparison.Ordinal);
        Assert.Contains($"domain: records made at {ClientEnd}, the end of this paired TCP channel that {client.NameWithPid} holds",
            card.Lines[1], StringComparison.Ordinal);
        Assert.Equal($"Direction: {sent.ObservationCount:N0} outbound, drawn above the midline · 0 inbound, below · "
            + "0 with no data direction, marked on it", card.Lines[2]);
        Assert.EndsWith(" in this interval at both ends", card.Lines[3], StringComparison.Ordinal);
        Assert.EndsWith("(shared scale)", card.Lines[4], StringComparison.Ordinal);

        // A chosen end scopes the table and the caption.
        workspace.SelectChannelEnd(clientEnd.End);
        Assert.Equal(clientEnd.End, workspace.SelectedChannelEndOption.End);
        Assert.StartsWith($"Records made at {client.NameWithPid} · {ClientEnd}", workspace.IntervalTableScope,
            StringComparison.Ordinal);
        Assert.Equal(clientEnd.Buckets.Select(bucket => bucket.Interval), workspace.Intervals.Select(row => row.Interval));
        Assert.Equal(sent.ObservationCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
            workspace.Intervals.Single(row => row.Interval == sent.Interval).Observations);
        Assert.Contains("table/step focus", workspace.TimelineCaption, StringComparison.Ordinal);

        // A same-session publication restores the end with the rung and shows the carried lanes at once.
        WorkspaceNavigationMemento saved = workspace.CaptureNavigation();
        Assert.Equal(clientEnd.End, saved.SelectedChannelEnd);
        using (WorkspaceViewModel next = Open(session))
        {
            Assert.Null(next.RestoreNavigation(saved));
            next.AdoptTimeline(workspace.CarryTimeline());
            Assert.Same(ends, next.TimelineChannelEndLanes);
            Assert.Equal(clientEnd.End, next.SelectedChannelEnd);
            Assert.StartsWith("Records made at", next.IntervalTableScope, StringComparison.Ordinal);
        }

        // The evidence rung keeps the channel's count but draws no end lanes, so the table lists the channel again.
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.False(workspace.ShowsChannelEndLanes);
        Assert.DoesNotContain("Records made at", workspace.IntervalTableScope, StringComparison.Ordinal);
        Assert.True(workspace.Ascend());
        Assert.True(workspace.ShowsChannelEndLanes);
        Assert.StartsWith("Records made at", workspace.IntervalTableScope, StringComparison.Ordinal);

        // An end is a place on one channel: another focus forgets it.
        workspace.ReturnTo(2);
        Assert.Null(workspace.SelectedChannelEnd);
        Assert.Null(workspace.SelectedChannelEndOption.End);

        static int Sum(IReadOnlyList<TimelineBucket> buckets) => buckets.Sum(bucket => bucket.ObservationCount);
    }

    [Fact(DisplayName = "§6.2: a large group zoomed keeps its process lanes, counted in fewer columns, and says so")]
    public async Task ALargeGroupZoomedKeepsItsLanesCountedCoarser()
    {
        using var session = new TemporarySession();

        // Forty instances of one executable, each created and then sending ten times across the capture.
        Publish(session.Store, [.. Enumerable.Range(0, 40).SelectMany(index => new[]
        {
            Lifecycle(index + 1, ObservationKind.Create, 2_000 + index, (ulong)(index * 20)) with
            {
                ResourceName = @"C:\Tools\pool.exe", SessionRelativeTicks = (index + 1) * 100L,
            },
        }.Concat(Enumerable.Range(0, 10).Select(step =>
            Transfer(100 + (step * 100) + index, ObservationKind.Send, AccountingSide.SendSide, 8, 2_000 + index,
                (ulong)((index * 20) + step + 1)) with { SessionRelativeTicks = (100 + (step * 100) + index) * 100L })))]);
        using WorkspaceViewModel workspace = Open(session);
        string group = workspace.Snapshot.Processes.First(node => node.ProcessId == 2_000).GroupKey;
        TimeRange extent = workspace.Snapshot.Extent;
        DescendTo(workspace, group);

        // Zoomed in at 1,000 columns, forty lanes would need 40,000 cells: they are counted in the 500 that fit.
        workspace.RequestTimelineDetail(new TimeRange(extent.StartTicks + 1, extent.EndTicks), 1_000);
        await workspace.TimelineDetailReady;
        Assert.Null(workspace.ProcessLaneProblem);
        Assert.True(workspace.ShowsProcessLanes);
        Assert.Equal(40, workspace.ProcessLaneDisplay.Count);
        Assert.All(workspace.ProcessLaneDisplay, lane => Assert.Equal(500, lane.Buckets.Count));
        // The view starts one tick after the first record, the first instance's creation.
        Assert.Equal((40 * 11) - 1, workspace.ProcessLaneDisplay.Sum(lane => lane.Buckets.Sum(bucket => bucket.ObservationCount)));
        string cells = 20_000.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
        Assert.Contains($"40 process lanes in 500 columns, coarser than the view, to stay within {cells} cells",
            workspace.TimelineCaption, StringComparison.Ordinal);

        // A lane bucket's card says whose count it is, at which resolution, and why.
        ProcessTimelineLane lane = workspace.ProcessLaneDisplay[0];
        TimelineBucket bucket = lane.Buckets.First(candidate => candidate.ObservationCount > 0);
        ProcessNode owner = workspace.Snapshot.Processes.Single(node => node.Id == lane.ProcessId);
        Assert.Contains($"Resolution: the lanes' own count, 500 columns, coarser than the view's so the group's 40 lanes stay within {cells} cells",
            workspace.DescribeTimelineHover(bucket, 1, ownerLane: owner).Lines);
    }

    private static int FocusTotal(WorkspaceViewModel workspace) =>
        Assert.IsAssignableFrom<IReadOnlyList<TimelineBucket>>(workspace.TimelineFocusBuckets).Sum(bucket => bucket.ObservationCount);

    [Fact]
    public async Task ASelectedProcessAtMachineAndAChannelFromTheListScopeTheRungVisibly()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);
        ProcessNode server = workspace.Snapshot.Processes.Single(node => node.ProcessId == 200);

        workspace.SelectProcess(server.Id);
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.Contains(workspace.Filters, filter => filter.Field == "scope" && filter.Label == server.Name);
        Assert.StartsWith("Records owned by", workspace.EvidenceScopeText, StringComparison.Ordinal);
        await workspace.LoadMoreEvidenceAsync();
        Assert.Equal(Exchanges + 1, workspace.RungRows.Count);
        Assert.All(workspace.RungRows, row => Assert.EndsWith("· PID 200", row.Detail, StringComparison.Ordinal));

        Assert.True(workspace.Ascend());
        Channel channel = workspace.Snapshot.Channels.Single();
        Assert.True(workspace.ShowChannelEvidence(channel));
        await workspace.EvidenceReady;
        Assert.StartsWith("Paired TCP channel", workspace.EvidenceScopeText, StringComparison.Ordinal);
        Assert.False(workspace.ShowChannelEvidence(channel));
    }

    [Fact]
    public async Task ANewerGenerationReplaysTheRungsScopeAndReselectsTheRecord()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel first = Open(session);
        Assert.True(first.ShowChannelEvidence(first.Snapshot.Channels.Single()));
        await first.EvidenceReady;
        first.SelectedRung = first.RungRows[3];
        WorkspaceNavigationMemento saved = first.CaptureNavigation();

        // Loading more while held reads the newer generation and continues after the last row shown.
        Publish(session.Store, [Timed(Transfer(5_000, ObservationKind.Send, AccountingSide.SendSide, 1, 100, 9_000)
            .Between(ClientEnd, ServerEnd))]);
        await first.LoadMoreEvidenceAsync();
        await first.LoadMoreEvidenceAsync();
        Assert.Equal(2 * Exchanges + 1, first.RungRows.Count);
        Assert.Contains("later pages read generation 2", first.EvidenceStatus, StringComparison.Ordinal);

        using WorkspaceViewModel second = Open(session);
        Assert.Null(second.RestoreNavigation(saved));
        Assert.True(second.IsEvidenceRung);
        await second.EvidenceReady;
        Assert.StartsWith("Paired TCP channel", second.EvidenceScopeText, StringComparison.Ordinal);
        Assert.Equal(saved.SelectedRungKey, second.SelectedRung?.Key);
        Assert.True(second.HasSelectedEvidence);
    }

    [Fact]
    public async Task AScopeTheGenerationCannotReadIsStatedNotWidened()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);
        Channel missing = workspace.Snapshot.Channels.Single() with { Key = "transport:missing", Name = "gone" };

        Assert.True(workspace.ShowChannelEvidence(missing));
        await workspace.EvidenceReady;
        Assert.True(workspace.IsEmptyRung);
        Assert.Empty(workspace.RungRows);
        Assert.Contains("not uniquely admitted", workspace.EmptyReason, StringComparison.Ordinal);
        Assert.Equal("Source records unavailable", workspace.EvidenceStatus);
        Assert.False(workspace.CanLoadMoreEvidence);
    }

    [Fact(DisplayName = "R13: the machine rung counts each process's own records and says what none holds; lanes sit where their rows do")]
    public async Task OwnRecordsRankTheRungsAndOrderTheLanes()
    {
        // The one-sided sender, PID 300, is the busiest process though it has no paired peer; one record names no owner.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            .. Rows(),
            .. Enumerable.Range(0, 2 * Exchanges).Select(index => Timed(
                Transfer(4_001 + index, ObservationKind.Send, AccountingSide.SendSide, 3, 300, (ulong)(8_001 + index))
                    .Between("127.0.0.1:50001", "127.0.0.1:9090"))),
            Timed(Transfer(9_000, ObservationKind.Send, AccountingSide.SendSide, 3, null, 20_000).Between(ClientEnd, ServerEnd)),
        ]);
        using WorkspaceViewModel workspace = Open(session);
        Assert.EndsWith(" · each process's own records; 1 more row no process holds, in the timeline only",
            workspace.LevelSummary, StringComparison.Ordinal);
        Assert.EndsWith(" · own records", workspace.LevelSummaryShort, StringComparison.Ordinal);

        workspace.RequestTimelineDetail(workspace.Snapshot.Extent, 80);
        await workspace.TimelineDetailReady;
        ProcessNode sender = workspace.Snapshot.Processes.Single(node => node.ProcessId == 300);
        DescendTo(workspace, sender.GroupKey);
        await workspace.TimelineDetailReady;
        Assert.EndsWith(" · each process's own records", workspace.LevelSummary, StringComparison.Ordinal);
        Assert.Equal(sender.Id.ToString(), workspace.RungRows[0].Key);
        Assert.Equal((2 * Exchanges) + 1, sender.Records);

        // Each lane sits where its row does, so the busiest process's lane is on top rather than the lowest PID's.
        Assert.True(workspace.ShowsProcessLanes);
        Assert.Equal(workspace.RungRows.Select(row => row.Key), workspace.ProcessLaneDisplay.Select(lane => lane.ProcessId.ToString()));

        // A process's channel rung counts admitted paired TCP, and says so.
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        DescendTo(workspace, client.Id.ToString());
        Assert.EndsWith(" · admitted paired TCP only; not all session observations", workspace.LevelSummary, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R22: an RPC server no lifecycle record names is a process of its own calls, and its rows name it")]
    public async Task AnRpcServerIsTheProcessOfTheCallsItRaised()
    {
        // The service host raised every server call and appears in no lifecycle record, as a long-running host does not.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            .. Rows(),
            .. Enumerable.Range(0, 6).Select(index => Timed(RpcCall(20 + index, index % 2 == 0 ? ObservationKind.RequestStart
                : ObservationKind.RequestEnd, Direction.Inbound, raisedBy: 1_960, (ulong)(30_000 + index)))),
        ]);
        using WorkspaceViewModel workspace = Open(session);
        ProcessNode host = workspace.Snapshot.Processes.Single(node => node.ProcessId == 1_960);
        Assert.Equal(6, host.Records);

        DescendTo(workspace, host.GroupKey);
        DescendTo(workspace, host.Id.ToString());

        // Its rung lists only its own calls, so their records are its rung's total, not the zero of no paired channel.
        await workspace.RpcReady;
        Assert.False(workspace.IsEmptyRung);
        Assert.StartsWith("6 call records in 1 RPC channel · RPC carries no size", workspace.LevelSummary, StringComparison.Ordinal);
        Assert.StartsWith("6 call records", workspace.LevelSummaryShort, StringComparison.Ordinal);

        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.StartsWith("Records owned by", workspace.EvidenceScopeText, StringComparison.Ordinal);
        Assert.Equal(6, workspace.RungRows.Count);
        Assert.All(workspace.RungRows, row =>
        {
            Assert.StartsWith("RPC request ", row.Label, StringComparison.Ordinal);
            Assert.EndsWith("· PID 1960", row.Detail, StringComparison.Ordinal);
        });
    }

    [Fact(DisplayName = "P8: a process's RPC calls are channels by interface, listed a page at a time, each call's records one step away")]
    public async Task RpcCallsAreChannelsOfPagedCallsWithTheirRecordsOneStepAway()
    {
        Guid serviceControl = Guid.Parse("367abb81-9844-35f1-ad32-98f038001003");
        Guid other = Guid.Parse("0a74ef1c-41a4-4e06-83ae-dc74fb1cdd53");
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            .. Rows(),
            .. Enumerable.Range(0, 3).SelectMany(index => new[]
            {
                Timed(RpcCall(5_000 + (10 * index), ObservationKind.RequestStart, Direction.Outbound, 100,
                    (ulong)(40_000 + (2 * index)), Activity(index), serviceControl)),
                Timed(RpcCall(5_004 + (10 * index), ObservationKind.RequestEnd, Direction.Outbound, 100,
                    (ulong)(40_001 + (2 * index)), Activity(index), status: index == 2 ? 5 : 0)),
            }),
            .. Enumerable.Range(0, 150).SelectMany(index => new[]
            {
                Timed(RpcCall(6_000 + (10 * index), ObservationKind.RequestStart, Direction.Outbound, 100,
                    (ulong)(50_000 + (2 * index)), Activity(1_000 + index), other)),
                Timed(RpcCall(6_002 + (10 * index), ObservationKind.RequestEnd, Direction.Outbound, 100,
                    (ulong)(50_001 + (2 * index)), Activity(1_000 + index), status: 0)),
            }),
        ]);
        using WorkspaceViewModel workspace = Open(session);
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        DescendTo(workspace, client.GroupKey);
        DescendTo(workspace, client.Id.ToString());
        await workspace.RpcReady;

        // The process's calls to each interface are channels of its own, ranked by records beside its paired channel, which
        // reads by the process at its other end and keeps its own name for its tooltip and crumb.
        Assert.Equal(
            [
                other.ToString(),
                "↔ PID 200",
                "svcctl (Service Control Manager)",
            ],
            workspace.RungRows.Select(row => row.Label));
        Assert.Equal(workspace.Snapshot.Channels.Single().Name, workspace.RungRows[1].Source.Label);
        Assert.Equal("RPC client · 3 calls · 1 failed · median 400 ns", workspace.RungRows[2].Detail.Replace(' ', ' '));
        Assert.EndsWith(" · admitted paired TCP and this process's RPC calls by interface; not all session observations",
            workspace.LevelSummary, StringComparison.Ordinal);

        // A channel lists its calls a page at a time, in reading order.
        workspace.SelectedRung = workspace.RungRows[0];
        Assert.True(workspace.Descend());
        Assert.True(workspace.IsRpcChannelRung);
        await workspace.RpcReady;
        Assert.Equal(SessionRpcCalls.DefaultPageSize, workspace.RungRows.Count);
        Assert.True(workspace.CanLoadMore);
        Assert.Equal("Load more calls (M)", workspace.LoadMoreLabel);
        Assert.StartsWith("150 calls · median 200 ns · 100 listed", workspace.LevelSummaryShort, StringComparison.Ordinal);
        await workspace.LoadMoreAsync();
        Assert.Equal(150, workspace.RungRows.Count);
        Assert.False(workspace.CanLoadMore);
        Assert.All(workspace.RungRows, row => Assert.Equal("200 ns", row.Label));

        // The failing call to the named interface, and then its two records.
        Assert.True(workspace.Ascend());
        await workspace.RpcReady;
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "svcctl (Service Control Manager)");
        Assert.True(workspace.Descend());
        await workspace.RpcReady;
        RungRow failed = workspace.RungRows[2];
        Assert.Equal("failed, status 5", failed.Detail[(failed.Detail.IndexOf(" · ", StringComparison.Ordinal) + 3)..]);
        workspace.SelectedRung = failed;
        Assert.True(workspace.Descend());
        Assert.True(workspace.IsEvidenceRung);
        await workspace.EvidenceReady;
        Assert.Equal(["RPC request start", "RPC request end"], workspace.RungRows.Select(row => row.Label));
        Assert.StartsWith("Records of RPC call at +", workspace.EvidenceScopeText, StringComparison.Ordinal);

        // E at a channel reads every record of its calls.
        Assert.True(workspace.Ascend());
        await workspace.RpcReady;
        Assert.Equal(3, workspace.RungRows.Count);
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.Equal(6, workspace.RungRows.Count);
        Assert.StartsWith("Records of RPC calls to svcctl", workspace.EvidenceScopeText, StringComparison.Ordinal);
    }

    private static Guid Activity(int number) => new(number, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 1);

    [Fact(DisplayName = "I21: a process's HTTP exchanges are a row of its rung, listed with their parts, each exchange's buffers one step away")]
    public async Task HttpExchangesAreARowOfTheProcessRung()
    {
        // Process 100 made two HTTP exchanges beside its paired channel: exchange 1 in four buffers, and exchange 2, whose
        // response body's last buffer was not recorded.
        (long Ticks, ushort Event, long Number, long Sequence, long Flags, long Bytes)[] buffers =
        [
            (5_000, 2001, 1, 0, 3, 181), (5_002, 2003, 1, 0, 3, 115), (5_004, 2004, 1, 0, 1, 900), (5_006, 2004, 1, 1, 2, 0),
            (6_000, 2001, 2, 0, 3, 181), (6_002, 2003, 2, 0, 3, 115), (6_004, 2004, 2, 0, 1, 50),
        ];
        ObservationRowV1[] http = [.. buffers.Select((buffer, index) => Timed(Transfer(buffer.Ticks,
            buffer.Event <= 2002 ? ObservationKind.Send : ObservationKind.Receive,
            buffer.Event <= 2002 ? AccountingSide.SendSide : AccountingSide.ReceiveSide, buffer.Bytes, null, (ulong)(60_000 + index)) with
        {
            Mechanism = Mechanism.Http,
            Layer = ObservationLayer.Application,
            EventId = buffer.Event,
            HeaderProcessId = 100,
            Direction = buffer.Event <= 2002 ? Direction.Outbound : Direction.Inbound,
            ByteDomain = ByteDomain.ApplicationPayload,
        }))];
        SourceFieldRowV1[] fields =
        [
            .. buffers.SelectMany((buffer, index) => new[]
            {
                Field(http[index], SourceField.HttpExchangeId, buffer.Number),
                Field(http[index], SourceField.ContentBufferSequence, buffer.Sequence),
                Field(http[index], SourceField.ContentBufferFlags, buffer.Flags),
            }),
        ];
        using var session = new TemporarySession();
        Publish(session.Store, [.. Rows(), .. http], fields: fields);
        using WorkspaceViewModel workspace = Open(session);
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        DescendTo(workspace, client.GroupKey);
        DescendTo(workspace, client.Id.ToString());
        await workspace.HttpReady;

        // Its exchanges are a row of their own beside its paired channel, where it had said Nothing at this level of them.
        RungRow row = workspace.RungRows.Single(candidate => candidate.Label == "HTTP exchanges");
        Assert.Equal(HttpChannelSummary.Name, row.Source.Label);
        Assert.Equal("HTTP client · 2 exchanges · 1 not recorded whole · median 600 ns", row.Detail.Replace(' ', ' '));
        Assert.Equal("7", row.Observations);
        Assert.EndsWith(" · admitted paired TCP and this process's HTTP exchanges; not all session observations",
            workspace.LevelSummary, StringComparison.Ordinal);

        // Enter lists the exchanges, each leading with how long it took, or that its end was not recorded.
        workspace.SelectedRung = row;
        Assert.True(workspace.Descend());
        Assert.True(workspace.IsHttpChannelRung);
        await workspace.HttpReady;
        Assert.Equal(["600 ns", "response end not recorded"], workspace.RungRows.Select(exchange => exchange.Label));
        Assert.Contains("exchange 2 · request 181 B · response 115 B + 50 B · not recorded whole: response body",
            workspace.RungRows[1].Detail.Replace(' ', ' '), StringComparison.Ordinal);
        Assert.StartsWith("2 exchanges · 1 not recorded whole", workspace.LevelSummaryShort, StringComparison.Ordinal);
        Assert.False(workspace.CanLoadMore);

        // An exchange's buffers are one step away, and so are all of the process's.
        workspace.SelectedRung = workspace.RungRows[0];
        Assert.True(workspace.Descend());
        Assert.True(workspace.IsEvidenceRung);
        await workspace.EvidenceReady;
        Assert.Equal(4, workspace.RungRows.Count);
        Assert.All(workspace.RungRows, buffer => Assert.StartsWith("HTTP ", buffer.Label, StringComparison.Ordinal));
        Assert.StartsWith("Records of HTTP exchange at +", workspace.EvidenceScopeText, StringComparison.Ordinal);
        Assert.True(workspace.Ascend());
        await workspace.HttpReady;
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.Equal(7, workspace.RungRows.Count);
    }

    [Fact]
    public async Task ABrushedIntervalReRanksEveryRungAndClearingItRestoresTheWholeSession()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] rows = Rows();
        Publish(session.Store, rows);
        using WorkspaceViewModel workspace = Open(session);
        long whole = long.Parse(workspace.RungRows.Single().Observations, System.Globalization.NumberStyles.AllowThousands,
            System.Globalization.CultureInfo.CurrentCulture);
        Assert.False(workspace.IsRankedWithinInterval);
        Assert.Equal(string.Empty, workspace.RankingScopeText);

        // Only the first twenty exchanges fall inside the brush; the machine rung now counts exactly those.
        var brush = new TimeRange(10, 50);
        workspace.SelectInterval(brush);
        Assert.Contains("Ranking within", workspace.RankingScopeText, StringComparison.Ordinal);
        await workspace.IntervalReady;
        Assert.True(workspace.IsRankedWithinInterval);
        Assert.StartsWith("Ranked within", workspace.RankingScopeText, StringComparison.Ordinal);
        Assert.Equal("40", workspace.RungRows.Single().Observations);

        // The whole session's machine rung counts each process's own records: both creations, every exchange and the
        // unpaired send, which no edge carries.
        Assert.Equal(whole, workspace.WholeSnapshot.Processes.Sum(node => node.Records));
        Assert.Equal(rows.Length, whole);
        Assert.Equal(2 * Exchanges, workspace.WholeSnapshot.Edges.Sum(edge => edge.ObservationCount));

        // The channel rung and the relationship table count the same interval.
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        DescendTo(workspace, client.GroupKey);
        DescendTo(workspace, client.Id.ToString());
        Assert.Equal("40", workspace.RungRows.Single().Observations);
        Assert.Equal("40", workspace.Relationships.Single().Observations);

        // A newer brush supersedes one still being read; only the newest is applied.
        workspace.SelectInterval(new TimeRange(10, 20));
        workspace.SelectInterval(new TimeRange(10, 30));
        await workspace.IntervalReady;
        Assert.Equal("20", workspace.RungRows.Single().Observations);

        workspace.ClearSelection();
        await workspace.IntervalReady;
        Assert.False(workspace.IsRankedWithinInterval);
        Assert.Equal(workspace.WholeSnapshot, workspace.Snapshot);
        Assert.Equal((2 * Exchanges).ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
            workspace.RungRows.Single().Observations);
    }

    [Fact(DisplayName = "§6.4: with nothing brushed the ranking and E count the visible range, a brush wins, and keeping the range holds it")]
    public void TheVisibleRangeIsTheScopeWhenNothingIsBrushed() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);
        string whole = workspace.RungRows.Single().Observations;
        Assert.Null(workspace.ScopeInterval);
        Assert.False(workspace.ShowsRankingScope);
        Assert.False(workspace.CanKeepVisibleRange);

        // The timeline zoomed to [10, 50): the ranking counts only what is visible there, and says so while it counts.
        var visible = new TimeRange(10, 50);
        workspace.ShowVisibleRange(visible);
        Assert.Equal(visible, workspace.ScopeInterval);
        Assert.True(workspace.ScopeFollowsView);
        Assert.True(workspace.ShowsRankingScope);
        Assert.StartsWith("Ranking within the visible ", workspace.RankingScopeText, StringComparison.Ordinal);
        await workspace.IntervalReady;
        Assert.Equal("40", workspace.RungRows.Single().Observations);
        Assert.StartsWith("Ranked within the visible ", workspace.RankingScopeText, StringComparison.Ordinal);
        Assert.Equal("Visible " + WorkspaceTime.FormatRange(visible, System.Globalization.CultureInfo.CurrentCulture),
            workspace.IntervalLabel);
        Assert.Equal(visible, workspace.DescribeExport(DateTimeOffset.UnixEpoch).Interval);

        // A brush is explicit and wins however the timeline moves; clearing it gives the scope back to the view.
        workspace.SelectInterval(new TimeRange(10, 30));
        await workspace.IntervalReady;
        Assert.Equal("20", workspace.RungRows.Single().Observations);
        Assert.False(workspace.ScopeFollowsView);
        Assert.False(workspace.CanKeepVisibleRange);
        workspace.ShowVisibleRange(new TimeRange(10, 20));
        await workspace.IntervalReady;
        Assert.Equal("20", workspace.RungRows.Single().Observations);
        workspace.ClearSelection();
        await workspace.IntervalReady;
        Assert.Equal(new TimeRange(10, 20), workspace.ScopeInterval);
        Assert.Equal("10", workspace.RungRows.Single().Observations);

        // Fitting the timeline shows everything, and the ranking counts the whole session again.
        workspace.ShowVisibleRange(workspace.WholeSnapshot.Extent);
        await workspace.IntervalReady;
        Assert.Null(workspace.ScopeInterval);
        Assert.False(workspace.ShowsRankingScope);
        Assert.Equal(string.Empty, workspace.RankingScopeText);
        Assert.Equal(whole, workspace.RungRows.Single().Observations);

        // E lists exactly the records behind the counts on screen: the visible range's forty.
        workspace.ShowVisibleRange(visible);
        await workspace.IntervalReady;
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.Equal(40, workspace.RungRows.Count);
        Assert.False(workspace.CanLoadMoreEvidence);
        Assert.True(workspace.Ascend());

        // The scope lock keeps the visible range as the interval, so zooming on to look around counts the same range.
        Assert.True(workspace.KeepVisibleRange());
        Assert.Equal(visible, workspace.SelectedInterval);
        Assert.False(workspace.CanKeepVisibleRange);
        Assert.False(workspace.KeepVisibleRange());
        workspace.ShowVisibleRange(new TimeRange(10, 20));
        await workspace.IntervalReady;
        Assert.Equal(visible, workspace.ScopeInterval);
        Assert.Equal("40", workspace.RungRows.Single().Observations);
    });

    [Fact(DisplayName = "P21: a count superseded while it runs never applies, and every pane answers the one scope on screen")]
    public void ASupersededCountNeverApplies() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);

        // A second brush lands before the first one's count can: only the second is ever applied.
        workspace.SelectInterval(new TimeRange(10, 50));
        Task first = workspace.IntervalReady;
        var second = new TimeRange(10, 30);
        workspace.SelectInterval(second);
        await workspace.IntervalReady;
        await first;
        Assert.Equal(second, workspace.ScopeInterval);

        // The ranking, the relationship table, the graph and an export all answer that one scope, never a mixture.
        Assert.Equal("20", workspace.RungRows.Single().Observations);
        Assert.Equal("20", workspace.Relationships.Single().Observations);
        Assert.Equal(20, workspace.GraphDisplay.Edges.Sum(edge => edge.ObservationCount));
        Assert.Equal(second, workspace.DescribeExport(DateTimeOffset.UnixEpoch).Interval);
        Assert.StartsWith("Ranked within", workspace.RankingScopeText, StringComparison.Ordinal);
    });

    [Fact(DisplayName = "P21: a closed workspace applies nothing that was still being counted for it")]
    public void AClosedWorkspaceAppliesNothing() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        WorkspaceViewModel workspace = Open(session);
        workspace.SelectInterval(new TimeRange(10, 50));
        Task pending = workspace.IntervalReady;
        string whole = workspace.RungRows.Single().Observations;

        workspace.Dispose();
        await pending;
        Assert.False(workspace.IsRankedWithinInterval);
        Assert.Equal(whole, workspace.RungRows.Single().Observations);
    });

    [Fact(DisplayName = "P23: while a count is in flight the view stays populated, says it is pending, and the ladder still answers")]
    public void AnInFlightCountBlocksNothing() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        workspace.SelectInterval(new TimeRange(10, 50));
        await workspace.IntervalReady;
        Assert.Equal("40", workspace.RungRows.Single().Observations);

        // A new brush is being counted: the previous answer stays on screen, labelled pending, never cleared.
        workspace.SelectInterval(new TimeRange(10, 30));
        Assert.True(workspace.IsRankedWithinInterval);
        Assert.Equal("40", workspace.RungRows.Single().Observations);
        Assert.StartsWith("Ranking within", workspace.RankingScopeText, StringComparison.Ordinal);

        // Input is not gated on the count: a descent happens now, with rows, and the count lands afterwards.
        DescendTo(workspace, client.GroupKey);
        Assert.Equal("L1 · GROUP", workspace.LevelBadge);
        Assert.NotEmpty(workspace.RungRows);
        await workspace.IntervalReady;
        Assert.StartsWith("Ranked within", workspace.RankingScopeText, StringComparison.Ordinal);
        Assert.Equal(20, workspace.RungRows.Sum(row => long.Parse(row.Observations, System.Globalization.NumberStyles.AllowThousands,
            System.Globalization.CultureInfo.CurrentCulture)));
    });

    [Fact]
    public async Task AnExportNamesTheAppliedSnapshotAndSaysWhetherItIsTheWholeScope()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] rows = Rows();
        Publish(session.Store, rows);
        using WorkspaceViewModel workspace = Open(session);
        var at = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

        // A brushed ranking exports the interval its counts answer, and only once those counts are applied.
        var brush = new TimeRange(10, 50);
        workspace.SelectInterval(brush);
        Assert.Null(workspace.DescribeExport(at).Interval);
        await workspace.IntervalReady;
        ExportContext ranked = workspace.DescribeExport(at);
        Assert.Equal(brush, ranked.Interval);
        Assert.True(ranked.Complete);
        Assert.StartsWith("Ranked within", ranked.Scope, StringComparison.Ordinal);
        using (var json = System.Text.Json.JsonDocument.Parse((await workspace.ExportAsync(ExportFormat.Json, at)).Content))
        {
            Assert.Equal("ranking", json.RootElement.GetProperty("kind").GetString());
            Assert.Equal(40, json.RootElement.GetProperty("rows")[0].GetProperty("observations").GetInt64());
        }

        // At the evidence rung an export holds its whole scope, read in one pass, not only the page loaded so far.
        workspace.ClearSelection();
        await workspace.IntervalReady;
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.True(workspace.CanLoadMoreEvidence);
        SessionExportResult whole = await workspace.ExportAsync(ExportFormat.Csv, at);
        Assert.True(whole.Context.Complete);
        Assert.Equal(DetailLevel.Evidence, whole.Context.Rung);
        Assert.Equal(rows.Length, whole.Rows);
        Assert.Contains("Every record of this scope is included.", whole.Context.Caveats);
        string[] csv = whole.Content.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(rows.Length + 1, csv.Length);
    }

    [Fact(DisplayName = "§3.7: a record whose content the capture kept says in the inspector how much of what it kept")]
    public async Task TheInspectorStatesKeptContent()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] rows = Rows();
        Publish(session.Store, rows, content: (ContentHeader(recordLimit: 4),
        [
            Content(rows[2], "ping"u8.ToArray(), 4),
            Content(rows[3], "pong-pong"u8.ToArray(), 4),
        ]));
        using WorkspaceViewModel workspace = Open(session);
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;

        string ContentOf(int index)
        {
            workspace.SelectedRung = workspace.RungRows[index];
            return workspace.SelectedEvidenceFields.Single(field => field.Label == "Content").Value;
        }

        Assert.Equal("Kept whole: 4 bytes of an application payload it sent, UTF-8 text.", ContentOf(2));
        Assert.Equal("Kept in part: the first 4 of 9 bytes of an application payload it received, UTF-8 text; the other 5 "
            + "were cut by the 4-byte record limit.", ContentOf(3));
        Assert.StartsWith("None. The kernel's network events", ContentOf(4), StringComparison.Ordinal);

        // A record with kept content offers the content viewer, on its button, its key and its spoken name (§3.7).
        workspace.SelectedRung = workspace.RungRows[3];
        Assert.True(workspace.HasSelectedContent);

        // Its size says what it measures in words, never as a code's name (R5).
        string size = workspace.SelectedEvidenceFields.Single(field => field.Label == "Size").Value;
        Assert.EndsWith(" B carried by the transport", size, StringComparison.Ordinal);
        Assert.DoesNotContain("TransportObserved", size, StringComparison.Ordinal);
        Assert.EndsWith("Press Enter to open the original record. Press C to inspect its content.",
            workspace.RungRows[3].SpokenName, StringComparison.Ordinal);
        workspace.SelectedRung = workspace.RungRows[4];
        Assert.False(workspace.HasSelectedContent);
        Assert.EndsWith("Press Enter to open the original record.", workspace.RungRows[4].SpokenName, StringComparison.Ordinal);
    }

    private static WorkspaceViewModel Open(TemporarySession session)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        return new(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
    }

    private static void DescendTo(WorkspaceViewModel workspace, string key)
    {
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == key);
        Assert.True(workspace.Descend());
    }

    private static void RemoveFilter(WorkspaceViewModel workspace, string field)
    {
        workspace.SelectedFilter = workspace.Filters.First(filter => filter.Field == field);
        Assert.True(workspace.RemoveSelectedFilter());
    }

    /// <summary>A client and a server exchanging over one paired connection, and one one-sided flow elsewhere.</summary>
    private static ObservationRowV1[] Rows() =>
    [
        Timed(Lifecycle(1, ObservationKind.Create, 100, 1)),
        Timed(Lifecycle(2, ObservationKind.Create, 200, 2)),
        .. Enumerable.Range(0, Exchanges).SelectMany(index => new[]
        {
            Timed(Transfer(10 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 64, 100,
                (ulong)(100 + (2 * index))).Between(ClientEnd, ServerEnd)),
            Timed(Transfer(11 + (2 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200,
                (ulong)(101 + (2 * index))).Between(ServerEnd, ClientEnd)),
        }),
        Timed(Transfer(4_000, ObservationKind.Send, AccountingSide.SendSide, 3, 300, 8_000)
            .Between("127.0.0.1:50001", "127.0.0.1:9090")),
    ];

    /// <summary>A row whose session time is its reading in workspace ticks, so the timeline and marks can place it.</summary>
    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };
}
