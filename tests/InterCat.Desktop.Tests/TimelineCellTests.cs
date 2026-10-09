using System.Globalization;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>
/// A timeline cell's own explanation (§6.8: every visual claim is one action from the rule, version and coverage behind
/// it). A click on a cell makes it the analysis interval, explained beneath the time scope as a cell of its lane - which
/// of its lane's records it counts and by which rule, what its bar plots, the column it was counted in and the capture's
/// coverage over it - and an interval a step or the interval table chose is explained as a cell of the lane they follow.
/// </summary>
public sealed class TimelineCellTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";
    private const int Exchanges = 40;
    private const string Count = " Its bar plots their count per second.";
    private const string Placement = " Each record counts in the one column whose half-open interval holds its session time, here ";
    private const string Unplaced = "; a record without a usable session time counts in none.";
    private const string Unknown = " Coverage over the cell: unknown, so it may hold fewer records than happened, and it is drawn hatched.";

    [Fact(DisplayName = "§6.8: a cell chosen in a mechanism's lane says which records it counts, what its bar plots, its column and its coverage, until another interval or lane is chosen")]
    public void AMechanismLanesCellIsExplained() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);
        Assert.True(workspace.ShowsMechanismLanes);
        string overview = string.Create(CultureInfo.CurrentCulture,
            $"one of the overview's {workspace.Snapshot.Timeline.Count:N0} over the whole session");
        IReadOnlyList<TimelineBucket> tcp = Lane(workspace, Mechanism.Tcp);
        TimelineBucket first = tcp.First(bucket => bucket.ObservationCount > 1);
        Assert.False(workspace.HasCellExplanation);
        Assert.Equal("Click makes it the analysis interval, explained in the inspector · Shift+drag brushes a range",
            workspace.DescribeTimelineHover(first, 1, lane: Mechanism.Tcp).Lines[^1]);

        // A click on a TCP cell makes its interval the analysis interval, explained as a cell of the TCP lane.
        workspace.ChooseTimelineCell(first, Mechanism.Tcp);
        Assert.Equal("This bucket is the analysis interval", workspace.DescribeTimelineHover(first, 1, lane: Mechanism.Tcp).Lines[^1]);
        Assert.Equal(first.Interval, workspace.SelectedInterval);
        Assert.Equal(Counted(first.ObservationCount, "TCP record") + $" {Have(first.ObservationCount)} a session time in this interval: every TCP record, "
            + "whichever process it is bound to." + Count + Placement + overview + Unplaced + Unknown, workspace.CellExplanation);
        Assert.True(workspace.HasCellExplanation);

        // The same interval chosen in another lane is explained as that lane's cell: the creations in it, or none.
        TimelineBucket created = Lane(workspace, Mechanism.ProcessLifecycle).Single(bucket => bucket.Interval == first.Interval);
        workspace.ChooseTimelineCell(created, Mechanism.ProcessLifecycle);
        Assert.StartsWith(created.ObservationCount == 0
            ? "No process lifecycle record has a session time in this interval."
            : Counted(created.ObservationCount, "process lifecycle record"), workspace.CellExplanation, StringComparison.Ordinal);

        // A brushed range is no cell. An interval a step or the interval table chose is a cell of the lane they follow, the
        // selected one; with none selected, the machine rung, which draws its mechanisms' lanes and no machine row, has no
        // cell to explain.
        workspace.SelectInterval(new TimeRange(first.Interval.StartTicks, first.Interval.EndTicks + 1));
        Assert.False(workspace.HasCellExplanation);
        TimelineBucket next = tcp.First(bucket => bucket.ObservationCount > 0 && bucket.Interval.StartTicks >= first.Interval.EndTicks);
        workspace.SelectInterval(next.Interval);
        Assert.Equal(string.Empty, workspace.CellExplanation);
        workspace.SelectTimelineLane(Mechanism.Tcp);
        Assert.StartsWith(Counted(next.ObservationCount, "TCP record") + $" {Have(next.ObservationCount)} a session time in this interval",
            workspace.CellExplanation, StringComparison.Ordinal);

        // A click in another lane explains its cell, until another lane is selected.
        workspace.ChooseTimelineCell(Lane(workspace, Mechanism.ProcessLifecycle).Single(bucket => bucket.Interval == next.Interval),
            Mechanism.ProcessLifecycle);
        Assert.Contains("process lifecycle record", workspace.CellExplanation, StringComparison.Ordinal);
        workspace.SelectTimelineLane(null);
        workspace.SelectTimelineLane(Mechanism.Tcp);
        Assert.StartsWith(Counted(next.ObservationCount, "TCP record"), workspace.CellExplanation, StringComparison.Ordinal);

        // Zoomed, a cell is one of the view's own columns.
        TimeRange extent = workspace.Snapshot.Extent;
        workspace.RequestTimelineDetail(new TimeRange(extent.StartTicks, extent.StartTicks + (extent.SpanTicks / 2)), 40);
        await workspace.TimelineDetailReady;
        TimelineBucket fine = workspace.TimelineDetail!.MechanismLanes.Single(lane => lane.Mechanism == Mechanism.Tcp).Buckets
            .First(bucket => bucket.ObservationCount > 0);
        workspace.ChooseTimelineCell(fine, Mechanism.Tcp);
        Assert.Contains(" here one of this view's own 40;", workspace.CellExplanation, StringComparison.Ordinal);

        // Under a byte ranking the lanes plot bytes, and the cell says what its bar plots.
        workspace.ChooseTimelineCell(first, Mechanism.Tcp);
        workspace.RankBy = RankingMetric.BytesSent;
        await workspace.TimelineBytesReady;
        Assert.NotNull(workspace.TimelineBytes);
        Assert.Contains(" Its bar plots their bytes sent per second, from those that recorded a size.",
            workspace.CellExplanation, StringComparison.Ordinal);

        // The tour was derived by no rule, and explains no cell.
        using var tour = new WorkspaceViewModel(SyntheticWorkspace.Create(), "synthetic-tour-v1");
        TimelineBucket toured = tour.Snapshot.Timeline.First(bucket => bucket.ObservationCount > 0);
        tour.ChooseTimelineCell(toured);
        Assert.Equal(toured.Interval, tour.SelectedInterval);
        Assert.False(tour.HasCellExplanation);
    });

    [Fact(DisplayName = "§6.8: a zoom carried into a publication that draws a new lane explains no cell of it, since the lanes draw the overview until their own count arrives")]
    public void ACarriedZoomMissingALaneExplainsNoCell() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel first = Open(session);
        TimeRange extent = first.Snapshot.Extent;
        var zoom = new TimeRange(extent.StartTicks, extent.StartTicks + (extent.SpanTicks / 2));
        first.RequestTimelineDetail(zoom, 40);
        await first.TimelineDetailReady;
        first.SelectTimelineLane(Mechanism.Tcp);
        TimelineBucket fine = first.TimelineDetail!.MechanismLanes.Single(lane => lane.Mechanism == Mechanism.Tcp).Buckets
            .First(bucket => bucket.ObservationCount > 0);
        first.ChooseTimelineCell(fine, Mechanism.Tcp);
        Assert.Contains(" here one of this view's own 40;", first.CellExplanation, StringComparison.Ordinal);

        // The next publication draws a UDP lane the carried zoom never counted: until its own zoom arrives the lanes draw
        // the overview's columns, of which the chosen interval is none, so it is explained as no cell.
        Publish(session.Store, [Timed(Transfer(400, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 900)
            .Between(ClientEnd, ServerEnd) with { Mechanism = Mechanism.Udp })]);
        using WorkspaceViewModel next = Open(session);
        Assert.Contains(next.Snapshot.MechanismLanes, lane => lane.Mechanism == Mechanism.Udp);
        Assert.Null(next.RestoreNavigation(first.CaptureNavigation()));
        next.AdoptTimeline(first.CarryTimeline());
        Assert.Equal(fine.Interval, next.SelectedInterval);
        Assert.False(next.HasCellExplanation);

        // Its own zoom counts every lane, and the cell is explained again.
        next.RequestTimelineDetail(zoom, 40);
        await next.TimelineDetailReady;
        Assert.Contains(" here one of this view's own 40;", next.CellExplanation, StringComparison.Ordinal);
    });

    [Fact(DisplayName = "§6.8: a cell chosen in a group's process lane, counted coarser than the view, is the analysis interval and names its owner, the rule that bound its records and the columns it was counted in")]
    public void AProcessLanesCellIsExplained() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Pool(40));
        using WorkspaceViewModel workspace = Open(session);
        string group = workspace.Snapshot.Processes.First(node => node.ProcessId == 2_000).GroupKey;
        TimeRange extent = workspace.Snapshot.Extent;
        DescendTo(workspace, group);

        // Zoomed in at 1,000 columns, the forty lanes are counted in 500, each cell two of the machine row's columns wide.
        workspace.RequestTimelineDetail(new TimeRange(extent.StartTicks + 1, extent.EndTicks), 1_000);
        await workspace.TimelineDetailReady;
        ProcessTimelineLane lane = workspace.ProcessLaneDisplay[0];
        TimelineBucket cell = lane.Buckets.First(bucket => bucket.ObservationCount > 0);
        ProcessNode owner = workspace.Snapshot.Processes.Single(node => node.Id == lane.ProcessId);
        Assert.DoesNotContain(workspace.TimelineDetail!.Buckets, bucket => bucket.Interval == cell.Interval);

        // The cell itself becomes the analysis interval, not the machine column beneath it, and its card says so.
        workspace.ChooseTimelineCell(cell, ownerLane: owner);
        Assert.Equal(cell.Interval, workspace.SelectedInterval);
        Assert.Equal("This bucket is the analysis interval", workspace.DescribeTimelineHover(cell, 1, ownerLane: owner).Lines[^1]);
        string cells = 20_000.ToString("N0", CultureInfo.CurrentCulture);
        Assert.Equal(Counted(cell.ObservationCount, "record") + $" {Have(cell.ObservationCount)} a session time in this interval "
            + $"and {Is(cell.ObservationCount)} bound to {owner.NameWithPid}, {Its(cell.ObservationCount)} canonical owner, by "
            + "process-binding, version 4." + Count
            + Placement + $"one of the lanes' own 500, coarser than the view's so the group's 40 lanes stay within {cells} cells"
            + Unplaced + Unknown, workspace.CellExplanation);

        // Another process selected is the lane a step and the interval table follow, and its cell is explained instead.
        ProcessTimelineLane other = workspace.ProcessLaneDisplay[1];
        ProcessNode second = workspace.Snapshot.Processes.Single(node => node.Id == other.ProcessId);
        workspace.SelectProcess(second.Id);
        Assert.Equal(cell.Interval, workspace.SelectedInterval);

        // Its lane holds none of its sends there: an empty cell draws no bar, and is no proof of inactivity.
        Assert.Equal(0, other.Buckets.Single(bucket => bucket.Interval == cell.Interval).ObservationCount);
        Assert.Equal($"No record bound to {second.NameWithPid} has a session time in this interval." + Placement
            + $"one of the lanes' own 500, coarser than the view's so the group's 40 lanes stay within {cells} cells" + Unplaced
            + " Coverage over the cell: unknown, so an empty cell is not proof of inactivity, and it is drawn hatched.",
            workspace.CellExplanation);

        // A cell is a place in the rung's lanes, which the rung left takes with it, though the interval stays.
        workspace.SelectProcess(owner.Id);
        workspace.ChooseTimelineCell(cell, ownerLane: owner);
        Assert.Equal(new TimelineCellLane(Owner: owner.Id), workspace.CaptureNavigation().ExplainedCell);
        DescendTo(workspace, owner.Id.ToString());
        Assert.Equal(cell.Interval, workspace.SelectedInterval);
        Assert.Null(workspace.CaptureNavigation().ExplainedCell);
        Assert.DoesNotContain("lanes' own", workspace.CellExplanation, StringComparison.Ordinal);
    });

    [Fact(DisplayName = "§6.8: a cell of a process's source-direction row and of a channel's end says which records it holds, the rule that bound or paired them, and is kept by a publication")]
    public void DirectionAndEndCellsAreExplained() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        Channel channel = workspace.Snapshot.Channels.Single();
        workspace.RequestTimelineDetail(workspace.Snapshot.Extent, 80);
        DescendTo(workspace, client.GroupKey);
        DescendTo(workspace, client.Id.ToString());
        await workspace.TimelineDetailReady;

        // The view asks for the whole session in more columns than the overview holds, so it is counted in its own.
        string view = Placement + string.Create(CultureInfo.CurrentCulture,
            $"one of this view's own {workspace.TimelineDetail!.Buckets.Count:N0}") + Unplaced + Unknown;

        // The client's sends are its outbound row's, bound to it by the binding rule, and say what outbound means.
        TimelineBucket sent = workspace.TimelineDirectionLanes!.Single(lane => lane.Direction == Direction.Outbound).Buckets
            .First(bucket => bucket.ObservationCount > 0);
        workspace.ChooseTimelineCell(sent, directionLane: Direction.Outbound);
        Assert.Equal(Counted(sent.ObservationCount, "record") + $" marked outbound {Have(sent.ObservationCount)} a session time "
            + $"in this interval and {Is(sent.ObservationCount)} bound to {client.NameWithPid}, {Its(sent.ObservationCount)} "
            + "canonical owner, by process-binding, version 4. The source marks sends, connection attempts and client calls "
            + "outbound, which does not say who initiated the conversation." + Count + view, workspace.CellExplanation);

        // A publication of the same session keeps the cell, chosen in the same row.
        WorkspaceNavigationMemento saved = workspace.CaptureNavigation();
        Assert.Equal(new TimelineCellLane(Direction: Direction.Outbound), saved.ExplainedCell);
        using (WorkspaceViewModel next = Open(session))
        {
            next.RequestTimelineDetail(next.Snapshot.Extent, 80);
            Assert.Null(next.RestoreNavigation(saved));
            await next.TimelineDetailReady;
            Assert.Equal(workspace.CellExplanation, next.CellExplanation);
        }

        // Another row chosen is the one explained, and the rung left takes the chosen cell with it.
        workspace.SelectDirectionLane(Direction.DirectionNotApplicable);
        Assert.Contains(" with no data direction ", workspace.CellExplanation, StringComparison.Ordinal);
        Assert.DoesNotContain("marked outbound", workspace.CellExplanation, StringComparison.Ordinal);
        workspace.SelectDirectionLane(null);
        workspace.ChooseTimelineCell(sent, directionLane: Direction.Outbound);
        Assert.NotNull(workspace.CaptureNavigation().ExplainedCell);
        Assert.True(workspace.Ascend());
        Assert.Null(workspace.CaptureNavigation().ExplainedCell);
        DescendTo(workspace, client.Id.ToString());
        await workspace.TimelineDetailReady;

        // A channel's end holds the records made there, split by direction, and its ends were paired by the relation rule.
        DescendTo(workspace, channel.Key);
        await workspace.TimelineDetailReady;
        ChannelEndTimelineLane end = workspace.TimelineChannelEndLanes!.Single(candidate => candidate.Endpoint == ClientEnd);
        int at = Enumerable.Range(0, end.Buckets.Count).First(index => end.Buckets[index].ObservationCount > 0);
        TimelineBucket made = end.Buckets[at];
        workspace.ChooseTimelineCell(made, endLane: end);
        Assert.Equal(Counted(made.ObservationCount, "record") + $" made at {ClientEnd}, the end of this paired TCP channel that "
            + $"{client.NameWithPid} holds, {Have(made.ObservationCount)} a session time in this interval: "
            + string.Create(CultureInfo.CurrentCulture, $"{end.Outbound[at].ObservationCount:N0} outbound, ")
            + string.Create(CultureInfo.CurrentCulture, $"{end.Inbound[at].ObservationCount:N0} inbound and ")
            + string.Create(CultureInfo.CurrentCulture,
                $"{made.ObservationCount - end.Outbound[at].ObservationCount - end.Inbound[at].ObservationCount:N0} with no data direction.")
            + " The channel's ends were paired by transport-endpoint-relation, version 4." + Count + view, workspace.CellExplanation);

        // The other end chosen is the one explained.
        workspace.SelectChannelEnd(end.End == 0 ? 1 : 0);
        Assert.Contains($" made at {ServerEnd}, ", workspace.CellExplanation, StringComparison.Ordinal);
        workspace.SelectChannelEnd(null);

        // The machine row above them is the machine's own count of the whole session, beside the rung's focus.
        Assert.Equal(workspace.Snapshot.Extent, workspace.TimelineDetail!.Interval);
        TimelineBucket machine = workspace.TimelineDetail.Buckets.Single(bucket => bucket.Interval == made.Interval);
        workspace.ChooseTimelineCell(machine);
        Assert.StartsWith(Counted(machine.ObservationCount, "record") + " of any mechanism "
            + $"{Have(machine.ObservationCount)} a session time in this interval: the machine's own count, which no binding "
            + "or pairing rule narrows. Paired TCP channel", workspace.CellExplanation, StringComparison.Ordinal);
    });

    [Fact(DisplayName = "§6.4: E from a chosen timeline cell lists exactly its records, behind a filter whose removal widens it, as icat evidence lists them")]
    public void EvidenceFromAChosenCellIsItsRecords() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        using WorkspaceViewModel workspace = Open(session);
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        Channel channel = workspace.Snapshot.Channels.Single();
        workspace.RequestTimelineDetail(workspace.Snapshot.Extent, 80);
        await workspace.TimelineDetailReady;

        // A TCP cell: the card names its records, and E lists exactly them, behind a filter naming the mechanism.
        TimelineBucket tcp = Lane(workspace, Mechanism.Tcp).First(bucket => bucket.ObservationCount > 1);
        workspace.ChooseTimelineCell(tcp, Mechanism.Tcp);
        Assert.Equal("Records E lists", workspace.EvidenceHeading);
        Assert.EndsWith(" · TCP records only", workspace.EvidenceSummary, StringComparison.Ordinal);
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.Equal("Mechanism: TCP", Assert.Single(workspace.Filters, filter => filter.Field == EvidenceScopes.MechanismField).Chip);
        Assert.EndsWith(" · TCP records only", workspace.EvidenceScopeText, StringComparison.Ordinal);
        Assert.Equal(Ticks(SessionEvidenceQuery.ReadScope(session.Store, 1_000, interval: tcp.Interval, mechanism: Mechanism.Tcp)),
            workspace.EvidenceMarkTicks.Order());
        Assert.Equal(tcp.ObservationCount, workspace.EvidenceMarkTicks.Count);

        // The rung's timeline counts what E lists, the mechanism's records, in the cell as in every column; the filter
        // removed, E lists every record of the interval again.
        await workspace.TimelineDetailReady;
        Assert.Equal(tcp.ObservationCount,
            workspace.TimelineFocusBuckets!.Single(bucket => bucket.Interval == tcp.Interval).ObservationCount);
        Assert.Equal(Lane(workspace, Mechanism.Tcp).Sum(bucket => bucket.ObservationCount),
            workspace.TimelineFocusBuckets!.Sum(bucket => bucket.ObservationCount));
        workspace.SelectedFilter = workspace.Filters.Single(filter => filter.Field == EvidenceScopes.MechanismField);
        Assert.True(workspace.RemoveSelectedFilter());
        await workspace.EvidenceReady;
        Assert.Equal(Ticks(SessionEvidenceQuery.ReadScope(session.Store, 1_000, interval: tcp.Interval)), workspace.EvidenceMarkTicks.Order());
        workspace.ReturnTo(0);

        // A brushed range is no cell: E from it lists the rung's records, whichever lane is selected.
        workspace.SelectTimelineLane(Mechanism.Tcp);
        workspace.SelectInterval(new TimeRange(tcp.Interval.StartTicks, tcp.Interval.EndTicks + 1));
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.DoesNotContain(workspace.Filters, filter => filter.Field == EvidenceScopes.MechanismField);
        workspace.ReturnTo(0);
        workspace.SelectTimelineLane(null);

        // A process's outbound row: its records marked outbound.
        DescendTo(workspace, client.GroupKey);
        DescendTo(workspace, client.Id.ToString());
        await workspace.TimelineDetailReady;
        TimelineBucket sent = workspace.TimelineDirectionLanes!.Single(lane => lane.Direction == Direction.Outbound).Buckets
            .First(bucket => bucket.ObservationCount > 0);
        workspace.ChooseTimelineCell(sent, directionLane: Direction.Outbound);
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.EndsWith(" · records marked outbound only", workspace.EvidenceScopeText, StringComparison.Ordinal);
        Assert.Equal(Ticks(SessionEvidenceQuery.ReadScope(session.Store, 1_000, interval: sent.Interval, ownerProcesses: [client.Id],
            direction: Direction.Outbound)), workspace.EvidenceMarkTicks.Order());
        Assert.Equal(sent.ObservationCount, workspace.EvidenceMarkTicks.Count);
        Assert.True(workspace.Ascend());

        // A channel's end: the records made there.
        DescendTo(workspace, channel.Key);
        await workspace.TimelineDetailReady;
        ChannelEndTimelineLane end = workspace.TimelineChannelEndLanes!.Single(candidate => candidate.Endpoint == ServerEnd);
        TimelineBucket made = end.Buckets.First(bucket => bucket.ObservationCount > 0);
        workspace.ChooseTimelineCell(made, endLane: end);
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.Equal($"End: {ServerEnd}", Assert.Single(workspace.Filters, filter => filter.Field == EvidenceScopes.EndField).Chip);
        Assert.Equal(Ticks(SessionEvidenceQuery.ReadScope(session.Store, 1_000, channel.Key, made.Interval, end: end.End)),
            workspace.EvidenceMarkTicks.Order());
        Assert.Equal(made.ObservationCount, workspace.EvidenceMarkTicks.Count);
        Assert.True(workspace.Ascend());

        // The machine row's cell draws the rung's own records over the machine's: E lists the rung's own, as before.
        TimelineBucket context = workspace.Snapshot.Timeline.First(bucket => bucket.ObservationCount > 0);
        workspace.ChooseTimelineCell(context);
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.DoesNotContain(workspace.Filters, filter => filter.Field is EvidenceScopes.MechanismField or EvidenceScopes.DirectionField
            or EvidenceScopes.EndField);
        Assert.StartsWith("Paired TCP channel", workspace.EvidenceScopeText, StringComparison.Ordinal);
    });

    [Fact(DisplayName = "§6.4: E from a cell of a group's process lane lists that process's records in it, not the group's")]
    public void EvidenceFromAProcessLanesCellIsItsProcesss() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Pool(40));
        using WorkspaceViewModel workspace = Open(session);
        string group = workspace.Snapshot.Processes.First(node => node.ProcessId == 2_000).GroupKey;
        TimeRange extent = workspace.Snapshot.Extent;
        DescendTo(workspace, group);
        workspace.RequestTimelineDetail(new TimeRange(extent.StartTicks + 1, extent.EndTicks), 1_000);
        await workspace.TimelineDetailReady;
        ProcessTimelineLane lane = workspace.ProcessLaneDisplay[0];
        TimelineBucket cell = lane.Buckets.First(bucket => bucket.ObservationCount > 0);
        ProcessNode owner = workspace.Snapshot.Processes.Single(node => node.Id == lane.ProcessId);
        workspace.ChooseTimelineCell(cell, ownerLane: owner);
        Assert.StartsWith($"Records owned by {owner.NameWithPid}", workspace.EvidenceSummary, StringComparison.Ordinal);
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.StartsWith($"Records owned by {owner.NameWithPid}", workspace.EvidenceScopeText, StringComparison.Ordinal);
        Assert.Equal(cell.ObservationCount, workspace.EvidenceMarkTicks.Count);
        Assert.Equal(Ticks(SessionEvidenceQuery.ReadScope(session.Store, 1_000, interval: cell.Interval, ownerProcesses: [owner.Id])),
            workspace.EvidenceMarkTicks.Order());
    });

    private static long[] Ticks(SessionEvidencePage page) =>
        [.. page.Records.Select(record => record.Observation.SessionRelativeTicks!.Value / 100).Order()];

    private static string Counted(int count, string noun) =>
        string.Create(CultureInfo.CurrentCulture, $"{count:N0} {noun}{(count == 1 ? string.Empty : "s")}");

    private static string Have(int count) => count == 1 ? "has" : "have";

    private static string Is(int count) => count == 1 ? "is" : "are";

    private static string Its(int count) => count == 1 ? "its" : "their";

    /// <summary>A mechanism's lane as the machine rung draws it: the view's own count where it has arrived, else the overview's.</summary>
    private static IReadOnlyList<TimelineBucket> Lane(WorkspaceViewModel workspace, Mechanism mechanism) =>
        (workspace.TimelineDetail?.MechanismLanes ?? workspace.Snapshot.MechanismLanes).Single(lane => lane.Mechanism == mechanism).Buckets;

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

    /// <summary>client.exe sending to server.exe over one paired connection, the server receiving each send.</summary>
    private static ObservationRowV1[] Rows() =>
    [
        Timed(Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" }),
        Timed(Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe" }),
        .. Enumerable.Range(0, Exchanges).SelectMany(index => new[]
        {
            Timed(Transfer(10 + (5 * index), ObservationKind.Send, AccountingSide.SendSide, 64, 100,
                (ulong)(100 + (2 * index))).Between(ClientEnd, ServerEnd)),
            Timed(Transfer(11 + (5 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200,
                (ulong)(101 + (2 * index))).Between(ServerEnd, ClientEnd)),
        }),
    ];

    /// <summary><paramref name="count"/> instances of pool.exe, each created and then sending ten times across the capture.</summary>
    private static ObservationRowV1[] Pool(int count) =>
    [
        .. Enumerable.Range(0, count).SelectMany(index => new[]
        {
            Lifecycle(index + 1, ObservationKind.Create, 2_000 + index, (ulong)(index * 20)) with
            {
                ResourceName = @"C:\Tools\pool.exe", SessionRelativeTicks = (index + 1) * 100L,
            },
        }.Concat(Enumerable.Range(0, 10).Select(step =>
            Transfer(100 + (step * 100) + index, ObservationKind.Send, AccountingSide.SendSide, 8, 2_000 + index,
                (ulong)((index * 20) + step + 1)) with { SessionRelativeTicks = (100 + (step * 100) + index) * 100L }))),
    ];

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };
}
