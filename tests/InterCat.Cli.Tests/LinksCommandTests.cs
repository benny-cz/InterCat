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
/// icat links (`contracts/links-v1.md`, plan M12's causal navigation, R18): a process's recorded links, those the evidence
/// policy does not follow apart, the processes created near it that nothing links to it, what of it links to nothing, and
/// the shortest chain of followed links to another process.
/// </summary>
public sealed class LinksCommandTests
{
    [Fact(DisplayName = "P8: icat links lists what led to a process and what it led to, and says what is only near it in time")]
    public async Task IcatLinksListsAProcesssLinks()
    {
        using var session = Session();
        (InterCatExitCode code, string text, string said) = await Run("links", session.Path, "--process", "400");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("LINKS OF APP.EXE · PID 400", text, Ordinal);
        Assert.Matches(@"(?m)^  Follows +links the include-correlated evidence policy admits: direct and correlated\r?$", text);
        Assert.Equal(["its parent | shell.exe · PID 300 | the child's creation record names the parent by its start key | direct"],
            Rows(text, "LED TO IT"));
        Assert.Equal(
            [
                "served its calls | services.exe · PID 1960 | 2 calls to svcctl (Service Control Manager), each reaching the call "
                    + "that served it through its one ALPC message | correlated",
                "it connected to | server.exe · PID 4000 | 1 connection to port 8080 that it made, as the capture saw it open | correlated",
                "it connected to | 93.184.216.34 | 1 connection to port 443 at that address that it made; who is at an address is "
                    + "not observed | correlated",
                "it connected to | an end on this machine no record holds | 1 connection to port 9100 on this machine that it made; "
                    + "no record holds the other end | correlated",
            ],
            Rows(text, "IT LED TO"));
        string flowed = Regex.Replace(text, @"\s+", " ");
        Assert.Contains("Created within a second of it, and linked to it by nothing; nearness in time is never a link: other.exe · "
            + "PID 410, created 0.2 µs after it sibling.exe · PID 430, created 0.3 µs after it", flowed, Ordinal);
        Assert.Contains("NOT LINKED", text, Ordinal);
        Assert.Contains("2 TCP connections whose opening no record shows, so neither end is known to have made them; 2 UDP flows, "
            + "which nothing opens; 1 call whose other end is unresolved: no ALPC send on its thread during the call.", flowed, Ordinal);
        Assert.Contains("Another host is named by its address: who is at it is not observed.", text, Ordinal);
        Assert.Contains(LinkText.Note + " --to <process> finds a chain of them to another process.", flowed, Ordinal);
        Assert.DoesNotContain("NOT FOLLOWED", text, Ordinal);

        // A start named by a reused PID's later holder is listed as not followed; a process created later than a second
        // after the rest, and one whose creation was not witnessed, are near none.
        (code, text, said) = await Run("links", session.Path, "--process", "420");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("Nothing the evidence policy follows led to it.", text, Ordinal);
        Assert.Contains("It led to nothing the evidence policy follows.", text, Ordinal);
        Assert.Contains("The include-correlated evidence policy does not admit these links' strength: each is possible, not "
            + "established, and no chain is made through it.", Regex.Replace(text, @"\s+", " "), Ordinal);
        Assert.Equal(["its parent | new.exe · PID 77 #2 | the child's creation record names the parent's PID alone, which the parent "
            + "held when the child was created, after another process had held it | candidate"], Rows(text, "NOT FOLLOWED"));
        Assert.DoesNotContain("NOT LINKED", text, Ordinal);
        Assert.DoesNotContain("Another host is named", text, Ordinal);
        (code, text, said) = await Run("links", session.Path, "--process", "420", "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        JsonElement unfollowed = Assert.Single(JsonDocument.Parse(text).RootElement.GetProperty("notFollowed").EnumerateArray());
        Assert.Equal(("Started", "Candidate", 77), (unfollowed.GetProperty("kind").GetString(), unfollowed.GetProperty("strength").GetString(),
            unfollowed.GetProperty("from").GetProperty("processId").GetInt32()));
        (code, text, said) = await Run("links", session.Path, "--process", "450");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("No other process was created within a second of it without a link to it.", text, Ordinal);
        Assert.DoesNotContain("Another host is named", text, Ordinal);
        (code, text, said) = await Run("links", session.Path, "--process", "300");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("Its creation was not witnessed, so no process is said to have been created near it.", text, Ordinal);

        // The JSON: the process, its links each with its evidence, what is near and what links to nothing.
        (code, text, said) = await Run("links", session.Path, "--process", "400", "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        JsonElement root = JsonDocument.Parse(text).RootElement;
        Assert.Equal(("evidence-links-v1", "evidence-links-v1", "IncludeCorrelated", 400), (root.GetProperty("contract").GetString(),
            root.GetProperty("rule").GetString(), root.GetProperty("evidencePolicy").GetString(),
            root.GetProperty("process").GetProperty("processId").GetInt32()));
        JsonElement parent = root.GetProperty("ledToIt")[0];
        Assert.Equal(("Started", "Direct", 300, "Process"), (parent.GetProperty("kind").GetString(), parent.GetProperty("strength").GetString(),
            parent.GetProperty("from").GetProperty("processId").GetInt32(), parent.GetProperty("from").GetProperty("kind").GetString()));
        JsonElement served = root.GetProperty("itLedTo")[0];
        Assert.Equal(("Called", 2L, "svcctl"), (served.GetProperty("kind").GetString(), served.GetProperty("occurrences").GetInt64(),
            served.GetProperty("interfaceName").GetString()));
        JsonElement host = root.GetProperty("itLedTo")[2];
        Assert.Equal(("Host", "93.184.216.34", 443), (host.GetProperty("to").GetProperty("kind").GetString(),
            host.GetProperty("to").GetProperty("address").GetString(), host.GetProperty("port").GetInt32()));
        Assert.Equal("UnseenEnd", root.GetProperty("itLedTo")[3].GetProperty("to").GetProperty("kind").GetString());
        Assert.Equal((true, 5, 2L, 410), (root.GetProperty("creationWitnessed").GetBoolean(), root.GetProperty("near").GetArrayLength(),
            root.GetProperty("near")[0].GetProperty("offsetTicks").GetInt64(),
            root.GetProperty("near")[0].GetProperty("process").GetProperty("processId").GetInt32()));
        JsonElement unlinked = root.GetProperty("unlinked");
        Assert.Equal((2L, 2L, 1L, JsonValueKind.Null), (unlinked.GetProperty("unopenedConnections").GetInt64(),
            unlinked.GetProperty("flows").GetInt64(), unlinked.GetProperty("unresolvedCalls").GetProperty("NoSend").GetInt64(),
            root.GetProperty("chain").ValueKind));
    }

    [Fact(DisplayName = "P8: icat links --to follows a known dependency link by link, and finds none from an unrelated process at the same moment")]
    public async Task IcatLinksFollowsAChain()
    {
        using var session = Session();
        (InterCatExitCode code, string text, string said) = await Run("links", session.Path, "--process", "300", "--to", "1960");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("CHAIN FROM SHELL.EXE · PID 300 TO SERVICES.EXE · PID 1960", text, Ordinal);
        Assert.Equal(
            [
                "1 | shell.exe · PID 300 started app.exe · PID 400 | the child's creation record names the parent by its start key | direct",
                "2 | app.exe · PID 400's calls were served by services.exe · PID 1960 | 2 calls to svcctl (Service Control Manager), "
                    + "each reaching the call that served it through its one ALPC message | correlated",
            ],
            Rows(text, "CHAIN FROM"));
        Assert.Contains("2 links lead from shell.exe · PID 300 to services.exe · PID 1960; the chain is as strong as its weakest link: "
            + "correlated.", Regex.Replace(text, @"\s+", " "), Ordinal);

        // A connection in a chain is said from the end that made it.
        (code, text, said) = await Run("links", session.Path, "--process", "300", "--to", "4000");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Equal("2 | app.exe · PID 400 connected to server.exe · PID 4000 | 1 connection to port 8080 that it made, as the "
            + "capture saw it open | correlated", Rows(text, "CHAIN FROM")[1]);

        // other.exe made a call at the same moment as app.exe's first, which nothing links to services.exe.
        (code, text, said) = await Run("links", session.Path, "--process", "410", "--to", "1960");
        Assert.True(code == InterCatExitCode.Success, said);
        string flowed = Regex.Replace(text, @"\s+", " ");
        Assert.Contains("No chain of links the include-correlated evidence policy follows leads from other.exe · PID 410 to "
            + "services.exe · PID 1960.", flowed, Ordinal);
        Assert.DoesNotContain("One leads", flowed, Ordinal);

        // A chain the other way is named, with the command that lists it; one through a link not followed, with the policy
        // that follows it.
        LinkReading reading = EvidenceLinks.Read(session.Store);
        string Instance(int pid, int holder = 1) =>
            reading.Processes.Values.Single(node => node.ProcessId == pid && node.PidHolder == holder).Id.ToString();
        (code, text, said) = await Run("links", session.Path, "--process", "1960", "--to", "300");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains($"One leads the other way, from shell.exe · PID 300 to services.exe · PID 1960: --process {Instance(300)} --to "
            + $"{Instance(1960)} lists it.", Regex.Replace(text, @"\s+", " "), Ordinal);
        (code, text, said) = await Run("links", session.Path, "--process", Instance(77, 2), "--to", "420");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("One leads there through a link it does not follow, the weakest candidate: --evidence-policy "
            + "include-candidates follows it.", Regex.Replace(text, @"\s+", " "), Ordinal);
        (code, text, said) = await Run("links", session.Path, "--process", Instance(77, 2), "--to", "420", "--evidence-policy",
            "include-candidates", "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        JsonElement chain = JsonDocument.Parse(text).RootElement.GetProperty("chain");
        Assert.Equal((true, "Candidate", 1, 420), (chain.GetProperty("found").GetBoolean(), chain.GetProperty("weakest").GetString(),
            chain.GetProperty("links").GetArrayLength(), chain.GetProperty("to").GetProperty("processId").GetInt32()));

        // Under direct links alone, the dependency's correlated link is not followed, and the command says which policy is.
        (code, text, said) = await Run("links", session.Path, "--process", "300", "--to", "1960", "--evidence-policy", "direct-only");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("One leads there through a link it does not follow, the weakest correlated: --evidence-policy "
            + "include-correlated follows it.", Regex.Replace(text, @"\s+", " "), Ordinal);
        (code, text, said) = await Run("links", session.Path, "--process", "1960", "--to", "300", "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        chain = JsonDocument.Parse(text).RootElement.GetProperty("chain");
        Assert.Equal((false, JsonValueKind.Null), (chain.GetProperty("found").GetBoolean(), chain.GetProperty("weakest").ValueKind));
    }

    [Fact(DisplayName = "R18: icat links --interval lists what began in the interval alone, saying the interval and counting what of the process began outside it")]
    public async Task IcatLinksReadsAnInterval()
    {
        using var session = Session();

        // From 50 to 1,250: app.exe's calls and its first two connections; its start, and its later connections and flows,
        // began outside it.
        (InterCatExitCode code, string text, string said) = await Run("links", session.Path, "--process", "400", "--interval", "50:1250");
        Assert.True(code == InterCatExitCode.Success, said);
        string interval = ComparisonText.Interval(new TimeRange(50, 1_250), culture: System.Globalization.CultureInfo.CurrentCulture);
        Assert.Matches(new Regex("^  Interval +" + Regex.Escape(interval + ", what began in it alone") + @"\r?$", RegexOptions.Multiline), text);
        Assert.Contains("Nothing the evidence policy follows led to it.", text, Ordinal);
        Assert.Equal(["served its calls", "it connected to", "it connected to"], Rows(text, "IT LED TO").Select(row => row.Split(" | ")[0]));
        string flowed = Regex.Replace(text, @"\s+", " ");
        Assert.Contains("1 call whose other end is unresolved: no ALPC send on its thread during the call.", flowed, Ordinal);
        Assert.Contains("OUTSIDE THE INTERVAL", text, Ordinal);
        Assert.Contains("6 of the starts, calls, connections and flows it took part in began outside the interval, so none of them is "
            + "listed here.", flowed, Ordinal);

        // A chain runs through the interval's links alone, and says so where none does.
        (code, text, said) = await Run("links", session.Path, "--process", "300", "--to", "1960", "--interval", "50:1250");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("No chain of links the include-correlated evidence policy follows leads from shell.exe · PID 300 to "
            + "services.exe · PID 1960 in the interval.", Regex.Replace(text, @"\s+", " "), Ordinal);
        (code, text, said) = await Run("links", session.Path, "--process", "300", "--to", "1960");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.DoesNotContain("OUTSIDE THE INTERVAL", text, Ordinal);

        // The document says the interval and how much of the process began outside it.
        (code, text, said) = await Run("links", session.Path, "--process", "400", "--interval", "50:1250", "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        JsonElement root = JsonDocument.Parse(text).RootElement;
        Assert.Equal((50L, 1_250L, 6L), (root.GetProperty("interval").GetProperty("startTicks").GetInt64(),
            root.GetProperty("interval").GetProperty("endTicks").GetInt64(), root.GetProperty("outsideInterval").GetInt64()));
        (code, text, _) = await Run("links", session.Path, "--process", "400", "--json");
        root = JsonDocument.Parse(text).RootElement;
        Assert.Equal((JsonValueKind.Null, 0L), (root.GetProperty("interval").ValueKind, root.GetProperty("outsideInterval").GetInt64()));
    }

    [Fact(DisplayName = "R18: icat links refuses a process it cannot name, a PID the session reused, a chain to itself, and a policy it does not know")]
    public async Task IcatLinksRefusesWhatItCannotName()
    {
        using var session = Session();
        LinkReading reading = EvidenceLinks.Read(session.Store);
        string Instance(int pid, int holder) =>
            reading.Processes.Values.Single(node => node.ProcessId == pid && node.PidHolder == holder).Id.ToString();
        string unknown = Guid.NewGuid().ToString("N");
        foreach ((string[] args, string refusal) in new (string[], string)[]
        {
            ([], "--process names the process whose links to list: its PID, or its instance id where the PID was reused."),
            (["--process", "999"], "--process 999 names no process: no process of the session held PID 999."),
            (["--process", "77"], $"--process 77 names 2 processes, since the PID was reused: {Instance(77, 1)} (old.exe · PID 77 #1), "
                + $"{Instance(77, 2)} (new.exe · PID 77 #2). Name one by its instance id: --process <instance>."),
            (["--process", "app.exe"], "--process names a process by its PID or its instance id, as icat processes prints them; "
                + "'app.exe' is neither."),
            (["--process", unknown], $"--process {unknown} names no process instance of the session's current generation."),
            (["--process", "400", "--to", "999"], "--to 999 names no process: no process of the session held PID 999."),
            (["--process", "400", "--to", "400"], "--to names the process --process names: a chain leads from one process to another."),
            (["--process", "400", "--evidence-policy", "2"], "--evidence-policy expects direct-only, include-correlated, "
                + "include-candidates or all-including-conflicting; '2' is not one."),
            (["--process", "400", "--interval", "1250:50"], "--interval expects start:end, each bound a count of "
                + "100-nanosecond session ticks or a time with its unit, such as 1.5s, 250ms or 40us, with its end after its start; "
                + "'1250:50' is not one."),
        })
        {
            (InterCatExitCode code, _, string said) = await Run(["links", session.Path, .. args]);
            Assert.Equal(InterCatExitCode.InvalidInvocation, code);
            Assert.Contains(refusal, Regex.Replace(said, @"\s+", " "), Ordinal);
        }

        (InterCatExitCode missing, _, string why) = await Run("links");
        Assert.Equal(InterCatExitCode.InvalidInvocation, missing);
        Assert.Contains("A session directory is required: icat links <directory> --process <pid | instance>.", why, Ordinal);
        (InterCatExitCode help, string text, _) = await Run("links", "--help");
        Assert.Equal(InterCatExitCode.Success, help);
        Assert.Contains("icat links <session-directory> --process <pid | instance> [--to <pid | instance>]", text, Ordinal);

        // An evidence policy is taken as written or in kebab form, here as icat patterns takes it.
        (InterCatExitCode patterns, _, string said2) = await Run("patterns", session.Path, "--evidence-policy", "direct-only");
        Assert.True(patterns == InterCatExitCode.Success, said2);
        (InterCatExitCode empty, _, string said3) = await Run("patterns", session.Path, "--evidence-policy", "-");
        Assert.Equal(InterCatExitCode.InvalidInvocation, empty);
        Assert.Contains("'-' is not one.", said3, Ordinal);
    }

    private static TemporarySession Session()
    {
        var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = LinkRows();
        Publish(session.Store, rows, fields: fields, coverage: TransportLedger(tcp: true, udp: true));
        session.Store.ReleaseSegmentReaders();
        return session;
    }

    /// <summary>The rows of the table beneath the heading that starts with <paramref name="heading"/>, each cell joined by " | ".</summary>
    private static string[] Rows(string text, string heading)
    {
        string[] lines = [.. text.Split('\n').Select(line => line.TrimEnd('\r'))];
        int start = Array.FindIndex(lines, line => line.StartsWith(heading, Ordinal));
        Assert.True(start >= 0, $"No heading {heading}.");
        int header = Array.FindIndex(lines, start, line => Regex.IsMatch(line, @"^  (Link|Step) +"));
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
