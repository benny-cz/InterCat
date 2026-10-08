using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using InterCat.Desktop;
using Xunit;

namespace InterCat.Ui.Tests;

/// <summary>
/// The Explore card's Keep choice (§20.2, ADR-045): every record of a ten-minute capture by default, which needs no
/// configuration, or the newest stretch of session time of one that records until it is stopped - said beneath the choice,
/// and told to the capture it starts.
/// </summary>
public sealed class CaptureKeepWindowTests
{
    [AvaloniaFact(DisplayName = "S5: the Explore card keeps every record of ten minutes unless a person chooses a window, which it explains and the capture is told")]
    public async Task TheExploreCardOffersAWindowToKeep()
    {
        var window = new MainWindow { Width = 1_456, Height = 939 };
        window.Show();
        try
        {
            ComboBox selector = window.GetControl<ComboBox>("CaptureKeepSelector");
            TextBlock note = window.GetControl<TextBlock>("CaptureKeepNote");
            Grid row = window.GetControl<Grid>("CaptureKeepRow");

            // Every record of a ten-minute capture, the first-run default: nothing to explain, nothing released.
            Assert.Equal(["Every record, 10 minutes", "The last 10 minutes", "The last hour"],
                selector.Items.Cast<CaptureKeepChoice>().Select(choice => choice.Label));
            Assert.Same(CaptureKeepChoice.All[0], window.KeepChoice);
            Assert.False(note.IsVisible);
            CaptureRunOptions options = window.KeepChoice.Options(null);
            Assert.Equal((600, null), (options.MaximumDurationSeconds, options.Rolling));
            Assert.Equal("Every record, 10 minutes: records for up to 10 minutes and keeps every record.",
                CaptureKeepChoice.All[0].AccessibleName);

            // A window: the capture records until stopped, for up to a day, and its session keeps the last 10 minutes, as
            // the note beneath the choice says, with what can stop it sooner.
            selector.SelectedIndex = 1;
            Dispatch();
            Assert.True(note.IsVisible);
            Assert.Equal("This capture records until you stop it, for up to 24 hours, and keeps the last 10 minutes of "
                + "session time, releasing older records as it goes. The broker releases its own copy of what the session "
                + "gave up, so its journal limit bounds what it still holds rather than all it recorded.", note.Text);
            Assert.Equal("The last 10 minutes: " + window.KeepChoice.Explanation, ToolTip.GetTip(selector));
            options = window.KeepChoice.Options(null);
            Assert.Equal((CaptureKeepChoice.UntilStoppedSeconds, TimeSpan.FromMinutes(10), "the last 10 minutes"),
                (options.MaximumDurationSeconds, options.Rolling!.Keep, options.Rolling.Window));
            Assert.Equal(TimeSpan.FromHours(1), CaptureKeepChoice.All[2].Options(null).Rolling!.Keep);

            // While a capture runs the choice is not offered: it was told to the capture when it started.
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "Recording · follow latest", "Recording."));
            Dispatch();
            Assert.False(row.IsVisible);
            Assert.False(note.IsVisible);
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Unavailable, "Live capture needs Windows", "No capture was started."));
            Dispatch();
            Assert.True(row.IsVisible);
            Assert.True(note.IsVisible);
            Assert.Equal("What the capture keeps", AutomationProperties.GetName(selector));

            // Starting a capture tells it the choice made: here the last hour, until stopped. Where no capture can run, the
            // card says so and keeps the choice for the next try.
            selector.SelectedIndex = 2;
            window.GetControl<Button>("StartExploringButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            // The window asks whether to stop a capture still running when it closes, so the capture is let end first.
            await Assert.IsAssignableFrom<Task>(window.CaptureRun).WaitAsync(TimeSpan.FromSeconds(60));
            Dispatch();
            CaptureRunOptions started = Assert.IsType<CaptureRunOptions>(window.StartedWith);
            Assert.Equal((CaptureKeepChoice.UntilStoppedSeconds, TimeSpan.FromHours(1)),
                (started.MaximumDurationSeconds, started.Rolling!.Keep));
            Assert.Same(CaptureKeepChoice.All[2], window.KeepChoice);
        }
        finally
        {
            window.Close();
        }
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
