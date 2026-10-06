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
/// A process's lineage in the window's inspector: who started it and whom it started, with the parent and the
/// children one button away (`entities-v1` §3).
/// </summary>
public sealed class LineageWindowTests
{
    [AvaloniaFact(DisplayName = "§3.2: the inspector states who started a process and whom it started, and Go to parent selects it")]
    public async Task TheInspectorStatesAndFollowsTheLineage()
    {
        using var session = new TemporarySession();
        ObservationRowV1 launcher = Timed(Lifecycle(10, ObservationKind.Create, 400, 1) with { ResourceName = @"C:\Tools\launcher.exe" });
        ObservationRowV1 first = Timed(Lifecycle(20, ObservationKind.Create, 401, 2) with { ResourceName = @"C:\Tools\worker.exe" });
        ObservationRowV1 second = Timed(Lifecycle(30, ObservationKind.Create, 402, 3) with { ResourceName = @"C:\Tools\worker.exe" });
        Publish(session.Store, [launcher, first, second], fields:
        [
            Field(launcher, SourceField.ProcessStartSequence, 800),
            Field(first, SourceField.ProcessStartSequence, 801),
            Field(first, SourceField.ParentProcessId, 400),
            Field(first, SourceField.ParentStartSequence, 800),
            Field(second, SourceField.ProcessStartSequence, 802),
            Field(second, SourceField.ParentProcessId, 400),
            Field(second, SourceField.ParentStartSequence, 800),
        ]);
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        window.ApplyCaptureUpdate(new CaptureUiUpdate(CaptureUiPhase.Recording, "Recording", "A published generation.",
            SessionPath: session.Path, Overview: SessionOverviewProjector.Project(session.Store)));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;

        workspace.SelectProcess(workspace.Snapshot.Processes.Single(node => node.ProcessId == 401).Id);
        Dispatch();
        TextBlock parent = window.GetControl<TextBlock>("ParentText");
        TextBlock children = window.GetControl<TextBlock>("ChildrenText");
        Button goToParent = window.GetControl<Button>("SelectParentButton");
        Button selectChildren = window.GetControl<Button>("SelectChildrenButton");
        Assert.True(parent.IsEffectivelyVisible);
        Assert.Equal("launcher.exe · PID 400 · linked by its start key", parent.Text);
        Assert.Equal("None seen in this capture", children.Text);
        Assert.True(goToParent.IsEffectivelyEnabled);
        Assert.False(selectChildren.IsEffectivelyEnabled);
        Save(window, "lineage-1456x939.png");

        // Go to parent selects the launcher, whose card then names both workers and offers them as a set.
        goToParent.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatch();
        Assert.Equal(400, workspace.SelectedProcess?.ProcessId);
        Assert.Equal("2 processes: worker.exe · PID 401, worker.exe · PID 402", children.Text);
        Assert.True(selectChildren.IsEffectivelyEnabled);
        selectChildren.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatch();
        Assert.Equal([401, 402], workspace.ChosenProcesses.Select(process => process.ProcessId).Order());
        Assert.False(parent.IsEffectivelyVisible);
        window.Close();
    }

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
            Timed(Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 4).Between("127.0.0.1:50000", "127.0.0.1:8080")),
            Timed(Transfer(60, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 5).Between("127.0.0.1:50000", "127.0.0.1:8080")),
        ]);
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        window.ApplyCaptureUpdate(new CaptureUiUpdate(CaptureUiPhase.Complete, "Saved session open", "Saved.",
            SessionPath: session.Path, Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;
        TextBlock explanation = window.GetControl<TextBlock>("BindingText");
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
