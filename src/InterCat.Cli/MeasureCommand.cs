using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Capture.Windows;
using InterCat.Domain;

namespace InterCat.Cli;

/// <summary>What the capture was asked to do, recorded beside the result so a run can be repeated (I16).</summary>
internal sealed record SessionRecord(
    string SessionName,
    string OwnershipToken,
    int OwnerProcessId,
    string CaptureId,
    int BufferSizeKilobytes,
    int QueueCapacityRecords,
    double ReorderGraceSeconds,
    IReadOnlyList<ProviderEnablementRequest> RequestedProviders,
    int OtherActiveSessionsAtStart);

/// <summary>Whether a truth process was observed through the lifecycle source, and with what start key.</summary>
internal sealed record ProcessEvidence(
    int ProcessId,
    string Role,
    bool ObservedStart,
    bool ObservedExit,
    long? SequenceNumber,
    long? CreateFileTime);

/// <summary>The complete machine-readable result of one measured run.</summary>
internal sealed record MeasurementRun
{
    public required string FixtureId { get; init; }
    public required string RunId { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public required DateTimeOffset CompletedUtc { get; init; }
    public required ProbeEnvironment Environment { get; init; }
    public required string AdapterVersion { get; init; }
    public required SessionRecord Session { get; init; }
    public required IReadOnlyList<ProviderEnablementResult> Providers { get; init; }
    public required CaptureHealthSnapshot Health { get; init; }
    public required IReadOnlyList<string> Degradations { get; init; }
    public required IReadOnlyList<string> PlanRefusals { get; init; }
    public required TcpCoverageResult Coverage { get; init; }
    public required IReadOnlyList<ProcessEvidence> Processes { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// A transport truth workload `icat measure` can run: its fixture, the workload verb and the option that sets how many
/// flows it opens, and the mechanism its measurement is credited to.
/// </summary>
internal sealed record TransportScenario(
    string FixtureId,
    string Verb,
    Mechanism Mechanism,
    string SessionPrefix,
    string CountOption,
    string CountNoun,
    int DefaultSeed,
    int DefaultBytes)
{
    public static TransportScenario Tcp { get; } =
        new("FX-TCP-001", "tcp-loopback", Mechanism.Tcp, "m0tcp", "--connections", "connections", 20_260_920, 4_096);

    public static TransportScenario Udp { get; } =
        new("FX-UDP-001", "udp-loopback", Mechanism.Udp, "m1udp", "--sockets", "sockets", 20_260_923, 1_400);
}

/// <summary>
/// `icat measure tcp|udp`: runs the seeded truth workload under an owned ETW session and reports measured
/// coverage against the independent truth log (IC-003, IC-004, section 14.2).
/// </summary>
internal static partial class MeasureCommand
{
    private const int ObservationBudget = 500_000;

    public static async Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string mechanism = command.TakePositional() ?? "tcp";
        if (string.Equals(mechanism, "pipe", StringComparison.OrdinalIgnoreCase))
        {
            return await RunPipeAsync(command, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(mechanism, "rpc", StringComparison.OrdinalIgnoreCase))
        {
            return await RunRpcAsync(command, cancellationToken).ConfigureAwait(false);
        }

        TransportScenario? scenario = mechanism.ToUpperInvariant() switch
        {
            "TCP" => TransportScenario.Tcp,
            "UDP" => TransportScenario.Udp,
            _ => null,
        };
        if (scenario is null)
        {
            ConsoleUi.Failure(
                $"Only 'tcp', 'udp', 'pipe' and 'rpc' are measurable in this milestone. Unknown mechanism: {mechanism}");
            return InterCatExitCode.InvalidInvocation;
        }

        string? outputOption = command.TakeOption("--output");
        string? workloadOption = command.TakeOption("--workload");
        int seed = command.TakeIntegerOption("--seed") ?? scenario.DefaultSeed;
        int connections = command.TakeIntegerOption(scenario.CountOption) ?? 2;
        int messages = command.TakeIntegerOption("--messages") ?? 8;
        int maximumBytes = command.TakeIntegerOption("--bytes") ?? scenario.DefaultBytes;
        int graceSeconds = command.TakeIntegerOption("--grace") ?? 2;
        bool json = command.TryTakeFlag("--json");
        bool overwrite = command.TryTakeFlag("--overwrite");
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure($"Unknown or incomplete option: {unknown}");
            PrintHelp();
            return InterCatExitCode.InvalidInvocation;
        }

        var host = new TraceEventSessionHost();
        ProbeEnvironment environment = CapabilityInventoryProbe.DescribeEnvironment(host.IsElevated);
        if (!environment.IsElevated)
        {
            ConsoleUi.Failure(
                "Starting an ETW session requires elevation. Run this command from an elevated shell; "
                + "`icat capabilities` still works without it.");
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
            outputOption ?? Path.Combine("fixtures", scenario.FixtureId, "runs", runId));
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any() && !overwrite)
        {
            ConsoleUi.Failure($"{outputDirectory} already contains files. Pass --overwrite to replace them.");
            return InterCatExitCode.InvalidInvocation;
        }

        Directory.CreateDirectory(outputDirectory);

        var probe = new CapabilityInventoryProbe(new TdhEtwMetadataSource());
        string[] sourceIds = [WindowsSourceCatalog.KernelProcessSourceId, WindowsSourceCatalog.KernelNetworkSourceId];
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

        CaptureSessionIdentity identity = CaptureSessionIdentity.Create(scenario.SessionPrefix, System.Environment.ProcessId);
        var sessionPlan = new OwnedSessionPlan
        {
            Identity = identity,
            Sources = plans,
            Providers = BuildProviderRequests(plans),
            ReorderGrace = TimeSpan.FromSeconds(graceSeconds),
            MaximumDuration = TimeSpan.FromMinutes(2),
        };

        DateTimeOffset started = DateTimeOffset.UtcNow;
        await using var session = new OwnedCaptureSession(sessionPlan, host);
        ConsoleUi.Progress($"Starting owned session {identity.SessionName}.");
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

        var networkObservations = new List<NetworkTransferObservation>();
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

                    if (networkObservations.Count + processObservations.Count >= ObservationBudget)
                    {
                        overBudget++;
                        continue;
                    }

                    NetworkTransferObservation? network =
                        NetworkObservationBuilder.TryBuild(admitted, plan, captureId, 1, 1);
                    if (network is not null)
                    {
                        networkObservations.Add(network);
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

        ConsoleUi.Progress(
            $"Running {scenario.FixtureId}: {connections} {scenario.CountNoun}, {messages} messages each, seed {seed}.");
        int workloadExit = await RunWorkloadAsync(
            workload,
            scenario,
            outputDirectory,
            seed,
            connections,
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
        if (truth.Count == 0)
        {
            ConsoleUi.Failure("No truth records were produced, so nothing can be measured.");
            return InterCatExitCode.CorruptedInput;
        }

        var settings = new TcpCoverageSettings
        {
            FixtureId = scenario.FixtureId,
            BuildId = environment.BuildId,
            BuildIsSupported = environment.IsSupportedBuild,
            Reproduced = false,
        };
        TcpCoverageResult coverage = TcpCoverageEvaluator.Evaluate(truth, networkObservations, settings);
        IReadOnlyList<ProcessEvidence> processes = BuildProcessEvidence(truth, processObservations);

        var notes = new List<string>
        {
            "This run is an M0 spike: the capture path admits a bounded field projection, not the journal "
            + "envelope of section 18.1. It is evidence for feasibility, not a frozen implementation.",
            "The capability tier is computed from these counters alone; no mechanism is promoted by argument (P27).",
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

        var run = new MeasurementRun
        {
            FixtureId = scenario.FixtureId,
            RunId = runId,
            StartedUtc = started,
            CompletedUtc = DateTimeOffset.UtcNow,
            Environment = environment,
            AdapterVersion = CapabilityInventoryProbe.AdapterVersion,
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

        IReadOnlyList<NetworkTransferObservation> fixtureEvidence =
            TcpCoverageEvaluator.SelectFixtureEvidence(truth, networkObservations, settings);
        await WriteObservationsAsync(outputDirectory, fixtureEvidence, cancellationToken).ConfigureAwait(false);

        string measurementPath = Path.Combine(outputDirectory, "measurement.json");
        await File.WriteAllTextAsync(
            measurementPath,
            JsonSerializer.Serialize(run, JsonContracts.Indented),
            cancellationToken).ConfigureAwait(false);

        CapabilityReport report = probe.Probe(
            environment,
            BuildRuntimeEvidence(start, stop, coverage, scenario.FixtureId),
            new Dictionary<Mechanism, MechanismMeasurement> { [scenario.Mechanism] = coverage.Measurement });
        string reportPath = Path.Combine(outputDirectory, "capability-report.json");
        await File.WriteAllTextAsync(
            reportPath,
            JsonSerializer.Serialize(report, JsonContracts.Indented),
            cancellationToken).ConfigureAwait(false);

        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(run, JsonContracts.Indented));
        }
        else
        {
            Render(run);
        }

        ConsoleUi.Success($"Run artifacts written to {outputDirectory}");
        return stop.Health.IsLossFree && coverage.Assessment.Gaps.Count == 0
            ? InterCatExitCode.Success
            : InterCatExitCode.PartialResultSuccess;
    }

    private static void Render(MeasurementRun run)
    {
        ConsoleUi.Heading("Capture");
        ConsoleUi.Field("Session", run.Session.SessionName);
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
        ConsoleUi.Field("Omitted: denied", ConsoleUi.Count(health.PolicyOmissionsDescriptorDenied));
        ConsoleUi.Field("Undecodable: short body", ConsoleUi.Count(health.UndecodableBodyShorterThanSchema));
        ConsoleUi.Field("Undecodable: version", ConsoleUi.Count(health.UndecodableUnknownDescriptorVersion));
        ConsoleUi.Field("Provider-reported loss", ConsoleUi.Count(health.ProviderReportedEventLoss));
        ConsoleUi.Field("Consumer buffer loss", ConsoleUi.Count(health.ConsumerReportedBufferLoss));
        ConsoleUi.Field("Application drops", ConsoleUi.Count(health.ApplicationDrops));
        ConsoleUi.Note("Counters are independent quantities and are never added into one total.");

        ConsoleUi.Heading("Process evidence");
        var processRows = new List<IReadOnlyList<string>>(run.Processes.Count);
        foreach (ProcessEvidence process in run.Processes)
        {
            processRows.Add(
            [
                process.Role,
                process.ProcessId.ToString(CultureInfo.CurrentCulture),
                process.ObservedStart ? "yes" : "no",
                process.ObservedExit ? "yes" : "no",
                process.SequenceNumber?.ToString(CultureInfo.CurrentCulture) ?? "unknown",
            ]);
        }

        ConsoleUi.Table(["Role", "PID", "Start observed", "Exit observed", "Sequence number"], processRows);

        ConsoleUi.Heading("Measured coverage against the truth log");
        MechanismMeasurement measurement = run.Coverage.Measurement;
        ConsoleUi.Field("Truth operations", ConsoleUi.Count(measurement.TruthOperations));
        ConsoleUi.Field("With observation", ConsoleUi.Count(measurement.TruthOperationsWithAdmittedObservation));
        ConsoleUi.Field("Bound to a flow", ConsoleUi.Count(measurement.TruthOperationsBoundToResourceInstance));
        ConsoleUi.Field("Byte measured", ConsoleUi.Count(measurement.ByteMeasuredOperations));
        ConsoleUi.Field("Peer attributions", ConsoleUi.Count(measurement.PeerAttributions));
        ConsoleUi.Field("False attributions", ConsoleUi.Count(measurement.FalsePeerAttributions));
        ConsoleUi.Field("Truth bytes sent", ConsoleUi.Bytes(run.Coverage.TruthBytesSent));
        ConsoleUi.Field("Observed transport bytes", ConsoleUi.Bytes(run.Coverage.ObservedBytesSent));

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

    private static List<ProviderEnablementRequest> BuildProviderRequests(IReadOnlyList<SourceAdmissionPlan> plans)
        => [.. ProviderEnablementCompiler.Compile(plans)];

    private static List<SourceRuntimeEvidence> BuildRuntimeEvidence(
        CaptureStartResult start,
        CaptureStopResult stop,
        TcpCoverageResult coverage,
        string fixtureId)
    {
        var evidence = new List<SourceRuntimeEvidence>(start.Providers.Count);
        foreach (ProviderEnablementResult provider in start.Providers)
        {
            bool network = provider.SourceId == WindowsSourceCatalog.KernelNetworkSourceId;
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
                network
                    ? coverage.Assessment.Gaps.Count == 0 ? CheckOutcome.Passed : CheckOutcome.Inconclusive
                    : CheckOutcome.NotAttempted,
                network
                    ? $"Measured against {fixtureId}: tier {coverage.Assessment.Tier}."
                    : "No truth workload targets this source in this run.",
                network ? [fixtureId] : [],
                provider.Enabled ? null : provider.FailureReason));
        }

        return evidence;
    }

    private static List<ProcessEvidence> BuildProcessEvidence(
        IReadOnlyList<TruthRecord> truth,
        IReadOnlyList<ProcessInstanceObservation> observations)
    {
        Dictionary<int, string> roles = [];
        foreach (TruthRecord record in truth)
        {
            roles[record.ProcessId] = record.Role;
        }

        var evidence = new List<ProcessEvidence>(roles.Count);
        foreach ((int processId, string role) in roles)
        {
            bool observedStart = false;
            bool observedExit = false;
            long? sequence = null;
            long? createTime = null;
            foreach (ProcessInstanceObservation observation in observations)
            {
                if (observation.ProcessId != processId)
                {
                    continue;
                }

                if (observation.Kind is ObservationKind.Create or ObservationKind.Inventory)
                {
                    observedStart = true;
                    sequence ??= observation.SequenceNumber;
                    createTime ??= observation.CreateFileTime;
                }
                else if (observation.Kind == ObservationKind.Exit)
                {
                    observedExit = true;
                    sequence ??= observation.SequenceNumber;
                    createTime ??= observation.CreateFileTime;
                }
            }

            evidence.Add(new(processId, role, observedStart, observedExit, sequence, createTime));
        }

        return evidence;
    }

    private static async Task<int> RunWorkloadAsync(
        string workload,
        TransportScenario scenario,
        string truthDirectory,
        int seed,
        int connections,
        int messages,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(workload) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(scenario.Verb);
        start.ArgumentList.Add("--truth");
        start.ArgumentList.Add(truthDirectory);
        start.ArgumentList.Add("--seed");
        start.ArgumentList.Add(seed.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(scenario.CountOption);
        start.ArgumentList.Add(connections.ToString(CultureInfo.InvariantCulture));
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

    /// <summary>
    /// Writes the admitted observations beside the result. Raw evidence is what makes a measured claim
    /// checkable rather than asserted (section 13.5).
    /// </summary>
    private static async Task WriteObservationsAsync(
        string directory,
        IReadOnlyList<NetworkTransferObservation> observations,
        CancellationToken cancellationToken)
    {
        const int evidenceLimit = 20_000;
        await using var writer = new StreamWriter(Path.Combine(directory, "observations.jsonl"), append: false);
        int written = 0;
        foreach (NetworkTransferObservation observation in observations)
        {
            if (written++ == evidenceLimit)
            {
                break;
            }

            await writer.WriteLineAsync(JsonSerializer.Serialize(observation, JsonContracts.Compact).AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<TruthRecord>> ReadTruthAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        var records = new List<TruthRecord>();
        foreach (string path in Directory.EnumerateFiles(directory, "truth-*.jsonl"))
        {
            foreach (string line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                TruthRecord? record = JsonSerializer.Deserialize<TruthRecord>(line, JsonContracts.Compact);
                if (record is not null)
                {
                    records.Add(record);
                }
            }
        }

        return records;
    }

    private static string? ResolveWorkload(string? explicitPath)
    {
        if (explicitPath is not null)
        {
            return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;
        }

        const string executableName = "InterCat.TestWorkloads.exe";
        string beside = Path.Combine(AppContext.BaseDirectory, executableName);
        if (File.Exists(beside))
        {
            return beside;
        }

        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "InterCat.slnx")))
        {
            current = current.Parent;
        }

        if (current is null)
        {
            return null;
        }

        foreach (string configuration in (string[])["Debug", "Release"])
        {
            string candidate = Path.Combine(
                current.FullName,
                "src",
                "InterCat.TestWorkloads",
                "bin",
                configuration,
                "net10.0",
                executableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat measure tcp [--output <dir>] [--seed <n>] [--connections <n>] [--messages <n>]");
        ConsoleUi.Line("                 [--bytes <n>] [--grace <seconds>] [--workload <path>] [--overwrite] [--json]");
        ConsoleUi.Line("icat measure udp [--output <dir>] [--seed <n>] [--sockets <n>] [--messages <n>]");
        ConsoleUi.Line("                 [--bytes <n>] [--grace <seconds>] [--workload <path>] [--overwrite] [--json]");
        ConsoleUi.Line();
        ConsoleUi.Line("  Starts one uniquely named ETW session, runs FX-TCP-001 or FX-UDP-001, stops the session it");
        ConsoleUi.Line("  created, and measures the section 14.2 thresholds against the workload's independent truth log.");
        ConsoleUi.Line("  Other ETW sessions on the machine are never stopped or adopted.");
    }
}
