using Avalonia;
using Avalonia.Automation;
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
/// A selected process's connections in the window's inspector (§6.8): how many it made and accepted, as its card counts
/// them, stated where a keyboard and a screen reader reach them.
/// </summary>
public sealed class ConnectionsInspectorWindowTests
{
    [AvaloniaFact(DisplayName = "§6.8: the inspector states how many connections the selected process made and accepted, and names them so for a screen reader")]
    public async Task TheInspectorStatesTheConnections()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        window.ApplyCaptureUpdate(Recording(session));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        TextBlock connections = window.GetControl<TextBlock>("ConnectionsText");
        Assert.False(connections.IsEffectivelyVisible);

        // The client connected to the server and to another host; the server accepted the client's and the helper's, at its
        // own port 8080, which a screen reader hears too.
        workspace.SelectProcess(workspace.Snapshot.Processes.Single(node => node.ProcessId == 100).Id);
        Dispatch();
        Assert.True(connections.IsEffectivelyVisible);
        Assert.Equal("2 made · 0 accepted, as the capture saw them open", connections.Text);
        Assert.Equal("Connections: 2 made · 0 accepted, as the capture saw them open", AutomationProperties.GetName(connections));
        workspace.SelectProcess(workspace.Snapshot.Processes.Single(node => node.ProcessId == 200).Id);
        Dispatch();
        Assert.Equal("0 made · 2 accepted at port 8080, as the capture saw them open", connections.Text);
        Assert.Equal("Connections: 0 made · 2 accepted at port 8080, as the capture saw them open", AutomationProperties.GetName(connections));

        // Beneath them, the processes it talked with: each on a line of its own, marked from its side, over what it carried,
        // and who connected said in words for a screen reader as the keyboard reaches its row.
        ListBox peers = window.GetControl<ListBox>("PeersList");
        Assert.True(peers.IsEffectivelyVisible);
        Assert.Equal("Its relationships, the busiest first", window.GetControl<TextBlock>("PeersHeadingText").Text);
        Assert.Equal(["← helper.exe · PID 300", "1 accepted · TCP at 8080 · 4 records", "← client.exe · PID 100",
            "1 accepted · TCP at 8080 · 3 records"], Texts(peers));
        Assert.Equal("Its relationships, the busiest first; Enter on one chooses the process at its other end",
            AutomationProperties.GetName(peers));
        Assert.Equal("client.exe · PID 100 connected to it: 1 accepted, TCP at 8080, 3 records. Press Enter to choose the process at its "
            + "other end.", AutomationProperties.GetName(peers.ContainerFromIndex(1)!));
        Assert.False(window.GetControl<TextBlock>("PeersMoreText").IsEffectivelyVisible);
        Save(window, "connections-inspector-1456x939.png");

        // Enter on the client's chooses the client at its other end, whose own relationships the list then holds, the
        // keyboard staying on it, so who connected to whom is walked a key at a time.
        Assert.True(peers.ContainerFromIndex(1)!.Focus());
        Dispatch();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatch();
        Assert.Equal(100, workspace.SelectedProcess?.ProcessId);
        Assert.Equal(["→ server.exe · PID 200", "1 made · TCP at 8080 · 3 records"], Texts(peers));
        Assert.Same(peers.ContainerFromIndex(0), window.FocusManager?.GetFocusedElement());

        // Esc comes back: the server chosen again, the keyboard on the relationship it was left by; Esc again, with no hop
        // left, climbs the ladder as anywhere, and on the machine's rung lets the choice go, the list with it: the keyboard
        // goes to the ranked table, not to nothing, and chooses none of its rows.
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatch();
        Assert.Equal(200, workspace.SelectedProcess?.ProcessId);
        Assert.Equal("← client.exe · PID 100", Texts(peers)[2]);
        Assert.Same(peers.ContainerFromIndex(1), window.FocusManager?.GetFocusedElement());
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatch();
        Assert.Null(workspace.SelectedProcess);
        ListBox rungs = window.GetControl<ListBox>("RungList");
        Assert.True(rungs.IsKeyboardFocusWithin);
        Assert.Null(workspace.SelectedRung);

        // Nothing chosen, nothing said.
        workspace.ClearSelection();
        Dispatch();
        Assert.False(connections.IsEffectivelyVisible);

        // A program chosen names the programs its processes talked with, each pair on a line of its own over what joins it,
        // and who connected said in words for a screen reader as the keyboard reaches its row.
        ListBox programs = window.GetControl<ListBox>("ProgramsList");
        Assert.False(programs.IsEffectivelyVisible);
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "server.exe");
        Dispatch();
        Assert.True(programs.IsEffectivelyVisible);
        Assert.Equal("Relationships by program", window.GetControl<TextBlock>("ProgramsHeadingText").Text);
        Assert.Equal(["helper.exe → server.exe", "1 relationship · 4 records", "client.exe → server.exe",
            "1 relationship · 3 records"], Texts(programs));
        Assert.Equal("Relationships by program; Enter on one chooses the program at its other end",
            AutomationProperties.GetName(programs));
        Assert.Equal("client.exe connected to server.exe: 1 relationship, 3 records. Press Enter to choose the program at its "
            + "other end.", AutomationProperties.GetName(programs.ContainerFromIndex(1)!));
        Assert.False(window.GetControl<TextBlock>("ProgramsMoreText").IsEffectivelyVisible);
        Assert.False(window.GetControl<ListBox>("PeersList").IsEffectivelyVisible);

        // Enter on the client's chooses the client at its other end, as its ranked row does, the keyboard staying on the
        // list, now the client's; Esc comes back to the server, the keyboard on the pair it was left by, and Esc again lets
        // the choice go, the keyboard going to the ranked table, on the server's row.
        Assert.True(programs.ContainerFromIndex(1)!.Focus());
        Dispatch();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatch();
        Assert.Equal(("client.exe", "client.exe"), (workspace.SelectedGroup?.Name, workspace.SelectedRung?.Label));
        Assert.Equal(["client.exe → server.exe", "1 relationship · 3 records"], Texts(programs));
        Assert.Same(programs.ContainerFromIndex(0), window.FocusManager?.GetFocusedElement());
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatch();
        Assert.Equal(("server.exe", "server.exe"), (workspace.SelectedGroup?.Name, workspace.SelectedRung?.Label));
        Assert.Same(programs.ContainerFromIndex(1), window.FocusManager?.GetFocusedElement());
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatch();
        Assert.Null(workspace.SelectedGroup);
        Assert.Equal("server.exe", ((window.FocusManager?.GetFocusedElement() as ListBoxItem)?.DataContext as RungRow)?.Label);
        Assert.True(rungs.IsKeyboardFocusWithin);

        // A double click on a pair chooses its other end alike.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "server.exe");
        Dispatch();
        Control pair = Assert.IsAssignableFrom<Control>(programs.ContainerFromIndex(0));
        pair.BringIntoView();
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
        Point on = pair.TranslatePoint(new(pair.Bounds.Width / 2, pair.Bounds.Height / 2), window)!.Value;
        Assert.True(window.InputHitTest(on) is Visual hit && pair.IsVisualAncestorOf(hit));
        window.MouseDown(on, MouseButton.Left);
        window.MouseUp(on, MouseButton.Left);
        window.MouseDown(on, MouseButton.Left);
        window.MouseUp(on, MouseButton.Left);
        Dispatch();
        Assert.Equal("helper.exe", workspace.SelectedGroup?.Name);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R7/R15: a newer generation published while recording keeps the way back along a walk of programs, the keyboard on the pair it was left by wherever the list now holds it")]
    public async Task ANewerGenerationKeepsTheWayBackAlongAWalk()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(Recording(session));
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            Dispatch();
            ListBox programs = window.GetControl<ListBox>("ProgramsList");
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "server.exe");
            Dispatch();
            Assert.Equal(["helper.exe → server.exe", "1 relationship · 4 records", "client.exe → server.exe",
                "1 relationship · 3 records"], Texts(programs));
            Assert.True(programs.ContainerFromIndex(1)!.Focus());
            Dispatch();
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatch();
            Assert.Equal("client.exe", workspace.SelectedGroup?.Name);

            // The next generation, in which the client sent twice more, replaces the workspace: the client stays chosen with
            // the keyboard on its pair, and Esc comes back to the server, the keyboard on the client's pair, listed first now.
            Publish(session.Store,
            [
                Timed(Transfer(40, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 12).Between("127.0.0.1:50000", "127.0.0.1:8080")),
                Timed(Transfer(41, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 13).Between("127.0.0.1:50000", "127.0.0.1:8080")),
            ]);
            window.ApplyCaptureUpdate(Recording(session));
            var next = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            Assert.NotSame(workspace, next);
            await next.LayoutReady;
            Dispatch();
            Assert.Equal("client.exe", next.SelectedGroup?.Name);
            Assert.Same(programs.ContainerFromIndex(0), window.FocusManager?.GetFocusedElement());
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatch();
            Assert.Equal(("server.exe", "server.exe"), (next.SelectedGroup?.Name, next.SelectedRung?.Label));
            Assert.Equal(["client.exe → server.exe", "1 relationship · 5 records", "helper.exe → server.exe",
                "1 relationship · 4 records"], Texts(programs));
            Assert.Same(programs.ContainerFromIndex(0), window.FocusManager?.GetFocusedElement());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§6.3/R15: a hub's relationships and its program's pairs are listed whole by a button under each list, the keyboard on the first row it adds")]
    public async Task AHubsListsAreListedWholeByAButton()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Hub());
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(Recording(session));
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            Dispatch();
            ListBox peers = window.GetControl<ListBox>("PeersList");
            Button showPeers = window.GetControl<Button>("PeersToggleButton");
            workspace.SelectProcess(workspace.Snapshot.Processes.Single(node => node.ProcessId == 1000).Id);
            Dispatch();
            Assert.Equal(5, peers.ItemCount);
            Assert.True(showPeers.IsEffectivelyVisible);
            Assert.Equal(("Show all 7 relationships", "Show all 7 relationships"),
                (AutomationProperties.GetName(showPeers), Assert.IsType<TextBlock>(showPeers.Content).Text));

            // Pressed, it lists all seven, the keyboard on the sixth, the first it added, and says it names the busiest again.
            Assert.True(showPeers.Focus());
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatch();
            Assert.Equal(7, peers.ItemCount);
            Assert.Same(peers.ContainerFromIndex(5), window.FocusManager?.GetFocusedElement());
            Assert.Equal(("Show the busiest 5", "Show the busiest 5"),
                (AutomationProperties.GetName(showPeers), Assert.IsType<TextBlock>(showPeers.Content).Text));

            // Pressed again, with Space, which presses a button as it is let go, it names the busiest five, the keyboard
            // staying on it.
            Assert.True(showPeers.Focus());
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Dispatch();
            Assert.Equal(5, peers.ItemCount);
            Assert.Same(showPeers, window.FocusManager?.GetFocusedElement());

            // The program's pairs alike.
            ListBox programs = window.GetControl<ListBox>("ProgramsList");
            Button showPrograms = window.GetControl<Button>("ProgramsToggleButton");
            workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "hub.exe");
            Dispatch();
            Assert.Equal(5, programs.ItemCount);
            Assert.Equal(("Show all 7 programs", "Show all 7 programs"),
                (AutomationProperties.GetName(showPrograms), Assert.IsType<TextBlock>(showPrograms.Content).Text));
            Assert.True(showPrograms.Focus());
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatch();
            Assert.Equal(7, programs.ItemCount);
            Assert.Same(programs.ContainerFromIndex(5), window.FocusManager?.GetFocusedElement());
            Assert.Equal("Show the busiest 5", Assert.IsType<TextBlock>(showPrograms.Content).Text);
            Save(window, "hub-pairs-listed-whole-1456x939.png");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R7/R15: a newer generation published while recording keeps the keyboard on the relationship or pair of programs it was on, wherever the newer counts list it")]
    public async Task ANewerGenerationKeepsTheKeyboardOnItsRelationship()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Hub());
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(Recording(session));
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            Dispatch();
            ListBox peers = window.GetControl<ListBox>("PeersList");
            workspace.SelectProcess(workspace.Snapshot.Processes.Single(node => node.ProcessId == 1000).Id);
            Assert.True(workspace.TogglePeers());
            Dispatch();
            Assert.True(peers.ContainerFromIndex(5)!.Focus());
            Dispatch();
            Assert.Equal("→ peer5.exe · PID 2005", Focused(window)?.Peer);

            // The hub sends to peer0 twice and to peer5 once: the next generation lists peer0's first and peer5's second, and
            // the keyboard stays on peer5's, not on the row that now comes where it was, nor on the first.
            Publish(session.Store, MoreSends(1));
            window.ApplyCaptureUpdate(Recording(session));
            var next = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await next.LayoutReady;
            Dispatch();
            Assert.Equal(["→ peer0.exe · PID 2000", "→ peer5.exe · PID 2005"], next.SelectionPeers.Take(2).Select(row => row.Peer));
            Assert.Same(peers.ContainerFromIndex(1), window.FocusManager?.GetFocusedElement());
            Assert.Equal("1 made · TCP at 8005 · 3 records", Focused(window)?.Detail);

            // So does a program's pair: peer6's, last, listed second once the hub sent it twice more.
            ListBox programs = window.GetControl<ListBox>("ProgramsList");
            next.SelectedRung = next.RungRows.Single(row => row.Label == "hub.exe");
            Assert.True(next.TogglePrograms());
            Dispatch();
            Assert.Equal("hub.exe → peer6.exe", Assert.IsType<ProgramPairRow>(programs.Items[6]).Pair);
            Assert.True(programs.ContainerFromIndex(6)!.Focus());
            Dispatch();
            Publish(session.Store, MoreSends(2));
            window.ApplyCaptureUpdate(Recording(session));
            var last = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await last.LayoutReady;
            Dispatch();
            Assert.Equal("hub.exe → peer6.exe", Assert.IsType<ProgramPairRow>(programs.Items[1]).Pair);
            Assert.Same(programs.ContainerFromIndex(1), window.FocusManager?.GetFocusedElement());
        }
        finally
        {
            window.Close();
        }

        static PeerRow? Focused(Window window) => (window.FocusManager?.GetFocusedElement() as ListBoxItem)?.DataContext as PeerRow;

        // Round one: the hub sends to peer0 twice and to peer5 once. Round two: to peer6 twice.
        static ObservationRowV1[] MoreSends(int round) => round == 1
            ?
            [
                Timed(Transfer(60, ObservationKind.Send, AccountingSide.SendSide, 8, 1000, 300).Between("127.0.0.1:50000", "127.0.0.1:8000")),
                Timed(Transfer(61, ObservationKind.Send, AccountingSide.SendSide, 8, 1000, 301).Between("127.0.0.1:50000", "127.0.0.1:8000")),
                Timed(Transfer(62, ObservationKind.Send, AccountingSide.SendSide, 8, 1000, 302).Between("127.0.0.1:50005", "127.0.0.1:8005")),
            ]
            :
            [
                Timed(Transfer(70, ObservationKind.Send, AccountingSide.SendSide, 8, 1000, 310).Between("127.0.0.1:50006", "127.0.0.1:8006")),
                Timed(Transfer(71, ObservationKind.Send, AccountingSide.SendSide, 8, 1000, 311).Between("127.0.0.1:50006", "127.0.0.1:8006")),
            ];
    }

    [AvaloniaFact(DisplayName = "§6.3/R15: a chosen process's other hosts are listed in the inspector, Enter on one finds who reached it, a button lists them all, and a newer generation keeps the keyboard on its host")]
    public async Task AProcesssOtherHostsAreListed()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Crawler());
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(Recording(session));
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            Dispatch();
            ListBox hosts = window.GetControl<ListBox>("ReachedHostsList");
            Assert.False(hosts.IsEffectivelyVisible);
            workspace.SelectProcess(workspace.Snapshot.Processes.Single(node => node.ProcessId == 900).Id);
            Dispatch();
            Assert.True(hosts.IsEffectivelyVisible);
            Assert.Equal("Other hosts it reached, the busiest first", window.GetControl<TextBlock>("ReachedHostsHeadingText").Text);
            Assert.Equal(["52.0.0.1", "1 TCP connection · port 443 · 1 record"], Texts(hosts).Take(2));
            Assert.Equal("Other hosts it reached, the busiest first; Enter on one finds the processes that reached it",
                AutomationProperties.GetName(hosts));
            Assert.Equal("52.0.0.1, 1 TCP connection · port 443 · 1 record. Press Enter to find the processes that reached it.",
                AutomationProperties.GetName(hosts.ContainerFromIndex(0)!));

            // Enter on one finds every process that reached it, the crawler and the updater, the keyboard staying on it.
            Assert.True(hosts.ContainerFromIndex(0)!.Focus());
            Dispatch();
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatch();
            Assert.Equal("52.0.0.1", workspace.SearchText);
            Assert.Equal(["crawler.exe", "updater.exe"],
                workspace.SearchResults.Where(row => row.Hit.Kind == SearchHitKind.Process)
                    .Select(row => row.Hit.Label.Split(' ')[0]).Order(StringComparer.Ordinal));
            Assert.Equal(900, workspace.SelectedProcess?.ProcessId);
            Assert.Same(hosts.ContainerFromIndex(0), window.FocusManager?.GetFocusedElement());

            // The button lists all seven, the keyboard on the sixth.
            Button showHosts = window.GetControl<Button>("ReachedHostsToggleButton");
            Assert.Equal("Show all 7 hosts", AutomationProperties.GetName(showHosts));
            Assert.True(showHosts.Focus());
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatch();
            Assert.Equal(7, hosts.ItemCount);
            Assert.Same(hosts.ContainerFromIndex(5), window.FocusManager?.GetFocusedElement());
            Assert.Equal("Show the busiest 5", Assert.IsType<TextBlock>(showHosts.Content).Text);

            // On the seventh, the next generation - three sends to the third host and one to the seventh - lists the
            // seventh second, and the keyboard stays on it there.
            Assert.True(hosts.ContainerFromIndex(6)!.Focus());
            Dispatch();
            Publish(session.Store,
            [
                .. Enumerable.Range(0, 3).Select(index => Timed(Transfer(80 + index, ObservationKind.Send, AccountingSide.SendSide, 8, 900,
                    (ulong)(300 + index)).Between("192.168.1.10:52003", "52.0.0.3:443"))),
                Timed(Transfer(84, ObservationKind.Send, AccountingSide.SendSide, 8, 900, 310).Between("192.168.1.10:52007", "52.0.0.7:443")),
            ]);
            window.ApplyCaptureUpdate(Recording(session));
            var next = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await next.LayoutReady;
            Dispatch();
            Assert.Equal(["52.0.0.3", "52.0.0.7"], next.ReachedHostRows.Take(2).Select(row => row.Address));
            Assert.Same(hosts.ContainerFromIndex(1), window.FocusManager?.GetFocusedElement());

            // Its program's are listed alike, the heading saying whose they are.
            TextBlock heading = window.GetControl<TextBlock>("ReachedHostsHeadingText");
            next.SelectedRung = next.RungRows.Single(row => row.Label == "updater.exe");
            Dispatch();
            Assert.Equal("Other hosts its processes reached, the busiest first", heading.Text);
            Assert.Equal(["52.0.0.1", "1 TCP connection · port 443 · 1 record"], Texts(hosts));
            next.SelectProcess(next.Snapshot.Processes.Single(node => node.ProcessId == 900).Id);
            Dispatch();
            Assert.Equal("Other hosts it reached, the busiest first", heading.Text);

            // A double click on a host finds who reached it alike.
            Control first = Assert.IsAssignableFrom<Control>(hosts.ContainerFromIndex(0));
            first.BringIntoView();
            Dispatch();
            _ = window.CaptureRenderedFrame();
            Dispatch();
            Point on = first.TranslatePoint(new(first.Bounds.Width / 2, first.Bounds.Height / 2), window)!.Value;
            Assert.True(window.InputHitTest(on) is Visual hit && first.IsVisualAncestorOf(hit));
            window.MouseDown(on, MouseButton.Left);
            window.MouseUp(on, MouseButton.Left);
            window.MouseDown(on, MouseButton.Left);
            window.MouseUp(on, MouseButton.Left);
            Dispatch();
            Assert.Equal("52.0.0.3", next.SearchText);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>crawler.exe's PID 900 connects to seven hosts, 52.0.0.1 to 52.0.0.7, once each; updater.exe's PID 910 to the first.</summary>
    private static ObservationRowV1[] Crawler() =>
    [
        Timed(Lifecycle(1, ObservationKind.Create, 900, 1) with { ResourceName = @"C:\Tools\crawler.exe" }),
        Timed(Lifecycle(2, ObservationKind.Create, 910, 2) with { ResourceName = @"C:\Tools\updater.exe" }),
        .. Enumerable.Range(1, 7).Select(index => Timed(Transfer(10 + index, ObservationKind.Connect, AccountingSide.EndpointActivity, 0,
            900, (ulong)(10 + index)).Between($"192.168.1.10:{52000 + index}", $"52.0.0.{index}:443"))),
        Timed(Transfer(30, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 910, 30).Between("192.168.1.10:53000", "52.0.0.1:443")),
    ];

    /// <summary>hub.exe's PID 1000 connects to the one process of each of seven programs.</summary>
    private static ObservationRowV1[] Hub() =>
    [
        Timed(Lifecycle(1, ObservationKind.Create, 1000, 1) with { ResourceName = @"C:\Tools\hub.exe" }),
        .. Enumerable.Range(0, 7).SelectMany(index => new[]
        {
            Timed(Lifecycle(2 + index, ObservationKind.Create, 2000 + index, (ulong)(2 + index))
                with { ResourceName = $@"C:\Tools\peer{index}.exe" }),
            Timed(Transfer(20 + (2 * index), ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 1000, (ulong)(100 + (2 * index)))
                .Between($"127.0.0.1:{50000 + index}", $"127.0.0.1:{8000 + index}")),
            Timed(Transfer(21 + (2 * index), ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 2000 + index, (ulong)(101 + (2 * index)))
                .Between($"127.0.0.1:{8000 + index}", $"127.0.0.1:{50000 + index}")),
        }),
    ];

    /// <summary>
    /// The client connects to the server and to another host, and the helper to the server, sending twice to the client's
    /// once.
    /// </summary>
    private static ObservationRowV1[] Rows() =>
    [
        Timed(Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" }),
        Timed(Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe" }),
        Timed(Lifecycle(3, ObservationKind.Create, 300, 7) with { ResourceName = @"C:\Tools\helper.exe" }),
        Timed(Transfer(10, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 3).Between("127.0.0.1:50000", "127.0.0.1:8080")),
        Timed(Transfer(11, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 4).Between("127.0.0.1:8080", "127.0.0.1:50000")),
        Timed(Transfer(12, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 5).Between("127.0.0.1:50000", "127.0.0.1:8080")),
        Timed(Transfer(20, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 6).Between("192.168.1.10:52000", "203.0.113.1:443")),
        Timed(Transfer(30, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 300, 8).Between("127.0.0.1:50001", "127.0.0.1:8080")),
        Timed(Transfer(31, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 9).Between("127.0.0.1:8080", "127.0.0.1:50001")),
        Timed(Transfer(32, ObservationKind.Send, AccountingSide.SendSide, 8, 300, 10).Between("127.0.0.1:50001", "127.0.0.1:8080")),
        Timed(Transfer(33, ObservationKind.Send, AccountingSide.SendSide, 8, 300, 11).Between("127.0.0.1:50001", "127.0.0.1:8080")),
    ];

    /// <summary>The session's latest generation as a recording publishes it.</summary>
    private static CaptureUiUpdate Recording(TemporarySession session) => new(CaptureUiPhase.Recording, "Recording",
        "A published generation.", SessionPath: session.Path, Overview: SessionOverviewProjector.Project(session.Store));

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

    /// <summary>A row whose session time is its reading in workspace ticks.</summary>
    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    /// <summary>The words a list's rows show, row by row and line by line.</summary>
    private static string[] Texts(ItemsControl list) =>
        [.. list.GetRealizedContainers().SelectMany(container => container.GetVisualDescendants().OfType<TextBlock>())
            .Select(text => text.Text ?? string.Empty)];
}
