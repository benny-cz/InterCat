using System.Globalization;
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
/// §6.2's density cell carries the selected count or a compatible byte sum: under a byte ranking the machine rung's
/// mechanism lanes, a group's process lanes and a process's direction rows, with the machine row above them, plot the
/// bytes the ranking measures, read for the columns they draw, and a column whose records recorded no size is unmeasured,
/// never zero (R3). A rung whose rows cannot be counted says its timeline counts records.
/// </summary>
public sealed class TimelineBytesTests
{
    [Fact(DisplayName = "§6.2: under a byte ranking the machine rung's lanes plot the bytes it measures, an unmeasured column never as zero")]
    public void AByteRankingsLanesPlotItsBytes() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Traffic());
        using WorkspaceViewModel workspace = Open(session);
        workspace.RequestTimelineDetail(workspace.WholeSnapshot.Extent, 64);
        Assert.Null(workspace.TimelineBytes);
        Assert.StartsWith("Observed records by mechanism · 2 lanes · ", workspace.TimelineCaption, StringComparison.Ordinal);

        // The lanes' bytes are read off the UI thread; until they arrive the lanes plot records, and the caption says so.
        workspace.RankBy = RankingMetric.BytesSent;
        Assert.Null(workspace.TimelineBytes);
        Assert.StartsWith("Observed records by mechanism · reading bytes sent… · 2 lanes · ", workspace.TimelineCaption,
            StringComparison.Ordinal);
        await workspace.TimelineBytesReady;
        await workspace.RankingReady;
        TimelineByteLayer plotted = Assert.IsType<TimelineByteLayer>(workspace.TimelineBytes);
        Assert.Equal(RankingMetric.BytesSent, plotted.Metric);
        Assert.Null(plotted.Zoomed);
        Assert.StartsWith("Bytes sent per second by mechanism · 2 lanes · cross-hatched where no size was recorded · ",
            workspace.TimelineCaption, StringComparison.Ordinal);
        Assert.True(workspace.TimelineDrawsUnmeasured);
        Assert.True(workspace.DrawsUnmeasured);

        // Each lane's column holds its own records' bytes: TCP's sends, and nothing from the lifecycle's records.
        TimelineBucket At(Mechanism lane, long tick) => workspace.WholeSnapshot.MechanismLanes
            .Single(candidate => candidate.Mechanism == lane).Buckets.Single(bucket => bucket.Interval.StartTicks == tick);
        (long? Value, long Measured, long Unmeasured) Sent(Mechanism lane, long tick) =>
            workspace.TimelineBytes!.Of(lane, At(lane, tick).Interval)!.ValueOf(RankingMetric.BytesSent);
        Assert.Equal(((long?)500, 1L, 0L), Sent(Mechanism.Tcp, 10));
        Assert.Equal(((long?)0, 1L, 0L), Sent(Mechanism.Tcp, 13));
        Assert.Equal(((long?)null, 0L, 1L), Sent(Mechanism.Tcp, 14));
        Assert.Equal(((long?)null, 0L, 0L), Sent(Mechanism.ProcessLifecycle, 1));

        // A bar's card states what it plots, its rate against the busiest lane, and what recorded no size.
        List<string> Card(long tick) => [.. workspace.DescribeTimelineHover(At(Mechanism.Tcp, tick), 2_000, lane: Mechanism.Tcp).Lines];
        List<string> sent = Card(10);
        Assert.Contains("Basis: source observations · unit: bytes · domain: transport-observed bytes of TCP send records with a "
            + "session time · accounting: sender-accounted", sent);
        Assert.Contains($"Plotted: {WorkspaceRowBuilder.DescribeSize(500)} sent on 1 measured send", sent);
        Assert.Contains($"Rate: {WorkspaceRowBuilder.DescribeByteRate(500d * WorkspaceTime.TicksPerSecond)} · height against the "
            + $"busiest mechanism lane in this time view, {WorkspaceRowBuilder.DescribeByteRate(2_000)} (shared scale)", sent);
        Assert.Contains("Unmeasured: none; every send here recorded its size", sent);
        Assert.Contains($"Bytes: {WorkspaceRowBuilder.DescribeSize(500)} sent · no receive recorded", sent);
        Assert.Contains($"Resolution: the overview's {workspace.WholeSnapshot.Timeline.Count} buckets over the whole session", sent);
        Assert.Contains("Plotted: 0 B sent on 1 measured send", Card(13));
        List<string> blind = Card(14);
        Assert.Contains("Plotted: unmeasured, drawn cross-hatched · 1 send recorded no size, so the value is unknown, not zero", blind);
        Assert.Contains("Unmeasured: 1 send, none with a size", blind);
        Assert.Contains("Plotted: nothing · no send recorded in this interval", Card(20));

        // One read answers every byte ranking, so bytes received plots at once, and its receives all recorded a size.
        workspace.RankBy = RankingMetric.BytesReceived;
        Assert.Equal(RankingMetric.BytesReceived, workspace.TimelineBytes!.Metric);
        Assert.Same(plotted.Overview, workspace.TimelineBytes.Overview);
        Assert.False(workspace.TimelineDrawsUnmeasured);
        Assert.StartsWith("Bytes received per second by mechanism · 2 lanes · click", workspace.TimelineCaption, StringComparison.Ordinal);
        await workspace.RankingReady;

        // A zoomed view reads its own columns, which the cards and bars take where they lie.
        workspace.RankBy = RankingMetric.BytesSent;
        await workspace.RankingReady;
        workspace.RequestTimelineDetail(new TimeRange(10, 16), 3);
        await workspace.TimelineBytesReady;
        await workspace.TimelineDetailReady;
        SessionMechanismByteMeasures zoomed = Assert.IsType<SessionMechanismByteMeasures>(workspace.TimelineBytes!.Zoomed);
        Assert.Equal(new TimeRange(10, 16), zoomed.Interval);
        Assert.Equal(((long?)750, 2L, 0L), workspace.TimelineBytes.Of(Mechanism.Tcp, new TimeRange(10, 12))!.ValueOf(RankingMetric.BytesSent));
        Assert.Equal(((long?)null, 0L, 2L), workspace.TimelineBytes.Of(Mechanism.Tcp, new TimeRange(14, 16))!.ValueOf(RankingMetric.BytesSent));
        TimelineBucket fine = workspace.TimelineDetail!.MechanismLanes.Single(lane => lane.Mechanism == Mechanism.Tcp).Buckets[0];
        List<string> fineCard = [.. workspace.DescribeTimelineHover(fine, 2_000, lane: Mechanism.Tcp).Lines];
        Assert.Contains($"Plotted: {WorkspaceRowBuilder.DescribeSize(750)} sent on 2 measured sends", fineCard);
        Assert.Contains("Resolution: this view's own bytes, 3 buckets", fineCard);

        // The whole session again: the overview's columns answer it, and the zoomed ones are not drawn.
        workspace.RequestTimelineDetail(workspace.WholeSnapshot.Extent, 64);
        Assert.Null(workspace.TimelineBytes!.Zoomed);

        // Another rung's rows plot bytes of their own; back at the machine rung its lanes plot theirs again, unread.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "big.exe");
        Assert.True(workspace.Descend());
        Assert.Null(workspace.TimelineBytes);
        Assert.True(workspace.Ascend());
        Assert.Same(plotted.Overview, workspace.TimelineBytes!.Overview);

        // Records bring the record lanes back.
        workspace.RankBy = RankingMetric.Records;
        Assert.Null(workspace.TimelineBytes);
        Assert.False(workspace.DrawsUnmeasured);
        Assert.StartsWith("Observed records by mechanism · 2 lanes · ", workspace.TimelineCaption, StringComparison.Ordinal);
    });

    [Fact(DisplayName = "§6.2: under a byte ranking a group's process lanes, and the machine row above them, plot the bytes it measures")]
    public void AGroupsProcessLanesPlotItsBytes() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Traffic());
        using WorkspaceViewModel workspace = Open(session);
        workspace.RequestTimelineDetail(workspace.WholeSnapshot.Extent, 64);
        workspace.RankBy = RankingMetric.BytesSent;
        await workspace.TimelineBytesReady;
        await workspace.RankingReady;

        // big.exe's two processes, a lane each: their bytes are read once the lanes are counted, and said to be meanwhile.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "big.exe");
        Assert.True(workspace.Descend());
        await workspace.TimelineDetailReady;
        Assert.True(workspace.ShowsProcessLanes);
        Assert.Null(workspace.ProcessLaneBytes);
        Assert.Contains(" · 2 process lanes · reading bytes sent… · machine context above", workspace.TimelineCaption,
            StringComparison.Ordinal);
        await workspace.TimelineBytesReady;
        await workspace.RankingReady;
        ProcessLaneByteLayer plotted = Assert.IsType<ProcessLaneByteLayer>(workspace.ProcessLaneBytes);
        Assert.Equal(RankingMetric.BytesSent, plotted.Metric);
        Assert.Contains(" · 2 process lanes · bytes sent per second, cross-hatched where no size was recorded · machine context above",
            workspace.TimelineCaption, StringComparison.Ordinal);
        Assert.DoesNotContain("lanes count records", workspace.TimelineCaption, StringComparison.Ordinal);
        Assert.True(workspace.DrawsUnmeasured);

        // Each lane holds its own process's sends, and the machine row every record's: blind.exe's unsized one only there.
        ProcessNode first = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        ProcessNode second = workspace.Snapshot.Processes.Single(node => node.ProcessId == 101);
        TimelineBucket At(ProcessNode owner, long tick) => workspace.ProcessLaneDisplay.Single(lane => lane.ProcessId == owner.Id)
            .Buckets.Single(bucket => bucket.Interval.StartTicks == tick);
        (long? Value, long Measured, long Unmeasured) Sent(SessionIntervalByteMeasures lane, TimelineBucket bucket) =>
            lane.For(bucket.Interval)!.ValueOf(RankingMetric.BytesSent);
        Assert.Equal(((long?)500, 1L, 0L), Sent(plotted.Measures.Of(first.Id)!, At(first, 10)));
        Assert.Equal(((long?)null, 0L, 0L), Sent(plotted.Measures.Of(first.Id)!, At(first, 12)));
        Assert.Equal(((long?)1_000, 1L, 0L), Sent(plotted.Measures.Of(second.Id)!, At(second, 12)));
        TimelineBucket blind = workspace.WholeSnapshot.Timeline.Single(bucket => bucket.Interval.StartTicks == 14);
        Assert.Equal(((long?)null, 0L, 1L), Sent(plotted.Measures.Machine, blind));

        // A lane's card: its process's own sends, the hue its records give it, and the scale it shares with the machine row.
        List<string> lane = [.. workspace.DescribeTimelineHover(At(first, 10), 2_000, ownerLane: first).Lines];
        Assert.Contains($"1 observed record · {first.NameWithPid} lane · mostly TCP records", lane);
        Assert.Contains("Basis: source observations · unit: bytes · domain: transport-observed bytes of send records with a "
            + $"session time canonically owned by {first.NameWithPid} · accounting: sender-accounted", lane);
        Assert.Contains($"Plotted: {WorkspaceRowBuilder.DescribeSize(500)} sent on 1 measured send", lane);
        Assert.Contains($"Rate: {WorkspaceRowBuilder.DescribeByteRate(500d * WorkspaceTime.TicksPerSecond)} · height against the "
            + $"busiest visible lane including machine context, {WorkspaceRowBuilder.DescribeByteRate(2_000)} (shared scale)", lane);
        Assert.Contains($"Resolution: the lanes' own bytes, {workspace.ProcessLaneDisplay[0].Buckets.Count} buckets", lane);

        // The machine row's card: every record's sends, which the lanes are not added up to.
        List<string> machine = [.. workspace.DescribeTimelineHover(blind, 2_000).Lines];
        Assert.Contains("1 observed record · machine context, not added to the lanes", machine);
        Assert.Contains("Plotted: unmeasured, drawn cross-hatched · 1 send recorded no size, so the value is unknown, not zero", machine);
        Assert.Contains("Basis: source observations · unit: bytes · domain: transport-observed bytes of every send record with a "
            + "session time · accounting: sender-accounted", machine);

        // Records bring the record lanes back.
        workspace.RankBy = RankingMetric.Records;
        Assert.Null(workspace.ProcessLaneBytes);
        Assert.DoesNotContain("per second", workspace.TimelineCaption, StringComparison.Ordinal);
        await workspace.RankingReady;
    });

    [Fact(DisplayName = "§6.2: under a byte ranking a process's direction rows, and the machine row above them, plot the bytes it measures")]
    public void AProcesssDirectionRowsPlotItsBytes() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Traffic());
        using WorkspaceViewModel workspace = Open(session);
        workspace.RequestTimelineDetail(workspace.WholeSnapshot.Extent, 64);
        workspace.RankBy = RankingMetric.BytesSent;
        await workspace.TimelineBytesReady;
        await workspace.RankingReady;

        // Down to big.exe's PID 100, whose rows split its records by the direction their source states.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "big.exe");
        Assert.True(workspace.Descend());
        await workspace.TimelineDetailReady;
        ProcessNode sender = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == sender.Id.ToString());
        Assert.True(workspace.Descend());
        await workspace.TimelineDetailReady;
        Assert.True(workspace.ShowsDirectionLanes);
        Assert.Null(workspace.DirectionLaneBytes);
        Assert.Contains(" · by source direction · reading bytes sent… · machine context above", workspace.TimelineCaption,
            StringComparison.Ordinal);
        await workspace.TimelineBytesReady;
        await workspace.RankingReady;
        DirectionLaneByteLayer plotted = Assert.IsType<DirectionLaneByteLayer>(workspace.DirectionLaneBytes);
        Assert.Contains(" · by source direction · bytes sent per second, cross-hatched where no size was recorded · machine "
            + "context above", workspace.TimelineCaption, StringComparison.Ordinal);
        Assert.DoesNotContain("counts records here", workspace.TimelineCaption, StringComparison.Ordinal);

        // The outbound row holds the process's sends, the inbound row none of them, and the machine row every record's.
        TimelineBucket At(Direction direction, long tick) => workspace.TimelineDirectionLanes!
            .Single(lane => lane.Direction == direction).Buckets.Single(bucket => bucket.Interval.StartTicks == tick);
        (long? Value, long Measured, long Unmeasured) Sent(SessionIntervalByteMeasures row, TimelineBucket bucket) =>
            row.For(bucket.Interval)!.ValueOf(RankingMetric.BytesSent);
        Assert.Equal(((long?)500, 1L, 0L), Sent(plotted.Measures.Of(Direction.Outbound)!, At(Direction.Outbound, 10)));
        Assert.Equal(((long?)null, 0L, 0L), Sent(plotted.Measures.Of(Direction.Inbound)!, At(Direction.Inbound, 10)));
        TimelineBucket blind = workspace.WholeSnapshot.Timeline.Single(bucket => bucket.Interval.StartTicks == 14);
        Assert.Equal(((long?)null, 0L, 1L), Sent(plotted.Measures.Machine, blind));

        // A row's card: the process's own sends of that direction, and what the direction means.
        List<string> row = [.. workspace.DescribeTimelineHover(At(Direction.Outbound, 10), 2_000, directionLane: Direction.Outbound).Lines];
        Assert.Contains($"1 observed record · {WorkspaceViewModel.DirectionLabel(Direction.Outbound)} lane", row);
        Assert.Contains("Basis: source observations · unit: bytes · domain: transport-observed bytes of send records with a "
            + $"session time canonically owned by {sender.NameWithPid}, marked outbound · accounting: sender-accounted", row);
        Assert.Contains($"Plotted: {WorkspaceRowBuilder.DescribeSize(500)} sent on 1 measured send", row);
        Assert.Contains(row, line => line.StartsWith("Direction: outbound, as the source marks sends", StringComparison.Ordinal));
        Assert.Contains("1 observed record · machine context, not added to the lanes",
            workspace.DescribeTimelineHover(blind, 2_000).Lines);

        // Records bring the record rows back.
        workspace.RankBy = RankingMetric.Records;
        Assert.Null(workspace.DirectionLaneBytes);
        Assert.DoesNotContain("per second", workspace.TimelineCaption, StringComparison.Ordinal);
        await workspace.RankingReady;
    });

    [Fact(DisplayName = "§6.2: a later publication keeps plotting the earlier one's lane bytes until its own are read")]
    public void ALaterPublicationKeepsTheEarlierLaneBytes() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Traffic());
        using WorkspaceViewModel first = Open(session);
        first.RankBy = RankingMetric.BytesSent;
        await first.TimelineBytesReady;
        await first.RankingReady;
        SessionMechanismByteMeasures shown = first.TimelineBytes!.Overview;

        // The next view stands the earlier bytes in at once, rather than blinking back to records, then reads its own.
        using WorkspaceViewModel next = Open(session);
        next.RankBy = RankingMetric.BytesSent;
        next.AdoptTimeline(first.CarryTimeline());
        Assert.Same(shown, next.TimelineBytes!.Overview);
        Assert.StartsWith("Bytes sent per second by mechanism · ", next.TimelineCaption, StringComparison.Ordinal);
        await next.TimelineBytesReady;
        await next.RankingReady;
        Assert.NotSame(shown, next.TimelineBytes!.Overview);
        Assert.True(shown.Of(Mechanism.Tcp)!.Columns.SequenceEqual(next.TimelineBytes.Overview.Of(Mechanism.Tcp)!.Columns));

        // Another session's bytes never stand in, and a view whose session on disk turns out to be another one says why
        // its lanes still plot records.
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var elsewhere = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, Guid.NewGuid(), overview.Generation));
        elsewhere.RankBy = RankingMetric.BytesSent;
        elsewhere.AdoptTimeline(first.CarryTimeline());
        Assert.Null(elsewhere.TimelineBytes);
        await elsewhere.TimelineBytesReady;
        await elsewhere.RankingReady;
        Assert.Null(elsewhere.TimelineBytes);
        Assert.StartsWith("Observed records by mechanism · bytes sent could not be read: the session on disk is another one · 2 lanes",
            elsewhere.TimelineCaption, StringComparison.Ordinal);

        // Its group's lanes cannot be counted either, so the timeline says it counts records there, not bytes.
        elsewhere.RequestTimelineDetail(elsewhere.WholeSnapshot.Extent, 64);
        elsewhere.SelectedRung = elsewhere.RungRows.Single(row => row.Label == "big.exe");
        Assert.True(elsewhere.Descend());
        await elsewhere.TimelineDetailReady;
        Assert.False(elsewhere.ShowsProcessLanes);
        Assert.EndsWith(" could not be counted: the session on disk is another one. Every observed record is shown. · the "
            + "timeline counts records here, not bytes", elsewhere.TimelineCaption, StringComparison.Ordinal);
        await elsewhere.RankingReady;
    });

    [Fact(DisplayName = "§6.2: a finished session's lanes under a byte ranking plot the bytes its persisted overview kept, read from no segment")]
    public void AFinishedSessionsLanesPlotItsKeptBytes() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Traffic());
        using (WorkspaceViewModel live = Open(session))
        {
            // While no checkpoint is published, the lanes' bytes are read from the segments.
            live.RankBy = RankingMetric.BytesSent;
            await live.TimelineBytesReady;
            Assert.False(live.TimelineBytes!.Overview.FromPersistedOverview);
        }

        Assert.Equal(CheckpointOutcome.Published,
            SessionCheckpoints.Publish(session.Store, DateTimeOffset.Parse("2026-10-04T12:00:00Z", CultureInfo.InvariantCulture)).Outcome);

        // The checkpoint is a generation of its own, so this workspace derives nothing the first one held.
        using WorkspaceViewModel workspace = Open(session);
        workspace.RankBy = RankingMetric.BytesSent;
        await workspace.TimelineBytesReady;
        await workspace.RankingReady;

        // The window asks for the overview's own columns, which the finished session kept: the same bytes, and no read.
        TimelineByteLayer plotted = Assert.IsType<TimelineByteLayer>(workspace.TimelineBytes);
        Assert.True(plotted.Overview.FromPersistedOverview);
        TimelineBucket At(long tick) => workspace.WholeSnapshot.MechanismLanes
            .Single(candidate => candidate.Mechanism == Mechanism.Tcp).Buckets.Single(bucket => bucket.Interval.StartTicks == tick);
        Assert.Equal(((long?)500, 1L, 0L), plotted.Of(Mechanism.Tcp, At(10).Interval)!.ValueOf(RankingMetric.BytesSent));
        Assert.Equal(((long?)0, 1L, 0L), plotted.Of(Mechanism.Tcp, At(13).Interval)!.ValueOf(RankingMetric.BytesSent));
        Assert.Equal(((long?)null, 0L, 1L), plotted.Of(Mechanism.Tcp, At(14).Interval)!.ValueOf(RankingMetric.BytesSent));
        Assert.True(workspace.TimelineDrawsUnmeasured);
    });

    private static WorkspaceViewModel Open(TemporarySession session)
    {
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        return new(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
    }

    /// <summary>
    /// Four executables: big.exe's two processes send 750 and 1,000 bytes, zero.exe sends an empty message, blind.exe
    /// sends twice without recording a size, and listen.exe only receives, six times.
    /// </summary>
    private static ObservationRowV1[] Traffic() =>
    [
        Timed(Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\big.exe" }),
        Timed(Lifecycle(2, ObservationKind.Create, 101, 2) with { ResourceName = @"C:\Tools\big.exe" }),
        Timed(Lifecycle(3, ObservationKind.Create, 200, 3) with { ResourceName = @"C:\Tools\zero.exe" }),
        Timed(Lifecycle(4, ObservationKind.Create, 300, 4) with { ResourceName = @"C:\Tools\blind.exe" }),
        Timed(Lifecycle(5, ObservationKind.Create, 400, 5) with { ResourceName = @"C:\Tools\listen.exe" }),
        Timed(Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 500, 100, 10)),
        Timed(Transfer(11, ObservationKind.Send, AccountingSide.SendSide, 250, 100, 11)),
        Timed(Transfer(12, ObservationKind.Send, AccountingSide.SendSide, 1_000, 101, 12)),
        Timed(Transfer(13, ObservationKind.Send, AccountingSide.SendSide, 0, 200, 13)),
        Timed(Transfer(14, ObservationKind.Send, AccountingSide.SendSide, null, 300, 14)),
        Timed(Transfer(15, ObservationKind.Send, AccountingSide.SendSide, null, 300, 15)),
        .. Enumerable.Range(0, 6).Select(index =>
            Timed(Transfer(20 + index, ObservationKind.Receive, AccountingSide.ReceiveSide, 32, 400, (ulong)(20 + index)))),
    ];

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };
}
