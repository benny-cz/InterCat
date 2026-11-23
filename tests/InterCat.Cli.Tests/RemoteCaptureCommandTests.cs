using System.Net;
using System.Text.Json;
using InterCat.Domain;
using InterCat.Remote;
using Xunit;
using static System.StringComparison;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat remote capture (`remote-agent-v1` §4.4): a capture on an enrolled agent's host, its lease kept while it records,
/// stopped on Ctrl+C, and its start's acknowledgment kept as a barrier with its round trip. The agent runs in this process
/// on loopback, its host's capture a stand-in: no test here launches a broker or records.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class RemoteCaptureCommandTests
{
    [Fact(DisplayName = "§15: icat remote capture records on an enrolled agent's host until the capture ends, keeping its lease, and says when the agent acknowledged the start - a barrier within its round trip, not proof that two clocks agree")]
    public async Task IcatRemoteCaptureRecordsUntilTheCaptureEnds()
    {
        await using var agent = await LoopbackAgent.EnrolledAsync();
        agent.Driver.ClosesAfterStatuses = 8;

        (InterCatExitCode code, string text, string said) = await Run(CancellationToken.None,
            "remote", "capture", agent.Endpoint, "--duration", "30", "--lease", "3", "--state", agent.Controller, "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        JsonElement capture = JsonDocument.Parse(text).RootElement;
        Assert.Equal(("remote-capture-v1", "explore", 30, "closed", true),
            (capture.GetProperty("contract").GetString(), capture.GetProperty("profile").GetString(),
            capture.GetProperty("seconds").GetInt32(), capture.GetProperty("state").GetString(), capture.GetProperty("finalized").GetBoolean()));
        Assert.True(capture.GetProperty("records").GetInt64() > 0);
        JsonElement start = capture.GetProperty("start");
        Assert.True(start.GetProperty("roundTripMilliseconds").GetDouble() >= 0);
        Assert.True(start.GetProperty("withinMilliseconds").GetDouble() >= 0);
        Assert.Contains("not proof that their clocks agree", said, Ordinal);
        Assert.Equal("prepare explore 30 1024", agent.Driver.Calls[0]);
        Assert.DoesNotContain("stop", agent.Driver.Calls);
    }

    [Fact(DisplayName = "§15: Ctrl+C stops a remote capture early, the agent keeping what it recorded, and a capture is asked only of an agent this controller enrolled with")]
    public async Task CtrlCStopsARemoteCapture()
    {
        await using var agent = await LoopbackAgent.EnrolledAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        (InterCatExitCode code, string text, string said) = await Run(stop.Token,
            "remote", "capture", agent.Endpoint, "--duration", "60", "--lease", "3", "--state", agent.Controller, "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        JsonElement capture = JsonDocument.Parse(text).RootElement;
        Assert.Equal(("closed", "stopped by its controller"), (capture.GetProperty("state").GetString(), capture.GetProperty("reason").GetString()));
        Assert.Contains("stop", agent.Driver.Calls);

        (InterCatExitCode unknown, _, string why) = await Run(CancellationToken.None,
            "remote", "capture", "127.0.0.1:1", "--state", agent.Controller);
        Assert.Equal(InterCatExitCode.InvalidInvocation, unknown);
        Assert.Contains("enrolled with no agent at 127.0.0.1:1", why, Ordinal);
    }

    private static async Task<(InterCatExitCode Code, string Output, string Error)> Run(CancellationToken cancellationToken, params string[] args)
    {
        TextWriter output = Console.Out;
        TextWriter error = Console.Error;
        using var answered = new StringWriter();
        using var said = new StringWriter();
        Console.SetOut(answered);
        Console.SetError(said);
        try
        {
            InterCatExitCode code = await Icat.RunAsync(args, cancellationToken);
            return (code, answered.ToString(), said.ToString());
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
        }
    }

    /// <summary>An agent on loopback serving captures through a stand-in, and a controller enrolled with it by icat.</summary>
    private sealed class LoopbackAgent : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "icat-remote-capture-" + Guid.NewGuid().ToString("N"));
        private readonly RemoteIdentity identity = RemoteIdentity.Create("agent");
        private readonly AgentEnrollments enrollments = new();
        private readonly RemoteCaptures captures;
        private readonly RemoteAgentServer server;

        private LoopbackAgent()
        {
            Directory.CreateDirectory(root);
            captures = new RemoteCaptures(Driver, new RemoteCapturePolicy(600, 2_048, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(25)));
            server = new RemoteAgentServer(identity, enrollments, new RemoteAgentHost(Guid.NewGuid(), "capture-host", "Windows (test)", "1.0.0-test"),
                captures: captures);
            Endpoint = server.Start(new IPEndPoint(IPAddress.Loopback, 0)).ToString();
        }

        public StandInDriver Driver { get; } = new();

        public string Endpoint { get; }

        public string Controller => Path.Combine(root, "controller");

        public static async Task<LoopbackAgent> EnrolledAsync()
        {
            var agent = new LoopbackAgent();
            EnrollmentToken token = agent.enrollments.Issue(agent.identity.Pin, RemoteGrants.MetadataCapture);
            (InterCatExitCode code, _, string said) = await Run(CancellationToken.None, "remote", "enroll", agent.Endpoint, token.ToString(), "--state", agent.Controller);
            Assert.True(code == InterCatExitCode.Success, said);
            return agent;
        }

        public async ValueTask DisposeAsync()
        {
            await server.DisposeAsync();
            await captures.DisposeAsync();
            identity.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A host's capture as a test drives it: what the agent asked, and a capture that ends where told.</summary>
    private sealed class StandInDriver : IRemoteCaptureDriver
    {
        private readonly Lock gate = new();
        private readonly List<string> calls = [];
        private int statuses;
        private bool stopped;

        public int ClosesAfterStatuses { get; set; } = int.MaxValue;

        public IReadOnlyList<string> Calls
        {
            get
            {
                lock (gate)
                {
                    return [.. calls];
                }
            }
        }

        public Task<DriverPreparation> PrepareAsync(RemoteCaptureRequest request, CancellationToken cancellationToken)
        {
            Call($"prepare {request.Profile} {request.Seconds} {request.JournalMebibytes}");
            return Task.FromResult(new DriverPreparation(Guid.NewGuid(), request.Profile, request.Seconds, request.JournalMebibytes,
                "Process lifecycle and TCP transport events; no payload contents."));
        }

        public Task<Guid> StartAsync(Guid prepared, CancellationToken cancellationToken)
        {
            Call("start");
            return Task.FromResult(Guid.NewGuid());
        }

        public Task ReleaseAsync(Guid prepared, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DriverCaptureState> StatusAsync(Guid capture, CancellationToken cancellationToken)
        {
            int seen;
            lock (gate)
            {
                seen = ++statuses;
            }

            bool closed = stopped || seen >= ClosesAfterStatuses;
            return Task.FromResult(new DriverCaptureState(closed ? RemoteCaptureLifecycle.Closed : RemoteCaptureLifecycle.Recording,
                100L * seen, 0, closed, null));
        }

        public Task RenewAsync(Guid capture, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DriverCaptureState> StopAsync(Guid capture, CancellationToken cancellationToken)
        {
            Call("stop");
            stopped = true;
            return Task.FromResult(new DriverCaptureState(RemoteCaptureLifecycle.Closed, 100L * statuses, 0, true, null));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void Call(string what)
        {
            lock (gate)
            {
                calls.Add(what);
            }
        }
    }
}
