using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Ui.Tests;

/// <summary>
/// §11.3's third preset from the Desktop: the open session is saved as an exact, unredacted copy that reopens as the same
/// session, after the window has said what it holds.
/// </summary>
public sealed class OriginalPackageWindowTests
{
    [AvaloniaFact(DisplayName = "11.3: the window saves an original copy of the open session, which reopens as the same generation")]
    public async Task TheWindowSavesAnOriginalCopy()
    {
        using var session = new TemporarySession();
        DerivedGenerationResult published = Publish(session.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 1)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 1_000 },
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 2)
                .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 1_100 },
        ]);
        string destination = MainWindow.NewPackageDirectory(
            Path.Combine(Path.GetTempPath(), "InterCat.Ui.Tests.Packages"), DateTimeOffset.Now, "intercat-original-session");
        var window = new MainWindow { Width = 1456, Height = 939 };
        window.Show();
        try
        {
            Button original = window.GetControl<Button>("ShareOriginalButton");
            Button redacted = window.GetControl<Button>("SharePackageButton");
            Assert.False(original.IsEnabled);

            // While a capture is live, a copy would hold an unfinished session: the action waits for the stop.
            window.ApplyCaptureUpdate(Update(session, CaptureUiPhase.Recording));
            Dispatch();
            Assert.False(original.IsEnabled);
            window.ApplyCaptureUpdate(Update(session, CaptureUiPhase.Complete), forceOverview: true);
            Dispatch();
            Assert.True(original.IsEnabled);
            Assert.True(redacted.IsEnabled);

            // What the confirmation states before anything is saved: the generation and its records, the raw journal, the
            // unredacted contents and the host, and the warning.
            OriginalEvidencePackagePreview preview = OriginalEvidencePackage.Preview(session.Store);
            IReadOnlyList<string> disclosure = MainWindow.OriginalPackageDisclosure(preview);
            Assert.Contains(disclosure, text => text.Contains($"generation {published.Manifest.Generation}: 2 records, 1 raw journal file",
                StringComparison.Ordinal));
            Assert.Contains(disclosure, text => text.StartsWith("Unredacted: ", StringComparison.Ordinal));
            Assert.Contains(disclosure, text => text.StartsWith("Hosts: one, the capture's own", StringComparison.Ordinal));
            Assert.Equal(OriginalEvidencePackage.Warning, disclosure[^1]);

            // The prompt is as tall as what it states, so both actions are whole inside it, and it starts on Cancel.
            Window prompt = MainWindow.OriginalPackagePrompt(preview);
            prompt.Show();
            try
            {
                Dispatch();
                _ = prompt.CaptureRenderedFrame();
                Button cancel = prompt.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "CancelOriginalPackage");
                Button save = prompt.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "SaveOriginalPackage");
                Assert.True(cancel.IsFocused);
                foreach (Button action in new[] { cancel, save })
                {
                    Avalonia.Point corner = action.TranslatePoint(new(action.Bounds.Width, action.Bounds.Height), prompt)!.Value;
                    Assert.True(corner.X <= prompt.ClientSize.Width && corner.Y <= prompt.ClientSize.Height,
                        $"{action.Name} ends at {corner}, outside the {prompt.ClientSize} prompt.");
                }

                Save(prompt, "original-package-prompt.png");
            }
            finally
            {
                prompt.Close();
            }

            OriginalEvidencePackageResult? result = await window.WriteOriginalPackageAsync(session.Path, destination);
            Dispatch();
            Assert.NotNull(result);
            Assert.True(Directory.Exists(destination));
            Assert.Equal("Original session saved", window.GetControl<TextBlock>("CaptureStatus").Text);
            Assert.Contains(destination, window.GetControl<TextBlock>("CaptureDetail").Text, StringComparison.Ordinal);
            Assert.Contains("unredacted", window.GetControl<TextBlock>("CaptureDetail").Text, StringComparison.Ordinal);
            Assert.Equal("Share original session…", original.Content);
            Assert.True(redacted.IsEnabled);

            // The copy is the same session at the same generation.
            Assert.True(await window.OpenSessionAsync(destination));
            Dispatch();
            var copy = Assert.IsType<WorkspaceViewModel>(window.DataContext);
            Assert.Equal(published.Manifest.Generation, copy.DisplayedGeneration);
            Assert.Equal("Saved session open", window.GetControl<TextBlock>("CaptureStatus").Text);
            Assert.Null(copy.Snapshot.Redaction);
        }
        finally
        {
            window.Close();
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
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
