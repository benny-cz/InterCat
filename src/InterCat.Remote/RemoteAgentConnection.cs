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
public sealed class RemoteAgentConnection : IAsyncDisposable
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

    /// <summary>The pin of the agent reached, which is the one expected.</summary>
    public RemotePin Agent { get; }

    /// <summary>
    /// Connects to the agent at <paramref name="endpoint"/> as <paramref name="controller"/>, and goes on only where the
    /// agent's key is the one <paramref name="agent"/> names.
    /// </summary>
    /// <exception cref="RemoteAgentException">The agent's key is another, or no agent answered within <paramref name="timeout"/>.</exception>
    public static async Task<RemoteAgentConnection> ConnectAsync(IPEndPoint endpoint, RemoteIdentity controller, RemotePin agent,
        TimeSpan timeout, CancellationToken cancellationToken)
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
                    return presented == agent;
                },
            }, connecting.Token).ConfigureAwait(false);
            var connection = new RemoteAgentConnection(client, tls, agent);
            tls = null;
            return connection;
        }
        catch (Exception exception) when (exception is AuthenticationException or IOException or SocketException or OperationCanceledException)
        {
            tls?.Dispose();
            client.Dispose();
            if (!presented.IsEmpty && presented != agent)
            {
                throw new RemoteAgentException(null,
                    $"The host answering is not the agent expected: its key's pin begins {presented.Abbreviated}, not {agent.Abbreviated}.", exception);
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

    /// <summary>What the agent says of its host.</summary>
    public async Task<RemoteInventory> InventoryAsync(CancellationToken cancellationToken) =>
        RemoteInventory.Decode(await AskAsync(RemoteMessageType.Inventory, [], cancellationToken).ConfigureAwait(false));

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
