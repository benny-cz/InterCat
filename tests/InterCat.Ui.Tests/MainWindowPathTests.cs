using System.Runtime.CompilerServices;
using System.Text;
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
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// The main window's paths that only the methods behind them were tested through, run as a person runs them: a session's
/// channels, a record's original and its content opened from the window, by key or double click, records kept through the
/// pins dialog, a process's next page of HTTP exchanges, a call's other end by O, the ranked table and header by the
/// pointer, and a capture that ends before it shows anything.
/// </summary>
public sealed class MainWindowPathTests
{
    [AvaloniaFact(DisplayName = "§3.2: a session's channels, a record's original and its content open from the main window as a person opens them, each giving the keyboard back to the record")]
    public async Task RecordWindowsOpenAsAPersonOpensThem()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] rows =
        [
            Lifecycle(1, ObservationKind.Inventory, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 100 },
            Lifecycle(2, ObservationKind.Inventory, 200, 2) with { ResourceName = @"C:\Tools\server.exe", SessionRelativeTicks = 200 },
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 6, 100, 10).Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = 1_000 },
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 6, 200, 11).Between(ServerEnd, ClientEnd) with { SessionRelativeTicks = 1_100 },
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 16, 100, 12).Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = 2_000 },
            Transfer(21, ObservationKind.Receive, AccountingSide.ReceiveSide, 16, 200, 13).Between(ServerEnd, ClientEnd) with { SessionRelativeTicks = 2_100 },
        ];
        Publish(session.Store, rows, content: (ContentHeader(),
        [
            Content(rows[2], Encoding.UTF8.GetBytes("GET /x")),
            Content(rows[4], Encoding.UTF8.GetBytes("0123456789ABCDEF")),
        ]));
        var window = new MainWindow { Width = 1_456, Height = 939 };
        window.Show();
        try
        {
            WorkspaceViewModel workspace = await Open(window, session);
            ListBox rung = window.GetControl<ListBox>("RungList");

            // Browse paired channels opens the browser, whose Enter lists the chosen channel's records at the evidence rung,
            // the first with the keyboard.
            Button browse = window.GetControl<Button>("BrowseChannelsButton");
            SessionChannelWindow browser = Opened<SessionChannelWindow>(window, browse);
            ListBox channels = browser.GetVisualDescendants().OfType<ListBox>().Single();
            WaitFor(() => channels.ItemCount == 1 && browser.FocusManager?.GetFocusedElement() is ListBoxItem);
            browser.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            WaitFor(() => window.OwnedWindows.Count == 0);
            await workspace.EvidenceReady;
            Assert.Equal(("L5 · EVIDENCE", 4), (workspace.LevelBadge, workspace.RungRows.Count));
            WaitFor(() => RecordHasKeyboard(window, rung, 0));

            // Enter on a record opens its original record, whose text has the keyboard; Escape gives it back to the record.
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            SessionRawRecordWindow raw = Owned<SessionRawRecordWindow>(window);
            WaitFor(() => raw.FocusManager?.GetFocusedElement() is TextBox { Text.Length: > 0 });
            raw.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            WaitFor(() => window.OwnedWindows.Count == 0 && RecordHasKeyboard(window, rung, 0));

            // C on a record with content opens it, its bytes hidden until asked; Escape gives the keyboard back to the record.
            Assert.True(workspace.HasSelectedContent);
            window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.None);
            SessionContentWindow content = Owned<SessionContentWindow>(window);
            WaitFor(() => content.FocusManager?.GetFocusedElement() is Button { Content: "Show the kept bytes" });
            content.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            WaitFor(() => window.OwnedWindows.Count == 0 && RecordHasKeyboard(window, rung, 0));

            // The inspector's buttons open the same windows by the pointer.
            Button inspect = window.GetVisualDescendants().OfType<Button>().Single(button => button.IsEffectivelyVisible
                && Equals(button.Content, "Inspect its content (C)"));
            Escape(window, Opened<SessionContentWindow>(window, inspect));
            Button original = window.GetVisualDescendants().OfType<Button>().Single(button => button.IsEffectivelyVisible
                && Equals(button.Content, workspace.OriginalRecordLabel));
            Assert.Equal("Open original record (Enter)", ControlAutomationPeer.CreatePeerForElement(original).GetName());
            Assert.Equal("Open the selected record's original journal entry", AutomationProperties.GetHelpText(original));
            Escape(window, Opened<SessionRawRecordWindow>(window, original));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§11.3: a redacted package's record opens from its row as a synthetic record, and one it cannot verify is still called one")]
    public async Task APackagesRecordOpensAsASyntheticRecord()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 1).Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = 1_000 },
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 2).Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = 2_000 },
        ]);
        string destination = Path.Combine(Path.GetTempPath(), "InterCat.Ui.Tests.Packages", Guid.NewGuid().ToString("N"));
        RedactedSessionPackage.Create(session.Store, destination, Committed);
        var window = new MainWindow { Width = 1_456, Height = 939 };
        window.Show();
        try
        {
            Assert.True(await window.OpenSessionAsync(destination));
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            Assert.True(workspace.ShowEvidence());
            await workspace.EvidenceReady;
            workspace.SelectedRung = workspace.RungRows[0];
            Settle(window);
            Button open = window.GetVisualDescendants().OfType<Button>().Single(button => button.IsEffectivelyVisible
                && Equals(button.Content, "Open synthetic record (Enter)"));

            // A screen reader says the words shown, and hears what they open: never the original journal's entry.
            Assert.Equal("Open synthetic record (Enter)", ControlAutomationPeer.CreatePeerForElement(open).GetName());
            Assert.Equal("Open the selected record's synthetic entry; a redacted package holds no original record",
                AutomationProperties.GetHelpText(open));
            SessionRawRecordWindow raw = Opened<SessionRawRecordWindow>(window, open);
            WaitFor(() => raw.FocusManager?.GetFocusedElement() is TextBox { Text.Length: > 0 });
            Assert.Equal("InterCat · Synthetic record of a redacted package", raw.Title);
            Escape(window, raw);

            // With its journal gone from under the open package, the record cannot be read, and the window - told by the
            // row it was opened from - still calls it what the package holds.
            SessionStore package = SessionStore.OpenExisting(LocalOwnedDirectory.Open(destination));
            string[] journals = [.. package.Current!.Dependencies
                .Where(dependency => dependency.Kind == StoreDependencyKind.Journal)
                .Select(dependency => Path.Combine(destination, dependency.Name))];
            package.ReleaseSegmentReaders();
            Assert.NotEmpty(journals);
            foreach (string journal in journals) File.Delete(journal);
            raw = Opened<SessionRawRecordWindow>(window, open);
            TextBlock status = raw.GetVisualDescendants().OfType<TextBlock>()
                .Single(text => AutomationProperties.GetName(text) == "Synthetic record status");
            WaitFor(() => status.Text?.StartsWith("Could not verify the synthetic record: ", StringComparison.Ordinal) == true);
            Assert.Equal("InterCat · Synthetic record of a redacted package", raw.Title);
            Escape(window, raw);
        }
        finally
        {
            window.Close();
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    [AvaloniaFact(DisplayName = "R15: a main-window action not offered says why on itself - its tooltip, shown disabled, and its help text - and what it does once offered")]
    public async Task AnActionNotOfferedSaysWhy()
    {
        var window = new MainWindow { Width = 1_456, Height = 939 };

        // Before any session, none of its actions is offered, and each says why on itself from the first frame - before the
        // window has shown or read anything.
        foreach (string name in new[] { "ShowRecordsButton", "BrowseChannelsButton", "SharePackageButton", "ShareOriginalButton", "KeepRecordsButton" })
        {
            Assert.Equal("Open or record a session first.", WhyNot(window.GetControl<Button>(name)));
        }

        window.Show();
        try
        {
            Settle(window);

            Assert.Equal("Open or record a session first: there is no graph to lay out.", WhyNot(window.GetControl<Button>("RelayoutButton")));
            Assert.Equal("Select a node of the graph first.", WhyNot(window.GetControl<Button>("PinNodeButton")));

            // A session shown, each says what it does.
            using var session = new TemporarySession();
            Publish(session.Store,
            [
                Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 1).Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = 1_000 },
                Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 2).Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = 2_000 },
            ]);
            WorkspaceViewModel workspace = await Open(window, session);
            Assert.Equal("Show this rung's source records in the ladder", Offered(window.GetControl<Button>("ShowRecordsButton")));
            Assert.Equal("Save a reopenable, pseudonymized copy of this session to share", Offered(window.GetControl<Button>("SharePackageButton")));
            Assert.Equal("Save an exact, unredacted copy of this session's evidence to share", Offered(window.GetControl<Button>("ShareOriginalButton")));
            Assert.Equal("Keep this session's records from the time scope's start through every release, or remove a pin",
                Offered(window.GetControl<Button>("KeepRecordsButton")));
            Assert.Equal("Lay the graph out afresh, keeping pinned nodes where they are", Offered(window.GetControl<Button>("RelayoutButton")));

            // At the source records, Show source records says where the view is, and the record action waits for a record.
            Assert.True(workspace.ShowEvidence());
            await workspace.EvidenceReady;
            Settle(window);
            Assert.Equal("You are at the source records. Esc returns to where you were.", WhyNot(window.GetControl<Button>("ShowRecordsButton")));
            workspace.SelectedRung = null;
            Settle(window);
            Button record = window.GetVisualDescendants().OfType<Button>().Single(button => button.IsEffectivelyVisible
                && Equals(button.Content, workspace.OriginalRecordLabel));
            Assert.Equal("Choose a record first.", WhyNot(record));
            workspace.SelectedRung = workspace.RungRows[0];
            Settle(window);
            Assert.Equal("Open the selected record's original journal entry", Offered(record));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Why an action is not offered: its tooltip, shown on the disabled button, and its help text, which say the same.</summary>
    private static string? WhyNot(Button button) => Offers.WhyNot(button);

    /// <summary>What an offered action says it does.</summary>
    private static string? Offered(Button button)
    {
        Assert.True(button.IsEnabled);
        return AutomationProperties.GetHelpText(button);
    }

    [AvaloniaFact(DisplayName = "R15: Keep records places a pin through its dialog and removes it through the same, the button having the keyboard back each time")]
    public async Task KeepRecordsThroughItsDialog()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 1).Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = 1_000 },
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 2).Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = 2_000 },
        ]);
        var window = new MainWindow { Width = 1_456, Height = 939 };
        window.Show();
        try
        {
            _ = await Open(window, session);
            TextBlock status = window.GetControl<TextBlock>("CaptureStatus");
            Button keep = window.GetControl<Button>("KeepRecordsButton");

            // The dialog opens on why to keep them; Keep records places the pin, and the button has the keyboard back.
            Window prompt = Opened<Window>(window, keep);
            Assert.Equal("Keep this session's records", prompt.Title);
            TextBox reason = prompt.GetVisualDescendants().OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == "Why keep these records");
            Assert.Same(reason, prompt.FocusManager?.GetFocusedElement());
            reason.Text = "the incident";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            prompt.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "PlacePin").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => window.OwnedWindows.Count == 0 && status.Text == "Records kept");
            Assert.Equal("the incident", Assert.Single(session.Store.Pins()).Reason);
            WaitFor(() => ReferenceEquals(window.FocusManager?.GetFocusedElement(), keep));

            // Opened again it lists the pin standing, whose Remove takes it away.
            prompt = Opened<Window>(window, keep);
            prompt.GetVisualDescendants().OfType<Button>().Single(button => button.Name?.StartsWith("RemovePin", StringComparison.Ordinal) == true)
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => window.OwnedWindows.Count == 0 && status.Text == "Pin removed");
            Assert.Empty(session.Store.Pins());
            WaitFor(() => ReferenceEquals(window.FocusManager?.GetFocusedElement(), keep));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§3.2: M lists a process's next page of HTTP exchanges, as Load more does, the chosen row keeping the keyboard, until none is left")]
    public async Task MListsTheNextPageOfExchanges()
    {
        using var session = new TemporarySession();
        Publish(session.Store, HttpExchangeLaneTests.ManyExchanges(250, out SourceFieldRowV1[] fields), fields: fields);
        (MainWindow window, WorkspaceViewModel workspace, _) = await HttpExchangeLaneTests.OpenExchanges(session);
        try
        {
            Assert.Equal((100, true, "Load more exchanges (M)"), (workspace.RungRows.Count, workspace.CanLoadMore, workspace.LoadMoreLabel));
            Assert.Contains(" · 100 listed · ", workspace.LevelSummary, StringComparison.Ordinal);
            ListBox rung = window.GetControl<ListBox>("RungList");
            workspace.SelectedRung = workspace.RungRows[5];
            Settle(window);
            Assert.True(rung.ContainerFromIndex(5)!.Focus());
            RungRow chosen = workspace.SelectedRung;

            window.KeyPressQwerty(PhysicalKey.M, RawInputModifiers.None);
            await workspace.HttpReady;
            Settle(window);
            Assert.Equal((200, true), (workspace.RungRows.Count, workspace.CanLoadMore));
            Assert.Equal(chosen.Key, workspace.SelectedRung?.Key);
            Assert.True(RecordHasKeyboard(window, rung, 5));
            Assert.Contains(" · 200 listed · ", workspace.LevelSummary, StringComparison.Ordinal);

            // The button below the rows does the same, and the last page leaves nothing to load.
            window.GetVisualDescendants().OfType<Button>().Single(button => button.IsEffectivelyVisible
                && Equals(button.Content, "Load more exchanges (M)")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await workspace.HttpReady;
            Settle(window);
            Assert.Equal((250, false), (workspace.RungRows.Count, workspace.CanLoadMore));
            Assert.DoesNotContain(" listed", workspace.LevelSummary, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§6.7: a ranked row double-clicked opens its rung as Enter does, and the header's buttons act as their keys do")]
    public async Task ThePointerOpensRowsAndTheHeaderActs()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Inventory, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 100 },
            Lifecycle(2, ObservationKind.Inventory, 200, 2) with { ResourceName = @"C:\Tools\server.exe", SessionRelativeTicks = 200 },
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 10).Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = 1_000 },
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 11).Between(ServerEnd, ClientEnd) with { SessionRelativeTicks = 1_100 },
        ]);
        var window = new MainWindow { Width = 1_456, Height = 939 };
        window.Show();
        try
        {
            WorkspaceViewModel workspace = await Open(window, session);
            ListBox rung = window.GetControl<ListBox>("RungList");
            int client = workspace.RungRows.ToList().FindIndex(row => row.Label.Contains("client", StringComparison.Ordinal));
            int server = workspace.RungRows.ToList().FindIndex(row => row.Label.Contains("server", StringComparison.Ordinal));

            // Two processes chosen with Ctrl+click, Show their records lists theirs, as Enter does.
            Click(window, rung, client, RawInputModifiers.Control);
            Click(window, rung, server, RawInputModifiers.Control);
            Assert.True(workspace.HasMultiSelection);
            window.GetControl<Button>("ShowChosenRecordsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await workspace.EvidenceReady;
            Settle(window);
            Assert.Equal(("L5 · EVIDENCE", 4), (workspace.LevelBadge, workspace.RungRows.Count));

            // The header's button goes back up a rung, as Esc does, and the tables' button shows and hides them, as T does.
            Button ascend = window.GetControl<Button>("AscendButton");
            ascend.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Settle(window);
            Assert.Equal("L0 · MACHINE", workspace.LevelBadge);
            Button tables = window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, workspace.TableToggleLabel));
            bool shown = workspace.ShowTables;
            tables.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(!shown, workspace.ShowTables);
            tables.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(shown, workspace.ShowTables);

            // A row double-clicked opens its rung, as Enter does.
            Settle(window);
            client = workspace.RungRows.ToList().FindIndex(row => row.Label.Contains("client", StringComparison.Ordinal));
            Click(window, rung, client, RawInputModifiers.None);
            Click(window, rung, client, RawInputModifiers.None);
            Settle(window);
            Assert.Equal("L1 · GROUP", workspace.LevelBadge);
            Assert.Contains("client.exe", workspace.Crumbs[^1].Label, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [AvaloniaFact(DisplayName = "§3.2: O on a linked call opens the call at its other end, its records the keyboard's, and on a call whose other end is unknown does nothing")]
    public async Task OOpensACallsOtherEnd()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = LinkedCalls();
        Publish(session.Store, rows, fields: fields);
        var window = new MainWindow { Width = 1_400, Height = 900 };
        window.Show();
        try
        {
            WorkspaceViewModel workspace = await Open(window, session);
            await CallsOfTheCaller(workspace);
            Settle(window);
            ListBox rung = window.GetControl<ListBox>("RungList");
            Assert.Equal([true, false, true], workspace.RungRows.Select(row => row.HasOtherEnd));

            // The second call's other end is unknown: O on it goes nowhere, and the call keeps the keyboard.
            Click(window, rung, 1, RawInputModifiers.None);
            (string Badge, int Crumbs) at = (workspace.LevelBadge, workspace.Crumbs.Count);
            window.KeyPressQwerty(PhysicalKey.O, RawInputModifiers.None);
            Settle(window);
            Assert.Equal(at, (workspace.LevelBadge, workspace.Crumbs.Count));
            Assert.True(RecordHasKeyboard(window, rung, 1));

            // The first's is known: O opens the call that served it, as its menu does, its records the keyboard's.
            Click(window, rung, 0, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.O, RawInputModifiers.None);
            await workspace.EvidenceReady;
            Settle(window);
            Assert.True(workspace.IsEvidenceRung);
            Assert.Contains("RPC calls served on svcctl", workspace.Crumbs[^2].Label, StringComparison.Ordinal);
            WaitFor(() => RecordHasKeyboard(window, rung, 0));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§3.2: a record double-clicked opens its original record, as Enter does, and Esc gives the keyboard back to it")]
    public async Task ARecordDoubleClickedOpensItsOriginal()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 1).Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = 1_000 },
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 32, 100, 2).Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = 2_000 },
        ]);
        var window = new MainWindow { Width = 1_456, Height = 939 };
        window.Show();
        try
        {
            WorkspaceViewModel workspace = await Open(window, session);
            Assert.True(workspace.ShowEvidence());
            await workspace.EvidenceReady;
            Settle(window);
            ListBox rung = window.GetControl<ListBox>("RungList");

            // The second record, double-clicked, is the one its original record window opens on.
            Click(window, rung, 1, RawInputModifiers.None);
            Click(window, rung, 1, RawInputModifiers.None);
            SessionRawRecordWindow raw = Owned<SessionRawRecordWindow>(window);
            Assert.Equal((workspace.RungRows[1].Key, true), (workspace.SelectedRung?.Key, workspace.HasSelectedEvidence));
            WaitFor(() => raw.FocusManager?.GetFocusedElement() is TextBox { Text.Length: > 0 });
            raw.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            WaitFor(() => window.OwnedWindows.Count == 0 && RecordHasKeyboard(window, rung, 1));
            Assert.Equal("L5 · EVIDENCE", workspace.LevelBadge);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§3.1: a capture that ends before it shows anything gives back the session shown - its actions, name and records - and says whether it saved one")]
    public async Task ACaptureThatShowsNothingGivesTheSessionBack()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 1).Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = 1_000 },
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 2).Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = 2_000 },
        ]);
        string recorded = Path.Combine(Path.GetTempPath(), "InterCat.Ui.Tests.Captures", Guid.NewGuid().ToString("N"));
        var window = new MainWindow { Width = 1_456, Height = 939 };
        window.Show();
        try
        {
            WorkspaceViewModel workspace = await Open(window, session);
            TextBlock status = window.GetControl<TextBlock>("CaptureStatus");
            TextBlock detail = window.GetControl<TextBlock>("CaptureDetail");
            TextBlock path = window.GetControl<TextBlock>("CaptureSessionPath");
            string named = window.Title!;
            Assert.Equal(session.Path, path.Text);

            // An approval declined: the session is shown as it was, and is still the one the window's actions act on.
            window.CaptureRunner = (report, _, _) =>
            {
                report(new(CaptureUiPhase.Unavailable, "Administrator approval declined",
                    "Windows did not approve the broker, so nothing was recorded."));
                return Task.CompletedTask;
            };
            Start(window);
            WaitFor(() => status.Text == "Administrator approval declined");
            Settle(window);
            Assert.Same(workspace, window.DataContext);
            Assert.Equal((session.Path, named), (path.Text, window.Title));
            Assert.Equal("Show this rung's source records in the ladder", Offered(window.GetControl<Button>("ShowRecordsButton")));
            Assert.Equal("Keep this session's records from the time scope's start through every release, or remove a pin",
                Offered(window.GetControl<Button>("KeepRecordsButton")));
            Assert.Equal("Save an exact, unredacted copy of this session's evidence to share",
                Offered(window.GetControl<Button>("ShareOriginalButton")));

            // A capture that fails before saving anything says so, never that a session below holds its evidence; its
            // records still open from the session given back.
            window.CaptureRunner = (_, _, _) => throw new InvalidOperationException("The broker's pipe closed.");
            Start(window);
            WaitFor(() => status.Text == "Capture interrupted");
            Assert.Equal("The broker's pipe closed. The capture saved no session.", detail.Text);
            Assert.Equal((session.Path, named), (path.Text, window.Title));
            Assert.True(workspace.ShowEvidence());
            await workspace.EvidenceReady;
            Settle(window);
            ListBox rung = window.GetControl<ListBox>("RungList");
            Click(window, rung, 0, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            SessionRawRecordWindow raw = Owned<SessionRawRecordWindow>(window);
            WaitFor(() => raw.FocusManager?.GetFocusedElement() is TextBox { Text.Length: > 0 });
            raw.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            WaitFor(() => window.OwnedWindows.Count == 0);

            // One that showed a view of its own before it stopped leaves that view, which its words point to: the session
            // before is not given back over it.
            using var published = new TemporarySession();
            Publish(published.Store,
            [
                Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 16, 300, 3).Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = 3_000 },
            ]);
            window.CaptureRunner = (report, _, _) =>
            {
                report(new(CaptureUiPhase.Recording, "Recording · follow latest", "Recording.", SessionPath: published.Path,
                    Overview: SessionOverviewProjector.Project(published.Store)));
                report(new(CaptureUiPhase.Unavailable, "Capture interrupted",
                    "The broker stopped. Any published session data remains at the path shown below.", SessionPath: published.Path));
                return Task.CompletedTask;
            };
            Start(window);
            WaitFor(() => status.Text == "Capture interrupted" && path.Text == published.Path);
            Settle(window);
            Assert.NotSame(workspace, window.DataContext);
            Assert.Single(((WorkspaceViewModel)window.DataContext!).Snapshot.Processes);
            Assert.Equal($"InterCat · {Path.GetFileName(published.Path)}", window.Title);

            // One that recorded into a session of its own before it failed has shown that session's place instead, which
            // it points to; the session before is not given back over it.
            window.CaptureRunner = (report, _, _) =>
            {
                report(new(CaptureUiPhase.Recording, "Recording · follow latest", "Recording.", SessionPath: recorded));
                throw new InvalidOperationException("The broker's pipe closed.");
            };
            Start(window);
            WaitFor(() => status.Text == "Capture interrupted");
            Assert.Equal("The broker's pipe closed. Any published evidence is retained in the session shown below.", detail.Text);
            Assert.Equal(recorded, path.Text);
            Assert.True(((WorkspaceViewModel)window.DataContext!).IsEmptyWorkspace);
            Assert.Equal("Open or record a session first.", Offers.WhyNot(window.GetControl<Button>("ShowRecordsButton")));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Starts a capture as Start exploring does.</summary>
    private static void Start(MainWindow window)
    {
        Button start = window.GetControl<Button>("StartExploringButton");
        Assert.True(start.IsEffectivelyEnabled);
        start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    [AvaloniaFact(DisplayName = "R15: a ranked row's menu offers what acts on its row - a linked call's other end, never a selection a call or a record cannot join - and a row with nothing to offer opens none")]
    public async Task ARowsMenuOffersWhatActsOnIt()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = LinkedCalls();
        Publish(session.Store, rows, fields: fields);
        var window = new MainWindow { Width = 1_400, Height = 900 };
        window.Show();
        WorkspaceViewModel workspace = await Open(window, session);
        ProcessNode caller = workspace.Snapshot.Processes.Single(node => node.ProcessId == 400);
        ListBox rung = window.GetControl<ListBox>("RungList");

        // A process joins the selection, so its row's menu offers it, and nothing else.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == caller.GroupKey);
        Assert.True(workspace.Descend());
        Settle(window);
        Assert.True(OpenMenu(window, rung, 0, byKey: true));
        Assert.Equal(["Add to or remove from the selection (Ctrl+Space)"],
            MenuOf(rung, 0).Items.OfType<MenuItem>().Where(item => item.IsVisible).Select(item => item.Header));
        MenuOf(rung, 0).Close();
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == caller.Id.ToString());
        Assert.True(workspace.Descend());
        await workspace.RpcReady;
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label.StartsWith("svcctl", StringComparison.Ordinal));
        Assert.True(workspace.Descend());
        await workspace.RpcReady;
        Settle(window);
        Assert.Equal([true, false, true], workspace.RungRows.Select(row => row.HasOtherEnd));
        Assert.All(workspace.RungRows, row => Assert.False(row.JoinsSelection));

        // A call joins no selection, so its menu does not offer to add it, where it once offered and added nothing; a
        // linked call's offers its other end.
        ContextMenu menu = MenuOf(rung, 0);
        Assert.True(OpenMenu(window, rung, 0, byKey: true));
        Assert.Equal(["Open the call services.exe · 1960 served (O)"],
            menu.Items.OfType<MenuItem>().Where(item => item.IsVisible).Select(item => item.Header));

        // A call whose other end is unknown has nothing to offer: neither the key nor a right-click opens its menu.
        menu.Close();
        Settle(window);
        Assert.False(OpenMenu(window, rung, 1, byKey: true));
        Assert.False(OpenMenu(window, rung, 1, byKey: false));

        // The other end opens from the menu as O opens it: the call that served it, its records the keyboard's.
        Assert.True(OpenMenu(window, rung, 0, byKey: true));
        menu.Items.OfType<MenuItem>().Single(item => item.IsVisible).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        await workspace.EvidenceReady;
        Settle(window);
        Assert.True(workspace.IsEvidenceRung);
        Assert.Contains("RPC calls served on svcctl", workspace.Crumbs[^2].Label, StringComparison.Ordinal);
        Assert.True(RecordHasKeyboard(window, rung, 0));

        // A record has nothing to offer either.
        Assert.False(OpenMenu(window, rung, 0, byKey: true));
        Assert.False(OpenMenu(window, rung, 0, byKey: false));
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§6.7: a lane's name makes it the step focus - a mechanism's at the machine, a process's among a group's - the machine row's gives the group back, and the fold's chooses none")]
    public async Task ALanesNameChoosesIt()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Pool(230));
        var window = new MainWindow { Width = 1_400, Height = 900 };
        window.Show();
        WorkspaceViewModel workspace = await Open(window, session);
        TimelineView timeline = window.GetControl<TimelineView>("TimelineSurface");
        ComboBox lanes = window.GetControl<ComboBox>("TimelineLaneSelector");

        // At the machine, a mechanism lane's name chooses it, as its selector does, and the timeline has the keyboard.
        MechanismTimelineLane tcp = workspace.Snapshot.MechanismLanes.Single(lane => lane.Mechanism == Mechanism.Tcp);
        ClickName(window, timeline, timeline.PointOf(tcp.Buckets.First(bucket => bucket.ObservationCount > 0))!.Value.Y);
        Assert.Equal(Mechanism.Tcp, workspace.SelectedTimelineMechanism);
        Assert.Equal(Mechanism.Tcp, Assert.IsType<TimelineLaneOption>(lanes.SelectedItem).Mechanism);
        Assert.True(timeline.IsFocused);

        // Among the group's lanes, a process's name chooses its row in the ranked table.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label.StartsWith("pool.exe", StringComparison.Ordinal));
        Assert.True(workspace.Descend());
        Settle(window);
        timeline.RequestDetailNow();
        await workspace.TimelineDetailReady;
        Settle(window);
        FoldedProcessLane folded = Assert.IsType<FoldedProcessLane>(workspace.FoldedLane);
        ScrollViewer scroller = timeline.GetVisualAncestors().OfType<ScrollViewer>().First();
        double first = LaneY(timeline, workspace.ProcessLaneDisplay[0]);
        double second = LaneY(timeline, workspace.ProcessLaneDisplay[1]);
        ProcessInstanceId third = workspace.ProcessLaneDisplay[2].ProcessId;
        ClickName(window, timeline, LaneY(timeline, workspace.ProcessLaneDisplay[2]), scroller);
        Assert.Equal(third.ToString(), workspace.SelectedRung?.Key);
        Assert.Equal(third, workspace.SelectedProcess?.Id);

        // The fold's name stands for many processes, so it chooses none, and the process chosen stays.
        ClickName(window, timeline, timeline.PointOfFolded(folded.Buckets.First(bucket => bucket.ObservationCount > 0))!.Value.Y, scroller);
        Assert.Equal(third, workspace.SelectedProcess?.Id);

        // The machine row's name, above the first lane, gives the group's whole focus back.
        ClickName(window, timeline, first - (second - first), scroller);
        Assert.Null(workspace.SelectedProcess);
        Assert.Null(workspace.SelectedRung);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§6.7: Ctrl+click and Ctrl+Space on a row that joins no selection choose it as the list does, where they were taken and did nothing")]
    public async Task CtrlOnARowThatJoinsNoSelectionChoosesIt()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = LinkedCalls();
        Publish(session.Store, rows, fields: fields);
        var window = new MainWindow { Width = 1_400, Height = 900 };
        window.Show();
        WorkspaceViewModel workspace = await Open(window, session);
        await CallsOfTheCaller(workspace);
        Settle(window);
        ListBox rung = window.GetControl<ListBox>("RungList");
        Assert.Null(workspace.SelectedRung);

        // A call joins no selection: Ctrl+click chooses it, as a click does, rather than nothing.
        Click(window, rung, 1, RawInputModifiers.Control);
        Assert.Equal(workspace.RungRows[1].Key, workspace.SelectedRung?.Key);
        Assert.False(workspace.HasMultiSelection);

        // Ctrl+Space chooses the row with the keyboard, and lets it go again, as the list's own does.
        Assert.True(rung.ContainerFromIndex(0)!.Focus());
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.Control);
        Settle(window);
        Assert.Equal(workspace.RungRows[0].Key, workspace.SelectedRung?.Key);
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.Control);
        Settle(window);
        Assert.Null(workspace.SelectedRung);
        Assert.False(workspace.HasMultiSelection);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R21: a read that fails says so and why - a process's calls never read as none, and a selection that could not be counted is not still being counted")]
    public async Task AReadThatFailsSaysSo()
    {
        using var session = new TemporarySession();
        // Two instances of the caller, so the group's count and one instance's highlight are counted apart.
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = LinkedCalls();
        Publish(session.Store, [.. rows, Timed(Lifecycle(3, ObservationKind.Create, 402, 3) with { ResourceName = @"C:\Tools\caller.exe" })],
            fields: fields);
        var window = new MainWindow { Width = 1_400, Height = 900 };
        window.Show();
        WorkspaceViewModel workspace = await Open(window, session);

        // The session's segments are damaged while the window shows its overview, as a disk or another program might.
        foreach (string file in Directory.EnumerateFiles(session.Path)
            .Where(path => Path.GetFileName(path).StartsWith("seg-", StringComparison.Ordinal)
                || Path.GetFileName(path).StartsWith("dict-", StringComparison.Ordinal)))
        {
            File.WriteAllBytes(file, [1, 2, 3]);
        }

        // The caller's group, chosen, is counted for its highlight, which cannot be read: the caption says so and why,
        // where it said it was still counting.
        ProcessNode caller = workspace.Snapshot.Processes.Single(node => node.ProcessId == 400);
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == caller.GroupKey);
        await workspace.HighlightReady;
        Settle(window);
        Assert.Contains(" · the selection, caller.exe, could not be counted: This session cannot be read",
            workspace.TimelineCaption, StringComparison.Ordinal);
        Assert.DoesNotContain("counting the selection", workspace.TimelineCaption, StringComparison.Ordinal);

        // At the group, whose own count fails for the same reason, the caller chosen is said not to be counted without the
        // reason again.
        Assert.True(workspace.Descend());
        Settle(window);
        window.GetControl<TimelineView>("TimelineSurface").RequestDetailNow();
        await workspace.TimelineDetailReady;
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == caller.Id.ToString());
        await workspace.HighlightReady;
        Settle(window);
        Assert.EndsWith(" · the selection, caller.exe · PID 400, could not be counted", workspace.TimelineCaption, StringComparison.Ordinal);
        Assert.Equal(2, workspace.TimelineCaption.Split("This session cannot be read").Length);

        // The caller's own rung could not read its calls: it says so and why, and not that it has no channel to list.
        Assert.True(workspace.Descend());
        await workspace.RpcReady;
        await workspace.HttpReady;
        Settle(window);
        Assert.True(workspace.ShowsEmptyReason);
        Assert.StartsWith("This process's RPC calls could not be read: This session cannot be read", workspace.EmptyReason,
            StringComparison.Ordinal);
        Assert.EndsWith(". No admitted paired TCP or UDP channel belongs to this process in this generation.", workspace.EmptyReason,
            StringComparison.Ordinal);
        Assert.DoesNotContain("one step away", workspace.EmptyReason, StringComparison.Ordinal);
        Assert.EndsWith(" · RPC calls could not be read", workspace.LevelSummaryShort, StringComparison.Ordinal);
        Assert.Contains(" · This process's RPC calls could not be read: This session cannot be read", workspace.LevelSummary,
            StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R21: a process whose calls and HTTP exchanges could not be read for one reason says so once, naming both")]
    public async Task ReadsThatFailForOneReasonAreSaidOnce()
    {
        using var session = new TemporarySession();
        Guid scm = Guid.Parse("367abb81-9844-35f1-ad32-98f038001003");
        ObservationRowV1[] exchanges = HttpExchangeLaneTests.FiveExchanges(out SourceFieldRowV1[] fields);
        Publish(session.Store,
        [
            .. exchanges,
            Timed(RpcCall(150, ObservationKind.RequestStart, Direction.Outbound, 4_242, 900, Activity(31), scm)),
            Timed(RpcCall(160, ObservationKind.RequestEnd, Direction.Outbound, 4_242, 901, Activity(31), status: 0)),
            Timed(Transfer(170, ObservationKind.Send, AccountingSide.SendSide, 32, 4_242, 950).Between("127.0.0.1:51000", "10.0.0.7:443")),
        ], fields: fields);
        var window = new MainWindow { Width = 1_400, Height = 900 };
        window.Show();
        WorkspaceViewModel workspace = await Open(window, session);
        foreach (string file in Directory.EnumerateFiles(session.Path)
            .Where(path => Path.GetFileName(path).StartsWith("seg-", StringComparison.Ordinal)
                || Path.GetFileName(path).StartsWith("dict-", StringComparison.Ordinal)))
        {
            File.WriteAllBytes(file, [1, 2, 3]);
        }

        ProcessNode client = workspace.Snapshot.Processes.Single(node => node.ProcessId == 4_242);
        foreach (string key in new[] { client.GroupKey, client.Id.ToString() })
        {
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == key);
            Assert.True(workspace.Descend());
        }

        await workspace.RpcReady;
        await workspace.HttpReady;
        Settle(window);
        Assert.StartsWith("This process's RPC calls, HTTP exchanges and connections could not be read: This session cannot be read",
            workspace.EmptyReason, StringComparison.Ordinal);
        Assert.Equal(2, workspace.EmptyReason.Split("could not be read").Length);
        Assert.EndsWith(" · RPC calls, HTTP exchanges and connections could not be read", workspace.LevelSummaryShort,
            StringComparison.Ordinal);
        window.Close();
    }

    /// <summary>The caller's calls to the service control manager, at their channel's rung.</summary>
    private static async Task CallsOfTheCaller(WorkspaceViewModel workspace)
    {
        ProcessNode caller = workspace.Snapshot.Processes.Single(node => node.ProcessId == 400);
        foreach (string key in new[] { caller.GroupKey, caller.Id.ToString() })
        {
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == key);
            Assert.True(workspace.Descend());
        }

        await workspace.RpcReady;
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label.StartsWith("svcctl", StringComparison.Ordinal));
        Assert.True(workspace.Descend());
        await workspace.RpcReady;
    }

    /// <summary>The middle of a group's process lane, in the timeline's own coordinates.</summary>
    private static double LaneY(TimelineView timeline, ProcessTimelineLane lane) =>
        timeline.PointOf(lane.ProcessId, lane.Buckets.First(bucket => bucket.ObservationCount > 0))!.Value.Y;

    /// <summary>A click on the name of the lane at <paramref name="y"/>, in the gutter left of the plot, scrolled into view.</summary>
    private static void ClickName(Window window, TimelineView timeline, double y, ScrollViewer? scroller = null)
    {
        if (scroller is not null)
        {
            scroller.Offset = new Vector(scroller.Offset.X, Math.Clamp(y - (scroller.Bounds.Height / 2), 0,
                Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height)));
            Settle(window);
        }

        Point at = timeline.TranslatePoint(new Point(8, y), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Settle(window);
    }

    /// <summary>The menu a ranked row carries.</summary>
    private static ContextMenu MenuOf(ListBox rung, int index) =>
        rung.ContainerFromIndex(index)!.GetVisualDescendants().OfType<Control>().Select(control => control.ContextMenu)
            .OfType<ContextMenu>().First();

    /// <summary>Asks for a ranked row's menu by Shift+F10 on the row, or by a right-click on it; whether it opened.</summary>
    private static bool OpenMenu(Window window, ListBox rung, int index, bool byKey)
    {
        Control row = rung.ContainerFromIndex(index)!;
        if (byKey)
        {
            Assert.True(row.Focus());
            window.KeyPressQwerty(PhysicalKey.F10, RawInputModifiers.Shift);
            window.KeyReleaseQwerty(PhysicalKey.F10, RawInputModifiers.Shift);
        }
        else
        {
            Point at = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
            window.MouseDown(at, MouseButton.Right);
            window.MouseUp(at, MouseButton.Right);
        }

        Settle(window);
        return MenuOf(rung, index).IsOpen;
    }

    /// <summary>
    /// A caller's three calls to the service control manager and the service host serving them, ALPC linking the first
    /// and third to the calls that served them (ADR-034); the second's other end is unknown.
    /// </summary>
    private static (ObservationRowV1[] Rows, SourceFieldRowV1[] Fields) LinkedCalls()
    {
        Guid scm = Guid.Parse("367abb81-9844-35f1-ad32-98f038001003");
        ObservationRowV1[] rows =
        [
            Timed(Lifecycle(1, ObservationKind.Create, 400, 1) with { ResourceName = @"C:\Tools\caller.exe" }),
            Timed(Lifecycle(2, ObservationKind.Inventory, 1_960, 2) with { ResourceName = @"C:\Windows\System32\services.exe" }),
            Timed(RpcCall(100, ObservationKind.RequestStart, Direction.Outbound, 400, 10, Activity(1), scm)),
            Timed(RpcCall(120, ObservationKind.RequestEnd, Direction.Outbound, 400, 11, Activity(1), status: 0)),
            Timed(RpcCall(200, ObservationKind.RequestStart, Direction.Outbound, 400, 20, Activity(2), scm)),
            Timed(RpcCall(210, ObservationKind.RequestEnd, Direction.Outbound, 400, 21, Activity(2), status: 0)),
            Timed(RpcCall(300, ObservationKind.RequestStart, Direction.Outbound, 400, 30, Activity(3), scm)),
            Timed(RpcCall(340, ObservationKind.RequestEnd, Direction.Outbound, 400, 31, Activity(3), status: 5)),
            Timed(RpcCall(105, ObservationKind.RequestStart, Direction.Inbound, 1_960, 50, Activity(11), scm)),
            Timed(RpcCall(110, ObservationKind.RequestEnd, Direction.Inbound, 1_960, 51, Activity(11), status: 0)),
            Timed(RpcCall(205, ObservationKind.RequestStart, Direction.Inbound, 1_960, 52, Activity(12), scm)),
            Timed(RpcCall(207, ObservationKind.RequestEnd, Direction.Inbound, 1_960, 53, Activity(12), status: 0)),
            Timed(RpcCall(305, ObservationKind.RequestStart, Direction.Inbound, 1_960, 54, Activity(13), scm)),
            Timed(RpcCall(330, ObservationKind.RequestEnd, Direction.Inbound, 1_960, 55, Activity(13), status: 5)),
        ];
        ObservationRowV1[] messages =
        [
            Timed(Alpc(102, ObservationKind.Send, 400, 401, 60)),
            Timed(Alpc(103, ObservationKind.Receive, 1_960, 1_961, 61)),
            Timed(Alpc(302, ObservationKind.Send, 400, 401, 62)),
            Timed(Alpc(303, ObservationKind.Receive, 1_960, 1_961, 63)),
        ];
        ObservationRowV1[] all = [.. rows, .. messages];
        return (
            all,
            [
                .. all.Where(row => row is { Mechanism: Mechanism.Rpc, Kind: ObservationKind.RequestStart })
                    .Select(start => Field(start, SourceField.RpcProcedureNumber, 7)),
                Field(messages[0], SourceField.AlpcMessageId, 21),
                Field(messages[1], SourceField.AlpcMessageId, 21),
                Field(messages[2], SourceField.AlpcMessageId, 22),
                Field(messages[3], SourceField.AlpcMessageId, 22),
            ]);
    }

    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };

    private static Guid Activity(int number) => new(number, 0x5043, 0x4c4c, 0x80, 0, 0, 0, 0, 0, 0, 1);

    /// <summary>A group of <paramref name="count"/> pool.exe instances, more than the lanes a view draws, each busier by its index.</summary>
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

    /// <summary>A saved session shown in the window, as a capture's last update shows it, laid out.</summary>
    private static async Task<WorkspaceViewModel> Open(MainWindow window, TemporarySession session)
    {
        window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
            Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        Settle(window);
        return workspace;
    }

    /// <summary>The dialog a button opens, pressed as a person presses it with the keyboard on it.</summary>
    private static T Opened<T>(Window window, Button button)
        where T : Window
    {
        Assert.True(button.IsEffectivelyEnabled);
        Assert.True(button.Focus());
        Settle(window);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return Owned<T>(window);
    }

    private static T Owned<T>(Window window)
        where T : Window
    {
        WaitFor(() => window.OwnedWindows.OfType<T>().Any());
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return window.OwnedWindows.OfType<T>().Single();
    }

    /// <summary>
    /// Presses Escape in <paramref name="dialog"/> once it has the keyboard, as a person presses it, and waits for it to
    /// close; one left open fails saying whether it was still active, where its keyboard was and what its key handlers
    /// heard. Active is not enough: a dialog places the keyboard once it has read what it shows, and until then this
    /// platform gives a key to whatever had the keyboard before, the button that opened the dialog, which leaves it open.
    /// </summary>
    private static void Escape(Window window, Window dialog)
    {
        WaitFor(() => dialog.IsActive && dialog.FocusManager?.GetFocusedElement() is Visual focusedInside
            && dialog.IsVisualAncestorOf(focusedInside));
        var heard = new List<string>();
        dialog.AddHandler(InputElement.KeyDownEvent, (_, key) => heard.Add(
                $"{key.Key} {key.Route} at {key.Source?.GetType().Name ?? "nothing"}{(key.Handled ? ", handled" : string.Empty)}"),
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        string focused = dialog.FocusManager?.GetFocusedElement()?.GetType().Name ?? "nothing";
        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        for (int attempt = 0; attempt < 1_500 && window.OwnedWindows.Count != 0; attempt++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Assert.True(window.OwnedWindows.Count == 0, $"Escape left {dialog.GetType().Name} open for 15 s: active {dialog.IsActive}, "
            + $"visible {dialog.IsVisible}, the keyboard on {focused} when pressed and on "
            + $"{dialog.FocusManager?.GetFocusedElement()?.GetType().Name ?? "nothing"} now, heard: {string.Join("; ", heard)}");
    }

    /// <summary>Whether the ranked table's row at <paramref name="index"/> has the keyboard, once laid out.</summary>
    private static bool RecordHasKeyboard(Window window, ListBox rung, int index)
    {
        Settle(window);
        return window.FocusManager?.GetFocusedElement() is Control focused && ReferenceEquals(focused, rung.ContainerFromIndex(index));
    }

    private static void Click(Window window, ListBox list, int index, RawInputModifiers modifiers)
    {
        Control item = list.ContainerFromIndex(index)!;
        Point at = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left, modifiers);
        window.MouseUp(at, MouseButton.Left, modifiers);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static void Settle(Window window)
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        _ = window.CaptureRenderedFrame();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Runs the window's jobs until <paramref name="condition"/> holds, for up to 15 s, which a machine busy with other test
    /// runs needs, as the investigation window's tests wait; one that never holds fails naming what was waited for.
    /// </summary>
    private static void WaitFor(Func<bool> condition, [CallerArgumentExpression(nameof(condition))] string? waited = null)
    {
        for (int attempt = 0; attempt < 1_500 && !condition(); attempt++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Assert.True(condition(), "Waited 15 s for " + waited);
    }
}
