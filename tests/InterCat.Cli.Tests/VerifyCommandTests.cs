using System.Text.Json;
using System.Text.Json.Nodes;
using InterCat.Capture.Journal.Tests;
using InterCat.Domain;
using Xunit;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat verify as a person runs it (I14, R18, R21): a run rebuilt from each curated transport fixture re-evaluates to the
/// coverage the fixture was published with, its report states that coverage as icat measure does - each criterion in
/// words, the tier, every gap - and a run it cannot verify is refused, saying why.
/// </summary>
public sealed class VerifyCommandTests : IDisposable
{
    private readonly TemporaryDirectory root = new();

    public void Dispose() => root.Dispose();

    [Theory(DisplayName = "I14: icat verify re-evaluates a run rebuilt from a curated fixture to the coverage it was published with")]
    [InlineData("FX-TCP-001", "tcp")]
    [InlineData("FX-TCP-002", "tcp")]
    [InlineData("FX-UDP-001", "udp")]
    [InlineData("FX-UDP-002", "udp")]
    public async Task ACuratedFixtureVerifiesAsPublished(string fixture, string mechanism)
    {
        string run = Rebuild(fixture);
        string output = Path.Combine(root.Path, "curated");
        (InterCatExitCode code, string report, string said) = await Run("verify", mechanism, "--run", run, "--output", output);
        JsonNode published = Published(fixture);

        // UDP holds no resource to discover, so its topology criteria are never measured: gaps, said as such.
        bool udp = mechanism == "udp";
        Assert.True(code == (udp ? InterCatExitCode.PartialResultSuccess : InterCatExitCode.Success), said);
        Assert.Contains("FIXTURE RUN VERIFIED", report, StringComparison.Ordinal);
        Assert.Matches($@"(?m)^  Fixture +{fixture}, run {published["sourceRunId"]}\r?$", report);
        Assert.Matches($@"(?m)^  Truth records +{published["truthRecords"]}\r?$", report);
        Assert.Matches($@"(?m)^  Observations read +{published["observationsRead"]}\r?$", report);
        Assert.Matches(@"(?m)^  admitted observations +\d+/\d+ +100\.0 ?% +met\r?$", report);
        Assert.Matches(@"(?m)^  Computed tier +traffic visualization\r?$", report);
        if (udp)
        {
            Assert.Contains("- Resource discovery: not measured; the truth workload declared no eligible item for this criterion.",
                report, StringComparison.Ordinal);
            Assert.Contains("- Membership resolution: not measured; the truth workload declared no eligible item for this criterion.",
                report, StringComparison.Ordinal);
        }

        foreach (string identifier in new[] { "AdmittedObservations", "ResourceDiscovery", "TrafficVisualization" })
        {
            Assert.DoesNotContain(identifier, report, StringComparison.Ordinal);
        }

        Assert.Contains($"Curated fixture written to {output}", said, StringComparison.Ordinal);

        // The curated artifact holds the fixture's own truth, and coverage that counts what the published one counted.
        JsonNode written = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "verification.json")))!;
        Assert.Equal(VerifyCommand.ContractName, (string?)written["contract"]);
        Assert.True(JsonNode.DeepEquals(published["coverage"]!["measurement"], written["coverage"]!["measurement"]),
            written["coverage"]!["measurement"]!.ToJsonString());
        Assert.Equal((string?)published["coverage"]!["assessment"]!["tier"], (string?)written["coverage"]!["assessment"]!["tier"]);
        Assert.Equal(Counted(published), Counted(written));
        Assert.Equal((int)published["scopedObservationsWritten"]!, (int)written["scopedObservationsWritten"]!);
        Assert.Equal(File.ReadAllText(Path.Combine(Evidence(fixture), "truth.jsonl")), File.ReadAllText(Path.Combine(output, "truth.jsonl")));
    }

    [Fact(DisplayName = "R21: a run that missed half its operations is verified with every criterion it fails said in words, and exits partial")]
    public async Task ARunWithGapsSaysEachOne()
    {
        string run = Rebuild("FX-TCP-001");
        string observations = Path.Combine(run, "observations.jsonl");
        string[] lines = File.ReadAllLines(observations);
        File.WriteAllLines(observations, lines.Take(lines.Length / 2));

        (InterCatExitCode code, string report, _) = await Run("verify", "tcp", "--run", run, "--output", Path.Combine(root.Path, "thin"));
        Assert.Equal(InterCatExitCode.PartialResultSuccess, code);
        Assert.Matches($@"(?m)^  Observations read +{lines.Length / 2}\r?$", report);
        Assert.Matches(@"(?m)^  Excluded +0 unrelated whole-machine observations\r?$", report);
        Assert.Matches(@"(?m)^  Truth operations +48\r?$", report);
        Assert.Matches(@"(?m)^  With observation +24\r?$", report);
        Assert.Matches(@"(?m)^  Computed tier +experimental evidence\r?$", report);
        Assert.Matches(@"(?m)^  admitted observations +24/48 +50\.0 ?% +not met\r?$", report);
        Assert.Matches(@"(?m)^  - Admitted observations: measured 50\.0 ?% of 48, below the required 95 ?%\.\r?$", report);
        Assert.Matches(@"(?m)^  - Byte measurement: measured 50\.0 ?% of 48, below the required 90 ?%\.\r?$", report);
    }

    [Fact(DisplayName = "R18: icat verify refuses a run it cannot verify, saying why, and writes over curated output only when asked")]
    public async Task ARunItCannotVerifyIsRefused()
    {
        string run = Rebuild("FX-TCP-001");
        string output = Path.Combine(root.Path, "curated");

        (InterCatExitCode code, _, string said) = await Run("verify", "tcp", "--run", run);
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("Both --run <directory> and --output <directory> are required.", said, StringComparison.Ordinal);
        string missing = Path.Combine(root.Path, "missing");
        (code, _, said) = await Run("verify", "tcp", "--run", missing, "--output", output);
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains($"No run directory at {missing}.", said, StringComparison.Ordinal);

        // A UDP run is verified as UDP, the mechanism it measured.
        (code, _, said) = await Run("verify", "tcp", "--run", Rebuild("FX-UDP-001"), "--output", output);
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("The run measured FX-UDP-001, not a TCP fixture. Verify it as the mechanism it measured.", said,
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));

        // A run without its truth or its header is not one a measurement wrote.
        string truthless = Rebuild("FX-TCP-001", "truthless");
        File.Delete(Path.Combine(truthless, "truth-run.jsonl"));
        (code, _, said) = await Run("verify", "tcp", "--run", truthless, "--output", output);
        Assert.Equal(InterCatExitCode.CorruptedInput, code);
        Assert.Contains("The run contains no truth-*.jsonl files.", said, StringComparison.Ordinal);
        string headless = Rebuild("FX-TCP-001", "headless");
        File.Delete(Path.Combine(headless, "measurement.json"));
        (code, _, said) = await Run("verify", "tcp", "--run", headless, "--output", output);
        Assert.Equal(InterCatExitCode.CorruptedInput, code);
        Assert.Contains("The run must contain measurement.json and observations.jsonl.", said, StringComparison.Ordinal);

        // A file a measurement did not write is named, with the line it could not read, and never ends with a stack trace.
        string garbled = Rebuild("FX-TCP-001", "garbled");
        File.WriteAllText(Path.Combine(garbled, "measurement.json"), "not json");
        (code, _, said) = await Run("verify", "tcp", "--run", garbled, "--output", output);
        Assert.Equal(InterCatExitCode.CorruptedInput, code);
        Assert.StartsWith("x measurement.json is not the run header a measurement writes: ", said, StringComparison.Ordinal);
        foreach (string file in new[] { "truth-run.jsonl", "observations.jsonl" })
        {
            string broken = Rebuild("FX-TCP-001", "broken-" + file);
            File.AppendAllText(Path.Combine(broken, file), "{ not a record\n");
            int lines = File.ReadAllLines(Path.Combine(broken, file)).Length;
            (code, _, said) = await Run("verify", "tcp", "--run", broken, "--output", output);
            Assert.Equal(InterCatExitCode.CorruptedInput, code);
            Assert.StartsWith($"x Line {lines} of {file} is not a record a measurement writes: ", said, StringComparison.Ordinal);
            Assert.DoesNotContain("   at ", said, StringComparison.Ordinal);
        }

        Assert.False(Directory.Exists(output));

        // Curated output is written over only with --overwrite; a script reads the verification, naming its contract.
        Assert.Equal(InterCatExitCode.Success, (await Run("verify", "tcp", "--run", run, "--output", output)).Code);
        DateTime written = File.GetLastWriteTimeUtc(Path.Combine(output, "verification.json"));
        (code, _, said) = await Run("verify", "tcp", "--run", run, "--output", output);
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains($"{output} already contains files. Pass --overwrite to replace known outputs.", said, StringComparison.Ordinal);
        Assert.Equal(written, File.GetLastWriteTimeUtc(Path.Combine(output, "verification.json")));
        (code, string answer, _) = await Run("verify", "tcp", "--run", run, "--output", output, "--overwrite", "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        using JsonDocument document = JsonDocument.Parse(answer);
        Assert.Equal(VerifyCommand.ContractName, document.RootElement.GetProperty("contract").GetString());
        Assert.Equal("TrafficVisualization", document.RootElement.GetProperty("coverage").GetProperty("assessment").GetProperty("tier").GetString());
    }

    /// <summary>Each criterion's name and counts, as an assessment's JSON holds them; its gap sentence aside.</summary>
    private static string[] Counted(JsonNode verification) =>
    [
        .. verification["coverage"]!["assessment"]!["criteria"]!.AsArray().Select(criterion =>
            $"{criterion!["name"]} {criterion["numerator"]}/{criterion["denominator"]} {criterion["measured"]} {criterion["satisfied"]}"),
    ];

    /// <summary>
    /// The raw run a measurement of <paramref name="fixture"/> wrote, rebuilt from its curated evidence: the run's header,
    /// its truth log and its observations.
    /// </summary>
    private string Rebuild(string fixture, string? name = null)
    {
        string evidence = Evidence(fixture);
        string run = Path.Combine(root.Path, name ?? fixture);
        Directory.CreateDirectory(run);
        JsonNode published = Published(fixture);
        var header = new JsonObject
        {
            ["fixtureId"] = published["fixtureId"]!.DeepClone(),
            ["runId"] = published["sourceRunId"]!.DeepClone(),
            ["environment"] = published["environment"]!.DeepClone(),
            ["adapterVersion"] = published["adapterVersion"]!.DeepClone(),
        };
        File.WriteAllText(Path.Combine(run, "measurement.json"), header.ToJsonString());
        File.Copy(Path.Combine(evidence, "truth.jsonl"), Path.Combine(run, "truth-run.jsonl"));
        File.Copy(Path.Combine(evidence, "observations.jsonl"), Path.Combine(run, "observations.jsonl"));
        return run;
    }

    private static JsonNode Published(string fixture) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Evidence(fixture), "verification.json")))!;

    private static string Evidence(string fixture)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
        {
            current = current.Parent;
        }

        return Path.Combine(current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root."),
            "fixtures", fixture, "evidence");
    }

    private static async Task<(InterCatExitCode Code, string Output, string Error)> Run(params string[] args)
    {
        TextWriter output = Console.Out;
        TextWriter error = Console.Error;
        using var answered = new StringWriter();
        using var said = new StringWriter();
        Console.SetOut(answered);
        Console.SetError(said);
        try
        {
            InterCatExitCode code = await Icat.RunAsync(args, CancellationToken.None);
            return (code, answered.ToString(), said.ToString());
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
        }
    }
}
