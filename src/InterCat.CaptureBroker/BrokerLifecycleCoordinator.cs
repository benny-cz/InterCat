using InterCat.Domain;

namespace InterCat.CaptureBroker;

/// <summary>
/// Transport-independent ownership coordinator. It records an operation intent before touching the
/// capture runtime and records the completion before returning success. The pipe host (<see cref="BrokerHost"/>) supplies
/// an OS-authenticated <see cref="BrokerClientIdentity"/>; this class never accepts a PID as identity.
/// </summary>
public sealed class BrokerLifecycleCoordinator : IDisposable
{
    public static readonly TimeSpan MinimumLease = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MaximumLease = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan DefaultLease = TimeSpan.FromSeconds(30);

    private readonly PreparedPlanRegistry preparedPlans;
    private readonly IBrokerLifecycleStore store;
    private readonly IBrokerCaptureRuntime runtime;
    private readonly TimeProvider clock;
    private readonly TimeSpan leaseDuration;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<CaptureId, PendingStopCompletion> pendingStopCompletions = [];
    private bool disposed;

    public BrokerLifecycleCoordinator(
        PreparedPlanRegistry preparedPlans,
        IBrokerLifecycleStore store,
        IBrokerCaptureRuntime runtime,
        TimeProvider? clock = null,
        TimeSpan? leaseDuration = null)
    {
        this.preparedPlans = preparedPlans ?? throw new ArgumentNullException(nameof(preparedPlans));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.clock = clock ?? TimeProvider.System;
        this.leaseDuration = leaseDuration ?? DefaultLease;
        if (this.leaseDuration < MinimumLease || this.leaseDuration > MaximumLease)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseDuration),
                $"Owner lease must be between {MinimumLease} and {MaximumLease}.");
        }
    }

    public async Task<BrokerStartOutcome> StartAsync(
        string preparedToken,
        Guid requestId,
        BrokerClientIdentity client,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(client);
        string? identityProblem = client.Validate();
        if (requestId == Guid.Empty || identityProblem is not null)
        {
            return new(
                BrokerOperationCode.InvalidRequest,
                null,
                CaptureLifecycle.Idle,
                null,
                requestId == Guid.Empty ? "A non-empty start request ID is required." : identityProblem);
        }

        if (!PreparedPlanRegistry.TryFingerprint(preparedToken, out string tokenFingerprint))
        {
            return RejectedToken();
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BrokerStoredRequest? existing = await store
                .FindRequestAsync(client.Owner, requestId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.Kind != BrokerRequestKind.Start
                    || !string.Equals(existing.TargetKey, tokenFingerprint, StringComparison.Ordinal))
                {
                    return new(
                        BrokerOperationCode.RequestConflict,
                        existing.CaptureId,
                        CaptureLifecycle.Idle,
                        null,
                        "This request ID is already bound to a different broker operation.");
                }

                return existing.Completed
                    ? existing.StartOutcome!
                    : new(
                        BrokerOperationCode.OperationPending,
                        existing.CaptureId,
                        CaptureLifecycle.Starting,
                        null,
                        "The original start request has a durable intent but no completed result yet.");
            }

            PreparedPlanResolution resolution = preparedPlans.Resolve(preparedToken, client.Owner);
            if (resolution.Code == PreparedPlanResolutionCode.Rejected)
            {
                return RejectedToken();
            }

            if (resolution.Code == PreparedPlanResolutionCode.AlreadyUsed)
            {
                return new(
                    BrokerOperationCode.PreparedPlanAlreadyUsed,
                    resolution.ExistingCaptureId,
                    CaptureLifecycle.Idle,
                    null,
                    "The prepared plan already belongs to a capture. Reuse its original request result instead of starting another session.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset now = clock.GetUtcNow();
            CaptureId captureId = CaptureId.New();
            var ownership = new BrokerCaptureOwnership
            {
                CaptureId = captureId,
                Owner = client.Owner,
                Session = BrokerSessionOwnership.Create(captureId),
                PlanDigest = resolution.Plan!.Digest,
                State = CaptureLifecycle.Starting,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                LeaseExpiresAtUtc = now.Add(leaseDuration),
                StopMilestones = BrokerStopMilestones.None,
            };
            var intent = new BrokerStoredRequest
            {
                Owner = client.Owner,
                RequestId = requestId,
                Kind = BrokerRequestKind.Start,
                TargetKey = tokenFingerprint,
                CaptureId = captureId,
                Completed = false,
            };

            try
            {
                await store.SaveStartIntentAsync(ownership, intent, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return PersistenceFailure(captureId, CaptureLifecycle.Idle, exception);
            }

            if (!preparedPlans.MarkUsed(tokenFingerprint, client.Owner, captureId))
            {
                return new(
                    BrokerOperationCode.PersistenceFailure,
                    captureId,
                    CaptureLifecycle.Starting,
                    null,
                    "The prepared plan changed after the durable start intent. No runtime start was attempted; recovery must resolve the pending intent.");
            }

            BrokerRuntimeStartOutcome runtimeOutcome;
            try
            {
                runtimeOutcome = await runtime
                    .StartAsync(ownership, resolution.Plan, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                runtimeOutcome = new(false, BoundReason($"Capture start failed: {exception.Message}"));
            }

            string? failure = BoundReason(runtimeOutcome.FailureReason);
            BrokerStartOutcome outcome = runtimeOutcome.Started
                ? new(
                    BrokerOperationCode.Started,
                    captureId,
                    CaptureLifecycle.Recording,
                    ownership.LeaseExpiresAtUtc,
                    null)
                : new(
                    BrokerOperationCode.StartFailed,
                    captureId,
                    CaptureLifecycle.Closed,
                    null,
                    failure ?? "The capture runtime refused to start.");
            BrokerCaptureOwnership completedOwnership = ownership with
            {
                State = outcome.State,
                UpdatedAtUtc = clock.GetUtcNow(),
                FailureReason = outcome.FailureReason,
            };
            BrokerStoredRequest completedRequest = intent with
            {
                Completed = true,
                StartOutcome = outcome,
            };

            try
            {
                await store
                    .SaveStartCompletionAsync(completedOwnership, completedRequest, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                if (runtimeOutcome.Started)
                {
                    await TryCompensatingStopAsync(ownership).ConfigureAwait(false);
                }

                return PersistenceFailure(captureId, CaptureLifecycle.Starting, exception);
            }

            return outcome;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<BrokerStopOutcome> StopAsync(
        CaptureId captureId,
        Guid requestId,
        BrokerClientIdentity client,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(client);
        string? identityProblem = client.Validate();
        if (captureId.Value == Guid.Empty || requestId == Guid.Empty || identityProblem is not null)
        {
            return new(
                BrokerOperationCode.InvalidRequest,
                captureId,
                CaptureLifecycle.Idle,
                BrokerStopMilestones.None,
                "A valid capture, request ID and authenticated client identity are required.");
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await StopOwnedAsync(
                    captureId,
                    requestId,
                    BrokerRequestKind.Stop,
                    client.Owner,
                    verifyOwner: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<BrokerLeaseOutcome> RenewOwnerLeaseAsync(
        CaptureId captureId,
        BrokerClientIdentity client,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(client);
        string? identityProblem = client.Validate();
        if (captureId.Value == Guid.Empty || identityProblem is not null)
        {
            return new(
                BrokerOperationCode.InvalidRequest,
                captureId,
                CaptureLifecycle.Idle,
                null,
                "A valid capture and authenticated client identity are required.");
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BrokerCaptureOwnership? ownership = await store
                .FindCaptureAsync(captureId, cancellationToken)
                .ConfigureAwait(false);
            if (ownership is null || !ownership.Owner.Matches(client.Owner))
            {
                return new(
                    BrokerOperationCode.CaptureUnavailable,
                    captureId,
                    ownership?.State ?? CaptureLifecycle.Idle,
                    null,
                    "The capture is unavailable to this authenticated owner.");
            }

            if (pendingStopCompletions.ContainsKey(captureId))
            {
                _ = await RetryPendingStopCompletionAsync(captureId).ConfigureAwait(false);
                ownership = await store.FindCaptureAsync(captureId, CancellationToken.None).ConfigureAwait(false)
                    ?? throw new InvalidDataException(
                        "The capture ownership record disappeared while retrying a stop completion.");
            }

            if (ownership.State is not (CaptureLifecycle.Starting or CaptureLifecycle.Recording))
            {
                return new(
                    BrokerOperationCode.CaptureUnavailable,
                    captureId,
                    ownership.State,
                    null,
                    ownership.FailureReason ?? "The capture is no longer recording; its owner lease cannot be renewed.");
            }

            ownership = await ReconcileIfCompletedAsync(ownership, cancellationToken).ConfigureAwait(false);
            if (ownership.State is not (CaptureLifecycle.Starting or CaptureLifecycle.Recording))
            {
                return new(
                    BrokerOperationCode.CaptureUnavailable,
                    captureId,
                    ownership.State,
                    null,
                    ownership.FailureReason ?? "The capture has already stopped; its owner lease cannot be renewed.");
            }

            DateTimeOffset now = clock.GetUtcNow();
            if (ownership.LeaseExpiresAtUtc <= now)
            {
                return new(
                    BrokerOperationCode.LeaseExpired,
                    captureId,
                    ownership.State,
                    ownership.LeaseExpiresAtUtc,
                    "The owner lease has expired; it cannot be revived. The broker will stop the orphaned capture.");
            }

            BrokerCaptureOwnership renewed = ownership with
            {
                LeaseExpiresAtUtc = now.Add(leaseDuration),
                UpdatedAtUtc = now,
            };
            try
            {
                await store.SaveLeaseAsync(renewed, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return new(
                    BrokerOperationCode.PersistenceFailure,
                    captureId,
                    ownership.State,
                    ownership.LeaseExpiresAtUtc,
                    BoundReason($"The renewed lease could not be persisted: {exception.Message}"));
            }

            return new(
                BrokerOperationCode.LeaseRenewed,
                captureId,
                renewed.State,
                renewed.LeaseExpiresAtUtc,
                null);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<BrokerCaptureOwnership?> GetStatusAsync(
        CaptureId captureId,
        BrokerClientIdentity client,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(client);
        if (captureId.Value == Guid.Empty || client.Validate() is not null)
        {
            return null;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BrokerCaptureOwnership? ownership = await store
                .FindCaptureAsync(captureId, cancellationToken)
                .ConfigureAwait(false);
            if (ownership is null || !ownership.Owner.Matches(client.Owner))
            {
                return null;
            }

            if (pendingStopCompletions.ContainsKey(captureId))
            {
                _ = await RetryPendingStopCompletionAsync(captureId).ConfigureAwait(false);
                ownership = await store.FindCaptureAsync(captureId, CancellationToken.None).ConfigureAwait(false)
                    ?? throw new InvalidDataException(
                        "The capture ownership record disappeared while retrying a stop completion.");
            }

            return await ReconcileIfCompletedAsync(ownership, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Persists completed duration/journal/disk stops even when no client asks for status. A host calls this
    /// periodically; status and lease renewal also reconcile their capture before answering.
    /// </summary>
    public async Task<IReadOnlyList<BrokerStopOutcome>> ReconcileCompletedCapturesAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var outcomes = new List<BrokerStopOutcome>();
            outcomes.AddRange(await RetryPendingStopCompletionsCoreAsync().ConfigureAwait(false));
            if (runtime is not IBrokerCaptureCompletionProbe probe)
            {
                return outcomes;
            }

            BrokerLifecycleSnapshot snapshot = await store.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            foreach (BrokerCaptureOwnership ownership in snapshot.Captures
                .Where(capture => capture.State == CaptureLifecycle.Recording))
            {
                if (await probe.HasCompletedAsync(ownership.CaptureId, cancellationToken).ConfigureAwait(false))
                {
                    outcomes.Add(await StopOwnedAsync(
                            ownership.CaptureId,
                            Guid.NewGuid(),
                            BrokerRequestKind.AutonomousStop,
                            ownership.Owner,
                            verifyOwner: false,
                            CancellationToken.None)
                        .ConfigureAwait(false));
                }
            }

            return outcomes;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Retries stop completions whose runtime result is already known but whose durable completion write failed.
    /// The runtime is not invoked again: doing so could discard already-proven finalization milestones.
    /// </summary>
    public async Task<IReadOnlyList<BrokerStopOutcome>> RetryPendingStopCompletionsAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RetryPendingStopCompletionsCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Reconciles a reopened durable store before accepting commands. Interrupted starts are never
    /// promoted to success; the runtime receives the persisted session ownership token and is asked to
    /// stop it. Pending/partial stops resume. A recording is stopped even while its owner lease is
    /// unexpired: the new broker process cannot assume it owns an ETW handle held by its predecessor.
    /// </summary>
    public async Task<BrokerRecoveryReport> RecoverAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BrokerLifecycleSnapshot snapshot = await store
                .ReadSnapshotAsync(cancellationToken)
                .ConfigureAwait(false);
            Dictionary<CaptureId, BrokerCaptureOwnership> captures = snapshot.Captures.ToDictionary(
                capture => capture.CaptureId);
            var handled = new HashSet<CaptureId>();
            var items = new List<BrokerRecoveryItem>();

            foreach (BrokerStoredRequest pending in snapshot.Requests
                .Where(request => !request.Completed)
                .OrderBy(request => request.RequestId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!captures.TryGetValue(pending.CaptureId, out BrokerCaptureOwnership? ownership))
                {
                    throw new InvalidDataException(
                        $"Pending request '{pending.RequestId}' has no capture ownership record.");
                }

                BrokerRuntimeStopOutcome runtimeStop = await StopForRecoveryAsync(ownership).ConfigureAwait(false);
                BrokerStopMilestones milestones = runtimeStop.Milestones with { Requested = true };
                CaptureLifecycle state = ResolveStopState(milestones, ownership.State, runtimeStop.Terminal);
                string? runtimeFailure = BoundReason(runtimeStop.FailureReason);
                BrokerCaptureOwnership updated = ownership with
                {
                    State = state,
                    UpdatedAtUtc = clock.GetUtcNow(),
                    StopMilestones = milestones,
                    FailureReason = runtimeFailure,
                };

                if (pending.Kind == BrokerRequestKind.Start)
                {
                    string reason = BoundReason(
                        "Recovered an interrupted start. No start success was durably exposed; the broker "
                        + "conservatively requested stop using the persisted session ownership token."
                        + (runtimeFailure is null ? string.Empty : $" {runtimeFailure}"))!;
                    BrokerStartOutcome startOutcome = new(
                        BrokerOperationCode.StartFailed,
                        ownership.CaptureId,
                        state,
                        null,
                        reason);
                    await store.SaveStartCompletionAsync(
                            updated with { FailureReason = reason },
                            pending with { Completed = true, StartOutcome = startOutcome },
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    items.Add(new(
                        BrokerRecoveryAction.InterruptedStartStopped,
                        ownership.CaptureId,
                        state,
                        milestones,
                        reason));
                }
                else
                {
                    BrokerOperationCode code = milestones.FullyFinalized
                        ? BrokerOperationCode.Stopped
                        : BrokerOperationCode.StopPartial;
                    BrokerStopOutcome stopOutcome = new(
                        code,
                        ownership.CaptureId,
                        state,
                        milestones,
                        runtimeFailure);
                    await store.SaveStopCompletionAsync(
                            updated,
                            pending with { Completed = true, StopOutcome = stopOutcome },
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    items.Add(new(
                        BrokerRecoveryAction.PendingStopResumed,
                        ownership.CaptureId,
                        state,
                        milestones,
                        runtimeFailure ?? "Recovered and completed a pending stop request."));
                }

                handled.Add(ownership.CaptureId);
            }

            snapshot = await store.ReadSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            DateTimeOffset now = clock.GetUtcNow();
            foreach (BrokerCaptureOwnership ownership in snapshot.Captures.OrderBy(item => item.CreatedAtUtc))
            {
                if (handled.Contains(ownership.CaptureId) || ownership.State == CaptureLifecycle.Closed)
                {
                    continue;
                }

                BrokerRecoveryAction action = ownership.State switch
                {
                    CaptureLifecycle.Stopping or CaptureLifecycle.Finalizing =>
                        BrokerRecoveryAction.PartialStopRetried,
                    CaptureLifecycle.Recording when ownership.LeaseExpiresAtUtc > now =>
                        BrokerRecoveryAction.RestartedActiveCaptureStopRequested,
                    CaptureLifecycle.Recording => BrokerRecoveryAction.ExpiredLeaseStopped,
                    _ => BrokerRecoveryAction.UnownedStateStopped,
                };
                BrokerStopOutcome outcome = await StopOwnedAsync(
                        ownership.CaptureId,
                        Guid.NewGuid(),
                        BrokerRequestKind.RecoveryStop,
                        ownership.Owner,
                        verifyOwner: false,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                items.Add(new(
                    action,
                    ownership.CaptureId,
                    outcome.State,
                    outcome.Milestones,
                    outcome.FailureReason ?? "Recovery requested stop using the persisted session ownership token."));
            }

            return new(items);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Whether any capture still holds, or may be acquiring, an ETW session. A partial stop does not count: it is
    /// retried by the next broker's recovery and must not keep an otherwise idle broker running forever.
    /// </summary>
    public async Task<bool> HasActiveCapturesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BrokerLifecycleSnapshot snapshot = await store.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return snapshot.Captures.Any(IsActive);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Stops every active capture because the host is exiting. The process cannot hand its ETW sessions to a successor,
    /// so a clean stop with finalized evidence is better than an orphan the next broker reclaims with partial evidence.
    /// Queued stop completions are retried first; a capture whose stop stays partial remains retryable at recovery.
    /// </summary>
    public async Task<IReadOnlyList<BrokerStopOutcome>> StopActiveCapturesForShutdownAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var outcomes = new List<BrokerStopOutcome>();
            outcomes.AddRange(await RetryPendingStopCompletionsCoreAsync().ConfigureAwait(false));
            BrokerLifecycleSnapshot snapshot = await store.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            foreach (BrokerCaptureOwnership ownership in snapshot.Captures
                .Where(IsActive)
                .OrderBy(capture => capture.CreatedAtUtc))
            {
                outcomes.Add(await StopOwnedAsync(
                        ownership.CaptureId,
                        Guid.NewGuid(),
                        BrokerRequestKind.HostShutdownStop,
                        ownership.Owner,
                        verifyOwner: false,
                        CancellationToken.None)
                    .ConfigureAwait(false));
            }

            return outcomes;
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool IsActive(BrokerCaptureOwnership capture) =>
        capture.State is CaptureLifecycle.Starting or CaptureLifecycle.Recording;

    /// <summary>Stops every expired interactive owner lease. A partial stop remains retryable.</summary>
    public async Task<IReadOnlyList<BrokerStopOutcome>> StopExpiredLeasesAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = clock.GetUtcNow();
            IReadOnlyList<BrokerCaptureOwnership> expired = await store
                .FindExpiredLeasesAsync(now, cancellationToken)
                .ConfigureAwait(false);
            var outcomes = new List<BrokerStopOutcome>(expired.Count);
            foreach (BrokerCaptureOwnership ownership in expired)
            {
                BrokerStopOutcome outcome = await StopOwnedAsync(
                        ownership.CaptureId,
                        Guid.NewGuid(),
                        BrokerRequestKind.ExpiredLeaseStop,
                        ownership.Owner,
                        verifyOwner: false,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                outcomes.Add(outcome);
            }

            return outcomes;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<BrokerStopOutcome> StopOwnedAsync(
        CaptureId captureId,
        Guid requestId,
        BrokerRequestKind kind,
        BrokerOwnerIdentity owner,
        bool verifyOwner,
        CancellationToken cancellationToken)
    {
        string targetKey = captureId.Value.ToString("N");
        BrokerStoredRequest? existing = await store
            .FindRequestAsync(owner, requestId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Kind != kind
                || !string.Equals(existing.TargetKey, targetKey, StringComparison.Ordinal))
            {
                return new(
                    BrokerOperationCode.RequestConflict,
                    existing.CaptureId,
                    CaptureLifecycle.Idle,
                    BrokerStopMilestones.None,
                    "This request ID is already bound to a different broker operation.");
            }

            return existing.Completed
                ? existing.StopOutcome!
                : new(
                    BrokerOperationCode.OperationPending,
                    existing.CaptureId,
                    CaptureLifecycle.Stopping,
                    BrokerStopMilestones.None with { Requested = true },
                    "The original stop request has a durable intent but no completed result yet.");
        }

        BrokerCaptureOwnership? ownership = await store
            .FindCaptureAsync(captureId, cancellationToken)
            .ConfigureAwait(false);
        if (ownership is null || (verifyOwner && !ownership.Owner.Matches(owner)))
        {
            return new(
                BrokerOperationCode.CaptureUnavailable,
                captureId,
                CaptureLifecycle.Idle,
                BrokerStopMilestones.None,
                "The capture is unavailable to this authenticated owner.");
        }

        // The runtime already returned for this capture; only its durable completion is outstanding. Stopping the
        // runtime again could replace proven milestones with the weaker evidence of a second stop, so a new stop
        // request first completes the queued result and otherwise reports the persistence failure again.
        if (pendingStopCompletions.ContainsKey(captureId))
        {
            BrokerStopOutcome retried = await RetryPendingStopCompletionAsync(captureId).ConfigureAwait(false);
            if (retried.Code == BrokerOperationCode.PersistenceFailure)
            {
                return retried;
            }

            ownership = await store.FindCaptureAsync(captureId, CancellationToken.None).ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    "The capture ownership record disappeared while retrying a stop completion.");
        }

        BrokerStopMilestones requested = ownership.StopMilestones with { Requested = true };
        var intent = new BrokerStoredRequest
        {
            Owner = owner,
            RequestId = requestId,
            Kind = kind,
            TargetKey = targetKey,
            CaptureId = captureId,
            Completed = false,
        };
        BrokerCaptureOwnership stopping = ownership with
        {
            State = ownership.State == CaptureLifecycle.Closed
                ? CaptureLifecycle.Closed
                : CaptureLifecycle.Stopping,
            UpdatedAtUtc = clock.GetUtcNow(),
            StopMilestones = requested,
        };

        try
        {
            await store.SaveStopIntentAsync(stopping, intent, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(
                BrokerOperationCode.PersistenceFailure,
                captureId,
                ownership.State,
                ownership.StopMilestones,
                BoundReason($"The stop intent could not be persisted: {exception.Message}"));
        }

        BrokerRuntimeStopOutcome runtimeOutcome;
        BrokerOperationCode code;
        if (ownership.State == CaptureLifecycle.Closed)
        {
            runtimeOutcome = new(requested);
            code = BrokerOperationCode.AlreadyStopped;
        }
        else
        {
            try
            {
                runtimeOutcome = await runtime
                    .StopAsync(ownership, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                runtimeOutcome = new(
                    requested,
                    BoundReason($"Capture stop failed: {exception.Message}"));
            }

            code = runtimeOutcome.Milestones.FullyFinalized
                ? BrokerOperationCode.Stopped
                : BrokerOperationCode.StopPartial;
        }

        CaptureLifecycle completedState = ResolveStopState(
            runtimeOutcome.Milestones,
            ownership.State,
            runtimeOutcome.Terminal);
        string? failure = BoundReason(runtimeOutcome.FailureReason);
        BrokerStopOutcome outcome = new(
            code,
            captureId,
            completedState,
            runtimeOutcome.Milestones,
            failure);
        BrokerCaptureOwnership completedOwnership = stopping with
        {
            State = completedState,
            UpdatedAtUtc = clock.GetUtcNow(),
            StopMilestones = runtimeOutcome.Milestones,
            FailureReason = failure,
        };
        BrokerStoredRequest completedRequest = intent with
        {
            Completed = true,
            StopOutcome = outcome,
        };

        try
        {
            await store
                .SaveStopCompletionAsync(completedOwnership, completedRequest, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            pendingStopCompletions[captureId] = new(completedOwnership, completedRequest);
            return new(
                BrokerOperationCode.PersistenceFailure,
                captureId,
                stopping.State,
                runtimeOutcome.Milestones,
                BoundReason($"The stop result could not be persisted and is queued for retry: {exception.Message}"));
        }

        pendingStopCompletions.Remove(captureId);
        return outcome;
    }

    private async Task<IReadOnlyList<BrokerStopOutcome>> RetryPendingStopCompletionsCoreAsync()
    {
        if (pendingStopCompletions.Count == 0)
        {
            return [];
        }

        var outcomes = new List<BrokerStopOutcome>(pendingStopCompletions.Count);
        foreach (CaptureId captureId in pendingStopCompletions.Keys.ToArray())
        {
            outcomes.Add(await RetryPendingStopCompletionAsync(captureId).ConfigureAwait(false));
        }

        return outcomes;
    }

    private async Task<BrokerStopOutcome> RetryPendingStopCompletionAsync(CaptureId captureId)
    {
        if (!pendingStopCompletions.TryGetValue(captureId, out PendingStopCompletion? pending))
        {
            BrokerCaptureOwnership? current = await store.FindCaptureAsync(
                    captureId, CancellationToken.None)
                .ConfigureAwait(false);
            return new(
                current?.State == CaptureLifecycle.Closed
                    ? BrokerOperationCode.AlreadyStopped
                    : BrokerOperationCode.CaptureUnavailable,
                captureId,
                current?.State ?? CaptureLifecycle.Idle,
                current?.StopMilestones ?? BrokerStopMilestones.None,
                null);
        }

        try
        {
            await store.SaveStopCompletionAsync(
                    pending.Ownership, pending.Request, CancellationToken.None)
                .ConfigureAwait(false);
            pendingStopCompletions.Remove(captureId);
            return pending.Request.StopOutcome!;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(
                BrokerOperationCode.PersistenceFailure,
                captureId,
                CaptureLifecycle.Stopping,
                pending.Ownership.StopMilestones,
                BoundReason($"The queued stop result still could not be persisted: {exception.Message}"));
        }
    }

    private async Task TryCompensatingStopAsync(BrokerCaptureOwnership ownership)
    {
        try
        {
            _ = await runtime.StopAsync(ownership, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The durable start intent remains for recovery. Never replace the persistence failure with
            // an exception from best-effort compensation.
        }
    }

    private async Task<BrokerCaptureOwnership> ReconcileIfCompletedAsync(
        BrokerCaptureOwnership ownership,
        CancellationToken cancellationToken)
    {
        if (ownership.State != CaptureLifecycle.Recording
            || runtime is not IBrokerCaptureCompletionProbe probe
            || !await probe.HasCompletedAsync(ownership.CaptureId, cancellationToken).ConfigureAwait(false))
        {
            return ownership;
        }

        BrokerStopOutcome outcome = await StopOwnedAsync(
                ownership.CaptureId,
                Guid.NewGuid(),
                BrokerRequestKind.AutonomousStop,
                ownership.Owner,
                verifyOwner: false,
                CancellationToken.None)
            .ConfigureAwait(false);
        BrokerCaptureOwnership? persisted = await store.FindCaptureAsync(
                ownership.CaptureId, CancellationToken.None)
            .ConfigureAwait(false);
        if (outcome.Code == BrokerOperationCode.PersistenceFailure)
        {
            return (persisted ?? ownership) with
            {
                State = CaptureLifecycle.Stopping,
                FailureReason = outcome.FailureReason,
            };
        }

        return persisted ?? throw new InvalidDataException(
            "The capture ownership record disappeared during autonomous stop reconciliation.");
    }

    private async Task<BrokerRuntimeStopOutcome> StopForRecoveryAsync(BrokerCaptureOwnership ownership)
    {
        try
        {
            return await runtime.StopAsync(ownership, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(
                ownership.StopMilestones with { Requested = true },
                BoundReason($"Recovery stop failed: {exception.Message}"));
        }
    }

    private static CaptureLifecycle ResolveStopState(
        BrokerStopMilestones milestones,
        CaptureLifecycle previousState,
        bool terminal)
    {
        // A terminal partial stop closes with its partial milestones: the milestones, not the state, say what is proven.
        if (previousState == CaptureLifecycle.Closed || milestones.FullyFinalized || terminal)
        {
            return CaptureLifecycle.Closed;
        }

        return milestones.ProvidersStopped && milestones.CallbacksDrained
            ? CaptureLifecycle.Finalizing
            : CaptureLifecycle.Stopping;
    }

    private static BrokerStartOutcome RejectedToken() => new(
        BrokerOperationCode.PreparedTokenRejected,
        null,
        CaptureLifecycle.Idle,
        null,
        "The prepared plan token is invalid, expired, or belongs to another authenticated logon session. Prepare the request again.");

    private static BrokerStartOutcome PersistenceFailure(
        CaptureId captureId,
        CaptureLifecycle state,
        Exception exception) => new(
            BrokerOperationCode.PersistenceFailure,
            captureId,
            state,
            null,
            BoundReason($"Broker ownership state could not be persisted: {exception.Message}"));

    private static string? BoundReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        string sanitized = new(reason
            .Where(character => !char.IsControl(character) || character is '\r' or '\n' or '\t')
            .ToArray());
        return sanitized.Length <= 512 ? sanitized : sanitized[..512];
    }

    private sealed record PendingStopCompletion(
        BrokerCaptureOwnership Ownership,
        BrokerStoredRequest Request);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        gate.Dispose();
    }
}
