using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.CaptureBroker.Tests.BrokerPlanFixture;

namespace InterCat.CaptureBroker.Tests;

[SupportedOSPlatform("windows")]
public sealed class BrokerHostTests
{
    private static readonly BrokerCaptureQuota SmallQuota = new(30, 1_048_576, 16_777_216);

    [Fact]
    public void LaunchOptionsParseACompleteServeRequest()
    {
        Guid instance = Guid.NewGuid();
        BrokerLaunchOptions? options = BrokerLaunchOptions.Parse(
            [
                "serve", "--owner-sid", "s-1-5-21-1000", "--owner-logon-session", "0x3E7AB",
                "--instance", instance.ToString(), "--idle-exit-seconds", "60",
            ],
            out BrokerLaunchParseError? error);

        Assert.Null(error);
        Assert.NotNull(options);
        Assert.Equal(new BrokerOwnerIdentity("S-1-5-21-1000", 0x3E7AB), options.Owner);
        Assert.Equal(instance, options.ServerInstanceId);
        Assert.Equal(TimeSpan.FromSeconds(60), options.IdleExitAfter);
    }

    [Theory]
    [InlineData(BrokerLaunchParseErrorKind.NotServe)]
    [InlineData(BrokerLaunchParseErrorKind.NotServe, "--owner-sid", "S-1-5-21-1")]
    [InlineData(BrokerLaunchParseErrorKind.Invalid, "serve", "--owner-sid", "S-1-5-21-1", "--owner-logon-session", "7", "--instance", "8b1f4c2e-2f7e-4a55-9d7e-3b8c1d7e9a10", "--enable-unqualified-live-capture")]
    [InlineData(BrokerLaunchParseErrorKind.Invalid, "serve", "--owner-sid", "S-1-5-21-1", "--owner-logon-session", "0", "--instance", "8b1f4c2e-2f7e-4a55-9d7e-3b8c1d7e9a10")]
    [InlineData(BrokerLaunchParseErrorKind.Invalid, "serve", "--owner-sid", "not-a-sid", "--owner-logon-session", "7", "--instance", "8b1f4c2e-2f7e-4a55-9d7e-3b8c1d7e9a10")]
    [InlineData(BrokerLaunchParseErrorKind.Invalid, "serve", "--owner-sid", "S-1-5-21-1", "--owner-logon-session", "7", "--instance", "00000000-0000-0000-0000-000000000000")]
    [InlineData(BrokerLaunchParseErrorKind.Invalid, "serve", "--owner-sid", "S-1-5-21-1", "--owner-logon-session", "7")]
    [InlineData(BrokerLaunchParseErrorKind.Invalid, "serve", "--owner-sid", "S-1-5-21-1", "--owner-sid", "S-1-5-21-2", "--owner-logon-session", "7", "--instance", "8b1f4c2e-2f7e-4a55-9d7e-3b8c1d7e9a10")]
    [InlineData(BrokerLaunchParseErrorKind.Invalid, "serve", "--owner-sid", "--owner-logon-session", "7", "--instance", "8b1f4c2e-2f7e-4a55-9d7e-3b8c1d7e9a10")]
    [InlineData(BrokerLaunchParseErrorKind.Invalid, "serve", "--owner-sid", "S-1-5-21-1", "--owner-logon-session", "7", "--instance", "8b1f4c2e-2f7e-4a55-9d7e-3b8c1d7e9a10", "--idle-exit-seconds", "9")]
    [InlineData(BrokerLaunchParseErrorKind.Invalid, "serve", "--owner-sid", "S-1-5-21-1", "--owner-logon-session", "7", "--instance", "8b1f4c2e-2f7e-4a55-9d7e-3b8c1d7e9a10", "--root", "C:\\x")]
    public void LaunchOptionsRefuseAnythingButACompleteServe(BrokerLaunchParseErrorKind expected, params string[] args)
    {
        Assert.Null(BrokerLaunchOptions.Parse(args, out BrokerLaunchParseError? error));
        Assert.Equal(expected, Assert.IsType<BrokerLaunchParseError>(error).Kind);
    }

    [Fact]
    public async Task ProcessWithoutAServeCommandFailsClosedBeforeCreatingAnything()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        int bare = await BrokerProcess.RunAsync([], stdout, stderr, CancellationToken.None);
        Assert.Equal((int)InterCatExitCode.PermissionOrCapabilityFailure, bare);
        Assert.Contains("not run directly", stderr.ToString(), StringComparison.Ordinal);

        int malformed = await BrokerProcess.RunAsync(["serve", "--bogus"], stdout, stderr, CancellationToken.None);
        Assert.Equal((int)InterCatExitCode.InvalidInvocation, malformed);
        Assert.Empty(stdout.ToString());
    }

    [Fact(DisplayName = "R16: a served capture stops over the pipe, and one left recording is finalized at shutdown")]
    public async Task ServedProcessRoundTripsCapturesAndFinalizesOnShutdown()
    {
        using var fixture = ProcessFixture.Create();
        using var shutdown = new CancellationTokenSource();
        Task<int> serving = fixture.ServeAsync(shutdown.Token);
        string pipeName = await fixture.Listening;

        CaptureId stoppedByClient;
        CaptureId leftRecording;
        await using (PipeClient client = await PipeClient.ConnectAsync(pipeName))
        {
            await client.HelloAsync();
            stoppedByClient = await client.StartAsync();
            var status = Assert.IsType<BrokerCaptureStatusResponse>(await client.SendAsync(new BrokerGetStatusRequest(stoppedByClient)));
            Assert.Equal(CaptureLifecycle.Recording, status.State);

            var stopped = Assert.IsType<BrokerStopCaptureResponse>(
                await client.SendAsync(new BrokerStopCaptureRequest(stoppedByClient, Guid.NewGuid())));
            Assert.Equal(BrokerOperationCode.Stopped, stopped.Code);
            Assert.True(stopped.Milestones.FullyFinalized);

            leftRecording = await client.StartAsync();
        }

        // The same pipe instance serves the next client: the name is never released between connections.
        // The production client, which also checks that the pipe is served by the broker process (here: this one).
        await using (WindowsBrokerPipeClient second = await WindowsBrokerPipeClient.ConnectAsync(
            pipeName,
            Environment.ProcessId,
            TimeSpan.FromSeconds(10)))
        {
            Assert.IsType<BrokerHelloResponse>(await second.SendAsync(new BrokerHelloRequest(
                Guid.NewGuid(),
                1,
                1,
                BrokerHelloNegotiator.SupportedFeatures,
                BrokerProtocolFeature.PreparedPlanDigest)));
            var status = Assert.IsType<BrokerCaptureStatusResponse>(await second.SendAsync(new BrokerGetStatusRequest(leftRecording)));
            Assert.Equal(CaptureLifecycle.Recording, status.State);
        }

        await shutdown.CancelAsync();
        Assert.Equal((int)InterCatExitCode.Success, await serving.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Empty(fixture.Etw.Active);
        Assert.Contains("ShutdownStop", fixture.Diagnostics, StringComparison.Ordinal);

        using WindowsBrokerRoot root = fixture.Reprovision();
        using var store = new FileBrokerLifecycleStore(root);
        BrokerLifecycleSnapshot snapshot = await store.ReadSnapshotAsync(CancellationToken.None);
        Assert.All(snapshot.Captures, capture => Assert.Equal(CaptureLifecycle.Closed, capture.State));
        Assert.Contains(snapshot.Requests, request =>
            request.CaptureId == leftRecording && request.Kind == BrokerRequestKind.HostShutdownStop && request.Completed);
        foreach (CaptureId captureId in new[] { stoppedByClient, leftRecording })
        {
            using WindowsBrokerRoot evidence = root.OpenCaptureDirectory(captureId);
            Assert.Equal(captureId.Value, SessionStore.OpenExisting(evidence).Current!.SessionId);
        }
    }

    [Fact(DisplayName = "R16: restart recovery stops a predecessor's session before the pipe exists")]
    public async Task RecoveryCompletesBeforeTheFirstCommandCanArrive()
    {
        using var fixture = ProcessFixture.Create();
        BrokerCaptureOwnership interrupted;
        using (WindowsBrokerRoot root = fixture.Reprovision())
        using (var store = new FileBrokerLifecycleStore(root))
        {
            CaptureId captureId = CaptureId.New();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            interrupted = new()
            {
                CaptureId = captureId,
                Owner = fixture.Owner,
                Session = BrokerSessionOwnership.Create(captureId),
                PlanDigest = PreparedFocused().Digest,
                State = CaptureLifecycle.Starting,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                LeaseExpiresAtUtc = now.AddMinutes(1),
                StopMilestones = BrokerStopMilestones.None,
            };
            await store.SaveStartIntentAsync(
                interrupted,
                new()
                {
                    Owner = fixture.Owner,
                    RequestId = Guid.NewGuid(),
                    Kind = BrokerRequestKind.Start,
                    TargetKey = "interrupted-start",
                    CaptureId = captureId,
                    Completed = false,
                },
                CancellationToken.None);
        }

        fixture.Etw.Active.Add(interrupted.Session.SessionName);
        bool reclaimedBeforeListening = false;
        using var shutdown = new CancellationTokenSource();
        Task<int> serving = fixture.ServeAsync(
            shutdown.Token,
            _ => reclaimedBeforeListening = fixture.Etw.Reclaimed.Contains(interrupted.Session.SessionName));
        await fixture.Listening;
        await shutdown.CancelAsync();

        // The predecessor's evidence was never finalized, so the honest exit is a partial result.
        Assert.Equal((int)InterCatExitCode.PartialResultSuccess, await serving.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(reclaimedBeforeListening);
        Assert.Empty(fixture.Etw.Active);
        Assert.Contains("InterruptedStartStopped", fixture.Diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdleHostExitsOnlyOnceNothingIsRecording()
    {
        var registry = new PreparedPlanRegistry();
        var runtime = new BrokerFakeRuntime();
        using var preparation = new BrokerPreparationCoordinator(new FixedPlanSource(), registry, Runtime);
        using var lifecycle = new BrokerLifecycleCoordinator(registry, new InMemoryBrokerLifecycleStore(), runtime);
        BrokerClientIdentity current = WindowsBrokerTokenIdentity.ReadCurrentProcess();
        var host = new BrokerHost(preparation, lifecycle, Settings(current.Owner, idle: TimeSpan.FromMilliseconds(200)));
        var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Started after recovery: a capture that exists before the host runs is a predecessor's, and recovery stops it.
        Task<BrokerHostResult> running = host.RunAsync(_ => listening.SetResult());
        await listening.Task;
        PreparedPlanGrant grant = registry.Issue(PreparedFocused(), current);
        BrokerStartOutcome started = await lifecycle.StartAsync(grant.Token, Guid.NewGuid(), current);
        Assert.Equal(BrokerOperationCode.Started, started.Code);
        await Task.Delay(TimeSpan.FromMilliseconds(800));
        Assert.False(running.IsCompleted, "the host exited while a capture was still recording");

        Assert.Equal(
            BrokerOperationCode.Stopped,
            (await lifecycle.StopAsync(started.CaptureId!.Value, Guid.NewGuid(), current)).Code);
        BrokerHostResult result = await running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(BrokerHostExitReason.Idle, result.Reason);
        Assert.Empty(result.ShutdownStops);
        Assert.Equal(0, result.ClientsServed);
        Assert.Equal(1, runtime.StopCount);
    }

    [Fact]
    public async Task ARefusedClientIsDisconnectedAndTheHostKeepsServing()
    {
        BrokerClientIdentity current = WindowsBrokerTokenIdentity.ReadCurrentProcess();
        BrokerOwnerIdentity otherSession = current.Owner with
        {
            LogonSessionId = current.LogonSessionId == ulong.MaxValue ? 1 : current.LogonSessionId + 1,
        };
        var registry = new PreparedPlanRegistry();
        using var preparation = new BrokerPreparationCoordinator(new FixedPlanSource(), registry, Runtime);
        using var lifecycle = new BrokerLifecycleCoordinator(registry, new InMemoryBrokerLifecycleStore(), new BrokerFakeRuntime());
        var events = new List<BrokerHostEvent>();
        var host = new BrokerHost(
            preparation,
            lifecycle,
            Settings(otherSession, idle: TimeSpan.FromMinutes(5)),
            log: entry =>
            {
                lock (events)
                {
                    events.Add(entry);
                }
            });
        var listening = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shutdown = new CancellationTokenSource();
        Task<BrokerHostResult> running = host.RunAsync(path => listening.SetResult(path), shutdown.Token);
        string pipeName = (await listening.Task).Replace(@"\\.\pipe\", "", StringComparison.Ordinal);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            await using PipeClient refused = await PipeClient.ConnectAsync(pipeName);
            try
            {
                await refused.WriteHelloAsync();
            }
            catch (IOException)
            {
                // The broker may disconnect before the request is even written.
            }

            Assert.Null(await refused.TryReadAsync());
        }

        Assert.False(running.IsCompleted);
        await shutdown.CancelAsync();
        BrokerHostResult result = await running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(BrokerHostExitReason.Cancelled, result.Reason);
        Assert.Equal(0, result.ClientsServed);
        Assert.Equal(2, result.ClientsRefused);
        lock (events)
        {
            Assert.Equal(2, events.Count(entry => entry.Kind == BrokerHostEventKind.ClientRefused));
        }
    }

    [Fact(DisplayName = "R16: a client refuses a pipe served by any process but the broker it launched")]
    public async Task ClientRefusesAPipeServedByAnotherProcessBeforeSendingAnything()
    {
        string pipeName = $"InterCat.Broker.v1.{Guid.NewGuid():N}";
        await using var squatter = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        Task accepted = squatter.WaitForConnectionAsync();
        int launchedBroker = Environment.ProcessId == int.MaxValue ? 1 : Environment.ProcessId + 1;

        BrokerServerIdentityException refused = await Assert.ThrowsAsync<BrokerServerIdentityException>(() =>
            WindowsBrokerPipeClient.ConnectAsync(pipeName, launchedBroker, TimeSpan.FromSeconds(10)));
        await accepted;

        Assert.Contains($"process {Environment.ProcessId}", refused.Message, StringComparison.Ordinal);
        Assert.Contains("nothing was sent", refused.Message, StringComparison.Ordinal);
        byte[] buffer = new byte[1];
        Assert.Equal(0, await squatter.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private static BrokerHostSettings Settings(BrokerOwnerIdentity owner, TimeSpan idle) => new()
    {
        Owner = owner,
        ServerInstanceId = Guid.NewGuid(),
        ServerVersion = "host-fixture-1",
        IdleExitAfter = idle,
        IdleCheckInterval = TimeSpan.FromMilliseconds(25),
        MaintenanceInterval = TimeSpan.FromMilliseconds(50),
    };

    /// <summary>A real broker process composition over a temporary protected root and a scripted ETW host.</summary>
    private sealed class ProcessFixture : IDisposable
    {
        private readonly string parent;
        private readonly BrokerRootSecurityPolicy policy;
        private readonly StringWriter stderr = new();
        private readonly TaskCompletionSource<string> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private ProcessFixture(string parent, BrokerRootSecurityPolicy policy)
        {
            this.parent = parent;
            this.policy = policy;
            Owner = WindowsBrokerTokenIdentity.ReadCurrentProcess().Owner;
        }

        public BrokerOwnerIdentity Owner { get; }
        public ScriptedEtwHost Etw { get; } = new();
        public Task<string> Listening => listening.Task;

        public string Diagnostics
        {
            get
            {
                lock (stderr)
                {
                    return stderr.ToString();
                }
            }
        }

        public static ProcessFixture Create() =>
            new(TemporaryBrokerRoot.CreateTemporaryParent(), TemporaryBrokerRoot.PolicyForCurrentProcess());

        public Task<int> ServeAsync(CancellationToken cancellationToken, Action<string>? beforeListening = null)
        {
            var options = new BrokerLaunchOptions(Owner, Guid.NewGuid(), TimeSpan.FromMinutes(5));
            var dependencies = new BrokerProcessDependencies(
                RootRequest(),
                Etw,
                Etw,
                new FixedPlanSource(),
                Runtime);
            return Task.Run(() => BrokerProcess.ServeAsync(
                options,
                dependencies,
                TextWriter.Null,
                TextWriter.Synchronized(new LockedWriter(stderr)),
                cancellationToken,
                path =>
                {
                    beforeListening?.Invoke(path);
                    listening.TrySetResult(path.Replace(@"\\.\pipe\", "", StringComparison.Ordinal));
                }));
        }

        public WindowsBrokerRoot Reprovision() => WindowsBrokerRoot.Provision(RootRequest());

        public void Dispose()
        {
            try
            {
                Directory.Delete(parent, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        private BrokerRootRequest RootRequest() => new()
        {
            TrustedParentDirectory = parent,
            RootDirectoryName = TemporaryBrokerRoot.RootName,
            CapturingUserSid = Owner.UserSid,
            Policy = policy,
        };

        private sealed class LockedWriter(StringWriter inner) : TextWriter
        {
            public override System.Text.Encoding Encoding => inner.Encoding;

            public override void Write(char value)
            {
                lock (inner)
                {
                    inner.Write(value);
                }
            }

            public override void Write(string? value)
            {
                lock (inner)
                {
                    inner.Write(value);
                }
            }
        }
    }

    private sealed class PipeClient : IAsyncDisposable
    {
        private readonly NamedPipeClientStream stream;

        private PipeClient(NamedPipeClientStream stream) => this.stream = stream;

        public static async Task<PipeClient> ConnectAsync(string pipeName)
        {
            var stream = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous,
                TokenImpersonationLevel.Impersonation);
            await stream.ConnectAsync(10_000);
            return new(stream);
        }

        public Task<Guid> WriteHelloAsync() => WriteAsync(new BrokerHelloRequest(
            Guid.NewGuid(),
            1,
            1,
            BrokerHelloNegotiator.SupportedFeatures,
            BrokerProtocolFeature.PreparedPlanDigest));

        public async Task HelloAsync()
        {
            await WriteHelloAsync();
            Assert.IsType<BrokerHelloResponse>(BrokerWireResponseCodec.Decode(Assert.IsType<BrokerWireFrame>(await TryReadAsync())));
        }

        public async Task<CaptureId> StartAsync()
        {
            var prepared = Assert.IsType<BrokerPrepareCaptureResponse>(await SendAsync(new BrokerPrepareCaptureRequest(
                "focused-transport",
                Mechanism.Tcp,
                [],
                false,
                false,
                SmallQuota,
                BrokerRetentionPolicy.StopAtLimit,
                null)));
            Assert.True(prepared.Prepared);
            var started = Assert.IsType<BrokerStartCaptureResponse>(
                await SendAsync(new BrokerStartCaptureRequest(prepared.Grant!.Token, Guid.NewGuid())));
            Assert.Equal(BrokerOperationCode.Started, started.Code);
            return Assert.IsType<CaptureId>(started.CaptureId);
        }

        public async Task<BrokerWireResponse> SendAsync(BrokerWireRequest request)
        {
            Guid correlationId = await WriteAsync(request);
            BrokerWireFrame response = Assert.IsType<BrokerWireFrame>(await TryReadAsync());
            Assert.Equal(correlationId, response.CorrelationId);
            return BrokerWireResponseCodec.Decode(response);
        }

        public async Task<BrokerWireFrame?> TryReadAsync()
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                return await BrokerWireFrameCodec.ReadAsync(stream, timeout.Token);
            }
            catch (IOException)
            {
                return null;
            }
        }

        public ValueTask DisposeAsync() => stream.DisposeAsync();

        private async Task<Guid> WriteAsync(BrokerWireRequest request)
        {
            Guid correlationId = Guid.NewGuid();
            BrokerWireFrame frame = BrokerWireRequestCodec.Encode(request, correlationId);
            await BrokerWireFrameCodec.WriteAsync(stream, frame);
            await stream.FlushAsync();
            return correlationId;
        }
    }

    private sealed class FixedPlanSource : IBrokerCapturePlanSource
    {
        public ValueTask<CapabilityReport> ProbeAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The host fixtures do not probe capabilities.");

        public ValueTask<EffectiveCapturePlan> CompileAsync(
            CaptureProfileRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(CompileFocused(request.FocusedProcessIds, request.AllowBroaderCapture));
    }
}
