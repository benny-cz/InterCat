using System.Text.Json;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// What a process's own lifecycle records say of it beyond its identity (ADR-093): FX-PROC-001's process starts and its
/// session's rundown, measured against what the application model and each process's token give, read by the layout the
/// machine registers past the mandatory label's variable-length SID.
/// </summary>
public sealed class ProcessEnrichmentFeasibilityTests
{
    [Fact(DisplayName = "R3: FX-PROC-001's process starts carry each child's package full name and application ID - a packaged child's as the application model gives them, an unpackaged child's empty, an observed none - beside its token's elevation, integrity level and session, read by the registered layout past the mandatory label's SID, which TDH's own decoding loses; and every rundown record a running process's package")]
    public void ProcessStartsCarryTheirPackageAndToken()
    {
        string evidence = Path.Combine(RepositoryRoot(), "fixtures", "FX-PROC-001", "evidence");
        using JsonDocument measurement = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "measurement.json")));
        using JsonDocument scenario = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "scenario.json")));
        JsonElement[] runs = [.. measurement.RootElement.GetProperty("runs").EnumerateArray()];
        Assert.Equal(3, runs.Length);

        // The committed truth: the packaged child's package as the application model gave it, the unpackaged child's none,
        // each image by its file name alone.
        JsonElement[] children = [.. scenario.RootElement.GetProperty("children").EnumerateArray()];
        JsonElement packaged = Assert.Single(children, static child => child.GetProperty("role").GetString() == "packaged");
        JsonElement unpackaged = Assert.Single(children, static child => child.GetProperty("role").GetString() == "unpackaged");
        Assert.EndsWith("_8wekyb3d8bbwe", packaged.GetProperty("packageFullName").GetString(), StringComparison.Ordinal);
        Assert.Equal(("winget", JsonValueKind.Null), (packaged.GetProperty("packageRelativeApplicationId").GetString(),
            unpackaged.GetProperty("packageFullName").ValueKind));
        Assert.All(children, static child => Assert.Equal(Path.GetFileName(child.GetProperty("image").GetString()), child.GetProperty("image").GetString()));

        foreach (JsonElement run in runs)
        {
            // Nothing lost; each child's one process start, at version 4, carrying every field as its truth - the first run's
            // the committed truth's own - its package past the SID that TDH's decoding by name loses.
            Assert.Equal(0, run.GetProperty("eventsLost").GetInt32());
            foreach (JsonElement child in run.GetProperty("children").EnumerateArray())
            {
                Assert.Equal((1, 4), (child.GetProperty("startsRecorded").GetInt32(), child.GetProperty("version").GetInt32()));
                Assert.All(child.GetProperty("fields").EnumerateObject(), field => Assert.True(field.Value.GetProperty("same").GetBoolean(), field.Name));
                bool hasPackage = child.GetProperty("fields").GetProperty("packageFullName").GetProperty("truth").ValueKind != JsonValueKind.Null;
                Assert.Equal(!hasPackage, child.GetProperty("packageFullNameAsTdhDecodesIt").GetProperty("same").GetBoolean());
            }

            // Every rundown record a running process's package as the application model gave it then, packaged and not, none
            // disagreeing, each with its mandatory label - and none with a package as TDH decodes it.
            JsonElement rundown = run.GetProperty("rundown");
            Assert.Equal([2], rundown.GetProperty("versions").EnumerateArray().Select(static version => version.GetInt32()));
            Assert.Equal(0, rundown.GetProperty("disagreeing").GetInt32());
            Assert.InRange(rundown.GetProperty("packagedAgreeing").GetInt32(), 1, int.MaxValue);
            Assert.InRange(rundown.GetProperty("unpackagedAgreeing").GetInt32(), 1, int.MaxValue);
            Assert.Equal(rundown.GetProperty("readable").GetInt32(),
                rundown.GetProperty("packagedAgreeing").GetInt32() + rundown.GetProperty("unpackagedAgreeing").GetInt32());
            Assert.Equal((rundown.GetProperty("records").GetInt32(), 0), (rundown.GetProperty("withAMandatoryLabel").GetInt32(),
                rundown.GetProperty("withAPackageAsTdhDecodesIt").GetInt32()));

            // And the run is its bench report's.
            using JsonDocument report = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), run.GetProperty("bench").GetString()!)));
            Assert.Equal(JsonSerializer.Serialize(rundown), JsonSerializer.Serialize(report.RootElement.GetProperty("rundown")));
        }

        JsonElement first = runs[0].GetProperty("children").EnumerateArray().Single(static child => child.GetProperty("role").GetString() == "packaged");
        Assert.Equal(packaged.GetProperty("packageFullName").GetString(),
            first.GetProperty("fields").GetProperty("packageFullName").GetProperty("recorded").GetString());
        Assert.Equal(packaged.GetProperty("integrityLevelSid").GetString(),
            first.GetProperty("fields").GetProperty("integrityLevel").GetProperty("recorded").GetString());
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
