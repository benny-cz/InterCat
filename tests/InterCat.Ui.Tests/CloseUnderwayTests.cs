using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using InterCat.Application;
using InterCat.Capture.Journal;
using InterCat.Capture.Journal.Tests;
using InterCat.Desktop;
using InterCat.Storage;
using Xunit;

namespace InterCat.Ui.Tests;

/// <summary>
/// A window closed while something it would stop is under way - a package being saved, an unfinished capture being
/// finished - asks first. Kept going, the window stays open; stopped, it closes only once the work has ended, so no
/// package's unsaved folder outlives it, and a finish keeps what it derived (§3.1, §8.4, §11.3).
/// </summary>
public sealed class CloseUnderwayTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "InterCat.Ui.Tests.Close", Guid.NewGuid().ToString("N"));

    public CloseUnderwayTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [AvaloniaFact(DisplayName = "§3.1: closing the window while Explore records asks first: kept, it records on; stopped, the window closes once the capture is saved")]
    public async Task ClosingWhileExploreRecordsAsksFirst()
    {
        var window = new MainWindow { Width = 1_280, Height = 800 };
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.CaptureRunner = async (report, stop, _) =>
        {
            // It records until it is stopped, then saves when the test lets it, as the broker finalizes a capture.
            report(new CaptureUiUpdate(CaptureUiPhase.Recording, "Recording · follow latest", "Recording."));
            try
            {
                await Task.Delay(Timeout.Infinite, stop);
            }
            catch (OperationCanceledException)
            {
                stopping.SetResult();
            }

            await saved.Task;
        };
        window.Show();
        try
        {
            window.GetControl<Button>("StartExploringButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.GetControl<Button>("StopCaptureButton").IsVisible);

            // Kept, it records on, and the window stays.
            window.Close();
            Window question = await Question(window);
            Assert.Equal(("Stop Explore?", "Keep recording", "Stop, save, and close"),
                (question.Title, Label(question, 0), Label(question, 1)));
            Assert.Equal(["Explore is recording. Closing stops it: the broker finalizes the capture, and all published "
                + "evidence stays saved."], Said(question));
            Press(question, "Keep recording");
            await Settle();
            Assert.True(window.IsVisible);
            Assert.False(stopping.Task.IsCompleted);

            // Stopped, the window says it is saving, and closes once the capture is saved.
            window.Close();
            Press(await Question(window), "Stop, save, and close");
            await Until(() => stopping.Task.IsCompleted && Status(window) == "Stopping and saving before close");
            await Settle();
            Assert.True(window.IsVisible);
            saved.SetResult();
            await Until(() => !window.IsVisible);
        }
        finally
        {
            saved.TrySetResult();
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§11.3: closing the window while a redacted package is saved asks first: kept, it is saved; stopped, the window closes once the package's unsaved folder is removed")]
    public async Task ClosingWhileARedactedPackageIsSavedAsksFirst()
    {
        (MainWindow window, DemoInvestigationResult demo) = await Opened();
        string target = Directory.CreateDirectory(Path.Combine(folder, "shared")).FullName;
        using var hold = new Hold();
        window.RedactedPackager = (store, destination, now, interval, progress, cancellation) =>
            RedactedSessionPackage.Create(store, destination, now, interval,
                hold.On(progress, update => update.Stage == RedactedPackageStage.Writing, cancellation), cancellation);
        try
        {
            // Asked while the package writes into its unsaved folder, Keep packaging leaves the window open, and the
            // package goes on to be saved.
            string kept = Path.Combine(target, "kept");
            Task<RedactedSessionPackageResult?> saving = window.WriteRedactedPackageAsync(demo.ClientSession, kept);
            await hold.Reached;
            Assert.Contains(".partial-", Path.GetFileName(Assert.Single(Directory.EnumerateDirectories(target))),
                StringComparison.Ordinal);
            window.Close();
            Window question = await Question(window);
            Assert.Equal("Stop packaging?", question.Title);
            Assert.Equal([$"A redacted package of a session is being saved to {kept}. Closing stops it and removes what it "
                + "has written so far; the session itself is unchanged."], Said(question));
            Assert.Equal("Keep packaging", Assert.IsType<Button>(question.FocusManager?.GetFocusedElement()).Content);
            Press(question, "Keep packaging");
            Assert.True(window.IsVisible);
            hold.Release();
            Assert.NotNull(await Ended(saving));
            Assert.Equal([kept], Directory.EnumerateDirectories(target));

            // Stopped, the window waits for the package, held partway, and says so; it closes once the package has
            // removed its unsaved folder, and nothing of it is saved.
            using var second = new Hold();
            window.RedactedPackager = (store, destination, now, interval, progress, cancellation) =>
                RedactedSessionPackage.Create(store, destination, now, interval,
                    second.On(progress, update => update.Stage == RedactedPackageStage.Writing, cancellation), cancellation);
            string stopped = Path.Combine(target, "stopped");
            Task<RedactedSessionPackageResult?> stopping = window.WriteRedactedPackageAsync(demo.ClientSession, stopped);
            await second.Reached;
            string[] atClose = [];
            window.Closed += (_, _) => atClose = [.. Directory.EnumerateDirectories(target)];
            window.Close();
            Press(await Question(window), "Stop and close");
            await Until(() => Status(window) == "Stopping before close");
            Assert.True(window.IsVisible);
            second.Release();
            await Until(() => !window.IsVisible);
            Assert.Null(await Ended(stopping));
            Assert.Equal([kept], atClose);
            Assert.Equal([kept], Directory.EnumerateDirectories(target));
        }
        finally
        {
            hold.Release();
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§11.3: closing the window while an exact copy is saved asks first; Escape keeps copying, and stopped, the window closes once the copy's unsaved folder is removed")]
    public async Task ClosingWhileAnExactCopyIsSavedAsksFirst()
    {
        (MainWindow window, DemoInvestigationResult demo) = await Opened();
        string target = Directory.CreateDirectory(Path.Combine(folder, "original")).FullName;
        using var hold = new Hold();
        window.OriginalPackager = (store, destination, progress, cancellation) => OriginalEvidencePackage.Create(
            store, destination, hold.On(progress, update => update.Stage == OriginalPackageStage.Copying, cancellation),
            cancellation);
        try
        {
            string copy = Path.Combine(target, "copy");
            Guid session = SessionStore.OpenExisting(LocalOwnedDirectory.Open(demo.ClientSession)).Current!.SessionId;
            Task<OriginalEvidencePackageResult?> copying = window.WriteOriginalPackageAsync(demo.ClientSession, session, copy);
            await hold.Reached;

            // Escape is the question's safe answer: the copy goes on, and the window stays.
            window.Close();
            Window question = await Question(window);
            Assert.Equal(("Stop packaging?", "Keep packaging", "Stop and close"),
                (question.Title, Label(question, 0), Label(question, 1)));
            Assert.Equal([$"An exact copy of a session is being saved to {copy}. Closing stops it and removes what it has "
                + "written so far; the session itself is unchanged."], Said(question));
            question.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            await Until(() => window.OwnedWindows.Count == 0);
            Assert.True(window.IsVisible);

            string[] atClose = [];
            window.Closed += (_, _) => atClose = [.. Directory.EnumerateDirectories(target)];
            window.Close();
            Press(await Question(window), "Stop and close");
            await Until(() => Status(window) == "Stopping before close");
            Assert.True(window.IsVisible);
            hold.Release();
            await Until(() => !window.IsVisible);
            Assert.Null(await Ended(copying));
            Assert.Empty(atClose);
            Assert.Empty(Directory.EnumerateDirectories(target));
        }
        finally
        {
            hold.Release();
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§3.1: closing the window while an unfinished capture is finished asks first; stopped, it closes once the finish has stopped, and the next launch offers the capture again")]
    public async Task ClosingWhileACaptureIsFinishedAsksFirst()
    {
        using var root = new TemporaryDirectory();
        (string sessions, string session, _) = await InterruptedCaptureWindowTests.CrashedFollow(root.Path, unfinalized: false);
        var window = new MainWindow { Width = 1_080, Height = 700 };
        using var hold = new Hold();
        window.CaptureFinisher = (ticket, progress, cancellation) =>
        {
            // Held before it derives the rest, as a long capture's finish is partway.
            hold.Here(cancellation);
            return InterruptedFollow.Finish(ticket, progress, cancellationToken: cancellation);
        };
        window.Show();
        try
        {
            await window.UseSessionRootAsync(sessions);
            Dispatch();
            Task finishing = window.ActOnInterruptedCaptureAsync();
            await hold.Reached;

            // Kept going, the finish goes on and the window stays.
            window.Close();
            Window question = await Question(window);
            Assert.Equal(("Stop finishing?", "Keep finishing"), (question.Title, Label(question, 0)));
            Assert.Equal([$"The unfinished capture is being finished into {session}. Closing stops it: what it has derived "
                + "stays in the session, and InterCat offers to finish the rest when it next opens."], Said(question));
            Press(question, "Keep finishing");
            Assert.True(window.IsVisible);

            // Stopped, the window waits for the finish to stop, and the next launch offers to finish the rest.
            window.Close();
            Press(await Question(window), "Stop and close");
            await Until(() => Status(window) == "Stopping before close");
            Assert.True(window.IsVisible);
            hold.Release();
            await Until(() => !window.IsVisible);
            await Ended(finishing);
            Assert.True(File.Exists(LiveFollowTicket.PathFor(session)));

            var next = new MainWindow { Width = 1_080, Height = 700 };
            next.Show();
            await next.UseSessionRootAsync(sessions);
            Dispatch();
            Assert.True(next.GetControl<Border>("UnfinishedCaptureCard").IsVisible);
            Assert.EndsWith("is not finished saving", next.GetControl<TextBlock>("UnfinishedCaptureHeadline").Text,
                StringComparison.Ordinal);
            Assert.Equal("Finish saving", next.GetControl<Button>("FinishCaptureButton").Content);
            next.Close();
        }
        finally
        {
            hold.Release();
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§8.4: closing an investigation's window while its package is saved asks first; stopped, it closes once the package's unsaved folder is removed, and InterCat stays open")]
    public async Task ClosingAnInvestigationWhileItIsPackagedAsksFirst()
    {
        (MainWindow main, DemoInvestigationResult demo) = await Opened();
        string target = Directory.CreateDirectory(Path.Combine(folder, "packages")).FullName;
        InvestigationWindow window = main.ShowInvestigation(demo.WorkspacePath);
        using var hold = new Hold();
        window.Packager = (workspace, destination, only, progress, cancellation) => InvestigationPackage.Create(
            workspace, destination, only, hold.On(progress, _ => true, cancellation), cancellation);
        try
        {
            await Until(() => window.View is not null);
            string package = Path.Combine(target, "case-package");
            Task<InvestigationPackageResult?> packaging = window.WritePackageAsync(package, null);
            await hold.Reached;
            window.Close();
            Window question = await Question(window);
            Assert.Equal(("Stop packaging?", "Keep packaging"), (question.Title, Label(question, 0)));
            Assert.Equal([$"{Path.GetFileName(demo.WorkspacePath)} is being packaged to {package}. Closing stops it and "
                + "removes what it has written so far; the investigation and its sessions are unchanged."], Said(question));

            string[] atClose = [];
            window.Closed += (_, _) => atClose = [.. Directory.EnumerateDirectories(target)];
            Press(question, "Stop and close");
            await Until(() => StatusOf(window) == "Stopping the package before the window closes…");
            Assert.True(window.IsVisible);
            hold.Release();
            await Until(() => !window.IsVisible);
            Assert.Null(await Ended(packaging));
            Assert.Empty(atClose);
            Assert.Empty(Directory.EnumerateDirectories(target));
            Assert.True(main.IsVisible);
            Assert.Empty(main.Investigations);
        }
        finally
        {
            hold.Release();
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§8.4: closing InterCat while it and an investigation's window each save a package asks once about both, and closes both once neither package's unsaved folder is left")]
    public async Task ClosingInterCatStopsEveryPackageUnderway()
    {
        (MainWindow main, DemoInvestigationResult demo) = await Opened();
        string target = Directory.CreateDirectory(Path.Combine(folder, "both")).FullName;
        InvestigationWindow investigation = main.ShowInvestigation(demo.WorkspacePath);
        using var redacted = new Hold();
        using var packaged = new Hold();
        main.RedactedPackager = (store, destination, now, interval, progress, cancellation) =>
            RedactedSessionPackage.Create(store, destination, now, interval,
                redacted.On(progress, update => update.Stage == RedactedPackageStage.Writing, cancellation), cancellation);
        investigation.Packager = (workspace, destination, only, progress, cancellation) => InvestigationPackage.Create(
            workspace, destination, only, packaged.On(progress, _ => true, cancellation), cancellation);
        try
        {
            await Until(() => investigation.View is not null);
            string session = Path.Combine(target, "session");
            string workspace = Path.Combine(target, "case-package");
            Task<RedactedSessionPackageResult?> saving = main.WriteRedactedPackageAsync(demo.ClientSession, session);
            Task<InvestigationPackageResult?> packaging = investigation.WritePackageAsync(workspace, null);
            await redacted.Reached;
            await packaged.Reached;

            main.Close();
            Window question = await Question(main);
            Assert.Equal(("Stop and close?", "Keep InterCat open", "Stop all and close"),
                (question.Title, Label(question, 0), Label(question, 1)));
            Assert.Equal(
                [
                    $"A redacted package of a session is being saved to {session}. Closing stops it and removes what it has "
                        + "written so far; the session itself is unchanged.",
                    $"{Path.GetFileName(demo.WorkspacePath)} is being packaged to {workspace}. Closing stops it and removes "
                        + "what it has written so far; the investigation and its sessions are unchanged.",
                ],
                Said(question));

            string[] atClose = [];
            main.Closed += (_, _) => atClose = [.. Directory.EnumerateDirectories(target)];
            Press(question, "Stop all and close");
            await Until(() => Status(main) == "Stopping before close");
            redacted.Release();
            Assert.Null(await Ended(saving));
            await Settle();
            Assert.True(main.IsVisible);
            packaged.Release();
            await Until(() => !main.IsVisible && !investigation.IsVisible);
            Assert.Null(await Ended(packaging));
            Assert.Empty(atClose);
            Assert.Empty(Directory.EnumerateDirectories(target));
        }
        finally
        {
            redacted.Release();
            packaged.Release();
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§8.4: a package begun while InterCat stops to close is stopped too, and InterCat closes only once it has ended")]
    public async Task APackageBegunWhileClosingIsStoppedToo()
    {
        (MainWindow main, DemoInvestigationResult demo) = await Opened();
        string target = Directory.CreateDirectory(Path.Combine(folder, "meanwhile")).FullName;
        InvestigationWindow investigation = main.ShowInvestigation(demo.WorkspacePath);
        using var redacted = new Hold();
        using var packaged = new Hold();
        main.RedactedPackager = (store, destination, now, interval, progress, cancellation) =>
            RedactedSessionPackage.Create(store, destination, now, interval,
                redacted.On(progress, update => update.Stage == RedactedPackageStage.Writing, cancellation), cancellation);
        investigation.Packager = (workspace, destination, only, progress, cancellation) => InvestigationPackage.Create(
            workspace, destination, only, packaged.On(progress, _ => true, cancellation), cancellation);
        try
        {
            await Until(() => investigation.View is not null);
            Task<RedactedSessionPackageResult?> saving =
                main.WriteRedactedPackageAsync(demo.ClientSession, Path.Combine(target, "session"));
            await redacted.Reached;
            main.Close();
            Press(await Question(main), "Stop and close");
            await Until(() => Status(main) == "Stopping before close");

            // While InterCat waits for its package, the investigation's window begins one of its own: once the first
            // has stopped, InterCat stops that one too, and waits for it.
            Task<InvestigationPackageResult?> packaging =
                investigation.WritePackageAsync(Path.Combine(target, "case-package"), null);
            await packaged.Reached;
            redacted.Release();
            Assert.Null(await Ended(saving));
            await Settle();
            Assert.True(main.IsVisible);
            Assert.True(investigation.IsVisible);
            packaged.Release();
            await Until(() => !main.IsVisible && !investigation.IsVisible);
            Assert.Null(await Ended(packaging));
            Assert.Empty(Directory.EnumerateDirectories(target));
        }
        finally
        {
            redacted.Release();
            packaged.Release();
            main.Close();
        }
    }

    /// <summary>A window showing the demo's client session.</summary>
    private async Task<(MainWindow Window, DemoInvestigationResult Demo)> Opened()
    {
        DemoInvestigationResult demo = DemoInvestigation.Create(Path.Combine(folder, "demo"));
        var window = new MainWindow { Width = 1_280, Height = 800 };
        window.Show();
        Assert.True(await window.OpenSessionAsync(demo.ClientSession));
        Dispatch();
        return (window, demo);
    }

    /// <summary>The question a window asks over itself before it closes, once it is shown.</summary>
    private static async Task<Window> Question(Window owner)
    {
        await Until(() => owner.OwnedWindows.Count == 1 && owner.OwnedWindows[0].IsVisible);
        return owner.OwnedWindows[0];
    }

    /// <summary>What the question says, paragraph by paragraph, above its answers.</summary>
    private static List<string?> Said(Window question) =>
        [.. ((StackPanel)question.Content!).Children.OfType<TextBlock>().Select(paragraph => paragraph.Text)];

    /// <summary>The words of the question's answer at <paramref name="index"/>, the safe one first.</summary>
    private static object? Label(Window question, int index) =>
        question.GetLogicalDescendants().OfType<Button>().ElementAt(index).Content;

    private static void Press(Window window, string label)
    {
        Button button = window.GetLogicalDescendants().OfType<Button>()
            .First(candidate => Equals(candidate.Content, label) && candidate.IsEffectivelyVisible);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatch();
    }

    private static string? Status(MainWindow window) => window.GetControl<TextBlock>("CaptureStatus").Text;

    private static string? StatusOf(InvestigationWindow window) =>
        window.GetVisualDescendants().OfType<TextBlock>()
            .Single(block => Avalonia.Automation.AutomationProperties.GetName(block) == "Investigation status").Text;

    private static async Task Until(Func<bool> condition)
    {
        for (int wait = 0; wait < 500 && !condition(); wait++)
        {
            Dispatch();
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private static void Dispatch() => Dispatcher.UIThread.RunJobs();

    /// <summary>A window's work once it has ended; one that never ends fails the test rather than hang it.</summary>
    private static Task<T> Ended<T>(Task<T> work) => work.WaitAsync(TimeSpan.FromSeconds(30));

    private static Task Ended(Task work) => work.WaitAsync(TimeSpan.FromSeconds(30));

    /// <summary>Lets what a worker ended with reach the windows: a moment's turns of the dispatcher.</summary>
    private static async Task Settle()
    {
        for (int turn = 0; turn < 20; turn++)
        {
            await Task.Delay(10);
            Dispatch();
        }
    }

    /// <summary>
    /// Holds a worker partway, at the first step it reports from its own thread that a test picks: a package writing into
    /// its unsaved folder, a finish between its steps. Let go, the worker goes on, and stops there if it was cancelled
    /// meanwhile, as a worker that looks between its steps does.
    /// </summary>
    private sealed class Hold : IDisposable
    {
        private readonly ManualResetEventSlim released = new();
        private readonly TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int held;

        /// <summary>Done once the worker is held; a worker that never comes fails the test rather than hang it.</summary>
        public Task Reached => reached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        /// <summary>Holds the worker at the first step it reports that <paramref name="at"/> picks.</summary>
        public IProgress<T> On<T>(IProgress<T> window, Func<T, bool> at, CancellationToken cancellation) =>
            new Step<T>(update =>
            {
                if (at(update)) Here(cancellation);
                window.Report(update);
            });

        /// <summary>Holds the worker here, the first time it comes here, until it is let go.</summary>
        public void Here(CancellationToken cancellation)
        {
            if (Interlocked.Exchange(ref held, 1) != 0) return;
            reached.SetResult();

            // Held whether or not it is cancelled meanwhile: a worker partway through a step finishes the step first.
            released.Wait(CancellationToken.None);
            cancellation.ThrowIfCancellationRequested();
        }

        public void Release() => released.Set();

        public void Dispose()
        {
            released.Set();
            released.Dispose();
        }

        private sealed class Step<T>(Action<T> report) : IProgress<T>
        {
            public void Report(T value) => report(value);
        }
    }
}
