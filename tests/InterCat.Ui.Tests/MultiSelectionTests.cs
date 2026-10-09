using Avalonia;
using Avalonia.Automation;
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

    /// <summary>A third process sending to a remote endpoint the capture holds nothing of.</summary>
    private static ObservationRowV1[] Third(int count) =>
    [
        .. Enumerable.Range(0, count).Select(index =>
            Transfer(12 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 32, 300, (ulong)(5_000 + index))
                .Between("127.0.0.1:51000", "10.0.0.7:443") with { SessionRelativeTicks = (12 + (2 * index)) * 100L }),
    ];

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
