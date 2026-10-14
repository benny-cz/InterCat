using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using InterCat.Application;
using InterCat.CaptureBroker;
using InterCat.Desktop;
using InterCat.Domain;
using Xunit;

namespace InterCat.Ui.Tests;

/// <summary>
/// The window's content capture (§11.1, ADR-049): asked for by the person's own processes, its limits and the consent to
/// see what is kept, then reviewed as the broker prepared it, before anything is recorded.
/// </summary>
public sealed class ContentCaptureWindowTests
{
    private static readonly DateTimeOffset Started = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);

    [AvaloniaFact(DisplayName = "§11.1: a content capture is asked for by its processes, limits and consent, each named, before the broker is asked anything")]
    public void AContentCaptureIsAskedForBeforeTheBroker()
    {
        SeenProcess[] running =
        [
            new(4_242, "client", Started),
            new(5_150, "client", Started.AddSeconds(1)),
            new(6_001, "worker", Started.AddSeconds(2)),
        ];
        var refreshed = new List<SeenProcess>(running);
        var chooser = new ContentCaptureWindow(running, () => refreshed);
        chooser.Show();
        try
        {
            Dispatch();

            // It says what is kept and whose before anything else, and starts on Cancel, so a reflexive Enter asks nothing.
            Assert.True(Named<Button>(chooser, "Cancel: nothing is asked of the broker").IsFocused);
            Assert.Contains(chooser.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == ContentCaptureWindow.Intro);
            Assert.Equal(3, chooser.Listed.Count);
            Assert.All(chooser.Listed, name => Assert.Matches(@"^\w+, process \d+, started \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}$", name));
            Assert.False(chooser.CanProceed);
            Assert.Null(chooser.Chosen);
            Assert.Equal("Choose the processes whose messages to keep.", chooser.ChosenStatement);

            // Its review, not offered, says why on itself in the line's words; offered, it says what it asks of the broker.
            Button review = Named<Button>(chooser, "Review with the broker…");
            Assert.Equal("Choose the processes whose messages to keep.", Offers.WhyNot(review));

            // A filter narrows the list by name or ID; one chosen stays listed whatever the filter.
            chooser.Filter("work");
            Assert.Equal(["worker, process 6001"], chooser.Listed.Select(Head));
            chooser.Filter("51");
            Assert.Equal(["client, process 5150"], chooser.Listed.Select(Head));
            chooser.Choose(5_150, chosen: true);
            chooser.Filter("nothing like it");
            Assert.Equal(["client, process 5150"], chooser.Listed.Select(Head));
            chooser.Filter(string.Empty);

            // Chosen, it asks for every exchange of the process within the default limits, its content never shown.
            Assert.True(chooser.CanProceed);
            Assert.Equal("Ask the broker to prepare the capture, then review what it would keep before anything is recorded",
                Offers.Offered(review));
            Assert.Equal("1 process chosen. The capture keeps up to 16 MiB in all, the first 64 KiB of each message, for up to "
                + "10 minutes.", chooser.ChosenStatement);
            ContentCaptureChoice choice = chooser.Chosen!;
            Assert.Equal([running[1]], choice.Processes);
            Assert.Equal((64 * 1024, 16L * 1024 * 1024, ContentInspectionMode.Disabled, 600),
                (choice.MaximumRecordBytes, choice.MaximumSessionBytes, choice.Inspection, choice.MaximumDurationSeconds));

            // Other limits, a longer length and the consent to see what is kept are the person's to choose.
            chooser.Choose(4_242, chosen: true);
            chooser.Limit(record: 2, session: 2, length: 2);
            chooser.Consent(shown: true);
            choice = chooser.Chosen!;
            Assert.Equal([4_242, 5_150], choice.Processes.Select(process => process.ProcessId));
            Assert.Equal((1024 * 1024, 1024L * 1024 * 1024, ContentInspectionMode.HexAndText, 3_600),
                (choice.MaximumRecordBytes, choice.MaximumSessionBytes, choice.Inspection, choice.MaximumDurationSeconds));
            Assert.Equal("2 processes chosen. The capture keeps up to 1 GiB in all, the first 1 MiB of each message, for up to "
                + "1 hour.", chooser.ChosenStatement);
            Save(chooser, "content-capture-chooser.png");

            // A refreshed list keeps what was chosen of the processes still running; one gone is chosen no more.
            refreshed.RemoveAt(0);
            refreshed.Add(new(7_777, "newcomer", Started.AddMinutes(1)));
            Named<Button>(chooser, "Refresh the list").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatch();
            Assert.Equal(["client, process 5150", "worker, process 6001", "newcomer, process 7777"], chooser.Listed.Select(Head));
            Assert.Equal([5_150], chooser.Chosen!.Processes.Select(process => process.ProcessId));

            // Every element a screen reader lands on is named, and its line says what the choice comes to.
            List<string> unheard = AccessibilityAuditTests.Unheard(chooser, out _);
            Assert.True(unheard.Count == 0, string.Join(Environment.NewLine, unheard));
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(Named<TextBlock>(chooser, "Processes chosen")));
        }
        finally
        {
            chooser.Close();
        }

        // More processes than a request names cannot be asked for, and the line says why.
        SeenProcess[] many = [.. Enumerable.Range(1, ContentCaptureWindow.MaximumProcesses + 1).Select(id => new SeenProcess(id, "many", Started))];
        var crowded = new ContentCaptureWindow(many);
        crowded.Show();
        try
        {
            foreach (SeenProcess process in many)
            {
                crowded.Choose(process.ProcessId, chosen: true);
            }

            Assert.False(crowded.CanProceed);
            Assert.Equal("65 processes are chosen; a content capture names at most 64.", crowded.ChosenStatement);
            Assert.Equal(crowded.ChosenStatement, Offers.WhyNot(Named<Button>(crowded, "Review with the broker…")));
            Assert.False(Named<Button>(crowded, "Refresh the list").IsVisible);
        }
        finally
        {
            crowded.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§11.1: a content capture's review states what the broker would keep of whose messages, and starts nothing on Cancel")]
    public void TheReviewStatesWhatTheBrokerWouldKeep()
    {
        BrokerEffectiveCaptureSummary summary = Prepared();
        var review = new ContentCaptureReviewWindow(summary, Mechanism.Http,
            new Dictionary<int, SeenProcess> { [4_242] = new(4_242, "client", Started) });
        review.Show();
        try
        {
            Dispatch();
            Assert.True(Named<Button>(review, "Cancel: nothing is recorded").IsFocused);
            Assert.Equal(
            [
                "Content", "Process 4242", "Limits", "Inspection", "Records for",
            ], review.Lines.Select(line => line.Label));
            Assert.Equal($"HTTP messages WinINet raises ({ContentSources.WinInetCapture}), every channel of the processes below",
                review.Lines[0].Value);
            Assert.StartsWith("client, started ", review.Lines[1].Value, StringComparison.Ordinal);
            Assert.Equal("its bytes are never shown", review.Lines[3].Value);
            Assert.Equal("up to 1 hour, or 1 GiB of journal, keeping 1 GiB free on the recording volume", review.Lines[4].Value);
            Assert.Equal([summary.CollectionStatement, summary.Disclosure], review.Statements);
            Assert.Equal("Start capture", Named<Button>(review, "Start capture").Content);
            Save(review, "content-capture-review.png");
            List<string> unheard = AccessibilityAuditTests.Unheard(review, out _);
            Assert.True(unheard.Count == 0, string.Join(Environment.NewLine, unheard));
        }
        finally
        {
            review.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§11.1: the Explore card keeps your processes' messages as a Keep choice, whose start asks which processes, taking no room from the ranked list")]
    public async Task TheExploreCardOffersAContentCapture()
    {
        // The smallest window, whose rail is the narrowest: the choice takes no room of its own.
        var window = new MainWindow { Width = 1_080, Height = 700 };
        window.Show();
        try
        {
            ComboBox keep = window.GetControl<ComboBox>("CaptureKeepSelector");
            TextBlock note = window.GetControl<TextBlock>("CaptureKeepNote");
            Button start = window.GetControl<Button>("StartExploringButton");
            CaptureKeepChoice messages = CaptureKeepChoice.All[^1];
            Assert.True(messages.Content);
            Assert.Equal("Your processes' messages", messages.Label);

            // Chosen, the note says what it keeps and that the broker's review comes first, and Start asks which processes.
            keep.SelectedItem = messages;
            Dispatch();
            Save(window, "content-capture-card-1080x700.png");
            Assert.True(note.IsVisible);

            // Its start is named whole however narrow the rail.
            Assert.True(start.DesiredSize.Width <= start.Bounds.Width + 0.5,
                $"The start wants {start.DesiredSize.Width:F0} px and has {start.Bounds.Width:F0}.");
            Assert.Equal("This capture " + messages.Explanation, note.Text);
            Assert.Equal("This capture keeps the HTTP messages of processes of yours you choose next, within limits you set; "
                + "you review it before anything is recorded.", note.Text);
            Assert.Equal("Choose processes…", start.Content);
            Assert.StartsWith("Choose your own processes whose HTTP messages", AutomationProperties.GetHelpText(start),
                StringComparison.Ordinal);

            // While a capture runs it is not offered.
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "Recording · follow latest", "Recording."));
            Dispatch();
            Assert.False(start.IsVisible);
            Assert.False(note.IsVisible);

            // A content capture that cannot run here says why on its own start, which still chooses processes, not Explore.
            window.BeginContentCapture(new([new(4_242, "client", Started)], 64 * 1024, 16L * 1024 * 1024,
                ContentInspectionMode.Disabled, 600));
            await Assert.IsAssignableFrom<Task>(window.CaptureRun).WaitAsync(TimeSpan.FromSeconds(60));
            Dispatch();
            CaptureRunOptions started = Assert.IsType<CaptureRunOptions>(window.StartedWith);
            Assert.Equal((600, (RollingRetentionPolicy?)null), (started.MaximumDurationSeconds, started.Rolling));
            Assert.Equal([4_242], started.Content!.Request.ProcessIds);
            Assert.NotNull(started.Review);
            Assert.True(start.IsVisible);
            Assert.Equal("Choose processes…", start.Content);
            Assert.Equal("You can still open a saved session or explore the demo. No capture was started.", ToolTip.GetTip(start));

            // Back on Explore, the action starts it, not a retry of a capture that was not Explore.
            keep.SelectedIndex = 0;
            Dispatch();
            Assert.Equal(("Start exploring (Ctrl+R)", "Start the default Explore capture"),
                (start.Content, AutomationProperties.GetHelpText(start)));
            Assert.False(note.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§11.1: Choose processes… and Ctrl+R open the chooser over the window's own processes, whose Esc starts nothing and gives the keyboard back")]
    public async Task TheChooserOpensFromTheCardAndEscStartsNothing()
    {
        var window = new MainWindow { Width = 1_080, Height = 700 };
        window.Show();
        try
        {
            window.GetControl<ComboBox>("CaptureKeepSelector").SelectedItem = CaptureKeepChoice.All[^1];
            Button start = window.GetControl<Button>("StartExploringButton");
            Assert.True(start.Focus());
            Dispatch();

            // Enter on Choose processes… asks which processes - the window lists them off its own thread first - with the
            // keyboard on the chooser; Esc closes it, nothing starts, and the keyboard is back on the button.
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            ContentCaptureWindow chooser = await Chooser(window);
            Assert.True(chooser.IsKeyboardFocusWithin);
            chooser.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatch();
            Assert.False(chooser.IsVisible);
            Assert.Null(window.CaptureRun);
            Assert.Null(window.StartedWith);
            Assert.True(start.IsFocused);

            // Ctrl+R, the start's key, asks the same.
            window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.Control);
            chooser = await Chooser(window);
            chooser.Close();
            Dispatch();
            Assert.Null(window.CaptureRun);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§11.1: a content capture runs whole from the card - your process chosen, the broker's plan reviewed - and starts only on Start capture")]
    public async Task AContentCaptureRunsWholeFromTheCard()
    {
        var window = new MainWindow { Width = 1_080, Height = 700 };
        window.RunningProcesses = () => [new(4_242, "client", Started)];
        var asked = new List<(IReadOnlyList<int> Processes, bool Started)>();
        window.CaptureRunner = async (report, _, options) =>
        {
            // As the broker's runner does: the plan it prepared is reviewed before anything is recorded.
            report(new(CaptureUiPhase.Starting, "Starting the content capture", "Preparing."));
            bool started = await options.Review!(Prepared());
            lock (asked) asked.Add((options.Content!.Request.ProcessIds, started));
            report(new(CaptureUiPhase.Unavailable, started ? "The broker stopped" : "The content capture did not start",
                started ? "The test ends the capture." : "You did not start it after its review. Nothing was recorded."));
        };
        window.Show();
        try
        {
            window.GetControl<ComboBox>("CaptureKeepSelector").SelectedItem = CaptureKeepChoice.All[^1];
            Dispatch();
            foreach (bool start in new[] { false, true })
            {
                // Choose processes… lists the person's own; the process is chosen by its box, and Review asks the broker.
                window.GetControl<Button>("StartExploringButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ContentCaptureWindow chooser = await Chooser(window);
                Assert.Equal(["client, process 4242"], chooser.Listed.Select(Head));
                Button review = Named<Button>(chooser, "Review with the broker…");
                Assert.False(review.IsEnabled);
                CheckBox box = chooser.GetVisualDescendants().OfType<CheckBox>()
                    .Single(candidate => AutomationProperties.GetName(candidate)?.StartsWith("client, process 4242", StringComparison.Ordinal) == true);
                box.IsChecked = true;
                Dispatch();
                review.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                // The broker's plan is reviewed over the window, the process named; only Start capture starts it.
                ContentCaptureReviewWindow plan = await Owned<ContentCaptureReviewWindow>(window);
                Assert.False(chooser.IsVisible);
                Assert.StartsWith("client, started ", plan.Lines.Single(line => line.Label == "Process 4242").Value, StringComparison.Ordinal);
                Named<Button>(plan, start ? "Start capture" : "Cancel: nothing is recorded")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Assert.IsAssignableFrom<Task>(window.CaptureRun).WaitAsync(TimeSpan.FromSeconds(30));
                Dispatch();
                Assert.Equal([4_242], asked[^1].Processes);
                Assert.Equal(start, asked[^1].Started);
            }

            Assert.Equal("The broker stopped", window.GetControl<TextBlock>("CaptureStatus").Text);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A content capture of process 4242's messages as the broker would prepare it.</summary>
    private static BrokerEffectiveCaptureSummary Prepared() => new(
        "content", "content", AdmissionMode.ScopedContent, AdmissionMode.ScopedContent, null, null, [4_242], [4_242],
        true, false, false,
        [new("etw/kernel/process", ProviderProcessScope.WholeMachineRequiredContext, [], true, "Lifecycle."),
            new(ContentSources.WinInetCapture, ProviderProcessScope.ProcessFiltered, [4_242], false, "Content.")],
        "Content is kept only from the named processes.",
        "WinINet keeps each HTTP exchange's messages; over HTTPS as their plaintext.",
        new BrokerCaptureQuota(3_600, 1L << 30, 1L << 30), BrokerRetentionPolicy.StopAtLimit, [],
        BrokerJournalPublication.Live, 4_000, null,
        new(ContentSources.WinInetCapture, [Started], 64 * 1024, 16L * 1024 * 1024, ContentInspectionMode.Disabled, ["*"]));

    /// <summary>A dialog the window shows over itself, once it is shown.</summary>
    private static async Task<T> Owned<T>(Window window)
        where T : Window
    {
        for (int attempt = 0; attempt < 200 && !window.OwnedWindows.OfType<T>().Any(); attempt++)
        {
            await Task.Delay(25);
            Dispatch();
        }

        Dispatch();
        return window.OwnedWindows.OfType<T>().Single();
    }

    /// <summary>The chooser once open: the processes are listed off the window's thread before it shows.</summary>
    private static async Task<ContentCaptureWindow> Chooser(Window window)
    {
        for (int attempt = 0; attempt < 200 && !window.OwnedWindows.OfType<ContentCaptureWindow>().Any(); attempt++)
        {
            await Task.Delay(25);
            Dispatch();
        }

        ContentCaptureWindow chooser = window.OwnedWindows.OfType<ContentCaptureWindow>().Single();
        Dispatch();
        return chooser;
    }

    /// <summary>A process as the list names it, without its start, which the time zone writes.</summary>
    private static string Head(string name) => name[..name.IndexOf(", started", StringComparison.Ordinal)];

    private static T Named<T>(Window window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<T>()
            .Single(control => AutomationProperties.GetName(control) == name
                || ControlAutomationPeer.CreatePeerForElement(control).GetName() == name);

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    private static void Save(Window window, string name)
    {
        for (int pass = 0; pass < 4; pass++)
        {
            Dispatch();
            _ = window.CaptureRenderedFrame();
        }

        Avalonia.Media.Imaging.WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        string directory = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(directory);
        frame!.Save(Path.Combine(directory, name));
    }
}
