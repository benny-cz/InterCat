using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
/// M5's accessible keyboard workflow: a person who never takes up a pointer opens a session's busiest process, steps to a
/// moment of its traffic, reads the records there and opens one, and comes back, every step by a key the window names,
/// with the keyboard always on something a screen reader can say (§6.7, R15).
/// </summary>
public sealed class KeyboardWorkflowTests
{
    [AvaloniaFact(DisplayName = "R15: a session is walked by keyboard alone: a process opened, a moment stepped to, its records read and one opened in E, and back")]
    public async Task ASessionIsWalkedByKeyboardAlone()
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
        ListBox rail = window.GetControl<ListBox>("RungList");
        GraphView graph = window.GetControl<GraphView>("GraphSurface");
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        Border inspector = window.GetControl<Border>("Inspector");

        // F6 gives the ranked table the keyboard, on its first row: Enter opens client.exe's group, and Enter its process.
        Press(window, PhysicalKey.F6);
        Assert.True(rail.IsKeyboardFocusWithin, Focused(window));
        Press(window, PhysicalKey.Enter);
        Assert.Equal("L1 · GROUP", workspace.LevelBadge);
        Press(window, PhysicalKey.Enter);
        Assert.Equal("L2 · PROCESS", workspace.LevelBadge);
        await workspace.TimelineDetailReady;
        Dispatch();

        // F6 moves on to the graph and again to the timeline, where ] steps to the first moment the process holds records,
        // and again to the next: the inspector, still titled for the process the rung is about, explains that cell and
        // lists its records.
        Press(window, PhysicalKey.F6);
        Assert.True(graph.IsKeyboardFocusWithin, Focused(window));
        Press(window, PhysicalKey.F6);
        Assert.True(timeline.IsKeyboardFocusWithin, Focused(window));
        Press(window, PhysicalKey.BracketRight);
        TimeRange first = Assert.IsType<TimeRange>(workspace.SelectedInterval);
        Press(window, PhysicalKey.BracketRight);
        TimeRange moment = Assert.IsType<TimeRange>(workspace.SelectedInterval);
        Assert.True(moment.StartTicks >= first.EndTicks);
        await workspace.IntervalReady;
        await workspace.CellRecordsReady;
        Dispatch();
        Assert.Equal("client.exe", workspace.SelectionTitle);
        Assert.True(workspace.HasCellExplanation);
        ListBox records = window.GetControl<ListBox>("CellRecordsList");
        Assert.True(records.ItemCount > 1, $"{records.ItemCount} records listed");

        // F6 moves to the inspector, onto the first listed record; Down chooses the next, and Enter opens it in E, selected
        // there, with E's table holding the keyboard on it.
        Press(window, PhysicalKey.F6);
        Assert.True(records.IsKeyboardFocusWithin, Focused(window));
        Press(window, PhysicalKey.ArrowDown);
        CellRecordRow second = Assert.IsType<CellRecordRow>(records.Items[1]);
        Assert.Same(second, records.SelectedItem);
        Press(window, PhysicalKey.Enter);
        Assert.True(workspace.IsEvidenceRung);
        await workspace.EvidenceReady;
        Dispatch();
        Assert.Equal(second.Key, workspace.SelectedRung?.Key);
        Assert.True(rail.IsKeyboardFocusWithin, Focused(window));

        // Esc comes back to the process with the moment kept, the keyboard on its table, from which Shift+F6 goes round to
        // the inspector.
        Press(window, PhysicalKey.Escape);
        Assert.Equal("L2 · PROCESS", workspace.LevelBadge);
        Assert.Equal(moment, workspace.SelectedInterval);
        Assert.True(rail.IsKeyboardFocusWithin, Focused(window));
        Press(window, PhysicalKey.F6, RawInputModifiers.Shift);
        Assert.True(inspector.IsKeyboardFocusWithin, Focused(window));

        // T shows the tables over the panes: F6 goes from the ranked table to the relationship table, then to the interval
        // table on the row for the moment, and round to the ranked table.
        Press(window, PhysicalKey.T);
        Assert.True(workspace.ShowTables);
        Press(window, PhysicalKey.F6);
        Assert.True(rail.IsKeyboardFocusWithin, Focused(window));
        Press(window, PhysicalKey.F6);
        Assert.True(window.GetControl<ListBox>("RelationshipList").IsKeyboardFocusWithin, Focused(window));
        Press(window, PhysicalKey.F6);
        ListBox intervals = window.GetControl<ListBox>("IntervalList");
        Assert.True(intervals.IsKeyboardFocusWithin, Focused(window));
        Assert.Equal(moment, Assert.IsType<IntervalRow>(intervals.SelectedItem).Interval);
        Assert.Same(intervals.SelectedItem, (window.FocusManager?.GetFocusedElement() as Control)?.DataContext);
        Press(window, PhysicalKey.F6);
        Assert.True(rail.IsKeyboardFocusWithin, Focused(window));
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§6.7: F6 passes by a pane out of sight, leaves the search box for the pane after the rail, and types nothing")]
    public async Task F6PassesByAPaneOutOfSight()
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
        GraphView graph = window.GetControl<GraphView>("GraphSurface");
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        Border inspector = window.GetControl<Border>("Inspector");

        // The graph and the timeline say so where a screen reader asks what they are.
        foreach (Control surface in new Control[] { graph, timeline })
        {
            Assert.EndsWith(" F6 moves to the next pane, Shift+F6 to the one before.",
                ControlAutomationPeer.CreatePeerForElement(surface).GetHelpText(), StringComparison.Ordinal);
        }

        // Shift+F6 from nowhere goes to the last pane, the inspector; F6 from it goes round to the ranked table.
        Press(window, PhysicalKey.F6, RawInputModifiers.Shift);
        Assert.True(inspector.IsKeyboardFocusWithin, Focused(window));
        Press(window, PhysicalKey.F6);
        Assert.True(window.GetControl<ListBox>("RungList").IsKeyboardFocusWithin, Focused(window));

        // From the search box F6 goes to the graph, and types nothing there.
        Press(window, PhysicalKey.F, RawInputModifiers.Control);
        TextBox search = window.GetControl<TextBox>("SearchBox");
        Assert.True(search.IsKeyboardFocusWithin, Focused(window));
        Press(window, PhysicalKey.F6);
        Assert.True(graph.IsKeyboardFocusWithin, Focused(window));
        Assert.Equal(string.Empty, search.Text ?? string.Empty);

        // With the timeline filling the column, the graph out of sight is passed by either way.
        Press(window, PhysicalKey.F6);
        Assert.True(timeline.IsKeyboardFocusWithin, Focused(window));
        Press(window, PhysicalKey.F11);
        Assert.False(graph.IsEffectivelyVisible);
        Press(window, PhysicalKey.F6, RawInputModifiers.Shift);
        Assert.True(window.GetControl<ListBox>("RungList").IsKeyboardFocusWithin, Focused(window));
        Press(window, PhysicalKey.F6);
        Assert.True(timeline.IsKeyboardFocusWithin, Focused(window));
        Press(window, PhysicalKey.F6);
        Assert.True(inspector.IsKeyboardFocusWithin, Focused(window));
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§6.7: with no session open the keyboard starts on Start exploring, and F6 and Shift+F6 bring it back there")]
    public void F6ComesBackToWhereTheRailWasLeft()
    {
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        Dispatch();
        Button explore = window.GetControl<Button>("StartExploringButton");
        Assert.True(explore.IsFocused, Focused(window));
        Press(window, PhysicalKey.F6);
        Assert.True(window.GetControl<GraphView>("GraphSurface").IsKeyboardFocusWithin, Focused(window));
        Press(window, PhysicalKey.F6, RawInputModifiers.Shift);
        Assert.True(explore.IsFocused, Focused(window));
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R15: Enter on an interval row lists the moment's records in E, as E does, and an empty moment's says why it lists none")]
    public async Task EnterOnAnIntervalListsItsRecords()
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

        // client.exe's group is chosen in the ranked table, and the interval table, reached by F6, chooses the first moment
        // with Home: the group stays chosen, in the ranked table too, counted for the moment - its start and three datagrams.
        RungRow group = workspace.RungRows[0];
        workspace.SelectedRung = group;
        Press(window, PhysicalKey.T);
        Press(window, PhysicalKey.F6);
        Press(window, PhysicalKey.F6);
        Press(window, PhysicalKey.F6);
        ListBox intervals = window.GetControl<ListBox>("IntervalList");
        ListBox rail = window.GetControl<ListBox>("RungList");
        Assert.True(intervals.IsKeyboardFocusWithin, Focused(window));
        Press(window, PhysicalKey.Home);
        TimeRange moment = Assert.IsType<IntervalRow>(intervals.SelectedItem).Interval;
        Assert.Equal(moment, workspace.SelectedInterval);
        await workspace.IntervalReady;
        Dispatch();
        Assert.Equal((group.Key, "4"), (workspace.SelectedRung?.Key, workspace.SelectedRung?.Observations));
        Assert.Equal(group.Key, Assert.IsType<RungRow>(rail.SelectedItem).Key);

        // Enter lists those four records, as E would, rather than opening the group's rung, and E's table has the keyboard.
        Press(window, PhysicalKey.Enter);
        Assert.True(workspace.IsEvidenceRung, workspace.LevelBadge);
        await workspace.EvidenceReady;
        Dispatch();
        Assert.Equal(moment, workspace.SelectedInterval);
        Assert.Equal(4, workspace.RungRows.Count);
        Assert.True(rail.IsKeyboardFocusWithin, Focused(window));

        // Esc comes back to the moment. Two rows down is a run of empty intervals, one row, and Enter on it lists nothing:
        // the reason has the keyboard, named by what it says, rather than nothing.
        Press(window, PhysicalKey.Escape);
        Assert.Equal("L0 · MACHINE", workspace.LevelBadge);
        Assert.True(rail.IsKeyboardFocusWithin, Focused(window));
        Press(window, PhysicalKey.F6);
        Press(window, PhysicalKey.F6);
        Assert.True(intervals.IsKeyboardFocusWithin, Focused(window));
        Press(window, PhysicalKey.ArrowDown);
        Press(window, PhysicalKey.ArrowDown);
        IntervalRow empty = Assert.IsType<IntervalRow>(intervals.SelectedItem);
        Assert.Equal((0L, 2), (empty.ObservationCount, empty.Cells));
        Press(window, PhysicalKey.Enter);
        Assert.True(workspace.IsEvidenceRung, workspace.LevelBadge);
        await workspace.EvidenceReady;
        Dispatch();
        Assert.Equal(empty.Interval, workspace.SelectedInterval);
        Assert.Empty(workspace.RungRows);
        ScrollViewer reason = window.GetControl<ScrollViewer>("EmptyReasonCard");
        Assert.True(reason.IsFocused, Focused(window));
        Assert.StartsWith("No admitted source record is in this scope.", AutomationProperties.GetName(reason), StringComparison.Ordinal);

        // Esc leaves it as from any record, and the ranked table has the keyboard again.
        Press(window, PhysicalKey.Escape);
        Assert.Equal("L0 · MACHINE", workspace.LevelBadge);
        Assert.True(rail.IsKeyboardFocusWithin, Focused(window));

        // E in the interval table on the first moment does what Enter does: the table's rows are rebuilt for E, so E's
        // table has the keyboard rather than nothing.
        Press(window, PhysicalKey.F6);
        Press(window, PhysicalKey.F6);
        Press(window, PhysicalKey.Home);
        Assert.Equal(moment, workspace.SelectedInterval);
        await workspace.IntervalReady;
        Dispatch();
        Press(window, PhysicalKey.E);
        Assert.True(workspace.IsEvidenceRung, workspace.LevelBadge);
        await workspace.EvidenceReady;
        Dispatch();
        Assert.Equal(4, workspace.RungRows.Count);
        Assert.True(rail.IsKeyboardFocusWithin, Focused(window));
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R15: E pressed in a list E rebuilds - a cell's records, the relationship table - gives E's table the keyboard")]
    public async Task EFromAListItRebuildsGivesItsTableTheKeyboard()
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
        ListBox rail = window.GetControl<ListBox>("RungList");

        // client.exe's process is opened, ] chooses its first moment with records, and F6 reaches the records the inspector
        // lists for that cell: E there lists them, and E's table has the keyboard rather than nothing.
        Press(window, PhysicalKey.F6);
        Press(window, PhysicalKey.Enter);
        Press(window, PhysicalKey.Enter);
        Assert.Equal("L2 · PROCESS", workspace.LevelBadge);
        await workspace.TimelineDetailReady;
        Dispatch();
        Press(window, PhysicalKey.F6);
        Press(window, PhysicalKey.F6);
        Press(window, PhysicalKey.BracketRight);
        await workspace.IntervalReady;
        await workspace.CellRecordsReady;
        Dispatch();
        ListBox records = window.GetControl<ListBox>("CellRecordsList");
        int listed = records.ItemCount;
        Assert.True(listed > 0, "no record listed");
        Press(window, PhysicalKey.F6);
        Assert.True(records.IsKeyboardFocusWithin, Focused(window));
        Press(window, PhysicalKey.E);
        Assert.True(workspace.IsEvidenceRung, workspace.LevelBadge);
        await workspace.EvidenceReady;
        Dispatch();
        Assert.Equal(listed, workspace.RungRows.Count);
        Assert.True(rail.IsKeyboardFocusWithin, Focused(window));

        // Back at the process, T shows the tables and F6 reaches the relationship table: E there lists the moment's records
        // of the process, and E's table has the keyboard again.
        Press(window, PhysicalKey.Escape);
        Assert.Equal("L2 · PROCESS", workspace.LevelBadge);
        Assert.True(rail.IsKeyboardFocusWithin, Focused(window));
        Press(window, PhysicalKey.T);
        Press(window, PhysicalKey.F6);
        Assert.True(window.GetControl<ListBox>("RelationshipList").IsKeyboardFocusWithin, Focused(window));
        Press(window, PhysicalKey.E);
        Assert.True(workspace.IsEvidenceRung, workspace.LevelBadge);
        await workspace.EvidenceReady;
        Dispatch();
        Assert.NotEmpty(workspace.RungRows);
        Assert.True(rail.IsKeyboardFocusWithin, Focused(window));
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R15: at E the chosen record's facts are a list the keyboard reads a fact at a time, each said as its name and value")]
    public async Task ARecordsFactsAreReadAFactAtATime()
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
        ListBox rail = window.GetControl<ListBox>("RungList");

        // E lists the session's records, its table holding the keyboard, and Down chooses the second.
        Press(window, PhysicalKey.F6);
        Press(window, PhysicalKey.E);
        Assert.True(workspace.IsEvidenceRung, workspace.LevelBadge);
        await workspace.EvidenceReady;
        Dispatch();
        Assert.True(rail.IsKeyboardFocusWithin, Focused(window));
        Press(window, PhysicalKey.ArrowDown);
        string chosen = Assert.IsType<RungRow>(workspace.SelectedRung).Key;
        Assert.True(workspace.HasSelectedEvidence);

        // Shift+F6 goes round to the inspector, whose list of the record's facts has the keyboard on the first, said as its
        // name and its value; Down reads the next, and the record stays the one chosen.
        Press(window, PhysicalKey.F6, RawInputModifiers.Shift);
        ListBox facts = window.GetControl<ListBox>("EvidenceFieldList");
        Assert.True(facts.IsKeyboardFocusWithin, Focused(window));
        IReadOnlyList<EvidenceField> fields = workspace.SelectedEvidenceFields;
        Assert.True(fields.Count > 2, $"{fields.Count} facts");
        Control first = Assert.IsAssignableFrom<Control>(facts.ContainerFromIndex(0));
        Assert.Same(first, window.FocusManager?.GetFocusedElement());
        Assert.Equal($"{fields[0].Label}: {fields[0].Value}", AutomationProperties.GetName(first));
        Press(window, PhysicalKey.ArrowDown);
        Control second = Assert.IsAssignableFrom<Control>(facts.ContainerFromIndex(1));
        Assert.Same(second, window.FocusManager?.GetFocusedElement());
        Assert.Equal($"{fields[1].Label}: {fields[1].Value}", AutomationProperties.GetName(second));
        Assert.Equal(chosen, workspace.SelectedRung?.Key);

        // Every fact is there for a screen reader, the last too, though the inspector shows only the first few.
        Control last = Assert.IsAssignableFrom<Control>(facts.ContainerFromIndex(fields.Count - 1));
        Assert.Equal($"{fields[^1].Label}: {fields[^1].Value}", AutomationProperties.GetName(last));

        // F6 goes on round to E's table, on the record chosen.
        Press(window, PhysicalKey.F6);
        Assert.True(rail.IsKeyboardFocusWithin, Focused(window));
        Assert.Equal(chosen, Assert.IsType<RungRow>(Assert.IsAssignableFrom<Control>(window.FocusManager?.GetFocusedElement()).DataContext).Key);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R15: a rung with no rows gives the reason it has none the keyboard, which F6 reaches as the rail")]
    public async Task F6ReachesAnEmptyRungsReason()
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

        // A moment with no record is the analysis interval, and E is pressed with the keyboard on the timeline, which keeps
        // it: E lists nothing, and says why.
        workspace.SelectInterval(new TimeRange(3, 5));
        await workspace.IntervalReady;
        Dispatch();
        Press(window, PhysicalKey.F6);
        Press(window, PhysicalKey.F6);
        Press(window, PhysicalKey.F6);
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        Assert.True(timeline.IsFocused, Focused(window));
        Press(window, PhysicalKey.E);
        Assert.True(workspace.IsEvidenceRung, workspace.LevelBadge);
        await workspace.EvidenceReady;
        Dispatch();
        Assert.Empty(workspace.RungRows);
        Assert.True(timeline.IsFocused, Focused(window));

        // F6 goes on to the inspector and round to the rail, whose ranked table lists nothing: the reason has the keyboard,
        // not the search box above it.
        Press(window, PhysicalKey.F6);
        Assert.True(window.GetControl<Control>("Inspector").IsKeyboardFocusWithin, Focused(window));
        Press(window, PhysicalKey.F6);
        ScrollViewer reason = window.GetControl<ScrollViewer>("EmptyReasonCard");
        Assert.True(reason.IsFocused, Focused(window));
        Assert.Equal(workspace.EmptyReason, AutomationProperties.GetName(reason));
        window.Close();
    }

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        Dispatch();
    }

    /// <summary>What has the keyboard, for a failed step to say.</summary>
    private static string Focused(Window window) =>
        "The keyboard is on " + (window.FocusManager?.GetFocusedElement() switch
        {
            null => "nothing",
            Control control => $"{control.GetType().Name} {control.Name} ({control.DataContext?.GetType().Name})",
            var other => other.ToString(),
        });

    private static CaptureUiUpdate Update(TemporarySession session) => new(
        CaptureUiPhase.Recording, "Recording", "A published generation.", SessionPath: session.Path,
        Overview: SessionOverviewProjector.Project(session.Store));

    /// <summary>
    /// client.exe, sending three datagrams to a resolver as it starts and three more a moment later, then to server.exe over
    /// one paired connection with a datagram after each send.
    /// </summary>
    private static ObservationRowV1[] Rows() =>
    [
        Timed(Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" }),
        Timed(Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe" }),
        .. Burst(1),
        .. Burst(5),
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

    /// <summary>Three datagrams client.exe sends to the resolver at one native tick.</summary>
    private static IEnumerable<ObservationRowV1> Burst(int tick) => Enumerable.Range(0, 3).Select(index => Timed(Transfer(tick,
        ObservationKind.Send, AccountingSide.SendSide, 20 + index, 100, (ulong)(80 + (3 * tick) + index))
        .Between("127.0.0.1:50002", "127.0.0.1:53") with { Mechanism = Mechanism.Udp }));

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
