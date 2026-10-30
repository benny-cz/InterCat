using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
/// §20.6's support bundle from the window: it says what the bundle holds and leaves out, in the words `icat support` lists
/// them in, before a file is chosen, and the bundle it writes holds the session shown by its folder's name and nothing a
/// record holds (P16).
/// </summary>
public sealed class SupportBundleWindowTests
{
    [AvaloniaFact(DisplayName = "P16: the window says what a support bundle holds before saving it, and saves the session shown with nothing a record holds")]
    public async Task TheWindowSavesASupportBundle()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 4_242, 1) with
            {
                ResourceName = @"C:\Users\alice\Secret Tools\leaky-tool.exe", SessionRelativeTicks = 100,
            },
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 300, 4_242, 10)
                .Between("10.11.12.13:40404", "10.99.98.97:50505") with { SessionRelativeTicks = 1_000 },
        ]);
        session.Store.ReleaseSegmentReaders();
        var window = new MainWindow { Width = 1_080, Height = 700 };
        window.Show();
        window.ApplyCaptureUpdate(new(CaptureUiPhase.Complete, "Saved session open", "Saved.", SessionPath: session.Path,
            Overview: SessionOverviewProjector.Project(session.Store)), forceOverview: true);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // Offered whatever is shown, and named for a screen reader by what it saves.
        Button offered = window.GetControl<Button>("SupportBundleButton");
        Assert.True(offered.IsEffectivelyEnabled);
        Assert.Contains("with no names, addresses or content", AutomationProperties.GetHelpText(offered), StringComparison.Ordinal);

        // The question says what it holds and leaves out, in icat support's words, before a file is chosen: the errors the
        // window did not expect among them, where the application counts them.
        Window prompt = MainWindow.SupportBundlePrompt(1, unexpectedErrors: true);
        string[] said = [.. ((StackPanel)prompt.Content!).Children.OfType<TextBlock>().Select(block => block.Text!)];
        Assert.Contains("Holds: " + string.Join("; ", SupportBundle.Holds(1, capabilities: false, unexpectedErrors: true)) + ".", said);
        Assert.Contains("Leaves out: " + string.Join("; ", SupportBundle.LeftOut) + ".", said);
        Assert.Contains("Made here, it holds no capability report: icat support adds this machine's.", said);

        // An error the window did not expect, whose words name what a record holds, as an error's may.
        var counted = new InternalErrorTally();
        counted.Count(Thrown(@"Row 4242 of C:\Users\alice\Secret Tools\leaky-tool.exe sent to 10.99.98.97:50505"));

        // Saved, it holds the session by its folder's name, no capability report, the error by its kind and the method it
        // was thrown from, and none of the names, endpoints or words.
        string bundle = Path.Combine(Path.GetTempPath(), "intercat-support-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await MainWindow.WriteSupportBundleAsync(bundle, [session.Path], counted.Counted());
            string written = File.ReadAllText(bundle);
            using JsonDocument document = JsonDocument.Parse(written);
            JsonElement root = document.RootElement;
            Assert.Equal(SupportBundle.Contract, root.GetProperty("contract").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("capabilities").ValueKind);
            Assert.Equal(SupportBundle.Holds(1, capabilities: false, unexpectedErrors: true),
                root.GetProperty("holds").EnumerateArray().Select(hold => hold.GetString()));
            JsonElement shown = root.GetProperty("sessions").EnumerateArray().Single();
            Assert.Equal((Path.GetFileName(session.Path), 2L), (shown.GetProperty("folder").GetString(), shown.GetProperty("rows").GetInt64()));
            JsonElement errors = root.GetProperty("unexpectedErrors");
            JsonElement error = errors.GetProperty("errors").EnumerateArray().Single();
            Assert.Equal((1L, "System.InvalidCastException", 1L), (errors.GetProperty("contained").GetInt64(),
                error.GetProperty("kind").GetString(), error.GetProperty("count").GetInt64()));
            Assert.Equal("InterCat.Ui.Tests.SupportBundleWindowTests.Thrown",
                error.GetProperty("thrownFrom").EnumerateArray().First().GetString());
            foreach (string needle in new[] { "alice", "leaky-tool", "Secret Tools", "10.11.12.13", "10.99.98.97",
                Path.GetDirectoryName(session.Path)! })
            {
                Assert.DoesNotContain(needle, written, StringComparison.OrdinalIgnoreCase);
            }

            // The bundle's own write time and the session folder's random name may hold a port's digits by chance - a time
            // once read ".1440404" - so the ports are looked for everywhere else.
            string elsewhere = System.Text.RegularExpressions.Regex.Replace(
                written.Replace(Path.GetFileName(session.Path), "#", StringComparison.OrdinalIgnoreCase),
                @"\d{4}-\d{2}-\d{2}T[0-9:.]+(?:Z|[+-]\d{2}:\d{2})", "#");
            Assert.Contains("#", elsewhere, StringComparison.Ordinal);
            foreach (string port in new[] { "40404", "50505" })
            {
                Assert.DoesNotContain(port, elsewhere, StringComparison.Ordinal);
            }
        }
        finally
        {
            File.Delete(bundle);
            window.Close();
        }

        // Without a tally, as where nothing contains the window's errors, the bundle says none was counted.
        await MainWindow.WriteSupportBundleAsync(bundle, [], unexpected: null);
        try
        {
            using JsonDocument uncounted = JsonDocument.Parse(File.ReadAllText(bundle));
            Assert.Equal(JsonValueKind.Null, uncounted.RootElement.GetProperty("unexpectedErrors").ValueKind);
            Assert.Equal(SupportBundle.Holds(0, capabilities: false, unexpectedErrors: false),
                uncounted.RootElement.GetProperty("holds").EnumerateArray().Select(hold => hold.GetString()));
        }
        finally
        {
            File.Delete(bundle);
        }
    }

    /// <summary>An exception as one a defect throws: thrown, and caught to be counted.</summary>
    private static InvalidCastException Thrown(string words)
    {
        try
        {
            throw new InvalidCastException(words);
        }
        catch (InvalidCastException thrown)
        {
            return thrown;
        }
    }
}
