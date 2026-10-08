using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// §14 M5's demo from the window: one action makes InterCat's generated demo the first time, beside the saved sessions and
/// never among them, opens its investigation, and says what it is; later it opens the same demo, and a folder holding
/// anything else is left as it is.
/// </summary>
public sealed class DemoEntryWindowTests
{
    [AvaloniaFact(DisplayName = "§14: Explore the demo makes the demo once, beside the saved sessions, and opens its investigation saying it was generated")]
    public async Task ExploringTheDemoOpensItsInvestigation()
    {
        string root = Path.Combine(Path.GetTempPath(), "intercat-demo-entry-" + Guid.NewGuid().ToString("N"));
        var window = new MainWindow { Width = 1_080, Height = 700, DemoRoot = root };
        try
        {
            window.Show();

            // Its own folder sits beside the user's sessions folder, never in it, so the saved sessions list never shows it.
            string sessions = DesktopCaptureRunner.DefaultSessionRoot();
            Assert.Equal(Path.Combine(Path.GetDirectoryName(sessions)!, "Demo"), DemoPlace.Root());

            // Offered before any session, named for what it opens, beside what exploring records; no line that says nothing
            // yet takes room.
            Button offered = window.GetControl<Button>("ExploreDemoButton");
            Assert.True(offered.IsVisible && offered.IsEffectivelyEnabled);
            Assert.True(window.GetControl<TextBlock>("CaptureIntro").IsVisible);
            Assert.False(window.GetControl<TextBlock>("CaptureSessionPath").IsVisible);
            Assert.Contains("nothing in it was captured", AutomationProperties.GetHelpText(offered), StringComparison.Ordinal);

            // The first time it makes the demo in its own folder; while it does, a second click starts nothing: it is
            // answered at once, with nothing opened.
            Task<InvestigationWindow?> opening = window.ExploreDemoAsync();
            Assert.False(offered.IsEnabled);
            Assert.False(window.GetControl<MenuItem>("ExploreDemoItem").IsEnabled);
            Task<InvestigationWindow?> second = window.ExploreDemoAsync();
            Assert.True(second.IsCompleted);
            Assert.Null(await second);

            // Then it opens the demo's investigation, saying what it is, and can be clicked again.
            InvestigationWindow first = Assert.IsType<InvestigationWindow>(await opening);
            string workspace = Path.Combine(root, "demo", DemoInvestigation.WorkspaceFileName);
            Assert.Equal(workspace, first.WorkspacePath);
            Assert.True(offered.IsEnabled);
            Assert.StartsWith(DemoInvestigation.Disclosure + " ", window.GetControl<TextBlock>("CaptureDetail").Text,
                StringComparison.Ordinal);
            Assert.Equal(["demo"], Directory.EnumerateDirectories(root).Select(Path.GetFileName));
            string made = InvestigationWorkspace.Read(workspace).WorkspaceId.ToString();
            first.Close();

            // Later it opens the same demo rather than making another.
            InvestigationWindow again = Assert.IsType<InvestigationWindow>(await window.ExploreDemoAsync());
            Assert.Equal(workspace, again.WorkspacePath);
            Assert.Equal(made, InvestigationWorkspace.Read(workspace).WorkspaceId.ToString());
            Assert.Equal(["demo"], Directory.EnumerateDirectories(root).Select(Path.GetFileName));
            again.Close();

            // A demo folder a stopped write left - its sessions without the investigation file - is left as it is, and the
            // next free folder takes the demo.
            string stopped = Path.Combine(root, "stopped");
            Directory.CreateDirectory(Path.Combine(stopped, "demo", DemoInvestigation.ClientFolder));
            File.WriteAllText(Path.Combine(stopped, "demo", DemoInvestigation.WorkspaceFileName + ".making"), "{}");
            window.DemoRoot = stopped;
            InvestigationWindow beside = Assert.IsType<InvestigationWindow>(await window.ExploreDemoAsync());
            Assert.Equal(Path.Combine(stopped, "demo-2", DemoInvestigation.WorkspaceFileName), beside.WorkspacePath);
            Assert.Equal([DemoInvestigation.ClientFolder, DemoInvestigation.WorkspaceFileName + ".making"],
                Directory.EnumerateFileSystemEntries(Path.Combine(stopped, "demo")).Select(Path.GetFileName)
                    .Order(StringComparer.Ordinal));
            beside.Close();

            // When every demo folder holds something else, it says so, names where they are, and opens nothing.
            string full = Path.Combine(root, "full");
            foreach (string name in Enumerable.Range(1, 100).Select(index =>
                index == 1 ? "demo" : string.Create(CultureInfo.InvariantCulture, $"demo-{index}")))
            {
                Directory.CreateDirectory(Path.Combine(full, name));
                File.WriteAllText(Path.Combine(full, name, "notes.txt"), "mine");
            }

            window.DemoRoot = full;
            Assert.Null(await window.ExploreDemoAsync());
            Assert.Equal($"The demo could not be opened: Every demo folder in {full} holds something else; remove one to make "
                + "the demo again.", window.GetControl<TextBlock>("CaptureDetail").Text);
            Assert.True(offered.IsEnabled);

            // While a capture runs, the card holds only what can be done to it.
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "Recording", "Recording."));
            Assert.False(offered.IsVisible);
        }
        finally
        {
            window.Close();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaFact(DisplayName = "§6.1: with a session shown the card drops its first-run guidance and empty lines, so the smallest window's ranked list has three rows in view, and the demo waits in the Investigation menu")]
    public async Task TheDemoLeavesTheRankedListItsRoom()
    {
        string root = Path.Combine(Path.GetTempPath(), "intercat-demo-menu-" + Guid.NewGuid().ToString("N"));
        using var session = new TemporarySession();
        List<ObservationRowV1> rows =
        [
            Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 100 },
            Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe", SessionRelativeTicks = 200 },
            Lifecycle(3, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Tools\third.exe", SessionRelativeTicks = 300 },
            .. Enumerable.Range(0, 21).Select(index => Transfer(10 + index, ObservationKind.Send, AccountingSide.SendSide, 10,
                100 * (1 + index % 3), (ulong)(10 + index)).Between("127.0.0.1:50000", "127.0.0.1:8080")
                with { SessionRelativeTicks = (10 + index) * 100L }),
        ];
        Publish(session.Store, rows);
        var window = new MainWindow { Width = 1_080, Height = 700, DemoRoot = root };
        try
        {
            window.Show();
            window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
                Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
            for (int pass = 0; pass < 4; pass++)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                _ = window.CaptureRenderedFrame();
            }

            // The card no longer offers the demo or says what exploring records, and keeps no line that says nothing, so it
            // shows all it holds unscrolled and leaves the ranked list room for each of the three processes' rows.
            Assert.False(window.GetControl<Button>("ExploreDemoButton").IsVisible);
            Assert.False(window.GetControl<TextBlock>("CaptureEyebrow").IsVisible);
            Assert.False(window.GetControl<TextBlock>("CaptureIntro").IsVisible);
            Assert.False(window.GetControl<TextBlock>("CaptureLatency").IsVisible);

            // What the next capture keeps is still chosen there, before it starts.
            Assert.True(window.GetControl<Grid>("CaptureKeepRow").IsVisible);
            Control actions = window.GetControl<Control>("RailActions");
            Assert.True(actions.DesiredSize.Height <= actions.MaxHeight,
                $"The card wants {actions.DesiredSize.Height:F0} px of the {actions.MaxHeight:F0} it may have.");
            ListBox list = window.GetControl<ListBox>("RungList");
            Assert.Equal(3, list.ItemCount);
            Assert.All(Enumerable.Range(0, 3), index => Assert.NotNull(list.ContainerFromIndex(index)));

            // The Investigation menu still offers it, described as the card did, and it opens beside the session.
            MenuItem item = window.GetControl<MenuItem>("ExploreDemoItem");
            Assert.True(item.IsVisible && item.IsEnabled);
            Assert.Equal("Explore the demo", item.Header);
            Assert.Contains("nothing in it was captured", AutomationProperties.GetHelpText(item), StringComparison.Ordinal);
            InvestigationWindow demo = Assert.IsType<InvestigationWindow>(await window.ExploreDemoAsync());
            Assert.Equal(Path.Combine(root, "demo", DemoInvestigation.WorkspaceFileName), demo.WorkspacePath);
            Assert.True(item.IsEnabled);
            demo.Close();
        }
        finally
        {
            window.Close();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
