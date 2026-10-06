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
/// The inspector's explanation in the window (§6.8): a process's count, or a channel's pairing, one action from the rule
/// behind it - the choice of its row - with what the rule left out or could not see.
/// </summary>
public sealed class ExplanationWindowTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [AvaloniaFact(DisplayName = "§6.8: a process chosen in the ranked table is explained in the inspector, a reused PID's later holder with what it left out")]
    public async Task AChosenProcessIsExplained()
    {
        // PID 100 exits and is created again, and its second holder sends twice: candidates the default policy withholds.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Timed(Lifecycle(10, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" }),
            Timed(Lifecycle(30, ObservationKind.Exit, 100, 2)),
            Timed(Lifecycle(40, ObservationKind.Create, 100, 3) with { ResourceName = @"C:\Tools\client.exe" }),
            Timed(Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 4).Between(ClientEnd, ServerEnd)),
            Timed(Transfer(60, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 5).Between(ClientEnd, ServerEnd)),
        ]);
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        window.ApplyCaptureUpdate(new CaptureUiUpdate(CaptureUiPhase.Complete, "Saved session open", "Saved.",
            SessionPath: session.Path, Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        TextBlock explanation = window.GetControl<TextBlock>("ExplanationText");
        Assert.False(explanation.IsEffectivelyVisible);

        // The client's group opens on its two processes, and the later holder chosen among them is explained beside them.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "client.exe");
        Assert.True(workspace.Descend());
        Dispatch();
        ProcessNode later = workspace.Snapshot.Processes.Single(node => node.PidHolder == 2);
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == later.Id.ToString());
        Dispatch();
        Assert.True(explanation.IsEffectivelyVisible);
        Assert.StartsWith("PID 100 was held by 2 processes in this capture, and this was the 2nd.", explanation.Text,
            StringComparison.Ordinal);
        Assert.Contains("leaving out 2 records bound to it over the session.", explanation.Text, StringComparison.Ordinal);
        Save(window, "binding-1456x939.png");
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§6.8: a channel chosen among a process's rows is explained in the inspector: its rule, whether the capture saw it open and close, and its key")]
    public async Task AChosenChannelIsExplained()
    {
        // The client connects to the server, and the capture sees the connection open and close at both ends.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Timed(Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" }),
            Timed(Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe" }),
            Timed(Transfer(10, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 3).Between(ClientEnd, ServerEnd)),
            Timed(Transfer(11, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 4).Between(ServerEnd, ClientEnd)),
            Timed(Transfer(12, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 5).Between(ClientEnd, ServerEnd)),
            Timed(Transfer(13, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 6).Between(ServerEnd, ClientEnd)),
            Timed(Transfer(14, ObservationKind.Disconnect, AccountingSide.EndpointActivity, 0, 100, 7).Between(ClientEnd, ServerEnd)),
            Timed(Transfer(15, ObservationKind.Disconnect, AccountingSide.EndpointActivity, 0, 200, 8).Between(ServerEnd, ClientEnd)),
        ]);
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        window.ApplyCaptureUpdate(new CaptureUiUpdate(CaptureUiPhase.Complete, "Saved session open", "Saved.",
            SessionPath: session.Path, Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        TextBlock explanation = window.GetControl<TextBlock>("ExplanationText");
        TextBlock heading = Assert.IsType<TextBlock>(Assert.IsType<StackPanel>(explanation.Parent).Children[0]);

        // The client's rung lists its one channel; chosen there, the inspector says how its two ends were paired.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "client.exe");
        Assert.True(workspace.Descend());
        workspace.SelectedRung = workspace.RungRows.Single();
        Assert.True(workspace.Descend());
        Dispatch();
        Assert.Equal("How its records are counted", heading.Text);
        Channel channel = workspace.Snapshot.Channels.Single();
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == channel.Key);
        Dispatch();
        Assert.True(explanation.IsEffectivelyVisible);
        Assert.Equal("How it was paired", heading.Text);
        Assert.StartsWith("Each end's records bind to one process and name the other end: paired by "
            + "transport-endpoint-relation, version 4, correlated. Opened and closed in the capture.", explanation.Text,
            StringComparison.Ordinal);
        Assert.Contains(channel.Key, explanation.Text, StringComparison.Ordinal);
        Save(window, "explanation-channel-1456x939.png");
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
