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
/// The recurring patterns window (`contracts/patterns-v1.md` §7, R18): what recurs in `icat patterns`'s words, every end of
/// the chosen pattern, what no pattern holds, and a pattern's processes made the workspace's selection (§6.7).
/// </summary>
public sealed class PatternsWindowTests
{
    private const string SelectThem = "Select its processes";
    private const string GoToIt = "Go to its process";
    private const string Reading = "The session's patterns are being read.";

    [AvaloniaFact(DisplayName = "R18: the patterns window lists what recurs in icat patterns's words, says every end of the chosen pattern, and returns its processes on Enter")]
    public async Task TheWindowListsWhatRecurs()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = PatternRows();
        Publish(session.Store, rows, fields: fields, coverage: TransportLedger(tcp: true, udp: true));
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        TimeRange span = CommunicationPatterns.Read(session.Store).Span!.Value;
        WorkspaceSnapshot snapshot = OverviewWorkspace.From(overview);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        using var window = new PatternsWindow(session.Path, overview.SessionId, overview.Generation, EvidencePolicy.IncludeCorrelated);

        // Until the session is read, the window's actions wait, each saying so on itself.
        Assert.Equal(Reading, Offers.WhyNot(Labelled(window, SelectThem)));
        Assert.Equal(Reading, Offers.WhyNot(Once(window)));
        Task<PatternChoice?> chosen = window.ShowDialog<PatternChoice?>(owner);
        ListBox list = Named<ListBox>(window, "Recurring communication patterns. Enter selects the chosen pattern's processes.");
        await Until(() => list.ItemCount > 0);

        // The recurring patterns, the heaviest first, each by its signature, then how often it occurred and who took part, as
        // icat patterns lists them; the lines about the list say how many there are, the coverage and what no pattern holds.
        Assert.Equal("InterCat · Recurring patterns", window.Title);
        Assert.Equal(
            [
                "1. RPC calls to svcctl (Service Control Manager), procedure 7 · 3 calls made · 3 served · clients: 1 process · "
                    + "servers: 1 process · in 1 of 10 tenths",
                "2. TCP at port 8080 here · 3 connections · clients: 2 processes of 1 program · servers: 1 process of 1 program · "
                    + "in 3 of 10 tenths",
                "3. TCP to port 443 at other hosts · 2 connections · clients: 2 processes of 1 program · servers: 2 hosts · in 2 of "
                    + "10 tenths",
                "4. UDP to port 53 at other hosts · 2 flows · clients: 1 process of 1 program · servers: 1 host · in 2 of 10 tenths",
                "5. UDP at port 5353 here · 2 flows · clients: 1 process of 1 program · servers: 1 process of 1 program · in 1 of 10 "
                    + "tenths",
            ],
            list.Items.Select(item => item?.ToString()));
        Assert.Equal(string.Create(CultureInfo.CurrentCulture, $"Generation {overview.Generation:N0} · 5 recurring patterns, and 3 ")
            + "that each occurred once", Named<TextBlock>(window, "Patterns status").Text);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(Named<TextBlock>(window, "Patterns status")));
        Assert.Equal("Coverage: covered for TCP and UDP · no other mechanism collected", Named<TextBlock>(window, "What the capture covered").Text);
        Assert.Equal("In no pattern: 2 TCP connections the capture saw open at neither end, so no end of them has a role and 2 UDP "
            + "flows between two dynamic ports, which have no service port.", Named<TextBlock>(window, "What no pattern holds").Text);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == PatternText.Note);

        // The heaviest has the keyboard, and a screen reader hears each row whole.
        Dispatch();
        Assert.Same(list.ContainerFromIndex(0), window.FocusManager?.GetFocusedElement());
        Assert.Equal("1. RPC calls to svcctl (Service Control Manager), procedure 7: 3 calls made · 3 served · clients: 1 process · "
            + "servers: 1 process · in 1 of 10 tenths.", AutomationProperties.GetName(list.ContainerFromIndex(0)!));

        // The chosen pattern says what its signature is made of, when it occurred, and every end in its role, the clients
        // first, each in its role's unit.
        ListBox ends = Named<ListBox>(window, "Who took part in the chosen pattern");
        Assert.Equal("Pattern 1: RPC calls to svcctl (Service Control Manager), procedure 7", Starting(window, "Pattern ").Text);
        Assert.Equal(["Client · PID 400 · executable not witnessed · 3 calls made",
            "Server · PID 1960 · executable not witnessed · 3 calls served"], ends.Items.Cast<string>());
        list.SelectedIndex = 1;
        Assert.Equal("Pattern 2: TCP at port 8080 here", Starting(window, "Pattern ").Text);
        Assert.StartsWith("Made of: TCP connections a process of this machine accepted at this port, whoever connected:",
            Starting(window, "Made of: ").Text, StringComparison.Ordinal);
        Assert.Equal("Occurred: 3 connections, the first beginning at " + WorkspaceTime.FormatInstant(1_000, span.SpanTicks,
            CultureInfo.CurrentCulture) + " and the last at " + WorkspaceTime.FormatInstant(8_000, span.SpanTicks, CultureInfo.CurrentCulture)
            + ", in 3 of the session's 10 tenths", Starting(window, "Occurred: ").Text);
        Assert.Equal("Signature: tcp:here:8080", Starting(window, "Signature: ").Text);
        Assert.Equal("Clients: 2 processes of 1 program · servers: 1 process of 1 program", Named<TextBlock>(window, "Its clients and servers").Text);
        Assert.Equal(["Client · browser.exe · PID 3000 · 2 connections", "Client · browser.exe · PID 3001 · 1 connection",
            "Server · server.exe · PID 4000 · 3 connections"], ends.Items.Cast<string>());
        Assert.False(HostNote(window).IsVisible);
        Assert.Equal("Close this list and select the pattern's processes together in InterCat's window, as Ctrl+click does; "
            + "Enter there lists their records.", Offers.Offered(Labelled(window, SelectThem)));

        // Another host is named by its address, which says who is at it is not observed.
        list.SelectedIndex = 2;
        Assert.Equal(["Client · browser.exe · PID 3000 · 1 connection", "Client · browser.exe · PID 3001 · 1 connection",
            "Server · 142.250.1.1 · 1 connection", "Server · 142.250.1.2 · 1 connection"], ends.Items.Cast<string>());
        Assert.True(HostNote(window).IsVisible);
        list.SelectedIndex = -1;
        Assert.False(HostNote(window).IsVisible);

        // One process alone is gone to, as a click on its node selects it.
        list.SelectedIndex = 3;
        Assert.Equal("Close this list and select the pattern's process in InterCat's window.", Offers.Offered(Labelled(window, GoToIt)));

        // Those that occurred once are listed after the recurring ones where asked, the chosen pattern staying chosen; an end
        // on this machine no record holds is said so.
        Assert.Equal("List the patterns that occurred only once as well, after those that recur", Offers.Offered(Once(window)));
        Once(window).IsChecked = true;
        Assert.Equal(8, list.ItemCount);
        Assert.Equal(3, list.SelectedIndex);
        Assert.Equal(
            [
                "6. TCP at port 3389 here · 1 connection · clients: 1 host · servers: 1 process of 1 program · in 1 of 10 tenths",
                "7. TCP at port 9000 here · 1 connection · clients: 1 process of 1 program · servers: ends no record holds · in 1 of "
                    + "10 tenths",
                "8. UDP at port 161 here · 1 flow · clients: 1 host · servers: 1 process of 1 program · in 1 of 10 tenths",
            ],
            list.Items.Skip(5).Select(item => item?.ToString()));
        list.SelectedIndex = 6;
        Assert.Equal(["Client · agent.exe · PID 6000 · 1 connection", "Server · ends on this machine no record holds · 1 connection"],
            ends.Items.Cast<string>());
        Assert.False(HostNote(window).IsVisible);

        // Listed without them again, a pattern no longer listed leaves the heaviest chosen.
        Once(window).IsChecked = false;
        Assert.Equal((5, 0), (list.ItemCount, list.SelectedIndex));

        // With none chosen, the window says to choose one; Enter on the local pattern closes it with its processes, the
        // clients first, each once.
        list.SelectedIndex = -1;
        Assert.Equal("Choose a pattern first.", Offers.WhyNot(Labelled(window, SelectThem)));
        Assert.Equal((false, 0), (ends.IsVisible, ends.ItemCount));
        list.SelectedIndex = 1;
        _ = window.CaptureRenderedFrame();
        Dispatch();
        Assert.True(list.ContainerFromIndex(1)!.Focus());
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatch();
        await Until(() => chosen.IsCompleted);
        PatternChoice choice = Assert.IsType<PatternChoice>(await chosen);
        Assert.Equal("tcp:here:8080", choice.Pattern.Key);
        Assert.Equal([3_000, 3_001, 4_000], choice.Processes.Select(id => snapshot.Processes.Single(process => process.Id == id).ProcessId));

        // Escape closes the window with nothing chosen, as it cancels InterCat's prompts.
        using var again = new PatternsWindow(session.Path, overview.SessionId, overview.Generation, EvidencePolicy.IncludeCorrelated);
        Task<PatternChoice?> cancelled = again.ShowDialog<PatternChoice?>(owner);
        ListBox listed = Named<ListBox>(again, "Recurring communication patterns. Enter selects the chosen pattern's processes.");
        await Until(() => listed.ItemCount > 0);
        Dispatch();
        again.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatch();
        await Until(() => cancelled.IsCompleted);
        Assert.Null(await cancelled);
        owner.Close();
    }

    [AvaloniaFact(DisplayName = "R21: the patterns window says why it lists none - a session with no pattern, none that recurs, or none its evidence policy admits")]
    public async Task TheWindowSaysWhyItListsNone()
    {
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();

        // A capture that recorded processes alone holds no pattern, which the window says; its actions say there is none.
        using (var empty = new TemporarySession())
        {
            Publish(empty.Store, [Lifecycle(1, ObservationKind.Inventory, 100, 1) with { ResourceName = @"C:\Apps\client.exe", SessionRelativeTicks = 100 }]);
            SessionOverviewBundle overview = SessionOverviewProjector.Project(empty.Store);
            using var window = new PatternsWindow(empty.Path, overview.SessionId, overview.Generation, EvidencePolicy.IncludeCorrelated);
            _ = window.ShowDialog<PatternChoice?>(owner);
            await Until(() => Named<TextBlock>(window, "Patterns status").Text?.StartsWith("Generation", StringComparison.Ordinal) == true);
            Assert.Equal("This session holds no pattern: no TCP connection the capture saw open, UDP flow with a service port or "
                + "RPC call naming its interface is in it; the coverage above says whether one could have been seen.",
                Starting(window, "This session").Text);
            Assert.Equal("This session holds no pattern.", Offers.WhyNot(Labelled(window, SelectThem)));
            Assert.Equal("This session holds no pattern.", Offers.WhyNot(Once(window)));
            Assert.False(Named<TextBlock>(window, "What no pattern holds").IsVisible);
            Dispatch();
            Assert.Equal("Close", Assert.IsType<Button>(window.FocusManager?.GetFocusedElement()).Content);
            window.Close();
        }

        // Each pattern occurring once, the list holds none until asked for those; a program two folders tell apart is named
        // beside its process.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Inventory, 100, 1) with { ResourceName = @"C:\Apps\cmd\git.exe", SessionRelativeTicks = 100 },
            Lifecycle(2, ObservationKind.Inventory, 200, 2) with { ResourceName = @"C:\Apps\mingw\git.exe", SessionRelativeTicks = 200 },
            Lifecycle(3, ObservationKind.Inventory, 300, 3) with { ResourceName = @"C:\Apps\server.exe", SessionRelativeTicks = 300 },
            Transfer(10, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 10)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 1_000 },
            Transfer(11, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 300, 11)
                .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 1_100 },
            Transfer(12, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 12)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 1_200 },
        ], coverage: TransportLedger(tcp: true, udp: false));
        SessionOverviewBundle once = SessionOverviewProjector.Project(session.Store);
        using (var window = new PatternsWindow(session.Path, once.SessionId, once.Generation, EvidencePolicy.IncludeCorrelated))
        {
            _ = window.ShowDialog<PatternChoice?>(owner);
            await Until(() => Named<TextBlock>(window, "Patterns status").Text?.StartsWith("Generation", StringComparison.Ordinal) == true);
            ListBox list = Named<ListBox>(window, "Recurring communication patterns. Enter selects the chosen pattern's processes.");
            Assert.Equal(0, list.ItemCount);
            Assert.Equal("No pattern occurred more than once; Include those that occurred once lists those that did.",
                Starting(window, "No pattern").Text);
            Assert.Equal("Choose a pattern first.", Offers.WhyNot(Labelled(window, SelectThem)));
            Once(window).IsChecked = true;
            Assert.Equal((1, 0), (list.ItemCount, list.SelectedIndex));
            Assert.Equal(["Client · git.exe · PID 100 · program git.exe (cmd) · 1 connection",
                "Server · server.exe · PID 300 · 1 connection"],
                Named<ListBox>(window, "Who took part in the chosen pattern").Items.Cast<string>());
            window.Close();
        }

        // Every pattern recurring, none is left to list; a process connecting to itself is one process, gone to.
        using (var itself = new TemporarySession())
        {
            Publish(itself.Store,
            [
                Lifecycle(3, ObservationKind.Inventory, 300, 3) with { ResourceName = @"C:\Apps\server.exe", SessionRelativeTicks = 300 },
                .. Enumerable.Range(0, 2).SelectMany(index => new[]
                {
                    Transfer(10 + (index * 10), ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 300, (ulong)(10 + (index * 10)))
                        .Between($"127.0.0.1:{50_000 + index}", "127.0.0.1:7000") with { SessionRelativeTicks = 1_000 + (index * 1_000) },
                    Transfer(11 + (index * 10), ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 300, (ulong)(11 + (index * 10)))
                        .Between("127.0.0.1:7000", $"127.0.0.1:{50_000 + index}") with { SessionRelativeTicks = 1_100 + (index * 1_000) },
                }),
            ], coverage: TransportLedger(tcp: true, udp: false));
            SessionOverviewBundle overview = SessionOverviewProjector.Project(itself.Store);
            using var window = new PatternsWindow(itself.Path, overview.SessionId, overview.Generation, EvidencePolicy.IncludeCorrelated);
            _ = window.ShowDialog<PatternChoice?>(owner);
            await Until(() => Named<TextBlock>(window, "Patterns status").Text?.StartsWith("Generation", StringComparison.Ordinal) == true);
            Assert.Equal("Every pattern in this session occurred more than once, so the list holds them all.", Offers.WhyNot(Once(window)));
            Assert.Equal(["Client · server.exe · PID 300 · 2 connections", "Server · server.exe · PID 300 · 2 connections"],
                Named<ListBox>(window, "Who took part in the chosen pattern").Items.Cast<string>());
            Assert.Equal("Close this list and select the pattern's process in InterCat's window.", Offers.Offered(Labelled(window, GoToIt)));
            window.Close();
        }

        // Counted under the window's evidence policy, the status says which; what it left out is counted, never dropped.
        using (var window = new PatternsWindow(session.Path, once.SessionId, once.Generation, EvidencePolicy.DirectOnly))
        {
            _ = window.ShowDialog<PatternChoice?>(owner);
            await Until(() => Named<TextBlock>(window, "Patterns status").Text?.StartsWith("Generation", StringComparison.Ordinal) == true);
            Assert.Equal(string.Create(CultureInfo.CurrentCulture, $"Generation {once.Generation:N0} · 0 recurring patterns · counted ")
                + "under the direct-only evidence policy, as InterCat's window counts", Named<TextBlock>(window, "Patterns status").Text);
            Assert.StartsWith("This session holds no pattern the direct-only evidence policy admits;", Starting(window, "This session").Text,
                StringComparison.Ordinal);
            Assert.EndsWith(" the direct-only evidence policy did not admit.", Named<TextBlock>(window, "What no pattern holds").Text,
                StringComparison.Ordinal);
            window.Close();
        }

        // A folder holding an older generation than InterCat's window shows is refused, and one holding another session.
        using (var window = new PatternsWindow(session.Path, once.SessionId, once.Generation + 1, EvidencePolicy.IncludeCorrelated))
        {
            _ = window.ShowDialog<PatternChoice?>(owner);
            await Until(() => Named<TextBlock>(window, "Patterns status").Text?.StartsWith("This folder", StringComparison.Ordinal) == true);
            Assert.Equal("This folder no longer holds the generation InterCat's window shows. Close this list and open it again from "
                + "that window.", Named<TextBlock>(window, "Patterns status").Text);
            Assert.Equal("No patterns are shown; the status line says why.", Offers.WhyNot(Labelled(window, SelectThem)));
            Assert.Equal("No patterns are shown; the status line says why.", Offers.WhyNot(Once(window)));
            window.Close();
        }

        using (var window = new PatternsWindow(session.Path, Guid.NewGuid(), once.Generation, EvidencePolicy.IncludeCorrelated))
        {
            _ = window.ShowDialog<PatternChoice?>(owner);
            await Until(() => Named<TextBlock>(window, "Patterns status").Text?.StartsWith("Could not", StringComparison.Ordinal) == true);
            Assert.StartsWith("Could not read the session's patterns: ", Named<TextBlock>(window, "Patterns status").Text, StringComparison.Ordinal);
            Assert.Equal("No patterns are shown; the status line says why.", Offers.WhyNot(Labelled(window, SelectThem)));
            window.Close();
        }

        owner.Close();
    }

    [AvaloniaFact(DisplayName = "§6.7: Recurring patterns… selects the chosen pattern's processes together, or goes to its one process, and the graph has the keyboard")]
    public async Task ThePatternsProcessesBecomeTheSelection()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = PatternRows();
        Publish(session.Store, rows, fields: fields, coverage: TransportLedger(tcp: true, udp: true));
        var window = new MainWindow { Width = 1_456, Height = 939 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
                Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            Dispatch();
            Button button = window.GetControl<Button>("PatternsButton");
            Assert.True(button.IsEnabled);
            Assert.Equal("List the session's recurring communication patterns", AutomationProperties.GetHelpText(button));
            Assert.Equal("List what recurs in this session's communication - each service, port and RPC procedure, the heaviest "
                + "first, with who took part - and select a pattern's processes.", ToolTip.GetTip(button));

            // The local pattern's three processes become the selection together, and the graph has the keyboard.
            PatternsWindow patterns = Opened(window, button);
            ListBox list = Named<ListBox>(patterns, "Recurring communication patterns. Enter selects the chosen pattern's processes.");
            await Until(() => list.ItemCount > 0);
            list.SelectedIndex = 1;
            Labelled(patterns, SelectThem).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.OwnedWindows.Count == 0 && workspace.HasMultiSelection);
            Assert.Equal([3_000, 3_001, 4_000], workspace.ChosenProcesses.Select(process => process.ProcessId));
            Assert.True(window.GetControl<GraphView>("GraphSurface").IsFocused);

            // A pattern of one process goes to it, in place of the set.
            patterns = Opened(window, button);
            list = Named<ListBox>(patterns, "Recurring communication patterns. Enter selects the chosen pattern's processes.");
            await Until(() => list.ItemCount > 0);
            list.SelectedIndex = 3;
            Labelled(patterns, GoToIt).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.OwnedWindows.Count == 0 && workspace.SelectedProcess is not null);
            Assert.Equal((5_000, false), (workspace.SelectedProcess!.ProcessId, workspace.HasMultiSelection));

            // Closed with none chosen, the selection stays as it was.
            patterns = Opened(window, button);
            await Until(() => Named<TextBlock>(patterns, "Patterns status").Text?.StartsWith("Generation", StringComparison.Ordinal) == true);
            patterns.Close();
            await Until(() => window.OwnedWindows.Count == 0);
            Assert.Equal(5_000, workspace.SelectedProcess!.ProcessId);

            // A newer generation the window does not show yet: the list says so, and a pattern only it holds selects nothing,
            // which the window says.
            Publish(session.Store,
            [
                Lifecycle(20_000, ObservationKind.Create, 7_000, 20_001) with { ResourceName = @"C:\Apps\late.exe" },
                Lifecycle(20_010, ObservationKind.Create, 7_001, 20_011) with { ResourceName = @"C:\Apps\later.exe" },
                .. Enumerable.Range(0, 2).SelectMany(index => new[]
                {
                    Transfer(20_100 + (index * 10), ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 7_000, (ulong)(20_100 + (index * 10)))
                        .Between($"127.0.0.1:{50_500 + index}", "127.0.0.1:6000"),
                    Transfer(20_101 + (index * 10), ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 7_001, (ulong)(20_101 + (index * 10)))
                        .Between("127.0.0.1:6000", $"127.0.0.1:{50_500 + index}"),
                }),
            ]);
            patterns = Opened(window, button);
            TextBlock status = Named<TextBlock>(patterns, "Patterns status");
            await Until(() => status.Text?.StartsWith("Generation", StringComparison.Ordinal) == true);
            Assert.Contains(" (InterCat's window shows generation 1: a process only the newer one holds is not selected there) · ",
                status.Text, StringComparison.Ordinal);
            list = Named<ListBox>(patterns, "Recurring communication patterns. Enter selects the chosen pattern's processes.");
            list.SelectedIndex = list.Items.Cast<PatternRow>().ToList().FindIndex(row => row.Pattern.Key == "tcp:here:6000");
            Labelled(patterns, SelectThem).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.OwnedWindows.Count == 0);
            Assert.Equal("None of the pattern's processes is in the generation this window shows; open the recurring patterns again "
                + "once it shows the newer one.", window.GetControl<TextBlock>("CaptureDetail").Text);
            Assert.Equal(5_000, workspace.SelectedProcess!.ProcessId);

            // Among the source records, where no process is selected, the patterns wait, saying how to leave them.
            Assert.True(workspace.ShowEvidence());
            await workspace.EvidenceReady;
            Dispatch();
            Assert.Equal("Esc leaves the source records; recurring patterns can be listed from any other level.", Offers.WhyNot(button));
            Assert.False(workspace.ChooseProcesses([workspace.WholeSnapshot.Processes[0].Id]));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: the patterns window cuts off no text at its smallest and names every element a screen reader lands on")]
    public async Task TheWindowIsLegibleAndHeard()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = PatternRows();
        Publish(session.Store, rows, fields: fields, coverage: TransportLedger(tcp: true, udp: true));
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        using var window = new PatternsWindow(session.Path, overview.SessionId, overview.Generation, EvidencePolicy.IncludeCorrelated);
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        _ = window.ShowDialog<PatternChoice?>(owner);
        ListBox list = Named<ListBox>(window, "Recurring communication patterns. Enter selects the chosen pattern's processes.");
        await Until(() => list.ItemCount > 0);
        var problems = new List<string>();
        foreach (int index in new[] { 0, 1, 2 })
        {
            list.SelectedIndex = index;
            problems.AddRange(LegibleTextTests.CutOff(window, $"patterns, pattern {index + 1} chosen"));
            problems.AddRange(AccessibilityAuditTests.Unheard(window, out _).Select(problem => $"patterns, pattern {index + 1}: {problem}"));
        }

        window.Close();
        owner.Close();
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>The dialog a button opens, pressed as a person presses it with the keyboard on it.</summary>
    private static PatternsWindow Opened(Window window, Button button)
    {
        Assert.True(button.IsEffectivelyEnabled);
        Assert.True(button.Focus());
        Dispatch();
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (int wait = 0; wait < 500 && !window.OwnedWindows.OfType<PatternsWindow>().Any(); wait++)
        {
            Dispatch();
            Thread.Sleep(10);
        }

        return window.OwnedWindows.OfType<PatternsWindow>().Single();
    }

    private static T Named<T>(Window window, string name)
        where T : Control =>
        window.GetLogicalDescendants().OfType<T>().Single(control => AutomationProperties.GetName(control) == name);

    private static TextBlock Starting(Window window, string start) =>
        window.GetLogicalDescendants().OfType<TextBlock>().Single(block => block.IsVisible
            && block.Text?.StartsWith(start, StringComparison.Ordinal) == true);

    private static TextBlock HostNote(Window window) =>
        window.GetLogicalDescendants().OfType<TextBlock>().Single(block => block.Text == PatternText.HostNote);

    private static CheckBox Once(Window window) => window.GetLogicalDescendants().OfType<CheckBox>().Single();

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
