using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Capture.Journal;
using InterCat.Capture.Recording;
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

    /// <summary>Whether only admitted evidence was published, for `icat follow` to derive (ADR-027).</summary>
    public required bool EvidenceOnly { get; init; }

    /// <summary>How many compaction generations coalesced the recording's small publications (ADR-026).</summary>
    public required int Compactions { get; init; }

    /// <summary>Why a compaction was not published, or null; the recording itself was, whole.</summary>
    public required string? CompactionFailure { get; init; }
    public required double? PublishEverySeconds { get; init; }
    public required CaptureHealthSnapshot? Health { get; init; }
    public required IReadOnlyList<string> Degradations { get; init; }
    public required IReadOnlyList<SessionMechanismCoverageDocument> Coverage { get; init; }

    /// <summary>How many records' content a scoped content capture kept or omitted (`contracts/content-v1.md`).</summary>
    public long ContentFragments { get; init; }

    /// <summary>How many content bytes it kept.</summary>
    public long ContentKeptBytes { get; init; }

    /// <summary>Whether kept content reached the profile's session limit, which stopped the capture.</summary>
    public bool ContentLimitReached { get; init; }
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

        string profileOption = command.TakeOption("--profile") ?? "explore";
        string? mechanismOption = command.TakeOption("--mechanism");
        string? durationOption = command.TakeOption("--duration");
        string? publishOption = command.TakeOption("--publish-every");
        string? contentSource = command.TakeOption("--source");
        string? maximumRecordText = command.TakeOption("--max-record-bytes");
        string? maximumSessionText = command.TakeOption("--max-session-bytes");
        string? inspectionOption = command.TakeOption("--inspection");
        string? retentionOption = command.TakeOption("--retention");
        var processOptions = new List<string>();
        for (string? value; (value = command.TakeOption("--pid")) is not null;) processOptions.Add(value);
        var channelOptions = new List<string>();
        for (string? value; (value = command.TakeOption("--channel")) is not null;) channelOptions.Add(value);
        string? sessionPath = command.TakePositional();
        bool evidenceOnly = command.TryTakeFlag("--evidence-only");
        bool json = command.TryTakeFlag("--json");
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure(CommandLine.Unknown(unknown!));
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        if (sessionPath is null)
        {
            ConsoleUi.Failure("A new session directory is required: icat record <directory>");
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        CaptureProfileKind? profile = profileOption.ToUpperInvariant() switch
        {
            "EXPLORE" => CaptureProfileKind.Explore,
            "FOCUSED-TRANSPORT" or "FOCUSEDTRANSPORT" => CaptureProfileKind.FocusedTransport,
            "RPC-PEERS" or "RPCPEERS" => CaptureProfileKind.RpcPeers,
            "CONTENT-FIXTURE" or "CONTENTFIXTURE" => CaptureProfileKind.ContentFixture,
            "CONTENT" => CaptureProfileKind.Content,
            _ => null,
        };
        if (profile is null)
        {
            ConsoleUi.Failure(
                $"--profile records explore, focused-transport, rpc-peers, content or content-fixture; '{profileOption}' is not "
                + "one. icat profiles lists every intent and what it would collect.");
            return InterCatExitCode.InvalidInvocation;
        }

        // Kept content is published beside its journal chunk, which an evidence session's follower does not mirror.
        if (profile is CaptureProfileKind.ContentFixture or CaptureProfileKind.Content && evidenceOnly)
        {
            ConsoleUi.Failure("--evidence-only records metadata evidence for a follower, which does not mirror kept content; "
                + "record content without it.");
            return InterCatExitCode.InvalidInvocation;
        }

        Mechanism? mechanism = mechanismOption?.ToUpperInvariant() switch
        {
            null => null,
            "TCP" => Mechanism.Tcp,
            "UDP" => Mechanism.Udp,
            "HTTP" => Mechanism.Http,
            _ => (Mechanism)0,
        };
        bool mechanismFits = profile switch
        {
            CaptureProfileKind.FocusedTransport => mechanism is Mechanism.Tcp or Mechanism.Udp,
            CaptureProfileKind.Content => mechanism is not null and not (Mechanism)0,
            _ => mechanism is null,
        };
        if (!mechanismFits)
        {
            ConsoleUi.Failure(
                "--mechanism tcp or udp names the transport of --profile focused-transport, and http the messages a content "
                + "request keeps; no other profile takes one.");
            return InterCatExitCode.InvalidInvocation;
        }

        bool contentOptions = contentSource is not null || maximumRecordText is not null || maximumSessionText is not null
            || inspectionOption is not null || retentionOption is not null || processOptions.Count > 0 || channelOptions.Count > 0;
        ContentCaptureRequest? contentRequest = null;
        if (profile == CaptureProfileKind.Content)
        {
            // Nothing about content is assumed: its source, its processes, its channels, both limits and the consent to
            // inspect it are the request's own, stated on the command line and checked before anything starts.
            string? problem = ContentRequest(contentSource, mechanism!.Value, processOptions, channelOptions, maximumRecordText,
                maximumSessionText, inspectionOption, retentionOption, out contentRequest);
            if (problem is not null)
            {
                ConsoleUi.Failure(problem);
                ConsoleUi.Explain(PrintHelp);
                return InterCatExitCode.InvalidInvocation;
            }
        }
        else if (contentOptions)
        {
            ConsoleUi.Failure("--source, --pid, --channel, the byte limits, --inspection and --retention make a content "
                + "request, and only --profile content takes one.");
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
            new(profile.Value, FocusedMechanism: profile == CaptureProfileKind.FocusedTransport ? mechanism : null,
                Content: contentRequest),
            inventory,
            environment);
        if (!effective.CanStart)
        {
            ConsoleUi.Failure("The profile cannot start on this machine, so nothing was recorded:");
            ConsoleUi.Explain(() =>
            {
                foreach (ProfileSourceDecision decision in effective.SourceDecisions.Where(decision => decision.State == ProfileSourceDecisionState.Blocking))
                {
                    ConsoleUi.Bullet($"{decision.SourceId}: {decision.Reason}");
                }

                foreach (string diagnostic in effective.Diagnostics)
                {
                    ConsoleUi.Bullet(diagnostic);
                }
            });

            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        if (contentRequest is not null)
        {
            // Each named process as it is now. The capture holds each open while it runs, so its ID stays its own, and one
            // that is not running is refused here, before anything starts (ADR-037).
            foreach (int processId in contentRequest.ProcessIds)
            {
                if (RunningProcess(processId) is not { } running)
                {
                    ConsoleUi.Failure(
                        $"Process {processId} is not running, so nothing was recorded: a content request names running "
                        + "processes, which the capture holds open so their IDs stay theirs. Task Manager's Details tab "
                        + "lists running processes with their IDs.");
                    return InterCatExitCode.InvalidInvocation;
                }

                ConsoleUi.Progress($"Content is kept from process {processId}, {running}, held open while the capture runs so its ID stays its own.");
            }
        }

        // What the capture collects, stated before it starts, as a preview states it (§11.1).
        ConsoleUi.Progress(effective.CollectionStatement);
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
            output: evidenceOnly ? LiveRecordingOutput.EvidenceOnly : LiveRecordingOutput.Session,
            calibration: ClockCalibrationSource.Local,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Start.Started)
        {
            ConsoleUi.Failure(result.Start.FailureReason ?? "The capture did not start.");
            ConsoleUi.Explain(() =>
            {
                foreach (ProviderEnablementResult provider in result.Start.Providers.Where(provider => !provider.Enabled))
                {
                    ConsoleUi.Bullet($"{provider.SourceId}: {provider.FailureReason}");
                }
            });

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
            EvidenceOnly = evidenceOnly,
            Compactions = result.Compactions,
            CompactionFailure = result.CompactionFailure,
            PublishEverySeconds = publishSeconds > 0 ? publishSeconds : null,
            Health = result.Stop?.Health,
            Degradations = result.Stop?.Degradations ?? [],
            Coverage = result.Coverage is null ? [] : CollectedCoverage(result.Coverage),
            ContentFragments = result.ContentFragments,
            ContentKeptBytes = result.ContentKeptBytes,
            ContentLimitReached = result.ContentLimitReached,
        };

        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(document, JsonContracts.Indented));
        }
        else
        {
            Render(document);
        }

        if (!evidenceOnly && result.Generation is not null)
        {
            // Ctrl+C is how a recording is stopped early, so it does not cancel the checkpoint of what was recorded.
            _ = CheckpointStep.Publish(store, CancellationToken.None);
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

        if (document.EvidenceOnly)
        {
            ConsoleUi.Field("Derived", "nothing: this elevated process published the admitted evidence alone");
        }

        // Kept content is restricted evidence beside the journal (ADR-036): said once, with its limit if it bit.
        if (document.ContentFragments > 0)
        {
            ConsoleUi.Field("Content kept", $"{ConsoleUi.Bytes(document.ContentKeptBytes)} of "
                + $"{ConsoleUi.Count(document.ContentFragments)} {(document.ContentFragments == 1 ? "record" : "records")}"
                + (document.ContentLimitReached ? ", until the content limit stopped the capture" : string.Empty));
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
        ConsoleUi.Note(
            document.EvidenceOnly
                ? $"icat follow \"{document.Path}\" <session-dir> derives the session from it in an ordinary shell, and can "
                    + "run while a recording records; icat processes and icat metric then read that session."
                : $"icat session \"{document.Path}\" shows what it holds; icat processes and icat metric read it.");
        ConsoleUi.Success($"Recorded {ConsoleUi.Count(document.JournaledRecords)} records.");
    }

    /// <summary>
    /// A content request from its options, or the problem with them. Every part is the request's own and required, except
    /// the retention, whose one bounded behaviour is stop-at-limit (`contracts/content-v1.md` §5).
    /// </summary>
    /// <summary>A running process's image and start, in words; null when no process has the ID, or it has exited.</summary>
    private static string? RunningProcess(int processId)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return null;
        }

        using (process)
        {
            try
            {
                if (process.HasExited)
                {
                    return null;
                }

                string image = process.ProcessName;
                string started;
                try
                {
                    started = "started " + process.StartTime.ToString("T", CultureInfo.CurrentCulture);
                }
                catch (Exception exception) when (exception is Win32Exception or NotSupportedException)
                {
                    started = "its start not readable";
                }

                return $"{image} ({started})";
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            catch (Win32Exception)
            {
                return "its image not readable";
            }
        }
    }

    private static string? ContentRequest(string? source, Mechanism mechanism, List<string> processes, List<string> channels,
        string? maximumRecord, string? maximumSession, string? inspection, string? retention, out ContentCaptureRequest? request)
    {
        request = null;
        if (source is null || processes.Count == 0 || channels.Count == 0 || maximumRecord is null || maximumSession is null
            || inspection is null)
        {
            return "--profile content needs --source, --mechanism, at least one --pid and --channel, --max-record-bytes, "
                + "--max-session-bytes and --inspection.";
        }

        var processIds = new List<int>(processes.Count);
        foreach (string text in processes)
        {
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int processId) || processId <= 0)
            {
                return $"--pid takes a positive process ID; '{text}' is not one.";
            }

            processIds.Add(processId);
        }

        if (!int.TryParse(maximumRecord, NumberStyles.None, CultureInfo.InvariantCulture, out int recordBytes))
        {
            return $"--max-record-bytes takes a whole number of bytes; '{maximumRecord}' is not one.";
        }

        if (!long.TryParse(maximumSession, NumberStyles.None, CultureInfo.InvariantCulture, out long sessionBytes))
        {
            return $"--max-session-bytes takes a whole number of bytes; '{maximumSession}' is not one.";
        }

        ContentInspectionMode? mode = inspection.Trim().ToLowerInvariant() switch
        {
            "hex-text" or "hextext" => ContentInspectionMode.HexAndText,
            "disabled" => ContentInspectionMode.Disabled,
            _ => null,
        };
        if (mode is null)
        {
            return "--inspection is hex-text, which lets a person see kept bytes when they ask, or disabled, which never does.";
        }

        if (retention is not null && retention.Trim().ToLowerInvariant() is not ("stop-at-limit" or "stopatlimit"))
        {
            return "--retention is stop-at-limit, the one bounded behaviour: the capture stops when kept content reaches its limit.";
        }

        request = new()
        {
            SourceId = source,
            Mechanism = mechanism,
            ProcessIds = processIds,
            ChannelSelectors = channels,
            MaximumRecordBytes = recordBytes,
            MaximumSessionBytes = sessionBytes,
            Retention = ContentRetentionMode.StopAtLimit,
            Inspection = mode.Value,
        };
        return ContentCapturePolicyCompiler.Validate(request);
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat record <new-session-dir> [--profile explore|focused-transport|rpc-peers|content|content-fixture]");
        ConsoleUi.Line("            [--mechanism tcp|udp|http] [--duration <seconds>] [--publish-every <seconds>] [--evidence-only] [--json]");
        ConsoleUi.Line("            content: --source <source-id> --mechanism http --pid <id> ... --channel * --inspection hex-text|disabled");
        ConsoleUi.Line("                     --max-record-bytes <n> --max-session-bytes <n> [--retention stop-at-limit]");
        ConsoleUi.Line();
        ConsoleUi.Line("  Captures live under one uniquely named ETW session straight into a new session directory,");
        ConsoleUi.Line($"  for --duration seconds (default {DefaultSeconds}, at most {MaximumSeconds:N0}) or until Ctrl+C, which keeps what");
        ConsoleUi.Line("  was recorded. The profile decides what is admitted; the session publishes its journal,");
        ConsoleUi.Line("  derived segments and a coverage ledger of what its sources delivered and lost. Needs");
        ConsoleUi.Line("  elevation. Other ETW sessions on the machine are never stopped or adopted.");
        ConsoleUi.Line($"  --publish-every (default {DefaultPublishSeconds} s) publishes what was recorded so far as a new generation, one");
        ConsoleUi.Line("  journal chunk each, so session, processes and metric can read the capture while it records; 0");
        ConsoleUi.Line("  publishes once, when it stops. Its coverage ledger is published with the last generation.");
        ConsoleUi.Line("  --evidence-only publishes the admitted evidence alone - journal chunks, plan and ledger - and");
        ConsoleUi.Line("  derives nothing in the elevated process; icat follow derives the session from it (ADR-027).");
        ConsoleUi.Line("  --profile content keeps the message bytes of a source admitted for content - WinINet's HTTP");
        ConsoleUi.Line("  exchanges, etw/manifest/Microsoft-Windows-WinINet-Capture (ADR-037) - from the processes --pid");
        ConsoleUi.Line("  names only, which the provider's own process filter holds before anything is kept; --channel *");
        ConsoleUi.Line("  says every exchange of theirs is kept. It stops when kept content reaches --max-session-bytes,");
        ConsoleUi.Line("  and it records through icat record only, never through the broker.");
    }
}
