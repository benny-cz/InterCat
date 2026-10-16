using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using InterCat.Application;
using InterCat.Capture.Journal.Tests;
using InterCat.Desktop;
using InterCat.Desktop.Theme;
using InterCat.Desktop.Presentation;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// An investigation in the window (ADR-038, M4): its sessions where they stand, their hosts and their time, a moved one
/// relinked, and a member opened in InterCat's own window.
/// </summary>
public sealed class InvestigationWindowTests
{
    [AvaloniaFact(DisplayName = "R22: an investigation window lists its sessions where they stand, relinks one that moved, and opens one")]
    public async Task AnInvestigationWindowListsItsSessions()
    {
        using var root = new TemporaryDirectory();
        string alpha = Session(root.Path, "alpha");
        string beta = Session(root.Path, "beta");
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        Guid a = InvestigationWorkspace.Add(workspace, alpha, Committed).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, beta, Committed).SessionId;
        InvestigationWorkspace.Align(workspace, b, 2_000_000_000, a, 5_000_000_000, 500_000, 50, null, Committed);
        string moved = Path.Combine(root.Path, "archive", "beta");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        Directory.Move(beta, moved);

        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            // The rail offers it below the saved sessions, whole at the minimum window.
            AssertInside(main.GetControl<Button>("InvestigationButton"), main.GetControl<Control>("Rail"), main);

            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            InvestigationView view = window.View!;
            CultureInfo culture = CultureInfo.CurrentCulture;
            Assert.Equal([$"Session {Short(a)}, present", $"Session {Short(b)}, missing"], view.Members.Select(row => row.Title));
            Assert.Equal("Its clock is the investigation's time", view.Members[0].Time);
            Assert.Equal($"Aligned by a person: its {2m.ToString("0.000######", culture)} s is the reference's "
                + $"{5m.ToString("0.000######", culture)} s, within ±{500m.ToString("N0", culture)} µs, drifting at most 50 ppm",
                view.Members[1].Time);
            Assert.Contains("moved or removed", view.Members[1].Reason, StringComparison.Ordinal);
            Assert.False(view.Members[1].HoldsItsCapture);
            Assert.StartsWith("1 session is not where it was last found", Named<TextBlock>(window, "Investigation status").Text,
                StringComparison.Ordinal);

            // Each session is named for the ear in a sentence, never by its record's fields.
            ListBox list = Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(view.Members[1].AccessibleName, AutomationProperties.GetName(list.ContainerFromIndex(1)!));
            Assert.EndsWith("moved or removed. " + InvestigationWorkspace.RelinkWhereItIs, view.Members[1].AccessibleName,
                StringComparison.Ordinal);
            Save(window, "investigation-window.png");

            // A moved session is relinked where it is now; a folder that holds no session is refused, and says why.
            list.SelectedIndex = 1;
            Assert.False(Named<Button>(window, "Open in InterCat").IsEnabled);
            await window.RelinkAsync(b, moved);
            WaitFor(() => window.View!.Members[1].State == WorkspaceMemberState.Present);
            Directory.CreateDirectory(Path.Combine(root.Path, "empty"));
            await window.AddAsync([Path.Combine(root.Path, "empty")]);
            WaitFor(() => Named<TextBlock>(window, "Investigation status").Text?.Contains("was not added", StringComparison.Ordinal) == true);

            // A member opens in InterCat's own window, as a saved session would.
            list.SelectedIndex = 0;
            Button open = Named<Button>(window, "Open in InterCat");
            Assert.True(open.IsEnabled);
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => main.GetControl<TextBlock>("CaptureSessionPath").Text == alpha);
            Assert.Equal("Saved session open", main.GetControl<TextBlock>("CaptureStatus").Text);
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§8.3: a session opened from its investigation names its hosts' candidate other ends in Other hosts' list, read again under another evidence policy; opened on its own, it names none")]
    public async Task ASessionOpenedFromItsInvestigationNamesItsHostsCandidateOtherEnds()
    {
        using var root = new TemporaryDirectory();
        DemoInvestigationResult demo = DemoInvestigation.Create(Path.Combine(root.Path, "demo"));
        const string Candidates = " · candidate other ends: demo server: api.exe · PID 5100 and files.exe · PID 5300";
        var main = new MainWindow { Width = 1440, Height = 900 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(demo.WorkspacePath);
            WaitFor(() => window.View is not null);
            ListBox list = Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one");
            list.SelectedIndex = window.View!.Members.ToList().FindIndex(member =>
                member.FullPath.EndsWith(DemoInvestigation.ClientFolder, StringComparison.Ordinal));
            Named<Button>(window, "Open in InterCat").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => main.GetControl<TextBlock>("CaptureSessionPath").Text == demo.ClientSession);

            // Its hosts' candidate other ends are read in the background, and Other hosts' list names them where it lists
            // their host, wrapped whole within the inspector.
            var shown = (WorkspaceViewModel)main.DataContext!;
            await shown.LayoutReady;
            await shown.CandidateHostsReady;
            shown.SelectGraphNode(shown.GraphDisplay.OtherHosts!.Key);
            ListBox hosts = main.GetControl<ListBox>("HostList");
            TextBlock server = HostDetail(hosts, "10.0.0.20");
            Assert.EndsWith(Candidates, server.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("candidate", HostDetail(hosts, "10.0.0.53").Text, StringComparison.Ordinal);
            Save(main, "inspector-hosts-candidates.png");
            Assert.True(server.TextLayout.Width <= server.Bounds.Width + 0.5,
                $"The host's {server.TextLayout.Width} px run past its {server.Bounds.Width} px.");

            // At the window's first size both hosts are listed above the inspector's fold, the candidates beside the first,
            // and what they are said beneath them.
            ScrollViewer inspector = main.GetControl<Border>("Inspector").GetVisualDescendants().OfType<ScrollViewer>().First();
            foreach (HostRow row in hosts.Items.Cast<HostRow>())
            {
                Control container = hosts.ContainerFromItem(row)!;
                double bottom = container.TranslatePoint(new Point(0, container.Bounds.Height), inspector)!.Value.Y;
                Assert.True(bottom <= inspector.Bounds.Height,
                    $"{row.Address} ends {bottom} px down, below the inspector's {inspector.Bounds.Height} px.");
            }

            Assert.StartsWith("The candidate other ends named for 1 of its 2 hosts are ", main.GetControl<TextBlock>("HostsNoteText").Text,
                StringComparison.Ordinal);

            // Counted under another evidence policy, they are read again under it, and named again.
            Assert.True(await main.ChooseEvidencePolicyAsync(EvidencePolicy.IncludeCandidates));
            shown = (WorkspaceViewModel)main.DataContext!;
            await shown.CandidateHostsReady;
            Assert.Equal(shown.GraphDisplay.OtherHosts!.Key, shown.SelectedGraphNodeKey);
            Assert.EndsWith(Candidates, HostDetail(hosts, "10.0.0.20").Text, StringComparison.Ordinal);
            window.Close();

            // Opened on its own, the session names none.
            Assert.True(await main.OpenSessionAsync(demo.ClientSession));
            shown = (WorkspaceViewModel)main.DataContext!;
            await shown.LayoutReady;
            await shown.CandidateHostsReady;
            shown.SelectGraphNode(shown.GraphDisplay.OtherHosts!.Key);
            Assert.DoesNotContain("candidate", HostDetail(hosts, "10.0.0.20").Text, StringComparison.Ordinal);
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R22: each session opened says its own notice once, though it was published in the same generation as the one shown before it")]
    public async Task EachSessionOpenedSaysItsOwnNoticeOnce()
    {
        using var root = new TemporaryDirectory();
        DemoInvestigationResult demo = DemoInvestigation.Create(Path.Combine(root.Path, "demo"));
        const string Kept = "Its pins and view settings are kept in the investigation demo.icat-workspace.";
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(demo.WorkspacePath);
            WaitFor(() => window.View is not null);
            ListBox list = Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one");
            TextBlock detail = main.GetControl<TextBlock>("CaptureDetail");

            // The demo's two sessions were both published in their second generation, so each opens in the same words, and
            // each says once that its investigation keeps its pins.
            foreach (string session in new[] { demo.ClientSession, demo.ServerSession })
            {
                list.SelectedIndex = window.View!.Members.ToList().FindIndex(member =>
                    Path.GetFileName(member.FullPath) == Path.GetFileName(session));
                Named<Button>(window, "Open in InterCat").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                WaitFor(() => main.GetControl<TextBlock>("CaptureSessionPath").Text == session);
                Assert.Equal("Published generation 2. " + Kept, detail.Text);
            }

            window.Close();

            // Opened on its own, a session in the same words says nothing of the investigation it was last opened from.
            Assert.True(await main.OpenSessionAsync(demo.ClientSession));
            Assert.Equal("Published generation 2.", detail.Text);
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R22: a session opened from an investigation keeps its pins, ranking, evidence policy and lane scale there, which its window lists, and gets them back when opened from it again")]
    public async Task AnInvestigationKeepsASessionsPins()
    {
        using var root = new TemporaryDirectory();
        string paired = PairedSession(root.Path, "paired");
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        Guid a = InvestigationWorkspace.Add(workspace, paired, Committed).SessionId;
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            ListBox list = Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one");
            list.SelectedIndex = 0;
            Button open = Named<Button>(window, "Open in InterCat");
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => main.GetControl<TextBlock>("CaptureSessionPath").Text == paired);
            Assert.EndsWith("Its pins and view settings are kept in the investigation case.icat-workspace.",
                main.GetControl<TextBlock>("CaptureDetail").Text, StringComparison.Ordinal);
            Assert.Null(window.View!.Members[0].Kept);

            // A node pinned on its graph is kept in the investigation, where it was put.
            var shown = (WorkspaceViewModel)main.DataContext!;
            await shown.LayoutReady;
            string key = shown.GraphDisplay.Nodes[0].Key;
            var place = new GraphPoint(0.25, 0.75);
            Assert.True(shown.PinGraphNode(key, place));
            await main.InvestigationWritten;
            WorkspacePin kept = Assert.Single(InvestigationWorkspace.LayoutOf(InvestigationWorkspace.Read(workspace), a)!.Pins);
            Assert.Equal((key, 0.25, 0.75), (kept.Key, kept.X, kept.Y));

            // The investigation's window says so in the session's row as it is written, without being read again.
            const string Pinned = "Opens with 1 node pinned on its graph, as it was left here.";
            WaitFor(() => window.View!.Members[0].Kept == Pinned);
            Assert.Equal(Pinned, KeptLine(list).Text);
            Assert.Contains(Pinned, AutomationProperties.GetName(list.ContainerFromIndex(0)!), StringComparison.Ordinal);
            Assert.Same(window.View!.Members[0], list.SelectedItem);

            // So is what its rows are ranked by, and whether per second (§26.3's sort), each as it is chosen.
            shown.RankBy = RankingMetric.BytesSent;
            await main.InvestigationWritten;
            WorkspaceLayout ranked = InvestigationWorkspace.LayoutOf(InvestigationWorkspace.Read(workspace), a)!;
            Assert.Equal((RankingMetric.BytesSent, false, 1), (ranked.RankBy, ranked.PerSecond, ranked.Pins.Count));
            shown.PerSecond = true;
            await main.InvestigationWritten;
            Assert.True(InvestigationWorkspace.LayoutOf(InvestigationWorkspace.Read(workspace), a)!.PerSecond);

            // And so is counting a reused PID's candidates (§6.8), which projects the session again and keeps its layout.
            Assert.True(await main.ChooseEvidencePolicyAsync(EvidencePolicy.IncludeCandidates));
            await main.InvestigationWritten;
            var counting = (WorkspaceViewModel)main.DataContext!;
            Assert.Equal((EvidencePolicy.IncludeCandidates, true, RankingMetric.BytesSent),
                (counting.EvidencePolicy, counting.IsGraphNodePinned(key), counting.RankBy));
            WorkspaceLayout candidates = InvestigationWorkspace.LayoutOf(InvestigationWorkspace.Read(workspace), a)!;
            Assert.Equal((EvidencePolicy?)EvidencePolicy.IncludeCandidates, candidates.EvidencePolicy);
            Assert.Single(candidates.Pins);

            // And so is reading each timeline lane on its own scale (§6.2).
            counting.ScalesEachLane = true;
            await main.InvestigationWritten;
            Assert.True(InvestigationWorkspace.LayoutOf(InvestigationWorkspace.Read(workspace), a)!.ScalesEachLane);

            // The row lists all it keeps, as the investigation read again says it.
            const string Everything = "Opens with 1 node pinned on its graph, its rows ranked by bytes sent per second, its records "
                + "counted with candidates and each of its timeline lanes on its own scale, as it was left here.";
            WaitFor(() => window.View!.Members[0].Kept == Everything);
            Assert.Equal(Everything, KeptLine(list).Text);
            Save(window, "investigation-window-kept.png");
            await window.RefreshAsync();
            Assert.Equal(Everything, window.View!.Members[0].Kept);

            // Opened on its own, the session holds none of the investigation's pins, ranks by records, and counts correlated
            // evidence.
            Assert.True(await main.OpenSessionAsync(paired));
            var alone = (WorkspaceViewModel)main.DataContext!;
            Assert.False(alone.IsGraphNodePinned(key));
            Assert.Equal((RankingMetric.Records, false, EvidencePolicy.IncludeCorrelated, false),
                (alone.RankBy, alone.PerSecond, alone.EvidencePolicy, alone.ScalesEachLane));
            Assert.False(main.GetControl<StackPanel>("EvidencePolicyPanel").IsVisible);

            // Opened from the investigation again, its node is pinned where it was put, and its rows ranked as they were.
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => ((WorkspaceViewModel)main.DataContext!).IsGraphNodePinned(key));
            var again = (WorkspaceViewModel)main.DataContext!;
            Assert.Equal(place, again.GraphPins[key]);
            Assert.Equal((RankingMetric.BytesSent, true, EvidencePolicy.IncludeCandidates, true),
                (again.RankBy, again.PerSecond, again.EvidencePolicy, again.ScalesEachLane));
            Assert.Contains("which put back 1 node pinned on its graph, its rows ranked by bytes sent per second, its records "
                + "counted with candidates and each of its timeline lanes on its own scale.", main.GetControl<TextBlock>("CaptureDetail").Text,
                StringComparison.Ordinal);
            Assert.True(main.GetControl<StackPanel>("EvidencePolicyPanel").IsVisible);

            // Released, the pin is gone and the ranking kept; ranked by records again, the investigation keeps no layout of it.
            Assert.True(again.UnpinGraphNode(key));
            await main.InvestigationWritten;
            WorkspaceLayout released = InvestigationWorkspace.LayoutOf(InvestigationWorkspace.Read(workspace), a)!;
            Assert.Equal((RankingMetric.BytesSent, 0), (released.RankBy, released.Pins.Count));
            again.RankBy = RankingMetric.Records;
            again.PerSecond = false;
            await main.InvestigationWritten;
            Assert.Equal((EvidencePolicy?)EvidencePolicy.IncludeCandidates,
                InvestigationWorkspace.LayoutOf(InvestigationWorkspace.Read(workspace), a)!.EvidencePolicy);
            Assert.True(await main.ChooseEvidencePolicyAsync(EvidencePolicy.IncludeCorrelated));
            await main.InvestigationWritten;
            Assert.True(InvestigationWorkspace.LayoutOf(InvestigationWorkspace.Read(workspace), a)!.ScalesEachLane);
            ((WorkspaceViewModel)main.DataContext!).ScalesEachLane = false;
            await main.InvestigationWritten;
            Assert.Null(InvestigationWorkspace.LayoutOf(InvestigationWorkspace.Read(workspace), a));

            // Keeping nothing, its row says nothing of it.
            WaitFor(() => window.View!.Members[0].Kept is null);
            Assert.False(KeptLine(list, visible: false).IsVisible);

            // A layout written while the investigation is being read again is not lost to a reading that began before it.
            Task reading = window.RefreshAsync();
            window.LayoutKept(workspace, a, new WorkspaceLayout { SessionId = a, Pins = [], ScalesEachLane = true, UpdatedUtc = Committed });
            await reading;
            Assert.Equal("Opens with each of its timeline lanes on its own scale, as it was left here.", window.View!.Members[0].Kept);

            // Only over that reading: the next one says what the file holds, which here was never written.
            await window.RefreshAsync();
            Assert.Null(window.View!.Members[0].Kept);
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§6.8: Restore defaults in a session opened from its investigation keeps the defaults there, and its pins")]
    public async Task RestoringTheDefaultViewKeepsTheDefaultsInTheInvestigation()
    {
        using var root = new TemporaryDirectory();
        string paired = PairedSession(root.Path, "paired");
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        Guid a = InvestigationWorkspace.Add(workspace, paired, Committed).SessionId;
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one").SelectedIndex = 0;
            Button open = Named<Button>(window, "Open in InterCat");
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => main.GetControl<TextBlock>("CaptureSessionPath").Text == paired);

            // A pinned node, a ranking by bytes sent per second, candidates counted and each lane on its own scale are kept.
            var shown = (WorkspaceViewModel)main.DataContext!;
            await shown.LayoutReady;
            string key = shown.GraphDisplay.Nodes[0].Key;
            Assert.True(shown.PinGraphNode(key, new GraphPoint(0.25, 0.75)));
            shown.RankBy = RankingMetric.BytesSent;
            shown.PerSecond = true;
            shown.ScalesEachLane = true;
            Assert.True(await main.ChooseEvidencePolicyAsync(EvidencePolicy.IncludeCandidates));
            await main.InvestigationWritten;
            WorkspaceLayout changed = InvestigationWorkspace.LayoutOf(InvestigationWorkspace.Read(workspace), a)!;
            Assert.Equal((RankingMetric?)RankingMetric.BytesSent, changed.RankBy);
            Assert.Equal((EvidencePolicy?)EvidencePolicy.IncludeCandidates, changed.EvidencePolicy);

            // Restored, the investigation keeps the pin alone, and opened from it again the session shows the defaults.
            Assert.True(await main.RestoreDefaultViewAsync());
            await main.InvestigationWritten;
            WorkspaceLayout restored = InvestigationWorkspace.LayoutOf(InvestigationWorkspace.Read(workspace), a)!;
            Assert.Equal((null, false, null, false, 1), (restored.RankBy, restored.PerSecond, restored.EvidencePolicy,
                restored.ScalesEachLane, restored.Pins.Count));
            Assert.True(await main.OpenSessionAsync(paired));
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => ((WorkspaceViewModel)main.DataContext!).IsGraphNodePinned(key));
            var again = (WorkspaceViewModel)main.DataContext!;
            Assert.Equal((RankingMetric.Records, false, EvidencePolicy.IncludeCorrelated, false, false),
                (again.RankBy, again.PerSecond, again.EvidencePolicy, again.ScalesEachLane, again.DiffersFromDefaults));
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R22: an investigation keeps the window's panes as a person left them, puts them back for any of its sessions, and its window says so")]
    public async Task AnInvestigationKeepsTheWindowsPanes()
    {
        using var root = new TemporaryDirectory();
        string alpha = PairedSession(root.Path, "alpha");
        string beta = PairedSession(root.Path, "beta");
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        InvestigationWorkspace.Add(workspace, alpha, Committed);
        InvestigationWorkspace.Add(workspace, beta, Committed);
        static (double, WorkspacePane?) Kept(WorkspacePanes? panes) => (panes!.GraphShare, panes.Expanded);
        static string Said(WorkspacePanes panes) => InvestigationRows.PanesKept(panes, CultureInfo.CurrentCulture)!;
        var main = new MainWindow { Width = 1456, Height = 939 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            ListBox list = Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one");
            Button open = Named<Button>(window, "Open in InterCat");
            list.SelectedIndex = 0;
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => main.GetControl<TextBlock>("CaptureSessionPath").Text == alpha);
            Assert.Null(window.View!.Panes);
            Assert.Null(PanesLine(window));

            // The person drags the split towards the graph, giving the timeline more of the column: nothing is written while
            // the splitter is held, and the graph's share as drawn is kept once it is let go.
            Border graphPane = main.GetControl<Border>("GraphPane");
            Border timelinePane = main.GetControl<Border>("TimelinePane");
            GridSplitter splitter = main.GetControl<GridSplitter>("PaneSplitter");
            double Drawn() => graphPane.Bounds.Height / (graphPane.Bounds.Height + timelinePane.Bounds.Height);
            Render(main);
            Point grip = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), main)!.Value;
            main.MouseDown(grip, MouseButton.Left);
            main.MouseMove(grip + new Vector(0, -60));
            main.MouseMove(grip + new Vector(0, -120));
            await main.InvestigationWritten;
            Assert.Null(InvestigationWorkspace.Read(workspace).Panes);
            main.MouseUp(grip + new Vector(0, -120), MouseButton.Left);
            await main.InvestigationWritten;
            Render(main);
            WorkspacePanes split = InvestigationWorkspace.Read(workspace).Panes!;
            Assert.Null(split.Expanded);
            Assert.Equal(Drawn(), split.GraphShare, 0.005);
            Assert.True(split.GraphShare < 0.45, $"The graph kept {split.GraphShare} of the panes' height.");

            // The investigation's window says so beneath its sessions as it is written, without being read again.
            WaitFor(() => window.View!.Panes == Said(split));
            Assert.StartsWith("Its sessions open with the graph at ", window.View!.Panes, StringComparison.Ordinal);
            Assert.True(PanesLine(window)!.IsEffectivelyVisible);

            // The timeline fills the column at the person's command, kept with the split the two return to.
            ToggleButton timelineExpand = main.GetControl<ToggleButton>("TimelineExpandToggle");
            timelineExpand.IsChecked = true;
            await main.InvestigationWritten;
            WorkspacePanes filling = InvestigationWorkspace.Read(workspace).Panes!;
            Assert.Equal((split.GraphShare, (WorkspacePane?)WorkspacePane.Timeline), Kept(filling));
            WaitFor(() => window.View!.Panes == Said(filling));
            Assert.Contains("the timeline filling the column and the graph at ", window.View!.Panes, StringComparison.Ordinal);

            // A session opened on its own leaves the panes as they are, and what is done with them there is not kept.
            Assert.True(await main.OpenSessionAsync(beta));
            Assert.Equal(MainPane.Timeline, main.ExpandedPane);
            timelineExpand.IsChecked = false;
            await main.InvestigationWritten;
            Assert.Equal(Kept(filling), Kept(InvestigationWorkspace.Read(workspace).Panes));

            // Any of its sessions opened from it again puts them back as they were left there, and its notice says so.
            list.SelectedIndex = 1;
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => main.ExpandedPane == MainPane.Timeline);
            Assert.False(graphPane.IsEffectivelyVisible);
            Assert.True(timelineExpand.IsChecked);
            Assert.EndsWith("Its pins and view settings are kept in the investigation case.icat-workspace, which put back "
                + filling.Describe(CultureInfo.CurrentCulture) + ".", main.GetControl<TextBlock>("CaptureDetail").Text,
                StringComparison.Ordinal);

            // F11 gives both their places back, at the split the person left there, and the panes are kept so.
            main.KeyPressQwerty(PhysicalKey.F11, RawInputModifiers.None);
            Render(main);
            Assert.Null(main.ExpandedPane);
            Assert.Equal(split.GraphShare, Drawn(), 0.005);
            await main.InvestigationWritten;
            Assert.Equal((split.GraphShare, (WorkspacePane?)null), Kept(InvestigationWorkspace.Read(workspace).Panes));

            // A key on the splitter moves the split, which is kept as it moves.
            splitter.Focus();
            main.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            Render(main);
            await main.InvestigationWritten;
            WorkspacePanes stepped = InvestigationWorkspace.Read(workspace).Panes!;
            Assert.True(stepped.GraphShare > split.GraphShare, $"A key moved the split from {split.GraphShare} to {stepped.GraphShare}.");
            Assert.Equal(Drawn(), stepped.GraphShare, 0.005);

            // No split hides a pane: dragged as far as it goes either way, each keeps its least height, which only letting the
            // other fill the column takes away.
            foreach (double toward in new[] { -1, 1 })
            {
                grip = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), main)!.Value;
                main.MouseDown(grip, MouseButton.Left);
                main.MouseMove(grip + new Vector(0, toward * 400));
                main.MouseMove(new Point(grip.X, toward < 0 ? 1 : main.Bounds.Height - 1));
                main.MouseUp(new Point(grip.X, toward < 0 ? 1 : main.Bounds.Height - 1), MouseButton.Left);
                Render(main);
                Assert.True(graphPane.Bounds.Height >= 159.5, $"The graph was dragged to {graphPane.Bounds.Height:0.#} px.");
                Assert.True(timelinePane.Bounds.Height >= 179.5, $"The timeline was dragged to {timelinePane.Bounds.Height:0.#} px.");
            }

            await main.InvestigationWritten;
            Assert.Equal(Drawn(), InvestigationWorkspace.Read(workspace).Panes!.GraphShare, 0.005);

            // Back at equal halves with both shown, the panes keep nothing, and the investigation's window says nothing of them.
            RowDefinitions rows = main.GetControl<Grid>("PanesGrid").RowDefinitions;
            (rows[0].Height, rows[2].Height) = (GridLength.Star, GridLength.Star);
            Render(main);
            await main.InvestigationWritten;
            Assert.Null(InvestigationWorkspace.Read(workspace).Panes);
            WaitFor(() => window.View!.Panes is null);
            Assert.Null(PanesLine(window));
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R22: an investigation keeps the widths a person gave the ranked table and the inspector by their edges, and puts them back for any of its sessions")]
    public async Task AnInvestigationKeepsTheColumnsWidths()
    {
        using var root = new TemporaryDirectory();
        string alpha = PairedSession(root.Path, "alpha");
        string beta = PairedSession(root.Path, "beta");
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        InvestigationWorkspace.Add(workspace, alpha, Committed);
        InvestigationWorkspace.Add(workspace, beta, Committed);
        static (double?, double?) Widths(WorkspacePanes? panes) => (panes!.RailWidth, panes.InspectorWidth);
        var main = new MainWindow { Width = 1456, Height = 939 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            ListBox list = Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one");
            Button open = Named<Button>(window, "Open in InterCat");
            list.SelectedIndex = 0;
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => main.GetControl<TextBlock>("CaptureSessionPath").Text == alpha);
            ColumnDefinitions columns = main.GetControl<Grid>("WindowGrid").ColumnDefinitions;

            // Arrow keys on the inspector's edge widen it, kept as it moves; a drag of the rail's edge is kept once let go.
            GridSplitter inspectorEdge = main.GetControl<GridSplitter>("InspectorSplitter");
            Assert.True(inspectorEdge.Focus());
            for (int press = 0; press < 6; press++)
            {
                main.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
            }

            Render(main);
            await main.InvestigationWritten;
            double inspector = columns[2].ActualWidth;
            Assert.True(inspector > 330, $"The inspector is {inspector:F0} px wide.");
            GridSplitter railEdge = main.GetControl<GridSplitter>("RailSplitter");
            Point grip = railEdge.TranslatePoint(new Point(railEdge.Bounds.Width / 2, railEdge.Bounds.Height / 2), main)!.Value;
            main.MouseDown(grip, MouseButton.Left);
            main.MouseMove(grip + new Vector(40, 0));
            main.MouseMove(grip + new Vector(70, 0));
            Render(main);
            await main.InvestigationWritten;
            Assert.Null(InvestigationWorkspace.Read(workspace).Panes!.RailWidth);
            main.MouseUp(grip + new Vector(70, 0), MouseButton.Left);
            Render(main);
            await main.InvestigationWritten;
            double rail = columns[0].ActualWidth;
            Assert.Equal(((double?)Math.Round(rail), (double?)Math.Round(inspector)), Widths(InvestigationWorkspace.Read(workspace).Panes));
            string said = string.Create(CultureInfo.CurrentCulture,
                $"the ranked table {Math.Round(rail):N0} pixels wide and the inspector {Math.Round(inspector):N0} pixels wide");
            WaitFor(() => window.View!.Panes == $"Its sessions open with {said}, as the window was left here.");

            // A window narrowed below what they leave the main panes takes the room back, which is the window's, not kept.
            main.Width = 1080;
            Render(main);
            await main.InvestigationWritten;
            Assert.Equal(WorkspacePanes.InspectorDesign, columns[2].ActualWidth, 0.5);
            Assert.Equal(((double?)Math.Round(rail), (double?)Math.Round(inspector)), Widths(InvestigationWorkspace.Read(workspace).Panes));
            main.Width = 1456;
            Render(main);

            // A session opened on its own leaves the columns as they are; any opened from the investigation puts them back,
            // and its notice says so.
            Assert.True(await main.OpenSessionAsync(beta));
            list.SelectedIndex = 1;
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => main.GetControl<TextBlock>("CaptureSessionPath").Text == beta
                && Math.Abs(columns[2].ActualWidth - inspector) < 1);
            Render(main);
            Assert.Equal(Math.Round(rail), columns[0].ActualWidth, 0.5);
            Assert.EndsWith($"which put back {said}.", main.GetControl<TextBlock>("CaptureDetail").Text, StringComparison.Ordinal);
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    /// <summary>The line beneath an investigation's sessions that says what opening any of them puts back of the window's panes.</summary>
    private static TextBlock? PanesLine(InvestigationWindow window)
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return window.GetVisualDescendants().OfType<TextBlock>().SingleOrDefault(line => line.IsEffectivelyVisible
            && line.Text?.StartsWith("Its sessions open with", StringComparison.Ordinal) == true);
    }

    private static void Render(Window window)
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        _ = window.CaptureRenderedFrame();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The line of the first session's row that says what the investigation keeps of its view.</summary>
    private static TextBlock KeptLine(ListBox list, bool visible = true)
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var row = (InvestigationMemberRow)list.Items[0]!;
        TextBlock[] lines = [.. list.ContainerFromIndex(0)!.GetVisualDescendants().OfType<TextBlock>()];
        return visible
            ? lines.Single(line => line.IsVisible && line.Text == row.Kept)
            : lines.Single(line => !line.IsVisible && line.Text is null && line.Classes.Contains("muted"));
    }

    [AvaloniaFact(DisplayName = "R22: a session opened from an investigation keeps its pinned lanes there, in the order pinned, and gets them back")]
    public async Task AnInvestigationKeepsASessionsPinnedLanes()
    {
        using var root = new TemporaryDirectory();
        string pool = PoolSession(root.Path, "pool");
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        Guid a = InvestigationWorkspace.Add(workspace, pool, Committed).SessionId;
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            ListBox list = Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one");
            list.SelectedIndex = 0;
            Button open = Named<Button>(window, "Open in InterCat");
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => main.GetControl<TextBlock>("CaptureSessionPath").Text == pool);

            // Two lanes pinned, the quieter first, are kept in the order pinned, which is not their instances' order.
            var shown = (WorkspaceViewModel)main.DataContext!;
            ProcessInstanceId[] pinned = await PinLanesAsync(main, shown, 503, 501);
            Assert.Equal([pinned[0].Value, pinned[1].Value],
                InvestigationWorkspace.LayoutOf(InvestigationWorkspace.Read(workspace), a)!.PinnedLanes);
            const string Kept = "Opens with 2 process lanes pinned on its timeline, as it was left here.";
            WaitFor(() => window.View!.Members[0].Kept == Kept);
            Assert.Equal(Kept, KeptLine(list).Text);

            // Opened on its own, the session pins no lane.
            Assert.True(await main.OpenSessionAsync(pool));
            Assert.Empty(((WorkspaceViewModel)main.DataContext!).PinnedLanes);

            // Opened from the investigation again, its lanes are pinned as they were, drawn first in the order pinned, and
            // its status says what was put back.
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => ((WorkspaceViewModel)main.DataContext!).PinnedLanes.Count == 2);
            var again = (WorkspaceViewModel)main.DataContext!;
            Assert.Equal(pinned, again.PinnedLanes);
            Assert.Contains("which put back 2 process lanes pinned on its timeline.", main.GetControl<TextBlock>("CaptureDetail").Text,
                StringComparison.Ordinal);
            await OpenPoolGroupAsync(again);
            Assert.Equal(pinned, again.ProcessLaneDisplay.Take(2).Select(lane => lane.ProcessId));

            // Unpinned, both are gone from the investigation, which then keeps nothing of the session.
            foreach (ProcessInstanceId lane in pinned)
            {
                again.SelectProcess(lane);
                Assert.True(again.ToggleSelectedLanePin());
            }

            await main.InvestigationWritten;
            Assert.Null(InvestigationWorkspace.LayoutOf(InvestigationWorkspace.Read(workspace), a));
            WaitFor(() => window.View!.Members[0].Kept is null);
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R22: a session opened from an investigation keeps its grouping by terminal session there, and is grouped so again")]
    public async Task AnInvestigationKeepsASessionsGrouping()
    {
        using var root = new TemporaryDirectory();
        string named = TerminalSessionsSession(root.Path, "named");
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        Guid a = InvestigationWorkspace.Add(workspace, named, Committed).SessionId;
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            ListBox list = Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one");
            list.SelectedIndex = 0;
            Button open = Named<Button>(window, "Open in InterCat");
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => main.GetControl<TextBlock>("CaptureSessionPath").Text == named);

            // Grouped by terminal session, the session keeps the grouping in its investigation, whose window says so.
            Assert.True(main.ChooseGrouping(LaneGrouping.UserSession));
            await main.InvestigationWritten;
            Assert.Equal(LaneGrouping.UserSession, InvestigationWorkspace.LayoutOf(InvestigationWorkspace.Read(workspace), a)!.Grouping);
            const string Kept = "Opens with its processes grouped by terminal session, as it was left here.";
            WaitFor(() => window.View!.Members[0].Kept == Kept);
            Assert.Equal(Kept, KeptLine(list).Text);

            // Opened on its own, the session is grouped by executable.
            Assert.True(await main.OpenSessionAsync(named));
            Assert.Equal(LaneGrouping.Executable, ((WorkspaceViewModel)main.DataContext!).Grouping);

            // Opened from the investigation again, it is grouped by terminal session from its first view, and its status
            // says what was put back.
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => ((WorkspaceViewModel)main.DataContext!).Grouping == LaneGrouping.UserSession);
            var again = (WorkspaceViewModel)main.DataContext!;
            Assert.Equal("Terminal session", main.GetControl<ComboBox>("GroupBySelector").SelectedItem?.ToString());
            Assert.Contains("which put back its processes grouped by terminal session.", main.GetControl<TextBlock>("CaptureDetail").Text,
                StringComparison.Ordinal);
            Assert.Contains(again.RungRows, row => row.Label == "Terminal session 1");

            // Grouped by executable again, the investigation keeps nothing of the session.
            Assert.True(main.ChooseGrouping(LaneGrouping.Executable));
            await main.InvestigationWritten;
            Assert.Null(InvestigationWorkspace.LayoutOf(InvestigationWorkspace.Read(workspace), a));
            WaitFor(() => window.View!.Members[0].Kept is null);
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§19.5: a session opened from an investigation keeps InterCat's own processes set aside there, and opens so again")]
    public async Task AnInvestigationKeepsInterCatsOwnSetAside()
    {
        using var root = new TemporaryDirectory();
        string collected = Directory.CreateDirectory(Path.Combine(root.Path, "collected")).FullName;
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(collected), Guid.NewGuid(), "investigation-window-tests");
        PublishCollected(store);
        store.ReleaseSegmentReaders();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        Guid a = InvestigationWorkspace.Add(workspace, collected, Committed).SessionId;
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            ListBox list = Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one");
            list.SelectedIndex = 0;
            Button open = Named<Button>(window, "Open in InterCat");
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => main.GetControl<TextBlock>("CaptureSessionPath").Text == collected);

            // Set aside, the session keeps the choice in its investigation, whose window says so.
            Assert.True(main.ChooseCollectorsAside(true));
            await main.InvestigationWritten;
            Assert.True(InvestigationWorkspace.LayoutOf(InvestigationWorkspace.Read(workspace), a)!.CollectorsAside);
            const string Kept = "Opens with InterCat's own processes set aside, as it was left here.";
            WaitFor(() => window.View!.Members[0].Kept == Kept);
            Assert.Equal(Kept, KeptLine(list).Text);

            // Opened on its own, the session shows them.
            Assert.True(await main.OpenSessionAsync(collected));
            Assert.False(((WorkspaceViewModel)main.DataContext!).CollectorsSetAside);

            // Opened from the investigation again, they are set aside from its first view, and its status says so.
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => main.DataContext is WorkspaceViewModel { CollectorsSetAside: true });
            var again = (WorkspaceViewModel)main.DataContext!;
            Assert.DoesNotContain(again.RungRows, row => row.Label == "intercat-broker.exe");
            Assert.Contains("which put back InterCat's own processes set aside.", main.GetControl<TextBlock>("CaptureDetail").Text,
                StringComparison.Ordinal);

            // Shown again, the investigation keeps nothing of the session.
            Assert.True(main.ChooseCollectorsAside(false));
            await main.InvestigationWritten;
            Assert.Null(InvestigationWorkspace.LayoutOf(InvestigationWorkspace.Read(workspace), a));
            WaitFor(() => window.View!.Members[0].Kept is null);
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§26.3: a session opened from an investigation keeps its times read on the wall clock there, and is read so from its first view when opened from it again")]
    public async Task AnInvestigationKeepsTheWallClock()
    {
        using var root = new TemporaryDirectory();
        string calibrated = Directory.CreateDirectory(Path.Combine(root.Path, "calibrated")).FullName;
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(calibrated), Guid.NewGuid(), "investigation-window-tests");
        DateTimeOffset noon = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        Publish(store,
            [Transfer(1_100, ObservationKind.Send, AccountingSide.SendSide, 64, 100).Between("127.0.0.1:50000", "127.0.0.1:8080")
                with { SessionRelativeTicks = 110_000 }],
            calibration: new ClockCalibrationV1
            {
                Contract = ClockCalibrationV1.ContractName,
                CaptureId = InterCat.Analysis.Tests.TestSessions.Capture.Value,
                ClockId = TestClock.Id.Value,
                WallClock = "test-wall-clock",
                Samples = [new() { NativeTicks = 0, Utc = noon, AcquisitionUncertaintyNanoseconds = 200 }],
            });
        store.ReleaseSegmentReaders();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        Guid a = InvestigationWorkspace.Add(workspace, calibrated, Committed).SessionId;
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            ListBox list = Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one");
            list.SelectedIndex = 0;
            Button open = Named<Button>(window, "Open in InterCat");
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => main.GetControl<TextBlock>("CaptureSessionPath").Text == calibrated);

            // Read on the wall clock, the session keeps the choice in its investigation, whose window says so.
            ((WorkspaceViewModel)main.DataContext!).ReadsWallClock = true;
            await main.InvestigationWritten;
            Assert.True(InvestigationWorkspace.LayoutOf(InvestigationWorkspace.Read(workspace), a)!.WallClock);
            const string Kept = "Opens with its times read on the wall clock, as it was left here.";
            WaitFor(() => window.View!.Members[0].Kept == Kept);
            Assert.Equal(Kept, KeptLine(list).Text);

            // Opened on its own, the session reads session time.
            Assert.True(await main.OpenSessionAsync(calibrated));
            Assert.False(((WorkspaceViewModel)main.DataContext!).ReadsWallClock);

            // Opened from the investigation again, it reads the wall clock from its first view, and its status says so.
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => main.DataContext is WorkspaceViewModel { ReadsWallClock: true });
            Assert.True(main.GetControl<ToggleButton>("WallClockToggle").IsChecked);
            Assert.Contains("which put back its times read on the wall clock.", main.GetControl<TextBlock>("CaptureDetail").Text,
                StringComparison.Ordinal);

            // Read in session time again, the investigation keeps nothing of the session.
            ((WorkspaceViewModel)main.DataContext!).ReadsWallClock = false;
            await main.InvestigationWritten;
            Assert.Null(InvestigationWorkspace.LayoutOf(InvestigationWorkspace.Read(workspace), a));
            WaitFor(() => window.View!.Members[0].Kept is null);

            // A session whose capture recorded no wall clock is read in session time, whatever its layout says, and its notice
            // says nothing of a wall clock it could not put back.
            string plain = Directory.CreateDirectory(Path.Combine(root.Path, "plain")).FullName;
            SessionStore imported = SessionStore.Open(LocalOwnedDirectory.Open(plain), Guid.NewGuid(), "investigation-window-tests");
            Publish(imported,
                [Transfer(1_100, ObservationKind.Send, AccountingSide.SendSide, 64, 100).Between("127.0.0.1:50000", "127.0.0.1:8080")
                    with { SessionRelativeTicks = 110_000 }],
                capture: CaptureId.New());
            imported.ReleaseSegmentReaders();
            Guid b = InvestigationWorkspace.Add(workspace, plain, Committed).SessionId;
            InvestigationWorkspace.SetLayout(workspace, b, [], Committed, scalesEachLane: true, wallClock: true);
            await window.RefreshAsync();
            WaitFor(() => window.View!.Members.Count == 2);
            list.SelectedIndex = 1;
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => main.GetControl<TextBlock>("CaptureSessionPath").Text == plain);
            WaitFor(() => main.DataContext is WorkspaceViewModel { ScalesEachLane: true });
            Assert.False(((WorkspaceViewModel)main.DataContext!).ReadsWallClock);
            Assert.Contains("which put back each of its timeline lanes on its own scale.",
                main.GetControl<TextBlock>("CaptureDetail").Text, StringComparison.Ordinal);
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§26.3: a session opened from an investigation keeps its Other hosts opened there, drawn so from its first graph with a host pinned where it was put")]
    public async Task AnInvestigationKeepsOtherHostsOpened()
    {
        using var root = new TemporaryDirectory();
        string reaching = HostsSession(root.Path, "reaching");
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        Guid a = InvestigationWorkspace.Add(workspace, reaching, Committed).SessionId;
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            ListBox list = Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one");
            list.SelectedIndex = 0;
            Button open = Named<Button>(window, "Open in InterCat");
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => main.GetControl<TextBlock>("CaptureSessionPath").Text == reaching);

            // Opened by its node, Other hosts is kept opened in the investigation, whose window says so, and the rail names
            // it among the settings changed from their defaults.
            var shown = (WorkspaceViewModel)main.DataContext!;
            await shown.LayoutReady;
            Assert.True(shown.OpenGraphNode(GraphProjection.OtherHostsKey));
            await shown.LayoutReady;
            await main.InvestigationWritten;
            Assert.True(InvestigationWorkspace.LayoutOf(InvestigationWorkspace.Read(workspace), a)!.HostsOpened);
            const string Kept = "Opens with its busiest other hosts drawn on their own, as it was left here.";
            WaitFor(() => window.View!.Members[0].Kept == Kept);
            Assert.Equal(Kept, KeptLine(list).Text);
            Assert.Equal("Not the default view: its busiest other hosts drawn on their own.", shown.ChangedSettingsText);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(main.GetControl<Control>("DefaultsPanel").IsVisible);
            Assert.Equal("1 setting changed", main.GetControl<TextBlock>("ChangedSettingsLine").Text);

            // A host drawn on its own is pinned where it was put, as any node is.
            string host = GraphProjection.HostKey("142.250.1.1");
            var place = new GraphPoint(0.85, 0.2);
            Assert.True(shown.PinGraphNode(host, place));
            await main.InvestigationWritten;
            Assert.Equal(host, Assert.Single(InvestigationWorkspace.LayoutOf(InvestigationWorkspace.Read(workspace), a)!.Pins).Key);

            // Opened on its own, the session's Other hosts is folded.
            Assert.True(await main.OpenSessionAsync(reaching));
            var alone = (WorkspaceViewModel)main.DataContext!;
            Assert.False(alone.HostsOpened);
            Assert.DoesNotContain(alone.GraphDisplay.Nodes, node => node.Kind == GraphNodeKind.Host);

            // Opened from the investigation again, its first graph draws the hosts on their own, the pinned one where it was
            // put before any layout arrives and after, and its status says so.
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => main.DataContext is WorkspaceViewModel { HostsOpened: true });
            var again = (WorkspaceViewModel)main.DataContext!;
            Assert.Equal("142.250.1.1, 8.8.8.8", string.Join(", ", again.GraphDisplay.OpenedHosts!));
            Assert.Equal(place, again.DisplayPositions[host]);
            Assert.Contains("which put back 1 node pinned on its graph and its busiest other hosts drawn on their own.",
                main.GetControl<TextBlock>("CaptureDetail").Text, StringComparison.Ordinal);
            await again.LayoutReady;
            Assert.Equal(place, again.DisplayPositions[host]);

            // The default view folds it again and keeps the pin, which the investigation then keeps alone.
            Assert.True(await main.RestoreDefaultViewAsync());
            Assert.False(again.HostsOpened);
            Assert.False(again.DiffersFromDefaults);
            await main.InvestigationWritten;
            WorkspaceLayout folded = InvestigationWorkspace.LayoutOf(InvestigationWorkspace.Read(workspace), a)!;
            Assert.Equal((false, host), (folded.HostsOpened, Assert.Single(folded.Pins).Key));
            WaitFor(() => window.View!.Members[0].Kept == "Opens with 1 node pinned on its graph, as it was left here.");

            // A session none of whose processes reached another host opens with what else its layout keeps, and its notice
            // and rail say nothing of hosts it has none of.
            string paired = PairedSession(root.Path, "paired");
            Guid b = InvestigationWorkspace.Add(workspace, paired, Committed).SessionId;
            InvestigationWorkspace.SetLayout(workspace, b, [], Committed, scalesEachLane: true, hostsOpened: true);
            await window.RefreshAsync();
            WaitFor(() => window.View!.Members.Count == 2);
            list.SelectedIndex = 1;
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => main.GetControl<TextBlock>("CaptureSessionPath").Text == paired);
            WaitFor(() => main.DataContext is WorkspaceViewModel { ScalesEachLane: true });
            Assert.Contains("which put back each of its timeline lanes on its own scale.",
                main.GetControl<TextBlock>("CaptureDetail").Text, StringComparison.Ordinal);
            Assert.Equal("Not the default view: each of its timeline lanes on its own scale.",
                ((WorkspaceViewModel)main.DataContext!).ChangedSettingsText);
        }
        finally
        {
            main.Close();
        }
    }

    /// <summary>Opens pool.exe's group in <paramref name="workspace"/>, its lanes counted.</summary>
    private static async Task OpenPoolGroupAsync(WorkspaceViewModel workspace)
    {
        await workspace.LayoutReady;
        workspace.SelectedRung = workspace.RungRows.Single(row => row.Label.StartsWith("pool.exe", StringComparison.Ordinal));
        Assert.True(workspace.Descend());
        WaitFor(() => workspace.ShowsProcessLanes);
        await workspace.TimelineDetailReady;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Pins the lanes of the pool.exe instances holding <paramref name="pids"/>, in that order, once kept.</summary>
    private static async Task<ProcessInstanceId[]> PinLanesAsync(MainWindow main, WorkspaceViewModel workspace, params int[] pids)
    {
        await OpenPoolGroupAsync(workspace);
        ProcessInstanceId[] lanes = [.. pids.Select(pid => workspace.Snapshot.Processes.Single(process => process.ProcessId == pid).Id)];
        foreach (ProcessInstanceId lane in lanes)
        {
            workspace.SelectProcess(lane);
            Assert.True(workspace.ToggleSelectedLanePin());
        }

        await main.InvestigationWritten;
        return lanes;
    }

    [AvaloniaFact(DisplayName = "R22: a new investigation starts empty, and an existing file is opened, never written over")]
    public void ANewInvestigationStartsEmpty()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "fresh" + InvestigationWorkspace.Extension);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = Assert.IsType<InvestigationWindow>(main.StartInvestigation(workspace));
            WaitFor(() => window.View is not null);
            Assert.Empty(window.View!.Members);
            Assert.Equal("No session yet: add the sessions this investigation covers.", window.View.Summary);
            Assert.Equal("A new investigation: add the sessions it covers.", Named<TextBlock>(window, "Investigation status").Text);
            string written = File.ReadAllText(workspace);
            window.Close();

            // Asked to make one that exists, the window opens it as it is, saying it holds no session yet - never that
            // every session is where it was last found.
            InvestigationWindow again = Assert.IsType<InvestigationWindow>(main.StartInvestigation(workspace));
            WaitFor(() => again.View is not null);
            Assert.Equal(written, File.ReadAllText(workspace));
            Assert.Equal("This investigation holds no session yet: add the sessions it covers.",
                Named<TextBlock>(again, "Investigation status").Text);

            // Its file gone from under its window, a new one started there is read again by that window, not left saying
            // it was moved, renamed or deleted.
            File.Delete(workspace);
            _ = again.RefreshAsync();
            WaitFor(() => again.View is null && Named<TextBlock>(again, "Investigation status").Text!.StartsWith(
                "This investigation could not be read: No file is at ", StringComparison.Ordinal));
            Assert.Same(again, main.StartInvestigation(workspace));
            WaitFor(() => again.View is not null);
            Assert.Equal("A new investigation: add the sessions it covers.", Named<TextBlock>(again, "Investigation status").Text);
            Assert.Same(again, Assert.Single(main.Investigations));
            again.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R21: the investigation window aligns a session by an instant read in both, says what is missing, and withdraws it")]
    public async Task TheWindowAlignsAndWithdraws()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        Guid a = InvestigationWorkspace.Add(workspace, Session(root.Path, "alpha"), Committed).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, Session(root.Path, "beta"), Committed).SessionId;
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            ListBox list = Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one");
            list.SelectedIndex = 1;
            Assert.True(Named<Button>(window, "Align the selected session to the investigation's time").IsEnabled);
            Assert.False(Named<Button>(window, "Withdraw alignment").IsEnabled);

            // The dialog aligns to the other session's clock, and says in words what it needs; a session aligned to nothing yet
            // has no alignment in force to state.
            InvestigationAlignWindow dialog = window.AlignDialogForSelected()!;
            dialog.Show(window);
            Assert.False(Named<TextBlock>(dialog, "The alignment in force").IsVisible);
            Assert.False(await dialog.AlignAsync());
            Assert.Equal("Write both instants in seconds of their own session's time, such as 12.5.",
                Named<TextBlock>(dialog, "Alignment status").Text);

            // By boot, sessions that recorded no calibration are refused, in words.
            dialog.Choose(WorkspaceAlignmentMode.SameBoot);
            Assert.False(await dialog.AlignAsync());
            Assert.Contains("records no clock calibration", Named<TextBlock>(dialog, "Alignment status").Text, StringComparison.Ordinal);

            dialog.Choose(WorkspaceAlignmentMode.Manual);
            Field(dialog, "Its instant (").Text = "2";
            Field(dialog, "is the reference's instant, in seconds").Text = "5,5";
            Field(dialog, "The clocks drift at most (ppm)").Text = "20";
            Save(dialog, "investigation-align.png");

            // Alpha is the one session beta can be aligned to, which the choice, not offered, says; Align waits while it
            // aligns, saying so.
            ComboBox to = Named<ComboBox>(dialog, "Session to align to");
            Assert.False(to.IsEnabled);
            Assert.True(ToolTip.GetShowOnDisabled(to));
            Assert.Equal("It is the only session this one can be aligned to.", AutomationProperties.GetHelpText(to));
            List<(bool, string?)> aligning = Offers.Heard(Named<Button>(dialog, "Align this session"));
            Assert.True(await dialog.AlignAsync());
            Assert.Equal([(false, $"Session {Short(b)} is being aligned."), (true, null)], aligning);
            InvestigationWorkspaceFile aligned = InvestigationWorkspace.Read(workspace);
            WorkspaceAlignment made = InvestigationWorkspace.ActiveAlignment(aligned, b)!;
            Assert.Equal((a, 2_000_000_000L, 5_500_000_000L, 1_000_000L, (double?)20),
                (aligned.TimeReference!.Value, made.SessionNanoseconds!.Value, made.ReferenceNanoseconds!.Value,
                    made.WithinNanoseconds!.Value, made.DriftPartsPerMillion));

            // The window shows it, and withdraws it; the revision is kept.
            await window.RefreshAsync();
            WaitFor(() => window.View!.Members[1].IsAligned);
            Assert.StartsWith("Aligned by a person", window.View!.Members[1].Time, StringComparison.Ordinal);
            list.SelectedIndex = 1;
            Assert.True(Named<Button>(window, "Withdraw alignment").IsEnabled);
            await window.WithdrawSelectedAsync();
            WaitFor(() => !window.View!.Members[1].IsAligned);
            Assert.Equal(2, InvestigationWorkspace.Read(workspace).Alignments.Count);
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R21: the investigation window aligns a session at two instants, and says the rate they measure")]
    public async Task TheWindowAlignsAtTwoInstants()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        _ = InvestigationWorkspace.Add(workspace, Session(root.Path, "alpha"), Committed);
        Guid b = InvestigationWorkspace.Add(workspace, Session(root.Path, "beta"), Committed).SessionId;
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one").SelectedIndex = 1;
            InvestigationAlignWindow dialog = window.AlignDialogForSelected()!;
            dialog.Show(window);
            dialog.Choose(WorkspaceAlignmentMode.Manual);
            CultureInfo culture = CultureInfo.CurrentCulture;
            Field(dialog, "Its instant (").Text = "1";
            Field(dialog, "is the reference's instant, in seconds").Text = "3";
            Field(dialog, "A second instant of it, in seconds").Text = "11";

            // Half of a second instant is refused, in words; both measure the clocks' rate, and the drift bounds its wander.
            Assert.False(await dialog.AlignAsync());
            Assert.Equal("Write both second instants in seconds of their own session's time, or leave both empty.",
                Named<TextBlock>(dialog, "Alignment status").Text);
            Field(dialog, "is the reference's second, in seconds").Text = 13.0001m.ToString(culture);
            Field(dialog, "The clocks drift at most (ppm)").Text = 0.5m.ToString(culture);
            Save(dialog, "investigation-align-two.png");
            Assert.True(await dialog.AlignAsync());
            WorkspaceAlignment made = InvestigationWorkspace.ActiveAlignment(InvestigationWorkspace.Read(workspace), b)!;
            Assert.Equal((1_000_000_000L, 3_000_000_000L, 11_000_000_000L, 13_000_100_000L, (double?)0.5),
                (made.SessionNanoseconds!.Value, made.ReferenceNanoseconds!.Value, made.SecondSessionNanoseconds!.Value,
                    made.SecondReferenceNanoseconds!.Value, made.DriftPartsPerMillion));

            // The window says both instants, the rate they measure and its wander.
            await window.RefreshAsync();
            WaitFor(() => window.View!.Members[1].IsAligned);
            string time = window.View!.Members[1].Time;
            Assert.StartsWith($"Aligned by a person at two instants: its {1m.ToString("0.000", culture)} s and {11m.ToString("0.000", culture)} s "
                + $"are the reference's {3m.ToString("0.000", culture)} s and {13.0001m.ToString("0.000######", culture)} s, within ±",
                time, StringComparison.Ordinal);
            Assert.EndsWith($", so its clock runs +10 ppm against the reference's, its rate wandering at most {0.5m.ToString("0.###", culture)} ppm",
                time, StringComparison.Ordinal);

            // Opened again, the dialog starts from that alignment, says it, and aligning from it states the same again.
            Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one").SelectedIndex = 1;
            InvestigationAlignWindow again = window.AlignDialogForSelected()!;
            again.Show(window);
            TextBlock now = Named<TextBlock>(again, "The alignment in force");
            Assert.True(now.IsVisible);
            Assert.Equal($"Now: {time}. Aligning it again replaces this; withdrawing it is in the investigation's window.", now.Text);
            string[] boxes =
            [
                "Its instant (", "is the reference's instant, in seconds", "A second instant of it, in seconds",
                "is the reference's second, in seconds", "within", "The clocks drift at most (ppm)",
            ];
            Assert.Equal(["1", "3", "11", 13.0001m.ToString(culture), "1 ms", 0.5.ToString("R", culture)],
                boxes.Select(caption => Field(again, caption).Text));

            // A screen reader names each box by the words shown beside it, and says its hint after them.
            TextBox within = Field(again, "within");
            Assert.Equal(("within", "How sure you are that each pair is one instant."),
                (ControlAutomationPeer.CreatePeerForElement(within).GetName(), AutomationProperties.GetHelpText(within)));

            // Each box keeps its own height beside a hint of several lines, the drift's beside the longest.
            Save(again, "investigation-align-again.png");
            Assert.True(Field(again, boxes[^1]).IsEffectivelyVisible);
            Assert.All(boxes, caption => Assert.Equal(Field(again, boxes[0]).Bounds.Height, Field(again, caption).Bounds.Height, 1));
            Assert.True(await again.AlignAsync());
            WorkspaceAlignment restated = InvestigationWorkspace.ActiveAlignment(InvestigationWorkspace.Read(workspace), b)!;
            Assert.Equal(made with { Revision = restated.Revision, RecordedUtc = restated.RecordedUtc }, restated);
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§8.2: a session aligned by its wall clock opens the Align dialog on that alignment, and an investigation with no translation or view says so in each list's place")]
    public async Task TheDialogsStartFromWhatIsKept()
    {
        using var root = new TemporaryDirectory();
        DemoInvestigationResult demo = DemoInvestigation.Create(Path.Combine(root.Path, "demo"));
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(demo.WorkspacePath);
            WaitFor(() => window.View is not null);
            InvestigationMemberRow server = window.View!.Members.Single(row => row.Alignment is not null);
            WorkspaceAlignment aligned = server.Alignment!;
            Assert.Equal(WorkspaceAlignmentMode.WallClock, aligned.Mode);

            // The dialog opens on the wall clocks, the reference and what was stated of them, and says the alignment in force.
            Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one").SelectedItem = server;
            InvestigationAlignWindow dialog = window.AlignDialogForSelected()!;
            dialog.Show(window);
            CultureInfo culture = CultureInfo.CurrentCulture;
            Assert.Equal((true, false, false), (Named<RadioButton>(dialog, "By their wall clocks").IsChecked == true,
                Named<RadioButton>(dialog, "By one or two instants I read in both").IsChecked == true,
                Named<RadioButton>(dialog, "By their boot: exact, when both captures recorded one boot").IsChecked == true));
            Assert.Equal(InvestigationInput.WriteDuration(aligned.SynchronizationNanoseconds!.Value, culture),
                Field(dialog, "The wall clocks agreed within").Text);
            Assert.Equal(InvestigationInput.WritePartsPerMillion(aligned.DriftPartsPerMillion!.Value, culture),
                Field(dialog, "The clocks drift at most (ppm)").Text);
            Assert.Equal(aligned.ReferenceSessionId, Assert.IsType<AlignmentReference>(Named<ComboBox>(dialog, "Session to align to").SelectedItem).SessionId);
            Assert.StartsWith("Now: Aligned by the wall clocks", Named<TextBlock>(dialog, "The alignment in force").Text, StringComparison.Ordinal);
            Save(dialog, "investigation-align-wall-clock.png");

            // Aligning from what it opened on states the same alignment again.
            Assert.True(await dialog.AlignAsync());
            WorkspaceAlignment restated = InvestigationWorkspace.ActiveAlignment(InvestigationWorkspace.Read(demo.WorkspacePath), server.SessionId)!;
            Assert.Equal(aligned with { Revision = restated.Revision, RecordedUtc = restated.RecordedUtc, Note = restated.Note }, restated);

            // The demo states no translation and saves no view: each list says so in its place.
            foreach ((Window list, string none) in new (Window, string)[]
            {
                (window.TranslationsDialog(), "No translation is stated, so candidate joins mirror only endpoints the two captures name alike."),
                (window.ViewsDialog(), "No view is saved yet: name the interval the timeline shows, below, to show it again."),
            })
            {
                list.Show(window);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.True(list.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == none).IsEffectivelyVisible, none);
                list.Close();
            }

            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R21: the investigation window aligns a session through another aligned one, and never through itself")]
    public async Task TheWindowAlignsThroughAnotherSession()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        Guid a = InvestigationWorkspace.Add(workspace, Session(root.Path, "alpha"), Committed).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, Session(root.Path, "beta"), Committed).SessionId;
        Guid c = InvestigationWorkspace.Add(workspace, Session(root.Path, "gamma"), Committed).SessionId;
        InvestigationWorkspace.Align(workspace, b, 0, a, 5_000_000_000, 1_000_000, 10, null, Committed);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            ListBox list = Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one");

            // Gamma may be aligned to the investigation's clock or to beta, which is placed in it.
            list.SelectedIndex = 2;
            InvestigationAlignWindow dialog = window.AlignDialogForSelected()!;
            dialog.Show(window);
            ComboBox to = Named<ComboBox>(dialog, "Session to align to");
            Assert.True(to.IsEnabled);
            Assert.Equal("The investigation's clock, or a session placed in it", AutomationProperties.GetHelpText(to));
            Assert.Equal([a, b], ((IEnumerable<AlignmentReference>)to.ItemsSource!).Select(choice => choice.SessionId));
            to.SelectedIndex = 1;
            dialog.Choose(WorkspaceAlignmentMode.Manual);
            Field(dialog, "Its instant (").Text = "0";
            Field(dialog, "is the reference's instant, in seconds").Text = "1";
            Field(dialog, "The clocks drift at most (ppm)").Text = "1";
            Assert.True(await dialog.AlignAsync());
            Assert.Equal(b, InvestigationWorkspace.ActiveAlignment(InvestigationWorkspace.Read(workspace), c)!.ReferenceSessionId);

            // The window says whom it is aligned to, and that the investigation's sessions are placed through one another.
            await window.RefreshAsync();
            WaitFor(() => window.View!.Members[2].IsAligned);
            CultureInfo culture = CultureInfo.CurrentCulture;
            Assert.StartsWith($"Aligned by a person to session {Short(b)}, itself aligned: its {0m.ToString("0.000", culture)} s is "
                + $"session {Short(b)}'s {1m.ToString("0.000", culture)} s", window.View!.Members[2].Time, StringComparison.Ordinal);
            Assert.EndsWith(", directly or through another.", window.View.Time, StringComparison.Ordinal);

            // Opened again, gamma's dialog starts from beta, the session it is aligned to.
            list.SelectedIndex = 2;
            InvestigationAlignWindow gammas = window.AlignDialogForSelected()!;
            gammas.Show(window);
            Assert.Equal(b, Assert.IsType<AlignmentReference>(Named<ComboBox>(gammas, "Session to align to").SelectedItem).SessionId);
            gammas.Close();

            // Beta, which gamma is aligned through, may be aligned only to the investigation's clock now: never through gamma.
            list.SelectedIndex = 1;
            InvestigationAlignWindow betas = window.AlignDialogForSelected()!;
            betas.Show(window);
            Assert.Equal([a], ((IEnumerable<AlignmentReference>)Named<ComboBox>(betas, "Session to align to").ItemsSource!).Select(choice => choice.SessionId));
            betas.Close();
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R22: the investigation window takes a person's word that two host identities are one host, and its withdrawal")]
    public async Task TheWindowTakesAPersonsWordThatTwoHostsAreOne()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        _ = InvestigationWorkspace.Add(workspace, Session(root.Path, "alpha"), Committed);
        _ = InvestigationWorkspace.Add(workspace, Session(root.Path, "beta"), Committed);
        Guid[] hosts = [.. InvestigationWorkspace.Read(workspace).Members.Select(member => member.HostId)];
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            Assert.EndsWith(" · 2 hosts", window.View!.Summary, StringComparison.Ordinal);
            ListBox list = Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one");
            list.SelectedIndex = 0;
            Assert.True(Named<Button>(window, "One host…").IsEnabled);

            // Confirmed from alpha's side, the two identities read as one host, which the confirmation alone makes them.
            InvestigationHostWindow dialog = window.HostDialogForSelected()!;
            dialog.Show(window);
            Assert.Equal("They are not one host by your word, so there is nothing to withdraw.",
                Offers.WhyNot(Named<Button>(dialog, "They are not one host")));
            Save(dialog, "investigation-one-host.png");

            // Confirm waits while the word is recorded, saying so.
            Button confirm = Named<Button>(dialog, "They are one host");
            Assert.Equal("Confirm that the two identities are one host", Offers.Offered(confirm));
            List<(bool, string?)> deciding = Offers.Heard(confirm);
            Assert.True(await dialog.DecideAsync(WorkspaceHostDecision.Confirmed));
            Assert.Equal([(false, "Your word is being recorded in the investigation."), (true, "Confirm that the two identities are one host")],
                deciding);
            await window.RefreshAsync();
            WaitFor(() => window.View!.Summary.EndsWith(" · 1 host (2 identities)", StringComparison.Ordinal));
            Assert.StartsWith($"host {Short(hosts[0])}, one host with host {Short(hosts[1])} by a person's confirmation · ",
                window.View!.Members[0].Detail, StringComparison.Ordinal);

            // Withdrawn from beta's side, they are two hosts again; both revisions are kept.
            list.SelectedIndex = 1;
            InvestigationHostWindow back = window.HostDialogForSelected()!;
            back.Show(window);
            Assert.Equal("Withdraw your confirmation that the two identities are one host",
                Offers.Offered(Named<Button>(back, "They are not one host")));
            Assert.Equal("They are one host by your word already.", Offers.WhyNot(Named<Button>(back, "They are one host")));
            Assert.True(await back.DecideAsync(WorkspaceHostDecision.Withdrawn));
            await window.RefreshAsync();
            WaitFor(() => window.View!.Summary.EndsWith(" · 2 hosts", StringComparison.Ordinal));
            Assert.Equal(2, InvestigationWorkspace.Read(workspace).HostEquivalences.Count);

            // With no other host listed, neither is offered, and each says so.
            var alone = new InvestigationHostWindow(workspace, hosts[0], "alpha", []);
            alone.Show(window);
            Assert.Equal("No other host of this investigation is listed.", Offers.WhyNot(Named<Button>(alone, "They are one host")));
            Assert.Equal("No other host of this investigation is listed.", Offers.WhyNot(Named<Button>(alone, "They are not one host")));
            alone.Close();
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R21: the investigation window compares two instants, placing each and ordering them only beyond their uncertainty")]
    public async Task TheWindowComparesTwoInstants()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        Guid a = InvestigationWorkspace.Add(workspace, Session(root.Path, "alpha"), Committed).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, Session(root.Path, "beta"), Committed).SessionId;
        Guid c = InvestigationWorkspace.Add(workspace, Session(root.Path, "gamma"), Committed).SessionId;
        InvestigationWorkspace.Align(workspace, b, 2_000_000_000, a, 5_500_000_000, 500_000, 50, null, Committed);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            window.ShowTab(2);
            WaitFor(() => window.Timeline is not null);
            Assert.True(Named<Button>(window, "Compare instants…").IsEnabled);
            InvestigationCompareWindow dialog = window.CompareDialog()!;
            dialog.Show(window);
            CultureInfo culture = CultureInfo.CurrentCulture;

            // Beta's 2 s is alpha's 5.5 s within 500 µs: one instant, whose order is not stated; alpha's is exact. Compare
            // waits while it compares, saying so.
            dialog.Enter(a, 5.5m.ToString(culture), b, "2");
            List<(bool, string?)> comparing = Offers.Heard(Named<Button>(dialog, "Compare the two instants"));
            WorkspaceComparison? tie = await dialog.CompareAsync();
            Assert.Equal([(false, "The two instants are being compared."), (true, null)], comparing);
            Assert.Equal(TimeOrder.Ambiguous, tie!.Result.Order);
            Assert.Contains($"The first instant, session {Short(a)}'s {5.5m.ToString("0.000", culture)} s, is the investigation's "
                + $"{5.5m.ToString("0.000", culture)} s, exactly.", dialog.Said, StringComparison.Ordinal);
            Assert.Contains($"The second instant, session {Short(b)}'s {2m.ToString("0.000", culture)} s, is the investigation's "
                + $"{5.5m.ToString("0.000", culture)} s, within ±", dialog.Said, StringComparison.Ordinal);
            Save(dialog, "investigation-compare.png");

            // Half a second apart is an order; an unaligned session's instant has none, and says why; words are not seconds.
            dialog.Enter(a, "6", b, "2");
            Assert.Equal(TimeOrder.After, (await dialog.CompareAsync())!.Result.Order);
            dialog.Enter(a, "6", c, "1");
            Assert.Equal(TimeOrder.Unknown, (await dialog.CompareAsync())!.Result.Order);
            Assert.Contains($"session {Short(c)}'s {1m.ToString("0.000", culture)} s, has no place in the investigation's time: "
                + $"session {Short(c)} is not aligned", dialog.Said, StringComparison.Ordinal);
            dialog.Enter(a, "soon", b, "2");
            Assert.Null(await dialog.CompareAsync());
            Assert.Equal("Write both instants in seconds of their own session's time, such as 12.5.", dialog.Said);
            dialog.Close();
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R21: the investigation window zooms its merged time, and opens a column's records in InterCat")]
    public async Task TheWindowZoomsAndOpensAColumn()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        string alphaPath = Datagrams(root.Path, "alpha", 4);
        string betaPath = Datagrams(root.Path, "beta", 6);
        Guid a = InvestigationWorkspace.Add(workspace, alphaPath, Committed).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, betaPath, Committed).SessionId;
        InvestigationWorkspace.Align(workspace, b, 0, a, 1_000_000_000, 1_000, 0, null, Committed);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            window.ShowTab(2);
            WaitFor(() => window.Timeline is not null);
            TimeRange whole = window.Timeline!.Interval!.Value;
            Assert.True(Named<Button>(window, "Zoom in").IsEnabled);
            Assert.False(Named<Button>(window, "Whole investigation").IsEnabled);

            // The chosen column says its session, its time and its records; the cursor starts on the busiest.
            Assert.Contains($"Session {Short(a)}, column 1 of 160: ", window.ColumnReadout, StringComparison.Ordinal);
            Assert.EndsWith("4 records, coverage: unknown. Enter opens them in InterCat.", window.ColumnReadout, StringComparison.Ordinal);

            // Its time is said as the session's timeline says an interval, in the digits its span needs.
            Assert.Contains(": " + WorkspaceTime.FormatRange(window.Timeline.Lanes[0].Buckets[0].Interval, CultureInfo.CurrentCulture)
                + " of the investigation's time, ", window.ColumnReadout, StringComparison.Ordinal);

            // The keyboard moves the cursor along a lane and to the next one: Right, End, Down.
            Control chart = window.GetVisualDescendants().OfType<InvestigationTimelineControl>().Single();
            chart.Focus();
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            Assert.Contains($"Session {Short(a)}, column 2 of 160: ", window.ColumnReadout, StringComparison.Ordinal);
            window.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            Assert.StartsWith($"Session {Short(b)}, column 160 of 160: ", window.ColumnReadout, StringComparison.Ordinal);
            Save(window, "investigation-timeline-cursor.png");

            // Zoomed in around beta's column, half as much time is shown, within the whole; the whole comes back.
            int column = window.Timeline.Lanes[1].Buckets.ToList().FindIndex(bucket => bucket.ObservationCount > 0);
            window.ChooseColumn(1, column);
            Named<Button>(window, "Zoom in").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => window.Zoom is not null && window.Timeline!.Interval != whole);
            TimeRange zoomed = window.Timeline!.Interval!.Value;
            Assert.InRange(zoomed.EndTicks - zoomed.StartTicks, (whole.EndTicks - whole.StartTicks) / 2 - 1, (whole.EndTicks - whole.StartTicks) / 2 + 1);
            Assert.True(zoomed.StartTicks >= whole.StartTicks && zoomed.EndTicks <= whole.EndTicks);
            await window.ZoomToAsync(null);
            Assert.Equal((null, (TimeRange?)whole), (window.Zoom, window.Timeline!.Interval));

            // A column opens its session in InterCat's window, its interval selected, in the session's own time.
            TimeRange own = window.Timeline.Lanes[1].OwnIntervals[column];
            // A column holding none of a session's records opens nothing, and says so.
            Assert.False(await window.OpenColumnAsync(new TimelineColumn(1, 0)));
            Assert.StartsWith("Column 1 holds no records of Session ", Named<TextBlock>(window, "Investigation status").Text, StringComparison.Ordinal);
            Assert.EndsWith(", and what its capture covered there is unknown, so that is not proof of inactivity. There is nothing of it to open: "
                + "choose a column with records.", Named<TextBlock>(window, "Investigation status").Text, StringComparison.Ordinal);

            // Only what the session holds of the column is selected: this one reaches back before beta's capture began. Its
            // button opens the column chosen, as Enter does.
            window.ChooseColumn(1, column);
            Named<Button>(window, "Open this column").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => main.GetControl<TextBlock>("CaptureSessionPath").Text == betaPath);
            var opened = (WorkspaceViewModel)main.DataContext!;
            TimeRange extent = opened.Snapshot.Extent;
            Assert.True(own.StartTicks < extent.StartTicks);
            TimeRange held = new(Math.Max(own.StartTicks, extent.StartTicks), Math.Min(own.EndTicks, extent.EndTicks));
            Assert.Equal(held, opened.SelectedInterval);
            TimeRange viewport = main.GetControl<TimelineView>("TimelineSurface").Viewport;
            Assert.True(viewport.StartTicks <= held.StartTicks && viewport.EndTicks >= held.EndTicks, $"viewport {viewport} held {held}");
            Assert.Equal($"Opened Session {Short(b)} in the InterCat window, zoomed to {WorkspaceTime.FormatRange(own, CultureInfo.CurrentCulture)} "
                + "of its own time, the column's interval selected.", Named<TextBlock>(window, "Investigation status").Text);
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§8.2: a zoom of the merged time is let go, and said, once another session's clock is the investigation's time or no session is aligned")]
    public async Task AZoomIsLetGoWhenTheClockChanges()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        Guid a = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "alpha", 4), Committed).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "beta", 6), Committed).SessionId;
        InvestigationWorkspace.Align(workspace, b, 0, a, 1_000_000_000, 1_000, 0, null, Committed);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            window.ShowTab(2);
            WaitFor(() => window.Timeline is not null);
            Assert.Equal(a, window.Timeline!.TimeReference);
            await window.ZoomToAsync(BetasColumns(window));

            // Elsewhere - icat, or another window - beta's alignment is withdrawn and alpha aligned to beta, so beta's clock
            // is the investigation's time: the range zoomed is a range of another clock, and the whole is shown again.
            InvestigationWorkspace.Withdraw(workspace, b, Committed);
            InvestigationWorkspace.Align(workspace, a, 0, b, 5_000_000_000, 1_000, 0, null, Committed);
            await window.RefreshAsync();
            await window.ShowTimelineAsync();
            Assert.Null(window.Zoom);
            Assert.Equal((b, window.Timeline!.Whole), (window.Timeline.TimeReference!.Value, window.Timeline.Interval));
            Assert.EndsWith($"The timeline shows the whole investigation again: its time is now Session {Short(b)}'s clock.",
                Named<TextBlock>(window, "Investigation status").Text, StringComparison.Ordinal);

            // With no session aligned there is no time to zoom: the zoom goes, and Zoom in, Zoom out and Whole wait.
            await window.ZoomToAsync(BetasColumns(window, lane: 1));
            Assert.NotNull(window.Zoom);
            window.ShowTab(0);
            WaitFor(() => true);
            Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one").SelectedIndex = 0;
            await window.WithdrawSelectedAsync();
            window.ShowTab(2);
            WaitFor(() => window.Timeline!.Interval is null);
            Assert.Null(window.Zoom);
            Assert.Equal($"Session {Short(a)} is not aligned any more; its earlier alignment is kept in the file.",
                Named<TextBlock>(window, "Investigation status").Text);
            Assert.False(Named<Button>(window, "Zoom in").IsEnabled);
            Assert.False(Named<Button>(window, "Zoom out").IsEnabled);
            Assert.False(Named<Button>(window, "Whole investigation").IsEnabled);
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§8.2: a zoom no placed session meets any more is let go and said, and one that still does zooms out within the new whole")]
    public async Task AZoomFollowsTheWholeItIsIn()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        Guid a = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "alpha", 4), Committed).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "beta", 6), Committed).SessionId;
        Guid c = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "gamma", 5), Committed).SessionId;
        InvestigationWorkspace.Align(workspace, b, 0, a, 1_000_000_000, 1_000, 0, null, Committed);
        InvestigationWorkspace.Align(workspace, c, 0, a, 100_000_000_000, 1_000, 0, null, Committed);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            window.ShowTab(2);
            WaitFor(() => window.Timeline is not null);

            // Zoomed to gamma's records, a hundred seconds in, and gamma's alignment withdrawn: no session placed meets the
            // range, so the whole is shown again, and the window says why after what the withdrawal did.
            await window.ZoomToAsync(BetasColumns(window, lane: 2));
            window.ShowTab(0);
            WaitFor(() => true);
            Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one").SelectedIndex = 2;
            await window.WithdrawSelectedAsync();
            window.ShowTab(2);
            WaitFor(() => window.Zoom is null);
            Assert.Equal(new TimeRange(1_000, 10_001_051), window.Timeline!.Interval);
            Assert.Equal($"Session {Short(c)} is not aligned any more; its earlier alignment is kept in the file. The timeline "
                + "shows the whole investigation again: no session is placed in the time it showed.",
                Named<TextBlock>(window, "Investigation status").Text);

            // Zoomed to the second half, and gamma aligned again, a hundred seconds on: the range still meets a session and
            // stays, and Zoom out widens it within the investigation as it is now, not the second it spanned before.
            await window.ZoomToAsync(new TimeRange(5_000_000, 10_001_051));
            InvestigationWorkspace.Align(workspace, c, 0, a, 100_000_000_000, 1_000, 0, null, Committed);
            await window.RefreshAsync();
            await window.ShowTimelineAsync();
            Assert.Equal(new TimeRange(5_000_000, 10_001_051), window.Zoom);
            Named<Button>(window, "Zoom out").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => window.Zoom is { } wider && wider.EndTicks > 10_001_051);
            Assert.Equal(10_002_102, window.Zoom!.Value.SpanTicks);
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    /// <summary>A range of the merged time about the records of the session in <paramref name="lane"/>, wider than they are.</summary>
    private static TimeRange BetasColumns(InvestigationWindow window, int lane = 1)
    {
        IReadOnlyList<TimelineBucket> buckets = window.Timeline!.Lanes[lane].Buckets;
        TimeRange first = buckets.First(bucket => bucket.ObservationCount > 0).Interval;
        TimeRange last = buckets.Last(bucket => bucket.ObservationCount > 0).Interval;
        return new TimeRange(first.StartTicks - 5_000, last.EndTicks + 5_000);
    }

    [AvaloniaFact(DisplayName = "R22: a member whose folder changed since its window read it is never opened as another session, and the window says why and what to do")]
    public async Task AMemberThatChangedIsNotOpenedAsAnother()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        string alphaPath = Datagrams(root.Path, "alpha", 4);
        string betaPath = Datagrams(root.Path, "beta", 6);
        string gamma = Session(root.Path, "gamma");
        Guid g = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(gamma)).SessionId;
        Guid a = InvestigationWorkspace.Add(workspace, alphaPath, Committed).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, betaPath, Committed).SessionId;
        InvestigationWorkspace.Align(workspace, b, 0, a, 1_000_000_000, 1_000, 0, null, Committed);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            window.ShowTab(2);
            WaitFor(() => window.Timeline is not null);
            int column = window.Timeline!.Lanes[0].Buckets.ToList().FindIndex(bucket => bucket.ObservationCount > 0);

            // Another session is written into alpha's folder after the investigation read it, and InterCat's window opens
            // that folder, as a person told it now holds another session would: it shows the other session.
            foreach (string file in Directory.EnumerateFiles(alphaPath)) File.Delete(file);
            foreach (string file in Directory.EnumerateFiles(gamma)) File.Copy(file, Path.Combine(alphaPath, Path.GetFileName(file)));
            Assert.True(await main.OpenSessionAsync(alphaPath));
            Assert.True(ShowsGamma(main));

            // alpha's column in the merged time, drawn before, opens nothing - the window keeps what it shows, which is not
            // alpha, rather than zoom it as alpha - and the investigation says why, as alpha's row now does, and what to do.
            Assert.False(await window.OpenColumnAsync(new TimelineColumn(0, column)));
            TextBlock status = Named<TextBlock>(window, "Investigation status");
            Assert.StartsWith($"Session {Short(a)} could not be opened: Session {Short(g)} of capture ", status.Text, StringComparison.Ordinal);
            Assert.EndsWith($" is at this path, not session {Short(a)}. " + InvestigationWorkspace.RelinkWhereItIs, status.Text,
                StringComparison.Ordinal);
            Assert.True(ShowsGamma(main));
            Assert.Null(((WorkspaceViewModel)main.DataContext!).SelectedInterval);
            Assert.Equal(("Could not open this session", SessionStore.AnotherSession + " What the window shows is unchanged."),
                (main.GetControl<TextBlock>("CaptureStatus").Text, main.GetControl<TextBlock>("CaptureDetail").Text));
            InvestigationMemberRow alpha = window.View!.Members[0];
            Assert.Equal((WorkspaceMemberState.Different, false), (alpha.State, alpha.HoldsItsCapture));

            // Its row says the step once, where it said it twice.
            Assert.EndsWith($" is at this path, not session {Short(a)}. " + InvestigationWorkspace.RelinkWhereItIs, alpha.AccessibleName,
                StringComparison.Ordinal);

            // beta opens from its row; its folder emptied after the row was read, opening it again opens nothing, and says that
            // its session left it, never that none was published there.
            window.ShowTab(0);
            ListBox list = Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one");
            list.SelectedIndex = 1;
            Named<Button>(window, "Open in InterCat").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => main.GetControl<TextBlock>("CaptureSessionPath").Text == betaPath);
            foreach (string file in Directory.EnumerateFiles(betaPath)) File.Delete(file);
            Named<Button>(window, "Open in InterCat").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => status.Text?.StartsWith($"Session {Short(b)} could not be opened: ", StringComparison.Ordinal) == true);
            Assert.Equal($"Session {Short(b)} could not be opened: The folder at this path holds no session now: the session was "
                + "moved or removed. " + InvestigationWorkspace.RelinkWhereItIs, status.Text);
            Assert.Equal(SessionStore.SessionGone + " What the window shows is unchanged.",
                main.GetControl<TextBlock>("CaptureDetail").Text);
            Assert.Equal((WorkspaceMemberState.Unreadable, false), (window.View!.Members[1].State, window.View.Members[1].HoldsItsCapture));
            Assert.False(Named<Button>(window, "Open in InterCat").IsEnabled);
            window.Close();
        }
        finally
        {
            main.Close();
        }

        // gamma's one process is 400; the members' is 100.
        static bool ShowsGamma(MainWindow shown) =>
            ((WorkspaceViewModel)shown.DataContext!).Snapshot.Processes.Select(process => process.ProcessId).SequenceEqual([400]);
    }

    [AvaloniaFact(DisplayName = "§8.4: a session opened from its investigation is shown in front, its ranked table with the keyboard, the investigation a peer of InterCat's window rather than one held in front of it")]
    public void ASessionOpenedFromTheInvestigationIsShownInFront()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        string alpha = Datagrams(root.Path, "alpha", 4);
        _ = InvestigationWorkspace.Add(workspace, alpha, Committed);
        _ = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "beta", 6), Committed);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            // It opens centred over InterCat's window, as a window it owned would, but Windows holds an owned window in front
            // of its owner: a session opened from it, shown in InterCat's window, was drawn behind it.
            main.Position = new PixelPoint(120, 60);
            InvestigationWindow window = main.ShowInvestigation(workspace);
            Assert.Null(window.Owner);
            Assert.DoesNotContain(window, main.OwnedWindows);
            Assert.Same(window, Assert.Single(main.Investigations));
            Assert.Equal(new PixelPoint(120 + ((1080 - 940) / 2), 60 + ((700 - 660) / 2)), window.Position);
            WaitFor(() => window.View is not null);

            // Enter on a session opens it in InterCat's window, which comes in front with the keyboard on its ranked table,
            // where it was on Start exploring, whose Enter would start a capture.
            int activated = 0;
            main.Activated += (_, _) => activated++;
            ListBox list = Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one");
            list.SelectedIndex = 0;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(list.ContainerFromIndex(0)!.Focus());
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            WaitFor(() => main.GetControl<TextBlock>("CaptureSessionPath").Text == alpha);
            WaitFor(() =>
            {
                Render(main);
                return main.FocusManager?.GetFocusedElement() is Visual focused && main.GetControl<ListBox>("RungList").IsVisualAncestorOf(focused);
            });
            Assert.Equal(1, activated);
            Assert.True(window.IsVisible);
            window.Close();
            Assert.Empty(main.Investigations);
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§8.4: an investigation already shown comes in front, restored, rather than in a second window, and closes with InterCat's window")]
    public void AnInvestigationShownAgainComesInFront()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        _ = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "alpha", 4), Committed);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        InvestigationWindow window = main.ShowInvestigation(workspace);
        WaitFor(() => window.View is not null);

        // Opened again - from the menu, the command line or a package saved - its window comes back in front, restored, since
        // two windows of one investigation would each write its file.
        int activated = 0;
        window.Activated += (_, _) => activated++;
        window.WindowState = WindowState.Minimized;
        Assert.Same(window, main.ShowInvestigation(Path.Combine(root.Path, ".", "case" + InvestigationWorkspace.Extension)));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Same(window, Assert.Single(main.Investigations));
        Assert.Equal((WindowState.Normal, 1), (window.WindowState, activated));

        // Another investigation has a window of its own; each opens its sessions in InterCat's window, so none outlives it.
        InvestigationWindow other = Assert.IsType<InvestigationWindow>(
            main.StartInvestigation(Path.Combine(root.Path, "other" + InvestigationWorkspace.Extension)));
        Assert.NotSame(window, other);
        Assert.Equal(2, main.Investigations.Count);
        int closed = 0;
        window.Closed += (_, _) => closed++;
        other.Closed += (_, _) => closed++;
        main.Close();
        Assert.Equal(2, closed);
        Assert.Empty(main.Investigations);
    }

    [AvaloniaFact(DisplayName = "R13: a zoom of the merged time asked for and left before it was read is never drawn, so a key pressed meanwhile acts on the view asked for")]
    public void AZoomLeftBeforeItIsReadIsNeverDrawn()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        Guid a = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "alpha", 4), Committed).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "beta", 6), Committed).SessionId;
        InvestigationWorkspace.Align(workspace, b, 0, a, 1_000_000_000, 1_000, 0, null, Committed);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            window.ShowTab(2);
            WaitFor(() => window.Timeline is not null);
            TimeRange whole = window.Timeline!.Interval!.Value;
            int drawn = window.TimelinesDrawn;

            // A quarter asked for, and the whole again before the quarter was read: the quarter is never drawn, only the whole.
            Task quarter = window.ZoomToAsync(new TimeRange(whole.StartTicks, whole.StartTicks + ((whole.EndTicks - whole.StartTicks) / 4)));
            Task again = window.ZoomToAsync(null);
            WaitFor(() => quarter.IsCompleted && again.IsCompleted);
            Assert.Equal((drawn + 1, whole), (window.TimelinesDrawn, window.Timeline!.Interval!.Value));
            Assert.Null(window.Zoom);
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R13: the merged time chooses the column under a click, zooms around the pointer under the wheel, and opens a column double-clicked in InterCat's window, in front")]
    public void TheMergedTimeAnswersThePointer()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        string alphaPath = Datagrams(root.Path, "alpha", 4);
        string betaPath = Datagrams(root.Path, "beta", 6);
        Guid a = InvestigationWorkspace.Add(workspace, alphaPath, Committed).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, betaPath, Committed).SessionId;
        InvestigationWorkspace.Align(workspace, b, 0, a, 1_000_000_000, 1_000, 0, null, Committed);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            window.ShowTab(2);
            WaitFor(() => window.Timeline is not null);
            Render(window);
            InvestigationTimelineControl chart = window.GetVisualDescendants().OfType<InvestigationTimelineControl>().Single();
            TimeRange whole = window.Timeline!.Interval!.Value;
            int columns = window.Timeline.Lanes[1].Buckets.Count;
            int busy = window.Timeline.Lanes[1].Buckets.ToList().FindIndex(bucket => bucket.ObservationCount > 0);
            double plot = chart.Bounds.Width - InvestigationTimelineControl.LabelWidth - 12;
            Point Where(int lane, double column) => chart.TranslatePoint(new Point(
                InvestigationTimelineControl.LabelWidth + (column * plot / columns),
                InvestigationTimelineControl.AxisHeight + ((lane + 0.5) * InvestigationTimelineControl.LaneHeight)), window)!.Value;

            // A click chooses the column under the pointer, and the merged time takes the keyboard; on the axis it chooses none.
            Point at = Where(1, busy + 0.5);
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Assert.StartsWith($"Session {Short(b)}, column {busy + 1} of {columns}: ", window.ColumnReadout, StringComparison.Ordinal);
            Assert.True(chart.IsFocused);
            Point axis = chart.TranslatePoint(new Point(InvestigationTimelineControl.LabelWidth + 20, 4), window)!.Value;
            window.MouseDown(axis, MouseButton.Left);
            window.MouseUp(axis, MouseButton.Left);
            Assert.StartsWith($"Session {Short(b)}, column {busy + 1} of {columns}: ", window.ColumnReadout, StringComparison.Ordinal);

            // The wheel zooms in around the instant under the pointer, which stays under it, and out again to the whole.
            long under = whole.StartTicks + (long)((whole.EndTicks - whole.StartTicks) * ((busy + 0.5) / columns));
            window.MouseWheel(at, new Vector(0, 1));
            WaitFor(() => window.Zoom is not null && window.Timeline!.Interval != whole);
            TimeRange zoomed = window.Timeline!.Interval!.Value;
            long half = (whole.EndTicks - whole.StartTicks) / 2;
            Assert.InRange(zoomed.EndTicks - zoomed.StartTicks, half - 1, half + 1);
            double before = (under - whole.StartTicks) / (double)(whole.EndTicks - whole.StartTicks);
            double after = (under - zoomed.StartTicks) / (double)(zoomed.EndTicks - zoomed.StartTicks);
            Assert.InRange(after - before, -2.0 / columns, 2.0 / columns);
            window.MouseWheel(at, new Vector(0, -1));
            WaitFor(() => window.Zoom is null && window.Timeline!.Interval == whole);

            // A double click opens the column under the pointer in InterCat's window, which comes in front with the keyboard on
            // its ranked table.
            int activated = 0;
            main.Activated += (_, _) => activated++;
            Render(window);
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            WaitFor(() => main.GetControl<TextBlock>("CaptureSessionPath").Text == betaPath);
            WaitFor(() =>
            {
                Render(main);
                return main.FocusManager?.GetFocusedElement() is Visual focused && main.GetControl<ListBox>("RungList").IsVisualAncestorOf(focused);
            });
            Assert.Equal(1, activated);
            Assert.StartsWith($"Opened Session {Short(b)} in the InterCat window, zoomed to ", Named<TextBlock>(window, "Investigation status").Text,
                StringComparison.Ordinal);

            // Come back to, the investigation has the keyboard where it was, on the merged time.
            window.Activate();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(chart.IsFocused);

            // A third click in time is no second double click: it counted on past two, and opened the column again.
            window.MouseDown(axis, MouseButton.Left);
            window.MouseUp(axis, MouseButton.Left);
            for (int click = 0; click < 3; click++)
            {
                window.MouseDown(at, MouseButton.Left);
                window.MouseUp(at, MouseButton.Left);
            }

            WaitFor(() => activated >= 2);
            for (int settle = 0; settle < 20; settle++)
            {
                Render(main);
                Thread.Sleep(5);
            }

            Assert.Equal(2, activated);

            // The keys the cursor answers to beside those that walk it: Left, Home and Up move it, + and - zoom around its column
            // as Zoom in and Zoom out do, 0 shows the whole, and Enter opens the column, alpha's first, so.
            window.Activate();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(chart.IsFocused);
            window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
            Assert.StartsWith($"Session {Short(b)}, column {busy} of {columns}: ", window.ColumnReadout, StringComparison.Ordinal);
            window.KeyPressQwerty(PhysicalKey.Home, RawInputModifiers.None);
            Assert.StartsWith($"Session {Short(b)}, column 1 of {columns}: ", window.ColumnReadout, StringComparison.Ordinal);
            window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
            Assert.StartsWith($"Session {Short(a)}, column 1 of {columns}: ", window.ColumnReadout, StringComparison.Ordinal);
            window.KeyPressQwerty(PhysicalKey.Equal, RawInputModifiers.None);
            WaitFor(() => window.Zoom is not null);
            window.KeyPressQwerty(PhysicalKey.Minus, RawInputModifiers.None);
            WaitFor(() => window.Zoom is null && window.Timeline!.Interval == whole);
            window.KeyPressQwerty(PhysicalKey.NumPadAdd, RawInputModifiers.None);
            WaitFor(() => window.Zoom is not null);
            window.KeyPressQwerty(PhysicalKey.Digit0, RawInputModifiers.None);
            WaitFor(() => window.Zoom is null && window.Timeline!.Interval == whole);
            Render(window);
            chart.Focus();
            window.KeyPressQwerty(PhysicalKey.Home, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            WaitFor(() => main.GetControl<TextBlock>("CaptureSessionPath").Text == alphaPath);
            WaitFor(() => activated == 3);
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: each investigation dialog opened from its button records what it is for, says so, and gives the keyboard back to that button")]
    public async Task EachDialogOpenedFromItsButtonDoesWhatItIsFor()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        _ = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "alpha", 4), Committed);
        Guid b = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "beta", 6), Committed).SessionId;
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            ListBox list = Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one");
            list.SelectedIndex = 1;
            TextBlock status = Named<TextBlock>(window, "Investigation status");

            // Align: Enter in its fields aligns the session, which stays selected, the status says so, and Align has the keyboard.
            Button align = Named<Button>(window, "Align the selected session to the investigation's time");
            InvestigationAlignWindow aligning = Open<InvestigationAlignWindow>(window, align);
            Field(aligning, "Its instant (").Text = "2";
            Field(aligning, "is the reference's instant, in seconds").Text = "5";
            Assert.True(Field(aligning, "Its instant (").Focus());
            aligning.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            WaitFor(() => status.Text == $"Session {Short(b)} aligned to the investigation's time.");
            Assert.Contains(InvestigationWorkspace.Read(workspace).Alignments, alignment => alignment.SessionId == b);
            Assert.Equal(1, list.SelectedIndex);
            Assert.Same(align, window.FocusManager?.GetFocusedElement());

            // One host, cancelled, records nothing.
            Button oneHost = Named<Button>(window, "One host…");
            Escape(window, Open<InvestigationHostWindow>(window, oneHost));
            Assert.Same(oneHost, window.FocusManager?.GetFocusedElement());
            Assert.Empty(InvestigationWorkspace.Read(workspace).HostEquivalences);

            // A translation stated finds the candidate joins again as its dialog closes, since they mirror through it.
            window.ShowTab(1);
            Assert.Null(window.Candidates);
            Button translations = Named<Button>(window, "Known translations…");
            InvestigationTranslationsWindow translating = Open<InvestigationTranslationsWindow>(window, translations);
            translating.Enter("203.0.113.7:8443", "10.0.0.2:443", "the router forwards 8443");
            Assert.True(await translating.StateAsync());
            Escape(window, translating);
            WaitFor(() => window.Candidates is not null);
            Assert.Same(translations, window.FocusManager?.GetFocusedElement());

            // A view chosen in Views shows it.
            window.ShowTab(2);
            WaitFor(() => window.Timeline is not null);
            TimeRange whole = window.Timeline!.Interval!.Value;
            TimeRange part = new(whole.StartTicks, whole.StartTicks + ((whole.EndTicks - whole.StartTicks) / 4));
            await window.ZoomToAsync(part);
            Button views = Named<Button>(window, "Views…");
            InvestigationViewsWindow saving = Open<InvestigationViewsWindow>(window, views);
            saving.NameIt("the first quarter");
            Assert.True(await saving.SaveAsync());
            Escape(window, saving);
            await window.ZoomToAsync(null);
            InvestigationViewsWindow choosing = Open<InvestigationViewsWindow>(window, views);
            WaitFor(() => choosing.Listed.Count == 1);
            choosing.Select(0);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            choosing.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            WaitFor(() => window.OwnedWindows.Count == 0 && window.Zoom == part);

            // Compare instants opens on its sessions, and Escape gives the keyboard back.
            Button compare = Named<Button>(window, "Compare instants…");
            Escape(window, Open<InvestigationCompareWindow>(window, compare));
            Assert.Same(compare, window.FocusManager?.GetFocusedElement());

            // A note's words, saved with Ctrl+Enter, are listed, and the status says so.
            window.ShowTab(3);
            Button addNote = Named<Button>(window, "Add a note, pinned at the timeline's chosen column when there is one");
            InvestigationNoteWindow writing = Open<InvestigationNoteWindow>(window, addNote);
            writing.Enter("The retry storm begins here.");
            writing.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
            WaitFor(() => status.Text == "The note is added.");
            Assert.Equal("The retry storm begins here.", Assert.Single(InvestigationWorkspace.Read(workspace).Notes).Text);
            Assert.Same(addNote, window.FocusManager?.GetFocusedElement());
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    /// <summary>Opens a dialog as a person does, from its button with the keyboard on it, and the dialog it opened.</summary>
    private static T Open<T>(Window window, Button button)
        where T : Window
    {
        Assert.True(button.Focus());
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitFor(() => window.OwnedWindows.OfType<T>().Any());
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return window.OwnedWindows.OfType<T>().Single();
    }

    /// <summary>Closes a dialog with Escape, as a person cancels one, and waits until it is gone.</summary>
    /// <summary>
    /// Presses Escape in <paramref name="dialog"/> once it has the keyboard, as a person presses it, and waits for it to
    /// close: until a dialog places the keyboard this platform gives a key to whatever had it before.
    /// </summary>
    private static void Escape(Window window, Window dialog)
    {
        WaitFor(() => dialog.FocusManager?.GetFocusedElement() is Visual focusedInside && dialog.IsVisualAncestorOf(focusedInside));
        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        WaitFor(() => window.OwnedWindows.Count == 0);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact(DisplayName = "R22: the investigation window states a known address translation, and a candidate joins through it")]
    public async Task TheWindowStatesAKnownTranslation()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        _ = InvestigationWorkspace.Add(workspace, Session(root.Path, "client", ["10.0.0.1:50000", "203.0.113.7:8443"], 100), Committed);
        _ = InvestigationWorkspace.Add(workspace, Session(root.Path, "server", ["10.0.0.2:443", "10.0.0.1:50000"], 200), Committed);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            window.ShowTab(1);
            await window.FindCandidatesAsync();
            Assert.Empty(window.Candidates!.Rows);

            // The client dialed a forward's public endpoint; stated, the two ends meet through it, and say so.
            InvestigationTranslationsWindow dialog = window.TranslationsDialog();
            dialog.Show(window);
            dialog.Load();

            // With none stated, there is nothing to withdraw; stating one says what it does.
            const string Writing = "A change to the investigation is being written.";
            const string States = "State that the seen endpoint is the other";
            Button state = Named<Button>(dialog, "State translation");
            Assert.Equal("No translation is stated.", Offers.WhyNot(Named<Button>(dialog, "Withdraw the selected translation")));
            Assert.Equal(States, Offers.Offered(state));
            dialog.Enter("127.0.0.1:80", "10.0.0.2:80");
            Assert.False(await dialog.StateAsync());
            Assert.Contains("loopback", Named<TextBlock>(dialog, "Translation status").Text, StringComparison.Ordinal);
            dialog.Enter("203.0.113.7:8443", "10.0.0.2:443", "the router forwards 8443");

            // State translation waits while the statement is written, saying so; stated, the translation is to be chosen
            // before it is withdrawn.
            List<(bool, string?)> stating = Offers.Heard(state);
            Assert.True(await dialog.StateAsync());
            Assert.Equal([(false, Writing), (true, States)], stating);
            Assert.Equal("Choose a translation first.", Offers.WhyNot(Named<Button>(dialog, "Withdraw the selected translation")));
            Assert.Equal(("203.0.113.7:8443", "10.0.0.2:443"), (Assert.Single(dialog.Listed).Seen, dialog.Listed[0].Is));
            Save(dialog, "investigation-translations.png");
            dialog.Close();
            await window.FindCandidatesAsync();
            InvestigationCandidateRow joined = Assert.Single(window.Candidates!.Rows);
            Assert.Contains("A person stated that 203.0.113.7:8443 is 10.0.0.2:443", joined.Evidence, StringComparison.Ordinal);

            // Withdrawn, the two ends mirror nothing again; the statement is kept as a revision.
            InvestigationTranslationsWindow again = window.TranslationsDialog();
            again.Show(window);
            again.Load();
            again.Select(0);
            Button withdraw = Named<Button>(again, "Withdraw the selected translation");
            Assert.Null(Offers.Offered(withdraw));
            List<(bool, string?)> withdrawing = Offers.Heard(withdraw);
            Assert.True(await again.WithdrawSelectedAsync());
            Assert.Empty(again.Listed);
            Assert.Equal([(false, Writing), (false, "No translation is stated.")], withdrawing);
            again.Close();
            await window.FindCandidatesAsync();
            Assert.Empty(window.Candidates!.Rows);
            Assert.Equal(2, InvestigationWorkspace.Read(workspace).AddressTranslations.Count);
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R22: the investigation window keeps notes: pinned at an instant, reworded, shown on the timeline, removed")]
    public async Task TheWindowKeepsNotes()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        Guid a = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "alpha", 4), Committed).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "beta", 6), Committed).SessionId;
        InvestigationWorkspace.Align(workspace, b, 0, a, 1_000_000_000, 1_000, 0, null, Committed);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            CultureInfo culture = CultureInfo.CurrentCulture;

            // A note pinned at beta's 100 µs reads where the investigation's time places it.
            InvestigationNoteWindow dialog = window.NoteDialog(reword: false)!;
            dialog.Show(window);

            // Unpinned, where it would be pinned is not offered, and says how to offer it.
            const string Unpinned = "Choose Pin it at an instant of a session to say where.";
            Assert.Equal(Unpinned, Offers.WhyNot(Named<ComboBox>(dialog, "The session the note is pinned in")));
            Assert.Equal(Unpinned, Offers.WhyNot(Named<TextBox>(dialog, "The instant it is pinned at, in seconds of that session's time")));
            dialog.Enter("Beta's datagrams begin", b, 0.0001m.ToString(culture));
            Assert.Null(Offers.Offered(Named<ComboBox>(dialog, "The session the note is pinned in")));

            // Add note waits while the note is written, saying so.
            Button add = Named<Button>(dialog, "Add note");
            List<(bool, string?)> adding = Offers.Heard(add);
            Assert.True(await dialog.SaveAsync());
            Assert.Equal([(false, "The note is being written to the investigation."), (true, "Add the note")], adding);
            await window.RefreshAsync();
            InvestigationNoteRow row = Assert.Single(window.View!.Notes);
            Assert.Equal("Beta's datagrams begin", row.Text);
            Assert.StartsWith($"pinned at session {Short(b)}'s {0.0001m.ToString("0.000######", culture)} s, the investigation's "
                + $"{1.0001m.ToString("0.000######", culture)} s within ±", row.Where, StringComparison.Ordinal);
            window.ShowTab(3);
            window.SelectNote(0);
            Save(window, "investigation-notes.png");

            // Reworded, it keeps its pin; shown on the timeline, the timeline zooms to it with the cursor on its column.
            InvestigationNoteWindow reword = window.NoteDialog(reword: true)!;
            reword.Show(window);
            reword.Enter("Beta's first datagram");
            Assert.True(await reword.SaveAsync());
            await window.RefreshAsync();
            Assert.Equal(("Beta's first datagram", row.At), (window.View!.Notes[0].Text, window.View.Notes[0].At));
            window.SelectNote(0);
            await window.ShowSelectedNoteAsync();
            WaitFor(() => window.Zoom is not null && window.Timeline!.Notes.Count == 1);
            Assert.StartsWith($"Session {Short(b)}, column ", window.ColumnReadout, StringComparison.Ordinal);
            Assert.Contains(window.TimelineSentences, sentence => sentence.StartsWith($"Note {window.View.Notes[0].NoteId.ToString("N")[..8]}, pinned at session {Short(b)}", StringComparison.Ordinal));

            // Removed, it is gone from the list and the timeline; its revisions stay in the file.
            window.SelectNote(0);
            await window.RemoveSelectedNoteAsync();
            Assert.Empty(window.View!.Notes);
            Assert.Equal(3, InvestigationWorkspace.Read(workspace).Notes.Count);
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R22: the investigation window saves the view shown, and shows it again")]
    public async Task TheWindowSavesAndShowsViews()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        Guid a = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "alpha", 4), Committed).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "beta", 6), Committed).SessionId;
        InvestigationWorkspace.Align(workspace, b, 0, a, 1_000_000_000, 1_000, 0, null, Committed);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            window.ShowTab(2);
            WaitFor(() => window.Timeline is not null);
            TimeRange whole = window.Timeline!.Interval!.Value;

            // Zoomed in, the interval shown is saved under a name.
            await window.ZoomToAsync(new TimeRange(whole.StartTicks, whole.StartTicks + ((whole.EndTicks - whole.StartTicks) / 4)));
            TimeRange zoomed = window.Timeline!.Interval!.Value;
            InvestigationViewsWindow dialog = window.ViewsDialog();
            dialog.Show(window);
            dialog.Load();

            // With no view saved, none is offered to show or remove, and each says so; saving says what it does.
            Assert.Equal("No view is saved yet.", Offers.WhyNot(Named<Button>(dialog, "Show the selected view on the timeline")));
            Assert.Equal("No view is saved yet.", Offers.WhyNot(Named<Button>(dialog, "Remove the selected view")));
            Assert.Equal("Save the interval the timeline shows now under this name", Offers.Offered(Named<Button>(dialog, "Save the view shown")));
            dialog.NameIt("Alpha's datagrams");
            Assert.True(await dialog.SaveAsync());
            Assert.Equal("Choose a view first.", Offers.WhyNot(Named<Button>(dialog, "Show the selected view on the timeline")));
            Assert.Equal("Choose a view first.", Offers.WhyNot(Named<Button>(dialog, "Remove the selected view")));
            Assert.Equal(("Alpha's datagrams", zoomed, true), (Assert.Single(dialog.Listed).View.Name, dialog.Listed[0].View.Interval!.Value, dialog.Listed[0].Current));
            Save(dialog, "investigation-views.png");
            dialog.Close();

            // Back on the whole, the saved view shows its interval again.
            await window.ZoomToAsync(null);
            WorkspaceView saved = Assert.Single(InvestigationWorkspace.ViewsInForce(InvestigationWorkspace.Read(workspace)));
            await window.ZoomToAsync(saved.Interval);
            Assert.Equal(zoomed, window.Timeline!.Interval);

            // Removed, it is gone from the list; its revisions stay in the file.
            InvestigationViewsWindow again = window.ViewsDialog();
            again.Show(window);
            again.Load();
            again.Select(0);
            Assert.Null(Offers.Offered(Named<Button>(again, "Show the selected view on the timeline")));
            Assert.Null(Offers.Offered(Named<Button>(again, "Remove the selected view")));
            Assert.True(await again.RemoveSelectedAsync());
            Assert.Empty(again.Listed);
            again.Close();
            Assert.Equal(2, InvestigationWorkspace.Read(workspace).Views.Count);
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R22: in the saved views Enter in the name saves the view shown, and Enter on a view shows it as a double click does")]
    public async Task SavedViewsAnswerEnter()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        Guid a = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "alpha", 4), Committed).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "beta", 6), Committed).SessionId;
        InvestigationWorkspace.Align(workspace, b, 0, a, 1_000_000_000, 1_000, 0, null, Committed);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            window.ShowTab(2);
            WaitFor(() => window.Timeline is not null);
            TimeRange whole = window.Timeline!.Interval!.Value;
            await window.ZoomToAsync(new TimeRange(whole.StartTicks, whole.StartTicks + ((whole.EndTicks - whole.StartTicks) / 4)));
            TimeRange zoomed = window.Timeline!.Interval!.Value;

            // Typed into the name box, Enter saves the view shown under that name.
            InvestigationViewsWindow dialog = window.ViewsDialog();
            Task<TimeRange?> shown = dialog.ShowDialog<TimeRange?>(window);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(dialog.GetVisualDescendants().OfType<TextBox>().Single().Focus());
            dialog.NameIt("The first datagrams");
            dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            WaitFor(() => dialog.Listed.Count == 1);
            Assert.Equal(("The first datagrams", zoomed), (dialog.Listed[0].View.Name, dialog.Listed[0].View.Interval!.Value));

            // The dialog says the interval shown, and lists the view by its own, as the session's timeline says one.
            string said = WorkspaceTime.FormatRange(zoomed, CultureInfo.CurrentCulture);
            Assert.Equal("The first datagrams · " + said, dialog.Listed[0].ToString());
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == $"The timeline shows {said} now.");

            // Enter on the view with the keyboard, chosen or not, shows it: the dialog answers with its interval.
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            ListBox listed = dialog.GetVisualDescendants().OfType<ListBox>().Single();
            Assert.Null(listed.SelectedItem);
            Assert.True(listed.ContainerFromIndex(0)!.Focus());
            Assert.Null(listed.SelectedItem);
            dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            WaitFor(() => shown.IsCompleted);
            Assert.Equal(zoomed, await shown);

            // Once the time reference changes, the view is kept but is not of this time: Enter on it says so, and the
            // dialog stays open rather than showing nothing.
            InvestigationWorkspace.Withdraw(workspace, b, Committed);
            InvestigationWorkspace.Align(workspace, a, 0, b, 0, 1_000, 1, null, Committed);
            InvestigationViewsWindow stale = window.ViewsDialog();
            stale.Show(window);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.False(Assert.Single(stale.Listed).Current);
            Assert.Equal("'The first datagrams' was saved on another time reference, so this one cannot show it.",
                Offers.WhyNot(Named<Button>(stale, "Show the selected view on the timeline")));
            Assert.Null(Offers.Offered(Named<Button>(stale, "Remove the selected view")));
            Assert.True(stale.GetVisualDescendants().OfType<ListBox>().Single().ContainerFromIndex(0)!.Focus());
            stale.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Assert.True(stale.IsVisible);
            Assert.Equal("'The first datagrams' was saved on another time reference, so this one cannot show it.", stale.Status);
            stale.Close();
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R21: the investigation window says where every compared capture covered TCP and UDP, and in the caution ink where one fell short")]
    public async Task TheWindowSaysTheComparedCapturesCoveredBoth()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        InvestigationWorkspace.Add(workspace, Session(root.Path, "client", ["10.0.0.1:50000", "10.0.0.2:443"], 100, TransportLedger(tcp: true, udp: true)), Committed);
        InvestigationWorkspace.Add(workspace, Session(root.Path, "server", ["10.0.0.3:443", "10.0.0.1:50001"], 200, TransportLedger(tcp: true, udp: true)), Committed);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            window.ShowTab(1);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await window.FindCandidatesAsync();
            Render(window);

            // No connection mirrors another, and both captures covered both mechanisms: no caution, and the notes say why the
            // absence means something.
            Assert.Equal("No candidate join. " + WorkspaceCorrelation.NoCandidate(coverageShort: false), window.Candidates!.Summary);
            Assert.False(window.Candidates.CoverageShort);
            Assert.False(Named<TextBlock>(window, "What the compared captures did not cover").IsEffectivelyVisible);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible
                && text.Text?.StartsWith("Every compared capture covered TCP and UDP over what it recorded, so a connection with no candidate "
                    + "has no mirror in what they recorded. ", StringComparison.Ordinal) == true);

            // A third capture, with no ledger, leaves what it covered unknown: said in the caution ink, and no candidate is then
            // no proof there was none.
            Guid third = InvestigationWorkspace.Add(workspace, Session(root.Path, "third", ["10.0.0.5:443", "10.0.0.1:50002"], 300), Committed).SessionId;
            await window.FindCandidatesAsync();
            Render(window);
            Assert.Equal("No candidate join. " + WorkspaceCorrelation.NoCandidate(coverageShort: true), window.Candidates!.Summary);
            TextBlock coverage = Named<TextBlock>(window, "What the compared captures did not cover");
            Assert.True(coverage.IsEffectivelyVisible);
            Assert.Equal($"What session {Short(third)} covered of TCP and UDP is unknown (this generation publishes no coverage ledger), so the "
                + "mirror of a connection may be missing from it.", coverage.Text);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text?.StartsWith("Every compared capture", StringComparison.Ordinal) == true);
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R22: the investigation window lists candidate joins with their evidence, none established")]
    public async Task TheWindowListsCandidateJoins()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        InvestigationWorkspace.Add(workspace, Session(root.Path, "client", ["10.0.0.1:50000", "10.0.0.2:443"], 100), Committed);
        InvestigationWorkspace.Add(workspace, Session(root.Path, "server", ["10.0.0.2:443", "10.0.0.1:50000"], 200), Committed);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            window.ShowTab(1);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await window.FindCandidatesAsync();
            InvestigationCandidates found = window.Candidates!;
            InvestigationCandidateRow row = Assert.Single(found.Rows);
            Assert.Equal("TCP 10.0.0.1:50000 ⇄ 10.0.0.2:443 · lifetimes not comparable", row.Title);

            // Each end says what its records measured each way, and a way they recorded nothing as such, never as 0 B (R21).
            Assert.EndsWith("(PID 100), 64 B sent, no receive recorded, connected and closed in the capture", row.First, StringComparison.Ordinal);
            Assert.StartsWith("Candidate join: TCP 10.0.0.1:50000", row.AccessibleName, StringComparison.Ordinal);
            Assert.Equal("1 candidate join, none established by evidence; 0 not the only match of a connection.", found.Summary);
            Assert.Equal(found.Summary, Named<TextBlock>(window, "Candidate joins status").Text);

            // Neither capture has a ledger, so what each covered of TCP and UDP is unknown: said in the caution ink, since a
            // mirror may be missing for that alone (R21).
            TextBlock coverage = Named<TextBlock>(window, "What the compared captures did not cover");
            Render(window);
            Assert.True(found.CoverageShort && coverage.IsEffectivelyVisible);
            Assert.Equal(string.Join(" ", found.Coverage), coverage.Text);
            Assert.Matches("^What sessions [0-9a-f]{8} and [0-9a-f]{8} covered of TCP and UDP is unknown \\(this generation publishes no coverage "
                + "ledger\\), so the mirror of a connection may be missing from them\\.$", Assert.Single(found.Coverage));
            Assert.Equal(ThemeResources.ToColor(ThemePalette.Status(ThemeResources.CurrentMode).Caution), RenderedPixels.ColorOf(coverage.Foreground));
            ListBox list = Named<ListBox>(window, "Candidate joins between the sessions; none is established");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(row.AccessibleName, AutomationProperties.GetName(list.ContainerFromIndex(0)!));

            // Accepted as your decision, it says so, and can be withdrawn; every revision stays in the file.
            window.SelectCandidate(0);
            Button accept = Named<Button>(window, "Accept as one connection");
            Assert.True(accept.IsEnabled);
            Assert.False(Named<Button>(window, "Withdraw decision").IsEnabled);
            await window.DecideSelectedAsync(WorkspaceJoinDecision.Accepted);
            Assert.EndsWith("· accepted by a person", window.Candidates!.Rows[0].Title, StringComparison.Ordinal);
            Assert.Equal("1 candidate join, none established by evidence; 1 accepted and 0 rejected by a person; 0 not the only match of a connection.",
                window.Candidates.Summary);
            window.SelectCandidate(0);
            Assert.False(accept.IsEnabled);
            Assert.True(Named<Button>(window, "Withdraw decision").IsEnabled);
            Save(window, "investigation-candidates.png");
            await window.DecideSelectedAsync(WorkspaceJoinDecision.Withdrawn);
            Assert.Null(window.Candidates!.Rows[0].Decision);
            Assert.Equal(2, InvestigationWorkspace.Read(workspace).Joins.Count);
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R21: the investigation window's timeline places each session on the investigation's time, and says it in words")]
    public async Task TheWindowDrawsTheInvestigationsTimeline()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        Guid a = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "alpha", 4), Committed).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "beta", 6), Committed).SessionId;
        _ = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "gamma", 2), Committed);
        InvestigationWorkspace.Align(workspace, b, 0, a, 1_000_000_000, 500_000, 10, null, Committed);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            window.ShowTab(2);
            WaitFor(() => window.Timeline is not null);
            InvestigationTimelineView timeline = window.Timeline!;
            Assert.Equal([true, true, false], timeline.Lanes.Select(lane => lane.Placed));
            Assert.Equal((4L, 6L), (timeline.Lanes[0].Records, timeline.Lanes[1].Records));
            Assert.EndsWith("of the investigation's time, the investigation's own clock, exactly. Coverage over its 160 columns: all unknown. "
                + "Read at its generation 1.", window.TimelineSentences[0], StringComparison.Ordinal);
            Assert.Contains("records, from ", window.TimelineSentences[1], StringComparison.Ordinal);
            Assert.Contains("placed within ±", window.TimelineSentences[1], StringComparison.Ordinal);
            Assert.EndsWith("not placed: not aligned to the investigation's time.", window.TimelineSentences[2], StringComparison.Ordinal);
            Assert.Equal(string.Join("\n", window.TimelineSentences),
                Named<TextBlock>(window, "The investigation's timeline, each session in words").Text);
            Save(window, "investigation-timeline.png");
            await Task.CompletedTask;
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R21: the investigation's timeline hatches where a session's capture saw nothing, and says so of a column and a lane")]
    public async Task TheTimelineHatchesWhereACaptureSawNothing()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        Guid a = InvestigationWorkspace.Add(workspace, Ledgered(root.Path, "alpha"), Committed).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "beta", 2), Committed).SessionId;
        InvestigationWorkspace.Align(workspace, b, 0, a, 0, 1_000, 0, null, Committed);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            window.ShowTab(2);
            WaitFor(() => window.Timeline is not null);

            // The middle of each run of columns: of none where the capture covered them, past its readings, of none in its
            // lossy epoch, and of its records there.
            List<TimelineBucket> buckets = [.. window.Timeline!.Lanes[0].Buckets];
            int Middle(Predicate<TimelineBucket> run) => (buckets.FindIndex(run) + buckets.FindLastIndex(run)) / 2;
            int quiet = Middle(bucket => bucket is { ObservationCount: 0, Coverage: CoverageState.Covered });
            int unknown = Middle(bucket => bucket.Coverage == CoverageState.UnknownCoverage);
            int gap = Middle(bucket => bucket is { ObservationCount: 0, Coverage: CoverageState.PartialGap });
            int seen = buckets.FindIndex(bucket => bucket is { ObservationCount: > 0, Coverage: CoverageState.PartialGap });
            Assert.True(quiet > 0 && unknown > quiet && gap > unknown && seen > gap, $"{quiet}, {unknown}, {gap}, {seen}");
            Assert.Equal((CoverageState.Covered, CoverageState.UnknownCoverage, CoverageState.PartialGap),
                (buckets[quiet].Coverage, buckets[unknown].Coverage, buckets[gap].Coverage));

            // A column the capture covered is drawn plain, though it holds none; one past its readings has a thin strip along
            // the lane's foot, as the session's own timeline draws it; one where it lost records is hatched through the
            // lane, over any records it did see there.
            InvestigationTimelineControl chart = window.GetVisualDescendants().OfType<InvestigationTimelineControl>().Single();
            const double Lane = InvestigationTimelineControl.LaneHeight;
            Assert.Null(chart.CoverageCell(0, quiet));
            Rect strip = chart.CoverageCell(0, unknown)!.Value;
            Assert.Equal((InvestigationTimelineControl.AxisHeight + Lane - 9, 5d), (strip.Top, strip.Height));
            Rect hatched = chart.CoverageCell(0, gap)!.Value;
            Assert.Equal((InvestigationTimelineControl.AxisHeight + 4, Lane - 8), (hatched.Top, hatched.Height));
            Assert.Equal(hatched.Height, chart.CoverageCell(0, seen)!.Value.Height);
            double width = (chart.Bounds.Width - InvestigationTimelineControl.LabelWidth - 12) / buckets.Count;
            Assert.Equal((InvestigationTimelineControl.LabelWidth + (unknown * width) + 0.5, width - 1), (strip.Left, strip.Width));
            Assert.Equal((InvestigationTimelineControl.LabelWidth + (gap * width) + 0.5, width - 1), (hatched.Left, hatched.Width));
            Assert.Equal([null, null, null], new[] { chart.CoverageCell(0, -1), chart.CoverageCell(0, buckets.Count), chart.CoverageCell(2, unknown) });

            // Each run of such columns is hatched as one band, not a comb of columns: past the readings, then the lossy epoch;
            // and the other session, which has no ledger, unknown across its lane.
            Rect Spanning(int lane, CoverageState state) => chart.CoverageCell(lane, buckets.FindIndex(bucket => bucket.Coverage == state))!.Value
                .Union(chart.CoverageCell(lane, buckets.FindLastIndex(bucket => bucket.Coverage == state))!.Value);
            Assert.Equal([Spanning(0, CoverageState.UnknownCoverage), Spanning(0, CoverageState.PartialGap)], chart.CoverageRuns(0));
            Assert.Equal([chart.CoverageCell(1, 0)!.Value.Union(chart.CoverageCell(1, buckets.Count - 1)!.Value)], chart.CoverageRuns(1));
            Assert.Empty(chart.CoverageRuns(2));

            // Drawn as it is said: the caution ink only at the strip's foot, through the gap's lane, and nowhere in the
            // covered column.
            Render(window);
            Avalonia.Media.Imaging.WriteableBitmap frame = window.CaptureRenderedFrame()!;
            Color caution = ThemeResources.ToColor(ThemePalette.Status(ThemeResources.CurrentMode).Caution);
            Color extent = ThemeResources.ToColor(ThemePalette.Surfaces(ThemeResources.CurrentMode).Elevated);
            int Cautious(int column, double from, double to)
            {
                int inked = 0;
                for (double x = InvestigationTimelineControl.LabelWidth + (column * width) + 1.5; x < InvestigationTimelineControl.LabelWidth + ((column + 1) * width) - 1; x++)
                {
                    for (double y = InvestigationTimelineControl.AxisHeight + from; y < InvestigationTimelineControl.AxisHeight + to; y++)
                    {
                        Color pixel = RenderedPixels.At(frame, chart.TranslatePoint(new Point(x, y), window)!.Value);
                        inked += Distance(pixel, caution) < Distance(pixel, extent) ? 1 : 0;
                    }
                }

                return inked;
            }

            Assert.Equal(0, Cautious(quiet, 3, Lane - 3));
            Assert.True(Cautious(unknown, Lane - 10, Lane - 3) > 0, "The strip is not drawn where the capture's coverage is unknown.");
            Assert.Equal(0, Cautious(unknown, 3, Lane - 11));
            Assert.True(Cautious(gap, 8, Lane - 12) > 0, "The lane is not hatched where the capture lost records.");
            Save(window, "investigation-timeline-coverage.png");
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text?.Contains(
                "Hatching marks where its capture lost records or collected none, and a thin hatched strip where what it covered is unknown, "
                + "so an empty column there is no proof of inactivity.", StringComparison.Ordinal) == true);

            // Said as it is drawn: of a column where the cursor stands, of a column of none opened, and of the whole lane.
            window.ChooseColumn(0, quiet);
            Assert.EndsWith(", 0 records, coverage: covered.", window.ColumnReadout, StringComparison.Ordinal);
            window.ChooseColumn(0, unknown);
            Assert.EndsWith(", 0 records, coverage: unknown.", window.ColumnReadout, StringComparison.Ordinal);
            window.ChooseColumn(0, seen);
            string records = buckets[seen].ObservationCount == 1 ? "1 record" : $"{buckets[seen].ObservationCount} records";
            Assert.EndsWith($", {records}, coverage: partial gap, not extrapolated. Enter opens them in InterCat.", window.ColumnReadout, StringComparison.Ordinal);
            Assert.False(await window.OpenColumnAsync(new TimelineColumn(0, quiet)));
            Assert.Equal(string.Create(CultureInfo.CurrentCulture, $"Column {quiet + 1:N0} holds no records of Session {Short(a)}, so there is nothing of it to open: choose a column with records."),
                Named<TextBlock>(window, "Investigation status").Text);
            Assert.False(await window.OpenColumnAsync(new TimelineColumn(0, gap)));
            Assert.Equal(string.Create(CultureInfo.CurrentCulture, $"Column {gap + 1:N0} holds no records of Session {Short(a)}, and its capture has a partial gap there, so that is not proof of inactivity. There is nothing of it to open: choose a column with records."),
                Named<TextBlock>(window, "Investigation status").Text);
            int Columns(CoverageState state) => buckets.Count(bucket => bucket.Coverage == state);
            Assert.Contains(string.Create(CultureInfo.CurrentCulture, $" Coverage over its 160 columns: {Columns(CoverageState.Covered):N0} covered; "
                + $"{Columns(CoverageState.PartialGap):N0} partial gap, not extrapolated; {Columns(CoverageState.UnknownCoverage):N0} unknown. "),
                window.TimelineSentences[0], StringComparison.Ordinal);
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [Fact(DisplayName = "R21: a column of none opened in the investigation says why that is no proof of inactivity, in each coverage state's words")]
    public void AColumnOfNoneSaysWhatItsCaptureCovered()
    {
        string Said(CoverageState coverage) => InvestigationWindow.NothingToOpen(2, "Session 0a1b2c3d", coverage);
        Assert.Equal("Column 3 holds no records of Session 0a1b2c3d, so there is nothing of it to open: choose a column with records.",
            Said(CoverageState.Covered));
        Assert.Equal(
            [
                "its capture was at reduced fidelity there",
                "its capture has a partial gap there",
                "its capture collected nothing there",
                "what its capture covered there is unknown",
                "what its capture covered there is unknown",
            ],
            new[] { CoverageState.ReducedFidelity, CoverageState.PartialGap, CoverageState.NotCollected, CoverageState.UnknownCoverage, (CoverageState)9 }
                .Select(state => Said(state)["Column 3 holds no records of Session 0a1b2c3d, and ".Length..^", so that is not proof of inactivity. There is nothing of it to open: choose a column with records.".Length]));
    }

    [AvaloniaFact(DisplayName = "R22: the investigation window says when two captures of one host ran at once")]
    public void TheWindowSaysWhenCapturesOfOneHostRanAtOnce()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        Guid a = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "alpha", 4, "lab-1"), Committed).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "beta", 6, "lab-1"), Committed).SessionId;
        InvestigationWorkspace.Align(workspace, b, 0, a, 0, 1_000, 0, null, Committed);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            string said = Assert.Single(window.View!.Overlaps!);
            Assert.StartsWith($"Sessions {Short(a)} and {Short(b)} of one host ran at once for ", said, StringComparison.Ordinal);
            TextBlock shown = Named<TextBlock>(window, "Sessions of one host that ran at once, or may have");
            Assert.True(shown.IsVisible);
            Assert.Equal(said, shown.Text);
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R22: the investigation window packages the investigation with the sessions a person chooses")]
    public async Task TheWindowPackagesTheInvestigation()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        Guid a = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "alpha", 4), Committed).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "beta", 6), Committed).SessionId;
        string gamma = Datagrams(root.Path, "gamma", 2);
        Guid c = InvestigationWorkspace.Add(workspace, gamma, Committed).SessionId;
        Directory.Delete(gamma, recursive: true);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            Button package = Named<Button>(window, "Package this investigation with its sessions, to share it");
            Assert.True(package.IsEnabled);

            // The confirmation offers each session that can be copied, chosen, and says what the choice exposes.
            var prompt = new InvestigationPackageWindow(InvestigationPackage.Preview(workspace));
            prompt.Show(window);
            CheckBox Box(Guid session) => prompt.GetVisualDescendants().OfType<CheckBox>()
                .Single(box => (box.Content as TextBlock)?.Text?.Contains($" · session {Short(session)}", StringComparison.Ordinal) == true);

            // A session that can be copied says what choosing it copies; the one gone says why it cannot be.
            Assert.Equal($"Copy session {Short(a)}, found in alpha", Offers.Offered(Box(a)));
            Assert.Equal("It is missing, so it cannot be copied. Nothing is at this path: the session was moved or removed. "
                + "Relink the session where it is now.", Offers.WhyNot(Box(c)));
            Assert.Equal([a, b], prompt.Chosen);
            Assert.StartsWith("This saves the investigation case.icat-workspace with an exact copy of 2 of its 3 sessions",
                prompt.Statements[0], StringComparison.Ordinal);
            Assert.Equal(InvestigationPackage.Warning, prompt.Statements[^1]);
            Assert.Equal("Save unredacted package…", Named<Button>(prompt, "Save unredacted package…").Content);
            Save(prompt, "investigation-package-prompt.png");
            prompt.Choose(a, copied: false);
            Assert.StartsWith("This saves the investigation case.icat-workspace with an exact copy of 1 of its 3 sessions",
                prompt.Statements[0], StringComparison.Ordinal);
            prompt.Choose(b, copied: false);
            Assert.False(prompt.CanSave);
            Assert.Equal("Choose a session to copy first.", Offers.WhyNot(Named<Button>(prompt, "Save package…")));
            prompt.Choose(b, copied: true);
            Assert.True(prompt.CanSave);
            Assert.Equal("Save the package in a new folder you choose", Offers.Offered(Named<Button>(prompt, "Save unredacted package…")));
            Assert.Equal([b], prompt.Chosen);
            prompt.Close();

            // With no session where it was last found, none can be copied, and Save says so.
            string lone = Path.Combine(root.Path, "lone" + InvestigationWorkspace.Extension);
            InvestigationWorkspace.Create(lone, Committed);
            string delta = Datagrams(root.Path, "delta", 2);
            _ = InvestigationWorkspace.Add(lone, delta, Committed);
            Directory.Delete(delta, recursive: true);
            var nothing = new InvestigationPackageWindow(InvestigationPackage.Preview(lone));
            nothing.Show(window);
            Assert.Equal("No session can be copied: none is where it was last found. Relink one to copy it.",
                Offers.WhyNot(Named<Button>(nothing, "Save package…")));
            nothing.Close();

            // The package holds what was chosen, and the status line says where it is and that it verified.
            string destination = Path.Combine(root.Path, "shared", "case-package");
            InvestigationPackageResult? result = await window.WritePackageAsync(destination, [b]);
            Assert.NotNull(result);
            Assert.Equal([null, $"sessions/{Short(b)}-beta", null], result!.Members.Select(member => member.PackagedPath));
            TextBlock status = Named<TextBlock>(window, "Investigation status");
            Assert.Equal(InvestigationWindow.Saved(result), status.Text);
            Assert.Contains("2 sessions were not copied and stay references to relink.", status.Text, StringComparison.Ordinal);
            Assert.Equal(("Package…", true), (package.Content, package.IsEnabled));

            // Opened, it is the same investigation: the copy is found beside its file, the others where they were.
            InvestigationWindow opened = main.ShowInvestigation(result.WorkspacePath);
            WaitFor(() => opened.View is not null);
            Assert.Equal([$"Session {Short(a)}, present", $"Session {Short(b)}, present", $"Session {Short(c)}, missing"],
                opened.View!.Members.Select(row => row.Title));
            Assert.StartsWith(Path.Combine(destination, "sessions"), opened.View.Members[1].FullPath, StringComparison.OrdinalIgnoreCase);
            opened.Close();

            // A package goes only to a new folder: a second one there is refused, and the window says so.
            Assert.Null(await window.WritePackageAsync(destination, null));
            Assert.StartsWith("The investigation could not be packaged: ", status.Text, StringComparison.Ordinal);
            Assert.Contains("exists", status.Text, StringComparison.Ordinal);
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: an investigation action not offered says why on itself, and Enter on a session not where it was last found says so")]
    public async Task AnActionNotOfferedSaysWhy()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            // With no session yet, each action on one says to add one, and the rest what they wait for.
            InvestigationWindow empty = main.ShowInvestigation(workspace);
            WaitFor(() => empty.View is not null);
            foreach (string action in new[] { "Open in InterCat", "Relink…", "Align…", "Withdraw alignment", "One host…" })
                Assert.Equal("Add a session first.", WhyNot(empty, action));
            Assert.Equal("Add a session first: a package holds the investigation with its sessions.", WhyNot(empty, "Package…"));
            empty.ShowTab(1);
            Assert.Equal("Find candidate joins first.", WhyNot(empty, "Reject"));
            empty.ShowTab(2);
            await empty.ShowTimelineAsync();
            Assert.Equal("Add a session first.", WhyNot(empty, "Compare instants…"));
            Assert.Equal("No session has a place in the investigation's time yet: align one to another first.", WhyNot(empty, "Zoom in"));
            Assert.Equal("Choose a column of the timeline first.", WhyNot(empty, "Open this column"));
            empty.ShowTab(3);
            Assert.Equal("Add a note first.", WhyNot(empty, "Reword…"));
            empty.Close();

            // Two sessions of two hosts, the server's aligned to the client's clock, and a note about the whole investigation.
            Guid a = InvestigationWorkspace.Add(workspace, Session(root.Path, "client", ["10.0.0.1:50000", "10.0.0.2:443"], 100), Committed).SessionId;
            Guid b = InvestigationWorkspace.Add(workspace, Session(root.Path, "server", ["10.0.0.2:443", "10.0.0.1:50000"], 200), Committed).SessionId;
            InvestigationWorkspace.Align(workspace, b, 0, a, 0, 1_000_000, 50, null, Committed);
            _ = InvestigationWorkspace.AddNote(workspace, "the first look", null, Committed);
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is { Members.Count: 2, Notes.Count: 1 });
            ListBox sessions = Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one");

            // The time reference is aligned to nothing, so neither aligning nor withdrawing is offered for it; the session
            // aligned to it is offered both, each saying what it does where its name does not.
            sessions.SelectedIndex = 0;
            Assert.Equal($"Session {Short(a)} is the investigation's time reference: the other sessions are aligned to its clock.",
                WhyNot(window, "Align…"));
            Assert.Equal($"Session {Short(a)} is the investigation's time reference, so it has no alignment to withdraw.",
                WhyNot(window, "Withdraw alignment"));
            sessions.SelectedIndex = 1;
            Assert.Null(Offered(window, "Align…"));
            Assert.Equal("Withdraw the selected session's alignment", Offered(window, "Withdraw alignment"));
            Assert.Equal("Say whether the selected session's host is one host with another", Offered(window, "One host…"));
            Assert.Equal("Open the selected session in InterCat", Offered(window, "Open in InterCat"));

            // A join is decided once each way: with none made there is none to withdraw, and one made is not made again.
            window.ShowTab(1);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal("Find candidate joins first.", WhyNot(window, "Accept as one connection"));
            await window.FindCandidatesAsync();
            Assert.Equal("Select a candidate join first.", WhyNot(window, "Reject"));
            window.SelectCandidate(0);
            Assert.Equal("You have made no decision about this candidate to withdraw.", WhyNot(window, "Withdraw decision"));
            await window.DecideSelectedAsync(WorkspaceJoinDecision.Accepted);
            window.SelectCandidate(0);
            Assert.Equal("You accepted this candidate as one connection already.", WhyNot(window, "Accept as one connection"));
            Assert.Equal("Withdraw your decision about the selected candidate", Offered(window, "Withdraw decision"));

            // The whole investigation shown, it is neither zoomed out nor shown whole again; the column the timeline's keys
            // stand on opens.
            window.ShowTab(2);
            await window.ShowTimelineAsync();
            Assert.Equal("The timeline shows the whole investigation already.", WhyNot(window, "Zoom out"));
            Assert.Equal("The timeline shows the whole investigation already.", WhyNot(window, "Whole investigation"));
            Assert.Equal("Zoom the timeline in around the chosen column", Offered(window, "Zoom in"));
            Assert.Equal("Open the chosen column's records in InterCat", Offered(window, "Open this column"));

            // A note about the whole investigation has no place on the timeline; a note is reworded once one is chosen.
            window.ShowTab(3);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal("Select a note first.", WhyNot(window, "Reword…"));
            window.SelectNote(0);
            Assert.Equal("That note is about the whole investigation, not an instant, so the timeline has no place for it.",
                WhyNot(window, "Show on the timeline"));
            Assert.Null(Offered(window, "Reword…"));

            // A session not where it was last found does not open, and says why on Open and when Enter asks it to open.
            window.ShowTab(0);
            Directory.Move(Path.Combine(root.Path, "server"), Path.Combine(root.Path, "server-moved"));
            await window.RefreshAsync();
            sessions.SelectedIndex = 1;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            string notThere = $"Session {Short(b)} is not where it was last found: relink it to open it.";
            Assert.Equal(notThere, WhyNot(window, "Open in InterCat"));
            Assert.True(sessions.ContainerFromIndex(1)!.Focus());
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(notThere, Named<TextBlock>(window, "Investigation status").Text);

            // An investigation that no longer reads offers nothing of what it held, and says why.
            File.WriteAllText(workspace, "{");
            await window.RefreshAsync();
            const string unread = "The investigation could not be read: Refresh reads it again.";
            Assert.Equal(unread, WhyNot(window, "Open in InterCat"));
            Assert.Equal(unread, WhyNot(window, "Package…"));
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§8.4: Package asks nothing when no session is there to copy, nor when the investigation no longer reads, and says why")]
    public void PackageSaysWhyThereIsNothingToPackage()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        string alpha = Datagrams(root.Path, "alpha", 4);
        _ = InvestigationWorkspace.Add(workspace, alpha, Committed);
        Directory.Delete(alpha, recursive: true);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is { Members.Count: 1 });
            Button package = Named<Button>(window, "Package this investigation with its sessions, to share it");
            TextBlock status = Named<TextBlock>(window, "Investigation status");

            // Its one session is not where it was last found, so there is nothing to copy: no confirmation is asked.
            Assert.True(package.IsEnabled);
            package.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => status.Text?.StartsWith("No session", StringComparison.Ordinal) == true);
            Assert.Equal("No session of this investigation is where it was last found, so there is nothing to package. Relink "
                + "its sessions first.", status.Text);
            Assert.Empty(window.OwnedWindows);

            // The file is measured as it is now: one written anew since, naming no session, has none to package either.
            File.Delete(workspace);
            InvestigationWorkspace.Create(workspace, Committed);
            package.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => status.Text?.StartsWith("This investigation", StringComparison.Ordinal) == true);
            Assert.Equal("This investigation holds no session yet, so there is nothing to package.", status.Text);
            Assert.Empty(window.OwnedWindows);

            // An investigation that no longer reads is not measured: the window says so, then why, and asks nothing.
            File.WriteAllText(workspace, "{");
            string unreadable = Assert.Throws<InvalidDataException>(() => InvestigationWorkspace.Read(workspace)).Message;
            package.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => status.Text?.StartsWith("The investigation could not", StringComparison.Ordinal) == true);
            Assert.Equal("The investigation could not be measured to package it: " + unreadable, status.Text);
            Assert.Empty(window.OwnedWindows);
            Assert.Equal(("Package…", true), (package.Content, package.IsEnabled));
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§8.4: an action on an investigation whose file no longer reads, or is gone, says what it did not do, then why, a gone file in the window's own words")]
    public async Task AnActionNotDoneSaysWhatAndWhy()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        Guid a = InvestigationWorkspace.Add(workspace, Session(root.Path, "client", ["10.0.0.1:50000", "10.0.0.2:443"], 100), Committed).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, Session(root.Path, "server", ["10.0.0.2:443", "10.0.0.1:50000"], 200), Committed).SessionId;
        InvestigationWorkspace.Align(workspace, b, 0, a, 0, 1_000_000, 50, null, Committed);
        _ = InvestigationWorkspace.AddNote(workspace, "the first look", null, Committed);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is { Members.Count: 2, Notes.Count: 1 });
            Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one").SelectedIndex = 1;
            window.SelectNote(0);
            TextBlock status = Named<TextBlock>(window, "Investigation status");
            window.ShowTab(1);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await window.FindCandidatesAsync();
            Assert.Single(window.Candidates!.Rows);
            window.SelectCandidate(0);
            TextBlock joins = Named<TextBlock>(window, "Candidate joins status");

            // The file no longer reads as an investigation: each action says what it did not do, then why.
            File.WriteAllText(workspace, "{");
            string unreadable = Assert.Throws<InvalidDataException>(() => InvestigationWorkspace.Read(workspace)).Message;
            await window.RemoveSelectedNoteAsync();
            Assert.Equal("The note was not removed: " + unreadable, status.Text);
            await window.WithdrawSelectedAsync();
            Assert.Equal($"Session {Short(b)}'s alignment was not withdrawn: " + unreadable, status.Text);
            await window.RelinkAsync(b, window.View!.Members[1].FullPath);
            Assert.Equal($"Session {Short(b)} was not relinked: " + unreadable, status.Text);
            await window.DecideSelectedAsync(WorkspaceJoinDecision.Accepted);
            Assert.Equal("The join was not accepted: " + unreadable, joins.Text);
            await window.DecideSelectedAsync(WorkspaceJoinDecision.Rejected);
            Assert.Equal("The join was not rejected: " + unreadable, joins.Text);
            await window.DecideSelectedAsync(WorkspaceJoinDecision.Withdrawn);
            Assert.Equal("Your decision was not withdrawn: " + unreadable, joins.Text);

            // A session that could not be added says so, and the investigation is not read again only to say it cannot be.
            await window.AddAsync([Session(root.Path, "third")]);
            Assert.Equal("third was not added: " + unreadable, status.Text);
            Assert.Equal(2, window.View!.Members.Count);

            // Gone, the file is said to be gone - never with the command line's advice to make one - and so is it read again.
            File.Delete(workspace);
            string gone = $"No file is at {workspace} any more; it was moved, renamed or deleted.";
            await window.RemoveSelectedNoteAsync();
            Assert.Equal("The note was not removed: " + gone, status.Text);
            await window.RefreshAsync();
            Assert.Equal("This investigation could not be read: " + gone, status.Text);
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§8.4: a dialog of an investigation whose file no longer reads, or is gone, says what it did not do, then why")]
    public async Task ADialogsActionNotDoneSaysWhatAndWhy()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        Guid a = InvestigationWorkspace.Add(workspace, Session(root.Path, "client", ["10.0.0.1:50000", "10.0.0.2:443"], 100), Committed).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, Session(root.Path, "server", ["10.0.0.2:443", "10.0.0.1:50000"], 200), Committed).SessionId;
        InvestigationWorkspace.Align(workspace, b, 0, a, 0, 1_000_000, 50, null, Committed);
        _ = InvestigationWorkspace.AddNote(workspace, "the first look", null, Committed);
        _ = InvestigationWorkspace.StateTranslation(workspace, "203.0.113.7:8443", "10.0.0.2:443", null, Committed);
        var interval = new TimeRange(0, 10_000_000);
        _ = InvestigationWorkspace.SaveView(workspace, "the first second", interval, Committed);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is { Members.Count: 2, Notes.Count: 1 });
            Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one").SelectedIndex = 1;
            window.SelectNote(0);

            // Each dialog opens on the investigation as it read...
            InvestigationAlignWindow align = window.AlignDialogForSelected()!;
            InvestigationHostWindow host = window.HostDialogForSelected()!;
            InvestigationNoteWindow adding = window.NoteDialog(reword: false)!;
            InvestigationNoteWindow rewording = window.NoteDialog(reword: true)!;
            InvestigationTranslationsWindow translations = window.TranslationsDialog();
            var views = new InvestigationViewsWindow(workspace, interval);
            InvestigationCompareWindow compare = window.CompareDialog()!;
            foreach (Window dialog in new Window[] { align, host, adding, rewording, translations, views, compare })
            {
                dialog.Show(window);
            }

            translations.Load();
            translations.Select(0);
            views.Load();
            views.Select(0);

            // ...and the file then no longer reads: each says what it did not do, then why.
            File.WriteAllText(workspace, "{");
            string unreadable = Assert.Throws<InvalidDataException>(() => InvestigationWorkspace.Read(workspace)).Message;
            Assert.False(await align.AlignAsync());
            Assert.Equal($"Session {Short(b)} was not aligned: " + unreadable, Named<TextBlock>(align, "Alignment status").Text);
            Assert.False(await host.DecideAsync(WorkspaceHostDecision.Confirmed));
            Assert.Equal("Nothing was recorded: " + unreadable, Named<TextBlock>(host, "One host status").Text);
            adding.Enter("a second look");
            Assert.False(await adding.SaveAsync());
            Assert.Equal("The note was not added: " + unreadable, Named<TextBlock>(adding, "Note status").Text);
            rewording.Enter("the first look, again");
            Assert.False(await rewording.SaveAsync());
            Assert.Equal("The note was not reworded: " + unreadable, Named<TextBlock>(rewording, "Note status").Text);
            Assert.False(await translations.WithdrawSelectedAsync());
            Assert.Equal("The translation was not withdrawn: " + unreadable, Named<TextBlock>(translations, "Translation status").Text);
            translations.Enter("203.0.113.8:8443", "10.0.0.2:443");
            Assert.False(await translations.StateAsync());
            Assert.Equal("The translation was not stated: " + unreadable, Named<TextBlock>(translations, "Translation status").Text);
            Assert.False(await views.RemoveSelectedAsync());
            Assert.Equal("'the first second' was not removed: " + unreadable, Named<TextBlock>(views, "Saved views status").Text);
            views.NameIt("the second second");
            Assert.False(await views.SaveAsync());
            Assert.Equal("The view was not saved: " + unreadable, Named<TextBlock>(views, "Saved views status").Text);

            // Gone, the file is said to be gone, as the investigation's window says it.
            File.Delete(workspace);
            string gone = $"No file is at {workspace} any more; it was moved, renamed or deleted.";
            Assert.False(await host.DecideAsync(WorkspaceHostDecision.Confirmed));
            Assert.Equal("Nothing was recorded: " + gone, Named<TextBlock>(host, "One host status").Text);
            compare.Enter(a, "1", b, "1");
            Assert.Null(await compare.CompareAsync());
            Assert.Equal("They could not be compared: " + gone, Named<TextBlock>(compare, "What may be said of their order").Text);
            translations.Load();
            Assert.Equal("The investigation could not be read: " + gone, Named<TextBlock>(translations, "Translation status").Text);
            views.Load();
            Assert.Equal("The investigation could not be read: " + gone, Named<TextBlock>(views, "Saved views status").Text);

            // Opened afresh on a file that does not read, the lists hold nothing to act on, and their actions say why.
            const string Unread = "The investigation could not be read; the status line says why.";
            var freshTranslations = new InvestigationTranslationsWindow(workspace);
            freshTranslations.Show(window);
            freshTranslations.Load();
            Assert.Equal(Unread, Offers.WhyNot(Named<Button>(freshTranslations, "Withdraw the selected translation")));
            var freshViews = new InvestigationViewsWindow(workspace, interval);
            freshViews.Show(window);
            freshViews.Load();
            Assert.Equal(Unread, Offers.WhyNot(Named<Button>(freshViews, "Show the selected view on the timeline")));
            Assert.Equal(Unread, Offers.WhyNot(Named<Button>(freshViews, "Remove the selected view")));

            // Read again once a file is there, an empty list says it holds none.
            InvestigationWorkspace.Create(workspace, Committed);
            freshTranslations.Load();
            Assert.Equal("No translation is stated.", Offers.WhyNot(Named<Button>(freshTranslations, "Withdraw the selected translation")));
            freshViews.Load();
            Assert.Equal("No view is saved yet.", Offers.WhyNot(Named<Button>(freshViews, "Remove the selected view")));
            freshTranslations.Close();
            freshViews.Close();
            foreach (Window dialog in new Window[] { align, host, adding, rewording, translations, views, compare })
            {
                dialog.Close();
            }

            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R5: an alignment another session is aligned through is not withdrawn, and the window says whose rests on it and what to do first, each session named as its row is")]
    public async Task AnAlignmentAnotherRestsOnIsKept()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        Guid a = InvestigationWorkspace.Add(workspace, Session(root.Path, "alpha"), Committed).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, Session(root.Path, "beta"), Committed).SessionId;
        Guid c = InvestigationWorkspace.Add(workspace, Session(root.Path, "gamma"), Committed).SessionId;
        InvestigationWorkspace.Align(workspace, b, 0, a, 0, 1_000_000, 50, null, Committed);
        InvestigationWorkspace.Align(workspace, c, 0, b, 0, 1_000_000, 50, null, Committed);
        string kept = File.ReadAllText(workspace);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is { Members.Count: 3 });
            Assert.Equal([$"Session {Short(a)}", $"Session {Short(b)}", $"Session {Short(c)}"],
                window.View!.Members.Select(row => row.Title.Split(',')[0]));
            Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one").SelectedIndex = 1;
            Button withdraw = Named<Button>(window, "Withdraw alignment");
            Assert.True(withdraw.IsEnabled);
            await window.WithdrawSelectedAsync();
            Assert.Equal($"Session {Short(b)}'s alignment was not withdrawn: Session {Short(c)} is aligned through session {Short(b)}, so "
                + $"withdrawing session {Short(b)}'s alignment would leave it with no place in the investigation's time. Withdraw session "
                + $"{Short(c)}'s alignment, or align it to another session, first.", Named<TextBlock>(window, "Investigation status").Text);
            Assert.Equal(kept, File.ReadAllText(workspace));
            Assert.True(window.View!.Members[1].IsAligned);
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    /// <summary>A session whose process 100 sends <paramref name="records"/> datagrams 100 µs into its capture, 1 µs apart.</summary>
    private static string Datagrams(string root, string name, int records, string? host = null)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory), Guid.NewGuid(), "investigation-window-tests");
        _ = Publish(
            store,
            [
                .. Enumerable.Range(0, records).Select(index =>
                    Transfer(1_000 + (index * 10), ObservationKind.Send, AccountingSide.SendSide, 10, 100, (ulong)(index + 1))
                        .Between("192.168.1.5:61000", "8.8.8.8:53") with { Mechanism = Mechanism.Udp, SessionRelativeTicks = (1_000 + (index * 10)) * 100 }),
            ],
            capture: CaptureId.New(),
            clock: ClockFor(ClockId.New(), host ?? "lab-" + name));
        store.ReleaseSegmentReaders();
        return directory;
    }

    /// <summary>
    /// A session whose process 100 sends four datagrams 100 µs into its capture and four more 500 µs in, with its capture's
    /// ledger: readings delivered to 200 µs with nothing lost, none from then to 400 µs, and to 600 µs with 3 events lost.
    /// </summary>
    private static string Ledgered(string root, string name)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory), Guid.NewGuid(), "investigation-window-tests");
        _ = Publish(
            store,
            [
                .. new long[] { 1_000, 1_010, 1_020, 1_030, 5_000, 5_010, 5_020, 5_030 }.Select((ticks, index) =>
                    Transfer(ticks, ObservationKind.Send, AccountingSide.SendSide, 10, 100, (ulong)(index + 1))
                        .Between("192.168.1.5:61000", "8.8.8.8:53") with { Mechanism = Mechanism.Udp, SessionRelativeTicks = ticks * 100 }),
            ],
            capture: CaptureId.New(),
            clock: ClockFor(ClockId.New(), "lab-" + name),
            coverage: new CoverageLedgerV1 { Contract = CoverageLedgerV1.ContractName, Epochs = [Epoch(1, 0, 2_000, 0), Epoch(2, 4_000, 6_000, 3)] });
        store.ReleaseSegmentReaders();
        return directory;
    }

    /// <summary>A live epoch between two delivered readings that collected the datagrams' one descriptor.</summary>
    private static CoverageEpochV1 Epoch(int number, long first, long last, long lost) => new()
    {
        Epoch = number,
        Acquisition = CoverageAcquisition.LiveCapture,
        FirstDeliveredNativeTicks = first,
        LastDeliveredNativeTicks = last,
        Collected = [new CoverageCollectedV1 { ProviderId = NetworkProvider, ProviderName = "network", EventId = 10, Version = 0, Mechanism = Mechanism.Udp }],
        Deliveries = [new CoverageDeliveryV1 { ProviderId = NetworkProvider, EventId = 10, Version = 0, Delivered = 4, Admitted = 4, Omitted = 0 }],
        Losses =
        [
            new CoverageLossV1 { Layer = LossLayer.SourceSession, Lost = lost },
            new CoverageLossV1 { Layer = LossLayer.ConsumerBuffers, Lost = 0 },
            new CoverageLossV1 { Layer = LossLayer.CallbackQueue, Lost = 0 },
            new CoverageLossV1 { Layer = LossLayer.Storage, Lost = 0 },
        ],
    };

    private static double Distance(Color one, Color other) =>
        Math.Sqrt(Math.Pow(one.R - other.R, 2) + Math.Pow(one.G - other.G, 2) + Math.Pow(one.B - other.B, 2));

    /// <summary>A session whose one process holds one end of a TCP connection: it opens, sends and closes it.</summary>
    private static string Session(string root, string name, string[] ends, int owner, CoverageLedgerV1? coverage = null)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory), Guid.NewGuid(), "investigation-window-tests");
        _ = Publish(
            store,
            [
                Lifecycle(1, ObservationKind.Create, owner, 1) with { SessionRelativeTicks = 100 },
                Transfer(1_000, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, owner, 20).Between(ends[0], ends[1]) with { SessionRelativeTicks = 100_000 },
                Transfer(1_001, ObservationKind.Send, AccountingSide.SendSide, 64, owner, 21).Between(ends[0], ends[1]) with { SessionRelativeTicks = 100_100 },
                Transfer(1_002, ObservationKind.Disconnect, AccountingSide.EndpointActivity, 0, owner, 22).Between(ends[0], ends[1]) with { SessionRelativeTicks = 100_200 },
            ],
            capture: CaptureId.New(),
            clock: ClockFor(ClockId.New(), "lab-" + name),
            coverage: coverage);
        store.ReleaseSegmentReaders();
        return directory;
    }

    private static string Session(string root, string name)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory), Guid.NewGuid(), "investigation-window-tests");
        _ = Publish(
            store,
            [Lifecycle(100, ObservationKind.Create, 400, 1) with { SessionRelativeTicks = 100 }],
            capture: CaptureId.New(),
            clock: ClockFor(ClockId.New(), "lab-" + name));
        store.ReleaseSegmentReaders();
        return directory;
    }

    /// <summary>client.exe (PID 100) sends server.exe (PID 200) 100 bytes over loopback TCP, both ends captured: one drawn relationship.</summary>
    private static string PairedSession(string root, string name)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory), Guid.NewGuid(), "investigation-window-tests");
        _ = Publish(
            store,
            [
                Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 100 },
                Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe", SessionRelativeTicks = 200 },
                Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 100, 100, 10)
                    .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 1_000 },
                Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 100, 200, 11)
                    .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 1_100 },
            ],
            capture: CaptureId.New(),
            clock: ClockFor(ClockId.New(), "lab-" + name));
        store.ReleaseSegmentReaders();
        return directory;
    }

    /// <summary>
    /// client.exe and server.exe related over loopback, and browser.exe's TCP to two other hosts, 142.250.1.1 the busier, in
    /// a folder of its own beneath <paramref name="root"/>.
    /// </summary>
    private static string HostsSession(string root, string name)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory), Guid.NewGuid(), "investigation-window-tests");
        _ = Publish(
            store,
            [
                Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 100 },
                Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe", SessionRelativeTicks = 200 },
                Lifecycle(3, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 300 },
                Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 100, 100, 10)
                    .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 1_000 },
                Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 100, 200, 11)
                    .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 1_100 },
                Transfer(12, ObservationKind.Send, AccountingSide.SendSide, 500, 300, 12)
                    .Between("192.168.1.10:52000", "142.250.1.1:443") with { SessionRelativeTicks = 1_200 },
                Transfer(13, ObservationKind.Send, AccountingSide.SendSide, 500, 300, 13)
                    .Between("192.168.1.10:52000", "142.250.1.1:443") with { SessionRelativeTicks = 1_300 },
                Transfer(14, ObservationKind.Send, AccountingSide.SendSide, 500, 300, 14)
                    .Between("192.168.1.10:52001", "8.8.8.8:443") with { SessionRelativeTicks = 1_400 },
            ],
            capture: CaptureId.New(),
            clock: ClockFor(ClockId.New(), "lab-" + name));
        store.ReleaseSegmentReaders();
        return directory;
    }

    /// <summary>A session of four pool.exe instances, the first sending most, in a folder of its own beneath <paramref name="root"/>.</summary>
    private static string PoolSession(string root, string name)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory), Guid.NewGuid(), "investigation-window-tests");
        _ = Publish(
            store,
            [
                .. Enumerable.Range(0, 4).Select(index => Lifecycle(1 + index, ObservationKind.Create, 500 + index, (ulong)(1 + index))
                    with { ResourceName = @"C:\Tools\pool.exe", SessionRelativeTicks = 100L * (1 + index) }),
                .. Enumerable.Range(0, 4).SelectMany(index => Enumerable.Range(0, 4 - index).Select(send =>
                    Transfer(100 + (index * 10) + send, ObservationKind.Send, AccountingSide.SendSide, 100, 500 + index,
                        (ulong)(100 + (index * 10) + send)) with { SessionRelativeTicks = 100L * (100 + (index * 10) + send) })),
            ],
            capture: CaptureId.New(),
            clock: ClockFor(ClockId.New(), "lab-" + name));
        store.ReleaseSegmentReaders();
        return directory;
    }

    /// <summary>
    /// A session of processes in terminal sessions 0, 1 and 2 and one naming none (<see cref="TerminalSessions"/>), in a
    /// folder of its own beneath <paramref name="root"/>.
    /// </summary>
    private static string TerminalSessionsSession(string root, string name)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory), Guid.NewGuid(), "investigation-window-tests");
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = TerminalSessions();
        _ = Publish(store, rows, capture: CaptureId.New(), clock: ClockFor(ClockId.New(), "lab-" + name), fields: fields);
        store.ReleaseSegmentReaders();
        return directory;
    }

    private static string Short(Guid identity) => identity.ToString("N")[..8];

    /// <summary>
    /// Why the shown action labelled <paramref name="label"/> is not offered: its tooltip, shown on the disabled button, and
    /// its help text, which say the same.
    /// </summary>
    private static string? WhyNot(Window window, string label) => Offers.WhyNot(Labelled(window, label));

    /// <summary>What the shown action labelled <paramref name="label"/> says it does, offered; null where its name says it.</summary>
    private static string? Offered(Window window, string label) => Offers.Offered(Labelled(window, label));

    private static Button Labelled(Window window, string label)
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, label));
    }

    /// <summary>What Other hosts' list shows beneath the address of the host at <paramref name="address"/>.</summary>
    private static TextBlock HostDetail(ListBox hosts, string address)
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        int index = hosts.Items.Cast<HostRow>().ToList().FindIndex(row => row.Address == address);
        return hosts.ContainerFromIndex(index)!.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.Text != address);
    }

    /// <summary>
    /// Runs the window's jobs until <paramref name="condition"/> holds, for up to 15 s, which a machine busy with other test
    /// runs needs; one that never holds fails naming what was waited for.
    /// </summary>
    private static void WaitFor(Func<bool> condition, [CallerArgumentExpression(nameof(condition))] string? waited = null)
    {
        for (int attempt = 0; attempt < 1_500 && !condition(); attempt++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Assert.True(condition(), "Waited 15 s for " + waited);
    }

    /// <summary>The field shown, as a screen reader names it by the words beside it, whose caption begins so.</summary>
    private static TextBox Field(Window window, string caption) =>
        window.GetVisualDescendants().OfType<TextBox>().Single(box => box.IsEffectivelyVisible
            && ControlAutomationPeer.CreatePeerForElement(box).GetName().StartsWith(caption, StringComparison.Ordinal));

    /// <summary>
    /// The one control of its kind named <paramref name="name"/>: given that name, or heard by it, as a button is by the
    /// label it shows.
    /// </summary>
    private static T Named<T>(Window window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<T>()
            .Single(control => AutomationProperties.GetName(control) == name
                || ControlAutomationPeer.CreatePeerForElement(control).GetName() == name);

    private static void AssertInside(Control inner, Control outer, Window window)
    {
        Rect bounds = new(inner.TranslatePoint(default, window)!.Value, inner.Bounds.Size);
        Rect container = new(outer.TranslatePoint(default, window)!.Value, outer.Bounds.Size);
        Assert.True(
            container.Contains(bounds.TopLeft) && container.Contains(bounds.BottomRight - new Vector(0.5, 0.5)),
            $"{inner.Name} at {bounds} is not inside {outer.Name} at {container}.");
    }

    private static void Save(Window window, string name)
    {
        for (int pass = 0; pass < 4; pass++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            _ = window.CaptureRenderedFrame();
        }

        Avalonia.Media.Imaging.WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        string directory = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(directory);
        frame!.Save(Path.Combine(directory, name));
    }
}
