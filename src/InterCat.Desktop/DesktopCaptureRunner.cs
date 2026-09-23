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
    SessionOverviewBundle? Overview = null);

/// <summary>
/// Ordinary-integrity Desktop owner of a bounded Explore capture. The elevated broker writes only raw evidence;
/// this process follows it into a user-owned session and projects only published generations (ADR-027, R16, R7).
/// </summary>
public static class DesktopCaptureRunner
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LeaseRenewal = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FinishTimeout = TimeSpan.FromSeconds(60);

    public static BrokerPrepareCaptureRequest ExploreRequest() => new(
        "explore", null, [], false, false,
        new BrokerCaptureQuota(600, 1_024L * 1_024 * 1_024, 1_024L * 1_024 * 1_024),
        BrokerRetentionPolicy.StopAtLimit, null, BrokerJournalPublication.Live);

    public static string Describe(BrokerEffectiveCaptureSummary summary) =>
        $"{summary.EffectiveProfileId ?? summary.RequestedProfileId} · "
        + $"{string.Join(", ", summary.Sources.Select(source => source.SourceId))} · "
        + $"up to {summary.Quota.MaximumDurationSeconds / 60:N0} min / "
        + $"{summary.Quota.MaximumJournalBytes / (1024 * 1024):N0} MiB journal · "
        + $"{summary.PublicationIntervalMilliseconds / 1000d:0.#} s publication. "
        + summary.CollectionStatement + " " + summary.Disclosure
        + (summary.Diagnostics.Count == 0 ? string.Empty
            : " Diagnostics: " + string.Join("; ", summary.Diagnostics));

    public static async Task RunAsync(Action<CaptureUiUpdate> report, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!OperatingSystem.IsWindows())
        {
            report(new(CaptureUiPhase.Unavailable, "Live capture needs Windows",
                "You can still open an existing session. No capture was started."));
            return;
        }

        BrokerLaunchTarget? target = BrokerLaunchTarget.BesideCurrentProcess();
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
            WindowsBrokerPipeClient client = connection.Client;
            if (await client.SendAsync(new BrokerHelloRequest(Guid.NewGuid(), 1, 1,
                        BrokerHelloNegotiator.SupportedFeatures, BrokerProtocolFeature.PreparedPlanDigest), stop)
                    .ConfigureAwait(false) is not BrokerHelloResponse)
            {
                throw new InvalidDataException("The capture broker protocol is incompatible. Reinstall InterCat.");
            }

            BrokerWireResponse response = await client.SendAsync(ExploreRequest(), stop).ConfigureAwait(false);
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
            BrokerCaptureStatusResponse status = await StatusAsync(client, started, CancellationToken.None)
                .ConfigureAwait(false);
            string evidencePath = status.EvidenceDirectory
                ?? throw new InvalidDataException("The broker did not publish an evidence location.");
            string userData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(userData))
            {
                throw new InvalidOperationException("Windows did not provide a local user-data directory.");
            }

            sessionPath = Path.Combine(userData, "InterCat", "Sessions",
                $"explore-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{started.Value:N}");
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
                        FollowStep step = follower.CatchUp(cancellationToken: work);
                        last = step;
                        if (derived!.Current is { } current && current.Generation != shownGeneration)
                        {
                            shownGeneration = current.Generation;
                            try
                            {
                                SessionOverviewBundle overview = SessionOverviewProjector.Project(derived, cancellationToken: work);
                                report(new(stopSent ? CaptureUiPhase.Finishing : CaptureUiPhase.Recording,
                                    $"{step.DerivedRecords.ToString("N0", CultureInfo.CurrentCulture)} observed records",
                                    $"Generation {current.Generation:N0} · {step.DerivedChunks:N0} published chunks. "
                                    + "Graph edges are paired TCP only; other observations remain in the timeline.",
                                    summary, sessionPath, overview));
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

    [SupportedOSPlatform("windows")]
    private static async Task<BrokerCaptureStatusResponse> StatusAsync(
        WindowsBrokerPipeClient client, CaptureId id, CancellationToken cancellationToken) =>
        await client.SendAsync(new BrokerGetStatusRequest(id), cancellationToken).ConfigureAwait(false)
            as BrokerCaptureStatusResponse
        ?? throw new InvalidDataException("The broker did not return this capture's status.");
}
