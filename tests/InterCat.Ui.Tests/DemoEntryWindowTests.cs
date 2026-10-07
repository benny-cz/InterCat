using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Desktop.Presentation;
using Xunit;

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

            // Offered before any capture, named for what it opens.
            Button offered = window.GetControl<Button>("ExploreDemoButton");
            Assert.True(offered.IsVisible && offered.IsEffectivelyEnabled);
            Assert.Contains("nothing in it was captured", AutomationProperties.GetHelpText(offered), StringComparison.Ordinal);

            // The first time it makes the demo in its own folder; while it does, a second click starts nothing: it is
            // answered at once, with nothing opened.
            Task<InvestigationWindow?> opening = window.ExploreDemoAsync();
            Assert.False(offered.IsEnabled);
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
}
