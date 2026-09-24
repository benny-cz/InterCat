using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.Versioning;
using InterCat.Capture.Recording;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.CaptureBroker;

/// <summary>
/// Privileged, evidence-only capture runtime. It never derives rows or adopts an existing evidence directory.
/// <see cref="BrokerHost"/> serves it only behind an explicit unqualified-capture opt-in until real ETW has been exercised
/// through an elevated crash/restart.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BrokerEvidenceCaptureRuntime : IBrokerCaptureRuntime, IBrokerCaptureCompletionProbe, IAsyncDisposable
{
    private readonly WindowsBrokerRoot root;
    private readonly IEtwSessionHost host;
    private readonly IEtwSessionReclaimer reclaimer;
    private readonly Func<string, VolumeSpace> volumeProbe;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<CaptureId, ActiveCapture> active = [];

    // Read by status requests without the start/stop gate, which a stop can hold while it drains.
    private readonly ConcurrentDictionary<CaptureId, LiveHealthProbe> health = new();
    private bool disposed;

    public BrokerEvidenceCaptureRuntime(
        WindowsBrokerRoot root,
        IEtwSessionHost host,
        IEtwSessionReclaimer reclaimer,
        Func<string, VolumeSpace>? volumeProbe = null)
    {
        this.root = root ?? throw new ArgumentNullException(nameof(root));
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.reclaimer = reclaimer ?? throw new ArgumentNullException(nameof(reclaimer));
        this.volumeProbe = volumeProbe ?? WindowsVolumeSpace.Probe;
    }

    /// <summary>Where this capture's evidence is published; its owner may read and follow it but never write.</summary>
    public string EvidenceDirectory(CaptureId captureId) =>
        System.IO.Path.Combine(root.Path, $"capture-{captureId.Value:N}");

    /// <summary>
    /// The capture's acquisition counters while it records, for a status request; null when it is not recording. An
    /// unreadable loss counter is reported as unknown rather than zero.
    /// </summary>
    public BrokerCaptureHealth? ReadHealth(CaptureId captureId) =>
        health.TryGetValue(captureId, out LiveHealthProbe? probe) && probe.Read() is { } live
            ? new(
                live.Snapshot.AdmittedRecords,
                live.Snapshot.ObservedRecords,
                live.Snapshot.ApplicationDrops,
                live.SourceLossReadable ? live.Snapshot.ProviderReportedEventLoss : null,
                live.SourceLossReadable ? live.Snapshot.ConsumerReportedBufferLoss : null,
                live.Snapshot.QueueDepth,
                live.Snapshot.QueueCapacity)
            : null;

    public async ValueTask<bool> HasCompletedAsync(CaptureId captureId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return active.TryGetValue(captureId, out ActiveCapture? capture)
                && capture.Run?.IsCompleted == true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<BrokerRuntimeStartOutcome> StartAsync(
        BrokerCaptureOwnership ownership,
        PreparedCapturePlan plan,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(ownership);
        ArgumentNullException.ThrowIfNull(plan);
        if (!ownership.Session.IsValidFor(ownership.CaptureId)
            || !ownership.Session.HasFullTokenInName
            || !string.Equals(ownership.PlanDigest, plan.Digest, StringComparison.Ordinal)
            || plan.Quota.Validate() is not null
            || plan.Retention != BrokerRetentionPolicy.StopAtLimit)
        {
            return new(false, "The durable capture ownership, prepared digest or operational limits are invalid.");
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (active.ContainsKey(ownership.CaptureId))
            {
                return new(false, "This capture is already running in the broker.");
            }

            if (!HasStartHeadroom(root.Path, plan.Quota.MinimumFreeDiskBytes, out string? diskProblem))
            {
                return new(false, diskProblem);
            }

            WindowsBrokerRoot captureRoot = root.OpenCaptureDirectory(ownership.CaptureId);
            if (!captureRoot.Report.CreatedByThisBroker)
            {
                captureRoot.Dispose();
                return new(false, "The capture evidence directory already exists; a new start cannot adopt its contents.");
            }

            var capture = new ActiveCapture(captureRoot);
            active.Add(ownership.CaptureId, capture);
            bool acknowledged = false;
            try
            {
                var identity = CaptureSessionIdentity.FromDurableOwnership(
                    ownership.CaptureId,
                    ownership.Session.SessionName,
                    ownership.Session.OwnershipToken,
                    Environment.ProcessId,
                    ownership.CreatedAtUtc);
                var sessionPlan = new OwnedSessionPlan
                {
                    Identity = identity,
                    Providers = plan.Providers,
                    Sources = plan.Sources,
                    Admission = plan.EffectiveAdmission,
                    MaximumDuration = TimeSpan.FromSeconds(plan.Quota.MaximumDurationSeconds),
                    PreserveExtendedData = plan.PreserveExtendedData,
                    DeliveryFlushInterval = plan.PublicationInterval is null
                        ? null
                        : BrokerJournalPublicationPolicy.LiveDeliveryFlush,
                };
                SessionStore store = SessionStore.Open(captureRoot, ownership.CaptureId.Value, plan.Digest);
                if (store.Current is not null)
                {
                    throw new InvalidDataException("A new capture directory unexpectedly contains a published generation.");
                }

                capture.Stop.CancelAfter(sessionPlan.MaximumDuration);
                var probe = new LiveHealthProbe();
                health[ownership.CaptureId] = probe;
                capture.Run = LiveRecorder.RecordAsync(
                    sessionPlan,
                    host,
                    store,
                    token => MonitorFreeDiskAsync(capture, plan.Quota.MinimumFreeDiskBytes, token),
                    ownership.CreatedAtUtc,
                    publishEvery: plan.PublicationInterval,
                    derive: null,
                    onReady: _ => capture.Ready.TrySetResult(),
                    maximumJournalBytes: plan.Quota.MaximumJournalBytes,
                    diskFloor: new LiveDiskFloor(
                        plan.Quota.MinimumFreeDiskBytes, () => volumeProbe(captureRoot.Path)),
                    publishFirstAfter: plan.FirstPublication,
                    healthProbe: probe,
                    cancellationToken: capture.Stop.Token);
                _ = capture.Run.ContinueWith(
                    _ => health.TryRemove(ownership.CaptureId, out LiveHealthProbe? _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                Task first = await Task.WhenAny(capture.Ready.Task, capture.Run).ConfigureAwait(false);
                if (first == capture.Ready.Task && !capture.Run.IsCompleted)
                {
                    acknowledged = true;
                    return new(true);
                }

                LiveCaptureResult result = await capture.Run.ConfigureAwait(false);
                return new(false, result.Start.FailureReason ?? "Capture stopped before its start could be acknowledged.");
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                try
                {
                    _ = reclaimer.StopPreviouslyOwnedSession(
                        ownership.Session.SessionName, ownership.Session.OwnershipToken);
                }
                catch (Exception cleanup) when (cleanup is not OutOfMemoryException)
                {
                    return new(false, $"Capture start failed: {exception.Message}; owned-session cleanup failed: {cleanup.Message}");
                }

                return new(false, $"Capture start failed: {exception.Message}");
            }
            finally
            {
                if (!acknowledged)
                {
                    active.Remove(ownership.CaptureId);
                    capture.Dispose();
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<BrokerRuntimeStopOutcome> StopAsync(
        BrokerCaptureOwnership ownership,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(ownership);
        if (!ownership.Session.IsValidFor(ownership.CaptureId))
        {
            return new(BrokerStopMilestones.None, "The durable ETW name does not match this capture and token.");
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!active.Remove(ownership.CaptureId, out ActiveCapture? capture))
            {
                return ReclaimAfterRestart(ownership);
            }

            try
            {
                capture.UserStopRequested = !capture.Run!.IsCompleted;
                capture.Stop.Cancel();
                LiveCaptureResult result = await capture.Run!.ConfigureAwait(false);
                if (result.Stop is null || result.Generation is null)
                {
                    return new(new(true, result.Stop?.ProvidersStopped ?? false,
                            result.Stop?.CallbacksDrained ?? false, false, true),
                        "The capture stopped without a verified final journal generation.");
                }

                try
                {
                    _ = reclaimer.StopPreviouslyOwnedSession(
                        ownership.Session.SessionName, ownership.Session.OwnershipToken);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    return new(new(true, false, true, true, true),
                        $"The journal finalized, but the owned ETW session could not be confirmed stopped: {exception.Message}");
                }

                string? reason = !result.Stop.CallbacksDrained
                    ? "The journal finalized, but the ETW delivery pump did not confirm callback drain."
                    : result.JournalQuotaReached
                        ? "The configured journal byte limit was reached; the admitted prefix was finalized."
                        : result.DiskReserveReached
                            ? $"{result.DiskReserveReason} The admitted prefix was finalized."
                        : capture.LimitReason ?? (!capture.UserStopRequested
                            ? "The configured maximum capture duration elapsed; evidence was finalized."
                            : null);
                return new(new(true, true, result.Stop.CallbacksDrained, true, true), reason);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                BrokerRuntimeStopOutcome reclaimed = ReclaimAfterRestart(ownership);
                return reclaimed with
                {
                    FailureReason = $"Final journal publication failed: {exception.Message} "
                        + (reclaimed.FailureReason ?? "The ETW stop was attempted."),
                };
            }
            finally
            {
                capture.Dispose();
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (ActiveCapture capture in active.Values)
            {
                capture.Stop.Cancel();
            }

            foreach (ActiveCapture capture in active.Values)
            {
                try
                {
                    if (capture.Run is not null)
                    {
                        _ = await capture.Run.ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // Durable startup recovery owns a failed finalization; disposal cannot claim one.
                }
                finally
                {
                    capture.Dispose();
                }
            }

            active.Clear();
        }
        finally
        {
            gate.Release();
            gate.Dispose();
        }
    }

    private BrokerRuntimeStopOutcome ReclaimAfterRestart(BrokerCaptureOwnership ownership)
    {
        bool providerStopped = ownership.StopMilestones.ProvidersStopped;
        string? providerProblem = null;
        if (!providerStopped)
        {
            try
            {
                _ = reclaimer.StopPreviouslyOwnedSession(
                    ownership.Session.SessionName, ownership.Session.OwnershipToken);
                providerStopped = true;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                providerProblem = $"The owned ETW session could not be confirmed stopped: {exception.Message}";
            }
        }

        bool publishedCallbacksDrained = false;
        string? journalProblem = null;
        bool alreadyProven = ownership.StopMilestones is { JournalFinalized: true, CallbacksDrained: true };
        bool verifiedFinalPublication = !alreadyProven && TryVerifyFinalPublication(
            ownership, out publishedCallbacksDrained, out journalProblem);
        bool journalFinalized = ownership.StopMilestones.JournalFinalized || verifiedFinalPublication;

        // A callback drain already committed before the crash remains evidence. A new finalization marker may also
        // prove it, because LiveRecorder stages that marker only after the owned session stop has returned.
        bool callbacksDrained = ownership.StopMilestones.CallbacksDrained
            || (verifiedFinalPublication && publishedCallbacksDrained);
        BrokerStopMilestones milestones = new(
            Requested: true,
            ProvidersStopped: providerStopped,
            CallbacksDrained: callbacksDrained,
            JournalFinalized: journalFinalized,
            AnalysisFinalized: true);
        if (milestones.FullyFinalized)
        {
            return new(milestones);
        }

        var problems = new List<string>(3);
        if (!providerStopped)
        {
            problems.Add(providerProblem ?? "The owned ETW session is not proven stopped.");
        }

        if (!callbacksDrained)
        {
            problems.Add(
                "The previous process did not durably prove callback drain and no verified finalization marker does so.");
        }

        if (!journalFinalized)
        {
            problems.Add(journalProblem ?? "Final journal publication is not proven.");
        }

        // With the session proven stopped and its process gone, nothing a later stop does can prove more. Release the
        // dead writer's staging and close the capture as interrupted rather than retrying it at every broker start.
        // A provider stop that failed stays retryable: stopping the session later is real progress.
        if (providerStopped && TryReleaseInterruptedCapture(ownership, out string summary))
        {
            problems.Insert(0, summary);
            return new(milestones, string.Join(" ", problems), Terminal: true);
        }

        return new(milestones, string.Join(" ", problems));
    }

    /// <summary>
    /// Removes the staging files the interrupted writer abandoned and states what evidence remains. Returns false when
    /// staging could not be released (it is still owned, or the store cannot be opened), so the capture stays retryable.
    /// </summary>
    private bool TryReleaseInterruptedCapture(BrokerCaptureOwnership ownership, out string summary)
    {
        const string Interrupted = "Interrupted: the broker recording this capture ended before finalizing it.";
        if (!Directory.Exists(EvidenceDirectory(ownership.CaptureId)))
        {
            summary = $"{Interrupted} No evidence had been published.";
            return true;
        }

        try
        {
            using WindowsBrokerRoot captureRoot = root.OpenExistingCaptureDirectory(ownership.CaptureId);
            SessionStore store = SessionStore.Open(captureRoot, ownership.CaptureId.Value, ownership.PlanDigest);
            StagingCleanupReport cleanup = store.CleanupAbandonedStaging();
            if (cleanup.ActiveFiles.Count > 0)
            {
                summary = string.Empty;
                return false;
            }

            StoreDependency[] journals = store.Current?.Dependencies
                .Where(dependency => dependency.Kind == StoreDependencyKind.Journal)
                .ToArray() ?? [];
            string kept = journals.Length == 0
                ? "No evidence had been published."
                : $"The published prefix is kept: {journals.Length} journal chunk(s), "
                    + $"{journals.Sum(journal => journal.LengthBytes).ToString("N0", CultureInfo.InvariantCulture)} bytes, "
                    + $"generation {store.Current!.Generation}; records after the last publication were lost.";
            summary = $"{Interrupted} {kept} Released {cleanup.RemovedFiles.Count} abandoned staging file(s).";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException)
        {
            summary = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Verifies a final publication from the protected capture store. A complete journal by itself is insufficient:
    /// every live chunk is complete. New recordings publish capture-finalization-v1 only with their last chunk; a
    /// coverage ledger is accepted as the legacy last-chunk marker because older LiveRecorder builds published it only
    /// on the final generation. The boundary journal is replayed through its terminal before the milestone is promoted.
    /// </summary>
    private bool TryVerifyFinalPublication(
        BrokerCaptureOwnership ownership,
        out bool callbacksDrained,
        out string? problem)
    {
        callbacksDrained = false;
        try
        {
            using WindowsBrokerRoot captureRoot = root.OpenExistingCaptureDirectory(ownership.CaptureId);
            SessionStore store = SessionStore.Open(captureRoot, ownership.CaptureId.Value, ownership.PlanDigest);
            SessionManifestV1 manifest = store.Current
                ?? throw new InvalidDataException("The capture store has no committed generation.");
            if (!string.Equals(manifest.SourceIdentity, ownership.PlanDigest, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The committed generation does not name the prepared-plan digest in durable ownership.");
            }

            CaptureFinalizationV1? finalization = CaptureFinalizationV1.Read(captureRoot, manifest);
            bool legacyCoverageFinal = false;
            if (finalization is not null)
            {
                if (finalization.CaptureId != ownership.CaptureId.Value)
                {
                    throw new InvalidDataException(
                        "The capture finalization marker belongs to a different capture.");
                }

                callbacksDrained = finalization.CallbacksDrained;
            }
            else if (manifest.Dependencies.Any(dependency => dependency.Kind == StoreDependencyKind.CoverageLedger))
            {
                _ = SessionSegments.CoverageLedger(captureRoot, manifest)
                    ?? throw new InvalidDataException("The legacy final generation names no readable coverage ledger.");
                legacyCoverageFinal = true;
            }
            else
            {
                problem = "No capture-finalization marker (or legacy final coverage ledger) is committed.";
                return false;
            }

            CommittedBoundary boundary = manifest.Boundary;
            StoreDependency journal = manifest.Dependencies.SingleOrDefault(dependency =>
                    dependency.Kind == StoreDependencyKind.Journal
                    && dependency.Name.Equals(boundary.JournalName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("The committed boundary journal is not a manifest dependency.");
            if (journal.LengthBytes != boundary.CommittedBytes
                || !string.Equals(journal.Digest, boundary.Digest, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The committed boundary does not match its journal dependency.");
            }

            using FileStream stream = captureRoot.OpenOwnedFile(
                journal.Name, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
            (CaptureId journalCapture, _) = JournalV1Reader.ReadSourceClock(stream);
            if (journalCapture != ownership.CaptureId)
            {
                throw new InvalidDataException("The committed final journal belongs to a different capture.");
            }

            long records = JournalV1Reader.ReplayBatches(stream, static (_, _, _, _) => { }, CancellationToken.None);
            if (records != boundary.CommittedRecords)
            {
                throw new InvalidDataException(
                    $"The committed boundary states {boundary.CommittedRecords} records but its journal replays {records}.");
            }

            problem = legacyCoverageFinal
                ? "Final publication was proven by the legacy final coverage ledger."
                : null;
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException
            or InvalidOperationException)
        {
            problem = $"Final journal publication could not be verified: {exception.Message}";
            return false;
        }
    }

    /// <summary>
    /// Observes the volume while recording is idle. The recorder enforces the floor at every append; this notices
    /// another program consuming the volume while InterCat writes nothing, and ends the capture while there is still
    /// room to finish it.
    /// </summary>
    private async Task MonitorFreeDiskAsync(ActiveCapture capture, long minimumFreeBytes, CancellationToken token)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            try
            {
                long available = volumeProbe(capture.Root.Path).AvailableBytes;
                if (available < minimumFreeBytes)
                {
                    capture.LimitReason =
                        $"Free space on the evidence volume ({LiveDiskFloor.Describe(available)}) fell below the "
                        + $"configured {LiveDiskFloor.Describe(minimumFreeBytes)} reserve while the capture was idle, "
                        + "so recording stopped and the admitted prefix was finalized.";
                    return;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                capture.LimitReason =
                    $"Free space on the evidence volume could not be verified ({exception.Message}), so recording "
                    + "stopped and the admitted prefix was finalized.";
                return;
            }
        }
    }

    /// <summary>
    /// Refuses a start that could not finish: the volume must hold the configured reserve plus the bounded headroom a
    /// stopped recording needs to publish its last generation.
    /// </summary>
    private bool HasStartHeadroom(string path, long minimumFreeBytes, out string? problem)
    {
        try
        {
            VolumeSpace space = volumeProbe(path);
            long headroom = LiveDiskFloor.FinalizationHeadroom(dependenciesAfterFinal: 4, space.AllocationUnitBytes);
            long needed = checked(minimumFreeBytes + headroom);
            problem = space.AvailableBytes < needed
                ? $"The evidence volume has {LiveDiskFloor.Describe(space.AvailableBytes)} free; starting needs at least "
                    + $"{LiveDiskFloor.Describe(needed)} (the {LiveDiskFloor.Describe(minimumFreeBytes)} reserve plus "
                    + $"{LiveDiskFloor.Describe(headroom)} to finish the recording). Free space on that volume or "
                    + "lower the reserve."
                : null;
            return problem is null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
            or OverflowException)
        {
            problem = $"Free space on the evidence volume could not be verified: {exception.Message}";
            return false;
        }
    }

    private sealed class ActiveCapture(WindowsBrokerRoot root) : IDisposable
    {
        public WindowsBrokerRoot Root { get; } = root;
        public CancellationTokenSource Stop { get; } = new();
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<LiveCaptureResult>? Run { get; set; }
        public string? LimitReason { get; set; }
        public bool UserStopRequested { get; set; }

        public void Dispose()
        {
            Stop.Dispose();
            Root.Dispose();
        }
    }
}
