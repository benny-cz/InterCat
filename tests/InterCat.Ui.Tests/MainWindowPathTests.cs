using System.Text;
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
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// The main window's paths that only the methods behind them were tested through, run as a person runs them: a session's
/// channels, a record's original and its content opened from the window, records kept through the pins dialog, a process's
/// next page of HTTP exchanges, and the ranked table and header by the pointer.
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
            Escape(window, Opened<SessionRawRecordWindow>(window, original));
        }
        finally
        {
            window.Close();
        }
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

    private static void Escape(Window window, Window dialog)
    {
        WaitFor(() => dialog.IsActive);
        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        WaitFor(() => window.OwnedWindows.Count == 0);
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

    private static void WaitFor(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Assert.True(condition());
    }
}
