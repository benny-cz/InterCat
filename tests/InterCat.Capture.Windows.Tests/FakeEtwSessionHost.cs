using InterCat.Capture.Windows;

namespace InterCat.Capture.Windows.Tests;

/// <summary>
/// A host that records what a capture attempt did to the machine. It lets the lifecycle, its cleanup and
/// its non-interference rules be tested without ETW or elevation (R19).
/// </summary>
internal sealed class FakeEtwSessionHost : IEtwSessionHost
{
    private readonly List<string> existingSessions;

    public FakeEtwSessionHost(params string[] existingSessions) => this.existingSessions = [.. existingSessions];

    public bool? IsElevated { get; set; } = true;

    public bool FailProviderEnable { get; set; }

    public string ProviderFailureReason { get; set; } = "access is denied";

    public bool FailCreate { get; set; }

    public bool FailCaptureState { get; set; }

    public bool RefuseFlush { get; set; }

    /// <summary>Refuses kernel flags, as a machine at its limit of eight system loggers would.</summary>
    public bool RefuseKernelFlags { get; set; }

    /// <summary>Every enablement, in order: "kernel:<flags>" or a provider's source id.</summary>
    public List<string> Enablements { get; } = [];

    /// <summary>Records this host produces once the pump starts.</summary>
    public List<AdmittedEvent> Scripted { get; } = [];

    /// <summary>
    /// Records raised after <see cref="Scripted"/> that still wait in the session's buffers: as ETW does, the pump delivers
    /// them when the session stops, and never when delivery is ended first.
    /// </summary>
    public List<AdmittedEvent> Buffered { get; } = [];

    /// <summary>Whether the capture ended delivery while the session still ran.</summary>
    public bool DeliveryEndedBeforeStop { get; private set; }

    public List<string> StoppedSessions { get; } = [];

    public List<string> CreatedSessions { get; } = [];

    public FakeOwnedSession? Last { get; private set; }

    public IReadOnlyList<string> ListActiveSessionNames() => existingSessions;

    public IOwnedEtwSession CreateExclusive(OwnedSessionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (FailCreate)
        {
            throw new EtwSessionException("the session could not be created in this test");
        }

        if (existingSessions.Contains(plan.Identity.SessionName, StringComparer.Ordinal))
        {
            throw new EtwSessionException("a session with this exact name already exists");
        }

        CreatedSessions.Add(plan.Identity.SessionName);
        existingSessions.Add(plan.Identity.SessionName);
        Last = new(this, plan);
        return Last;
    }

    internal sealed class FakeOwnedSession(FakeEtwSessionHost host, OwnedSessionPlan plan) : IOwnedEtwSession
    {
        private readonly ManualResetEventSlim pumping = new(false);
        private readonly ManualResetEventSlim scriptedRecordsDelivered = new(false);
        private volatile bool stopRequested;
        private volatile bool sessionStopped;

        public string SessionName => plan.Identity.SessionName;

        public int CaptureStateRequests { get; private set; }

        public long ProviderLoss { get; set; }

        public long ConsumerLoss { get; set; }

        public bool Disposed { get; private set; }

        public ProviderEnablementResult Enable(ProviderEnablementRequest request)
        {
            host.Enablements.Add(request.SourceId);
            return host.FailProviderEnable
                ? new(request.SourceId, false, host.ProviderFailureReason)
                : new(request.SourceId, true, null);
        }

        public ProviderEnablementResult EnableKernelFlags(string sourceId, ulong flags)
        {
            host.Enablements.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"kernel:0x{flags:X}"));
            return host.RefuseKernelFlags
                ? new(sourceId, false, "the machine already runs eight system loggers")
                : new(sourceId, true, null);
        }

        public bool TryRequestCaptureState(ProviderEnablementRequest request, out string? failureReason)
        {
            CaptureStateRequests++;
            failureReason = host.FailCaptureState ? "rundown refused in this test" : null;
            return !host.FailCaptureState;
        }

        public void Pump(EventAdmissionTable table, IAdmittedEventSink sink, CancellationToken cancellationToken)
        {
            pumping.Set();
            foreach (AdmittedEvent scripted in host.Scripted)
            {
                if (stopRequested || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                sink.OnObserved(new DeliveredRecord(Guid.Empty, scripted.EventId, scripted.Version, scripted.TimestampQpc));
                _ = sink.Admit(scripted);
            }

            scriptedRecordsDelivered.Set();

            while (!stopRequested && !sessionStopped && !cancellationToken.IsCancellationRequested)
            {
                Thread.Sleep(5);
            }

            if (sessionStopped && !stopRequested)
            {
                foreach (AdmittedEvent buffered in host.Buffered)
                {
                    sink.OnObserved(new DeliveredRecord(Guid.Empty, buffered.EventId, buffered.Version, buffered.TimestampQpc));
                    _ = sink.Admit(buffered);
                }
            }
        }

        public void RequestStopProcessing()
        {
            host.DeliveryEndedBeforeStop |= !sessionStopped;
            stopRequested = true;
        }

        /// <summary>
        /// Set on one thread, a loss read there signals <see cref="LossReadStalled"/>, waits for the event, then fails
        /// as a query does when the session was stopped under it. Reads on other threads answer normally.
        /// </summary>
        [ThreadStatic]
        internal static ManualResetEventSlim? StallLossRead;

        public ManualResetEventSlim LossReadStalled { get; } = new(false);

        public SourceLossReading ReadLoss()
        {
            if (StallLossRead is { } release)
            {
                LossReadStalled.Set();
                release.Wait(TimeSpan.FromSeconds(10));
                throw new EtwSessionException("The session was stopped under this read.");
            }

            return new(ProviderLoss, ConsumerLoss);
        }

        private int flushRequests;

        public int FlushRequests => Volatile.Read(ref flushRequests);

        public bool TryFlushDelivery()
        {
            Interlocked.Increment(ref flushRequests);
            return !host.RefuseFlush;
        }

        public void StopSession()
        {
            host.StoppedSessions.Add(SessionName);
            sessionStopped = true;
        }

        public void Dispose() => Disposed = true;

        public void WaitUntilPumping() => pumping.Wait(TimeSpan.FromSeconds(5));

        public void WaitUntilScriptedRecordsDelivered() =>
            scriptedRecordsDelivered.Wait(TimeSpan.FromSeconds(5));
    }
}
