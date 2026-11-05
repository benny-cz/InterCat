using System.Text.Json;
using InterCat.Domain;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// Mailslots (ADR-085): FX-MAIL-001's mailslot, measured under the kernel's classic file I/O class and
/// Microsoft-Windows-Kernel-File, produced no record of either while both recorded the same processes' own files, so the
/// mechanism is unsupported on its build and the capability report says so, though no source covers it.
/// </summary>
public sealed class MailslotFeasibilityTests
{
    private const string Build = "10.0.26220.0-x64";
    private static readonly string[] Sources = ["classicFileIo", "kernelFile"];

    [Fact(DisplayName = "P27: FX-MAIL-001's mailslot is unsupported on its build - in three runs neither the kernel's classic file I/O class nor Microsoft-Windows-Kernel-File recorded its creation, an open or a message, while both recorded the two processes' own files")]
    public void AMailslotIsRecordedByNoSource()
    {
        string root = RepositoryRoot();
        using JsonDocument measurement = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "fixtures", "FX-MAIL-001", "evidence", "measurement.json")));
        JsonElement[] runs = [.. measurement.RootElement.GetProperty("runs").EnumerateArray()];
        Assert.Equal(3, runs.Length);
        foreach (JsonElement run in runs)
        {
            // The run's own report, as the probe wrote it, says what its measurement states.
            using JsonDocument report = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, run.GetProperty("bench").GetString()!, "report.json")));
            JsonElement truth = report.RootElement.GetProperty("truth");
            int messages = truth.GetProperty("messagesWritten").GetInt32();
            Assert.InRange(messages, 1, int.MaxValue);
            Assert.Equal((messages, truth.GetProperty("bytesWritten").GetInt64()),
                (truth.GetProperty("messagesRead").GetInt32(), truth.GetProperty("bytesRead").GetInt64()));
            Assert.Equal(messages, run.GetProperty("truth").GetProperty("messagesWritten").GetInt32());
            foreach (string name in Sources)
            {
                JsonElement source = report.RootElement.GetProperty(name);

                // Nothing lost, and not one record of the mailslot: none naming it, none of its file objects, and none of the two
                // processes' reads or writes of a message's or a read's size.
                Assert.Equal((0, 0, 0, 0, 0), (source.GetProperty("eventsLost").GetInt32(), source.GetProperty("mailslotFileObjects").GetInt32(),
                    source.GetProperty("mailslotRecordsByKindAndRole").EnumerateObject().Count(),
                    source.GetProperty("clientWritesOfATruthMessagesSize").GetInt32(), source.GetProperty("serverReadsAskingTheTruthsSize").GetInt32()));

                // While the same session recorded each process's own files: the source was live for them.
                Assert.InRange(source.GetProperty("controlRecordsOfTheServer").GetInt32(), 100, int.MaxValue);
                Assert.InRange(source.GetProperty("controlRecordsOfTheClient").GetInt32(), 100, int.MaxValue);
                Assert.Equal(source.GetProperty("controlRecordsOfTheServer").GetInt32(),
                    run.GetProperty(name).GetProperty("controlRecordsOfTheServer").GetInt32());
            }
        }

        // So the build's coverage carries the mechanism unsupported, from this fixture.
        MeasuredCoverageEntry measured = Assert.Single(MeasuredCoverage.Entries, entry => entry.Mechanism == Mechanism.Mailslot);
        Assert.Equal((CapabilityTier.Unsupported, "FX-MAIL-001", Build), (measured.Tier, measured.Fixture, measured.Build));
    }

    [Fact(DisplayName = "P27: icat capabilities reports a mechanism no source covers where a fixture measured it on the build - mailslots unsupported, by FX-MAIL-001 - and leaves it out on a build none measured it on")]
    public void AMeasuredMechanismWithNoSourceIsReported()
    {
        Assert.DoesNotContain(WindowsSourceCatalog.All, source => source.Mechanisms.Contains(Mechanism.Mailslot));
        var inventory = new CapabilityInventoryProbe(new NoMetadata());

        CapabilityReport measured = inventory.Probe(Environment(Build));
        MechanismCapability mailslot = Assert.Single(measured.Mechanisms, mechanism => mechanism.Mechanism == Mechanism.Mailslot);
        Assert.Equal((CapabilityState.Unsupported, CapabilityTier.Unsupported), (mailslot.State, mailslot.Tier));
        Assert.Equal(["FX-MAIL-001"], mailslot.FixtureIds);
        Assert.Empty(mailslot.SourceIds);
        Assert.Contains("No source in the catalog records this mechanism", mailslot.UnavailableReason, StringComparison.Ordinal);
        Assert.Contains("tier Unsupported on this build, measured by FX-MAIL-001", mailslot.Summary, StringComparison.Ordinal);

        // On a build no fixture measured it on, and with no source to cover it, the report claims nothing of it.
        CapabilityReport other = inventory.Probe(Environment("10.0.99999.0-x64"));
        Assert.DoesNotContain(other.Mechanisms, mechanism => mechanism.Mechanism == Mechanism.Mailslot);
    }

    private static ProbeEnvironment Environment(string build) => CapabilityInventoryProbe.DescribeEnvironment(isElevated: false) with { BuildId = build };

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the InterCat repository root.");
    }

    /// <summary>A machine whose metadata registers nothing: the report's sources are unavailable, its measured tiers its own.</summary>
    private sealed class NoMetadata : IEtwMetadataSource
    {
        public bool IsAvailable => true;

        public string? UnavailableReason => null;

        public RegisteredProvider? TryResolveProvider(string providerName) => null;

        public int CountPublishedProviders() => 0;

        public ManifestReadResult TryReadManifest(Guid providerGuid) => ManifestReadResult.Failure("not registered in this test");

        public ProviderSchema? TryReadClassic(Guid classGuid, string className, int version, IReadOnlyList<int> opcodes) => null;
    }
}
