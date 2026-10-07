using System.Text;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Capture.Journal.Tests;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// The windows and prompts beside the main one, as UI Automation exposes them to a screen reader (R15, §6.5): every
/// element the reader lands on is named for the ear, as the main window's are, and never by a record's fields.
/// </summary>
public sealed class SecondaryWindowAuditTests
{
    [AvaloniaFact(DisplayName = "R15: the content, raw record and channel windows name every element a screen reader lands on")]
    public async Task TheRecordWindowsAreNamedForTheEar()
    {
        using var session = new TemporarySession();
        ObservationRowV1[] rows =
        [
            Lifecycle(1, ObservationKind.Inventory, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 100 },
            Lifecycle(2, ObservationKind.Inventory, 200, 2) with { ResourceName = @"C:\Tools\server.exe", SessionRelativeTicks = 200 },
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 10)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 1_000 },
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 11)
                .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 1_100 },
        ];
        Publish(session.Store, rows, content: (ContentHeader(recordLimit: 1_024),
            [Content(rows[2], Encoding.UTF8.GetBytes("GET /index.html HTTP/1.1\r\nHost: example\r\n\r\n"), 1_024)]));
        SessionEvidencePage page = SessionEvidenceQuery.Read(session.Store, resolveOwners: true);
        SessionEvidenceRecord sent = page.Records.Single(record => record.Observation.Kind == ObservationKind.Send);
        var unheard = new List<string>();

        using (var content = new SessionContentWindow(session.Path, page.SessionId, sent))
        {
            content.Show();
            await Pause();
            unheard.AddRange(Unheard(content, "content, before its bytes"));
            content.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Show the kept bytes")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Pause();
            unheard.AddRange(Unheard(content, "content, with its bytes"));
        }

        using (var raw = new SessionRawRecordWindow(session.Path, page.SessionId, sent))
        {
            raw.Show();
            await Pause();
            unheard.AddRange(Unheard(raw, "raw record"));
        }

        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        WorkspaceSnapshot snapshot = OverviewWorkspace.From(overview);
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        using (var browser = new SessionChannelWindow(session.Path, overview.SessionId, overview.Generation, null,
            id => snapshot.Processes.FirstOrDefault(process => process.Id == id)?.NameWithPid))
        {
            _ = browser.ShowDialog<Channel?>(owner);
            await Pause();
            unheard.AddRange(Unheard(browser, "channels"));
            browser.Close();
        }

        owner.Close();
        Assert.True(unheard.Count == 0, string.Join(Environment.NewLine, unheard));
    }

    [AvaloniaFact(DisplayName = "R15: the investigation window's pages and its dialogs name every element a screen reader lands on")]
    public async Task TheInvestigationWindowIsNamedForTheEar()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        Guid a = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "alpha", 4, "lab-1"), Committed).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "beta", 6, "lab-1"), Committed).SessionId;
        Guid c = InvestigationWorkspace.Add(workspace, Datagrams(root.Path, "gamma", 3, "lab-2"), Committed).SessionId;
        InvestigationWorkspace.Align(workspace, b, 0, a, 0, 1_000, 0, null, Committed);
        InvestigationWorkspace.Align(workspace, c, 2_000_000_000, a, 5_000_000_000, 500_000, 50, null, Committed);
        InvestigationWorkspace.AddNote(workspace, "The migration starts here.", new WorkspaceNoteAnchor(c, 2_000_000_000), Committed);
        InvestigationWorkspace.AddNote(workspace, "Nothing failed before it.", null, Committed);

        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace);
            await Until(() => window.View is not null);
            var unheard = new List<string>();
            TabControl tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
            string[] pages = ["sessions", "candidate joins", "timeline", "notes"];
            for (int page = 0; page < pages.Length; page++)
            {
                tabs.SelectedIndex = page;
                if (page == 1) await window.FindCandidatesAsync();
                await Until(() => page != 2 || window.Timeline is not null);
                await Pause();
                unheard.AddRange(Unheard(window, $"investigation, {pages[page]}"));
            }

            tabs.SelectedIndex = 0;
            window.GetVisualDescendants().OfType<ListBox>()
                .Single(list => AutomationProperties.GetName(list) == "Sessions of this investigation; press Enter to open the selected one").SelectedIndex = 1;
            window.SelectNote(0);
            await Pause();
            // The package chooser restates what the package holds as its choices change, and reports its save in a prompt
            // of its own; every other dialog says in a status line what it did.
            foreach ((string name, Window? dialog, bool reports) in new (string, Window?, bool)[]
            {
                ("align", window.AlignDialogForSelected(), true),
                ("one host", window.HostDialogForSelected(), true),
                ("compare", window.CompareDialog(), true),
                ("add a note", window.NoteDialog(reword: false), true),
                ("reword a note", window.NoteDialog(reword: true), true),
                ("translations", window.TranslationsDialog(), true),
                ("views", window.ViewsDialog(), true),
                ("package", new InvestigationPackageWindow(InvestigationPackage.Preview(workspace)), false),
            })
            {
                Assert.NotNull(dialog);
                dialog!.Show(window);
                await Pause();
                unheard.AddRange(Unheard(dialog, name, reports));
                dialog.Close();
            }

            window.Close();
            Assert.True(unheard.Count == 0, string.Join(Environment.NewLine, unheard));
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: every prompt the windows build names every element a screen reader lands on")]
    public async Task EveryPromptIsNamedForTheEar()
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
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        _ = InvestigationWorkspace.Add(workspace, session.Path, Committed);
        InvestigationPackageResult investigation = InvestigationPackage.Create(workspace, Path.Combine(root.Path, "investigation"));

        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        var unheard = new List<string>();
        foreach ((string name, Window prompt) in new (string, Window)[]
        {
            ("redacted report", MainWindow.RedactedSharePrompt()),
            ("original package", MainWindow.OriginalPackagePrompt(OriginalEvidencePackage.Preview(session.Store))),
            ("original package saved", MainWindow.OriginalResultPrompt(original)),
            ("redacted package", MainWindow.RedactedPackagePrompt(SessionOverviewProjector.Project(session.Store))),
            ("redacted package of a time scope", MainWindow.RedactedPackagePrompt(SessionOverviewProjector.Project(session.Store),
                new TimeRange(1_000, 2_000))),
            ("redacted package over the bound", MainWindow.RedactedPackagePrompt(SessionOverviewProjector.Project(session.Store)
                with { ObservationRows = RedactedSessionPackage.MaximumRows + 1 })),
            ("redacted package saved", MainWindow.RedactedPackageResultPrompt(redacted)),
            ("stop Explore", MainWindow.StopExplorePrompt()),
            ("support bundle", MainWindow.SupportBundlePrompt(1)),
            ("investigation package saved", InvestigationWindow.PackageResultPrompt(investigation, offersOpen: true)),
        })
        {
            _ = prompt.ShowDialog<bool>(owner);
            await Pause();
            unheard.AddRange(Unheard(prompt, name, reports: false));
            prompt.Close();
        }

        owner.Close();
        Assert.True(unheard.Count == 0, string.Join(Environment.NewLine, unheard));
    }

    /// <summary>
    /// What a screen reader cannot use in <paramref name="window"/>, each problem prefixed with where it was found. A window
    /// that <paramref name="reports"/> what its actions do has a status line, which the audit holds to being announced.
    /// </summary>
    private static List<string> Unheard(Window window, string where, bool reports = true)
    {
        List<string> problems = [.. AccessibilityAuditTests.Unheard(window, out _).Select(problem => $"{where}: {problem}")];
        if (reports && !window.GetVisualDescendants().OfType<TextBlock>()
            .Any(text => AutomationProperties.GetLiveSetting(text) != AutomationLiveSetting.Off))
        {
            problems.Add($"{where}: no status line announces what its actions do");
        }

        return problems;
    }

    /// <summary>A session of <paramref name="records"/> datagrams, captured on <paramref name="host"/>.</summary>
    private static string Datagrams(string root, string name, int records, string host)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory), Guid.NewGuid(), "secondary-window-audit-tests");
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

    private static async Task Until(Func<bool> condition)
    {
        for (int wait = 0; wait < 500 && !condition(); wait++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    /// <summary>Lets a window read what it shows, on its own thread, and lay it out.</summary>
    private static async Task Pause()
    {
        for (int wait = 0; wait < 40; wait++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }
}
