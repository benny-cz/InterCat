using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Domain;

namespace InterCat.Cli;

/// <summary>A portable, shareable re-evaluation of one raw local TCP or UDP measurement run (R18, I14).</summary>
internal sealed record FixtureVerification
{
    /// <summary>The contract this document follows, so a reader can refuse a version it does not know (§20.4).</summary>
    public required string Contract { get; init; }

    public required string FixtureId { get; init; }
    public required string SourceRunId { get; init; }
    public required DateTimeOffset VerifiedAtUtc { get; init; }
    public required ProbeEnvironment Environment { get; init; }
    public required string AdapterVersion { get; init; }
    public required int TruthRecords { get; init; }
    public required int ObservationsRead { get; init; }
    public required int ScopedObservationsWritten { get; init; }
    public required int ExcludedWholeMachineObservations { get; init; }
    public required TcpCoverageResult Coverage { get; init; }
    public required IReadOnlyDictionary<string, string> InputSha256 { get; init; }
    public required bool Shareable { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

internal sealed record CapturedRunHeader
{
    public required string FixtureId { get; init; }
    public required string RunId { get; init; }
    public required ProbeEnvironment Environment { get; init; }
    public required string AdapterVersion { get; init; }
}

internal static class VerifyCommand
{
    /// <summary>The contract a verification's document names.</summary>
    public const string ContractName = "fixture-verification-v1";

    private const long MaximumArtifactBytes = 128L * 1024 * 1024;
    private const int MaximumRecords = 1_000_000;

    public static async Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string mechanism = command.TakeVerb() ?? "tcp";
        Mechanism? expectedMechanism = mechanism.ToUpperInvariant() switch
        {
            "TCP" => Mechanism.Tcp,
            "UDP" => Mechanism.Udp,
            _ => null,
        };
        if (expectedMechanism is null)
        {
            // The word most often put here by mistake is a session's folder, since verifying sounds like what a session
            // needs: it is verified whenever it is opened, which no command has to be asked to do.
            ConsoleUi.Failure(Directory.Exists(mechanism)
                ? $"{Path.GetFullPath(mechanism)} is a folder; icat verify names a mechanism, tcp or udp, and re-evaluates "
                    + "the run icat measure recorded for it: icat verify <tcp|udp> --run <raw-run-dir> --output "
                    + "<curated-dir>. A session is verified whenever it is opened; icat session <directory> reports what "
                    + "that found."
                : CommandLine.Expects("icat verify", mechanism, ["tcp", "udp"]) + " It re-evaluates the run icat measure recorded "
                    + "for one.");
            return InterCatExitCode.InvalidInvocation;
        }

        string? runOption = command.TakeOption("--run");
        string? outputOption = command.TakeOption("--output");
        bool overwrite = command.TryTakeFlag("--overwrite");
        bool json = command.TryTakeFlag("--json");
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure(command.Refusal(unknown!));
            return InterCatExitCode.InvalidInvocation;
        }

        if (runOption is null || outputOption is null)
        {
            ConsoleUi.Failure("Both --run <directory> and --output <directory> are required.");
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        string runDirectory = Path.GetFullPath(runOption);
        string outputDirectory = Path.GetFullPath(outputOption);
        if (!Directory.Exists(runDirectory))
        {
            ConsoleUi.Failure($"No run directory at {runDirectory}.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (OutputFiles.FolderRefusal(outputDirectory, "icat verify writes the curated run into a folder") is { } blocked)
        {
            ConsoleUi.Failure(blocked);
            return InterCatExitCode.InvalidInvocation;
        }

        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any() && !overwrite)
        {
            ConsoleUi.Failure($"{outputDirectory} already contains files. Pass --overwrite to replace known outputs.");
            return InterCatExitCode.InvalidInvocation;
        }

        string measurementPath = Path.Combine(runDirectory, "measurement.json");
        string observationsPath = Path.Combine(runDirectory, "observations.jsonl");
        if (!File.Exists(measurementPath) || !File.Exists(observationsPath))
        {
            ConsoleUi.Failure("The run must contain measurement.json and observations.jsonl.");
            return InterCatExitCode.CorruptedInput;
        }

        EnsureBounded(measurementPath);
        EnsureBounded(observationsPath);
        CapturedRunHeader header;
        try
        {
            header = JsonSerializer.Deserialize<CapturedRunHeader>(
                await File.ReadAllTextAsync(measurementPath, cancellationToken).ConfigureAwait(false),
                JsonContracts.Compact)
                ?? throw new InvalidDataException("measurement.json has no run header.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"measurement.json is not the run header a measurement writes: {exception.Message}", exception);
        }
        // The run's own fixture says which family its loopback flows are in.
        TransportScenario? scenario = TransportScenario.All.FirstOrDefault(
            candidate => string.Equals(candidate.FixtureId, header.FixtureId, StringComparison.Ordinal));
        if (scenario is null || scenario.Mechanism != expectedMechanism)
        {
            ConsoleUi.Failure(
                $"The run measured {header.FixtureId}, not a {mechanism.ToUpperInvariant()} fixture. Verify it as the "
                + "mechanism it measured.");
            return InterCatExitCode.InvalidInvocation;
        }

        string[] truthPaths = Directory.EnumerateFiles(runDirectory, "truth-*.jsonl")
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (truthPaths.Length == 0)
        {
            ConsoleUi.Failure("The run contains no truth-*.jsonl files.");
            return InterCatExitCode.CorruptedInput;
        }

        var truth = new List<TruthRecord>();
        foreach (string path in truthPaths)
        {
            EnsureBounded(path);
            truth.AddRange(await ReadJsonLinesAsync<TruthRecord>(path, cancellationToken).ConfigureAwait(false));
        }

        IReadOnlyList<NetworkTransferObservation> observations =
            await ReadJsonLinesAsync<NetworkTransferObservation>(observationsPath, cancellationToken).ConfigureAwait(false);
        var settings = new TcpCoverageSettings
        {
            FixtureId = header.FixtureId,
            BuildId = header.Environment.BuildId,
            BuildIsSupported = header.Environment.IsSupportedBuild,
            Reproduced = true,
            LoopbackAddress6 = scenario.LoopbackAddress6,
        };
        TcpCoverageResult coverage = TcpCoverageEvaluator.Evaluate(truth, observations, settings);
        IReadOnlyList<NetworkTransferObservation> scoped =
            TcpCoverageEvaluator.SelectFixtureEvidence(truth, observations, settings);

        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in truthPaths.Append(measurementPath).Append(observationsPath))
        {
            hashes[Path.GetFileName(path)] = await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
        }

        var verification = new FixtureVerification
        {
            Contract = ContractName,
            FixtureId = header.FixtureId,
            SourceRunId = header.RunId,
            VerifiedAtUtc = DateTimeOffset.UtcNow,
            Environment = header.Environment,
            AdapterVersion = header.AdapterVersion,
            TruthRecords = truth.Count,
            ObservationsRead = observations.Count,
            ScopedObservationsWritten = scoped.Count,
            ExcludedWholeMachineObservations = observations.Count - scoped.Count,
            Coverage = coverage,
            InputSha256 = hashes,
            Shareable = true,
            Notes =
            [
                "This curated artifact contains only the synthetic fixture's loopback flows.",
                "Unrelated whole-machine observations were counted and excluded before persistence (P16).",
                "Coverage was recomputed with deterministic one-to-one truth/observation matching (I14).",
            ],
        };

        Directory.CreateDirectory(outputDirectory);
        await WriteJsonLinesAsync(Path.Combine(outputDirectory, "truth.jsonl"), truth, cancellationToken)
            .ConfigureAwait(false);
        await WriteJsonLinesAsync(Path.Combine(outputDirectory, "observations.jsonl"), scoped, cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "verification.json"),
            JsonSerializer.Serialize(verification, JsonContracts.Indented),
            cancellationToken).ConfigureAwait(false);

        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(verification, JsonContracts.Indented));
        }
        else
        {
            // What was read and written, then the coverage as icat measure states it, every gap included (R18).
            ConsoleUi.Heading("Fixture run verified");
            ConsoleUi.Field("Fixture", $"{header.FixtureId}, run {header.RunId}");
            ConsoleUi.Field("Truth records", ConsoleUi.Count(truth.Count));
            ConsoleUi.Field("Observations read", ConsoleUi.Count(observations.Count));
            ConsoleUi.Field("Written", $"{CountText.Of(scoped.Count, "observation")} of the fixture's own loopback flows");
            ConsoleUi.Field("Excluded", $"{CountText.Of(observations.Count - scoped.Count, "unrelated whole-machine observation")}");
            CoverageReport.Render(coverage);
            ConsoleUi.Line();
            foreach (string note in verification.Notes)
            {
                ConsoleUi.Note(note);
            }

            ConsoleUi.Success($"Curated fixture written to {outputDirectory}.");
        }

        return coverage.Assessment.Gaps.Count == 0
            ? InterCatExitCode.Success
            : InterCatExitCode.PartialResultSuccess;
    }

    private static void EnsureBounded(string path)
    {
        long length = new FileInfo(path).Length;
        if (length > MaximumArtifactBytes)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} exceeds the {MaximumArtifactBytes}-byte verification limit.");
        }
    }

    private static async Task<IReadOnlyList<T>> ReadJsonLinesAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        var records = new List<T>();
        using var reader = new StreamReader(path);
        long number = 0;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            number++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (records.Count == MaximumRecords)
            {
                throw new InvalidDataException($"{Path.GetFileName(path)} exceeds the {MaximumRecords}-record verification limit.");
            }

            T? record;
            try
            {
                record = JsonSerializer.Deserialize<T>(line, JsonContracts.Compact);
            }
            catch (JsonException exception)
            {
                // A line a measurement did not write is named where it is, never answered with a stack trace.
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                    $"Line {number} of {Path.GetFileName(path)} is not a record a measurement writes: {exception.Message}"), exception);
            }

            if (record is null)
            {
                throw new InvalidDataException($"{Path.GetFileName(path)} contains a null JSON record.");
            }

            records.Add(record);
        }

        return records;
    }

    private static async Task WriteJsonLinesAsync<T>(
        string path,
        IEnumerable<T> records,
        CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(path, append: false);
        foreach (T record in records)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(record, JsonContracts.Compact).AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat verify <tcp|udp> --run <raw-run-dir> --output <curated-dir> [--overwrite] [--json]");
        ConsoleUi.Line();
        ConsoleUi.Line("  Recomputes TCP or UDP coverage without ETW or elevation and writes a shareable artifact");
        ConsoleUi.Line("  containing only fixture-scoped loopback evidence. The raw run is never modified.");
    }
}
