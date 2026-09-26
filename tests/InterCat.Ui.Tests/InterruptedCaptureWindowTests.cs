using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using InterCat.Capture.Journal;
using InterCat.Capture.Journal.Tests;
using InterCat.Desktop;
using InterCat.Storage;
using Xunit;

namespace InterCat.Ui.Tests;

/// <summary>
/// §3.1 step 6 in the window: a viewer that crashed while recording loses no evidence, and the next launch offers to
/// finish its session from the evidence the broker kept.
/// </summary>
public sealed class InterruptedCaptureWindowTests
{
    [AvaloniaFact(DisplayName = "§3.1: the next launch offers to finish a crashed viewer's session, and finishing opens it whole")]
    public async Task TheNextLaunchFinishesACrashedSession()
    {
        using var root = new TemporaryDirectory();
        (string sessions, string session, string evidence) = await CrashedFollow(root.Path, unfinalized: false);
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            Border card = window.GetControl<Border>("UnfinishedCaptureCard");
            Assert.False(card.IsVisible);

            await window.UseSessionRootAsync(sessions);
            Dispatch();
            Assert.True(card.IsVisible);
            Assert.EndsWith("is not finished saving", window.GetControl<TextBlock>("UnfinishedCaptureHeadline").Text,
                StringComparison.Ordinal);
            Assert.Contains("not in your session yet", window.GetControl<TextBlock>("UnfinishedCaptureDetail").Text,
                StringComparison.Ordinal);
            Button finish = window.GetControl<Button>("FinishCaptureButton");
            Assert.True(finish.IsVisible);
            Assert.Equal("Finish saving", finish.Content);

            // The offer does not take first run's focus, and at the minimum window it lies whole inside the rail, above
            // the Explore card whose Start action stays in view.
            Assert.True(window.GetControl<Button>("StartExploringButton").IsFocused);
            Control rail = window.GetControl<Control>("Rail");
            AssertInside(card, rail, window);
            AssertInside(window.GetControl<Button>("StartExploringButton"), rail, window);
            AssertInside(window.GetControl<Button>("OpenSavedSessionButton"), rail, window);
            Save(window, "unfinished-capture-1080x700.png");

            await window.ActOnInterruptedCaptureAsync();
            Dispatch();

            Assert.False(card.IsVisible);
            Assert.Equal("Session saved", window.GetControl<TextBlock>("CaptureStatus").Text);
            Assert.Equal(session, window.GetControl<TextBlock>("CaptureSessionPath").Text);
            Assert.False(File.Exists(LiveFollowTicket.PathFor(session)));
            Assert.Equal(4, JournalRederivation.Verify(SessionStore.OpenExisting(LocalOwnedDirectory.Open(session)))
                .ObservationRows);
            Assert.True(Directory.Exists(evidence));
            Save(window, "unfinished-capture-finished-1080x700.png");

            // Nothing is left to offer at the launch after.
            await window.UseSessionRootAsync(sessions);
            Dispatch();
            Assert.False(card.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§3.1: a capture still stopping is waited for, and one forgotten is not offered again")]
    public async Task ACaptureStillStoppingIsWaitedForAndCanBeForgotten()
    {
        using var root = new TemporaryDirectory();
        (string sessions, string session, string evidence) = await CrashedFollow(root.Path, unfinalized: true);
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            await window.UseSessionRootAsync(sessions);
            Dispatch();
            Border card = window.GetControl<Border>("UnfinishedCaptureCard");
            Assert.True(card.IsVisible);
            Assert.EndsWith("is still stopping", window.GetControl<TextBlock>("UnfinishedCaptureHeadline").Text,
                StringComparison.Ordinal);
            Assert.False(window.GetControl<Button>("FinishCaptureButton").IsVisible);
            Button forget = window.GetControl<Button>("ForgetCaptureButton");
            Assert.Equal("Forget", forget.Content);

            // While a capture records, the card waits with the other actions that wait for it to end.
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "Recording · follow latest", "Recording."));
            Dispatch();
            Assert.False(card.IsVisible);
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Unavailable, "Explore did not start", "Declined."));
            Dispatch();
            Assert.True(card.IsVisible);

            await window.ForgetInterruptedCaptureAsync();
            Dispatch();
            Assert.False(card.IsVisible);
            Assert.False(File.Exists(LiveFollowTicket.PathFor(session)));
            Assert.True(Directory.Exists(session));
            Assert.True(Directory.Exists(evidence));
            await window.UseSessionRootAsync(sessions);
            Dispatch();
            Assert.False(card.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A capture's evidence, and a session whose viewer followed its first chunk and crashed, leaving its ticket. An
    /// unfinalized capture is the evidence as its first publication left it, and its owner lease has not settled yet.
    /// </summary>
    private static async Task<(string Sessions, string Session, string Evidence)> CrashedFollow(string root, bool unfinalized)
    {
        string evidencePath = Path.Combine(root, "broker", "capture-1");
        Directory.CreateDirectory(evidencePath);
        _ = await EvidenceRecordings.RecordEvidence(evidencePath, ordinals: [1, 2, 3, 4]);
        if (unfinalized)
        {
            _ = EvidenceRecordings.RewindToUnfinalized(evidencePath);
        }

        SessionStore evidence = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidencePath));
        string sessions = Path.Combine(root, "Sessions");
        string session = Path.Combine(sessions, "explore-20260926T153534Z-1");
        Directory.CreateDirectory(session);
        SessionStore followed = SessionStore.Open(
            LocalOwnedDirectory.Open(session), evidence.Current!.SessionId, evidence.Current.SourceIdentity);
        if (!unfinalized)
        {
            _ = LiveSessionFollower.Open(evidence, followed).CatchUp(maximumChunks: 1);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        LiveFollowTicket.For(evidence.Current.SessionId, evidencePath, session, now.AddMinutes(-1), now.AddSeconds(20))
            .Hold().Dispose();
        return (sessions, session, evidencePath);
    }

    private static void AssertInside(Control inner, Control outer, Window window)
    {
        Avalonia.Rect bounds = WindowBounds(inner, window);
        Avalonia.Rect container = WindowBounds(outer, window);
        Assert.True(
            container.Contains(bounds.TopLeft) && container.Contains(bounds.BottomRight - new Avalonia.Vector(0.5, 0.5)),
            $"{inner.Name} at {bounds} is not inside {outer.Name} at {container}.");
    }

    private static Avalonia.Rect WindowBounds(Control control, Window window) =>
        new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);

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
