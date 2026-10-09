using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using InterCat.Application;
using InterCat.Capture.Journal.Tests;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// The investigation window by keyboard alone (R15): it opens with the keyboard on its session, Ctrl+Tab shows its pages,
/// its timeline says the column its keys move to, Enter shows a note there, and the keyboard is never left on nothing - by
/// a button that disables itself, a list read again or a page hidden.
/// </summary>
public sealed class InvestigationKeyboardTests
{
    [AvaloniaFact(DisplayName = "R15: the investigation window opens with the keyboard on its session, keeps it there as the list is read again, and Ctrl+Tab shows its pages")]
    public async Task TheWindowOpensWithTheKeyboardOnItsSession()
    {
        using var root = new TemporaryDirectory();
        (string workspace, _, Guid b) = Aligned(root.Path);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = Open(main, workspace);
            ListBox sessions = Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one");

            // The first session's row has the keyboard as the window opens, so a screen reader reads the session.
            Control first = Assert.IsAssignableFrom<Control>(sessions.ContainerFromIndex(0));
            Assert.Same(first, Focus(window));
            Assert.Equal(window.View!.Members[0].AccessibleName, AutomationProperties.GetName(first));

            // Down chooses the next session; read again, the list gives the keyboard back to it, in its new row.
            Press(window, PhysicalKey.ArrowDown);
            Assert.Equal(b, Assert.IsType<InvestigationMemberRow>(sessions.SelectedItem).SessionId);
            await window.RefreshAsync();
            Dispatch();
            Assert.Same(sessions.ContainerFromIndex(1), Focus(window));
            Assert.Equal(b, Assert.IsType<InvestigationMemberRow>(sessions.SelectedItem).SessionId);

            // So does a row restated as InterCat's window keeps the session's layout, without a reading.
            string? before = window.View!.Members[1].Kept;
            window.LayoutKept(workspace, b, new WorkspaceLayout { SessionId = b, Pins = [], ScalesEachLane = true, UpdatedUtc = Committed });
            Dispatch();
            Assert.NotEqual(before, window.View!.Members[1].Kept);
            Assert.Same(sessions.ContainerFromIndex(1), Focus(window));

            // Ctrl+Tab and Ctrl+Shift+Tab, or Ctrl+Page Down and Ctrl+Page Up, show the next or previous page from anywhere,
            // the keyboard on its tab; past the last page is the first.
            TabControl pages = window.GetVisualDescendants().OfType<TabControl>().Single();
            Press(window, PhysicalKey.Tab, RawInputModifiers.Control);
            Assert.Equal(1, pages.SelectedIndex);
            Assert.Same(pages.ContainerFromIndex(1), Focus(window));
            Press(window, PhysicalKey.Tab, RawInputModifiers.Control | RawInputModifiers.Shift);
            Press(window, PhysicalKey.Tab, RawInputModifiers.Control | RawInputModifiers.Shift);
            Assert.Equal(3, pages.SelectedIndex);
            Assert.Same(pages.ContainerFromIndex(3), Focus(window));
            Press(window, PhysicalKey.PageDown, RawInputModifiers.Control);
            Assert.Equal(0, pages.SelectedIndex);
            Assert.Same(pages.ContainerFromIndex(0), Focus(window));
            Press(window, PhysicalKey.PageUp, RawInputModifiers.Control);
            Assert.Equal(3, pages.SelectedIndex);
            Press(window, PhysicalKey.PageDown, RawInputModifiers.Control);

            // A reading leaves the keyboard where a person moved it: on a control of this window - one for the chosen session,
            // which the new rows' choosing it again does not take from it - or of another window.
            Button relink = Named<Button>(window, "Relink the selected session to where it is now");
            TabTo(window, relink);
            await window.RefreshAsync();
            Dispatch();
            Assert.Same(relink, Focus(window));
            before = window.View!.Members[1].Kept;
            window.LayoutKept(workspace, b, new WorkspaceLayout { SessionId = b, Pins = [], ScalesEachLane = true, UpdatedUtc = Committed });
            Dispatch();
            Assert.NotEqual(before, window.View!.Members[1].Kept);
            Assert.Same(relink, Focus(window));
            Button start = main.GetControl<Button>("StartExploringButton");
            Assert.True(start.Focus(NavigationMethod.Tab));
            await window.RefreshAsync();
            Dispatch();
            Assert.Same(start, Focus(window));
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: an investigation read only once its window is active, as a desktop activates it, still gives its session the keyboard")]
    public async Task AnInvestigationReadLateStillGivesItsSessionTheKeyboard()
    {
        using var root = new TemporaryDirectory();
        (string workspace, _, _) = Aligned(root.Path);
        byte[] kept = await File.ReadAllBytesAsync(workspace);
        await File.WriteAllTextAsync(workspace, "not an investigation");
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            // The window is active before its investigation is read, as a desktop activates a window before any reading
            // ends: here the file cannot be read until it is put back.
            InvestigationWindow window = main.ShowInvestigation(workspace);
            TextBlock status = Named<TextBlock>(window, "Investigation status");
            WaitFor(() => window.IsActive && status.Text is { } said && said.StartsWith("This investigation could not be read", StringComparison.Ordinal));
            Dispatch();
            Assert.Null(window.View);

            // Read once it can be, its first session's row has the keyboard - not Add sessions, which nothing read had
            // given it while no session was known.
            await File.WriteAllBytesAsync(workspace, kept);
            await window.RefreshAsync();
            Dispatch();
            ListBox sessions = Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one");
            Assert.Same(sessions.ContainerFromIndex(0), Focus(window));
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: an investigation with no session yet opens with the keyboard on Add sessions")]
    public void AnEmptyInvestigationOpensOnAddSessions()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = Open(main, workspace);
            Assert.Empty(window.View!.Members);
            Assert.Same(Named<Button>(window, "Add sessions to this investigation"), Focus(window));
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: the investigation's timeline says the column its keys move to, and only its own name once the keyboard leaves")]
    public void TheTimelineSaysTheColumnItsKeysMoveTo()
    {
        using var root = new TemporaryDirectory();
        (string workspace, _, Guid b) = Aligned(root.Path);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = Open(main, workspace);

            // Ctrl+Tab twice shows the timeline, which keeps its own name while the page's tab has the keyboard.
            Press(window, PhysicalKey.Tab, RawInputModifiers.Control);
            Press(window, PhysicalKey.Tab, RawInputModifiers.Control);
            WaitFor(() => window.Timeline is not null);
            Dispatch();
            InvestigationTimelineControl chart = window.GetVisualDescendants().OfType<InvestigationTimelineControl>().Single();
            AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(chart);
            var said = new List<string?>();
            peer.PropertyChanged += (_, changed) =>
            {
                if (changed.Property == AutomationElementIdentifiers.NameProperty) said.Add(changed.NewValue as string);
            };
            Assert.Equal("The investigation's timeline", peer.GetName());

            // Tab reaches it past the page's tools: it is named by the column chosen, as the line beneath it says it, and a
            // screen reader is told.
            TabTo(window, chart);
            Assert.Equal("The investigation's timeline: " + window.ColumnReadout, peer.GetName());
            Assert.Equal(peer.GetName(), said[^1]);

            // Right moves to the next column, and Down to the next session's lane, each said as it is moved to.
            string before = window.ColumnReadout;
            Press(window, PhysicalKey.ArrowRight);
            Assert.NotEqual(before, window.ColumnReadout);
            Assert.Equal("The investigation's timeline: " + window.ColumnReadout, peer.GetName());
            Assert.Equal(peer.GetName(), said[^1]);
            Press(window, PhysicalKey.ArrowDown);
            Assert.StartsWith($"The investigation's timeline: Session {Short(b)}, column ", peer.GetName(), StringComparison.Ordinal);
            Assert.Equal(peer.GetName(), said[^1]);

            // A zoom says the column it leaves the cursor on, whose time it changed.
            string whole = peer.GetName()!;
            TimeRange shown = window.Timeline!.Interval!.Value;
            Press(window, PhysicalKey.NumPadAdd);
            WaitFor(() => window.Timeline!.Interval != shown);
            Dispatch();
            Assert.NotEqual(whole, peer.GetName());
            Assert.Equal("The investigation's timeline: " + window.ColumnReadout, peer.GetName());
            Assert.Equal(peer.GetName(), said[^1]);

            // Tab moves the keyboard on: the timeline keeps its own name, though its column stays chosen.
            Press(window, PhysicalKey.Tab);
            Assert.False(chart.IsKeyboardFocusWithin, Focused(window));
            Assert.NotNull(chart.ChosenColumn);
            Assert.Equal("The investigation's timeline", peer.GetName());
            Assert.Equal(peer.GetName(), said[^1]);
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: the investigation's timeline with no session placed says so, with the keyboard or without")]
    public void ATimelineWithNoSessionPlacedSaysSo()
    {
        var chart = new InvestigationTimelineControl();
        var window = new Window { Content = chart, Width = 600, Height = 300 };
        window.Show();
        Dispatch();
        Assert.Equal("The investigation's timeline: no session is placed on it", AutomationProperties.GetName(chart));
        Assert.True(chart.Focus(NavigationMethod.Tab));
        Dispatch();
        Assert.Equal("The investigation's timeline: no session is placed on it", AutomationProperties.GetName(chart));
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R15: Enter on a pinned note shows it on the timeline, which takes the keyboard and says its column; a note it cannot show stays, and the status says why")]
    public async Task EnterShowsANoteOnTheTimeline()
    {
        using var root = new TemporaryDirectory();
        (string workspace, _, Guid b) = Aligned(root.Path);
        Guid c = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "gamma", 2), Committed).SessionId;
        _ = InvestigationWorkspace.AddNote(workspace, "Beta begins", new WorkspaceNoteAnchor(b, 100_000), Committed);
        _ = InvestigationWorkspace.AddNote(workspace, "Gamma has no place", new WorkspaceNoteAnchor(c, 100_000), Committed);
        _ = InvestigationWorkspace.AddNote(workspace, "About all of it", null, Committed);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = Open(main, workspace);
            TabControl pages = window.GetVisualDescendants().OfType<TabControl>().Single();
            TextBlock status = Named<TextBlock>(window, "Investigation status");

            // Ctrl+Shift+Tab shows the notes; Tab enters their list, and Home chooses the first.
            Press(window, PhysicalKey.Tab, RawInputModifiers.Control | RawInputModifiers.Shift);
            Assert.Equal(3, pages.SelectedIndex);
            ListBox notes = Named<ListBox>(window, "Notes on this investigation; press Enter to show a pinned one on the timeline");
            Press(window, PhysicalKey.Tab);
            Assert.True(notes.IsKeyboardFocusWithin, Focused(window));
            Press(window, PhysicalKey.Home);
            Assert.Equal("Beta begins", Assert.IsType<InvestigationNoteRow>(notes.SelectedItem).Text);

            // A note whose session has no place stays listed with the keyboard on it, and the status says why; so does a note
            // about the whole investigation.
            Press(window, PhysicalKey.ArrowDown);
            Press(window, PhysicalKey.Enter);
            WaitFor(() => status.Text == "That note's session has no place in the investigation's time, so the timeline cannot show it.");
            Assert.Equal(3, pages.SelectedIndex);
            Assert.Same(notes.ContainerFromIndex(1), Focus(window));
            Press(window, PhysicalKey.ArrowDown);
            Press(window, PhysicalKey.Enter);
            Assert.Equal("That note is about the whole investigation, not an instant, so the timeline has no place for it.", status.Text);
            Assert.Equal(3, pages.SelectedIndex);
            Assert.Same(notes.ContainerFromIndex(2), Focus(window));

            // Enter on a pinned note shows the timeline zoomed around it, the keyboard on the timeline, which says the note's
            // column.
            Press(window, PhysicalKey.Home);
            Press(window, PhysicalKey.Enter);
            WaitFor(() => window.Zoom is not null && Focus(window) is InvestigationTimelineControl);
            Assert.Equal(2, pages.SelectedIndex);
            var chart = (InvestigationTimelineControl)Focus(window)!;
            Assert.StartsWith($"Session {Short(b)}, column ", window.ColumnReadout, StringComparison.Ordinal);
            Assert.Equal("The investigation's timeline: " + window.ColumnReadout, AutomationProperties.GetName(chart));

            // Show on the timeline does the same from its button.
            await window.ZoomToAsync(null);
            Press(window, PhysicalKey.Tab, RawInputModifiers.Control);
            Assert.Equal(3, pages.SelectedIndex);
            TabTo(window, Named<Button>(window, "Show on the timeline"));
            Press(window, PhysicalKey.Enter);
            WaitFor(() => window.Zoom is not null && chart.IsFocused);
            Assert.Equal(2, pages.SelectedIndex);
            Assert.Equal("The investigation's timeline: " + window.ColumnReadout, AutomationProperties.GetName(chart));
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: a button that disables itself by what it did gives the keyboard to its page; Remove and Find keep it while they can")]
    public void AButtonThatDisablesItselfGivesTheKeyboardToItsPage()
    {
        using var root = new TemporaryDirectory();
        (string workspace, _, Guid b) = Aligned(root.Path);
        _ = InvestigationWorkspace.AddNote(workspace, "Beta begins", new WorkspaceNoteAnchor(b, 100_000), Committed);
        _ = InvestigationWorkspace.AddNote(workspace, "About all of it", null, Committed);
        _ = InvestigationWorkspace.AddNote(workspace, "A third note", null, Committed);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = Open(main, workspace);
            ListBox sessions = Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one");

            // Withdraw alignment, done, is disabled for the session it withdrew: the session's row has the keyboard, and says
            // it is not aligned.
            Press(window, PhysicalKey.ArrowDown);
            TabTo(window, Named<Button>(window, "Withdraw alignment"));
            Press(window, PhysicalKey.Enter);
            WaitFor(() => window.View!.Members[1] is { SessionId: var withdrawn, IsAligned: false } && withdrawn == b);
            Dispatch();
            Assert.Same(sessions.ContainerFromIndex(1), Focus(window));
            InvestigationWorkspace.Align(workspace, b, 0, window.View!.Members[0].SessionId, 1_000_000_000, 1_000, 0, null, Committed);

            // Whole investigation, done, is disabled: the timeline has the keyboard, and says its column.
            Press(window, PhysicalKey.Tab, RawInputModifiers.Control);
            Press(window, PhysicalKey.Tab, RawInputModifiers.Control);
            WaitFor(() => window.Timeline is not null);
            InvestigationTimelineControl chart = window.GetVisualDescendants().OfType<InvestigationTimelineControl>().Single();
            Button zoomIn = Named<Button>(window, "Zoom in");
            TabTo(window, zoomIn);
            TimeRange whole = window.Timeline!.Interval!.Value;
            Press(window, PhysicalKey.Enter);
            WaitFor(() => window.Timeline!.Interval != whole);
            Dispatch();
            Assert.Same(zoomIn, Focus(window));
            TabTo(window, Named<Button>(window, "Whole investigation"));
            Press(window, PhysicalKey.Enter);
            WaitFor(() => window.Zoom is null && chart.IsFocused);
            Assert.Equal("The investigation's timeline: " + window.ColumnReadout, AutomationProperties.GetName(chart));

            // Remove chooses the note in the removed one's place - the next, or the one before the last - as a list chooses the
            // next row when one is removed, and keeps the keyboard for it; with no note left it is disabled, and Add a note
            // has the keyboard.
            Press(window, PhysicalKey.Tab, RawInputModifiers.Control);
            ListBox notes = Named<ListBox>(window, "Notes on this investigation; press Enter to show a pinned one on the timeline");
            Press(window, PhysicalKey.Tab);
            Press(window, PhysicalKey.Home);
            Press(window, PhysicalKey.ArrowDown);
            Assert.Equal("About all of it", Assert.IsType<InvestigationNoteRow>(notes.SelectedItem).Text);
            Button remove = Named<Button>(window, "Remove the selected note");
            TabTo(window, remove);
            Press(window, PhysicalKey.Enter);
            WaitFor(() => window.View!.Notes.Count == 2);
            Dispatch();
            Assert.Equal("A third note", Assert.IsType<InvestigationNoteRow>(notes.SelectedItem).Text);
            Assert.Same(remove, Focus(window));
            Press(window, PhysicalKey.Enter);
            WaitFor(() => window.View!.Notes.Count == 1);
            Dispatch();
            Assert.Equal("Beta begins", Assert.IsType<InvestigationNoteRow>(notes.SelectedItem).Text);
            Assert.Same(remove, Focus(window));
            Press(window, PhysicalKey.Enter);
            WaitFor(() => window.View!.Notes.Count == 0);
            Dispatch();
            Assert.False(remove.IsEnabled);
            Assert.Same(Named<Button>(window, "Add a note, pinned at the timeline's chosen column when there is one"), Focus(window));

            // Find candidate joins stays enabled as it reads, so the keyboard stays on it.
            Press(window, PhysicalKey.PageUp, RawInputModifiers.Control);
            Press(window, PhysicalKey.PageUp, RawInputModifiers.Control);
            Button find = Named<Button>(window, "Find candidate joins between the sessions");
            TabTo(window, find);
            Press(window, PhysicalKey.Enter);
            WaitFor(() => window.Candidates is not null);
            Dispatch();
            Assert.Same(find, Focus(window));
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: candidate joins found again keep the keyboard on the button for the chosen one, and a decision gives it to the candidate's row")]
    public async Task CandidatesFoundAgainKeepTheKeyboard()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        _ = InvestigationWorkspace.Add(workspace, Connected(root.Path, "client", ["10.0.0.1:50000", "10.0.0.2:443"], 100), Committed);
        _ = InvestigationWorkspace.Add(workspace, Connected(root.Path, "server", ["10.0.0.2:443", "10.0.0.1:50000"], 200), Committed);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = Open(main, workspace);
            Press(window, PhysicalKey.Tab, RawInputModifiers.Control);
            Button find = Named<Button>(window, "Find candidate joins between the sessions");
            TabTo(window, find);
            Press(window, PhysicalKey.Enter);
            WaitFor(() => window.Candidates is not null);
            Dispatch();
            Assert.Same(find, Focus(window));

            // Tab enters the candidates, and Home chooses the first.
            ListBox list = Named<ListBox>(window, "Candidate joins between the sessions; none is established");
            Press(window, PhysicalKey.Tab);
            Press(window, PhysicalKey.Home);
            Assert.Equal(0, list.SelectedIndex);

            // Found again, the candidates are new rows: the chosen one's row has the keyboard back, chosen still.
            await window.FindCandidatesAsync();
            Dispatch();
            Assert.Same(list.ContainerFromIndex(0), Focus(window));
            Assert.Equal(0, list.SelectedIndex);
            Button accept = Named<Button>(window, "Accept as one connection");

            // Found again with the keyboard on Accept, Accept keeps it, its candidate chosen again.
            TabTo(window, accept);
            await window.FindCandidatesAsync();
            Dispatch();
            Assert.Same(accept, Focus(window));
            Assert.Equal(0, list.SelectedIndex);

            // Accepted, Accept no longer applies to it: the candidate's row has the keyboard, and says it is accepted.
            Press(window, PhysicalKey.Enter);
            WaitFor(() => window.Candidates!.Rows[0].Decision == WorkspaceJoinDecision.Accepted);
            Dispatch();
            Assert.False(accept.IsEnabled);
            Control row = Assert.IsAssignableFrom<Control>(list.ContainerFromIndex(0));
            Assert.Same(row, Focus(window));
            Assert.Equal(window.Candidates!.Rows[0].AccessibleName, AutomationProperties.GetName(row));
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: F1 lists the investigation window's keys by where they act, and Esc gives the keyboard back to the session")]
    public void F1ListsTheInvestigationWindowsKeys()
    {
        using var root = new TemporaryDirectory();
        (string workspace, _, _) = Aligned(root.Path);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = Open(main, workspace);
            Control session = Assert.IsAssignableFrom<Control>(Focus(window));

            // F1 opens the sheet over the window, its list given the keyboard on its first row.
            Press(window, PhysicalKey.F1);
            KeysWindow sheet = Assert.IsType<KeysWindow>(window.KeysSheet);
            Assert.True(sheet.IsVisible);
            Assert.Equal("Keys", sheet.Title);
            Assert.Equal(InvestigationKeys.Intro, sheet.Intro);
            ListBox keys = sheet.List;
            Assert.Same(keys.ContainerFromIndex(0), sheet.FocusManager?.GetFocusedElement());
            Save(sheet, "investigation-keys-sheet.png");

            // It lists this window's keys, not the main window's, each place's first row saying the place.
            IReadOnlyList<WindowKey> listed = InvestigationKeys.All;
            Assert.Same(listed, keys.ItemsSource);
            Assert.Equal(["Anywhere", "A page's tab", "Sessions", "Candidate joins", "Timeline", "Notes", "A dialog"],
                listed.Where(key => key.Heads).Select(key => key.Where));
            Assert.Equal("A dialog. Enter: in one of its fields, do what the dialog is for: align, compare, state a translation or "
                + "save a view.", listed.Single(key => key is { Where: "A dialog", Heads: true }).AccessibleName);
            Assert.Equal("Anywhere. F1: list the keys this window takes.", AutomationProperties.GetName(keys.ContainerFromIndex(0)!));
            int pages = listed.ToList().FindIndex(key => key.Keys == "Ctrl+Tab or Ctrl+Shift+Tab");
            Assert.Equal("Ctrl+Tab or Ctrl+Shift+Tab: show the next or previous page: the sessions, the candidate joins, the "
                + "timeline or the notes.", listed[pages].AccessibleName);
            int zoom = listed.ToList().FindIndex(key => key.Keys == "+ or -");
            Assert.Equal("Plus or minus: zoom in or out around the chosen column.", listed[zoom].AccessibleName);
            Assert.Equal("Timeline. Left or Right: choose the previous or next column.",
                listed.Single(key => key is { Where: "Timeline", Heads: true }).AccessibleName);

            // Asked for again while it is open, it stays the one sheet; Esc closes it, the keyboard back on the session.
            Assert.True(window.ShowKeysAsync().IsCompleted);
            Assert.Same(sheet, window.KeysSheet);
            sheet.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatch();
            Assert.Null(window.KeysSheet);
            Assert.True(window.IsVisible);
            Assert.Same(session, Focus(window));

            // Keys (F1) at the footer's start opens the same sheet, for a pointer as for a screen reader's invoke.
            Button button = Named<Button>(window, "Keys (F1)");
            Assert.Equal("List every key this window takes, by where it acts", AutomationProperties.GetHelpText(button));
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatch();
            sheet = Assert.IsType<KeysWindow>(window.KeysSheet);
            Assert.Same(listed, sheet.List.ItemsSource);
            sheet.Close();
            Dispatch();
            Assert.Null(window.KeysSheet);
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    /// <summary>An investigation of alpha and beta, beta aligned so its start is alpha's 1 s.</summary>
    private static (string Workspace, Guid Alpha, Guid Beta) Aligned(string root)
    {
        string workspace = Path.Combine(root, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        Guid a = InvestigationWorkspace.Add(workspace, Datagrams(root, "alpha", 4), Committed).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, Datagrams(root, "beta", 6), Committed).SessionId;
        InvestigationWorkspace.Align(workspace, b, 0, a, 1_000_000_000, 1_000, 0, null, Committed);
        return (workspace, a, b);
    }

    private static InvestigationWindow Open(MainWindow main, string workspace)
    {
        InvestigationWindow window = main.ShowInvestigation(workspace);
        WaitFor(() => window.View is not null);
        Dispatch();
        return window;
    }

    /// <summary>A session whose process 100 sends <paramref name="records"/> datagrams 100 µs into its capture, 1 µs apart.</summary>
    private static string Datagrams(string root, string name, int records)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory), Guid.NewGuid(), "investigation-keyboard-tests");
        _ = Publish(
            store,
            [
                .. Enumerable.Range(0, records).Select(index =>
                    Transfer(1_000 + (index * 10), ObservationKind.Send, AccountingSide.SendSide, 10, 100, (ulong)(index + 1))
                        .Between("192.168.1.5:61000", "8.8.8.8:53") with { Mechanism = Mechanism.Udp, SessionRelativeTicks = (1_000 + (index * 10)) * 100 }),
            ],
            capture: CaptureId.New(),
            clock: ClockFor(ClockId.New(), "lab-" + name));
        store.ReleaseSegmentReaders();
        return directory;
    }

    /// <summary>A session whose process <paramref name="owner"/> opens, sends on and closes one TCP connection between two ends.</summary>
    private static string Connected(string root, string name, string[] ends, int owner)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory), Guid.NewGuid(), "investigation-keyboard-tests");
        _ = Publish(
            store,
            [
                Lifecycle(1, ObservationKind.Create, owner, 1) with { SessionRelativeTicks = 100 },
                Transfer(1_000, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, owner, 20).Between(ends[0], ends[1]) with { SessionRelativeTicks = 100_000 },
                Transfer(1_001, ObservationKind.Send, AccountingSide.SendSide, 64, owner, 21).Between(ends[0], ends[1]) with { SessionRelativeTicks = 100_100 },
                Transfer(1_002, ObservationKind.Disconnect, AccountingSide.EndpointActivity, 0, owner, 22).Between(ends[0], ends[1]) with { SessionRelativeTicks = 100_200 },
            ],
            capture: CaptureId.New(),
            clock: ClockFor(ClockId.New(), "lab-" + name));
        store.ReleaseSegmentReaders();
        return directory;
    }

    /// <summary>Presses Tab until <paramref name="target"/> has the keyboard, as a person would.</summary>
    private static void TabTo(Window window, Control target)
    {
        for (int press = 0; press < 20 && !target.IsFocused; press++)
        {
            Press(window, PhysicalKey.Tab);
        }

        Assert.True(target.IsFocused, Focused(window));
    }

    private static void Save(Window window, string name)
    {
        Avalonia.Media.Imaging.WriteableBitmap frame = Assert.IsAssignableFrom<Avalonia.Media.Imaging.WriteableBitmap>(window.CaptureRenderedFrame());
        string directory = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(directory);
        frame.Save(Path.Combine(directory, name));
    }

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        Dispatch();
    }

    private static IInputElement? Focus(Window window) => window.FocusManager?.GetFocusedElement();

    private static string Focused(Window window) =>
        "The keyboard is on " + (Focus(window) switch
        {
            null => "nothing",
            Control control => $"{control.GetType().Name} '{AutomationProperties.GetName(control)}' ({(control as ContentControl)?.Content})",
            var other => other.ToString(),
        });

    private static T Named<T>(Window window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<T>()
            .Single(control => AutomationProperties.GetName(control) == name
                || ControlAutomationPeer.CreatePeerForElement(control).GetName() == name);

    private static string Short(Guid identity) => identity.ToString("N")[..8];

    private static void WaitFor(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            Dispatch();
            Thread.Sleep(10);
        }

        Assert.True(condition());
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
