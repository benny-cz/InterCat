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
        Assert.Matches(@"(?m)^  Host +Connections +Ports +Records\r?$", text);
        string[] rows = [.. Regex.Matches(text, @"(?m)^  (\S+) +(\d.*?(?:process|processes)) +(\d.*?) +(\d+)\r?$").Select(match =>
            $"{match.Groups[1].Value} | {match.Groups[2].Value} | {match.Groups[3].Value.TrimEnd()} | {match.Groups[4].Value}")];
        Assert.Equal(
            [
                "142.250.1.1 | 2 TCP connections from 2 processes | 443 and 8443 | 5",
                "142.250.1.10 | 1 TCP connection from 1 process | 443 | 1",
                "2001:db8::1 | 1 TCP connection from 1 process | 443 | 1",
                "8.8.8.8 | 1 UDP flow from 1 process | 53 | 1",
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
        Assert.Equal(("other-hosts-v2", 4, 2), (root.GetProperty("contract").GetString(), root.GetProperty("hosts").GetInt32(),
            root.GetProperty("processes").GetInt32()));
        JsonElement busiest = root.GetProperty("listed")[0];
        Assert.Equal(("142.250.1.1", 2, 0, 2, 5L), (busiest.GetProperty("address").GetString(), busiest.GetProperty("tcpConnections").GetInt32(),
            busiest.GetProperty("udpFlows").GetInt32(), busiest.GetProperty("processes").GetInt32(), busiest.GetProperty("records").GetInt64()));
        Assert.Equal([443, 8443], busiest.GetProperty("ports").EnumerateArray().Select(port => port.GetInt32()));

        // A limit that is no count of hosts is refused.
        (code, _, said) = await Run("hosts", session.Path, "--limit", "0");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("--limit expects a number of hosts of 1 or more", said + text, Ordinal);

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
