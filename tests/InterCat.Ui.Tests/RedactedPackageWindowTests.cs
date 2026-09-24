using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// §11.3 from the Desktop: an open session is shared as a redacted package, and a package opened in the window says it
/// is one wherever its names, IDs and records are read.
/// </summary>
public sealed class RedactedPackageWindowTests
{
    private static readonly Guid PrivateProvider = Guid.Parse("c0de5ec2-0000-4000-8000-00000000abcd");

    [AvaloniaFact(DisplayName = "11.3: the window packages the open session and opens the package as a labelled package")]
    public async Task TheWindowPackagesAndReopensASession()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 1)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 1_000 },
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 2)
                .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 1_100, ProviderId = PrivateProvider },
        ]);
        string destination = MainWindow.NewPackageDirectory(Path.Combine(Path.GetTempPath(), "InterCat.Ui.Tests.Packages"),
            DateTimeOffset.Now);
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            Button share = window.GetControl<Button>("SharePackageButton");
            Assert.False(share.IsEnabled);

            // While a capture is live, a package would hold an unfinished session: the action waits for the stop.
            window.ApplyCaptureUpdate(Update(session, CaptureUiPhase.Recording));
            Dispatch();
            Assert.False(share.IsEnabled);
            window.ApplyCaptureUpdate(Update(session, CaptureUiPhase.Complete), forceOverview: true);
            Dispatch();
            Assert.True(share.IsEnabled);

            RedactedSessionPackageResult? result = await window.WriteRedactedPackageAsync(session.Path, destination);
            Dispatch();
            Assert.NotNull(result);
            Assert.True(Directory.Exists(destination));
            Assert.Equal("Redacted session package saved", window.GetControl<TextBlock>("CaptureStatus").Text);
            Assert.Contains(destination, window.GetControl<TextBlock>("CaptureDetail").Text, StringComparison.Ordinal);
            Assert.Equal("Share redacted session…", share.Content);

            Assert.True(await window.OpenSessionAsync(destination));
            Dispatch();
            var package = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            Assert.StartsWith("Redacted package", package.Snapshot.Title, StringComparison.Ordinal);
            Assert.NotNull(package.Snapshot.Redaction);
            Assert.StartsWith(OverviewWorkspace.RedactedDisclosure, package.WorkspaceDisclosure, StringComparison.Ordinal);
            Assert.Equal("Redacted session package open", window.GetControl<TextBlock>("CaptureStatus").Text);
            Assert.Contains("pseudonyms", window.GetControl<TextBlock>("CaptureDetail").Text, StringComparison.Ordinal);
            await package.LayoutReady;
            Dispatch();
            Save(window, "redacted-package-1456x939.png");

            // In the evidence inspector a pseudonymized provider is called one; a public provider keeps its name.
            Assert.True(package.ShowEvidence());
            await package.EvidenceReady;
            Dispatch();
            string[] sources = [.. package.RungRows.Select(row =>
            {
                package.SelectedRung = row;
                return package.SelectedEvidenceFields.Single(field => field.Label == "Source").Value;
            })];
            Assert.Contains(sources, source => source.StartsWith("Microsoft-Windows-Kernel-Network", StringComparison.Ordinal));
            Assert.Contains(sources, source => source.StartsWith("provider-", StringComparison.Ordinal)
                && source.Contains("(pseudonym)", StringComparison.Ordinal));
            Assert.Equal("Open synthetic record (Enter)", package.OriginalRecordLabel);
            Assert.Equal("Redacted package", window.GetControl<TextBlock>("HealthStateText").Text);
            Assert.DoesNotContain(sources, source => source.Contains(PrivateProvider.ToString("D"), StringComparison.Ordinal));
            Dispatch();
            Save(window, "redacted-package-evidence-1456x939.png");
        }
        finally
        {
            window.Close();
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    [AvaloniaFact(DisplayName = "11.3: a new package folder never reuses an existing name")]
    public void PackageFoldersAreNumberedRatherThanReused()
    {
        string parent = Path.Combine(Path.GetTempPath(), "InterCat.Ui.Tests.Packages", Guid.NewGuid().ToString("N"));
        var now = new DateTimeOffset(2026, 9, 24, 18, 30, 5, TimeSpan.Zero);
        try
        {
            string first = MainWindow.NewPackageDirectory(parent, now);
            Assert.Equal(Path.Combine(parent, "intercat-redacted-session-20260924-183005"), first);
            Directory.CreateDirectory(first);
            Assert.Equal(first + "-2", MainWindow.NewPackageDirectory(parent, now));
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        }
    }

    private static CaptureUiUpdate Update(TemporarySession session, CaptureUiPhase phase) => new(
        phase, phase == CaptureUiPhase.Recording ? "Recording" : "Saved session open", "A published generation.",
        SessionPath: session.Path, Overview: SessionOverviewProjector.Project(session.Store));

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    private static void Save(Window window, string name)
    {
        Avalonia.Media.Imaging.WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        string directory = Path.Combine(AppContext.BaseDirectory, "rendered");
        Directory.CreateDirectory(directory);
        frame!.Save(Path.Combine(directory, name));
    }
}
