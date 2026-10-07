using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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
/// The ranked table's Group by selector in a real window (§6.3): chosen through the control, it regroups the rail's rows
/// and the graph by the terminal session each process's lifecycle records name, from the overview already projected, and
/// keeps what the person had chosen; a live publication keeps the grouping, and a session opened afterwards starts by
/// executable.
/// </summary>
public sealed class GroupingWindowTests
{
    [AvaloniaFact(DisplayName = "§6.3: Group by regroups the rail by terminal session, keeps the selection, ranking and time, and holds for a later publication")]
    public async Task GroupByRegroupsTheRail()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = TerminalSessions();
        Publish(session.Store, rows, fields: fields);
        var window = new MainWindow { Width = 1_080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var byExecutable = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await byExecutable.LayoutReady;
        _ = window.CaptureRenderedFrame();

        // The selector stands beneath the ranking's, its box in line with the ranking's, and says what each grouping means.
        Control row = window.GetControl<Control>("GroupByRow");
        ComboBox selector = window.GetControl<ComboBox>("GroupBySelector");
        ComboBox rankBy = window.GetControl<ComboBox>("RankBySelector");
        Assert.True(row.IsEffectivelyVisible);
        Assert.Equal("Group processes by", AutomationProperties.GetName(selector));
        Assert.Equal(WorkspaceViewModel.GroupingDefinition, AutomationProperties.GetHelpText(selector));
        Assert.Equal(["Executable", "Terminal session"], selector.Items.Select(item => item?.ToString()));
        Assert.Equal("Executable", selector.SelectedItem?.ToString());
        Point rankBox = rankBy.TranslatePoint(default, window)!.Value;
        Point groupBox = selector.TranslatePoint(default, window)!.Value;
        Control perSecond = window.GetControl<Control>("PerSecondToggle");
        Assert.Equal(rankBox.X, groupBox.X, 0.5);
        Assert.Equal(
            perSecond.TranslatePoint(new(perSecond.Bounds.Width, 0), window)!.Value.X,
            selector.TranslatePoint(new(selector.Bounds.Width, 0), window)!.Value.X, 0.5);
        Assert.True(groupBox.Y >= rankBox.Y + rankBy.Bounds.Height);

        // Its options read as sentences, and its value as the grouping's name (R15).
        selector.IsDropDownOpen = true;
        Dispatch();
        for (int index = 0; index < byExecutable.GroupingOptions.Count; index++)
        {
            Control item = Assert.IsAssignableFrom<Control>(selector.ContainerFromIndex(index));
            Assert.Equal(byExecutable.GroupingOptions[index].AccessibleName, AutomationProperties.GetName(item));
        }

        selector.IsDropDownOpen = false;
        Dispatch();
        IValueProvider value = Assert.IsAssignableFrom<IValueProvider>(ControlAutomationPeer.CreatePeerForElement(selector));
        Assert.Equal("Executable", value.Value);

        // A person ranks by peers and selects the server, then groups by terminal session.
        byExecutable.RankBy = RankingMetric.ActivePeers;
        await byExecutable.RankingReady;
        ProcessNode server = byExecutable.Snapshot.Processes.Single(process => process.ProcessId == 200);
        byExecutable.SelectProcess(server.Id);
        selector.SelectedItem = selector.Items.OfType<GroupingOption>().Single(option => option.Grouping == LaneGrouping.UserSession);
        Dispatch();
        var bySession = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.NotSame(byExecutable, bySession);
        Assert.Equal(LaneGrouping.UserSession, bySession.Grouping);
        Assert.Equal("Terminal session", selector.SelectedItem?.ToString());
        Assert.Equal((RankingMetric.ActivePeers, server.Id), (bySession.RankBy, bySession.SelectedProcess?.Id));

        // Each session's peers are its processes' together, as icat metric --group-by session counts them; the processes
        // naming no session resolved none, which is unmeasured rather than zero.
        await bySession.RankingReady;
        Dispatch();
        Assert.Equal(
            [("Terminal session 1", (long?)3), ("Terminal session 0", 2), ("Terminal session 2", 1), ("Terminal session not recorded", null)],
            bySession.RungRows.Select(rung => (rung.Label, rung.Source.Ranked?.Value)));
        Assert.True(row.IsEffectivelyVisible);

        // A session's rung lists its processes, and names the session where a group's name stands; the grouping is not
        // offered there, since its rows are processes, and is as it was on the way back up.
        bySession.SelectedRung = bySession.RungRows.Single(rung => rung.Label == "Terminal session 1");
        Assert.True(bySession.Descend());
        Dispatch();
        Assert.False(row.IsEffectivelyVisible);
        Assert.Equal(["Machine", "Group: Terminal session 1"], bySession.Crumbs.Select(crumb => crumb.Label));
        Assert.Equal(["client.exe", "server.exe"], bySession.RungRows.Select(rung => rung.Label).Order(StringComparer.Ordinal));
        Assert.True(bySession.Ascend());
        Dispatch();
        Assert.True(row.IsEffectivelyVisible);
        Assert.Equal(LaneGrouping.UserSession, bySession.Grouping);

        // Chosen from a session's rung, by the window's own command, the view returns to the machine rung, whose rows are
        // what changed, rather than say its rung went missing from the generation.
        bySession.SelectedRung = bySession.RungRows.Single(rung => rung.Label == "Terminal session 1");
        Assert.True(bySession.Descend());
        string? said = window.GetControl<TextBlock>("CaptureDetail").Text;
        Assert.True(window.ChooseGrouping(LaneGrouping.Executable));
        var fromBelow = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.Equal((LaneGrouping.Executable, "Machine"), (fromBelow.Grouping, Assert.Single(fromBelow.Crumbs).Label));
        Assert.Equal(said, window.GetControl<TextBlock>("CaptureDetail").Text);
        Assert.True(window.ChooseGrouping(LaneGrouping.UserSession));
        bySession = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.Equal(LaneGrouping.UserSession, bySession.Grouping);

        // The next publication is grouped by session too, and a brushed time survives grouping by executable again.
        Publish(session.Store,
        [
            Transfer(60, ObservationKind.Send, AccountingSide.SendSide, 4, 101, 60).Between("127.0.0.1:50001", "127.0.0.1:9090")
                with { SessionRelativeTicks = 6_000 },
        ]);
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var later = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.NotSame(bySession, later);
        Assert.Contains("generation 2", later.Snapshot.Title, StringComparison.Ordinal);
        Assert.Equal(LaneGrouping.UserSession, later.Grouping);
        Assert.Equal("Terminal session", selector.SelectedItem?.ToString());
        TimeRange extent = later.Snapshot.Extent;
        var brush = new TimeRange(extent.StartTicks + 1, extent.StartTicks + (extent.SpanTicks / 2));
        later.SelectInterval(brush);
        await later.IntervalReady;
        selector.SelectedItem = selector.Items.OfType<GroupingOption>().Single(option => option.Grouping == LaneGrouping.Executable);
        Dispatch();
        var again = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.Equal((LaneGrouping.Executable, (TimeRange?)brush), (again.Grouping, again.SelectedInterval));
        Assert.Equal(
            ["client.exe", "server.exe", "service.exe", "tool.exe"],
            again.RungRows.Select(rung => rung.Label).Order(StringComparer.Ordinal));
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§6.3: a session opened afterwards starts grouped by executable, and one naming no terminal session offers no choice")]
    public async Task AnotherSessionStartsByExecutable()
    {
        using var named = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = TerminalSessions();
        Publish(named.Store, rows, fields: fields);
        using var other = new TemporarySession();
        Publish(other.Store, rows, fields: fields);
        using var unnamed = new TemporarySession();
        Publish(unnamed.Store, rows);
        var window = new MainWindow { Width = 1_080, Height = 700 };
        window.Show();
        Assert.True(await window.OpenSessionAsync(named.Path));
        Dispatch();
        Assert.True(window.ChooseGrouping(LaneGrouping.UserSession));
        Assert.False(window.ChooseGrouping(LaneGrouping.UserSession));
        Assert.Equal(LaneGrouping.UserSession, Assert.IsType<WorkspaceViewModel>(window.DataContext).Grouping);

        // Another session is a workspace of its own, grouped by executable until its person chooses.
        Assert.True(await window.OpenSessionAsync(other.Path));
        Dispatch();
        var opened = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.Equal(LaneGrouping.Executable, opened.Grouping);
        Assert.True(window.GetControl<Control>("GroupByRow").IsEffectivelyVisible);

        // A session whose records name no terminal session has one grouping, and the rail no selector.
        Assert.True(await window.OpenSessionAsync(unnamed.Path));
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Assert.False(window.GetControl<Control>("GroupByRow").IsEffectivelyVisible);
        Assert.False(window.ChooseGrouping(LaneGrouping.UserSession));
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§6.3: a publication whose processes name no terminal session is shown by executable, and the choice returns with one that names them")]
    public void APublicationNamingNoSessionIsShownByExecutable()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = TerminalSessions();
        Publish(session.Store, rows, fields: fields);
        var window = new MainWindow { Width = 1_080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        Assert.True(window.ChooseGrouping(LaneGrouping.UserSession));

        // A later publication whose processes name no session has nothing to group by one, so it is shown by executable,
        // with no selector, rather than as one group of processes whose session was not recorded.
        Publish(session.Store, [Later(60)]);
        SessionOverviewBundle second = SessionOverviewProjector.Project(session.Store);
        SessionOverviewBundle unnamed = second with { Nodes = [.. second.Nodes.Select(node => node with { TerminalSession = null })] };
        window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "Recording", "A published generation.", SessionPath: session.Path,
            Overview: unnamed));
        Dispatch();
        var shown = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.Contains("generation 2", shown.Snapshot.Title, StringComparison.Ordinal);
        Assert.Equal(LaneGrouping.Executable, shown.Grouping);
        Assert.False(window.GetControl<Control>("GroupByRow").IsEffectivelyVisible);

        // The person's choice stands: the next publication that names sessions is grouped by them again.
        Publish(session.Store, [Later(70)]);
        window.ApplyCaptureUpdate(Update(session));
        Dispatch();
        var third = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        Assert.Contains("generation 3", third.Snapshot.Title, StringComparison.Ordinal);
        Assert.Equal(LaneGrouping.UserSession, third.Grouping);
        window.Close();

        static ObservationRowV1 Later(long ticks) =>
            Transfer(ticks, ObservationKind.Send, AccountingSide.SendSide, 4, 101, (ulong)ticks).Between("127.0.0.1:50001", "127.0.0.1:9090")
                with { SessionRelativeTicks = ticks * 100 };
    }

    private static CaptureUiUpdate Update(TemporarySession session) => new(
        CaptureUiPhase.Recording, "Recording", "A published generation.", SessionPath: session.Path,
        Overview: SessionOverviewProjector.Project(session.Store));

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
