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
/// §6.8 in the window: a setting changed from its first-run default is named in the rail, and Restore the default view
/// puts every one back in one command, as each one's own control would, keeping the time and the selected process.
/// </summary>
public sealed class DefaultViewWindowTests
{
    [AvaloniaFact(DisplayName = "§6.8: the rail names each setting changed from its default, and Restore the default view puts them all back, keeping the time and selection")]
    public async Task RestoringTheDefaultViewPutsEverySettingBack()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = TerminalSessions();
        Publish(session.Store, rows, fields: fields);
        var window = new MainWindow { Width = 1_080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var opened = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await opened.LayoutReady;
        Control panel = window.GetControl<Control>("DefaultsPanel");
        Assert.False(panel.IsVisible);
        Assert.False(await window.RestoreDefaultViewAsync());

        // A person ranks by bytes sent per second, reads each lane on its own scale, groups by terminal session and counts
        // candidates, then selects the server and brushes the first half; the rail names every setting changed.
        opened.RankBy = RankingMetric.BytesSent;
        opened.PerSecond = true;
        opened.ScalesEachLane = true;
        Assert.True(window.ChooseGrouping(LaneGrouping.UserSession));
        Assert.True(await window.ChooseEvidencePolicyAsync(EvidencePolicy.IncludeCandidates));
        var changed = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        ProcessNode server = changed.Snapshot.Processes.Single(process => process.ProcessId == 200);
        changed.SelectProcess(server.Id);
        TimeRange extent = changed.Snapshot.Extent;
        var brush = new TimeRange(extent.StartTicks + 1, extent.StartTicks + (extent.SpanTicks / 2));
        changed.SelectInterval(brush);
        await changed.IntervalReady;
        Dispatch();
        Assert.True(panel.IsVisible);
        TextBlock line = window.GetControl<TextBlock>("ChangedSettingsLine");
        Assert.Equal("4 settings changed", line.Text);
        const string Changed = "Not the default view: its processes grouped by terminal session, its rows ranked by bytes sent "
            + "per second, its records counted with candidates and each of its timeline lanes on its own scale.";
        Assert.Equal((Changed, Changed), (ToolTip.GetTip(line), AutomationProperties.GetHelpText(line)));

        // The line and its command share one row of the rail, which keeps its height for the ranked rows.
        Button restore = window.GetControl<Button>("RestoreDefaultsButton");
        Assert.Equal(line.TranslatePoint(new(0, line.Bounds.Height / 2), window)!.Value.Y,
            restore.TranslatePoint(new(0, restore.Bounds.Height / 2), window)!.Value.Y, 1.0);

        // One command puts every setting back, as each one's control would, and the time and the selected process stay.
        restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitFor(() => window.DataContext is WorkspaceViewModel { EvidencePolicy: EvidencePolicy.IncludeCorrelated });
        var restored = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.Equal((RankingMetric.Records, false, false, LaneGrouping.Executable),
            (restored.RankBy, restored.PerSecond, restored.ScalesEachLane, restored.Grouping));
        Assert.Equal((server.Id, brush), (restored.SelectedProcess?.Id, restored.SelectedInterval));
        Dispatch();
        Assert.False(panel.IsVisible);
        Assert.Equal("Executable", window.GetControl<ComboBox>("GroupBySelector").SelectedItem?.ToString());
        Assert.False(await window.RestoreDefaultViewAsync());
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
