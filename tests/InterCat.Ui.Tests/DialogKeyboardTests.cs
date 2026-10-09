using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Capture.Journal.Tests;
using InterCat.CaptureBroker;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// The dialogs and prompts by keyboard alone (R15): each opens with the keyboard on one of its own controls, which a
/// screen reader says, and Esc closes it; Enter does what a form dialog is for, and Ctrl+Enter saves a note's words.
/// </summary>
public sealed class DialogKeyboardTests
{
    private static readonly DateTimeOffset Started = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);

    [AvaloniaFact(DisplayName = "R15: every dialog and prompt opens with the keyboard on one of its own controls, and Esc closes it")]
    public async Task EveryDialogOpensWithTheKeyboardOnItsOwnControl()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 10)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 1_000 },
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 11)
                .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 1_100 },
        ]);
        using var root = new TemporaryDirectory();
        OriginalEvidencePackageResult original = OriginalEvidencePackage.Create(session.Store, Path.Combine(root.Path, "original"));
        RedactedSessionPackageResult redacted = RedactedSessionPackage.Create(session.Store, Path.Combine(root.Path, "redacted"),
            DateTimeOffset.UnixEpoch);
        string packaged = Path.Combine(root.Path, "packaged" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(packaged, Committed);
        _ = InvestigationWorkspace.Add(packaged, session.Path, Committed);
        InvestigationPackageResult investigation = InvestigationPackage.Create(packaged, Path.Combine(root.Path, "investigation"));
        var pin = new RetentionPin
        {
            Id = Guid.NewGuid(), FromNanoseconds = 300_000_000, AllowanceBytes = 8L << 20, PlacedUtc = Started, Reason = "the failover",
        };
        var offer = new MainWindow.PinOffer(500_000_000, "0.500 s", FromScope: true, ReleasedBefore: null, HeldBytes: 3L << 20,
            Pins: [(pin, RetentionPinText.Describe(pin, "0.300 s"))]);
        var problems = new List<string>();

        // The prompts the windows build, and the windows they open over the main one.
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        foreach ((string name, Window prompt) in new (string, Window)[]
        {
            ("redacted report", MainWindow.RedactedSharePrompt()),
            ("original package", MainWindow.OriginalPackagePrompt(OriginalEvidencePackage.Preview(session.Store))),
            ("original package saved", MainWindow.OriginalResultPrompt(original)),
            ("redacted package", MainWindow.RedactedPackagePrompt(SessionOverviewProjector.Project(session.Store))),
            ("redacted package saved", MainWindow.RedactedPackageResultPrompt(redacted)),
            ("stop Explore", MainWindow.StopExplorePrompt()),
            ("support bundle", MainWindow.SupportBundlePrompt(1)),
            ("pin", MainWindow.PinPrompt(offer)),
            ("investigation package saved", InvestigationWindow.PackageResultPrompt(investigation, offersOpen: true)),
            ("keys", new KeysWindow()),
            ("investigation keys", new KeysWindow(InvestigationKeys.All, InvestigationKeys.Intro)),
            ("content capture", new ContentCaptureWindow([new(4_242, "client", Started)])),
            ("content capture review", new ContentCaptureReviewWindow(Summary(), Mechanism.Http,
                new Dictionary<int, SeenProcess> { [4_242] = new(4_242, "client", Started) })),
        })
        {
            problems.AddRange(await OpenAndEscape(prompt, owner, name));
        }

        owner.Close();

        // The investigation window's dialogs, over it.
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        _ = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "alpha", 3, "lab-1"), Committed);
        _ = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "beta", 3, "lab-2"), Committed);
        _ = InvestigationWorkspace.AddNote(workspace, "Nothing failed before it.", null, Committed);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one").SelectedIndex = 1;
            window.SelectNote(0);
            Dispatch();
            foreach ((string name, Window? dialog) in new (string, Window?)[]
            {
                ("align", window.AlignDialogForSelected()),
                ("one host", window.HostDialogForSelected()),
                ("compare", window.CompareDialog()),
                ("add a note", window.NoteDialog(reword: false)),
                ("reword a note", window.NoteDialog(reword: true)),
                ("translations", window.TranslationsDialog()),
                ("views", window.ViewsDialog()),
                ("package", new InvestigationPackageWindow(InvestigationPackage.Preview(workspace))),
            })
            {
                problems.AddRange(await OpenAndEscape(Assert.IsAssignableFrom<Window>(dialog), window, name));
            }

            Assert.True(window.IsVisible);
            window.Close();
        }
        finally
        {
            main.Close();
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [AvaloniaFact(DisplayName = "R15: the Align dialog opens on the way chosen to align, and Enter aligns, as Align does")]
    public async Task EnterAligns()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        _ = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "alpha", 3, "lab-1"), Committed);
        Guid b = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "beta", 3, "lab-2"), Committed).SessionId;
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one").SelectedIndex = 1;
            InvestigationAlignWindow dialog = window.AlignDialogForSelected()!;
            Task<bool> aligned = dialog.ShowDialog<bool>(window);
            Dispatch();

            // The way chosen - by instants read in both - has the keyboard, and a screen reader says it.
            RadioButton chosen = Assert.IsType<RadioButton>(dialog.FocusManager?.GetFocusedElement());
            Assert.Equal((true, "By one or two instants I read in both"), (chosen.IsChecked, chosen.Content as string));

            // Tab goes on to the first instant it asks for; Enter there with the instants unwritten says what is missing,
            // and aligns nothing.
            TextBox at = Named<TextBox>(dialog, "The instant in this session, in seconds");
            Press(dialog, PhysicalKey.Tab);
            Assert.True(at.IsFocused);
            Press(dialog, PhysicalKey.Enter);
            Assert.Equal("Write both instants in seconds of their own session's time, such as 12.5.",
                Named<TextBlock>(dialog, "Alignment status").Text);
            Assert.False(aligned.IsCompleted);

            // Written, Enter in a field aligns, and the dialog closes as Align closes it.
            at.Text = "2";
            Named<TextBox>(dialog, "The same instant in the reference session, in seconds").Text = "5";
            Press(dialog, PhysicalKey.Enter);
            WaitFor(() => aligned.IsCompleted);
            Assert.True(await aligned);
            Assert.Contains(InvestigationWorkspace.Read(workspace).Alignments, alignment => alignment.SessionId == b);
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: Enter in a translation's fields states it, as State translation does")]
    public void EnterStatesATranslation()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        var dialog = new InvestigationTranslationsWindow(workspace);
        _ = dialog.ShowDialog<bool>(owner);
        Dispatch();
        TextBox seen = Named<TextBox>(dialog, "The endpoint as one capture sees it");
        Assert.True(seen.IsFocused);
        dialog.Enter("203.0.113.7:8443", "10.0.0.2:443", "the router forwards 8443");
        Press(dialog, PhysicalKey.Enter);
        WaitFor(() => dialog.Listed.Count == 1);
        Assert.Equal(("203.0.113.7:8443", "10.0.0.2:443"), (dialog.Listed[0].Seen, dialog.Listed[0].Is));

        // Enter on a translation listed states nothing: only the fields' Enter does.
        TextBlock status = Named<TextBlock>(dialog, "Translation status");
        string? stated = status.Text;
        ListBox known = Named<ListBox>(dialog, "Address translations in force");
        Assert.True(Assert.IsAssignableFrom<Control>(known.ContainerFromIndex(0)).Focus(NavigationMethod.Tab));
        Press(dialog, PhysicalKey.Enter);
        Assert.Equal(stated, status.Text);
        Assert.Single(dialog.Listed);
        dialog.Close();
        owner.Close();
    }

    [AvaloniaFact(DisplayName = "R15: Ctrl+Enter saves a note, whose words take Enter as a new line")]
    public async Task CtrlEnterSavesANote()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        _ = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "alpha", 3, "lab-1"), Committed);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            InvestigationNoteWindow dialog = window.NoteDialog(reword: false)!;
            Task<bool> saved = dialog.ShowDialog<bool>(window);
            Dispatch();
            TextBox words = Named<TextBox>(dialog, "The note's words");
            Assert.True(words.IsFocused);
            dialog.Enter("The upload stalls");

            // Enter alone is a new line in the words; Ctrl+Enter saves the note.
            Press(dialog, PhysicalKey.Enter);
            Assert.Contains("\n", words.Text ?? string.Empty, StringComparison.Ordinal);
            Assert.False(saved.IsCompleted);
            Press(dialog, PhysicalKey.Enter, RawInputModifiers.Control);
            WaitFor(() => saved.IsCompleted);
            Assert.True(await saved);
            Assert.Contains("The upload stalls", Assert.Single(InvestigationWorkspace.Read(workspace).Notes).Text, StringComparison.Ordinal);
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: the views dialog opens on its first view, chosen, else on the name to save the view shown, else on Close")]
    public async Task TheViewsDialogOpensWhereItCanBeUsed()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        Guid a = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "alpha", 3, "lab-1"), Committed).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "beta", 3, "lab-2"), Committed).SessionId;
        InvestigationWorkspace.Align(workspace, b, 0, a, 1_000_000_000, 1_000, 0, null, Committed);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();

        // With nothing shown to save and no view saved, Close has the keyboard.
        var nothing = new InvestigationViewsWindow(workspace, null);
        _ = nothing.ShowDialog<TimeRange?>(owner);
        Dispatch();
        Assert.Equal("Close", Assert.IsType<Button>(nothing.FocusManager?.GetFocusedElement()).Content);
        nothing.Close();
        Dispatch();

        // With an interval shown and no view saved, the name to save it under has the keyboard, and Enter saves it.
        var shown = new TimeRange(1_000, 2_000);
        var first = new InvestigationViewsWindow(workspace, shown);
        _ = first.ShowDialog<TimeRange?>(owner);
        Dispatch();
        TextBox name = Named<TextBox>(first, "A name for the view shown now");
        Assert.True(name.IsFocused);
        name.Text = "the stall";
        Press(first, PhysicalKey.Enter);
        WaitFor(() => first.Listed.Count == 1);
        first.Close();
        Dispatch();

        // With a view saved, the first is chosen and has the keyboard, so Enter shows it.
        var again = new InvestigationViewsWindow(workspace, shown);
        Task<TimeRange?> chosen = again.ShowDialog<TimeRange?>(owner);
        Dispatch();
        ListBox views = Named<ListBox>(again, "Saved views of the investigation's time");
        Assert.Equal(0, views.SelectedIndex);
        Assert.Same(views.ContainerFromIndex(0), again.FocusManager?.GetFocusedElement());
        Press(again, PhysicalKey.Enter);
        WaitFor(() => chosen.IsCompleted);
        Assert.Equal(shown, await chosen);
        owner.Close();
    }

    /// <summary>
    /// Opens <paramref name="dialog"/> over <paramref name="owner"/> and presses Esc in it; says what is wrong: the keyboard
    /// on nothing of its own as it opens, or Esc not closing it.
    /// </summary>
    private static async Task<List<string>> OpenAndEscape(Window dialog, Window owner, string name)
    {
        var problems = new List<string>();
        _ = dialog.ShowDialog<object?>(owner);
        for (int pass = 0; pass < 5; pass++)
        {
            Dispatch();
            await Task.Delay(10);
        }

        if (dialog.FocusManager?.GetFocusedElement() is not Control focused || !dialog.IsVisualAncestorOf(focused))
        {
            problems.Add($"{name}: opens with the keyboard on nothing of its own");
            dialog.Close();
            Dispatch();
            return problems;
        }

        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatch();
        if (dialog.IsVisible)
        {
            problems.Add($"{name}: Esc does not close it");
            dialog.Close();
            Dispatch();
        }

        return problems;
    }

    /// <summary>What the broker would keep of a content capture of process 4242, as its review states it.</summary>
    private static BrokerEffectiveCaptureSummary Summary() => new(
        "content", "content", AdmissionMode.ScopedContent, AdmissionMode.ScopedContent, null, null, [4_242], [4_242],
        true, false, false,
        [new("etw/kernel/process", ProviderProcessScope.WholeMachineRequiredContext, [], true, "Lifecycle."),
            new(ContentSources.WinInetCapture, ProviderProcessScope.ProcessFiltered, [4_242], false, "Content.")],
        "Content is kept only from the named processes.",
        "WinINet keeps each HTTP exchange's messages; over HTTPS as their plaintext.",
        new BrokerCaptureQuota(3_600, 1L << 30, 1L << 30), BrokerRetentionPolicy.StopAtLimit, [],
        BrokerJournalPublication.Live, 4_000, null,
        new(ContentSources.WinInetCapture, [Started], 64 * 1024, 16L * 1024 * 1024, ContentInspectionMode.Disabled, ["*"]));

    /// <summary>A session of <paramref name="records"/> datagrams, captured on <paramref name="host"/>.</summary>
    private static string Datagrams(string root, string name, int records, string host)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory), Guid.NewGuid(), "dialog-keyboard-tests");
        _ = Publish(
            store,
            [
                .. Enumerable.Range(0, records).Select(index =>
                    Transfer(1_000 + (index * 10), ObservationKind.Send, AccountingSide.SendSide, 10, 100, (ulong)(index + 1))
                        .Between("192.168.1.5:61000", "8.8.8.8:53") with { Mechanism = Mechanism.Udp, SessionRelativeTicks = (1_000 + (index * 10)) * 100 }),
            ],
            capture: CaptureId.New(),
            clock: ClockFor(ClockId.New(), host));
        store.ReleaseSegmentReaders();
        return directory;
    }

    private static T Named<T>(Window window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<T>()
            .Single(control => AutomationProperties.GetName(control) == name
                || ControlAutomationPeer.CreatePeerForElement(control).GetName() == name);

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        Dispatch();
    }

    private static void WaitFor(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            Dispatch();
            Thread.Sleep(10);
        }

        Assert.True(condition());
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
