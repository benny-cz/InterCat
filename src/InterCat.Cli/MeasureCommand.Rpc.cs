using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Capture.Windows;
using InterCat.Domain;

namespace InterCat.Cli;

/// <summary>The complete machine-readable result of one measured local RPC run.</summary>
internal sealed record RpcMeasurementRun
{
    public required string FixtureId { get; init; }
    public required string RunId { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public required DateTimeOffset CompletedUtc { get; init; }
    public required ProbeEnvironment Environment { get; init; }
    public required string AdapterVersion { get; init; }
    public required SessionRecord Session { get; init; }
    public required string ExpectedInterface { get; init; }
    public required int ClientProcessId { get; init; }
    public required int ExpectedServerProcessId { get; init; }
    public required IReadOnlyList<ProviderEnablementResult> Providers { get; init; }
    public required CaptureHealthSnapshot Health { get; init; }
    public required IReadOnlyList<string> Degradations { get; init; }
    public required IReadOnlyList<string> PlanRefusals { get; init; }
    public required RpcCoverageResult Coverage { get; init; }
    public required bool RepeatRunObservedCalls { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// `icat measure rpc`: runs the local RPC truth workload twice under one owned ETW session and reports
/// measured coverage for the RPC mechanism (IC-006, section 14.2). The repeat is what lets a result claim
/// reproducibility rather than assert it.
/// </summary>
internal static partial class MeasureCommand
{
    private static async Task<InterCatExitCode> RunRpcAsync(CommandLine command, CancellationToken cancellationToken)
    {
        string? outputOption = command.TakeOption("--output");
        string? workloadOption = command.TakeOption("--workload");
        string serviceName = command.TakeOption("--service") ?? "Schedule";
        int calls = command.TakeIntegerOption("--calls") ?? 12;
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
            outputOption ?? Path.Combine("fixtures", "FX-RPC-001", "runs", runId));
        if (Directory.Exists(outputDirectory)
            && Directory.EnumerateFileSystemEntries(outputDirectory).Any()
            && !overwrite)
        {
            ConsoleUi.Failure($"{outputDirectory} already contains files. Pass --overwrite to replace them.");
            return InterCatExitCode.InvalidInvocation;
        }

        Directory.CreateDirectory(outputDirectory);

        var probe = new CapabilityInventoryProbe(new TdhEtwMetadataSource());
        string[] sourceIds = [WindowsSourceCatalog.KernelProcessSourceId, WindowsSourceCatalog.RpcSourceId];
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

        CaptureSessionIdentity identity = CaptureSessionIdentity.Create("m0rpc", System.Environment.ProcessId);
        var sessionPlan = new OwnedSessionPlan
        {
            Identity = identity,
            Sources = plans,
            Providers = BuildProviderRequests(plans),
            ReorderGrace = TimeSpan.FromSeconds(graceSeconds),
            MaximumDuration = TimeSpan.FromMinutes(2),
            QueueCapacityRecords = 262_144,
        };

        DateTimeOffset started = DateTimeOffset.UtcNow;
        await using var session = new OwnedCaptureSession(sessionPlan, host);
        ConsoleUi.Progress($"Starting owned session {identity.SessionName}.");
        ConsoleUi.Warn(
            "RPC activity is captured machine-wide for the duration of this run, because the callee is a "
            + "Windows service host. Payload-bearing debug descriptors stay denied.");
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

        var rpcObservations = new List<RpcCallObservation>();
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

                    if (rpcObservations.Count >= ObservationBudget)
                    {
                        overBudget++;
                        continue;
                    }

                    RpcCallObservation? call = RpcObservationBuilder.TryBuild(admitted, plan, captureId, 1, 1);
                    if (call is not null)
                    {
                        rpcObservations.Add(call);
                    }
                }
            },
            CancellationToken.None);

        string firstDirectory = Path.Combine(outputDirectory, "repeat-1");
        string secondDirectory = Path.Combine(outputDirectory, "repeat-2");
        ConsoleUi.Progress($"Running FX-RPC-001 twice: {calls} calls against the {serviceName} service.");
        int firstExit = await RunRpcWorkloadAsync(workload, firstDirectory, calls, serviceName, cancellationToken)
            .ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false);
        int secondExit = await RunRpcWorkloadAsync(workload, secondDirectory, calls, serviceName, cancellationToken)
            .ConfigureAwait(false);
        if (firstExit != 0 || secondExit != 0)
        {
            ConsoleUi.Warn($"A workload run exited with code {firstExit} and {secondExit}; a truth log may be incomplete.");
        }

        ConsoleUi.Progress($"Waiting {graceSeconds}s of reorder grace before stopping the session.");
        await Task.Delay(TimeSpan.FromSeconds(graceSeconds), cancellationToken).ConfigureAwait(false);
        CaptureStopResult stop = await session.StopAsync(cancellationToken).ConfigureAwait(false);
        await consumer.ConfigureAwait(false);
        ConsoleUi.Success($"Stopped. State {stop.State}, {stop.Health.AdmittedRecords} admitted records.");

        RpcScenario? firstScenario = await ReadRpcScenarioAsync(firstDirectory, cancellationToken).ConfigureAwait(false);
        RpcScenario? secondScenario = await ReadRpcScenarioAsync(secondDirectory, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TruthRecord> firstTruth = await ReadTruthAsync(firstDirectory, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TruthRecord> secondTruth = await ReadTruthAsync(secondDirectory, cancellationToken).ConfigureAwait(false);
        if (firstScenario is null || firstTruth.Count == 0)
        {
            ConsoleUi.Failure("No truth records were produced, so nothing can be measured.");
            return InterCatExitCode.CorruptedInput;
        }

        RpcCoverageResult second = secondScenario is null || secondTruth.Count == 0
            ? EmptyRepeat()
            : RpcCoverageEvaluator.Evaluate(secondTruth, rpcObservations, SettingsFor(secondScenario, environment, false));
        bool repeatObserved = second.Measurement.TruthOperationsWithAdmittedObservation > 0;

        RpcCoverageResult coverage = RpcCoverageEvaluator.Evaluate(
            firstTruth,
            rpcObservations,
            SettingsFor(firstScenario, environment, repeatObserved));

        var notes = new List<string>
        {
            "This run is an M0 spike: the capture path admits a bounded field projection, not the journal "
            + "envelope of section 18.1. It is evidence for feasibility, not a frozen implementation.",
            "The workload issues one logged call per service query, while the Windows API behind it makes "
            + "several calls to the same interface. A truth call therefore answers to at least one observed call.",
            repeatObserved
                ? "The fixture ran twice in this capture and both runs produced observations, so the result is reproducible within this build."
                : "The repeat run produced no observation, so this result is not yet reproducible.",
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

        var run = new RpcMeasurementRun
        {
            FixtureId = "FX-RPC-001",
            RunId = runId,
            StartedUtc = started,
            CompletedUtc = DateTimeOffset.UtcNow,
            Environment = environment,
            AdapterVersion = CapabilityInventoryProbe.AdapterVersion,
            ExpectedInterface = firstScenario.ExpectedInterfaceUuid,
            ClientProcessId = firstScenario.ClientProcessId,
            ExpectedServerProcessId = firstScenario.ExpectedServerProcessId,
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
            RepeatRunObservedCalls = repeatObserved,
            Notes = notes,
        };

        await WriteRpcEvidenceAsync(outputDirectory, rpcObservations, firstScenario, cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "measurement.json"),
            JsonSerializer.Serialize(run, JsonContracts.Indented),
            cancellationToken).ConfigureAwait(false);

        CapabilityReport report = probe.Probe(
            environment,
            BuildRpcRuntimeEvidence(start, stop, coverage),
            new Dictionary<Mechanism, MechanismMeasurement> { [Mechanism.Rpc] = coverage.Measurement });
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
            RenderRpc(run);
        }

        ConsoleUi.Success($"Run artifacts written to {outputDirectory}");
        return stop.Health.IsLossFree && coverage.Assessment.Gaps.Count == 0
            ? InterCatExitCode.Success
            : InterCatExitCode.PartialResultSuccess;
    }

    private static RpcCoverageSettings SettingsFor(RpcScenario scenario, ProbeEnvironment environment, bool reproduced) => new()
    {
        ExpectedInterface = Guid.Parse(scenario.ExpectedInterfaceUuid),
        ClientProcessId = scenario.ClientProcessId,
        ExpectedServerProcessId = scenario.ExpectedServerProcessId,
        FixtureId = "FX-RPC-001",
        BuildId = environment.BuildId,
        BuildIsSupported = environment.IsSupportedBuild,
        Reproduced = reproduced,
    };

    private static RpcCoverageResult EmptyRepeat()
    {
        var measurement = new MechanismMeasurement
        {
            FixtureId = "FX-RPC-001",
            BuildId = "unknown",
            Reproduced = false,
            MeasuredOnSupportedBuild = false,
        };

        return new()
        {
            Measurement = measurement,
            Assessment = CoverageTierCalculator.Assess(measurement),
            Calls = [],
            PeerAttributions = [],
            CompletionsPaired = 0,
            ServerSideRecords = 0,
            ObservationsOutsideScope = 0,
            ControlRecordsFromFixtureProcess = 0,
            Notes = ["The repeat run produced no truth log."],
        };
    }

    private static void RenderRpc(RpcMeasurementRun run)
    {
        ConsoleUi.Heading("Capture");
        ConsoleUi.Field("Session", run.Session.SessionName);
        ConsoleUi.Field("Interface", run.ExpectedInterface);
        ConsoleUi.Field("Client process", run.ClientProcessId.ToString(CultureInfo.CurrentCulture));
        ConsoleUi.Field("Expected callee", run.ExpectedServerProcessId.ToString(CultureInfo.CurrentCulture));
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
        ConsoleUi.Field("Omitted: denied", ConsoleUi.Count(health.PolicyOmissionsDescriptorDenied));
        ConsoleUi.Field("Undecodable: version", ConsoleUi.Count(health.UndecodableUnknownDescriptorVersion));
        ConsoleUi.Field("Provider-reported loss", ConsoleUi.Count(health.ProviderReportedEventLoss));
        ConsoleUi.Field("Application drops", ConsoleUi.Count(health.ApplicationDrops));

        ConsoleUi.Heading("Measured coverage against the truth log");
        MechanismMeasurement measurement = run.Coverage.Measurement;
        ConsoleUi.Field("Truth calls", ConsoleUi.Count(measurement.TruthOperations));
        ConsoleUi.Field("With observation", ConsoleUi.Count(measurement.TruthOperationsWithAdmittedObservation));
        ConsoleUi.Field("Bound to interface", ConsoleUi.Count(measurement.TruthOperationsBoundToResourceInstance));
        ConsoleUi.Field("Completions paired", ConsoleUi.Count(run.Coverage.CompletionsPaired));
        ConsoleUi.Field("Peer attributions", ConsoleUi.Count(measurement.PeerAttributions));
        ConsoleUi.Field("False attributions", ConsoleUi.Count(measurement.FalsePeerAttributions));
        ConsoleUi.Field("Server-side records", ConsoleUi.Count(run.Coverage.ServerSideRecords));
        ConsoleUi.Field("Control records", ConsoleUi.Count(run.Coverage.ControlRecordsFromFixtureProcess));
        ConsoleUi.Field("Reproduced", run.RepeatRunObservedCalls ? "yes, in a second run" : "no");
        ConsoleUi.Note("This source exposes no size, so no byte value is reported for any call.");

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

    private static List<SourceRuntimeEvidence> BuildRpcRuntimeEvidence(
        CaptureStartResult start,
        CaptureStopResult stop,
        RpcCoverageResult coverage)
    {
        var evidence = new List<SourceRuntimeEvidence>(start.Providers.Count);
        foreach (ProviderEnablementResult provider in start.Providers)
        {
            bool rpc = provider.SourceId == WindowsSourceCatalog.RpcSourceId;
            bool admitted = stop.Health.AdmittedRecords > 0;
            evidence.Add(new(
                provider.SourceId,
                provider.Enabled ? CheckOutcome.Passed : CheckOutcome.Failed,
                provider.Enabled
                    ? "The session enabled this provider with the recorded level and event-id filter, and kept "
                        + "the payload-bearing debug descriptors denied."
                    : provider.FailureReason ?? "Enablement was refused.",
                admitted ? CheckOutcome.Passed : CheckOutcome.Inconclusive,
                admitted
                    ? $"The capture admitted {stop.Health.AdmittedRecords} records with "
                        + $"{stop.Health.ProviderReportedEventLoss} provider-reported losses."
                    : "The provider was enabled but no matching record arrived, which is not an observed zero (R21).",
                rpc
                    ? coverage.Assessment.Gaps.Count == 0 ? CheckOutcome.Passed : CheckOutcome.Inconclusive
                    : CheckOutcome.NotAttempted,
                rpc
                    ? $"Measured against FX-RPC-001: tier {coverage.Assessment.Tier}."
                    : "No truth workload targets this source in this run.",
                rpc ? ["FX-RPC-001"] : [],
                provider.Enabled ? null : provider.FailureReason));
        }

        return evidence;
    }

    /// <summary>
    /// Writes only the calls that belong to this fixture: the client process's own calls and the server
    /// records that share an activity with them. Unrelated machine RPC never reaches a stored artifact (P16).
    /// </summary>
    private static async Task WriteRpcEvidenceAsync(
        string directory,
        IReadOnlyList<RpcCallObservation> observations,
        RpcScenario scenario,
        CancellationToken cancellationToken)
    {
        HashSet<Guid> activities = [];
        foreach (RpcCallObservation observation in observations)
        {
            if (observation.ProcessId == scenario.ClientProcessId && observation.ActivityId != Guid.Empty)
            {
                activities.Add(observation.ActivityId);
            }
        }

        await using var writer = new StreamWriter(Path.Combine(directory, "observations.jsonl"), append: false);
        foreach (RpcCallObservation observation in observations)
        {
            if (observation.ProcessId != scenario.ClientProcessId && !activities.Contains(observation.ActivityId))
            {
                continue;
            }

            await writer
                .WriteLineAsync(JsonSerializer.Serialize(observation, JsonContracts.Compact).AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<RpcScenario?> ReadRpcScenarioAsync(string directory, CancellationToken cancellationToken)
    {
        string path = Path.Combine(directory, "scenario.json");
        if (!File.Exists(path))
        {
            return null;
        }

        string text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<RpcScenario>(text, JsonContracts.Compact);
    }

    private static async Task<int> RunRpcWorkloadAsync(
        string workload,
        string truthDirectory,
        int calls,
        string serviceName,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(workload) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("rpc-local");
        start.ArgumentList.Add("--truth");
        start.ArgumentList.Add(truthDirectory);
        start.ArgumentList.Add("--calls");
        start.ArgumentList.Add(calls.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--service");
        start.ArgumentList.Add(serviceName);

        using Process? process = Process.Start(start);
        if (process is null)
        {
            return -1;
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }
}

/// <summary>The scenario file the RPC workload writes, read back to scope the measurement.</summary>
internal sealed record RpcScenario
{
    public required string ScenarioId { get; init; }
    public required int Calls { get; init; }
    public required string ExpectedInterfaceUuid { get; init; }
    public required int ClientProcessId { get; init; }
    public required int ExpectedServerProcessId { get; init; }
}
