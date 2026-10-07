using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
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
/// §19.5's visible view filter in the window: the rail names InterCat's own processes beside one command that sets them
/// aside from the rows, the graph and the channels, counts them while they are, and shows them again; the setting is
/// named as changed, and Restore defaults shows them.
/// </summary>
public sealed class CollectorSetAsideWindowTests
{
    [AvaloniaFact(DisplayName = "§19.5: the rail's command sets InterCat's own processes aside and shows them again, and Restore defaults shows them")]
    public async Task TheRailSetsInterCatsOwnAsideAndShowsThemAgain()
    {
        using var session = new TemporarySession();
        PublishCollected(session.Store);
        var window = new MainWindow { Width = 1_080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var opened = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await opened.LayoutReady;
        Control panel = window.GetControl<Control>("CollectorsPanel");
        TextBlock line = window.GetControl<TextBlock>("CollectorsLineText");
        Button command = window.GetControl<Button>("CollectorsAsideButton");
        Assert.True(panel.IsVisible);
        Assert.Equal(("InterCat's own: intercat-broker.exe · PID 4120 #1", "Set aside"), (line.Text, command.Content as string));
        Assert.Equal((opened.CollectorsText, opened.CollectorsText), (ToolTip.GetTip(line), AutomationProperties.GetHelpText(line)));
        Assert.False(window.GetControl<Control>("DefaultsPanel").IsVisible);

        // The line and its command share one row of the rail, which keeps its height for the ranked rows.
        Assert.Equal(line.TranslatePoint(new(0, line.Bounds.Height / 2), window)!.Value.Y,
            command.TranslatePoint(new(0, command.Bounds.Height / 2), window)!.Value.Y, 1.0);

        // The broker is selected; set aside, it leaves the rows and is no longer the selection, and the rail counts it.
        ProcessNode broker = opened.Snapshot.Processes.Single(process => process.Collector == CollectorRole.Broker);
        opened.SelectProcess(broker.Id);
        command.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatch();
        var aside = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.NotSame(opened, aside);
        await aside.LayoutReady;
        Assert.True(aside.CollectorsSetAside);
        Assert.Null(aside.SelectedProcess);
        Assert.DoesNotContain(aside.RungRows, row => row.Label == "intercat-broker.exe");
        Assert.DoesNotContain(aside.Snapshot.Processes, process => process.Id == broker.Id);
        Dispatch();
        Assert.Equal(("InterCat's own set aside: 1 process, 3 records", "Show them"), (line.Text, command.Content as string));
        Assert.True(window.GetControl<Control>("DefaultsPanel").IsVisible);
        Assert.Equal("1 setting changed", window.GetControl<TextBlock>("ChangedSettingsLine").Text);
        Assert.False(window.ChooseCollectorsAside(true));
        // The broker was set aside, not lost, so nothing says it is missing from the generation.
        Assert.DoesNotContain("not in this generation", window.GetControl<TextBlock>("CaptureDetail").Text,
            StringComparison.Ordinal);

        // Its reads leave it out of what a ranking totals over the rows shown: only the client has a peer among them.
        aside.RankBy = RankingMetric.ActivePeers;
        await aside.RankingReady;
        Assert.Equal("1 process has a peer", aside.RankingNote);
        aside.RankBy = RankingMetric.Records;

        // Showing them again puts the broker back among the rows.
        command.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatch();
        var shown = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.False(shown.CollectorsSetAside);
        Assert.Contains(shown.RungRows, row => row.Label == "intercat-broker.exe");
        Assert.Equal("Set aside", command.Content as string);

        // Set aside again, Restore defaults shows them, as every other setting it puts back.
        Assert.True(window.ChooseCollectorsAside(true));
        Dispatch();
        window.GetControl<Button>("RestoreDefaultsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitFor(() => window.DataContext is WorkspaceViewModel { CollectorsSetAside: false });
        Dispatch();
        Assert.False(window.GetControl<Control>("DefaultsPanel").IsVisible);
        Assert.Contains(Assert.IsType<WorkspaceViewModel>(window.DataContext).RungRows, row => row.Label == "intercat-broker.exe");

        // Another session opens with its own processes shown, whatever the last one's choice was.
        Assert.True(window.ChooseCollectorsAside(true));
        using var other = new TemporarySession();
        PublishCollected(other.Store);
        Assert.True(await window.OpenSessionAsync(other.Path));
        Dispatch();
        var another = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.False(another.CollectorsSetAside);
        Assert.Contains(another.RungRows, row => row.Label == "intercat-broker.exe");

        // A capture naming none of InterCat's own has no line to show, and nothing to set aside.
        using var unnamed = new TemporarySession();
        PublishCollected(unnamed.Store, named: false);
        Assert.True(await window.OpenSessionAsync(unnamed.Path));
        Dispatch();
        Assert.False(panel.IsVisible);
        Assert.False(window.ChooseCollectorsAside(true));
        window.Close();
    }

    private static CaptureUiUpdate Update(TemporarySession session) => new(
        CaptureUiPhase.Recording, "Recording", "A published generation.", SessionPath: session.Path,
        Overview: SessionOverviewProjector.Project(session.Store));

    private static void WaitFor(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            Dispatch();
            Thread.Sleep(10);
        }

        Assert.True(condition());
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
