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
/// The way back to a chosen timeline cell (§3.2, §6.8): E from it lists its lane's records, a record listed beside it opens
/// in E, and Enter opens a row over its interval; Esc or the breadcrumb back to the rung that way began chooses the cell
/// again there, explained as a cell of the lane it was chosen in, as the way back to a relationship chooses it again, and
/// forward to a rung an ascent left with a cell chosen chooses it again too.
/// </summary>
public sealed class CellWayBackTests
{
    [Fact(DisplayName = "§3.2: the way back from E on a chosen cell chooses the cell again, explained and listing its records - by Esc, by the breadcrumb, and forward and back")]
    public void TheWayBackFromACellsRecordsChoosesItAgain() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, TimelineCellTests.Rows());
        using WorkspaceViewModel workspace = TimelineCellTests.Open(session);
        TimelineBucket cell = TimelineCellTests.Lane(workspace, Mechanism.Tcp).First(bucket => bucket.ObservationCount > 1);
        workspace.ChooseTimelineCell(cell, Mechanism.Tcp);
        string explained = workspace.CellExplanation;
        (string, string) titled = ("Timeline cell", "TCP lane · " + cell.ObservationCount + " records");
        Assert.Equal(titled, (workspace.SelectionTitle, workspace.SelectionSubtitle));

        // E lists its TCP records; Esc comes back to the cell, explained, titled and listing its records as before, and the
        // timeline's name, said as it changes, ends saying it is the cell.
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.Equal(new CellOrigin(0, DetailLevel.Machine, null, new TimelineCellLane(Mechanism: Mechanism.Tcp)),
            Assert.Single(workspace.CaptureNavigation().CellOrigins!));
        var spoken = new List<string?>();
        workspace.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(WorkspaceViewModel.IntervalSpoken)) spoken.Add(workspace.IntervalSpoken);
        };
        Assert.True(workspace.Ascend());
        Assert.Equal((cell.Interval, explained), (workspace.SelectedInterval, workspace.CellExplanation));
        Assert.Equal(titled, (workspace.SelectionTitle, workspace.SelectionSubtitle));
        Assert.EndsWith(", TCP lane, " + cell.ObservationCount + " records", spoken[^1], StringComparison.Ordinal);
        await workspace.CellRecordsReady;
        Assert.Equal(cell.ObservationCount, workspace.CellRecordRows.Count);
        Assert.Null(workspace.CaptureNavigation().CellOrigins);

        // So E lists them again, and the breadcrumb back comes to the cell too; the crumb of the rung shown, chosen again,
        // keeps it chosen; forward to E and back, it is chosen again.
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.Equal("Mechanism: TCP", Assert.Single(workspace.Filters, filter => filter.Field == EvidenceScopes.MechanismField).Chip);
        workspace.ReturnTo(0);
        Assert.Equal(explained, workspace.CellExplanation);
        workspace.ReturnTo(0);
        Assert.Equal(explained, workspace.CellExplanation);
        Assert.True(workspace.GoForward());
        await workspace.EvidenceReady;
        Assert.True(workspace.Ascend());
        Assert.Equal(explained, workspace.CellExplanation);

        // Another cell chosen there and forward to E again, which comes back with the interval it was left with: back from
        // it, the cell chosen last is chosen again.
        TimelineBucket other = TimelineCellTests.Lane(workspace, Mechanism.Tcp)
            .First(bucket => bucket.ObservationCount > 0 && bucket.Interval.StartTicks >= cell.Interval.EndTicks);
        workspace.ChooseTimelineCell(other, Mechanism.Tcp);
        string otherExplained = workspace.CellExplanation;
        Assert.True(workspace.GoForward());
        await workspace.EvidenceReady;
        Assert.Equal(cell.Interval, workspace.SelectedInterval);
        Assert.True(workspace.Ascend());
        Assert.Equal((other.Interval, otherExplained), (workspace.SelectedInterval, workspace.CellExplanation));
        workspace.ChooseTimelineCell(cell, Mechanism.Tcp);

        // A record listed beside it opens in E selected there, and Esc comes back to the cell.
        await workspace.CellRecordsReady;
        Assert.True(workspace.OpenCellRecord(workspace.CellRecordRows[0]));
        await workspace.EvidenceReady;
        Assert.True(workspace.Ascend());
        Assert.Equal(explained, workspace.CellExplanation);

        // A lane selected after it is the one explained, and E from there a way down of its own: back from it, that lane's
        // cell is explained, not the one chosen before.
        workspace.SelectTimelineLane(Mechanism.ProcessLifecycle);
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.True(workspace.Ascend());
        Assert.Contains("process lifecycle record", workspace.CellExplanation, StringComparison.Ordinal);
        workspace.SelectTimelineLane(null);

        // Enter on a ranked row opens it over the cell's interval, and the way back chooses the row and the cell again.
        workspace.ChooseTimelineCell(cell, Mechanism.Tcp);
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "client.exe");
        Assert.True(workspace.Descend());
        Assert.True(workspace.Ascend());
        Assert.Equal(("client.exe", explained), (workspace.SelectedRung?.Label, workspace.CellExplanation));

        // Another interval chosen there, forward and back again come to that interval, no cell.
        TimelineBucket next = TimelineCellTests.Lane(workspace, Mechanism.Tcp)
            .First(bucket => bucket.ObservationCount > 0 && bucket.Interval.StartTicks >= cell.Interval.EndTicks);
        workspace.SelectInterval(next.Interval);
        Assert.True(workspace.GoForward());
        Assert.True(workspace.Ascend());
        Assert.Equal((next.Interval, false), (workspace.SelectedInterval, workspace.HasCellExplanation));

        // Another interval chosen ends the choice, so a way down from it comes back to no cell, and keeps no way back to one.
        workspace.ClearSelection();
        workspace.SelectInterval(new TimeRange(cell.Interval.StartTicks, cell.Interval.EndTicks + 1));
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.True(workspace.Ascend());
        Assert.Equal((false, "Time range"), (workspace.HasCellExplanation, workspace.SelectionTitle));
        Assert.Null(workspace.CaptureNavigation().CellOrigins);
    });

    [Fact(DisplayName = "§3.2: the way back to a cell of a process's row chooses it in that row, explained in the columns it was chosen in where the timeline cut them afresh, and a newer generation keeps it")]
    public void TheWayBackChoosesACellInItsRow() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, TimelineCellTests.Rows());
        using WorkspaceViewModel workspace = TimelineCellTests.Open(session);
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        workspace.RequestTimelineDetail(workspace.Snapshot.Extent, 80);
        TimelineCellTests.DescendTo(workspace, client.GroupKey);
        TimelineCellTests.DescendTo(workspace, client.Id.ToString());
        await workspace.TimelineDetailReady;

        // The client's outbound row's cell: E lists its sends, and back from them it is that row's cell again, not the
        // machine row's.
        TimelineBucket sent = workspace.TimelineDirectionLanes!.Single(lane => lane.Direction == Direction.Outbound).Buckets
            .First(bucket => bucket.ObservationCount > 0);
        workspace.ChooseTimelineCell(sent, directionLane: Direction.Outbound);
        string explained = workspace.CellExplanation;
        Assert.Contains(" marked outbound ", explained, StringComparison.Ordinal);
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.True(workspace.Ascend());
        await workspace.TimelineDetailReady;
        Assert.Equal(explained, workspace.CellExplanation);

        // Left by an ascent and entered again by Alt+Right, the rung is as it was left, its cell chosen; and so with the cell
        // chosen there last, not the one before.
        Assert.True(workspace.Ascend());
        Assert.NotEqual(explained, workspace.CellExplanation);
        Assert.True(workspace.GoForward());
        await workspace.TimelineDetailReady;
        Assert.Equal(explained, workspace.CellExplanation);
        TimelineBucket later = workspace.TimelineDirectionLanes!.Single(lane => lane.Direction == Direction.Outbound).Buckets
            .First(bucket => bucket.ObservationCount > 0 && bucket.Interval.StartTicks >= sent.Interval.EndTicks);
        workspace.ChooseTimelineCell(later, directionLane: Direction.Outbound);
        string laterExplained = workspace.CellExplanation;
        Assert.True(workspace.Ascend());
        Assert.True(workspace.GoForward());
        await workspace.TimelineDetailReady;
        Assert.Equal((later.Interval, laterExplained), (workspace.SelectedInterval, workspace.CellExplanation));

        // So by the breadcrumb up to the machine and forward again, with the first cell chosen there since.
        workspace.ChooseTimelineCell(sent, directionLane: Direction.Outbound);
        workspace.ReturnTo(0);
        Assert.True(workspace.GoForward());
        Assert.True(workspace.GoForward());
        await workspace.TimelineDetailReady;
        Assert.Equal((sent.Interval, explained), (workspace.SelectedInterval, workspace.CellExplanation));

        // Entered again by hand rather than forward, even over the same interval, the rung is a way down of its own and comes
        // to no cell.
        Assert.True(workspace.Ascend());
        workspace.SelectInterval(sent.Interval);
        TimelineCellTests.DescendTo(workspace, client.Id.ToString());
        Assert.True(workspace.Ascend());
        Assert.True(workspace.GoForward());
        await workspace.TimelineDetailReady;
        Assert.Equal(sent.Interval, workspace.SelectedInterval);
        Assert.NotEqual(explained, workspace.CellExplanation);
        workspace.ChooseTimelineCell(sent, directionLane: Direction.Outbound);

        // A newer generation keeps the way back: restored at E, Esc chooses the cell again in its row; restored with the
        // cell chosen, E and back choose it again.
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        WorkspaceNavigationMemento atRecords = workspace.CaptureNavigation();
        Assert.True(workspace.Ascend());
        await workspace.TimelineDetailReady;
        WorkspaceNavigationMemento atCell = workspace.CaptureNavigation();
        foreach (WorkspaceNavigationMemento saved in (WorkspaceNavigationMemento[])[atRecords, atCell])
        {
            using WorkspaceViewModel next = TimelineCellTests.Open(session);
            next.RequestTimelineDetail(next.Snapshot.Extent, 80);
            Assert.Null(next.RestoreNavigation(saved));
            if (!next.IsEvidenceRung)
            {
                await next.TimelineDetailReady;
                Assert.True(next.ShowEvidence());
                await next.EvidenceReady;
            }

            Assert.True(next.Ascend());
            await next.TimelineDetailReady;
            Assert.Equal(explained, next.CellExplanation);
        }

        // Its columns cut afresh while E listed its records, it is explained in those it was chosen in.
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        workspace.RequestTimelineDetail(workspace.Snapshot.Extent, 57);
        Assert.True(workspace.Ascend());
        await workspace.TimelineDetailReady;
        Assert.DoesNotContain(workspace.TimelineDirectionLanes!.Single(lane => lane.Direction == Direction.Outbound).Buckets,
            bucket => bucket.Interval == sent.Interval);
        Assert.Equal(explained.Replace(" here one of this view's own 80;", " here one of this view's own 80 when it was chosen, a "
            + "column the timeline no longer draws since it cut its columns afresh;", StringComparison.Ordinal), workspace.CellExplanation);
    });

    [Fact(DisplayName = "§3.2: a cell chosen at a process's rung is not chosen again where the way back comes to another process's rung at that depth")]
    public void TheWayBackToAnotherRungChoosesNoCell() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, TimelineCellTests.Rows());
        using WorkspaceViewModel workspace = TimelineCellTests.Open(session);
        await workspace.LayoutReady;
        CommunicationEdge paired = Assert.Single(workspace.Snapshot.Edges);
        ProcessNode target = workspace.Snapshot.Processes.Single(node => node.Id == paired.TargetId);

        // At the rung of the relationship's other end, a cell of a row of its records is chosen and the relationship
        // opened: the ladder runs through its source's rungs instead, so the way back to that depth chooses no cell.
        workspace.RequestTimelineDetail(workspace.Snapshot.Extent, 80);
        TimelineCellTests.DescendTo(workspace, target.GroupKey);
        TimelineCellTests.DescendTo(workspace, target.Id.ToString());
        await workspace.TimelineDetailReady;
        DirectionTimelineLane row = workspace.TimelineDirectionLanes!.First(lane => lane.Buckets.Any(bucket => bucket.ObservationCount > 0));
        workspace.ChooseTimelineCell(row.Buckets.First(bucket => bucket.ObservationCount > 0), directionLane: row.Direction);
        string bound = $" bound to {target.NameWithPid}, ";
        Assert.Contains(bound, workspace.CellExplanation, StringComparison.Ordinal);
        workspace.SelectedRelationship = workspace.Relationships.Single(relationship => relationship.Key == paired.Key);
        Assert.True(workspace.OpenSelectedRelationship());
        Assert.True(workspace.Ascend());
        await workspace.TimelineDetailReady;
        Assert.Equal(paired.SourceId.ToString(), workspace.CaptureNavigation().Breadcrumb[^1].Focus?.Key);
        Assert.DoesNotContain(bound, workspace.CellExplanation, StringComparison.Ordinal);
        Assert.Null(workspace.CaptureNavigation().ExplainedCell);
        Assert.Null(workspace.CaptureNavigation().CellOrigins);
    });

    [Fact(DisplayName = "§6.8: back at a channel's rung before its ends are counted again, the end's cell chosen there is explained once they are, and E meanwhile lists the rung's records")]
    public void ACellIsExplainedOnceItsLaneIsDrawnAgain() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, TimelineCellTests.Rows());
        using WorkspaceViewModel workspace = TimelineCellTests.Open(session);
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        Channel channel = workspace.Snapshot.Channels.Single();
        workspace.RequestTimelineDetail(workspace.Snapshot.Extent, 80);
        foreach (string key in (string[])[client.GroupKey, client.Id.ToString(), channel.Key])
        {
            TimelineCellTests.DescendTo(workspace, key);
        }

        await workspace.TimelineDetailReady;
        ChannelEndTimelineLane end = workspace.TimelineChannelEndLanes!.First(lane => lane.Buckets.Any(bucket => bucket.ObservationCount > 0));
        workspace.ChooseTimelineCell(end.Buckets.First(bucket => bucket.ObservationCount > 0), endLane: end);
        string explained = workspace.CellExplanation;
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;

        // Its ends are counted again after the way back arrives: until then the rung draws no end's lane, so the cell is
        // no cell yet, and E lists the channel's records with no end's filter.
        Assert.True(workspace.Ascend());
        Assert.False(workspace.ShowsChannelEndLanes);
        Assert.False(workspace.HasCellExplanation);
        Assert.True(workspace.ShowEvidence());
        Assert.DoesNotContain(workspace.Filters, filter => filter.Field == EvidenceScopes.EndField);
        Assert.True(workspace.Ascend());
        await workspace.TimelineDetailReady;
        Assert.Equal(explained, workspace.CellExplanation);
    });
}
