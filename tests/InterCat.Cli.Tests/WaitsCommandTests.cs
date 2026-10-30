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
/// icat waits (`contracts/waits-v1.md`, plan M12's bottleneck views, R18): the time processes spent inside the calls they
/// made, by interface and by the process that served them, with that server's own time; the servers callers waited on;
/// one process's calls and the calls it served; and what no time is said for.
/// </summary>
public sealed class WaitsCommandTests
{
    [Fact(DisplayName = "R3: icat waits lists the time inside each caller's calls, the most first, with the server's own time over linked calls")]
    public async Task IcatWaitsListsTheTimeInsideCalls()
    {
        using var session = Session(LinkRows());
        (InterCatExitCode code, string text, string said) = await Run("waits", session.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("WAITS", text, Ordinal);
        Assert.Matches(@"(?m)^  Counted +4 completed calls of 2 processes, the most time first\r?$", text);
        Assert.Equal(
            [
                "app.exe · PID 400 | svcctl (Service Control Manager) | services.exe · PID 1960 | 2 | 1 | 6.0 µs | 2.0 µs | 4.0 µs | 4.0 µs | "
                    + "3.0 µs of 6.0 µs (50%)",
                "other.exe · PID 410 | svcctl (Service Control Manager) | not linked: no ALPC send on its thread during the call | 1 | 0 | "
                    + "1.8 µs | 1.8 µs | 1.8 µs | 1.8 µs | -",
                "app.exe · PID 400 | svcctl (Service Control Manager) | not linked: no ALPC send on its thread during the call | 1 | 0 | "
                    + "1.0 µs | 1.0 µs | 1.0 µs | 1.0 µs | -",
            ],
            Rows(text, "WAITS", "Caller"));
        Assert.Equal(["services.exe · PID 1960 | 1 | 2 | 6.0 µs | 3.0 µs of 6.0 µs (50%)"], Rows(text, "SERVERS", "Server"));
        Assert.Contains(WaitText.Note, Regex.Replace(text, @"\s+", " "), Ordinal);
        Assert.DoesNotContain("No time is said for", text, Ordinal);

        // --limit lists as many as asked, saying how many more there are.
        (code, text, said) = await Run("waits", session.Path, "--limit", "1");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Single(Rows(text, "WAITS", "Caller"));
        Assert.Contains("2 more, with less time; --limit 3 lists every one.", text, Ordinal);

        // One process: the calls it made, and the calls it served, each caller named.
        (code, text, said) = await Run("waits", session.Path, "--process", "400");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("WAITS OF APP.EXE · PID 400", text, Ordinal);
        Assert.Matches(@"(?m)^  Counted +3 completed calls it made and 0 calls it served, the most time first\r?$", text);
        Assert.Equal(2, Rows(text, "ITS CALLS", "Calls to").Length);
        Assert.Contains("No linked client call reached it.", text, Ordinal);
        (code, text, said) = await Run("waits", session.Path, "--process", "1960");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("It made no completed call.", text, Ordinal);
        Assert.Equal(["app.exe · PID 400 | svcctl (Service Control Manager) | 2 | 6.0 µs | 3.0 µs of 6.0 µs (50%)"],
            Rows(text, "CALLS IT SERVED", "Caller"));

        // The JSON: every wait counted, those listed with both sides' times, and the servers.
        (code, text, said) = await Run("waits", session.Path, "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        JsonElement root = JsonDocument.Parse(text).RootElement;
        Assert.Equal(("call-waits-v1", 3, 3), (root.GetProperty("contract").GetString(), root.GetProperty("waits").GetInt32(),
            root.GetProperty("listed").GetArrayLength()));
        JsonElement wait = root.GetProperty("listed")[0];
        Assert.Equal((400, 1960, "svcctl", 6_000L, 2_000L, 3_000L), (wait.GetProperty("caller").GetProperty("processId").GetInt32(),
            wait.GetProperty("server").GetProperty("processId").GetInt32(), wait.GetProperty("interfaceName").GetString(),
            wait.GetProperty("inCallsNanoseconds").GetInt64(), wait.GetProperty("durationsNanoseconds").GetProperty("median").GetInt64(),
            wait.GetProperty("serverRanNanoseconds").GetInt64()));
        Assert.Equal((JsonValueKind.Null, 1L), (root.GetProperty("listed")[1].GetProperty("server").ValueKind,
            root.GetProperty("listed")[1].GetProperty("unresolved").GetProperty("NoSend").GetInt64()));
        Assert.Equal((1, 2L), (root.GetProperty("servers")[0].GetProperty("callers").GetInt32(),
            root.GetProperty("servers")[0].GetProperty("calls").GetInt64()));
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (root.GetProperty("process").ValueKind, root.GetProperty("served").ValueKind));
        (code, text, said) = await Run("waits", session.Path, "--process", "1960", "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        root = JsonDocument.Parse(text).RootElement;
        Assert.Equal((1960, 0, 1, JsonValueKind.Null), (root.GetProperty("process").GetProperty("processId").GetInt32(),
            root.GetProperty("waits").GetInt32(), root.GetProperty("served").GetArrayLength(), root.GetProperty("servers").ValueKind));
    }

    [Fact(DisplayName = "R3: icat waits says what no time is said for, and refuses what it cannot list")]
    public async Task IcatWaitsSaysWhatNoTimeIsSaidFor()
    {
        using var session = Session(WaitRows());
        (InterCatExitCode code, string text, string said) = await Run("waits", session.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("No time is said for 3 calls whose span is not validated: 1 open at capture end, 1 start not observed and 1 "
            + "no activity id, or 1 call the include-correlated evidence policy does not admit the caller of.",
            Regex.Replace(text, @"\s+", " "), Ordinal);
        (code, text, said) = await Run("waits", session.Path, "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        JsonElement root = JsonDocument.Parse(text).RootElement;
        Assert.Equal((1L, 1L, 1L, 1L), (root.GetProperty("notValidated").GetProperty("OpenAtCaptureEnd").GetInt64(),
            root.GetProperty("notValidated").GetProperty("StartNotObserved").GetInt64(),
            root.GetProperty("notValidated").GetProperty("NoActivityId").GetInt64(), root.GetProperty("notAdmitted").GetInt64()));

        // A session with no call says so, the coverage above saying whether one could have been seen.
        using var empty = Session(([Lifecycle(1, ObservationKind.Inventory, 100, 1) with { SessionRelativeTicks = 100 }], []));
        (code, text, said) = await Run("waits", empty.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("No completed call is in this session; the coverage above says whether one could have been seen.", text, Ordinal);
        Assert.DoesNotContain("SERVERS", text, Ordinal);

        foreach ((string[] args, string refusal) in new (string[], string)[]
        {
            (["--limit", "0"], "--limit expects a number of rows of 1 or more; '0' is not one."),
            (["--process", "999"], "--process 999 names no process: no process of the session held PID 999."),
            (["--evidence-policy", "2"], "--evidence-policy expects one of: DirectOnly, IncludeCorrelated, IncludeCandidates, "
                + "AllIncludingConflicting. '2' is not one."),
            (["--interval", "500:150"], "--interval expects start:end, each bound a count of 100-nanosecond session "
                + "ticks or a time with its unit, such as 1.5s, 250ms or 40us, with its end after its start; '500:150' is not one."),
        })
        {
            (InterCatExitCode refused, _, string why) = await Run(["waits", session.Path, .. args]);
            Assert.Equal(InterCatExitCode.InvalidInvocation, refused);
            Assert.Contains(refusal, Regex.Replace(why, @"\s+", " "), Ordinal);
        }

        (InterCatExitCode missing, _, string none) = await Run("waits");
        Assert.Equal(InterCatExitCode.InvalidInvocation, missing);
        Assert.Contains("A session directory is required: icat waits <directory>.", none, Ordinal);
        (InterCatExitCode help, string usage, _) = await Run("waits", "--help");
        Assert.Equal(InterCatExitCode.Success, help);
        Assert.Contains("icat waits <session-directory> [--process <pid | instance>] [--limit <n>]", usage, Ordinal);
    }

    [Fact(DisplayName = "R18: icat waits --interval times the calls that began in the interval alone, each to its stop, saying the interval and counting those that began outside it")]
    public async Task IcatWaitsReadsAnInterval()
    {
        using var session = Session(WaitRows());

        // From 150 to 500: PID 400's second and third calls to the service control manager, the third linked to the call PID
        // 1960 served, and PID 600's two short calls; PID 400's first call and its two after 500 began outside it.
        (InterCatExitCode code, string text, string said) = await Run("waits", session.Path, "--interval", "150:500");
        Assert.True(code == InterCatExitCode.Success, said);
        string interval = ComparisonText.Interval(new TimeRange(150, 500), culture: System.Globalization.CultureInfo.CurrentCulture);
        Assert.Matches(new Regex("^  Interval +" + Regex.Escape(interval + ", the calls that began in it alone") + @"\r?$", RegexOptions.Multiline),
            text);
        Assert.Matches(@"(?m)^  Counted +4 completed calls of 2 processes, the most time first\r?$", text);
        string[] waits = Rows(text, "WAITS", "Caller");
        Assert.Equal(3, waits.Length);
        Assert.Equal("PID 400 | svcctl (Service Control Manager) | PID 1960 | 1 | 1 | 4.0 µs | 4.0 µs | 4.0 µs | 4.0 µs | 2.5 µs of 4.0 µs (62%)",
            waits[0]);
        Assert.StartsWith("PID 600 | svcctl (Service Control Manager) | not linked: ", waits[1], Ordinal);
        Assert.Contains(" | 2 | 0 | 1.0 µs | ", waits[1], Ordinal);
        Assert.StartsWith("PID 400 | svcctl (Service Control Manager) | not linked: ", waits[2], Ordinal);
        Assert.Equal(["PID 1960 | 1 | 1 | 4.0 µs | 2.5 µs of 4.0 µs (62%)"], Rows(text, "SERVERS", "Server"));
        string words = Regex.Replace(text, @"\s+", " ");
        Assert.Contains("No time is said for 3 calls whose span is not validated: 1 open at capture end, 1 start not observed and 1 "
            + "no activity id, or 1 call the include-correlated evidence policy does not admit the caller of, or 3 calls that began "
            + "outside the interval.", words, Ordinal);
        Assert.Contains(WaitText.IntervalNote, words, Ordinal);

        // A call is timed to its stop, even past the interval's end.
        (code, text, said) = await Run("waits", session.Path, "--interval", "250:320");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Equal(["PID 400 | svcctl (Service Control Manager) | PID 1960 | 1 | 1 | 4.0 µs | 4.0 µs | 4.0 µs | 4.0 µs | 2.5 µs of 4.0 µs (62%)"],
            Rows(text, "WAITS", "Caller"));

        // One process over the interval: the calls it began in it, and the calls it served that began there.
        (code, text, said) = await Run("waits", session.Path, "--process", "1960", "--interval", "150:500");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("It began no completed call in the interval.", text, Ordinal);
        Assert.Equal(["PID 400 | svcctl (Service Control Manager) | 1 | 4.0 µs | 2.5 µs of 4.0 µs (62%)"], Rows(text, "CALLS IT SERVED", "Caller"));

        // An interval no call began in says so, counting every call outside it.
        (code, text, said) = await Run("waits", session.Path, "--interval", "2000:3000");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("No completed call began in the interval; the coverage above says whether one could have been seen.", text, Ordinal);
        Assert.Contains("No time is said for 11 calls that began outside the interval.", Regex.Replace(text, @"\s+", " "), Ordinal);

        // The document says the interval and how many calls began outside it.
        (code, text, said) = await Run("waits", session.Path, "--interval", "150:500", "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        JsonElement root = JsonDocument.Parse(text).RootElement;
        Assert.Equal((150L, 500L, 3L), (root.GetProperty("interval").GetProperty("startTicks").GetInt64(),
            root.GetProperty("interval").GetProperty("endTicks").GetInt64(), root.GetProperty("outsideInterval").GetInt64()));
        (code, text, _) = await Run("waits", session.Path, "--json");
        root = JsonDocument.Parse(text).RootElement;
        Assert.Equal((JsonValueKind.Null, 0L), (root.GetProperty("interval").ValueKind, root.GetProperty("outsideInterval").GetInt64()));
    }

    private static TemporarySession Session((ObservationRowV1[] Rows, SourceFieldRowV1[] Fields) session)
    {
        var temporary = new TemporarySession();
        Publish(temporary.Store, session.Rows, fields: session.Fields, coverage: TransportLedger(tcp: true, udp: true));
        temporary.Store.ReleaseSegmentReaders();
        return temporary;
    }

    /// <summary>The rows of the table beneath <paramref name="heading"/> whose header starts with <paramref name="first"/>, each cell joined by " | ".</summary>
    private static string[] Rows(string text, string heading, string first)
    {
        string[] lines = [.. text.Split('\n').Select(line => line.TrimEnd('\r'))];
        int start = Array.FindIndex(lines, line => line.StartsWith(heading, Ordinal));
        Assert.True(start >= 0, $"No heading {heading}.");
        int header = Array.FindIndex(lines, start, line => line.StartsWith("  " + first + " ", Ordinal));
        Assert.True(header >= 0, $"No table under {heading}.");
        return [.. lines.Skip(header + 1).TakeWhile(line => line.StartsWith("  ", Ordinal) && Regex.IsMatch(line, @"\S\s{2,}\S"))
            .Select(line => string.Join(" | ", Regex.Split(line.Trim(), @"\s{2,}")))];
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
