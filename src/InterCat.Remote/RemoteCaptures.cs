namespace InterCat.Remote;

/// <summary>What a driver prepared on the agent's host: the agent's handle for it, and what the host will record.</summary>
public sealed record DriverPreparation(Guid Prepared, string Profile, uint Seconds, uint JournalMebibytes, string Collects);

/// <summary>A capture its driver started: its identity, and the folder where its host's broker keeps its evidence.</summary>
public sealed record DriverStart(Guid CaptureId, string Evidence);

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

    /// <summary>
    /// Starts what it prepared, returning the capture's identity and where its evidence is kept. A host that cannot say
    /// where stops the capture and refuses, since evidence no one can find can never be fetched.
    /// </summary>
    Task<DriverStart> StartAsync(Guid prepared, CancellationToken cancellationToken);

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
/// stops its capture. Each capture started is recorded with where its evidence is, and once the agent no longer holds it
/// open its evidence is served to the controller that started it, listed and read but never written (§4.5).
/// </summary>
public sealed class RemoteCaptures : IAsyncDisposable
{
    private readonly IRemoteCaptureDriver driver;
    private readonly RemoteCapturePolicy policy;
    private readonly TimeProvider time;
    private readonly IRemoteAudit? audit;
    private readonly IRemoteCaptureRecords records;
    private readonly IRemoteEvidenceSource? evidence;
    private readonly List<Served> served = [];
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource stopping = new();
    private readonly Task loop;
    private Slot? slot;
    private bool disposed;

    public RemoteCaptures(IRemoteCaptureDriver driver, RemoteCapturePolicy? policy = null, TimeProvider? time = null, IRemoteAudit? audit = null,
        IRemoteCaptureRecords? records = null, IRemoteEvidenceSource? evidence = null)
    {
        ArgumentNullException.ThrowIfNull(driver);
        this.driver = driver;
        this.policy = policy ?? RemoteCapturePolicy.Default;
        this.time = time ?? TimeProvider.System;
        this.audit = audit;
        this.records = records ?? new MemoryCaptureRecords();
        this.evidence = evidence;
        loop = Task.Run(() => KeepAsync(stopping.Token), CancellationToken.None);
    }

    /// <summary>The agent's bounds.</summary>
    public RemoteCapturePolicy Policy => policy;

    /// <summary>Whether the agent serves the evidence of its captures to the controllers that started them.</summary>
    public bool ServesEvidence => evidence is not null;

    /// <summary>Why the agent's bounds refuse a capture of <paramref name="seconds"/> and a journal of <paramref name="journalMebibytes"/>; null where they hold it.</summary>
    internal string? BoundsProblem(uint seconds, uint journalMebibytes) =>
        seconds < 1 || seconds > policy.MaximumSeconds
            ? $"This agent records for 1 to {policy.MaximumSeconds:N0} seconds, not {seconds:N0}."
            : journalMebibytes < 1 || journalMebibytes > policy.MaximumJournalMebibytes
                ? $"This agent keeps a journal of 1 to {policy.MaximumJournalMebibytes:N0} MiB, not {journalMebibytes:N0}."
                : null;

    internal async Task<RemotePreparedCapture> PrepareAsync(RemotePin owner, RemoteCaptureRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Profile))
        {
            throw new RemoteRequestRefusedException(RemoteErrorCode.Malformed, "A capture names its profile.");
        }

        if (BoundsProblem(request.Seconds, request.JournalMebibytes) is { } outside)
        {
            throw new RemoteRequestRefusedException(RemoteErrorCode.NotPermitted, outside);
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

            slot = new Slot(owner, prepared.Prepared, prepared.Profile, time.GetUtcNow() + policy.PreparedFor);
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

            DriverStart started;
            try
            {
                started = await driver.StartAsync(prepared.Prepared, cancellationToken).ConfigureAwait(false);
            }
            catch (RemoteCaptureRefusedException refused)
            {
                slot = null;
                throw new RemoteRequestRefusedException(RemoteErrorCode.Failed, refused.Message);
            }

            Guid captureId = started.CaptureId;
            DateTimeOffset now = time.GetUtcNow();
            try
            {
                records.Add(new RemoteCaptureRecord(captureId, owner, started.Evidence, prepared.Profile, now));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // A capture the agent keeps no record of could never be fetched, so it is stopped rather than left recording.
                // Why the record failed stays on the agent's host: its words name the agent's folder.
                try
                {
                    _ = await driver.StopAsync(captureId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (RemoteCaptureRefusedException)
                {
                    // Its broker stops it all the same once the agent's hold lapses.
                }

                slot = null;
                throw new RemoteRequestRefusedException(RemoteErrorCode.Failed,
                    "The agent could not keep a record of the capture, so it stopped it; its administrator finds why on its host.");
            }

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

    /// <summary>
    /// A page of the evidence of a capture <paramref name="owner"/> started (`remote-agent-v1` §4.5). A listing of the
    /// current generation reads the evidence afresh; a page of a generation the evidence no longer names is refused as
    /// busy, and the controller lists it again.
    /// </summary>
    internal async Task<RemoteEvidencePage> ListAsync(RemotePin owner, RemoteListRequest request, CancellationToken cancellationToken)
    {
        Served listed = await ServedAsync(owner, request.CaptureId, fresh: request.Generation == 0, cancellationToken).ConfigureAwait(false);
        RemoteEvidenceSnapshot snapshot = listed.Snapshot;
        if (request.Generation != 0 && request.Generation != snapshot.Generation)
        {
            throw new RemoteRequestRefusedException(RemoteErrorCode.Busy,
                "The capture's evidence changed since this controller listed it; it lists it again.");
        }

        if (request.First > snapshot.Files.Count)
        {
            throw new RemoteRequestRefusedException(RemoteErrorCode.NotPermitted,
                $"The capture's evidence lists {snapshot.Files.Count:N0} files, not {request.First:N0} or more.");
        }

        var files = new List<RemoteEvidenceFile>();
        int bytes = 0;
        for (int at = (int)request.First; at < snapshot.Files.Count && (request.Most == 0 || files.Count < request.Most); at++)
        {
            int size = RemoteEvidencePage.EncodedSize(snapshot.Files[at]);
            if (bytes + size > RemoteEvidencePage.MaximumFilesBytes)
            {
                break;
            }

            files.Add(snapshot.Files[at]);
            bytes += size;
        }

        return new RemoteEvidencePage(request.CaptureId, snapshot.Generation, snapshot.PointerBytes, (uint)snapshot.Files.Count,
            snapshot.TotalBytes, request.First, files);
    }

    /// <summary>Bytes of a file the evidence of a capture <paramref name="owner"/> started lists, as many as one read answers with.</summary>
    internal async Task<RemoteFileBytes> ReadAsync(RemotePin owner, RemoteReadRequest request, CancellationToken cancellationToken)
    {
        Served listed = await ServedAsync(owner, request.CaptureId, fresh: false, cancellationToken).ConfigureAwait(false);
        RemoteEvidenceFile file = listed.Snapshot.Files.FirstOrDefault(listedFile => listedFile.Name.Equals(request.Name, StringComparison.Ordinal))
            ?? throw new RemoteRequestRefusedException(RemoteErrorCode.NotPermitted, "The capture's evidence lists no file of that name.");
        if (request.Offset < 0 || request.Offset > file.Length)
        {
            throw new RemoteRequestRefusedException(RemoteErrorCode.NotPermitted,
                $"'{file.Name}' is {file.Length:N0} bytes; a read starts within it.");
        }

        int count = (int)Math.Min(Math.Min(request.Count, (uint)RemoteFileBytes.MaximumBytes), file.Length - request.Offset);
        byte[] bytes = new byte[count];
        if (count > 0 && evidence!.Read(listed.Evidence, file.Name, request.Offset, bytes) != count)
        {
            throw new RemoteRequestRefusedException(RemoteErrorCode.Failed,
                $"'{file.Name}' is shorter on the agent's host than its evidence records.");
        }

        return new RemoteFileBytes(request.Offset, bytes);
    }

    /// <summary>
    /// The evidence of a capture <paramref name="owner"/> started, listed once and kept for the reads that follow; listed
    /// afresh where <paramref name="fresh"/> asks. A capture the agent still holds open is not served until it closes.
    /// </summary>
    private async Task<Served> ServedAsync(RemotePin owner, Guid captureId, bool fresh, CancellationToken cancellationToken)
    {
        if (evidence is null)
        {
            throw new RemoteRequestRefusedException(RemoteErrorCode.Unsupported, "This agent serves no evidence.");
        }

        lock (served)
        {
            if (!fresh && served.Find(kept => kept.CaptureId == captureId && kept.Owner == owner) is { } known)
            {
                return known;
            }
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (slot is { CaptureId: { } held, Closed: false } && held == captureId)
            {
                throw new RemoteRequestRefusedException(RemoteErrorCode.Busy,
                    "The capture is still recording; its evidence is fetched once it closes.");
            }
        }
        finally
        {
            gate.Release();
        }

        RemoteCaptureRecord? record;
        try
        {
            record = records.Read().LastOrDefault(kept => kept.CaptureId == captureId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new RemoteRequestRefusedException(RemoteErrorCode.Failed,
                "The agent cannot read its records of the captures it started; its administrator finds why on its host.");
        }

        if (record is null || record.Owner != owner)
        {
            throw new RemoteRequestRefusedException(RemoteErrorCode.NotPermitted,
                "This controller started no capture of that identity here, or the agent no longer remembers it.");
        }

        RemoteEvidenceSnapshot snapshot;
        try
        {
            snapshot = evidence.List(record.Evidence);
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or FileNotFoundException)
        {
            throw new RemoteRequestRefusedException(RemoteErrorCode.Failed, "The capture's evidence is no longer on the agent's host.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // The store's own words can name the folder on the agent's host, which the agent never says.
            throw new RemoteRequestRefusedException(RemoteErrorCode.Failed,
                "The capture's evidence on the agent's host cannot be read whole, or does not verify, so it is not served.");
        }

        if (snapshot.Problem() is { } problem)
        {
            throw new RemoteRequestRefusedException(RemoteErrorCode.Failed, "The capture's evidence is not served as it is: " + problem + ".");
        }

        var listed = new Served(captureId, owner, record.Evidence, snapshot);
        lock (served)
        {
            _ = served.RemoveAll(kept => kept.CaptureId == captureId);
            served.Add(listed);
            if (served.Count > MaximumServed)
            {
                served.RemoveAt(0);
            }
        }

        return listed;
    }

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

    /// <summary>Where the capture stands now, as the agent answers it: its highest counts, and when it answered by its clock.</summary>
    private RemoteCaptureState Note(Slot running, DriverCaptureState state, string? reason)
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
            state.Finalized, running.Reason ?? state.Reason ?? string.Empty, time.GetUtcNow());
    }

    private void Record(RemotePin controller, string outcome) =>
        audit?.Record(new RemoteAuditEntry(time.GetUtcNow(), controller, RemoteMessageType.StopCapture, outcome));

    /// <summary>How many captures' listings the agent keeps for the reads that follow them.</summary>
    private const int MaximumServed = 4;

    /// <summary>A capture's evidence as listed for its owner, kept for the reads that follow.</summary>
    private sealed record Served(Guid CaptureId, RemotePin Owner, string Evidence, RemoteEvidenceSnapshot Snapshot);

    private sealed class Slot(RemotePin owner, Guid prepared, string profile, DateTimeOffset preparedUntil)
    {
        public RemotePin Owner { get; } = owner;
        public Guid Prepared { get; } = prepared;
        public string Profile { get; } = profile;
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
