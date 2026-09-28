using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using InterCat.Application;
using InterCat.Capture.Journal.Tests;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// An investigation in the window (ADR-038, M4): its sessions where they stand, their hosts and their time, a moved one
/// relinked, and a member opened in InterCat's own window.
/// </summary>
public sealed class InvestigationWindowTests
{
    [AvaloniaFact(DisplayName = "R22: an investigation window lists its sessions where they stand, relinks one that moved, and opens one")]
    public async Task AnInvestigationWindowListsItsSessions()
    {
        using var root = new TemporaryDirectory();
        string alpha = Session(root.Path, "alpha");
        string beta = Session(root.Path, "beta");
        string workspace = Path.Combine(root.Path, "case" + InvestigationWorkspace.Extension);
        InvestigationWorkspace.Create(workspace, Committed);
        Guid a = InvestigationWorkspace.Add(workspace, alpha, Committed).SessionId;
        Guid b = InvestigationWorkspace.Add(workspace, beta, Committed).SessionId;
        InvestigationWorkspace.Align(workspace, b, 2_000_000_000, a, 5_000_000_000, 500_000, 50, null, Committed);
        string moved = Path.Combine(root.Path, "archive", "beta");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        Directory.Move(beta, moved);

        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            // The rail offers it below the saved sessions, whole at the minimum window.
            AssertInside(main.GetControl<Button>("InvestigationButton"), main.GetControl<Control>("Rail"), main);

            InvestigationWindow window = main.ShowInvestigation(workspace);
            WaitFor(() => window.View is not null);
            InvestigationView view = window.View!;
            CultureInfo culture = CultureInfo.CurrentCulture;
            Assert.Equal([$"Session {Short(a)}, present", $"Session {Short(b)}, missing"], view.Members.Select(row => row.Title));
            Assert.Equal("Its clock is the investigation's time", view.Members[0].Time);
            Assert.Equal($"Aligned by a person: its {2m.ToString("0.000######", culture)} s is the reference's "
                + $"{5m.ToString("0.000######", culture)} s, within ±{500m.ToString("N0", culture)} µs, drifting at most 50 ppm",
                view.Members[1].Time);
            Assert.Contains("moved or removed", view.Members[1].Reason, StringComparison.Ordinal);
            Assert.False(view.Members[1].HoldsItsCapture);
            Assert.StartsWith("1 session is not where it was last found", Named<TextBlock>(window, "Investigation status").Text,
                StringComparison.Ordinal);

            // Each session is named for the ear in a sentence, never by its record's fields.
            ListBox list = Named<ListBox>(window, "Sessions of this investigation; press Enter to open the selected one");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(view.Members[1].AccessibleName, AutomationProperties.GetName(list.ContainerFromIndex(1)!));
            Assert.EndsWith("Relink it to open it.", view.Members[1].AccessibleName, StringComparison.Ordinal);
            Save(window, "investigation-window.png");

            // A moved session is relinked where it is now; a folder that holds no session is refused, and says why.
            list.SelectedIndex = 1;
            Assert.False(Named<Button>(window, "Open the selected session in InterCat").IsEnabled);
            await window.RelinkAsync(b, moved);
            WaitFor(() => window.View!.Members[1].State == WorkspaceMemberState.Present);
            Directory.CreateDirectory(Path.Combine(root.Path, "empty"));
            await window.AddAsync([Path.Combine(root.Path, "empty")]);
            WaitFor(() => Named<TextBlock>(window, "Investigation status").Text?.Contains("was not added", StringComparison.Ordinal) == true);

            // A member opens in InterCat's own window, as a saved session would.
            list.SelectedIndex = 0;
            Button open = Named<Button>(window, "Open the selected session in InterCat");
            Assert.True(open.IsEnabled);
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => main.GetControl<TextBlock>("CaptureSessionPath").Text == alpha);
            Assert.Equal("Saved session open", main.GetControl<TextBlock>("CaptureStatus").Text);
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R22: a new investigation starts empty, and an existing file is opened, never written over")]
    public void ANewInvestigationStartsEmpty()
    {
        using var root = new TemporaryDirectory();
        string workspace = Path.Combine(root.Path, "fresh" + InvestigationWorkspace.Extension);
        var main = new MainWindow { Width = 1080, Height = 700 };
        main.Show();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(workspace, create: true);
            WaitFor(() => window.View is not null);
            Assert.Empty(window.View!.Members);
            Assert.Equal("No session yet: add the sessions this investigation covers.", window.View.Summary);
            Assert.Equal("A new investigation: add the sessions it covers.", Named<TextBlock>(window, "Investigation status").Text);
            string written = File.ReadAllText(workspace);
            window.Close();

            // Asked to make one that exists, the window opens it as it is.
            InvestigationWindow again = main.ShowInvestigation(workspace, create: true);
            WaitFor(() => again.View is not null);
            Assert.Equal(written, File.ReadAllText(workspace));
            again.Close();
        }
        finally
        {
            main.Close();
        }
    }

    private static string Session(string root, string name)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory), Guid.NewGuid(), "investigation-window-tests");
        _ = Publish(
            store,
            [Lifecycle(100, ObservationKind.Create, 400, 1) with { SessionRelativeTicks = 100 }],
            capture: CaptureId.New(),
            clock: ClockFor(ClockId.New(), "lab-" + name));
        store.ReleaseSegmentReaders();
        return directory;
    }

    private static string Short(Guid identity) => identity.ToString("N")[..8];

    private static void WaitFor(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Assert.True(condition());
    }

    private static T Named<T>(Window window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(control => AutomationProperties.GetName(control) == name);

    private static void AssertInside(Control inner, Control outer, Window window)
    {
        Rect bounds = new(inner.TranslatePoint(default, window)!.Value, inner.Bounds.Size);
        Rect container = new(outer.TranslatePoint(default, window)!.Value, outer.Bounds.Size);
        Assert.True(
            container.Contains(bounds.TopLeft) && container.Contains(bounds.BottomRight - new Vector(0.5, 0.5)),
            $"{inner.Name} at {bounds} is not inside {outer.Name} at {container}.");
    }

    private static void Save(Window window, string name)
    {
        Avalonia.Media.Imaging.WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        string directory = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(directory);
        frame!.Save(Path.Combine(directory, name));
    }
}
