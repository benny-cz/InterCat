using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using InterCat.Application;
using InterCat.Desktop;
using Xunit;

namespace InterCat.Ui.Tests;

/// <summary>
/// The main window's flows behind a file or folder choice, run whole with the choice answered as a person would answer it
/// - a file chosen, none, or one the flow cannot use - each writing what it says it wrote and saying what it did, or
/// nothing at all when nothing was chosen.
/// </summary>
public sealed class ChosenPathFlowTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "InterCat.Ui.Tests.Chosen", Guid.NewGuid().ToString("N"));

    public ChosenPathFlowTests() => Directory.CreateDirectory(folder);

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

    [AvaloniaFact(DisplayName = "§6.4: Export this view writes the file chosen, as CSV or JSON by its name, says so, and writes nothing when none is chosen or says why it could not")]
    public async Task ExportWritesTheFileChosen()
    {
        (MainWindow window, AnsweredChoices choices) = await Opened();
        TextBlock detail = window.GetControl<TextBlock>("CaptureDetail");
        string before = detail.Text!;

        // None chosen: nothing is written and nothing is said; what was asked names the view and suggests JSON.
        choices.Answer(null);
        Click(window, "ExportButton");
        await Until(() => choices.Asked == 1);
        Assert.Equal(("Export this view", ".json"), (choices.Saves[0].Title, Path.GetExtension(choices.Saves[0].SuggestedFileName)));
        Assert.Equal(before, detail.Text);
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder, "*.json").Concat(Directory.EnumerateFileSystemEntries(folder, "*.csv")));

        // A file named .csv is CSV, one named .json is JSON, and the card says what was written where.
        string csv = Path.Combine(folder, "ranked.csv");
        choices.Answer(csv);
        Click(window, "ExportButton");
        await Until(() => detail.Text!.EndsWith($" to {csv}.", StringComparison.Ordinal));
        Assert.StartsWith("Exported ", detail.Text, StringComparison.Ordinal);
        Assert.False(File.ReadAllText(csv).TrimStart().StartsWith('{'));

        string json = Path.Combine(folder, "ranked.json");
        choices.Answer(json);
        Click(window, "ExportButton");
        await Until(() => detail.Text!.EndsWith($" to {json}.", StringComparison.Ordinal));
        using (JsonDocument written = JsonDocument.Parse(File.ReadAllText(json)))
        {
            Assert.Equal(JsonValueKind.Object, written.RootElement.ValueKind);
        }

        // A file the export cannot write is said, and the export may be tried again.
        choices.Answer(folder);
        Click(window, "ExportButton");
        await Until(() => detail.Text!.StartsWith("Could not export this view: ", StringComparison.Ordinal));
        choices.Answer(Path.Combine(folder, "again.json"));
        Click(window, "ExportButton");
        await Until(() => File.Exists(Path.Combine(folder, "again.json")));
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§11.3: a redacted sharing report asks first, chooses its file only once asked, and says what it saved")]
    public async Task ARedactedReportAsksFirst()
    {
        (MainWindow window, AnsweredChoices choices) = await Opened();
        TextBlock detail = window.GetControl<TextBlock>("CaptureDetail");

        // Declined, no file is asked for.
        Click(window, "ShareRedactedButton");
        Window prompt = await Prompt(window);
        Press(prompt, "Cancel");
        await Until(() => window.OwnedWindows.Count == 0);
        Assert.Equal(0, choices.Asked);

        string report = Path.Combine(folder, "report.json");
        choices.Answer(report);
        Click(window, "ShareRedactedButton");
        Press(await Prompt(window), "Choose report file");
        await Until(() => detail.Text!.EndsWith($" to {report}.", StringComparison.Ordinal));
        Assert.Equal("Save redacted sharing report", choices.Saves[0].Title);
        Assert.Equal(RedactedShareExport.SuggestedName("json"), choices.Saves[0].SuggestedFileName);
        Assert.StartsWith("Saved redacted report with ", detail.Text, StringComparison.Ordinal);
        Assert.True(File.Exists(report));
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§20.6: a support bundle says what it holds, then saves to the file chosen, or nothing when none is")]
    public async Task ASupportBundleSavesToTheFileChosen()
    {
        (MainWindow window, AnsweredChoices choices) = await Opened();
        TextBlock detail = window.GetControl<TextBlock>("CaptureDetail");
        string before = detail.Text!;

        choices.Answer(null);
        Click(window, "SupportBundleButton");
        Press(await Prompt(window), "Choose bundle file");
        await Until(() => choices.Asked == 1);
        Assert.Equal(("Save support bundle", SupportBundle.SuggestedName), (choices.Saves[0].Title, choices.Saves[0].SuggestedFileName));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(before, detail.Text);

        string bundle = Path.Combine(folder, "support.json");
        choices.Answer(bundle);
        Click(window, "SupportBundleButton");
        Press(await Prompt(window), "Choose bundle file");
        await Until(() => File.Exists(bundle));
        await Until(() => detail.Text == $"Support bundle saved to {bundle}. It holds no name, address or content; review it before sending it.");
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§6.1: Open saved session opens the folder chosen, says why one is no session, and does nothing when none is chosen")]
    public async Task OpenSavedSessionOpensTheFolderChosen()
    {
        DemoInvestigationResult demo = DemoInvestigation.Create(Path.Combine(folder, "demo"));
        var window = new MainWindow { Width = 1_280, Height = 800 };
        var choices = new AnsweredChoices(window);
        window.Choices = choices;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        TextBlock status = window.GetControl<TextBlock>("CaptureStatus");
        string idle = status.Text!;

        choices.Answer(null);
        Click(window, "OpenSavedSessionButton");
        await Until(() => choices.Asked == 1);
        Assert.Equal("Open an InterCat session directory", choices.Folders[0].Title);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(idle, status.Text);

        // A dialog the system could not show chooses nothing, and says why where the card says what an action did; the
        // window goes on as it was.
        choices.Fail(new InvalidOperationException("No dialog service is available"));
        Click(window, "OpenSavedSessionButton");
        await Until(() => status.Text == "Nothing could be chosen");
        Assert.Equal("The system could not show its dialog to choose a folder: No dialog service is available. Nothing "
            + "was opened or saved, and the current workspace is unchanged.", window.GetControl<TextBlock>("CaptureDetail").Text);

        // A folder that holds no session is said to be none, and the window keeps what it showed.
        string empty = Directory.CreateDirectory(Path.Combine(folder, "nothing-here")).FullName;
        choices.Answer(empty);
        Click(window, "OpenSavedSessionButton");
        await Until(() => status.Text == "Could not open this session");
        Assert.EndsWith(" The current workspace is unchanged.", window.GetControl<TextBlock>("CaptureDetail").Text, StringComparison.Ordinal);

        // Where the picker would start is a convenience: a system that cannot find it still shows the picker.
        await window.UseSessionRootAsync(folder);
        choices.StartFailure = new InvalidOperationException("No shell folder for this path");
        choices.Answer(demo.ServerSession);
        Click(window, "OpenSavedSessionButton");
        await Until(() => status.Text == "Saved session open");
        Assert.Null(choices.Folders[^1].SuggestedStartLocation);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "R15: Ctrl+O opens a saved session as Open saved session does, and waits with it while a capture runs")]
    public async Task CtrlOOpensASavedSession()
    {
        DemoInvestigationResult demo = DemoInvestigation.Create(Path.Combine(folder, "demo"));
        var window = new MainWindow { Width = 1_280, Height = 800 };
        var choices = new AnsweredChoices(window);
        window.Choices = choices;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        TextBlock status = window.GetControl<TextBlock>("CaptureStatus");
        Button open = window.GetControl<Button>("OpenSavedSessionButton");
        Assert.Equal(("Open saved session", "Open an existing InterCat session (Ctrl+O)", "Ctrl+O"),
            (open.Content, ToolTip.GetTip(open), Avalonia.Automation.AutomationProperties.GetAcceleratorKey(open)));
        Assert.Contains(WindowKeys.All, key => (key.Where, key.Keys, key.Does)
            == ("Anywhere", "Ctrl+O", "open a saved session, as Open saved session does"));

        // While a capture runs, Open saved session waits hidden, and so does its key.
        window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "Recording", "Recording."));
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(Avalonia.Input.PhysicalKey.O, Avalonia.Input.RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, choices.Asked);

        window.ApplyCaptureUpdate(new(CaptureUiPhase.Unavailable, "Explore did not start", "Declined."));
        Dispatcher.UIThread.RunJobs();
        choices.Answer(demo.ServerSession);
        window.KeyPressQwerty(Avalonia.Input.PhysicalKey.O, Avalonia.Input.RawInputModifiers.Control);
        await Until(() => status.Text == "Saved session open");
        Assert.Equal(1, choices.Asked);
        Assert.Equal("Open an InterCat session directory", choices.Folders[0].Title);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§8.4: the Investigation menu opens the file chosen, or starts one at the name chosen, in a window of its own")]
    public async Task TheInvestigationMenuOpensOrStartsTheFileChosen()
    {
        DemoInvestigationResult demo = DemoInvestigation.Create(Path.Combine(folder, "demo"));
        var window = new MainWindow { Width = 1_280, Height = 800 };
        var choices = new AnsweredChoices(window);
        window.Choices = choices;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // None chosen opens nothing.
        choices.Answer(null);
        Choose(window, "OpenInvestigationItem");
        await Until(() => choices.Asked == 1);
        Assert.Equal("Open an InterCat investigation", choices.Opens[0].Title);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(window.OwnedWindows);
        Assert.Empty(window.Investigations);

        // Nor does a dialog the system could not show, which says why.
        choices.Fail(new IOException("The shell refused the dialog."));
        Choose(window, "OpenInvestigationItem");
        TextBlock status = window.GetControl<TextBlock>("CaptureStatus");
        await Until(() => status.Text == "Nothing could be chosen");
        Assert.StartsWith("The system could not show its dialog to choose a file: The shell refused the dialog. ",
            window.GetControl<TextBlock>("CaptureDetail").Text, StringComparison.Ordinal);
        Assert.Empty(window.Investigations);

        choices.Answer(demo.WorkspacePath);
        Choose(window, "OpenInvestigationItem");
        await Until(() => window.Investigations.Count > 0);
        InvestigationWindow opened = window.Investigations.Single();
        opened.Close();

        // A new investigation is made at the name chosen, suggested with its extension, and never written over one there.
        string made = Path.Combine(folder, "case" + InvestigationWorkspace.Extension);
        choices.Answer(made);
        Choose(window, "NewInvestigationItem");
        await Until(() => window.Investigations.Count > 0);
        Assert.Equal("investigation" + InvestigationWorkspace.Extension, choices.Saves[0].SuggestedFileName);
        Assert.True(File.Exists(made));
        Assert.Empty(InvestigationWorkspace.Read(made).Members);
        window.Investigations.Single().Close();
        string existing = File.ReadAllText(demo.WorkspacePath);
        choices.Answer(demo.WorkspacePath);
        Choose(window, "NewInvestigationItem");
        await Until(() => window.Investigations.Count > 0);
        Assert.Equal(existing, File.ReadAllText(demo.WorkspacePath));
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§11.3: Share redacted session saves a verified package in a new folder under the one chosen, nothing when none is, and opens it to review")]
    public async Task ARedactedPackageGoesUnderTheFolderChosen()
    {
        (MainWindow window, AnsweredChoices choices) = await Opened();
        TextBlock detail = window.GetControl<TextBlock>("CaptureDetail");
        string target = Directory.CreateDirectory(Path.Combine(folder, "shared")).FullName;

        // Its scope is asked first, then the folder; none chosen saves nothing.
        choices.Answer(null);
        Click(window, "SharePackageButton");
        Press(await Prompt(window), "Choose a folder…");
        await Until(() => choices.Asked == 1);
        Assert.Equal("Choose where to save the redacted session package", choices.Folders[0].Title);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));

        // Chosen, the package is a new folder under it, verified before it is said to be saved, and opened to review.
        choices.Answer(target);
        Click(window, "SharePackageButton");
        Press(await Prompt(window), "Choose a folder…");
        await Until(() => window.OwnedWindows.Count == 1 && window.OwnedWindows[0].Title == "Redacted session package saved");
        string package = Assert.Single(Directory.EnumerateDirectories(target));
        Assert.StartsWith("intercat-redacted-session-", Path.GetFileName(package), StringComparison.Ordinal);
        Assert.StartsWith("Saved ", detail.Text, StringComparison.Ordinal);
        Assert.Contains($" to {package}. ", detail.Text, StringComparison.Ordinal);
        Press(window.OwnedWindows[0], "Open it here to review");
        await Until(() => window.OwnedWindows.Count == 0
            && window.GetControl<TextBlock>("CaptureStatus").Text == "Redacted session package open");
        await window.SessionFilesChecked;
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§11.3: Share original session says what it holds, then saves an exact copy in a new folder under the one chosen")]
    public async Task AnOriginalCopyGoesUnderTheFolderChosen()
    {
        (MainWindow window, AnsweredChoices choices) = await Opened();
        TextBlock detail = window.GetControl<TextBlock>("CaptureDetail");
        string target = Directory.CreateDirectory(Path.Combine(folder, "original")).FullName;

        // Declined at what it holds, no folder is asked for.
        Click(window, "ShareOriginalButton");
        Press(await Prompt(window), "Cancel");
        await Until(() => window.OwnedWindows.Count == 0);
        Assert.Equal(0, choices.Asked);

        choices.Answer(target);
        Click(window, "ShareOriginalButton");
        Press(await Prompt(window), "Save unredacted copy…");
        await Until(() => window.OwnedWindows.Count == 1 && window.OwnedWindows[0].Title == "Original session saved");
        Assert.Equal("Choose where to save the original, unredacted session", choices.Folders[0].Title);
        string copy = Assert.Single(Directory.EnumerateDirectories(target));
        Assert.StartsWith("intercat-original-session-", Path.GetFileName(copy), StringComparison.Ordinal);
        Assert.StartsWith("Saved an exact copy of generation 2 to " + copy + ". ", detail.Text, StringComparison.Ordinal);
        Press(window.OwnedWindows[0], "Done");
        await Until(() => window.OwnedWindows.Count == 0);
        window.Close();
    }

    [AvaloniaFact(DisplayName = "§8.4: the investigation window adds the folders chosen, relinks a session to where it is now, and packages under the folder chosen")]
    public async Task TheInvestigationWindowUsesTheFoldersChosen()
    {
        DemoInvestigationResult demo = DemoInvestigation.Create(Path.Combine(folder, "demo"));
        string workspace = Path.Combine(folder, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, DateTimeOffset.UtcNow);
        var main = new MainWindow { Width = 1_280, Height = 800 };
        main.Show();
        InvestigationWindow window = main.ShowInvestigation(workspace);
        var choices = new AnsweredChoices(window);
        window.Choices = choices;
        await Until(() => window.View is not null);

        // Several sessions are added from one choice; none chosen adds none, nor does a dialog the system could not
        // show, which the window says.
        choices.AnswerFolders();
        Press(window, "Add sessions…");
        await Until(() => choices.Asked == 1);
        Assert.True(choices.Folders[0].AllowMultiple);
        choices.Fail(new InvalidOperationException("No dialog service is available"));
        Press(window, "Add sessions…");
        await Until(() => Texts(window).Contains("The system could not show its dialog to choose a folder: No dialog "
            + "service is available. Nothing was added, relinked or written."));
        Assert.Empty(window.View!.Members);
        choices.AnswerFolders(demo.ClientSession, demo.ServerSession);
        Press(window, "Add sessions…");
        await Until(() => window.View is { Members.Count: 2 });

        // A session moved is relinked to the folder chosen as where it is now.
        string moved = Path.Combine(folder, "moved-server");
        Directory.Move(demo.ServerSession, moved);
        ListBox list = window.GetVisualDescendants().OfType<ListBox>().First(candidate => candidate.ItemCount == 2);
        list.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        choices.Answer(moved);
        Press(window, "Relink…");
        await Until(() => InvestigationWorkspace.Read(workspace).Members[1].Path.EndsWith("moved-server", StringComparison.Ordinal));
        Assert.Matches("^Where is Session [0-9a-f]{8} now\\?$", choices.Folders[3].Title);
        await Until(() => window.View is { } view && view.Members.All(member => member.State == WorkspaceMemberState.Present));

        // Packaged under the folder chosen, after the confirmation; none chosen packages nothing and says so.
        string target = Directory.CreateDirectory(Path.Combine(folder, "packages")).FullName;
        choices.Answer(null);
        Press(window, "Package…");
        Press(await Prompt(window), "Save unredacted package…");
        await Until(() => StatusOf(window) == "Nothing was packaged.");
        choices.Answer(target);
        Press(window, "Package…");
        Press(await Prompt(window), "Save unredacted package…");
        await Until(() => window.OwnedWindows.Count == 1 && window.OwnedWindows[0].Title == "Investigation package saved");
        string package = Assert.Single(Directory.EnumerateDirectories(target));
        Assert.StartsWith("case-package-", Path.GetFileName(package), StringComparison.Ordinal);
        Assert.StartsWith("Saved the investigation with 2 sessions to " + package + ".", StatusOf(window), StringComparison.Ordinal);

        // Opened here, the package's investigation has a window of its own beside this one's, as InterCat's other investigations do.
        Press(window.OwnedWindows[0], "Open it here");
        await Until(() => main.Investigations.Count == 2);
        Assert.Equal(Path.Combine(package, "case" + InvestigationWorkspace.Extension), main.Investigations[1].WorkspacePath);
        main.Close();
    }

    /// <summary>A window showing the demo's client session, its choices answered by the test.</summary>
    private async Task<(MainWindow Window, AnsweredChoices Choices)> Opened()
    {
        DemoInvestigationResult demo = DemoInvestigation.Create(Path.Combine(folder, "demo"));
        var window = new MainWindow { Width = 1_280, Height = 800 };
        var choices = new AnsweredChoices(window);
        window.Choices = choices;
        window.Show();
        Assert.True(await window.OpenSessionAsync(demo.ClientSession));
        Dispatcher.UIThread.RunJobs();
        return (window, choices);
    }

    private static void Click(Window window, string name) =>
        window.GetControl<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void Choose(Window window, string name) =>
        window.GetControl<MenuItem>(name).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    /// <summary>The prompt the window shows over itself, once it is shown.</summary>
    private static async Task<Window> Prompt(Window owner)
    {
        await Until(() => owner.OwnedWindows.Count == 1);
        return owner.OwnedWindows[0];
    }

    private static string? StatusOf(InvestigationWindow window) =>
        window.GetVisualDescendants().OfType<TextBlock>()
            .Single(block => Avalonia.Automation.AutomationProperties.GetName(block) == "Investigation status").Text;

    /// <summary>Presses a window's button by what it says.</summary>
    private static void Press(Window prompt, string label)
    {
        Button button = prompt.GetLogicalDescendants().OfType<Button>()
            .First(candidate => Equals(candidate.Content, label) && candidate.IsEffectivelyVisible);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static List<string> Texts(Window window) =>
        [.. window.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).OfType<string>()];

    private static async Task Until(Func<bool> condition)
    {
        for (int wait = 0; wait < 500 && !condition(); wait++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
