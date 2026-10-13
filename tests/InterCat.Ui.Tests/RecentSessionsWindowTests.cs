using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
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
            Assert.Equal(SessionStore.NoGeneration + " The current workspace is unchanged.",
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
                + "The current workspace is unchanged.", detail.Text);
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
                + "another session is there now. The current workspace is unchanged.", detail.Text);
            RecentSessionRow replacement = list.Items.Cast<RecentSessionRow>().First();
            Assert.Equal((middle, false), (replacement.Path, replacement.SessionId == listed[1].SessionId));

            // Torn: a folder that still holds a session's files, which cannot be read, is never said gone. The open says
            // why, beside the row it named, and the list and the keyboard stay where they were.
            File.WriteAllText(Path.Combine(oldest, SessionPointerV1.FileName), "{");
            await Enter(window, list, 1);
            Assert.Equal("Could not open this session", status.Text);
            Assert.StartsWith("This session cannot be read, because no generation of it verifies: ", detail.Text,
                StringComparison.Ordinal);
            Assert.EndsWith("Its files are left as they are. The current workspace is unchanged.", detail.Text,
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
                + "The current workspace is unchanged.", detail.Text);
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
