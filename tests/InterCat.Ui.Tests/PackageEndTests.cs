using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Storage;
using Xunit;

namespace InterCat.Ui.Tests;

/// <summary>
/// A package stopped by its own button partway, or one that cannot be made, saves nothing and leaves no unsaved folder;
/// the card, or the investigation's status line, says which, and the button offers the package again (§8.4, §11.3).
/// </summary>
public sealed class PackageEndTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "InterCat.Ui.Tests.PackageEnd", Guid.NewGuid().ToString("N"));

    public PackageEndTests() => Directory.CreateDirectory(folder);

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

    [AvaloniaFact(DisplayName = "§11.3: Cancel packaging stops a redacted package or an exact copy partway, removes its unsaved folder, and the card says nothing was saved")]
    public async Task CancelPackagingStopsAPackagePartway()
    {
        DemoInvestigationResult demo = DemoInvestigation.Create(Path.Combine(folder, "demo"));
        string target = Directory.CreateDirectory(Path.Combine(folder, "shared")).FullName;
        var window = new MainWindow { Width = 1_280, Height = 800 };
        window.Show();
        using var redacted = new WorkHold();
        using var copied = new WorkHold();
        window.RedactedPackager = (store, destination, now, interval, progress, cancellation) =>
            RedactedSessionPackage.Create(store, destination, now, interval,
                redacted.On(progress, update => update.Stage == RedactedPackageStage.Writing, cancellation), cancellation);
        window.OriginalPackager = (store, destination, progress, cancellation) => OriginalEvidencePackage.Create(
            store, destination, copied.On(progress, update => update.Stage == OriginalPackageStage.Copying, cancellation),
            cancellation);
        try
        {
            Assert.True(await window.OpenSessionAsync(demo.ClientSession));
            Dispatch();
            string headline = Status(window)!;
            Button share = window.GetControl<Button>("SharePackageButton");
            Button original = window.GetControl<Button>("ShareOriginalButton");

            // The redacted package's own button stops it once it has begun writing.
            Task<RedactedSessionPackageResult?> saving = window.WriteRedactedPackageAsync(
                demo.ClientSession, Path.Combine(target, "redacted"));
            await redacted.Reached;
            Assert.Equal("Cancel packaging", share.Content);
            share.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            redacted.Release();
            Assert.Null(await saving.WaitAsync(TimeSpan.FromSeconds(30)));
            Dispatch();
            Assert.Equal((headline, "Packaging cancelled. Nothing was saved, and the session is unchanged."),
                (Status(window), Detail(window)));
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
            Assert.Equal("Share redacted session…", share.Content);

            // So does an exact copy's.
            Guid session = SessionStore.OpenExisting(LocalOwnedDirectory.Open(demo.ClientSession)).Current!.SessionId;
            Task<OriginalEvidencePackageResult?> copying = window.WriteOriginalPackageAsync(
                demo.ClientSession, session, Path.Combine(target, "copy"));
            await copied.Reached;
            Assert.Equal("Cancel packaging", original.Content);
            original.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            copied.Release();
            Assert.Null(await copying.WaitAsync(TimeSpan.FromSeconds(30)));
            Dispatch();
            Assert.Equal((headline, "Copying cancelled. Nothing was saved, and the session is unchanged."),
                (Status(window), Detail(window)));
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
            Assert.Equal("Share original session…", original.Content);
        }
        finally
        {
            redacted.Release();
            copied.Release();
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§11.3: a package that cannot be made says why, saves nothing, and the card's headline is as it was")]
    public async Task APackageThatCannotBeMadeSaysWhy()
    {
        DemoInvestigationResult demo = DemoInvestigation.Create(Path.Combine(folder, "demo"));
        string target = Directory.CreateDirectory(Path.Combine(folder, "shared")).FullName;
        var window = new MainWindow { Width = 1_280, Height = 800 };
        window.RedactedPackager = (_, _, _, _, _, _) => throw new IOException("There is not enough space on the disk.");
        window.OriginalPackager = (_, _, _, _) => throw new UnauthorizedAccessException("Access to the folder is denied.");
        window.Show();
        try
        {
            Assert.True(await window.OpenSessionAsync(demo.ClientSession));
            Dispatch();
            string headline = Status(window)!;
            Assert.Null(await window.WriteRedactedPackageAsync(demo.ClientSession, Path.Combine(target, "redacted")));
            Assert.Equal((headline, "Could not create the redacted package: There is not enough space on the disk."),
                (Status(window), Detail(window)));
            Guid session = SessionStore.OpenExisting(LocalOwnedDirectory.Open(demo.ClientSession)).Current!.SessionId;
            Assert.Null(await window.WriteOriginalPackageAsync(demo.ClientSession, session, Path.Combine(target, "copy")));
            Assert.Equal((headline, "Could not save the original session: Access to the folder is denied."),
                (Status(window), Detail(window)));
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
            Assert.Equal(("Share redacted session…", "Share original session…"),
                (window.GetControl<Button>("SharePackageButton").Content, window.GetControl<Button>("ShareOriginalButton").Content));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "§8.4: Cancel packaging stops an investigation's package partway, removes its unsaved folder, and its status says nothing was saved")]
    public async Task CancelPackagingStopsAnInvestigationsPackage()
    {
        DemoInvestigationResult demo = DemoInvestigation.Create(Path.Combine(folder, "demo"));
        string target = Directory.CreateDirectory(Path.Combine(folder, "packages")).FullName;
        var main = new MainWindow { Width = 1_280, Height = 800 };
        main.Show();
        using var hold = new WorkHold();
        try
        {
            InvestigationWindow window = main.ShowInvestigation(demo.WorkspacePath);
            window.Packager = (workspace, destination, only, progress, cancellation) => InvestigationPackage.Create(
                workspace, destination, only, hold.On(progress, _ => true, cancellation), cancellation);
            await Until(() => window.View is { Members.Count: 2 });
            Task<InvestigationPackageResult?> packaging = window.WritePackageAsync(Path.Combine(target, "case-package"), null);
            await hold.Reached;
            Button package = Named<Button>(window, "Cancel packaging; nothing is saved");
            Assert.Equal("Cancel packaging", package.Content);
            package.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            hold.Release();
            Assert.Null(await packaging.WaitAsync(TimeSpan.FromSeconds(30)));
            Dispatch();
            Assert.Equal("Packaging cancelled. Nothing was saved, and the investigation and its sessions are unchanged.",
                StatusOf(window));
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
            Assert.Equal("Package…", package.Content);
        }
        finally
        {
            hold.Release();
            main.Close();
        }
    }

    private static string? Status(MainWindow window) => window.GetControl<TextBlock>("CaptureStatus").Text;

    private static string? Detail(MainWindow window) => window.GetControl<TextBlock>("CaptureDetail").Text;

    private static T Named<T>(Window window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<T>()
            .Single(control => AutomationProperties.GetName(control) == name
                || ControlAutomationPeer.CreatePeerForElement(control).GetName() == name);

    private static string? StatusOf(InvestigationWindow window) =>
        window.GetVisualDescendants().OfType<TextBlock>()
            .Single(block => AutomationProperties.GetName(block) == "Investigation status").Text;

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
