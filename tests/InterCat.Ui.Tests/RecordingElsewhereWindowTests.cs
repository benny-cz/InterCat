using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Capture.Journal;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// §19.3, ADR-057 for a session another process records - another window's capture, or `icat capture`: a window that opens
/// it follows what that capture publishes as it follows its own, says so, holds a newer generation while records are read
/// or the view is paused, and shows the session as saved once the recording ends. The capture's running follow holds the
/// ticket beside the session, whose sharing only Windows enforces, so these windows are told the follow's state by a
/// stand-in.
/// </summary>
public sealed class RecordingElsewhereWindowTests
{
    private const string ClientEnd = "127.0.0.1:50000";
    private const string ServerEnd = "127.0.0.1:8080";

    [AvaloniaFact(DisplayName = "§19.3: a session opened while another capture records it follows what that capture publishes, holds it while paused, and shows the session as saved once the recording ends")]
    public async Task ASessionRecordedElsewhereIsFollowed()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(0, 10));
        LiveFollowState recording = LiveFollowState.Running;
        bool held = false;
        var window = new MainWindow
        {
            RecordingProbe = _ => held ? throw new IOException("The ticket is held for a moment.") : recording,
            RecordingWatchInterval = TimeSpan.FromMilliseconds(20),
        };
        window.Show();
        Assert.True(await window.OpenSessionAsync(session.Path));
        Dispatch();

        // The card and the health strip say the session is still being recorded, and that the view follows it; the loss
        // is stated once it stops, and a package waits for it.
        Assert.Equal("Session open · still being recorded", window.GetControl<TextBlock>("CaptureStatus").Text);
        Assert.StartsWith("Another InterCat capture, from another window or icat capture, is recording this session",
            window.GetControl<TextBlock>("CaptureDetail").Text, StringComparison.Ordinal);
        Assert.Equal("Recording elsewhere · following live", window.GetControl<TextBlock>("HealthStateText").Text);
        Assert.Equal("Loss is stated when recording stops", window.GetControl<TextBlock>("HealthLossText").Text);
        Assert.True(window.GetControl<Button>("FollowButton").IsVisible);
        Button package = window.GetControl<Button>("SharePackageButton");
        Assert.False(package.IsEnabled);
        Assert.StartsWith("Another capture is still recording this session", ToolTip.GetTip(package) as string, StringComparison.Ordinal);

        // The card keeps what it says, such as where an export was written, until the recording says something new.
        var choices = new AnsweredChoices(window);
        window.Choices = choices;
        string folder = Directory.CreateTempSubdirectory("InterCat.Ui.Tests.Elsewhere.").FullName;
        string csv = Path.Combine(folder, "view.csv");
        try
        {
            choices.Answer(csv);
            window.GetControl<Button>("ExportButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            TextBlock detail = window.GetControl<TextBlock>("CaptureDetail");
            await Until(() => detail.Text!.EndsWith($" to {csv}.", StringComparison.Ordinal));
            for (int turn = 0; turn < 10; turn++)
            {
                Dispatch();
                await Task.Delay(20);
            }

            Assert.EndsWith($" to {csv}.", detail.Text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        // The other capture publishes, and the view follows it, though a look meanwhile could not be made.
        held = true;
        Publish(session.Store, Exchange(10, 5));
        for (int turn = 0; turn < 5; turn++)
        {
            Dispatch();
            await Task.Delay(20);
        }

        held = false;
        await Until(() => Title(window).Contains("generation 2", StringComparison.Ordinal));
        Assert.StartsWith("last publication", window.GetControl<TextBlock>("HealthFreshnessText").Text, StringComparison.Ordinal);
        Assert.True(window.GetControl<Button>("FollowButton").IsVisible);
        Assert.EndsWith($" to {csv}.", window.GetControl<TextBlock>("CaptureDetail").Text, StringComparison.Ordinal);

        // Paused, a newer publication is held and offered, and the banner says recording continues; F follows again.
        var paused = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        window.GetControl<ListBox>("RungList").Focus();
        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.None);
        Assert.Equal("Recording elsewhere · view paused", window.GetControl<TextBlock>("HealthStateText").Text);
        Publish(session.Store, Exchange(15, 5));
        await Until(() => window.GetControl<Border>("HeldBanner").IsVisible);
        Assert.Same(paused, window.DataContext);
        Assert.Equal("View paused at generation 2. Generation 3 is published; recording continues.",
            window.GetControl<TextBlock>("HeldBannerText").Text);
        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.None);
        Dispatch();
        Assert.Contains("generation 3", Title(window), StringComparison.Ordinal);
        Assert.Equal("Recording elsewhere · following live", window.GetControl<TextBlock>("HealthStateText").Text);

        // The recording ends: its follow publishes the last generation and lets its ticket go. The view shows it as the
        // session saved, follows nothing more, and a package is offered again.
        Publish(session.Store, Exchange(20, 5));
        recording = LiveFollowState.None;
        await window.RecordingWatched.WaitAsync(TimeSpan.FromMinutes(1));
        Dispatch();
        Assert.Contains("generation 4", Title(window), StringComparison.Ordinal);
        Assert.Equal("Saved session open", window.GetControl<TextBlock>("CaptureStatus").Text);
        Assert.Equal("Its recording ended: generation 4 is the session as saved.", window.GetControl<TextBlock>("CaptureDetail").Text);
        Assert.Equal("Saved session", window.GetControl<TextBlock>("HealthStateText").Text);
        Assert.False(window.GetControl<Button>("FollowButton").IsVisible);
        Assert.True(package.IsEnabled);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§19.3: a newer generation of a session recorded elsewhere is held while its records are read, and a follow that stops early is said to have")]
    public async Task ARecordingElsewhereIsHeldWhileRecordsAreRead()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(0, 10));
        LiveFollowState recording = LiveFollowState.Running;
        var window = new MainWindow { RecordingProbe = _ => recording, RecordingWatchInterval = TimeSpan.FromMilliseconds(20) };
        window.Show();
        Assert.True(await window.OpenSessionAsync(session.Path));
        Dispatch();
        var reading = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        window.GetControl<ListBox>("RungList").Focus();
        window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);
        await reading.EvidenceReady;
        Dispatch();

        Publish(session.Store, Exchange(10, 5));
        await Until(() => window.GetControl<Border>("HeldBanner").IsVisible);
        Assert.Same(reading, window.DataContext);
        Assert.Equal("Recording elsewhere · view held while you read records", window.GetControl<TextBlock>("HealthStateText").Text);
        Assert.Equal("Showing generation 1 while you read records. Generation 2 is published; recording continues.",
            window.GetControl<TextBlock>("HeldBannerText").Text);

        // Its follow ends before the session holds all the capture published: the view says so, and what it shows stays
        // until the records are left.
        recording = LiveFollowState.Interrupted;
        await window.RecordingWatched.WaitAsync(TimeSpan.FromMinutes(1));
        Dispatch();
        Assert.Equal("The capture recording it stopped before this session held all it published: generation 2 is the newest it holds.",
            window.GetControl<TextBlock>("CaptureDetail").Text);
        Assert.False(window.GetControl<Button>("FollowButton").IsVisible);
        Assert.Equal("Showing generation 1 while you read records. Generation 2 is the session as saved.",
            window.GetControl<TextBlock>("HeldBannerText").Text);
        Assert.True(window.GetControl<Button>("SharePackageButton").IsEnabled);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatch();
        Assert.Contains("generation 2", Title(window), StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§19.3: a recording elsewhere that ends while the view is paused shows the generation it held, as the session saved")]
    public async Task ARecordingEndedWhilePausedShowsTheSessionSaved()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Exchange(0, 10));
        LiveFollowState recording = LiveFollowState.Running;
        var window = new MainWindow { RecordingProbe = _ => recording, RecordingWatchInterval = TimeSpan.FromMilliseconds(20) };
        window.Show();
        Assert.True(await window.OpenSessionAsync(session.Path));
        Dispatch();
        window.GetControl<ListBox>("RungList").Focus();
        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.None);
        Publish(session.Store, Exchange(10, 5));
        await Until(() => window.GetControl<Border>("HeldBanner").IsVisible);
        Assert.Equal("View paused at generation 1. Generation 2 is published; recording continues.",
            window.GetControl<TextBlock>("HeldBannerText").Text);

        // Nothing more is published to hold the view from, so the generation it held is shown as the session saved.
        recording = LiveFollowState.None;
        await window.RecordingWatched.WaitAsync(TimeSpan.FromMinutes(1));
        Dispatch();
        Assert.Contains("generation 2", Title(window), StringComparison.Ordinal);
        Assert.False(window.GetControl<Border>("HeldBanner").IsVisible);
        Assert.Equal("Its recording ended: generation 2 is the session as saved.", window.GetControl<TextBlock>("CaptureDetail").Text);
        Assert.Equal("Saved session", window.GetControl<TextBlock>("HealthStateText").Text);
        Assert.False(window.GetControl<Button>("FollowButton").IsVisible);
        Assert.True(window.GetControl<Button>("SharePackageButton").IsEnabled);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§19.3: opening another session stops following a session recorded elsewhere, and a saved session is not followed")]
    public async Task OpeningAnotherStopsFollowing()
    {
        using var session = new TemporarySession();
        using var other = new TemporarySession();
        Publish(session.Store, Exchange(0, 10));
        Publish(other.Store, Exchange(0, 4));
        var window = new MainWindow
        {
            RecordingProbe = path => path == session.Path ? LiveFollowState.Running : LiveFollowState.None,
            RecordingWatchInterval = TimeSpan.FromMilliseconds(20),
        };
        window.Show();
        Assert.True(await window.OpenSessionAsync(session.Path));
        Task following = window.RecordingWatched;
        Assert.False(following.IsCompleted);

        Assert.True(await window.OpenSessionAsync(other.Path));
        await following.WaitAsync(TimeSpan.FromMinutes(1));
        Assert.True(window.RecordingWatched.IsCompleted);
        Dispatch();
        Assert.Equal("Saved session open", window.GetControl<TextBlock>("CaptureStatus").Text);
        Assert.Equal("Saved session", window.GetControl<TextBlock>("HealthStateText").Text);

        // The first session publishes on; the view of the other one stays as it is.
        Publish(session.Store, Exchange(10, 5));
        for (int turn = 0; turn < 10; turn++)
        {
            Dispatch();
            await Task.Delay(20);
        }

        Assert.Contains("generation 1", Title(window), StringComparison.Ordinal);
        Assert.False(window.GetControl<Button>("FollowButton").IsVisible);
        window.Close();
    }

    [AvaloniaTheory(DisplayName = "§19.3: a look at a recording elsewhere still under way when another session is opened is let go, whether it found nothing new, a newer generation or the recording ended, and the other session's view and card stay as they are")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ALookUnderWayWhenAnotherIsOpenedIsLetGo(bool published, bool ended)
    {
        using var session = new TemporarySession();
        using var other = new TemporarySession();
        Publish(session.Store, Exchange(0, 10));
        Publish(other.Store, Exchange(0, 4));
        using var gate = new ManualResetEventSlim();
        bool hold = false;
        bool looking = false;
        var window = new MainWindow
        {
            RecordingProbe = path =>
            {
                if (path != session.Path)
                {
                    return LiveFollowState.None;
                }

                // Once the session is open, a look reading the ticket waits until another session has been opened, and
                // finds the recording running still or ended.
                if (!Volatile.Read(ref hold))
                {
                    return LiveFollowState.Running;
                }

                Volatile.Write(ref looking, true);
                gate.Wait(TimeSpan.FromMinutes(1));
                return ended ? LiveFollowState.None : LiveFollowState.Running;
            },
            RecordingWatchInterval = TimeSpan.FromMilliseconds(20),
        };
        window.Show();
        Assert.True(await window.OpenSessionAsync(session.Path));
        Task following = window.RecordingWatched;
        Volatile.Write(ref hold, true);
        await Until(() => Volatile.Read(ref looking));

        // A newer generation published meanwhile is projected for a view that no longer shows its session, and let go.
        if (published)
        {
            Publish(session.Store, Exchange(10, 5));
        }

        Assert.True(await window.OpenSessionAsync(other.Path));
        Dispatch();
        string[] card = Card(window);
        Assert.Equal("Saved session open", card[0]);
        gate.Set();
        await following.WaitAsync(TimeSpan.FromMinutes(1));
        Dispatch();
        Assert.Equal(SessionOverviewProjector.Project(other.Store).Extent, Assert.IsType<WorkspaceViewModel>(window.DataContext).Snapshot.Extent);
        Assert.Contains("generation 1", Title(window), StringComparison.Ordinal);
        Assert.Equal(card, Card(window));
        Assert.False(window.GetControl<Button>("FollowButton").IsVisible);
        window.Close();

        static string[] Card(MainWindow window) =>
        [
            window.GetControl<TextBlock>("CaptureStatus").Text!,
            window.GetControl<TextBlock>("CaptureDetail").Text!,
            window.GetControl<TextBlock>("HealthStateText").Text!,
        ];
    }

    private static string Title(MainWindow window) => Assert.IsType<WorkspaceViewModel>(window.DataContext).Snapshot.Title;

    /// <summary>Lets the window run until <paramref name="done"/> holds, as its following looks for and applies generations.</summary>
    private static async Task Until(Func<bool> done)
    {
        for (int turn = 0; turn < 1_000 && !done(); turn++)
        {
            Dispatch();
            await Task.Delay(10);
        }

        Dispatch();
        Assert.True(done());
    }

    /// <summary>A client sending and a server receiving, <paramref name="count"/> times from <paramref name="first"/>.</summary>
    private static ObservationRowV1[] Exchange(int first, int count) =>
    [
        .. Enumerable.Range(first, count).SelectMany(index => new[]
        {
            Transfer(10 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 64, 100, (ulong)(100 + (2 * index)))
                .Between(ClientEnd, ServerEnd) with { SessionRelativeTicks = (10 + (2 * index)) * 100L },
            Transfer(11 + (2 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200,
                (ulong)(101 + (2 * index))).Between(ServerEnd, ClientEnd) with { SessionRelativeTicks = (11 + (2 * index)) * 100L },
        }),
    ];

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
