using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using Xunit.Sdk;

namespace InterCat.Ui.Tests;

/// <summary>
/// A headless Avalonia fact that runs only against a real session on this machine: set <c>INTERCAT_REAL_SESSION</c> to a
/// session directory, such as <c>icat import &lt;etl&gt; --into &lt;dir&gt;</c> of a real capture. Real sessions are
/// local-only evidence and never fixtures, so without the variable the test is reported as skipped, never as passed.
/// </summary>
[XunitTestCaseDiscoverer("Avalonia.Headless.XUnit.AvaloniaUIFactDiscoverer", "Avalonia.Headless.XUnit")]
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RealSessionFactAttribute : FactAttribute
{
    public const string Variable = "INTERCAT_REAL_SESSION";

    public RealSessionFactAttribute()
    {
        if (SessionPath is null)
        {
            Skip = $"Set {Variable} to a local session directory to run this real-data qualification.";
        }
    }

    public static string? SessionPath => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } path
        && Directory.Exists(path) ? path : null;
}

/// <summary>
/// §6.3 qualification on real data. It opens the session as the Desktop does, asserts the projection's invariants - the
/// display budget, every process in exactly one node, every relationship addressable, processes without a relationship
/// counted in one node, executable names that read as names - and drives the gestures a person uses: selecting the
/// no-relationship node, selecting the largest executable group, opening it and brushing half the session. Frames and a
/// timing/node listing are written under <c>rendered/real-session</c> beside the test assembly.
/// </summary>
public sealed class RealSessionGraphTests
{
    [RealSessionFact(DisplayName = "§6.3: a real session opens as a bounded, legible graph that omits no process")]
    public async Task RealSessionOpensAsABoundedLegibleGraph()
    {
        string path = RealSessionFactAttribute.SessionPath!;
        var report = new StringBuilder();
        var clock = Stopwatch.StartNew();
        SessionOverviewBundle overview = SessionOverviewProjector.Project(SessionStore.OpenExisting(LocalOwnedDirectory.Open(path)));
        long projected = clock.ElapsedMilliseconds;
        WorkspaceSnapshot snapshot = OverviewWorkspace.From(overview);
        clock.Restart();
        GraphDisplay alone = GraphProjection.Project(snapshot);
        long graphProjection = clock.ElapsedMilliseconds;
        clock.Restart();
        _ = GraphLayout.ComputeDisplay(overview.GraphIdentity, alone);
        long layout = clock.ElapsedMilliseconds;
        report.AppendLine(CultureInfo.InvariantCulture,
            $"{overview.Nodes.Count} processes, {overview.Edges.Count} relationships, {overview.Groups.Count} groups");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"overview projection {projected} ms, graph projection {graphProjection} ms, layout {layout} ms");

        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        clock.Restart();
        window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Real-session qualification.",
            SessionPath: path, Overview: overview), forceOverview: true);
        var viewModel = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        long opened = clock.ElapsedMilliseconds;
        await viewModel.LayoutReady;
        Dispatch();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"window opened the workspace in {opened} ms; layout applied {clock.ElapsedMilliseconds} ms after");

        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        Assert.NotEmpty(snapshot.MechanismLanes);
        Assert.True(viewModel.ShowsMechanismLanes);
        report.AppendLine(CultureInfo.InvariantCulture,
            $"timeline: {snapshot.MechanismLanes.Count} exact mechanism lanes, {timeline.Bounds.Width:0} × {timeline.Bounds.Height:0} px");
        foreach (MechanismTimelineLane lane in snapshot.MechanismLanes)
        {
            TimelineBucket bucket = lane.Buckets.First(candidate => candidate.ObservationCount > 0);
            Point point = timeline.TranslatePoint(timeline.PointOf(bucket)!.Value, window)!.Value;
            window.MouseMove(point);
            Dispatch();
            Assert.Equal(bucket, timeline.HoveredBucket);
            Assert.Contains(EvidenceRowText.MechanismName(lane.Mechanism),
                Assert.IsType<HoverCard>(timeline.HoverCard).Lines[0], StringComparison.Ordinal);
        }

        GraphDisplay display = viewModel.GraphDisplay;
        AssertComplete(overview, display);
        HashSet<ProcessInstanceId> related = [.. overview.Edges.SelectMany(edge => new[] { edge.SourceId, edge.TargetId })];
        int quiet = overview.Nodes.Count(node => !related.Contains(node.Id));
        if (quiet >= 2)
        {
            GraphDisplayNode quietNode = Assert.Single(display.Nodes, node => node.Kind == GraphNodeKind.Quiet);
            Assert.Equal(quiet, quietNode.Members.Count);
            Assert.Equal(0, quietNode.Relationships);
        }

        // An executable group reads as its file name; folders are added only to tell two same-named executables apart.
        Assert.All(overview.Groups, group => Assert.DoesNotContain('\\', group.Name.Split(" (")[0]));
        report.AppendLine(CultureInfo.InvariantCulture, $"summary: {viewModel.GraphSummary}");
        Describe(report, "machine rung", display);
        Save(window, "machine.png");

        if (display.Nodes.FirstOrDefault(node => node.Kind == GraphNodeKind.Quiet) is { } aggregate)
        {
            viewModel.SelectGraphNode(aggregate.Key);
            Dispatch();
            Assert.Equal("No relationships", viewModel.SelectionTitle);
            Assert.Contains(quiet.ToString("N0", CultureInfo.CurrentCulture), viewModel.SelectionSubtitle, StringComparison.Ordinal);
            Assert.Contains(aggregate.Key, viewModel.SelectedGraphNodeKeys);
            report.AppendLine(CultureInfo.InvariantCulture, $"quiet selected: {viewModel.SelectionSubtitle}");
            Save(window, "quiet-selected.png");
        }

        // Open the largest collapsed group when the machine rung draws one, since that is the path compaction exists
        // for; otherwise the largest group, which shows how an opened group's quiet members are counted.
        ProcessGroup largest = overview.Groups
            .OrderByDescending(group => display.Nodes.Any(node => node.Kind == GraphNodeKind.Group && node.GroupKey == group.Key))
            .ThenByDescending(group => overview.Nodes.Count(node => node.GroupKey == group.Key))
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .First();
        viewModel.SelectedRung = viewModel.RungRows.First(row => row.Key == largest.Key);
        Dispatch();
        Assert.Equal(largest.Name, viewModel.SelectionTitle);
        if (largest.Detail is { } fullPath)
        {
            Assert.Contains(fullPath, viewModel.SelectionSubtitle, StringComparison.Ordinal);
        }

        Assert.NotEmpty(viewModel.SelectedGraphNodeKeys.Concat(viewModel.PartlySelectedGraphNodeKeys));
        report.AppendLine(CultureInfo.InvariantCulture, $"group selected: {largest.Name}: {viewModel.SelectionSubtitle}");
        Save(window, "group-selected.png");

        clock.Restart();
        Assert.True(viewModel.Descend());
        await viewModel.LayoutReady;
        Dispatch();
        report.AppendLine(CultureInfo.InvariantCulture, $"group opened and laid out in {clock.ElapsedMilliseconds} ms");
        display = viewModel.GraphDisplay;
        AssertComplete(overview, display);
        Assert.Equal(largest.Key, display.ExpandedGroup);
        Assert.All(overview.Nodes.Where(node => node.GroupKey == largest.Key && related.Contains(node.Id)),
            node => Assert.Contains(display.NodeOf(node.Id)!.Kind, new[] { GraphNodeKind.Process, GraphNodeKind.OtherMembers }));

        // §3.2 L1: the group within its neighbourhood - its members and their peers. Every other process is context.
        HashSet<ProcessInstanceId> members = [.. overview.Nodes.Where(node => node.GroupKey == largest.Key).Select(node => node.Id)];
        HashSet<ProcessInstanceId> neighbourhood = [.. members];
        foreach (CommunicationEdge edge in overview.Edges)
        {
            if (members.Contains(edge.SourceId)) neighbourhood.Add(edge.TargetId);
            if (members.Contains(edge.TargetId)) neighbourhood.Add(edge.SourceId);
        }

        Assert.All(overview.Nodes.Where(node => !neighbourhood.Contains(node.Id)),
            node => Assert.Equal(GraphNodeKind.Context, display.NodeOf(node.Id)!.Kind));
        Assert.All(overview.Nodes.Where(node => neighbourhood.Contains(node.Id)),
            node => Assert.NotEqual(GraphNodeKind.Context, display.NodeOf(node.Id)!.Kind));
        Describe(report, "group opened", display);

        // The timeline follows the rung: the group's own records in colour, inside the whole timeline's grey (§3.2).
        clock.Restart();
        await viewModel.TimelineDetailReady;
        Dispatch();
        Assert.True(viewModel.TimelineShowsFocus, viewModel.TimelineCaption);
        IReadOnlyList<TimelineBucket> focus = Assert.IsAssignableFrom<IReadOnlyList<TimelineBucket>>(viewModel.TimelineFocusBuckets);
        IReadOnlyList<TimelineBucket> whole = viewModel.TimelineDetail?.Buckets ?? viewModel.Snapshot.Timeline;
        Assert.Equal(whole.Select(bucket => bucket.Interval), focus.Select(bucket => bucket.Interval));
        Assert.All(whole.Zip(focus), pair => Assert.InRange(pair.Second.ObservationCount, 0, pair.First.ObservationCount));
        if (members.Count > 1)
        {
            if (members.Count <= SessionTimelineQuery.MaximumProcessLanes
                && (long)members.Count * focus.Count <= SessionTimelineQuery.MaximumProcessLaneCells)
            {
                IReadOnlyList<ProcessTimelineLane> lanes =
                    Assert.IsAssignableFrom<IReadOnlyList<ProcessTimelineLane>>(viewModel.TimelineProcessLanes);
                Assert.Equal(members.Count, lanes.Count);
                Assert.All(focus.Select((bucket, index) => (bucket, index)), pair =>
                    Assert.Equal(pair.bucket.ObservationCount, lanes.Sum(lane => lane.Buckets[pair.index].ObservationCount)));
                report.AppendLine(CultureInfo.InvariantCulture, $"L1 process lanes: {lanes.Count} exact owner rows");
                Assert.True(viewModel.ShowsProcessLanes);
                ScrollViewer laneScroller = window.GetControl<ScrollViewer>("TimelineLaneScroller");
                if (lanes.Count > 12)
                {
                    Assert.True(timeline.Bounds.Height > laneScroller.Bounds.Height);
                    TimeRange beforeWheel = timeline.Viewport;
                    Point gutter = timeline.TranslatePoint(new(30, 50), window)!.Value;
                    window.MouseWheel(gutter, new Vector(0, -1));
                    Dispatch();
                    Assert.True(laneScroller.Offset.Y > 0);
                    Assert.Equal(beforeWheel, timeline.Viewport);
                }

                ProcessTimelineLane observed = lanes.First(lane =>
                    lane.Buckets.Any(bucket => bucket.ObservationCount > 0));
                TimelineBucket observedBucket = observed.Buckets.First(bucket => bucket.ObservationCount > 0);
                Point local = timeline.PointOf(observed.ProcessId, observedBucket)!.Value;
                laneScroller.Offset = new Vector(laneScroller.Offset.X,
                    Math.Clamp(local.Y - (laneScroller.Bounds.Height / 2), 0,
                        Math.Max(0, laneScroller.Extent.Height - laneScroller.Viewport.Height)));
                Dispatch();
                Point onOwner = timeline.TranslatePoint(local, window)!.Value;
                window.MouseMove(onOwner);
                Dispatch();
                Assert.Equal(observedBucket, timeline.HoveredBucket);
                Assert.Contains(observed.ProcessId.ToString(),
                    Assert.IsType<HoverCard>(timeline.HoverCard).Lines[1], StringComparison.Ordinal);
                laneScroller.Offset = new Vector(laneScroller.Offset.X, 0);
                Dispatch();
                using var carried = new WorkspaceViewModel(snapshot, overview.GraphIdentity,
                    new SessionEvidenceSource(path, overview.SessionId, overview.Generation));
                Assert.Null(carried.RestoreNavigation(viewModel.CaptureNavigation()));
                carried.AdoptTimeline(viewModel.CarryTimeline());
                Assert.Same(lanes, carried.TimelineProcessLanes);
                using var machine = new WorkspaceViewModel(snapshot, overview.GraphIdentity,
                    new SessionEvidenceSource(path, overview.SessionId, overview.Generation));
                machine.AdoptTimeline(viewModel.CarryTimeline());
                Assert.Null(machine.TimelineProcessLanes);
            }
            else
            {
                Assert.Empty(viewModel.TimelineProcessLanes!);
                Assert.NotNull(viewModel.ProcessLaneProblem);
                report.AppendLine(CultureInfo.InvariantCulture, $"L1 lane bound: {viewModel.ProcessLaneProblem}");
            }
        }
        report.AppendLine(CultureInfo.InvariantCulture,
            $"group timeline counted in {clock.ElapsedMilliseconds} ms more: {focus.Sum(bucket => (long)bucket.ObservationCount)} of "
            + $"{whole.Sum(bucket => (long)bucket.ObservationCount)} records · {viewModel.TimelineCaption}");

        // The opened group's table starts at its first row, whatever row of the machine rung was scrolled to.
        Assert.Equal(0, Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window.GetControl<ListBox>("RungList"))
            .OfType<ScrollViewer>().First().Offset.Y);
        Save(window, "group-open.png");

        if (viewModel.ShowsProcessLanes && viewModel.ProcessLaneDisplay.Count > 12)
        {
            ScrollViewer laneScroller = window.GetControl<ScrollViewer>("TimelineLaneScroller");
            ProcessTimelineLane farOwner = viewModel.ProcessLaneDisplay[^1];
            viewModel.SelectedRung = viewModel.RungRows.First(row => row.Key == farOwner.ProcessId.ToString());
            Dispatch();
            Assert.True(laneScroller.Offset.Y > 0, "Selecting a ranked process must reveal its off-screen lane.");
            viewModel.ClearProcessLaneFocus();
            Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window.GetControl<ListBox>("RungList"))
                .OfType<ScrollViewer>().First().Offset = new Vector(0, 0);
            laneScroller.Offset = new Vector(laneScroller.Offset.X, 0);
            Dispatch();
        }

        // §3.2 L2: the group's first ranked member, its records split by the source's direction. The rows partition the
        // instance's own count bucket by bucket, and the group rung comes back as it was.
        if (viewModel.RungRows.FirstOrDefault(row => Guid.TryParse(row.Key, out Guid id)
            && members.Contains(new ProcessInstanceId(id))) is { } memberRow)
        {
            viewModel.SelectedRung = memberRow;
            clock.Restart();
            Assert.True(viewModel.Descend());
            string rows = await QualifyDirectionRows(window, viewModel, "process-open.png");
            report.AppendLine(CultureInfo.InvariantCulture,
                $"L2 {memberRow.Label} ({memberRow.Detail}) counted in {clock.ElapsedMilliseconds} ms: {rows}");
            Assert.True(viewModel.Ascend());
            await viewModel.LayoutReady;
            await viewModel.TimelineDetailReady;
            Dispatch();
            Assert.Equal(largest.Key, viewModel.GraphDisplay.ExpandedGroup);
            display = viewModel.GraphDisplay;
        }

        // A brush re-counts the drawing; it must not re-cluster it under the hand (§6.4).
        string[] structure = [.. display.Nodes.Select(node => node.Key)];
        if (overview.Extent is { } extent && extent.EndTicks - extent.StartTicks > 4)
        {
            long quarter = (extent.EndTicks - extent.StartTicks) / 4;
            viewModel.SelectInterval(new TimeRange(extent.StartTicks + quarter, extent.EndTicks - quarter));
            await viewModel.IntervalReady;
            Dispatch();
            Assert.Null(viewModel.GraphLayoutProblem);
            Assert.Equal(structure, viewModel.GraphDisplay.Nodes.Select(node => node.Key));
            Save(window, "group-open-brushed.png");
        }

        window.Close();

        // The minimum window: the same machine rung must stay legible where the pane is smallest.
        var small = new MainWindow { Width = 1080, Height = 700 };
        small.Show();
        small.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Real-session qualification.",
            SessionPath: path, Overview: overview), forceOverview: true);
        var smallModel = Assert.IsType<WorkspaceViewModel>(small.DataContext);
        await smallModel.LayoutReady;
        Dispatch();
        Save(small, "machine-1080x700.png");

        // §3.2 L2 at the minimum window, on the process with the most relationships: real sends and receives in rows.
        if (overview.Edges.Count > 0)
        {
            ProcessInstanceId talker = overview.Edges.SelectMany(edge => new[] { edge.SourceId, edge.TargetId })
                .GroupBy(id => id).OrderByDescending(group => group.Count()).ThenBy(group => group.Key.Value).First().Key;
            ProcessNode talking = overview.Nodes.Single(node => node.Id == talker);
            smallModel.SelectedRung = smallModel.RungRows.First(row => row.Key == talking.GroupKey);
            Assert.True(smallModel.Descend());
            await smallModel.LayoutReady;
            smallModel.SelectedRung = smallModel.RungRows.First(row => row.Key == talker.ToString());
            clock.Restart();
            Assert.True(smallModel.Descend());
            string rows = await QualifyDirectionRows(small, smallModel, "process-directions-1080x700.png");
            report.AppendLine(CultureInfo.InvariantCulture,
                $"L2 {talking.NameWithPid}, the busiest relationship end, counted in {clock.ElapsedMilliseconds} ms: {rows}");

            // §3.2 L3: its busiest channel, one lane per end. The ends partition the channel's count bucket by bucket,
            // and each end's bands are its own outbound and inbound records.
            smallModel.SelectedRung = smallModel.RungRows[0];
            clock.Restart();
            Assert.True(smallModel.Descend());
            await smallModel.TimelineDetailReady;
            Dispatch();
            Assert.True(smallModel.ShowsChannelEndLanes, smallModel.TimelineCaption);
            IReadOnlyList<TimelineBucket> channel = Assert.IsAssignableFrom<IReadOnlyList<TimelineBucket>>(smallModel.TimelineFocusBuckets);
            IReadOnlyList<ChannelEndTimelineLane> ends = smallModel.TimelineChannelEndLanes!;
            Assert.Equal([0, 1], ends.Select(end => end.End));
            Assert.All(channel.Select((bucket, index) => (bucket, index)), pair =>
                Assert.Equal(pair.bucket.ObservationCount, ends.Sum(end => end.Buckets[pair.index].ObservationCount)));
            Assert.All(ends, end => Assert.All(end.Buckets.Select((bucket, index) => (bucket, index)), pair =>
                Assert.InRange(end.Outbound[pair.index].ObservationCount + end.Inbound[pair.index].ObservationCount,
                    0, pair.bucket.ObservationCount)));
            string endTotals = string.Join(" | ", ends.Select(end => string.Create(CultureInfo.InvariantCulture,
                $"{smallModel.ChannelEndLabel(end)}: out {end.Outbound.Sum(bucket => bucket.ObservationCount)} · "
                + $"in {end.Inbound.Sum(bucket => bucket.ObservationCount)} · all {end.Buckets.Sum(bucket => bucket.ObservationCount)}")));
            report.AppendLine(CultureInfo.InvariantCulture, $"L3 channel counted in {clock.ElapsedMilliseconds} ms: {endTotals}");

            TimelineView smallTimeline = small.GetControl<TimelineView>("TimelineSurface");
            ChannelEndTimelineLane busiestEnd = ends.OrderByDescending(end => end.Buckets.Sum(bucket => bucket.ObservationCount)).First();
            TimelineBucket endBucket = busiestEnd.Buckets.First(bucket => bucket.ObservationCount > 0);
            small.MouseMove(smallTimeline.TranslatePoint(smallTimeline.PointOfEnd(busiestEnd.End, endBucket)!.Value, small)!.Value);
            Dispatch();
            Assert.Equal(endBucket, smallTimeline.HoveredBucket);
            Assert.EndsWith($" · the {busiestEnd.Endpoint} end, held by {smallModel.ChannelEndHolder(busiestEnd)}",
                Assert.IsType<HoverCard>(smallTimeline.HoverCard).Lines[0], StringComparison.Ordinal);
            small.MouseMove(new Point(1, 1));
            Dispatch();
            Save(small, "channel-ends-1080x700.png");
        }

        small.Close();
        File.WriteAllText(Path.Combine(Output, "report.txt"), report.ToString());
    }

    /// <summary>
    /// At an instance rung: its source-direction rows partition its own count bucket by bucket, and the busiest row's
    /// first occupied bucket hovers with its row's name and direction word. Returns the rows' totals for the report.
    /// </summary>
    private static async Task<string> QualifyDirectionRows(Window window, WorkspaceViewModel viewModel, string frame)
    {
        await viewModel.TimelineDetailReady;
        Dispatch();
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        Assert.True(viewModel.ShowsDirectionLanes, viewModel.TimelineCaption);
        IReadOnlyList<TimelineBucket> own = Assert.IsAssignableFrom<IReadOnlyList<TimelineBucket>>(viewModel.TimelineFocusBuckets);
        IReadOnlyList<DirectionTimelineLane> directions = viewModel.TimelineDirectionLanes!;
        Assert.Equal(SessionTimelineQuery.LaneDirections, directions.Select(lane => lane.Direction));
        Assert.All(own.Select((bucket, index) => (bucket, index)), pair =>
            Assert.Equal(pair.bucket.ObservationCount, directions.Sum(lane => lane.Buckets[pair.index].ObservationCount)));

        DirectionTimelineLane busiest = directions
            .OrderByDescending(lane => lane.Buckets.Sum(bucket => (long)bucket.ObservationCount)).First();
        if (busiest.Buckets.FirstOrDefault(bucket => bucket.ObservationCount > 0) is { } busiestBucket)
        {
            ScrollViewer laneScroller = window.GetControl<ScrollViewer>("TimelineLaneScroller");
            Point local = timeline.PointOf(busiest.Direction, busiestBucket)!.Value;
            laneScroller.Offset = new Vector(laneScroller.Offset.X, Math.Clamp(local.Y - (laneScroller.Bounds.Height / 2),
                0, Math.Max(0, laneScroller.Extent.Height - laneScroller.Viewport.Height)));
            Dispatch();
            window.MouseMove(timeline.TranslatePoint(local, window)!.Value);
            Dispatch();
            Assert.Equal(busiestBucket, timeline.HoveredBucket);
            HoverCard card = Assert.IsType<HoverCard>(timeline.HoverCard);
            Assert.EndsWith($" · {WorkspaceViewModel.DirectionLabel(busiest.Direction)} lane", card.Lines[0],
                StringComparison.Ordinal);
            Assert.StartsWith("Direction: ", card.Lines[2], StringComparison.Ordinal);
            Save(window, frame);
            laneScroller.Offset = new Vector(laneScroller.Offset.X, 0);
            window.MouseMove(new Point(0, 0));
            Dispatch();
        }

        return string.Join(" · ", directions.Select(lane => string.Create(CultureInfo.InvariantCulture,
            $"{WorkspaceViewModel.DirectionLabel(lane.Direction)} {lane.Buckets.Sum(bucket => (long)bucket.ObservationCount)}")));
    }

    private static void AssertComplete(SessionOverviewBundle overview, GraphDisplay display)
    {
        Assert.InRange(display.Nodes.Count, 1, GraphDisplayBudget.Default.Nodes);
        Assert.InRange(display.Edges.Count, 0, GraphDisplayBudget.Default.Edges);
        Assert.Equal(overview.Nodes.Count, display.Nodes.Sum(node => node.Members.Count));
        Assert.All(overview.Nodes, node => Assert.NotNull(display.NodeOf(node.Id)));
        Assert.All(overview.Edges, edge =>
            Assert.True(display.EdgeOf(edge.Key) is not null || display.NodeOfRelationship(edge.Key) is not null));
    }

    private static void Describe(StringBuilder report, string title, GraphDisplay display)
    {
        report.AppendLine(CultureInfo.InvariantCulture, $"{title}: {display.Nodes.Count} nodes, {display.Edges.Count} edges");
        foreach (GraphDisplayNode node in display.Nodes.OrderByDescending(node => node.Members.Count).Take(12))
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"  {node.Kind,-12} {node.Label} · {node.Members.Count} members · {node.Relationships} relationships · {node.Observations} observations");
        }
    }

    private static string Output
    {
        get
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "rendered", "real-session");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    private static void Save(Window window, string name)
    {
        Avalonia.Media.Imaging.WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(Output, name));
    }
}
