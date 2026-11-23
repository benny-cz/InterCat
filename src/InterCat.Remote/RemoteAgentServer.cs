using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace InterCat.Remote;

/// <summary>What an agent says of its host to an enrolled controller.</summary>
public sealed record RemoteAgentHost(Guid HostId, string MachineName, string OperatingSystem, string InterCatVersion);

/// <summary>How much an agent serves, and how long it waits (`remote-agent-v1` §8).</summary>
public sealed record RemoteAgentLimits(int Connections, TimeSpan Handshake, TimeSpan Idle)
{
    public static RemoteAgentLimits Default { get; } = new(8, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60));
}

/// <summary>
/// A remote agent's endpoint (`remote-agent-v1`): it listens where its administrator configured it, on no other address,
/// secures each connection with TLS under its own key, requires the controller's certificate, knows the controller by the
/// pin of its key, and answers the version 1.0 messages alone. A key not enrolled may say hello and enroll with a token;
/// everything else asks an enrolled, unrevoked key, within its grants and the agent's own policy. Every request is audited.
/// </summary>
public sealed class RemoteAgentServer : IAsyncDisposable
{
    private readonly RemoteIdentity identity;
    private readonly AgentEnrollments enrollments;
    private readonly RemoteAgentHost host;
    private readonly RemoteAgentPolicy policy;
    private readonly IRemoteAudit audit;
    private readonly RemoteAgentLimits limits;
    private readonly TimeProvider time;
    private readonly CancellationTokenSource stopping = new();
    private readonly Lock gate = new();
    private readonly HashSet<Task> connections = [];
    private TcpListener? listener;
    private Task? accepting;
    private bool disposed;

    public RemoteAgentServer(
        RemoteIdentity identity,
        AgentEnrollments enrollments,
        RemoteAgentHost host,
        RemoteAgentPolicy? policy = null,
        IRemoteAudit? audit = null,
        RemoteAgentLimits? limits = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(enrollments);
        ArgumentNullException.ThrowIfNull(host);
        this.identity = identity;
        this.enrollments = enrollments;
        this.host = host;
        this.policy = policy ?? RemoteAgentPolicy.Default;
        this.audit = audit ?? new RemoteAuditLog();
        this.limits = limits ?? RemoteAgentLimits.Default;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Starts listening at <paramref name="endpoint"/> and returns where: port 0 takes a port the system chooses.</summary>
    public IPEndPoint Start(IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        lock (gate)
        {
            if (listener is not null)
            {
                throw new InvalidOperationException("The agent is already listening.");
            }

            listener = new TcpListener(endpoint);
            listener.Start();
            accepting = AcceptAsync(listener, stopping.Token);
            return (IPEndPoint)listener.LocalEndpoint;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        await stopping.CancelAsync().ConfigureAwait(false);
        listener?.Stop();
        if (accepting is not null)
        {
            await accepting.ConfigureAwait(false);
        }

        Task[] open;
        lock (gate)
        {
            open = [.. connections];
        }

        await Task.WhenAll(open).ConfigureAwait(false);
        stopping.Dispose();
    }

    private async Task AcceptAsync(TcpListener listening, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket connection;
            try
            {
                connection = await listening.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            lock (gate)
            {
                if (connections.Count >= limits.Connections)
                {
                    // Before TLS nothing can be said securely: a connection past the limit is closed unanswered.
                    connection.Dispose();
                    continue;
                }

                Task serving = ServeAsync(connection, cancellationToken);
                connections.Add(serving);
                _ = serving.ContinueWith(done =>
                {
                    lock (gate)
                    {
                        connections.Remove(done);
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private async Task ServeAsync(Socket connection, CancellationToken cancellationToken)
    {
        await Task.Yield();
        using (connection)
        {
            var tls = new SslStream(new NetworkStream(connection, ownsSocket: false), leaveInnerStreamOpen: false);
            await using (tls.ConfigureAwait(false))
            {
                RemotePin controller;
                try
                {
                    using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    handshake.CancelAfter(limits.Handshake);
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = identity.Certificate,
                        ClientCertificateRequired = true,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,

                        // Any key may connect, to enroll; what it may then do, its pin decides, not a chain or a name.
                        RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null,
                    }, handshake.Token).ConfigureAwait(false);
                    controller = tls.RemoteCertificate is { } presented ? RemotePin.Of(presented) : default;
                }
                catch (Exception exception) when (exception is AuthenticationException or IOException or OperationCanceledException or SocketException)
                {
                    return;
                }

                if (controller.IsEmpty)
                {
                    return;
                }

                await ConverseAsync(tls, controller, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ConverseAsync(SslStream tls, RemotePin controller, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            RemoteFrame? request;
            try
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idle.CancelAfter(limits.Idle);
                request = await RemoteFrameCodec.ReadAsync(tls, idle.Token).ConfigureAwait(false);
            }
            catch (InvalidDataException exception)
            {
                // A frame not version 1's ends the connection, after the agent says why.
                Record(controller, RemoteMessageType.Error, "refused: malformed");
                await TryRefuseAsync(tls, RemoteErrorCode.Malformed, exception.Message, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            if (request is null)
            {
                return;
            }

            RemoteFrame response = Answer(request, controller, out bool ends);
            try
            {
                await RemoteFrameCodec.WriteAsync(tls, response, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            if (ends)
            {
                return;
            }
        }
    }

    /// <summary>
    /// The answer to <paramref name="request"/>, and whether the connection then ends: after a request version 1 would not
    /// write, and after a refused enrollment - a token is not guessed one connection at a time.
    /// </summary>
    private RemoteFrame Answer(RemoteFrame request, RemotePin controller, out bool ends)
    {
        ends = true;
        if ((request.Flags & RemoteFrameAttributes.Request) == 0)
        {
            return Refuse(request, controller, RemoteErrorCode.Malformed, "The agent answers requests.");
        }

        RemoteFrame answer;
        try
        {
            answer = request.Type switch
            {
                RemoteMessageType.Hello => Hello(request, controller),
                RemoteMessageType.Enroll => Enroll(request, controller),
                RemoteMessageType.Inventory => Inventory(request, controller),
                _ => Refuse(request, controller, RemoteErrorCode.Unsupported,
                    $"Message type {(ushort)request.Type} is not one this agent answers."),
            };
        }
        catch (InvalidDataException exception)
        {
            return Refuse(request, controller, RemoteErrorCode.Malformed, exception.Message);
        }

        ends = answer.IsError && request.Type == RemoteMessageType.Enroll;
        return answer;
    }

    private RemoteFrame Hello(RemoteFrame request, RemotePin controller)
    {
        _ = RemoteFields.Read(request.Payload);
        EnrolledController? enrolled = enrollments.IsRevoked(controller) ? null : enrollments.Find(controller);
        var hello = new RemoteHello(RemoteFrameCodec.Major, RemoteFrameCodec.Minor, enrolled is not null,
            enrolled is null ? null : host.HostId, enrolled is null ? RemoteGrants.None : policy.Effective(enrolled.Grants));
        Record(controller, RemoteMessageType.Hello, enrolled is null ? "answered: not enrolled" : "answered");
        return Respond(request, hello.Encode());
    }

    private RemoteFrame Enroll(RemoteFrame request, RemotePin controller)
    {
        byte[] secret = RemoteFields.Read(request.Payload).ReadBytes(1, EnrollmentToken.SecretLength);
        if (enrollments.IsRevoked(controller))
        {
            return Refuse(request, controller, RemoteErrorCode.Revoked,
                "This key was revoked on the agent's host; a controller enrolls again with a new key.");
        }

        if (enrollments.Admit(secret, controller) is not { } admitted)
        {
            return Refuse(request, controller, RemoteErrorCode.TokenRefused,
                "The agent knows no unexpired, unused token with this secret.");
        }

        Record(controller, RemoteMessageType.Enroll, "admitted: " + string.Join(",", admitted.Grants.Names()));
        return Respond(request, new RemoteEnrolled(host.HostId, policy.Effective(admitted.Grants)).Encode());
    }

    private RemoteFrame Inventory(RemoteFrame request, RemotePin controller)
    {
        _ = RemoteFields.Read(request.Payload);
        if (Unadmitted(request, controller) is { } refusal)
        {
            return refusal;
        }

        Record(controller, RemoteMessageType.Inventory, "answered");
        return Respond(request, new RemoteInventory(host.HostId, host.MachineName, host.OperatingSystem, host.InterCatVersion,
            ServesCapture: false).Encode());
    }

    /// <summary>The refusal a request from a key not enrolled, or revoked, receives; null where the key is admitted.</summary>
    private RemoteFrame? Unadmitted(RemoteFrame request, RemotePin controller) =>
        enrollments.IsRevoked(controller)
            ? Refuse(request, controller, RemoteErrorCode.Revoked, "This key was revoked on the agent's host.")
            : enrollments.Find(controller) is null
                ? Refuse(request, controller, RemoteErrorCode.NotEnrolled, "This key is not enrolled with the agent.")
                : null;

    private static RemoteFrame Respond(RemoteFrame request, byte[] payload) =>
        new(request.Type, RemoteFrameAttributes.Response, request.CorrelationId, payload);

    private RemoteFrame Refuse(RemoteFrame request, RemotePin controller, RemoteErrorCode code, string message)
    {
        Record(controller, Enum.IsDefined(request.Type) ? request.Type : RemoteMessageType.Error, "refused: " + Words(code));
        return new(RemoteMessageType.Error, RemoteFrameAttributes.Response | RemoteFrameAttributes.Error, request.CorrelationId,
            new RemoteRefusal(code, message).Encode());
    }

    /// <summary>Says why the connection ends, where the stream still takes it; a frame it could not read has no correlation.</summary>
    private static async Task TryRefuseAsync(Stream stream, RemoteErrorCode code, string message, CancellationToken cancellationToken)
    {
        try
        {
            await RemoteFrameCodec.WriteAsync(stream, new RemoteFrame(RemoteMessageType.Error,
                RemoteFrameAttributes.Response | RemoteFrameAttributes.Error, Guid.Empty, new RemoteRefusal(code, message).Encode()),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // The connection ends either way.
        }
    }

    private void Record(RemotePin controller, RemoteMessageType action, string outcome) =>
        audit.Record(new RemoteAuditEntry(time.GetUtcNow(), controller, action, outcome));

    /// <summary>A refusal's code in the words the audit and the contract use.</summary>
    public static string Words(RemoteErrorCode code) => code switch
    {
        RemoteErrorCode.Malformed => "malformed",
        RemoteErrorCode.Unsupported => "unsupported",
        RemoteErrorCode.NotEnrolled => "not-enrolled",
        RemoteErrorCode.TokenRefused => "token-refused",
        RemoteErrorCode.Revoked => "revoked",
        RemoteErrorCode.NotPermitted => "not-permitted",
        RemoteErrorCode.Busy => "busy",
        _ => "unknown",
    };
}
