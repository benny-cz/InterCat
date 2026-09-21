using InterCat.Capture.Windows;

namespace InterCat.Capture.Windows.Tests;

internal sealed class FakeEtlFileSessionHost : IEtlFileSessionHost
{
    private readonly List<string> existingSessions;

    public FakeEtlFileSessionHost(params string[] existingSessions) => this.existingSessions = [.. existingSessions];

    public bool? IsElevated { get; set; } = true;

    public bool FailProviderEnable { get; set; }

    public bool ThrowDuringProviderEnable { get; set; }

    public string? ReturnedSessionName { get; set; }

    public long EventsLost { get; set; }

    public int OutputBytesOnStop { get; set; }

    public List<string> CreatedSessions { get; } = [];

    public List<string> StoppedSessions { get; } = [];

    public List<string> DisposedSessions { get; } = [];

    public IReadOnlyList<string> ListActiveSessionNames() => existingSessions;

    public IOwnedEtlFileSession CreateExclusive(EtlFileCapturePlan plan, string validatedOutputPath)
    {
        string name = ReturnedSessionName ?? plan.Session.Identity.SessionName;
        CreatedSessions.Add(name);
        return new FakeOwnedEtlFileSession(this, name, validatedOutputPath);
    }

    private sealed class FakeOwnedEtlFileSession(
        FakeEtlFileSessionHost host,
        string sessionName,
        string outputPath) : IOwnedEtlFileSession
    {
        public string SessionName => sessionName;

        public string OutputPath => outputPath;

        public ProviderEnablementResult Enable(ProviderEnablementRequest request)
        {
            if (host.ThrowDuringProviderEnable)
            {
                throw new InvalidOperationException("scripted provider exception");
            }

            return host.FailProviderEnable
                ? new(request.SourceId, false, "scripted refusal")
                : new(request.SourceId, true, null);
        }

        public bool TryRequestCaptureState(ProviderEnablementRequest request, out string? failureReason)
        {
            failureReason = null;
            return true;
        }

        public long ReadEventsLost() => host.EventsLost;

        public void StopSession()
        {
            host.StoppedSessions.Add(SessionName);
            if (host.OutputBytesOnStop > 0)
            {
                File.WriteAllBytes(OutputPath, new byte[host.OutputBytesOnStop]);
            }
        }

        public void Dispose() => host.DisposedSessions.Add(SessionName);
    }
}
