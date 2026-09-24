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

public sealed record CaptureUiUpdate(
    CaptureUiPhase Phase,
    string Headline,
    string Detail,
    string? Summary = null,
    string? SessionPath = null,
    SessionOverviewBundle? Overview = null,
    CaptureMilestones? Milestones = null,
    CaptureVisibility? Visibility = null,
    BrokerCaptureHealth? LiveHealth = null);

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
        report = update =>
        {
            update = update with { Milestones = milestones, LiveHealth = update.LiveHealth ?? health };
            if (update.Phase == CaptureUiPhase.Recording)
            {
                lastLive = update with { Overview = null, Visibility = null };
            }

            inner(update);
        };
        DateTimeOffset healthReported = DateTimeOffset.MinValue;
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
            string sessionRoot = options.SessionRoot ?? LocalSessionRoot();
            sessionPath = Path.Combine(sessionRoot, $"explore-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{started.Value:N}");
            report(new(CaptureUiPhase.Recording, "Recording · follow latest",
                "Raw evidence is broker-owned. Published chunks are derived into your session below. "
                + "Unobserved activity is not zero.", summary, sessionPath));

            LiveSessionFollower? follower = null;
            SessionStore? derived = null;
            long shownGeneration = -1;
            DateTimeOffset nextRenewal = DateTimeOffset.UtcNow + LeaseRenewal;
            bool stopSent = false;
            bool settled = false;
            FollowStep? last = null;
            using var finishing = new CancellationTokenSource();
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
                    if (follower is null)
                    {
                        SessionStore evidence = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidencePath));
                        if (evidence.Current is { } source)
                        {
                            Directory.CreateDirectory(sessionPath);
                            derived = SessionStore.Open(LocalOwnedDirectory.Open(sessionPath),
                                source.SessionId, source.SourceIdentity);
                            follower = LiveSessionFollower.Open(evidence, derived, cancellationToken: work);
                        }
                    }

                    if (follower is not null)
                    {
                        long followStarted = Stopwatch.GetTimestamp();
                        FollowStep step = follower.CatchUp(cancellationToken: work);
                        TimeSpan derivation = Stopwatch.GetElapsedTime(followStarted);
                        last = step;
                        if (derived!.Current is { } current && current.Generation != shownGeneration)
                        {
                            shownGeneration = current.Generation;
                            milestones = milestones with { FirstGeneration = milestones.FirstGeneration ?? elapsed.Elapsed };
                            try
                            {
                                long projectionStarted = Stopwatch.GetTimestamp();
                                SessionOverviewBundle overview = SessionOverviewProjector.Project(derived, cancellationToken: work);
                                TimeSpan projection = Stopwatch.GetElapsedTime(projectionStarted);
                                milestones = milestones with { FirstOverview = milestones.FirstOverview ?? elapsed.Elapsed };
                                report(new(stopSent ? CaptureUiPhase.Finishing : CaptureUiPhase.Recording,
                                    $"{step.DerivedRecords.ToString("N0", CultureInfo.CurrentCulture)} observed records",
                                    $"Generation {current.Generation:N0} · {step.DerivedChunks:N0} published chunks. "
                                    + "Graph edges are paired TCP only; other observations remain in the timeline.",
                                    summary, sessionPath, overview,
                                    Visibility: new(current.Generation, step.DerivedRecords, Stopwatch.GetTimestamp(),
                                        derivation, projection)));
                            }
                            catch (InvalidOperationException exception)
                            {
                                report(new(CaptureUiPhase.Recording, "Capture continues; overview is bounded",
                                    exception.Message + " The session is kept and can be inspected headlessly.",
                                    summary, sessionPath));
                            }
                        }
                    }

                    status = await StatusAsync(client, started, work).ConfigureAwait(false);

                    // Live counters change continuously; they are passed on at most once a second, only while recording.
                    // Once stop is sent the window shows finishing, and a re-sent recording update would undo that.
                    if (!stopSent && status.Health is { } live && live != health && lastLive is { } recording
                        && DateTimeOffset.UtcNow - healthReported >= HealthReport)
                    {
                        health = live;
                        healthReported = DateTimeOffset.UtcNow;
                        report(recording with { LiveHealth = live });
                    }

                    if (status.State == CaptureLifecycle.Closed)
                    {
                        if (settled)
                        {
                            closed = true;
                            bool hasSession = derived?.Current is not null;
                            bool allPublishedFollowed = last is not null
                                && last.DerivedChunks == last.EvidenceChunks;
                            report(new(CaptureUiPhase.Complete,
                                !hasSession ? "Capture closed without a derived session"
                                    : status.StopMilestones.FullyFinalized && allPublishedFollowed
                                        ? "Session saved" : "Partial session saved",
                                !hasSession
                                    ? "No evidence chunk was published before closure. The broker kept its raw outcome; "
                                        + "there is no analysis session at the path below."
                                    : !allPublishedFollowed
                                        ? "Not every published chunk reached the viewer. The existing derived session is kept."
                                        : status.FailureReason ?? "The capture is closed. All published evidence was followed.",
                                summary, sessionPath));
                            return;
                        }

                        settled = true;
                        continue;
                    }

                    if (!stopSent && DateTimeOffset.UtcNow >= nextRenewal)
                    {
                        _ = await client.SendAsync(new BrokerRenewOwnerLeaseRequest(started), work)
                            .ConfigureAwait(false);
                        nextRenewal = DateTimeOffset.UtcNow + LeaseRenewal;
                    }

                    await Task.Delay(Poll, work).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!stopSent && stop.IsCancellationRequested)
                {
                    // Stop was requested during a follow, status or renewal. The next pass uses a fresh
                    // finishing token, requests broker stop, and derives the published prefix.
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

    /// <summary>The user's own session folder; a viewer never writes derived sessions into broker-owned space (ADR-027).</summary>
    private static string LocalSessionRoot()
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
