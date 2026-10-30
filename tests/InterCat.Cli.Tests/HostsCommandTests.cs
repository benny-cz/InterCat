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
        Assert.Matches(@"(?m)^  Host +Scope +Processes +Connections +Ports +Records\r?$", text);
        string[] rows = [.. Regex.Matches(text, @"(?m)^  (\S+) +(\S+) +(\S+ · PID \d+(?: and \d+ more)?) +(\d+ (?:TCP|UDP) \S+) +(\d.*?) +(\d+)\r?$")
            .Select(match => $"{match.Groups[1].Value} | {match.Groups[2].Value} | {match.Groups[3].Value} | {match.Groups[4].Value} | "
                + $"{match.Groups[5].Value.TrimEnd()} | {match.Groups[6].Value}")];

        // Each names who reached it: the busiest there, the updater's three records to 142.250.1.1 before the browser's two,
        // and how many more.
        Assert.Equal(
            [
                "142.250.1.1 | - | updater.exe · PID 310 and 1 more | 2 TCP connections | 443 and 8443 | 5",
                "142.250.1.10 | - | browser.exe · PID 300 | 1 TCP connection | 443 | 1",
                "2001:db8::1 | - | updater.exe · PID 310 | 1 TCP connection | 443 | 1",
                "8.8.8.8 | - | browser.exe · PID 300 | 1 UDP flow | 53 | 1",
            ],
            rows);
        Assert.Contains("who is at it is not observed. Processes names the busiest process whose connections ran there and how many "
            + "more there are; icat hosts <directory> --address <address> lists every one.", Regex.Replace(text, @"\s+", " "), Ordinal);

        // As many as asked, saying how many more there are.
        (code, text, _) = await Run("hosts", session.Path, "--limit", "2");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Contains("2 more, quieter; --limit 4 lists every one.", text, Ordinal);
        Assert.DoesNotContain("8.8.8.8", text, Ordinal);

        // The same as JSON: every host's count, the listed hosts each with its transports, processes and records.
        (code, text, _) = await Run("hosts", session.Path, "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        JsonElement root = JsonDocument.Parse(text).RootElement;
        Assert.Equal(("other-hosts-v8", "records", 4, 2), (root.GetProperty("contract").GetString(),
            root.GetProperty("rankedBy").GetString(), root.GetProperty("hosts").GetInt32(), root.GetProperty("processes").GetInt32()));
        JsonElement busiest = root.GetProperty("listed")[0];
        Assert.Equal(("142.250.1.1", 2, 0, 2, 5L), (busiest.GetProperty("address").GetString(), busiest.GetProperty("tcpConnections").GetInt32(),
            busiest.GetProperty("udpFlows").GetInt32(), busiest.GetProperty("processes").GetInt32(), busiest.GetProperty("records").GetInt64()));
        Assert.Equal([443, 8443], busiest.GetProperty("ports").EnumerateArray().Select(port => port.GetInt32()));
        Assert.Equal(JsonValueKind.Null, busiest.GetProperty("scope").ValueKind);
        Assert.Equal(JsonValueKind.Null, busiest.GetProperty("transfers").ValueKind);
        Assert.Equal([("updater.exe · PID 310", 3L), ("browser.exe · PID 300", 2L)], busiest.GetProperty("reachedBy").EnumerateArray()
            .Select(reach => (reach.GetProperty("name").GetString(), reach.GetProperty("records").GetInt64())));
        Assert.All(busiest.GetProperty("reachedBy").EnumerateArray(), reach => Assert.True(Guid.TryParse(reach.GetProperty("process").GetString(), out _)));

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
        Assert.Matches(@"(?m)^  239\.255\.255\.250 +multicast +PID 320 +1 UDP flow +1900 +1\r?$", text);
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

    [Fact(DisplayName = "R18: icat hosts --program lists the other hosts one program's processes reached, or those of them that reached an address, as a chosen program's inspector lists them")]
    public async Task IcatHostsListsOneProgramsHosts()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            .. Rows(),
            Lifecycle(3, ObservationKind.Create, 320, 11) with { ResourceName = @"C:\Apps\quiet.exe", SessionRelativeTicks = 120 },
            Lifecycle(4, ObservationKind.Create, 330, 12) with { ResourceName = @"C:\Tools\quiet.exe", SessionRelativeTicks = 130 },
        ]);
        session.Store.ReleaseSegmentReaders();

        // The updater's hosts alone: its three records to 142.250.1.1's port 8443 and its one to 2001:db8::1, named so.
        (InterCatExitCode code, string text, string said) = await Run("hosts", session.Path, "--program", "updater.exe");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("OTHER HOSTS UPDATER.EXE'S PROCESSES REACHED", text, Ordinal);
        Assert.Matches(@"(?m)^  Program +updater\.exe · 1 process\r?$", text);
        Assert.Matches(@"(?m)^  Hosts +2 hosts, reached by 1 process\r?$", text);
        Assert.Matches(@"(?m)^  142\.250\.1\.1 +- +updater\.exe · PID 310 +1 TCP connection +8443 +3\r?$", text);
        Assert.Matches(@"(?m)^  2001:db8::1 +- +updater\.exe · PID 310 +1 TCP connection +443 +1\r?$", text);
        Assert.DoesNotContain("8.8.8.8", text, Ordinal);

        // By its file name's stem alike, and as JSON, which names the program.
        (code, text, _) = await Run("hosts", session.Path, "--program", "updater", "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        JsonElement root = JsonDocument.Parse(text).RootElement;
        JsonElement program = root.GetProperty("program");
        Assert.Equal(("other-hosts-v8", "updater.exe", @"C:\Apps\updater.exe", 1, 2, 1), (root.GetProperty("contract").GetString(),
            program.GetProperty("name").GetString(), program.GetProperty("path").GetString(), program.GetProperty("processes").GetInt32(),
            root.GetProperty("hosts").GetInt32(), root.GetProperty("processes").GetInt32()));
        (code, text, _) = await Run("hosts", session.Path, "--json");
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(text).RootElement.GetProperty("program").ValueKind);

        // With --address, those of its processes that reached it: the updater at 142.250.1.1, not the browser.
        (code, text, _) = await Run("hosts", session.Path, "--program", "updater.exe", "--address", "142.250");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Contains("UPDATER.EXE'S PROCESSES THAT REACHED 142.250", text, Ordinal);
        Assert.Matches(@"(?m)^  Program +updater\.exe · 1 process\r?$", text);
        Assert.Contains("updater.exe · PID 310", text, Ordinal);
        Assert.DoesNotContain("browser.exe", text, Ordinal);
        (code, text, _) = await Run("hosts", session.Path, "--program", "updater.exe", "--address", "142.250", "--json");
        Assert.Equal("updater.exe", JsonDocument.Parse(text).RootElement.GetProperty("program").GetProperty("name").GetString());
        (code, text, _) = await Run("hosts", session.Path, "--program", "updater.exe", "--address", "8.8.8.8");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Contains("No process of updater.exe reached an address 8.8.8.8 names", Regex.Replace(text, @"\s+", " "), Ordinal);
        Assert.Contains("icat hosts <directory> --program updater.exe lists every host they reached.", Regex.Replace(text, @"\s+", " "),
            Ordinal);
        (code, text, _) = await Run("hosts", session.Path, "--program", "updater.exe", "--address", ":9999");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Contains("No process of updater.exe reached :9999", Regex.Replace(text, @"\s+", " "), Ordinal);
        Assert.Contains("icat hosts <directory> --program updater.exe lists every host they reached, with its ports.",
            Regex.Replace(text, @"\s+", " "), Ordinal);

        // A byte ranking counts its connections alone.
        (code, text, _) = await Run("hosts", session.Path, "--program", "updater.exe", "--rank-by", "bytes-sent", "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Equal(["142.250.1.1", "2001:db8::1"], JsonDocument.Parse(text).RootElement.GetProperty("listed").EnumerateArray()
            .Select(host => host.GetProperty("address").GetString()));

        // A name two programs share is refused, saying how each is listed; named as listed, one that reached none says so,
        // and where none of it reached an address, the command that lists its hosts names it so a shell takes it whole.
        (code, text, said) = await Run("hosts", session.Path, "--program", "quiet.exe");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("--program quiet.exe names 2 programs - quiet.exe (Apps) and quiet.exe (Tools) - so name one as they are listed.",
            Regex.Replace(said + text, @"\s+", " "), Ordinal);
        (code, text, _) = await Run("hosts", session.Path, "--program", "quiet.exe (Apps)");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Contains("No process of quiet.exe (Apps) reached another host", Regex.Replace(text, @"\s+", " "), Ordinal);
        (code, text, _) = await Run("hosts", session.Path, "--program", @"C:\Tools\quiet.exe", "--address", "8.8.8.8");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Contains("No process of quiet.exe (Tools) reached an address 8.8.8.8 names", Regex.Replace(text, @"\s+", " "), Ordinal);
        Assert.Contains("icat hosts <directory> --program \"quiet.exe (Tools)\" lists every host they reached.",
            Regex.Replace(text, @"\s+", " "), Ordinal);

        // One the session does not hold, or no name at all, is refused.
        (code, text, said) = await Run("hosts", session.Path, "--program", "nothing.exe");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("No program of this session is named nothing.exe", said + text, Ordinal);
        (code, text, said) = await Run("hosts", session.Path, "--program", " ");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("--program takes a program's name as icat programs lists it", said + text, Ordinal);
    }

    [Fact(DisplayName = "R18: icat hosts and icat overview name who reached each host, the busiest there first and how many more, one that reached it over both transports once")]
    public async Task IcatHostsNamesWhoReachedEachHost()
    {
        // agent.exe reached 10.0.0.9 over TCP and over UDP, two records each, and sync.exe over TCP, three records.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 400, 1) with { ResourceName = @"C:\Apps\agent.exe", SessionRelativeTicks = 100 },
            Lifecycle(2, ObservationKind.Create, 410, 2) with { ResourceName = @"C:\Apps\sync.exe", SessionRelativeTicks = 110 },
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 100, 400, 3).Between("192.168.1.10:52000", "10.0.0.9:443") with { SessionRelativeTicks = 200 },
            Transfer(21, ObservationKind.Send, AccountingSide.SendSide, 100, 400, 4).Between("192.168.1.10:52000", "10.0.0.9:443") with { SessionRelativeTicks = 210 },
            Transfer(22, ObservationKind.Send, AccountingSide.SendSide, 40, 400, 5).Between("192.168.1.10:61000", "10.0.0.9:53")
                with { Mechanism = Mechanism.Udp, EventId = 42, SessionRelativeTicks = 220 },
            Transfer(23, ObservationKind.Send, AccountingSide.SendSide, 40, 400, 6).Between("192.168.1.10:61000", "10.0.0.9:53")
                with { Mechanism = Mechanism.Udp, EventId = 42, SessionRelativeTicks = 230 },
            Transfer(24, ObservationKind.Send, AccountingSide.SendSide, 100, 410, 7).Between("192.168.1.10:53000", "10.0.0.9:443") with { SessionRelativeTicks = 240 },
            Transfer(25, ObservationKind.Send, AccountingSide.SendSide, 100, 410, 8).Between("192.168.1.10:53000", "10.0.0.9:443") with { SessionRelativeTicks = 250 },
            Transfer(26, ObservationKind.Send, AccountingSide.SendSide, 100, 410, 9).Between("192.168.1.10:53000", "10.0.0.9:443") with { SessionRelativeTicks = 260 },
        ]);
        session.Store.ReleaseSegmentReaders();

        // The agent's four records, over both transports, are more than the sync's three: it is named, once, and one more.
        (InterCatExitCode code, string text, string said) = await Run("hosts", session.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  10\.0\.0\.9 +private +agent\.exe · PID 400 and 1 more +2 TCP connections and 1 UDP flow +53 and 443 +7\r?$", text);
        (code, text, _) = await Run("hosts", session.Path, "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        JsonElement host = Assert.Single(JsonDocument.Parse(text).RootElement.GetProperty("listed").EnumerateArray());
        Assert.Equal([("agent.exe · PID 400", 4L), ("sync.exe · PID 410", 3L)], host.GetProperty("reachedBy").EnumerateArray()
            .Select(reach => (reach.GetProperty("name").GetString(), reach.GetProperty("records").GetInt64())));
        Assert.Equal(2, host.GetProperty("processes").GetInt32());

        // The overview's busiest hosts name them so, and say where every host and who reached it are listed.
        (code, text, said) = await Run("overview", session.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Busiest other host +Processes +Connections +Ports +Records\r?$", text);
        Assert.Matches(@"(?m)^  10\.0\.0\.9 +agent\.exe · PID 400 and 1 more +2 TCP connections and 1 UDP flow +53 and 443 +7\r?$", text);
        Assert.Contains("1 other host in all, counted by address; who is at them is not observed. icat hosts <directory> lists every "
            + "one, and with --address <address> every process that reached one.", Regex.Replace(text, @"\s+", " "), Ordinal);
    }

    [Fact(DisplayName = "R18: icat processes --pid lists the other hosts an instance's connections ran to, by address, the most bytes first, its own connections alone")]
    public async Task IcatProcessesListsTheHostsAnInstanceReached()
    {
        using var session = new TemporarySession();
        Publish(session.Store, Rows());
        session.Store.ReleaseSegmentReaders();

        // The browser's three hosts, the most bytes first: its own two records to 142.250.1.1, not the updater's three.
        (InterCatExitCode code, string text, string said) = await Run("processes", session.Path, "--pid", "300");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("  Other hosts it reached, the most bytes sent and received first; who is at an address is not observed:",
            text, Ordinal);
        Assert.Matches(@"(?m)^  Host +Scope +Connections +Ports +Records +Sent +Received\r?$", text);
        string[] rows = [.. Regex.Matches(text, @"(?m)^  (\S+) +- +(\d+ (?:TCP|UDP) \S+) +(\d+) +(\d+) +([\d,]+ B) +no receives\r?$")
            .Select(match => $"{match.Groups[1].Value} | {match.Groups[2].Value} | {match.Groups[3].Value} | {match.Groups[4].Value} | "
                + match.Groups[5].Value)];
        Assert.Equal(
            [
                "142.250.1.1 | 1 TCP connection | 443 | 2 | 200 B",
                "142.250.1.10 | 1 TCP connection | 443 | 1 | 100 B",
                "8.8.8.8 | 1 UDP flow | 53 | 1 | 40 B",
            ],
            rows);
        Assert.DoesNotContain("more, quieter", text, Ordinal);

        // As JSON, every host of the instance asked about, and none of any other.
        (code, text, _) = await Run("processes", session.Path, "--pid", "300", "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        JsonElement[] instances = [.. JsonDocument.Parse(text).RootElement.GetProperty("instances").EnumerateArray()];
        JsonElement browser = instances.Single(item => item.GetProperty("process").GetProperty("processId").GetInt32() == 300);
        Assert.Equal(
            [("142.250.1.1", 1, 0, 2L, 200L, "443"), ("142.250.1.10", 1, 0, 1L, 100L, "443"), ("8.8.8.8", 0, 1, 1L, 40L, "53")],
            browser.GetProperty("otherHosts").EnumerateArray().Select(host => (host.GetProperty("address").GetString(),
                host.GetProperty("tcpConnections").GetInt32(), host.GetProperty("udpFlows").GetInt32(), host.GetProperty("records").GetInt64(),
                host.GetProperty("sentBytes").GetInt64(), Ports(host.GetProperty("ports")))));
        Assert.Equal(JsonValueKind.Null, instances.Single(item => item.GetProperty("process").GetProperty("processId").GetInt32() == 310)
            .GetProperty("otherHosts").ValueKind);

        // Who reached an address points to it.
        (_, text, _) = await Run("hosts", session.Path, "--address", "142.250.1.1");
        Assert.Contains("Who is at an address is not observed. icat processes <directory> --pid <pid> lists every other host a "
            + "process reached.", Regex.Replace(text, @"\s+", " "), Ordinal);

        // A host a process accepted a connection from says so, at its own port; the hosts tie on bytes and records, so by
        // address.
        using var service = new TemporarySession();
        Publish(service.Store,
        [
            Lifecycle(1, ObservationKind.Create, 300, 1) with { ResourceName = @"C:\Windows\System32\svchost.exe", SessionRelativeTicks = 1 },
            Transfer(10, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 300, 2).Between("192.168.1.10:3389", "10.0.0.5:50000"),
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 100, 300, 3).Between("192.168.1.10:3389", "10.0.0.5:50000"),
            Transfer(12, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 300, 4).Between("192.168.1.10:52000", "10.0.0.6:443"),
            Transfer(13, ObservationKind.Send, AccountingSide.SendSide, 100, 300, 5).Between("192.168.1.10:52000", "10.0.0.6:443"),
        ]);
        service.Store.ReleaseSegmentReaders();
        (code, text, said) = await Run("processes", service.Path, "--pid", "300");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Host +Scope +Connections +Ports +Accepted here +Records +Sent +Received\r?$", text);
        Assert.Matches(@"(?m)^  10\.0\.0\.5 +private +1 TCP connection +- +1 at 3389 +2 +no sends +100 B\r?$", text);
        Assert.Matches(@"(?m)^  10\.0\.0\.6 +private +1 TCP connection +443 +- +2 +100 B +no receives\r?$", text);
        Assert.True(text.IndexOf("10.0.0.5 ", Ordinal) < text.IndexOf("10.0.0.6 ", Ordinal));
        Assert.DoesNotMatch(@"\b50000\b", text);

        // Past twenty hosts, the quietest are counted, and where each is listed said, by its instance id.
        using var wide = new TemporarySession();
        Publish(wide.Store,
        [
            Lifecycle(1, ObservationKind.Create, 500, 1) with { ResourceName = @"C:\Apps\crawler.exe", SessionRelativeTicks = 1 },
            .. Enumerable.Range(1, 22).Select(index => Transfer(10 + index, ObservationKind.Send, AccountingSide.SendSide, 1_000 - index, 500,
                (ulong)(10 + index)).Between($"192.168.1.10:{52000 + index}", $"10.1.0.{index}:443") with { SessionRelativeTicks = 10 + index }),
        ]);
        wide.Store.ReleaseSegmentReaders();
        (code, text, said) = await Run("processes", wide.Path, "--pid", "500");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  10\.1\.0\.20 +private +1 TCP connection +443 +1 +980 B +no receives\r?$", text);
        Assert.DoesNotMatch(@"(?m)^  10\.1\.0\.2[12] ", text);
        string crawler = InterCat.Application.SessionOverviewProjector.Project(wide.Store).Nodes.Single().Id.ToString();
        wide.Store.ReleaseSegmentReaders();
        Assert.Contains($"2 more, quieter; --json lists every one, and icat channels <directory> --process {crawler} --one-sided each "
            + "of its connections.", Regex.Replace(text, @"\s+", " "), Ordinal);
        (_, text, _) = await Run("processes", wide.Path, "--pid", "500", "--json");
        Assert.Equal(22, JsonDocument.Parse(text).RootElement.GetProperty("instances")[0].GetProperty("otherHosts").GetArrayLength());
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

    [Fact(DisplayName = "R18: icat hosts says how many of a host's connections a process accepted from it, at its own ports, apart from the ports it ran to")]
    public async Task IcatHostsSaysWhatWasAccepted()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 300, 1) with { ResourceName = @"C:\Windows\System32\svchost.exe", SessionRelativeTicks = 1 },
            // 10.0.0.5 connected to the service at its own port 3389, from the host's 50000; the service connected to
            // 10.0.0.6's 443.
            Transfer(10, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 300, 2).Between("192.168.1.10:3389", "10.0.0.5:50000"),
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 100, 300, 3).Between("192.168.1.10:3389", "10.0.0.5:50000"),
            Transfer(12, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 300, 4).Between("192.168.1.10:52000", "10.0.0.6:443"),
            Transfer(13, ObservationKind.Send, AccountingSide.SendSide, 100, 300, 5).Between("192.168.1.10:52000", "10.0.0.6:443"),
        ]);
        session.Store.ReleaseSegmentReaders();

        // The accepted connection is said at the service's own port, never at the host's port 50000, which the host chose.
        (InterCatExitCode code, string text, string said) = await Run("hosts", session.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Host +Scope +Processes +Connections +Ports +Accepted here +Records\r?$", text);
        Assert.Matches(@"(?m)^  10\.0\.0\.5 +private +svchost\.exe · PID 300 +1 TCP connection +- +1 at 3389 +2\r?$", text);
        Assert.Matches(@"(?m)^  10\.0\.0\.6 +private +svchost\.exe · PID 300 +1 TCP connection +443 +- +2\r?$", text);
        Assert.DoesNotMatch(@"\b50000\b", text);
        Assert.Contains("Accepted here counts the TCP connections a process of this session accepted from the host", text, Ordinal);

        // Ranked by bytes, each host is counted alike: the accepted connection at the service's port, none at the host's.
        (code, text, said) = await Run("hosts", session.Path, "--rank-by", "bytes-sent");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Host +Scope +Processes +Connections +Ports +Accepted here +Records +Sent +Received\r?$", text);
        Assert.Matches(@"(?m)^  10\.0\.0\.6 +private +svchost\.exe · PID 300 +1 TCP connection +443 +- +2 +100 B +no receives\r?$", text);
        Assert.Matches(@"(?m)^  10\.0\.0\.5 +private +svchost\.exe · PID 300 +1 TCP connection +- +1 at 3389 +2 +no sends +100 B\r?$", text);
        Assert.DoesNotMatch(@"\b50000\b", text);

        // As JSON, how many each process connected and accepted, and its own ports the accepted ones arrived at, under any
        // ranking.
        foreach (string[] ranking in new[] { Array.Empty<string>(), ["--rank-by", "bytes-sent-and-received"] })
        {
            (code, text, _) = await Run(["hosts", session.Path, "--json", .. ranking]);
            Assert.Equal(InterCatExitCode.Success, code);
            JsonElement[] listed = [.. JsonDocument.Parse(text).RootElement.GetProperty("listed").EnumerateArray()];
            Assert.Equal(
                [("10.0.0.5", 0, 1, "3389", ""), ("10.0.0.6", 1, 0, "", "443")],
                listed.Select(host => (host.GetProperty("address").GetString(), host.GetProperty("connected").GetInt32(),
                    host.GetProperty("accepted").GetInt32(), Ports(host.GetProperty("acceptedPorts")), Ports(host.GetProperty("ports"))))
                    .OrderBy(host => host.Item1, StringComparer.Ordinal));
        }

        // Who reached an address says the same, process by process.
        (code, text, _) = await Run("hosts", session.Path, "--address", "10.0.0.5");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^  Process +Address +Connections +Ports +Accepted here +Records\r?$", text);
        Assert.Matches(@"(?m)^ *svchost\.exe · PID 300 +10\.0\.0\.5 +1 TCP connection +- +1 at 3389 +2\r?$", text);
        (code, text, _) = await Run("hosts", session.Path, "--address", "10.0.0.5", "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        JsonElement reached = JsonDocument.Parse(text).RootElement.GetProperty("reached")[0];
        Assert.Equal((0, 1, "3389"), (reached.GetProperty("connected").GetInt32(), reached.GetProperty("accepted").GetInt32(),
            Ports(reached.GetProperty("acceptedPorts"))));

        // A port alone finds the process that accepted connections at that port of its own, and never at the host's.
        (code, text, _) = await Run("hosts", session.Path, "--address", ":3389");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^ *svchost\.exe · PID 300 +10\.0\.0\.5 +1 TCP connection +- +1 at 3389 +2\r?$", text);
        (_, text, _) = await Run("hosts", session.Path, "--address", ":50000");
        Assert.Contains("No process of this session reached :50000", text, Ordinal);

        // Where no listed host had a connection accepted, there is no such column.
        (_, text, _) = await Run("hosts", session.Path, "--address", "10.0.0.6");
        Assert.Matches(@"(?m)^  Process +Address +Connections +Ports +Records\r?$", text);
        Assert.DoesNotContain("Accepted here", text, Ordinal);

        // Its process's connections name the accepted one from where it came, at the process's own end.
        string service = InterCat.Application.SessionOverviewProjector.Project(session.Store).Nodes.Single().Id.ToString();
        session.Store.ReleaseSegmentReaders();
        (code, text, _) = await Run("channels", session.Path, "--process", service, "--one-sided");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^  TCP from 10\.0\.0\.5:50000 at 192\.168\.1\.10:3389 · 2 records · .* · accepted in the capture, still open at its end\r?$",
            text);
        Assert.Matches(@"(?m)^  TCP to 10\.0\.0\.6:443 from 192\.168\.1\.10:52000 · 2 records · .* · connected in the capture, still open at its end\r?$",
            text);
        (code, text, _) = await Run("channels", session.Path, "--process", service, "--one-sided", "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        JsonElement document = JsonDocument.Parse(text).RootElement;
        Assert.Equal("connection-list-v2", document.GetProperty("contract").GetString());
        Assert.Equal(["Accepted", "Connected"], document.GetProperty("list").GetProperty("connections").EnumerateArray()
            .Select(connection => connection.GetProperty("opening").GetString()).Order(StringComparer.Ordinal));
    }

    private static string Ports(JsonElement ports) => string.Join(",", ports.EnumerateArray().Select(port => port.GetInt32()));

    [Fact(DisplayName = "R18: icat hosts says what it takes, refuses what names no session, and lists as many processes reaching an address as asked")]
    public async Task IcatHostsSaysWhatItTakes()
    {
        // Asked for help, it says what it takes, ports and the rule an address is named by among them.
        (InterCatExitCode code, string text, string said) = await Run("hosts", "--help");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.StartsWith("icat hosts <session-directory> [--address <address>] [--program <name>] [--limit <n>]", text, Ordinal);
        Assert.Contains("--program takes one program's processes alone", text, Ordinal);
        Assert.Contains("[--rank-by records|bytes-sent|bytes-received|bytes-sent-and-received]", text, Ordinal);
        Assert.Contains("[--investigation <file>] [--json]", text, Ordinal);
        Assert.Contains("142.250.1.1:443 or [2001:db8::1]:443", text, Ordinal);
        Assert.Contains("process accepted connections from a host, how many and at which of its own ports.", text, Ordinal);
        Assert.Contains("process whose connections there hold the most records and how many more reached it.", text, Ordinal);
        Assert.Contains("or which accepted connections at that port of their own", text, Ordinal);
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
        Assert.Matches(@"(?m)^  Host +Scope +Processes +Connections +Ports +Records +Sent +Received\r?$", text);
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
        Assert.Equal(("other-hosts-v8", "bytes-sent-and-received"), (root.GetProperty("contract").GetString(),
            root.GetProperty("rankedBy").GetString()));
        JsonElement transfers = root.GetProperty("listed")[0].GetProperty("transfers");
        Assert.Equal((5L, 500L, 0L, 0L), (transfers.GetProperty("sends").GetInt64(), transfers.GetProperty("sentBytes").GetInt64(),
            transfers.GetProperty("unmeasuredSends").GetInt64(), transfers.GetProperty("receives").GetInt64()));
        Assert.Equal("bytes-received", JsonDocument.Parse((await Run("hosts", session.Path, "--rank-by", "bytes-received", "--json")).Output)
            .RootElement.GetProperty("rankedBy").GetString());

        // A ranking hosts hold nothing of, and one beside --address, which lists processes, are refused.
        (code, _, said) = await Run("hosts", session.Path, "--rank-by", "rpc-calls-made");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("--rank-by expects records, bytes-sent, bytes-received or bytes-sent-and-received; "
            + "'rpc-calls-made' is not one.", said, Ordinal);
        (code, _, said) = await Run("hosts", session.Path, "--rank-by", "bytes-sent", "--address", "142.250");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("--rank-by ranks the hosts; --address lists the processes that reached one", said, Ordinal);
    }

    [Fact(DisplayName = "R18: icat processes --pid heads an instance's other hosts by those that connected to it only where it accepted every connection with them")]
    public async Task AnInstancesOtherHostsAreHeadedByWhichWayTheyConnected()
    {
        // server.exe accepted a connection from another host and made none; dns.exe accepted one and sent a datagram to
        // another host, a flow it did not accept.
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 200, 1) with { ResourceName = @"C:\Apps\server.exe" },
            Lifecycle(2, ObservationKind.Create, 300, 2) with { ResourceName = @"C:\Apps\dns.exe" },
            Transfer(10, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 200, 3).Between("192.168.1.10:445", "10.0.0.6:50000"),
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 100, 200, 4).Between("192.168.1.10:445", "10.0.0.6:50000"),
            Transfer(20, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 300, 5).Between("192.168.1.10:853", "10.0.0.8:50001"),
            Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 32, 300, 6).Between("192.168.1.10:53000", "10.0.0.53:53")
                with { Mechanism = Mechanism.Udp, EventId = 42 },
        ]);
        session.Store.ReleaseSegmentReaders();

        Assert.Contains("  Other hosts that connected to it, the most bytes sent and received first; who is at an address is not "
            + "observed:", (await Run("processes", session.Path, "--pid", "200")).Output.ReplaceLineEndings("\n"), Ordinal);
        Assert.Contains("  Other hosts it reached, the most bytes sent and received first; who is at an address is not observed:",
            (await Run("processes", session.Path, "--pid", "300")).Output.ReplaceLineEndings("\n"), Ordinal);
    }

    [Fact(DisplayName = "R18: icat hosts --investigation names each host's candidate other ends in the investigation's other captures, and refuses an investigation it cannot use")]
    public async Task IcatHostsNamesCandidateOtherEnds()
    {
        string folder = Path.Combine(Path.GetTempPath(), "InterCat.Cli.Tests.Hosts", Guid.NewGuid().ToString("N"));
        try
        {
            InterCat.Application.DemoInvestigationResult demo = InterCat.Application.DemoInvestigation.Create(Path.Combine(folder, "demo"));

            // The client's connections to the server's address are candidates for two of the server's processes; its
            // resolver's, which no member captured, for none.
            (InterCatExitCode code, string text, string said) = await Run("hosts", demo.ClientSession, "--investigation", demo.WorkspacePath);
            Assert.True(code == InterCatExitCode.Success, said);
            Assert.Matches(@"(?m)^  Host +Scope +Processes +Connections +Ports +Records +Candidate other ends\r?$", text);
            Assert.Matches(@"(?m)^  10\.0\.0\.20 +private +.* 80 +demo server: api\.exe · PID 5100 and files\.exe · PID 5300\r?$", text);
            Assert.Matches(@"(?m)^  10\.0\.0\.53 +private +.* 12 +none\r?$", text);
            Assert.Contains("never an established join", text, Ordinal);

            // As JSON, each listed host's candidate other ends; read without an investigation, none is said.
            (code, text, _) = await Run("hosts", demo.ClientSession, "--investigation", demo.WorkspacePath, "--json");
            Assert.Equal(InterCatExitCode.Success, code);
            JsonElement listed = JsonDocument.Parse(text).RootElement.GetProperty("listed");
            JsonElement api = listed[0].GetProperty("candidateOtherEnds")[0];
            Assert.Equal(("demo server", "api.exe", 5100, 1, false), (api.GetProperty("host").GetString(), api.GetProperty("process").GetString(),
                api.GetProperty("processId").GetInt32(), api.GetProperty("connections").GetInt32(), api.GetProperty("accepted").GetBoolean()));
            Assert.Equal(0, listed[1].GetProperty("candidateOtherEnds").GetArrayLength());
            Assert.Equal(JsonValueKind.Null, JsonDocument.Parse((await Run("hosts", demo.ClientSession, "--json")).Output).RootElement
                .GetProperty("listed")[0].GetProperty("candidateOtherEnds").ValueKind);

            // The server's api accepted its one connection with another host, which icat processes --pid heads so.
            Assert.Contains("  Other hosts that connected to it, the most bytes sent and received first; who is at an address is "
                + "not observed:", (await Run("processes", demo.ServerSession, "--pid", "5100")).Output.ReplaceLineEndings("\n"), Ordinal);

            // An investigation the session is no member of, a file that is none, and beside --address, are refused.
            using var stranger = new TemporarySession();
            Publish(stranger.Store, Rows());
            stranger.Store.ReleaseSegmentReaders();
            (code, _, said) = await Run("hosts", stranger.Path, "--investigation", demo.WorkspacePath);
            Assert.Equal(InterCatExitCode.InvalidInvocation, code);
            Assert.Contains("This session is no member of ", said, Ordinal);
            string notOne = Path.Combine(folder, "not-an-investigation.json");
            File.WriteAllText(notOne, "{}");
            (code, _, said) = await Run("hosts", demo.ClientSession, "--investigation", notOne);
            Assert.Equal(InterCatExitCode.InvalidInvocation, code);
            Assert.Contains("could not be read", said, Ordinal);
            (code, _, said) = await Run("hosts", demo.ClientSession, "--investigation", demo.WorkspacePath, "--address", "10.0.0");
            Assert.Equal(InterCatExitCode.InvalidInvocation, code);
            Assert.Contains("--investigation names the hosts' candidate other ends; --address lists the processes that reached one.",
                said, Ordinal);
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
