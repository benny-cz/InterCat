using System.Text.Json;
using System.Text.RegularExpressions;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;
using static System.StringComparison;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat ports (§6.3, §7.4, R18): the ports a session's processes accepted TCP connections at, each with the process that
/// accepted them and who connected - its processes, other hosts by address, or processes on this machine the capture did
/// not observe - the busiest first, read from the openings each process's card counts.
/// </summary>
public sealed class PortsCommandTests
{
    [Fact(DisplayName = "R18: icat ports lists the ports the session's processes accepted connections at, each with who connected, the busiest first")]
    public async Task IcatPortsListsWhoAcceptedConnectionsWhere()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        session.Store.ReleaseSegmentReaders();

        // The busiest first, ties by port: the helper's 7000, the server's 8080, its 8443, which other hosts reached, and
        // its 9000, which only processes it did not observe reached, then the probe's 8080 and the port a process connected
        // to itself at. A port's programs are named where there are three or fewer. A connection seen opening neither way,
        // a datagram flow and a connection made to another host are no one's listening.
        (InterCatExitCode code, string text, string said) = await Run("ports", session.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("LISTENING PORTS", text, Ordinal);
        Assert.Matches(@"(?m)^  Ports +5 ports, at which 4 processes accepted 19 TCP connections\r?$", text);
        Assert.Matches(@"(?m)^  Ranked by +the connections each accepted, the most first, then by port\r?$", text);
        Assert.Matches(@"(?m)^  Port +Accepted by +Connections +From\r?$", text);
        Assert.Equal(
            [
                "7000 | helper.exe · PID 300 | 5 | 4 from 4 processes of 4 programs · 1 from 10.0.0.6",
                "8080 | server.exe · PID 200 | 4 | 4 from 3 processes of probe.exe, client.exe and helper.exe",
                "8443 | server.exe · PID 200 | 4 | 4 from 3 hosts",
                "9000 | server.exe · PID 200 | 4 | 4 from unobserved processes on this machine",
                "8080 | probe.exe · PID 500 | 1 | 1 from client.exe · PID 100",
                "52011 | pipe.exe · PID 400 | 1 | 1 from pipe.exe · PID 400 (itself)",
            ],
            Listed(text));
        Assert.Contains("Each port is the process's own, which the TCP connections it accepted arrived at, as the capture saw "
            + "them open, whoever connected", Regex.Replace(text, @"\s+", " "), Ordinal);
        Assert.DoesNotContain("more, quieter", text, Ordinal);

        // As many as asked, and how many more there are.
        (code, text, said) = await Run("ports", session.Path, "--limit", "2");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Equal(2, Listed(text).Length);
        Assert.Contains("4 more, quieter; --limit 6 lists every one.", text, Ordinal);
        (code, text, said) = await Run("ports", session.Path, "--limit", "2", "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Equal((5, 6, 2), (JsonDocument.Parse(text).RootElement.GetProperty("ports").GetInt32(),
            JsonDocument.Parse(text).RootElement.GetProperty("listeners").GetInt32(),
            JsonDocument.Parse(text).RootElement.GetProperty("listed").GetArrayLength()));
        foreach (string asked in new[] { "0", "two" })
        {
            (code, _, said) = await Run("ports", session.Path, "--limit", asked);
            Assert.Equal(InterCatExitCode.InvalidInvocation, code);
            Assert.Contains($"--limit expects a number of listeners of 1 or more; '{asked}' is not one.", said, Ordinal);
        }

        // The JSON names every process and host that connected, with how many each made.
        (code, text, said) = await Run("ports", session.Path, "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        JsonElement root = JsonDocument.Parse(text).RootElement;
        Assert.Equal(("listening-ports-v1", 5, 6), (root.GetProperty("contract").GetString(), root.GetProperty("ports").GetInt32(),
            root.GetProperty("listeners").GetInt32()));
        JsonElement[] listed = [.. root.GetProperty("listed").EnumerateArray()];
        Assert.Equal(
            [
                "7000 300 helper.exe 5: 4 · 1 · 0 [100 client.exe 1, 500 probe.exe 1, 400 pipe.exe 1, 200 server.exe 1] [10.0.0.6 1]",
                "8080 200 server.exe 4: 4 · 0 · 0 [500 probe.exe 2, 100 client.exe 1, 300 helper.exe 1] []",
                "8443 200 server.exe 4: 0 · 4 · 0 [] [10.0.0.20 2, 10.0.0.10 1, 10.0.0.9 1]",
                "9000 200 server.exe 4: 0 · 0 · 4 [] []",
                "8080 500 probe.exe 1: 1 · 0 · 0 [100 client.exe 1] []",
                "52011 400 pipe.exe 1: 1 · 0 · 0 [400 pipe.exe 1] []",
            ],
            listed.Select(listener => $"{listener.GetProperty("port").GetInt32()} {Described(listener.GetProperty("process"))} "
                + $"{listener.GetProperty("accepted").GetInt64()}: {listener.GetProperty("fromProcesses").GetInt64()} · "
                + $"{listener.GetProperty("fromHosts").GetInt64()} · {listener.GetProperty("fromUnseenLocalEnds").GetInt64()} ["
                + string.Join(", ", listener.GetProperty("connectors").EnumerateArray().Select(connector =>
                    $"{Described(connector.GetProperty("process"))} {connector.GetProperty("connections").GetInt64()}"))
                + "] ["
                + string.Join(", ", listener.GetProperty("hosts").EnumerateArray().Select(host =>
                    $"{host.GetProperty("address").GetString()} {host.GetProperty("connections").GetInt64()}"))
                + "]"));
        Assert.Matches("^[0-9a-f]{32}$", listed[0].GetProperty("process").GetProperty("instanceId").GetString()!);

        // Its help says what it lists.
        (_, text, _) = await Run("ports", "--help");
        Assert.Contains("The ports this machine's processes accepted TCP connections at, as the capture saw them open",
            Regex.Replace(text, @"\s+", " "), Ordinal);

        static string Described(JsonElement process) =>
            $"{process.GetProperty("processId").GetInt32()} {process.GetProperty("name").GetString()}";
    }

    [Fact(DisplayName = "R18: icat ports --program lists one program's processes' listening ports, as its inspector does, and refuses a program it cannot name")]
    public async Task IcatPortsListsOneProgramsPorts()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        session.Store.ReleaseSegmentReaders();

        // The server's program: its process's three ports, the program named above them.
        (InterCatExitCode code, string text, string said) = await Run("ports", session.Path, "--program", "server.exe");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Program +server\.exe · 1 process\r?$", text);
        Assert.Matches(@"(?m)^  Ports +3 ports, at which 1 process accepted 12 TCP connections\r?$", text);
        Assert.Equal(
            [
                "8080 | server.exe · PID 200 | 4 | 4 from 3 processes of probe.exe, client.exe and helper.exe",
                "8443 | server.exe · PID 200 | 4 | 4 from 3 hosts",
                "9000 | server.exe · PID 200 | 4 | 4 from unobserved processes on this machine",
            ],
            Listed(text));

        // Its document names the program, and the machine's names none.
        (code, text, said) = await Run("ports", session.Path, "--program", "server.exe", "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        JsonElement root = JsonDocument.Parse(text).RootElement;
        Assert.Equal(("server.exe", 1, 3, 3), (root.GetProperty("program").GetProperty("name").GetString(),
            root.GetProperty("program").GetProperty("processes").GetInt32(), root.GetProperty("ports").GetInt32(),
            root.GetProperty("listeners").GetInt32()));
        (_, text, _) = await Run("ports", session.Path, "--json");
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(text).RootElement.GetProperty("program").ValueKind);

        // A program whose processes accepted nothing says so by its name.
        (code, text, said) = await Run("ports", session.Path, "--program", "client.exe");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("No process of client.exe accepted a TCP connection the capture saw open", Regex.Replace(text, @"\s+", " "),
            Ordinal);
        Assert.Empty(Listed(text));

        // One the session does not hold, or no name at all, is refused.
        (code, _, said) = await Run("ports", session.Path, "--program", "nothing.exe");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("No program of this session is named nothing.exe", said, Ordinal);
        (code, _, said) = await Run("ports", session.Path, "--program", " ");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("--program takes a program's name as icat programs lists it", said, Ordinal);

        // A folder that is not there is said to be not there.
        string nowhere = Path.Combine(Path.GetTempPath(), "icat-ports-" + Guid.NewGuid().ToString("N"));
        (code, _, said) = await Run("ports", nowhere);
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains($"No session directory at {nowhere}.", said, Ordinal);

        // A name two programs share is refused, saying how each is listed; named as listed, one is, with each of its
        // processes' ports.
        using var twins = new TemporarySession();
        Publish(twins.Store,
        [
            Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Apps\quiet.exe" },
            Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\quiet.exe" },
            Lifecycle(3, ObservationKind.Create, 201, 3) with { ResourceName = @"C:\Tools\quiet.exe" },
            Accepted(10, 100, "192.168.1.10:445", "10.0.0.6:51000"),
            Accepted(11, 200, "192.168.1.10:3389", "10.0.0.7:51000"),
            Accepted(12, 201, "192.168.1.10:3390", "10.0.0.8:51000"),
        ]);
        twins.Store.ReleaseSegmentReaders();
        (code, text, said) = await Run("ports", twins.Path, "--program", "quiet.exe");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("--program quiet.exe names 2 programs - quiet.exe (Apps) and quiet.exe (Tools) - so name one as they are listed.",
            Regex.Replace(said + text, @"\s+", " "), Ordinal);
        (code, text, said) = await Run("ports", twins.Path, "--program", "quiet.exe (Tools)");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Program +quiet\.exe \(Tools\) · 2 processes\r?$", text);
        Assert.Equal(["3389 | quiet.exe · PID 200 | 1 | 1 from 10.0.0.7", "3390 | quiet.exe · PID 201 | 1 | 1 from 10.0.0.8"],
            Listed(text));
        (code, text, said) = await Run("ports", twins.Path, "--program", "quiet.exe (Tools)", "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Equal(2, JsonDocument.Parse(text).RootElement.GetProperty("program").GetProperty("processes").GetInt32());

        // Its help says what it takes.
        (_, text, _) = await Run("ports", "--help");
        Assert.Contains("--program takes one program's processes alone", Regex.Replace(text, @"\s+", " "), Ordinal);
    }

    [Fact(DisplayName = "R18: icat ports says no process accepted a connection where every one the capture saw open was made, and refuses a session that records no open")]
    public async Task IcatPortsSaysWhyItListsNone()
    {
        // The client connected to another host and to nothing else.
        using var outbound = new TemporarySession();
        Publish(outbound.Store,
        [
            Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Apps\client.exe" },
            Transfer(10, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100).Between("192.168.1.10:52000", "203.0.113.1:443"),
        ]);
        outbound.Store.ReleaseSegmentReaders();
        (InterCatExitCode code, string text, string said) = await Run("ports", outbound.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("No process of this session accepted a TCP connection the capture saw open: each it saw open was made to "
            + "an end no record holds, most often another host's.", Regex.Replace(text, @"\s+", " "), Ordinal);
        Assert.DoesNotContain("Accepted by", text, Ordinal);
        (code, text, said) = await Run("ports", outbound.Path, "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Equal(0, JsonDocument.Parse(text).RootElement.GetProperty("listed").GetArrayLength());

        // A session whose capture recorded no open cannot say who accepted any connection.
        using var unopened = new TemporarySession();
        Publish(unopened.Store,
        [
            Transfer(12, ObservationKind.Send, AccountingSide.SendSide, 8, 100).Between("127.0.0.1:50000", "127.0.0.1:8080"),
            Transfer(13, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200).Between("127.0.0.1:8080", "127.0.0.1:50000"),
        ]);
        unopened.Store.ReleaseSegmentReaders();
        foreach (string[] args in new[] { new[] { "ports", unopened.Path }, ["ports", unopened.Path, "--json"] })
        {
            (code, text, said) = await Run(args);
            Assert.Equal((InterCatExitCode.PermissionOrCapabilityFailure, string.Empty), (code, text));
            Assert.Contains("This session records no connection's open, so at which ports its processes accepted connections is "
                + "unknown rather than none.", Regex.Replace(said, @"\s+", " "), Ordinal);
        }
    }

    [Fact(DisplayName = "R18: icat ports --investigation names, listener by listener, the process of another member's capture whose connection mirrors one it accepted, and refuses an investigation it cannot use")]
    public async Task IcatPortsNamesCandidateOtherEnds()
    {
        string folder = Path.Combine(Path.GetTempPath(), "InterCat.Cli.Tests.Ports", Guid.NewGuid().ToString("N"));
        try
        {
            InterCat.Application.DemoInvestigationResult demo = InterCat.Application.DemoInvestigation.Create(Path.Combine(folder, "demo"));

            // The client's browser connected to the server's api at its 443 and the client's sync to its files at 8443, where
            // the client's address names both; the db, which only the api connected to, has no other host to name an end of.
            (InterCatExitCode code, string text, string said) = await Run("ports", demo.ServerSession, "--investigation",
                demo.WorkspacePath);
            Assert.True(code == InterCatExitCode.Success, said);
            Assert.Matches(@"(?m)^  Port +Accepted by +Connections +From +Candidate other ends\r?$", text);
            Assert.Equal(
                [
                    "443 | api.exe · PID 5100 | 1 | 1 from 10.0.0.10 | demo client: browser.exe · PID 4100",
                    "5432 | db.exe · PID 5200 | 1 | 1 from api.exe · PID 5100 | -",
                    "8443 | files.exe · PID 5300 | 1 | 1 from 10.0.0.10 | demo client: sync.exe · PID 4200",
                ],
                [.. Regex.Matches(text, @"(?m)^  (\d+) +(\S+ · PID \d+) +(\d+) +(.+?)  +(\S.*?)\r?$")
                    .Select(match => string.Join(" | ", match.Groups.Values.Skip(1).Select(group => group.Value)))]);
            Assert.Contains("never an established join", Regex.Replace(text, @"\s+", " "), Ordinal);

            // As JSON, each listed listener's candidate other ends; read without an investigation, none is said.
            (code, text, _) = await Run("ports", demo.ServerSession, "--investigation", demo.WorkspacePath, "--json");
            Assert.Equal(InterCatExitCode.Success, code);
            JsonElement[] listed = [.. JsonDocument.Parse(text).RootElement.GetProperty("listed").EnumerateArray()];
            Assert.Equal(["443: demo client browser.exe 4100 1 False", "5432: ", "8443: demo client sync.exe 4200 1 False"],
                listed.Select(listener => $"{listener.GetProperty("port").GetInt32()}: " + string.Join(", ",
                    listener.GetProperty("candidateOtherEnds").EnumerateArray().Select(end =>
                        $"{end.GetProperty("host").GetString()} {end.GetProperty("process").GetString()} "
                        + $"{end.GetProperty("processId").GetInt32()} {end.GetProperty("connections").GetInt32()} "
                        + end.GetProperty("accepted").GetBoolean()))));
            Assert.Equal(JsonValueKind.Null, JsonDocument.Parse((await Run("ports", demo.ServerSession, "--json")).Output).RootElement
                .GetProperty("listed")[0].GetProperty("candidateOtherEnds").ValueKind);

            // A join a person rejected names no end: the api's listener, which another host connected to, then has none.
            InterCat.Application.ConnectionCandidate api = InterCat.Application.WorkspaceCorrelation.Candidates(demo.WorkspacePath)
                .Candidates.Single(candidate => candidate.First.Connection.Holder.ProcessId == 5100
                    || candidate.Second.Connection.Holder.ProcessId == 5100);
            InterCat.Application.InvestigationWorkspace.Decide(demo.WorkspacePath, api.Ends.First, api.Ends.Second,
                InterCat.Application.WorkspaceJoinDecision.Rejected, null, DateTimeOffset.UtcNow);
            (code, text, said) = await Run("ports", demo.ServerSession, "--investigation", demo.WorkspacePath);
            Assert.True(code == InterCatExitCode.Success, said);
            Assert.Matches(@"(?m)^  443 +api\.exe · PID 5100 +1 +1 from 10\.0\.0\.10 +none\r?$", text);

            // An investigation the session is no member of, and a file that is none, are refused.
            using var stranger = new TemporarySession();
            Publish(stranger.Store, Rows());
            stranger.Store.ReleaseSegmentReaders();
            (code, _, said) = await Run("ports", stranger.Path, "--investigation", demo.WorkspacePath);
            Assert.Equal(InterCatExitCode.InvalidInvocation, code);
            Assert.Contains("This session is no member of ", said, Ordinal);
            string notOne = Path.Combine(folder, "not-an-investigation.json");
            File.WriteAllText(notOne, "{}");
            (code, _, said) = await Run("ports", demo.ServerSession, "--investigation", notOne);
            Assert.Equal(InterCatExitCode.InvalidInvocation, code);
            Assert.Contains("could not be read", said, Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// The probe connects to the server's 8080 twice and the client and the helper once each; three other hosts connect to
    /// its 8443, one twice; processes on this machine the capture did not observe connect to its 9000 over IPv4's and
    /// IPv6's loopback, an IPv4-mapped loopback address and the server's own address; the server, the client, the probe,
    /// the pipe and another host connect to the helper's 7000; the client connects to the probe's own 8080, at another
    /// address; and the pipe connects to itself. The client also sends to the helper's 6000 over a connection seen opening
    /// neither way, sends it a datagram, and connects to another host.
    /// </summary>
    private static ObservationRowV1[] Rows() =>
    [
        Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Apps\client.exe" },
        Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Apps\server.exe" },
        Lifecycle(3, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Apps\helper.exe" },
        Lifecycle(4, ObservationKind.Create, 400, 4) with { ResourceName = @"C:\Apps\pipe.exe" },
        Lifecycle(5, ObservationKind.Create, 500, 5) with { ResourceName = @"C:\Apps\probe.exe" },
        .. Connection(10, 100, "127.0.0.1:50000", 200, "127.0.0.1:8080"),
        .. Connection(20, 500, "127.0.0.1:50001", 200, "127.0.0.1:8080"),
        .. Connection(30, 500, "127.0.0.1:50002", 200, "127.0.0.1:8080"),
        .. Connection(35, 300, "127.0.0.1:50007", 200, "127.0.0.1:8080"),
        Accepted(40, 200, "192.168.1.10:8443", "10.0.0.9:51000"),
        Accepted(41, 200, "192.168.1.10:8443", "10.0.0.10:51000"),
        Accepted(42, 200, "192.168.1.10:8443", "10.0.0.20:51000"),
        Accepted(43, 200, "192.168.1.10:8443", "10.0.0.20:51001"),
        Accepted(50, 200, "127.0.0.1:9000", "127.0.0.1:52000"),
        Accepted(51, 200, "[::1]:9000", "[::1]:52001"),
        Accepted(52, 200, "[::ffff:192.168.1.10]:9000", "[::ffff:127.0.0.2]:52002"),
        Accepted(53, 200, "192.168.1.10:9000", "192.168.1.10:52003"),
        .. Connection(60, 200, "127.0.0.1:50003", 300, "127.0.0.1:7000"),
        .. Connection(63, 100, "127.0.0.1:50005", 300, "127.0.0.1:7000"),
        .. Connection(66, 500, "127.0.0.1:50008", 300, "127.0.0.1:7000"),
        .. Connection(70, 400, "127.0.0.1:50009", 300, "127.0.0.1:7000"),
        Accepted(73, 300, "192.168.1.10:7000", "10.0.0.6:51002"),
        .. Connection(75, 100, "127.0.0.1:50006", 500, "127.0.0.2:8080"),
        .. Connection(80, 400, "127.0.0.1:52010", 400, "127.0.0.1:52011"),
        Transfer(90, ObservationKind.Send, AccountingSide.SendSide, 8, 100).Between("127.0.0.1:50004", "127.0.0.1:6000"),
        Transfer(91, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 300).Between("127.0.0.1:6000", "127.0.0.1:50004"),
        Transfer(92, ObservationKind.Send, AccountingSide.SendSide, 4, 100).Between("127.0.0.1:53100", "127.0.0.1:5353")
            with { Mechanism = Mechanism.Udp, EventId = 42 },
        Transfer(93, ObservationKind.Receive, AccountingSide.ReceiveSide, 4, 300).Between("127.0.0.1:53100", "127.0.0.1:5353")
            with { Mechanism = Mechanism.Udp, EventId = 42 },
        Transfer(94, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100).Between("192.168.1.10:52020", "203.0.113.1:443"),
    ];

    /// <summary>One TCP connection <paramref name="maker"/> made to <paramref name="taker"/>, opened at its end, and a send across it.</summary>
    private static ObservationRowV1[] Connection(long at, int maker, string from, int taker, string to) =>
    [
        Transfer(at, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, maker).Between(from, to),
        Transfer(at + 1, ObservationKind.Send, AccountingSide.SendSide, 8, maker).Between(from, to),
        Transfer(at + 2, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, taker).Between(to, from),
    ];

    /// <summary>A connection <paramref name="taker"/> accepted at <paramref name="local"/>, whose other end no record holds.</summary>
    private static ObservationRowV1 Accepted(long at, int taker, string local, string remote) =>
        Transfer(at, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, taker).Between(local, remote);

    /// <summary>The table's rows: each port, the process that accepted there, its connections and who connected.</summary>
    private static string[] Listed(string text) =>
        [.. Regex.Matches(text, @"(?m)^  (\d+) +(\S+ · PID \d+) +(\d+) +(\d.*?)\r?$")
            .Select(match => string.Join(" | ", match.Groups.Values.Skip(1).Select(group => group.Value)))];

    private static async Task<(InterCatExitCode Code, string Output, string Error)> Run(params string[] args)
    {
        TextWriter output = Console.Out;
        TextWriter error = Console.Error;
        using var answered = new StringWriter();
        using var said = new StringWriter();
        Console.SetOut(answered);
        Console.SetError(said);
        try
        {
            InterCatExitCode code = await Icat.RunAsync(args, CancellationToken.None);
            return (code, answered.ToString(), said.ToString());
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
        }
    }
}
