namespace InterCat.CaptureBroker;

/// <summary>What one maintenance pass did, or why it could not finish.</summary>
/// <param name="Reconciled">
/// Autonomous stops made durable (duration, journal-limit or disk-floor ends) and queued stop completions retried.
/// </param>
/// <param name="ExpiredLeaseStops">Captures stopped because their interactive owner lease expired.</param>
/// <param name="Failure">Why part of the pass failed; the other part still ran. Null when both succeeded.</param>
/// <param name="ConsecutiveFailures">Failed passes in a row, including this one; zero after a clean pass.</param>
public sealed record BrokerMaintenanceReport(
    DateTimeOffset AtUtc,
    IReadOnlyList<BrokerStopOutcome> Reconciled,
    IReadOnlyList<BrokerStopOutcome> ExpiredLeaseStops,
    string? Failure,
    int ConsecutiveFailures)
{
    /// <summary>Whether a host should log this pass: it changed a capture or something failed.</summary>
    public bool IsNotable => Failure is not null || Reconciled.Count > 0 || ExpiredLeaseStops.Count > 0;
}

/// <summary>
/// The broker host's no-client maintenance (plan §20.3). Recovery runs once, before the pipe accepts commands; then every
/// pass makes autonomous stops durable, retries stop completions whose durable write failed, and stops captures whose
/// owner lease expired. None of that waits for a client to ask for status. A failing pass is reported and the loop
/// keeps running, because a transient store error must not leave later captures unreconciled.
/// </summary>
public sealed class BrokerMaintenanceLoop
{
    /// <summary>
    /// One second: well inside the shortest owner lease, and the same resolution as the runtime's duration and idle
    /// free-space checks, so a finished capture becomes durable status within about a second.
    /// </summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    private readonly BrokerLifecycleCoordinator coordinator;
    private readonly TimeProvider clock;
    private readonly Action<BrokerMaintenanceReport>? onReport;
    private int consecutiveFailures;

    public BrokerMaintenanceLoop(
        BrokerLifecycleCoordinator coordinator,
        TimeProvider? clock = null,
        TimeSpan? interval = null,
        Action<BrokerMaintenanceReport>? onReport = null)
    {
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.clock = clock ?? TimeProvider.System;
        Interval = interval ?? DefaultInterval;
        if (Interval <= TimeSpan.Zero || Interval > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(interval), Interval, "A maintenance interval is positive and at most one minute.");
        }

        this.onReport = onReport;
    }

    public TimeSpan Interval { get; }

    /// <summary>
    /// Reconciles the reopened durable store. The host must finish this before accepting pipe commands, so no client
    /// observes a capture its predecessor process left recording.
    /// </summary>
    public Task<BrokerRecoveryReport> RecoverAsync(CancellationToken cancellationToken = default) =>
        coordinator.RecoverAsync(cancellationToken);

    /// <summary>
    /// One pass. Each half runs even when the other failed. Cancellation is the only exception it lets through.
    /// </summary>
    public async Task<BrokerMaintenanceReport> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var failures = new List<string>(2);
        IReadOnlyList<BrokerStopOutcome> reconciled = [];
        IReadOnlyList<BrokerStopOutcome> expired = [];
        try
        {
            reconciled = await coordinator.ReconcileCompletedCapturesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsPassFailure(exception, cancellationToken))
        {
            failures.Add($"Completed captures could not be reconciled: {exception.Message}");
        }

        try
        {
            expired = await coordinator.StopExpiredLeasesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsPassFailure(exception, cancellationToken))
        {
            failures.Add($"Expired owner leases could not be swept: {exception.Message}");
        }

        // A persistence failure inside a stop is an outcome, not an exception; it still means the pass is not clean.
        IEnumerable<BrokerStopOutcome> persistenceFailures = reconciled.Concat(expired)
            .Where(outcome => outcome.Code == BrokerOperationCode.PersistenceFailure);
        foreach (BrokerStopOutcome outcome in persistenceFailures)
        {
            failures.Add($"Capture {outcome.CaptureId?.Value:N}: {outcome.FailureReason ?? "stop result not persisted"}");
        }

        consecutiveFailures = failures.Count == 0 ? 0 : consecutiveFailures + 1;
        return new(
            clock.GetUtcNow(),
            reconciled,
            expired,
            failures.Count == 0 ? null : string.Join(" ", failures),
            consecutiveFailures);
    }

    /// <summary>
    /// Runs passes every <see cref="Interval"/> until cancelled or the coordinator is disposed, reporting each notable
    /// pass. Returns normally on either end; it never throws cancellation to the host.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, clock, cancellationToken).ConfigureAwait(false);
                BrokerMaintenanceReport report = await RunOnceAsync(cancellationToken).ConfigureAwait(false);
                if (report.IsNotable)
                {
                    Report(report);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                // The coordinator is gone; the host is shutting down.
                return;
            }
        }
    }

    private void Report(BrokerMaintenanceReport report)
    {
        try
        {
            onReport?.Invoke(report);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Reporting is diagnostics. A failing log sink must not stop captures from being reconciled.
        }
    }

    private static bool IsPassFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is not (OutOfMemoryException or ObjectDisposedException)
        && !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested);
}
