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
/// The session's listening ports in the window's inspector while nothing is chosen (§6.3): where a keyboard and a screen
/// reader reach them, Enter or a double click on one choosing the process that accepted its connections, the keyboard on
/// what its inspector lists of who connected, and Esc coming back to the port.
/// </summary>
public sealed class ListeningPortsWindowTests
{
    [AvaloniaFact(DisplayName = "§6.3: with nothing chosen the inspector lists the listening ports; Enter on one chooses its process, the keyboard on who connected, and Esc comes back to the port")]
    public async Task TheInspectorListsTheListeningPorts()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "Recording", "A published generation.", SessionPath: session.Path,
            Overview: SessionOverviewProjector.Project(session.Store)));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;

        // Nothing chosen: the busiest five, each port in a column of its own beside the process that accepted there and who
        // connected, read whole for a screen reader as the keyboard reaches it.
        ListBox ports = window.GetControl<ListBox>("ListenersList");
        Assert.True(ports.IsEffectivelyVisible);
        Assert.Equal("6 listening ports, the busiest first", window.GetControl<TextBlock>("ListenersHeadingText").Text);
        Assert.Equal(
            [
                "8080", "server.exe · PID\u00A0200", "2 connections accepted from 2 processes of client.exe and helper.exe",
                "445", "files.exe · PID\u00A0600", "1 connection accepted from 10.0.0.7",
                "8443", "server.exe · PID\u00A0200", "1 connection accepted from 10.0.0.6",
                "9001", "server.exe · PID\u00A0200", "1 connection accepted from 10.0.0.8",
                "9002", "server.exe · PID\u00A0200", "1 connection accepted from 10.0.0.8",
            ],
            Texts(ports));
        Assert.Equal("6 listening ports, the busiest first: the ports this machine's processes accepted connections at; Enter "
            + "on one chooses the process that accepted them", AutomationProperties.GetName(ports));
        Assert.Equal("files.exe · PID 600 accepted 1 connection at port 445, from 10.0.0.7. Press Enter to choose that process.",
            AutomationProperties.GetName(ports.ContainerFromIndex(1)!));
        Assert.False(window.GetControl<TextBlock>("ListenersMoreText").IsEffectivelyVisible);

        // Every row's port column is as wide as the widest port's, so the processes beside them start at one edge: files.exe
        // beside a three-digit port where the server is beside four.
        double[] edges = [.. Enumerable.Range(0, 5).Select(index => ports.ContainerFromIndex(index)!
            .GetVisualDescendants().OfType<TextBlock>().ElementAt(1).TranslatePoint(default, ports)!.Value.X)];
        Assert.All(edges, edge => Assert.Equal(edges[0], edge, 0.5));

        // Who connected runs beneath both, from the port's edge, so a narrow inspector does not stack it a word to a line.
        TextBlock[] first = [.. ports.ContainerFromIndex(0)!.GetVisualDescendants().OfType<TextBlock>()];
        Assert.Equal(first[0].TranslatePoint(default, ports)!.Value.X, first[2].TranslatePoint(default, ports)!.Value.X, 0.5);
        Assert.True(first[2].TranslatePoint(default, ports)!.Value.Y
            >= first[1].TranslatePoint(new Point(0, first[1].Bounds.Height), ports)!.Value.Y);
        Save(window, "listening-ports-1456x939.png");

        // Enter on the server's 8080 chooses the server, the list going with the choice, and the keyboard goes to its
        // relationships, which say who connected to it there.
        Assert.True(ports.ContainerFromIndex(0)!.Focus());
        Dispatch();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatch();
        Assert.Equal(200, workspace.SelectedProcess?.ProcessId);
        Assert.False(ports.IsEffectivelyVisible);
        ListBox peers = window.GetControl<ListBox>("PeersList");
        Assert.Same(peers.ContainerFromIndex(0), window.FocusManager?.GetFocusedElement());

        // Esc comes back: nothing chosen, the keyboard on the port it was left by.
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatch();
        Assert.Null(workspace.SelectedProcess);
        Assert.True(ports.IsEffectivelyVisible);
        Assert.Same(ports.ContainerFromIndex(0), window.FocusManager?.GetFocusedElement());

        // A double click on files.exe's 445 chooses it, whose only connection another host made: the keyboard goes to that
        // host, the other hosts that connected to it; Esc comes back to the port.
        _ = window.CaptureRenderedFrame();
        Control files = ports.ContainerFromIndex(1)!;
        Point at = files.TranslatePoint(new Point(files.Bounds.Width / 2, files.Bounds.Height / 2), window)!.Value;
        for (int click = 0; click < 2; click++)
        {
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
        }

        Dispatch();
        Assert.Equal(600, workspace.SelectedProcess?.ProcessId);
        Assert.Equal("Other hosts that connected to it, the busiest first",
            window.GetControl<TextBlock>("ReachedHostsHeadingText").Text);
        ListBox hosts = window.GetControl<ListBox>("ReachedHostsList");
        Assert.Same(hosts.ContainerFromIndex(0), window.FocusManager?.GetFocusedElement());
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatch();
        Assert.Null(workspace.SelectedProcess);
        Assert.Same(ports.ContainerFromIndex(1), window.FocusManager?.GetFocusedElement());

        // The button lists the sixth, the keyboard on it, and the busiest five again.
        Button toggle = window.GetControl<Button>("ListenersToggleButton");
        Assert.Equal("Show all 6 listening ports", AutomationProperties.GetName(toggle));
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatch();
        Assert.Equal(6, ports.ItemCount);
        Assert.Same(ports.ContainerFromIndex(5), window.FocusManager?.GetFocusedElement());
        Assert.Equal("Show the busiest 5", AutomationProperties.GetName(toggle));
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatch();
        Assert.Equal(5, ports.ItemCount);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R7/R15: a newer generation published while recording keeps the keyboard on the listening port it was on, wherever the newer counts list it")]
    public async Task ANewerGenerationKeepsTheKeyboardOnItsPort()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "Recording", "A published generation.",
                SessionPath: session.Path, Overview: SessionOverviewProjector.Project(session.Store)));
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            Dispatch();
            ListBox ports = window.GetControl<ListBox>("ListenersList");
            Assert.True(ports.ContainerFromIndex(4)!.Focus());
            Dispatch();
            Assert.Equal((ushort)9002, Focused(window)?.Port);

            // Another connection arrives at the server's 9002: the next generation lists it second, after the 8080 it ties
            // with, and the keyboard stays on it, not on the row that now comes where it was, nor on the first.
            Publish(session.Store,
            [
                Timed(Transfer(40, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 40).Between("192.168.1.10:9002", "10.0.0.9:51004")),
            ]);
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "Recording", "A published generation.",
                SessionPath: session.Path, Overview: SessionOverviewProjector.Project(session.Store)));
            var next = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await next.LayoutReady;
            Dispatch();
            Assert.Equal(["8080", "9002"], next.ListenerRows.Take(2).Select(row => row.PortText));
            Assert.Same(ports.ContainerFromIndex(1), window.FocusManager?.GetFocusedElement());
            Assert.Equal("2 connections accepted from 2 hosts", Focused(window)?.Detail);
        }
        finally
        {
            window.Close();
        }

        static ListenerRow? Focused(Window window) => (window.FocusManager?.GetFocusedElement() as ListBoxItem)?.DataContext as ListenerRow;
    }

    [AvaloniaFact(DisplayName = "§8.3/R15: a session read with its investigation names its ports' candidate other ends beneath their rows, the keyboard staying on the port it was on")]
    public async Task CandidateOtherEndsKeepTheKeyboardOnItsPort()
    {
        string folder = Path.Combine(Path.GetTempPath(), "InterCat.Ui.Tests.ListeningPorts", Guid.NewGuid().ToString("N"));
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            DemoInvestigationResult demo = DemoInvestigation.Create(Path.Combine(folder, "demo"));
            SessionOverviewBundle overview = SessionOverviewProjector.Project(
                SessionStore.OpenForViewing(LocalOwnedDirectory.Open(demo.ServerSession)));
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: demo.ServerSession,
                Overview: overview), forceOverview: true);
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            Dispatch();
            ListBox ports = window.GetControl<ListBox>("ListenersList");
            TextBlock note = window.GetControl<TextBlock>("ListenersNoteText");
            Assert.False(note.IsEffectivelyVisible);
            Assert.True(ports.ContainerFromIndex(2)!.Focus());
            Dispatch();
            Assert.Equal((ushort)8443, Focused(window)?.Port);

            // The candidates read restate the rows: files.exe's 8443 names the client's sync.exe, the keyboard still on it, and
            // the note beneath says what they are.
            workspace.ReadCandidateHosts(demo.WorkspacePath);
            await workspace.CandidateHostsReady;
            Dispatch();
            Assert.Same(ports.ContainerFromIndex(2), window.FocusManager?.GetFocusedElement());
            Assert.EndsWith(" · candidate other ends: demo client: sync.exe · PID 4200", Focused(window)?.Detail, StringComparison.Ordinal);
            Assert.True(note.IsEffectivelyVisible);
            Assert.StartsWith("The candidate other ends named here are processes of the investigation's other sessions",
                note.Text, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        static ListenerRow? Focused(Window window) => (window.FocusManager?.GetFocusedElement() as ListBoxItem)?.DataContext as ListenerRow;
    }

    /// <summary>
    /// The client and the helper connect to the server's 8080; other hosts connect to the server's 8443, 9001, 9002 and
    /// 9003 and to files.exe's 445.
    /// </summary>
    private static ObservationRowV1[] Rows() =>
    [
        Timed(Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" }),
        Timed(Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe" }),
        Timed(Lifecycle(3, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Tools\helper.exe" }),
        Timed(Lifecycle(4, ObservationKind.Create, 600, 4) with { ResourceName = @"C:\Tools\files.exe" }),
        Timed(Transfer(10, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 10).Between("127.0.0.1:50000", "127.0.0.1:8080")),
        Timed(Transfer(11, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 11).Between("127.0.0.1:8080", "127.0.0.1:50000")),
        Timed(Transfer(12, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 12).Between("127.0.0.1:50000", "127.0.0.1:8080")),
        Timed(Transfer(20, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 300, 20).Between("127.0.0.1:50001", "127.0.0.1:8080")),
        Timed(Transfer(21, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 21).Between("127.0.0.1:8080", "127.0.0.1:50001")),
        Timed(Transfer(22, ObservationKind.Send, AccountingSide.SendSide, 8, 300, 22).Between("127.0.0.1:50001", "127.0.0.1:8080")),
        Timed(Transfer(30, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 600, 30).Between("192.168.1.10:445", "10.0.0.7:51000")),
        Timed(Transfer(31, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 31).Between("192.168.1.10:8443", "10.0.0.6:51000")),
        Timed(Transfer(32, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 32).Between("192.168.1.10:9001", "10.0.0.8:51001")),
        Timed(Transfer(33, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 33).Between("192.168.1.10:9002", "10.0.0.8:51002")),
        Timed(Transfer(34, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 34).Between("192.168.1.10:9003", "10.0.0.8:51003")),
    ];

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
