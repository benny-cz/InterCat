using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using InterCat.Application;
using InterCat.Capture.Journal.Tests;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// §20.1 and S1 in the window: a saved session opens without its files being hashed first, and they are hashed after the
/// first view. Files that changed after they were published fall back to the last complete generation, and the window
/// says it shows that one rather than passing it off as the newest (S7).
/// </summary>
public sealed class SessionFilesWindowTests
{
    [AvaloniaFact(DisplayName = "§20.1: a session whose newest files changed after publication falls back to its last complete generation, and says so")]
    public async Task ChangedFilesFallBackAfterTheFirstView()
    {
        using var root = new TemporaryDirectory();
        (string session, string newest) = TwoGenerations(root.Path);

        // The trailer is never read by a first view: only the file's digest covers it.
        string file = Path.Combine(session, newest);
        Damage(file, (int)new FileInfo(file).Length - 1);
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        using var release = new ManualResetEventSlim();
        try
        {
            // The hashing is held back until the first view has been seen; a session this small hashes at once.
            Func<SessionStore, StoreContentReport> verify = window.SessionFilesVerifier;
            window.SessionFilesVerifier = store =>
            {
                release.Wait();
                return verify(store);
            };
            Assert.True(await window.OpenSessionAsync(session));
            Dispatch();
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            Assert.Equal(2, workspace.DisplayedGeneration);
            Assert.DoesNotContain("could not be verified", Detail(window), StringComparison.Ordinal);

            release.Set();
            await window.SessionFilesChecked;
            Dispatch();

            workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            Assert.Equal(1, workspace.DisplayedGeneration);
            Assert.Contains("Its newest generation could not be verified", Detail(window), StringComparison.Ordinal);
            Assert.Contains($"is shown: '{newest}' does not match what generation 2 records", Detail(window), StringComparison.Ordinal);
            Assert.Contains("generation 1, the last complete one, is shown", Detail(window), StringComparison.Ordinal);
            Assert.Contains("Nothing on disk was changed", Detail(window), StringComparison.Ordinal);
        }
        finally
        {
            release.Set();
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§20.1: a session whose first view reads a changed file opens its last complete generation at once, and says so")]
    public async Task AFirstViewThatReadsAChangedFileFallsBackAtOnce()
    {
        using var root = new TemporaryDirectory();
        (string session, string newest) = TwoGenerations(root.Path);

        // A segment's time column is read when the segment opens, so the first view meets this damage itself.
        SessionManifestV1 manifest = SessionStore.OpenExisting(LocalOwnedDirectory.Open(session)).Current!;
        int ticks = SessionSegments.Open(LocalOwnedDirectory.Open(session), manifest, newest)
            .Column(SegmentColumnId.NativeTicks)!.ValueOffset;
        Damage(Path.Combine(session, newest), ticks);
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            Assert.True(await window.OpenSessionAsync(session));
            Dispatch();

            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            Assert.Equal(1, workspace.DisplayedGeneration);
            Assert.Equal("Saved session open", window.GetControl<TextBlock>("CaptureStatus").Text);
            Assert.Contains($"'{newest}' does not match what generation 2 records", Detail(window), StringComparison.Ordinal);
            Assert.Contains("generation 1, the last complete one, is shown", Detail(window), StringComparison.Ordinal);

            // What remains to hash is the last complete generation's, and it checks out: nothing further changes.
            await window.SessionFilesChecked;
            Dispatch();
            Assert.Equal(1, Assert.IsType<WorkspaceViewModel>(window.DataContext).DisplayedGeneration);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A session with two generations; the second publishes its own segment, whose name is returned.</summary>
    [AvaloniaFact(DisplayName = "§20.1: a package asked for while a session's files are checked waits for the check, and asks about what the check leaves shown")]
    public async Task APackageWaitsForTheFilesCheck()
    {
        using var root = new TemporaryDirectory();
        (string session, string newest) = TwoGenerations(root.Path);
        string file = Path.Combine(session, newest);
        Damage(file, (int)new FileInfo(file).Length - 1);
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        using var first = new ManualResetEventSlim();
        using var second = new ManualResetEventSlim();
        ManualResetEventSlim gate = first;
        try
        {
            Func<SessionStore, StoreContentReport> verify = window.SessionFilesVerifier;
            window.SessionFilesVerifier = store =>
            {
                gate.Wait();
                return verify(store);
            };
            Assert.True(await window.OpenSessionAsync(session));
            Dispatch();

            // Asked for while the files are checked, the package waits and says so; asked again, it is the same ask.
            Click(window, "SharePackageButton");
            Click(window, "SharePackageButton");
            Dispatch();
            Assert.Equal(Waiting, Detail(window));
            Assert.Empty(window.OwnedWindows);

            // The check finds the newest generation changed, and the package is asked about the last complete one, which
            // the window opens again in its place.
            first.Set();
            await Until(() => window.OwnedWindows.Count == 1);
            Assert.Equal(1, Assert.IsType<WorkspaceViewModel>(window.DataContext).DisplayedGeneration);
            Press(window.OwnedWindows[0], "Cancel");
            await Until(() => window.OwnedWindows.Count == 0);
            Assert.Contains("generation 1, the last complete one, is shown", Detail(window), StringComparison.Ordinal);

            // An exact copy waits for the check of the session opened again, as the redacted package did.
            gate = second;
            Assert.True(await window.OpenSessionAsync(session));
            Dispatch();
            Click(window, "ShareOriginalButton");
            Dispatch();
            Assert.Equal(Waiting, Detail(window));
            Assert.Empty(window.OwnedWindows);
            second.Set();
            await Until(() => window.OwnedWindows.Count == 1);
            Press(window.OwnedWindows[0], "Cancel");
            await Until(() => window.OwnedWindows.Count == 0);
        }
        finally
        {
            first.Set();
            second.Set();
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§20.1: a check that finds changed files while a package holds the session shown leaves it as it is, and says nothing untrue of it")]
    public async Task ACheckWhileAPackageHoldsTheSessionLeavesIt()
    {
        using var root = new TemporaryDirectory();
        (string session, string newest) = TwoGenerations(root.Path);
        string file = Path.Combine(session, newest);
        Damage(file, (int)new FileInfo(file).Length - 1);
        string other = DemoInvestigation.Create(Path.Combine(root.Path, "demo")).ClientSession;
        var window = new MainWindow { Width = 1080, Height = 700 };
        using var release = new ManualResetEventSlim();
        using var hold = new WorkHold();
        window.RedactedPackager = (store, destination, now, interval, progress, cancellation) =>
            RedactedSessionPackage.Create(store, destination, now, interval,
                hold.On(progress, update => update.Stage == RedactedPackageStage.Writing, cancellation), cancellation);
        window.Show();
        try
        {
            Func<SessionStore, StoreContentReport> verify = window.SessionFilesVerifier;
            window.SessionFilesVerifier = store =>
            {
                release.Wait();
                return verify(store);
            };
            Assert.True(await window.OpenSessionAsync(session));
            Dispatch();

            // A package made while the files are checked - as Share's wait does not make one - holds the session shown,
            // which the check then leaves as it is, saying nothing of a generation it could not open.
            Task<RedactedSessionPackageResult?> saving = window.WriteRedactedPackageAsync(other, Path.Combine(root.Path, "package"));
            await hold.Reached;
            release.Set();
            await window.SessionFilesChecked.WaitAsync(TimeSpan.FromSeconds(30));
            Dispatch();
            Assert.Equal(2, Assert.IsType<WorkspaceViewModel>(window.DataContext).DisplayedGeneration);
            Assert.DoesNotContain("no earlier generation is complete", Detail(window), StringComparison.Ordinal);
            hold.Release();
            Assert.NotNull(await saving.WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            release.Set();
            hold.Release();
            window.Close();
        }
    }

    private const string Waiting =
        "The session's files are being checked against what was published; the package is asked for once they are.";

    private static void Click(Window window, string name) =>
        window.GetControl<Button>(name).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    private static void Press(Window window, string label)
    {
        Button button = window.GetLogicalDescendants().OfType<Button>()
            .First(candidate => Equals(candidate.Content, label) && candidate.IsEffectivelyVisible);
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatch();
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int wait = 0; wait < 500 && !condition(); wait++)
        {
            Dispatch();
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private static (string Session, string Newest) TwoGenerations(string root)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, "explore-20260926T200000Z-files")).FullName;
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory), Guid.NewGuid(), "files-window-tests");
        _ = Publish(store, Exchange(first: 0, count: 20));
        DerivedGenerationResult second = Publish(store, Exchange(first: 20, count: 20));
        Assert.Equal(2, second.Manifest.Generation);
        return (directory, second.Segments[0].Name);
    }

    /// <summary>A client sending and a server receiving, <paramref name="count"/> times from exchange <paramref name="first"/>.</summary>
    private static ObservationRowV1[] Exchange(int first, int count) =>
    [
        .. Enumerable.Range(first, count).SelectMany(index => new[]
        {
            Transfer(10 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 64, 100, (ulong)(100 + (2 * index)))
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = (10 + (2 * index)) * 100L },
            Transfer(11 + (2 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200,
                (ulong)(101 + (2 * index))).Between("127.0.0.1:8080", "127.0.0.1:50000")
                with { SessionRelativeTicks = (11 + (2 * index)) * 100L },
        }),
    ];

    /// <summary>Flips every bit of one byte of a published file in place, keeping its length.</summary>
    private static void Damage(string path, int offset)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        stream.Position = offset;
        int value = stream.ReadByte();
        stream.Position = offset;
        stream.WriteByte((byte)(value ^ 0xFF));
    }

    private static string Detail(Window window) => window.GetControl<TextBlock>("CaptureDetail").Text ?? string.Empty;

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
