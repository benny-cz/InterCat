using System.IO.Pipes;
using System.Security.Principal;
using InterCat.Capture.Windows;
using InterCat.Domain;
using Xunit;
using static InterCat.CaptureBroker.Tests.BrokerPlanFixture;

namespace InterCat.CaptureBroker.Tests;

public sealed class WindowsBrokerPipeSecurityTests
{
    [Fact]
    public void CurrentProcessIdentityComesFromTokenAndIsStable()
    {
        BrokerClientIdentity first = WindowsBrokerTokenIdentity.ReadCurrentProcess();
        BrokerClientIdentity second = WindowsBrokerTokenIdentity.ReadCurrentProcess();

        Assert.Null(first.Validate());
        Assert.True(first.Owner.Matches(second.Owner));
        Assert.Equal(first.UserSid, WindowsBrokerTokenIdentity.CanonicalizeSid(first.UserSid));
        Assert.True(first.IntegrityLevel >= 0x1000);
    }

    [Fact]
    public void MalformedSidIsRefusedBeforeNativeObjectCreation()
    {
        Assert.Throws<ArgumentException>(() => WindowsBrokerTokenIdentity.CanonicalizeSid("S-1-not-a-sid"));
    }

    [Fact]
    public void PipeDescriptorNamesOnlySystemAndExpectedUser()
    {
        BrokerClientIdentity current = WindowsBrokerTokenIdentity.ReadCurrentProcess();
        using WindowsBrokerPipeServerInstance server = WindowsBrokerPipeServerInstance.Create(
            Guid.NewGuid(),
            current.Owner);

        Assert.StartsWith("D:P", server.SecurityDescriptorSddl, StringComparison.Ordinal);
        Assert.Contains($";;;{current.UserSid})", server.SecurityDescriptorSddl, StringComparison.Ordinal);
        Assert.Contains(";;;SY)", server.SecurityDescriptorSddl, StringComparison.Ordinal);
        Assert.DoesNotContain(";;;WD)", server.SecurityDescriptorSddl, StringComparison.Ordinal);
        Assert.DoesNotContain(";;;AN)", server.SecurityDescriptorSddl, StringComparison.Ordinal);
        Assert.DoesNotContain(";;;AU)", server.SecurityDescriptorSddl, StringComparison.Ordinal);
        Assert.DoesNotContain(";;;BA)", server.SecurityDescriptorSddl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedirectorStyleClientIsRejected()
    {
        BrokerClientIdentity current = WindowsBrokerTokenIdentity.ReadCurrentProcess();
        await using WindowsBrokerPipeServerInstance server = WindowsBrokerPipeServerInstance.Create(
            Guid.NewGuid(),
            current.Owner);
        await using var remoteStyleClient = new NamedPipeClientStream(
            Environment.MachineName,
            server.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation);

        await Assert.ThrowsAnyAsync<Exception>(async () => await remoteStyleClient.ConnectAsync(1_000));
        Assert.False(server.IsConnected);
    }

    [Fact]
    public void ExistingPipeNamePreventsFirstInstanceCreation()
    {
        BrokerClientIdentity current = WindowsBrokerTokenIdentity.ReadCurrentProcess();
        Guid serverInstanceId = Guid.NewGuid();
        string pipeName = $"InterCat.Broker.v1.{serverInstanceId:N}";
        using var squatter = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        IOException failure = Assert.Throws<IOException>(() =>
            WindowsBrokerPipeServerInstance.Create(serverInstanceId, current.Owner));

        Assert.Contains("first-instance", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectedClientIdentityAndLogonSessionComeFromImpersonatedToken()
    {
        BrokerClientIdentity current = WindowsBrokerTokenIdentity.ReadCurrentProcess();
        await using WindowsBrokerPipeServerInstance server = WindowsBrokerPipeServerInstance.Create(
            Guid.NewGuid(),
            current.Owner);
        await using NamedPipeClientStream client = CreateClient(server.PipeName);

        Task wait = server.WaitForConnectionAsync().AsTask();
        await client.ConnectAsync(5_000);
        await wait;
        BrokerAuthenticatedPipeClient authenticated = server.AuthenticateConnectedClient();

        Assert.True(current.Owner.Matches(authenticated.Identity.Owner));
        Assert.Equal((uint)Environment.ProcessId, authenticated.DiagnosticProcessId);
        Assert.Equal(current.IntegrityLevel, authenticated.Identity.IntegrityLevel);
        Assert.Equal(current.IsElevated, authenticated.Identity.IsElevated);
    }

    [Fact]
    public async Task MatchingSidFromDifferentExpectedLogonSessionIsRejected()
    {
        BrokerClientIdentity current = WindowsBrokerTokenIdentity.ReadCurrentProcess();
        BrokerOwnerIdentity wrongSession = current.Owner with
        {
            LogonSessionId = current.LogonSessionId == ulong.MaxValue
                ? current.LogonSessionId - 1
                : current.LogonSessionId + 1,
        };
        await using WindowsBrokerPipeServerInstance server = WindowsBrokerPipeServerInstance.Create(
            Guid.NewGuid(),
            wrongSession);
        await using NamedPipeClientStream client = CreateClient(server.PipeName);
        Task wait = server.WaitForConnectionAsync().AsTask();
        await client.ConnectAsync(5_000);
        await wait;

        Assert.Throws<UnauthorizedAccessException>(() => server.AuthenticateConnectedClient());
    }

    [Fact]
    public async Task AuthenticatedPipeRunsHelloThroughBoundedDispatcherLoop()
    {
        BrokerClientIdentity current = WindowsBrokerTokenIdentity.ReadCurrentProcess();
        Guid serverInstanceId = Guid.NewGuid();
        await using WindowsBrokerPipeServerInstance server = WindowsBrokerPipeServerInstance.Create(
            serverInstanceId,
            current.Owner);
        await using NamedPipeClientStream client = CreateClient(server.PipeName);
        Task wait = server.WaitForConnectionAsync().AsTask();
        await client.ConnectAsync(5_000);
        await wait;
        BrokerAuthenticatedPipeClient authenticated = server.AuthenticateConnectedClient();
        var registry = new PreparedPlanRegistry();
        using var preparation = new BrokerPreparationCoordinator(new UnusedPlanSource(), registry, Runtime);
        using var lifecycle = new BrokerLifecycleCoordinator(registry, new InMemoryBrokerLifecycleStore(), new BrokerFakeRuntime());
        var dispatcher = new BrokerConnectionDispatcher(
            authenticated.Identity,
            preparation,
            lifecycle,
            serverInstanceId,
            "fixture-1");
        Task processing = BrokerPipeConnectionProcessor.ProcessAsync(server.Stream, dispatcher);
        Guid correlationId = Guid.NewGuid();
        BrokerWireFrame hello = BrokerWireRequestCodec.Encode(
            new BrokerHelloRequest(
                Guid.NewGuid(),
                1,
                1,
                BrokerHelloNegotiator.SupportedFeatures,
                BrokerProtocolFeature.PreparedPlanDigest),
            correlationId);

        await BrokerWireFrameCodec.WriteAsync(client, hello);
        await client.FlushAsync();
        BrokerWireFrame response = Assert.IsType<BrokerWireFrame>(await BrokerWireFrameCodec.ReadAsync(client));
        await client.DisposeAsync();
        await processing.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(correlationId, response.CorrelationId);
        BrokerHelloResponse negotiated = Assert.IsType<BrokerHelloResponse>(BrokerWireResponseCodec.Decode(response));
        Assert.Equal(serverInstanceId, negotiated.ServerInstanceId);
    }

    private static NamedPipeClientStream CreateClient(string pipeName) =>
        new(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation);

    private sealed class UnusedPlanSource : IBrokerCapturePlanSource
    {
        public ValueTask<CapabilityReport> ProbeAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Hello must not probe capabilities.");

        public ValueTask<EffectiveCapturePlan> CompileAsync(
            CaptureProfileRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Hello must not compile a capture plan.");
    }
}
