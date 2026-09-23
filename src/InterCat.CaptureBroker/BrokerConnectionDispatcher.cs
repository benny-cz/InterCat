using InterCat.Domain;
using System.Text;

namespace InterCat.CaptureBroker;

/// <summary>
/// Per-connection, transport-independent command state. A host must construct this only
/// after deriving <see cref="BrokerClientIdentity"/> from the connected process token.
/// </summary>
public sealed class BrokerConnectionDispatcher
{
    private readonly BrokerClientIdentity client;
    private readonly BrokerPreparationCoordinator preparation;
    private readonly BrokerLifecycleCoordinator lifecycle;
    private readonly Guid serverInstanceId;
    private readonly string serverVersion;
    private readonly Func<CaptureId, string?>? evidenceDirectory;
    private readonly Lock stateGate = new();
    private readonly HashSet<Guid> inFlight = [];
    private ConnectionState state;

    public BrokerConnectionDispatcher(
        BrokerClientIdentity client,
        BrokerPreparationCoordinator preparation,
        BrokerLifecycleCoordinator lifecycle,
        Guid serverInstanceId,
        string serverVersion,
        Func<CaptureId, string?>? evidenceDirectory = null)
    {
        this.evidenceDirectory = evidenceDirectory;
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.preparation = preparation ?? throw new ArgumentNullException(nameof(preparation));
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.serverInstanceId = serverInstanceId != Guid.Empty
            ? serverInstanceId
            : throw new ArgumentException("A non-empty server instance ID is required.", nameof(serverInstanceId));
        ArgumentException.ThrowIfNullOrWhiteSpace(serverVersion);
        if (Encoding.UTF8.GetByteCount(serverVersion) > 128 || serverVersion.Any(char.IsControl))
        {
            throw new ArgumentException("The server version is too long or contains controls.", nameof(serverVersion));
        }

        this.serverVersion = serverVersion;
        string? identityProblem = client.Validate();
        if (identityProblem is not null)
        {
            throw new ArgumentException(identityProblem, nameof(client));
        }
    }

    public async ValueTask<BrokerWireFrame> DispatchAsync(
        BrokerWireFrame frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        bool accepted;
        lock (stateGate)
        {
            accepted = inFlight.Add(frame.CorrelationId);
        }

        if (!accepted)
        {
            return Error(
                frame.CorrelationId,
                BrokerErrorCode.DuplicateCorrelationId,
                "This correlation ID is already in flight on the connection. Wait for its response before reusing it.");
        }

        try
        {
            BrokerWireRequest request;
            try
            {
                request = BrokerWireRequestCodec.Decode(frame);
                BrokerPrepareRequestPolicy.Validate(request);
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
            {
                return Error(
                    frame.CorrelationId,
                    BrokerErrorCode.InvalidRequest,
                    BoundProtocolMessage(exception.Message));
            }

            if (request is BrokerHelloRequest hello)
            {
                return DispatchHello(hello, frame.CorrelationId);
            }

            lock (stateGate)
            {
                if (state != ConnectionState.Ready)
                {
                    return Error(
                        frame.CorrelationId,
                        BrokerErrorCode.HelloRequired,
                        "Complete a compatible Hello exchange before sending broker commands.");
                }
            }

            BrokerWireResponse response = await DispatchCommandAsync(request, cancellationToken).ConfigureAwait(false);
            return BrokerWireResponseCodec.Encode(response, frame.CorrelationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Error(
                frame.CorrelationId,
                BrokerErrorCode.InternalFailure,
                "The broker could not complete the command. No unconfirmed success should be assumed.",
                retryable: true);
        }
        finally
        {
            lock (stateGate)
            {
                inFlight.Remove(frame.CorrelationId);
            }
        }
    }

    private BrokerWireFrame DispatchHello(BrokerHelloRequest request, Guid correlationId)
    {
        lock (stateGate)
        {
            if (state != ConnectionState.New)
            {
                return Error(
                    correlationId,
                    BrokerErrorCode.ProtocolStateConflict,
                    state == ConnectionState.Ready
                        ? "Hello has already completed on this connection. Open a new connection to renegotiate."
                        : "Another Hello exchange is already in progress on this connection.");
            }

            state = ConnectionState.Negotiating;
        }

        BrokerHelloNegotiation negotiation;
        try
        {
            negotiation = BrokerHelloNegotiator.Negotiate(request, serverInstanceId, serverVersion);
        }
        catch
        {
            lock (stateGate)
            {
                state = ConnectionState.New;
            }

            throw;
        }

        lock (stateGate)
        {
            state = negotiation.Accepted ? ConnectionState.Ready : ConnectionState.New;
        }

        return negotiation.Response is not null
            ? BrokerWireResponseCodec.Encode(negotiation.Response, correlationId)
            : Error(
                correlationId,
                BrokerErrorCode.HelloRejected,
                BoundProtocolMessage(negotiation.Refusal ?? "The protocol negotiation was refused."));
    }

    private async ValueTask<BrokerWireResponse> DispatchCommandAsync(
        BrokerWireRequest request,
        CancellationToken cancellationToken) =>
        request switch
        {
            BrokerGetCapabilitiesRequest =>
                await preparation.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false),
            BrokerPrepareCaptureRequest prepare =>
                await preparation.PrepareAsync(prepare, client, cancellationToken).ConfigureAwait(false),
            BrokerStartCaptureRequest start =>
                ToResponse(await lifecycle
                    .StartAsync(start.PreparedToken, start.RequestId, client, cancellationToken)
                    .ConfigureAwait(false)),
            BrokerGetStatusRequest status =>
                ToResponse(
                    status.CaptureId,
                    await lifecycle.GetStatusAsync(status.CaptureId, client, cancellationToken).ConfigureAwait(false)),
            BrokerStopCaptureRequest stop =>
                ToResponse(await lifecycle
                    .StopAsync(stop.CaptureId, stop.RequestId, client, cancellationToken)
                    .ConfigureAwait(false)),
            BrokerRenewOwnerLeaseRequest renew =>
                ToResponse(await lifecycle
                    .RenewOwnerLeaseAsync(renew.CaptureId, client, cancellationToken)
                    .ConfigureAwait(false)),
            _ => throw new InvalidOperationException("A decoded request has no dispatcher command."),
        };

    private static BrokerStartCaptureResponse ToResponse(BrokerStartOutcome outcome) =>
        new(outcome.Code, outcome.CaptureId, outcome.State, outcome.LeaseExpiresAtUtc, outcome.FailureReason);

    private BrokerWireResponse ToResponse(
        CaptureId requestedCaptureId,
        BrokerCaptureOwnership? ownership) =>
        ownership is null
            ? new BrokerErrorResponse(
                BrokerErrorCode.CaptureUnavailable,
                "The capture is unavailable to this authenticated owner.",
                Retryable: false)
            : new BrokerCaptureStatusResponse(
                ownership.CaptureId,
                ownership.State,
                ownership.PlanDigest,
                ownership.CreatedAtUtc,
                ownership.UpdatedAtUtc,
                ownership.LeaseExpiresAtUtc,
                ownership.StopMilestones,
                ownership.FailureReason,
                evidenceDirectory?.Invoke(ownership.CaptureId));

    private static BrokerStopCaptureResponse ToResponse(BrokerStopOutcome outcome) =>
        new(outcome.Code, outcome.CaptureId, outcome.State, outcome.Milestones, outcome.FailureReason);

    private static BrokerRenewOwnerLeaseResponse ToResponse(BrokerLeaseOutcome outcome) =>
        new(outcome.Code, outcome.CaptureId, outcome.State, outcome.LeaseExpiresAtUtc, outcome.FailureReason);

    private static BrokerWireFrame Error(
        Guid correlationId,
        BrokerErrorCode code,
        string message,
        bool retryable = false) =>
        BrokerWireResponseCodec.Encode(new BrokerErrorResponse(code, message, retryable), correlationId);

    private static string BoundProtocolMessage(string message)
    {
        string safe = new string(message.Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Trim();
        if (safe.Length == 0)
        {
            return "The broker request is invalid.";
        }

        const string suffix = "...";
        int length = safe.Length;
        while (length > 0
            && Encoding.UTF8.GetByteCount(safe.AsSpan(0, length)) + suffix.Length > 512)
        {
            length--;
            if (length > 0 && char.IsHighSurrogate(safe[length - 1]))
            {
                length--;
            }
        }

        return length == safe.Length ? safe : safe[..length].TrimEnd() + suffix;
    }

    private enum ConnectionState
    {
        New,
        Negotiating,
        Ready,
    }
}
