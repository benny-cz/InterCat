using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using InterCat.Application;
using InterCat.Capture.Journal.Tests;
using InterCat.Desktop;
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
            Assert.False(Named<Button>(window, "Open the selected session in InterCat").IsEnabled);
            await window.RelinkAsync(b, moved);
            WaitFor(() => window.View!.Members[1].State == WorkspaceMemberState.Present);
            Directory.CreateDirectory(Path.Combine(root.Path, "empty"));
            await window.AddAsync([Path.Combine(root.Path, "empty")]);
            WaitFor(() => Named<TextBlock>(window, "Investigation status").Text?.Contains("was not added", StringComparison.Ordinal) == true);

            // A member opens in InterCat's own window, as a saved session would.
            list.SelectedIndex = 0;
            Button open = Named<Button>(window, "Open the selected session in InterCat");
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
            Assert.False(Named<Button>(window, "Withdraw the selected session's alignment").IsEnabled);

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
            Assert.True(Named<Button>(window, "Withdraw the selected session's alignment").IsEnabled);
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
            Assert.True(Named<Button>(window, "Say whether the selected session's host is one host with another").IsEnabled);

            // Confirmed from alpha's side, the two identities read as one host, which the confirmation alone makes them.
            InvestigationHostWindow dialog = window.HostDialogForSelected()!;
            dialog.Show(window);
            Assert.False(Named<Button>(dialog, "Withdraw your confirmation that the two identities are one host").IsEnabled);
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
            Assert.True(Named<Button>(back, "Withdraw your confirmation that the two identities are one host").IsEnabled);
            Assert.False(Named<Button>(back, "Confirm that the two identities are one host").IsEnabled);
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
            Assert.True(Named<Button>(window, "Compare an instant of one session with an instant of another").IsEnabled);
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
            Assert.True(Named<Button>(window, "Zoom the timeline in around the chosen column").IsEnabled);
            Assert.False(Named<Button>(window, "Show the whole investigation on the timeline").IsEnabled);

            // The chosen column says its session, its time and its records; the cursor starts on the busiest.
            Assert.Contains($"Session {Short(a)}, column 1 of 160: ", window.ColumnReadout, StringComparison.Ordinal);
            Assert.EndsWith("4 records. Enter opens them in InterCat.", window.ColumnReadout, StringComparison.Ordinal);

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
            Named<Button>(window, "Zoom the timeline in around the chosen column").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
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
            Assert.StartsWith("Candidate join: TCP 10.0.0.1:50000", row.AccessibleName, StringComparison.Ordinal);
            Assert.Equal("1 candidate join, none established by evidence; 0 not the only match of a connection.", found.Summary);
            Assert.Equal(found.Summary, Named<TextBlock>(window, "What finding candidate joins found").Text);
            ListBox list = Named<ListBox>(window, "Candidate joins between the sessions; none is established");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(row.AccessibleName, AutomationProperties.GetName(list.ContainerFromIndex(0)!));

            // Accepted as your decision, it says so, and can be withdrawn; every revision stays in the file.
            window.SelectCandidate(0);
            Button accept = Named<Button>(window, "Accept the selected candidate as one connection, as your decision");
            Assert.True(accept.IsEnabled);
            Assert.False(Named<Button>(window, "Withdraw your decision about the selected candidate").IsEnabled);
            await window.DecideSelectedAsync(WorkspaceJoinDecision.Accepted);
            Assert.EndsWith("· accepted by a person", window.Candidates!.Rows[0].Title, StringComparison.Ordinal);
            Assert.Equal("1 candidate join, none established by evidence; 1 accepted and 0 rejected by a person; 0 not the only match of a connection.",
                window.Candidates.Summary);
            window.SelectCandidate(0);
            Assert.False(accept.IsEnabled);
            Assert.True(Named<Button>(window, "Withdraw your decision about the selected candidate").IsEnabled);
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
            Assert.EndsWith("of the investigation's time, the investigation's own clock, exactly.", window.TimelineSentences[0], StringComparison.Ordinal);
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
            Assert.False(Named<CheckBox>(prompt, $"Copy session {Short(c)}, found in gamma").IsEnabled);
            Assert.Equal([a, b], prompt.Chosen);
            Assert.StartsWith("This saves the investigation case.icat-workspace with an exact copy of 2 of its 3 sessions",
                prompt.Statements[0], StringComparison.Ordinal);
            Assert.Equal(InvestigationPackage.Warning, prompt.Statements[^1]);
            Assert.Equal("Save unredacted package…", Named<Button>(prompt, "Save the package in a new folder you choose").Content);
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

    private static T Named<T>(Window window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(control => AutomationProperties.GetName(control) == name);

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
        Avalonia.Media.Imaging.WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        string directory = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(directory);
        frame!.Save(Path.Combine(directory, name));
    }
}
