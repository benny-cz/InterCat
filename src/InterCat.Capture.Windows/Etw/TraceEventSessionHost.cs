using System.Globalization;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.Capture.Windows;

/// <summary>
/// The live ETW host. It creates a uniquely named real-time session, never restarts or adopts an existing
/// one, and stops only the session it created (section 9.2, P14).
/// </summary>
public sealed class TraceEventSessionHost : IEtwSessionHost
{
    public bool? IsElevated => OperatingSystem.IsWindows() ? TraceEventSession.IsElevated() : false;

    public IReadOnlyList<string> ListActiveSessionNames()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            return TraceEventSession.GetActiveSessionNames();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new EtwSessionException($"Active ETW sessions could not be listed: {exception.Message}", exception);
        }
    }

    public IOwnedEtwSession CreateExclusive(OwnedSessionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!OperatingSystem.IsWindows())
        {
            throw new EtwSessionException("ETW capture is available only on Windows.");
        }

        TraceEventSession? created = null;
        try
        {
            created = new(
                plan.Identity.SessionName,
                TraceEventSessionOptions.Create | TraceEventSessionOptions.NoRestartOnCreate)
            {
                StopOnDispose = true,
                BufferQuantumKB = plan.BufferSizeKilobytes,
            };
            return new TraceEventOwnedSession(created, plan);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            created?.Dispose();
            throw new EtwSessionException(
                $"The session '{plan.Identity.SessionName}' could not be created: {exception.Message}",
                exception);
        }
    }

    private sealed class TraceEventOwnedSession(TraceEventSession session, OwnedSessionPlan plan) : IOwnedEtwSession
    {
        private ETWTraceEventSource? source;
        private bool stopped;

        public string SessionName => session.SessionName;

        public ProviderEnablementResult Enable(ProviderEnablementRequest request) =>
            TraceEventProviderControl.Enable(session, request);

        public bool TryRequestCaptureState(ProviderEnablementRequest request, out string? failureReason) =>
            TraceEventProviderControl.TryRequestCaptureState(session, request, out failureReason);

        public void Pump(EventAdmissionTable table, IAdmittedEventSink sink, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(table);
            ArgumentNullException.ThrowIfNull(sink);

            ETWTraceEventSource live = session.Source;
            source = live;
            var admitter = new TraceEventRecordAdmitter(table, plan.Providers, sink, plan.PreserveExtendedData);
            void OnEvent(TraceEvent data) => admitter.Admit(data);

            live.AllEvents += OnEvent;
            using CancellationTokenRegistration registration = cancellationToken.Register(live.StopProcessing);
            try
            {
                live.Process();
            }
            finally
            {
                live.AllEvents -= OnEvent;
            }
        }

        public void RequestStopProcessing()
        {
            try
            {
                source?.StopProcessing();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Stopping an already finished pump is not a defect.
            }
        }

        public SourceLossReading ReadLoss()
        {
            try
            {
                return new(session.EventsLost, source?.EventsLost ?? 0);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                throw new EtwSessionException($"Loss counters could not be read: {exception.Message}", exception);
            }
        }

        public void StopSession()
        {
            if (stopped)
            {
                return;
            }

            stopped = true;
            try
            {
                session.Stop(noThrow: true);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                throw new EtwSessionException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The owned session '{session.SessionName}' could not be stopped: {exception.Message}"),
                    exception);
            }
        }

        public void Dispose()
        {
            source?.Dispose();
            session.Dispose();
        }
    }
}
