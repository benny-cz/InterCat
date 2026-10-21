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
        Publish(session.Store,
        [
            Timed(Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" }),
            Timed(Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe" }),
            Timed(Transfer(10, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 3).Between("127.0.0.1:50000", "127.0.0.1:8080")),
            Timed(Transfer(11, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 4).Between("127.0.0.1:8080", "127.0.0.1:50000")),
            Timed(Transfer(12, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 5).Between("127.0.0.1:50000", "127.0.0.1:8080")),
            Timed(Transfer(20, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 6).Between("192.168.1.10:52000", "203.0.113.1:443")),
        ]);
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        window.ApplyCaptureUpdate(new CaptureUiUpdate(CaptureUiPhase.Recording, "Recording", "A published generation.",
            SessionPath: session.Path, Overview: SessionOverviewProjector.Project(session.Store)));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        TextBlock connections = window.GetControl<TextBlock>("ConnectionsText");
        Assert.False(connections.IsEffectivelyVisible);

        // The client connected to the server and to another host; the server accepted the one.
        workspace.SelectProcess(workspace.Snapshot.Processes.Single(node => node.ProcessId == 100).Id);
        Dispatch();
        Assert.True(connections.IsEffectivelyVisible);
        Assert.Equal("2 made · 0 accepted, as the capture saw them open", connections.Text);
        Assert.Equal("Connections: 2 made · 0 accepted, as the capture saw them open", AutomationProperties.GetName(connections));
        workspace.SelectProcess(workspace.Snapshot.Processes.Single(node => node.ProcessId == 200).Id);
        Dispatch();
        Assert.Equal("0 made · 1 accepted, as the capture saw them open", connections.Text);

        // Beneath them, the processes it talked with: each on a line of its own, marked from its side, over what it carried,
        // and who connected said in words for a screen reader as the keyboard reaches its row.
        ListBox peers = window.GetControl<ListBox>("PeersList");
        Assert.True(peers.IsEffectivelyVisible);
        Assert.Equal("Its relationships, the busiest first", window.GetControl<TextBlock>("PeersHeadingText").Text);
        Assert.Equal(["← client.exe · PID 100", "1 accepted · TCP · 3 records"], Texts(peers));
        Assert.Equal("Its relationships, the busiest first; Enter on one chooses the process at its other end",
            AutomationProperties.GetName(peers));
        Assert.Equal("client.exe · PID 100 connected to it: 1 accepted, TCP, 3 records. Press Enter to choose the process at its "
            + "other end.", AutomationProperties.GetName(peers.ContainerFromIndex(0)!));
        Assert.False(window.GetControl<TextBlock>("PeersMoreText").IsEffectivelyVisible);
        Save(window, "connections-inspector-1456x939.png");

        // Enter on it chooses the client at its other end, whose own relationships the list then holds, the keyboard staying
        // on it, so who connected to whom is walked a key at a time.
        Assert.True(peers.ContainerFromIndex(0)!.Focus());
        Dispatch();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatch();
        Assert.Equal(100, workspace.SelectedProcess?.ProcessId);
        Assert.Equal(["→ server.exe · PID 200", "1 made · TCP · 3 records"], Texts(peers));
        Assert.Same(peers.ContainerFromIndex(0), window.FocusManager?.GetFocusedElement());

        // Esc comes back: the server chosen again, the keyboard on the relationship it was left by; Esc again, with no hop
        // left, climbs the ladder as anywhere, and on the machine's rung lets the choice go.
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatch();
        Assert.Equal(200, workspace.SelectedProcess?.ProcessId);
        Assert.Equal(["← client.exe · PID 100", "1 accepted · TCP · 3 records"], Texts(peers));
        Assert.Same(peers.ContainerFromIndex(0), window.FocusManager?.GetFocusedElement());
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatch();
        Assert.Null(workspace.SelectedProcess);

        // Nothing chosen, nothing said.
        workspace.ClearSelection();
        Dispatch();
        Assert.False(connections.IsEffectivelyVisible);

        // A program chosen names the programs its processes talked with, each pair on a line of its own over what joins it,
        // and a screen reader hears who connected in words.
        ItemsControl programs = window.GetControl<ItemsControl>("ProgramsList");
        Assert.False(programs.IsEffectivelyVisible);
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "client.exe");
        Dispatch();
        Assert.True(programs.IsEffectivelyVisible);
        Assert.Equal("Relationships by program", window.GetControl<TextBlock>("ProgramsHeadingText").Text);
        Assert.Equal(["client.exe → server.exe", "1 relationship · 3 records"],
            programs.GetRealizedContainers().SelectMany(container => container.GetVisualDescendants().OfType<TextBlock>())
                .Select(text => text.Text));
        Assert.Equal("Relationships by program: client.exe connected to server.exe: 1 relationship, 3 records",
            AutomationProperties.GetName(programs));
        Assert.False(window.GetControl<TextBlock>("ProgramsMoreText").IsEffectivelyVisible);
        Assert.False(window.GetControl<ListBox>("PeersList").IsEffectivelyVisible);
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

    /// <summary>A row whose session time is its reading in workspace ticks.</summary>
    private static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    /// <summary>The words a list's rows show, row by row and line by line.</summary>
    private static string[] Texts(ItemsControl list) =>
        [.. list.GetRealizedContainers().SelectMany(container => container.GetVisualDescendants().OfType<TextBlock>())
            .Select(text => text.Text ?? string.Empty)];
}
