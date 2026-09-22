using InterCat.Domain;

namespace InterCat.CaptureBroker;

/// <summary>
/// Transport-independent ownership coordinator. It records an operation intent before touching the
/// capture runtime and records the completion before returning success. The future pipe host supplies
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
                    .StartAsync(captureId, resolution.Plan, CancellationToken.None)
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
                    await TryCompensatingStopAsync(captureId).ConfigureAwait(false);
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
            if (ownership is null
                || !ownership.Owner.Matches(client.Owner)
                || ownership.State is not (CaptureLifecycle.Starting or CaptureLifecycle.Recording))
            {
                return new(
                    BrokerOperationCode.CaptureUnavailable,
                    captureId,
                    ownership?.State ?? CaptureLifecycle.Idle,
                    null,
                    "The capture is unavailable to this authenticated owner.");
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
            return ownership is not null && ownership.Owner.Matches(client.Owner) ? ownership : null;
        }
        finally
        {
            gate.Release();
        }
    }

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
                    .StopAsync(captureId, CancellationToken.None)
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

        CaptureLifecycle completedState = ResolveStopState(runtimeOutcome.Milestones, ownership.State);
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
            return new(
                BrokerOperationCode.PersistenceFailure,
                captureId,
                stopping.State,
                stopping.StopMilestones,
                BoundReason($"The stop result could not be persisted: {exception.Message}"));
        }

        return outcome;
    }

    private async Task TryCompensatingStopAsync(CaptureId captureId)
    {
        try
        {
            _ = await runtime.StopAsync(captureId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The durable start intent remains for recovery. Never replace the persistence failure with
            // an exception from best-effort compensation.
        }
    }

    private static CaptureLifecycle ResolveStopState(
        BrokerStopMilestones milestones,
        CaptureLifecycle previousState)
    {
        if (previousState == CaptureLifecycle.Closed || milestones.FullyFinalized)
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
