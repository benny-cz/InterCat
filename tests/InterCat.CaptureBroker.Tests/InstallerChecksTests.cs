using System.Text.Json;
using Xunit;

namespace InterCat.CaptureBroker.Tests;

/// <summary>
/// The package's checks on a real machine (ADR-115, `installer/Test-Installation.ps1`), held to what §20.5 and §25 ask of
/// an installation: installed, signed and its root secured; a capture through the installed broker; an update that keeps
/// what was recorded; a removal refused while a capture records; and removals leaving nothing but the captures kept.
/// </summary>
public sealed class InstallerChecksTests
{
    [Fact(DisplayName = "§25: the package installs signed, records through its broker, updates, refuses removal while recording, and is removed leaving nothing but the captures it was not asked to remove")]
    public void ThePackagesChecksHold()
    {
        string[] reports = [.. Directory.EnumerateDirectories(Path.Combine(RepositoryRoot(), "bench", "results"), "installer-checks-*")
            .Select(directory => Path.Combine(directory, "report.json")).Where(File.Exists).Order(StringComparer.Ordinal)];
        Assert.NotEmpty(reports);
        foreach (string path in reports)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement report = document.RootElement;
            Assert.Equal("intercat.installer-checks.v1", report.GetProperty("schema").GetString());

            // Before: elevated, the runtime the package depends on, and nothing of InterCat installed or running.
            JsonElement before = report.GetProperty("before");
            Assert.True(before.GetProperty("elevated").GetBoolean() && before.GetProperty("desktopRuntime10").GetBoolean());
            AssertNothingInstalled(before.GetProperty("leftovers"));
            bool rootExisted = before.GetProperty("rootExisted").GetBoolean();
            int capturesBefore = before.GetProperty("capturesInIt").GetInt32();

            // Installed: every one of InterCat's own binaries and the package signed by the test certificate, icat
            // answering, the path and the menu entry there, and the root carrying exactly the production descriptor.
            JsonElement install = report.GetProperty("install");
            Assert.Equal(0, install.GetProperty("exit").GetInt32());
            Assert.InRange(install.GetProperty("ownBinaries").GetInt32(), 1, int.MaxValue);
            Assert.Equal(install.GetProperty("ownBinaries").GetInt32(), install.GetProperty("ownSignedByTheCertificate").GetInt32());
            Assert.True(install.GetProperty("packageSignedByTheCertificate").GetBoolean() && install.GetProperty("icatAnswers").GetBoolean());
            AssertInstalled(install.GetProperty("leftovers"), 0, capturesBefore);
            AssertProductionRoot(install.GetProperty("rootSecurity"));

            // A capture through the installed broker closes finished, leaving no broker and no trace session running.
            JsonElement capture = report.GetProperty("capture");
            AssertFinished(capture);
            AssertInstalled(capture.GetProperty("leftovers"), 1, capturesBefore);

            // The update installs over it and keeps the root, its security and what it recorded.
            JsonElement update = report.GetProperty("update");
            Assert.Equal(0, update.GetProperty("exit").GetInt32());
            AssertInstalled(update.GetProperty("leftovers"), 1, capturesBefore);
            AssertProductionRoot(update.GetProperty("rootSecurity"));

            // A removal while a capture records fails - Windows Installer's 1603 - and the capture goes on to finish.
            JsonElement refused = report.GetProperty("refusedWhileRecording");
            Assert.True(refused.GetProperty("brokerRunning").GetBoolean());
            Assert.Equal((1603, true, true), (refused.GetProperty("exit").GetInt32(), refused.GetProperty("stillInstalled").GetBoolean(),
                refused.GetProperty("brokerStillRunning").GetBoolean()));
            AssertFinished(refused.GetProperty("capture"));
            AssertInstalled(refused.GetProperty("leftovers"), 2, capturesBefore);

            // The removal leaves nothing installed or running, and keeps the root with every capture.
            JsonElement removed = report.GetProperty("removeKeepingCaptures");
            Assert.Equal(0, removed.GetProperty("exit").GetInt32());
            AssertNothingInstalled(removed.GetProperty("leftovers"));
            Assert.Equal((true, 2, capturesBefore), Root(removed.GetProperty("leftovers")));

            // Removed asking for the captures, nothing is left - except on a machine whose root held captures before,
            // where that removal is never run.
            JsonElement removedWithCaptures = report.GetProperty("removeWithCaptures");
            if (rootExisted)
            {
                Assert.StartsWith("The machine's broker root held captures before these checks", removedWithCaptures.GetProperty("skipped").GetString(), StringComparison.Ordinal);
                Assert.Equal((2, capturesBefore, capturesBefore), (report.GetProperty("after").GetProperty("ownCapturesDeleted").GetInt32(),
                    report.GetProperty("after").GetProperty("capturesInTheRoot").GetInt32(), report.GetProperty("after").GetProperty("capturesBefore").GetInt32()));
            }
            else
            {
                Assert.Equal(0, removedWithCaptures.GetProperty("exit").GetInt32());
                Assert.Equal((false, 0, 0), Root(removedWithCaptures.GetProperty("leftovers")));
            }

            // Installed and removed with nothing recorded meanwhile: the root goes with it, unless it holds captures.
            Assert.Equal(0, report.GetProperty("freshInstall").GetProperty("exit").GetInt32());
            JsonElement unused = report.GetProperty("removeUnused");
            Assert.Equal(0, unused.GetProperty("exit").GetInt32());
            AssertNothingInstalled(unused.GetProperty("leftovers"));
            Assert.Equal(rootExisted, unused.GetProperty("leftovers").GetProperty("root").GetBoolean());
        }
    }

    private static void AssertNothingInstalled(JsonElement leftovers) =>
        Assert.Equal((false, false, false, false, 0, 0), (leftovers.GetProperty("installed").GetBoolean(), leftovers.GetProperty("folder").GetBoolean(),
            leftovers.GetProperty("pathEntry").GetBoolean(), leftovers.GetProperty("startMenuEntry").GetBoolean(),
            leftovers.GetProperty("brokerProcesses").GetInt32(), leftovers.GetProperty("traceSessions").GetInt32()));

    private static void AssertInstalled(JsonElement leftovers, int captures, int others)
    {
        Assert.Equal((true, true, true, true, 0, 0), (leftovers.GetProperty("installed").GetBoolean(), leftovers.GetProperty("folder").GetBoolean(),
            leftovers.GetProperty("pathEntry").GetBoolean(), leftovers.GetProperty("startMenuEntry").GetBoolean(),
            leftovers.GetProperty("brokerProcesses").GetInt32(), leftovers.GetProperty("traceSessions").GetInt32()));
        Assert.Equal((true, captures, others), Root(leftovers));
    }

    private static (bool Root, int Ours, int Others) Root(JsonElement leftovers) =>
        (leftovers.GetProperty("root").GetBoolean(), leftovers.GetProperty("capturesOfTheseChecks").GetInt32(), leftovers.GetProperty("otherCaptures").GetInt32());

    private static void AssertProductionRoot(JsonElement security) =>
        Assert.Equal((true, true, true, true), (security.GetProperty("ownerIsAdministrators").GetBoolean(), security.GetProperty("daclProtected").GetBoolean(),
            security.GetProperty("daclIsTheProductions").GetBoolean(), security.GetProperty("highNoWriteUpLabel").GetBoolean()));

    private static void AssertFinished(JsonElement capture)
    {
        Assert.Equal((0, true, "Closed"), (capture.GetProperty("exit").GetInt32(), capture.GetProperty("finished").GetBoolean(), capture.GetProperty("state").GetString()));
        Assert.InRange(capture.GetProperty("derivedRecords").GetInt64(), 1, long.MaxValue);
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the InterCat repository root.");
    }
}
