using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using InterCat.Domain;
using InterCat.Remote;
using Xunit;
using static System.StringComparison;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat agent and icat remote (ADR-117): an agent its host's administrator runs and stops, and a controller that enrolls
/// with it by its token and lists the agents it enrolled with. Each runs on loopback, its folders under the run's
/// temporary folder.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class RemoteCommandTests
{
    [Fact(DisplayName = "§15: icat remote enroll enrolls with an agent by its token and keeps the agent, and icat remote hosts lists it and checks what it allows now - revoked once its administrator revokes this controller")]
    public async Task IcatRemoteEnrollsAndChecksItsAgents()
    {
        using var folders = new Folders();
        AgentState agent = AgentState.Open(folders.Agent, RemoteFolderOwners.CurrentUser);
        using RemoteIdentity identity = agent.Identity();
        var host = new RemoteAgentHost(Guid.NewGuid(), "far-host", "Windows (test)", "1.0.0-test");
        var server = new RemoteAgentServer(identity, agent.Enrollments, host, audit: agent.Audit);
        await using (server)
        {
            string endpoint = server.Start(new IPEndPoint(IPAddress.Loopback, 0)).ToString();
            EnrollmentToken token = agent.Enrollments.Issue(identity.Pin, RemoteGrants.MetadataCapture | RemoteGrants.Download);

            (InterCatExitCode code, string text, string said) = await Run("remote", "enroll", endpoint, token.ToString(), "--state", folders.Controller, "--json");
            Assert.True(code == InterCatExitCode.Success, said);
            JsonElement enrolled = JsonDocument.Parse(text).RootElement;
            Assert.Equal(("remote-enrollment-v1", identity.Pin.Hex, host.HostId, "far-host"),
                (enrolled.GetProperty("contract").GetString(), enrolled.GetProperty("agent").GetString(),
                enrolled.GetProperty("hostId").GetGuid(), enrolled.GetProperty("machineName").GetString()));
            Assert.Equal(["metadata-capture", "download"], enrolled.GetProperty("grants").EnumerateArray().Select(grant => grant.GetString()));
            RemotePin controller = RemotePin.TryParse(enrolled.GetProperty("controller").GetString(), out RemotePin pin) ? pin : default;
            Assert.Equal([controller], agent.Enrollments.Controllers.Select(entry => entry.Pin));

            (code, text, said) = await Run("remote", "hosts", "--state", folders.Controller, "--check");
            Assert.True(code == InterCatExitCode.Success, said);
            Assert.Contains("far-host", text, Ordinal);
            Assert.Contains("enrolled: metadata-capture, download", text, Ordinal);

            // Its administrator revokes this controller: the check says so.
            Assert.True(agent.Enrollments.Revoke(controller));
            (code, text, _) = await Run("remote", "hosts", "--state", folders.Controller, "--check", "--json");
            Assert.Equal(InterCatExitCode.Success, code);
            JsonElement listed = Assert.Single(JsonDocument.Parse(text).RootElement.GetProperty("agents").EnumerateArray());
            Assert.Equal(("revoked", identity.Pin.Hex), (listed.GetProperty("check").GetString(), listed.GetProperty("pin").GetString()));

            // A token used once is refused, and the controller says what to ask for.
            (code, _, said) = await Run("remote", "enroll", endpoint, token.ToString(), "--state", folders.Controller);
            Assert.Equal(InterCatExitCode.PermissionOrCapabilityFailure, code);
            Assert.Contains("revoked this controller's key", said, Ordinal);
        }

        // An address, not a name; a token whole.
        (InterCatExitCode refused, _, string why) = await Run("remote", "enroll", "far-host:7457", "icat-enroll-1.AAAA", "--state", folders.Controller);
        Assert.Equal((InterCatExitCode.InvalidInvocation, true), (refused, why.Contains("not a name", Ordinal)));
        (refused, _, why) = await Run("remote", "enroll", "127.0.0.1:7457", "icat-enroll-1.AAAA", "--state", folders.Controller);
        Assert.Equal((InterCatExitCode.InvalidInvocation, true), (refused, why.Contains("copied whole", Ordinal)));

        // The agent gone, its controller says it is unreachable rather than failing.
        (InterCatExitCode checkedCode, string checkedText, _) = await Run("remote", "hosts", "--state", folders.Controller, "--check");
        Assert.Equal(InterCatExitCode.Success, checkedCode);
        Assert.Contains("unreachable", checkedText, Ordinal);
    }

    [Fact(DisplayName = "§15: icat agent runs only from an elevated prompt; there it issues a token, serves the controller enrolled with it until stopped, and lists, revokes and audits it by its key")]
    public async Task IcatAgentServesUntilStopped()
    {
        using var folders = new Folders();
        if (!RemoteFolder.RunsAsAdministrator())
        {
            (InterCatExitCode code, _, string said) = await Run("agent", "token", "--root", folders.Agent);
            Assert.Equal(InterCatExitCode.PermissionOrCapabilityFailure, code);
            Assert.Contains("elevated prompt", said, Ordinal);
            Assert.False(Directory.Exists(folders.Agent));
            return;
        }

        (InterCatExitCode issued, string tokenJson, string issuedSaid) = await Run("agent", "token", "--root", folders.Agent, "--grant", "download", "--json");
        Assert.True(issued == InterCatExitCode.Success, issuedSaid);
        JsonElement tokenDocument = JsonDocument.Parse(tokenJson).RootElement;
        Assert.Equal("remote-agent-token-v1", tokenDocument.GetProperty("contract").GetString());
        Assert.True(EnrollmentToken.TryParse(tokenDocument.GetProperty("token").GetString(), out EnrollmentToken? token, out _));
        Assert.Null(RemoteFolder.Problem(new DirectoryInfo(folders.Agent), RemoteFolderOwners.Administrators));

        // Served until stopped: a controller enrolls while it runs.
        int port = FreePort();
        using var stop = new CancellationTokenSource();
        var served = new StringWriter();
        Task<InterCatExitCode> serving = RunInto(TextWriter.Synchronized(served), stop.Token, "agent", "serve", "--listen", $"127.0.0.1:{port}", "--root", folders.Agent);
        using var controller = RemoteIdentity.Create("controller");
        RemoteAgentConnection connection = await ConnectWhenListeningAsync(new IPEndPoint(IPAddress.Loopback, port), controller, token!.Agent);
        await using (connection)
        {
            RemoteEnrolled enrolled = await connection.EnrollAsync(token, CancellationToken.None);
            Assert.Equal(RemoteGrants.Download, enrolled.Grants);
        }

        await stop.CancelAsync();
        Assert.Equal(InterCatExitCode.Success, await serving);
        Assert.Contains("The agent stopped", served.ToString(), Ordinal);

        (InterCatExitCode listedCode, string listed, _) = await Run("agent", "controllers", "--root", folders.Agent, "--json");
        Assert.Equal(InterCatExitCode.Success, listedCode);
        Assert.Equal(controller.Pin.Hex, Assert.Single(JsonDocument.Parse(listed).RootElement.GetProperty("controllers").EnumerateArray()).GetProperty("pin").GetString());

        (InterCatExitCode revokedCode, _, string revoked) = await Run("agent", "revoke", controller.Pin.Hex[..16], "--root", folders.Agent);
        Assert.Equal(InterCatExitCode.Success, revokedCode);
        Assert.Contains(controller.Pin.Hex, revoked, Ordinal);
        Assert.Equal([controller.Pin], AgentState.Open(folders.Agent).Enrollments.Revoked);

        (InterCatExitCode auditCode, string audit, _) = await Run("agent", "audit", "--root", folders.Agent, "--json");
        Assert.Equal(InterCatExitCode.Success, auditCode);
        JsonElement entry = Assert.Single(JsonDocument.Parse(audit).RootElement.GetProperty("entries").EnumerateArray());
        Assert.Equal((controller.Pin.Hex, "enroll", "admitted: download"),
            (entry.GetProperty("controller").GetString(), entry.GetProperty("action").GetString(), entry.GetProperty("outcome").GetString()));
        Assert.DoesNotContain("127.0.0.1", audit, Ordinal);
    }

    [Fact(DisplayName = "§15: an agent's administrator rotates its own key - while it serves it tells each enrolled controller the next key, and once finished and started again the controller follows it there, taking the new key as the agent's")]
    public async Task AnAgentsOwnKeyIsRotated()
    {
        using var folders = new Folders();
        if (!RemoteFolder.RunsAsAdministrator())
        {
            return;
        }

        (InterCatExitCode code, string text, string said) = await Run("agent", "token", "--root", folders.Agent, "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        string token = JsonDocument.Parse(text).RootElement.GetProperty("token").GetString()!;
        int port = FreePort();
        string endpoint = $"127.0.0.1:{port}";
        RemotePin current;
        using (RemoteIdentity key = AgentState.Open(folders.Agent).Identity())
        {
            current = key.Pin;
        }

        await ServeWhileAsync(folders.Agent, port, current, async () =>
        {
            (code, _, said) = await Run("remote", "enroll", endpoint, token, "--state", folders.Controller);
            Assert.True(code == InterCatExitCode.Success, said);
        });

        // Begun: the next key is kept beside the agent's, and no controller was told it yet.
        (code, text, said) = await Run("agent", "rotate", "--root", folders.Agent, "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        JsonElement begun = JsonDocument.Parse(text).RootElement;
        string next = begun.GetProperty("next").GetString()!;
        Assert.Equal(("remote-agent-rotation-v1", current.Hex, false, 1), (begun.GetProperty("contract").GetString(), begun.GetProperty("key").GetString(),
            begun.GetProperty("finished").GetBoolean(), begun.GetProperty("untold").GetArrayLength()));

        // Serving with its key, the agent tells the controller its next key, which the controller keeps.
        await ServeWhileAsync(folders.Agent, port, current, async () =>
        {
            (code, _, said) = await Run("remote", "hosts", "--check", "--state", folders.Controller);
            Assert.True(code == InterCatExitCode.Success, said);
        });
        KnownAgent known = Assert.Single(ControllerState.Open(folders.Controller).Agents());
        Assert.Equal((current, next), (known.Pin, known.NextPin?.Hex));

        // Finished, and started again under the new key: the controller follows, taking it as the agent's.
        (code, text, said) = await Run("agent", "rotate", "--finish", "--root", folders.Agent, "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        JsonElement finished = JsonDocument.Parse(text).RootElement;
        Assert.Equal((true, 0), (finished.GetProperty("finished").GetBoolean(), finished.GetProperty("untold").GetArrayLength()));
        Assert.True(RemotePin.TryParse(next, out RemotePin rotated));
        await ServeWhileAsync(folders.Agent, port, rotated, async () =>
        {
            (code, text, said) = await Run("remote", "hosts", "--check", "--state", folders.Controller, "--json");
            Assert.True(code == InterCatExitCode.Success, said);
            Assert.StartsWith("enrolled", JsonDocument.Parse(text).RootElement.GetProperty("agents")[0].GetProperty("check").GetString(), Ordinal);
        });
        known = Assert.Single(ControllerState.Open(folders.Controller).Agents());
        Assert.Equal((rotated, (RemotePin?)null), (known.Pin, known.NextPin));
        Assert.Contains(JsonDocument.Parse((await Run("agent", "audit", "--root", folders.Agent, "--json")).Output).RootElement.GetProperty("entries")
            .EnumerateArray(), entry => entry.GetProperty("outcome").GetString()!.Contains("told its next key", StringComparison.Ordinal));
    }

    /// <summary>Serves the agent kept in <paramref name="root"/> on <paramref name="port"/> while <paramref name="act"/> runs, then stops it.</summary>
    private static async Task ServeWhileAsync(string root, int port, RemotePin key, Func<Task> act)
    {
        using var stop = new CancellationTokenSource();
        var served = new StringWriter();
        Task<InterCatExitCode> serving = RunInto(TextWriter.Synchronized(served), stop.Token, "agent", "serve", "--listen", $"127.0.0.1:{port}", "--root", root);
        using (var probe = RemoteIdentity.Create("controller"))
        {
            await using RemoteAgentConnection listening = await ConnectWhenListeningAsync(new IPEndPoint(IPAddress.Loopback, port), probe, key);
        }

        try
        {
            await act();
        }
        finally
        {
            await stop.CancelAsync();
            Assert.Equal(InterCatExitCode.Success, await serving);
        }
    }

    private static async Task<RemoteAgentConnection> ConnectWhenListeningAsync(IPEndPoint endpoint, RemoteIdentity controller, RemotePin agent)
    {
        DateTime giveUp = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            try
            {
                return await RemoteAgentConnection.ConnectAsync(endpoint, controller, agent, TimeSpan.FromSeconds(5), CancellationToken.None);
            }
            catch (RemoteAgentException) when (DateTime.UtcNow < giveUp)
            {
                await Task.Delay(100);
            }
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<(InterCatExitCode Code, string Output, string Error)> Run(params string[] args)
    {
        using var answered = new StringWriter();
        using var said = new StringWriter();
        InterCatExitCode code = await RunInto(answered, said, CancellationToken.None, args);
        return (code, answered.ToString(), said.ToString());
    }

    private static Task<InterCatExitCode> RunInto(TextWriter output, CancellationToken cancellationToken, params string[] args) =>
        RunInto(output, output, cancellationToken, args);

    private static async Task<InterCatExitCode> RunInto(TextWriter output, TextWriter error, CancellationToken cancellationToken, params string[] args)
    {
        TextWriter outputBefore = Console.Out;
        TextWriter errorBefore = Console.Error;
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            return await Icat.RunAsync(args, cancellationToken);
        }
        finally
        {
            Console.SetOut(outputBefore);
            Console.SetError(errorBefore);
        }
    }

    /// <summary>The agent's and the controller's folders, under the run's temporary folder, gone with the test.</summary>
    private sealed class Folders : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "icat-remote-cli-" + Guid.NewGuid().ToString("N"));

        public Folders() => Directory.CreateDirectory(root);

        public string Agent => Path.Combine(root, "agent");

        public string Controller => Path.Combine(root, "controller");

        public void Dispose()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
