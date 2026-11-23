using System.Net;
using System.Net.Sockets;
using Xunit;
using static System.Threading.CancellationToken;

namespace InterCat.Remote.Tests;

/// <summary>A host's clock: the system's, read as far ahead as a test says.</summary>
internal sealed class OffsetTime(TimeSpan offset) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + offset;
}

/// <summary>Each step a capture across several agents took, kept as it was reported.</summary>
internal sealed class KeptSteps : IProgress<RemoteHostEvent>
{
    private readonly Lock gate = new();
    private readonly List<(RemoteHostStep Step, string Host)> steps = [];

    public IReadOnlyList<(RemoteHostStep Step, string Host)> Steps
    {
        get
        {
            lock (gate)
            {
                return [.. steps];
            }
        }
    }

    public void Report(RemoteHostEvent value)
    {
        lock (gate)
        {
            steps.Add((value.Step, value.Host.Host.Name));
        }
    }
}

/// <summary>
/// M11's capture across several agents (`remote-agent-v1` §4.6, ADR-117): every host prepared at once, every prepared host
/// started as one barrier whose acknowledgments are read with the controller's clock either side, each lease kept and a
/// lost connection made again within it, every capture still open stopped together, and a host that cannot record
/// stopping only its own part. The agents run on loopback, each its own clock; their hosts' captures are stand-ins.
/// </summary>
public sealed class RemoteCoordinationTests
{
    private static readonly RemoteCaptureRequest Explore = new("explore", 60, 64);

    [Fact(DisplayName = "§15: one start across several agents is a barrier - each agent's clock read within half its round trip, a slow start only widening its bound, each host's clock against another's bounded by both - and each capture closes on its own")]
    public async Task OneStartAcrossSeveralAgentsReadsEachClock()
    {
        TimeSpan[] offsets = [TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(-3)];
        CaptureAgent[] agents = [.. offsets.Select(offset => CaptureAgent.Start(clock: new OffsetTime(offset)))];
        try
        {
            // The first host starts slowly: every other start is sent before its answer comes, as one barrier sends them.
            agents[0].Driver.StartDelay = TimeSpan.FromMilliseconds(500);
            foreach (CaptureAgent agent in agents)
            {
                agent.Driver.ClosesAfterStatuses = 8;
            }

            using var controller = RemoteIdentity.Create("controller");
            RemoteCaptureHost[] hosts = await EnrolledAsync(controller, agents);
            var steps = new KeptSteps();
            IReadOnlyList<RemoteHostCapture> parts = await RemoteCoordinatedCapture.RunAsync(hosts, controller, Explore,
                TimeSpan.FromSeconds(3), steps, None);

            Assert.All(parts, part => Assert.True(part is { Started: true, Failure: null, State.State: RemoteCaptureLifecycle.Closed, End: not null },
                $"{part.Host.Name}: {part.Failure?.Reason}"));
            Assert.All(parts, part => Assert.False(part.StoppedByController));

            // Each agent's clock read where it is, within the bound its round trip leaves; the slow start shows as bound.
            foreach ((RemoteHostCapture part, TimeSpan offset) in parts.Zip(offsets))
            {
                Assert.True((part.Start!.AgentAhead - offset).Duration() <= part.Start.Within,
                    $"{part.Host.Name} read {part.Start.AgentAhead.TotalMilliseconds} ms ahead within {part.Start.Within.TotalMilliseconds}, not {offset.TotalMilliseconds}");
                Assert.True((part.End!.AgentAhead - offset).Duration() <= part.End.Within);
            }

            Assert.True(parts[0].Start!.Within >= TimeSpan.FromMilliseconds(250));
            Assert.True(parts.Max(part => part.Start!.SentUtc) < parts[0].Start!.ReceivedUtc, "Every start is sent before any answer is awaited.");
            foreach (int at in new[] { 1, 2 })
            {
                (TimeSpan ahead, TimeSpan within) = parts[at].Start!.Against(parts[0].Start!);
                Assert.True((ahead - offsets[at]).Duration() <= within);
                Assert.Equal(parts[at].Start!.Within + parts[0].Start!.Within, within);
            }

            Assert.All(agents, agent => Assert.Equal(["prepare explore 60 64", "start"],
                agent.Driver.Calls.Where(call => call.StartsWith("prepare", StringComparison.Ordinal) || call is "start" or "stop")));
            Assert.Equal(3, steps.Steps.Count(step => step.Step == RemoteHostStep.Started));
            Assert.Equal(3, steps.Steps.Count(step => step.Step == RemoteHostStep.Closed));
        }
        finally
        {
            foreach (CaptureAgent agent in agents)
            {
                await agent.DisposeAsync();
            }
        }
    }

    [Fact(DisplayName = "§15: a host that cannot be reached or refuses keeps no other host from recording, and each says what became of its part")]
    public async Task AHostThatCannotRecordStopsOnlyItsPart()
    {
        await using var recording = CaptureAgent.Start(clock: new OffsetTime(TimeSpan.Zero));
        await using var refusing = CaptureAgent.Start(clock: new OffsetTime(TimeSpan.Zero));
        recording.Driver.ClosesAfterStatuses = 8;
        refusing.Driver.RefusesWith = "this build declares no provider for it";
        using var controller = RemoteIdentity.Create("controller");
        RemoteCaptureHost[] enrolled = await EnrolledAsync(controller, [recording, refusing]);
        using var elsewhere = RemoteIdentity.Create("agent");
        RemoteCaptureHost[] hosts = [.. enrolled, new RemoteCaptureHost(NobodyListens(), elsewhere.Pin, "host-offline")];

        var steps = new KeptSteps();
        IReadOnlyList<RemoteHostCapture> parts = await RemoteCoordinatedCapture.RunAsync(hosts, controller, Explore,
            TimeSpan.FromSeconds(3), steps, None, connectTimeout: TimeSpan.FromSeconds(3));

        Assert.True(parts[0] is { Started: true, Failure: null, State.State: RemoteCaptureLifecycle.Closed });
        Assert.Equal(RemoteHostStage.Prepare, parts[1].Failure?.Stage);
        Assert.Contains("this build declares no provider for it", parts[1].Failure!.Reason, StringComparison.Ordinal);
        Assert.Equal(RemoteHostStage.Connect, parts[2].Failure?.Stage);
        Assert.False(parts[1].Started || parts[2].Started);
        Assert.Equal([("host-0", RemoteHostStep.Started)], steps.Steps.Where(step => step.Step == RemoteHostStep.Started).Select(step => (step.Host, step.Step)));
        Assert.DoesNotContain("start", refusing.Driver.Calls);
    }

    [Fact(DisplayName = "§15: a controller whose connections are lost makes them again while each capture's lease holds, so its captures record on, and stopping stops every host together, each stop's answer read across it")]
    public async Task LostConnectionsAreMadeAgainAndAStopStopsEveryHost()
    {
        // This agent closes a connection idle longer than a third of a second, so every renewal finds it lost.
        await using var forgetting = CaptureAgent.Start(clock: new OffsetTime(TimeSpan.Zero),
            limits: RemoteAgentLimits.Default with { Idle = TimeSpan.FromMilliseconds(300) });
        await using var steady = CaptureAgent.Start(clock: new OffsetTime(TimeSpan.FromSeconds(2)));
        using var controller = RemoteIdentity.Create("controller");
        RemoteCaptureHost[] hosts = await EnrolledAsync(controller, [forgetting, steady]);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var steps = new KeptSteps();
        IReadOnlyList<RemoteHostCapture> parts = await RemoteCoordinatedCapture.RunAsync(hosts, controller, Explore,
            TimeSpan.FromSeconds(3), steps, stop.Token);

        Assert.True(parts[0].Reconnections >= 2, $"made again {parts[0].Reconnections} times");
        Assert.Equal(0, parts[1].Reconnections);
        foreach (RemoteHostCapture part in parts)
        {
            Assert.True(part is { Failure: null, StoppedByController: true, State.State: RemoteCaptureLifecycle.Closed }, part.Failure?.Reason);
            Assert.Equal("stopped by its controller", part.State!.Reason);
            Assert.True((part.End!.AgentAhead - (part == parts[1] ? TimeSpan.FromSeconds(2) : TimeSpan.Zero)).Duration() <= part.End.Within);
        }

        Assert.Contains(steps.Steps, step => step == (RemoteHostStep.Reconnected, "host-0"));
        Assert.Single(forgetting.Driver.Calls, "stop");
        Assert.Single(steady.Driver.Calls, "stop");
    }

    [Fact(DisplayName = "§15: an agent the controller cannot reach again before its lease lapses is said to be lost, and the agent stops that capture on its own")]
    public async Task AnAgentUnreachedPastItsLeaseIsLost()
    {
        await using var agent = CaptureAgent.Start(clock: new OffsetTime(TimeSpan.Zero));
        using var controller = RemoteIdentity.Create("controller");
        RemoteCaptureHost[] hosts = await EnrolledAsync(controller, [agent]);
        var steps = new StartedThen(async () => await agent.Server.DisposeAsync());

        IReadOnlyList<RemoteHostCapture> parts = await RemoteCoordinatedCapture.RunAsync(hosts, controller, Explore,
            TimeSpan.FromSeconds(2), steps, None, connectTimeout: TimeSpan.FromSeconds(1));

        RemoteHostCapture part = Assert.Single(parts);
        Assert.Equal(RemoteHostStage.Keep, part.Failure?.Stage);
        Assert.Contains("could not be reached again before the capture's lease lapsed", part.Failure!.Reason, StringComparison.Ordinal);
        Assert.True(await agent.AskedAsync("stop"), "The agent stops the capture whose lease lapsed.");
    }

    [Fact(DisplayName = "§15: a capture is recorded on at least one agent, each named once")]
    public async Task EachAgentIsNamedOnce()
    {
        using var controller = RemoteIdentity.Create("controller");
        var host = new RemoteCaptureHost(new IPEndPoint(IPAddress.Loopback, 7457), controller.Pin, "host");
        await Assert.ThrowsAsync<ArgumentException>(() => RemoteCoordinatedCapture.RunAsync([], controller, Explore, TimeSpan.FromSeconds(3), null, None));
        await Assert.ThrowsAsync<ArgumentException>(() => RemoteCoordinatedCapture.RunAsync([host, host with { Name = "again" }], controller, Explore,
            TimeSpan.FromSeconds(3), null, None));
    }

    /// <summary>Enrolls <paramref name="controller"/> with each agent, granting metadata capture, and names its hosts in order.</summary>
    private static async Task<RemoteCaptureHost[]> EnrolledAsync(RemoteIdentity controller, CaptureAgent[] agents)
    {
        var hosts = new RemoteCaptureHost[agents.Length];
        for (int at = 0; at < agents.Length; at++)
        {
            await using RemoteAgentConnection enrolled = await agents[at].EnrolledAsync(controller, RemoteGrants.MetadataCapture);
            hosts[at] = new RemoteCaptureHost(agents[at].Endpoint, agents[at].Identity.Pin, $"host-{at}");
        }

        return hosts;
    }

    /// <summary>A loopback endpoint no one listens on.</summary>
    private static IPEndPoint NobodyListens()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        listener.Stop();
        return endpoint;
    }

    /// <summary>Does something once the first host started, as the network failing would.</summary>
    private sealed class StartedThen(Func<Task> then) : IProgress<RemoteHostEvent>
    {
        private int done;

        public void Report(RemoteHostEvent value)
        {
            if (value.Step == RemoteHostStep.Started && Interlocked.Exchange(ref done, 1) == 0)
            {
                then().GetAwaiter().GetResult();
            }
        }
    }
}
