namespace InterCat.Capture.Windows;

/// <summary>A capture attempt failed for a stated, non-retryable reason.</summary>
public sealed class EtwSessionException : Exception
{
    public EtwSessionException()
        : this("The ETW session attempt failed.")
    {
    }

    public EtwSessionException(string message)
        : base(message)
    {
    }

    public EtwSessionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Receives admitted records from the capture callback. Implementations must return promptly and do
/// bounded work only: no query, no name resolution, no blocking I/O, no unpooled allocation (R9, P12).
/// </summary>
public interface IAdmittedEventSink
{
    /// <summary>Called for every delivered record, before any admission decision.</summary>
    void OnObserved();

    /// <summary>Takes ownership of an admitted record. Returns false when the bounded queue is full.</summary>
    bool Admit(in AdmittedEvent admitted);

    void OnOmitted(OmissionReason reason);

    void OnUndecodable(UndecodableReason reason);
}

/// <summary>
/// A session this process created and therefore owns. Stopping affects only this session; no operation
/// here can reach a session created by another tool (section 9.2, P14).
/// </summary>
public interface IOwnedEtwSession : IDisposable
{
    string SessionName { get; }

    ProviderEnablementResult Enable(ProviderEnablementRequest request);

    /// <summary>Requests a provider state rundown after delivery has started (section 18.5).</summary>
    bool TryRequestCaptureState(ProviderEnablementRequest request, out string? failureReason);

    /// <summary>Runs the delivery pump until stop is requested or the token is cancelled. Blocking.</summary>
    void Pump(EventAdmissionTable table, IAdmittedEventSink sink, CancellationToken cancellationToken);

    void RequestStopProcessing();

    SourceLossReading ReadLoss();

    /// <summary>Stops the ETW session this handle created. Safe to call more than once.</summary>
    void StopSession();
}

/// <summary>
/// Creates owned ETW sessions. The adapter depends on this interface so the lifecycle state machine,
/// its cleanup and its non-interference rules are tested without ETW, elevation, or Windows (R19).
/// </summary>
public interface IEtwSessionHost
{
    /// <summary>Null when elevation cannot be determined on this host.</summary>
    bool? IsElevated { get; }

    /// <summary>Names of sessions currently active on the machine. Used to refuse, never to take over (P14).</summary>
    IReadOnlyList<string> ListActiveSessionNames();

    /// <summary>
    /// Creates a session under the plan's unique name. Fails when that exact name already exists rather
    /// than restarting or adopting the existing session (P14).
    /// </summary>
    IOwnedEtwSession CreateExclusive(OwnedSessionPlan plan);
}
