using System.Globalization;
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
/// icat patterns (`contracts/patterns-v1.md`, plan M12, R18): the session's recurring communication patterns, the heaviest
/// first, with who took part in each role; one pattern's signature and every participant; and what no pattern holds.
/// </summary>
public sealed class PatternsCommandTests
{
    [Fact(DisplayName = "R18: icat patterns lists the recurring patterns, the heaviest first, each end in its role, and counts what no pattern holds")]
    public async Task IcatPatternsListsTheRecurringPatterns()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = PatternRows();
        Publish(session.Store, rows, fields: fields, coverage: TransportLedger(tcp: true, udp: true));
        session.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string text, string said) = await Run("patterns", session.Path, "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        JsonElement spread = JsonDocument.Parse(text).RootElement.GetProperty("span");
        var span = new TimeRange(spread.GetProperty("startTicks").GetInt64(), spread.GetProperty("endTicks").GetInt64());
        (code, text, said) = await Run("patterns", session.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Patterns +5 recurring patterns, and 3 that each occurred once, which --all lists\r?$", text);
        Assert.Matches(@"(?m)^  Coverage +covered for TCP and UDP · no other mechanism collected\r?$", text);
        Assert.Equal(
            [
                "1 | RPC calls to svcctl (Service Control Manager), procedure 7 | 3 calls made · 3 served | 1 process | 1 process | 1 of 10",
                "2 | TCP at port 8080 here | 3 connections | 2 processes of 1 program | 1 process of 1 program | 3 of 10",
                "3 | TCP to port 443 at other hosts | 2 connections | 2 processes of 1 program | 2 hosts | 2 of 10",
                "4 | UDP to port 53 at other hosts | 2 flows | 1 process of 1 program | 1 host | 2 of 10",
                "5 | UDP at port 5353 here | 2 flows | 1 process of 1 program | 1 process of 1 program | 1 of 10",
            ],
            Listed(text));
        string flowed = Regex.Replace(text, @"\s+", " ");
        Assert.Contains("A pattern groups what recurs by its mechanism, its service port or RPC interface and procedure, and where "
            + "its server end is, never by a process, address or time;", flowed, Ordinal);
        Assert.Contains("In no pattern: 2 TCP connections the capture saw open at neither end, so no end of them has a role and 2 "
            + "UDP flows between two dynamic ports, which have no service port.", flowed, Ordinal);
        Assert.DoesNotContain("more, lighter", text, Ordinal);

        // --all lists the one that occurred once too, and --limit as many as asked, saying how many more there are.
        (code, text, said) = await Run("patterns", session.Path, "--all");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Patterns +5 recurring patterns, and 3 that each occurred once, listed too\r?$", text);
        Assert.Equal(
            ["6 | TCP at port 3389 here | 1 connection | 1 host | 1 process of 1 program | 1 of 10",
                "7 | TCP at port 9000 here | 1 connection | 1 process of 1 program | ends no record holds | 1 of 10",
                "8 | UDP at port 161 here | 1 flow | 1 host | 1 process of 1 program | 1 of 10"],
            Listed(text)[^3..]);
        (code, text, said) = await Run("patterns", session.Path, "--limit", "1");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Single(Listed(text));
        Assert.Contains("4 more, lighter; --limit 5 lists every one.", text, Ordinal);

        // One pattern: what its signature is made of, when it occurred, and every participant in each role.
        (code, text, said) = await Run("patterns", session.Path, "--pattern", "2");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("PATTERN 2: TCP AT PORT 8080 HERE", text, Ordinal);
        Assert.Matches(@"(?m)^  Signature +tcp:here:8080\r?$", text);
        flowed = Regex.Replace(text, @"\s+", " ");
        Assert.Contains("Made of TCP connections a process of this machine accepted at this port, whoever connected:", flowed, Ordinal);
        Assert.Contains("Occurred 3 connections, the first beginning at " + WorkspaceTime.FormatInstant(1_000, span.SpanTicks, CultureInfo.CurrentCulture)
            + " and the last at " + WorkspaceTime.FormatInstant(8_000, span.SpanTicks, CultureInfo.CurrentCulture) + ", in 3 of the session's 10 tenths", flowed,
            Ordinal);
        Assert.Contains("Clients 2 processes of 1 program", flowed, Ordinal);
        Assert.Matches(@"(?m)^  browser\.exe · PID 3000 +browser\.exe +2\r?$", text);
        Assert.Matches(@"(?m)^  browser\.exe · PID 3001 +browser\.exe +1\r?$", text);
        Assert.Matches(@"(?m)^  server\.exe · PID 4000 +server\.exe +3\r?$", text);
        Assert.DoesNotContain("who is at it is not observed", text, Ordinal);
        (code, text, said) = await Run("patterns", session.Path, "--pattern", "3");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  142\.250\.1\.1 +- +1\r?$", text);
        Assert.Contains("Another host is named by its address: who is at it is not observed.", text, Ordinal);
        (code, text, said) = await Run("patterns", session.Path, "--pattern", "1");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  End +Program +Calls made\r?$", text);
        Assert.Matches(@"(?m)^  End +Program +Calls served\r?$", text);

        // The JSON: every pattern counted, those listed with their participants and how each signature was made.
        (code, text, said) = await Run("patterns", session.Path, "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        JsonElement root = JsonDocument.Parse(text).RootElement;
        Assert.Equal("communication-patterns-v1", root.GetProperty("contract").GetString());
        Assert.Equal((8, 5, 3, false, 10), (root.GetProperty("patterns").GetInt32(), root.GetProperty("recurring").GetInt32(),
            root.GetProperty("occurredOnce").GetInt32(), root.GetProperty("includesOnce").GetBoolean(), root.GetProperty("parts").GetInt32()));
        Assert.Equal((2L, 2L, 0L, 0L), (root.GetProperty("unopenedConnections").GetInt64(), root.GetProperty("dynamicFlows").GetInt64(),
            root.GetProperty("callsWithoutInterface").GetInt64(), root.GetProperty("notAdmitted").GetInt64()));
        Assert.Equal(5, root.GetProperty("listed").GetArrayLength());
        JsonElement local = root.GetProperty("listed")[1];
        Assert.Equal((2, "tcp:here:8080", "TCP", "Here", 8080, "TCP at port 8080 here"), (local.GetProperty("rank").GetInt32(),
            local.GetProperty("key").GetString(), local.GetProperty("mechanism").GetString(), local.GetProperty("place").GetString(),
            local.GetProperty("port").GetInt32(), local.GetProperty("signature").GetString()));
        Assert.Equal((3L, 0L, 3), (local.GetProperty("occurrences").GetInt64(), local.GetProperty("served").GetInt64(),
            local.GetProperty("tenths").GetInt32()));
        JsonElement client = local.GetProperty("clients")[0];
        Assert.Equal(("Process", 3_000, "browser.exe", "browser.exe", 2L), (client.GetProperty("kind").GetString(),
            client.GetProperty("processId").GetInt32(), client.GetProperty("name").GetString(), client.GetProperty("program").GetString(),
            client.GetProperty("occurrences").GetInt64()));
        JsonElement host = root.GetProperty("listed")[2].GetProperty("servers")[0];
        Assert.Equal(("Host", "142.250.1.1", JsonValueKind.Null), (host.GetProperty("kind").GetString(),
            host.GetProperty("address").GetString(), host.GetProperty("instanceId").ValueKind));
        JsonElement called = root.GetProperty("listed")[0];
        Assert.Equal(("svcctl", 7L, 3L), (called.GetProperty("interfaceName").GetString(), called.GetProperty("procedure").GetInt64(),
            called.GetProperty("served").GetInt64()));
        Assert.StartsWith("RPC calls whose starts named this interface and procedure", called.GetProperty("explanation").GetString(),
            Ordinal);
        (code, text, said) = await Run("patterns", session.Path, "--pattern", "6", "--all", "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        root = JsonDocument.Parse(text).RootElement;
        Assert.Equal((6, "tcp:here:3389", true), (root.GetProperty("listed")[0].GetProperty("rank").GetInt32(),
            root.GetProperty("listed")[0].GetProperty("key").GetString(), root.GetProperty("includesOnce").GetBoolean()));
        Assert.Equal("Host", Participant(root, 0, "clients", 0, out string? address));
        Assert.Equal("10.0.0.5", address);
        (code, text, said) = await Run("patterns", session.Path, "--pattern", "7", "--all", "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        root = JsonDocument.Parse(text).RootElement;
        Assert.Equal("UnseenEnd", Participant(root, 0, "servers", 0, out address));
        Assert.Null(address);
        (code, text, said) = await Run("patterns", session.Path, "--pattern", "7", "--all");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  ends on this machine no record holds +- +1\r?$", text);

        // The last of those listed is shown as every other is.
        (code, text, said) = await Run("patterns", session.Path, "--pattern", "8", "--all");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("PATTERN 8: UDP AT PORT 161 HERE", text, Ordinal);
        (code, text, said) = await Run("patterns", session.Path, "--pattern", "5");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("PATTERN 5: UDP AT PORT 5353 HERE", text, Ordinal);
    }

    [Fact(DisplayName = "R18: icat patterns refuses a rank it does not have, a limit beside a pattern, and a policy or limit it does not know")]
    public async Task IcatPatternsRefusesWhatItCannotList()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = PatternRows();
        Publish(session.Store, rows, fields: fields, coverage: TransportLedger(tcp: true, udp: true));
        session.Store.ReleaseSegmentReaders();

        foreach ((string[] args, string refusal) in new (string[], string)[]
        {
            (["--pattern", "0"], "--pattern names a pattern by its rank, 1 for the heaviest; '0' is not one."),
            (["--pattern", "6"], "--pattern 6 names no pattern: the session has 5 recurring patterns; --all counts those that occurred once."),
            (["--pattern", "9", "--all"], "--pattern 9 names no pattern: the session has 8 patterns."),
            (["--pattern", "1", "--limit", "2"], "--limit lists patterns and --pattern shows one; pass one of them."),
            (["--limit", "0"], "--limit expects a number of patterns of 1 or more; '0' is not one."),
            (["--evidence-policy", "2"], "--evidence-policy expects one of: DirectOnly, IncludeCorrelated, IncludeCandidates, "
                + "AllIncludingConflicting. '2' is not one."),
        })
        {
            (InterCatExitCode code, _, string said) = await Run(["patterns", session.Path, .. args]);
            Assert.Equal(InterCatExitCode.InvalidInvocation, code);
            Assert.Contains(refusal, Regex.Replace(said, @"\s+", " "), Ordinal);
        }

        (InterCatExitCode missing, _, string why) = await Run("patterns");
        Assert.Equal(InterCatExitCode.InvalidInvocation, missing);
        Assert.Contains("A session directory is required: icat patterns <directory>.", why, Ordinal);
        (InterCatExitCode direct, string text, string said2) = await Run("patterns", session.Path, "--evidence-policy", "directonly");
        Assert.True(direct == InterCatExitCode.Success, said2);
        Assert.Contains("This session holds no pattern the direct-only evidence policy admits; what it did not admit is counted "
            + "beneath, and the coverage above says whether any could have been seen.", Regex.Replace(text, @"\s+", " "), Ordinal);
        Assert.Contains("the direct-only evidence policy did not admit", Regex.Replace(text, @"\s+", " "), Ordinal);
    }

    [Fact(DisplayName = "R21: icat patterns says why it lists none - a session with no pattern, or none that occurred more than once")]
    public async Task IcatPatternsSaysWhyItListsNone()
    {
        using var empty = new TemporarySession();
        Publish(empty.Store, [Lifecycle(1, ObservationKind.Inventory, 100, 1) with { ResourceName = @"C:\Apps\client.exe", SessionRelativeTicks = 100 }]);
        empty.Store.ReleaseSegmentReaders();
        (InterCatExitCode code, string text, string said) = await Run("patterns", empty.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("This session holds no pattern: no TCP connection the capture saw open, UDP flow with a service port or RPC "
            + "call naming its interface is in it; the coverage above says whether one could have been seen.",
            Regex.Replace(text, @"\s+", " "), Ordinal);
        Assert.DoesNotContain("In no pattern", text, Ordinal);

        using var once = new TemporarySession();
        Publish(once.Store,
        [
            Transfer(10, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 10)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 1_000 },
            Transfer(11, ObservationKind.Accept, AccountingSide.EndpointActivity, 0, 300, 11)
                .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = 1_100 },
        ]);
        once.Store.ReleaseSegmentReaders();
        (code, text, said) = await Run("patterns", once.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Patterns +0 recurring patterns, and 1 that occurred once, which --all lists\r?$", text);
        Assert.Contains("No pattern occurred more than once; --all lists those that occurred once.", text, Ordinal);
        Assert.Empty(Listed(text));
    }

    /// <summary>One participant's kind, from the JSON, and its address where it has one.</summary>
    private static string Participant(JsonElement root, int pattern, string role, int index, out string? address)
    {
        JsonElement participant = root.GetProperty("listed")[pattern].GetProperty(role)[index];
        address = participant.GetProperty("address").GetString();
        return participant.GetProperty("kind").GetString()!;
    }

    /// <summary>The table's rows, each cell joined by " | ".</summary>
    private static string[] Listed(string text) =>
        [.. text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => Regex.IsMatch(line, @"^  \d+  "))
            .Select(line => string.Join(" | ", Regex.Split(line.Trim(), @"\s{2,}")))];

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
