using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace InterCat.Remote.Tests;

/// <summary>A clock a test moves by hand.</summary>
internal sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>An agent listening on loopback under a key of its own, with its enrollments, audit and clock.</summary>
internal sealed class AgentFixture : IAsyncDisposable
{
    private AgentFixture(RemoteIdentity identity, RemoteAgentServer server, IPEndPoint endpoint, AgentEnrollments enrollments,
        RemoteAuditLog audit, ManualTime time, RemoteAgentHost host)
    {
        Identity = identity;
        Server = server;
        Endpoint = endpoint;
        Enrollments = enrollments;
        Audit = audit;
        Time = time;
        Host = host;
    }

    public RemoteIdentity Identity { get; }
    public RemoteAgentServer Server { get; }
    public IPEndPoint Endpoint { get; }
    public AgentEnrollments Enrollments { get; }
    public RemoteAuditLog Audit { get; }
    public ManualTime Time { get; }
    public RemoteAgentHost Host { get; }

    public static AgentFixture Start(RemoteAgentPolicy? policy = null, RemoteAgentLimits? limits = null)
    {
        var time = new ManualTime();
        var identity = RemoteIdentity.Create("agent");
        var enrollments = new AgentEnrollments(time);
        var audit = new RemoteAuditLog();
        var host = new RemoteAgentHost(Guid.NewGuid(), "test-host", "Windows (test)", "1.0.0-test");
        var server = new RemoteAgentServer(identity, enrollments, host, policy, audit, limits, time);
        IPEndPoint endpoint = server.Start(new IPEndPoint(IPAddress.Loopback, 0));
        return new(identity, server, endpoint, enrollments, audit, time, host);
    }

    /// <summary>A controller's connection to this agent, expecting its own pin.</summary>
    public Task<RemoteAgentConnection> ConnectAsync(RemoteIdentity controller) =>
        RemoteAgentConnection.ConnectAsync(Endpoint, controller, Identity.Pin, TimeSpan.FromSeconds(10), CancellationToken.None);

    /// <summary>
    /// A TLS stream to this agent under <paramref name="controller"/>'s key that writes whatever bytes a test gives it, the
    /// agent's key checked as a controller checks it.
    /// </summary>
    public async Task<(TcpClient Client, SslStream Stream)> RawAsync(RemoteIdentity controller)
    {
        var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(Endpoint);
        var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        RemotePin agent = Identity.Pin;
        await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "intercat-agent",
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            ClientCertificates = [controller.Certificate],
            LocalCertificateSelectionCallback = (_, _, _, _, _) => controller.Certificate,
            RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null && RemotePin.Of(certificate) == agent,
        });
        return (client, stream);
    }

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        Identity.Dispose();
    }
}
