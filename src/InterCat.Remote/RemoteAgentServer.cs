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
/// pin of its key, and answers the version 1.6 messages alone. A key not enrolled may say hello and enroll with a token;
/// everything else asks an enrolled, unrevoked key, within its grants and the agent's own policy. Every request that
/// changes something, or begins a fetch, is audited.
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
    private readonly RemoteCaptures? captures;
    private readonly RemotePin? nextKey;
    private readonly IRemoteCapturePreview? preview;
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
        TimeProvider? time = null,
        RemoteCaptures? captures = null,
        RemotePin? nextKey = null,
        IRemoteCapturePreview? preview = null)
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
        this.captures = captures;
        this.nextKey = nextKey;
        this.preview = preview;
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

            (RemoteFrame response, bool ends) = await AnswerAsync(request, controller, cancellationToken).ConfigureAwait(false);
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
    private async Task<(RemoteFrame Answer, bool Ends)> AnswerAsync(RemoteFrame request, RemotePin controller, CancellationToken cancellationToken)
    {
        if ((request.Flags & RemoteFrameAttributes.Request) == 0)
        {
            return (Refuse(request, controller, RemoteErrorCode.Malformed, "The agent answers requests."), true);
        }

        RemoteFrame answer;
        try
        {
            answer = request.Type switch
            {
                RemoteMessageType.Hello => Hello(request, controller),
                RemoteMessageType.Enroll => Enroll(request, controller),
                RemoteMessageType.Inventory => Inventory(request, controller),
                RemoteMessageType.Rotate => Rotate(request, controller),
                RemoteMessageType.PrepareCapture or RemoteMessageType.StartCapture or RemoteMessageType.CaptureStatus
                    or RemoteMessageType.RenewCapture or RemoteMessageType.StopCapture
                    => await CaptureAsync(request, controller, cancellationToken).ConfigureAwait(false),
                RemoteMessageType.ListCapture or RemoteMessageType.ReadCaptureFile
                    => await FetchAsync(request, controller, cancellationToken).ConfigureAwait(false),
                RemoteMessageType.PreviewCapture => await PreviewAsync(request, controller, cancellationToken).ConfigureAwait(false),
                _ => Refuse(request, controller, RemoteErrorCode.Unsupported,
                    $"Message type {(ushort)request.Type} is not one this agent answers."),
            };
        }
        catch (InvalidDataException exception)
        {
            return (Refuse(request, controller, RemoteErrorCode.Malformed, exception.Message), true);
        }

        return (answer, answer.IsError && request.Type == RemoteMessageType.Enroll);
    }

    /// <summary>
    /// A capture request (`remote-agent-v1` §4.4), from an enrolled key holding metadata capture within the agent's policy,
    /// where the agent serves captures; each answer audited with what it did.
    /// </summary>
    private async Task<RemoteFrame> CaptureAsync(RemoteFrame request, RemotePin controller, CancellationToken cancellationToken)
    {
        if (captures is null)
        {
            return Refuse(request, controller, RemoteErrorCode.Unsupported, "This agent serves no capture.");
        }

        (EnrolledController? admitted, RemoteFrame? refusal) = Admitted(request, controller);
        if (refusal is not null)
        {
            return refusal;
        }

        if (!policy.Effective(admitted!.Grants).HasFlag(RemoteGrants.MetadataCapture))
        {
            return Refuse(request, controller, RemoteErrorCode.NotPermitted,
                "This controller was not granted metadata capture here, or this agent does not allow it.");
        }

        // The request is read before the host is asked anything, so a request version 1 would not write is refused as such,
        // and a failure of the host's own is said as the host's.
        RemoteCaptureRequest? asked = request.Type == RemoteMessageType.PrepareCapture ? RemoteCaptureRequest.Decode(request.Payload) : null;
        RemoteStartRequest? starting = request.Type == RemoteMessageType.StartCapture ? RemoteStartRequest.Decode(request.Payload) : null;
        Guid capture = asked is null && starting is null ? RemoteCaptureReference.Decode(request.Payload).CaptureId : Guid.Empty;
        try
        {
            switch (request.Type)
            {
                case RemoteMessageType.PrepareCapture:
                    RemotePreparedCapture prepared = await captures.PrepareAsync(controller, asked!, cancellationToken).ConfigureAwait(false);
                    Record(controller, request.Type, $"prepared: {prepared.Profile}, {prepared.Seconds} s, {prepared.JournalMebibytes} MiB");
                    return Respond(request, prepared.Encode());
                case RemoteMessageType.StartCapture:
                    RemoteCaptureStarted started = await captures.StartAsync(controller, starting!, cancellationToken).ConfigureAwait(false);
                    Record(controller, request.Type, $"started: {started.CaptureId:D}");
                    return Respond(request, started.Encode());
                default:
                    RemoteCaptureState state = request.Type switch
                    {
                        RemoteMessageType.CaptureStatus => await captures.StatusAsync(controller, capture, cancellationToken).ConfigureAwait(false),
                        RemoteMessageType.RenewCapture => await captures.RenewAsync(controller, capture, cancellationToken).ConfigureAwait(false),
                        _ => await captures.StopAsync(controller, capture, cancellationToken).ConfigureAwait(false),
                    };
                    // Status and renewal come every few seconds while a capture runs; the audit keeps what changed it.
                    if (request.Type == RemoteMessageType.StopCapture)
                    {
                        Record(controller, request.Type, $"answered: {state.State.ToString().ToLowerInvariant()}");
                    }

                    return Respond(request, state.Encode());
            }
        }
        catch (RemoteRequestRefusedException refused)
        {
            return Refuse(request, controller, refused.Code, refused.Message);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ObjectDisposedException or TimeoutException)
        {
            return Refuse(request, controller, RemoteErrorCode.Failed, "The host's capture broker failed: " + exception.Message);
        }
    }

    /// <summary>
    /// A capture request previewed on the agent's host (`remote-agent-v1` §4.10), for an enrolled key holding metadata capture
    /// within the agent's policy: the host's preview, compiled from its own schemas and starting nothing, and whether the
    /// agent would prepare the request as asked - one that keeps content only for a key granted contents where the agent's
    /// policy keeps them, and the host never at this version - said in the agent's words and audited.
    /// </summary>
    private async Task<RemoteFrame> PreviewAsync(RemoteFrame request, RemotePin controller, CancellationToken cancellationToken)
    {
        if (preview is null)
        {
            return Refuse(request, controller, RemoteErrorCode.Unsupported, "This agent previews no capture.");
        }

        (EnrolledController? admitted, RemoteFrame? refusal) = Admitted(request, controller);
        if (refusal is not null)
        {
            return refusal;
        }

        RemoteGrants granted = admitted!.Grants;
        if (!policy.Effective(granted).HasFlag(RemoteGrants.MetadataCapture))
        {
            return Refuse(request, controller, RemoteErrorCode.NotPermitted,
                "This controller was not granted metadata capture here, or this agent does not allow it.");
        }

        RemotePreviewRequest asked = RemotePreviewRequest.Decode(request.Payload);
        RemotePreviewed previewed;
        try
        {
            previewed = await Task.Run(() => preview.Preview(asked), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            return Refuse(request, controller, RemoteErrorCode.Failed, "The host could not compile its preview: " + exception.Message);
        }

        if (previewed.Preview is { } document && System.Text.Encoding.UTF8.GetByteCount(document) > RemoteCapturePreview.MostPreviewBytes)
        {
            return Refuse(request, controller, RemoteErrorCode.Failed, "The host's preview is larger than one answer holds.");
        }

        // Content is the agent's to allow: a key not granted it, or an agent whose policy keeps none, is told so first.
        string? reason = previewed switch
        {
            { KeepsContent: true } when !granted.HasFlag(RemoteGrants.Contents) => "This controller was not granted contents here.",
            { KeepsContent: true } when !policy.Allowed.HasFlag(RemoteGrants.Contents) => "This agent's policy keeps no contents.",
            { Refusal: { } refused } => refused,
            _ => captures is null ? "This agent records no capture." : captures.BoundsProblem(asked.Seconds, asked.JournalMebibytes),
        };
        // The audit names the catalog's profile, never the request's own words, and no reason, which may name a process
        // or a channel (P16).
        Record(controller, request.Type, $"previewed: {previewed.Profile ?? "no profile"}; "
            + (reason is null ? "would prepare it" : "would not prepare it"));
        return Respond(request, new RemoteCapturePreview(reason is null, reason, previewed.Preview).Encode());
    }

    /// <summary>
    /// A capture's evidence listed or read (`remote-agent-v1` §4.5), for an enrolled key holding download within the agent's
    /// policy, of a capture that key started; a fetch's first listing is audited, its reads are not.
    /// </summary>
    private async Task<RemoteFrame> FetchAsync(RemoteFrame request, RemotePin controller, CancellationToken cancellationToken)
    {
        if (captures is not { ServesEvidence: true })
        {
            return Refuse(request, controller, RemoteErrorCode.Unsupported, "This agent serves no evidence.");
        }

        (EnrolledController? admitted, RemoteFrame? refusal) = Admitted(request, controller);
        if (refusal is not null)
        {
            return refusal;
        }

        if (!policy.Effective(admitted!.Grants).HasFlag(RemoteGrants.Download))
        {
            return Refuse(request, controller, RemoteErrorCode.NotPermitted,
                "This controller was not granted download here, or this agent does not allow it.");
        }

        RemoteListRequest? listing = request.Type == RemoteMessageType.ListCapture ? RemoteListRequest.Decode(request.Payload) : null;
        RemoteReadRequest? reading = listing is null ? RemoteReadRequest.Decode(request.Payload) : null;
        try
        {
            if (listing is not null)
            {
                RemoteEvidencePage page = await captures.ListAsync(controller, listing, cancellationToken).ConfigureAwait(false);
                if (listing.First == 0)
                {
                    Record(controller, request.Type, $"listed: {page.CaptureId:D}, {page.Total} files, {page.TotalBytes} bytes");
                }

                return Respond(request, page.Encode());
            }

            return Respond(request, (await captures.ReadAsync(controller, reading!, cancellationToken).ConfigureAwait(false)).Encode());
        }
        catch (RemoteRequestRefusedException refused)
        {
            return Refuse(request, controller, refused.Code, refused.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // What the host's file system says can name the folder on the agent's host, which the agent never says.
            return Refuse(request, controller, RemoteErrorCode.Failed, "The capture's evidence could not be read on the agent's host.");
        }
    }

    private RemoteFrame Hello(RemoteFrame request, RemotePin controller)
    {
        _ = RemoteFields.Read(request.Payload);
        (EnrolledController? standing, bool revoked) = enrollments.Standing(controller);
        EnrolledController? enrolled = revoked ? null : standing;
        var hello = new RemoteHello(RemoteFrameCodec.Major, RemoteFrameCodec.Minor, enrolled is not null,
            enrolled is null ? null : host.HostId, enrolled is null ? RemoteGrants.None : policy.Effective(enrolled.Grants),
            enrolled is null ? null : nextKey);
        Record(controller, RemoteMessageType.Hello, enrolled is null ? "answered: not enrolled" : "answered" + TellNext(controller));
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

        Record(controller, RemoteMessageType.Enroll, "admitted: " + string.Join(",", admitted.Grants.Names())
            + (admitted.RotatedFrom is { } replaced ? $"; replaces {replaced.Abbreviated}, revoked" : string.Empty)
            + TellNext(controller));
        return Respond(request, new RemoteEnrolled(host.HostId, policy.Effective(admitted.Grants), nextKey).Encode());
    }

    /// <summary>
    /// A key's rotation (`remote-agent-v1` §4.8): an enrolled, unrevoked key is given a secret its controller's next key
    /// enrolls with, taking its grants, and admitting that key revokes this one.
    /// </summary>
    private RemoteFrame Rotate(RemoteFrame request, RemotePin controller)
    {
        _ = RemoteFields.Read(request.Payload);
        if (Admitted(request, controller).Refusal is { } refusal)
        {
            return refusal;
        }

        // Revoked as it asked, between the check and the issue: refused as any revoked key is.
        if (enrollments.IssueRotation(controller) is not { } rotation)
        {
            return Refuse(request, controller, RemoteErrorCode.Revoked, "This key was revoked on the agent's host.");
        }

        (byte[] secret, DateTimeOffset expires) = rotation;
        Record(controller, RemoteMessageType.Rotate, "issued: its next key enrolls in its place within 10 minutes");
        return Respond(request, new RemoteRotation(secret, expires).Encode());
    }

    private RemoteFrame Inventory(RemoteFrame request, RemotePin controller)
    {
        _ = RemoteFields.Read(request.Payload);
        if (Admitted(request, controller).Refusal is { } refusal)
        {
            return refusal;
        }

        Record(controller, RemoteMessageType.Inventory, "answered");
        return Respond(request, new RemoteInventory(host.HostId, host.MachineName, host.OperatingSystem, host.InterCatVersion,
            ServesCapture: captures is not null).Encode());
    }

    /// <summary>
    /// While the agent's own key's rotation is under way, keeps that <paramref name="controller"/> is told its next key, and
    /// says so in the audit's words the first time.
    /// </summary>
    private string TellNext(RemotePin controller) =>
        nextKey is { } next && enrollments.Told(controller, next) ? $"; told its next key, {next.Abbreviated}" : string.Empty;

    /// <summary>
    /// The enrollment a request is admitted by, read once for it - or the refusal a key not enrolled, or revoked, receives,
    /// with no enrollment.
    /// </summary>
    private (EnrolledController? Enrolled, RemoteFrame? Refusal) Admitted(RemoteFrame request, RemotePin controller)
    {
        (EnrolledController? enrolled, bool revoked) = enrollments.Standing(controller);
        return revoked
            ? (null, Refuse(request, controller, RemoteErrorCode.Revoked, "This key was revoked on the agent's host."))
            : enrolled is null
                ? (null, Refuse(request, controller, RemoteErrorCode.NotEnrolled, "This key is not enrolled with the agent."))
                : (enrolled, null);
    }

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
        RemoteErrorCode.Failed => "failed",
        _ => "unknown",
    };
}
