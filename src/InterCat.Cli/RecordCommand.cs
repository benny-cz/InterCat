using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Capture.Journal;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>What a recording captured and published, as a document a tool can read without this CLI.</summary>
internal sealed record RecordingDocument
{
    public required string Contract { get; init; }
    public required string Path { get; init; }
    public required string Profile { get; init; }
    public required string? Mechanism { get; init; }
    public required double RequestedSeconds { get; init; }
    public required string CaptureId { get; init; }
    public required long? Generation { get; init; }
    public required long JournaledRecords { get; init; }
    public required int Publications { get; init; }

    /// <summary>How many compaction generations coalesced the recording's small publications (ADR-026).</summary>
    public required int Compactions { get; init; }

    /// <summary>Why a compaction was not published, or null; the recording itself was, whole.</summary>
    public required string? CompactionFailure { get; init; }
    public required double? PublishEverySeconds { get; init; }
    public required CaptureHealthSnapshot? Health { get; init; }
    public required IReadOnlyList<string> Degradations { get; init; }
    public required IReadOnlyList<SessionMechanismCoverageDocument> Coverage { get; init; }
}

/// <summary>
/// `icat record`: captures live under an owned ETW session straight into a new session directory, which the session,
/// metric and process commands then read like any imported one (IC-014, M2). The capture profile decides what is
/// admitted; nothing outside it is enabled, and the session this command created is the only one it stops (P14).
/// </summary>
internal static class RecordCommand
{
    private const int DefaultSeconds = 10;
    private const int MaximumSeconds = 3_600;
    private const int DefaultPublishSeconds = 5;

    public static async Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? sessionPath = command.TakePositional();
        string profileOption = command.TakeOption("--profile") ?? "explore";
        string? mechanismOption = command.TakeOption("--mechanism");
        string? durationOption = command.TakeOption("--duration");
        string? publishOption = command.TakeOption("--publish-every");
        bool json = command.TryTakeFlag("--json");
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure($"Unknown or incomplete option: {unknown}");
            PrintHelp();
            return InterCatExitCode.InvalidInvocation;
        }

        if (sessionPath is null)
        {
            ConsoleUi.Failure("A new session directory is required: icat record <directory>");
            PrintHelp();
            return InterCatExitCode.InvalidInvocation;
        }

        CaptureProfileKind? profile = profileOption.ToUpperInvariant() switch
        {
            "EXPLORE" => CaptureProfileKind.Explore,
            "FOCUSED-TRANSPORT" or "FOCUSEDTRANSPORT" => CaptureProfileKind.FocusedTransport,
            _ => null,
        };
        if (profile is null)
        {
            ConsoleUi.Failure(
                $"--profile records explore or focused-transport; '{profileOption}' is not one. "
                + "icat profiles lists every intent and what it would collect.");
            return InterCatExitCode.InvalidInvocation;
        }

        Mechanism? mechanism = mechanismOption?.ToUpperInvariant() switch
        {
            null => null,
            "TCP" => Mechanism.Tcp,
            "UDP" => Mechanism.Udp,
            _ => (Mechanism)0,
        };
        if (mechanism == (Mechanism)0 || (mechanism is not null) != (profile == CaptureProfileKind.FocusedTransport))
        {
            ConsoleUi.Failure(
                "--mechanism tcp or udp names the transport of --profile focused-transport, and only of that profile.");
            return InterCatExitCode.InvalidInvocation;
        }

        int seconds = DefaultSeconds;
        if (durationOption is not null
            && (!int.TryParse(durationOption, NumberStyles.None, CultureInfo.InvariantCulture, out seconds)
                || seconds is < 1 or > MaximumSeconds))
        {
            ConsoleUi.Failure($"--duration is a whole number of seconds from 1 to {MaximumSeconds:N0}; '{durationOption}' is not one.");
            return InterCatExitCode.InvalidInvocation;
        }

        // Publishing as it records lets session, processes and metric read the capture while it runs; zero publishes
        // once, when the capture stops.
        int publishSeconds = DefaultPublishSeconds;
        if (publishOption is not null
            && (!int.TryParse(publishOption, NumberStyles.None, CultureInfo.InvariantCulture, out publishSeconds)
                || publishSeconds > MaximumSeconds))
        {
            ConsoleUi.Failure(
                $"--publish-every is a whole number of seconds up to {MaximumSeconds:N0}, or 0 to publish once at the end; "
                + $"'{publishOption}' is not one.");
            return InterCatExitCode.InvalidInvocation;
        }

        string full = Path.GetFullPath(sessionPath);
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
        {
            ConsoleUi.Failure(
                $"{full} is not empty. A recording is published into a new session, so it can never mix with another "
                + "capture's evidence; choose a new directory.");
            return InterCatExitCode.InvalidInvocation;
        }

        var host = new TraceEventSessionHost();
        ProbeEnvironment environment = CapabilityInventoryProbe.DescribeEnvironment(host.IsElevated);
        if (!environment.IsElevated)
        {
            ConsoleUi.Failure(
                "Starting an ETW session requires elevation. Run icat record from an elevated shell; icat profiles "
                + "previews what it would collect without it.");
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        ConsoleUi.Progress("Compiling the capture profile from the schemas this machine reports.");
        var inventory = new CapabilityInventoryProbe(new TdhEtwMetadataSource());
        EffectiveCapturePlan effective = CaptureProfileCompiler.Compile(
            new(profile.Value, FocusedMechanism: mechanism),
            inventory,
            environment);
        if (!effective.CanStart)
        {
            ConsoleUi.Failure("The profile cannot start on this machine, so nothing was recorded:");
            foreach (ProfileSourceDecision decision in effective.SourceDecisions.Where(decision => decision.State == ProfileSourceDecisionState.Blocking))
            {
                ConsoleUi.Bullet($"{decision.SourceId}: {decision.Reason}");
            }

            foreach (string diagnostic in effective.Diagnostics)
            {
                ConsoleUi.Bullet(diagnostic);
            }

            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        CaptureSessionIdentity identity = CaptureSessionIdentity.Create("m1record", System.Environment.ProcessId);
        var plan = new OwnedSessionPlan
        {
            Identity = identity,
            Sources = effective.Sources,
            Providers = effective.Providers,
            PreserveExtendedData = effective.PreserveExtendedData,
            MaximumDuration = TimeSpan.FromSeconds(seconds) + TimeSpan.FromMinutes(1),
        };

        Directory.CreateDirectory(full);
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(full), identity.CaptureId.Value, $"live-capture:{identity.CaptureId}");
        ConsoleUi.Progress(
            $"Recording {effective.EffectiveProfileId} into {full} for {seconds:N0} s under owned session "
            + $"{identity.SessionName}. "
            + (publishSeconds > 0
                ? $"It is published every {publishSeconds:N0} s, so other icat commands can read it while it records. "
                : "It is published when the capture stops. ")
            + "Ctrl+C stops early and keeps what was recorded.");
        LiveRecordingResult result = await LiveSessionRecorder.RecordAsync(
            plan,
            host,
            store,
            until => Task.Delay(TimeSpan.FromSeconds(seconds), until),
            DateTimeOffset.UtcNow,
            publishEvery: publishSeconds > 0 ? TimeSpan.FromSeconds(publishSeconds) : null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Start.Started)
        {
            ConsoleUi.Failure(result.Start.FailureReason ?? "The capture did not start.");
            foreach (ProviderEnablementResult provider in result.Start.Providers.Where(provider => !provider.Enabled))
            {
                ConsoleUi.Bullet($"{provider.SourceId}: {provider.FailureReason}");
            }

            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        var document = new RecordingDocument
        {
            Contract = "store-v1",
            Path = full,
            Profile = effective.EffectiveProfileId ?? profileOption,
            Mechanism = mechanism?.ToString(),
            RequestedSeconds = seconds,
            CaptureId = identity.CaptureId.ToString(),
            Generation = result.Generation?.Manifest.Generation,
            JournaledRecords = result.JournaledRecords,
            Publications = result.Publications,
            Compactions = result.Compactions,
            CompactionFailure = result.CompactionFailure,
            PublishEverySeconds = publishSeconds > 0 ? publishSeconds : null,
            Health = result.Stop?.Health,
            Degradations = result.Stop?.Degradations ?? [],
            Coverage = result.Coverage is null ? [] : CollectedCoverage(result.Coverage),
        };

        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(document, JsonContracts.Indented));
        }
        else
        {
            Render(document);
        }

        bool lossFree = result.Stop?.Health.IsLossFree ?? false;
        return lossFree && document.Degradations.Count == 0
            ? InterCatExitCode.Success
            : InterCatExitCode.PartialResultSuccess;
    }

    /// <summary>The coverage of each mechanism the capture collected; the rest were never collected by its profile.</summary>
    private static List<SessionMechanismCoverageDocument> CollectedCoverage(CoverageLedgerV1 ledger) =>
    [
        .. SessionCoverage.ByMechanism(ledger)
            .Where(entry => entry.State != CoverageState.NotCollected)
            .Select(entry => new SessionMechanismCoverageDocument
            {
                Mechanism = entry.Mechanism.ToString(),
                State = entry.State.ToString(),
                Reason = entry.Reason,
            }),
    ];

    private static void Render(RecordingDocument document)
    {
        ConsoleUi.Heading("Recording");
        ConsoleUi.Field("Session", document.Path);
        ConsoleUi.Field("Profile", document.Mechanism is null ? document.Profile : $"{document.Profile} ({document.Mechanism})");
        ConsoleUi.Field("Generation", document.Generation is { } generation ? ConsoleUi.Count(generation) : "none published");
        ConsoleUi.Field("Journaled records", ConsoleUi.Count(document.JournaledRecords));
        ConsoleUi.Field(
            "Published",
            document.Publications == 1
                ? "once, when the capture stopped"
                : $"{document.Publications:N0} generations, one journal chunk each; the last holds them all");
        if (document.Compactions > 0)
        {
            ConsoleUi.Field(
                "Compacted",
                document.Compactions == 1
                    ? "once, when the capture stopped; every row was kept"
                    : $"{document.Compactions:N0} times, while recording and when it stopped; every row was kept");
        }

        if (document.CompactionFailure is { } failure)
        {
            ConsoleUi.Warn(
                $"A compaction was not published ({failure}). The recording is whole; run icat compact to coalesce it.");
        }
        if (document.Health is { } health)
        {
            ConsoleUi.Field("Delivered", ConsoleUi.Count(health.ObservedRecords));
            ConsoleUi.Field("Admitted", ConsoleUi.Count(health.AdmittedRecords));
            ConsoleUi.Field(
                "Not admitted",
                ConsoleUi.Count(
                    health.PolicyOmissionsUnrequestedProvider + health.PolicyOmissionsDescriptorNotAdmitted
                    + health.PolicyOmissionsDescriptorDenied)
                + " by policy, which is not loss");
            ConsoleUi.Field(
                "Lost",
                health.IsLossFree
                    ? "nothing reported"
                    : $"{ConsoleUi.Count(health.ProviderReportedEventLoss)} events by the session, "
                        + $"{ConsoleUi.Count(health.ConsumerReportedBufferLoss)} buffers by the consumer, "
                        + $"{ConsoleUi.Count(health.ApplicationDrops)} records by InterCat's full queue");
        }

        if (document.Coverage.Count == 0)
        {
            ConsoleUi.Field("Coverage", "unknown: a loss counter could not be read, so no ledger was published");
        }
        else
        {
            ConsoleUi.Line();
            ConsoleUi.Table(
                ["Mechanism", "Coverage", "Why"],
                [.. document.Coverage.Select(entry => new[] { entry.Mechanism, entry.State, entry.Reason })]);
        }

        foreach (string degradation in document.Degradations)
        {
            ConsoleUi.Warn(degradation);
        }

        ConsoleUi.Line();
        ConsoleUi.Note($"icat session \"{document.Path}\" shows what it holds; icat processes and icat metric read it.");
        ConsoleUi.Success($"Recorded {ConsoleUi.Count(document.JournaledRecords)} records.");
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat record <new-session-dir> [--profile explore|focused-transport] [--mechanism tcp|udp]");
        ConsoleUi.Line("            [--duration <seconds>] [--publish-every <seconds>] [--json]");
        ConsoleUi.Line();
        ConsoleUi.Line("  Captures live under one uniquely named ETW session straight into a new session directory,");
        ConsoleUi.Line($"  for --duration seconds (default {DefaultSeconds}, at most {MaximumSeconds:N0}) or until Ctrl+C, which keeps what");
        ConsoleUi.Line("  was recorded. The profile decides what is admitted; the session publishes its journal,");
        ConsoleUi.Line("  derived segments and a coverage ledger of what its sources delivered and lost. Needs");
        ConsoleUi.Line("  elevation. Other ETW sessions on the machine are never stopped or adopted.");
        ConsoleUi.Line($"  --publish-every (default {DefaultPublishSeconds} s) publishes what was recorded so far as a new generation, one");
        ConsoleUi.Line("  journal chunk each, so session, processes and metric can read the capture while it records; 0");
        ConsoleUi.Line("  publishes once, when it stops. Its coverage ledger is published with the last generation.");
    }
}
