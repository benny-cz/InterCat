using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// The window's comparison of its time scope with another interval (`contracts/comparison-v1.md`): offered where the scope
/// is an interval, stating each row in `icat compare`'s words, and opening the row chosen.
/// </summary>
public sealed class SessionCompareWindowTests
{
    private const long Second = 10_000_000;

    [AvaloniaFact(DisplayName = "R18: Compare with… states each row as icat compare does, the most changed first, and Enter opens the row's relationship")]
    public async Task CompareWithStatesEachRowAndOpensTheChosenOne()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows(), coverage: Ledger());
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            Assert.True(await window.OpenSessionAsync(session.Path));
            Dispatch();
            var workspace = (WorkspaceViewModel)window.DataContext!;
            await workspace.LayoutReady;

            // The whole session is no interval to compare: the button is not offered until a range is brushed.
            Button offered = window.GetControl<Button>("CompareButton");
            Assert.False(workspace.CanCompareIntervals);
            Assert.False(offered.IsVisible);
            workspace.SelectInterval(new TimeRange(12 * Second, 16 * Second));
            Dispatch();
            Assert.True(workspace.CanCompareIntervals);
            Assert.True(offered.IsVisible);
            Assert.Equal("Compare with… (Ctrl+B)", offered.Content);
            Save(window, "compare-offered.png");

            // Ctrl+B opens it, comparing the scope with the same length just before it.
            window.KeyPressQwerty(PhysicalKey.B, RawInputModifiers.Control);
            SessionCompareWindow comparing = await Opened(window);
            await comparing.Compared;
            Dispatch();
            CultureInfo culture = CultureInfo.CurrentCulture;
            Assert.Equal("InterCat · Compare two intervals", comparing.Title);
            string length = WorkspaceTime.FormatDuration(4 * Second, culture);
            Assert.Equal(
                [
                    $"The {length} before it, {WorkspaceTime.FormatHalfOpenRange(new TimeRange(8 * Second, 12 * Second), culture)}",
                    $"The {length} after it, {WorkspaceTime.FormatHalfOpenRange(new TimeRange(16 * Second, 20 * Second), culture)}",
                ],
                comparing.Offered.Take(2));
            Assert.StartsWith("The whole session, ", comparing.Offered[2], StringComparison.Ordinal);
            Assert.Equal("Another interval, typed in seconds…", comparing.Offered[3]);
            Assert.Contains(comparing.GetLogicalDescendants().OfType<TextBlock>(), block => block.Text
                == $"{WorkspaceTime.FormatHalfOpenRange(new TimeRange(12 * Second, 16 * Second), culture)}, {length} long, the window's time scope");

            // The worker's connection to the queue grew; the client's to the server held its rate. Each reads as icat compare
            // states it, and the first row has the keyboard.
            Assert.Equal(
                [
                    $"worker.exe · PID 300 → queue.exe · PID 400 | TCP at 9090 | 2 · {Rate(0.5, culture)}/s | 12 · {Rate(3, culture)}/s | up {Rate(2.5, culture)}/s (+500%)",
                    $"client.exe · PID 100 → server.exe · PID 200 | TCP at 8080 | 8 · {Rate(2, culture)}/s | 8 · {Rate(2, culture)}/s | unchanged",
                ],
                comparing.Rows.Select(row => $"{row.Ends} | {row.Over} | {row.BaselineValue} | {row.ComparedValue} | {row.ChangeCell}"));
            Assert.Equal($"Generation {1.ToString("N0", culture)} · 2 relationships between processes compared, the most changed first",
                comparing.Said);
            ListBox list = comparing.GetVisualDescendants().OfType<ListBox>().Single();
            Assert.Same(list.ContainerFromIndex(0), comparing.FocusManager?.GetFocusedElement());
            Assert.Equal(comparing.Rows[0].AccessibleName, AutomationProperties.GetName(list.ContainerFromIndex(0)!));
            Assert.Contains(comparing.GetLogicalDescendants().OfType<TextBlock>(),
                block => block.Text == "Coverage in the baseline: covered for TCP · no other mechanism collected\n"
                    + "Coverage in the compared interval: covered for TCP · no other mechanism collected");
            Assert.Contains(comparing.GetLogicalDescendants().OfType<TextBlock>(),
                block => block.Text?.StartsWith(ComparisonText.RatesNote, StringComparison.Ordinal) == true);
            Save(comparing, "compare-intervals.png");

            // Each value column is as wide as its widest cell, the heading's included, and the heading's line up with the rows'.
            Grid[] lines = [.. comparing.GetVisualDescendants().OfType<Grid>().Where(grid => grid.ColumnDefinitions.Count == 5)];
            Assert.Equal(3, lines.Length);
            foreach (Grid line in lines)
            {
                for (int column = 1; column < 5; column++)
                {
                    Assert.Equal(lines[0].ColumnDefinitions[column].ActualWidth, line.ColumnDefinitions[column].ActualWidth, 3);
                    TextBlock cell = line.Children.OfType<TextBlock>().Single(block => Grid.GetColumn(block) == column);
                    var whole = new TextBlock { Text = cell.Text, FontFamily = cell.FontFamily, FontSize = cell.FontSize, FontWeight = cell.FontWeight };
                    whole.Measure(Avalonia.Size.Infinity);
                    Assert.True(whole.DesiredSize.Width + cell.Margin.Left <= line.ColumnDefinitions[column].ActualWidth + 0.5,
                        $"'{cell.Text}' is cut off: {whole.DesiredSize.Width} in {line.ColumnDefinitions[column].ActualWidth}");
                }
            }

            // By bytes, what was sent across: the worker sent nothing before, so its sends appeared.
            comparing.CompareByBytes(true);
            await comparing.Compared;
            Assert.Equal(
                ["worker.exe · PID 300 → queue.exe · PID 400 | nothing sent | 384 B · 96 B/s | appeared",
                    "client.exe · PID 100 → server.exe · PID 200 | 256 B · 64 B/s | 256 B · 64 B/s | unchanged"],
                comparing.Rows.Select(row => $"{row.Ends} | {row.BaselineValue} | {row.ComparedValue} | {row.ChangeCell}"));

            // Choosing a baseline to type lets the rows of another go, saying what to write; it waits for both times.
            comparing.CompareByBytes(false);
            await comparing.Compared;
            comparing.ChooseBaseline(3);
            await comparing.Compared;
            Assert.Equal(SessionCompareWindow.WriteBaseline, comparing.Said);
            Assert.Empty(comparing.Rows);
            Assert.Null(comparing.Shown);
            Assert.Equal("Choose a row first.", Offers.WhyNot(Labelled(comparing, "Show in the window")));
            comparing.Enter("4", "2");
            await comparing.CompareAsync();
            Assert.Equal(SessionCompareWindow.WriteBaseline, comparing.Said);
            comparing.Enter(0.ToString(culture), 4.ToString(culture));
            await comparing.CompareAsync();
            Dispatch();
            Assert.Equal(["appeared", "unchanged"], comparing.Rows.Select(row => row.ChangeCell));

            // Enter on the chosen row closes the comparison and opens its relationship, as a double click on its edge does.
            int depth = workspace.Crumbs.Count;
            list.SelectedIndex = 0;
            Assert.True(list.ContainerFromIndex(0)!.Focus(NavigationMethod.Tab));
            comparing.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await Until(() => !window.OwnedWindows.OfType<SessionCompareWindow>().Any());
            await Until(() => ((WorkspaceViewModel)window.DataContext!).Crumbs.Count > depth);
            Assert.Contains(((WorkspaceViewModel)window.DataContext!).Crumbs, crumb => crumb.Label.Contains("worker.exe", StringComparison.Ordinal));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AHostsRowOpensTheProcessThatReachedIt()
    {
        // worker.exe reached another host's 443, more often later than earlier.
        using var session = new TemporarySession();
        List<ObservationRowV1> rows =
        [
            Timed(Lifecycle(1, ObservationKind.Inventory, 300, 1) with { ResourceName = @"C:\Tools\worker.exe" }),
            Timed(Transfer(Second, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 300)
                .Between("192.168.1.10:52000", "142.250.1.1:443")),
        ];
        for (long at = 4; at < 40; at++)
        {
            rows.Add(Timed(Transfer((at * Second / 2) + 1, ObservationKind.Send, AccountingSide.SendSide, 100, 300)
                .Between("192.168.1.10:52000", "142.250.1.1:443")));
        }

        Publish(session.Store, [.. rows.OrderBy(row => row.NativeTicks)], coverage: Ledger());
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            Assert.True(await window.OpenSessionAsync(session.Path));
            Dispatch();
            var workspace = (WorkspaceViewModel)window.DataContext!;
            await workspace.LayoutReady;
            workspace.SelectInterval(new TimeRange(10 * Second, 20 * Second));
            Dispatch();

            // The button beside the graph opens it, as Ctrl+B does.
            window.GetControl<Button>("CompareButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            SessionCompareWindow dialog = await Opened(window);
            await dialog.Compared;
            Dispatch();
            ComparisonRow host = Assert.Single(dialog.Rows);
            Assert.Equal("worker.exe · PID 300 → 142.250.1.1", host.Ends);
            Assert.Equal("TCP at 443", host.Over);

            // Show in the window opens the process whose connections the row compares.
            int depth = workspace.Crumbs.Count;
            Button show = dialog.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, "Show in the window"));
            show.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Until(() => !window.OwnedWindows.OfType<SessionCompareWindow>().Any());
            await Until(() => ((WorkspaceViewModel)window.DataContext!).Crumbs.Count > depth);
            Assert.Contains(((WorkspaceViewModel)window.DataContext!).Crumbs, crumb => crumb.Label.Contains("PID 300", StringComparison.Ordinal));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: Show on the graph marks each edge by how it changed, says so beneath the header, on its card and in the table, and Clear takes it away")]
    public async Task ShowOnTheGraphMarksEachEdgeByHowItChanged()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows(), coverage: Ledger());
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            Assert.True(await window.OpenSessionAsync(session.Path));
            Dispatch();
            var workspace = (WorkspaceViewModel)window.DataContext!;
            await workspace.LayoutReady;
            CultureInfo culture = CultureInfo.CurrentCulture;
            Border bar = window.GetControl<Border>("ComparisonShownBar");
            GraphView graph = window.GetControl<GraphView>("GraphSurface");
            Assert.False(bar.IsVisible);

            // Two intervals with no record in either compare no row, so the graph has nothing to mark.
            workspace.SelectInterval(new TimeRange(3 * Second / 10, 9 * Second / 10));
            Dispatch();
            Task comparing = window.CompareIntervalsAsync();
            SessionCompareWindow dialog = await Opened(window);
            await dialog.Compared;
            dialog.ChooseBaseline(dialog.Offered.ToList().IndexOf("Another interval, typed in seconds…"));
            await dialog.Compared;
            dialog.Enter(0.3.ToString(culture), 0.9.ToString(culture));
            await dialog.CompareAsync();
            Dispatch();
            Assert.NotNull(dialog.Shown);
            Assert.Empty(dialog.Rows);
            Assert.Equal(SessionCompareWindow.NothingOnGraph, Offers.WhyNot(Labelled(dialog, "Show on the graph")));
            dialog.Close();
            await comparing;
            Dispatch();
            Assert.False(workspace.ShowsComparison);

            // Over the scope and the same length before it, the client's exchanges held steady and the worker's rose; with
            // the timeline filling the column, showing them on the graph gives the graph its place back and the keyboard.
            window.ExpandPane(MainPane.Timeline);
            workspace.SelectInterval(new TimeRange(12 * Second, 16 * Second));
            Dispatch();
            comparing = window.CompareIntervalsAsync();
            dialog = await Opened(window);
            await dialog.Compared;
            Dispatch();

            // While the intervals are read again there is nothing yet to show on the graph.
            Task reading = dialog.CompareAsync();
            Assert.Equal("The two intervals are being compared.", Offers.WhyNot(Labelled(dialog, "Show on the graph")));
            await reading;
            Dispatch();
            Button onGraph = Labelled(dialog, "Show on the graph");
            Assert.True(onGraph.IsEnabled);
            Assert.Equal(SessionCompareWindow.OnGraphDoes, AutomationProperties.GetHelpText(onGraph));
            Click(onGraph);
            await comparing;
            Dispatch();
            Assert.Same(dialog.Shown, dialog.ShownOnGraph);
            Assert.Null(window.ExpandedPane);
            Assert.True(graph.IsFocused);
            Assert.True(workspace.ShowsComparison);
            GraphComparisonOverlay overlay = workspace.ComparisonOverlay!;
            Assert.Same(dialog.Shown, overlay.Comparison);
            string steady = overlay.Edges.Single(pair => pair.Value.Mark == GraphComparisonMark.Unchanged).Key;
            string rose = overlay.Edges.Single(pair => pair.Value.Mark == GraphComparisonMark.Increased).Key;
            Assert.Equal(2, overlay.Edges.Count);

            // The line beneath the header says what the marks are, each with its glyph, so no mark is the only place a
            // change is stated.
            Assert.True(bar.IsVisible);
            string said = window.GetControl<TextBlock>("ComparisonShownText").Text!;
            Assert.Equal(workspace.ComparisonShownSaid, said);
            Assert.Equal("The graph marks each edge by how it changed from " + workspace.TimeBase.HalfOpenRange(new TimeRange(8 * Second,
                12 * Second), culture) + " to " + workspace.TimeBase.HalfOpenRange(new TimeRange(12 * Second, 16 * Second), culture)
                + ", by records, a row per process. 2 edges are marked: 1 unchanged =, 1 up ▲.", said);
            Assert.Equal(said, AutomationProperties.GetName(window.GetControl<TextBlock>("ComparisonShownText")));

            // Each mark is drawn on its edge, between the two nodes it joins and clear of both.
            Save(window, "compare-on-graph.png");
            Assert.Equal(new[] { rose, steady }.Order(StringComparer.Ordinal), graph.DrawnComparisonMarks.Keys.Order(StringComparer.Ordinal));
            foreach ((string key, (Point at, GraphComparisonMarked marked)) in graph.DrawnComparisonMarks)
            {
                GraphDisplayEdge edge = workspace.GraphDisplay.Edges.Single(drawn => drawn.Key == key);
                Point from = graph.PointOf(edge.SourceKey)!.Value, to = graph.PointOf(edge.TargetKey)!.Value;
                double length = Distance(from, to);
                Assert.Equal(length, Distance(from, at) + Distance(at, to), 3);
                Assert.True(Distance(from, at) >= graph.RadiusOf(edge.SourceKey)!.Value + GraphView.ComparisonMarkRadius);
                Assert.True(Distance(at, to) >= graph.RadiusOf(edge.TargetKey)!.Value + GraphView.ComparisonMarkRadius);
                Assert.Same(overlay.Edges[key], marked);
            }

            // An edge's card says how it changed in icat compare's words: hovered, the card says it, and says another
            // comparison's once that is shown, the edge still hovered.
            Assert.Contains("Compared: = unchanged", workspace.DescribeGraphHover(steady)!.Lines);
            Assert.Contains("Compared: ▲ up 2.50/s (+500%)", workspace.DescribeGraphHover(rose)!.Lines);
            GraphDisplayEdge rising = workspace.GraphDisplay.Edges.Single(edge => edge.Key == rose);
            Point riseFrom = graph.PointOf(rising.SourceKey)!.Value, riseTo = graph.PointOf(rising.TargetKey)!.Value;
            window.MouseMove(graph.TranslatePoint(riseFrom + ((riseTo - riseFrom) * 0.35), window)!.Value);
            Dispatch();
            Assert.Equal(rose, graph.HoveredKey);
            Assert.Contains("Compared: ▲ up 2.50/s (+500%)", graph.HoverCard!.Lines);
            IntervalComparison shown = dialog.ShownOnGraph!;
            Assert.True(workspace.ShowComparison(shown with
            {
                Relationships = [.. shown.Relationships.Select(row => row.Change == ComparisonChange.Increased
                    ? row with { Change = ComparisonChange.Decreased } : row)],
            }));
            Dispatch();
            Assert.Equal(rose, graph.HoveredKey);
            Assert.Contains(graph.HoverCard!.Lines, line => line.StartsWith("Compared: ▼ down", StringComparison.Ordinal));
            Assert.True(workspace.ShowComparison(shown));
            Dispatch();
            Assert.Contains("Compared: ▲ up 2.50/s (+500%)", graph.HoverCard!.Lines);
            window.MouseMove(new Point(2, 2));
            Dispatch();

            // The relationship table carries each edge's mark, and says how it changed to a screen reader.
            workspace.ShowTables = true;
            Dispatch();
            ListBox table = window.GetControl<ListBox>("RelationshipList");
            Control Row(string key) => table.GetRealizedContainers().Single(row => ((RelationshipRow)row.DataContext!).Key == key);
            Assert.Equal(("=", "▲"), (Row(steady).Tag, Row(rose).Tag));
            Assert.Equal("compared: ▲ up 2.50/s (+500%)", AutomationProperties.GetItemStatus(Row(rose)));
            Assert.Equal("▲", Row(rose).GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "RelationshipCompared").Text);

            // Clear takes the marks away, from the graph, its header, its cards and the table, and gives the keyboard back to
            // the graph, its own button going with the line.
            Button clear = window.GetControl<Button>("ClearComparisonButton");
            Assert.True(clear.Focus());
            Click(clear);
            Assert.False(workspace.ShowsComparison);
            Assert.Null(workspace.ComparisonOverlay);
            Assert.Equal(string.Empty, workspace.ComparisonShownSaid);
            Assert.False(bar.IsVisible);
            Assert.True(graph.IsFocused);
            Assert.Equal((null, null), (Row(steady).Tag, Row(rose).Tag));
            Assert.Null(AutomationProperties.GetItemStatus(Row(rose)));
            Assert.DoesNotContain(workspace.DescribeGraphHover(rose)!.Lines, line => line.StartsWith("Compared", StringComparison.Ordinal));
            workspace.ShowTables = false;
            Save(window, "compare-on-graph-cleared.png");
            Assert.Empty(graph.DrawnComparisonMarks);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R7: a comparison shown on the graph stays through a newer generation, which it says it has not read, and goes with its session")]
    public async Task AComparisonOnTheGraphStaysThroughANewerGeneration()
    {
        using var session = new TemporarySession();
        using var another = new TemporarySession(Guid.Parse("0c0c0c0c-0000-4000-8000-000000000762"));
        Publish(session.Store, Rows(), coverage: Ledger());
        Publish(another.Store, Rows(), coverage: Ledger());
        IntervalComparison comparison = SessionComparison.Compare(session.Store, new TimeRange(8 * Second, 12 * Second),
            new TimeRange(12 * Second, 16 * Second));
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            // The tour's graph reads no session, and a comparison of another session than the one shown is not its own.
            Assert.False(((WorkspaceViewModel)window.DataContext!).ShowComparison(comparison));
            window.ApplyCaptureUpdate(Recording(session));
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            Dispatch();
            Assert.False(workspace.ShowComparison(comparison with { SessionId = Guid.NewGuid() }));
            Assert.False(workspace.ShowsComparison);
            Assert.True(workspace.ShowComparison(comparison));
            Assert.Same(comparison, workspace.CarryComparison());
            GraphComparisonOverlay overlay = workspace.ComparisonOverlay!;
            Assert.Same(overlay, workspace.ComparisonOverlay);
            Assert.Equal(2, overlay.Edges.Count);

            // The next generation holds a relationship the comparison never read: the marks stay where they were, the new
            // edge carries none, and the line says what is new has no mark.
            Publish(session.Store, Exchange(25 * Second, 300, 200, "127.0.0.1:50009", "127.0.0.1:8080"));
            window.ApplyCaptureUpdate(Recording(session));
            var next = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            Assert.NotSame(workspace, next);
            await next.LayoutReady;
            Dispatch();
            Assert.True(next.ShowsComparison);
            Assert.Same(comparison, next.ComparisonOverlay!.Comparison);
            Assert.Equal(overlay.Edges.Keys.Order(StringComparer.Ordinal), next.ComparisonOverlay.Edges.Keys.Order(StringComparer.Ordinal));
            Assert.Equal(1, next.ComparisonOverlay.Unmarked);
            string unread = next.GraphDisplay.Edges.Single(edge => !next.ComparisonOverlay.Edges.ContainsKey(edge.Key)).Key;
            Assert.Contains("Compared: no row, no record in either interval, so no mark", next.DescribeGraphHover(unread)!.Lines);
            Assert.True(window.GetControl<Border>("ComparisonShownBar").IsVisible);
            Assert.EndsWith("1 drawn edge holds no row of it, no record in either interval, and carries no mark. It was read at an "
                + "earlier generation of the session than the window shows, so what the session holds since has no mark; Compare "
                + "with… reads it again.", next.ComparisonShownSaid, StringComparison.Ordinal);

            // Another session shown leaves the comparison behind.
            Assert.True(await window.OpenSessionAsync(another.Path));
            Dispatch();
            var elsewhere = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await elsewhere.LayoutReady;
            Assert.False(elsewhere.ShowsComparison);
            Assert.Null(elsewhere.CarryComparison());
            Assert.False(window.GetControl<Border>("ComparisonShownBar").IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R21: the window's comparison says why a row cannot be compared, each reason once beneath the rows")]
    public async Task TheComparisonSaysWhyARowCannotBeCompared()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows(), coverage: TransportLedger(tcp: true, udp: false) with
        {
            Epochs =
            [
                TransportLedger(tcp: true, udp: false).Epochs[0] with { LastDeliveredNativeTicks = (14 * Second) - 1 },
                TransportLedger(tcp: false, udp: true).Epochs[0] with
                {
                    Epoch = 2, FirstDeliveredNativeTicks = 14 * Second, LastDeliveredNativeTicks = 30 * Second,
                },
            ],
        });
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        using var comparing = new SessionCompareWindow(session.Path, overview.SessionId, new TimeRange(14 * Second, 18 * Second),
            new TimeRange(0, 30 * Second), recorded: true, SessionClock.Session(TimeZoneInfo.Utc), EvidencePolicy.IncludeCorrelated);
        Task<ComparedRelationship?> chosen = comparing.ShowDialog<ComparedRelationship?>(owner);
        await Until(() => comparing.Shown is not null);
        await comparing.Compared;
        Dispatch();

        // A recorded session offers its whole recording; TCP not collected after 14 s makes every row's change unstatable,
        // each saying so by one reason, said once.
        Assert.StartsWith("The whole recording, ", comparing.Offered[2], StringComparison.Ordinal);
        string reason = "TCP was not collected over the compared interval: no admitted descriptor records it.";
        Assert.All(comparing.Rows, row => Assert.Equal(("not comparable [1]", reason), (row.ChangeCell, row.Reason)));
        Assert.Contains(comparing.GetLogicalDescendants().OfType<TextBlock>(), block => block.Text == "[1] " + reason);
        Assert.EndsWith("; not comparable: " + reason, comparing.Rows[0].AccessibleName, StringComparison.Ordinal);
        Save(comparing, "compare-not-comparable.png");

        // A row whose change cannot be stated is still shown in the window; Esc closes the comparison choosing none.
        Assert.Equal(SessionCompareWindow.ShowDoes, Offers.Offered(Labelled(comparing, "Show in the window")));
        Assert.Equal(SessionCompareWindow.CompareDoes, Offers.Offered(Labelled(comparing, "Compare")));
        comparing.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatch();
        Assert.Null(await chosen);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task AScopeNearTheStartIsComparedWithWhatFollowsItCountingTheQuiet()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows(), coverage: Ledger());
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        using var comparing = new SessionCompareWindow(session.Path, overview.SessionId, new TimeRange(2 * Second, 6 * Second),
            new TimeRange(0, 30 * Second), recorded: false, SessionClock.Session(TimeZoneInfo.Utc), EvidencePolicy.IncludeCorrelated);
        _ = comparing.ShowDialog<ComparedRelationship?>(owner);
        await Until(() => comparing.Shown is not null);
        await comparing.Compared;

        // Four seconds before two would start before the session, so the four after it come first; the worker's connection,
        // made at 10.5 s, is quiet in both.
        CultureInfo culture = CultureInfo.CurrentCulture;
        Assert.StartsWith($"The {WorkspaceTime.FormatDuration(4 * Second, culture)} after it, ", comparing.Offered[0], StringComparison.Ordinal);
        Assert.Equal(4, comparing.Offered.Count);
        Assert.StartsWith("The whole session, ", comparing.Offered[1], StringComparison.Ordinal);
        Assert.Equal(SessionCompareWindow.ChooseCapture, comparing.Offered[^1]);
        Assert.Equal($"Generation {1.ToString("N0", culture)} · 1 relationship between processes compared, the most changed first "
            + "· 1 quiet in both", comparing.Said);
        comparing.Close();
        owner.Close();
    }

    [AvaloniaFact]
    public async Task MoreRowsThanTheListHoldsAreCountedBeneathIt()
    {
        // Two hundred and five processes each reached another host once, in the compared interval alone.
        using var session = new TemporarySession();
        int hosts = SessionCompareWindow.Listed + 5;
        Publish(session.Store, [.. Enumerable.Range(0, hosts).Select(index => Timed(Transfer((5 * Second) + index,
            ObservationKind.Send, AccountingSide.SendSide, 10, 1_000 + index)
            .Between(string.Create(CultureInfo.InvariantCulture, $"192.168.1.10:{20_000 + index}"), "142.250.1.1:443")))],
            coverage: Ledger());
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        using var comparing = new SessionCompareWindow(session.Path, overview.SessionId, new TimeRange(4 * Second, 8 * Second),
            new TimeRange(0, 30 * Second), recorded: true, SessionClock.Session(TimeZoneInfo.Utc), EvidencePolicy.IncludeCorrelated);
        _ = comparing.ShowDialog<ComparedRelationship?>(owner);
        await Until(() => comparing.Shown is not null);
        await comparing.Compared;
        Dispatch();

        // The list holds the most changed, and says how many more there are.
        Assert.Equal((hosts, SessionCompareWindow.Listed), (comparing.Shown!.Relationships.Count, comparing.Rows.Count));
        Assert.Contains(comparing.GetLogicalDescendants().OfType<TextBlock>(),
            block => block.Text?.StartsWith("5 more, less changed, are not listed.\n", StringComparison.Ordinal) == true);
        comparing.Close();
        owner.Close();
    }

    [AvaloniaFact(DisplayName = "I22: Share redacted… asks first, then saves every row compared where chosen, naming no process")]
    public async Task ShareRedactedSavesTheComparisonAsAReport()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows(), coverage: Ledger());
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        DirectoryInfo folder = Directory.CreateTempSubdirectory("intercat-compare-share-");
        using var comparing = new SessionCompareWindow(session.Path, overview.SessionId, new TimeRange(12 * Second, 16 * Second),
            new TimeRange(0, 30 * Second), recorded: true, SessionClock.Session(TimeZoneInfo.Utc), EvidencePolicy.IncludeCorrelated);
        var choices = new AnsweredChoices(comparing);
        comparing.Choices = choices;
        try
        {
            // Nothing is shared before a comparison is shown.
            Button share = Labelled(comparing, "Share redacted…");
            Assert.Equal(SessionCompareWindow.NothingToShare, Offers.WhyNot(share));
            _ = comparing.ShowDialog<ComparedRelationship?>(owner);
            await Until(() => comparing.Shown is not null);
            await comparing.Compared;
            Dispatch();
            Assert.Equal(SessionCompareWindow.ShareDoes, Offers.Offered(share));
            string found = comparing.Said;

            // Declined, no file is asked for, and nothing is said. While it is asked, the comparison waits to be shown on the
            // graph, which would close the window under the report.
            Click(share);
            Window sharing = await Prompt(comparing);
            Assert.Equal("A redacted report is being saved.", Offers.WhyNot(Labelled(comparing, "Show on the graph")));
            Press(sharing, "Cancel");
            await comparing.Shared;
            Assert.Equal((0, string.Empty), (choices.Asked, comparing.Done));
            Assert.Equal(SessionCompareWindow.OnGraphDoes, Offers.Offered(Labelled(comparing, "Show on the graph")));

            // Asked for, every row compared is saved where chosen, and where is said beside the buttons, the comparison's
            // own status still saying what it found.
            string report = Path.Combine(folder.FullName, "comparison.json");
            choices.Answer(report);
            Click(share);
            Press(await Prompt(comparing), "Choose report file");
            await comparing.Shared;
            Dispatch();
            Assert.Equal(("Save redacted comparison report", RedactedShareExport.SuggestedName("json")),
                (choices.Saves[0].Title, choices.Saves[0].SuggestedFileName));
            Assert.Equal($"Saved a redacted report of 2 rows to {report}. Review it before sharing it.", comparing.Done);
            Assert.Equal(found, comparing.Said);
            string written = File.ReadAllText(report);
            foreach (string kept in new[] { "client.exe", "server.exe", "worker.exe", "queue.exe", "127.0.0.1", "PID ", session.Path })
                Assert.DoesNotContain(kept, written, StringComparison.OrdinalIgnoreCase);
            using (JsonDocument json = JsonDocument.Parse(written))
            {
                Assert.Equal(("comparison", 2), (json.RootElement.GetProperty("kind").GetString(),
                    json.RootElement.GetProperty("rows").GetArrayLength()));
            }

            Save(comparing, "compare-shared.png");

            // A name ending .csv saves CSV; a file that cannot be written is said, and sharing may be asked for again.
            string csv = Path.Combine(folder.FullName, "comparison.csv");
            choices.Answer(csv);
            Click(share);
            Press(await Prompt(comparing), "Choose report file");
            await comparing.Shared;
            Assert.StartsWith("contract,report_id,policy,kind,", File.ReadAllText(csv), StringComparison.Ordinal);
            choices.Answer(folder.FullName);
            Click(share);
            Press(await Prompt(comparing), "Choose report file");
            await comparing.Shared;
            Dispatch();
            Assert.StartsWith("Could not save the redacted report: ", comparing.Done, StringComparison.Ordinal);
            Assert.Equal(SessionCompareWindow.ShareDoes, Offers.Offered(share));

            // Comparing again lets what was said of the report before go.
            comparing.CompareByBytes(true);
            await comparing.Compared;
            Assert.Equal(string.Empty, comparing.Done);
        }
        finally
        {
            comparing.Close();
            owner.Close();
            folder.Delete(recursive: true);
        }
    }

    [AvaloniaFact(DisplayName = "R22: the window compares executables, a restarted process's executable one row, and opens its rung")]
    public async Task TheWindowComparesExecutables()
    {
        // client.exe's process exchanged with server.exe until it exited at 9.5 s, and its next process from 10.5 s; the
        // session's records begin at its start, so the ten seconds before the scope are within it.
        using var session = new TemporarySession();
        List<ObservationRowV1> rows =
        [
            Timed(Lifecycle(0, ObservationKind.Inventory, 100, 1) with { ResourceName = @"C:\Tools\client.exe" }),
            Timed(Lifecycle(0, ObservationKind.Inventory, 200, 2) with { ResourceName = @"C:\Tools\server.exe" }),
            .. Opened(Second / 2, 100, 200, "127.0.0.1:50000", "127.0.0.1:8080"),
            Timed(Lifecycle(95 * Second / 10, ObservationKind.Exit, 100, (ulong)(95 * Second / 10))),
            Timed(Lifecycle(105 * Second / 10, ObservationKind.Create, 150, (ulong)(105 * Second / 10)) with
            {
                ResourceName = @"C:\Tools\client.exe",
            }),
            .. Opened(105 * Second / 10 + 100, 150, 200, "127.0.0.1:50001", "127.0.0.1:8080"),
        ];
        for (long at = 1; at < 9; at++) rows.AddRange(Exchange(at * Second, 100, 200, "127.0.0.1:50000", "127.0.0.1:8080"));
        for (long at = 11; at < 19; at++) rows.AddRange(Exchange(at * Second, 150, 200, "127.0.0.1:50001", "127.0.0.1:8080"));
        Publish(session.Store, [.. rows.OrderBy(row => row.NativeTicks)], coverage: Ledger());
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            Assert.True(await window.OpenSessionAsync(session.Path));
            Dispatch();
            var workspace = (WorkspaceViewModel)window.DataContext!;
            await workspace.LayoutReady;
            workspace.SelectInterval(new TimeRange(10 * Second, 20 * Second));
            Dispatch();
            Task opened = window.CompareIntervalsAsync();
            SessionCompareWindow comparing = await Opened(window);
            await comparing.Compared;
            Dispatch();

            // A session opened on its own has no investigation to save the comparison in, which Save says.
            Assert.Equal(SessionCompareWindow.SaveNeedsInvestigation, Offers.WhyNot(Labelled(comparing, "Save in the investigation…")));

            // By process, the restart is a relationship that disappeared and another that appeared.
            Assert.Equal(["appeared", "disappeared"], comparing.Rows.Select(row => row.ChangeCell).Order());
            Assert.Equal(SessionCompareWindow.RowsDoes, Offers.Offered(RowsChoice(comparing)));

            // By executable, the two processes are client.exe's one row, unchanged, as its notes say why.
            comparing.CompareByExecutable(true);
            await comparing.Compared;
            Dispatch();
            ComparisonRow row = Assert.Single(comparing.Rows);
            Assert.Equal("client.exe → server.exe | TCP at 8080 | unchanged", $"{row.Ends} | {row.Over} | {row.ChangeCell}");
            Assert.StartsWith($"Generation {1.ToString("N0", CultureInfo.CurrentCulture)} · 1 relationship between executables compared",
                comparing.Said, StringComparison.Ordinal);
            Assert.Contains(comparing.GetLogicalDescendants().OfType<TextBlock>(), block => block.Text?.Contains(
                "Each row is an executable's processes together, grouped by the path their image was witnessed at", StringComparison.Ordinal) == true);
            Save(comparing, "compare-executables.png");

            // Show in the window opens client.exe's rung, its processes listed.
            Assert.Equal(SessionCompareWindow.ShowDoes, Offers.Offered(Labelled(comparing, "Show in the window")));
            Click(Labelled(comparing, "Show in the window"));
            await opened;
            Dispatch();
            Assert.Contains(workspace.Crumbs, crumb => crumb.Label.Contains("client.exe", StringComparison.Ordinal));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "I12: the window compares its scope with another saved capture by executable, and says why a folder is none")]
    public async Task TheWindowComparesWithAnotherCapture()
    {
        // The same exchanges in two captures: this session's, and another saved beside it. The scope is all of this session,
        // as long as all of the other.
        using var other = new TemporarySession();
        using var session = new TemporarySession(Guid.Parse("0c0c0c0c-0000-4000-8000-000000000757"));
        Publish(other.Store, Rows(), coverage: Ledger());
        Publish(session.Store, Rows(), coverage: Ledger());
        DirectoryInfo empty = Directory.CreateTempSubdirectory("intercat-compare-none-");
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        using var comparing = new SessionCompareWindow(session.Path, overview.SessionId, overview.Extent!.Value,
            overview.Extent.Value, recorded: false, SessionClock.Session(TimeZoneInfo.Utc), EvidencePolicy.IncludeCorrelated)
        {
            // As a window grouped by terminal session would, it cannot open an executable's rows.
            WhyNotShown = row => row.Executables is not null || row.ExecutableHost is not null ? "Not in this window." : null,
        };
        var choices = new AnsweredChoices(comparing);
        comparing.Choices = choices;
        try
        {
            _ = comparing.ShowDialog<ComparedRelationship?>(owner);
            await Until(() => comparing.Shown is not null);
            await comparing.Compared;
            Dispatch();
            string found = comparing.Said;
            Assert.Equal(SessionCompareWindow.ShowDoes, Offers.Offered(Labelled(comparing, "Show in the window")));
            Assert.Equal(SessionCompareWindow.SaveNeedsInvestigation, Offers.WhyNot(Labelled(comparing, "Save in the investigation…")));
            int choose = comparing.Offered.ToList().IndexOf(SessionCompareWindow.ChooseCapture);
            Assert.True(choose > 0);

            // Declined, a folder that holds no session, and this session's own folder: each leaves the baseline as it was.
            foreach ((string[] Answer, string? Said) attempt in new (string[], string?)[]
            {
                ([], null),
                ([empty.FullName], "That folder holds no session to compare with: "),
                ([session.Path], "That folder holds the session shown: choose another capture, or an interval of this one."),
            })
            {
                choices.AnswerFolders(attempt.Answer);
                comparing.ChooseBaseline(choose);
                await comparing.CaptureChosen;
                Dispatch();
                Assert.Equal((0, found), (comparing.Offered.ToList().IndexOf(SelectedBaseline(comparing)), comparing.Said));
                if (attempt.Said is null) Assert.Equal(string.Empty, comparing.Done);
                else Assert.StartsWith(attempt.Said, comparing.Done, StringComparison.Ordinal);
            }

            // Another capture's folder offers its whole session as the baseline, the rows by executable, which two captures
            // compare by alone.
            choices.AnswerFolders(other.Path);
            comparing.ChooseBaseline(choose);
            await comparing.CaptureChosen;
            await comparing.Compared;
            Dispatch();
            string offered = $"The whole session of {Path.GetFileName(other.Path)}, ";
            Assert.StartsWith(offered, comparing.Offered[choose], StringComparison.Ordinal);
            Assert.Equal(SessionCompareWindow.ChooseCapture, comparing.Offered[choose + 1]);
            Assert.True(comparing.ComparesExecutables);
            Assert.Equal(SessionCompareWindow.AcrossCaptures, Offers.WhyNot(RowsChoice(comparing)));
            Assert.Equal(TestSessions.Session, comparing.Shown!.BaselineSessionId);
            Assert.StartsWith("Generation 1, the baseline capture's 1 · ", comparing.Said, StringComparison.Ordinal);
            Assert.Contains(comparing.GetLogicalDescendants().OfType<TextBlock>(), block => block.Text?.Contains(
                "Each row pairs the processes each capture ran from one path", StringComparison.Ordinal) == true);
            Assert.All(comparing.Rows, row => Assert.Equal("unchanged", row.ChangeCell));
            Assert.Equal("Not in this window.", Offers.WhyNot(Labelled(comparing, "Show in the window")));

            // Drawn column by column, the two captures' rows are cut each on its own clock.
            comparing.DrawTimeline(true);
            await Until(() => comparing.Timeline is not null);
            await comparing.Compared;
            Assert.Equal((TestSessions.Session, 20), (comparing.Timeline!.Whole.BaselineSessionId!.Value, comparing.Timeline.Columns.Count));
            comparing.DrawTimeline(false);

            // Choosing another capture again keeps one offered, the latest; an interval of this session lets the rows be
            // chosen again.
            choices.AnswerFolders(other.Path);
            comparing.ChooseBaseline(choose + 1);
            await comparing.CaptureChosen;
            await comparing.Compared;
            Assert.Single(comparing.Offered, label => label.StartsWith(offered, StringComparison.Ordinal));
            comparing.ChooseBaseline(0);
            await comparing.Compared;
            Assert.Equal(SessionCompareWindow.RowsDoes, Offers.Offered(RowsChoice(comparing)));
            Assert.Null(comparing.Shown!.BaselineSessionId);
        }
        finally
        {
            comparing.Close();
            owner.Close();
            empty.Delete(recursive: true);
        }
    }

    [AvaloniaFact(DisplayName = "R22: the Compare window saves the comparison shown in its investigation by name, and a saved one opens as saved")]
    public async Task TheWindowSavesAComparisonInItsInvestigation()
    {
        // This session, a member of the investigation, and another capture of the same exchanges, which is not one yet.
        using var other = new TemporarySession();
        using var session = new TemporarySession(Guid.Parse("0c0c0c0c-0000-4000-8000-000000000759"));
        Publish(other.Store, Rows(), coverage: Ledger(), capture: CaptureId.New(), clock: ClockFor(Clock, "another-host"));
        Publish(session.Store, Rows(), coverage: Ledger());
        other.Store.ReleaseSegmentReaders();
        session.Store.ReleaseSegmentReaders();
        DirectoryInfo folder = Directory.CreateTempSubdirectory("intercat-compare-saved-");
        string workspace = Path.Combine(folder.FullName, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, DateTimeOffset.UtcNow);
        InvestigationWorkspace.Add(workspace, session.Path, DateTimeOffset.UtcNow);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        SessionClock clock = SessionClock.Session(TimeZoneInfo.Utc);
        var scope = new TimeRange(12 * Second, 16 * Second);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        var comparing = new SessionCompareWindow(session.Path, overview.SessionId, scope, overview.Extent!.Value, recorded: false,
            clock, EvidencePolicy.IncludeCorrelated, workspace);
        var choices = new AnsweredChoices(comparing);
        comparing.Choices = choices;
        bool closedFirst = false;
        Window? reopened = null;
        try
        {
            _ = comparing.ShowDialog<ComparedRelationship?>(owner);
            await Until(() => comparing.Shown is not null);
            await comparing.Compared;
            Dispatch();
            Button save = Labelled(comparing, "Save in the investigation…");
            Assert.Equal(SessionCompareWindow.SaveDoes, Offers.Offered(save));

            // Saved under a name asked for: offered once one is typed, and said before it is saved.
            async Task<Window> Asked()
            {
                Click(save);
                return await Prompt(comparing);
            }

            static TextBlock Said(Window prompt) => prompt.GetLogicalDescendants().OfType<TextBlock>()
                .Single(block => AutomationProperties.GetName(block) == "What saving under this name does");
            Window prompt = await Asked();
            TextBox name = prompt.GetLogicalDescendants().OfType<TextBox>().Single();
            Assert.Equal("Name the comparison first.", Offers.WhyNot(Labelled(prompt, "Save")));
            Assert.Equal("The comparison is being saved in the investigation.", Offers.WhyNot(Labelled(comparing, "Show on the graph")));
            SavedComparison asked = new()
            {
                Revision = 0,
                Name = string.Empty,
                Baseline = new SavedComparisonInterval { SessionId = overview.SessionId, StartTicks = 8 * Second, EndTicks = 12 * Second },
                Compared = new SavedComparisonInterval { SessionId = overview.SessionId, StartTicks = scope.StartTicks, EndTicks = scope.EndTicks },
                Metric = ComparisonMetric.Records,
                GroupedBy = ComparisonGrouping.Process,
                RecordedUtc = DateTimeOffset.UtcNow,
            };
            Assert.Equal($"case.icat-workspace keeps this comparison under a name: {ComparisonText.Saved(asked, CultureInfo.CurrentCulture)}. "
                + "It keeps what to compare and never the answer, which is read afresh each time it is opened.",
                prompt.GetLogicalDescendants().OfType<TextBlock>().First().Text);
            name.Text = " before the burst ";
            Dispatch();
            Assert.Equal("Saves it as 'before the burst'.", Said(prompt).Text);
            Save(prompt, "compare-save-prompt.png");
            Press(prompt, "Save");
            await comparing.SavedIn;
            Dispatch();
            Assert.Equal("Saved in case.icat-workspace as 'before the burst'; the investigation's Comparisons… opens it again.",
                comparing.Done);
            Assert.Equal(SessionCompareWindow.OnGraphDoes, Offers.Offered(Labelled(comparing, "Show on the graph")));
            SavedComparison saved = Assert.Single(InvestigationWorkspace.ComparisonsInForce(InvestigationWorkspace.Read(workspace)));
            Assert.Equal((new TimeRange(8 * Second, 12 * Second), scope, ComparisonMetric.Records, ComparisonGrouping.Process),
                (saved.Baseline!.Interval!.Value, saved.Compared!.Interval!.Value, saved.Metric!.Value, saved.GroupedBy!.Value));
            Assert.Equal((overview.SessionId, overview.SessionId), (saved.Baseline.SessionId, saved.Compared.SessionId));

            // Under a name kept, in any case, the prompt says saving replaces it; declined, nothing is saved, and saved, it is
            // replaced, by bytes.
            comparing.CompareByBytes(true);
            await comparing.Compared;
            prompt = await Asked();
            prompt.GetLogicalDescendants().OfType<TextBox>().Single().Text = "BEFORE the burst";
            Dispatch();
            Assert.Equal("Replaces the comparison saved as 'before the burst'; its earlier revisions stay in the file.", Said(prompt).Text);
            Press(prompt, "Cancel");
            await comparing.SavedIn;
            Assert.Single(InvestigationWorkspace.Read(workspace).Comparisons);
            prompt = await Asked();
            prompt.GetLogicalDescendants().OfType<TextBox>().Single().Text = "BEFORE the burst";
            Dispatch();
            Press(prompt, "Save");
            await comparing.SavedIn;
            saved = Assert.Single(InvestigationWorkspace.ComparisonsInForce(InvestigationWorkspace.Read(workspace)));
            Assert.Equal(("BEFORE the burst", 2, ComparisonMetric.BytesSent), (saved.Name, saved.Revision, saved.Metric!.Value));

            // Another capture the investigation does not hold is not saved there, and the window says to add it first; added,
            // it is saved, by executable.
            choices.AnswerFolders(other.Path);
            comparing.ChooseBaseline(comparing.Offered.ToList().IndexOf(SessionCompareWindow.ChooseCapture));
            await comparing.CaptureChosen;
            await comparing.Compared;
            Dispatch();
            Click(save);
            await Until(() => comparing.SavedIn.IsCompleted || comparing.OwnedWindows.Count > 0);
            Assert.Empty(comparing.OwnedWindows);
            await comparing.SavedIn;
            Dispatch();
            Assert.Equal($"The comparison was not saved: the baseline capture, session {TestSessions.Session.ToString("N")[..8]}, is "
                + "not a session of case.icat-workspace. Add it to the investigation, then save.", comparing.Done);
            InvestigationWorkspace.Add(workspace, other.Path, DateTimeOffset.UtcNow);
            prompt = await Asked();
            prompt.GetLogicalDescendants().OfType<TextBox>().Single().Text = "yesterday";
            Dispatch();
            Press(prompt, "Save");
            await comparing.SavedIn;
            SavedComparison across = InvestigationWorkspace.ComparisonNamed(InvestigationWorkspace.Read(workspace), "yesterday");
            Assert.Equal((true, ComparisonGrouping.Executable, TestSessions.Session), (across.AcrossCaptures, across.GroupedBy!.Value,
                across.Baseline!.SessionId));
            comparing.Close();
            closedFirst = true;

            // Opened as saved, the window offers its baseline first, chosen, of the other capture's folder, and compares it as
            // saved: by bytes, of executables, which two captures compare by alone.
            var opened = new SessionCompareWindow(session.Path, overview.SessionId, across.Compared!.Interval!.Value,
                overview.Extent.Value, recorded: false, clock, EvidencePolicy.IncludeCorrelated, workspace,
                new SavedComparisonOpening(across, other.Path, workspace));
            reopened = opened;
            _ = opened.ShowDialog<ComparedRelationship?>(owner);
            await Until(() => opened.Shown is not null);
            await opened.Compared;
            Dispatch();
            Assert.Equal($"As saved in 'yesterday', {WorkspaceTime.FormatHalfOpenRange(across.Baseline.Interval!.Value, CultureInfo.CurrentCulture)} "
                + $"of {Path.GetFileName(other.Path)}", opened.Offered[0]);
            Assert.Equal(opened.Offered[0], SelectedBaseline(opened));
            Assert.Contains(opened.GetLogicalDescendants().OfType<TextBlock>(), block => block.Text?.EndsWith(", as saved in 'yesterday'",
                StringComparison.Ordinal) == true);
            Assert.Equal((TestSessions.Session, across.Baseline.Interval!.Value, ComparisonMetric.BytesSent, ComparisonGrouping.Executable),
                (opened.Shown!.BaselineSessionId!.Value, opened.Shown.Baseline, opened.Shown.Metric, opened.Shown.Grouping));
            Assert.True(opened.ComparesExecutables);
            Assert.Equal(SessionCompareWindow.SaveDoes, Offers.Offered(Labelled(opened, "Save in the investigation…")));
            Save(opened, "compare-opened-as-saved.png");

            // Without the other capture's folder, its baseline is not offered: it would be read from this session instead.
            using var unfound = new SessionCompareWindow(session.Path, overview.SessionId, across.Compared.Interval!.Value,
                overview.Extent.Value, recorded: false, clock, EvidencePolicy.IncludeCorrelated, workspace,
                new SavedComparisonOpening(across, null, workspace));
            Assert.DoesNotContain(unfound.Offered, label => label.StartsWith("As saved in", StringComparison.Ordinal));
        }
        finally
        {
            if (!closedFirst) comparing.Close();
            reopened?.Close();
            owner.Close();
            folder.Delete(recursive: true);
        }
    }

    [AvaloniaFact(DisplayName = "R22: the Compare window says the notes about the saved comparison it shows, marks each noted row, and adds one about it or its chosen row")]
    public async Task TheWindowSaysAndAddsNotesAboutASavedComparison()
    {
        using var session = new TemporarySession(Guid.Parse("0c0c0c0c-0000-4000-8000-000000000772"));
        Publish(session.Store, Rows(), coverage: Ledger());
        session.Store.ReleaseSegmentReaders();
        DirectoryInfo folder = Directory.CreateTempSubdirectory("intercat-compare-notes-");
        (string workspace, SessionOverviewBundle overview, SavedComparison saved) = Noted(session, folder);
        IntervalComparison read = SessionComparison.Compare(session.Store, saved.Baseline!.Interval!.Value, saved.Compared!.Interval!.Value,
            ComparisonMetric.Records, EvidencePolicy.IncludeCorrelated, ComparisonGrouping.Process);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        var comparing = new SessionCompareWindow(session.Path, overview.SessionId, saved.Compared.Interval!.Value, overview.Extent!.Value,
            recorded: false, SessionClock.Session(TimeZoneInfo.Utc), EvidencePolicy.IncludeCorrelated, workspace,
            new SavedComparisonOpening(saved, null, workspace));
        try
        {
            _ = comparing.ShowDialog<ComparedRelationship?>(owner);
            await Until(() => comparing.Shown is not null);
            await comparing.Compared;
            Dispatch();

            // Opened as saved, it says the notes about it in the order written - a row's by its rank and ends, one about a
            // row it does not compare as such - and marks the row a note is about, in the list and as a screen reader says it.
            Assert.Equal(2, comparing.Rows.Count);
            Assert.Equal("3 notes about 'before the burst', the first written first:", comparing.NotesSaid);
            Assert.Equal(["About the comparison: The burst is the nightly backup's.", $"About row 2, {comparing.Rows[1].Ends}: Steady, as expected.",
                "About a row the comparison shown does not compare: Gone since the update."], comparing.NotesListed);
            Assert.Equal((0, 1), (comparing.Rows[0].Noted, comparing.Rows[1].Noted));
            Assert.Equal(comparing.Rows[1].Ends + " · 1 note", comparing.Rows[1].EndsCell);
            Assert.Contains(RowsList(comparing).GetVisualDescendants().OfType<TextBlock>(), text => text.Text == comparing.Rows[1].EndsCell);
            Assert.EndsWith("; 1 note about it, beneath the rows", comparing.Rows[1].AccessibleName, StringComparison.Ordinal);
            Assert.DoesNotContain("note", comparing.Rows[0].AccessibleName, StringComparison.Ordinal);
            Button write = Labelled(comparing, "Add a note…");
            Assert.Equal(SessionCompareWindow.NoteDoes, Offers.Offered(write));
            Save(comparing, "compare-notes.png");

            // A note about the chosen row is about the whole comparison until the row is chosen instead; written, it is said
            // with the others, the row marked again, and the row stays chosen.
            comparing.ChooseRow(1);
            Click(write);
            var writing = Assert.IsType<InvestigationNoteWindow>(await Prompt(comparing));
            Assert.Equal("Add a note about a comparison", writing.Title);
            RadioButton whole = Choice(writing, "About the comparison 'before the burst' as a whole");
            RadioButton row = Choice(writing, $"About its row 2: {comparing.Rows[1].Ends}");
            Assert.Equal((true, true), (whole.IsChecked == true, row.IsEnabled));
            Assert.Equal("A note is being written to the investigation.", Offers.WhyNot(write));
            Save(writing, "compare-note-prompt.png");
            writing.AboutTheRow(true);
            writing.Enter("Steadier than the day before.");
            Press(writing, "Add note");
            await comparing.Noted;
            Dispatch();
            Assert.Equal("Added a note about row 2 of 'before the burst' to case.icat-workspace.", comparing.Done);
            Assert.Equal("4 notes about 'before the burst', the first written first:", comparing.NotesSaid);
            Assert.Equal($"About row 2, {comparing.Rows[1].Ends}: Steadier than the day before.", comparing.NotesListed[3]);
            Assert.Equal((comparing.Rows[1].Ends + " · 2 notes", 1), (comparing.Rows[1].EndsCell, RowsList(comparing).SelectedIndex));
            Assert.Equal(new WorkspaceNoteSubject("before the burst", read.Relationships[1].Key),
                InvestigationWorkspace.NotesAbout(InvestigationWorkspace.Read(workspace), "before the burst")[3].About);
            Assert.Equal(SessionCompareWindow.NoteDoes, Offers.Offered(write));

            // About the first row; then, its row still chosen, about the whole comparison, as the prompt has it first.
            comparing.ChooseRow(0);
            Click(write);
            writing = Assert.IsType<InvestigationNoteWindow>(await Prompt(comparing));
            writing.AboutTheRow(true);
            writing.Enter("The worker drains its queue here.");
            Press(writing, "Add note");
            await comparing.Noted;
            Dispatch();
            Assert.Equal("Added a note about row 1 of 'before the burst' to case.icat-workspace.", comparing.Done);
            Assert.Equal((comparing.Rows[0].Ends + " · 1 note", $"About row 1, {comparing.Rows[0].Ends}: The worker drains its queue here."),
                (comparing.Rows[0].EndsCell, comparing.NotesListed[4]));
            Click(write);
            writing = Assert.IsType<InvestigationNoteWindow>(await Prompt(comparing));
            writing.Enter("Compare again after the patch.");
            Press(writing, "Add note");
            await comparing.Noted;
            Dispatch();
            Assert.Equal("Added a note about 'before the burst' to case.icat-workspace.", comparing.Done);
            Assert.Equal("About the comparison: Compare again after the patch.", comparing.NotesListed[5]);

            // With no row chosen, only the whole comparison can be noted; declined, nothing is written.
            comparing.ChooseRow(-1);
            Click(write);
            writing = Assert.IsType<InvestigationNoteWindow>(await Prompt(comparing));
            Assert.Equal("Choose a row in the comparison's list first.", Offers.WhyNot(Choice(writing, "About its chosen row")));
            Press(writing, "Cancel");
            await comparing.Noted;
            Assert.Equal(6, InvestigationWorkspace.NotesAbout(InvestigationWorkspace.Read(workspace), "before the burst").Count);
            Assert.Equal(6, comparing.NotesListed.Count);

            // A baseline still to type compares nothing, so nothing is said of notes.
            comparing.ChooseBaseline(comparing.Offered.ToList().IndexOf("Another interval, typed in seconds…"));
            await comparing.Compared;
            Dispatch();
            Assert.Equal((string.Empty, 0), (comparing.NotesSaid, comparing.NotesListed.Count));
            Assert.Equal(SessionCompareWindow.NothingToShare, Offers.WhyNot(write));
            comparing.ChooseBaseline(0);
            await comparing.Compared;

            // Compared by bytes, it is no comparison the investigation saved: it says so and marks no row; saved, it says the
            // notes about it - none yet - and can be noted.
            comparing.CompareByBytes(true);
            await comparing.Compared;
            Dispatch();
            Assert.Equal("Save the comparison in the investigation to write notes about it and its rows.", comparing.NotesSaid);
            Assert.Empty(comparing.NotesListed);
            Assert.All(comparing.Rows, shown => Assert.Equal(0, shown.Noted));
            Assert.Equal(SessionCompareWindow.NoteNeedsSaving, Offers.WhyNot(write));
            Click(Labelled(comparing, "Save in the investigation…"));
            Window prompt = await Prompt(comparing);
            prompt.GetLogicalDescendants().OfType<TextBox>().Single().Text = "by bytes";
            Dispatch();
            Press(prompt, "Save");
            await comparing.SavedIn;
            Dispatch();
            Assert.Equal("No note about 'by bytes' yet: Add a note… writes one about it, or about its chosen row.", comparing.NotesSaid);
            Assert.Equal(SessionCompareWindow.NoteDoes, Offers.Offered(write));

            // By records again, it is the comparison saved as 'before the burst' again - until it is saved alike under
            // another name, the name it is then known by.
            comparing.CompareByBytes(false);
            await comparing.Compared;
            Dispatch();
            Assert.Equal("6 notes about 'before the burst', the first written first:", comparing.NotesSaid);
            Click(Labelled(comparing, "Save in the investigation…"));
            prompt = await Prompt(comparing);
            prompt.GetLogicalDescendants().OfType<TextBox>().Single().Text = "again";
            Dispatch();
            Press(prompt, "Save");
            await comparing.SavedIn;
            Dispatch();
            Assert.Equal("No note about 'again' yet: Add a note… writes one about it, or about its chosen row.", comparing.NotesSaid);
        }
        finally
        {
            comparing.Close();
            owner.Close();
            folder.Delete(recursive: true);
        }
    }

    [AvaloniaFact(DisplayName = "R22: the Compare window knows a comparison its investigation saved alike, the one it opened as first, and says why it can keep no note")]
    public async Task TheWindowKnowsAComparisonSavedAlike()
    {
        using var session = new TemporarySession(Guid.Parse("0c0c0c0c-0000-4000-8000-000000000773"));
        Publish(session.Store, Rows(), coverage: Ledger());
        session.Store.ReleaseSegmentReaders();
        DirectoryInfo folder = Directory.CreateTempSubdirectory("intercat-compare-alike-");
        (string workspace, SessionOverviewBundle overview, SavedComparison saved) = Noted(session, folder);
        SavedComparison alike = InvestigationWorkspace.SaveComparison(workspace, "alike", saved.Baseline!, saved.Compared!,
            ComparisonMetric.Records, ComparisonGrouping.Process, DateTimeOffset.UtcNow);
        SessionClock clock = SessionClock.Session(TimeZoneInfo.Utc);
        TimeRange scope = saved.Compared!.Interval!.Value;
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        var windows = new List<SessionCompareWindow>();
        async Task<SessionCompareWindow> Shown(string? investigation, SavedComparisonOpening? opening = null)
        {
            var window = new SessionCompareWindow(session.Path, overview.SessionId, scope, overview.Extent!.Value, recorded: false, clock,
                EvidencePolicy.IncludeCorrelated, investigation, opening);
            windows.Add(window);
            _ = window.ShowDialog<ComparedRelationship?>(owner);
            await Until(() => window.Shown is not null);
            await window.Compared;
            Dispatch();
            return window;
        }

        try
        {
            // Opened on the scope, not as saved, its first baseline - the same length before it - is the comparison saved
            // first alike; opened as the one saved alike after it, it is that one.
            SessionCompareWindow comparing = await Shown(workspace);
            Assert.Equal("3 notes about 'before the burst', the first written first:", comparing.NotesSaid);
            SessionCompareWindow asAlike = await Shown(workspace, new SavedComparisonOpening(alike, null, workspace));
            Assert.Equal("No note about 'alike' yet: Add a note… writes one about it, or about its chosen row.", asAlike.NotesSaid);
            Assert.Equal(SessionCompareWindow.NoteDoes, Offers.Offered(Labelled(asAlike, "Add a note…")));

            // The investigation moved away, its notes cannot be read, which the window says, and why it writes none.
            File.Move(workspace, workspace + ".moved");
            await comparing.CompareAsync();
            Dispatch();
            string gone = $"No file is at {workspace} any more; it was moved, renamed or deleted.";
            Assert.Equal("The notes about it could not be read: " + gone, comparing.NotesSaid);
            Assert.Equal("The investigation could not be read: " + gone, Offers.WhyNot(Labelled(comparing, "Add a note…")));
            Assert.Empty(comparing.NotesListed);

            // A session opened on its own keeps no note, and says why.
            SessionCompareWindow alone = await Shown(null);
            Assert.Equal(string.Empty, alone.NotesSaid);
            Assert.Equal(SessionCompareWindow.NoteNeedsInvestigation, Offers.WhyNot(Labelled(alone, "Add a note…")));
        }
        finally
        {
            foreach (SessionCompareWindow window in windows)
            {
                window.Close();
            }

            owner.Close();
            folder.Delete(recursive: true);
        }
    }

    [AvaloniaFact(DisplayName = "R15: the Compare window with notes beneath its rows cuts off no text at its smallest, keeps its rows in view, and names every element")]
    public async Task TheWindowWithNotesIsLegibleAndHeard()
    {
        using var session = new TemporarySession(Guid.Parse("0c0c0c0c-0000-4000-8000-000000000774"));
        Publish(session.Store, Rows(), coverage: Ledger());
        session.Store.ReleaseSegmentReaders();
        DirectoryInfo folder = Directory.CreateTempSubdirectory("intercat-compare-legible-");
        (string workspace, SessionOverviewBundle overview, SavedComparison saved) = Noted(session, folder);
        InvestigationWorkspace.AddNoteAbout(workspace, string.Join(" ", Enumerable.Repeat("A longer note that wraps across the list's width.", 6)),
            new("before the burst", null), DateTimeOffset.UtcNow);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        var comparing = new SessionCompareWindow(session.Path, overview.SessionId, saved.Compared!.Interval!.Value, overview.Extent!.Value,
            recorded: false, SessionClock.Session(TimeZoneInfo.Utc), EvidencePolicy.IncludeCorrelated, workspace,
            new SavedComparisonOpening(saved, null, workspace));
        comparing.Width = comparing.MinWidth;
        comparing.Height = comparing.MinHeight;
        try
        {
            _ = comparing.ShowDialog<ComparedRelationship?>(owner);
            await Until(() => comparing.Shown is not null);
            await comparing.Compared;
            Dispatch();
            var problems = new List<string>();
            void Legible(string where)
            {
                problems.AddRange(LegibleTextTests.CutOff(comparing, where));
                problems.AddRange(AccessibilityAuditTests.Unheard(comparing, out _).Select(problem => $"{where}: {problem}"));
                ListBox rows = RowsList(comparing);
                if (rows.Bounds.Height < 2 * rows.ContainerFromIndex(0)!.Bounds.Height)
                {
                    problems.Add($"{where}: {rows.Bounds.Height:0} px of rows keeps fewer than two in view.");
                }
            }

            Legible("compare with notes");
            Save(comparing, "compare-notes-smallest.png");
            TextBlock didLine = comparing.GetLogicalDescendants().OfType<TextBlock>()
                .Single(text => AutomationProperties.GetName(text) == "What the window's last action did");
            Assert.False(didLine.IsVisible);

            // The prompt that adds a note, and the window once it says what adding one did.
            Click(Labelled(comparing, "Add a note…"));
            var writing = Assert.IsType<InvestigationNoteWindow>(await Prompt(comparing));
            problems.AddRange(LegibleTextTests.CutOff(writing, "the note prompt"));
            problems.AddRange(AccessibilityAuditTests.Unheard(writing, out _).Select(problem => "the note prompt: " + problem));
            writing.Enter("Seen at the window's smallest.");
            Press(writing, "Add note");
            await comparing.Noted;
            Dispatch();
            Assert.StartsWith("Added a note about", comparing.Done, StringComparison.Ordinal);
            Assert.True(didLine.IsVisible);
            Legible("compare with a note added");
            Save(comparing, "compare-notes-smallest-noted.png");

            // Taller, what is said beneath the rows shows whole, the notes with it, without scrolling.
            comparing.Height = 1000;
            Dispatch();
            ScrollViewer said = comparing.GetVisualDescendants().OfType<ScrollViewer>().Single(scroll => scroll.Content is StackPanel panel
                && panel.Children.OfType<TextBlock>().Any(text => AutomationProperties.GetName(text) == "The notes written about the comparison"));
            _ = comparing.CaptureRenderedFrame();
            Dispatch();
            if (said.Extent.Height > said.Viewport.Height + 1)
            {
                problems.Add($"a taller window: {said.Viewport.Height:0} px shows {said.Extent.Height:0} px of what is said beneath the rows.");
            }

            Save(comparing, "compare-notes-taller.png");
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }
        finally
        {
            comparing.Close();
            owner.Close();
            folder.Delete(recursive: true);
        }
    }

    [AvaloniaFact(DisplayName = "R22: the investigation's Comparisons… opens a saved comparison in InterCat's window as saved, and removes one")]
    public async Task TheInvestigationOpensASavedComparison()
    {
        // This session and another capture of the same exchanges, both of the investigation, and four comparisons saved in
        // it: of this session's two intervals by bytes, of the other capture's first ten seconds, of its executables, and of
        // ten seconds after its records end.
        using var other = new TemporarySession();
        using var session = new TemporarySession(Guid.Parse("0c0c0c0c-0000-4000-8000-000000000760"));
        Publish(other.Store, Rows(), coverage: Ledger(), capture: CaptureId.New(), clock: ClockFor(Clock, "another-host"));
        Publish(session.Store, Rows(), coverage: Ledger());
        other.Store.ReleaseSegmentReaders();
        session.Store.ReleaseSegmentReaders();
        DirectoryInfo folder = Directory.CreateTempSubdirectory("intercat-compare-opened-");
        string workspace = Path.Combine(folder.FullName, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, DateTimeOffset.UtcNow);
        Guid id = InvestigationWorkspace.Add(workspace, session.Path, DateTimeOffset.UtcNow).SessionId;
        Guid otherId = InvestigationWorkspace.Add(workspace, other.Path, DateTimeOffset.UtcNow).SessionId;
        SavedComparison Saved(string name, Guid baselineSession, long from, long to, long start, long end, ComparisonMetric by,
            ComparisonGrouping rows) => InvestigationWorkspace.SaveComparison(workspace, name,
            new SavedComparisonInterval { SessionId = baselineSession, StartTicks = from, EndTicks = to },
            new SavedComparisonInterval { SessionId = id, StartTicks = start, EndTicks = end }, by, rows, DateTimeOffset.UtcNow);
        SavedComparison saved = Saved("before the update", id, 0, 10 * Second, 10 * Second, 30 * Second, ComparisonMetric.BytesSent,
            ComparisonGrouping.Process);
        SavedComparison across = Saved("yesterday", otherId, 0, 10 * Second, 0, 10 * Second, ComparisonMetric.Records,
            ComparisonGrouping.Executable);
        SavedComparison programs = Saved("programs", id, 0, 10 * Second, 10 * Second, 20 * Second, ComparisonMetric.Records,
            ComparisonGrouping.Executable);
        SavedComparison after = Saved("after it ended", id, 0, 10 * Second, 100 * Second, 110 * Second, ComparisonMetric.Records,
            ComparisonGrouping.Process);
        var main = new MainWindow { Width = 1456, Height = 939 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            await Until(() => window.View is not null);

            // The window's pages are in tabs, which its logical tree lists twice; what is drawn is listed once.
            Button button = window.GetVisualDescendants().OfType<Button>().Single(drawn => Equals(drawn.Content, "Comparisons…"));
            Assert.Equal("Open or remove a comparison saved in this investigation", AutomationProperties.GetHelpText(button));
            TextBlock status = window.GetVisualDescendants().OfType<TextBlock>()
                .Single(block => AutomationProperties.GetName(block) == "Investigation status");
            async Task<InvestigationComparisonsWindow> Listing()
            {
                Assert.True(button.Focus());
                Click(button);
                await Until(() => window.OwnedWindows.OfType<InvestigationComparisonsWindow>().Any());
                return window.OwnedWindows.OfType<InvestigationComparisonsWindow>().Single();
            }

            async Task<SessionCompareWindow> Comparing(SavedComparison opened)
            {
                Assert.True(await window.OpenComparisonAsync(opened));
                SessionCompareWindow shown = await Opened(main);
                await Until(() => shown.Shown is not null);
                await shown.Compared;
                Dispatch();
                return shown;
            }

            async Task Done(SessionCompareWindow shown)
            {
                shown.Close();
                await Until(() => main.OwnedWindows.Count == 0);
            }

            SessionCompareWindow comparing;

            // Opened first, a comparison of ten seconds past the session's records opens the session whole, with no time
            // scope, and the comparison all the same: its rows not comparable there, never disappeared, since what the
            // capture covered there is unknown.
            comparing = await Comparing(after);
            Assert.Null(((WorkspaceViewModel)main.DataContext!).ScopeInterval);
            Assert.Equal(new TimeRange(100 * Second, 110 * Second), comparing.Shown!.Compared);
            Assert.NotEmpty(comparing.Shown.Relationships);
            Assert.All(comparing.Shown.Relationships, row => Assert.Equal(ComparisonChange.NotComparable, row.Change));
            await Done(comparing);

            // Listed in icat compare's words, the first chosen, Enter on it opens its session at its compared interval in
            // InterCat's window, and the Compare window on it as saved: that interval, though the session's records end
            // before it does, and its baseline, chosen first.
            InvestigationComparisonsWindow choosing = await Listing();
            await Until(() => choosing.Listed.Count == 4 && choosing.FocusManager?.GetFocusedElement() is ListBoxItem);
            Assert.Equal($"before the update · {ComparisonText.Saved(saved, CultureInfo.CurrentCulture)}", choosing.Listed[0].ToString());
            Save(choosing, "investigation-comparisons.png");
            choosing.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            comparing = await Opened(main);
            await Until(() => comparing.Shown is not null);
            await comparing.Compared;
            Dispatch();
            Assert.StartsWith("As saved in 'before the update', ", comparing.Offered[0], StringComparison.Ordinal);
            Assert.Equal(comparing.Offered[0], SelectedBaseline(comparing));
            Assert.Equal((new TimeRange(0, 10 * Second), new TimeRange(10 * Second, 30 * Second), ComparisonMetric.BytesSent),
                (comparing.Shown!.Baseline, comparing.Shown.Compared, comparing.Shown.Metric));
            Assert.Equal(session.Path, main.GetControl<TextBlock>("CaptureSessionPath").Text);
            await Until(() => status.Text == "Opened 'before the update' in the InterCat window: Session 0c0c0c0c at its compared "
                + "interval, compared as saved.");

            // Opened from the investigation, it can be saved there again, as can any comparison of the session it opened.
            Assert.Equal(SessionCompareWindow.SaveDoes, Offers.Offered(Labelled(comparing, "Save in the investigation…")));
            await Done(comparing);
            Task again = main.CompareIntervalsAsync();
            comparing = await Opened(main);
            await Until(() => comparing.Shown is not null);
            Assert.DoesNotContain(comparing.Offered, label => label.StartsWith("As saved in", StringComparison.Ordinal));
            Assert.Equal(SessionCompareWindow.SaveDoes, Offers.Offered(Labelled(comparing, "Save in the investigation…")));
            await Done(comparing);
            await again;

            // Another capture's interval is read from its folder, where the investigation finds it, by executable, and one
            // session's by executable stays so.
            comparing = await Comparing(across);
            Assert.Equal($"As saved in 'yesterday', {WorkspaceTime.FormatHalfOpenRange(new TimeRange(0, 10 * Second), CultureInfo.CurrentCulture)} "
                + $"of {Path.GetFileName(other.Path)}", comparing.Offered[0]);
            Assert.Equal((otherId, true), (comparing.Shown!.BaselineSessionId!.Value, comparing.ComparesExecutables));
            await Done(comparing);
            comparing = await Comparing(programs);
            Assert.Equal((ComparisonGrouping.Executable, true, (Guid?)null),
                (comparing.Shown!.Grouping, comparing.ComparesExecutables, comparing.Shown.BaselineSessionId));
            await Done(comparing);

            // The session opened on its own, a saved comparison opened on it can still be saved in its investigation, though
            // a comparison of the session's own is not offered it.
            Assert.True(await main.OpenSessionAsync(session.Path));
            Dispatch();
            comparing = await Comparing(saved);
            Assert.Equal(SessionCompareWindow.SaveDoes, Offers.Offered(Labelled(comparing, "Save in the investigation…")));
            await Done(comparing);
            again = main.CompareIntervalsAsync();
            comparing = await Opened(main);
            Assert.Equal(SessionCompareWindow.SaveNeedsInvestigation, Offers.WhyNot(Labelled(comparing, "Save in the investigation…")));
            await Done(comparing);
            await again;

            // Removed, its revisions are kept; with none left, the list says how one is saved, and offers nothing to open.
            choosing = await Listing();
            await Until(() => choosing.Listed.Count == 4);
            choosing.Select(0);
            Click(Labelled(choosing, "Remove"));
            await Until(() => choosing.Listed.Count == 3);
            Assert.Equal("'before the update' is removed; its revisions are kept in the file.", choosing.Status);
            choosing.Close();
            foreach (string name in new[] { "yesterday", "programs", "after it ended" })
            {
                InvestigationWorkspace.RemoveComparison(workspace, name, DateTimeOffset.UtcNow);
            }

            choosing = await Listing();
            await Until(() => choosing.IsActive);
            Assert.Empty(choosing.Listed);
            Assert.True(choosing.GetLogicalDescendants().OfType<TextBlock>().Single(block => block.Text?.StartsWith(
                "No comparison is saved yet.", StringComparison.Ordinal) == true).IsVisible);
            Assert.Equal("No comparison is saved yet.", Offers.WhyNot(Labelled(choosing, "Open")));
            Assert.Equal(8, InvestigationWorkspace.Read(workspace).Comparisons.Count);
            choosing.Close();

            // A comparison whose session, or whose other capture, is not where it was last found is not opened, and says
            // which to relink.
            string recorded = File.ReadAllText(workspace);
            File.WriteAllText(workspace, recorded.Replace(Path.GetFileName(other.Path), "moved-away", StringComparison.Ordinal));
            await window.RefreshAsync();
            Assert.False(await window.OpenComparisonAsync(across));
            Assert.Equal($"'yesterday' compares Session {otherId.ToString("N")[..8]}, which is not where it was last found: relink it "
                + "to open the comparison.", status.Text);
            File.WriteAllText(workspace, recorded.Replace(Path.GetFileName(session.Path), "moved-away", StringComparison.Ordinal));
            await window.RefreshAsync();
            Assert.False(await window.OpenComparisonAsync(saved));
            Assert.Equal("'before the update' compares Session 0c0c0c0c, which is not where it was last found: relink it to open the "
                + "comparison.", status.Text);
            Assert.Empty(main.OwnedWindows);
            window.Close();
        }
        finally
        {
            main.Close();
            folder.Delete(recursive: true);
        }
    }

    [AvaloniaFact(DisplayName = "R21: the investigation's saved comparisons say why one could not be listed, removed or opened, and Escape closes their list")]
    public async Task WhatTheSavedComparisonsCouldNotDoIsSaid()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows(), coverage: Ledger());
        session.Store.ReleaseSegmentReaders();
        DirectoryInfo folder = Directory.CreateTempSubdirectory("intercat-compare-unread-");
        string workspace = Path.Combine(folder.FullName, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, DateTimeOffset.UtcNow);
        Guid id = InvestigationWorkspace.Add(workspace, session.Path, DateTimeOffset.UtcNow).SessionId;
        SavedComparison saved = InvestigationWorkspace.SaveComparison(workspace, "before the update",
            new SavedComparisonInterval { SessionId = id, StartTicks = 0, EndTicks = 10 * Second },
            new SavedComparisonInterval { SessionId = id, StartTicks = 10 * Second, EndTicks = 30 * Second },
            ComparisonMetric.Records, ComparisonGrouping.Process, DateTimeOffset.UtcNow);
        string recorded = File.ReadAllText(workspace);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        try
        {
            // Listed, then unreadable by the time it is removed: the list says why it was not, and the file is as it was.
            {
                var choosing = new InvestigationComparisonsWindow(workspace);
                _ = choosing.ShowDialog<SavedComparison?>(owner);
                await Until(() => choosing.Listed.Count == 1);
                File.WriteAllText(workspace, "{ not an investigation");
                choosing.Select(0);
                Click(Labelled(choosing, "Remove"));
                await Until(() => choosing.Status.Contains("was not removed", StringComparison.Ordinal));
                Assert.StartsWith("'before the update' was not removed: ", choosing.Status, StringComparison.Ordinal);
                Assert.Equal("{ not an investigation", File.ReadAllText(workspace));

                // Escape closes the list, choosing nothing.
                Dispatch();
                choosing.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                await Until(() => !choosing.IsVisible);
            }

            // An investigation that cannot be read lists nothing, and says why.
            {
                var unread = new InvestigationComparisonsWindow(workspace);
                _ = unread.ShowDialog<SavedComparison?>(owner);
                await Until(() => unread.Status.StartsWith("The investigation could not be read: ", StringComparison.Ordinal));
                Assert.Empty(unread.Listed);
                Assert.Equal("The investigation could not be read; the status line says why.", Offers.WhyNot(Labelled(unread, "Open")));
                unread.Close();
            }

            // The investigation's window opens a saved comparison only once it has read the investigation, and only where
            // InterCat's window is open to show it in, saying so.
            using (var window = new InvestigationWindow(workspace, openSession: null))
            {
                window.Show();
                await window.RefreshAsync();
                Assert.False(await window.OpenComparisonAsync(saved));
                Assert.Equal("'before the update' was not opened: The investigation could not be read: Refresh reads it again.",
                    Status(window));
                File.WriteAllText(workspace, recorded);
                await window.RefreshAsync();
                Assert.False(await window.OpenComparisonAsync(saved));
                Assert.Equal("'before the update' was not opened: InterCat's window is not open to show it in.", Status(window));
                window.Close();
            }
        }
        finally
        {
            owner.Close();
            folder.Delete(recursive: true);
        }

        static string Status(Window window) => window.GetVisualDescendants().OfType<TextBlock>()
            .Single(block => AutomationProperties.GetName(block) == "Investigation status").Text ?? string.Empty;
    }

    [AvaloniaFact(DisplayName = "R21: the Compare window draws the chosen row's columns, a column the capture did not collect hatched and said why")]
    public async Task TheWindowDrawsTheChosenRowsColumns()
    {
        // client.exe exchanged with server.exe each second, worker.exe with queue.exe each half second from the thirteenth;
        // the capture collected TCP for its first fifteen seconds only, and UDP alone after them.
        using var session = new TemporarySession();
        CoverageLedgerV1 collected = Ledger();
        Publish(session.Store, Rows(), coverage: collected with
        {
            Epochs =
            [
                collected.Epochs[0] with { LastDeliveredNativeTicks = (15 * Second) - 1 },
                TransportLedger(tcp: false, udp: true).Epochs[0] with
                {
                    Epoch = 2, FirstDeliveredNativeTicks = 15 * Second, LastDeliveredNativeTicks = 40 * Second,
                },
            ],
        });
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        using var comparing = new SessionCompareWindow(session.Path, overview.SessionId, new TimeRange(10 * Second, 20 * Second),
            overview.Extent!.Value, recorded: false, SessionClock.Session(TimeZoneInfo.Utc), EvidencePolicy.IncludeCorrelated);
        try
        {
            _ = comparing.ShowDialog<ComparedRelationship?>(owner);
            await Until(() => comparing.Shown is not null);
            await comparing.Compared;
            Dispatch();

            // Not drawn until asked, no column is read.
            Assert.Null(comparing.Timeline);
            IntervalComparison alone = comparing.Shown!;
            comparing.DrawTimeline(true);
            await Until(() => comparing.Timeline is not null);
            await comparing.Compared;
            Dispatch();

            // Drawn, the comparison is read again with its columns, twenty of each interval, and the chosen row's first
            // column is said in icat compare's words.
            ComparisonTimeline timeline = comparing.Timeline!;
            Assert.Same(timeline.Whole, comparing.Shown);
            Assert.NotSame(alone, comparing.Shown);
            Assert.Equal(20, timeline.Columns.Count);
            Assert.All(comparing.Rows, row => Assert.Equal(20, timeline.Of(row.Compared).Count));
            ComparedRelationship first = comparing.Rows[0].Compared;
            ComparedRelationship cell = timeline.Of(first)[0];
            SessionClock clock = SessionClock.Session(TimeZoneInfo.Utc);
            CultureInfo culture = CultureInfo.CurrentCulture;
            Assert.Equal("Column 1 of 20: " + clock.HalfOpenRange(timeline.Columns[0].Baseline, culture) + " "
                + ComparisonText.Value(cell.Baseline, timeline.Columns[0].Baseline, ComparisonMetric.Records, culture) + " against "
                + clock.HalfOpenRange(timeline.Columns[0].Compared, culture) + " "
                + ComparisonText.Value(cell.Compared, timeline.Columns[0].Compared, ComparisonMetric.Records, culture) + ", "
                + ComparisonText.Change(cell, timeline.Columns[0].Baseline, timeline.Columns[0].Compared, ComparisonMetric.Records, culture)
                + ".", comparing.TimelineSaid);
            Save(comparing, "compare-difference-timeline.png");

            // A column past what the capture collected is drawn hatched, and its words say why it compares nothing.
            int stopped = timeline.Of(first).ToList().FindIndex(each => each.Change == ComparisonChange.NotComparable);
            Assert.Equal(10, stopped);
            comparing.ChooseColumn(stopped);
            Assert.StartsWith("Column 11 of 20: ", comparing.TimelineSaid, StringComparison.Ordinal);
            Assert.EndsWith(", not comparable: TCP was not collected over the compared interval: no admitted descriptor records it.",
                comparing.TimelineSaid, StringComparison.Ordinal);

            // From the keyboard: Right, Home and End move the cursor, and Left stops at the first column.
            DifferenceTimelineControl chart = comparing.GetLogicalDescendants().OfType<DifferenceTimelineControl>().Single();
            Assert.True(chart.Focus());
            foreach ((PhysicalKey Key, int Column) press in new[]
            {
                (PhysicalKey.ArrowRight, 12), (PhysicalKey.End, 20), (PhysicalKey.Home, 1), (PhysicalKey.ArrowLeft, 1), (PhysicalKey.ArrowRight, 2),
            })
            {
                comparing.KeyPressQwerty(press.Key, RawInputModifiers.None);
                Dispatch();
                Assert.StartsWith($"Column {press.Column} of 20: ", comparing.TimelineSaid, StringComparison.Ordinal);
            }

            // A key it does not take leaves the column chosen, which a screen reader hears as the chart's status.
            comparing.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Dispatch();
            Assert.Equal(1, chart.ChosenColumn);
            Avalonia.Automation.Peers.AutomationPeer peer = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(chart);
            Assert.Equal("difference timeline", peer.GetLocalizedControlType());
            Assert.StartsWith("Column 2 of 20: ", peer.GetItemStatus(), StringComparison.Ordinal);

            // A click chooses the column under it.
            Point eleventh = chart.TranslatePoint(new Point(6 + ((chart.Bounds.Width - 12) * 10.5 / 20), 60), comparing)!.Value;
            comparing.MouseDown(eleventh, MouseButton.Left);
            comparing.MouseUp(eleventh, MouseButton.Left);
            Dispatch();
            Assert.StartsWith("Column 11 of 20: ", comparing.TimelineSaid, StringComparison.Ordinal);

            // Another row chosen, the chart draws its columns, its cursor where it was: at the second column, where client.exe
            // exchanged and worker.exe had not yet.
            comparing.ChooseColumn(1);
            string ofFirst = comparing.TimelineSaid;
            comparing.ChooseRow(1);
            Dispatch();
            Assert.StartsWith("Column 2 of 20: ", comparing.TimelineSaid, StringComparison.Ordinal);
            Assert.NotEqual(ofFirst, comparing.TimelineSaid);
            Assert.NotEqual(first.Key, comparing.Rows[1].Compared.Key);

            // No row chosen, the line says to choose one; undrawn, the chart goes, and the comparison shown stays.
            comparing.ChooseRow(-1);
            Dispatch();
            Assert.Equal(SessionCompareWindow.ChooseRowToDraw, comparing.TimelineSaid);
            IntervalComparison drawn = comparing.Shown!;
            comparing.DrawTimeline(false);
            Dispatch();
            Assert.Same(drawn, comparing.Shown);
            Assert.False(comparing.GetLogicalDescendants().OfType<DifferenceTimelineControl>().Single().IsEffectivelyVisible);
        }
        finally
        {
            comparing.Close();
            owner.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R21: the Compare window compares a typed baseline on Enter, its save prompt takes Enter and Escape, and it says why it could not save or compare")]
    public async Task WhatTheCompareWindowCouldNotDoIsSaid()
    {
        using var session = new TemporarySession(Guid.Parse("0c0c0c0c-0000-4000-8000-000000000799"));
        Publish(session.Store, Rows(), coverage: Ledger());
        session.Store.ReleaseSegmentReaders();
        DirectoryInfo folder = Directory.CreateTempSubdirectory("intercat-compare-trouble-");
        string workspace = Path.Combine(folder.FullName, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, DateTimeOffset.UtcNow);
        InvestigationWorkspace.Add(workspace, session.Path, DateTimeOffset.UtcNow);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        session.Store.ReleaseSegmentReaders();
        SessionClock clock = SessionClock.Session(TimeZoneInfo.Utc);
        var scope = new TimeRange(12 * Second, 16 * Second);
        CultureInfo culture = CultureInfo.CurrentCulture;
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        try
        {
            using (var comparing = new SessionCompareWindow(session.Path, overview.SessionId, scope, overview.Extent!.Value, recorded: false,
                clock, EvidencePolicy.IncludeCorrelated, workspace))
            {
                _ = comparing.ShowDialog<ComparedRelationship?>(owner);
                await Until(() => comparing.Shown is not null);
                await comparing.Compared;

                // A baseline typed in seconds is compared when Enter is pressed in either box.
                comparing.ChooseBaseline(comparing.Offered.ToList().IndexOf("Another interval, typed in seconds…"));
                await comparing.Compared;
                comparing.Enter(0.ToString(culture), 4.ToString(culture));
                TextBox end = comparing.GetLogicalDescendants().OfType<TextBox>()
                    .Single(box => AutomationProperties.GetName(box) == "The baseline's end, in seconds of session time");
                end.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
                await Until(() => comparing.Rows.Count > 0);
                await comparing.Compared;
                Dispatch();
                Assert.Equal(["appeared", "unchanged"], comparing.Rows.Select(row => row.ChangeCell));

                // An investigation that can no longer be read is said, and nothing is saved.
                File.WriteAllText(workspace, "{ not an investigation");
                Click(Labelled(comparing, "Save in the investigation…"));
                await Until(() => comparing.OwnedWindows.Count == 1 || comparing.Done.Length > 0);
                if (comparing.OwnedWindows.Count == 1)
                {
                    Window asked = comparing.OwnedWindows[0];
                    asked.GetLogicalDescendants().OfType<TextBox>().Single().Text = "nightly";
                    Dispatch();
                    Press(asked, "Save");
                }

                await comparing.SavedIn;
                Dispatch();
                Assert.StartsWith("The comparison was not saved: ", comparing.Done, StringComparison.Ordinal);
                comparing.Close();
            }

            // The save prompt keeps a name on Enter in its box, and Escape keeps none.
            Window prompt = SessionCompareWindow.SavePrompt("case" + InvestigationWorkspace.Extension, "described", []);
            Task<string?> named = prompt.ShowDialog<string?>(owner);
            Dispatch();
            prompt.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.Null(await named);
            prompt = SessionCompareWindow.SavePrompt("case" + InvestigationWorkspace.Extension, "described", []);
            named = prompt.ShowDialog<string?>(owner);
            await Until(() => prompt.GetLogicalDescendants().OfType<TextBox>().Single().IsKeyboardFocusWithin);
            prompt.GetLogicalDescendants().OfType<TextBox>().Single().Text = " nightly ";
            Dispatch();
            prompt.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Assert.Equal("nightly", await named);

            // A folder that now holds another session is said, and nothing is compared.
            using var other = new SessionCompareWindow(session.Path, Guid.NewGuid(), scope, overview.Extent!.Value, recorded: false, clock,
                EvidencePolicy.IncludeCorrelated);
            _ = other.ShowDialog<ComparedRelationship?>(owner);
            await Until(() => other.Said.StartsWith("Could not compare", StringComparison.Ordinal));
            Assert.StartsWith("Could not compare the two intervals: ", other.Said, StringComparison.Ordinal);
            Assert.Empty(other.Rows);
            other.Close();
        }
        finally
        {
            owner.Close();
            folder.Delete(recursive: true);
        }
    }

    [AvaloniaFact(DisplayName = "R3: a lower bound reads at least, in the window as on the command line")]
    public void ALowerBoundReadsAtLeast()
    {
        var coverage = new InterCat.Analysis.MechanismCoverage(Mechanism.Tcp, CoverageState.Covered, "covered");
        var exact = new ComparedMeasure(2, 100, 1, 0, coverage);
        var bound = new ComparedMeasure(4, 400, 1, 1, coverage);
        var row = new ComparisonRow(new ComparedRelationship(exact, bound, ComparisonChange.Increased, null) { FromLowerBound = true },
            "a.exe · PID 1 → b.exe · PID 2", "TCP", "100 B · 10 B/s", "≥400 B · ≥40 B/s", "up at least 30 B/s");
        Assert.Equal("a.exe · PID 1 → b.exe · PID 2 over TCP: 100 B · 10 B/s in the baseline, at least 400 B · at least 40 B/s in "
            + "the compared interval; up at least 30 B/s", row.AccessibleName);
        Assert.Equal("up at least 30 B/s", row.ChangeCell);
    }

    /// <summary>
    /// Two relationships, each second a record or two over twenty seconds: client.exe's connection to server.exe's 8080, made
    /// at 0.2 s, with an exchange each second from the first; and worker.exe's to queue.exe's 9090, made at 10.5 s, with an
    /// exchange each half second from the thirteenth.
    /// </summary>
    private static ObservationRowV1[] Rows()
    {
        List<ObservationRowV1> rows =
        [
            Timed(Lifecycle(1, ObservationKind.Inventory, 100, 1) with { ResourceName = @"C:\Tools\client.exe" }),
            Timed(Lifecycle(2, ObservationKind.Inventory, 200, 2) with { ResourceName = @"C:\Tools\server.exe" }),
            Timed(Lifecycle(3, ObservationKind.Inventory, 300, 3) with { ResourceName = @"C:\Tools\worker.exe" }),
            Timed(Lifecycle(4, ObservationKind.Inventory, 400, 4) with { ResourceName = @"C:\Tools\queue.exe" }),
            .. Opened(2 * Second / 10, 100, 200, "127.0.0.1:50000", "127.0.0.1:8080"),
            .. Opened(105 * Second / 10, 300, 400, "127.0.0.1:50001", "127.0.0.1:9090"),
        ];
        for (long at = 1; at < 21; at++)
        {
            rows.AddRange(Exchange(at * Second, 100, 200, "127.0.0.1:50000", "127.0.0.1:8080"));
        }

        for (long at = 26; at < 42; at++)
        {
            rows.AddRange(Exchange((at * Second / 2) + 3, 300, 400, "127.0.0.1:50001", "127.0.0.1:9090"));
        }

        return [.. rows.OrderBy(row => row.NativeTicks)];
    }

    /// <summary>A client's connect and the server's accept of it, a microsecond apart.</summary>
    private static ObservationRowV1[] Opened(long ticks, int client, int server, string clientEnd, string serverEnd) =>
    [
        Timed(Transfer(ticks, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, client).Between(clientEnd, serverEnd)),
        Timed(Transfer(ticks + 10, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, server).Between(serverEnd, clientEnd)),
    ];

    /// <summary>A client's send of 64 B and the server's receive of it, a microsecond apart.</summary>
    private static ObservationRowV1[] Exchange(long ticks, int client, int server, string clientEnd, string serverEnd) =>
    [
        Timed(Transfer(ticks, ObservationKind.Send, AccountingSide.SendSide, 64, client).Between(clientEnd, serverEnd)),
        Timed(Transfer(ticks + 10, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, server).Between(serverEnd, clientEnd)),
    ];

    /// <summary>A live capture's one epoch over the first thirty seconds, which collected TCP and lost nothing.</summary>
    private static CoverageLedgerV1 Ledger() => TransportLedger(tcp: true, udp: false) with
    {
        Epochs = [TransportLedger(tcp: true, udp: false).Epochs[0] with { LastDeliveredNativeTicks = 30 * Second }],
    };

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };

    private static string Rate(double perSecond, CultureInfo culture) => ComparisonText.Rate(perSecond, culture);

    /// <summary>The comparison window the main window opened, once it has.</summary>
    private static async Task<SessionCompareWindow> Opened(Window window)
    {
        await Until(() => window.OwnedWindows.OfType<SessionCompareWindow>().Any());
        return window.OwnedWindows.OfType<SessionCompareWindow>().Single();
    }

    /// <summary>
    /// An investigation of <paramref name="session"/> in <paramref name="folder"/> that saved a comparison of its 4 s from 12 s
    /// with the 4 s before, as 'before the burst', and three notes about it: one about it, one about its second row, and one
    /// about a row it does not compare.
    /// </summary>
    private static (string Workspace, SessionOverviewBundle Overview, SavedComparison Saved) Noted(TemporarySession session, DirectoryInfo folder)
    {
        string workspace = Path.Combine(folder.FullName, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, DateTimeOffset.UtcNow);
        InvestigationWorkspace.Add(workspace, session.Path, DateTimeOffset.UtcNow);
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        SavedComparison saved = InvestigationWorkspace.SaveComparison(workspace, "before the burst",
            new SavedComparisonInterval { SessionId = overview.SessionId, StartTicks = 8 * Second, EndTicks = 12 * Second },
            new SavedComparisonInterval { SessionId = overview.SessionId, StartTicks = 12 * Second, EndTicks = 16 * Second },
            ComparisonMetric.Records, ComparisonGrouping.Process, DateTimeOffset.UtcNow);
        IntervalComparison read = SessionComparison.Compare(session.Store, saved.Baseline!.Interval!.Value, saved.Compared!.Interval!.Value,
            ComparisonMetric.Records, EvidencePolicy.IncludeCorrelated, ComparisonGrouping.Process);
        InvestigationWorkspace.AddNoteAbout(workspace, "The burst is the nightly backup's.", new("before the burst", null), DateTimeOffset.UtcNow);
        InvestigationWorkspace.AddNoteAbout(workspace, "Steady, as expected.", new("before the burst", read.Relationships[1].Key),
            DateTimeOffset.UtcNow);
        InvestigationWorkspace.AddNoteAbout(workspace, "Gone since the update.", new("before the burst", "tcp:nowhere"), DateTimeOffset.UtcNow);
        session.Store.ReleaseSegmentReaders();
        return (workspace, overview, saved);
    }

    /// <summary>The window's list of the rows compared.</summary>
    private static ListBox RowsList(Window window) => window.GetLogicalDescendants().OfType<ListBox>()
        .Single(list => AutomationProperties.GetName(list)?.StartsWith("The rows compared", StringComparison.Ordinal) == true);

    /// <summary>A prompt's choice, by what it says.</summary>
    private static RadioButton Choice(Window window, string name) => window.GetLogicalDescendants().OfType<RadioButton>()
        .Single(choice => AutomationProperties.GetName(choice) == name);

    private static Button Labelled(Window window, string label) =>
        window.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, label));

    /// <summary>The window's choice of what each row compares.</summary>
    private static ComboBox RowsChoice(Window window) => window.GetLogicalDescendants().OfType<ComboBox>()
        .Single(choice => AutomationProperties.GetName(choice)?.StartsWith("What each row compares", StringComparison.Ordinal) == true);

    /// <summary>The baseline the window has chosen, by its label.</summary>
    private static string SelectedBaseline(Window window) => window.GetLogicalDescendants().OfType<ComboBox>()
        .Single(choice => AutomationProperties.GetName(choice) == "The baseline the analysis interval is compared with")
        .SelectedItem!.ToString()!;

    private static void Click(Button button)
    {
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatch();
    }

    /// <summary>The prompt <paramref name="owner"/> opened, once it has.</summary>
    private static async Task<Window> Prompt(Window owner)
    {
        await Until(() => owner.OwnedWindows.Count == 1);
        return owner.OwnedWindows[0];
    }

    /// <summary>Presses a prompt's button by what it says.</summary>
    private static void Press(Window prompt, string label) => Click(Labelled(prompt, label));

    private static async Task Until(Func<bool> condition)
    {
        for (int wait = 0; wait < 400 && !condition(); wait++)
        {
            await Task.Delay(25);
            Dispatch();
        }

        Assert.True(condition());
    }

    /// <summary>Saves a window as drawn, for a person to look at beside the test's results.</summary>
    private static void Save(Window window, string name)
    {
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
        string directory = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(directory);
        window.CaptureRenderedFrame()!.Save(Path.Combine(directory, name));
    }

    /// <summary>A recording's publication of the session's newest generation.</summary>
    private static CaptureUiUpdate Recording(TemporarySession session) => new(CaptureUiPhase.Recording, "Recording",
        "A published generation.", SessionPath: session.Path, Overview: SessionOverviewProjector.Project(session.Store));

    private static double Distance(Point from, Point to) => Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2));

    private static void Dispatch() => Dispatcher.UIThread.RunJobs();
}
