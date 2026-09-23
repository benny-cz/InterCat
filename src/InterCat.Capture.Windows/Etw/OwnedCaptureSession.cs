using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using InterCat.Domain;

namespace InterCat.Capture.Windows;

/// <summary>The outcome of a start attempt, including every provider refusal (section 9.1).</summary>
public sealed record CaptureStartResult(
    bool Started,
    CaptureLifecycle State,
    IReadOnlyList<ProviderEnablementResult> Providers,
    string? FailureReason,
    IReadOnlyList<string> Degradations,
    int OtherActiveSessionCount);

/// <summary>The outcome of a stop, with the final health ledger and the recorded interval.</summary>
public sealed record CaptureStopResult(
    CaptureLifecycle State,
    CaptureHealthSnapshot Health,
    long? FirstRecordUtcTicks,
    long? LastRecordUtcTicks,
    DateTimeOffset RecordingStartedUtc,
    DateTimeOffset RecordingStoppedUtc,
    IReadOnlyList<string> Degradations)
{
    /// <summary>False when the ETW delivery pump was still running after the stop timeout.</summary>
    public bool CallbacksDrained { get; init; }

    /// <summary>True only when stopping the exact session created by this capture succeeded.</summary>
    public bool ProvidersStopped { get; init; }
}

/// <summary>
/// One owned ETW session with the lifecycle of section 9.2: Idle, Probing, Starting, Recording, Stopping,
/// Finalizing, Closed. Degradation is an orthogonal flag with reasons, not a state. Cleanup touches only
/// resources this attempt created (P14), and the callback path performs bounded admission only (R9).
/// </summary>
public sealed class OwnedCaptureSession : IAsyncDisposable
{
    private readonly OwnedSessionPlan plan;
    private readonly IEtwSessionHost host;
    private readonly TimeProvider clock;
    private readonly CaptureHealthLedger ledger = new();
    private readonly CaptureStageLedger stages = new();
    private readonly Channel<AdmittedEvent> records;
    private readonly EventAdmissionTable admissionTable;
    private readonly List<string> degradations = [];
    private readonly Lock gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly IDeliveryObserver? observer;
    private readonly Guid[] providerBySource;

    private IOwnedEtwSession? session;
    private Task? pumpTask;
    private Task? healthTask;
    private CaptureLifecycle state = CaptureLifecycle.Idle;
    private long firstRecordUtcTicks;
    private long lastRecordUtcTicks;
    private DateTimeOffset recordingStartedUtc;
    private DateTimeOffset recordingStoppedUtc;
    private CaptureClockEvidence? sourceClock;
    private int sourceClockChecked;
    private bool disposed;
    private bool providersStopped;
    private volatile bool sourceLossUnreadable;

    /// <param name="observer">
    /// Told every delivery outcome with its descriptor, from the callback, when a caller keeps a coverage ledger.
    /// </param>
    public OwnedCaptureSession(
        OwnedSessionPlan plan,
        IEtwSessionHost host,
        TimeProvider? clock = null,
        IDeliveryObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(host);

        this.plan = plan;
        this.host = host;
        this.clock = clock ?? TimeProvider.System;
        this.observer = observer;
        providerBySource = new Guid[plan.Sources.Count == 0 ? 0 : plan.Sources.Max(source => source.SourceIndex) + 1];
        foreach (SourceAdmissionPlan source in plan.Sources)
        {
            providerBySource[source.SourceIndex] = source.ProviderGuid;
        }

        admissionTable = new(plan.Sources);
        records = Channel.CreateBounded<AdmittedEvent>(new BoundedChannelOptions(plan.QueueCapacityRecords)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
    }

    public CaptureSessionIdentity Identity => plan.Identity;

    public CaptureLifecycle State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    public bool IsDegraded
    {
        get
        {
            lock (gate)
            {
                return degradations.Count > 0;
            }
        }
    }

    public IReadOnlyList<string> Degradations
    {
        get
        {
            lock (gate)
            {
                return degradations.ToArray();
            }
        }
    }

    /// <summary>Admitted records in acquisition order. Acquisition order and display order stay distinct (I7).</summary>
    public ChannelReader<AdmittedEvent> Records => records.Reader;

    public EventAdmissionTable AdmissionTable => admissionTable;

    /// <summary>
    /// The capture's source clock and the evidence for it. Before the first record it is an assumption;
    /// after the first record it is either confirmed or refused, never silently assumed (I8, section 18.3).
    /// </summary>
    public CaptureClockEvidence? SourceClock
    {
        get
        {
            lock (gate)
            {
                return sourceClock;
            }
        }
    }

    /// <summary>
    /// Whether the source's loss counters failed to read at any point. A coverage ledger then cannot claim what the
    /// session lost, because an unread counter is not a reported zero (`coverage-v1` §3, R21).
    /// </summary>
    public bool SourceLossUnreadable => sourceLossUnreadable;

    /// <summary>Isolated stage overhead for this capture (section 12).</summary>
    public CaptureStageSnapshot ReadStageMetrics() => stages.Read(plan.QueueCapacityRecords);

    public async Task<CaptureStartResult> StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Transition(CaptureLifecycle.Idle, CaptureLifecycle.Probing);

        if (host.IsElevated == false)
        {
            return Fail(
                "Starting an ETW session requires elevation. The probe ran without it, so no session was created.",
                []);
        }

        IReadOnlyList<string> active;
        try
        {
            active = host.ListActiveSessionNames();
        }
        catch (EtwSessionException exception)
        {
            return Fail($"Active sessions could not be listed: {exception.Message}", []);
        }

        int otherSessions = 0;
        foreach (string name in active)
        {
            if (plan.Identity.Owns(name))
            {
                return Fail(
                    $"A session named '{name}' already exists. InterCat refuses to adopt or restart a session it did not create.",
                    []);
            }

            otherSessions++;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Transition(CaptureLifecycle.Probing, CaptureLifecycle.Starting);

        IOwnedEtwSession created;
        try
        {
            created = host.CreateExclusive(plan);
        }
        catch (EtwSessionException exception)
        {
            return Fail(exception.Message, []);
        }

        session = created;
        var providerResults = new List<ProviderEnablementResult>(plan.Providers.Count);
        try
        {
            foreach (ProviderEnablementRequest request in plan.Providers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProviderEnablementResult result = created.Enable(request);
                providerResults.Add(result);
                if (!result.Enabled)
                {
                    AddDegradation($"{result.SourceId}: {result.FailureReason ?? "the provider refused enablement."}");
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await CleanUpCreatedResourcesAsync().ConfigureAwait(false);
            return exception is OperationCanceledException
                ? Fail("The start was cancelled; the session this attempt created was stopped.", providerResults)
                : Fail($"Provider enablement failed: {exception.Message}", providerResults);
        }

        bool anyEnabled = false;
        foreach (ProviderEnablementResult result in providerResults)
        {
            anyEnabled |= result.Enabled;
        }

        if (!anyEnabled)
        {
            await CleanUpCreatedResourcesAsync().ConfigureAwait(false);
            return Fail("No requested provider could be enabled, so no capture was started.", providerResults);
        }

        recordingStartedUtc = clock.GetUtcNow();
        lock (gate)
        {
            sourceClock = EtwSourceClock.DescribeLocal();
        }

        if (plan.PreserveExtendedData && !TraceEventRecordAccessor.Shared.IsAvailable)
        {
            string reason = TraceEventRecordAccessor.Shared.UnavailableReason
                ?? "The extended-data accessor is unavailable for an unstated reason.";
            stages.RecordExtendedDataUnavailable(reason);
            AddDegradation(
                "Extended-data items were requested but cannot be read on this adapter build, so every "
                + $"record reports them as unavailable rather than absent: {reason}");
        }

        pumpTask = Task.Factory.StartNew(
            () => RunPump(created),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Transition(CaptureLifecycle.Starting, CaptureLifecycle.Recording);

        // Delivery is running before inventory is requested, so short-lived state is less likely missed (18.5).
        foreach (ProviderEnablementRequest request in plan.Providers)
        {
            if (!request.RequestCaptureState)
            {
                continue;
            }

            if (!created.TryRequestCaptureState(request, out string? failureReason))
            {
                AddDegradation(
                    $"{request.SourceId}: state rundown was refused ({failureReason ?? "no reason reported"}), "
                    + "so processes started before this capture stay unknown rather than absent.");
            }
        }

        healthTask = Task.Run(() => SampleHealthAsync(lifetime.Token), CancellationToken.None);
        return new(true, CaptureLifecycle.Recording, providerResults, null, Degradations, otherSessions);
    }

    public CaptureHealthSnapshot ReadHealth()
    {
        IOwnedEtwSession? current = session;
        if (current is not null)
        {
            try
            {
                SourceLossReading loss = current.ReadLoss();
                ledger.RecordSourceLoss(loss.ProviderReportedEventLoss, loss.ConsumerReportedBufferLoss);
            }
            catch (EtwSessionException)
            {
                sourceLossUnreadable = true;
                AddDegradation("Source loss counters could not be read; reported loss may be understated.");
            }
        }

        return ledger.Read(records.Reader.Count, plan.QueueCapacityRecords);
    }

    public async Task<CaptureStopResult> StopAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        lock (gate)
        {
            if (state is CaptureLifecycle.Closed)
            {
                return BuildStopResult();
            }

            state = CaptureLifecycle.Stopping;
        }

        session?.RequestStopProcessing();
        if (pumpTask is not null)
        {
            try
            {
                await pumpTask.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                AddDegradation("The delivery pump did not stop within 15 seconds; counters may be incomplete.");
            }
            catch (OperationCanceledException)
            {
                AddDegradation("The stop was cancelled while the delivery pump was still running.");
            }
        }

        lock (gate)
        {
            state = CaptureLifecycle.Finalizing;
        }

        _ = ReadHealth();
        recordingStoppedUtc = clock.GetUtcNow();
        await CleanUpCreatedResourcesAsync().ConfigureAwait(false);
        records.Writer.TryComplete();

        lock (gate)
        {
            state = CaptureLifecycle.Closed;
        }

        return BuildStopResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await lifetime.CancelAsync().ConfigureAwait(false);
        session?.RequestStopProcessing();
        if (pumpTask is not null)
        {
            await Task.WhenAny(pumpTask, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        }

        await CleanUpCreatedResourcesAsync().ConfigureAwait(false);
        records.Writer.TryComplete();
        lifetime.Dispose();

        lock (gate)
        {
            state = CaptureLifecycle.Closed;
        }
    }

    private void RunPump(IOwnedEtwSession owned)
    {
        var sink = new QueueSink(this);
        long allocatedAtStart = GC.GetAllocatedBytesForCurrentThread();
        bool cpuReadable = ThreadCpuTime.TryReadCurrentThread(out ThreadCpuReading cpuAtStart);
        try
        {
            owned.Pump(admissionTable, sink, lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            // A cancelled pump is an ordinary stop, not a defect.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AddDegradation($"The delivery pump stopped early: {exception.Message}");
        }
        finally
        {
            // The pump thread is the callback thread, so its own totals isolate admission from the rest
            // of the process. An unreadable processor clock is reported as null, never as zero (R21).
            ThreadCpuReading? consumed = null;
            if (cpuReadable && ThreadCpuTime.TryReadCurrentThread(out ThreadCpuReading cpuAtEnd))
            {
                consumed = cpuAtEnd - cpuAtStart;
            }

            stages.RecordDeliveryThread(GC.GetAllocatedBytesForCurrentThread() - allocatedAtStart, consumed);
            records.Writer.TryComplete();
        }
    }

    private async Task SampleHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(plan.HealthSamplingInterval, clock);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                _ = ReadHealth();
            }
        }
        catch (OperationCanceledException)
        {
            // Sampling ends with the capture.
        }
    }

    private bool Enqueue(in AdmittedEvent admitted)
    {
        if (!records.Writer.TryWrite(admitted))
        {
            ledger.RecordApplicationDrop();
            return false;
        }

        ledger.RecordAdmitted();
        stages.RecordQueueDepth(records.Reader.Count);
        ConfirmSourceClock(admitted.TimestampQpc);
        long ticks = admitted.TimestampUtcTicks;
        if (Interlocked.CompareExchange(ref firstRecordUtcTicks, ticks, 0) == 0)
        {
            Interlocked.Exchange(ref lastRecordUtcTicks, ticks);
        }
        else
        {
            long previous = Interlocked.Read(ref lastRecordUtcTicks);
            if (ticks > previous)
            {
                Interlocked.Exchange(ref lastRecordUtcTicks, ticks);
            }
        }

        return true;
    }

    /// <summary>
    /// Checks the capture's assumed clock against the first delivered reading. A refusal becomes a
    /// degradation rather than a conversion, because a wrong encoding would distort every later time (I8).
    /// </summary>
    private void ConfirmSourceClock(long nativeTicks)
    {
        // The check happens once per capture, so the callback path reads a flag rather than taking the
        // lock for every record (R9).
        if (Interlocked.CompareExchange(ref sourceClockChecked, 1, 0) != 0)
        {
            return;
        }

        string? refusal = null;
        lock (gate)
        {
            if (sourceClock is null || sourceClock.Confidence != SourceClockConfidence.Unconfirmed)
            {
                return;
            }

            sourceClock = EtwSourceClock.Check(sourceClock, nativeTicks);
            if (sourceClock.Confidence == SourceClockConfidence.Refused)
            {
                refusal = sourceClock.RefusalReason;
            }
        }

        if (refusal is not null)
        {
            AddDegradation(refusal);
        }
    }

    private async Task CleanUpCreatedResourcesAsync()
    {
        IOwnedEtwSession? current = Interlocked.Exchange(ref session, null);
        if (current is null)
        {
            return;
        }

        try
        {
            if (!plan.Identity.Owns(current.SessionName))
            {
                AddDegradation(
                    $"Refused to stop '{current.SessionName}': it is not the session this attempt created.");
                return;
            }

            current.StopSession();
            providersStopped = true;
        }
        catch (EtwSessionException exception)
        {
            AddDegradation($"The owned session could not be stopped cleanly: {exception.Message}");
        }
        finally
        {
            current.Dispose();
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private CaptureStartResult Fail(string reason, IReadOnlyList<ProviderEnablementResult> providers)
    {
        lock (gate)
        {
            state = CaptureLifecycle.Closed;
        }

        return new(false, CaptureLifecycle.Closed, providers, reason, Degradations, 0);
    }

    private CaptureStopResult BuildStopResult() => new(
        CaptureLifecycle.Closed,
        ledger.Read(records.Reader.Count, plan.QueueCapacityRecords),
        Interlocked.Read(ref firstRecordUtcTicks) == 0 ? null : Interlocked.Read(ref firstRecordUtcTicks),
        Interlocked.Read(ref lastRecordUtcTicks) == 0 ? null : Interlocked.Read(ref lastRecordUtcTicks),
        recordingStartedUtc,
        recordingStoppedUtc == default ? clock.GetUtcNow() : recordingStoppedUtc,
        Degradations)
    {
        CallbacksDrained = pumpTask?.IsCompleted ?? true,
        ProvidersStopped = providersStopped,
    };

    private void Transition(CaptureLifecycle expected, CaptureLifecycle next)
    {
        lock (gate)
        {
            if (state != expected)
            {
                throw new InvalidOperationException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"A capture in state {state} cannot move to {next}."));
            }

            state = next;
        }
    }

    private void AddDegradation(string reason)
    {
        lock (gate)
        {
            if (!degradations.Contains(reason, StringComparer.Ordinal))
            {
                degradations.Add(reason);
            }
        }
    }

    private sealed class QueueSink(OwnedCaptureSession owner) : IAdmittedEventSink
    {
        public void OnObserved(in DeliveredRecord delivered)
        {
            owner.ledger.RecordObserved();
            owner.observer?.Delivered(in delivered);
        }

        public bool Admit(in AdmittedEvent admitted)
        {
            bool queued = owner.Enqueue(admitted);
            if (owner.observer is { } observer && (uint)admitted.SourceIndex < (uint)owner.providerBySource.Length)
            {
                observer.Admitted(owner.providerBySource[admitted.SourceIndex], admitted.EventId, admitted.Version, queued);
            }

            return queued;
        }

        public void OnOmitted(OmissionReason reason, in DeliveredRecord delivered)
        {
            owner.ledger.RecordOmission(reason);
            owner.observer?.Omitted(reason, in delivered);
        }

        public void OnUndecodable(UndecodableReason reason, in DeliveredRecord delivered)
        {
            owner.ledger.RecordUndecodable(reason);
            owner.observer?.Undecodable(reason, in delivered);
        }

        public void OnCallbackCompleted(in CallbackCost cost) => owner.stages.RecordCallback(in cost);
    }
}
