using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using InterCat.Application;
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

    /// <summary>The rolling retention the follow ran (`--keep-last`); null when the session kept every record.</summary>
    public RollingDocument? Rolling { get; init; }
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
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan LeaseRenewal = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FinishAfterStop = TimeSpan.FromSeconds(60);

    public static async Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string profile = command.TakeOption("--profile") ?? "explore";
        string? mechanismOption = command.TakeOption("--mechanism");
        string? pidsOption = command.TakeOption("--pid");
        bool broader = command.TryTakeFlag("--allow-broader");
        string? durationOption = command.TakeOption("--duration");
        string? journalOption = command.TakeOption("--max-journal-mib");
        string? freeOption = command.TakeOption("--min-free-mib");
        string? keepOption = command.TakeOption("--keep-last");
        string? brokerOption = command.TakeOption("--broker");
        string? sessionOption = command.TakePositional();
        bool json = command.TryTakeFlag("--json");
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure(CommandLine.Unknown(unknown!));
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        if (sessionOption is null)
        {
            ConsoleUi.Failure("A new session directory is required: icat capture <new-session-dir>.");
            ConsoleUi.Explain(PrintHelp);
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

        if (!RollingFollow.TryParse(keepOption, out RollingRetention? rolling, out string? keepProblem))
        {
            ConsoleUi.Failure(keepProblem!);
            return InterCatExitCode.InvalidInvocation;
        }

        // A capture that keeps a window has the broker release its own copy of what the session gave up (ADR-048).
        if (rolling is not null)
        {
            request = request! with { Retention = BrokerRetentionPolicy.ReleaseFollowed };
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
            return await CaptureAsync(connection.Client, request!, sessionPath, json, rolling, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<InterCatExitCode> CaptureAsync(
        WindowsBrokerPipeClient client,
        BrokerPrepareCaptureRequest request,
        string sessionPath,
        bool json,
        RollingRetention? rolling,
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

        // Where this session's evidence is, held beside the session while it is followed (live-follow-v1). If this
        // process ends early the broker still keeps the capture, and `icat follow <session>` or the Desktop's next launch
        // finishes the session from it. Without a ticket only that shortcut is lost, never the capture.
        using LiveFollowHold? ticket = HoldTicket(captureId, evidencePath, sessionPath, status.LeaseExpiresAtUtc);
        (FollowStep? last, BrokerCaptureStatusResponse final, SessionStore? derived) =
            await FollowUntilClosedAsync(client, captureId, evidencePath, sessionPath, json, ticket,
                    new CaptureLimits(TimeSpan.FromSeconds(request.Quota.MaximumDurationSeconds),
                        request.Quota.MaximumJournalBytes, request.Quota.MinimumFreeDiskBytes),
                    rolling, cancellationToken)
                .ConfigureAwait(false);

        // The follow returns only once the capture is closed, and a closed capture publishes nothing more: a session that
        // holds every chunk it published, or a capture that published none, leaves nothing to finish.
        if (last is null || last.DerivedChunks == last.EvidenceChunks)
        {
            ticket?.Complete();
        }

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
            Rolling = derived is null ? null : RollingFollow.Describe(rolling, derived),
        };
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(document, JsonContracts.Indented));
        }
        else
        {
            Render(document);
        }

        if (document.Finished && derived is not null)
        {
            // Ctrl+C is how a capture is stopped early, so it does not cancel the checkpoint of what the capture finished.
            _ = CheckpointStep.Publish(derived, CancellationToken.None);
        }

        return document.Finished ? InterCatExitCode.Success : InterCatExitCode.PartialResultSuccess;
    }

    /// <summary>
    /// The derived session's size and, while the capture records under <paramref name="limits"/>, when they stop it: the
    /// window's words for the same facts (§12.1 S5, R18). A progress line never ends a follow: a size that cannot be read
    /// just now is said to be, and the next publication states it.
    /// </summary>
    private static string Growth(SessionStore derived, long records, CaptureLimits? limits, string evidencePath, string sessionPath)
    {
        try
        {
            using EvidenceLease lease = derived.AcquireLease();
            SessionManifestV1 manifest = lease.Manifest;
            SessionSize size = SessionGrowth.Measure(manifest, records);
            CaptureHeadroom? headroom = limits is not null
                && SessionSegments.SourceClock(derived.Root, manifest) is { } clock
                && SessionRecording.Began(derived.Root, manifest, clock) is { } began
                    ? SessionGrowth.Headroom(size, began, DateTimeOffset.UtcNow, limits,
                        RecordingVolume.Measure(evidencePath, sessionPath))
                    : null;
            return SessionGrowth.Statement(size, headroom);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return "Its size could not be read just now: " + exception.Message;
        }
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
        LiveFollowHold? ticket,
        CaptureLimits limits,
        RollingRetention? rolling,
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
                        + $"published chunk(s), generation {ConsoleUi.Count(step.DerivedGeneration ?? 0)}. "
                        + Growth(derived!, step.DerivedRecords, stopRequested ? null : limits, evidencePath, sessionPath));
                }

                // Between two mirrors, by the follow's own writer: the only place a release may be published (ADR-044).
                // A session that outgrew what a pin keeping it past its window allows stops its capture, rather than
                // release the pinned records or grow on.
                if (RollingFollow.Step(rolling, derived!, new(step.MirroredChunks), json, work) is { } pinned && !stopRequested)
                {
                    stopRequested = true;
                    finishing.CancelAfter(FinishAfterStop);
                    ConsoleUi.Warn("Stopping the capture; deriving what it published. " + pinned + " "
                        + RollingFollow.GoOn(sessionPath));
                    if (await client.SendAsync(new BrokerStopCaptureRequest(captureId, Guid.NewGuid()), finishing.Token)
                            .ConfigureAwait(false) is BrokerStopCaptureResponse { FailureReason: { } refused })
                    {
                        ConsoleUi.Warn(refused);
                    }
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
                // The ticket keeps the lease's new expiry, which is how a later finish judges whether the capture can
                // still be recording.
                // A capture that keeps a window says what its session gave up, which the broker then releases (ADR-048).
                long? gaveUp = rolling is null ? null : last?.ReleasedChunks;
                if (await client.SendAsync(new BrokerRenewOwnerLeaseRequest(captureId, gaveUp), cancellationToken).ConfigureAwait(false)
                    is BrokerRenewOwnerLeaseResponse { LeaseExpiresAtUtc: { } expires })
                {
                    ticket?.Renew(expires);
                }

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

    /// <summary>
    /// Writes and holds the follow's ticket beside the session directory; null when it cannot be written, which costs
    /// only the shortcut to finishing the session later, never the capture.
    /// </summary>
    private static LiveFollowHold? HoldTicket(
        CaptureId captureId, string evidencePath, string sessionPath, DateTimeOffset ownerLeaseExpiresUtc)
    {
        try
        {
            // The ticket lives in the folder that will hold the session, which need not exist before the first chunk.
            if (Path.GetDirectoryName(sessionPath) is { Length: > 0 } parent)
            {
                _ = Directory.CreateDirectory(parent);
            }

            return LiveFollowTicket.For(captureId.Value, evidencePath, sessionPath, DateTimeOffset.UtcNow, ownerLeaseExpiresUtc)
                .Hold();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ConsoleUi.Warn(
                $"No follow ticket could be written beside {sessionPath} ({ConsoleUi.Reason(exception)}). If this command ends early, "
                + $"finish the session with icat follow {evidencePath} {sessionPath}.");
            return null;
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
                ? $"first within {BrokerJournalPublicationPolicy.FirstLivePublication.TotalSeconds:0.#} s, then every "
                    + $"{summary.PublicationIntervalMilliseconds / 1000d:0.#} s while recording"
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
        if (document.Rolling is { } rolling)
        {
            ConsoleUi.Field("Rolling", $"keeps {rolling.Window} of session time; "
                + (rolling.KeptFromNanoseconds is { } from
                    ? $"{CountText.Of(rolling.Releases, "release")}, every record kept from {SessionTimeText.Seconds(from, CultureInfo.CurrentCulture)}"
                    : "nothing released"));
            if (rolling.PinnedFromNanoseconds is { } pinned)
            {
                ConsoleUi.Field("Pinned", $"a pin keeps every record from {SessionTimeText.Seconds(pinned, CultureInfo.CurrentCulture)}, "
                    + $"so the session keeps more than {rolling.Window}");
            }

            if (rolling.Stopped is { } stopped)
            {
                ConsoleUi.Note("The capture was stopped: " + stopped);
            }
        }

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
        ConsoleUi.Line("               [--max-journal-mib <n>] [--min-free-mib <n>] [--keep-last <seconds>] [--broker <exe>]");
        ConsoleUi.Line("               [--json]");
        ConsoleUi.Line("      Captures live without running icat elevated: the capture broker is started on demand");
        ConsoleUi.Line("      (Windows asks for approval), records evidence only, and this process derives the session.");
        ConsoleUi.Line("      Stops at --duration (default 60 s) or on Ctrl+C; everything published is kept. If this");
        ConsoleUi.Line("      process ends early, icat follow <new-session-dir> finishes the session from the ticket");
        ConsoleUi.Line("      it leaves beside it. --keep-last keeps the session to the newest stretch of session time");
        ConsoleUi.Line("      that long, releasing the records read before it, but for what later ones rest on, and the");
        ConsoleUi.Line("      broker releases its own copy of what the session gave up, so --max-journal-mib bounds what");
        ConsoleUi.Line("      it holds rather than all it recorded. A pin (icat pin) keeps the records read from its");
        ConsoleUi.Line("      moment; once the session holds more than the pin allows, the capture stops rather than");
        ConsoleUi.Line("      release them.");
    }
}
