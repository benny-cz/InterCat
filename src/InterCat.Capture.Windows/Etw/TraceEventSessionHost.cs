using System.Globalization;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.Capture.Windows;

/// <summary>
/// The live ETW host. A new capture creates a uniquely named real-time session and never restarts or
/// adopts an existing one. Recovery may attach only to the exact token-shaped name from a durable
/// broker ownership record, to stop an orphan from the former process (section 9.2, P14).
/// </summary>
public sealed class TraceEventSessionHost : IEtwSessionHost, IEtwSessionReclaimer
{
    private const string CurrentBrokerPrefix = "InterCat-b-";
    private const string LegacyBrokerPrefix = "InterCat-broker-";

    public bool? IsElevated => OperatingSystem.IsWindows() ? TraceEventSession.IsElevated() : false;

    /// <inheritdoc />
    public bool StopPreviouslyOwnedSession(string sessionName, Guid ownershipToken)
    {
        if (!IsBrokerOwnershipName(sessionName, ownershipToken))
        {
            throw new ArgumentException(
                "Recovery requires an exact InterCat broker session name containing its durable ownership token.",
                nameof(sessionName));
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("ETW recovery requires Windows.");
        }

        if (!ListActiveSessionNames().Contains(sessionName, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            // Attach never creates or replaces a session. A name collision remains a refusal rather
            // than the default TraceEventSession constructor's stop-and-recreate behavior.
            using var attached = new TraceEventSession(sessionName, TraceEventSessionOptions.Attach)
            {
                StopOnDispose = false,
            };
            attached.Stop(noThrow: false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A concurrent stop is harmless, but an inaccessible session that still exists is not.
            if (!ListActiveSessionNames().Contains(sessionName, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }

            throw new EtwSessionException(
                $"The previously owned ETW session '{sessionName}' could not be stopped: {exception.Message}",
                exception);
        }

        if (ListActiveSessionNames().Contains(sessionName, StringComparer.OrdinalIgnoreCase))
        {
            throw new EtwSessionException(
                $"The previously owned ETW session '{sessionName}' still exists after its stop request.");
        }

        return true;
    }

    internal static bool IsBrokerOwnershipName(string? name, Guid token)
    {
        if (token == Guid.Empty || name is null)
        {
            return false;
        }

        string hex = token.ToString("N");
        if (name.StartsWith(CurrentBrokerPrefix, StringComparison.Ordinal))
        {
            ReadOnlySpan<char> remainder = name.AsSpan(CurrentBrokerPrefix.Length);
            return remainder.Length == 16 + 1 + 32
                && remainder[16] == '-'
                && remainder[..16].ToString().All(Uri.IsHexDigit)
                && remainder[17..].SequenceEqual(hex);
        }

        if (name.StartsWith(LegacyBrokerPrefix, StringComparison.Ordinal))
        {
            ReadOnlySpan<char> remainder = name.AsSpan(LegacyBrokerPrefix.Length);
            return remainder.Length == 32 + 1 + 15
                && remainder[32] == '-'
                && remainder[..32].ToString().All(Uri.IsHexDigit)
                && remainder[33..].SequenceEqual(hex.AsSpan(0, 15));
        }

        return false;
    }

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
