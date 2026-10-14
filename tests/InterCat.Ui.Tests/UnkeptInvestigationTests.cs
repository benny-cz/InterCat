using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using Xunit;

namespace InterCat.Ui.Tests;

/// <summary>
/// A session opened from its investigation keeps what a person changes - its layout and view settings, the window's
/// panes - in the investigation's file (§26.3). A change the file cannot take is said beside the session's status once,
/// however often changes fail, and taken back once one is kept again.
/// </summary>
public sealed class UnkeptInvestigationTests : IDisposable
{
    private const string LayoutUnkept = "The pins and view settings could not be kept in the investigation: ";
    private const string PanesUnkept = "The window's panes could not be kept in the investigation: ";

    private readonly string folder = Path.Combine(Path.GetTempPath(), "InterCat.Ui.Tests.Unkept", Guid.NewGuid().ToString("N"));

    public UnkeptInvestigationTests() => Directory.CreateDirectory(folder);

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

    [AvaloniaFact(DisplayName = "§26.3: a change the investigation cannot keep is said once beside the session's status, however often changes fail, and taken back once one is kept")]
    public async Task AnUnkeptChangeIsSaidOnce()
    {
        DemoInvestigationResult demo = DemoInvestigation.Create(Path.Combine(folder, "demo"));
        var main = new MainWindow { Width = 1_280, Height = 800 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(demo.WorkspacePath);
            await Until(() => window.View is { Members.Count: 2 });
            ListBox members = Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one");
            members.SelectedIndex = window.View!.Members.ToList().FindIndex(row => row.FullPath == demo.ClientSession);
            Named<Button>(window, "Open in InterCat").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => main.GetControl<TextBlock>("CaptureSessionPath").Text == demo.ClientSession);
            await Written(main);
            var shown = (WorkspaceViewModel)main.DataContext!;
            TextBlock detail = main.GetControl<TextBlock>("CaptureDetail");

            // The investigation's file no longer reads as one, so nothing more is kept in it: said once for the view,
            // and once for the panes, however often each is changed.
            string file = File.ReadAllText(demo.WorkspacePath);
            File.WriteAllText(demo.WorkspacePath, "{");
            shown.RankBy = RankingMetric.BytesSent;
            await Written(main);
            shown.RankBy = RankingMetric.Records;
            await Written(main);
            main.ExpandPane(MainPane.Graph);
            await Written(main);
            main.ExpandPane(null);
            await Written(main);
            Assert.Equal((1, 1), (Count(detail.Text, LayoutUnkept), Count(detail.Text, PanesUnkept)));

            // Gone, the file is said to be gone, in the window's words - never the command line's advice to make one - and
            // still once for each.
            File.Delete(demo.WorkspacePath);
            string gone = $"No file is at {demo.WorkspacePath} any more; it was moved, renamed or deleted.";
            shown.RankBy = RankingMetric.BytesSent;
            await Written(main);
            shown.RankBy = RankingMetric.Records;
            await Written(main);
            main.ExpandPane(MainPane.Graph);
            await Written(main);
            main.ExpandPane(null);
            await Written(main);
            Assert.Equal((1, 1, 1, 1), (Count(detail.Text, LayoutUnkept), Count(detail.Text, PanesUnkept),
                Count(detail.Text, LayoutUnkept + gone), Count(detail.Text, PanesUnkept + gone)));

            // Readable again, a change is kept, and the card no longer says it is not.
            File.WriteAllText(demo.WorkspacePath, file);
            shown.RankBy = RankingMetric.BytesSent;
            await Written(main);
            Assert.Equal((0, 1), (Count(detail.Text, LayoutUnkept), Count(detail.Text, PanesUnkept)));
            main.ExpandPane(MainPane.Graph);
            await Written(main);
            Assert.Equal((0, 0), (Count(detail.Text, LayoutUnkept), Count(detail.Text, PanesUnkept)));
            Assert.Equal(RankingMetric.BytesSent, InvestigationWorkspace.LayoutOf(InvestigationWorkspace.Read(demo.WorkspacePath),
                InvestigationWorkspace.Read(demo.WorkspacePath).Members.Single(member => member.Path.Contains("client",
                    StringComparison.OrdinalIgnoreCase)).SessionId)!.RankBy);
        }
        finally
        {
            main.Close();
        }
    }

    private static int Count(string? text, string sentence)
    {
        int count = 0;
        for (int at = (text ?? string.Empty).IndexOf(sentence, StringComparison.Ordinal); at >= 0;
             at = text!.IndexOf(sentence, at + sentence.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>Waits for what the window writes to its investigation, kept or not, and for what it then says.</summary>
    private static async Task Written(MainWindow window)
    {
        try
        {
            await window.InvestigationWritten.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            // The window says why it was not kept; that is what the test reads.
        }

        for (int turn = 0; turn < 10; turn++)
        {
            await Task.Delay(10);
            Dispatch();
        }
    }

    private static T Named<T>(Window window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<T>()
            .Single(control => AutomationProperties.GetName(control) == name
                || ControlAutomationPeer.CreatePeerForElement(control).GetName() == name);

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
}
