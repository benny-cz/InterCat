using InterCat.Analysis.Tests;
using InterCat.Capture.Journal.Tests;
using InterCat.Domain;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Cli.Tests;

/// <summary>
/// The refusals of icat pin, checkpoint, channels, exchanges and profiles that no test had run, each as a person meets it
/// (R18): an option's value refused by naming what it expects and the value given, an option given twice said to be, and
/// a checkpoint's report the one place its outcome is said.
/// </summary>
public sealed class RefusalWordsTests : IDisposable
{
    private readonly TemporaryDirectory root = new();
    private readonly TemporarySession session = new();

    public RefusalWordsTests()
    {
        Publish(session.Store,
            [Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 1).Between("127.0.0.1:50000", "127.0.0.1:8080")]);
        session.Store.ReleaseSegmentReaders();
    }

    public void Dispose()
    {
        session.Dispose();
        root.Dispose();
    }

    public static TheoryData<string[], string> Refusals => new()
    {
        { ["pin", "{session}", "--from", "0 s", "--reason", "keep", "--allow-mib", "lots"],
            "--allow-mib expects whole mebibytes from 1 to 1,073,741,824, the most the session may hold while the pin stands; 'lots' is not one." },
        { ["pin", "{session}", "--remove", "abc", "--from", "1 s"], "--remove names the pin it removes alone: it takes no --from, --allow-mib or --reason." },
        { ["pin", "{session}", "--allow-mib", "5"], "--allow-mib and --reason go with the moment a pin keeps records from" },
        { ["pin", "{session}", "--from", "0 s"], "A pin says why it keeps the records, so whoever finds it later knows: --reason <text>." },
        { ["pin", "{session}", "--remove", "zzz"], "No pin of this session is named zzz. icat pin \"{session}\" lists them." },
        { ["pin"], "A session directory is required: icat pin <directory>." },
        { ["channels", "{session}", "--process", "x"], "--process names a process by its PID or its instance id, as icat processes prints them; 'x' is neither." },
        { ["channels", "{session}", "--page-size", "0"], "--page-size expects a number of channels from 1 to 200; '0' is not one." },
        { ["channels", "{session}", "--cursor", "bogus"], "The channel cursor is malformed; restart from the first page." },
        { ["exchanges", "{session}", "--pid", "x"], "--pid expects a process id; 'x' is not one." },
        { ["exchanges", "{session}", "--pid", "1", "--format", "csv"], "--format sets what --output writes: give --output with it." },
        { ["exchanges", "{session}", "--pid", "1", "--output", "out.txt", "--format", "xml"], "--format expects json or csv; 'xml' is not one." },
        { ["exchanges", "{session}", "--output", "out.csv"], "A table holds one line per listed exchange: give --pid, or --exchanges with a number." },
        { ["exchanges", "{session}", "--decode"], "--decode reads the heads of the exchanges listed: give --pid, or --exchanges with a number." },
        { ["exchanges", "{session}", "--exchanges", "lots"], "--exchanges expects a number of exchanges per process up to 10,000; 'lots' is not one." },
        { ["checkpoint"], "A published session directory is required: icat checkpoint <directory>." },
        { ["exchanges"], "A session directory is required: icat exchanges <directory>." },
        { ["exchanges", "{nowhere}"], "No session directory at {nowhere}." },
        { ["operations"], "A session directory is required: icat operations <directory>." },
        { ["processes"], "A session directory is required: icat processes <directory>." },
        { ["session"], "A session directory is required: icat session <directory>." },
        { ["record"], "A new session directory is required: icat record <directory>." },
        { ["bench", "--bogus"], "Unknown or incomplete option: --bogus" },
        { ["measure", "tcp", "--seed", "abc"], "--seed expects a whole number; 'abc' is not one." },
        { ["measure", "udp", "--sockets", "two"], "--sockets expects a whole number; 'two' is not one." },
        { ["measure", "pipe", "--messages", "many"], "--messages expects a whole number; 'many' is not one." },
        { ["measure", "rpc", "--calls", "1.5"], "--calls expects a whole number; '1.5' is not one." },
        { ["bench", "--no-storage-probe", "--series", "{nowhere}"], "No series result at {nowhere}." },
        { ["content", "{session}", "--find", "x", "--json"], "--find lists the records holding the text for a person, never into JSON: leave out --json." },
        { ["content", "{session}", "--find", "x", "--reveal"],
            "--find searches the session's kept content, not one record: leave out the record's locator and --reveal, --save, --decode, --part, --from and --to." },
        { ["content", "{session}", "--find", "x", "--pid", "y"], "--pid expects a process id; 'y' is not one." },
        { ["content", "{session}", "--find", "x", "--limit", "0"], "--limit expects a number of records from 1 to 10,000; '0' is not one." },
        { ["content", "{session}", "--find", ""], "The argument after --find is empty, as an unset variable leaves it: give the value it stands for." },
        { ["content", "{session}", "--previews"],
            "--pid, --process, --evidence-policy, --limit, --max-mib, --max-seconds, --ignore-case, --previews and --output apply only to --find." },
        { ["content", "{session}", "--max-mib", "5"],
            "--pid, --process, --evidence-policy, --limit, --max-mib, --max-seconds, --ignore-case, --previews and --output apply only to --find." },
        { ["content", "{session}", "--find", "x", "--max-mib", "0"], "--max-mib expects a number of mebibytes from 1 to 65,536; '0' is not one." },
        { ["content", "{session}", "--find", "x", "--max-seconds", "3601"],
            "--max-seconds expects a number of seconds from 1 to 3,600; '3601' is not one." },
        { ["content", "{session}", "--output", "found.csv"],
            "--pid, --process, --evidence-policy, --limit, --max-mib, --max-seconds, --ignore-case, --previews and --output apply only to --find." },
        { ["content", "{session}", "--find", "x", "--overwrite"], "--overwrite replaces the file --output names: give --output with it." },
        { ["content", "{session}", "--process", "100"],
            "--pid, --process, --evidence-policy, --limit, --max-mib, --max-seconds, --ignore-case, --previews and --output apply only to --find." },
        { ["content", "{session}", "--evidence-policy", "direct-only"],
            "--pid, --process, --evidence-policy, --limit, --max-mib, --max-seconds, --ignore-case, --previews and --output apply only to --find." },
        { ["content", "{session}", "--find", "x", "--pid", "100", "--process", "100"],
            "--pid keeps to the records that carry a PID, whichever process held it, and --process to the records of one process: give one of them." },
        { ["content", "{session}", "--find", "x", "--evidence-policy", "direct-only"],
            "--evidence-policy chooses which records bind to the process --process names: give --process with it." },
        { ["content", "{session}", "--find", "x", "--process", "100", "--evidence-policy", "2"],
            "--evidence-policy expects one of: DirectOnly, IncludeCorrelated, IncludeCandidates, AllIncludingConflicting. '2' is not one." },
        { ["content", "{session}", "--find", "x", "--process", "999"], "--process 999 names no process: no process of the session held PID 999." },
        { ["content", "--find", "x"], "A session directory is required: icat content <directory> --find <text>." },
        { ["content", "{session}", "--session-id", "00000000-0000-0000-0000-000000000001", "--generation", "1", "--segment", "s",
            "--row", "0", "--reveal", "--json"],
            "--json states a record's content facts only; its bytes are shown with --reveal, saved with --save or decoded with --decode, never written into JSON." },
        { ["profiles", "nonsense"], "'nonsense' is no capture profile; icat profiles lists every one." },
        { ["profiles", "focused-transport", "--pid", "x"], "--pid expects a process id; 'x' is not one." },
        { ["profiles", "focused-transport", "--pid", "5", "--pid", "5"], "--pid 5 is given more than once; give it once." },
        { ["profiles", "focused-transport", "--mechanism", "smtp"],
            "--mechanism expects tcp, udp, rpc or http; 'smtp' is not one. Which of them a profile can capture is checked per profile." },
        { ["profiles", "content", "--retention", "forever"], "--retention expects stop-at-limit; 'forever' is not one." },
        { ["profiles", "content", "--inspection", "full"], "--inspection expects disabled or hex-text; 'full' is not one." },
        { ["profiles", "content", "--channel", "a", "--channel", "a"], "--channel a is given more than once; give it once." },
        { ["profiles", "explore", "--mechanism", "tcp"], "Mechanism, process and content-scope options apply only to focused-transport or content." },
        { ["profiles", "--retention", "stop-at-limit"], "Profile options require one profile name. List profiles without additional options." },
        { ["profiles", "content", "--channel", "a\tb"], "Each --channel selector must contain 1 to 256 printable characters." },
        { ["profiles", "focused-transport", .. Enumerable.Range(1, 65).SelectMany(pid => new[] { "--pid", pid.ToString(System.Globalization.CultureInfo.InvariantCulture) })],
            "A profile preview accepts at most 64 --pid values." },
        { ["profiles", "content", .. Enumerable.Range(1, 65).SelectMany(index => new[] { "--channel", "c" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) })],
            "A content preview accepts at most 64 --channel values." },
        { ["profiles", "content", "--max-record-bytes", "x"], "--max-record-bytes requires a positive decimal integer." },
        { ["profiles", "content", "--max-session-bytes", "0"], "--max-session-bytes requires a positive decimal integer." },
        { ["profiles", "focused-transport", "--retention", "stop-at-limit"], "Content source, channel, budget, retention and inspection options apply only to content." },
        { ["profiles", "focused-transport"], "focused-transport requires --mechanism tcp." },
        { ["profiles", "focused-transport", "--mechanism", "tcp", "--allow-broader-capture"],
            "--allow-broader-capture is meaningful only with at least one --pid selection." },
        { ["profiles", "content", "--allow-broader-capture"], "Content scope never accepts --allow-broader-capture; an unenforceable content scope is refused." },
        { ["profiles", "content", "--mechanism", "udp", "--inspection", "disabled"],
            "content requires --source, --mechanism, at least one --pid and --channel, both byte limits, --retention, and --inspection." },
        { ["profiles", "explore", "--mechanism", "rpc"], "Mechanism, process and content-scope options apply only to focused-transport or content." },
        { ["profiles", "content", "--source", "etw/manifest/Nope", "--mechanism", "http", "--pid", "4242", "--channel", "example.com",
            "--max-record-bytes", "4096", "--max-session-bytes", "65536", "--retention", "stop-at-limit", "--inspection", "hex-text"],
            "The requested content source is not in the adapter catalog." },
    };

    [Theory(DisplayName = "R18: icat pin, channels, exchanges, checkpoint and profiles refuse what they cannot read, naming what they expect")]
    [MemberData(nameof(Refusals))]
    public async Task WhatCannotBeReadIsRefused(string[] args, string expected)
    {
        string nowhere = Path.Combine(root.Path, "nowhere");
        string Placed(string text) =>
            text.Replace("{session}", session.Path, StringComparison.Ordinal).Replace("{nowhere}", nowhere, StringComparison.Ordinal);
        (InterCatExitCode code, string output, string said) = await Run([.. args.Select(Placed)]);

        Assert.Equal((InterCatExitCode.InvalidInvocation, string.Empty), (code, output));
        Assert.Contains(Placed(expected), said, StringComparison.Ordinal);
    }

    /// <summary>Every command that takes --evidence-policy, as it runs over the session with nothing else wrong.</summary>
    public static TheoryData<string[]> PolicyRuns => new()
    {
        { ["evidence", "{session}"] },
        { ["exchanges", "{session}"] },
        { ["links", "{session}", "--process", "100"] },
        { ["metric", "{session}", "--metric", "observations"] },
        { ["operations", "{session}"] },
        { ["outliers", "{session}", "--interval", "195:200"] },
        { ["patterns", "{session}"] },
        { ["processes", "{session}"] },
        { ["waits", "{session}"] },
        { ["content", "{session}", "--find", "x", "--process", "100"] },
    };

    [Theory(DisplayName = "R18: every command that takes --evidence-policy reads a policy by its name, as written or in kebab form, never by its number")]
    [MemberData(nameof(PolicyRuns))]
    public async Task EveryCommandReadsAPolicyByName(string[] args)
    {
        string[] With(string policy) =>
            [.. args.Select(arg => arg.Replace("{session}", session.Path, StringComparison.Ordinal)), "--evidence-policy", policy];

        // A number is what the durable format stores, not what a person types.
        (InterCatExitCode code, string output, string said) = await Run(With("2"));
        Assert.Equal((InterCatExitCode.InvalidInvocation, string.Empty), (code, output));
        Assert.Contains("--evidence-policy expects one of: DirectOnly, IncludeCorrelated, IncludeCandidates, AllIncludingConflicting. "
            + "'2' is not one.", said, StringComparison.Ordinal);
        foreach (string named in new[] { "direct-only", "DirectOnly" })
        {
            (code, _, said) = await Run(With(named));
            Assert.True(code == InterCatExitCode.Success, said);
        }
    }

    [Fact(DisplayName = "R18: a report file that exists is never replaced without --overwrite, as icat profiles says it as every command does")]
    public async Task AnExistingReportIsNotReplaced()
    {
        string report = Path.Combine(root.Path, "report.json");
        File.WriteAllText(report, "{}");
        foreach (string[] args in new[]
        {
            ["profiles", "explore", "--output", report], ["exchanges", session.Path, "--output", report],
            ["checkpoint", session.Path, "--output", report], ["compact", session.Path, "--output", report],
            ["capabilities", "--output", report], ["export", session.Path, "--output", report],
            ["operations", session.Path, "--output", report], ["metric", session.Path, "--metric", "observations", "--output", report],
            ["processes", session.Path, "--output", report], ["session", session.Path, "--output", report],
            ["rederive", session.Path, "--check", "--output", report],
            new[] { "retain", session.Path, "--release-before", "1 s", "--output", report },
            ["bench", "--no-storage-probe", "--output", report],
        })
        {
            // Refused before anything is read, so nothing is said of what would have been written.
            (InterCatExitCode code, string output, string said) = await Run(args);
            Assert.Equal((InterCatExitCode.InvalidInvocation, string.Empty), (code, output));
            Assert.Contains($"{report} exists. Pass --overwrite to replace", said, StringComparison.Ordinal);
        }

        Assert.Equal("{}", File.ReadAllText(report));
    }

    [Fact(DisplayName = "R18: icat bench writes its baseline where asked, and refuses a series it cannot read in words, never with a stack trace")]
    public async Task TheBaselineIsWrittenAndASeriesReadOrRefused()
    {
        string baseline = Path.Combine(root.Path, "out", "baseline.json");
        (InterCatExitCode code, string output, string said) = await Run("bench", "--no-storage-probe", "--output", baseline);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains($"Baseline written to {baseline}.", said, StringComparison.Ordinal);
        using (System.Text.Json.JsonDocument written = System.Text.Json.JsonDocument.Parse(File.ReadAllText(baseline)))
        {
            Assert.Equal("intercat.baseline.v1", written.RootElement.GetProperty("schema").GetString());
        }

        Assert.Contains("SECTION 12 BUDGETS", output, StringComparison.Ordinal);

        // A file that is not a series result is said to be, and is no budget of the baseline.
        string notJson = Path.Combine(root.Path, "series.txt");
        File.WriteAllText(notJson, "not json");
        string noBudgets = Path.Combine(root.Path, "series.json");
        File.WriteAllText(noBudgets, "[1, 2]");
        foreach (string series in new[] { notJson, noBudgets })
        {
            (code, output, said) = await Run("bench", "--no-storage-probe", "--series", series);
            Assert.Equal((InterCatExitCode.CorruptedInput, string.Empty), (code, output));
            Assert.StartsWith($"x '{series}' ", said, StringComparison.Ordinal);
            Assert.DoesNotContain("   at ", said, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "R18: icat profiles writes its catalog to --output as --json prints it, into a folder it makes, and says it wrote the catalog there")]
    public async Task TheProfileCatalogIsWrittenWhereAsked()
    {
        string written = Path.Combine(root.Path, "profiles", "catalog.json");
        (InterCatExitCode code, _, string said) = await Run("profiles", "--output", written);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains($"Profile catalog written to {written}.", said, StringComparison.Ordinal);
        Assert.Equal((await Run("profiles", "--json")).Output.TrimEnd(), File.ReadAllText(written).TrimEnd());
    }

    public static TheoryData<string[]> JsonRuns => new()
    {
        { ["channels", "{session}", "--json"] }, { ["compact", "{session}", "--check", "--json"] }, { ["evidence", "{session}", "--json"] },
        { ["exchanges", "{session}", "--json"] }, { ["hosts", "{session}", "--json"] },
        { ["metric", "{session}", "--metric", "observations", "--json"] }, { ["operations", "{session}", "--json"] },
        { ["overview", "{session}", "--json"] }, { ["patterns", "{session}", "--json"] }, { ["pin", "{session}", "--json"] },
        { ["processes", "{session}", "--json"] }, { ["programs", "{session}", "--json"] }, { ["session", "{session}", "--json"] },
        { ["timeline", "{session}", "--interval", "0:1000", "--json"] }, { ["waits", "{session}", "--json"] },
        { ["checkpoint", "{session}", "--json"] }, { ["capabilities", "--json"] }, { ["profiles", "--json"] },
        { ["profiles", "explore", "--json"] }, { ["bench", "--no-storage-probe", "--json"] },
    };

    [Theory(DisplayName = "R18: a command asked for --json prints one JSON document and nothing else, what it says of its work going to the error stream")]
    [MemberData(nameof(JsonRuns))]
    public async Task JsonIsAllAJsonRunPrints(string[] args)
    {
        (InterCatExitCode code, string output, string said) = await Run([.. args.Select(arg => arg.Replace("{session}", session.Path, StringComparison.Ordinal))]);

        Assert.True(code is not InterCatExitCode.InvalidInvocation, said);
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(output);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, document.RootElement.ValueKind);
    }

    [Fact(DisplayName = "R18: icat compact and export refuse a folder that holds no session, and icat timeline a process or an end it cannot read, in words")]
    public async Task AMissingFolderOrAnUnreadOptionIsRefused()
    {
        string missing = Path.Combine(root.Path, "no-session");
        foreach ((string[] args, string expected) in new (string[], string)[]
        {
            (["compact"], "A published session directory is required: icat compact <directory>."),
            (["compact", missing], $"No session directory at {missing}."),
            (["export", missing, "--output", Path.Combine(root.Path, "export.json")], $"No session directory at {missing}."),
            (["timeline", session.Path, "--interval", "0:1000", "--process", "100"],
                "--process requires a process instance id, as icat processes prints it; '100' is not one. A PID is not an instance identity."),
            (["timeline", session.Path, "--interval", "0:1000", "--end", "2"], "--end is 0 or 1, the channel's first or second end; '2' is neither."),
        })
        {
            (InterCatExitCode code, string output, string said) = await Run(args);
            Assert.Equal((InterCatExitCode.InvalidInvocation, string.Empty), (code, output));
            Assert.Contains(expected, said, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "R18: icat capabilities, compact and operations write their reports to --output as --json prints them, into a folder they make, and say so")]
    public async Task ReportsAreWrittenWhereAsked()
    {
        foreach ((string[] args, string written) in new (string[], string)[]
        {
            (["capabilities"], "Capability report written to"),
            (["compact", session.Path, "--check"], "Compaction report written to"),
            (["operations", session.Path], "RPC calls written to"),
        })
        {
            string report = Path.Combine(root.Path, "written", args[0] + ".json");
            (InterCatExitCode code, _, string said) = await Run([.. args, "--output", report]);
            Assert.True(code is InterCatExitCode.Success or InterCatExitCode.PartialResultSuccess, said);
            Assert.Contains($"{written} {report}.", said, StringComparison.Ordinal);

            // The same document --json prints, but for the moment a capability probe was made.
            static System.Text.Json.Nodes.JsonNode? Unprobed(string json)
            {
                System.Text.Json.Nodes.JsonNode? document = System.Text.Json.Nodes.JsonNode.Parse(json);
                _ = (document as System.Text.Json.Nodes.JsonObject)?.Remove("probedAtUtc");
                return document;
            }

            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(Unprobed((await Run([.. args, "--json"])).Output), Unprobed(File.ReadAllText(report))),
                args[0]);
        }
    }

    [Fact(DisplayName = "R18: icat checkpoint says what came of it in its report alone, and that it publishes one only when the generation names none")]
    public async Task ACheckpointsReportSaysWhatCameOfIt()
    {
        (InterCatExitCode published, string first, string publishing) = await Run("checkpoint", session.Path);
        Assert.Equal(InterCatExitCode.Success, published);
        Assert.Contains("CHECKPOINT PUBLISHED", first, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^  Generation +1 → 2\r?$", first);
        Assert.Contains("Publishing the session's derivation checkpoint unless the generation names one, so it reopens without "
            + "reading every record.", publishing, StringComparison.Ordinal);
        Assert.DoesNotContain("Derivation checkpoint of generation", publishing, StringComparison.Ordinal);

        // Asked again, nothing is published, and why is said once: in the report.
        string report = Path.Combine(root.Path, "checkpoint.json");
        (InterCatExitCode again, string second, string said) = await Run("checkpoint", session.Path, "--output", report);
        Assert.Equal(InterCatExitCode.Success, again);
        Assert.Contains("NO CHECKPOINT PUBLISHED", second, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^  Generation +2\r?$", second);
        Assert.Contains("The generation already names a checkpoint", second, StringComparison.Ordinal);
        Assert.DoesNotContain("The generation already names a checkpoint", said, StringComparison.Ordinal);
        Assert.Contains($"Checkpoint report written to {report}.", said, StringComparison.Ordinal);
        Assert.Contains("\"outcome\": \"AlreadyCurrent\"", File.ReadAllText(report), StringComparison.Ordinal);

        // A recording's follow still publishes its own and says so, as every other command that writes a session does.
        string evidence = Path.Combine(root.Path, "evidence");
        Directory.CreateDirectory(evidence);
        _ = await EvidenceRecordings.RecordEvidence(evidence, ordinals: [1, 2], bursts: [1]);
        (_, _, string following) = await Run("follow", evidence, Path.Combine(root.Path, "followed"));
        Assert.Contains("Publishing the session's derivation checkpoint, so it reopens without reading every record.", following,
            StringComparison.Ordinal);
        Assert.Matches(@"Derivation checkpoint of generation \d+ published as generation \d+", following);
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
