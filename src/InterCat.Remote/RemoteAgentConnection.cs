using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace InterCat.Remote;

/// <summary>
/// A controller's connection to one agent (`remote-agent-v1`): TLS under the controller's own key, the agent's key checked
/// against the pin the controller expects before anything is sent - so a token's secret reaches the agent its token names
/// and no other - and then one request at a time.
/// </summary>
public sealed class RemoteAgentConnection : IAsyncDisposable, IRemoteEvidenceAgent
{
    private readonly TcpClient client;
    private readonly SslStream tls;
    private readonly SemaphoreSlim turn = new(1, 1);

    private RemoteAgentConnection(TcpClient client, SslStream tls, RemotePin agent)
    {
        this.client = client;
        this.tls = tls;
        Agent = agent;
    }

    /// <summary>The pin of the agent reached: the one expected, or the next key it told this controller of.</summary>
    public RemotePin Agent { get; }

    /// <summary>
    /// Connects to the agent at <paramref name="endpoint"/> as <paramref name="controller"/>, and goes on only where the
    /// agent's key is the one <paramref name="agent"/> names - or <paramref name="next"/>, the next key that agent told this
    /// controller of while its rotation was under way (`remote-agent-v1` §4.9); <see cref="Agent"/> says which answered.
    /// </summary>
    /// <exception cref="RemoteAgentException">The agent's key is another, or no agent answered within <paramref name="timeout"/>.</exception>
    public static async Task<RemoteAgentConnection> ConnectAsync(IPEndPoint endpoint, RemoteIdentity controller, RemotePin agent,
        TimeSpan timeout, CancellationToken cancellationToken, RemotePin? next = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(controller);
        if (agent.IsEmpty)
        {
            throw new ArgumentException("A controller connects to the agent a pin names.", nameof(agent));
        }

        var client = new TcpClient(endpoint.AddressFamily);
        SslStream? tls = null;
        RemotePin presented = default;
        try
        {
            using var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connecting.CancelAfter(timeout);
            await client.ConnectAsync(endpoint, connecting.Token).ConfigureAwait(false);
            tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                // The name is not what is checked: the agent's key is, against the pin.
                TargetHost = "intercat-agent",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                ClientCertificates = [controller.Certificate],
                LocalCertificateSelectionCallback = (_, _, _, _, _) => controller.Certificate,
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    presented = certificate is null ? default : RemotePin.Of(certificate);
                    return presented == agent || (next is { } told && presented == told);
                },
            }, connecting.Token).ConfigureAwait(false);
            var connection = new RemoteAgentConnection(client, tls, presented);
            tls = null;
            return connection;
        }
        catch (Exception exception) when (exception is AuthenticationException or IOException or SocketException or OperationCanceledException)
        {
            tls?.Dispose();
            client.Dispose();
            if (!presented.IsEmpty && presented != agent && presented != next)
            {
                throw new RemoteAgentException(null,
                    $"The host answering is not the agent expected: its key's pin begins {presented.Abbreviated}, not {agent.Abbreviated}.", exception)
                {
                    Presented = presented,
                };
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new RemoteAgentException(null, "No agent answered as one: " + exception.Message, exception);
        }
    }

    /// <summary>Says hello: the protocol the agent speaks, and whether this controller's key is enrolled there.</summary>
    public async Task<RemoteHello> HelloAsync(CancellationToken cancellationToken) =>
        RemoteHello.Decode(await AskAsync(RemoteMessageType.Hello,
            new RemoteFieldWriter().WriteUInt16(1, RemoteFrameCodec.Major).WriteUInt16(2, RemoteFrameCodec.Minor).ToArray(),
            cancellationToken).ConfigureAwait(false));

    /// <summary>Enrolls this controller's key with <paramref name="token"/>, which must name the agent reached.</summary>
    public async Task<RemoteEnrolled> EnrollAsync(EnrollmentToken token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.Agent != Agent)
        {
            throw new ArgumentException("The token names another agent than the one reached.", nameof(token));
        }

        return RemoteEnrolled.Decode(await AskAsync(RemoteMessageType.Enroll,
            new RemoteFieldWriter().WriteBytes(1, token.Secret).ToArray(), cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Asks the agent for a rotation of this controller's key (`remote-agent-v1` §4.8): the secret this controller's next key
    /// enrolls with, which replaces this key there.
    /// </summary>
    public async Task<RemoteRotation> RotateAsync(CancellationToken cancellationToken) =>
        RemoteRotation.Decode(await AskAsync(RemoteMessageType.Rotate, [], cancellationToken).ConfigureAwait(false));

    /// <summary>What the agent says of its host.</summary>
    public async Task<RemoteInventory> InventoryAsync(CancellationToken cancellationToken) =>
        RemoteInventory.Decode(await AskAsync(RemoteMessageType.Inventory, [], cancellationToken).ConfigureAwait(false));

    /// <summary>Asks the agent to prepare a capture, and returns what its host would record, to be started soon or let go.</summary>
    public async Task<RemotePreparedCapture> PrepareCaptureAsync(RemoteCaptureRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RemotePreparedCapture.Decode(await AskAsync(RemoteMessageType.PrepareCapture, request.Encode(), cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Asks the agent to preview <paramref name="request"/> on its host (`remote-agent-v1` §4.10): its host's preview and
    /// whether it would prepare the request as asked. Nothing is prepared or started.
    /// </summary>
    public async Task<RemoteCapturePreview> PreviewCaptureAsync(RemotePreviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RemoteCapturePreview.Decode(await AskAsync(RemoteMessageType.PreviewCapture, request.Encode(), cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Starts a prepared capture, holding a control lease of <paramref name="lease"/> to be renewed.</summary>
    public async Task<RemoteCaptureStarted> StartCaptureAsync(Guid prepared, TimeSpan lease, CancellationToken cancellationToken) =>
        RemoteCaptureStarted.Decode(await AskAsync(RemoteMessageType.StartCapture,
            new RemoteStartRequest(prepared, checked((ushort)lease.TotalSeconds)).Encode(), cancellationToken).ConfigureAwait(false));

    /// <summary>Where the capture stands.</summary>
    public Task<RemoteCaptureState> CaptureStatusAsync(Guid capture, CancellationToken cancellationToken) =>
        CaptureAsync(RemoteMessageType.CaptureStatus, capture, cancellationToken);

    /// <summary>Renews the capture's control lease, and says where it stands.</summary>
    public Task<RemoteCaptureState> RenewCaptureAsync(Guid capture, CancellationToken cancellationToken) =>
        CaptureAsync(RemoteMessageType.RenewCapture, capture, cancellationToken);

    /// <summary>Stops the capture, and says where it stands.</summary>
    public Task<RemoteCaptureState> StopCaptureAsync(Guid capture, CancellationToken cancellationToken) =>
        CaptureAsync(RemoteMessageType.StopCapture, capture, cancellationToken);

    /// <summary>
    /// A page of the evidence of a capture this controller started: from <paramref name="first"/>, at most
    /// <paramref name="most"/> files (0 for as many as a page holds), of <paramref name="generation"/> (0 for the current one).
    /// </summary>
    public async Task<RemoteEvidencePage> ListCaptureAsync(Guid capture, long generation, uint first, uint most, CancellationToken cancellationToken)
    {
        RemoteEvidencePage page = RemoteEvidencePage.Decode(await AskAsync(RemoteMessageType.ListCapture,
            new RemoteListRequest(capture, generation, first, most).Encode(), cancellationToken).ConfigureAwait(false));
        return page.CaptureId == capture && page.First == first && (generation == 0 || page.Generation == generation)
            ? page
            : throw new RemoteAgentException(RemoteErrorCode.Malformed, "The agent listed another capture, generation or page than the one asked.");
    }

    /// <summary>Bytes of a file the evidence of a capture this controller started lists, from <paramref name="offset"/>.</summary>
    public async Task<RemoteFileBytes> ReadCaptureFileAsync(Guid capture, string name, long offset, uint count, CancellationToken cancellationToken)
    {
        RemoteFileBytes bytes = RemoteFileBytes.Decode(await AskAsync(RemoteMessageType.ReadCaptureFile,
            new RemoteReadRequest(capture, name, offset, count).Encode(), cancellationToken).ConfigureAwait(false));
        return bytes.Offset == offset && bytes.Bytes.Length <= count
            ? bytes
            : throw new RemoteAgentException(RemoteErrorCode.Malformed, "The agent read from another place, or more, than asked.");
    }

    private async Task<RemoteCaptureState> CaptureAsync(RemoteMessageType type, Guid capture, CancellationToken cancellationToken) =>
        RemoteCaptureState.Decode(await AskAsync(type, new RemoteCaptureReference(capture).Encode(), cancellationToken).ConfigureAwait(false));

    /// <summary>Sends one request and returns its answer's payload; a refusal is thrown with its reason.</summary>
    internal async Task<byte[]> AskAsync(RemoteMessageType type, byte[] payload, CancellationToken cancellationToken)
    {
        await turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var correlation = Guid.NewGuid();
            await RemoteFrameCodec.WriteAsync(tls, new RemoteFrame(type, RemoteFrameAttributes.Request, correlation, payload),
                cancellationToken).ConfigureAwait(false);
            RemoteFrame answer = await RemoteFrameCodec.ReadAsync(tls, cancellationToken).ConfigureAwait(false)
                ?? throw new RemoteAgentException(null, "The agent closed the connection without answering.");
            if (answer.IsError)
            {
                RemoteRefusal refusal = RemoteRefusal.Decode(answer.Payload);
                throw new RemoteAgentException(refusal.Code, refusal.Message);
            }

            if (answer.CorrelationId != correlation || answer.Type != type)
            {
                throw new RemoteAgentException(null, "The agent answered another request than the one asked.");
            }

            return answer.Payload;
        }
        catch (InvalidDataException exception)
        {
            throw new RemoteAgentException(RemoteErrorCode.Malformed, "The agent's answer is not one version 1 writes: " + exception.Message, exception);
        }
        finally
        {
            turn.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await tls.DisposeAsync().ConfigureAwait(false);
        client.Dispose();
        turn.Dispose();
    }
}
