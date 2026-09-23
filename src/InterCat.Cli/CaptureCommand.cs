using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using InterCat.Capture.Journal;
using InterCat.CaptureBroker;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>What a broker capture kept, as a document a tool can read without this CLI.</summary>
internal sealed record CaptureDocument
{
    public required string Contract { get; init; }
    public required string SessionPath { get; init; }
    public required string? EvidencePath { get; init; }
    public required string CaptureId { get; init; }
    public required string Profile { get; init; }
    public required int RequestedSeconds { get; init; }
    public required double? PublishEverySeconds { get; init; }
    public required string State { get; init; }
    public required BrokerStopMilestones Milestones { get; init; }
    public required string? Reason { get; init; }

    /// <summary>Whether every published chunk is derived and the capture proved its last publication.</summary>
    public required bool Finished { get; init; }
    public required int EvidenceChunks { get; init; }
    public required int DerivedChunks { get; init; }
    public required long DerivedRecords { get; init; }
    public required long? Generation { get; init; }
}

/// <summary>
/// `icat capture`: live capture from an ordinary-integrity prompt (§3.1, §20.4). The privileged broker is launched on
/// demand - Windows asks for approval - and records evidence only; this process follows that evidence into a session of
/// the user's own, deriving its rows here (ADR-027). The capture stops at its duration, on Ctrl+C, or when its owner
/// lease lapses because this process went away; what was published is always kept.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class CaptureCommand
{
    private const int DefaultSeconds = 60;
    private const int MaximumSeconds = 86_400;
    private const long DefaultJournalMebibytes = 1_024;
    private const long DefaultFreeMebibytes = 1_024;
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LeaseRenewal = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FinishAfterStop = TimeSpan.FromSeconds(60);

    public static async Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? sessionOption = command.TakePositional();
        string profile = command.TakeOption("--profile") ?? "explore";
        string? mechanismOption = command.TakeOption("--mechanism");
        string? pidsOption = command.TakeOption("--pid");
        bool broader = command.TryTakeFlag("--allow-broader");
        string? durationOption = command.TakeOption("--duration");
        string? journalOption = command.TakeOption("--max-journal-mib");
        string? freeOption = command.TakeOption("--min-free-mib");
        string? brokerOption = command.TakeOption("--broker");
        bool json = command.TryTakeFlag("--json");
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure($"Unknown or incomplete option: {unknown}");
            PrintHelp();
            return InterCatExitCode.InvalidInvocation;
        }

        if (sessionOption is null)
        {
            ConsoleUi.Failure("A new session directory is required: icat capture <new-session-dir>.");
            PrintHelp();
            return InterCatExitCode.InvalidInvocation;
        }

        string sessionPath = Path.GetFullPath(sessionOption);
        if (File.Exists(sessionPath) || (Directory.Exists(sessionPath) && Directory.EnumerateFileSystemEntries(sessionPath).Any()))
        {
            ConsoleUi.Failure($"{sessionPath} already exists and is not empty. A capture writes only into a new session directory.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (!TryParseRequest(profile, mechanismOption, pidsOption, broader, durationOption, journalOption, freeOption,
            out BrokerPrepareCaptureRequest? request, out string? problem))
        {
            ConsoleUi.Failure(problem!);
            return InterCatExitCode.InvalidInvocation;
        }

        BrokerLaunchTarget? target = brokerOption is not null
            ? new(Path.GetFullPath(brokerOption), ["serve"])
            : BrokerLaunchTarget.BesideCurrentProcess();
        if (target is null)
        {
            ConsoleUi.Failure(
                $"The capture broker ({BrokerLaunchTarget.ExecutableName}) is not installed beside icat. "
                + "Reinstall InterCat, or in a development build pass --broker <path to InterCat.CaptureBroker.exe>.");
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        ConsoleUi.Progress("Starting the capture broker. Windows asks for administrator approval to record system-wide events.");
        BrokerConnection connection;
        try
        {
            connection = await WindowsBrokerLauncher.LaunchAsync(target, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (BrokerLaunchException exception)
        {
            ConsoleUi.Failure(exception.Message);
            return exception.Failure is BrokerLaunchFailure.ElevationDeclined or BrokerLaunchFailure.BrokerNotInstalled
                or BrokerLaunchFailure.BrokerExited or BrokerLaunchFailure.ServerNotTheBroker
                ? InterCatExitCode.PermissionOrCapabilityFailure
                : InterCatExitCode.PartialResultSuccess;
        }

        await using (connection.ConfigureAwait(false))
        {
            return await CaptureAsync(connection.Client, request!, sessionPath, json, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<InterCatExitCode> CaptureAsync(
        WindowsBrokerPipeClient client,
        BrokerPrepareCaptureRequest request,
        string sessionPath,
        bool json,
        CancellationToken cancellationToken)
    {
        if (await client.SendAsync(
                new BrokerHelloRequest(
                    Guid.NewGuid(), 1, 1, BrokerHelloNegotiator.SupportedFeatures, BrokerProtocolFeature.PreparedPlanDigest),
                cancellationToken).ConfigureAwait(false) is not BrokerHelloResponse)
        {
            ConsoleUi.Failure("The capture broker does not speak a compatible protocol version. Reinstall InterCat.");
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        BrokerWireResponse prepareResponse = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (prepareResponse is not BrokerPrepareCaptureResponse prepared)
        {
            ConsoleUi.Failure(Describe(prepareResponse));
            return InterCatExitCode.InvalidInvocation;
        }

        if (!prepared.Prepared)
        {
            ConsoleUi.Failure($"This capture cannot run here: {prepared.RefusalReason}");
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        if (!json)
        {
            RenderSummary(prepared.Summary);
        }

        BrokerWireResponse startResponse = await client.SendAsync(
                new BrokerStartCaptureRequest(prepared.Grant!.Token, Guid.NewGuid()), cancellationToken)
            .ConfigureAwait(false);
        if (startResponse is not BrokerStartCaptureResponse { Code: BrokerOperationCode.Started, CaptureId: { } captureId })
        {
            ConsoleUi.Failure(startResponse is BrokerStartCaptureResponse failed
                ? $"The capture did not start: {failed.FailureReason ?? failed.Code.ToString()}"
                : Describe(startResponse));
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        BrokerCaptureStatusResponse status = await StatusAsync(client, captureId, CancellationToken.None).ConfigureAwait(false);
        if (status.EvidenceDirectory is not { } evidencePath)
        {
            ConsoleUi.Failure("The capture broker did not say where it records; stopping the capture.");
            _ = await client.SendAsync(new BrokerStopCaptureRequest(captureId, Guid.NewGuid()), CancellationToken.None)
                .ConfigureAwait(false);
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        ConsoleUi.Progress(
            $"Recording for up to {request.Quota.MaximumDurationSeconds:N0} s into {sessionPath}. Ctrl+C stops early; "
            + "everything published so far is kept.");
        (FollowStep? last, BrokerCaptureStatusResponse final, SessionStore? derived) =
            await FollowUntilClosedAsync(client, captureId, evidencePath, sessionPath, json, cancellationToken)
                .ConfigureAwait(false);

        var document = new CaptureDocument
        {
            Contract = "capture-v1",
            SessionPath = sessionPath,
            EvidencePath = evidencePath,
            CaptureId = captureId.Value.ToString("N"),
            Profile = request.ProfileId,
            RequestedSeconds = request.Quota.MaximumDurationSeconds,
            PublishEverySeconds = prepared.Summary.PublicationIntervalMilliseconds > 0
                ? prepared.Summary.PublicationIntervalMilliseconds / 1000d
                : null,
            State = final.State.ToString(),
            Milestones = final.StopMilestones,
            Reason = final.FailureReason,
            Finished = last?.Finished == true && final.StopMilestones.FullyFinalized,
            EvidenceChunks = last?.EvidenceChunks ?? 0,
            DerivedChunks = last?.DerivedChunks ?? 0,
            DerivedRecords = last?.DerivedRecords ?? 0,
            Generation = derived?.Current?.Generation,
        };
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(document, JsonContracts.Indented));
        }
        else
        {
            Render(document);
        }

        return document.Finished ? InterCatExitCode.Success : InterCatExitCode.PartialResultSuccess;
    }

    /// <summary>
    /// Derives while the capture records, renewing the owner lease. Ctrl+C requests a stop and then keeps deriving until
    /// the capture is closed. The follow ends when every published chunk is derived and the broker reports the capture
    /// closed - finished, or interrupted with only a published prefix.
    /// </summary>
    private static async Task<(FollowStep? Last, BrokerCaptureStatusResponse Final, SessionStore? Derived)> FollowUntilClosedAsync(
        WindowsBrokerPipeClient client,
        CaptureId captureId,
        string evidencePath,
        string sessionPath,
        bool json,
        CancellationToken cancellationToken)
    {
        FollowStep? last = null;
        LiveSessionFollower? follower = null;
        SessionStore? derived = null;
        bool stopRequested = false;
        bool settled = false;
        using var finishing = new CancellationTokenSource();
        DateTimeOffset nextRenewal = DateTimeOffset.UtcNow + LeaseRenewal;
        while (true)
        {
            if (cancellationToken.IsCancellationRequested && !stopRequested)
            {
                stopRequested = true;
                finishing.CancelAfter(FinishAfterStop);
                ConsoleUi.Warn("Stopping the capture; deriving what it published.");
                BrokerWireResponse stopped = await client.SendAsync(
                        new BrokerStopCaptureRequest(captureId, Guid.NewGuid()), finishing.Token)
                    .ConfigureAwait(false);
                if (stopped is BrokerStopCaptureResponse { FailureReason: { } reason })
                {
                    ConsoleUi.Warn(reason);
                }
            }

            CancellationToken work = stopRequested ? finishing.Token : cancellationToken;
            if (follower is null)
            {
                SessionStore evidence = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidencePath));
                if (evidence.Current is { } source)
                {
                    Directory.CreateDirectory(sessionPath);
                    derived = SessionStore.Open(LocalOwnedDirectory.Open(sessionPath), source.SessionId, source.SourceIdentity);
                    follower = LiveSessionFollower.Open(evidence, derived, cancellationToken: work);
                }
            }

            if (follower is not null)
            {
                FollowStep step = follower.CatchUp(cancellationToken: work);
                if (step.MirroredChunks > 0 && !json)
                {
                    ConsoleUi.Progress(
                        $"{ConsoleUi.Count(step.DerivedRecords)} records derived from {ConsoleUi.Count(step.DerivedChunks)} "
                        + $"published chunk(s), generation {ConsoleUi.Count(step.DerivedGeneration ?? 0)}.");
                }

                last = step;
            }

            BrokerCaptureStatusResponse status = await StatusAsync(client, captureId, work).ConfigureAwait(false);
            if (status.State == CaptureLifecycle.Closed)
            {
                // A closed capture publishes nothing more. One further pass after seeing it closed derives whatever it
                // published last; an interrupted capture never proves finality, so closure, not the marker, ends this.
                if (settled)
                {
                    return (last, status, derived);
                }

                settled = true;
                continue;
            }

            if (!stopRequested && DateTimeOffset.UtcNow >= nextRenewal)
            {
                _ = await client.SendAsync(new BrokerRenewOwnerLeaseRequest(captureId), cancellationToken).ConfigureAwait(false);
                nextRenewal = DateTimeOffset.UtcNow + LeaseRenewal;
            }

            try
            {
                await Task.Delay(Poll, work).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stopRequested && cancellationToken.IsCancellationRequested)
            {
                // Ctrl+C: the next pass requests the stop and keeps deriving.
            }
        }
    }

    private static async Task<BrokerCaptureStatusResponse> StatusAsync(
        WindowsBrokerPipeClient client,
        CaptureId captureId,
        CancellationToken cancellationToken) =>
        await client.SendAsync(new BrokerGetStatusRequest(captureId), cancellationToken).ConfigureAwait(false)
            as BrokerCaptureStatusResponse
        ?? throw new InvalidDataException("The capture broker did not report this capture's status.");

    private static bool TryParseRequest(
        string profile,
        string? mechanismOption,
        string? pidsOption,
        bool broader,
        string? durationOption,
        string? journalOption,
        string? freeOption,
        out BrokerPrepareCaptureRequest? request,
        out string? problem)
    {
        request = null;
        problem = null;
        int seconds = DefaultSeconds;
        if (durationOption is not null
            && (!int.TryParse(durationOption.TrimEnd('s'), NumberStyles.None, CultureInfo.InvariantCulture, out seconds)
                || seconds is < 1 or > MaximumSeconds))
        {
            problem = $"--duration is whole seconds from 1 to {MaximumSeconds:N0}; '{durationOption}' is not one.";
            return false;
        }

        long journal = DefaultJournalMebibytes;
        long free = DefaultFreeMebibytes;
        if ((journalOption is not null && (!long.TryParse(journalOption, NumberStyles.None, CultureInfo.InvariantCulture, out journal) || journal < 1))
            || (freeOption is not null && (!long.TryParse(freeOption, NumberStyles.None, CultureInfo.InvariantCulture, out free) || free < 0)))
        {
            problem = "--max-journal-mib and --min-free-mib take whole mebibytes.";
            return false;
        }

        Mechanism? mechanism = mechanismOption?.ToUpperInvariant() switch
        {
            null => null,
            "TCP" => Mechanism.Tcp,
            _ => (Mechanism)0,
        };
        List<int> pids = [];
        foreach (string item in (pidsOption ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0)
            {
                problem = $"--pid takes positive process IDs separated by commas; '{item}' is not one.";
                return false;
            }

            pids.Add(pid);
        }

        bool focused = string.Equals(profile, "focused-transport", StringComparison.Ordinal);
        if (!focused && !string.Equals(profile, "explore", StringComparison.Ordinal))
        {
            problem = $"--profile is explore or focused-transport; '{profile}' is not one. icat profiles lists what each collects.";
            return false;
        }

        if (focused != (mechanism is not null) || mechanism == (Mechanism)0 || (!focused && (pids.Count > 0 || broader)))
        {
            problem = "focused-transport takes --mechanism tcp and optionally --pid <id,...> and --allow-broader; explore takes neither.";
            return false;
        }

        request = new(
            profile,
            mechanism,
            pids,
            broader,
            false,
            new BrokerCaptureQuota(seconds, journal * 1024 * 1024, free * 1024 * 1024),
            BrokerRetentionPolicy.StopAtLimit,
            null,

            // Live, so a capture interrupted by a killed broker still keeps everything published (plan revision 82).
            BrokerJournalPublication.Live);
        return true;
    }

    private static void RenderSummary(BrokerEffectiveCaptureSummary summary)
    {
        ConsoleUi.Heading("Capture");
        ConsoleUi.Field("Profile", summary.EffectiveProfileId ?? summary.RequestedProfileId);
        ConsoleUi.Field("Sources", string.Join(", ", summary.Sources.Select(source => source.SourceId)));
        ConsoleUi.Field("Stops after", $"{summary.Quota.MaximumDurationSeconds:N0} s, or {ConsoleUi.Size(summary.Quota.MaximumJournalBytes)} of journal");
        ConsoleUi.Field("Keeps free", $"{ConsoleUi.Size(summary.Quota.MinimumFreeDiskBytes)} on the recording volume");
        ConsoleUi.Field(
            "Published",
            summary.PublicationIntervalMilliseconds > 0
                ? $"every {summary.PublicationIntervalMilliseconds / 1000d:0.#} s while recording"
                : "once, when the capture stops");
        ConsoleUi.Note(summary.CollectionStatement);
        if (!summary.CollectionStatement.Contains(summary.Disclosure, StringComparison.Ordinal))
        {
            ConsoleUi.Note(summary.Disclosure);
        }
        foreach (string diagnostic in summary.Diagnostics)
        {
            ConsoleUi.Bullet(diagnostic);
        }
    }

    private static void Render(CaptureDocument document)
    {
        ConsoleUi.Heading(document.Finished ? "Capture complete" : "Capture kept partially");
        ConsoleUi.Field("Session", document.SessionPath);
        ConsoleUi.Field("Records", $"{ConsoleUi.Count(document.DerivedRecords)} derived from {ConsoleUi.Count(document.DerivedChunks)} chunk(s)");
        ConsoleUi.Field("Generation", document.Generation is { } generation ? ConsoleUi.Count(generation) : "none published");
        ConsoleUi.Field("Capture", $"{document.CaptureId} ({document.State})");
        if (document.Reason is { } reason)
        {
            ConsoleUi.Note(reason);
        }

        if (document.Finished)
        {
            ConsoleUi.Note($"Open it with: icat session \"{document.SessionPath}\" (or processes, metric).");
        }
        else if (document.DerivedChunks > 0)
        {
            ConsoleUi.Note("What was published before the capture ended is an ordinary session: icat session, processes and metric read it.");
        }
    }

    private static string Describe(BrokerWireResponse response) => response is BrokerErrorResponse error
        ? error.UserMessage
        : $"The capture broker answered unexpectedly ({response.GetType().Name}).";

    private static void PrintHelp()
    {
        ConsoleUi.Line("  icat capture <new-session-dir> [--duration <seconds>] [--profile explore|focused-transport]");
        ConsoleUi.Line("               [--mechanism tcp] [--pid <id,...>] [--allow-broader]");
        ConsoleUi.Line("               [--max-journal-mib <n>] [--min-free-mib <n>] [--broker <exe>] [--json]");
        ConsoleUi.Line("      Captures live without running icat elevated: the capture broker is started on demand");
        ConsoleUi.Line("      (Windows asks for approval), records evidence only, and this process derives the session.");
        ConsoleUi.Line("      Stops at --duration (default 60 s) or on Ctrl+C; everything published is kept.");
    }
}
