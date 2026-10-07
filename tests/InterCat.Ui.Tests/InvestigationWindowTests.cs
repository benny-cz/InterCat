using System.Globalization;
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
            Assert.EndsWith("Relink it to open it.", view.Members[1].AccessibleName, StringComparison.Ordinal);
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

    [AvaloniaFact(DisplayName = "R22: a new investigation starts empty, and an existing file is opened, never written over")]
    public void ANewInvestigationStartsEmpty()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "fresh" + InvestigationWorkspace.Extension);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace, create: true);
            WaitFor(() => window.View is not null);
            Assert.Empty(window.View!.Members);
            Assert.Equal("No session yet: add the sessions this investigation covers.", window.View.Summary);
            Assert.Equal("A new investigation: add the sessions it covers.", Named<TextBlock>(window, "Investigation status").Text);
            string written = File.ReadAllText(workspace);
            window.Close();

            // Asked to make one that exists, the window opens it as it is.
            InvestigationWindow again = main.ShowInvestigation(workspace, create: true);
            WaitFor(() => again.View is not null);
            Assert.Equal(written, File.ReadAllText(workspace));
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

            // The dialog aligns to the other session's clock, and says in words what it needs.
            InvestigationAlignWindow dialog = window.AlignDialogForSelected()!;
            dialog.Show(window);
            Assert.False(await dialog.AlignAsync());
            Assert.Equal("Write both instants in seconds of their own session's time, such as 12.5.",
                Named<TextBlock>(dialog, "Alignment status").Text);

            // By boot, sessions that recorded no calibration are refused, in words.
            dialog.Choose(WorkspaceAlignmentMode.SameBoot);
            Assert.False(await dialog.AlignAsync());
            Assert.Contains("records no clock calibration", Named<TextBlock>(dialog, "Alignment status").Text, StringComparison.Ordinal);

            dialog.Choose(WorkspaceAlignmentMode.Manual);
            Named<TextBox>(dialog, "The instant in this session, in seconds").Text = "2";
            Named<TextBox>(dialog, "The same instant in the reference session, in seconds").Text = "5,5";
            Named<TextBox>(dialog, "How fast the two clocks drift apart at most, in parts per million, if known").Text = "20";
            Save(dialog, "investigation-align.png");
            Assert.True(await dialog.AlignAsync());
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
            Named<TextBox>(dialog, "The instant in this session, in seconds").Text = "1";
            Named<TextBox>(dialog, "The same instant in the reference session, in seconds").Text = "3";
            Named<TextBox>(dialog, "A second instant in this session, in seconds, to measure the clocks' rate; optional").Text = "11";

            // Half of a second instant is refused, in words; both measure the clocks' rate, and the drift bounds its wander.
            Assert.False(await dialog.AlignAsync());
            Assert.Equal("Write both second instants in seconds of their own session's time, or leave both empty.",
                Named<TextBlock>(dialog, "Alignment status").Text);
            Named<TextBox>(dialog, "The same second instant in the reference session, in seconds").Text = 13.0001m.ToString(culture);
            Named<TextBox>(dialog, "How fast the two clocks drift apart at most, in parts per million, if known").Text = 0.5m.ToString(culture);
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
            ComboBox to = Named<ComboBox>(dialog, "The session to align to: the investigation's clock, or a session placed in it");
            Assert.True(to.IsEnabled);
            Assert.Equal([a, b], ((IEnumerable<AlignmentReference>)to.ItemsSource!).Select(choice => choice.SessionId));
            to.SelectedIndex = 1;
            dialog.Choose(WorkspaceAlignmentMode.Manual);
            Named<TextBox>(dialog, "The instant in this session, in seconds").Text = "0";
            Named<TextBox>(dialog, "The same instant in the reference session, in seconds").Text = "1";
            Named<TextBox>(dialog, "How fast the two clocks drift apart at most, in parts per million, if known").Text = "1";
            Assert.True(await dialog.AlignAsync());
            Assert.Equal(b, InvestigationWorkspace.ActiveAlignment(InvestigationWorkspace.Read(workspace), c)!.ReferenceSessionId);

            // The window says whom it is aligned to, and that the investigation's sessions are placed through one another.
            await window.RefreshAsync();
            WaitFor(() => window.View!.Members[2].IsAligned);
            CultureInfo culture = CultureInfo.CurrentCulture;
            Assert.StartsWith($"Aligned by a person to session {Short(b)}, itself aligned: its {0m.ToString("0.000", culture)} s is "
                + $"session {Short(b)}'s {1m.ToString("0.000", culture)} s", window.View!.Members[2].Time, StringComparison.Ordinal);
            Assert.EndsWith(", directly or through another.", window.View.Time, StringComparison.Ordinal);

            // Beta, which gamma is aligned through, may be aligned only to the investigation's clock now: never through gamma.
            list.SelectedIndex = 1;
            InvestigationAlignWindow betas = window.AlignDialogForSelected()!;
            betas.Show(window);
            Assert.Equal([a], ((IEnumerable<AlignmentReference>)Named<ComboBox>(betas,
                "The session to align to: the investigation's clock, or a session placed in it").ItemsSource!).Select(choice => choice.SessionId));
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
            Assert.False(Named<Button>(dialog, "They are not one host").IsEnabled);
            Save(dialog, "investigation-one-host.png");
            Assert.True(await dialog.DecideAsync(WorkspaceHostDecision.Confirmed));
            await window.RefreshAsync();
            WaitFor(() => window.View!.Summary.EndsWith(" · 1 host (2 identities)", StringComparison.Ordinal));
            Assert.StartsWith($"host {Short(hosts[0])}, one host with host {Short(hosts[1])} by a person's confirmation · ",
                window.View!.Members[0].Detail, StringComparison.Ordinal);

            // Withdrawn from beta's side, they are two hosts again; both revisions are kept.
            list.SelectedIndex = 1;
            InvestigationHostWindow back = window.HostDialogForSelected()!;
            back.Show(window);
            Assert.True(Named<Button>(back, "They are not one host").IsEnabled);
            Assert.False(Named<Button>(back, "They are one host").IsEnabled);
            Assert.True(await back.DecideAsync(WorkspaceHostDecision.Withdrawn));
            await window.RefreshAsync();
            WaitFor(() => window.View!.Summary.EndsWith(" · 2 hosts", StringComparison.Ordinal));
            Assert.Equal(2, InvestigationWorkspace.Read(workspace).HostEquivalences.Count);
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

            // Beta's 2 s is alpha's 5.5 s within 500 µs: one instant, whose order is not stated; alpha's is exact.
            dialog.Enter(a, 5.5m.ToString(culture), b, "2");
            WorkspaceComparison? tie = await dialog.CompareAsync();
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

            // Only what the session holds of the column is selected: this one reaches back before beta's capture began.
            Assert.True(await window.OpenColumnAsync(new TimelineColumn(1, column)));
            WaitFor(() => main.GetControl<TextBlock>("CaptureSessionPath").Text == betaPath);
            var opened = (WorkspaceViewModel)main.DataContext!;
            TimeRange extent = opened.Snapshot.Extent;
            Assert.True(own.StartTicks < extent.StartTicks);
            TimeRange held = new(Math.Max(own.StartTicks, extent.StartTicks), Math.Min(own.EndTicks, extent.EndTicks));
            Assert.Equal(held, opened.SelectedInterval);
            TimeRange viewport = main.GetControl<TimelineView>("TimelineSurface").Viewport;
            Assert.True(viewport.StartTicks <= held.StartTicks && viewport.EndTicks >= held.EndTicks, $"viewport {viewport} held {held}");
            Assert.StartsWith($"Opened Session {Short(b)} in the InterCat window, zoomed to ", Named<TextBlock>(window, "Investigation status").Text,
                StringComparison.Ordinal);
            window.Close();
        }
        finally
        {
            main.Close();
        }
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
            dialog.Enter("127.0.0.1:80", "10.0.0.2:80");
            Assert.False(await dialog.StateAsync());
            Assert.Contains("loopback", Named<TextBlock>(dialog, "Translation status").Text, StringComparison.Ordinal);
            dialog.Enter("203.0.113.7:8443", "10.0.0.2:443", "the router forwards 8443");
            Assert.True(await dialog.StateAsync());
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
            Assert.True(await again.WithdrawSelectedAsync());
            Assert.Empty(again.Listed);
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
            dialog.Enter("Beta's datagrams begin", b, 0.0001m.ToString(culture));
            Assert.True(await dialog.SaveAsync());
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
            dialog.NameIt("Alpha's datagrams");
            Assert.True(await dialog.SaveAsync());
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
            Assert.EndsWith("(PID 100), 64 B sent, no receive recorded, opened and closed in the capture", row.First, StringComparison.Ordinal);
            Assert.StartsWith("Candidate join: TCP 10.0.0.1:50000", row.AccessibleName, StringComparison.Ordinal);
            Assert.Equal("1 candidate join, none established by evidence; 0 not the only match of a connection.", found.Summary);
            Assert.Equal(found.Summary, Named<TextBlock>(window, "Candidate joins status").Text);
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
            Assert.False(prompt.GetVisualDescendants().OfType<CheckBox>()
                .Single(box => AutomationProperties.GetHelpText(box) == $"Copy session {Short(c)}, found in gamma").IsEnabled);
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
            prompt.Choose(b, copied: true);
            Assert.True(prompt.CanSave);
            Assert.Equal([b], prompt.Chosen);
            prompt.Close();

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
    private static string Session(string root, string name, string[] ends, int owner)
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
            clock: ClockFor(ClockId.New(), "lab-" + name));
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

    private static string Short(Guid identity) => identity.ToString("N")[..8];

    private static void WaitFor(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Assert.True(condition());
    }

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
