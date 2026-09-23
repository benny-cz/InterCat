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

    /// <summary>Records this host produces once the pump starts.</summary>
    public List<AdmittedEvent> Scripted { get; } = [];

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

        public string SessionName => plan.Identity.SessionName;

        public int CaptureStateRequests { get; private set; }

        public long ProviderLoss { get; set; }

        public long ConsumerLoss { get; set; }

        public bool Disposed { get; private set; }

        public ProviderEnablementResult Enable(ProviderEnablementRequest request) =>
            host.FailProviderEnable
                ? new(request.SourceId, false, host.ProviderFailureReason)
                : new(request.SourceId, true, null);

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

            while (!stopRequested && !cancellationToken.IsCancellationRequested)
            {
                Thread.Sleep(5);
            }
        }

        public void RequestStopProcessing() => stopRequested = true;

        public SourceLossReading ReadLoss() => new(ProviderLoss, ConsumerLoss);

        public void StopSession() => host.StoppedSessions.Add(SessionName);

        public void Dispose() => Disposed = true;

        public void WaitUntilPumping() => pumping.Wait(TimeSpan.FromSeconds(5));

        public void WaitUntilScriptedRecordsDelivered() =>
            scriptedRecordsDelivered.Wait(TimeSpan.FromSeconds(5));
    }
}
