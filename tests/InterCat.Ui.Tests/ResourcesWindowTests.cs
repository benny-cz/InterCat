using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.CaptureBroker;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// The session's resources in the window's inspector while nothing is chosen on the machine rung (M6): where a keyboard and
/// a screen reader reach them, by the object directory each name lies in, idle sockets last, and a resource arrowed onto is
/// what the evidence card counts.
/// </summary>
public sealed class ResourcesWindowTests
{
    [AvaloniaFact(DisplayName = "§6.3/R15: with nothing chosen the inspector lists the session's resources by namespace, each read whole, and arrowing onto one makes it what the evidence card counts, nothing in the window unheard")]
    public async Task TheInspectorListsTheResources()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = UnixSockets();
        Publish(session.Store, [.. rows.Select(row => row with { SessionRelativeTicks = row.NativeTicks * 100 })], fields: fields);
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        window.ApplyCaptureUpdate(new(CaptureUiPhase.Recording, "Recording", "A published generation.", SessionPath: session.Path,
            Overview: SessionOverviewProjector.Project(session.Store)));
        Dispatch();
        var workspace = Assert.IsType<WorkspaceViewModel>(window.DataContext);
        await workspace.LayoutReady;

        // Nothing chosen: the named object first, then the sockets no record named, the idle one last.
        ListBox resources = window.GetControl<ListBox>("ResourceList");
        Assert.True(resources.IsEffectivelyVisible);
        Assert.Equal("5 resources by namespace, 1 idle listed last", window.GetControl<TextBlock>("ResourcesHeadingText").Text);
        Assert.Equal("5 resources by namespace, 1 idle listed last: the resources this machine's processes held, by the object "
            + "directory each name lies in; E on one lists its records", AutomationProperties.GetName(resources));
        Assert.Equal(["Ready", @"Synchronization object · \Sessions\1\BaseNamedObjects · held by server.exe · PID 100 · 2 records"],
            Texts(resources).Take(2));
        Assert.Equal("Unix socket 5, unnamed · held by server.exe · PID 100 · 3 records · idle. Press E to list its records.",
            AutomationProperties.GetName(resources.ContainerFromIndex(4)!));
        Assert.False(window.GetControl<Button>("ResourcesToggleButton").IsEffectivelyVisible);

        // Nothing in the window goes unheard while the list shows.
        List<string> problems = AccessibilityAuditTests.Unheard(window, out _);
        Assert.True(problems.Count == 0, string.Join("\n", problems));

        // The keyboard on the object, Down arrows onto socket 6, which the evidence card then counts.
        Assert.True(resources.ContainerFromIndex(0)!.Focus());
        Dispatch();
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        Dispatch();
        Assert.Equal("resource:hub:socket:6", workspace.ChosenResourceRow?.Key);
        Assert.Equal(("Selected resource", "5 records of its socket · 1 send, 1 receive"), (workspace.EvidenceHeading, workspace.EvidenceSummary));
        Save(window, "resources-1456x939.png");
        window.Close();
    }

    /// <summary>Keeps what the window drew beside the tests' other renders, for a person to look at.</summary>
    private static void Save(Window window, string name)
    {
        Dispatch();
        _ = window.CaptureRenderedFrame();
        Dispatch();
        string directory = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(directory);
        window.CaptureRenderedFrame()!.Save(Path.Combine(directory, name));
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    /// <summary>The words a list's rows show, row by row and line by line.</summary>
    private static string[] Texts(ItemsControl list) =>
        [.. list.GetRealizedContainers().SelectMany(container => container.GetVisualDescendants().OfType<TextBlock>())
            .Select(text => text.Text ?? string.Empty)];
}
