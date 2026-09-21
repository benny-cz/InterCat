using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Capture.Windows;
using InterCat.Domain;

namespace InterCat.Cli;

/// <summary>The complete machine-readable result of one measured named-pipe run.</summary>
internal sealed record PipeMeasurementRun
{
    public required string FixtureId { get; init; }
    public required string RunId { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public required DateTimeOffset CompletedUtc { get; init; }
    public required ProbeEnvironment Environment { get; init; }
    public required string AdapterVersion { get; init; }
    public required SessionRecord Session { get; init; }
    public required string PipePath { get; init; }
    public required IReadOnlyList<ProviderEnablementResult> Providers { get; init; }
    public required CaptureHealthSnapshot Health { get; init; }
    public required IReadOnlyList<string> Degradations { get; init; }
    public required IReadOnlyList<string> PlanRefusals { get; init; }
    public required PipeCoverageResult Coverage { get; init; }
    public required IReadOnlyList<ProcessEvidence> Processes { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// `icat measure pipe`: runs the seeded named-pipe truth workload under an owned ETW session and reports
/// measured coverage for the named-pipe mechanism (IC-005, section 14.2).
/// </summary>
internal static partial class MeasureCommand
{
    private static async Task<InterCatExitCode> RunPipeAsync(CommandLine command, CancellationToken cancellationToken)
    {
        string? outputOption = command.TakeOption("--output");
        string? workloadOption = command.TakeOption("--workload");
        int seed = command.TakeIntegerOption("--seed") ?? 20_260_921;
        int messages = command.TakeIntegerOption("--messages") ?? 8;
        int maximumBytes = command.TakeIntegerOption("--bytes") ?? 4_096;
        int graceSeconds = command.TakeIntegerOption("--grace") ?? 2;
        bool json = command.TryTakeFlag("--json");
        bool overwrite = command.TryTakeFlag("--overwrite");
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure($"Unknown or incomplete option: {unknown}");
            return InterCatExitCode.InvalidInvocation;
        }

        var host = new TraceEventSessionHost();
        ProbeEnvironment environment = CapabilityInventoryProbe.DescribeEnvironment(host.IsElevated);
        if (!environment.IsElevated)
        {
            ConsoleUi.Failure("Starting an ETW session requires elevation. Run this command from an elevated shell.");
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        string? workload = ResolveWorkload(workloadOption);
        if (workload is null)
        {
            ConsoleUi.Failure(
                "The truth workload executable was not found. Build InterCat.TestWorkloads or pass --workload <path>.");
            return InterCatExitCode.InvalidInvocation;
        }

        string runId = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string outputDirectory = Path.GetFullPath(
            outputOption ?? Path.Combine("fixtures", "FX-PIPE-001", "runs", runId));
        if (Directory.Exists(outputDirectory)
            && Directory.EnumerateFileSystemEntries(outputDirectory).Any()
            && !overwrite)
        {
            ConsoleUi.Failure($"{outputDirectory} already contains files. Pass --overwrite to replace them.");
            return InterCatExitCode.InvalidInvocation;
        }

        Directory.CreateDirectory(outputDirectory);

        var probe = new CapabilityInventoryProbe(new TdhEtwMetadataSource());
        string[] sourceIds = [WindowsSourceCatalog.KernelProcessSourceId, WindowsSourceCatalog.KernelFileSourceId];
        ConsoleUi.Progress("Compiling admission plans from the schemas this machine reports.");
        IReadOnlyList<SourceAdmissionPlan> plans = probe.CompilePlans(sourceIds, out IReadOnlyList<string> refusals);
        foreach (string refusal in refusals)
        {
            ConsoleUi.Warn(refusal);
        }

        if (plans.Count == 0)
        {
            ConsoleUi.Failure("No source could be compiled into an admission plan, so no capture was started.");
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        CaptureSessionIdentity identity = CaptureSessionIdentity.Create("m0pipe", System.Environment.ProcessId);
        var sessionPlan = new OwnedSessionPlan
        {
            Identity = identity,
            Sources = plans,
            Providers = BuildProviderRequests(plans),
            ReorderGrace = TimeSpan.FromSeconds(graceSeconds),
            MaximumDuration = TimeSpan.FromMinutes(2),

            // File activity is whole-machine and bursty, so this run gets a larger bounded queue. Overflow
            // still becomes a counted drop rather than back pressure on acquisition (R8).
            QueueCapacityRecords = 262_144,
        };

        DateTimeOffset started = DateTimeOffset.UtcNow;
        await using var session = new OwnedCaptureSession(sessionPlan, host);
        ConsoleUi.Progress($"Starting owned session {identity.SessionName}.");
        ConsoleUi.Warn(
            "Kernel file activity is captured machine-wide for the duration of this run, because a pipe peer "
            + "lives in another process. The run is bounded and stops automatically.");
        CaptureStartResult start = await session.StartAsync(cancellationToken).ConfigureAwait(false);
        if (!start.Started)
        {
            ConsoleUi.Failure(start.FailureReason ?? "The capture did not start.");
            foreach (ProviderEnablementResult provider in start.Providers)
            {
                if (!provider.Enabled)
                {
                    ConsoleUi.Bullet($"{provider.SourceId}: {provider.FailureReason}");
                }
            }

            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        ConsoleUi.Success($"Recording. {start.OtherActiveSessionCount} other ETW sessions continue untouched.");

        var pipeObservations = new List<PipeOperationObservation>();
        var processObservations = new List<ProcessInstanceObservation>();
        int overBudget = 0;
        EventAdmissionTable table = session.AdmissionTable;
        CaptureId captureId = identity.CaptureId;
        Task consumer = Task.Run(
            async () =>
            {
                await foreach (AdmittedEvent admitted in session.Records.ReadAllAsync(CancellationToken.None)
                    .ConfigureAwait(false))
                {
                    AdmittedEventPlan? plan = table.FindBySourceIndex(
                        admitted.SourceIndex,
                        admitted.EventId,
                        admitted.Version);
                    if (plan is null)
                    {
                        continue;
                    }

                    if (pipeObservations.Count + processObservations.Count >= ObservationBudget)
                    {
                        overBudget++;
                        continue;
                    }

                    PipeOperationObservation? pipe =
                        PipeObservationBuilder.TryBuild(admitted, plan, captureId, 1, 1);
                    if (pipe is not null)
                    {
                        pipeObservations.Add(pipe);
                        continue;
                    }

                    ProcessInstanceObservation? process =
                        ProcessObservationBuilder.TryBuild(admitted, plan, captureId, 1, 1);
                    if (process is not null)
                    {
                        processObservations.Add(process);
                    }
                }
            },
            CancellationToken.None);

        ConsoleUi.Progress($"Running FX-PIPE-001: {messages} messages, seed {seed}.");
        int workloadExit = await RunPipeWorkloadAsync(
            workload,
            outputDirectory,
            seed,
            messages,
            maximumBytes,
            cancellationToken).ConfigureAwait(false);
        if (workloadExit != 0)
        {
            ConsoleUi.Warn($"The workload exited with code {workloadExit}; its truth log may be incomplete.");
        }

        ConsoleUi.Progress($"Waiting {graceSeconds}s of reorder grace before stopping the session.");
        await Task.Delay(TimeSpan.FromSeconds(graceSeconds), cancellationToken).ConfigureAwait(false);
        CaptureStopResult stop = await session.StopAsync(cancellationToken).ConfigureAwait(false);
        await consumer.ConfigureAwait(false);
        ConsoleUi.Success($"Stopped. State {stop.State}, {stop.Health.AdmittedRecords} admitted records.");

        IReadOnlyList<TruthRecord> truth = await ReadTruthAsync(outputDirectory, cancellationToken).ConfigureAwait(false);
        string? pipePath = ReadPipePath(outputDirectory, truth);
        if (truth.Count == 0 || pipePath is null)
        {
            ConsoleUi.Failure("No truth records or no pipe path were produced, so nothing can be measured.");
            return InterCatExitCode.CorruptedInput;
        }

        var settings = new PipeCoverageSettings
        {
            FixtureId = "FX-PIPE-001",
            PipePath = pipePath,
            BuildId = environment.BuildId,
            BuildIsSupported = environment.IsSupportedBuild,
            Reproduced = false,
        };
        PipeCoverageResult coverage = PipeCoverageEvaluator.Evaluate(truth, pipeObservations, settings);
        IReadOnlyList<ProcessEvidence> processes = BuildProcessEvidence(truth, processObservations);

        var notes = new List<string>
        {
            "This run is an M0 spike: the capture path admits a bounded field projection, not the journal "
            + "envelope of section 18.1. It is evidence for feasibility, not a frozen implementation.",
            "Named-pipe operations reach InterCat through the kernel file path, so every claim here is about "
            + "file-layer evidence for a pipe object, not about a dedicated pipe source.",
        };
        if (overBudget > 0)
        {
            notes.Add($"{overBudget} admitted records were not decoded because the run budget of {ObservationBudget} was reached.");
        }

        if (!environment.IsSupportedBuild)
        {
            notes.Add(
                $"Build {environment.BuildId} is outside the section 1.3 support matrix, so this result cannot "
                + "promote a mechanism beyond experimental evidence.");
        }

        var run = new PipeMeasurementRun
        {
            FixtureId = "FX-PIPE-001",
            RunId = runId,
            StartedUtc = started,
            CompletedUtc = DateTimeOffset.UtcNow,
            Environment = environment,
            AdapterVersion = CapabilityInventoryProbe.AdapterVersion,
            PipePath = pipePath,
            Session = new(
                identity.SessionName,
                identity.OwnershipToken.ToString("D", CultureInfo.InvariantCulture),
                identity.OwnerProcessId,
                identity.CaptureId.ToString(),
                sessionPlan.BufferSizeKilobytes,
                sessionPlan.QueueCapacityRecords,
                sessionPlan.ReorderGrace.TotalSeconds,
                sessionPlan.Providers,
                start.OtherActiveSessionCount),
            Providers = start.Providers,
            Health = stop.Health,
            Degradations = stop.Degradations,
            PlanRefusals = refusals,
            Coverage = coverage,
            Processes = processes,
            Notes = notes,
        };

        HashSet<int> fixtureProcessIds = [];
        foreach (ProcessEvidence process in processes)
        {
            fixtureProcessIds.Add(process.ProcessId);
        }

        await WritePipeEvidenceAsync(outputDirectory, pipeObservations, pipePath, fixtureProcessIds, cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "measurement.json"),
            JsonSerializer.Serialize(run, JsonContracts.Indented),
            cancellationToken).ConfigureAwait(false);

        CapabilityReport report = probe.Probe(
            environment,
            BuildPipeRuntimeEvidence(start, stop, coverage),
            new Dictionary<Mechanism, MechanismMeasurement> { [Mechanism.NamedPipe] = coverage.Measurement });
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "capability-report.json"),
            JsonSerializer.Serialize(report, JsonContracts.Indented),
            cancellationToken).ConfigureAwait(false);

        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(run, JsonContracts.Indented));
        }
        else
        {
            RenderPipe(run);
        }

        ConsoleUi.Success($"Run artifacts written to {outputDirectory}");
        return stop.Health.IsLossFree && coverage.Assessment.Gaps.Count == 0
            ? InterCatExitCode.Success
            : InterCatExitCode.PartialResultSuccess;
    }

    private static void RenderPipe(PipeMeasurementRun run)
    {
        ConsoleUi.Heading("Capture");
        ConsoleUi.Field("Session", run.Session.SessionName);
        ConsoleUi.Field("Pipe", run.PipePath);
        ConsoleUi.Field("Other sessions", $"{run.Session.OtherActiveSessionsAtStart} left running");
        foreach (ProviderEnablementResult provider in run.Providers)
        {
            ConsoleUi.Field(
                "Provider",
                $"{provider.SourceId}: {(provider.Enabled ? "enabled" : "refused: " + provider.FailureReason)}");
        }

        ConsoleUi.Heading("Health ledger");
        CaptureHealthSnapshot health = run.Health;
        ConsoleUi.Field("Records observed", ConsoleUi.Count(health.ObservedRecords));
        ConsoleUi.Field("Records admitted", ConsoleUi.Count(health.AdmittedRecords));
        ConsoleUi.Field("Omitted: not admitted", ConsoleUi.Count(health.PolicyOmissionsDescriptorNotAdmitted));
        ConsoleUi.Field("Undecodable: short body", ConsoleUi.Count(health.UndecodableBodyShorterThanSchema));
        ConsoleUi.Field("Undecodable: version", ConsoleUi.Count(health.UndecodableUnknownDescriptorVersion));
        ConsoleUi.Field("Undecodable: pointer width", ConsoleUi.Count(health.UndecodablePointerWidthMismatch));
        ConsoleUi.Field("Provider-reported loss", ConsoleUi.Count(health.ProviderReportedEventLoss));
        ConsoleUi.Field("Consumer buffer loss", ConsoleUi.Count(health.ConsumerReportedBufferLoss));
        ConsoleUi.Field("Application drops", ConsoleUi.Count(health.ApplicationDrops));

        ConsoleUi.Heading("Measured coverage against the truth log");
        MechanismMeasurement measurement = run.Coverage.Measurement;
        ConsoleUi.Field("Truth operations", ConsoleUi.Count(measurement.TruthOperations));
        ConsoleUi.Field("With observation", ConsoleUi.Count(measurement.TruthOperationsWithAdmittedObservation));
        ConsoleUi.Field("Bound to an instance", ConsoleUi.Count(measurement.TruthOperationsBoundToResourceInstance));
        ConsoleUi.Field("Byte measured", ConsoleUi.Count(measurement.ByteMeasuredOperations));
        ConsoleUi.Field("Peer attributions", ConsoleUi.Count(measurement.PeerAttributions));
        ConsoleUi.Field("False attributions", ConsoleUi.Count(measurement.FalsePeerAttributions));
        ConsoleUi.Field("Requested bytes", ConsoleUi.Bytes(run.Coverage.ObservedRequestedBytes));
        ConsoleUi.Field("Completed bytes", ConsoleUi.Bytes(run.Coverage.ObservedCompletedBytes));
        ConsoleUi.Field(
            "Requested above completed",
            $"{run.Coverage.OperationsWithRequestedAboveCompleted} operations");
        ConsoleUi.Field("Without a completion", $"{run.Coverage.MatchedOperationsWithoutCompletion} operations");
        ConsoleUi.Field(
            "Fixture ops unnamed",
            $"{run.Coverage.FixtureProcessOperationsWithoutResource} operations from the fixture's processes");
        ConsoleUi.Field(
            "Control records",
            $"{run.Coverage.ControlRecordsFromFixtureProcesses} records from the fixture's processes");
        ConsoleUi.Note("Requested and completed bytes are different domains and are never summed (P3, I6).");

        var criteriaRows = new List<IReadOnlyList<string>>(run.Coverage.Assessment.Criteria.Count);
        foreach (TierCriterion criterion in run.Coverage.Assessment.Criteria)
        {
            criteriaRows.Add(
            [
                criterion.Name,
                $"{criterion.Numerator}/{criterion.Denominator}",
                ConsoleUi.Ratio(criterion.Measured),
                criterion.Satisfied ? "met" : "not met",
            ]);
        }

        ConsoleUi.Line();
        ConsoleUi.Table(["Criterion", "Counted", "Measured", "Result"], criteriaRows);
        ConsoleUi.Line();
        ConsoleUi.Field("Computed tier", run.Coverage.Assessment.Tier.ToString());
        foreach (string gap in run.Coverage.Assessment.Gaps)
        {
            ConsoleUi.Bullet(gap);
        }

        if (run.Degradations.Count > 0)
        {
            ConsoleUi.Heading("Degradations");
            foreach (string degradation in run.Degradations)
            {
                ConsoleUi.Bullet(degradation);
            }
        }

        ConsoleUi.Heading("Notes");
        foreach (string note in run.Notes)
        {
            ConsoleUi.Bullet(note);
        }

        foreach (string note in run.Coverage.Notes)
        {
            ConsoleUi.Bullet(note);
        }
    }

    private static List<SourceRuntimeEvidence> BuildPipeRuntimeEvidence(
        CaptureStartResult start,
        CaptureStopResult stop,
        PipeCoverageResult coverage)
    {
        var evidence = new List<SourceRuntimeEvidence>(start.Providers.Count);
        foreach (ProviderEnablementResult provider in start.Providers)
        {
            bool file = provider.SourceId == WindowsSourceCatalog.KernelFileSourceId;
            bool admitted = stop.Health.AdmittedRecords > 0;
            evidence.Add(new(
                provider.SourceId,
                provider.Enabled ? CheckOutcome.Passed : CheckOutcome.Failed,
                provider.Enabled
                    ? "The session enabled this provider with the recorded level, keyword mask and event-id filter."
                    : provider.FailureReason ?? "Enablement was refused.",
                admitted ? CheckOutcome.Passed : CheckOutcome.Inconclusive,
                admitted
                    ? $"The capture admitted {stop.Health.AdmittedRecords} records with "
                        + $"{stop.Health.ProviderReportedEventLoss} provider-reported losses."
                    : "The provider was enabled but no matching record arrived, which is not an observed zero (R21).",
                file
                    ? coverage.Assessment.Gaps.Count == 0 ? CheckOutcome.Passed : CheckOutcome.Inconclusive
                    : CheckOutcome.NotAttempted,
                file
                    ? $"Measured against FX-PIPE-001: tier {coverage.Assessment.Tier}."
                    : "No truth workload targets this source in this run.",
                file ? ["FX-PIPE-001"] : [],
                provider.Enabled ? null : provider.FailureReason));
        }

        return evidence;
    }

    /// <summary>
    /// Writes only the operations that resolved to this fixture's pipe. Unrelated file activity from the
    /// rest of the machine never reaches a stored artifact (P16).
    /// </summary>
    private static async Task WritePipeEvidenceAsync(
        string directory,
        IReadOnlyList<PipeOperationObservation> observations,
        string pipePath,
        IReadOnlyCollection<int> fixtureProcessIds,
        CancellationToken cancellationToken)
    {
        HashSet<ulong> fixtureObjects = [];
        HashSet<ulong> fixtureKeys = [];
        foreach (PipeOperationObservation observation in observations)
        {
            if (!string.Equals(observation.ResourceName, pipePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (observation.FileObject != 0)
            {
                fixtureObjects.Add(observation.FileObject);
            }

            if (observation.FileKey != 0)
            {
                fixtureKeys.Add(observation.FileKey);
            }
        }

        HashSet<ulong> fixtureIrps = [];
        foreach (PipeOperationObservation observation in observations)
        {
            if ((fixtureObjects.Contains(observation.FileObject) || fixtureKeys.Contains(observation.FileKey))
                && observation.IrpKey != 0)
            {
                fixtureIrps.Add(observation.IrpKey);
            }
        }

        await using var writer = new StreamWriter(Path.Combine(directory, "observations.jsonl"), append: false);
        foreach (PipeOperationObservation observation in observations)
        {
            bool inScope = fixtureProcessIds.Contains(observation.ProcessId)
                || string.Equals(observation.ResourceName, pipePath, StringComparison.OrdinalIgnoreCase)
                || fixtureObjects.Contains(observation.FileObject)
                || fixtureKeys.Contains(observation.FileKey)
                || (observation.Kind == ObservationKind.RequestEnd && fixtureIrps.Contains(observation.IrpKey));
            if (!inScope)
            {
                continue;
            }

            await writer
                .WriteLineAsync(JsonSerializer.Serialize(observation, JsonContracts.Compact).AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static string? ReadPipePath(string directory, IReadOnlyList<TruthRecord> truth)
    {
        string scenarioPath = Path.Combine(directory, "scenario.json");
        if (File.Exists(scenarioPath))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(scenarioPath));
            if (document.RootElement.TryGetProperty("pipePath", out JsonElement value))
            {
                return value.GetString();
            }
        }

        foreach (TruthRecord record in truth)
        {
            if (record.ResourceName is not null)
            {
                return record.ResourceName;
            }
        }

        return null;
    }

    private static async Task<int> RunPipeWorkloadAsync(
        string workload,
        string truthDirectory,
        int seed,
        int messages,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(workload) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("pipe-loopback");
        start.ArgumentList.Add("--truth");
        start.ArgumentList.Add(truthDirectory);
        start.ArgumentList.Add("--seed");
        start.ArgumentList.Add(seed.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--messages");
        start.ArgumentList.Add(messages.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--bytes");
        start.ArgumentList.Add(maximumBytes.ToString(CultureInfo.InvariantCulture));

        using Process? process = Process.Start(start);
        if (process is null)
        {
            return -1;
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }
}
