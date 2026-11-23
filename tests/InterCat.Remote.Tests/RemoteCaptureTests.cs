using System.Net;
using Xunit;

namespace InterCat.Remote.Tests;

/// <summary>A host's capture as a test drives it: what the agent asked, in order, and a capture that ends where told.</summary>
internal sealed class StandInDriver : IRemoteCaptureDriver
{
    private readonly Lock gate = new();
    private readonly List<string> calls = [];
    private Guid? prepared;
    private Guid? capture;
    private int statuses;
    private bool stopped;

    /// <summary>The status after which the capture closes on its own, as one reaching its duration does.</summary>
    public int ClosesAfterStatuses { get; set; } = int.MaxValue;

    /// <summary>Where set, the host refuses to prepare, in these words.</summary>
    public string? RefusesWith { get; set; }

    /// <summary>Whether the host says nothing of a closed capture's counts, as a real broker does once it closes.</summary>
    public bool ForgetsCountsWhenClosed { get; set; }

    /// <summary>Where the host says a capture's evidence is.</summary>
    public string Evidence { get; set; } = "stand-in-evidence";

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
        if (RefusesWith is { } refusal)
        {
            throw new RemoteCaptureRefusedException(refusal);
        }

        prepared = Guid.NewGuid();
        (statuses, stopped) = (0, false);
        return Task.FromResult(new DriverPreparation(prepared.Value, request.Profile, request.Seconds, request.JournalMebibytes,
            "Process lifecycle and TCP transport events; no payload contents."));
    }

    public Task<DriverStart> StartAsync(Guid preparation, CancellationToken cancellationToken)
    {
        Call("start");
        capture = preparation == prepared ? Guid.NewGuid() : throw new RemoteCaptureRefusedException("no such preparation");
        return Task.FromResult(new DriverStart(capture.Value, Evidence));
    }

    public Task ReleaseAsync(Guid preparation, CancellationToken cancellationToken)
    {
        Call("release");
        return Task.CompletedTask;
    }

    public Task<DriverCaptureState> StatusAsync(Guid captureId, CancellationToken cancellationToken)
    {
        int seen;
        lock (gate)
        {
            seen = ++statuses;
        }

        bool closed = stopped || seen >= ClosesAfterStatuses;
        return Task.FromResult(new DriverCaptureState(closed ? RemoteCaptureLifecycle.Closed : RemoteCaptureLifecycle.Recording,
            closed && ForgetsCountsWhenClosed ? 0 : 100L * seen, 0, closed, null));
    }

    public Task RenewAsync(Guid captureId, CancellationToken cancellationToken)
    {
        Call("renew");
        return Task.CompletedTask;
    }

    public Task<DriverCaptureState> StopAsync(Guid captureId, CancellationToken cancellationToken)
    {
        Call("stop");
        stopped = true;
        return Task.FromResult(new DriverCaptureState(RemoteCaptureLifecycle.Closed, ForgetsCountsWhenClosed ? 0 : 100L * statuses, 0, true, null));
    }

    public ValueTask DisposeAsync()
    {
        Call("dispose");
        return ValueTask.CompletedTask;
    }

    private void Call(string what)
    {
        lock (gate)
        {
            calls.Add(what);
        }
    }
}

/// <summary>An agent serving captures through a stand-in, its bounds short enough for a test to see a lease lapse.</summary>
internal sealed class CaptureAgent : IAsyncDisposable
{
    public static readonly RemoteCapturePolicy Quick = new(600, 2_048, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(25));

    private readonly bool ownsIdentity;

    private CaptureAgent(RemoteIdentity identity, AgentEnrollments enrollments, RemoteAuditLog audit, ManualTime time,
        StandInDriver driver, RemoteCaptures captures, RemoteAgentServer server, IPEndPoint endpoint, bool ownsIdentity)
    {
        this.ownsIdentity = ownsIdentity;
        Identity = identity;
        Enrollments = enrollments;
        Audit = audit;
        Time = time;
        Driver = driver;
        Captures = captures;
        Server = server;
        Endpoint = endpoint;
    }

    public RemoteIdentity Identity { get; }
    public AgentEnrollments Enrollments { get; }
    public RemoteAuditLog Audit { get; }
    public ManualTime Time { get; }
    public StandInDriver Driver { get; }
    public RemoteCaptures Captures { get; }
    public RemoteAgentServer Server { get; }
    public IPEndPoint Endpoint { get; }

    /// <summary>
    /// An agent on loopback serving <paramref name="evidence"/> where given, keeping its capture records in
    /// <paramref name="records"/>, and - where a test restarts it - under the key and enrollments it had before.
    /// </summary>
    public static CaptureAgent Start(IRemoteEvidenceSource? evidence = null, IRemoteCaptureRecords? records = null,
        RemoteIdentity? identity = null, AgentEnrollments? enrollments = null)
    {
        var time = new ManualTime();
        RemoteIdentity key = identity ?? RemoteIdentity.Create("agent");
        AgentEnrollments enrolled = enrollments ?? new AgentEnrollments(time);
        var audit = new RemoteAuditLog();
        var driver = new StandInDriver();
        var captures = new RemoteCaptures(driver, Quick, time, audit, records, evidence);
        var host = new RemoteAgentHost(Guid.NewGuid(), "capture-host", "Windows (test)", "1.0.0-test");
        var server = new RemoteAgentServer(key, enrolled, host, audit: audit, time: time, captures: captures);
        return new(key, enrolled, audit, time, driver, captures, server, server.Start(new IPEndPoint(IPAddress.Loopback, 0)),
            ownsIdentity: identity is null);
    }

    /// <summary>A controller enrolled with <paramref name="grants"/>, connected.</summary>
    public async Task<RemoteAgentConnection> EnrolledAsync(RemoteIdentity controller, RemoteGrants grants)
    {
        RemoteAgentConnection connection = await RemoteAgentConnection.ConnectAsync(Endpoint, controller, Identity.Pin, TimeSpan.FromSeconds(10), CancellationToken.None);
        _ = await connection.EnrollAsync(Enrollments.Issue(Identity.Pin, grants), CancellationToken.None);
        return connection;
    }

    /// <summary>Waits up to five seconds for the stand-in to have been asked <paramref name="call"/>.</summary>
    public async Task<bool> AskedAsync(string call)
    {
        for (DateTime giveUp = DateTime.UtcNow.AddSeconds(5); DateTime.UtcNow < giveUp; await Task.Delay(20))
        {
            if (Driver.Calls.Contains(call))
            {
                return true;
            }
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        await Captures.DisposeAsync();
        if (ownsIdentity)
        {
            Identity.Dispose();
        }
    }
}

/// <summary>
/// M11's remote capture (`remote-agent-v1` §4.4, ADR-117): an enrolled controller holding metadata capture prepares,
/// starts, follows and stops one capture on the agent's host within the agent's bounds, keeping a control lease the agent
/// stops the capture on when it lapses. The host's capture is a stand-in: no test here starts a broker or records.
/// </summary>
public sealed class RemoteCaptureTests
{
    [Fact(DisplayName = "§15: an enrolled controller holding metadata capture prepares, starts, follows and stops a capture on the agent's host within its bounds - the capture its own, refused to another controller, and the agent free again once it stops")]
    public async Task AControllerCapturesOnTheAgentsHost()
    {
        await using CaptureAgent agent = CaptureAgent.Start();
        using var controller = RemoteIdentity.Create("controller");
        using var other = RemoteIdentity.Create("controller");
        await using RemoteAgentConnection connection = await agent.EnrolledAsync(controller, RemoteGrants.MetadataCapture);
        await using RemoteAgentConnection stranger = await agent.EnrolledAsync(other, RemoteGrants.MetadataCapture);

        RemotePreparedCapture prepared = await connection.PrepareCaptureAsync(new RemoteCaptureRequest("explore", 60, 1_024), CancellationToken.None);
        Assert.Equal(("explore", 60u, 1_024u), (prepared.Profile, prepared.Seconds, prepared.JournalMebibytes));
        Assert.Contains("no payload contents", prepared.Collects, StringComparison.Ordinal);

        // Another controller can neither start it nor ask after it.
        RemoteAgentException notItsOwn = await Assert.ThrowsAsync<RemoteAgentException>(() =>
            stranger.StartCaptureAsync(prepared.Prepared, TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.Equal(RemoteErrorCode.NotPermitted, notItsOwn.Code);

        RemoteCaptureStarted started = await connection.StartCaptureAsync(prepared.Prepared, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal(agent.Time.Now, started.AcknowledgedUtc);
        Assert.Equal(agent.Time.Now.AddSeconds(10), started.LeaseExpiresUtc);

        RemoteCaptureState recording = await connection.CaptureStatusAsync(started.CaptureId, CancellationToken.None);
        Assert.Equal((RemoteCaptureLifecycle.Recording, true), (recording.State, recording.Records > 0));
        RemoteAgentException theirs = await Assert.ThrowsAsync<RemoteAgentException>(() =>
            stranger.StopCaptureAsync(started.CaptureId, CancellationToken.None));
        Assert.Equal(RemoteErrorCode.NotPermitted, theirs.Code);

        agent.Time.Now += TimeSpan.FromSeconds(5);
        RemoteCaptureState renewed = await connection.RenewCaptureAsync(started.CaptureId, CancellationToken.None);
        Assert.Equal(agent.Time.Now.AddSeconds(10), renewed.LeaseExpiresUtc);

        // Closed, the host says nothing of the counts, as a real broker does: the capture keeps the highest it said.
        agent.Driver.ForgetsCountsWhenClosed = true;
        RemoteCaptureState stopped = await connection.StopCaptureAsync(started.CaptureId, CancellationToken.None);
        Assert.Equal((RemoteCaptureLifecycle.Closed, true, "stopped by its controller"), (stopped.State, stopped.Finalized, stopped.Reason));
        Assert.True(stopped.Records >= renewed.Records && stopped.Records > 0, $"a closed capture read {stopped.Records} records");
        Assert.Contains("stop", agent.Driver.Calls);

        // Stopped, the agent takes the next capture; each step that changed one was audited.
        _ = await connection.PrepareCaptureAsync(new RemoteCaptureRequest("rpc-peers", 30, 512), CancellationToken.None);
        Assert.Equal(["prepared: explore, 60 s, 1024 MiB", "started: " + started.CaptureId.ToString("D"), "answered: closed"],
            agent.Audit.Entries.Where(entry => entry.Controller == controller.Pin && entry.Action is RemoteMessageType.PrepareCapture
                or RemoteMessageType.StartCapture or RemoteMessageType.StopCapture).Select(entry => entry.Outcome).Take(3));
    }

    [Fact(DisplayName = "§15: an agent refuses a capture without the grant, past its bounds, while it holds another, or that its host refuses - each saying why")]
    public async Task AnAgentRefusesWhatItWillNotRecord()
    {
        await using CaptureAgent agent = CaptureAgent.Start();
        using var downloader = RemoteIdentity.Create("controller");
        using var capturer = RemoteIdentity.Create("controller");
        await using RemoteAgentConnection fetching = await agent.EnrolledAsync(downloader, RemoteGrants.Download);
        await using RemoteAgentConnection connection = await agent.EnrolledAsync(capturer, RemoteGrants.MetadataCapture);

        RemoteAgentException ungranted = await Assert.ThrowsAsync<RemoteAgentException>(() =>
            fetching.PrepareCaptureAsync(new RemoteCaptureRequest("explore", 60, 1_024), CancellationToken.None));
        Assert.Equal(RemoteErrorCode.NotPermitted, ungranted.Code);

        RemoteAgentException tooLong = await Assert.ThrowsAsync<RemoteAgentException>(() =>
            connection.PrepareCaptureAsync(new RemoteCaptureRequest("explore", 601, 1_024), CancellationToken.None));
        Assert.Equal((RemoteErrorCode.NotPermitted, true), (tooLong.Code, tooLong.Message.Contains("1 to 600 seconds", StringComparison.Ordinal)));
        RemoteAgentException tooLarge = await Assert.ThrowsAsync<RemoteAgentException>(() =>
            connection.PrepareCaptureAsync(new RemoteCaptureRequest("explore", 60, 2_049), CancellationToken.None));
        Assert.Equal((RemoteErrorCode.NotPermitted, true), (tooLarge.Code, tooLarge.Message.Contains("2,048 MiB", StringComparison.Ordinal)));

        agent.Driver.RefusesWith = "this build records no such profile";
        RemoteAgentException hostRefused = await Assert.ThrowsAsync<RemoteAgentException>(() =>
            connection.PrepareCaptureAsync(new RemoteCaptureRequest("explore", 60, 1_024), CancellationToken.None));
        Assert.Equal((RemoteErrorCode.Failed, "this build records no such profile"), (hostRefused.Code, hostRefused.Message));
        agent.Driver.RefusesWith = null;

        RemotePreparedCapture prepared = await connection.PrepareCaptureAsync(new RemoteCaptureRequest("explore", 60, 1_024), CancellationToken.None);
        RemoteAgentException busy = await Assert.ThrowsAsync<RemoteAgentException>(() =>
            connection.PrepareCaptureAsync(new RemoteCaptureRequest("explore", 60, 1_024), CancellationToken.None));
        Assert.Equal(RemoteErrorCode.Busy, busy.Code);
        RemoteAgentException leaseTooLong = await Assert.ThrowsAsync<RemoteAgentException>(() =>
            connection.StartCaptureAsync(prepared.Prepared, TimeSpan.FromSeconds(61), CancellationToken.None));
        Assert.Equal(RemoteErrorCode.NotPermitted, leaseTooLong.Code);
        Assert.DoesNotContain("start", agent.Driver.Calls);
    }

    [Fact(DisplayName = "§15: while its controller's lease holds the agent renews its host's hold on the capture, and once the lease lapses it stops the capture and says why")]
    public async Task ALapsedLeaseStopsTheCapture()
    {
        await using CaptureAgent agent = CaptureAgent.Start();
        using var controller = RemoteIdentity.Create("controller");
        await using RemoteAgentConnection connection = await agent.EnrolledAsync(controller, RemoteGrants.MetadataCapture);
        RemotePreparedCapture prepared = await connection.PrepareCaptureAsync(new RemoteCaptureRequest("explore", 60, 1_024), CancellationToken.None);
        RemoteCaptureStarted started = await connection.StartCaptureAsync(prepared.Prepared, TimeSpan.FromSeconds(3), CancellationToken.None);

        // Within the lease, past the host's renewal: the agent renews its own hold, and stops nothing.
        agent.Time.Now += TimeSpan.FromSeconds(1);
        Assert.True(await agent.AskedAsync("renew"));
        Assert.DoesNotContain("stop", agent.Driver.Calls);

        // The controller goes quiet: the lease lapses, and the agent stops the capture.
        agent.Time.Now += TimeSpan.FromSeconds(3);
        Assert.True(await agent.AskedAsync("stop"));
        RemoteCaptureState state = await connection.CaptureStatusAsync(started.CaptureId, CancellationToken.None);
        Assert.Equal((RemoteCaptureLifecycle.Closed, "its controller's lease lapsed"), (state.State, state.Reason));
        Assert.Contains(agent.Audit.Entries, entry => entry.Outcome == "stopped: its controller's lease lapsed" && entry.Controller == controller.Pin);
    }

    [Fact(DisplayName = "§15: a capture that ends on its own frees the agent for the next, and an agent that stops stops the capture it holds")]
    public async Task TheAgentsCaptureEndsWithItOrWithTheAgent()
    {
        await using CaptureAgent agent = CaptureAgent.Start();
        using var controller = RemoteIdentity.Create("controller");
        await using RemoteAgentConnection connection = await agent.EnrolledAsync(controller, RemoteGrants.MetadataCapture);

        agent.Driver.ClosesAfterStatuses = 1;
        RemotePreparedCapture first = await connection.PrepareCaptureAsync(new RemoteCaptureRequest("explore", 5, 64), CancellationToken.None);
        RemoteCaptureStarted ended = await connection.StartCaptureAsync(first.Prepared, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal(RemoteCaptureLifecycle.Closed, (await connection.CaptureStatusAsync(ended.CaptureId, CancellationToken.None)).State);

        agent.Driver.ClosesAfterStatuses = int.MaxValue;
        RemotePreparedCapture second = await connection.PrepareCaptureAsync(new RemoteCaptureRequest("explore", 5, 64), CancellationToken.None);
        _ = await connection.StartCaptureAsync(second.Prepared, TimeSpan.FromSeconds(10), CancellationToken.None);
        int stopsBefore = agent.Driver.Calls.Count(call => call == "stop");

        await agent.Server.DisposeAsync();
        await agent.Captures.DisposeAsync();
        Assert.Equal(stopsBefore + 1, agent.Driver.Calls.Count(call => call == "stop"));
        Assert.Equal("dispose", agent.Driver.Calls[^1]);
        Assert.Contains(agent.Audit.Entries, entry => entry.Outcome == "stopped: the agent stopped");
    }
}
