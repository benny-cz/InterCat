using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Capture.Journal.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// The main window through a capture and a session opened, by keyboard alone (R15): the keyboard goes on to what the window
/// offers as the controls that had it hide or wait disabled - Stop while a capture records, the ranked table once its
/// session is shown - and never from where the person put it.
/// </summary>
public sealed class CaptureKeyboardTests
{
    [AvaloniaFact(DisplayName = "R15: a capture gives Stop the keyboard once it records, and the ranked table once Stop is pressed and the session saved")]
    public async Task ACaptureHandsTheKeyboardOn()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows(0));
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            Dispatch();
            Button start = window.GetControl<Button>("StartExploringButton");
            Button stop = window.GetControl<Button>("StopCaptureButton");
            ListBox rail = window.GetControl<ListBox>("RungList");
            Assert.True(start.IsFocused, Focused(window));

            // Start exploring hides as the capture starts; once it records, Stop has the keyboard, and keeps it as the first
            // view arrives.
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Starting, "Starting", "Starting."));
            Dispatch();
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "Recording", "Recording."));
            Dispatch();
            Assert.True(stop.IsFocused, Focused(window));
            window.ApplyCaptureUpdate(Published(session, CaptureUiPhase.Recording));
            await Assert.IsType<WorkspaceViewModel>(window.DataContext).LayoutReady;
            Dispatch();
            Assert.True(stop.IsFocused, Focused(window));

            // Enter on Stop waits it disabled while the session is saved: the ranked table has the keyboard then, on its
            // first row, and once the session is saved.
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatch();
            Assert.False(stop.IsEnabled);
            Assert.Same(rail.ContainerFromIndex(0), window.FocusManager?.GetFocusedElement());
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Finishing, "Stopping", "Stopping."));
            Dispatch();
            Publish(session.Store, Rows(100));
            window.ApplyCaptureUpdate(Published(session, CaptureUiPhase.Complete));
            await Assert.IsType<WorkspaceViewModel>(window.DataContext).LayoutReady;
            Dispatch();
            Assert.False(stop.IsVisible);
            Assert.True(rail.IsKeyboardFocusWithin, Focused(window));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: a recording's new view leaves the keyboard where the person put it - the ranked table, the search - never moving it to Stop")]
    public async Task ARecordingsNewViewLeavesTheKeyboardWhereItWas()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows(0));
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "Recording", "Recording."));
            Dispatch();
            window.ApplyCaptureUpdate(Published(session, CaptureUiPhase.Recording));
            await Assert.IsType<WorkspaceViewModel>(window.DataContext).LayoutReady;
            Dispatch();
            Button stop = window.GetControl<Button>("StopCaptureButton");
            ListBox rail = window.GetControl<ListBox>("RungList");
            Assert.True(stop.IsFocused, Focused(window));

            // The person takes the keyboard to the ranked table; a newer generation rebuilds its rows, and the new rows get
            // it back rather than Stop.
            Assert.True(Assert.IsAssignableFrom<Control>(rail.ContainerFromIndex(0)).Focus(NavigationMethod.Tab));
            Dispatch();
            Publish(session.Store, Rows(100));
            window.ApplyCaptureUpdate(Published(session, CaptureUiPhase.Recording));
            await Assert.IsType<WorkspaceViewModel>(window.DataContext).LayoutReady;
            Dispatch();
            Assert.True(rail.IsKeyboardFocusWithin, Focused(window));

            // The search box keeps it as the next view arrives.
            TextBox search = window.GetControl<TextBox>("SearchBox");
            Assert.True(search.Focus(NavigationMethod.Tab));
            Publish(session.Store, Rows(200));
            window.ApplyCaptureUpdate(Published(session, CaptureUiPhase.Recording));
            await Assert.IsType<WorkspaceViewModel>(window.DataContext).LayoutReady;
            Dispatch();
            Assert.True(search.IsFocused, Focused(window));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: a recording's new view rebuilding the inspector under the keyboard keeps it in the inspector, never on Stop")]
    public async Task ARebuiltInspectorKeepsTheKeyboard()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows(0));
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            Dispatch();
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "Recording", "Recording."));
            Dispatch();
            window.ApplyCaptureUpdate(Published(session, CaptureUiPhase.Recording));
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            Dispatch();

            // From the ranked table the client's process is opened, a moment stepped to on the timeline, and its records
            // read in the inspector.
            ListBox rail = window.GetControl<ListBox>("RungList");
            Assert.True(Assert.IsAssignableFrom<Control>(rail.ContainerFromIndex(0)).Focus(NavigationMethod.Tab));
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await workspace.TimelineDetailReady;
            Dispatch();
            window.KeyPressQwerty(PhysicalKey.F6, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.F6, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.BracketRight, RawInputModifiers.None);
            await workspace.IntervalReady;
            await workspace.CellRecordsReady;
            Dispatch();
            window.KeyPressQwerty(PhysicalKey.F6, RawInputModifiers.None);
            Dispatch();
            Control inspector = window.GetControl<Control>("Inspector");
            Assert.True(inspector.IsKeyboardFocusWithin, Focused(window));

            // A newer generation rebuilds what the inspector lists: the keyboard stays in the inspector, and never on Stop,
            // where the next Enter would stop the capture.
            Publish(session.Store, Rows(100));
            window.ApplyCaptureUpdate(Published(session, CaptureUiPhase.Recording));
            await Assert.IsType<WorkspaceViewModel>(window.DataContext).LayoutReady;
            for (int wait = 0; wait < 10; wait++)
            {
                Dispatch();
                await Task.Delay(10);
            }

            Assert.False(window.GetControl<Button>("StopCaptureButton").IsFocused, Focused(window));
            Assert.True(inspector.IsKeyboardFocusWithin, Focused(window));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: Forward and Restore defaults, which go once pressed, give the ranked table the keyboard")]
    public async Task ButtonsThatGoGiveTheRankedTableTheKeyboard()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows(0));
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            Dispatch();
            Assert.True(await window.OpenSessionAsync(session.Path));
            var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            await workspace.LayoutReady;
            Dispatch();
            ListBox rail = window.GetControl<ListBox>("RungList");

            // Down a rung and back up: Forward goes once it has gone forward, and the rung's table has the keyboard.
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatch();
            Assert.Equal("L1 · GROUP", workspace.LevelBadge);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatch();
            Assert.Equal("L0 · MACHINE", workspace.LevelBadge);
            Button forward = window.GetControl<Button>("ForwardButton");
            Assert.True(forward.Focus(NavigationMethod.Tab));
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatch();
            Assert.Equal("L1 · GROUP", workspace.LevelBadge);
            Assert.False(forward.IsEffectivelyVisible);
            Assert.True(rail.IsKeyboardFocusWithin, Focused(window));

            // A setting changed offers Restore defaults, which goes once it has restored them.
            workspace.PerSecond = true;
            Dispatch();
            Button restore = window.GetControl<Button>("RestoreDefaultsButton");
            Assert.True(restore.Focus(NavigationMethod.Tab));
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            for (int wait = 0; wait < 10 && restore.IsEffectivelyVisible; wait++)
            {
                Dispatch();
                await Task.Delay(10);
            }

            Dispatch();
            Assert.False(restore.IsEffectivelyVisible);
            Assert.True(rail.IsKeyboardFocusWithin, Focused(window));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: a capture's update gives the window no keyboard while another window is the one in use, and Stop has it once this one is")]
    public void AWindowNotInUseTakesNoKeyboard()
    {
        // Shown but not activated, as when a dialog, another of InterCat's windows or another application is the one a person
        // types into: an update there gives Stop no keyboard, so Enter typed there never stops the capture.
        var window = new MainWindow { Width = 1080, Height = 700, ShowActivated = false };
        window.Show();
        try
        {
            Dispatch();
            Assert.False(window.IsActive);
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "Recording", "Recording."));
            Dispatch();
            Button stop = window.GetControl<Button>("StopCaptureButton");
            Assert.True(stop.IsEffectivelyEnabled);
            Assert.Null(window.FocusManager?.GetFocusedElement());

            // Once this window is the one in use, Stop has the keyboard.
            window.Activate();
            Dispatch();
            Assert.True(stop.IsFocused, Focused(window));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: a saved session opened gives its ranked table the keyboard; one that does not open gives it back to Open saved session")]
    public async Task ASessionOpenedGivesItsRankedTableTheKeyboard()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows(0));
        using var empty = new TemporaryDirectory();
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            Dispatch();
            Button open = window.GetControl<Button>("OpenSavedSessionButton");
            Assert.True(open.Focus(NavigationMethod.Tab));

            // Open saved session waits disabled while it opens; a folder holding no session leaves it its keyboard back.
            Assert.False(await window.OpenSessionAsync(empty.Path));
            Dispatch();
            Assert.True(open.IsFocused, Focused(window));

            // A session opened gives its ranked table the keyboard, on its first row.
            Assert.True(await window.OpenSessionAsync(session.Path));
            await Assert.IsType<WorkspaceViewModel>(window.DataContext).LayoutReady;
            Dispatch();
            Assert.Same(window.GetControl<ListBox>("RungList").ContainerFromIndex(0), window.FocusManager?.GetFocusedElement());

            // With a session shown, one that does not open still gives Open saved session its keyboard back, rather than
            // move it to the ranked table.
            Assert.True(open.Focus(NavigationMethod.Tab));
            Assert.False(await window.OpenSessionAsync(empty.Path));
            Dispatch();
            Assert.True(open.IsFocused, Focused(window));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: Update to newest and Follow live give the ranked table the keyboard as the banner they sit in goes")]
    public async Task TheHeldBannersButtonsGiveTheRankedTableTheKeyboard()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows(0));
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            Dispatch();
            window.ApplyCaptureUpdate(Published(session, CaptureUiPhase.Recording));
            await Assert.IsType<WorkspaceViewModel>(window.DataContext).LayoutReady;
            Dispatch();
            ListBox rail = window.GetControl<ListBox>("RungList");
            Border banner = window.GetControl<Border>("HeldBanner");

            // F pauses the view; the next generation waits in the banner, and Enter on Update to newest shows it: the banner
            // goes, and the ranked table of the view it showed has the keyboard.
            window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.None);
            Publish(session.Store, Rows(100));
            window.ApplyCaptureUpdate(Published(session, CaptureUiPhase.Recording));
            Dispatch();
            Assert.True(banner.IsVisible);
            Button update = banner.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, "Update to newest (F5)"));
            Assert.True(update.Focus(NavigationMethod.Tab));

            // A newer generation arriving meanwhile is said in the banner, which keeps the keyboard on its button.
            Publish(session.Store, Rows(150));
            window.ApplyCaptureUpdate(Published(session, CaptureUiPhase.Recording));
            Dispatch();
            Assert.True(update.IsFocused, Focused(window));
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await Assert.IsType<WorkspaceViewModel>(window.DataContext).LayoutReady;
            Dispatch();
            Assert.False(banner.IsVisible);
            Assert.True(rail.IsKeyboardFocusWithin, Focused(window));

            // Still paused, the next generation waits again; Enter on Follow live follows it, and the same holds.
            Publish(session.Store, Rows(200));
            window.ApplyCaptureUpdate(Published(session, CaptureUiPhase.Recording));
            Dispatch();
            Button follow = window.GetControl<Button>("HeldFollowButton");
            Assert.True(follow.IsEffectivelyVisible);
            Assert.True(follow.Focus(NavigationMethod.Tab));
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await Assert.IsType<WorkspaceViewModel>(window.DataContext).LayoutReady;
            Dispatch();
            Assert.False(banner.IsVisible);
            Assert.True(rail.IsKeyboardFocusWithin, Focused(window));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: a capture that cannot start here gives Start exploring the keyboard back")]
    public async Task ACaptureThatCannotStartGivesStartTheKeyboardBack()
    {
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            Dispatch();
            Button start = window.GetControl<Button>("StartExploringButton");
            Assert.True(start.IsFocused, Focused(window));
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await Assert.IsAssignableFrom<Task>(window.CaptureRun).WaitAsync(TimeSpan.FromSeconds(60));
            Dispatch();
            Assert.True(start.IsVisible && start.IsEnabled);
            Assert.True(start.IsFocused, Focused(window));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: a capture stopped before it published anything gives Start exploring the keyboard back")]
    public void ACaptureThatPublishedNothingGivesStartTheKeyboardBack()
    {
        var window = new MainWindow { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            Dispatch();
            Button start = window.GetControl<Button>("StartExploringButton");
            Button stop = window.GetControl<Button>("StopCaptureButton");
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Starting, "Starting", "Starting."));
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "Recording", "Recording."));
            Dispatch();
            Assert.True(stop.IsFocused, Focused(window));

            // Stop pressed, with no view to give the keyboard to; once the capture ends, Start exploring has it again.
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Finishing, "Stopping", "Stopping."));
            Dispatch();
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Nothing recorded", "The capture published nothing."));
            Dispatch();
            Assert.True(start.IsFocused, Focused(window));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "R15: the demo's investigation closed gives Explore the demo, which waited disabled while it opened, the keyboard back")]
    public async Task TheDemoClosedGivesItsButtonTheKeyboardBack()
    {
        using var root = new TemporaryDirectory();
        var window = new MainWindow { Width = 1080, Height = 700, DemoRoot = root.Path };
        window.Show();
        try
        {
            Dispatch();
            Button demo = window.GetControl<Button>("ExploreDemoButton");
            Assert.True(demo.Focus(NavigationMethod.Tab));
            InvestigationWindow opened = Assert.IsType<InvestigationWindow>(await window.ExploreDemoAsync());
            for (int wait = 0; wait < 250 && opened.View is null; wait++)
            {
                Dispatch();
                await Task.Delay(10);
            }

            Dispatch();
            Assert.False(demo.IsFocused);
            opened.Close();
            window.Activate();
            Dispatch();
            Assert.True(demo.IsFocused, Focused(window));
        }
        finally
        {
            window.Close();
        }
    }

    private static CaptureUiUpdate Published(TemporarySession session, CaptureUiPhase phase) => new(
        phase, phase == CaptureUiPhase.Complete ? "Saved" : "Recording", "A published generation.", SessionPath: session.Path,
        Overview: SessionOverviewProjector.Project(session.Store));

    /// <summary>A client's send to a server and the server's receive, from <paramref name="at"/> on.</summary>
    private static ObservationRowV1[] Rows(long at) =>
    [
        Transfer(10 + at, ObservationKind.Send, AccountingSide.SendSide, 64, 100, (ulong)(10 + at))
            .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 1_000 + at },
        Transfer(11 + at, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, (ulong)(11 + at))
            .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 1_100 + at },
    ];

    private static string Focused(Window window) => window.FocusManager?.GetFocusedElement() switch
    {
        null => "the keyboard is on nothing",
        Control control => $"the keyboard is on {control.GetType().Name} {control.Name} (in the window: {window.IsVisualAncestorOf(control)})",
        { } other => "the keyboard is on " + other,
    };

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
