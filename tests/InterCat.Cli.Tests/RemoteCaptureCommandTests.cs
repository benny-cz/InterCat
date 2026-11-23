using System.Net;
using System.Text.Json;
using InterCat.Application;
using InterCat.Capture.Journal.Tests;
using InterCat.Domain;
using InterCat.Remote;
using InterCat.Storage;
using Xunit;
using static System.StringComparison;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat remote capture (`remote-agent-v1` §4.4): a capture on an enrolled agent's host, its lease kept while it records,
/// stopped on Ctrl+C, and its start's acknowledgment kept as a barrier with its round trip; and icat remote fetch (§4.5),
/// which brings what it recorded into a folder of its own for icat follow to derive. The agent runs in this process on
/// loopback, its host's capture a stand-in: no test here launches a broker or records.
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

    [Fact(DisplayName = "§15: icat remote fetch brings a capture's evidence from its agent's host whole - every file checked, the folder opened as the capture's evidence - and icat follow derives from it the session the evidence derives where it was recorded")]
    public async Task IcatRemoteFetchBringsTheEvidenceToDeriveItsSession()
    {
        await using var agent = await LoopbackAgent.EnrolledAsync(RemoteGrants.MetadataCapture | RemoteGrants.Download, new StoreEvidenceSource());
        string evidence = Path.Combine(agent.Root, "capture-evidence");
        Directory.CreateDirectory(evidence);
        _ = await EvidenceRecordings.RecordEvidence(evidence, ordinals: [1, 2, 3, 4]);
        SessionManifestV1 recorded = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidence)).Current!;
        string[] before = Listed(evidence);
        agent.Driver.StartsAs = recorded.SessionId;
        agent.Driver.Evidence = evidence;
        agent.Driver.ClosesAfterStatuses = 2;
        (InterCatExitCode code, _, string said) = await Run(CancellationToken.None,
            "remote", "capture", agent.Endpoint, "--duration", "30", "--lease", "3", "--state", agent.Controller);
        Assert.True(code == InterCatExitCode.Success, said);

        string fetched = Path.Combine(agent.Root, "fetched");
        (code, string text, said) = await Run(CancellationToken.None,
            "remote", "fetch", agent.Endpoint, recorded.SessionId.ToString("N"), fetched, "--state", agent.Controller, "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        JsonElement fetch = JsonDocument.Parse(text).RootElement;
        Assert.Equal(("remote-fetch-v1", recorded.Generation, recorded.Dependencies.Count + 1, false),
            (fetch.GetProperty("contract").GetString(), fetch.GetProperty("generation").GetInt64(), fetch.GetProperty("files").GetInt32(),
            fetch.GetProperty("alreadyFetched").GetBoolean()));
        Assert.Equal(fetch.GetProperty("bytes").GetInt64(), fetch.GetProperty("fetchedBytes").GetInt64());
        Assert.False(Directory.Exists(fetched + ".partial"));

        // The agent read the evidence and wrote nothing to it.
        Assert.Equal(before, Listed(evidence));

        // The fetched folder derives the session the evidence derives where it was recorded.
        (code, text, said) = await Run(CancellationToken.None, "follow", fetched, Path.Combine(agent.Root, "from-fetched"), "--once", "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        JsonElement fromFetched = JsonDocument.Parse(text).RootElement;
        (code, text, said) = await Run(CancellationToken.None, "follow", evidence, Path.Combine(agent.Root, "from-source"), "--once", "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        JsonElement fromSource = JsonDocument.Parse(text).RootElement;
        Assert.True(fromFetched.GetProperty("finished").GetBoolean());
        Assert.Equal((fromSource.GetProperty("evidenceChunks").GetInt32(), fromSource.GetProperty("derivedRecords").GetInt64()),
            (fromFetched.GetProperty("evidenceChunks").GetInt32(), fromFetched.GetProperty("derivedRecords").GetInt64()));

        // Fetched again, it holds the evidence already; and a folder holding something else is not written over.
        (code, text, said) = await Run(CancellationToken.None,
            "remote", "fetch", agent.Endpoint, recorded.SessionId.ToString("D"), fetched, "--state", agent.Controller, "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.True(JsonDocument.Parse(text).RootElement.GetProperty("alreadyFetched").GetBoolean());
        (code, _, said) = await Run(CancellationToken.None,
            "remote", "fetch", agent.Endpoint, recorded.SessionId.ToString("D"), Path.Combine(agent.Root, "from-source"), "--state", agent.Controller);
        Assert.Equal(InterCatExitCode.PermissionOrCapabilityFailure, code);
        Assert.Contains("holds something else already", said, Ordinal);
    }

    [Fact(DisplayName = "§15: icat remote fetch says why it fetches nothing - a controller not granted download, a capture not named, evidence that is another capture's - and publishes no folder")]
    public async Task IcatRemoteFetchSaysWhyItCannot()
    {
        await using var agent = await LoopbackAgent.EnrolledAsync(RemoteGrants.MetadataCapture, new StoreEvidenceSource());
        string folder = Path.Combine(agent.Root, "fetched");
        (InterCatExitCode code, _, string said) = await Run(CancellationToken.None,
            "remote", "fetch", agent.Endpoint, Guid.NewGuid().ToString("D"), folder, "--state", agent.Controller);
        Assert.Equal(InterCatExitCode.PermissionOrCapabilityFailure, code);
        Assert.Contains("not granted download", said, Ordinal);
        Assert.False(Directory.Exists(folder));

        (code, _, said) = await Run(CancellationToken.None, "remote", "fetch", agent.Endpoint, "not-a-capture", folder, "--state", agent.Controller);
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("icat remote fetch 192.0.2.10:7457 <capture> <folder>", said, Ordinal);

        // Evidence whose session is not the capture's is every file as listed, yet not the capture's: it is not published.
        await using var serving = await LoopbackAgent.EnrolledAsync(RemoteGrants.MetadataCapture | RemoteGrants.Download, new StoreEvidenceSource());
        string evidence = Path.Combine(serving.Root, "capture-evidence");
        Directory.CreateDirectory(evidence);
        _ = await EvidenceRecordings.RecordEvidence(evidence, ordinals: [1, 2]);
        serving.Driver.Evidence = evidence;
        serving.Driver.ClosesAfterStatuses = 2;
        (code, string text, said) = await Run(CancellationToken.None,
            "remote", "capture", serving.Endpoint, "--duration", "30", "--lease", "3", "--state", serving.Controller, "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        string capture = JsonDocument.Parse(text).RootElement.GetProperty("captureId").GetString()!;
        string elsewhere = Path.Combine(serving.Root, "fetched");
        (code, _, said) = await Run(CancellationToken.None, "remote", "fetch", serving.Endpoint, capture, elsewhere, "--state", serving.Controller);
        Assert.Equal(InterCatExitCode.PermissionOrCapabilityFailure, code);
        Assert.Contains("yet it does not verify as the capture's evidence: it is the evidence of another capture", said, Ordinal);
        Assert.False(Directory.Exists(elsewhere));
    }

    [Fact(DisplayName = "§15: icat remote capture records on several agents at once - one barrier, each host's acknowledgment kept with its round trip and its clock against the first host's - and says each host's part")]
    public async Task IcatRemoteCaptureRecordsOnSeveralAgentsAtOnce()
    {
        await using var first = await LoopbackAgent.EnrolledAsync();
        await using var second = await LoopbackAgent.EnrolledAsync();
        await second.EnrollAsync(first.Controller);
        first.Driver.ClosesAfterStatuses = 8;
        second.Driver.ClosesAfterStatuses = 8;

        (InterCatExitCode code, string text, string said) = await Run(CancellationToken.None,
            "remote", "capture", first.Endpoint, second.Endpoint, "--duration", "30", "--lease", "3", "--state", first.Controller, "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        JsonElement capture = JsonDocument.Parse(text).RootElement;
        Assert.Equal(("remote-coordinated-capture-v1", 2, first.Endpoint), (capture.GetProperty("contract").GetString(),
            capture.GetProperty("startedHosts").GetInt32(), capture.GetProperty("reference").GetString()));
        JsonElement barrier = capture.GetProperty("barrier");
        Assert.True(barrier.GetProperty("sentUtc").GetDateTime() <= barrier.GetProperty("receivedUtc").GetDateTime());
        JsonElement[] hosts = [.. capture.GetProperty("hosts").EnumerateArray()];
        Assert.Equal([first.Endpoint, second.Endpoint], hosts.Select(host => host.GetProperty("endpoint").GetString()));
        Assert.All(hosts, host => Assert.Equal(("closed", true, JsonValueKind.Null), (host.GetProperty("state").GetString(),
            host.GetProperty("finalized").GetBoolean(), host.GetProperty("failure").ValueKind)));
        Assert.All(hosts, host => Assert.True(host.GetProperty("end").GetProperty("withinMilliseconds").GetDouble() > 0));
        Assert.Equal(JsonValueKind.Null, hosts[0].GetProperty("clock").ValueKind);
        JsonElement clock = hosts[1].GetProperty("clock");
        Assert.True(Math.Abs(clock.GetProperty("aheadMilliseconds").GetDouble()) <= clock.GetProperty("withinMilliseconds").GetDouble(),
            "Two agents on one host read one clock, within the bound their round trips leave.");
        Assert.Contains("Ctrl+C stops every host", said, Ordinal);
    }

    [Fact(DisplayName = "§15: icat remote capture across several agents records on those it reaches, says which it did not and why, and names each agent once")]
    public async Task IcatRemoteCaptureSaysWhichHostsDidNotRecord()
    {
        await using var first = await LoopbackAgent.EnrolledAsync();
        await using var gone = await LoopbackAgent.EnrolledAsync();
        await gone.EnrollAsync(first.Controller);
        await gone.StopServingAsync();
        first.Driver.ClosesAfterStatuses = 8;

        (InterCatExitCode code, string text, string said) = await Run(CancellationToken.None,
            "remote", "capture", first.Endpoint, gone.Endpoint, "--duration", "30", "--lease", "3", "--state", first.Controller, "--json");
        Assert.True(code == InterCatExitCode.PartialResultSuccess, said);
        JsonElement[] hosts = [.. JsonDocument.Parse(text).RootElement.GetProperty("hosts").EnumerateArray()];
        Assert.Equal("closed", hosts[0].GetProperty("state").GetString());
        Assert.Equal(("connect", JsonValueKind.Null), (hosts[1].GetProperty("failure").GetProperty("stage").GetString(),
            hosts[1].GetProperty("captureId").ValueKind));

        (code, text, said) = await Run(CancellationToken.None,
            "remote", "capture", first.Endpoint, gone.Endpoint, "--duration", "30", "--lease", "3", "--state", first.Controller);
        Assert.Equal(InterCatExitCode.PartialResultSuccess, code);
        Assert.Matches(@$"(?m)^  capture-host +not reached: No agent answered as one", text);
        Assert.Contains("Started", text, Ordinal);

        (code, _, said) = await Run(CancellationToken.None, "remote", "capture", first.Endpoint, first.Endpoint, "--state", first.Controller);
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("Each agent is named once", said, Ordinal);
    }

    [Fact(DisplayName = "§15: icat remote collect gathers a capture across several agents into an investigation - each host's evidence fetched, its session derived and added, every host's part recorded and a host that recorded nothing kept in view - collecting again fetches and records nothing anew, and two hosts collected align by the clocks the capture read")]
    public async Task IcatRemoteCollectGathersAnInvestigation()
    {
        await using var first = await LoopbackAgent.EnrolledAsync(RemoteGrants.MetadataCapture | RemoteGrants.Download, new StoreEvidenceSource());
        await using var second = await LoopbackAgent.EnrolledAsync(RemoteGrants.MetadataCapture | RemoteGrants.Download, new StoreEvidenceSource());
        await using var gone = await LoopbackAgent.EnrolledAsync();
        await second.EnrollAsync(first.Controller, RemoteGrants.MetadataCapture | RemoteGrants.Download);
        await gone.EnrollAsync(first.Controller);
        await gone.StopServingAsync();
        var sessions = new List<Guid>();
        foreach (LoopbackAgent agent in new[] { first, second })
        {
            string evidence = Path.Combine(agent.Root, "capture-evidence");
            Directory.CreateDirectory(evidence);
            _ = await EvidenceRecordings.RecordEvidence(evidence, ordinals: [1, 2, 3], calibration: EvidenceRecordings.Calibration());
            Guid session = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidence)).Current!.SessionId;
            sessions.Add(session);
            agent.Driver.StartsAs = session;
            agent.Driver.Evidence = evidence;
            agent.Driver.ClosesAfterStatuses = 8;
        }

        (InterCatExitCode code, string text, string said) = await Run(CancellationToken.None, "remote", "capture", first.Endpoint,
            second.Endpoint, gone.Endpoint, "--duration", "30", "--lease", "3", "--state", first.Controller, "--json");
        Assert.True(code == InterCatExitCode.PartialResultSuccess, said);
        string record = Path.Combine(first.Root, "capture.json");
        File.WriteAllText(record, text);

        string folder = Path.Combine(first.Root, "investigation");
        (code, text, said) = await Run(CancellationToken.None, "remote", "collect", record, folder, "--state", first.Controller, "--json");
        Assert.True(code == InterCatExitCode.PartialResultSuccess, said);
        JsonElement collection = JsonDocument.Parse(text).RootElement;
        Assert.Equal(("remote-collection-v1", 2, 1), (collection.GetProperty("contract").GetString(),
            collection.GetProperty("collectedHosts").GetInt32(), collection.GetProperty("revision").GetInt32()));
        JsonElement[] hosts = [.. collection.GetProperty("hosts").EnumerateArray()];
        Assert.Equal(["Collected", "Collected", "NotRecorded"], hosts.Select(host => host.GetProperty("part").GetString()));
        Assert.Equal("connect", hosts[2].GetProperty("stage").GetString());

        string workspace = collection.GetProperty("workspace").GetString()!;
        InvestigationWorkspaceFile investigation = InvestigationWorkspace.Read(workspace);
        Assert.Equal(sessions.Order(), investigation.Members.Select(member => member.SessionId).Order());
        WorkspaceCoordination kept = Assert.Single(InvestigationWorkspace.CoordinationsInForce(investigation));
        Assert.All(kept.Hosts.Take(2), host => Assert.True(host is { Part: WorkspaceHostPart.Collected, Start: not null, End: not null }));
        Assert.True(kept.Hosts[2] is { Part: WorkspaceHostPart.NotRecorded, CaptureId: null, Stage: "connect" });
        Assert.DoesNotContain(gone.Endpoint, File.ReadAllText(workspace), Ordinal);

        // The investigation keeps the host that recorded nothing in view.
        (_, text, _) = await Run(CancellationToken.None, "workspace", "show", workspace);
        Assert.Contains("CAPTURED ACROSS HOSTS", text, Ordinal);
        Assert.Matches(@"(?m)^  capture-host +recorded nothing - connect: ", text);

        // Collected again, nothing is fetched or recorded anew.
        (code, text, said) = await Run(CancellationToken.None, "remote", "collect", record, folder, "--state", first.Controller, "--json");
        Assert.True(code == InterCatExitCode.PartialResultSuccess, said);
        JsonElement again = JsonDocument.Parse(text).RootElement;
        Assert.Equal(JsonValueKind.Null, again.GetProperty("revision").ValueKind);
        Assert.All(again.GetProperty("hosts").EnumerateArray().Take(2), host => Assert.Equal(0, host.GetProperty("fetchedBytes").GetInt64()));
        Assert.Single(InvestigationWorkspace.Read(workspace).Coordinations);

        // The two hosts collected align by the clocks the capture read at its start.
        (code, _, said) = await Run(CancellationToken.None, "workspace", "align", workspace, sessions[1].ToString("N")[..8],
            sessions[0].ToString("N")[..8], "--barrier", "--drift-ppm", "50");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("a barrier, never proof that the clocks agree", said, Ordinal);
        WorkspaceAlignment aligned = Assert.Single(InvestigationWorkspace.Read(workspace).Alignments);
        Assert.Equal((WorkspaceAlignmentMode.Barrier, (Guid?)kept.CoordinationId, (Guid?)sessions[0]),
            (aligned.Mode, aligned.CoordinationId, aligned.ReferenceSessionId));
        (code, _, said) = await Run(CancellationToken.None, "workspace", "align", workspace, sessions[1].ToString("N")[..8],
            sessions[0].ToString("N")[..8], "--barrier");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("Use icat workspace align <workspace> <session> <reference> --barrier --drift-ppm <rate>.", said, Ordinal);
    }

    [Fact(DisplayName = "§15: icat remote collect takes only what icat remote capture --json wrote of a capture across several agents")]
    public async Task IcatRemoteCollectTakesOnlyACaptureAcrossAgents()
    {
        await using var agent = await LoopbackAgent.EnrolledAsync();
        string record = Path.Combine(agent.Root, "capture.json");
        File.WriteAllText(record, "{\"contract\":\"remote-capture-v1\"}");
        (InterCatExitCode code, _, string said) = await Run(CancellationToken.None, "remote", "collect", record, Path.Combine(agent.Root, "case"),
            "--state", agent.Controller);
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("is not what icat remote capture --json wrote of a capture across several agents", said, Ordinal);
        Assert.False(Directory.Exists(Path.Combine(agent.Root, "case")));
    }

    /// <summary>The files of <paramref name="folder"/>, each with its length and when it was last written.</summary>
    private static string[] Listed(string folder) =>
        [.. new DirectoryInfo(folder).EnumerateFiles().OrderBy(file => file.Name, StringComparer.Ordinal)
            .Select(file => $"{file.Name} {file.Length} {file.LastWriteTimeUtc.Ticks}")];

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

        private LoopbackAgent(IRemoteEvidenceSource? evidence)
        {
            Directory.CreateDirectory(root);
            captures = new RemoteCaptures(Driver, new RemoteCapturePolicy(600, 2_048, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(25)), evidence: evidence);
            server = new RemoteAgentServer(identity, enrollments, new RemoteAgentHost(Guid.NewGuid(), "capture-host", "Windows (test)", "1.0.0-test"),
                captures: captures);
            Endpoint = server.Start(new IPEndPoint(IPAddress.Loopback, 0)).ToString();
        }

        public StandInDriver Driver { get; } = new();

        public string Endpoint { get; }

        public string Controller => Path.Combine(root, "controller");

        /// <summary>A scratch folder of the test's own, removed with the agent.</summary>
        public string Root => root;

        /// <summary>Enrolls the controller keeping its folder at <paramref name="controller"/> with this agent too.</summary>
        public async Task EnrollAsync(string controller, RemoteGrants grants = RemoteGrants.MetadataCapture)
        {
            EnrollmentToken token = enrollments.Issue(identity.Pin, grants);
            (InterCatExitCode code, _, string said) = await Run(CancellationToken.None, "remote", "enroll", Endpoint, token.ToString(), "--state", controller);
            Assert.True(code == InterCatExitCode.Success, said);
        }

        /// <summary>Stops listening, as an agent its administrator stopped, or a host gone from the network.</summary>
        public ValueTask StopServingAsync() => server.DisposeAsync();

        public static async Task<LoopbackAgent> EnrolledAsync(RemoteGrants grants = RemoteGrants.MetadataCapture, IRemoteEvidenceSource? evidence = null)
        {
            var agent = new LoopbackAgent(evidence);
            EnrollmentToken token = agent.enrollments.Issue(agent.identity.Pin, grants);
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

        /// <summary>The identity the host gives the capture it starts; a new one where not said.</summary>
        public Guid? StartsAs { get; set; }

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
            return Task.FromResult(new DriverPreparation(Guid.NewGuid(), request.Profile, request.Seconds, request.JournalMebibytes,
                "Process lifecycle and TCP transport events; no payload contents."));
        }

        public Task<DriverStart> StartAsync(Guid prepared, CancellationToken cancellationToken)
        {
            Call("start");
            return Task.FromResult(new DriverStart(StartsAs ?? Guid.NewGuid(), Evidence));
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
