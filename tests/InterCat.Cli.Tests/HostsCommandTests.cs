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
/// icat hosts (R18): the other hosts a session's processes reached, the busiest first, as the window's Other hosts node
/// names them, and with --address the processes that reached one, by the rule the window's search finds them by.
/// </summary>
public sealed class HostsCommandTests
{
    [Fact(DisplayName = "R18: icat hosts lists the other hosts the session's processes reached, the busiest first, and as many as asked")]
    public async Task IcatHostsListsTheOtherHosts()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        session.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string text, string said) = await Run("hosts", session.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Hosts +4 hosts, reached by 2 processes\r?$", text);
        Assert.Matches(@"(?m)^  Host +Scope +Connections +Ports +Records\r?$", text);
        string[] rows = [.. Regex.Matches(text, @"(?m)^  (\S+) +(\S+) +(\d.*?(?:process|processes)) +(\d.*?) +(\d+)\r?$").Select(match =>
            $"{match.Groups[1].Value} | {match.Groups[2].Value} | {match.Groups[3].Value} | {match.Groups[4].Value.TrimEnd()} | {match.Groups[5].Value}")];
        Assert.Equal(
            [
                "142.250.1.1 | - | 2 TCP connections from 2 processes | 443 and 8443 | 5",
                "142.250.1.10 | - | 1 TCP connection from 1 process | 443 | 1",
                "2001:db8::1 | - | 1 TCP connection from 1 process | 443 | 1",
                "8.8.8.8 | - | 1 UDP flow from 1 process | 53 | 1",
            ],
            rows);
        Assert.Contains("who is at it is not observed", text, Ordinal);

        // As many as asked, saying how many more there are.
        (code, text, _) = await Run("hosts", session.Path, "--limit", "2");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Contains("2 more, quieter; --limit 4 lists every one.", text, Ordinal);
        Assert.DoesNotContain("8.8.8.8", text, Ordinal);

        // The same as JSON: every host's count, the listed hosts each with its transports, processes and records.
        (code, text, _) = await Run("hosts", session.Path, "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        JsonElement root = JsonDocument.Parse(text).RootElement;
        Assert.Equal(("other-hosts-v4", "records", 4, 2), (root.GetProperty("contract").GetString(),
            root.GetProperty("rankedBy").GetString(), root.GetProperty("hosts").GetInt32(), root.GetProperty("processes").GetInt32()));
        JsonElement busiest = root.GetProperty("listed")[0];
        Assert.Equal(("142.250.1.1", 2, 0, 2, 5L), (busiest.GetProperty("address").GetString(), busiest.GetProperty("tcpConnections").GetInt32(),
            busiest.GetProperty("udpFlows").GetInt32(), busiest.GetProperty("processes").GetInt32(), busiest.GetProperty("records").GetInt64()));
        Assert.Equal([443, 8443], busiest.GetProperty("ports").EnumerateArray().Select(port => port.GetInt32()));
        Assert.Equal(JsonValueKind.Null, busiest.GetProperty("scope").ValueKind);
        Assert.Equal(JsonValueKind.Null, busiest.GetProperty("transfers").ValueKind);

        // A limit that is no count of hosts is refused.
        (code, _, said) = await Run("hosts", session.Path, "--limit", "0");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("--limit expects a number of hosts of 1 or more", said + text, Ordinal);

        // A scoped address says its scope: a multicast group's, here.
        using var scoped = new TemporarySession();
        Publish(scoped.Store,
        [
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 100, 320, 1).Between("192.168.1.10:61900", "239.255.255.250:1900")
                with { Mechanism = Mechanism.Udp, EventId = 42 },
        ]);
        scoped.Store.ReleaseSegmentReaders();
        (code, text, _) = await Run("hosts", scoped.Path);
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^  239\.255\.255\.250 +multicast +1 UDP flow from 1 process +1900 +1\r?$", text);
        (code, text, _) = await Run("hosts", scoped.Path, "--json");
        Assert.Equal("multicast", JsonDocument.Parse(text).RootElement.GetProperty("listed")[0].GetProperty("scope").GetString());

        // A session none of whose processes reached another host says so.
        using var local = new TemporarySession();
        Publish(local.Store,
        [
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 1).Between("127.0.0.1:50000", "127.0.0.1:8080"),
            Transfer(21, ObservationKind.Receive, AccountingSide.ReceiveSide, 8, 200, 2).Between("127.0.0.1:8080", "127.0.0.1:50000"),
        ]);
        local.Store.ReleaseSegmentReaders();
        (code, text, _) = await Run("hosts", local.Path);
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Contains("No process of this session reached another host", text, Ordinal);
    }

    [Fact(DisplayName = "R18: icat hosts --address lists the processes that reached an address, whole or begun up to a dot or colon, at a port or any address at a port, as the window's search finds them")]
    public async Task IcatHostsFindsTheProcessesThatReachedAnAddress()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        session.Store.ReleaseSegmentReaders();

        // A whole address: the processes that reached it, the busiest first, and not 142.250.1.10, which it only begins.
        (InterCatExitCode code, string text, string said) = await Run("hosts", session.Path, "--address", "142.250.1.1");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^ *updater\.exe · PID 310 +142\.250\.1\.1 +1 TCP connection +8443 +3\r?$", text);
        Assert.Matches(@"(?m)^ *browser\.exe · PID 300 +142\.250\.1\.1 +1 TCP connection +443 +2\r?$", text);
        Assert.True(text.IndexOf("updater.exe", Ordinal) < text.IndexOf("browser.exe", Ordinal));
        Assert.DoesNotContain("142.250.1.10", text, Ordinal);

        // Begun up to a dot, it finds every address it begins; IPv6 in either case.
        (code, text, _) = await Run("hosts", session.Path, "--address", "142.250");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^ *browser\.exe · PID 300 +142\.250\.1\.10 +1 TCP connection +443 +1\r?$", text);
        (code, text, _) = await Run("hosts", session.Path, "--address", "2001:DB8::", "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        JsonElement root = JsonDocument.Parse(text).RootElement;
        JsonElement reached = Assert.Single(root.GetProperty("reached").EnumerateArray());
        Assert.Equal(("updater.exe · PID 310", "2001:db8::1", "TCP", 1, 1L, 443),
            (reached.GetProperty("name").GetString(), reached.GetProperty("address").GetString(), reached.GetProperty("mechanism").GetString(),
                reached.GetProperty("connections").GetInt32(), reached.GetProperty("records").GetInt64(),
                Assert.Single(reached.GetProperty("ports").EnumerateArray()).GetInt32()));
        Assert.Equal(1, root.GetProperty("matched").GetInt32());
        Assert.True(Guid.TryParse(reached.GetProperty("process").GetString(), out _));

        // An endpoint finds the processes that reached the address at that port alone, and a port alone those that reached
        // any address at it; an endpoint no process reached says so in its own words.
        (code, text, _) = await Run("hosts", session.Path, "--address", "142.250.1.1:8443");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^ *updater\.exe · PID 310 +142\.250\.1\.1 +1 TCP connection +8443 +3\r?$", text);
        Assert.DoesNotContain("browser.exe", text, Ordinal);
        (code, text, _) = await Run("hosts", session.Path, "--address", ":443", "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        string[] atPort = [.. JsonDocument.Parse(text).RootElement.GetProperty("reached").EnumerateArray()
            .Select(row => row.GetProperty("name").GetString() + " " + row.GetProperty("address").GetString())];
        Assert.Equal("browser.exe · PID 300 142.250.1.1", atPort[0]);
        Assert.Equal(["browser.exe · PID 300 142.250.1.1", "browser.exe · PID 300 142.250.1.10", "updater.exe · PID 310 2001:db8::1"],
            atPort.Order(StringComparer.Ordinal));
        (code, text, _) = await Run("hosts", session.Path, "--address", "142.250.1.1:80");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Contains("No process of this session reached 142.250.1.1:80: an address whole at that port", text, Ordinal);

        // An address no process reached says so; a query with neither dot nor colon names no address and is refused.
        (code, text, _) = await Run("hosts", session.Path, "--address", "192.0.2.1");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Contains("No process of this session reached an address 192.0.2.1 names", text, Ordinal);
        (code, text, said) = await Run("hosts", session.Path, "--address", "142");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("--address takes an address, or its beginning up to a dot or colon", said + text, Ordinal);
    }

    [Fact(DisplayName = "R18: icat hosts says what it takes, refuses what names no session, and lists as many processes reaching an address as asked")]
    public async Task IcatHostsSaysWhatItTakes()
    {
        // Asked for help, it says what it takes, ports and the rule an address is named by among them.
        (InterCatExitCode code, string text, string said) = await Run("hosts", "--help");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.StartsWith("icat hosts <session-directory> [--address <address>] [--limit <n>]", text, Ordinal);
        Assert.Contains("[--rank-by records|bytes-sent|bytes-received|bytes-sent-and-received] [--json]", text, Ordinal);
        Assert.Contains("142.250.1.1:443 or [2001:db8::1]:443", text, Ordinal);
        Assert.All(text.Split('\n'), line => Assert.True(line.TrimEnd().Length <= 104, line));

        // With no session, an option it does not take, a folder that is not there or holds no session, it refuses and says
        // why, its help beside what it cannot do.
        (code, text, said) = await Run("hosts");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("A session directory is required: icat hosts <directory>.", said + text, Ordinal);
        Assert.Contains("icat hosts <session-directory>", said + text, Ordinal);
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        session.Store.ReleaseSegmentReaders();
        (code, text, said) = await Run("hosts", session.Path, "--bogus");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("--bogus", said + text, Ordinal);
        string nowhere = Path.Combine(Path.GetTempPath(), "icat-hosts-" + Guid.NewGuid().ToString("N"));
        (code, text, said) = await Run("hosts", nowhere);
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("No session directory at " + nowhere, said + text, Ordinal);
        string empty = Directory.CreateDirectory(nowhere).FullName;
        try
        {
            (code, text, said) = await Run("hosts", empty);
            Assert.NotEqual(InterCatExitCode.Success, code);
            Assert.Contains("No InterCat session has been published in this folder", said + text, Ordinal);
        }
        finally
        {
            Directory.Delete(empty);
        }

        // The processes that reached what an address begins are listed as many as asked, saying how many more there are.
        (code, text, _) = await Run("hosts", session.Path, "--address", "142.250", "--limit", "1");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^ *updater\.exe · PID 310 +142\.250\.1\.1 +1 TCP connection +8443 +3\r?$", text);
        Assert.Contains("2 more, quieter; --limit 3 lists every one.", text, Ordinal);
        Assert.DoesNotContain("browser.exe", text, Ordinal);
    }

    [Fact(DisplayName = "R18: icat hosts --rank-by lists the hosts by the bytes their connections sent or received there, with both directions', and refuses a ranking hosts hold nothing of")]
    public async Task IcatHostsRanksByBytes()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        session.Store.ReleaseSegmentReaders();

        // By bytes sent: 142.250.1.1's five 100 B sends first, then the two hosts sent 100 B, by address, then the resolver's
        // 40 B; none received anything.
        (InterCatExitCode code, string text, string said) = await Run("hosts", session.Path, "--rank-by", "bytes-sent");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Ranked by +bytes sent, as this machine's end of their connections measured them; hosts whose transfers "
            + @"stated no size follow, then those that made none\r?$", text);
        Assert.Matches(@"(?m)^  Host +Scope +Connections +Ports +Records +Sent +Received\r?$", text);
        string[] rows = [.. Regex.Matches(text, @"(?m)^  (\S+) +-  .*? +(\d+) +([\d,]+ B) +(no receives)\r?$").Select(match =>
            $"{match.Groups[1].Value} | {match.Groups[2].Value} | {match.Groups[3].Value} | {match.Groups[4].Value}")];
        Assert.Equal(
            [
                "142.250.1.1 | 5 | 500 B | no receives",
                "142.250.1.10 | 1 | 100 B | no receives",
                "2001:db8::1 | 1 | 100 B | no receives",
                "8.8.8.8 | 1 | 40 B | no receives",
            ],
            rows);

        // As JSON, the ranking and each host's transfers.
        (code, text, _) = await Run("hosts", session.Path, "--rank-by", "bytes-sent-and-received", "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        JsonElement root = JsonDocument.Parse(text).RootElement;
        Assert.Equal(("other-hosts-v4", "bytes-sent-and-received"), (root.GetProperty("contract").GetString(),
            root.GetProperty("rankedBy").GetString()));
        JsonElement transfers = root.GetProperty("listed")[0].GetProperty("transfers");
        Assert.Equal((5L, 500L, 0L, 0L), (transfers.GetProperty("sends").GetInt64(), transfers.GetProperty("sentBytes").GetInt64(),
            transfers.GetProperty("unmeasuredSends").GetInt64(), transfers.GetProperty("receives").GetInt64()));
        Assert.Equal("bytes-received", JsonDocument.Parse((await Run("hosts", session.Path, "--rank-by", "bytes-received", "--json")).Output)
            .RootElement.GetProperty("rankedBy").GetString());

        // A ranking hosts hold nothing of, and one beside --address, which lists processes, are refused.
        (code, _, said) = await Run("hosts", session.Path, "--rank-by", "rpc-calls-made");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("--rank-by ranks the hosts by records, bytes-sent, bytes-received or bytes-sent-and-received; "
            + "'rpc-calls-made' is none of them.", said, Ordinal);
        (code, _, said) = await Run("hosts", session.Path, "--rank-by", "bytes-sent", "--address", "142.250");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("--rank-by ranks the hosts; --address lists the processes that reached one", said, Ordinal);
    }

    [Fact(DisplayName = "R18: icat evidence and icat timeline take --host: the records of every connection to an address, whichever process held it")]
    public async Task IcatEvidenceAndTimelineTakeAHost()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        session.Store.ReleaseSegmentReaders();

        // Its records are every connection's to the address - the browser's two records and the updater's three - and
        // none of 142.250.1.10's, which it only begins.
        (InterCatExitCode code, string text, string said) = await Run("evidence", session.Path, "--host", "142.250.1.1", "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        JsonElement page = JsonDocument.Parse(text).RootElement.GetProperty("page");
        Assert.Equal(5, page.GetProperty("records").GetArrayLength());
        Assert.Equal("host:142.250.1.1", page.GetProperty("channelKey").GetString());
        (code, text, _) = await Run("evidence", session.Path, "--host", "142.250.1.1");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^  Host +142\.250\.1\.1, its connections whichever process held them\r?$", text);

        // The timeline counts them where they fell, saying what it counts.
        (code, text, said) = await Run("timeline", session.Path, "--interval", "0:1000", "--columns", "4", "--host", "142.250.1.1",
            "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        JsonElement detail = JsonDocument.Parse(text).RootElement.GetProperty("detail");
        Assert.Equal("records of the connections to 142.250.1.1", detail.GetProperty("scope").GetString());
        Assert.Equal(5, detail.GetProperty("buckets").EnumerateArray().Sum(bucket => bucket.GetProperty("observationCount").GetInt32()));

        // A host and a channel at once, or a host that is no address, is refused; an address none reached says so.
        (code, text, said) = await Run("evidence", session.Path, "--host", "142.250.1.1", "--channel", "connection:x");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("--host and --channel each name the records to read; name one.", said + text, Ordinal);
        (code, text, said) = await Run("timeline", session.Path, "--interval", "0:1000", "--host", "browser");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("--host takes an address its processes' connections ran to", said + text, Ordinal);
        (code, text, said) = await Run("evidence", session.Path, "--host", "192.0.2.1");
        Assert.NotEqual(InterCatExitCode.Success, code);
        Assert.Contains("No connection to 192.0.2.1 is in", said + text, Ordinal);
    }

    /// <summary>
    /// browser.exe's TCP to 142.250.1.1's port 443, two records, and to 142.250.1.10, and its UDP to a resolver; updater.exe's
    /// TCP to 142.250.1.1's port 8443, three records, and to an IPv6 host.
    /// </summary>
    private static ObservationRowV1[] Rows() =>
    [
        Lifecycle(1, ObservationKind.Create, 300, 1) with { ResourceName = @"C:\Apps\browser.exe", SessionRelativeTicks = 100 },
        Lifecycle(2, ObservationKind.Create, 310, 2) with { ResourceName = @"C:\Apps\updater.exe", SessionRelativeTicks = 110 },
        Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 100, 300, 3).Between("192.168.1.10:52000", "142.250.1.1:443") with { SessionRelativeTicks = 200 },
        Transfer(21, ObservationKind.Send, AccountingSide.SendSide, 100, 300, 4).Between("192.168.1.10:52000", "142.250.1.1:443") with { SessionRelativeTicks = 210 },
        Transfer(22, ObservationKind.Send, AccountingSide.SendSide, 100, 300, 5).Between("192.168.1.10:52001", "142.250.1.10:443") with { SessionRelativeTicks = 220 },
        Transfer(23, ObservationKind.Send, AccountingSide.SendSide, 40, 300, 6).Between("192.168.1.10:61000", "8.8.8.8:53")
            with { Mechanism = Mechanism.Udp, EventId = 42, SessionRelativeTicks = 230 },
        Transfer(24, ObservationKind.Send, AccountingSide.SendSide, 100, 310, 7).Between("192.168.1.10:53000", "142.250.1.1:8443") with { SessionRelativeTicks = 240 },
        Transfer(25, ObservationKind.Send, AccountingSide.SendSide, 100, 310, 8).Between("192.168.1.10:53000", "142.250.1.1:8443") with { SessionRelativeTicks = 250 },
        Transfer(26, ObservationKind.Send, AccountingSide.SendSide, 100, 310, 9).Between("192.168.1.10:53000", "142.250.1.1:8443") with { SessionRelativeTicks = 260 },
        Transfer(27, ObservationKind.Send, AccountingSide.SendSide, 100, 310, 10).Between("[2001:db8::10]:53001", "[2001:db8::1]:443") with { SessionRelativeTicks = 270 },
    ];

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
