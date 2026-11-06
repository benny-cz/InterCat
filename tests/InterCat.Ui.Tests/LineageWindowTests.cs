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

    [AvaloniaFact(DisplayName = "R3: the inspector states the terminal session and the package a process ran in beside its lineage, and a process in no package as in none")]
    public async Task TheInspectorStatesWhatAProcessRanIn()
    {
        const string Package = "Microsoft.DesktopAppInstaller_1.29.250.0_x64__8wekyb3d8bbwe";
        using var session = new TemporarySession();
        ObservationRowV1 winget = Timed(Lifecycle(10, ObservationKind.Create, 400, 1) with { ResourceName = @"C:\Program Files\WindowsApps\winget.exe" });
        ObservationRowV1 child = Timed(Lifecycle(20, ObservationKind.Create, 401, 2) with { ResourceName = @"C:\Tools\child.exe" });
        Publish(session.Store, [winget, child], fields:
        [
            Field(winget, SourceField.ProcessSessionId, 1),
            Field(winget, SourceField.ProcessPackageFullName, 0) with { Value = null, Text = Package },
            Field(child, SourceField.ProcessSessionId, 1),
            Field(child, SourceField.ProcessPackageFullName, 0),
        ]);
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        window.ApplyCaptureUpdate(new CaptureUiUpdate(CaptureUiPhase.Recording, "Recording", "A published generation.",
            SessionPath: session.Path, Overview: SessionOverviewProjector.Project(session.Store)));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;

        workspace.SelectProcess(workspace.Snapshot.Processes.Single(node => node.ProcessId == 400).Id);
        Dispatch();
        TextBlock terminalSession = window.GetControl<TextBlock>("TerminalSessionText");
        TextBlock package = window.GetControl<TextBlock>("PackageText");
        Assert.True(terminalSession.IsEffectivelyVisible && package.IsEffectivelyVisible);
        Assert.Equal<(string?, string?)>(("1", Package), (terminalSession.Text, package.Text));

        // The rows are below the card's fold at this height: the inspector is scrolled to them for the render a person reads.
        package.BringIntoView();
        Save(window, "process-package-1456x939.png");
        workspace.SelectProcess(workspace.Snapshot.Processes.Single(node => node.ProcessId == 401).Id);
        Dispatch();
        Assert.Equal("None: its lifecycle record names no package it ran in", package.Text);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R3: the inspector states the services a process hosted as its capture started and stopped beside its package")]
    public async Task TheInspectorStatesAProcesssServices()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields, ServiceInventoryV1[] inventories) = ServiceHosts();
        Publish(session.Store, rows, fields: fields, services: inventories);
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        window.ApplyCaptureUpdate(new CaptureUiUpdate(CaptureUiPhase.Recording, "Recording", "A published generation.",
            SessionPath: session.Path, Overview: SessionOverviewProjector.Project(session.Store)));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;

        workspace.SelectProcess(workspace.Snapshot.Processes.Single(node => node.ProcessId == 2400).Id);
        Dispatch();
        TextBlock services = window.GetControl<TextBlock>("ServicesText");
        Assert.True(services.IsEffectivelyVisible);
        Assert.Equal("W32Time, BITS (at the stop alone) · as the capture started and stopped", services.Text);

        // The row is below the card's fold at this height: the inspector is scrolled to it for the render a person reads.
        services.BringIntoView();
        Save(window, "process-services-1456x939.png");
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
