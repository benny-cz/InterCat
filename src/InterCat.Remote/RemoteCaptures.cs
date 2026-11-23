namespace InterCat.Remote;

/// <summary>What a driver prepared on the agent's host: the agent's handle for it, and what the host will record.</summary>
public sealed record DriverPreparation(Guid Prepared, string Profile, uint Seconds, uint JournalMebibytes, string Collects);

/// <summary>Where the host's capture stands, as its driver reads it.</summary>
public sealed record DriverCaptureState(RemoteCaptureLifecycle State, long Records, long Lost, bool Finalized, string? Reason);

/// <summary>
/// The agent's host's capture, as the agent drives it: through the host's capture broker in InterCat, a stand-in in tests.
/// A driver is asked for one capture at a time.
/// </summary>
public interface IRemoteCaptureDriver : IAsyncDisposable
{
    /// <summary>Prepares <paramref name="request"/>; a host that cannot record it refuses with a <see cref="RemoteCaptureRefusedException"/>.</summary>
    Task<DriverPreparation> PrepareAsync(RemoteCaptureRequest request, CancellationToken cancellationToken);

    /// <summary>Starts what it prepared, returning the capture's identity.</summary>
    Task<Guid> StartAsync(Guid prepared, CancellationToken cancellationToken);

    /// <summary>Lets go a preparation never started.</summary>
    Task ReleaseAsync(Guid prepared, CancellationToken cancellationToken);

    Task<DriverCaptureState> StatusAsync(Guid capture, CancellationToken cancellationToken);

    /// <summary>Keeps the host's own hold on the capture - its broker's owner lease - which lapses when the agent does.</summary>
    Task RenewAsync(Guid capture, CancellationToken cancellationToken);

    Task<DriverCaptureState> StopAsync(Guid capture, CancellationToken cancellationToken);
}

/// <summary>The agent's host refused a capture, in its own words.</summary>
public sealed class RemoteCaptureRefusedException : Exception
{
    public RemoteCaptureRefusedException()
    {
    }

    public RemoteCaptureRefusedException(string message)
        : base(message)
    {
    }

    public RemoteCaptureRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A request the agent refuses, with the code it answers.</summary>
internal sealed class RemoteRequestRefusedException(RemoteErrorCode code, string message) : Exception(message)
{
    public RemoteErrorCode Code { get; } = code;
}

/// <summary>
/// The agent's own bounds on a remote capture (`remote-agent-v1` §5): its longest duration and largest journal, the shortest
/// and longest control lease a controller may ask, how long a preparation waits to be started, and how often the agent
/// renews the host's own hold while the controller's lease holds.
/// </summary>
public sealed record RemoteCapturePolicy(
    uint MaximumSeconds,
    uint MaximumJournalMebibytes,
    TimeSpan MinimumLease,
    TimeSpan MaximumLease,
    TimeSpan PreparedFor,
    TimeSpan HostRenewal,
    TimeSpan Tick)
{
    /// <summary>An hour, 4 GiB, a lease of 10 seconds to 5 minutes, 30 seconds to start, the host renewed every 10.</summary>
    public static RemoteCapturePolicy Default { get; } = new(3_600, 4_096, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5),
        TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1));
}

/// <summary>
/// The agent's remote capture (`remote-agent-v1` §4.4): one at a time, belonging to the controller that started it, within
/// the agent's bounds. The controller keeps a control lease alive; while it holds, the agent keeps the host's own hold on
/// the capture, and when it lapses - the controller gone, or its network - the agent stops the capture. Should the agent
/// itself go, the host's hold lapses and its broker stops the capture on its own. An administrator stopping the agent
/// stops its capture.
/// </summary>
public sealed class RemoteCaptures : IAsyncDisposable
{
    private readonly IRemoteCaptureDriver driver;
    private readonly RemoteCapturePolicy policy;
    private readonly TimeProvider time;
    private readonly IRemoteAudit? audit;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource stopping = new();
    private readonly Task loop;
    private Slot? slot;
    private bool disposed;

    public RemoteCaptures(IRemoteCaptureDriver driver, RemoteCapturePolicy? policy = null, TimeProvider? time = null, IRemoteAudit? audit = null)
    {
        ArgumentNullException.ThrowIfNull(driver);
        this.driver = driver;
        this.policy = policy ?? RemoteCapturePolicy.Default;
        this.time = time ?? TimeProvider.System;
        this.audit = audit;
        loop = Task.Run(() => KeepAsync(stopping.Token), CancellationToken.None);
    }

    /// <summary>The agent's bounds.</summary>
    public RemoteCapturePolicy Policy => policy;

    internal async Task<RemotePreparedCapture> PrepareAsync(RemotePin owner, RemoteCaptureRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Profile))
        {
            throw new RemoteRequestRefusedException(RemoteErrorCode.Malformed, "A capture names its profile.");
        }

        if (request.Seconds < 1 || request.Seconds > policy.MaximumSeconds)
        {
            throw new RemoteRequestRefusedException(RemoteErrorCode.NotPermitted,
                $"This agent records for 1 to {policy.MaximumSeconds:N0} seconds, not {request.Seconds:N0}.");
        }

        if (request.JournalMebibytes < 1 || request.JournalMebibytes > policy.MaximumJournalMebibytes)
        {
            throw new RemoteRequestRefusedException(RemoteErrorCode.NotPermitted,
                $"This agent keeps a journal of 1 to {policy.MaximumJournalMebibytes:N0} MiB, not {request.JournalMebibytes:N0}.");
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LetGoLapsedPreparationAsync().ConfigureAwait(false);
            if (slot is { Closed: false })
            {
                throw new RemoteRequestRefusedException(RemoteErrorCode.Busy,
                    "This agent holds a capture already; it records one at a time.");
            }

            DriverPreparation prepared;
            try
            {
                prepared = await driver.PrepareAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (RemoteCaptureRefusedException refused)
            {
                throw new RemoteRequestRefusedException(RemoteErrorCode.Failed, refused.Message);
            }

            slot = new Slot(owner, prepared.Prepared, time.GetUtcNow() + policy.PreparedFor);
            return new RemotePreparedCapture(prepared.Prepared, prepared.Profile, prepared.Seconds, prepared.JournalMebibytes, prepared.Collects);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<RemoteCaptureStarted> StartAsync(RemotePin owner, RemoteStartRequest start, CancellationToken cancellationToken)
    {
        TimeSpan lease = TimeSpan.FromSeconds(start.LeaseSeconds);
        if (lease < policy.MinimumLease || lease > policy.MaximumLease)
        {
            throw new RemoteRequestRefusedException(RemoteErrorCode.NotPermitted,
                $"A control lease is {policy.MinimumLease.TotalSeconds:N0} to {policy.MaximumLease.TotalSeconds:N0} seconds here, not {start.LeaseSeconds:N0}.");
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LetGoLapsedPreparationAsync().ConfigureAwait(false);
            if (slot is not { CaptureId: null, Closed: false } prepared || prepared.Owner != owner || prepared.Prepared != start.Prepared)
            {
                throw new RemoteRequestRefusedException(RemoteErrorCode.NotPermitted,
                    "This controller holds no preparation of that identity here, or it lapsed unstarted.");
            }

            Guid captureId;
            try
            {
                captureId = await driver.StartAsync(prepared.Prepared, cancellationToken).ConfigureAwait(false);
            }
            catch (RemoteCaptureRefusedException refused)
            {
                slot = null;
                throw new RemoteRequestRefusedException(RemoteErrorCode.Failed, refused.Message);
            }

            DateTimeOffset now = time.GetUtcNow();
            prepared.CaptureId = captureId;
            prepared.Lease = lease;
            prepared.LeaseExpires = now + lease;
            prepared.HostRenewal = now + policy.HostRenewal;
            return new RemoteCaptureStarted(captureId, now, prepared.LeaseExpires);
        }
        finally
        {
            gate.Release();
        }
    }

    internal Task<RemoteCaptureState> StatusAsync(RemotePin owner, Guid captureId, CancellationToken cancellationToken) =>
        OnCaptureAsync(owner, captureId, async running =>
        {
            DriverCaptureState state = await driver.StatusAsync(captureId, cancellationToken).ConfigureAwait(false);
            return Note(running, state, reason: null);
        }, cancellationToken);

    internal Task<RemoteCaptureState> RenewAsync(RemotePin owner, Guid captureId, CancellationToken cancellationToken) =>
        OnCaptureAsync(owner, captureId, async running =>
        {
            if (!running.Closed)
            {
                running.LeaseExpires = time.GetUtcNow() + running.Lease;
            }

            DriverCaptureState state = await driver.StatusAsync(captureId, cancellationToken).ConfigureAwait(false);
            return Note(running, state, reason: null);
        }, cancellationToken);

    internal Task<RemoteCaptureState> StopAsync(RemotePin owner, Guid captureId, CancellationToken cancellationToken) =>
        OnCaptureAsync(owner, captureId, async running =>
        {
            if (running.Closed)
            {
                DriverCaptureState closed = await driver.StatusAsync(captureId, cancellationToken).ConfigureAwait(false);
                return Note(running, closed, reason: null);
            }

            DriverCaptureState state = await driver.StopAsync(captureId, cancellationToken).ConfigureAwait(false);

            // Stopped, the agent is free for another capture, though its host may still be finishing this one.
            running.Closed = true;
            return Note(running, state, "stopped by its controller");
        }, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await stopping.CancelAsync().ConfigureAwait(false);
        await loop.ConfigureAwait(false);
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (slot is { CaptureId: { } running, Closed: false } held)
            {
                _ = await driver.StopAsync(running, CancellationToken.None).ConfigureAwait(false);
                held.Closed = true;
                Record(held.Owner, "stopped: the agent stopped");
            }
            else if (slot is { CaptureId: null, Closed: false } prepared)
            {
                await driver.ReleaseAsync(prepared.Prepared, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }

        await driver.DisposeAsync().ConfigureAwait(false);
        stopping.Dispose();
    }

    private async Task<RemoteCaptureState> OnCaptureAsync(RemotePin owner, Guid captureId, Func<Slot, Task<RemoteCaptureState>> act,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (slot is not { CaptureId: { } held } running || held != captureId || running.Owner != owner)
            {
                throw new RemoteRequestRefusedException(RemoteErrorCode.NotPermitted,
                    "This controller holds no capture of that identity here.");
            }

            return await act(running).ConfigureAwait(false);
        }
        catch (RemoteCaptureRefusedException refused)
        {
            throw new RemoteRequestRefusedException(RemoteErrorCode.Failed, refused.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Keeps the agent's capture: renews the host's hold while the controller's lease holds, and stops it when it lapses.</summary>
    private async Task KeepAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(policy.Tick, cancellationToken).ConfigureAwait(false);
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await LetGoLapsedPreparationAsync().ConfigureAwait(false);
                DateTimeOffset now = time.GetUtcNow();
                if (slot is { CaptureId: { } captureId, Closed: false } running)
                {
                    if (now >= running.LeaseExpires)
                    {
                        _ = Note(running, await driver.StopAsync(captureId, CancellationToken.None).ConfigureAwait(false),
                            "its controller's lease lapsed");
                        running.Closed = true;
                        Record(running.Owner, "stopped: its controller's lease lapsed");
                    }
                    else if (now >= running.HostRenewal)
                    {
                        await driver.RenewAsync(captureId, CancellationToken.None).ConfigureAwait(false);
                        running.HostRenewal = now + policy.HostRenewal;
                        _ = Note(running, await driver.StatusAsync(captureId, CancellationToken.None).ConfigureAwait(false), reason: null);
                    }
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The host answered with a failure, or did not answer: the next tick asks again, and the controller's status
                // says what the host says. A lease that lapses is still stopped at the next tick that can reach the host.
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private async Task LetGoLapsedPreparationAsync()
    {
        if (slot is { CaptureId: null, Closed: false } prepared && time.GetUtcNow() >= prepared.PreparedUntil)
        {
            await driver.ReleaseAsync(prepared.Prepared, CancellationToken.None).ConfigureAwait(false);
            slot = null;
        }
    }

    private static RemoteCaptureState Note(Slot running, DriverCaptureState state, string? reason)
    {
        if (state.State == RemoteCaptureLifecycle.Closed)
        {
            running.Closed = true;
        }

        if (reason is not null && running.Reason is null)
        {
            running.Reason = reason;
        }

        // A host's broker counts a capture while it records and says nothing of it once closed: the highest counts it said
        // are the capture's, so a close never reads as nothing recorded (found by the first real capture, revision 1096).
        running.Records = Math.Max(running.Records, state.Records);
        running.Lost = Math.Max(running.Lost, state.Lost);
        return new RemoteCaptureState(running.CaptureId!.Value, state.State, running.Records, running.Lost, running.LeaseExpires,
            state.Finalized, running.Reason ?? state.Reason ?? string.Empty);
    }

    private void Record(RemotePin controller, string outcome) =>
        audit?.Record(new RemoteAuditEntry(time.GetUtcNow(), controller, RemoteMessageType.StopCapture, outcome));

    private sealed class Slot(RemotePin owner, Guid prepared, DateTimeOffset preparedUntil)
    {
        public RemotePin Owner { get; } = owner;
        public Guid Prepared { get; } = prepared;
        public DateTimeOffset PreparedUntil { get; } = preparedUntil;
        public Guid? CaptureId { get; set; }
        public TimeSpan Lease { get; set; }
        public DateTimeOffset LeaseExpires { get; set; }
        public DateTimeOffset HostRenewal { get; set; }
        public bool Closed { get; set; }
        public string? Reason { get; set; }
        public long Records { get; set; }
        public long Lost { get; set; }
    }
}
