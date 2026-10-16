using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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
        Save(window, "connections-inspector-1456x939.png");

        // Nothing chosen, nothing said.
        workspace.ClearSelection();
        Dispatch();
        Assert.False(connections.IsEffectivelyVisible);
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
}
