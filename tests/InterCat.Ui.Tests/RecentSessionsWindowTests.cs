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
