using InterCat.Domain;
using System.ComponentModel;
using System.Runtime.Versioning;

namespace InterCat.CaptureBroker;

public enum BrokerHostEventKind
{
    Recovered = 1,
    Listening = 2,
    ClientConnected = 3,
    ClientRefused = 4,
    ClientEnded = 5,
    Maintenance = 6,
    ShutdownStop = 7,
    Exiting = 8,
}

/// <summary>One line of host diagnostics. Messages carry IDs, states and reasons, never event data.</summary>
public sealed record BrokerHostEvent(DateTimeOffset AtUtc, BrokerHostEventKind Kind, string Message);

public enum BrokerHostExitReason
{
    /// <summary>No client and no active capture for the configured idle period.</summary>
    Idle = 1,

    /// <summary>The host was asked to stop (console close, Ctrl+C, service stop).</summary>
    Cancelled = 2,
}

/// <param name="Recovery">What recovery did before the pipe accepted its first command.</param>
/// <param name="ShutdownStops">Captures the host stopped on its way out; empty after an idle exit.</param>
public sealed record BrokerHostResult(
    BrokerHostExitReason Reason,
    BrokerRecoveryReport Recovery,
    int ClientsServed,
    int ClientsRefused,
    IReadOnlyList<BrokerStopOutcome> ShutdownStops);

public sealed record BrokerHostSettings
{
    /// <summary>
    /// Ten seconds. A client cannot rediscover a broker it did not launch (each launch names a fresh pipe), so an idle
    /// broker serves nobody; lingering only makes the next launch wait for the ownership log it still holds.
    /// </summary>
    public static readonly TimeSpan DefaultIdleExit = TimeSpan.FromSeconds(10);

    /// <summary>The single user and logon session the pipe admits.</summary>
    public required BrokerOwnerIdentity Owner { get; init; }

    /// <summary>Names the pipe; chosen by the launching client so it knows where to connect.</summary>
    public required Guid ServerInstanceId { get; init; }

    public required string ServerVersion { get; init; }

    /// <summary>
    /// How long the broker stays up with no connected client and no active capture. An on-demand elevated broker must
    /// not linger as an idle privileged process, and must not exit while it still holds an ETW session. The period runs
    /// only while nobody is connected and nothing records.
    /// </summary>
    public TimeSpan IdleExitAfter { get; init; } = DefaultIdleExit;

    /// <summary>How often a waiting host re-checks the idle condition.</summary>
    public TimeSpan IdleCheckInterval { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaintenanceInterval { get; init; } = BrokerMaintenanceLoop.DefaultInterval;
}

/// <summary>
/// The broker process (plan §20.3): recover the durable store, then serve one authenticated client at a time on a
/// single first-instance pipe while the maintenance loop reconciles captures in the background. The order is the
/// contract: no command is accepted before recovery has stopped what a predecessor left recording, and the host never
/// exits while it still records.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BrokerHost
{
    private readonly BrokerPreparationCoordinator preparation;
    private readonly BrokerLifecycleCoordinator lifecycle;
    private readonly BrokerHostSettings settings;
    private readonly TimeProvider clock;
    private readonly Action<BrokerHostEvent>? log;
    private readonly Func<CaptureId, string?>? evidenceDirectory;
    private readonly Func<CaptureId, BrokerCaptureHealth?>? liveHealth;
    private readonly Func<CaptureId, BrokerCapturePreview?>? livePreview;

    public BrokerHost(
        BrokerPreparationCoordinator preparation,
        BrokerLifecycleCoordinator lifecycle,
        BrokerHostSettings settings,
        TimeProvider? clock = null,
        Action<BrokerHostEvent>? log = null,
        Func<CaptureId, string?>? evidenceDirectory = null,
        Func<CaptureId, BrokerCaptureHealth?>? liveHealth = null,
        Func<CaptureId, BrokerCapturePreview?>? livePreview = null)
    {
        this.evidenceDirectory = evidenceDirectory;
        this.liveHealth = liveHealth;
        this.livePreview = livePreview;
        this.preparation = preparation ?? throw new ArgumentNullException(nameof(preparation));
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        if (settings.IdleExitAfter <= TimeSpan.Zero || settings.IdleCheckInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Idle periods must be positive.");
        }

        this.clock = clock ?? TimeProvider.System;
        this.log = log;
    }

    /// <summary>
    /// Runs until idle or cancelled. Recovery and pipe-creation failures propagate before any command is served;
    /// <paramref name="onListening"/> receives the full pipe path once the pipe exists.
    /// </summary>
    public async Task<BrokerHostResult> RunAsync(
        Action<string>? onListening = null,
        CancellationToken cancellationToken = default)
    {
        var maintenance = new BrokerMaintenanceLoop(
            lifecycle,
            clock,
            settings.MaintenanceInterval,
            ReportMaintenance);
        BrokerRecoveryReport recovery = await maintenance.RecoverAsync(cancellationToken).ConfigureAwait(false);
        Log(BrokerHostEventKind.Recovered, recovery.Items.Count == 0
            ? "Recovery found nothing a previous broker left running."
            : $"Recovery stopped or retried {recovery.Items.Count} capture(s): "
                + string.Join("; ", recovery.Items.Select(item =>
                    $"{item.CaptureId.Value:N} {item.Action} -> {item.State}")));

        await using WindowsBrokerPipeServerInstance pipe = WindowsBrokerPipeServerInstance.Create(
            settings.ServerInstanceId,
            settings.Owner);
        string pipePath = $@"\\.\pipe\{pipe.PipeName}";
        Log(BrokerHostEventKind.Listening, $"Listening on {pipePath}.");
        onListening?.Invoke(pipePath);

        using var stopMaintenance = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task maintenanceRun = maintenance.RunAsync(stopMaintenance.Token);
        int served = 0;
        int refused = 0;
        BrokerHostExitReason reason;
        try
        {
            reason = await ServeAsync(pipe, () => served++, () => refused++, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await stopMaintenance.CancelAsync().ConfigureAwait(false);
            await maintenanceRun.ConfigureAwait(false);
        }

        IReadOnlyList<BrokerStopOutcome> shutdownStops = await StopForShutdownAsync().ConfigureAwait(false);
        Log(BrokerHostEventKind.Exiting, reason == BrokerHostExitReason.Idle
            ? $"No client or active capture for {settings.IdleExitAfter.TotalSeconds:0} s; exiting."
            : "Shutdown requested; exiting.");
        return new(reason, recovery, served, refused, shutdownStops);
    }

    private async Task<BrokerHostExitReason> ServeAsync(
        WindowsBrokerPipeServerInstance pipe,
        Action countServed,
        Action countRefused,
        CancellationToken cancellationToken)
    {
        DateTimeOffset lastActivity = clock.GetUtcNow();
        while (true)
        {
            bool connected;
            try
            {
                (connected, lastActivity) = await WaitForClientOrIdleAsync(pipe, lastActivity, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return BrokerHostExitReason.Cancelled;
            }

            if (!connected)
            {
                return BrokerHostExitReason.Idle;
            }

            try
            {
                if (TryAuthenticate(pipe) is { } client)
                {
                    countServed();
                    await ServeClientAsync(pipe, client, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    countRefused();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return BrokerHostExitReason.Cancelled;
            }
            finally
            {
                pipe.DisconnectClient();
                lastActivity = clock.GetUtcNow();
            }
        }
    }

    private async Task<(bool Connected, DateTimeOffset LastActivity)> WaitForClientOrIdleAsync(
        WindowsBrokerPipeServerInstance pipe,
        DateTimeOffset lastActivity,
        CancellationToken cancellationToken)
    {
        using var abandonWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task wait = pipe.WaitForConnectionAsync(abandonWait.Token).AsTask();
        while (true)
        {
            Task tick = Task.Delay(settings.IdleCheckInterval, clock, cancellationToken);
            if (await Task.WhenAny(wait, tick).ConfigureAwait(false) == wait)
            {
                await wait.ConfigureAwait(false);
                return (true, lastActivity);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                await AbandonAsync(abandonWait, wait).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            DateTimeOffset now = clock.GetUtcNow();
            if (await HasActiveCapturesAsync(cancellationToken).ConfigureAwait(false))
            {
                lastActivity = now;
            }
            else if (now - lastActivity >= settings.IdleExitAfter)
            {
                await AbandonAsync(abandonWait, wait).ConfigureAwait(false);

                // A client may have connected in the instant before the wait was abandoned; serve it rather than
                // leaving it connected to a broker that is about to exit.
                return (pipe.IsConnected, lastActivity);
            }
        }
    }

    private static async Task AbandonAsync(CancellationTokenSource abandonWait, Task wait)
    {
        await abandonWait.CancelAsync().ConfigureAwait(false);
        try
        {
            await wait.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<bool> HasActiveCapturesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await lifecycle.HasActiveCapturesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
        {
            // Unknown is not idle: exiting could abandon a capture the store could not describe.
            Log(BrokerHostEventKind.Maintenance, $"Active captures could not be read, so the broker stays up: {exception.Message}");
            return true;
        }
    }

    private BrokerAuthenticatedPipeClient? TryAuthenticate(WindowsBrokerPipeServerInstance pipe)
    {
        try
        {
            BrokerAuthenticatedPipeClient client = pipe.AuthenticateConnectedClient();
            Log(BrokerHostEventKind.ClientConnected,
                $"Client connected: integrity 0x{client.Identity.IntegrityLevel:X}, "
                + $"elevated {client.Identity.IsElevated}, diagnostic PID {client.DiagnosticProcessId}.");
            return client;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or Win32Exception or IOException
            or InvalidOperationException)
        {
            Log(BrokerHostEventKind.ClientRefused, $"Client refused: {exception.Message}");
            return null;
        }
    }

    private async Task ServeClientAsync(
        WindowsBrokerPipeServerInstance pipe,
        BrokerAuthenticatedPipeClient client,
        CancellationToken cancellationToken)
    {
        var dispatcher = new BrokerConnectionDispatcher(
            client.Identity,
            preparation,
            lifecycle,
            settings.ServerInstanceId,
            settings.ServerVersion,
            evidenceDirectory,
            liveHealth,
            livePreview);
        try
        {
            await BrokerPipeConnectionProcessor.ProcessAsync(pipe.Stream, dispatcher, cancellationToken)
                .ConfigureAwait(false);
            Log(BrokerHostEventKind.ClientEnded, "Client disconnected.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException)
        {
            // A client that breaks the pipe or the framing loses its connection, not the broker.
            Log(BrokerHostEventKind.ClientEnded, $"Client connection closed: {exception.Message}");
        }
    }

    private async Task<IReadOnlyList<BrokerStopOutcome>> StopForShutdownAsync()
    {
        IReadOnlyList<BrokerStopOutcome> stops;
        try
        {
            stops = await lifecycle.StopActiveCapturesForShutdownAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log(BrokerHostEventKind.ShutdownStop,
                $"Active captures could not be stopped on exit; the next broker's recovery stops them: {exception.Message}");
            return [];
        }

        foreach (BrokerStopOutcome stop in stops)
        {
            Log(BrokerHostEventKind.ShutdownStop,
                $"Capture {stop.CaptureId?.Value:N} stopped on exit: {stop.Code} -> {stop.State}"
                + (stop.FailureReason is null ? "." : $" ({stop.FailureReason})."));
        }

        return stops;
    }

    private void ReportMaintenance(BrokerMaintenanceReport report)
    {
        string summary = $"{report.Reconciled.Count} reconciled, {report.ExpiredLeaseStops.Count} expired lease(s) stopped";
        Log(BrokerHostEventKind.Maintenance, report.Failure is null
            ? summary + "."
            : $"{summary}; failure #{report.ConsecutiveFailures}: {report.Failure}");
    }

    private void Log(BrokerHostEventKind kind, string message)
    {
        try
        {
            log?.Invoke(new(clock.GetUtcNow(), kind, message));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Diagnostics must never stop the broker from serving or cleaning up.
        }
    }
}
