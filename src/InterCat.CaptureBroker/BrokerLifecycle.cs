using InterCat.Domain;

namespace InterCat.CaptureBroker;

public enum BrokerOperationCode
{
    Started = 1,
    StartFailed = 2,
    PreparedTokenRejected = 3,
    PreparedPlanAlreadyUsed = 4,
    RequestConflict = 5,
    OperationPending = 6,
    CaptureUnavailable = 7,
    LeaseRenewed = 8,
    LeaseExpired = 9,
    Stopped = 10,
    AlreadyStopped = 11,
    StopPartial = 12,
    PersistenceFailure = 13,
    InvalidRequest = 14,
}

public enum BrokerRequestKind
{
    Start = 1,
    Stop = 2,
    ExpiredLeaseStop = 3,
    RecoveryStop = 4,
}

/// <summary>Stop progress is never collapsed into one optimistic success flag.</summary>
public sealed record BrokerStopMilestones(
    bool Requested,
    bool ProvidersStopped,
    bool CallbacksDrained,
    bool JournalFinalized,
    bool AnalysisFinalized)
{
    public static BrokerStopMilestones None { get; } = new(false, false, false, false, false);

    public bool FullyFinalized =>
        Requested && ProvidersStopped && CallbacksDrained && JournalFinalized && AnalysisFinalized;
}

public sealed record BrokerStartOutcome(
    BrokerOperationCode Code,
    CaptureId? CaptureId,
    CaptureLifecycle State,
    DateTimeOffset? LeaseExpiresAtUtc,
    string? FailureReason);

public sealed record BrokerStopOutcome(
    BrokerOperationCode Code,
    CaptureId? CaptureId,
    CaptureLifecycle State,
    BrokerStopMilestones Milestones,
    string? FailureReason);

public sealed record BrokerLeaseOutcome(
    BrokerOperationCode Code,
    CaptureId CaptureId,
    CaptureLifecycle State,
    DateTimeOffset? LeaseExpiresAtUtc,
    string? FailureReason);

public enum BrokerRecoveryAction
{
    InterruptedStartStopped = 1,
    PendingStopResumed = 2,
    PartialStopRetried = 3,
    UnownedStateStopped = 4,
    ExpiredLeaseStopped = 5,
    RestartedActiveCaptureStopRequested = 6,
}

public sealed record BrokerRecoveryItem(
    BrokerRecoveryAction Action,
    CaptureId CaptureId,
    CaptureLifecycle State,
    BrokerStopMilestones Milestones,
    string Summary);

public sealed record BrokerRecoveryReport(IReadOnlyList<BrokerRecoveryItem> Items)
{
    public int StoppedOrRetriedCount => Items.Count;
}

public sealed record BrokerRuntimeStartOutcome(bool Started, string? FailureReason = null);

public sealed record BrokerRuntimeStopOutcome(BrokerStopMilestones Milestones, string? FailureReason = null);

/// <summary>
/// Name plus unguessable proof for exactly one broker-created session. A matching name without the
/// ownership token never authorizes recovery cleanup.
/// </summary>
public sealed record BrokerSessionOwnership(string SessionName, Guid OwnershipToken)
{
    public static BrokerSessionOwnership Create(CaptureId captureId)
    {
        if (captureId.Value == Guid.Empty)
        {
            throw new ArgumentException("A session requires a non-empty capture ID.", nameof(captureId));
        }

        Guid token = Guid.NewGuid();
        return new(
            $"InterCat-broker-{captureId.Value:N}-{token:N}"[..64],
            token);
    }

    public bool IsValid =>
        OwnershipToken != Guid.Empty
        && SessionName.Length is > 0 and <= 64
        && SessionName.StartsWith("InterCat-broker-", StringComparison.Ordinal)
        && SessionName.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
}

/// <summary>Future ETW/journal integration plugs in here; tests use a deterministic fake.</summary>
public interface IBrokerCaptureRuntime
{
    Task<BrokerRuntimeStartOutcome> StartAsync(
        BrokerCaptureOwnership ownership,
        PreparedCapturePlan plan,
        CancellationToken cancellationToken);

    Task<BrokerRuntimeStopOutcome> StopAsync(
        BrokerCaptureOwnership ownership,
        CancellationToken cancellationToken);
}

/// <summary>Durable ownership state. Degradation stays orthogonal to lifecycle.</summary>
public sealed record BrokerCaptureOwnership
{
    public required CaptureId CaptureId { get; init; }
    public required BrokerOwnerIdentity Owner { get; init; }
    public required BrokerSessionOwnership Session { get; init; }
    public required string PlanDigest { get; init; }
    public required CaptureLifecycle State { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public required DateTimeOffset LeaseExpiresAtUtc { get; init; }
    public required BrokerStopMilestones StopMilestones { get; init; }
    public string? FailureReason { get; init; }
}

public sealed record BrokerStoredRequest
{
    public required BrokerOwnerIdentity Owner { get; init; }
    public required Guid RequestId { get; init; }
    public required BrokerRequestKind Kind { get; init; }
    public required string TargetKey { get; init; }
    public required CaptureId CaptureId { get; init; }
    public required bool Completed { get; init; }
    public BrokerStartOutcome? StartOutcome { get; init; }
    public BrokerStopOutcome? StopOutcome { get; init; }
}

public sealed record BrokerLifecycleSnapshot(
    IReadOnlyList<BrokerCaptureOwnership> Captures,
    IReadOnlyList<BrokerStoredRequest> Requests);

/// <summary>
/// Atomic persistence boundary. A production implementation must durably commit intent before a runtime
/// operation starts and durably commit its result before the result is exposed to a client.
/// </summary>
public interface IBrokerLifecycleStore
{
    ValueTask<BrokerStoredRequest?> FindRequestAsync(
        BrokerOwnerIdentity owner,
        Guid requestId,
        CancellationToken cancellationToken);

    ValueTask<BrokerCaptureOwnership?> FindCaptureAsync(
        CaptureId captureId,
        CancellationToken cancellationToken);

    ValueTask SaveStartIntentAsync(
        BrokerCaptureOwnership ownership,
        BrokerStoredRequest request,
        CancellationToken cancellationToken);

    ValueTask SaveStartCompletionAsync(
        BrokerCaptureOwnership ownership,
        BrokerStoredRequest request,
        CancellationToken cancellationToken);

    ValueTask SaveStopIntentAsync(
        BrokerCaptureOwnership ownership,
        BrokerStoredRequest request,
        CancellationToken cancellationToken);

    ValueTask SaveStopCompletionAsync(
        BrokerCaptureOwnership ownership,
        BrokerStoredRequest request,
        CancellationToken cancellationToken);

    ValueTask SaveLeaseAsync(
        BrokerCaptureOwnership ownership,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<BrokerCaptureOwnership>> FindExpiredLeasesAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);

    ValueTask<BrokerLifecycleSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Deterministic development/test store. It preserves the same atomic method boundaries but is not
/// crash-durable; the authenticated broker host must replace it before live capture is enabled.
/// </summary>
public class InMemoryBrokerLifecycleStore : IBrokerLifecycleStore
{
    private readonly Dictionary<CaptureId, BrokerCaptureOwnership> captures = [];
    private readonly Dictionary<RequestKey, BrokerStoredRequest> requests = [];
    private readonly Lock gate = new();

    public virtual ValueTask<BrokerStoredRequest?> FindRequestAsync(
        BrokerOwnerIdentity owner,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            requests.TryGetValue(new(owner, requestId), out BrokerStoredRequest? request);
            return ValueTask.FromResult(request);
        }
    }

    public virtual ValueTask<BrokerCaptureOwnership?> FindCaptureAsync(
        CaptureId captureId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            captures.TryGetValue(captureId, out BrokerCaptureOwnership? ownership);
            return ValueTask.FromResult(ownership);
        }
    }

    public virtual ValueTask SaveStartIntentAsync(
        BrokerCaptureOwnership ownership,
        BrokerStoredRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var key = new RequestKey(request.Owner, request.RequestId);
            if (captures.ContainsKey(ownership.CaptureId) || requests.ContainsKey(key))
            {
                throw new InvalidOperationException("The start intent already exists.");
            }

            captures.Add(ownership.CaptureId, ownership);
            requests.Add(key, request);
        }

        return ValueTask.CompletedTask;
    }

    public virtual ValueTask SaveStartCompletionAsync(
        BrokerCaptureOwnership ownership,
        BrokerStoredRequest request,
        CancellationToken cancellationToken) =>
        SaveCompletionAsync(ownership, request, BrokerRequestKind.Start, cancellationToken);

    public virtual ValueTask SaveStopIntentAsync(
        BrokerCaptureOwnership ownership,
        BrokerStoredRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var key = new RequestKey(request.Owner, request.RequestId);
            if (!captures.ContainsKey(ownership.CaptureId) || requests.ContainsKey(key))
            {
                throw new InvalidOperationException("The stop intent conflicts with stored state.");
            }

            captures[ownership.CaptureId] = ownership;
            requests.Add(key, request);
        }

        return ValueTask.CompletedTask;
    }

    public virtual ValueTask SaveStopCompletionAsync(
        BrokerCaptureOwnership ownership,
        BrokerStoredRequest request,
        CancellationToken cancellationToken) =>
        SaveCompletionAsync(ownership, request, request.Kind, cancellationToken);

    public virtual ValueTask SaveLeaseAsync(
        BrokerCaptureOwnership ownership,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!captures.ContainsKey(ownership.CaptureId))
            {
                throw new InvalidOperationException("The capture ownership record does not exist.");
            }

            captures[ownership.CaptureId] = ownership;
        }

        return ValueTask.CompletedTask;
    }

    public virtual ValueTask<IReadOnlyList<BrokerCaptureOwnership>> FindExpiredLeasesAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            IReadOnlyList<BrokerCaptureOwnership> expired =
            [
                .. captures.Values
                    .Where(capture =>
                        capture.LeaseExpiresAtUtc <= nowUtc
                        && capture.State is CaptureLifecycle.Starting
                            or CaptureLifecycle.Recording
                            or CaptureLifecycle.Stopping
                            or CaptureLifecycle.Finalizing)
                    .OrderBy(capture => capture.LeaseExpiresAtUtc),
            ];
            return ValueTask.FromResult(expired);
        }
    }

    public virtual ValueTask<BrokerLifecycleSnapshot> ReadSnapshotAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return ValueTask.FromResult(new BrokerLifecycleSnapshot(
                [.. captures.Values.OrderBy(capture => capture.CreatedAtUtc)],
                [.. requests.Values.OrderBy(request => request.RequestId)]));
        }
    }

    private ValueTask SaveCompletionAsync(
        BrokerCaptureOwnership ownership,
        BrokerStoredRequest request,
        BrokerRequestKind expectedKind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var key = new RequestKey(request.Owner, request.RequestId);
            if (!captures.ContainsKey(ownership.CaptureId)
                || !requests.TryGetValue(key, out BrokerStoredRequest? existing)
                || existing.Kind != expectedKind
                || existing.Completed)
            {
                throw new InvalidOperationException("The operation completion has no matching pending intent.");
            }

            captures[ownership.CaptureId] = ownership;
            requests[key] = request;
        }

        return ValueTask.CompletedTask;
    }

    private readonly record struct RequestKey(BrokerOwnerIdentity Owner, Guid RequestId);
}
