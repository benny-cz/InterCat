using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using InterCat.Application;
using InterCat.Capture.Journal;
using InterCat.CaptureBroker;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Desktop;

public enum CaptureUiPhase { Starting, Recording, Finishing, Complete, Unavailable }

/// <param name="LivePreview">The broker's latest live preview while recording (broker-v1 §5.9); null otherwise.</param>
/// <param name="OverviewChunks">How many published chunks <paramref name="Overview"/> was derived from.</param>
/// <param name="PreviewObservedQpc">
/// The machine's QPC reading when a newly read preview was handed to the window; null on an update that only repeats the
/// last one. Records carry readings of the same clock, so a record's delay to its first preview is exact.
/// </param>
public sealed record CaptureUiUpdate(
    CaptureUiPhase Phase,
    string Headline,
    string Detail,
    string? Summary = null,
    string? SessionPath = null,
    SessionOverviewBundle? Overview = null,
    CaptureMilestones? Milestones = null,
    CaptureVisibility? Visibility = null,
    BrokerCaptureHealth? LiveHealth = null,
    BrokerCapturePreview? LivePreview = null,
    int? OverviewChunks = null,
    long? PreviewObservedQpc = null);

/// <summary>
/// Elapsed time from the user's start action to each first-run step of section 3.1, so first feedback is measured
/// rather than asserted (section 12). A step not yet reached is null. The publication interval is the broker's compiled
/// live cadence, which bounds how fresh any published overview can be.
/// </summary>
public sealed record CaptureMilestones(
    TimeSpan? BrokerReady = null,
    TimeSpan? Prepared = null,
    TimeSpan? Started = null,
    TimeSpan? FirstGeneration = null,
    TimeSpan? FirstOverview = null,
    TimeSpan? PublicationInterval = null)
{
    /// <summary>How long after recording began the first overview reached the window, when both are known.</summary>
    public TimeSpan? FirstOverviewAfterStart => FirstOverview - Started;
}

/// <summary>
/// What the viewer had derived when it handed an overview to the window: the generation, every record derived so far,
/// and the machine's QPC reading at that moment. Records carry native QPC readings of the same clock, so a record's
/// delay from acquisition to the window is exact rather than estimated.
/// </summary>
public sealed record CaptureVisibility(
    long Generation,
    long DerivedRecords,
    long ObservedQpc,
    TimeSpan Derivation,
    TimeSpan Projection);

/// <summary>Where and for how long one Desktop capture runs. The defaults are the first-run Explore capture.</summary>
public sealed record CaptureRunOptions
{
    /// <summary>The broker to launch; null means the one installed beside the Desktop.</summary>
    public BrokerLaunchTarget? Broker { get; init; }

    /// <summary>The directory that receives the derived session; null means the user's local application data.</summary>
    public string? SessionRoot { get; init; }

    public int MaximumDurationSeconds { get; init; } = 600;
}

/// <summary>
/// Ordinary-integrity Desktop owner of a bounded Explore capture. The elevated broker writes only raw evidence;
/// this process follows it into a user-owned session and projects only published generations (ADR-027, R16, R7).
/// </summary>
public static class DesktopCaptureRunner
{
    /// <summary>How often live acquisition counters are passed on while they change; the strip reads them, not a chart.</summary>
    private static readonly TimeSpan HealthReport = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How often a changed live preview is passed on: the 4 Hz live-snapshot cadence of §19.3, so a record reaches the
    /// timeline's live edge well inside §12's event-to-visible budget while its chunk is still being written.
    /// </summary>
    private static readonly TimeSpan PreviewReport = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How often the viewer looks for a new publication. A look reads a small pointer file, so a short poll costs little and
    /// keeps the viewer's share of the event-to-visible delay small (plan §12, measured in first-feedback qualification).
    /// </summary>
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan LeaseRenewal = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FinishTimeout = TimeSpan.FromSeconds(60);

    public static BrokerPrepareCaptureRequest ExploreRequest(int maximumDurationSeconds = 600) => new(
        "explore", null, [], false, false,
        new BrokerCaptureQuota(maximumDurationSeconds, 1_024L * 1_024 * 1_024, 1_024L * 1_024 * 1_024),
        BrokerRetentionPolicy.StopAtLimit, null, BrokerJournalPublication.Live);

    public static string Describe(BrokerEffectiveCaptureSummary summary) =>
        $"{summary.EffectiveProfileId ?? summary.RequestedProfileId} · "
        + $"{string.Join(", ", summary.Sources.Select(source => source.SourceId))} · "
        + $"up to {summary.Quota.MaximumDurationSeconds / 60:N0} min / "
        + $"{summary.Quota.MaximumJournalBytes / (1024 * 1024):N0} MiB journal · "
        + (summary.PublicationIntervalMilliseconds > 0
            ? $"first view within {BrokerJournalPublicationPolicy.FirstLivePublication.TotalSeconds:0.#} s, then every "
                + $"{summary.PublicationIntervalMilliseconds / 1000d:0.#} s. "
            : "published when stopped. ")
        + summary.CollectionStatement + " " + summary.Disclosure
        + (summary.Diagnostics.Count == 0 ? string.Empty
            : " Diagnostics: " + string.Join("; ", summary.Diagnostics));

    public static async Task RunAsync(Action<CaptureUiUpdate> report, CancellationToken stop,
        CaptureRunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        options ??= new();
        Stopwatch elapsed = Stopwatch.StartNew();
        var milestones = new CaptureMilestones();
        Action<CaptureUiUpdate> inner = report;
        CaptureUiUpdate? lastLive = null;
        BrokerCaptureHealth? health = null;
        BrokerCapturePreview? preview = null;
        report = update =>
        {
            // Only a recording has something to preview: once stopping begins, every chunk is on its way to the viewer.
            update = update with
            {
                Milestones = milestones,
                LiveHealth = update.LiveHealth ?? health,
                LivePreview = update.Phase == CaptureUiPhase.Recording ? update.LivePreview ?? preview : null,
            };
            if (update.Phase == CaptureUiPhase.Recording)
            {
                lastLive = update with { Overview = null, Visibility = null, OverviewChunks = null, PreviewObservedQpc = null };
            }

            inner(update);
        };
        DateTimeOffset healthReported = DateTimeOffset.MinValue;
        DateTimeOffset previewReported = DateTimeOffset.MinValue;
        if (!OperatingSystem.IsWindows())
        {
            report(new(CaptureUiPhase.Unavailable, "Live capture needs Windows",
                "You can still open an existing session. No capture was started."));
            return;
        }

        BrokerLaunchTarget? target = options.Broker ?? BrokerLaunchTarget.BesideCurrentProcess();
        if (target is null)
        {
            report(new(CaptureUiPhase.Unavailable, "Capture broker is not installed",
                "Install InterCat with its capture broker beside the Desktop app. Saved sessions remain available."));
            return;
        }

        report(new(CaptureUiPhase.Starting, "Starting Explore",
            "Windows will ask once to approve the broker recording system-wide events. The viewer stays unprivileged."));
        BrokerConnection? connection = null;
        CaptureId? captureId = null;
        string? sessionPath = null;
        string? summary = null;
        bool closed = false;
        try
        {
            connection = await WindowsBrokerLauncher.LaunchAsync(target, cancellationToken: stop).ConfigureAwait(false);
            milestones = milestones with { BrokerReady = elapsed.Elapsed };
            WindowsBrokerPipeClient client = connection.Client;
            if (await client.SendAsync(new BrokerHelloRequest(Guid.NewGuid(), 1, 1,
                        BrokerHelloNegotiator.SupportedFeatures, BrokerProtocolFeature.PreparedPlanDigest), stop)
                    .ConfigureAwait(false) is not BrokerHelloResponse)
            {
                throw new InvalidDataException("The capture broker protocol is incompatible. Reinstall InterCat.");
            }

            BrokerWireResponse response = await client.SendAsync(ExploreRequest(options.MaximumDurationSeconds), stop)
                .ConfigureAwait(false);
            if (response is not BrokerPrepareCaptureResponse prepared)
            {
                throw new InvalidDataException("The broker did not return a capture review.");
            }

            if (!prepared.Prepared || prepared.Grant is null)
            {
                report(new(CaptureUiPhase.Unavailable, "Explore is unavailable here",
                    prepared.RefusalReason ?? "The broker refused this profile. No capture was started."));
                return;
            }

            summary = Describe(prepared.Summary);
            milestones = milestones with
            {
                Prepared = elapsed.Elapsed,
                PublicationInterval = TimeSpan.FromMilliseconds(prepared.Summary.PublicationIntervalMilliseconds),
            };
            report(new(CaptureUiPhase.Starting, "Capture plan ready", summary, summary));
            response = await client.SendAsync(
                new BrokerStartCaptureRequest(prepared.Grant.Token, Guid.NewGuid()), stop).ConfigureAwait(false);
            if (response is not BrokerStartCaptureResponse { Code: BrokerOperationCode.Started, CaptureId: { } started })
            {
                report(new(CaptureUiPhase.Unavailable, "Explore did not start",
                    response is BrokerStartCaptureResponse failed
                        ? failed.FailureReason ?? failed.Code.ToString()
                        : "The broker did not confirm the capture.", summary));
                return;
            }

            captureId = started;
            milestones = milestones with { Started = elapsed.Elapsed };
            BrokerCaptureStatusResponse status = await StatusAsync(client, started, CancellationToken.None)
                .ConfigureAwait(false);
            string evidencePath = status.EvidenceDirectory
                ?? throw new InvalidDataException("The broker did not publish an evidence location.");
            string sessionRoot = options.SessionRoot ?? DefaultSessionRoot();
            sessionPath = Path.Combine(sessionRoot, $"explore-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{started.Value:N}");

            // Where this session's evidence is, held beside the session while it is followed, so a later launch can
            // finish a follow that ends early (§3.1 step 6). Without it only that offer is lost, never the capture.
            using LiveFollowHold? ticket = HoldTicket(started, evidencePath, sessionPath, status.LeaseExpiresAtUtc);
            report(new(CaptureUiPhase.Recording, "Recording · follow latest",
                "Raw evidence is broker-owned. Published chunks are derived into your session below. "
                + "Unobserved activity is not zero.", summary, sessionPath));

            // Following and projecting run off this loop, one step at a time, so status, the live preview and the owner
            // lease keep their own cadence however long a growing session takes to derive (§12, §19.3, revision 128's
            // 10-minute measurement). A finished step wakes the loop at once, so its overview is not held for a poll.
            using var derivation = new LiveDerivation(evidencePath, sessionPath, elapsed);
            Task<LiveDerivationStep>? deriving = null;
            bool derivingAfterClose = false;
            long stepStarted = 0;
            long statusRead = 0;
            BrokerCaptureStatusResponse? closedStatus = null;
            DateTimeOffset nextRenewal = DateTimeOffset.UtcNow + LeaseRenewal;
            bool stopSent = false;
            using var finishing = new CancellationTokenSource();
            try
            {
                while (true)
                {
                    if (stop.IsCancellationRequested && !stopSent)
                    {
                        stopSent = true;
                        finishing.CancelAfter(FinishTimeout);
                        report(new(CaptureUiPhase.Finishing, "Stopping and keeping the session",
                            "The broker is finalizing. InterCat is deriving everything it published.", summary, sessionPath));
                        BrokerWireResponse stopped = await client.SendAsync(
                            new BrokerStopCaptureRequest(started, Guid.NewGuid()), finishing.Token)
                            .ConfigureAwait(false);
                        if (stopped is BrokerStopCaptureResponse { FailureReason: { } reason })
                        {
                            report(new(CaptureUiPhase.Finishing, "Stop needs attention",
                                reason + " The owner lease still bounds this capture.", summary, sessionPath));
                        }
                    }

                    try
                    {
                        CancellationToken work = stopSent ? finishing.Token : stop;
                        if (deriving is { IsCompleted: true } done)
                        {
                            deriving = null;

                            // Stopping cancels a step begun while recording; the next one runs under the finishing token.
                            if (!(done.IsCanceled && stopSent && !finishing.IsCancellationRequested))
                            {
                                LiveDerivationStep step = await done.ConfigureAwait(false);
                                HandOff(step);
                                if (derivingAfterClose && closedStatus is { } final)
                                {
                                    // This step began after the broker reported the capture closed, so it followed every
                                    // chunk the capture published.
                                    closed = true;
                                    bool hasSession = derivation.HasSession;
                                    bool allPublishedFollowed = derivation.Last is { } last
                                        && last.DerivedChunks == last.EvidenceChunks;

                                    // A closed capture publishes nothing more: a session holding every chunk it
                                    // published has nothing left for a later launch to finish.
                                    if (allPublishedFollowed)
                                    {
                                        ticket?.Complete();
                                    }
                                    report(new(CaptureUiPhase.Complete,
                                        !hasSession ? "Capture closed without a derived session"
                                            : final.StopMilestones.FullyFinalized && allPublishedFollowed
                                                ? "Session saved" : "Partial session saved",
                                        !hasSession
                                            ? "No evidence chunk was published before closure. The broker kept its raw "
                                                + "outcome; there is no analysis session at the path below."
                                            : !allPublishedFollowed
                                                ? "Not every published chunk reached the viewer. The existing derived "
                                                    + "session is kept."
                                                : final.FailureReason
                                                    ?? "The capture is closed. All published evidence was followed.",
                                        summary, sessionPath));
                                    return;
                                }
                            }
                        }

                        if (deriving is null && Stopwatch.GetElapsedTime(stepStarted) >= Poll)
                        {
                            stepStarted = Stopwatch.GetTimestamp();
                            derivingAfterClose = closedStatus is not null;
                            deriving = derivation.StepAsync(work);
                        }

                        // Status keeps its own cadence: a step that finishes early wakes the loop to hand its overview
                        // over, not to ask the broker again.
                        if (Stopwatch.GetElapsedTime(statusRead) >= Poll)
                        {
                            statusRead = Stopwatch.GetTimestamp();
                            status = await StatusAsync(client, started, work).ConfigureAwait(false);
                            if (status.State == CaptureLifecycle.Closed)
                            {
                                closedStatus ??= status;
                            }

                            // Live counters change continuously; they are passed on at most once a second, and the live
                            // preview at most four times a second, only while recording. Once stop is sent the window
                            // shows finishing, and a re-sent recording update would undo that.
                            if (!stopSent && lastLive is { } recording)
                            {
                                DateTimeOffset now = DateTimeOffset.UtcNow;
                                bool healthDue = status.Health is { } live && live != health && now - healthReported >= HealthReport;
                                bool previewDue = status.Preview is { } fresh && !SamePreview(fresh, preview)
                                    && now - previewReported >= PreviewReport;
                                if (healthDue)
                                {
                                    health = status.Health;
                                    healthReported = now;
                                }

                                if (previewDue)
                                {
                                    preview = status.Preview;
                                    previewReported = now;
                                }

                                if (healthDue || previewDue)
                                {
                                    report(recording with
                                    {
                                        LiveHealth = health,
                                        LivePreview = preview,
                                        PreviewObservedQpc = previewDue ? Stopwatch.GetTimestamp() : null,
                                    });
                                }
                            }

                            if (!stopSent && closedStatus is null && DateTimeOffset.UtcNow >= nextRenewal)
                            {
                                if (await client.SendAsync(new BrokerRenewOwnerLeaseRequest(started), work)
                                        .ConfigureAwait(false) is BrokerRenewOwnerLeaseResponse { LeaseExpiresAtUtc: { } expires })
                                {
                                    ticket?.Renew(expires);
                                }

                                nextRenewal = DateTimeOffset.UtcNow + LeaseRenewal;
                            }
                        }

                        // Sleep until the next status read or step is due, or until the running step finishes.
                        TimeSpan untilStatus = Poll - Stopwatch.GetElapsedTime(statusRead);
                        TimeSpan untilStep = deriving is null ? Poll - Stopwatch.GetElapsedTime(stepStarted) : untilStatus;
                        TimeSpan wait = untilStatus < untilStep ? untilStatus : untilStep;
                        Task tick = Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, work);
                        await (deriving is null ? tick : Task.WhenAny(tick, deriving)).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!stopSent && stop.IsCancellationRequested)
                    {
                        // Stop was requested during a status read, a renewal or the wait. The next pass uses a fresh
                        // finishing token, requests broker stop, and derives the published prefix.
                    }
                }
            }
            finally
            {
                // Nothing outlives the runner: a step still running is halted and awaited before the stores it holds go.
                await derivation.HaltAsync(deriving).ConfigureAwait(false);
            }

            void HandOff(LiveDerivationStep step)
            {
                if (step is not { Generation: { } generation, Follow: { } follow })
                {
                    return;
                }

                milestones = milestones with { FirstGeneration = milestones.FirstGeneration ?? step.GenerationAt };
                if (step.Overview is { } overview)
                {
                    milestones = milestones with { FirstOverview = milestones.FirstOverview ?? elapsed.Elapsed };
                    report(new(stopSent ? CaptureUiPhase.Finishing : CaptureUiPhase.Recording,
                        $"{follow.DerivedRecords.ToString("N0", CultureInfo.CurrentCulture)} observed records",
                        $"Generation {generation:N0} · {follow.DerivedChunks:N0} published chunks. "
                        + "Graph edges are paired TCP only; other observations remain in the timeline.",
                        summary, sessionPath, overview,
                        Visibility: new(generation, follow.DerivedRecords, Stopwatch.GetTimestamp(),
                            step.Derivation, step.Projection),
                        OverviewChunks: follow.DerivedChunks));
                }
                else if (step.OverviewProblem is { } problem)
                {
                    report(new(CaptureUiPhase.Recording, "Capture continues; overview is bounded",
                        problem + " The session is kept and can be inspected headlessly.",
                        summary, sessionPath));
                }
            }
        }
        catch (BrokerLaunchException exception)
        {
            report(new(CaptureUiPhase.Unavailable,
                exception.Failure == BrokerLaunchFailure.ElevationDeclined
                    ? "Administrator approval declined" : "Capture unavailable",
                exception.Message, summary, sessionPath));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException)
        {
            report(new(CaptureUiPhase.Unavailable, "Capture interrupted",
                exception.Message + " Any published session data remains at the path shown below.",
                summary, sessionPath));
        }
        finally
        {
            if (!closed && captureId is { } active && connection is not null)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    _ = await connection.Client.SendAsync(new BrokerStopCaptureRequest(active, Guid.NewGuid()),
                        timeout.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException
                    or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException)
                {
                    // The owner's lease still bounds capture lifetime when the pipe is gone.
                }
            }
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Whether a preview says nothing new: the same chunk, records and unbinned records. A count moves only with a record,
    /// so an unchanged total means unchanged counts.
    /// </summary>
    private static bool SamePreview(BrokerCapturePreview fresh, BrokerCapturePreview? previous) =>
        previous is not null && fresh.OpenChunk == previous.OpenChunk && fresh.RetainedChunks == previous.RetainedChunks
        && fresh.CountedRecords == previous.CountedRecords && fresh.UnbinnedRecords == previous.UnbinnedRecords;

    /// <summary>
    /// Writes and holds the follow's ticket; null when it cannot be written, which costs only a later launch's offer to
    /// finish the session, never the capture.
    /// </summary>
    private static LiveFollowHold? HoldTicket(
        CaptureId capture, string evidencePath, string sessionPath, DateTimeOffset ownerLeaseExpiresUtc)
    {
        try
        {
            return LiveFollowTicket.For(capture.Value, evidencePath, sessionPath, DateTimeOffset.UtcNow, ownerLeaseExpiresUtc)
                .Hold();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The user's own session folder; a viewer never writes derived sessions into broker-owned space (ADR-027).</summary>
    public static string DefaultSessionRoot()
    {
        string userData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(userData)
            ? throw new InvalidOperationException("Windows did not provide a local user-data directory.")
            : Path.Combine(userData, "InterCat", "Sessions");
    }

    [SupportedOSPlatform("windows")]
    private static async Task<BrokerCaptureStatusResponse> StatusAsync(
        WindowsBrokerPipeClient client, CaptureId id, CancellationToken cancellationToken) =>
        await client.SendAsync(new BrokerGetStatusRequest(id), cancellationToken).ConfigureAwait(false)
            as BrokerCaptureStatusResponse
        ?? throw new InvalidDataException("The broker did not return this capture's status.");
}
