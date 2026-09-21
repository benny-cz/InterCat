using System.Globalization;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.Capture.Windows;

/// <summary>TraceEvent-backed sequential ETL acquisition used by the IC-009 comparison harness.</summary>
public sealed class TraceEventEtlFileSessionHost : IEtlFileSessionHost
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

    public IOwnedEtlFileSession CreateExclusive(EtlFileCapturePlan plan, string validatedOutputPath)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(validatedOutputPath);
        if (!OperatingSystem.IsWindows())
        {
            throw new EtwSessionException("ETL capture is available only on Windows.");
        }

        TraceEventSession? created = null;
        try
        {
            created = new(
                plan.Session.Identity.SessionName,
                validatedOutputPath,
                TraceEventSessionOptions.Create | TraceEventSessionOptions.NoRestartOnCreate)
            {
                StopOnDispose = true,
                BufferSizeMB = plan.BufferSizeMegabytes,
                BufferQuantumKB = plan.Session.BufferSizeKilobytes,
            };
            return new TraceEventOwnedEtlFileSession(created, validatedOutputPath);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            created?.Dispose();
            throw new EtwSessionException(
                $"The ETL session '{plan.Session.Identity.SessionName}' could not be created: {exception.Message}",
                exception);
        }
    }

    private sealed class TraceEventOwnedEtlFileSession(TraceEventSession session, string outputPath)
        : IOwnedEtlFileSession
    {
        private bool stopped;

        public string SessionName => session.SessionName;

        public string OutputPath => outputPath;

        public ProviderEnablementResult Enable(ProviderEnablementRequest request) =>
            TraceEventProviderControl.Enable(session, request);

        public bool TryRequestCaptureState(ProviderEnablementRequest request, out string? failureReason) =>
            TraceEventProviderControl.TryRequestCaptureState(session, request, out failureReason);

        public long ReadEventsLost()
        {
            try
            {
                return session.EventsLost;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                throw new EtwSessionException($"ETL loss counters could not be read: {exception.Message}", exception);
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
                        $"The owned ETL session '{session.SessionName}' could not be stopped: {exception.Message}"),
                    exception);
            }
        }

        public void Dispose() => session.Dispose();
    }
}
