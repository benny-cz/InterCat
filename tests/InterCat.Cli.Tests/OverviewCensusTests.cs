using System.Text.Json;
using System.Text.RegularExpressions;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;
using static System.StringComparison;

namespace InterCat.Cli.Tests;

/// <summary>
/// icat overview says why a session's graph draws no relationship in the words the window says beneath its empty graph
/// (§6.8, R18), lists the processes whose connections the window draws to Other hosts (§6.3), and its JSON carries the
/// census and the host links those words are made from.
/// </summary>
public sealed class OverviewCensusTests
{
    [Fact(DisplayName = "R18: icat overview says why the graph draws no relationship, as the window does, lists the processes reaching other hosts, and its JSON carries both")]
    public async Task IcatOverviewSaysWhyTheGraphDrawsNone()
    {
        using var remote = new TemporarySession();
        Publish(remote.Store,
        [
            Lifecycle(1, ObservationKind.Create, 300, 1) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 100 },
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 500, 300, 2)
                .Between("192.168.1.10:52000", "142.250.1.1:443") with { SessionRelativeTicks = 200 },
            Transfer(21, ObservationKind.Receive, AccountingSide.ReceiveSide, 900, 300, 3)
                .Between("192.168.1.10:52000", "142.250.1.1:443") with { SessionRelativeTicks = 210 },
        ]);
        remote.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string text, string said) = await Run("overview", remote.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Relationships +0\r?$", text);
        Assert.Contains("No TCP connection was seen with a process of this session at each of its two ends. All 2 of its TCP "
            + "records belong to 1 connection to 1 other host. A process's connections are listed on its rung.",
            Regex.Replace(text, @"\s+", " "), Ordinal);

        // The processes with connections to other hosts, as the window draws them to Other hosts: the browser's one
        // connection, none of them accepted, so no column says so.
        Assert.Matches(@"(?m)^  Other hosts +1 TCP connection to 1 host\r?$", text);
        Assert.Matches(@"(?m)^  Process +Connections with other hosts\r?$", text);
        Assert.Matches(@"(?m)^ *browser\.exe · PID 300 +1 TCP connection to 1 host\r?$", text);

        // And the hosts they ran to, the busiest first, by address, with who reached each and over what, the ports at it and
        // its records.
        Assert.Matches(@"(?m)^  Busiest other host +Processes +Connections +Ports +Records\r?$", text);
        Assert.Matches(@"(?m)^  142\.250\.1\.1 +browser\.exe · PID 300 +1 TCP connection +443 +2\r?$", text);
        Assert.Contains("1 other host in all, counted by address; who is at them is not observed. icat hosts <directory> lists every "
            + "one, and with --address <address> every process that reached one.", Regex.Replace(text, @"\s+", " "), Ordinal);

        (code, text, said) = await Run("overview", remote.Path, "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        JsonElement bundle = JsonDocument.Parse(text).RootElement.GetProperty("bundle");
        JsonElement tcp = bundle.GetProperty("census").GetProperty("tcp");
        Assert.Equal((2, 1, 1, 0), (tcp.GetProperty("toOtherHosts").GetInt64(), tcp.GetProperty("otherHostConnections").GetInt32(),
            tcp.GetProperty("otherHosts").GetInt32(), tcp.GetProperty("relationships").GetInt32()));
        JsonElement link = Assert.Single(bundle.GetProperty("hostLinks").EnumerateArray());
        Assert.Equal((1, 1, 2), (link.GetProperty("connections").GetInt32(), link.GetProperty("hosts").GetInt32(),
            link.GetProperty("records").GetInt64()));
        Assert.StartsWith("hosts:tcp:", link.GetProperty("key").GetString(), Ordinal);
        JsonElement address = Assert.Single(link.GetProperty("addresses").EnumerateArray());
        Assert.Equal(("142.250.1.1", 1, 2L), (address.GetProperty("address").GetString(), address.GetProperty("connections").GetInt32(),
            address.GetProperty("records").GetInt64()));
        Assert.Equal([443], address.GetProperty("ports").EnumerateArray().Select(port => port.GetInt32()));

        // A session whose graph draws a relationship says nothing of why it would draw none.
        using var paired = new TemporarySession();
        Publish(paired.Store,
        [
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 200 },
            Transfer(21, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 2)
                .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 210 },
        ]);
        paired.Store.ReleaseSegmentReaders();
        (code, text, said) = await Run("overview", paired.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Relationships +1\r?$", text);
        Assert.DoesNotContain("No TCP connection was seen", text, Ordinal);
        Assert.DoesNotContain("Connections with other hosts", text, Ordinal);
        Assert.DoesNotContain("RPC peers", text, Ordinal);

        // One that also holds RPC calls no capture linked says how to draw them, as the window's graph does.
        using var calls = new TemporarySession();
        (ObservationRowV1[] rpc, SourceFieldRowV1[] fields) = LinkedRpcCalls();
        Publish(calls.Store,
        [
            .. rpc,
            Transfer(500, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 900).Between("127.0.0.1:50000", "127.0.0.1:8080"),
            Transfer(501, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 901).Between("127.0.0.1:8080", "127.0.0.1:50000"),
        ], fields: fields, coverage: RpcLedger(alpc: false));
        calls.Store.ReleaseSegmentReaders();
        (code, text, said) = await Run("overview", calls.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Relationships +1\r?$", text);
        Assert.Contains("RPC calls are on their processes' rungs: a call is drawn to the process that served it only from a "
            + "capture that collects ALPC - choose RPC peers in the window's Keep, or run icat capture --profile rpc-peers.",
            Regex.Replace(text, @"\s+", " "), Ordinal);
    }

    [Fact(DisplayName = "§6.8: icat overview lists the busiest relationships, each arrow pointing at the process connected to, and the connections each process accepted from other hosts")]
    public async Task IcatOverviewSaysWhoConnected()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 100 },
            Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe", SessionRelativeTicks = 110 },
            Lifecycle(3, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Tools\helper.exe", SessionRelativeTicks = 120 },

            // The client connects to the server's 8080 and sends three times.
            Transfer(10, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 10)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 200 },
            .. Enumerable.Range(0, 3).SelectMany(index => new[]
            {
                Transfer(11 + (2 * index), ObservationKind.Send, AccountingSide.SendSide, 8, 100, (ulong)(11 + (2 * index)))
                    .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 210 + index },
                Transfer(12 + (2 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, (ulong)(12 + (2 * index)))
                    .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 220 + index },
            }),

            // The helper connects to the server's 8080, and the server to the helper's 9000.
            Transfer(30, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 300, 30)
                .Between("127.0.0.1:50001", "127.0.0.1:8080") with { SessionRelativeTicks = 300 },
            Transfer(31, ObservationKind.Send, AccountingSide.SendSide, 8, 300, 31)
                .Between("127.0.0.1:50001", "127.0.0.1:8080") with { SessionRelativeTicks = 310 },
            Transfer(32, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 32)
                .Between("127.0.0.1:8080", "127.0.0.1:50001") with { SessionRelativeTicks = 320 },
            Transfer(33, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 200, 33)
                .Between("127.0.0.1:50002", "127.0.0.1:9000") with { SessionRelativeTicks = 330 },
            Transfer(34, ObservationKind.Send, AccountingSide.SendSide, 8, 200, 34)
                .Between("127.0.0.1:50002", "127.0.0.1:9000") with { SessionRelativeTicks = 340 },
            Transfer(35, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 300, 35)
                .Between("127.0.0.1:9000", "127.0.0.1:50002") with { SessionRelativeTicks = 350 },

            // The client and the helper, seen opening nothing.
            Transfer(40, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 40)
                .Between("127.0.0.1:50003", "127.0.0.1:7070") with { SessionRelativeTicks = 400 },
            Transfer(41, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 300, 41)
                .Between("127.0.0.1:7070", "127.0.0.1:50003") with { SessionRelativeTicks = 410 },

            // The server accepts a connection from another host at its 445.
            Transfer(50, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 50)
                .Between("192.168.1.10:445", "10.0.0.6:50001") with { SessionRelativeTicks = 500 },
            Transfer(51, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 51)
                .Between("192.168.1.10:445", "10.0.0.6:50001") with { SessionRelativeTicks = 510 },
        ]);
        session.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string text, string said) = await Run("overview", session.Path);
        Assert.True(code == InterCatExitCode.Success, said);

        // The busiest first, each arrow pointing at the process connected to whichever end the graph draws first, both
        // ways where each connected, and "and" where neither was seen to.
        Assert.Matches(@"(?m)^  Busiest relationship +Over +Records\r?$", text);
        Assert.Matches(@"(?m)^  client\.exe · PID 100 → server\.exe · PID 200 +TCP at 8080 +7\r?$", text);
        Assert.Matches(@"(?m)^  (helper\.exe · PID 300 ⇄ server\.exe · PID 200|server\.exe · PID 200 ⇄ helper\.exe · PID 300) +TCP at 8080 and 9000 +6\r?$", text);
        Assert.Matches(@"(?m)^  (client\.exe · PID 100 and helper\.exe · PID 300|helper\.exe · PID 300 and client\.exe · PID 100) +TCP +2\r?$", text);
        Assert.Contains("3 relationships in all. An arrow points at the process connected to, where the capture saw the "
            + "connections open, and ⇄ joins two that each connected to the other; two joined by \"and\" were seen opening "
            + "none. Records are both ends'. Over names the ports the connections arrived at, at the process connected to.",
            Regex.Replace(text, @"\s+", " "), Ordinal);
        Assert.True(text.IndexOf("client.exe · PID 100 → server.exe", Ordinal) < text.IndexOf(" ⇄ ", Ordinal));

        // Each two of its programs share one relationship, which that table names, so none sums them.
        Assert.DoesNotContain("Busiest pair of programs", text, Ordinal);

        // Its busiest processes say the connections each made and accepted, as icat processes counts them - the server's
        // three accepted counting the one from another host - at the ports of its own they arrived at, and where to read
        // with whom.
        Assert.Matches(@"(?m)^  Busiest process +Own records +Mostly +Connections\r?$", text);
        Assert.Matches(@"(?m)^  server\.exe · PID 200 +9 +TCP +1 made · 3 accepted at 445 and 8080\r?$", text);
        Assert.Matches(@"(?m)^  client\.exe · PID 100 +6 +TCP +1 made · 0 accepted\r?$", text);
        Assert.Matches(@"(?m)^  helper\.exe · PID 300 +5 +TCP +1 made · 1 accepted at 9000\r?$", text);
        Assert.Contains("Connections are each one's TCP connections made and accepted, as the capture saw them open, those "
            + "accepted at the ports of its own they arrived at. icat "
            + "processes <directory> --pid <PID> names who it connected to and who connected to it, and --rank-by "
            + "connections-made or connections-accepted lists the processes that did so most. icat ports <directory> lists the "
            + "ports they accepted connections at, and who connected to each.", Regex.Replace(text, @"\s+", " "), Ordinal);

        // The server accepted its connection from another host, at its own 445, which its row and the host's say.
        Assert.Matches(@"(?m)^  Process +Connections with other hosts +Accepted here\r?$", text);
        Assert.Matches(@"(?m)^  server\.exe · PID 200 +1 TCP connection from 1 host +1 at 445\r?$", text);
        Assert.Matches(@"(?m)^  Busiest other host +Processes +Connections +Ports +Accepted here +Records\r?$", text);
        Assert.Matches(@"(?m)^  10\.0\.0\.6 +server\.exe · PID 200 +1 TCP connection +- +1 at 445 +2\r?$", text);

        // The caveat says what the order of an edge's ends is, and where who connected is said.
        Assert.Contains("An edge's source and target are a stable display order, not a claim about direction: which process "
            + "made its connections is said only where the capture saw them open", Regex.Replace(text, @"\s+", " "), Ordinal);
    }

    [Fact(DisplayName = "§6.8: icat overview names an RPC relationship by the process that made its calls first, and says an arrow points at the one called")]
    public async Task IcatOverviewSaysWhoCalled()
    {
        // PID 400's calls linked through ALPC to the calls PID 1960 served them with.
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = LinkedRpcCalls();
        Publish(session.Store, rows, fields: fields, coverage: RpcLedger(alpc: true));
        session.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string text, string said) = await Run("overview", session.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  PID 400 → PID 1960 +RPC +8\r?$", text);

        // It records no connection's open, so its busiest processes say nothing of connections, which would read as none.
        Assert.Matches(@"(?m)^  Busiest process +Own records +Mostly\r?$", text);
        Assert.DoesNotContain("Connections are each one's", text, Ordinal);
        Assert.Contains("1 relationship in all. An arrow points at the process connected to, where the capture saw the "
            + "connections open, or called, where an RPC call was linked to the call that served it, and ⇄ joins two that each "
            + "did so to the other; two joined by \"and\" were seen opening none. Records are both ends'.",
            Regex.Replace(text, @"\s+", " "), Ordinal);
        Assert.DoesNotContain("Over names the ports", text, Ordinal);
        Assert.Contains("which called which over RPC only where each call was linked to the call that served it",
            Regex.Replace(text, @"\s+", " "), Ordinal);
    }

    [Fact(DisplayName = "§5.2: icat overview's busiest processes say none for one the capture saw open no connection of, where it saw others' open")]
    public async Task IcatOverviewSaysNoneForABusyProcessThatOpenedNone()
    {
        // The client connects to the server; a resolver, the busiest, sends three datagrams to another host, which open nothing.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 100 },
            Lifecycle(2, ObservationKind.Create, 200, 2) with { ResourceName = @"C:\Tools\server.exe", SessionRelativeTicks = 110 },
            Lifecycle(3, ObservationKind.Create, 300, 3) with { ResourceName = @"C:\Tools\resolver.exe", SessionRelativeTicks = 120 },
            Transfer(10, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 10)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 200 },
            Transfer(11, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 11)
                .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 210 },
            Transfer(12, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 12)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 220 },
            Transfer(13, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 13)
                .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 230 },
            .. Enumerable.Range(20, 3).Select(ticks => Transfer(ticks, ObservationKind.Send, AccountingSide.SendSide, 32, 300, (ulong)ticks)
                .Between("192.168.1.10:53000", "10.0.0.53:53") with { Mechanism = Mechanism.Udp, EventId = 42, SessionRelativeTicks = ticks * 10 }),
        ]);
        session.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string text, string said) = await Run("overview", session.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  resolver\.exe · PID 300 +4 +UDP +none\r?$", text);
        Assert.Matches(@"(?m)^  client\.exe · PID 100 +3 +TCP +1 made · 0 accepted\r?$", text);
        Assert.Matches(@"(?m)^  server\.exe · PID 200 +3 +TCP +0 made · 1 accepted at 8080\r?$", text);
    }

    [Fact(DisplayName = "§6.3: icat overview sums the relationships of programs that run several processes by the two programs at their ends, each pair named by who connected to whom")]
    public async Task IcatOverviewListsTheBusiestPairsOfPrograms()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 100 },
            Lifecycle(2, ObservationKind.Create, 101, 2) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 110 },
            Lifecycle(3, ObservationKind.Create, 200, 3) with { ResourceName = @"C:\Apps\server.exe", SessionRelativeTicks = 120 },

            // Both of the browser's processes connect to the server's 8080, and its second to its first's 9222.
            .. Connection(10, 100, "127.0.0.1:50000", 200, "127.0.0.1:8080"),
            .. Connection(20, 101, "127.0.0.1:50001", 200, "127.0.0.1:8080"),
            .. Connection(30, 101, "127.0.0.1:50002", 100, "127.0.0.1:9222"),
        ]);
        session.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string text, string said) = await Run("overview", session.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Relationships +3\r?$", text);

        // The busiest first: the browser's two processes to the server, then the browser's own two to each other.
        Assert.Matches(@"(?m)^  Busiest pair of programs +Relationships +Processes +Over +Records\r?$", text);
        Assert.Matches(@"(?m)^  browser\.exe → server\.exe +2 +3 +TCP at 8080 +6\r?$", text);
        Assert.Matches(@"(?m)^  browser\.exe → browser\.exe +1 +2 +TCP at 9222 +3\r?$", text);
        Assert.True(text.IndexOf("browser.exe → server.exe", Ordinal) < text.IndexOf("browser.exe → browser.exe", Ordinal));
        Assert.Contains("2 pairs of programs in all, each program by its executable: one named at both ends is its own processes "
            + "connecting to each other. Arrows and records read as above, and Processes counts those at either end. icat programs "
            + "<directory> lists every pair, and with --program <name> those one program is in.",
            Regex.Replace(text, @"\s+", " "), Ordinal);

        static ObservationRowV1[] Connection(long at, int maker, string from, int taker, string to) =>
        [
            Transfer(at, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, maker, (ulong)at)
                .Between(from, to) with { SessionRelativeTicks = 200 + at },
            Transfer(at + 1, ObservationKind.Send, AccountingSide.SendSide, 8, maker, (ulong)at + 1)
                .Between(from, to) with { SessionRelativeTicks = 201 + at },
            Transfer(at + 2, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, taker, (ulong)at + 2)
                .Between(to, from) with { SessionRelativeTicks = 202 + at },
        ];
    }

    [Fact(DisplayName = "§6.8: a relationship the overview lists names first the process that made its connections, whichever end it is drawn from")]
    public void ARelationshipNamesItsMakerFirst()
    {
        ProcessNode Node(int index, int pid, string name) => new(new ProcessInstanceId(Guid.Parse($"00000000-0000-0000-0000-{index:D12}")),
            pid, name, "test", name, 0.5, 0.5, CoverageState.Covered);
        ProcessNode a = Node(1, 100, "a.exe");
        ProcessNode b = Node(2, 200, "b.exe");
        var nodes = new Dictionary<ProcessInstanceId, ProcessNode> { [a.Id] = a, [b.Id] = b };
        CommunicationEdge Edge(int fromSource, int fromTarget) =>
            new("tcp:a:b", a.Id, b.Id, Mechanism.Tcp, 10, null, RelationStrength.Correlated)
            {
                Rule = RelationRule.TransportEndpoint,
                Evidence = ["tcp:a:b:0", "tcp:a:b:1"],
                SourceConnected = fromSource,
                TargetConnected = fromTarget,
            };
        Assert.Equal(
            ["a.exe · PID 100 → b.exe · PID 200", "b.exe · PID 200 → a.exe · PID 100", "a.exe · PID 100 ⇄ b.exe · PID 200",
                "a.exe · PID 100 and b.exe · PID 200"],
            new[] { Edge(2, 0), Edge(0, 1), Edge(1, 1), Edge(0, 0) }.Select(edge => OverviewCommand.Joined(edge, nodes)));
    }

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
