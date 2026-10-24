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
/// The links window (`contracts/links-v1.md` §8, plan M12's causal navigation): what led to a process and what it led to in
/// `icat links`'s words, a link followed to its process and back, links not followed and nearness in time never followed,
/// and the process it is centred on made the workspace's selection.
/// </summary>
public sealed class LinksWindowTests
{
    private const string Reading = "The session's links are being read.";
    private const string LedToIt = "Led to it. Enter follows the selected link to its process.";
    private const string ItLedTo = "It led to. Enter follows the selected link to its process.";

    [AvaloniaFact(DisplayName = "P8: the links window lists what led to a process and what it led to, follows a link to its process on Enter and returns with Back")]
    public async Task TheWindowFollowsLinks()
    {
        using var session = Session();
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        ProcessInstanceId Of(int pid) => overview.Nodes.Single(node => node.ProcessId == pid && node.PidHolder == 1).Id;
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        using var window = new LinksWindow(session.Path, overview.SessionId, overview.Generation, EvidencePolicy.IncludeCorrelated, Of(LinkApp));

        // Until the session is read, the window's actions wait, each saying so on itself.
        foreach (string label in new[] { "Back", "Follow", "Select this process" })
        {
            Assert.Equal(Reading, Offers.WhyNot(Labelled(window, label)));
        }

        Task<ProcessInstanceId?> chosen = window.ShowDialog<ProcessInstanceId?>(owner);
        TextBlock status = Named<TextBlock>(window, "Links status");
        await Until(() => status.Text?.StartsWith("Generation", StringComparison.Ordinal) == true);
        ListBox ledToIt = Named<ListBox>(window, LedToIt), itLedTo = Named<ListBox>(window, ItLedTo);

        // What led to app.exe and what it led to, each with its evidence and strength, as icat links lists them.
        Assert.Equal(("InterCat · Links", "Links of app.exe · PID 400"), (window.Title, Named<TextBlock>(window, "Whose links these are").Text));
        Assert.Equal(string.Create(CultureInfo.CurrentCulture, $"Generation {overview.Generation:N0} · follows direct and ")
            + "correlated links · 1 link led to it, 4 links it led to", status.Text);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
        Assert.Equal(["its parent · shell.exe · PID 300 · the child's creation record names the parent by its start key · direct"],
            ledToIt.Items.Select(item => item?.ToString()));
        Assert.Equal(
            [
                "served its calls · services.exe · PID 1960 · 2 calls to svcctl (Service Control Manager), each reaching the call that "
                    + "served it through its one ALPC message · correlated",
                "it connected to · server.exe · PID 4000 · 1 connection to port 8080 that it made, as the capture saw it open · correlated",
                "it connected to · 93.184.216.34 · 1 connection to port 443 at that address that it made; who is at an address is not "
                    + "observed · correlated",
                "it connected to · an end on this machine no record holds · 1 connection to port 9100 on this machine that it made; no "
                    + "record holds the other end · correlated",
            ],
            itLedTo.Items.Select(item => item?.ToString()));
        Assert.Equal("Coverage: covered for TCP · no other mechanism collected", Named<TextBlock>(window, "What the capture covered").Text);
        Assert.StartsWith("Created within a second of it, and linked to it by nothing; nearness in time is never a link: other.exe · "
            + "PID 410, created 0.2 µs after it; ", Named<TextBlock>(window, "Created near it in time, linked to it by nothing").Text,
            StringComparison.Ordinal);
        Assert.Equal("2 TCP connections whose opening no record shows, so neither end is known to have made them; 2 UDP flows, which "
            + "nothing opens; 1 call whose other end is unresolved: no ALPC send on its thread during the call.",
            Named<TextBlock>(window, "What of it links to nothing").Text);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == LinkText.Note);
        Assert.False(Panel(window, "Not followed").IsVisible);
        Assert.False(Named<TextBlock>(window, "The links followed to here").IsVisible);

        // The first link it led to has the keyboard, and a screen reader hears each link whole.
        Dispatch();
        Assert.Same(itLedTo.ContainerFromIndex(0), window.FocusManager?.GetFocusedElement());
        Assert.Equal("served its calls: services.exe · PID 1960. 2 calls to svcctl (Service Control Manager), each reaching the call "
            + "that served it through its one ALPC message, correlated.", AutomationProperties.GetName(itLedTo.ContainerFromIndex(0)!));
        Assert.Equal("No link has been followed to here yet.", Offers.WhyNot(Labelled(window, "Back")));
        Assert.Equal("List the links of services.exe · PID 1960 (Enter)", Offers.Offered(Labelled(window, "Follow")));
        Assert.Equal("Close this list and select app.exe · PID 400 in InterCat's window", Offers.Offered(Labelled(window, "Select this process")));

        // A host is an address: nothing is followed through it. Choosing a link in one list lets go of the other's.
        itLedTo.SelectedIndex = 2;
        Assert.Equal("Its other end is an address, not a process of the capture: nothing is followed through it.",
            Offers.WhyNot(Labelled(window, "Follow")));
        ledToIt.SelectedIndex = 0;
        Assert.Equal(-1, itLedTo.SelectedIndex);
        Assert.Equal("List the links of shell.exe · PID 300 (Enter)", Offers.Offered(Labelled(window, "Follow")));
        ledToIt.SelectedIndex = -1;
        Assert.Equal("Choose a link first.", Offers.WhyNot(Labelled(window, "Follow")));

        // Enter follows the chosen link: the window is centred on services.exe, the path walked said above it.
        itLedTo.SelectedIndex = 0;
        _ = window.CaptureRenderedFrame();
        Dispatch();
        Assert.True(itLedTo.ContainerFromIndex(0)!.Focus());
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatch();
        Assert.Equal("Links of services.exe · PID 1960", Named<TextBlock>(window, "Whose links these are").Text);
        Assert.Equal("Followed from app.exe · PID 400 › services.exe · PID 1960", Named<TextBlock>(window, "The links followed to here").Text);
        Assert.True(Named<TextBlock>(window, "The links followed to here").IsVisible);
        Assert.Equal(["called it · app.exe · PID 400 · 2 calls to svcctl (Service Control Manager), each reaching the call that served "
            + "it through its one ALPC message · correlated"], ledToIt.Items.Select(item => item?.ToString()));
        Assert.Equal((0, true, false), (itLedTo.ItemCount, Showing(window, "It led to nothing the evidence policy follows."),
            Showing(window, "Nothing the evidence policy follows led to it.")));
        Assert.Equal("Its creation was not witnessed, so no process is said to have been created near it.",
            Named<TextBlock>(window, "Created near it in time, linked to it by nothing").Text);
        Assert.Equal("2 calls it served that no linked client call reached: a client may be remote, not use ALPC, or not be in the capture.",
            Named<TextBlock>(window, "What of it links to nothing").Text);
        Assert.Equal("Return to app.exe · PID 400 (Alt+Left)", Offers.Offered(Labelled(window, "Back")));

        // A link is followed either way: back up the call to app.exe, the path walked said in the order it was walked.
        ledToIt.SelectedIndex = 0;
        Labelled(window, "Follow").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("Followed from app.exe · PID 400 › services.exe · PID 1960 › app.exe · PID 400",
            Named<TextBlock>(window, "The links followed to here").Text);

        // Alt+Left returns along the path, a link at a time; Back does too. A process with nothing unlinked says nothing of it.
        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.Alt);
        Dispatch();
        Assert.Equal("Links of services.exe · PID 1960", Named<TextBlock>(window, "Whose links these are").Text);
        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.Alt);
        Dispatch();
        Assert.Equal(("Links of app.exe · PID 400", false), (Named<TextBlock>(window, "Whose links these are").Text,
            Named<TextBlock>(window, "The links followed to here").IsVisible));
        ledToIt.SelectedIndex = 0;
        Labelled(window, "Follow").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("Links of shell.exe · PID 300", Named<TextBlock>(window, "Whose links these are").Text);
        Assert.False(Panel(window, "Not linked").IsVisible);
        Labelled(window, "Back").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("Links of app.exe · PID 400", Named<TextBlock>(window, "Whose links these are").Text);

        // Select this process closes the window with the process it is centred on.
        itLedTo.SelectedIndex = 1;
        Labelled(window, "Follow").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("Links of server.exe · PID 4000", Named<TextBlock>(window, "Whose links these are").Text);
        Labelled(window, "Select this process").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => chosen.IsCompleted);
        Assert.Equal(Of(LinkServer), await chosen);

        // Escape closes it with nothing chosen. A process created two seconds after the rest is near none of them.
        using var again = new LinksWindow(session.Path, overview.SessionId, overview.Generation, EvidencePolicy.IncludeCorrelated, Of(LinkLate));
        Task<ProcessInstanceId?> cancelled = again.ShowDialog<ProcessInstanceId?>(owner);
        await Until(() => Named<TextBlock>(again, "Links status").Text?.StartsWith("Generation", StringComparison.Ordinal) == true);
        Assert.Equal("No other process was created within a second of it without a link to it.",
            Named<TextBlock>(again, "Created near it in time, linked to it by nothing").Text);
        Dispatch();
        again.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await Until(() => cancelled.IsCompleted);
        Assert.Null(await cancelled);
        owner.Close();
    }

    [AvaloniaFact(DisplayName = "P8: the links window lists a link the evidence policy does not admit apart, and never follows it")]
    public async Task ALinkNotFollowedIsListedApart()
    {
        using var session = Session();
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        ProcessInstanceId child = overview.Nodes.Single(node => node.ProcessId == LinkReusedChild).Id;
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        using (var window = new LinksWindow(session.Path, overview.SessionId, overview.Generation, EvidencePolicy.IncludeCorrelated, child))
        {
            _ = window.ShowDialog<ProcessInstanceId?>(owner);
            await Until(() => Named<TextBlock>(window, "Links status").Text?.StartsWith("Generation", StringComparison.Ordinal) == true);
            Assert.True(Showing(window, "Nothing the evidence policy follows led to it."));
            Assert.True(Showing(window, "It led to nothing the evidence policy follows."));
            Assert.True(Panel(window, "Not followed").IsVisible);
            Assert.True(Showing(window, "The include-correlated evidence policy does not admit these links' strength: each is possible, "
                + "not established, and is never followed."));
            ListBox notFollowed = Named<ListBox>(window, "Links the evidence policy does not follow");
            Assert.Equal(["its parent · new.exe · PID 77 #2 · the child's creation record names the parent's PID alone, which the "
                + "parent held when the child was created, after another process had held it · candidate"],
                notFollowed.Items.Select(item => item?.ToString()));

            // With no followed link to choose, Close has the keyboard; the link not followed is never followed.
            Dispatch();
            Assert.Equal("Close", Assert.IsType<Button>(window.FocusManager?.GetFocusedElement()).Content);
            notFollowed.SelectedIndex = 0;
            Assert.Equal("The evidence policy does not admit this link's strength, so it is not followed.",
                Offers.WhyNot(Labelled(window, "Follow")));
            notFollowed.ContainerFromIndex(0)?.Focus();
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatch();
            Assert.Equal("Links of child.exe · PID 420", Named<TextBlock>(window, "Whose links these are").Text);
            window.Close();
        }

        // Counting candidates, the same link is followed.
        using (var window = new LinksWindow(session.Path, overview.SessionId, overview.Generation, EvidencePolicy.IncludeCandidates, child))
        {
            _ = window.ShowDialog<ProcessInstanceId?>(owner);
            await Until(() => Named<TextBlock>(window, "Links status").Text?.StartsWith("Generation", StringComparison.Ordinal) == true);
            Assert.Equal(1, Named<ListBox>(window, LedToIt).ItemCount);
            Assert.False(Panel(window, "Not followed").IsVisible);
            Assert.EndsWith("follows direct, correlated and candidate links · 1 link led to it, 0 links it led to",
                Named<TextBlock>(window, "Links status").Text, StringComparison.Ordinal);
            window.Close();
        }

        owner.Close();
    }

    [AvaloniaFact(DisplayName = "R21: the links window says why it lists none - an older generation, a process no longer held, or another session")]
    public async Task TheWindowSaysWhyItListsNone()
    {
        using var session = Session();
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        ProcessInstanceId app = overview.Nodes.Single(node => node.ProcessId == LinkApp).Id;
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        foreach ((Guid sessionId, long generation, ProcessInstanceId step, string said) in new[]
        {
            (overview.SessionId, overview.Generation + 1, app, "This folder no longer holds the generation InterCat's window shows. "
                + "Close this list and open it again from that window."),
            (overview.SessionId, overview.Generation, ProcessInstanceId.New(), "The session's current generation holds no such process, "
                + "so it has no links to list."),
            (Guid.NewGuid(), overview.Generation, app, "Could not read the session's links: "),
        })
        {
            using var window = new LinksWindow(session.Path, sessionId, generation, EvidencePolicy.IncludeCorrelated, step);
            _ = window.ShowDialog<ProcessInstanceId?>(owner);
            TextBlock status = Named<TextBlock>(window, "Links status");
            await Until(() => status.Text?.StartsWith("Reading", StringComparison.Ordinal) == false);
            Assert.StartsWith(said, status.Text, StringComparison.Ordinal);
            foreach (string label in new[] { "Back", "Follow", "Select this process" })
            {
                Assert.Equal("No links are shown; the status line says why.", Offers.WhyNot(Labelled(window, label)));
            }

            Assert.Equal(0, Named<ListBox>(window, ItLedTo).ItemCount);
            Assert.False(Showing(window, "It led to nothing the evidence policy follows."));
            window.Close();
        }

        owner.Close();
    }

    [AvaloniaFact(DisplayName = "§6.7: Follow its links… lists the selected process's links, and the process they reach becomes the selection")]
    public async Task TheMainWindowFollowsLinks()
    {
        using var session = Session();
        var window = new MainWindow { Width = 1_456, Height = 939 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
                Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            Dispatch();
            Button button = window.GetControl<Button>("LinksButton");
            Assert.Equal("Select a process first: its links are listed from it.", Offers.WhyNot(button));

            // Its links follow the selected process, and the one Select this process closes on becomes the selection.
            workspace.SelectProcess(workspace.Snapshot.Processes.Single(process => process.ProcessId == LinkApp).Id);
            Dispatch();
            Assert.True(button.IsEnabled);
            Assert.Equal("List the selected process's links", AutomationProperties.GetHelpText(button));
            Assert.StartsWith("List what led to app.exe · PID 400 and what it led to", ToolTip.GetTip(button) as string, StringComparison.Ordinal);
            LinksWindow links = Opened(window, button);
            await Until(() => Named<TextBlock>(links, "Links status").Text?.StartsWith("Generation", StringComparison.Ordinal) == true);
            Named<ListBox>(links, ItLedTo).SelectedIndex = 0;
            Labelled(links, "Follow").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Labelled(links, "Select this process").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.OwnedWindows.Count == 0 && workspace.SelectedProcess?.ProcessId == LinkServices);
            Assert.True(window.GetControl<GraphView>("GraphSurface").IsFocused);

            // A child of shell.exe only a newer generation holds is reached, and selects nothing, which the window says.
            workspace.SelectProcess(workspace.Snapshot.Processes.Single(process => process.ProcessId == LinkShell).Id);
            ObservationRowV1 newer = Lifecycle(30_000_000, ObservationKind.Create, 470, 9_000) with
            {
                ResourceName = @"C:\Apps\newer.exe",
                SessionRelativeTicks = 3_000_000_000,
            };
            Publish(session.Store, [newer], fields: [Field(newer, SourceField.ParentProcessId, LinkShell), Field(newer, SourceField.ParentStartSequence, 900)]);
            links = Opened(window, button);
            TextBlock status = Named<TextBlock>(links, "Links status");
            await Until(() => status.Text?.StartsWith("Generation", StringComparison.Ordinal) == true);
            Assert.Contains(" (InterCat's window shows generation 1) · ", status.Text, StringComparison.Ordinal);
            ListBox children = Named<ListBox>(links, ItLedTo);
            children.SelectedIndex = children.Items.Cast<LinkRow>().ToList().FindIndex(row => row.End == "newer.exe · PID 470");
            Labelled(links, "Follow").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("Links of newer.exe · PID 470", Named<TextBlock>(links, "Whose links these are").Text);
            Labelled(links, "Select this process").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.OwnedWindows.Count == 0);
            Assert.Equal("The process the links reached is not in the generation this window shows; open its links again once it "
                + "shows the newer one.", window.GetControl<TextBlock>("CaptureDetail").Text);
            Assert.Equal(LinkShell, workspace.SelectedProcess!.ProcessId);

            // Among the source records, where no process is selected, the links wait, saying how to leave them.
            Assert.True(workspace.ShowEvidence());
            await workspace.EvidenceReady;
            Dispatch();
            Assert.Equal("Esc leaves the source records; a process's links can be listed from any other level.", Offers.WhyNot(button));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: the links window cuts off no text at its smallest and names every element a screen reader lands on")]
    public async Task TheWindowIsLegibleAndHeard()
    {
        using var session = Session();
        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        var problems = new List<string>();
        foreach (int pid in new[] { LinkApp, LinkReusedChild })
        {
            using var window = new LinksWindow(session.Path, overview.SessionId, overview.Generation, EvidencePolicy.IncludeCorrelated,
                overview.Nodes.Single(node => node.ProcessId == pid).Id);
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            _ = window.ShowDialog<ProcessInstanceId?>(owner);
            await Until(() => Named<TextBlock>(window, "Links status").Text?.StartsWith("Generation", StringComparison.Ordinal) == true);
            problems.AddRange(LegibleTextTests.CutOff(window, $"links of PID {pid}"));
            problems.AddRange(AccessibilityAuditTests.Unheard(window, out _).Select(problem => $"links of PID {pid}: {problem}"));
            window.Close();
        }

        owner.Close();
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static TemporarySession Session()
    {
        var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = LinkRows();
        Publish(session.Store, rows, fields: fields, coverage: TransportLedger(tcp: true, udp: true));
        return session;
    }

    /// <summary>The dialog a button opens, pressed as a person presses it with the keyboard on it.</summary>
    private static LinksWindow Opened(Window window, Button button)
    {
        Assert.True(button.IsEffectivelyEnabled);
        Assert.True(button.Focus());
        Dispatch();
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (int wait = 0; wait < 500 && !window.OwnedWindows.OfType<LinksWindow>().Any(); wait++)
        {
            Dispatch();
            Thread.Sleep(10);
        }

        return window.OwnedWindows.OfType<LinksWindow>().Single();
    }

    private static T Named<T>(Window window, string name)
        where T : Control =>
        window.GetLogicalDescendants().OfType<T>().Single(control => AutomationProperties.GetName(control) == name);

    /// <summary>Whether a text the window holds is shown: in the tree, and visible with every panel it is in.</summary>
    private static bool Showing(Window window, string text) =>
        window.GetLogicalDescendants().OfType<TextBlock>().Single(block => block.Text == text).IsEffectivelyVisible;

    /// <summary>The panel a section label heads.</summary>
    private static Control Panel(Window window, string label) =>
        (Control)window.GetLogicalDescendants().OfType<TextBlock>().Single(block => block.Text == label).Parent!;

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
