using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// The time inside calls window (`contracts/waits-v1.md` §7, R18): the waits in `icat waits`'s words, who served the chosen
/// wait's calls and how long they took, the servers callers waited on, one process's calls and the calls it served, and a
/// wait's caller and server made the workspace's selection (§6.7).
/// </summary>
public sealed class WaitsWindowTests
{
    private const string SelectBoth = "Select caller and server";
    private const string GoToCaller = "Go to its caller";
    private const string Listed = "Time inside calls, the most first. Enter selects the chosen wait's caller and server.";
    private const string Reading = "The session's calls are being read.";

    [AvaloniaFact(DisplayName = "R18: the waits window lists the time inside calls in icat waits's words, says who served the chosen wait and how long, and returns its caller and server on Enter")]
    public async Task TheWindowListsTheTimeInsideCalls()
    {
        using var session = Session(LinkRows());
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        WorkspaceSnapshot snapshot = OverviewWorkspace.From(overview);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        using var window = new WaitsWindow(session.Path, overview.SessionId, overview.Generation, EvidencePolicy.IncludeCorrelated, null);

        // Until the session is read, the window's actions wait, each saying so on itself.
        Assert.Equal(Reading, Offers.WhyNot(Labelled(window, SelectBoth)));
        Assert.Equal(Reading, Offers.WhyNot(Only(window)));
        Task<WaitChoice?> chosen = window.ShowDialog<WaitChoice?>(owner);
        ListBox list = Named<ListBox>(window, Listed);
        await Until(() => list.ItemCount > 0);

        // The waits, the most time first, each by its caller and interface, then who served its calls and how long they took,
        // as icat waits lists them; the lines about the list say how many calls it counts and the coverage.
        Assert.Equal("InterCat · Time inside calls", window.Title);
        Assert.Equal(
            [
                "1. app.exe · PID 400 → svcctl (Service Control Manager) · served by services.exe · PID 1960 · 2 calls, 1 failed · "
                    + "6.0 µs in calls · the server ran 3.0 µs of 6.0 µs (50%)",
                "2. other.exe · PID 410 → svcctl (Service Control Manager) · not linked: no ALPC send on its thread during the call "
                    + "· 1 call · 1.8 µs in calls",
                "3. app.exe · PID 400 → svcctl (Service Control Manager) · not linked: no ALPC send on its thread during the call "
                    + "· 1 call · 1.0 µs in calls",
            ],
            list.Items.Select(item => item?.ToString()));
        Assert.Equal(string.Create(CultureInfo.CurrentCulture, $"Generation {overview.Generation:N0} · 4 completed calls of 2 processes, ")
            + "the most time first", Named<TextBlock>(window, "Waits status").Text);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(Named<TextBlock>(window, "Waits status")));
        Assert.StartsWith("Coverage", Named<TextBlock>(window, "What the capture covered").Text, StringComparison.Ordinal);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == WaitText.Note);
        Assert.False(Named<TextBlock>(window, "What no time is said for").IsVisible);
        Assert.Equal("No process was selected in InterCat's window, so every caller's calls are listed.", Offers.WhyNot(Only(window)));

        // The most time has the keyboard, and a screen reader hears each row whole.
        Dispatch();
        Assert.Same(list.ContainerFromIndex(0), window.FocusManager?.GetFocusedElement());
        Assert.Equal("1. app.exe · PID 400 → svcctl (Service Control Manager): served by services.exe · PID 1960 · 2 calls, 1 failed "
            + "· 6.0 µs in calls · the server ran 3.0 µs of 6.0 µs (50%).", AutomationProperties.GetName(list.ContainerFromIndex(0)!));

        // The chosen wait says who served its calls, how many there were and how long they took, beside the servers callers
        // waited on.
        Assert.Equal("Wait 1: app.exe · PID 400's calls to svcctl (Service Control Manager)", Starting(window, "Wait ").Text);
        Assert.Equal("Served by: services.exe · PID 1960", Named<TextBlock>(window, "Who served its calls").Text);
        Assert.Equal("Calls: 2 calls, 1 failed", Starting(window, "Calls: ").Text);
        Assert.Equal("In calls: 6.0 µs · median 2.0 µs · 95th percentile 4.0 µs · longest 4.0 µs", Starting(window, "In calls: ").Text);
        Assert.Equal("The server's own time: 3.0 µs of 6.0 µs (50%)", Starting(window, "The server's own time: ").Text);
        Assert.Equal("The servers callers waited on", Starting(window, "The servers callers").Text);
        Assert.Equal(["services.exe · PID 1960 · 1 caller · 2 calls · 6.0 µs in calls · it ran 3.0 µs of 6.0 µs (50%)"],
            Named<ListBox>(window, "The servers callers waited on").Items.Cast<string>());
        Assert.Equal("Close this list and select the wait's caller and server together in InterCat's window, as Ctrl+click does; "
            + "Enter there lists their records.", Offers.Offered(Labelled(window, SelectBoth)));

        // A wait no server is named for says why, and goes to its caller alone.
        list.SelectedIndex = 1;
        Assert.Equal("Served by: not linked: no ALPC send on its thread during the call", Named<TextBlock>(window, "Who served its calls").Text);
        Assert.Equal("The server's own time: not measured, as none of these calls is linked to a server call that completed.",
            Starting(window, "The server's own time: ").Text);
        Assert.Equal("Close this list and select the wait's caller in InterCat's window; no server is named for its calls.",
            Offers.Offered(Labelled(window, GoToCaller)));

        // With none chosen, the window says to choose one; Enter on the first closes it with its caller and server.
        list.SelectedIndex = -1;
        Assert.Equal("Choose a wait first.", Offers.WhyNot(Labelled(window, SelectBoth)));
        Assert.False(Named<TextBlock>(window, "Who served its calls").IsVisible);
        list.SelectedIndex = 0;
        _ = window.CaptureRenderedFrame();
        Dispatch();
        Assert.True(list.ContainerFromIndex(0)!.Focus());
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatch();
        await Until(() => chosen.IsCompleted);
        WaitChoice choice = Assert.IsType<WaitChoice>(await chosen);
        Assert.Equal(2L, choice.Wait.Calls);
        Assert.Equal([LinkApp, LinkServices], choice.Processes.Select(id => snapshot.Processes.Single(process => process.Id == id).ProcessId));

        // Escape closes the window with nothing chosen, as it cancels InterCat's prompts.
        using var again = new WaitsWindow(session.Path, overview.SessionId, overview.Generation, EvidencePolicy.IncludeCorrelated, null);
        Task<WaitChoice?> cancelled = again.ShowDialog<WaitChoice?>(owner);
        ListBox listed = Named<ListBox>(again, Listed);
        await Until(() => listed.ItemCount > 0);
        Dispatch();
        again.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatch();
        await Until(() => cancelled.IsCompleted);
        Assert.Null(await cancelled);
        owner.Close();
    }

    [AvaloniaFact(DisplayName = "R21: the waits window lists one process's calls and the calls it served, says why it lists none, and counts what no time is said for")]
    public async Task TheWindowSaysWhyItListsNone()
    {
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        using var session = Session(LinkRows());
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        ProcessInstanceId Of(int pid) => overview.Nodes.Single(node => node.ProcessId == pid && node.PidHolder == 1).Id;

        // Opened with the server selected, it lists the calls it made - none - and the calls it served, each caller named,
        // until asked for every caller's.
        using (var window = new WaitsWindow(session.Path, overview.SessionId, overview.Generation, EvidencePolicy.IncludeCorrelated,
            Of(LinkServices)))
        {
            _ = window.ShowDialog<WaitChoice?>(owner);
            await Until(() => Named<TextBlock>(window, "Waits status").Text?.StartsWith("Generation", StringComparison.Ordinal) == true);
            Assert.Equal(string.Create(CultureInfo.CurrentCulture, $"Generation {overview.Generation:N0} · 0 completed calls it made and 2 ")
                + "calls it served, the most time first", Named<TextBlock>(window, "Waits status").Text);
            Assert.Equal((true, "Only services.exe · PID 1960's calls and the calls it served"), (Only(window).IsChecked, Only(window).Content));
            Assert.Equal(0, Named<ListBox>(window, Listed).ItemCount);
            Assert.Equal("It made no completed call; unticking the box below lists every caller's.", Starting(window, "It made").Text);
            Assert.Equal("Choose a wait first.", Offers.WhyNot(Labelled(window, SelectBoth)));
            Assert.Equal(["app.exe · PID 400 → svcctl (Service Control Manager) · 2 calls, 1 failed · 6.0 µs in calls · it ran 3.0 µs of "
                + "6.0 µs (50%)"], Named<ListBox>(window, "Calls it served").Items.Cast<string>());
            Assert.Equal("Calls it served", Starting(window, "Calls it served").Text);
            Assert.Equal("List only the selected process's calls and the calls it served, as icat waits --process does",
                Offers.Offered(Only(window)));

            // Unticked, every caller's calls are listed, beside the servers callers waited on.
            Only(window).IsChecked = false;
            Assert.Equal((3, 0), (Named<ListBox>(window, Listed).ItemCount, Named<ListBox>(window, Listed).SelectedIndex));
            Assert.StartsWith(string.Create(CultureInfo.CurrentCulture, $"Generation {overview.Generation:N0} · 4 completed calls of 2"),
                Named<TextBlock>(window, "Waits status").Text, StringComparison.Ordinal);
            Assert.Single(Named<ListBox>(window, "The servers callers waited on").Items);
            window.Close();
        }

        // Opened with the caller selected, its own calls are listed; what it served, nothing, is said so.
        using (var window = new WaitsWindow(session.Path, overview.SessionId, overview.Generation, EvidencePolicy.IncludeCorrelated,
            Of(LinkApp)))
        {
            _ = window.ShowDialog<WaitChoice?>(owner);
            ListBox list = Named<ListBox>(window, Listed);
            await Until(() => list.ItemCount > 0);
            Assert.Equal(2, list.ItemCount);
            Assert.Equal(string.Create(CultureInfo.CurrentCulture, $"Generation {overview.Generation:N0} · 3 completed calls it made and 0 ")
                + "calls it served, the most time first", Named<TextBlock>(window, "Waits status").Text);
            Assert.Equal("Calls it served: no linked client call reached it.", Starting(window, "Calls it served").Text);
            Assert.False(Named<ListBox>(window, "Calls it served").IsVisible);

            // The chosen wait stays chosen as the list widens.
            list.SelectedIndex = 1;
            CallWait wait = ((WaitRow)list.SelectedItem!).Wait;
            Only(window).IsChecked = false;
            Assert.Same(wait, ((WaitRow)list.SelectedItem!).Wait);
            window.Close();
        }

        // What no time is said for is counted beneath the list.
        using (var waits = Session(WaitRows()))
        {
            SessionOverviewBundle counted = SessionOverviewProjector.Project(waits.Store);
            using var window = new WaitsWindow(waits.Path, counted.SessionId, counted.Generation, EvidencePolicy.IncludeCorrelated, null);
            _ = window.ShowDialog<WaitChoice?>(owner);
            await Until(() => Named<ListBox>(window, Listed).ItemCount > 0);
            TextBlock uncounted = Named<TextBlock>(window, "What no time is said for");
            Assert.True(uncounted.IsVisible);
            Assert.StartsWith("No time is said for 3 calls whose span is not validated: ", uncounted.Text, StringComparison.Ordinal);
            window.Close();
        }

        // A capture with no call says so; its actions say there is none.
        using (var empty = Session(([Lifecycle(1, ObservationKind.Inventory, 100, 1) with { SessionRelativeTicks = 100 }], [])))
        {
            SessionOverviewBundle none = SessionOverviewProjector.Project(empty.Store);
            using var window = new WaitsWindow(empty.Path, none.SessionId, none.Generation, EvidencePolicy.IncludeCorrelated, null);
            _ = window.ShowDialog<WaitChoice?>(owner);
            await Until(() => Named<TextBlock>(window, "Waits status").Text?.StartsWith("Generation", StringComparison.Ordinal) == true);
            Assert.Equal("No completed call is in this session; the coverage above says whether one could have been seen.",
                Starting(window, "No completed call").Text);
            Assert.Equal("This session holds no completed call.", Offers.WhyNot(Labelled(window, SelectBoth)));
            Assert.Equal("This session holds no completed call.", Offers.WhyNot(Only(window)));
            Assert.Equal("The servers callers waited on: none is named.", Starting(window, "The servers callers").Text);
            Dispatch();
            Assert.Equal("Close", Assert.IsType<Button>(window.FocusManager?.GetFocusedElement()).Content);
            window.Close();
        }

        // Counted under the window's evidence policy, the status says which.
        using (var window = new WaitsWindow(session.Path, overview.SessionId, overview.Generation, EvidencePolicy.DirectOnly, null))
        {
            _ = window.ShowDialog<WaitChoice?>(owner);
            await Until(() => Named<TextBlock>(window, "Waits status").Text?.StartsWith("Generation", StringComparison.Ordinal) == true);
            Assert.EndsWith(" · counted under the direct-only evidence policy, as InterCat's window counts",
                Named<TextBlock>(window, "Waits status").Text, StringComparison.Ordinal);
            window.Close();
        }

        // A folder holding an older generation than InterCat's window shows is refused, and one holding another session.
        using (var window = new WaitsWindow(session.Path, overview.SessionId, overview.Generation + 1, EvidencePolicy.IncludeCorrelated, null))
        {
            _ = window.ShowDialog<WaitChoice?>(owner);
            await Until(() => Named<TextBlock>(window, "Waits status").Text?.StartsWith("This folder", StringComparison.Ordinal) == true);
            Assert.Equal("This folder no longer holds the generation InterCat's window shows. Close this list and open it again from "
                + "that window.", Named<TextBlock>(window, "Waits status").Text);
            Assert.Equal("No waits are shown; the status line says why.", Offers.WhyNot(Labelled(window, SelectBoth)));
            Assert.Equal("No waits are shown; the status line says why.", Offers.WhyNot(Only(window)));
            window.Close();
        }

        using (var window = new WaitsWindow(session.Path, Guid.NewGuid(), overview.Generation, EvidencePolicy.IncludeCorrelated, null))
        {
            _ = window.ShowDialog<WaitChoice?>(owner);
            await Until(() => Named<TextBlock>(window, "Waits status").Text?.StartsWith("Could not", StringComparison.Ordinal) == true);
            Assert.StartsWith("Could not read the session's calls: ", Named<TextBlock>(window, "Waits status").Text, StringComparison.Ordinal);
            Assert.Equal("No waits are shown; the status line says why.", Offers.WhyNot(Labelled(window, SelectBoth)));
            window.Close();
        }

        owner.Close();
    }

    [AvaloniaFact(DisplayName = "R18: over InterCat's window's time scope, the waits window times the calls that began in it alone, each to its stop, and counts those that began outside it")]
    public async Task TheWindowKeepsToTheTimeScope()
    {
        using var session = Session(WaitRows());
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        ProcessInstanceId server = overview.Nodes.Single(node => node.ProcessId == 1_960 && node.PidHolder == 1).Id;
        var interval = new TimeRange(150, 500);
        SessionClock clock = SessionClock.Wall(new SessionWallClock(0, new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero), null, 200),
            TimeZoneInfo.Utc, interval);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        using (var window = new WaitsWindow(session.Path, overview.SessionId, overview.Generation, EvidencePolicy.IncludeCorrelated, null,
            interval, clock))
        {
            _ = window.ShowDialog<WaitChoice?>(owner);
            ListBox list = Named<ListBox>(window, Listed);
            await Until(() => list.ItemCount > 0);

            // The scope is said on InterCat's window's clock, here the wall clock; PID 400's second and third calls and PID
            // 600's two began in it, and the three that began outside it are counted beneath, with a call timed to its stop.
            Assert.Equal("The time each process spent inside the RPC calls it began over the window's time scope, the most first · "
                + ComparisonText.Interval(interval, clock, CultureInfo.CurrentCulture), Starting(window, "The time each process").Text);
            Assert.Contains("12:00:00", Starting(window, "The time each process").Text, StringComparison.Ordinal);
            Assert.Equal(3, list.ItemCount);
            Assert.Equal("1. PID 400 → svcctl (Service Control Manager) · served by PID 1960 · 1 call, 1 failed · 4.0 µs in calls · the "
                + "server ran 2.5 µs of 4.0 µs (62%)", list.Items[0]?.ToString());
            Assert.Equal(string.Create(CultureInfo.CurrentCulture, $"Generation {overview.Generation:N0} · 4 completed calls of 2 ")
                + "processes, the most time first", Named<TextBlock>(window, "Waits status").Text);
            Assert.EndsWith(", or 3 calls that began outside the interval.", Named<TextBlock>(window, "What no time is said for").Text,
                StringComparison.Ordinal);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == WaitText.Note + " " + WaitText.IntervalNote);
            window.Close();
        }

        // A process over the interval: the calls it began there, here none, and the calls it served that began there.
        using (var window = new WaitsWindow(session.Path, overview.SessionId, overview.Generation, EvidencePolicy.IncludeCorrelated, server,
            interval, clock))
        {
            _ = window.ShowDialog<WaitChoice?>(owner);
            await Until(() => Named<TextBlock>(window, "Waits status").Text?.StartsWith("Generation", StringComparison.Ordinal) == true);
            Assert.Equal("It began no completed call in the interval; unticking the box below lists every caller's.",
                Starting(window, "It began").Text);
            Assert.Equal(["PID 400 → svcctl (Service Control Manager) · 1 call, 1 failed · 4.0 µs in calls · it ran 2.5 µs of 4.0 µs (62%)"],
                Named<ListBox>(window, "Calls it served").Items.Cast<string>());
            window.Close();
        }

        // An interval no call began in holds none, which the window says of the interval.
        using (var window = new WaitsWindow(session.Path, overview.SessionId, overview.Generation, EvidencePolicy.IncludeCorrelated, null,
            new TimeRange(2_000, 3_000), clock))
        {
            _ = window.ShowDialog<WaitChoice?>(owner);
            await Until(() => Named<TextBlock>(window, "Waits status").Text?.StartsWith("Generation", StringComparison.Ordinal) == true);
            Assert.Equal("No completed call began in the interval; the coverage above says whether one could have been seen.",
                Starting(window, "No completed call").Text);
            Assert.Equal("No completed call began in the interval.", Offers.WhyNot(Labelled(window, SelectBoth)));
            Assert.Equal("No time is said for 11 calls that began outside the interval.", Named<TextBlock>(window, "What no time is said for").Text);
            window.Close();
        }

        owner.Close();
    }

    [AvaloniaFact(DisplayName = "§6.7: Time inside calls… selects the chosen wait's caller and server together, or goes to its caller, starting with the selected process's calls")]
    public async Task TheWaitsProcessesBecomeTheSelection()
    {
        using var session = Session(LinkRows());
        var window = new MainWindow { Width = 1_456, Height = 939 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
                Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            Dispatch();
            Button button = window.GetControl<Button>("WaitsButton");
            Assert.True(button.IsEnabled);
            Assert.Equal("List the time processes spent inside RPC calls", AutomationProperties.GetHelpText(button));
            Assert.Equal("List how long each process spent inside the RPC calls it made - by interface and by the process that "
                + "served them, the most first, with the server's own time - and select a wait's caller and server.", ToolTip.GetTip(button));

            // Over a time scope, the list keeps to the calls that began in it, said on the window's clock; let go, it is the
            // session's.
            var scope = new TimeRange(150, 500);
            workspace.SelectInterval(scope);
            await workspace.IntervalReady;
            Dispatch();
            WaitsWindow waits = Opened(window, button);
            ListBox list = Named<ListBox>(waits, Listed);
            await Until(() => list.ItemCount > 0);
            Assert.Equal("The time each process spent inside the RPC calls it began over the window's time scope, the most first · "
                + ComparisonText.Interval(scope, workspace.TimeBase, CultureInfo.CurrentCulture), Starting(waits, "The time each process").Text);
            Assert.Equal([LinkApp, LinkApp], list.Items.Cast<WaitRow>().Select(row => workspace.WholeSnapshot.Processes
                .Single(process => process.Id == row.Wait.Caller).ProcessId));
            waits.Close();
            await Until(() => window.OwnedWindows.Count == 0);
            workspace.ClearSelection();
            Assert.Null(workspace.ScopeInterval);

            // The first wait's caller and server become the selection together, and the graph has the keyboard.
            waits = Opened(window, button);
            list = Named<ListBox>(waits, Listed);
            await Until(() => list.ItemCount > 0);
            Labelled(waits, SelectBoth).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.OwnedWindows.Count == 0 && workspace.HasMultiSelection);
            Assert.Equal([LinkApp, LinkServices], workspace.ChosenProcesses.Select(process => process.ProcessId).Order());
            Assert.True(window.GetControl<GraphView>("GraphSurface").IsFocused);

            // A wait no server is named for goes to its caller, in place of the set.
            waits = Opened(window, button);
            list = Named<ListBox>(waits, Listed);
            await Until(() => list.ItemCount > 0);
            list.SelectedIndex = 1;
            Labelled(waits, GoToCaller).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.OwnedWindows.Count == 0 && workspace.SelectedProcess is not null);
            Assert.Equal((LinkOther, false), (workspace.SelectedProcess!.ProcessId, workspace.HasMultiSelection));

            // With a process selected, the button says so and the list starts with its calls.
            Assert.Equal("List how long each process spent inside the RPC calls it made - by interface and by the process that "
                + "served them, the most first, with the server's own time - starting with other.exe · PID 410's, and select a "
                + "wait's caller and server.", ToolTip.GetTip(button));
            waits = Opened(window, button);
            list = Named<ListBox>(waits, Listed);
            await Until(() => list.ItemCount > 0);
            Assert.Equal((1, true), (list.ItemCount, Only(waits).IsChecked));
            waits.Close();
            await Until(() => window.OwnedWindows.Count == 0);
            Assert.Equal(LinkOther, workspace.SelectedProcess!.ProcessId);

            // Among the source records the time inside calls waits, saying how to leave them.
            Assert.True(workspace.ShowEvidence());
            await workspace.EvidenceReady;
            Dispatch();
            Assert.Equal("Esc leaves the source records; the time inside calls can be listed from any other level.", Offers.WhyNot(button));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: the waits window cuts off no text at its smallest and names every element a screen reader lands on")]
    public async Task TheWindowIsLegibleAndHeard()
    {
        using var session = Session(LinkRows());
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        ProcessInstanceId served = overview.Nodes.Single(node => node.ProcessId == LinkServices && node.PidHolder == 1).Id;
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        var problems = new List<string>();
        foreach (ProcessInstanceId? process in new ProcessInstanceId?[] { null, served })
        {
            using var window = new WaitsWindow(session.Path, overview.SessionId, overview.Generation, EvidencePolicy.IncludeCorrelated,
                process);
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            _ = window.ShowDialog<WaitChoice?>(owner);
            await Until(() => Named<TextBlock>(window, "Waits status").Text?.StartsWith("Generation", StringComparison.Ordinal) == true);
            ListBox list = Named<ListBox>(window, Listed);
            foreach (int index in Enumerable.Range(0, Math.Max(1, list.ItemCount)))
            {
                list.SelectedIndex = list.ItemCount == 0 ? -1 : index;
                string where = $"waits{(process is null ? string.Empty : " of the server")}, wait {index + 1} chosen";
                problems.AddRange(LegibleTextTests.CutOff(window, where));
                problems.AddRange(AccessibilityAuditTests.Unheard(window, out _).Select(problem => $"{where}: {problem}"));
            }

            window.Close();
        }

        owner.Close();
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static TemporarySession Session((ObservationRowV1[] Rows, SourceFieldRowV1[] Fields) session)
    {
        var temporary = new TemporarySession();
        Publish(temporary.Store, session.Rows, fields: session.Fields, coverage: TransportLedger(tcp: true, udp: true));
        temporary.Store.ReleaseSegmentReaders();
        return temporary;
    }

    /// <summary>The dialog a button opens, pressed as a person presses it with the keyboard on it.</summary>
    private static WaitsWindow Opened(Window window, Button button)
    {
        Assert.True(button.IsEffectivelyEnabled);
        Assert.True(button.Focus());
        Dispatch();
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (int wait = 0; wait < 500 && !window.OwnedWindows.OfType<WaitsWindow>().Any(); wait++)
        {
            Dispatch();
            Thread.Sleep(10);
        }

        return window.OwnedWindows.OfType<WaitsWindow>().Single();
    }

    private static T Named<T>(Window window, string name)
        where T : Control =>
        window.GetLogicalDescendants().OfType<T>().Single(control => AutomationProperties.GetName(control) == name);

    private static TextBlock Starting(Window window, string start) =>
        window.GetLogicalDescendants().OfType<TextBlock>().Single(block => block.IsVisible
            && block.Text?.StartsWith(start, StringComparison.Ordinal) == true);

    private static CheckBox Only(Window window) => window.GetLogicalDescendants().OfType<CheckBox>().Single();

    private static Button Labelled(Window window, string label) =>
        window.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, label));

    private static async Task Until(Func<bool> condition)
    {
        for (int wait = 0; wait < 500 && !condition(); wait++)
        {
            Dispatch();
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
