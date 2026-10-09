using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>
/// §6.2's collapse groups: a group with more members than the lane bound draws its pinned and busiest members in lanes of
/// their own and folds the rest into one lane beneath them, counted exactly, named, explained and listed by E as the set
/// it is, where it drew no lane at all before.
/// </summary>
public sealed class FoldedLaneTests
{
    [Fact(DisplayName = "§6.2: a group past the lane bound draws its pinned and busiest lanes and folds the rest into one, explained and listed by E")]
    public void AGroupPastTheBoundFoldsItsQuietestMembers() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Pool(230));
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        await workspace.LayoutReady;

        // The quietest member is pinned before the group is opened, as an investigation puts a pin back.
        ProcessNode quietest = workspace.Snapshot.Processes.OrderBy(process => process.Records)
            .ThenBy(process => process.Id.ToString(), StringComparer.Ordinal).First();
        workspace.PinLanes([quietest.Id]);
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label.StartsWith("pool.exe", StringComparison.Ordinal));
        Assert.True(workspace.Descend());
        workspace.RequestTimelineDetail(workspace.Snapshot.Extent, 40);
        await workspace.TimelineDetailReady;

        // 199 lanes of their own - the pinned one first, then the busiest - and the other 31 folded into one beneath them.
        Assert.True(workspace.ShowsProcessLanes);
        Assert.Null(workspace.ProcessLaneProblem);
        Assert.Equal(SessionTimelineQuery.MaximumProcessLanes - 1, workspace.ProcessLaneDisplay.Count);
        Assert.Equal(quietest.Id, workspace.ProcessLaneDisplay[0].ProcessId);
        FoldedProcessLane folded = Assert.IsType<FoldedProcessLane>(workspace.FoldedLane);
        Assert.Equal(31, folded.Processes.Count);
        Assert.Equal("31 other members", workspace.FoldedLaneLabel);
        Assert.Contains("199 process lanes, and the other 31, least busy, folded into one lane beneath them",
            workspace.TimelineCaption, StringComparison.Ordinal);
        Dictionary<ProcessInstanceId, long> records = workspace.Snapshot.Processes.ToDictionary(process => process.Id, process => process.Records);
        long quietestDrawn = workspace.ProcessLaneDisplay.Skip(1).Min(lane => records[lane.ProcessId]);
        Assert.All(folded.Processes, member => Assert.True(records[member] <= quietestDrawn));
        Assert.DoesNotContain(quietest.Id, folded.Processes);

        // A search finds the folded members too, which have no lane of their own to mark, and the caption says how many.
        workspace.SearchText = "pool.exe";
        Assert.Contains(", 199 found by the search and 31 among the folded members", workspace.TimelineCaption,
            StringComparison.Ordinal);
        ProcessNode foldedMember = workspace.Snapshot.Processes.Single(process => process.Id == folded.Processes[0]);
        workspace.SearchText = foldedMember.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains(", 1 found by the search among the folded members", workspace.TimelineCaption, StringComparison.Ordinal);
        workspace.SearchText = string.Empty;
        Assert.DoesNotContain("found by the search", workspace.TimelineCaption, StringComparison.Ordinal);

        // The lanes and the folded one count every record of the group, none twice.
        Assert.Equal(workspace.Snapshot.Processes.Sum(process => process.Records),
            workspace.ProcessLaneDisplay.Sum(lane => lane.Buckets.Sum(bucket => (long)bucket.ObservationCount))
                + folded.Buckets.Sum(bucket => (long)bucket.ObservationCount));

        // A folded member has no lane to select, so the pin brings it out: pinned, it is counted in a lane of its own at the
        // top, after the one pinned before it, and the quietest member drawn before folds in its place; unpinned, it folds
        // again.
        ProcessNode outOfFold = workspace.Snapshot.Processes.Single(process => process.Id == folded.Processes[^1]);
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == outOfFold.Id.ToString());
        Assert.Equal(outOfFold, workspace.SelectedProcess);
        Assert.False(workspace.HasSelectedProcessLane);
        Assert.True(workspace.OffersLanePin);
        Assert.Equal("Pin lane (P)", workspace.LanePinLabel);
        Assert.True(workspace.ToggleSelectedLanePin());
        await workspace.TimelineDetailReady;
        Assert.Equal([quietest.Id, outOfFold.Id], workspace.ProcessLaneDisplay.Take(2).Select(lane => lane.ProcessId));
        Assert.Equal((199, 31), (workspace.ProcessLaneDisplay.Count, workspace.FoldedLane!.Processes.Count));
        Assert.DoesNotContain(outOfFold.Id, workspace.FoldedLane.Processes);
        Assert.True(workspace.HasSelectedProcessLane);
        Assert.Equal("Unpin lane (P)", workspace.LanePinLabel);
        Assert.Contains(", 2 pinned at the top", workspace.TimelineCaption, StringComparison.Ordinal);
        Assert.True(workspace.ToggleSelectedLanePin());
        await workspace.TimelineDetailReady;
        Assert.Contains(outOfFold.Id, workspace.FoldedLane!.Processes);
        Assert.False(workspace.HasSelectedProcessLane);
        Assert.True(workspace.OffersLanePin);
        workspace.SelectedRung = null;
        workspace.SelectedProcess = null;
        Assert.False(workspace.OffersLanePin);
        folded = Assert.IsType<FoldedProcessLane>(workspace.FoldedLane);

        // A folded cell is explained as one, its card names the folded lane, and E lists exactly the folded members' records.
        TimelineBucket cell = folded.Buckets.First(bucket => bucket.ObservationCount > 0);
        HoverCard card = workspace.DescribeTimelineHover(cell, 1.0, foldedLane: true);
        Assert.EndsWith(" · folded lane: 31 other members", card.Lines[0], StringComparison.Ordinal);
        Assert.StartsWith("Basis: source observations · unit: records · domain: records canonically owned by the 31 other "
            + "members of this group, its least busy, folded into one lane past the 200-lane bound", card.Lines[1],
            StringComparison.Ordinal);
        workspace.ChooseTimelineCell(cell, foldedLane: true);
        Assert.Contains("bound to one of the 31 other members of this group", workspace.CellExplanation, StringComparison.Ordinal);
        Assert.Contains("folded into one lane past the 200-lane bound, its 31 least busy members, so none of their records is "
            + "dropped", workspace.CellExplanation, StringComparison.Ordinal);
        Assert.Equal("Records E lists", workspace.EvidenceHeading);
        Assert.StartsWith("Records owned by the 31 processes of pool.exe's folded lane", workspace.EvidenceSummary,
            StringComparison.Ordinal);

        // The graph highlights the node the folded members are drawn in, as holding the cell's processes (§6.4).
        await workspace.IntervalReady;
        GraphDisplayNode holding = Assert.Single(workspace.GraphDisplay.Nodes,
            node => node.Members.Intersect(folded.Processes).Any());
        Assert.Equal([holding.Key], workspace.CellGraphNodes);
        Assert.EndsWith(" · the node holding the chosen cell's processes highlighted", workspace.GraphSummary,
            StringComparison.Ordinal);
        Assert.True(workspace.ShowEvidence());
        await workspace.EvidenceReady;
        Assert.Equal(cell.ObservationCount, workspace.EvidenceMarkTicks.Count);
        Assert.Equal(SessionEvidenceQuery.ReadScope(session.Store, 1_000, interval: cell.Interval, ownerProcesses: folded.Processes)
            .Records.Select(record => record.Observation.SessionRelativeTicks!.Value / 100).Order(), workspace.EvidenceMarkTicks.Order());
    });

    [Fact(DisplayName = "§6.2: a group within the lane bound folds nothing, as before")]
    public void AGroupWithinTheBoundFoldsNothing() => SingleThreadedContext.Run(async () =>
    {
        using var session = new TemporarySession();
        Publish(session.Store, Pool(12));
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        using var workspace = new WorkspaceViewModel(OverviewWorkspace.From(overview), overview.GraphIdentity,
            new SessionEvidenceSource(session.Path, overview.SessionId, overview.Generation));
        await workspace.LayoutReady;
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label.StartsWith("pool.exe", StringComparison.Ordinal));
        Assert.True(workspace.Descend());
        workspace.RequestTimelineDetail(workspace.Snapshot.Extent, 40);
        await workspace.TimelineDetailReady;
        Assert.Equal(12, workspace.ProcessLaneDisplay.Count);
        Assert.Null(workspace.FoldedLane);
        Assert.Equal(string.Empty, workspace.FoldedLaneLabel);
        Assert.DoesNotContain("folded", workspace.TimelineCaption, StringComparison.Ordinal);
    });

    /// <summary>
    /// <paramref name="count"/> instances of pool.exe, the one at index i created and then sending i % 7 times across the
    /// capture, so they are not all equally busy.
    /// </summary>
    private static ObservationRowV1[] Pool(int count) =>
    [
        .. Enumerable.Range(0, count).SelectMany(index => new[]
        {
            Lifecycle(index + 1, ObservationKind.Create, 2_000 + index, (ulong)(index * 20)) with
            {
                ResourceName = @"C:\Tools\pool.exe", SessionRelativeTicks = (index + 1) * 100L,
            },
        }.Concat(Enumerable.Range(0, index % 7).Select(step =>
            Transfer(1_000 + (step * 1_000) + index, ObservationKind.Send, AccountingSide.SendSide, 8, 2_000 + index,
                (ulong)((index * 20) + step + 1)) with { SessionRelativeTicks = (1_000 + (step * 1_000) + index) * 100L }))),
    ];
}
