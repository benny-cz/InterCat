using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System.Globalization;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using InterCat.Domain;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(InterCat.Ui.Tests.TestApplication))]

namespace InterCat.Ui.Tests;

/// <summary>
/// The headless lane the M0 interaction review asked for. The earlier review could implement the
/// keyboard path but not confirm it, because synthetic key delivery could not be driven from an
/// automation script. Here the keys are delivered to a real window and the result is asserted, so
/// "implemented and unverified" becomes "verified" (R15, section 6.7).
/// </summary>
public static class TestApplication
{
    public static AppBuilder BuildAvaloniaApp()
    {
        // Every rendering in these tests is of the dark theme, whatever the headless platform reports; a test that needs
        // the light one applies it and restores dark.
        App.PinnedMode = InterCat.Desktop.Theme.ThemeMode.Dark;
        return AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}

public sealed class LadderKeyboardTests
{
    [AvaloniaFact(DisplayName = "§6.7: Ctrl+F focuses search, Enter opens a hit through the ladder and Escape clears it")]
    public void SearchHasKeyboardAndPointerEquivalent()
    {
        (Window window, WorkspaceViewModel viewModel) = Open();
        FocusRankedTable(window);

        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);

        TextBox search = window.GetControl<TextBox>("SearchBox");
        ListBox hits = window.GetControl<ListBox>("SearchResultsList");
        ListBox ranked = window.GetControl<ListBox>("RungList");
        Assert.True(search.IsFocused);
        search.Text = "cache";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(hits.IsVisible);
        Assert.False(ranked.IsVisible);
        Assert.Contains(viewModel.SearchResults, row => row.Hit.Kind == SearchHitKind.Channel);
        Assert.DoesNotContain(viewModel.SearchResults, row => row.Label.Contains("intercat-cache", StringComparison.Ordinal));

        viewModel.SelectedSearchResult = viewModel.SearchResults.Single(row => row.Hit.Kind == SearchHitKind.Channel);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal("L3 · CHANNEL", viewModel.LevelBadge);
        Assert.Equal(string.Empty, viewModel.SearchText);
        Assert.False(hits.IsVisible);
        Assert.True(ranked.IsVisible || viewModel.ShowsEmptyReason);
        Assert.Equal(4, viewModel.Crumbs.Count);

        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
        search.Text = "not-an-entity";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal("No matches · names, PIDs and channel endpoints are searched", viewModel.SearchSummary);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.Equal(string.Empty, viewModel.SearchText);
        Assert.Equal("L3 · CHANNEL", viewModel.LevelBadge);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R15: the table toggle is reachable by keyboard while a list has focus")]
    public void TableToggleRespondsToItsKey()
    {
        (Window window, WorkspaceViewModel viewModel) = Open();
        FocusRankedTable(window);
        Assert.False(viewModel.ShowTables);

        window.KeyPressQwerty(PhysicalKey.T, RawInputModifiers.None);

        Assert.True(viewModel.ShowTables);

        window.KeyPressQwerty(PhysicalKey.T, RawInputModifiers.None);

        Assert.False(viewModel.ShowTables);
    }

    [AvaloniaFact(DisplayName = "R15: graph pin and re-layout commands have keyboard paths and a button equivalent")]
    public async Task GraphPlacementCommandsAreReachableWithoutDragging()
    {
        (Window window, WorkspaceViewModel viewModel) = Open();
        await viewModel.LayoutReady;
        GraphView graph = window.GetControl<GraphView>("GraphSurface");
        graph.Focus();
        GraphDisplayNode node = viewModel.GraphDisplay.Nodes.First(candidate => candidate.Kind == GraphNodeKind.Process);
        viewModel.SelectGraphNode(node.Key);

        Button pin = window.GetControl<Button>("PinNodeButton");
        Button relayout = window.GetControl<Button>("RelayoutButton");
        Assert.True(pin.IsEnabled);
        Assert.Equal("Pin node (P)", pin.Content);
        Assert.True(relayout.IsEnabled);

        window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.None);
        await viewModel.LayoutReady;
        Assert.Contains(node.Key, viewModel.PinnedGraphNodeKeys);
        Assert.Equal("Unpin node (P)", pin.Content);

        window.KeyPressQwerty(PhysicalKey.L, RawInputModifiers.None);
        await viewModel.LayoutReady;
        Assert.Contains(node.Key, viewModel.PinnedGraphNodeKeys);

        window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.None);
        await viewModel.LayoutReady;
        Assert.DoesNotContain(node.Key, viewModel.PinnedGraphNodeKeys);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§3.2: Enter descends a rung and Escape returns to exactly where it was")]
    public void EnterDescendsAndEscapeRestores()
    {
        (Window window, WorkspaceViewModel viewModel) = Open();
        FocusRankedTable(window);
        string machineBadge = viewModel.LevelBadge;
        string machineSummary = viewModel.LevelSummary;
        viewModel.SelectedRung = viewModel.RungRows[0];

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        Assert.Equal("L1 · GROUP", viewModel.LevelBadge);
        Assert.True(viewModel.CanAscend);
        Assert.Equal(2, viewModel.Crumbs.Count);
        Assert.True(viewModel.Crumbs[^1].IsCurrent);
        Assert.NotEmpty(viewModel.RungRows);

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        Assert.Equal(machineBadge, viewModel.LevelBadge);
        Assert.Equal(machineSummary, viewModel.LevelSummary);
        Assert.False(viewModel.CanAscend);
        Assert.Single(viewModel.Crumbs);
    }

    [AvaloniaFact(DisplayName = "§3.2: after Enter or Escape the new rung's table keeps the keyboard, so the next arrow and Enter act on it")]
    public void TheKeyboardStaysOnTheRailAcrossRungs()
    {
        (Window window, WorkspaceViewModel viewModel) = Open();
        ListBox list = window.GetControl<ListBox>("RungList");
        viewModel.SelectedRung = viewModel.RungRows[0];
        Settle(window);
        Assert.True(list.ContainerFromIndex(0)!.Focus());

        // Enter opens the group: its table has the keyboard, on its first row, so Down moves and Enter opens again.
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Settle(window);
        Assert.Equal("L1 · GROUP", viewModel.LevelBadge);
        IInputElement? focused = TopLevel.GetTopLevel(list)!.FocusManager!.GetFocusedElement();
        ListBoxItem item = Assert.IsType<ListBoxItem>(focused);
        Assert.Same(list, item.GetVisualAncestors().OfType<ListBox>().First());
        Assert.Equal(viewModel.RungRows[0].Key, Assert.IsType<RungRow>(item.DataContext).Key);
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        Settle(window);
        Assert.Equal(viewModel.RungRows[Math.Min(1, viewModel.RungRows.Count - 1)].Key, viewModel.SelectedRung?.Key);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Settle(window);
        Assert.Equal("L2 · PROCESS", viewModel.LevelBadge);

        // Escape returns a rung up, with the keyboard on the row the user had opened.
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Settle(window);
        Assert.Equal("L1 · GROUP", viewModel.LevelBadge);
        ListBoxItem back = Assert.IsType<ListBoxItem>(TopLevel.GetTopLevel(list)!.FocusManager!.GetFocusedElement());
        Assert.Equal(viewModel.SelectedRung?.Key, Assert.IsType<RungRow>(back.DataContext).Key);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§6.3: Enter on a process node opens its rung through its group, as its hover card says, and not again at its own rung")]
    public async Task EnterOpensAProcessNode()
    {
        (Window window, WorkspaceViewModel viewModel) = Open();
        await viewModel.LayoutReady;
        GraphView graph = window.GetControl<GraphView>("GraphSurface");
        GraphDisplayNode node = viewModel.GraphDisplay.Nodes.First(candidate => candidate.Kind == GraphNodeKind.Process);
        Assert.Contains("Double-click or Enter opens this process", viewModel.DescribeGraphHover(node.Key)!.Lines);
        Assert.True(graph.Focus());
        viewModel.SelectGraphNode(node.Key);

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Assert.Equal("L2 · PROCESS", viewModel.LevelBadge);
        Assert.Equal(3, viewModel.Crumbs.Count);

        // At its own rung the process is drawn again; Enter there opens nothing, and its card offers nothing to open.
        await viewModel.LayoutReady;
        GraphDisplayNode own = viewModel.GraphDisplay.Nodes.First(candidate => candidate.Process == node.Process);
        Assert.DoesNotContain(viewModel.DescribeGraphHover(own.Key)!.Lines, line => line.Contains("opens", StringComparison.Ordinal));
        Assert.False(viewModel.OpenGraphNode(own.Key));

        // Escape climbs back the way a descent through the group came.
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.Equal("L1 · GROUP", viewModel.LevelBadge);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§3.2: Enter opens the row that has the keyboard, before an arrow has selected any row")]
    public void EnterOpensTheRowWithTheKeyboard()
    {
        (Window window, WorkspaceViewModel viewModel) = Open();
        ListBox list = window.GetControl<ListBox>("RungList");
        Settle(window);
        Assert.Null(viewModel.SelectedRung);
        Assert.True(list.ContainerFromIndex(0)!.Focus());
        string opened = viewModel.RungRows[0].Label;

        // A new rung's first row has the keyboard with nothing selected, and says "Press Enter"; Enter opens it at once.
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Settle(window);
        Assert.Equal("L1 · GROUP", viewModel.LevelBadge);
        Assert.Contains(opened, viewModel.Crumbs[1].Label, StringComparison.Ordinal);
        Assert.Null(viewModel.SelectedRung);
        Assert.Equal(viewModel.RungRows[0].Key, TableRowWithKeyboard(list).Key);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Settle(window);
        Assert.Equal("L2 · PROCESS", viewModel.LevelBadge);

        // Enter on a crumb that has the keyboard returns to its rung, and the rung's table takes the keyboard back.
        CrumbRow machine = viewModel.Crumbs[0];
        ListBox crumbs = window.GetControl<ListBox>("CrumbList");
        Assert.True(crumbs.ContainerFromIndex(0)!.Focus());
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Settle(window);
        Assert.Equal("L0 · MACHINE", viewModel.LevelBadge);
        Assert.Equal(machine.Label, Assert.Single(viewModel.Crumbs).Label);
        Assert.Equal((viewModel.SelectedRung ?? viewModel.RungRows[0]).Key, TableRowWithKeyboard(list).Key);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R15: the timeline and minimap say the range in view, so a zoom and a fit are heard as well as drawn")]
    public void TheTimelineSaysWhatIsInView()
    {
        (Window window, WorkspaceViewModel viewModel) = Open();
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        AutomationPeer timelinePeer = ControlAutomationPeer.CreatePeerForElement(timeline);
        AutomationPeer minimapPeer = ControlAutomationPeer.CreatePeerForElement(window.GetControl<MinimapView>("MinimapSurface"));
        string whole = "the whole session in view, " + WorkspaceTime.FormatRange(viewModel.Snapshot.Extent, CultureInfo.CurrentCulture);
        Assert.EndsWith(" · " + whole, timelinePeer.GetItemStatus(), StringComparison.Ordinal);
        Assert.True(timeline.Focus());

        window.KeyPressQwerty(PhysicalKey.Equal, RawInputModifiers.None);
        Settle(window);
        Assert.False(timeline.IsFit);
        string zoomed = "in view " + WorkspaceTime.FormatRange(timeline.Viewport, CultureInfo.CurrentCulture) + " of the session's ";
        Assert.Contains(zoomed, timelinePeer.GetItemStatus(), StringComparison.Ordinal);
        Assert.StartsWith(zoomed, minimapPeer.GetItemStatus(), StringComparison.Ordinal);

        // 0 fits the whole session again, with nothing brushed, and both say so.
        window.KeyPressQwerty(PhysicalKey.Digit0, RawInputModifiers.None);
        Settle(window);
        Assert.Equal(whole, minimapPeer.GetItemStatus());
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§3.2: Escape out of the search, or Enter on a hit, gives the keyboard to the ranked table's row")]
    public void TheSearchHandsTheKeyboardBackToTheTable()
    {
        (Window window, WorkspaceViewModel viewModel) = Open();
        ListBox list = window.GetControl<ListBox>("RungList");
        TextBox search = window.GetControl<TextBox>("SearchBox");
        ListBox hits = window.GetControl<ListBox>("SearchResultsList");
        viewModel.SelectedRung = viewModel.RungRows[1];
        Settle(window);
        Assert.True(list.ContainerFromIndex(1)!.Focus());

        // Escape clears the search and returns the keyboard to the row it left; a second Escape then ascends as usual.
        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
        Assert.True(search.IsFocused);
        search.Text = "cache";
        Settle(window);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Settle(window);
        Assert.Equal(string.Empty, viewModel.SearchText);
        Assert.Equal(viewModel.RungRows[1].Key, TableRowWithKeyboard(list).Key);

        // Escape in an empty search box is the way out of it too, rather than a key that does nothing.
        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Settle(window);
        Assert.Equal(viewModel.RungRows[1].Key, TableRowWithKeyboard(list).Key);

        // Enter on a hit in the results opens it through the ladder, and the opened rung's table has the keyboard.
        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
        search.Text = "cache";
        Settle(window);
        SearchRow process = viewModel.SearchResults.First(row => row.Hit.Kind == SearchHitKind.Process);
        viewModel.SelectedSearchResult = process;
        Settle(window);
        Assert.True(hits.ContainerFromItem(process)!.Focus());
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Settle(window);
        Assert.Equal("L2 · PROCESS", viewModel.LevelBadge);
        Assert.False(hits.IsVisible);
        Assert.Equal((viewModel.SelectedRung ?? viewModel.RungRows[0]).Key, TableRowWithKeyboard(list).Key);

        // The next Down moves in the opened rung, as it would had the user descended there.
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        Settle(window);
        Assert.NotNull(viewModel.SelectedRung);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§3.2: Alt and Left ascend, the second gesture the ladder promises")]
    public void AltLeftAscends()
    {
        (Window window, WorkspaceViewModel viewModel) = Open();
        FocusRankedTable(window);
        viewModel.SelectedRung = viewModel.RungRows[0];
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Assert.True(viewModel.CanAscend);

        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.Alt);

        Assert.False(viewModel.CanAscend);
        Assert.Equal("L0 · MACHINE", viewModel.LevelBadge);
    }

    [AvaloniaFact(DisplayName = "§6.7: Alt and Right go forward to the rung Alt and Left left, and a button left of Back says where")]
    public void AltRightGoesForward()
    {
        (Window window, WorkspaceViewModel viewModel) = Open();
        FocusRankedTable(window);
        Button forward = window.GetControl<Button>("ForwardButton");
        Button back = window.GetControl<Button>("AscendButton");
        viewModel.SelectedRung = viewModel.RungRows[0];
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        string group = viewModel.LevelBadge;
        Settle(window);
        Assert.False(forward.IsVisible);
        double backRight = back.Bounds.Right;

        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.Alt);
        Settle(window);
        Assert.Equal("L0 · MACHINE", viewModel.LevelBadge);
        Assert.True(forward.IsVisible);
        Assert.Equal("Forward (Alt+Right)", forward.Content);
        Assert.Equal(viewModel.ForwardLabel, ToolTip.GetTip(forward));
        Assert.Equal(viewModel.ForwardLabel, AutomationProperties.GetName(forward));
        Assert.StartsWith("Forward to Group: ", viewModel.ForwardLabel, StringComparison.Ordinal);

        // Back keeps its place when Forward appears, so clicking Back again never lands on Forward.
        Assert.Equal(backRight, back.Bounds.Right);
        Assert.True(forward.Bounds.Right <= back.Bounds.Left);

        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Alt);
        Settle(window);
        Assert.Equal(group, viewModel.LevelBadge);
        Assert.False(forward.IsVisible);

        // The button is the same step as the keys.
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Settle(window);
        forward.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(group, viewModel.LevelBadge);
    }

    [AvaloniaFact(DisplayName = "§3.2: evidence is one key away from the rung the user is on")]
    public void EvidenceIsOneKeyAway()
    {
        (Window window, WorkspaceViewModel viewModel) = Open();
        FocusRankedTable(window);
        viewModel.SelectedRung = viewModel.RungRows[0];
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);

        Assert.Equal("L5 · EVIDENCE", viewModel.LevelBadge);
        Assert.NotEmpty(viewModel.RungRows);
        Assert.Contains(viewModel.Filters, filter => filter.Field == "scope");

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        Assert.Equal("L1 · GROUP", viewModel.LevelBadge);
    }

    [AvaloniaFact(DisplayName = "§3.2: a filter a descent implied can be removed without changing the level")]
    public void DeleteRemovesTheSelectedFilter()
    {
        (Window window, WorkspaceViewModel viewModel) = Open();
        FocusRankedTable(window);
        viewModel.SelectedRung = viewModel.RungRows[0];
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Assert.Single(viewModel.Filters);
        string level = viewModel.LevelBadge;
        viewModel.SelectedFilter = viewModel.Filters[0];

        window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);

        Assert.Empty(viewModel.Filters);
        Assert.False(viewModel.HasFilters);
        Assert.Equal(level, viewModel.LevelBadge);
    }

    [AvaloniaFact(DisplayName = "§3.2: descending to the deepest rung never leaves an unexplained empty pane")]
    public void EveryRungEitherHasRowsOrSaysWhyNot()
    {
        (Window window, WorkspaceViewModel viewModel) = Open();
        FocusRankedTable(window);

        for (int depth = 0; depth < 5; depth++)
        {
            if (viewModel.IsEmptyRung)
            {
                Assert.False(string.IsNullOrWhiteSpace(viewModel.EmptyReason));
                return;
            }

            Assert.NotEmpty(viewModel.RungRows);
            viewModel.SelectedRung = viewModel.RungRows[0];
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        }

        Assert.Equal("L5 · EVIDENCE", viewModel.LevelBadge);
        Assert.NotEmpty(viewModel.RungRows);
    }

    [AvaloniaFact(DisplayName = "R15: the breadcrumb returns to a named rung and drops the rungs below it")]
    public void BreadcrumbReturnsToANamedRung()
    {
        (Window window, WorkspaceViewModel viewModel) = Open();
        FocusRankedTable(window);
        viewModel.SelectedRung = viewModel.RungRows[0];
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        viewModel.SelectedRung = viewModel.RungRows[0];
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Assert.Equal(3, viewModel.Crumbs.Count);

        CrumbRow machine = viewModel.Crumbs[0];
        viewModel.SelectedCrumb = machine;

        Assert.Single(viewModel.Crumbs);
        Assert.Equal("L0 · MACHINE", viewModel.LevelBadge);
    }

    [AvaloniaFact(DisplayName = "§3.2: a rung the user moves to opens its ranked table at its first row, not part-way down")]
    public void ANewRungOpensItsTableAtItsFirstRow()
    {
        // Thirty-nine busy pairs rank above one quiet forty-process executable, so both its machine row and its own rung's
        // table need scrolling.
        ProcessGroup[] busy = [.. Enumerable.Range(0, 39).Select(index => new ProcessGroup($"busy{index}", $"busy{index}.exe", LaneGrouping.Executable))];
        ProcessGroup pool = new("pool", "pool.exe", LaneGrouping.Executable);
        ProcessNode[] pairs = [.. busy.SelectMany((group, index) => new[] { Node(index * 2, group.Key), Node((index * 2) + 1, group.Key) })];
        ProcessNode[] members = [.. Enumerable.Range(100, 40).Select(index => Node(index, pool.Key))];
        CommunicationEdge[] edges =
        [
            .. Enumerable.Range(0, busy.Length).Select(index => new CommunicationEdge(
                $"busy{index}", pairs[index * 2].Id, pairs[(index * 2) + 1].Id, Mechanism.Tcp, 1_000, null, RelationStrength.Direct)),
            .. Enumerable.Range(0, members.Length / 2).Select(index => new CommunicationEdge(
                $"pool{index}", members[index * 2].Id, members[(index * 2) + 1].Id, Mechanism.Tcp, 1, null, RelationStrength.Direct)),
        ];
        var viewModel = new WorkspaceViewModel(new WorkspaceSnapshot("Scroll", new(0, WorkspaceTime.TicksPerSecond),
            [.. busy, pool], [.. pairs, .. members], edges, [], [], [], []), "session:test:generation:1");
        var window = new MainWindow(viewModel) { Width = 1080, Height = 700 };
        window.Show();
        ListBox list = window.GetControl<ListBox>("RungList");
        ScrollViewer scroller = list.GetVisualDescendants().OfType<ScrollViewer>().First();

        RungRow row = viewModel.RungRows.Single(candidate => candidate.Key == pool.Key);
        viewModel.SelectedRung = row;
        list.ScrollIntoView(row);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(scroller.Offset.Y > 0);

        Assert.True(viewModel.Descend());
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(40, viewModel.RungRows.Count);
        Assert.Equal(0, scroller.Offset.Y);

        // The keyboard path: the table has focus on the scrolled-to row when Enter opens it.
        viewModel.ReturnTo(0);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        viewModel.SelectedRung = viewModel.RungRows.Single(candidate => candidate.Key == pool.Key);
        list.ScrollIntoView(viewModel.SelectedRung!);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        list.ContainerFromItem(viewModel.SelectedRung!)?.Focus();
        Assert.True(scroller.Offset.Y > 0);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal("L1 · GROUP", viewModel.LevelBadge);
        Assert.Equal(0, scroller.Offset.Y);
        window.Close();
    }

    private static ProcessNode Node(int index, string group) => new(
        new ProcessInstanceId(Guid.Parse($"00000000-0000-0000-0000-{index:D12}")),
        3_000 + index,
        $"Process {index}",
        "test",
        group,
        0.5,
        0.5,
        CoverageState.Covered);

    private static (Window Window, WorkspaceViewModel ViewModel) Open()
    {
        var window = new MainWindow(new WorkspaceViewModel());
        window.Show();
        return (window, (WorkspaceViewModel)window.DataContext!);
    }

    /// <summary>
    /// Focus the ranked list first. A focused list is exactly the situation the review found broken, so
    /// the test would pass for the wrong reason if it left focus on the window.
    /// </summary>
    private static void FocusRankedTable(Window window)
    {
        ListBox list = window.GetControl<ListBox>("RungList");
        list.Focus();
    }

    /// <summary>The ranked table's row that has the keyboard; fails when the keyboard is anywhere else.</summary>
    private static RungRow TableRowWithKeyboard(ListBox list)
    {
        ListBoxItem item = Assert.IsType<ListBoxItem>(TopLevel.GetTopLevel(list)!.FocusManager!.GetFocusedElement());
        Assert.Same(list, item.GetVisualAncestors().OfType<ListBox>().First());
        return Assert.IsType<RungRow>(item.DataContext);
    }

    /// <summary>Runs the bindings and a layout pass, so visibility and bounds are the ones drawn.</summary>
    private static void Settle(Window window)
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        _ = window.CaptureRenderedFrame();
    }
}
