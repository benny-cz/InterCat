using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
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

namespace InterCat.Ui.Tests;

/// <summary>
/// §6.7's multi-selection: Ctrl+click adds to or removes from a set of processes, an explicit predicate the timeline
/// highlights; Ctrl+Space does the same from the keyboard, and Enter turns the set into a filter.
/// </summary>
public sealed class MultiSelectionTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [AvaloniaFact(DisplayName = "R15: Ctrl+click and Ctrl+Space build a multi-selection the timeline highlights, and Enter lists exactly its records")]
    public async Task ChosenProcessesAreHighlightedAndFiltered()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [.. Named(), .. Exchange(30), .. Third(20)]);
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        ListBox list = window.GetControl<ListBox>("RungList");
        Assert.Equal(3, workspace.RungRows.Count);
        int client = Row(workspace, "client");
        int server = Row(workspace, "server");

        // Ctrl+click the client's and the server's rows: a set of two processes, not a row selected alone.
        CtrlClick(window, list, client);
        CtrlClick(window, list, server);
        Assert.Equal([100, 200], workspace.ChosenProcesses.Select(process => process.ProcessId));
        Assert.Null(workspace.SelectedRung);
        Assert.Equal("2 selected processes", workspace.SelectionTitle);
        Assert.Contains("Enter lists their records", workspace.SelectionSubtitle, StringComparison.Ordinal);
        Assert.True(window.GetControl<Button>("ShowChosenRecordsButton").IsVisible);
        await workspace.HighlightReady;
        Dispatch();
        Assert.Equal(31 + 31, workspace.TimelineHighlightBuckets!.Sum(bucket => bucket.ObservationCount));
        Assert.Contains("selection highlighted: 2 selected processes", workspace.TimelineCaption, StringComparison.Ordinal);

        // The chosen rows are marked where they are drawn, and say so to a screen reader; the third is not.
        int third = Enumerable.Range(0, workspace.RungRows.Count).Single(index => index != client && index != server);
        Assert.Equal([true, true, false], new[] { client, server, third }.Select(index => list.ContainerFromIndex(index)!.Classes.Contains("chosen")));
        Assert.Equal("in the selection", AutomationProperties.GetItemStatus(list.ContainerFromIndex(client)!));
        Assert.Null(AutomationProperties.GetItemStatus(list.ContainerFromIndex(third)!));
        Save(window, "multi-selection.png");

        // Ctrl+click on a chosen row takes it out again, and its mark with it.
        CtrlClick(window, list, client);
        Assert.Equal([200], workspace.ChosenProcesses.Select(process => process.ProcessId));
        Assert.DoesNotContain("chosen", list.ContainerFromIndex(client)!.Classes);
        Assert.Null(AutomationProperties.GetItemStatus(list.ContainerFromIndex(client)!));

        // From the keyboard, Ctrl+Down moves the focus without selecting, and Ctrl+Space adds the focused row.
        Assert.True(list.ContainerFromIndex(0)!.Focus());
        for (int step = 0; step < client; step++)
        {
            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.Control);
        }

        Assert.Same(list.ContainerFromIndex(client), TopLevel.GetTopLevel(list)!.FocusManager!.GetFocusedElement());
        Assert.Null(workspace.SelectedRung);
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.Control);
        Assert.Equal([100, 200], workspace.ChosenProcesses.Select(process => process.ProcessId));

        // Enter turns the set into a filter: the evidence rung lists exactly the two processes' records, the set its
        // visible filter, and the set is spent.
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await workspace.EvidenceReady;
        Dispatch();
        Assert.True(workspace.IsEvidenceRung);
        Assert.Contains(workspace.Filters, filter => filter.Label == "2 selected processes");
        Assert.Equal(31 + 31, workspace.RungRows.Count);
        Assert.False(workspace.HasMultiSelection);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§6.7: E with several processes chosen lists exactly their records, as Enter does, since the card counts them")]
    public async Task EListsTheChosenSet()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [.. Named(), .. Exchange(30), .. Third(20)]);
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        ListBox list = window.GetControl<ListBox>("RungList");
        CtrlClick(window, list, Row(workspace, "client"));
        CtrlClick(window, list, Row(workspace, "server"));
        Assert.Equal("Selected processes", workspace.EvidenceHeading);

        // E reads what the card above its button counts: the two chosen processes' records, the set its visible filter.
        window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);
        await workspace.EvidenceReady;
        Dispatch();
        Assert.True(workspace.IsEvidenceRung);
        Assert.Contains(workspace.Filters, filter => filter.Label == "2 selected processes");
        Assert.Equal(31 + 31, workspace.RungRows.Count);
        Assert.False(workspace.HasMultiSelection);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R15: Ctrl+click on graph nodes composes a set with the node selected before it, and a plain click replaces it")]
    public void GraphNodesComposeAndAPlainClickReplaces()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [.. Named(), .. Exchange(30), .. Third(20)]);
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        GraphView graph = window.GetControl<GraphView>("GraphSurface");
        GraphDisplayNode clientNode = NodeOf(workspace, 100);
        GraphDisplayNode serverNode = NodeOf(workspace, 200);

        // A plain click selects the client; a Ctrl+click on the server keeps it and adds the server.
        Click(window, graph, clientNode.Key, RawInputModifiers.None);
        Assert.Equal(100, workspace.SelectedProcess?.ProcessId);
        Click(window, graph, serverNode.Key, RawInputModifiers.Control);
        Assert.Equal([100, 200], workspace.ChosenProcesses.Select(process => process.ProcessId));
        Assert.Null(workspace.SelectedProcess);
        Assert.Contains(clientNode.Key, workspace.SelectedGraphNodeKeys);
        Assert.Contains(serverNode.Key, workspace.SelectedGraphNodeKeys);

        // A plain click is a new selection: the set is let go.
        Click(window, graph, serverNode.Key, RawInputModifiers.None);
        Assert.False(workspace.HasMultiSelection);
        Assert.Equal(200, workspace.SelectedProcess?.ProcessId);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R15: in the graph Ctrl+arrows move the keyboard without choosing, Ctrl+Space adds the node it is on, said aloud, and Enter lists the set's records")]
    public async Task TheGraphsKeyboardBuildsASet()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [.. Named(), .. Exchange(30), .. Third(20)]);
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        GraphView graph = window.GetControl<GraphView>("GraphSurface");
        AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(graph);
        IReadOnlyList<GraphDisplayNode> order = GraphView.Traversal(workspace.GraphDisplay);
        Assert.Equal(3, order.Count);

        // A click chooses the first node in the keyboard's order and gives the graph the keyboard on it.
        Click(window, graph, order[0].Key, RawInputModifiers.None);
        Assert.True(graph.IsKeyboardFocusWithin);
        Assert.Equal(order[0].Key, workspace.SelectedGraphNodeKey);

        // Ctrl+Right moves the keyboard to the next node without choosing it: the choice stays, and the graph says the node
        // the keyboard is on. The arrows chose every node they moved to, so Ctrl+Space could only take that node out again.
        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Control);
        Dispatch();
        Assert.Equal(order[0].Key, workspace.SelectedGraphNodeKey);
        Assert.False(workspace.HasMultiSelection);
        Assert.Equal(Spoken(workspace, order[1].Key), peer.GetName());

        // A publication restates the choice in a view of its own, and the keyboard stays on the node it was moved to.
        Publish(session.Store, [.. Enumerable.Range(0, 5).Select(index =>
            Transfer(60 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 32, 300, (ulong)(6_000 + index))
                .Between("127.0.0.1:51000", ThirdsUnseenEnd) with { SessionRelativeTicks = (60 + (2 * index)) * 100L })]);
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.Contains("generation 2", workspace.Snapshot.Title, StringComparison.Ordinal);
        Assert.Equal(order[0].Key, workspace.SelectedGraphNodeKey);
        Assert.Equal(Spoken(workspace, order[1].Key), peer.GetName());

        // Ctrl+Space adds that node to the one chosen before it, and the graph says it is in the selection.
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.Control);
        Dispatch();
        Assert.True(Holds(workspace, order[0], order[1]));
        Assert.Equal(Spoken(workspace, order[1].Key) + ", in the selection", peer.GetName());

        // Ctrl+Left goes back over the first, in the selection too, and wraps round to the last, which is not; Ctrl+Space adds
        // it, and takes it out again.
        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.Control);
        Dispatch();
        Assert.Equal(Spoken(workspace, order[0].Key) + ", in the selection", peer.GetName());
        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.Control);
        Dispatch();
        Assert.Equal(Spoken(workspace, order[2].Key), peer.GetName());
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.Control);
        Dispatch();
        Assert.True(Holds(workspace, order[0], order[1], order[2]));
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.Control);
        Dispatch();
        Assert.True(Holds(workspace, order[0], order[1]));
        Assert.Equal(Spoken(workspace, order[2].Key), peer.GetName());

        // Enter lists exactly the set's records, as it does from the ranked table.
        int records = workspace.ChosenProcesses.Sum(process => process.ProcessId == 300 ? 26 : 31);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await workspace.EvidenceReady;
        Dispatch();
        Assert.True(workspace.IsEvidenceRung);
        Assert.Contains(workspace.Filters, filter => filter.Label == "2 selected processes");
        Assert.Equal(records, workspace.RungRows.Count);

        // Back at the machine, a node chosen elsewhere - by its ranked row - takes the graph's keyboard from where it was left.
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatch();
        Assert.False(workspace.IsEvidenceRung);
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == order[1].GroupKey);
        Assert.True(graph.Focus());
        Dispatch();
        Assert.Equal(order[1].Key, workspace.SelectedGraphNodeKey);
        Assert.Equal(Spoken(workspace, order[1].Key), peer.GetName());

        // A moment chosen restates the choice as it was, and leaves the keyboard where Ctrl with an arrow moved it.
        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Control);
        Dispatch();
        workspace.SelectInterval(new TimeRange(10, 40));
        await workspace.IntervalReady;
        Dispatch();
        Assert.Equal(order[1].Key, workspace.SelectedGraphNodeKey);
        Assert.Equal(Spoken(workspace, order[2].Key), peer.GetName());
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R15: the graph's keyboard says a node holding part of the selection holds part of it, as its ranked row says")]
    public void TheGraphSaysANodePartlyInTheSelection()
    {
        // third.exe and fourth.exe have no relationship, so the graph counts them in one node.
        using var session = new TemporarySession();
        Publish(session.Store, [.. Named(), .. Exchange(30), .. Third(20),
            Lifecycle(4, ObservationKind.Inventory, 400, 4) with { ResourceName = @"C:\Tools\fourth.exe", SessionRelativeTicks = 400 }]);
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        GraphView graph = window.GetControl<GraphView>("GraphSurface");
        AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(graph);
        GraphDisplayNode quiet = Assert.Single(workspace.GraphDisplay.Nodes, node => node.Kind == GraphNodeKind.Quiet);
        Assert.Equal(2, quiet.Members.Count);

        // third.exe's row chosen with Ctrl+click: the node counting it with fourth.exe holds part of the selection.
        CtrlClick(window, window.GetControl<ListBox>("RungList"), Row(workspace, "third"));
        Assert.Equal([300], workspace.ChosenProcesses.Select(process => process.ProcessId));
        Assert.Equal([quiet.Key], workspace.PartlySelectedGraphNodeKeys);

        // The graph's keyboard, moved onto that node with Ctrl and an arrow, says so; Ctrl+Space adds the rest of it, which
        // it then says is in the selection.
        Assert.True(graph.Focus());
        int at = GraphView.Traversal(workspace.GraphDisplay).ToList().FindIndex(node => node.Key == quiet.Key);
        for (int step = 0; step < at; step++)
        {
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Control);
        }

        Dispatch();
        Assert.Equal(Spoken(workspace, quiet.Key) + ", partly in the selection", peer.GetName());
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.Control);
        Dispatch();
        Assert.Equal([300, 400], workspace.ChosenProcesses.Select(process => process.ProcessId));
        Assert.Equal(Spoken(workspace, quiet.Key) + ", in the selection", peer.GetName());
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R15: the context-menu key and Shift+F10 open the focused ranked row's menu, whose item adds it to the selection")]
    public void TheRowMenuOpensFromTheKeyboard()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [.. Named(), .. Exchange(30), .. Third(20)]);
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        ListBox list = window.GetControl<ListBox>("RungList");
        Control row = list.ContainerFromIndex(Row(workspace, "client"))!;
        ContextMenu menu = row.GetVisualDescendants().OfType<Control>().Select(control => control.ContextMenu).OfType<ContextMenu>().First();
        Assert.True(row.Focus());

        // The key raises its request as it comes up, on the focused row, above the element the menu is attached to; the menu
        // opens anyway.
        window.KeyPressQwerty(PhysicalKey.ContextMenu, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.ContextMenu, RawInputModifiers.None);
        Dispatch();
        Assert.True(menu.IsOpen);
        MenuItem toggle = Assert.IsType<MenuItem>(menu.Items[0]);
        toggle.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        Dispatch();
        Assert.Equal([100], workspace.ChosenProcesses.Select(process => process.ProcessId));

        // Shift+F10 is the same request on a keyboard without the key.
        Assert.True(row.Focus());
        window.KeyPressQwerty(PhysicalKey.F10, RawInputModifiers.Shift);
        window.KeyReleaseQwerty(PhysicalKey.F10, RawInputModifiers.Shift);
        Dispatch();
        Assert.True(menu.IsOpen);
        menu.Close();
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R15: a row in the selection is marked when it is drawn, as one scrolled into view after it was chosen")]
    public void ARowScrolledIntoViewIsMarked()
    {
        // Forty executables, each a group of its own, so the machine rung's table runs past the window.
        using var session = new TemporarySession();
        Publish(session.Store, [.. Enumerable.Range(0, 40).Select(index => Lifecycle(index + 1, ObservationKind.Create, 1_000 + index,
            (ulong)(index + 1)) with { ResourceName = $@"C:\Tools\tool{index:D2}.exe" })]);
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        ListBox list = window.GetControl<ListBox>("RungList");
        RungRow last = workspace.RungRows[^1];
        Assert.Null(list.ContainerFromItem(last));

        // Chosen with the first while out of sight, the last row comes into view marked as in the selection.
        workspace.ToggleRungInSelection(workspace.RungRows[0]);
        workspace.ToggleRungInSelection(last);
        Dispatch();
        list.ScrollIntoView(last);
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
        Control drawn = Assert.IsAssignableFrom<Control>(list.ContainerFromItem(last));
        Assert.Contains("chosen", drawn.Classes);
        Assert.Equal("in the selection", AutomationProperties.GetItemStatus(drawn));
        window.Close();
    }

    /// <summary>Keeps what the window drew beside the tests' other renders, for a person to look at.</summary>
    private static void Save(Window window, string name)
    {
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
        string directory = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(directory);
        window.CaptureRenderedFrame()!.Save(Path.Combine(directory, name));
    }

    private static GraphDisplayNode NodeOf(WorkspaceViewModel workspace, int processId)
    {
        ProcessInstanceId id = workspace.Snapshot.Processes.Single(process => process.ProcessId == processId).Id;
        return workspace.GraphDisplay.NodeOf(id)!;
    }

    /// <summary>Whether the multi-selection holds exactly the processes of <paramref name="nodes"/>.</summary>
    private static bool Holds(WorkspaceViewModel workspace, params GraphDisplayNode[] nodes) =>
        workspace.ChosenProcesses.Select(process => process.Id).ToHashSet().SetEquals(nodes.SelectMany(node => node.Members));

    /// <summary>The graph's name with the keyboard on <paramref name="node"/>, in its card's words.</summary>
    private static string Spoken(WorkspaceViewModel workspace, string node)
    {
        HoverCard card = Assert.IsType<HoverCard>(workspace.DescribeGraphHover(node));
        return $"{GraphView.SpokenName}: {card.Title}, {card.Lines[0]}";
    }

    private static int Row(WorkspaceViewModel workspace, string name) =>
        workspace.RungRows.ToList().FindIndex(row => row.Label.Contains(name, StringComparison.OrdinalIgnoreCase));

    private static void CtrlClick(Window window, ListBox list, int index)
    {
        Control item = list.ContainerFromIndex(index)!;
        Point at = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left, RawInputModifiers.Control);
        window.MouseUp(at, MouseButton.Left, RawInputModifiers.Control);
        Dispatch();
    }

    private static void Click(Window window, GraphView graph, string key, RawInputModifiers modifiers)
    {
        Point at = graph.TranslatePoint(graph.PointOf(key)!.Value, window)!.Value;
        window.MouseDown(at, MouseButton.Left, modifiers);
        window.MouseUp(at, MouseButton.Left, modifiers);
        Dispatch();
    }

    private static CaptureUiUpdate Update(TemporarySession session) => new(
        CaptureUiPhase.Recording, "Recording", "A published generation.", SessionPath: session.Path,
        Overview: SessionOverviewProjector.Project(session.Store));

    /// <summary>The client, the server and a third process as rundowns name them, so each is a group of its own.</summary>
    private static ObservationRowV1[] Named() =>
    [
        Lifecycle(1, ObservationKind.Inventory, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 100 },
        Lifecycle(2, ObservationKind.Inventory, 200, 2) with { ResourceName = @"C:\Tools\server.exe", SessionRelativeTicks = 200 },
        Lifecycle(3, ObservationKind.Inventory, 300, 3) with { ResourceName = @"C:\Tools\third.exe", SessionRelativeTicks = 300 },
    ];

    /// <summary>A client sending and a server receiving, <paramref name="count"/> times.</summary>
    private static ObservationRowV1[] Exchange(int count) =>
    [
        .. Enumerable.Range(0, count).SelectMany(index => new[]
        {
            Transfer(10 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 64, 100, (ulong)(100 + (2 * index)))
                .Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = (10 + (2 * index)) * 100L },
            Transfer(11 + (2 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200,
                (ulong)(101 + (2 * index))).Between(ServerEnd, ClientEnd) with { SessionRelativeTicks = (11 + (2 * index)) * 100L },
        }),
    ];

    /// <summary>
    /// A third process sending to an endpoint on this machine the capture holds nothing of: it relates to no process and
    /// reaches no other host, so the graph draws no edge of it.
    /// </summary>
    private static ObservationRowV1[] Third(int count) =>
    [
        .. Enumerable.Range(0, count).Select(index =>
            Transfer(12 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 32, 300, (ulong)(5_000 + index))
                .Between("127.0.0.1:51000", ThirdsUnseenEnd) with { SessionRelativeTicks = (12 + (2 * index)) * 100L }),
    ];

    /// <summary>Where the third process sends: a port on this machine no record of the capture holds.</summary>
    private const string ThirdsUnseenEnd = "127.0.0.1:7443";

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
