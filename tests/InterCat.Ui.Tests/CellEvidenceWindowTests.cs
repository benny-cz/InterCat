using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.CaptureBroker;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;
using static InterCat.Ui.Tests.RenderedPixels;

namespace InterCat.Ui.Tests;

/// <summary>
/// §6.4 in the window: selecting a timeline cell opens the exact contributing evidence. A click on a cell of a mechanism's
/// lane, then E, lists exactly that cell's records, behind a chip naming the mechanism, which the card named before.
/// </summary>
public sealed class CellEvidenceWindowTests
{
    [AvaloniaFact(DisplayName = "§6.4: a click on a mechanism lane's cell, then E, lists exactly its records behind a chip naming the mechanism")]
    public async Task EvidenceFromAClickedCellIsItsRecords()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        Dispatch();
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        Assert.True(workspace.ShowsMechanismLanes);
        await DrawnTimeline.Counted(window, workspace, timeline);

        // A UDP cell, clicked: the card beneath the time scope names the records E will list.
        TimelineBucket cell = DrawnTimeline.Lane(workspace, Mechanism.Udp).First(bucket => bucket.ObservationCount > 0);
        Point at = timeline.TranslatePoint(timeline.PointOf(cell)!.Value, window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatch();
        Assert.Equal(cell.Interval, workspace.SelectedInterval);
        Assert.EndsWith(" · UDP records only", window.GetControl<TextBlock>("EvidenceSummaryText").Text, StringComparison.Ordinal);

        // The inspector lists the cell's own records beneath the time scope, each named for a screen reader.
        await workspace.CellRecordsReady;
        Dispatch();
        Assert.True(window.GetControl<StackPanel>("CellRecordsPanel").IsEffectivelyVisible);
        Assert.Equal("Its " + (cell.ObservationCount == 1 ? "1 record" : $"{cell.ObservationCount} records"),
            window.GetControl<TextBlock>("CellRecordsHeadingText").Text);
        ItemsControl records = window.GetControl<ItemsControl>("CellRecordsList");
        Assert.Equal(cell.ObservationCount, records.ItemCount);
        CellRecordRow first = Assert.IsType<CellRecordRow>(records.Items[0]);
        Assert.StartsWith("UDP ", first.Title, StringComparison.Ordinal);
        Assert.Contains(records.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == first.Title && block.IsEffectivelyVisible);
        Assert.Contains(records.GetVisualDescendants().OfType<Control>(),
            control => AutomationProperties.GetName(control) == first.AccessibleName);
        Assert.Contains(", at ", first.AccessibleName, StringComparison.Ordinal);

        // E lists exactly them, and the filter bar says why.
        window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);
        Dispatch();
        await workspace.EvidenceReady;
        Dispatch();
        Assert.True(workspace.IsEvidenceRung);
        Assert.Contains(workspace.Filters, filter => filter.Chip == "Mechanism: UDP");
        Assert.Equal(cell.ObservationCount, workspace.EvidenceMarkTicks.Count);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§6.4: a record listed beside a chosen cell opens in E on Enter or a double click, selected there with the keyboard on it")]
    public async Task AListedRecordOpensInE()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [.. Rows(), .. Burst()]);
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        Dispatch();
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        await DrawnTimeline.Counted(window, workspace, timeline);
        TimelineBucket cell = DrawnTimeline.Lane(workspace, Mechanism.Udp).Single(bucket => bucket.ObservationCount == 3);
        ListBox records = await Choose(cell);
        Assert.Equal("Enter or a double click opens one in E", window.GetControl<TextBlock>("CellRecordsNoteText").Text);

        // Enter on the second record opens E with it selected, and E's table has the keyboard on it.
        CellRecordRow second = Assert.IsType<CellRecordRow>(records.Items[1]);
        Assert.True(records.ContainerFromIndex(1)!.Focus());
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatch();
        Assert.True(workspace.IsEvidenceRung);
        await workspace.EvidenceReady;
        Dispatch();
        Assert.Equal(second.Key, workspace.SelectedRung?.Key);
        Assert.True(workspace.HasSelectedEvidence);
        ListBox rungs = window.GetControl<ListBox>("RungList");
        Assert.Same(workspace.SelectedRung, rungs.SelectedItem);
        Assert.True(rungs.ContainerFromItem(rungs.SelectedItem!)!.IsKeyboardFocusWithin);

        // Back at the cell, a double click on the third opens it alike.
        Assert.True(workspace.Ascend());
        Dispatch();
        records = await Choose(cell);
        CellRecordRow third = Assert.IsType<CellRecordRow>(records.Items[2]);
        Control item = Assert.IsAssignableFrom<Control>(records.ContainerFromIndex(2));
        item.BringIntoView();
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
        Point on = item.TranslatePoint(new(item.Bounds.Width / 2, item.Bounds.Height / 2), window)!.Value;
        Assert.True(window.InputHitTest(on) is Visual hit && item.IsVisualAncestorOf(hit));
        window.MouseDown(on, MouseButton.Left);
        window.MouseUp(on, MouseButton.Left);
        window.MouseDown(on, MouseButton.Left);
        window.MouseUp(on, MouseButton.Left);
        Dispatch();
        Assert.True(workspace.IsEvidenceRung);
        await workspace.EvidenceReady;
        Dispatch();
        Assert.Equal(third.Key, workspace.SelectedRung?.Key);
        window.Close();

        async Task<ListBox> Choose(TimelineBucket chosen)
        {
            Point at = timeline.TranslatePoint(timeline.PointOf(chosen)!.Value, window)!.Value;
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Dispatch();
            await workspace.CellRecordsReady;
            Dispatch();
            ListBox list = window.GetControl<ListBox>("CellRecordsList");
            Assert.Equal(3, list.ItemCount);
            return list;
        }
    }

    [AvaloniaFact(DisplayName = "§6.4: a clicked cell highlights its relationship in the graph, or at a process's rung the process's node, fainter than a selection")]
    public async Task AClickedCellHighlightsTheGraph()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        Dispatch();
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        GraphView graph = window.GetControl<GraphView>("GraphSurface");
        TextBlock summary = window.GetControl<TextBlock>("GraphSummaryText");
        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 100);
        await DrawnTimeline.Counted(window, workspace, timeline);

        // A pixel beside the TCP edge, nearer the end no other edge meets: inside the halo a highlight draws, outside the
        // edge's own stroke and clear of the ring an edge may wear at its middle.
        GraphDisplayEdge tcp = workspace.GraphDisplay.Edges.Single(edge => edge.Mechanism == Mechanism.Tcp);
        bool sourceShared = workspace.GraphDisplay.Edges.Any(edge => edge != tcp
            && (edge.SourceKey == tcp.SourceKey || edge.TargetKey == tcp.SourceKey));
        Point lone = graph.PointOf(sourceShared ? tcp.TargetKey : tcp.SourceKey)!.Value;
        Vector along = graph.PointOf(sourceShared ? tcp.SourceKey : tcp.TargetKey)!.Value - lone;
        Vector across = new Vector(-along.Y, along.X) / along.Length;
        Point beside = graph.TranslatePoint(lone + (along * 0.35) + (across * 3.75), window)!.Value;

        // A TCP cell, clicked: once the graph counts its interval, its relationship is highlighted, and the summary says so.
        TimelineBucket cell = DrawnTimeline.Lane(workspace, Mechanism.Tcp).First(bucket => bucket.ObservationCount > 0);
        Point at = timeline.TranslatePoint(timeline.PointOf(cell)!.Value, window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatch();
        await workspace.IntervalReady;
        Color highlighted = At(Settle(window), beside);
        Assert.Equal([tcp.Key], workspace.CellGraphEdges);
        Assert.EndsWith(" · the chosen cell's relationship highlighted", summary.Text, StringComparison.Ordinal);

        // The relationship table marks its row as the graph haloes the edge: a bar at the halo's strength along its edge,
        // and an item status a screen reader announces with the row (R15).
        ListBox table = window.GetControl<ListBox>("RelationshipList");
        workspace.ShowTables = true;
        _ = Settle(window);
        RelationshipRow related = workspace.Relationships.Single(row => tcp.Relationships.Contains(row.Key));
        Control container = Assert.IsAssignableFrom<Control>(table.ContainerFromItem(related));
        Point bar = container.TranslatePoint(new Point(2, container.Bounds.Height / 2), window)!.Value;
        Color barred = At(Settle(window), bar);
        Assert.Contains("highlighted", container.Classes);
        Assert.Equal("highlighted for the chosen cell", AutomationProperties.GetItemStatus(container));
        workspace.ShowTables = false;

        // A process selected is what E would list, so over the same interval the highlight goes, and the row's mark with
        // it; the relationship chosen is haloed at the selection's whole strength, which the cell's highlight stays short
        // of.
        workspace.SelectedProcess = client;
        Color plain = At(Settle(window), beside);
        Assert.DoesNotContain("highlighted", summary.Text, StringComparison.Ordinal);
        workspace.ShowTables = true;
        Assert.NotEqual(barred, At(Settle(window), bar));
        Assert.DoesNotContain("highlighted", container.Classes);
        Assert.Null(AutomationProperties.GetItemStatus(container));
        workspace.ShowTables = false;
        workspace.SelectedRelationship = workspace.Relationships.Single(row => tcp.Relationships.Contains(row.Key));
        Color selected = At(Settle(window), beside);
        Assert.InRange(Distance(highlighted, plain), 1, Distance(selected, plain) - 1);

        // At the client's own rung, with the server selected, a cell of the client's sent row highlights the client's node:
        // a ring fainter than a selection's.
        workspace.ClearSelection();
        foreach (string key in new[] { client.GroupKey, client.Id.ToString() })
        {
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == key);
            Assert.True(workspace.Descend());
            await workspace.TimelineDetailReady;
        }

        await DrawnTimeline.Counted(window, workspace, timeline);
        GraphDisplayNode node = workspace.GraphDisplay.Nodes.Single(candidate => candidate.Process == client.Id);
        TimelineBucket sent = workspace.TimelineDirectionLanes!.Single(lane => lane.Direction == Direction.Outbound).Buckets
            .First(bucket => bucket.ObservationCount > 0);
        workspace.ChooseTimelineCell(sent, directionLane: Direction.Outbound);
        await workspace.IntervalReady;
        workspace.SelectedProcess = workspace.Snapshot.Processes.Single(candidate => candidate.ProcessId == 200);
        WriteableBitmap frame = Settle(window);
        Point ring = graph.TranslatePoint(RingPoint(graph, workspace.GraphDisplay, node), window)!.Value;
        Assert.Equal([node.Key], workspace.CellGraphNodes);
        Assert.EndsWith(" · the chosen cell's process highlighted", summary.Text, StringComparison.Ordinal);
        highlighted = At(frame, ring);
        workspace.SelectedRelationship = workspace.Relationships[0];
        plain = At(Settle(window), ring);
        Assert.Empty(workspace.CellGraphNodes);
        workspace.SelectedProcess = client;
        selected = At(Settle(window), ring);
        Assert.InRange(Distance(highlighted, plain), 1, Distance(selected, plain) - 1);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§6.4: a cell of a process's lane marks that process's ranked row, as the graph rings its node, and says so to a screen reader")]
    public async Task AProcessLanesCellMarksItsRankedRow()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Pool(3));
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label.StartsWith("pool.exe", StringComparison.Ordinal));
        Assert.True(workspace.Descend());
        await workspace.TimelineDetailReady;
        Dispatch();

        // A cell of the first process's lane: its row carries the bar at the ring's strength, and its status says why;
        // the other members' rows carry neither.
        ProcessTimelineLane lane = workspace.ProcessLaneDisplay[0];
        ProcessNode owner = workspace.Snapshot.Processes.Single(process => process.Id == lane.ProcessId);
        workspace.ChooseTimelineCell(lane.Buckets.First(bucket => bucket.ObservationCount > 0), ownerLane: owner);
        await workspace.IntervalReady;
        await workspace.TimelineDetailReady;
        await workspace.CellRecordsReady;
        Assert.Single(workspace.CellGraphNodes);
        WriteableBitmap frame = Settle(window);
        ListBox list = window.GetControl<ListBox>("RungList");
        Control marked = Assert.IsAssignableFrom<Control>(
            list.ContainerFromItem(workspace.RungRows.Single(row => row.Key == owner.Id.ToString())));
        Control other = Assert.IsAssignableFrom<Control>(
            list.ContainerFromItem(workspace.RungRows.First(row => row.Key != owner.Id.ToString())));
        Assert.Contains("highlighted", marked.Classes);
        Assert.Equal("highlighted for the chosen cell", AutomationProperties.GetItemStatus(marked));
        Assert.DoesNotContain("highlighted", other.Classes);
        Assert.Null(AutomationProperties.GetItemStatus(other));
        Assert.NotEqual(At(frame, marked.TranslatePoint(new Point(2, marked.Bounds.Height / 2), window)!.Value),
            At(frame, other.TranslatePoint(new Point(2, other.Bounds.Height / 2), window)!.Value));

        // The process selected is what E lists then, so the graph rings no node for the cell and its row loses the mark.
        workspace.SelectedProcess = owner;
        Dispatch();
        Assert.Empty(workspace.CellGraphNodes);
        Assert.DoesNotContain("highlighted", marked.Classes);
        Assert.Null(AutomationProperties.GetItemStatus(marked));
        window.Close();
    }

    /// <summary>
    /// A point on the ring a selection draws around <paramref name="node"/>, where neither an edge nor a placed label is
    /// drawn: the first of sixteen bearings at least ten pixels from every edge's line and outside every label.
    /// </summary>
    private static Point RingPoint(GraphView graph, GraphDisplay display, GraphDisplayNode node)
    {
        Point centre = graph.PointOf(node.Key)!.Value;
        double radius = graph.RadiusOf(node.Key)!.Value + GraphView.SelectionHalo;
        (Point From, Point To)[] lines = [.. display.Edges
            .Where(edge => graph.PointOf(edge.SourceKey) is not null && graph.PointOf(edge.TargetKey) is not null)
            .Select(edge => (graph.PointOf(edge.SourceKey)!.Value, graph.PointOf(edge.TargetKey)!.Value))];
        return Enumerable.Range(0, 16)
            .Select(step => centre + (new Vector(Math.Cos(step * Math.PI / 8), Math.Sin(step * Math.PI / 8)) * radius))
            .First(point => lines.All(line => FromLine(point, line.From, line.To) >= 10)
                && graph.PlacedLabels.All(label => !label.Inflate(3).Contains(point)));
    }

    /// <summary>How far a point lies from a drawn line between two points.</summary>
    private static double FromLine(Point point, Point from, Point to)
    {
        Vector line = to - from;
        double along = Math.Clamp(Vector.Dot(point - from, line) / Math.Max(line.SquaredLength, 1e-9), 0, 1);
        return ((Vector)(point - (from + (line * along)))).Length;
    }

    /// <summary>How far apart two colours are, channel by channel.</summary>
    private static int Distance(Color first, Color second) =>
        Math.Abs(first.R - second.R) + Math.Abs(first.G - second.G) + Math.Abs(first.B - second.B);

    private static WriteableBitmap Settle(Window window)
    {
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
        return window.CaptureRenderedFrame()!;
    }

    private static CaptureUiUpdate Update(TemporarySession session) => new(
        CaptureUiPhase.Recording, "Recording", "A published generation.", SessionPath: session.Path,
        Overview: SessionOverviewProjector.Project(session.Store));

    /// <summary>client.exe sending to server.exe over one paired connection, and a datagram to a resolver after each send.</summary>
    private static ObservationRowV1[] Rows() =>
    [
        Timed(Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" }),
        Timed(Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe" }),
        .. Enumerable.Range(0, 20).SelectMany(index => new[]
        {
            Timed(Transfer(10 + (5 * index), ObservationKind.Send, AccountingSide.SendSide, 64, 100, (ulong)(100 + (3 * index)))
                .Between("127.0.0.1:50000", "127.0.0.1:8080")),
            Timed(Transfer(11 + (5 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, (ulong)(101 + (3 * index)))
                .Between("127.0.0.1:8080", "127.0.0.1:50000")),
            Timed(Transfer(12 + (5 * index), ObservationKind.Send, AccountingSide.SendSide, 16, 100, (ulong)(102 + (3 * index)))
                .Between("127.0.0.1:50001", "127.0.0.1:53") with { Mechanism = Mechanism.Udp }),
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

    /// <summary>Three datagrams client.exe sent to the resolver in one tick, after the exchange: a cell of three records.</summary>
    private static ObservationRowV1[] Burst() =>
    [
        .. Enumerable.Range(0, 3).Select(index => Timed(Transfer(150, ObservationKind.Send, AccountingSide.SendSide, 20 + index, 100,
                (ulong)(300 + index)).Between("127.0.0.1:50002", "127.0.0.1:53") with { Mechanism = Mechanism.Udp })),
    ];

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
