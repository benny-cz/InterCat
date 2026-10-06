using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
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

    [AvaloniaFact(DisplayName = "§6.8: a group and a process chosen in the ranked table are explained in the inspector, with what a reused PID's later holder left out, each holder numbered")]
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

        // Chosen, the client's group says how it was formed and what its total leaves out of the later holder.
        TextBlock heading = Assert.IsType<TextBlock>(Assert.IsType<StackPanel>(explanation.Parent).Children[0]);
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "client.exe");
        Dispatch();
        Assert.True(explanation.IsEffectivelyVisible);
        Assert.Equal("How its processes are grouped", heading.Text);
        Assert.Contains("its total leaves out 2 records bound to 1 later holder of a reused PID among them.", explanation.Text,
            StringComparison.Ordinal);

        // It opens on its two processes, which read apart as icat processes numbers them: in their rows, their lanes and
        // the inspector, whose explanation of the later one stands beside them.
        Assert.True(workspace.Descend());
        Dispatch();
        ProcessNode later = workspace.Snapshot.Processes.Single(node => node.PidHolder == 2);
        Assert.Equal(["PID 100 #1 · created during capture", "PID 100 #2 · created during capture"],
            workspace.RungRows.Select(row => row.Detail).Order(StringComparer.Ordinal));
        await Until(() => workspace.ProcessLaneDisplay.Count == 2);
        Assert.Equal(["client.exe · PID 100 #1", "client.exe · PID 100 #2"],
            workspace.ProcessLaneDisplay.Select(lane => TimelineView.OwnerLabel(lane, workspace)).Order(StringComparer.Ordinal));
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == later.Id.ToString());
        Dispatch();
        Assert.Equal("PID 100 #2 · created during capture", workspace.SelectionSubtitle);
        Assert.True(explanation.IsEffectivelyVisible);
        Assert.Equal("How its records are counted", heading.Text);
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

    [AvaloniaFact(DisplayName = "§6.8: Count them as candidates counts a later holder's records everywhere, keeps the view, says so in the rail, and one click undoes it")]
    public async Task CandidatesAreCountedOneActionAway()
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
        Button count = window.GetControl<Button>("CountCandidatesButton");
        StackPanel rail = window.GetControl<StackPanel>("EvidencePolicyPanel");

        // The later holder chosen among its group's processes counts its creation alone, and offers what it left out.
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label == "client.exe");
        Assert.True(workspace.Descend());
        ProcessNode later = workspace.Snapshot.Processes.Single(node => node.PidHolder == 2);
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Key == later.Id.ToString());
        Dispatch();
        Assert.Equal("1", workspace.RungRows.Single(row => row.Key == later.Id.ToString()).Observations);
        Assert.True(count.IsEffectivelyVisible);
        Assert.False(rail.IsEffectivelyVisible);
        Save(window, "candidates-offered-1456x939.png");
        Assert.Empty(LegibleTextTests.CutOff(window, "the offer"));
        (window.Width, window.Height) = (window.MinWidth, window.MinHeight);
        Assert.Empty(LegibleTextTests.CutOff(window, "the offer, at the smallest window"));
        (window.Width, window.Height) = (1456, 939);

        // One click counts them: the view keeps its rung and choice, the row its sends, and the rail says what is counted.
        count.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => window.DataContext is WorkspaceViewModel { CountsCandidates: true });
        var counted = (WorkspaceViewModel)window.DataContext!;
        Dispatch();
        Assert.Equal(later.Id, counted.SelectedProcess?.Id);
        Assert.Equal("3", counted.RungRows.Single(row => row.Key == later.Id.ToString()).Observations);
        Assert.Contains("its total includes the 2 records bound to it over the session",
            window.GetControl<TextBlock>("ExplanationText").Text, StringComparison.Ordinal);
        Assert.False(count.IsEffectivelyVisible);
        Assert.True(rail.IsEffectivelyVisible);
        Assert.StartsWith("Counting candidates:", window.GetControl<TextBlock>("EvidencePolicyText").Text, StringComparison.Ordinal);
        Save(window, "candidates-1456x939.png");
        (window.Width, window.Height) = (window.MinWidth, window.MinHeight);
        Assert.Empty(LegibleTextTests.CutOff(window, "counting candidates, at the smallest window"));
        (window.Width, window.Height) = (1456, 939);

        // One more counts correlated evidence only again, and the rail has nothing unusual to say.
        window.GetControl<Button>("CountCorrelatedOnlyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => window.DataContext is WorkspaceViewModel { CountsCandidates: false });
        var restored = (WorkspaceViewModel)window.DataContext!;
        Dispatch();
        Assert.Equal("1", restored.RungRows.Single(row => row.Key == later.Id.ToString()).Observations);
        Assert.False(rail.IsEffectivelyVisible);
        Assert.True(count.IsEffectivelyVisible);

        // The choice is the workspace's: a session opened afterwards, even this one again, counts correlated evidence.
        Assert.True(await window.ChooseEvidencePolicyAsync(EvidencePolicy.IncludeCandidates));
        Assert.True(Assert.IsType<WorkspaceViewModel>(window.DataContext).CountsCandidates);
        Assert.True(await window.OpenSessionAsync(session.Path));
        Assert.False(Assert.IsType<WorkspaceViewModel>(window.DataContext).CountsCandidates);
        window.Close();
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int wait = 0; wait < 500 && !condition(); wait++)
        {
            Dispatch();
            await Task.Delay(10);
        }

        Assert.True(condition());
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
