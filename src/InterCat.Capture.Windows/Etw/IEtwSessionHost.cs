using InterCat.Domain;

namespace InterCat.Capture.Windows;

/// <summary>
/// One delivered record's descriptor and native reading, as the callback saw it before any admission decision. A sink
/// that keeps a coverage ledger attributes every outcome to it (`contracts/coverage-v1.md` §3); the reading is the
/// record's own clock value, never a relative time (I8).
/// </summary>
public readonly record struct DeliveredRecord(Guid ProviderId, int EventId, int Version, long NativeTicks);

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
    void OnObserved(in DeliveredRecord delivered);

    /// <summary>Takes ownership of an admitted record. Returns false when the bounded queue is full.</summary>
    bool Admit(in AdmittedEvent admitted);

    /// <summary>The record was delivered and the policy did not admit it. An omission is not a loss (section 20.6).</summary>
    void OnOmitted(OmissionReason reason, in DeliveredRecord delivered);

    /// <summary>The record could not be decoded, including a delivered version outside the admitted schema (section 18.3).</summary>
    void OnUndecodable(UndecodableReason reason, in DeliveredRecord delivered);

    /// <summary>
    /// Reports what one completed callback cost and what it copied. The default does nothing, so a sink
    /// that does not measure overhead is unchanged; a measuring sink gets the cost at the only place it
    /// can be attributed to the callback rather than to the process (section 12).
    /// </summary>
    void OnCallbackCompleted(in CallbackCost cost)
    {
    }
}

/// <summary>
/// Observes every delivery outcome with its descriptor, for a coverage ledger (`contracts/coverage-v1.md` §3). It is
/// called from the capture callback, so its work must be bounded and allocation-free once a descriptor has been seen
/// (R9, P12). A record the policy admitted but the bounded queue could not take is reported as not queued: it was lost
/// after admission, not admitted.
/// </summary>
public interface IDeliveryObserver
{
    void Delivered(in DeliveredRecord delivered);

    void Admitted(Guid providerId, int eventId, int version, bool queued);

    void Omitted(OmissionReason reason, in DeliveredRecord delivered);

    void Undecodable(UndecodableReason reason, in DeliveredRecord delivered);
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

    /// <summary>
    /// Asks ETW to deliver the session's partly filled buffers now. Nothing is lost or reordered by it; records only
    /// arrive sooner. False when the adapter cannot request it, in which case delivery waits for ETW's own flush.
    /// </summary>
    bool TryFlushDelivery() => false;

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

/// <summary>
/// Reclaims an ETW session after the process that created it died. The caller must first verify the
/// name and token against its protected, durable ownership record; this adapter also checks the name's
/// token shape and never creates or restarts a session during recovery.
/// </summary>
public interface IEtwSessionReclaimer
{
    /// <summary>Returns false only when the exact session was already absent.</summary>
    bool StopPreviouslyOwnedSession(string sessionName, Guid ownershipToken);
}
