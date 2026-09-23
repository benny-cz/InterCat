using InterCat.Capture.Recording;
using InterCat.Capture.Windows;
using Xunit;

namespace InterCat.CaptureBroker.Tests;

/// <summary>An ETW host that owns named sessions in memory and pumps nothing until asked to stop.</summary>
internal sealed class ScriptedEtwHost : IEtwSessionHost, IEtwSessionReclaimer
{
    public List<string> Active { get; } = [];
    public List<string> Created { get; } = [];
    public List<string> Reclaimed { get; } = [];
    public bool? IsElevated => true;

    public IReadOnlyList<string> ListActiveSessionNames() => Active;

    public IOwnedEtwSession CreateExclusive(OwnedSessionPlan plan)
    {
        if (Active.Contains(plan.Identity.SessionName, StringComparer.Ordinal))
        {
            throw new EtwSessionException("The exact session already exists.");
        }

        Active.Add(plan.Identity.SessionName);
        Created.Add(plan.Identity.SessionName);
        return new Session(this, plan.Identity.SessionName);
    }

    public bool StopPreviouslyOwnedSession(string sessionName, Guid ownershipToken)
    {
        Assert.EndsWith(ownershipToken.ToString("N"), sessionName, StringComparison.Ordinal);
        Reclaimed.Add(sessionName);
        return Active.Remove(sessionName);
    }

    private sealed class Session(ScriptedEtwHost host, string name) : IOwnedEtwSession
    {
        private volatile bool stop;
        public string SessionName => name;
        public ProviderEnablementResult Enable(ProviderEnablementRequest request) =>
            new(request.SourceId, true, null);
        public bool TryRequestCaptureState(ProviderEnablementRequest request, out string? failureReason)
        {
            failureReason = null;
            return true;
        }

        public void Pump(EventAdmissionTable table, IAdmittedEventSink sink, CancellationToken token)
        {
            while (!stop && !token.IsCancellationRequested)
            {
                Thread.Sleep(1);
            }
        }

        public void RequestStopProcessing() => stop = true;
        public SourceLossReading ReadLoss() => new(0, 0);
        public void StopSession() => host.Active.Remove(name);
        public void Dispose() { }
    }
}
