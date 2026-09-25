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
        await Assert.IsType<WorkspaceViewModel>(small.DataContext).LayoutReady;
        Dispatch();
        Save(small, "machine-1080x700.png");
        small.Close();
        File.WriteAllText(Path.Combine(Output, "report.txt"), report.ToString());
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
