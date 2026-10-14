using Avalonia;
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

/// <summary>§3.1 in the window: while no session is open, the saved ones are one gesture away.</summary>
public sealed class RecentSessionsWindowTests
{
    [AvaloniaFact(DisplayName = "§3.1: a folder no session was published in does not open, says so in a person's words, and is left as it was")]
    public async Task AFolderWithNoSessionDoesNotOpen()
    {
        using var root = new TemporaryDirectory();
        string folder = Path.Combine(root.Path, "Documents");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "a folder chosen by mistake");
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            Assert.False(await window.OpenSessionAsync(folder));
            Assert.Equal("Could not open this session", window.GetControl<TextBlock>("CaptureStatus").Text);
            Assert.Equal(SessionStore.NoGeneration + " What the window shows is unchanged.",
                window.GetControl<TextBlock>("CaptureDetail").Text);
            Assert.Equal(["notes.txt"], Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§3.1: while no session is open the saved ones are listed where the ranked table will be, and Enter opens one")]
    public async Task SavedSessionsAreListedAndOpen()
    {
        using var root = new TemporaryDirectory();
        string older = Session(root.Path, "explore-20260926T090000Z-a", records: 3, Committed.AddHours(-1));
        string newer = Session(root.Path, "explore-20260926T100000Z-b", records: 2, Committed);
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            StackPanel panel = window.GetControl<StackPanel>("RecentSessionsPanel");
            ListBox list = window.GetControl<ListBox>("RecentSessionsList");
            Assert.False(panel.IsVisible);

            await window.UseSessionRootAsync(root.Path);
            Dispatch();
            Assert.True(panel.IsVisible);
            Assert.Equal([newer, older], list.Items.Cast<RecentSessionRow>().Select(row => row.Path));

            // At the minimum window it lies in the rail with the Explore card's actions still whole below it, and it
            // does not take first run's focus.
            Control rail = window.GetControl<Control>("Rail");
            AssertInside(panel, rail, window);
            AssertInside(window.GetControl<Button>("StartExploringButton"), rail, window);
            AssertInside(window.GetControl<Button>("OpenSavedSessionButton"), rail, window);
            Assert.True(window.GetControl<Button>("StartExploringButton").IsFocused);
            Save(window, "recent-sessions-1080x700.png");

            // A running capture hides it with the other actions that wait for the capture to end.
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "Recording · follow latest", "Recording."));
            Dispatch();
            Assert.False(panel.IsVisible);
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Unavailable, "Explore did not start", "Declined."));
            Dispatch();
            Assert.True(panel.IsVisible);

            // From the keyboard, as Tab reaches it: a row takes the focus without being selected, and Enter opens that row.
            // A control that has just become visible takes focus once a layout pass has placed it.
            _ = window.CaptureRenderedFrame();
            Assert.True(list.ContainerFromIndex(0)!.Focus());
            Assert.Null(list.SelectedItem);
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            for (int attempt = 0; attempt < 200 && panel.IsVisible; attempt++)
            {
                await Task.Delay(10);
                Dispatch();
            }

            Assert.False(panel.IsVisible);
            Assert.Equal("Saved session open", window.GetControl<TextBlock>("CaptureStatus").Text);
            Assert.Equal(newer, window.GetControl<TextBlock>("CaptureSessionPath").Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§3.1: a saved session deleted, written over or emptied since the rail listed it is said gone as its row named it, never opened as another, and the list is read again; one torn is said why, and stays listed")]
    public async Task ASavedSessionGoneSinceListedIsSaidGone()
    {
        using var root = new TemporaryDirectory();
        string oldest = Session(root.Path, "explore-20260926T080000Z-a", records: 3, Committed.AddHours(-2));
        string middle = Session(root.Path, "explore-20260926T090000Z-b", records: 2, Committed.AddHours(-1));
        string newest = Session(root.Path, "explore-20260926T100000Z-c", records: 4, Committed);
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            ListBox list = window.GetControl<ListBox>("RecentSessionsList");
            TextBlock status = window.GetControl<TextBlock>("CaptureStatus");
            TextBlock detail = window.GetControl<TextBlock>("CaptureDetail");
            await window.UseSessionRootAsync(root.Path);
            Dispatch();
            RecentSessionRow[] listed = [.. list.Items.Cast<RecentSessionRow>()];
            Assert.Equal([newest, middle, oldest], listed.Select(row => row.Path));

            // Deleted: the row says what it was, the list no longer holds it, and the keyboard stays on the newest left.
            Directory.Delete(newest, recursive: true);
            await Enter(window, list, 0);
            Assert.Equal("Could not open this session", status.Text);
            Assert.Equal($"{listed[0].Title}, {listed[0].Detail}, is no longer in its folder: its files were moved or deleted. "
                + "What the window shows is unchanged.", detail.Text);
            Assert.Equal([middle, oldest], list.Items.Cast<RecentSessionRow>().Select(row => row.Path));
            Assert.Equal(middle, Assert.IsType<RecentSessionRow>(
                Assert.IsType<ListBoxItem>(window.FocusManager!.GetFocusedElement()).DataContext).Path);

            // Written over by another session: that one is not opened as the row's, and is listed in its place.
            foreach (string file in Directory.EnumerateFiles(middle))
            {
                File.Delete(file);
            }

            _ = Session(root.Path, "explore-20260926T090000Z-b", records: 7, Committed.AddHours(1));
            await Enter(window, list, 0);
            Assert.True(Assert.IsType<WorkspaceViewModel>(window.DataContext).IsEmptyWorkspace);
            Assert.Equal($"{listed[1].Title}, {listed[1].Detail}, is no longer in its folder: its files were moved or deleted; "
                + "another session is there now. What the window shows is unchanged.", detail.Text);
            RecentSessionRow replacement = list.Items.Cast<RecentSessionRow>().First();
            Assert.Equal((middle, false), (replacement.Path, replacement.SessionId == listed[1].SessionId));

            // Torn: a folder that still holds a session's files, which cannot be read, is never said gone. The open says
            // why, beside the row it named, and the list and the keyboard stay where they were.
            File.WriteAllText(Path.Combine(oldest, SessionPointerV1.FileName), "{");
            await Enter(window, list, 1);
            Assert.Equal("Could not open this session", status.Text);
            Assert.StartsWith("This session cannot be read, because no generation of it verifies: ", detail.Text,
                StringComparison.Ordinal);
            Assert.EndsWith("Its files are left as they are. What the window shows is unchanged.", detail.Text,
                StringComparison.Ordinal);
            Assert.Equal([middle, oldest], list.Items.Cast<RecentSessionRow>().Select(row => row.Path));
            Assert.Equal(oldest, Assert.IsType<RecentSessionRow>(
                Assert.IsType<ListBoxItem>(window.FocusManager!.GetFocusedElement()).DataContext).Path);

            // Emptied: said gone in the same words, never as a folder no session was ever published in.
            foreach (string file in Directory.EnumerateFiles(oldest))
            {
                File.Delete(file);
            }

            await Enter(window, list, 1);
            Assert.Equal($"{listed[2].Title}, {listed[2].Detail}, is no longer in its folder: its files were moved or deleted. "
                + "What the window shows is unchanged.", detail.Text);
            Assert.Equal([middle], list.Items.Cast<RecentSessionRow>().Select(row => row.Path));

            // The session listed in the other's place opens as itself.
            await Enter(window, list, 0);
            Assert.Equal("Saved session open", status.Text);
            Assert.Equal(middle, window.GetControl<TextBlock>("CaptureSessionPath").Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§3.1: the saved sessions are read again as the window comes back into use, the keyboard on the row it was on, and left as they are when nothing changed")]
    public async Task TheSavedSessionsAreReadAgainAsTheWindowComesBack()
    {
        using var root = new TemporaryDirectory();
        string a = Session(root.Path, "explore-20260926T080000Z-a", records: 1, Committed.AddHours(-3));
        string b = Session(root.Path, "explore-20260926T090000Z-b", records: 2, Committed.AddHours(-2));
        string c = Session(root.Path, "explore-20260926T100000Z-c", records: 3, Committed.AddHours(-1));
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            ListBox list = window.GetControl<ListBox>("RecentSessionsList");
            await window.UseSessionRootAsync(root.Path);
            Dispatch();
            Assert.Equal([c, b, a], Paths(list));

            // The keyboard is on a row as the person goes to another window, where a session is saved and one deleted.
            _ = window.CaptureRenderedFrame();
            Assert.True(list.ContainerFromIndex(1)!.Focus());
            string d = Session(root.Path, "explore-20260926T110000Z-d", records: 4, Committed);
            Directory.Delete(a, recursive: true);

            // Back in use, the list says what the folder holds, and the keyboard is on the row it was on, never passing
            // to another control meanwhile, which a screen reader would say.
            Button start = window.GetControl<Button>("StartExploringButton");
            int elsewhereFocused = 0;
            start.GotFocus += (_, _) => elsewhereFocused++;
            await ComeBack(window);
            Assert.Equal([d, c, b], Paths(list));
            Assert.Equal(b, FocusedRow(window).Path);
            Assert.Equal(0, elsewhereFocused);

            // With nothing changed meanwhile the rows are left as they are.
            object? rows = list.ItemsSource;
            await ComeBack(window);
            Assert.Same(rows, list.ItemsSource);
            Assert.Equal(b, FocusedRow(window).Path);

            // Its own row deleted meanwhile, the keyboard is on the newest still listed.
            Directory.Delete(b, recursive: true);
            await ComeBack(window);
            Assert.Equal([d, c], Paths(list));
            Assert.Equal(d, FocusedRow(window).Path);
            Assert.Equal(0, elsewhereFocused);

            // Every session deleted meanwhile, the list goes, and the keyboard with it to where the window gives it.
            Directory.Delete(c, recursive: true);
            Directory.Delete(d, recursive: true);
            await ComeBack(window);
            Assert.Empty(Paths(list));
            Assert.False(window.GetControl<Control>("RecentSessionsPanel").IsVisible);
            Assert.True(start.IsFocused);

            // While a session is shown the list is not read: it is read again when it is shown.
            string e = Session(root.Path, "explore-20260926T120000Z-e", records: 5, Committed.AddHours(1));
            await ComeBack(window);
            Assert.Equal([e], Paths(list));
            Assert.True(await window.OpenSessionAsync(e));
            Dispatch();
            rows = list.ItemsSource;
            _ = Session(root.Path, "explore-20260926T130000Z-f", records: 6, Committed.AddHours(2));
            await ComeBack(window);
            Assert.Same(rows, list.ItemsSource);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§3.1: a capture's session is listed once the capture ends, shown when no session is, and a saved session opens on a double click, as Enter opens it")]
    public async Task ACapturesSessionIsListedAndOpensOnADoubleClick()
    {
        using var root = new TemporaryDirectory();
        string captured = Path.Combine(root.Path, "explore-20261014T090000Z-c");
        var window = new MainWindow { Width = 1080, Height = 700 };
        int runs = 0;
        window.CaptureRunner = (report, _, _) =>
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                // The first capture saves its session among the saved ones, and ends.
                report(new(CaptureUiPhase.Recording, "Recording · follow latest", "Recording.", SessionPath: captured));
                SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(Directory.CreateDirectory(captured).FullName),
                    Guid.NewGuid(), "recent-window-tests");
                _ = Publish(store, [Lifecycle(100, ObservationKind.Create, 400, 1) with { SessionRelativeTicks = 100 }],
                    committedUtc: Committed);
                report(new(CaptureUiPhase.Complete, "Capture saved", "Saved.", SessionPath: captured,
                    Overview: SessionOverviewProjector.Project(store)));
                return Task.CompletedTask;
            }

            // The next records, then stops before it shows anything.
            string next = Path.Combine(root.Path, "explore-20261014T100000Z-d");
            report(new(CaptureUiPhase.Recording, "Recording · follow latest", "Recording.", SessionPath: next));
            report(new(CaptureUiPhase.Unavailable, "Capture interrupted", "The broker stopped.", SessionPath: next));
            return Task.CompletedTask;
        };
        window.Show();
        try
        {
            await window.UseSessionRootAsync(root.Path);
            Dispatch();
            StackPanel panel = window.GetControl<StackPanel>("RecentSessionsPanel");
            ListBox list = window.GetControl<ListBox>("RecentSessionsList");
            TextBlock status = window.GetControl<TextBlock>("CaptureStatus");
            Assert.Empty(Paths(list));

            // The capture's session is shown once it ends, and listed among the saved ones for when none is.
            window.GetControl<Button>("StartExploringButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => status.Text == "Capture saved" && Paths(list).SequenceEqual([captured]));
            Assert.False(panel.IsVisible);

            // The next capture shows nothing before it stops: no session is shown, and the saved ones are listed.
            window.GetControl<Button>("StartExploringButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => status.Text == "Capture interrupted");
            Assert.True(panel.IsVisible);
            Assert.Equal([captured], Paths(list));

            // A double click on it opens it, as Enter does.
            _ = window.CaptureRenderedFrame();
            Control row = list.ContainerFromIndex(0)!;
            Point at = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
            for (int click = 0; click < 2; click++)
            {
                window.MouseDown(at, MouseButton.Left);
                window.MouseUp(at, MouseButton.Left);
            }

            await Until(() => status.Text == "Saved session open");
            Assert.Equal(captured, window.GetControl<TextBlock>("CaptureSessionPath").Text);
            Assert.False(panel.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            await Task.Delay(10);
            Dispatch();
        }

        Assert.True(condition());
    }

    /// <summary>Brings the window back into use, as from another window, and lets it read its folder again.</summary>
    private static async Task ComeBack(MainWindow window)
    {
        window.Activate();
        Dispatch();
        await window.FolderRead;
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
    }

    private static string[] Paths(ListBox list) => [.. list.Items.Cast<RecentSessionRow>().Select(row => row.Path)];

    private static RecentSessionRow FocusedRow(MainWindow window) => Assert.IsType<RecentSessionRow>(
        Assert.IsType<ListBoxItem>(window.FocusManager!.GetFocusedElement()).DataContext);

    /// <summary>Presses Enter on a row of the saved sessions, as the keyboard reaches it, and lets the open finish.</summary>
    private static async Task Enter(MainWindow window, ListBox list, int index)
    {
        _ = window.CaptureRenderedFrame();
        Assert.True(list.ContainerFromIndex(index)!.Focus());
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        for (int attempt = 0; attempt < 100; attempt++)
        {
            await Task.Delay(10);
            Dispatch();
        }

        _ = window.CaptureRenderedFrame();
        Dispatch();
    }

    private static string Session(string root, string name, int records, DateTimeOffset savedUtc)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory), Guid.NewGuid(), "recent-window-tests");
        _ = Publish(
            store,
            [.. Enumerable.Range(0, records).Select(index =>
                Lifecycle(100 + index, ObservationKind.Create, 400 + index, (ulong)(index + 1)) with { SessionRelativeTicks = 100 + index })],
            committedUtc: savedUtc);
        return directory;
    }

    private static void AssertInside(Control inner, Control outer, Window window)
    {
        Rect bounds = new(inner.TranslatePoint(default, window)!.Value, inner.Bounds.Size);
        Rect container = new(outer.TranslatePoint(default, window)!.Value, outer.Bounds.Size);
        Assert.True(
            container.Contains(bounds.TopLeft) && container.Contains(bounds.BottomRight - new Vector(0.5, 0.5)),
            $"{inner.Name} at {bounds} is not inside {outer.Name} at {container}.");
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    private static void Save(Window window, string name)
    {
        Avalonia.Media.Imaging.WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        string directory = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(directory);
        frame!.Save(Path.Combine(directory, name));
    }
}
