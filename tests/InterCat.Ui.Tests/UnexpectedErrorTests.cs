using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using InterCat.Application;
using InterCat.Desktop;
using Xunit;

namespace InterCat.Ui.Tests;

/// <summary>
/// An error no action of the window expected (§20.6 Internal) is a defect in InterCat. Where the application contains them,
/// the window nearest the person says it where it says what an action did, and goes on running rather than closing with
/// everything it had open; an action started and not waited for has its error said too, rather than lost with its task. A
/// test contains none, so a defect fails the test that meets it.
/// </summary>
public sealed class UnexpectedErrorTests : IDisposable
{
    private const string Headline = "Stopped on an error InterCat did not expect";

    private const string Stopped =
        "What was being done stopped on an error InterCat did not expect, a defect in InterCat rather than in what was asked. ";

    private readonly string folder = Directory.CreateTempSubdirectory("intercat-unexpected-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    [AvaloniaFact(DisplayName = "§20.6: an error no action expected is said on the window's card, which keeps running; one started and not waited for is said too")]
    public async Task AnErrorNoActionExpectedIsSaidAndTheWindowKeepsRunning()
    {
        var window = new MainWindow { Width = 1_280, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        TextBlock status = window.GetControl<TextBlock>("CaptureStatus");
        TextBlock detail = window.GetControl<TextBlock>("CaptureDetail");
        try
        {
            // An error another handler has already taken is its to say, and is not said again.
            int takenElsewhere = 0;
            using (UnexpectedErrors.Contain(Dispatcher.UIThread, _ => ++takenElsewhere > 0))
            using (UnexpectedErrors.Contain(Dispatcher.UIThread, exception => UnexpectedErrors.SayIn([window], _ => true, exception)))
            {
                Dispatcher.UIThread.Post(() => throw new InvalidCastException("taken elsewhere"));
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(1, takenElsewhere);
                Assert.NotEqual(Headline, status.Text);
            }

            using (UnexpectedErrors.Contain(Dispatcher.UIThread, exception => UnexpectedErrors.SayIn([window], _ => true, exception)))
            {
                // An event handler's error, as one that has awaited something ends with it.
                Dispatcher.UIThread.Post(FailingHandler);
                await Until(() => status.Text == Headline);
                Assert.Equal(Stopped + "InvalidCastException: A row was read as the wrong kind. The window keeps running.", detail.Text);
                Assert.True(window.IsVisible);

                // An action started and not waited for ended its task with its error, where nothing said it.
                Task.FromException(new FormatException("The moment has no time zone")).Watched();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(Stopped + "FormatException: The moment has no time zone. The window keeps running.", detail.Text);

                // Several at once are said together, as one.
                Task.WhenAll(Task.FromException(new FormatException("first")), Task.FromException(new FormatException("second"))).Watched();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(Stopped + "AggregateException: One or more errors occurred. (first) (second). The window keeps running.",
                    detail.Text);

                // A cancelled action ended as it was asked to, so nothing is said of it.
                Task.FromCanceled(new CancellationToken(canceled: true)).Watched();
                Task.CompletedTask.Watched();
                Dispatcher.UIThread.RunJobs();
                Assert.Contains("AggregateException", detail.Text, StringComparison.Ordinal);

                // Running out of memory, as a reservation that could not be met says, is nothing safe to go on from.
                Assert.IsType<InsufficientMemoryException>(Record.Exception(() =>
                {
                    Dispatcher.UIThread.Post(() => throw new InsufficientMemoryException("No room for the layout"));
                    Dispatcher.UIThread.RunJobs();
                }));
            }

            // Without the application's containment, as in every other test, the same errors fail what meets them.
            Assert.IsType<FormatException>(Record.Exception(() =>
            {
                Task.FromException(new FormatException("unseen")).Watched();
                Dispatcher.UIThread.RunJobs();
            }));
            Assert.IsType<InvalidCastException>(Record.Exception(() =>
            {
                Dispatcher.UIThread.Post(() => throw new InvalidCastException("unseen"));
                Dispatcher.UIThread.RunJobs();
            }));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§20.6: an error no action expected is said in the window nearest the person, the owner of a dialog for it, and is not contained once no window can say it")]
    public async Task AnErrorIsSaidInTheWindowNearestThePerson()
    {
        DemoInvestigationResult demo = DemoInvestigation.Create(Path.Combine(folder, "demo"));
        var window = new MainWindow { Width = 1_280, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        TextBlock status = window.GetControl<TextBlock>("CaptureStatus");
        TextBlock detail = window.GetControl<TextBlock>("CaptureDetail");
        InvestigationWindow investigation = window.ShowInvestigation(demo.WorkspacePath);
        await Until(() => investigation.View is not null);
        var dialog = new Window { Title = "A dialog the investigation opened" };
        Task shown = dialog.ShowDialog(investigation);
        Dispatcher.UIThread.RunJobs();
        Window? active = investigation;
        using IDisposable containment = UnexpectedErrors.Contain(Dispatcher.UIThread,
            exception => UnexpectedErrors.SayIn([window, investigation, dialog], candidate => candidate == active, exception));
        try
        {
            // The active window says it, not the one listed first.
            string before = status.Text ?? string.Empty;
            Dispatcher.UIThread.Post(() => throw new InvalidCastException("in the investigation"));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Stopped + "InvalidCastException: in the investigation. The window keeps running.",
                InvestigationStatus(investigation).Text);
            Assert.Equal(before, status.Text);

            // A dialog says nothing of its own, so the window that owns it says it.
            active = dialog;
            Dispatcher.UIThread.Post(() => throw new InvalidCastException("in its dialog"));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Stopped + "InvalidCastException: in its dialog. The window keeps running.", InvestigationStatus(investigation).Text);
            Assert.Equal(before, status.Text);
            dialog.Close();
            await shown;

            // A window that has closed says nothing, so another open one does.
            active = investigation;
            investigation.Close();
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.Post(() => throw new InvalidCastException("after it closed"));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal((Headline, Stopped + "InvalidCastException: after it closed. The window keeps running."), (status.Text, detail.Text));

            // Once no window can say it, it is not contained: it is never passed over unsaid.
            window.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.IsType<InvalidCastException>(Record.Exception(() =>
            {
                Dispatcher.UIThread.Post(() => throw new InvalidCastException("with no window open"));
                Dispatcher.UIThread.RunJobs();
            }));
        }
        finally
        {
            dialog.Close();
            investigation.Close();
            window.Close();
        }
    }

    private static TextBlock InvestigationStatus(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>().Single(block => AutomationProperties.GetName(block) == "Investigation status");

    private static async void FailingHandler()
    {
        await Task.Yield();
        throw new InvalidCastException("A row was read as the wrong kind");
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int wait = 0; wait < 200 && !condition(); wait++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(condition());
    }
}
