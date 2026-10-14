using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using InterCat.Application;
using InterCat.Capture.Journal;
using InterCat.Capture.Journal.Tests;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using Xunit;

namespace InterCat.Ui.Tests;

/// <summary>
/// While something holds the session InterCat's window shows - a package of it being saved, a capture under way, an
/// unfinished capture being finished - nothing replaces it, and whatever asked says why: Open saved session and Start
/// exploring, their keys, and an investigation's Open, which says it in its own status line (§3.1, §8.4).
/// </summary>
public sealed class HeldSessionTests : IDisposable
{
    private const string PackageOpens = "A package of the session shown is being saved; open another session once it is "
        + "saved, or cancel it with Cancel packaging.";

    private readonly string folder = Path.Combine(Path.GetTempPath(), "InterCat.Ui.Tests.Held", Guid.NewGuid().ToString("N"));

    public HeldSessionTests() => Directory.CreateDirectory(folder);

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

    [AvaloniaFact(DisplayName = "§3.1: while a package of the session shown is saved, Open saved session, Start exploring and their keys open and start nothing, and say why; once it is saved, they do")]
    public async Task APackageHoldsTheSessionShown()
    {
        DemoInvestigationResult demo = DemoInvestigation.Create(Path.Combine(folder, "demo"));
        var window = new MainWindow { Width = 1_280, Height = 800 };
        var choices = new AnsweredChoices(window);
        window.Choices = choices;
        int started = 0;
        window.CaptureRunner = (_, _, _) =>
        {
            Interlocked.Increment(ref started);
            return Task.CompletedTask;
        };
        using var hold = new WorkHold();
        window.RedactedPackager = (store, destination, now, interval, progress, cancellation) =>
            RedactedSessionPackage.Create(store, destination, now, interval,
                hold.On(progress, update => update.Stage == RedactedPackageStage.Writing, cancellation), cancellation);
        window.Show();
        try
        {
            Assert.True(await window.OpenSessionAsync(demo.ClientSession));
            Dispatch();
            Task<RedactedSessionPackageResult?> saving =
                window.WriteRedactedPackageAsync(demo.ClientSession, Path.Combine(folder, "package"));
            await hold.Reached;
            string starts = "A package of the session shown is being saved; start exploring once it is saved, or cancel "
                + "it with Cancel packaging.";

            // Both stay offered while a package is saved, saying what they do; each asks for nothing and starts nothing,
            // and the card says why.
            Assert.Equal("Start the default Explore capture", Offers.Offered(window.GetControl<Button>("StartExploringButton")));
            Assert.Equal(OpenDoes, OpenOffered(window));
            Assert.Equal(PackageOpens, SaidAfter(window, () => Click(window, "OpenSavedSessionButton")));
            Assert.Equal(PackageOpens, SaidAfter(window, () => window.KeyPressQwerty(PhysicalKey.O, RawInputModifiers.Control)));
            Assert.Equal(starts, SaidAfter(window, () => Click(window, "StartExploringButton")));
            Assert.Equal(starts, SaidAfter(window, () => window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.Control)));
            await Settle();
            Assert.Equal((0, 0), (choices.Asked, started));
            Assert.Equal(demo.ClientSession, window.GetControl<TextBlock>("CaptureSessionPath").Text);

            // Once it is saved, they do.
            hold.Release();
            Assert.NotNull(await saving.WaitAsync(TimeSpan.FromSeconds(30)));
            choices.Answer(null);
            Click(window, "OpenSavedSessionButton");
            await Until(() => choices.Asked == 1);
            Click(window, "StartExploringButton");
            await Until(() => started == 1);
        }
        finally
        {
            hold.Release();
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§3.1: while a session is being opened, another open, Ctrl+O and Ctrl+R wait, and each says why")]
    public async Task AnOpenHoldsTheSessionShown()
    {
        DemoInvestigationResult demo = DemoInvestigation.Create(Path.Combine(folder, "demo"));
        var window = new MainWindow { Width = 1_280, Height = 800 };
        var choices = new AnsweredChoices(window);
        window.Choices = choices;
        int started = 0;
        window.CaptureRunner = (_, _, _) =>
        {
            Interlocked.Increment(ref started);
            return Task.CompletedTask;
        };
        using var hold = new WorkHold();
        window.SessionOpener = (path, expected) =>
        {
            // Held while it reads, as a large session's open is.
            hold.Here(CancellationToken.None);
            return SharedSessionStores.Open(path, expected);
        };
        window.Show();
        try
        {
            Task<bool> opening = window.OpenSessionAsync(demo.ClientSession);
            await hold.Reached;
            string opens = "A session is being opened; open another session once it is open.";

            // Each waits, saying why on itself, in the words its key says.
            Assert.Equal("A session is being opened; start exploring once it is open.",
                Offers.WhyNot(window.GetControl<Button>("StartExploringButton")));
            Assert.Equal(opens, Offers.WhyNot(window.GetControl<Button>("OpenSavedSessionButton")));
            Assert.Equal(opens, SaidAfter(window, () => window.KeyPressQwerty(PhysicalKey.O, RawInputModifiers.Control)));
            Assert.Equal("A session is being opened; start exploring once it is open.",
                SaidAfter(window, () => window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.Control)));

            // A second open is not begun beside the first, and says why.
            window.GetControl<TextBlock>("CaptureDetail").Text = string.Empty;
            Assert.False(await window.OpenSessionAsync(demo.ServerSession).WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal(opens, window.GetControl<TextBlock>("CaptureDetail").Text);

            hold.Release();
            Assert.True(await opening.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal(demo.ClientSession, window.GetControl<TextBlock>("CaptureSessionPath").Text);
            await Settle();
            Assert.Equal((0, 0), (choices.Asked, started));

            // Opened, both are offered again, saying what they do.
            Assert.Equal("Start the default Explore capture", Offers.Offered(window.GetControl<Button>("StartExploringButton")));
            Assert.Equal(OpenDoes, OpenOffered(window));
        }
        finally
        {
            hold.Release();
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§3.1: while a capture is under way or being saved, Ctrl+O opens nothing and Ctrl+R starts nothing more, and each says why")]
    public async Task ACaptureHoldsTheSessionShown()
    {
        var window = new MainWindow { Width = 1_280, Height = 800 };
        var choices = new AnsweredChoices(window);
        window.Choices = choices;
        int started = 0;
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.CaptureRunner = async (report, stop, _) =>
        {
            Interlocked.Increment(ref started);
            report(new CaptureUiUpdate(CaptureUiPhase.Recording, "Recording · follow latest", "Recording."));
            try
            {
                await Task.Delay(Timeout.Infinite, stop);
            }
            catch (OperationCanceledException)
            {
                // Stopped, it is saved when the test lets it, as the broker finalizes a capture.
                report(new CaptureUiUpdate(CaptureUiPhase.Finishing, "Stopping and saving", "Finalizing."));
                await saved.Task;
            }
        };
        window.Show();
        try
        {
            Click(window, "StartExploringButton");
            await Until(() => window.GetControl<Button>("StopCaptureButton").IsVisible);
            Assert.Equal("A capture is under way; open another session once it has stopped.",
                SaidAfter(window, () => window.KeyPressQwerty(PhysicalKey.O, RawInputModifiers.Control)));
            Assert.Equal("A capture is already under way; Stop ends it.",
                SaidAfter(window, () => window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.Control)));

            // Stopped and being saved, it still holds the session shown, and they say so.
            Click(window, "StopCaptureButton");
            await Until(() => window.GetControl<TextBlock>("CaptureStatus").Text == "Stopping and saving");
            Assert.Equal("The capture is being saved; open another session once it is.",
                SaidAfter(window, () => window.KeyPressQwerty(PhysicalKey.O, RawInputModifiers.Control)));
            Assert.Equal("The capture is being saved; start exploring once it is.",
                SaidAfter(window, () => window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.Control)));
            await Settle();
            Assert.Equal((0, 1), (choices.Asked, started));
        }
        finally
        {
            saved.TrySetResult();
            await Settle();
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§3.1: while an unfinished capture is finished, Ctrl+O and Ctrl+R open and start nothing, and each says why")]
    public async Task AFinishHoldsTheSessionShown()
    {
        using var root = new TemporaryDirectory();
        (string sessions, _, _) = await InterruptedCaptureWindowTests.CrashedFollow(root.Path, unfinalized: false);
        var window = new MainWindow { Width = 1_080, Height = 700 };
        var choices = new AnsweredChoices(window);
        window.Choices = choices;
        int started = 0;
        window.CaptureRunner = (_, _, _) =>
        {
            Interlocked.Increment(ref started);
            return Task.CompletedTask;
        };
        using var hold = new WorkHold();
        window.CaptureFinisher = (ticket, progress, cancellation) =>
        {
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
            Assert.Equal("An unfinished capture is being finished; open another session once it has finished, or stop it "
                + "with Stop finishing.", SaidAfter(window, () => window.KeyPressQwerty(PhysicalKey.O, RawInputModifiers.Control)));
            Assert.Equal("An unfinished capture is being finished; start exploring once it has finished, or stop it with "
                + "Stop finishing.", SaidAfter(window, () => window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.Control)));
            await Settle();
            Assert.Equal((0, 0), (choices.Asked, started));

            // Start, Open and Forget each say on themselves why they wait; Stop finishing is offered.
            Assert.Equal("An unfinished capture is being finished; start exploring once it has finished, or stop it with "
                + "Stop finishing.", Offers.WhyNot(window.GetControl<Button>("StartExploringButton")));
            Assert.Equal("An unfinished capture is being finished; open another session once it has finished, or stop it "
                + "with Stop finishing.", Offers.WhyNot(window.GetControl<Button>("OpenSavedSessionButton")));
            Assert.Equal("The session is being finished: stop finishing first to forget this capture.",
                Offers.WhyNot(window.GetControl<Button>("ForgetCaptureButton")));
            Button finish = window.GetControl<Button>("FinishCaptureButton");
            Assert.Equal("Stop finishing", finish.Content);
            Assert.Null(Offers.Offered(finish));

            // Asked to stop, it waits until the finish has stopped, saying so, and is offered again after.
            List<(bool, string?)> stopping = Offers.Heard(finish);
            Task stop = window.ActOnInterruptedCaptureAsync();
            Assert.Equal("Finishing is being stopped; the session keeps what was derived.", Offers.WhyNot(finish));
            hold.Release();
            await stop.WaitAsync(TimeSpan.FromSeconds(30));
            await finishing.WaitAsync(TimeSpan.FromSeconds(30));
            Dispatch();
            Assert.Equal([(false, "Finishing is being stopped; the session keeps what was derived."), (true, null)], stopping);
            Assert.Equal(("Forget", InterruptedCaptureOffer.ForgetExplanation), (window.GetControl<Button>("ForgetCaptureButton").Content,
                ToolTip.GetTip(window.GetControl<Button>("ForgetCaptureButton"))));
        }
        finally
        {
            hold.Release();
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: while a session is opened, an unfinished capture's card waits, saying so on its action")]
    public async Task AnOpenHoldsTheUnfinishedCapturesCard()
    {
        using var root = new TemporaryDirectory();
        (string sessions, _, _) = await InterruptedCaptureWindowTests.CrashedFollow(root.Path, unfinalized: false);
        DemoInvestigationResult demo = DemoInvestigation.Create(Path.Combine(folder, "demo"));
        var window = new MainWindow { Width = 1_080, Height = 700 };
        using var hold = new WorkHold();
        window.Show();
        try
        {
            await window.UseSessionRootAsync(sessions);
            Dispatch();
            Button finish = window.GetControl<Button>("FinishCaptureButton");
            Assert.True(finish.IsVisible);
            Assert.Null(Offers.Offered(finish));
            window.SessionOpener = (path, expected) =>
            {
                hold.Here(CancellationToken.None);
                return SharedSessionStores.Open(path, expected);
            };
            Task<bool> opening = window.OpenSessionAsync(demo.ClientSession);
            await hold.Reached;
            Assert.Equal("A session is being opened; this capture waits until it is open.", Offers.WhyNot(finish));
            hold.Release();
            Assert.True(await opening.WaitAsync(TimeSpan.FromSeconds(30)));
            Dispatch();
            Assert.Null(Offers.Offered(finish));
        }
        finally
        {
            hold.Release();
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§8.4: an investigation's Open says why InterCat's window did not open its session, in the words that window says it")]
    public async Task AnInvestigationSaysWhyItsSessionWasNotOpened()
    {
        DemoInvestigationResult demo = DemoInvestigation.Create(Path.Combine(folder, "demo"));
        var main = new MainWindow { Width = 1_280, Height = 800 };
        using var hold = new WorkHold();
        main.RedactedPackager = (store, destination, now, interval, progress, cancellation) =>
            RedactedSessionPackage.Create(store, destination, now, interval,
                hold.On(progress, update => update.Stage == RedactedPackageStage.Writing, cancellation), cancellation);
        main.Show();
        try
        {
            Assert.True(await main.OpenSessionAsync(demo.ClientSession));
            InvestigationWindow window = main.ShowInvestigation(demo.WorkspacePath);
            await Until(() => window.View is { Members.Count: 2 });
            Task<RedactedSessionPackageResult?> saving =
                main.WriteRedactedPackageAsync(demo.ClientSession, Path.Combine(folder, "package"));
            await hold.Reached;

            // The server's session is not opened over the package's: the investigation says why, as InterCat's card does.
            ListBox members = Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one");
            members.SelectedIndex = window.View!.Members.ToList().FindIndex(row => row.FullPath == demo.ServerSession);
            Named<Button>(window, "Open in InterCat").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            string server = window.View.Members.Single(row => row.FullPath == demo.ServerSession).Title.Split(',')[0];
            await Until(() => StatusOf(window) == $"{server} was not opened: {PackageOpens}");
            Assert.Equal(PackageOpens, main.GetControl<TextBlock>("CaptureDetail").Text);
            Assert.Equal(demo.ClientSession, main.GetControl<TextBlock>("CaptureSessionPath").Text);

            // Nor is a column of its records, which says the same.
            window.ShowTab(2);
            await Until(() => window.Timeline is not null);
            Guid serverId = window.View.Members.Single(row => row.FullPath == demo.ServerSession).SessionId;
            int lane = window.Timeline!.Lanes.ToList().FindIndex(each => each.SessionId == serverId);
            int column = window.Timeline.Lanes[lane].Buckets.ToList().FindIndex(bucket => bucket.ObservationCount > 0);
            Assert.False(await window.OpenColumnAsync(new TimelineColumn(lane, column)));
            Assert.Equal($"{server} was not opened: {PackageOpens}", StatusOf(window));
            window.ShowTab(0);

            // Once the package is saved, it opens.
            hold.Release();
            Assert.NotNull(await saving.WaitAsync(TimeSpan.FromSeconds(30)));
            Named<Button>(window, "Open in InterCat").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => main.GetControl<TextBlock>("CaptureSessionPath").Text == demo.ServerSession);
            await Until(() => StatusOf(window) == $"Opened {server} in the InterCat window.");
        }
        finally
        {
            hold.Release();
            main.Close();
        }
    }

    /// <summary>What the card says once <paramref name="act"/> is done, from a card cleared before it.</summary>
    private static string? SaidAfter(MainWindow window, Action act)
    {
        TextBlock detail = window.GetControl<TextBlock>("CaptureDetail");
        detail.Text = string.Empty;
        act();
        Dispatch();
        return detail.Text;
    }

    private static void Click(Window window, string name) =>
        window.GetControl<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private const string OpenDoes = "Open an existing InterCat session";

    /// <summary>What Open saved session, offered, says it does; its hover says more - its key and the folder it takes dropped.</summary>
    private static string? OpenOffered(Window window)
    {
        Button open = window.GetControl<Button>("OpenSavedSessionButton");
        Assert.True(open.IsEnabled);
        Assert.Equal("Open an existing InterCat session (Ctrl+O), or drop its folder on this window", ToolTip.GetTip(open));
        return AutomationProperties.GetHelpText(open);
    }

    private static T Named<T>(Window window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<T>()
            .Single(control => AutomationProperties.GetName(control) == name
                || ControlAutomationPeer.CreatePeerForElement(control).GetName() == name);

    private static string? StatusOf(InvestigationWindow window) =>
        window.GetVisualDescendants().OfType<TextBlock>()
            .Single(block => AutomationProperties.GetName(block) == "Investigation status").Text;

    private static async Task Until(Func<bool> condition)
    {
        for (int wait = 0; wait < 500 && !condition(); wait++)
        {
            Dispatch();
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    /// <summary>Lets what a worker ended with reach the windows: a moment's turns of the dispatcher.</summary>
    private static async Task Settle()
    {
        for (int turn = 0; turn < 20; turn++)
        {
            await Task.Delay(10);
            Dispatch();
        }
    }

    private static void Dispatch() => Dispatcher.UIThread.RunJobs();
}
