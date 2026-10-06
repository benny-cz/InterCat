using System.Text.Json;
using System.Text.RegularExpressions;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

// Every test here reads the process's own console, so none runs beside another.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace InterCat.Cli.Tests;

/// <summary>
/// icat as a person or a script runs it (§20.4): what it answers on stdout, what it says on stderr, and the exit code. A
/// script reads stdout as the answer, so whatever explains a refusal goes to stderr with it.
/// </summary>
public sealed class CommandLineTests : IDisposable
{
    private const string TooWide = "-9223372036854775808:0";

    /// <summary>Every command icat dispatches.</summary>
    private static readonly string[] Commands =
    [
        "capabilities", "profiles", "measure", "import", "record", "capture", "rederive", "session", "overview", "channels",
        "evidence", "timeline", "export", "package", "raw", "content", "recover", "staging", "retain", "compact",
        "checkpoint", "follow", "metric", "processes", "operations", "exchanges", "workspace", "verify", "bench",
    ];

    /// <summary>The fields of icat staging's preview, whose labels run past the column a short label takes.</summary>
    private static readonly string[] StagingFields =
        ["Session", "Abandoned staged files", "Marker-only files", "Active writer files", "Unmarked legacy files", "Files removed"];

    /// <summary>The fields an export's report ends with.</summary>
    private static readonly string[] ExportFields = ["Written to", "Contract", "Rung", "Breadcrumb", "Scope", "Ranked by", "Rows", "Complete"];

    /// <summary>The names a machine-readable answer gives its contract or version under.</summary>
    private static readonly string[] VersionNames = ["contract", "schemaVersion", "reportVersion"];

    private readonly TemporarySession session = new();

    public CommandLineTests()
    {
        // One 64-byte send and its receipt, 10 µs and 11 µs into the capture.
        Publish(session.Store,
        [
            Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 1).Between("127.0.0.1:50000", "127.0.0.1:8080")
                with { SessionRelativeTicks = 10_000 },
            Transfer(110, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 2).Between("127.0.0.1:8080", "127.0.0.1:50000")
                with { SessionRelativeTicks = 11_000 },
        ]);
        session.Store.ReleaseSegmentReaders();
    }

    public void Dispose() => session.Dispose();

    [Fact(DisplayName = "R18: icat's overview names every command and every form of it its own help gives, and capture says what it needs")]
    public async Task TheOverviewNamesEveryForm()
    {
        // The overview's synopses, by command: each command's lines that begin "icat <command>", and the lines that continue
        // them, indented past the description's six spaces.
        string overview = (await Run("--help")).Output;
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        foreach (string line in overview.Split('\n'))
        {
            Match synopsis = Regex.Match(line, @"^  icat ([a-z][a-z-]*)");
            if (synopsis.Success)
            {
                current = synopsis.Groups[1].Value;
            }
            else if (!Regex.IsMatch(line, @"^ {7,}\S"))
            {
                continue;
            }

            if (current is not null)
            {
                entries[current] = entries.GetValueOrDefault(current, string.Empty) + line + "\n";
            }
        }

        foreach (string command in Commands)
        {
            Assert.True(entries.ContainsKey(command), $"The overview has no entry for icat {command}.");

            // A form is named by the first word its synopsis writes after the command that is neither a placeholder nor
            // optional: workspace's subcommands, package's --original and --redacted, retain's two releases.
            string own = (await Run(command, "--help")).Output;
            foreach (Match synopsis in Regex.Matches(own, @"(?m)^\s*icat " + Regex.Escape(command) + @"\b(.*)$"))
            {
                string rest = synopsis.Groups[1].Value;
                for (string previous = string.Empty; previous != rest;)
                {
                    previous = rest;
                    rest = Regex.Replace(rest, @"<[^<>]*>|\([^()]*\)|\[[^\[\]]*\]", " ");
                }

                if (rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(word => word != "...") is { } form)
                {
                    Assert.True(Regex.IsMatch(entries[command], @"(?<![\w-])" + Regex.Escape(form) + @"(?![\w-])"),
                        $"icat {command} {form} is in its own help but not in the overview's synopsis of it.");
                }
            }
        }

        Assert.Contains("icat <command> --help lists every option of a command", overview, StringComparison.Ordinal);

        // A capture needs the broker, which runs on Windows alone: elsewhere icat says so, and what it can do instead.
        if (!OperatingSystem.IsWindows())
        {
            (InterCatExitCode code, string output, string said) = await Run("capture", "session");
            Assert.Equal((InterCatExitCode.PermissionOrCapabilityFailure, string.Empty), (code, output));
            Assert.StartsWith("x icat capture needs Windows", said, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "§20.4: a folder no session was published in is refused in a person's words, and every reader leaves it as it was")]
    public async Task AFolderWithNoSessionIsLeftAsItWas()
    {
        string folder = Path.Combine(Path.GetDirectoryName(session.Path)!, Guid.NewGuid().ToString("N"));
        string package = folder + "-package";
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "a folder chosen by mistake");
        try
        {
            string[] record = ["--session-id", Guid.NewGuid().ToString(), "--generation", "1", "--segment", "s", "--row", "0"];
            string[][] asked =
            [
                ["overview", folder], ["channels", folder], ["channels", folder, "--process", record[1], "--one-sided"],
                ["evidence", folder], ["timeline", folder, "--interval", "0:10"],
                ["export", folder, "--output", Path.Combine(package, "rows.csv")], ["processes", folder], ["operations", folder],
                ["exchanges", folder], ["metric", folder, "--metric", "observations"], ["raw", folder, .. record],
                ["content", folder, .. record], ["package", folder, "--redacted", "--output", package],
                ["retain", folder, "--release-content"], ["retain", folder, "--release-journal-before-record", "1"],
                ["rederive", folder], ["compact", folder], ["checkpoint", folder],
                ["recover", folder],
            ];
            foreach (string[] args in asked)
            {
                // Naming a folder that holds no session is a mistake in what was asked, as naming none is, whatever the
                // command would have done with a session.
                (InterCatExitCode code, string output, string said) = await Run(args);
                string invocation = "icat " + string.Join(' ', args);
                Assert.True(code == InterCatExitCode.InvalidInvocation && output.Length == 0, $"{invocation} exited {code}: {output}");
                Assert.True(said.Contains(SessionStore.NoGeneration, StringComparison.Ordinal), $"{invocation} said: {said}");
                Assert.Equal(["notes.txt"], Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName));
                Assert.False(Directory.Exists(package), $"{invocation} made {package}.");
            }

            // icat session reports what opening a folder found, and says what this one is in the same words.
            (InterCatExitCode reported, string report, _) = await Run("session", folder);
            Assert.Equal(InterCatExitCode.PartialResultSuccess, reported);
            Assert.Contains(SessionStore.NoGeneration, report, StringComparison.Ordinal);
            Assert.Matches(@"(?m)^  Acquired +nothing: no generation has been published here\r?$", report);
            Assert.Equal(["notes.txt"], Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName));

            // A folder handed to import, which reads a trace file, is called a folder, not something that is not there; so
            // is one handed to verify, which names a mechanism, since a session is verified whenever it is opened.
            (InterCatExitCode imported, _, string importSaid) = await Run("import", folder);
            Assert.Equal(InterCatExitCode.InvalidInvocation, imported);
            Assert.Contains("is a folder; icat import reads one ETL trace file", importSaid, StringComparison.Ordinal);
            (InterCatExitCode verified, string verifiedOutput, string verifySaid) = await Run("verify", folder);
            Assert.Equal((InterCatExitCode.InvalidInvocation, string.Empty), (verified, verifiedOutput));
            Assert.Contains("is a folder; icat verify names a mechanism", verifySaid, StringComparison.Ordinal);
            Assert.Contains("A session is verified whenever it is opened", verifySaid, StringComparison.Ordinal);
            Assert.DoesNotContain("is a folder", (await Run("verify", "tpc")).Error, StringComparison.Ordinal);
            Assert.Contains("icat measure runs the tcp, udp, pipe or rpc fixture; 'tpc' is none of them.",
                (await Run("measure", "tpc")).Error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact(DisplayName = "§20.4: a damaged session is corrupted input to every reader, an export's as well")]
    public async Task ADamagedSessionIsCorruptedInput()
    {
        string damaged = Path.Combine(Path.GetDirectoryName(session.Path)!, Guid.NewGuid().ToString("N"));
        string exported = damaged + "-export";
        Directory.CreateDirectory(damaged);
        try
        {
            foreach (string file in Directory.EnumerateFiles(session.Path).Where(file => !file.EndsWith(".lock", StringComparison.Ordinal)))
            {
                File.Copy(file, Path.Combine(damaged, Path.GetFileName(file)));
            }

            string segment = Directory.EnumerateFiles(damaged, "seg-*").Single();
            byte[] bytes = File.ReadAllBytes(segment);
            bytes[^1] ^= 0xFF;
            File.WriteAllBytes(segment, bytes);
            foreach (string[] args in new[] { ["overview", damaged], new[] { "export", damaged, "--output", Path.Combine(exported, "rows.csv") } })
            {
                (InterCatExitCode code, string output, string said) = await Run(args);
                string invocation = "icat " + string.Join(' ', args);
                Assert.True(code == InterCatExitCode.CorruptedInput && output.Length == 0, $"{invocation} exited {code}: {output}");
                Assert.True(said.Contains("no generation of it verifies", StringComparison.Ordinal), $"{invocation} said: {said}");
                Assert.False(Directory.Exists(exported), $"{invocation} made {exported}.");
            }
        }
        finally
        {
            Directory.Delete(damaged, recursive: true);
        }
    }

    [Fact(DisplayName = "§20.4: each group of a report's fields lines up, and the last group is written when its command ends")]
    public async Task AReportsFieldsLineUp()
    {
        // The staging preview's labels run past the column a short one takes; every value still starts with the rest.
        (InterCatExitCode previewed, string preview, _) = await Run("staging", session.Path);
        Assert.Equal(InterCatExitCode.Success, previewed);
        Assert.Single(StagingFields.Select(label => ValueColumn(preview, label)).Distinct());

        // An export's report ends with its fields, which nothing written after them would bring out.
        string destination = Path.Combine(Path.GetDirectoryName(session.Path)!, Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            (InterCatExitCode exported, string report, _) = await Run("export", session.Path, "--output", destination);
            Assert.Equal(InterCatExitCode.Success, exported);
            Assert.Single(ExportFields.Select(label => ValueColumn(report, label)).Distinct());
            Assert.EndsWith("yes", report.TrimEnd(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [Fact(DisplayName = "§20.4: a command answering from the last complete generation says so, with what kept the newest from verifying")]
    public async Task AnAnswerFromTheLastCompleteGenerationSaysWhy()
    {
        using var twice = new TemporarySession();
        Publish(twice.Store, [Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 1).Between("127.0.0.1:50000", "127.0.0.1:8080")]);
        string[] first = [.. twice.Store.Current!.Dependencies.Select(dependency => dependency.Name)];
        Publish(twice.Store, [Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 32, 100, 1).Between("127.0.0.1:50000", "127.0.0.1:8080")]);
        twice.Store.ReleaseSegmentReaders();
        StoreDependency newest = twice.Store.Current!.Dependencies.First(dependency =>
            dependency.Kind == StoreDependencyKind.Segment && !first.Contains(dependency.Name));
        string path = Path.Combine(twice.Path, newest.Name);
        File.WriteAllBytes(path, File.ReadAllBytes(path)[..^1]);
        string why = newest.LengthMismatch(newest.LengthBytes - 1, 2);

        (_, string measured, string measuring) = await Run("metric", twice.Path, "--metric", "observations");
        Assert.NotEmpty(measured);
        Assert.Contains($"The newest generation did not verify, so the answer is from the retained last-known-good generation 1: {why}.",
            measuring, StringComparison.Ordinal);
        (_, _, string rederiving) = await Run("rederive", twice.Path, "--check");
        Assert.Contains($"The newest generation did not verify, so the retained last-known-good generation 1 is re-derived: {why}.",
            rederiving, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R18: a refused invocation says why on stderr, and leaves stdout to the answer it did not give")]
    public async Task ARefusedInvocationLeavesStdoutToTheAnswer()
    {
        // Every command reads its arguments before it refuses one it does not know, so each is read here once; capture
        // runs only on Windows, and elsewhere says so before it reads any argument.
        foreach (string command in Commands.Where(command => command != "capture" || OperatingSystem.IsWindows()))
        {
            (InterCatExitCode code, string output, string error) = await Run(command, "--bogus");
            Assert.True(code == InterCatExitCode.InvalidInvocation, $"icat {command} --bogus exited {code}.");
            Assert.True(output.Length == 0, $"icat {command} --bogus wrote to stdout:\n{output}");
            Assert.StartsWith("x ", error, StringComparison.Ordinal);
        }

        string[][] refused =
        [
            ["frobnicate"],
            ["timeline", "--bogus"],
            ["timeline", session.Path],
            ["evidence", session.Path, "--page-size", "0", "--json"],
            ["export", session.Path],
            ["processes"],
            ["metric", session.Path],
            ["metric", session.Path, "--metric", "bytes-sent", "--json"],
            ["metric", session.Path, "--metric", "rate"],
            ["workspace", "frobnicate"],
        ];
        foreach (string[] args in refused)
        {
            (InterCatExitCode code, string output, string error) = await Run(args);
            string invocation = "icat " + string.Join(' ', args);
            Assert.True(code == InterCatExitCode.InvalidInvocation, $"{invocation} exited {code}.");
            Assert.True(output.Length == 0, $"{invocation} wrote to stdout:\n{output}");

            // The refusal, then what explains it: the help it was refused against, or what would complete it.
            Assert.StartsWith("x ", error, StringComparison.Ordinal);
            Assert.True(error.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length > 1, $"{invocation} explained nothing:\n{error}");
        }

        // A reader's refusal of a value is said in its own words, without .NET's name for the parameter.
        (InterCatExitCode malformed, string none, string reason) = await Run("evidence", session.Path, "--cursor", "abc");
        Assert.Equal(InterCatExitCode.InvalidInvocation, malformed);
        Assert.Empty(none);
        Assert.Equal("x The evidence cursor is malformed; restart from the first page.", reason.TrimEnd());

        // Asked for, help is the answer, on stdout.
        (InterCatExitCode helped, string help, string quiet) = await Run("timeline", "--help");
        Assert.Equal(InterCatExitCode.Success, helped);
        Assert.Contains("icat timeline <session-directory>", help, StringComparison.Ordinal);
        Assert.Empty(quiet);
    }

    [Fact(DisplayName = "I3: every command that reads an interval refuses one longer than a tick count, and one past the clock's range answers")]
    public async Task AnIntervalLongerThanATickCountIsRefused()
    {
        string export = Path.Combine(session.Path, "..", Guid.NewGuid().ToString("N") + ".json");
        string[][] tooWide =
        [
            ["timeline", session.Path, "--interval", TooWide],
            ["timeline", session.Path, "--interval", TooWide, "--bytes"],
            ["evidence", session.Path, "--interval", TooWide],
            ["export", session.Path, "--output", export, "--interval", TooWide],
            ["metric", session.Path, "--metric", "rate", "--rate-numerator", "observations", "--interval", TooWide],
        ];
        foreach (string[] args in tooWide)
        {
            (InterCatExitCode code, string output, string error) = await Run(args);
            string invocation = "icat " + string.Join(' ', args);
            Assert.True(code == InterCatExitCode.InvalidInvocation, $"{invocation} exited {code}: {error}");
            Assert.Contains("no interval is that long", error, StringComparison.Ordinal);
            Assert.Empty(output);
        }

        Assert.False(File.Exists(export));

        // Bounds past the clock's range stand for every reading on their side, so the interval holds both records, held
        // centred on the capture's epoch, and its rate divides by that interval.
        (InterCatExitCode answered, string rate, string said) = await Run(
            "metric", session.Path, "--metric", "rate", "--rate-numerator", "observations",
            "--interval", "-1000000000000000s:1000000000000000s");
        Assert.True(answered == InterCatExitCode.Success, said);
        Assert.Contains("2 records over 9,223,372,036,854,775,807 ticks", rate, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R2: a metric request refused for one part says the option that completes it")]
    public async Task ARefusedMetricSaysItsOption()
    {
        (string[] Args, string Remedy)[] refused =
        [
            (["--metric", "bytes-sent"], "Name it with --byte-domain: TransportObserved or CompletedIo."),
            (["--metric", "bytes-sent", "--byte-domain", "TransportObserved"], "Name it with --side: SendSide, ReceiveSide or CanonicalOwner."),
            (["--metric", "bytes-sent", "--byte-domain", "RequestedIo", "--side", "send"], "--byte-domain takes TransportObserved or CompletedIo here."),
            (["--metric", "observations", "--side", "send"], "Leave out --side."),
            (["--metric", "rate"], "Name it with --rate-numerator, one of the numerators below."),
            (["--metric", "duration", "--basis", "LogicalOperations"],
                "Name it with --duration: ClientCall, ServerExecution, IoCompletion, AlpcSendToReceive or Wait."),
        ];
        foreach ((string[] args, string remedy) in refused)
        {
            (InterCatExitCode code, string output, string error) = await Run(["metric", session.Path, .. args]);
            Assert.Equal(InterCatExitCode.InvalidInvocation, code);
            Assert.Contains(remedy, error, StringComparison.Ordinal);
            Assert.Empty(output);
        }

        // Completed as it says, the request answers.
        (InterCatExitCode answered, string sent, string said) = await Run(
            "metric", session.Path, "--metric", "bytes-sent", "--byte-domain", "TransportObserved", "--side", "SendSide");
        Assert.True(answered == InterCatExitCode.Success, said);
        Assert.Contains("64 B", sent, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R18: an option written before the session directory keeps its value, and a view may start before the epoch")]
    public async Task AnOptionBeforeTheSessionKeepsItsValue()
    {
        // Once, an option written first lost its value to the session's place: `--metric observations <session>` looked
        // for a session named "observations". Before the session or after it, each now answers the same.
        string[][] written =
        [
            ["session", "--rows", "1"],
            ["channels", "--page-size", "1"],
            ["evidence", "--page-size", "1"],
            ["timeline", "--interval", "0:100000", "--columns", "2"],
            ["processes", "--top", "1"],
            ["metric", "--metric", "observations"],
        ];
        foreach (string[] args in written)
        {
            (InterCatExitCode after, string answer, string said) = await Run([args[0], session.Path, .. args[1..], "--json"]);
            (InterCatExitCode before, string early, string saidEarly) = await Run([args[0], .. args[1..], session.Path, "--json"]);
            string invocation = "icat " + string.Join(' ', args);
            Assert.True(after == InterCatExitCode.Success, $"{invocation} <session>: {said}");
            Assert.True(before == InterCatExitCode.Success, $"{invocation} with the session last: {saidEarly}");
            Assert.Equal(Comparable(answer), Comparable(early));
        }

        // A workspace of two sessions, one aligned to the other, has a time of its own, which starts before either's
        // epoch for whatever was recorded earlier: a view from -5 s is an operand, never an unknown option.
        string workspace = Path.Combine(Path.GetDirectoryName(session.Path)!, Guid.NewGuid().ToString("N") + ".icat-workspace");
        string second = Path.Combine(Path.GetDirectoryName(session.Path)!, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(second);
            SessionStore other = SessionStore.Open(LocalOwnedDirectory.Open(second), Guid.NewGuid(), "cli-tests");
            Publish(other, [Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 8, 300, 1).Between("127.0.0.1:50001", "127.0.0.1:8081")
                with { SessionRelativeTicks = 10_000 }], capture: CaptureId.New(), clock: ClockFor(ClockId.New(), "second-host"));
            other.ReleaseSegmentReaders();
            Assert.Equal(InterCatExitCode.Success, (await Run("workspace", "new", workspace)).Code);
            Assert.Equal(InterCatExitCode.Success, (await Run("workspace", "add", workspace, session.Path, second)).Code);
            (InterCatExitCode aligned, _, string alignSaid) = await Run(
                "workspace", "align", workspace, $"{other.Current!.SessionId:N}@0", $"{TestSessions.Session:N}@0", "--within", "1ms");
            Assert.True(aligned == InterCatExitCode.Success, alignSaid);

            (InterCatExitCode saved, _, string viewSaid) = await Run("workspace", "view", workspace, "early", "-5", "10");
            Assert.True(saved == InterCatExitCode.Success, viewSaid);
            (InterCatExitCode unwritten, _, string refusal) = await Run("workspace", "note", workspace, "-> retry storm here");
            Assert.Equal(InterCatExitCode.InvalidInvocation, unwritten);
            Assert.Contains("an operand that starts with a dash follows --", refusal, StringComparison.Ordinal);
            (InterCatExitCode noted, _, string noteSaid) = await Run("workspace", "note", workspace, "--", "-> retry storm here");
            Assert.True(noted == InterCatExitCode.Success, noteSaid);
            string shown = (await Run("workspace", "show", workspace, "--json")).Output;
            Assert.Contains("\"startTicks\": -50000000", shown, StringComparison.Ordinal);
            Assert.Contains(@"-\u003E retry storm here", shown, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(workspace);
            Directory.Delete(second, recursive: true);
        }
    }

    [Fact(DisplayName = "R18: every command's --json answer is one JSON object on stdout naming its contract, whatever its exit code")]
    public async Task EveryJsonAnswerIsOneDocument()
    {
        string workspace = Path.Combine(Path.GetDirectoryName(session.Path)!, Guid.NewGuid().ToString("N") + ".icat-workspace");
        try
        {
            Assert.Equal(InterCatExitCode.Success, (await Run("workspace", "new", workspace)).Code);
            Assert.Equal(InterCatExitCode.Success, (await Run("workspace", "add", workspace, session.Path)).Code);
            string[][] asked =
            [
                ["capabilities"],
                ["profiles"],
                ["profiles", "explore"],
                ["session", session.Path],
                ["overview", session.Path],
                ["channels", session.Path],
                ["evidence", session.Path],
                ["timeline", session.Path, "--interval", "0:1000", "--bytes"],
                ["processes", session.Path],
                ["operations", session.Path],
                ["exchanges", session.Path],
                ["metric", session.Path, "--metric", "observations"],
                ["metric", session.Path, "--metric", "bytes-sent", "--byte-domain", "TransportObserved", "--side", "send", "--group-by", "process"],
                ["metric", "--matrix"],
                ["compact", session.Path, "--check"],
                ["retain", session.Path, "--release-content"],
                ["recover", session.Path],
                ["staging", session.Path],
                ["workspace", "show", workspace],
                ["workspace", "correlate", workspace],
            ];
            foreach (string[] args in asked)
            {
                (InterCatExitCode code, string output, string said) = await Run([.. args, "--json"]);
                string invocation = "icat " + string.Join(' ', args) + " --json";
                Assert.True(code is not (InterCatExitCode.InvalidInvocation or InterCatExitCode.CorruptedInput), $"{invocation} exited {code}: {said}");
                try
                {
                    // An object that names its contract, so a reader can refuse a version it does not know.
                    using JsonDocument answer = JsonDocument.Parse(output);
                    Assert.True(answer.RootElement.ValueKind == JsonValueKind.Object, $"{invocation} answered {answer.RootElement.ValueKind}.");
                    Assert.True(VersionNames.Any(name => answer.RootElement.TryGetProperty(name, out _)),
                        $"{invocation} names no contract.");
                }
                catch (JsonException exception)
                {
                    Assert.Fail($"{invocation} wrote what is not one JSON document ({exception.Message}):\n{output}");
                }
            }
        }
        finally
        {
            File.Delete(workspace);
        }
    }

    [Fact(DisplayName = "R22: icat workspace show states what each session's layout keeps, its pins, its ranking and its evidence policy")]
    public async Task WorkspaceShowStatesEachLayout()
    {
        string workspace = Path.Combine(Path.GetDirectoryName(session.Path)!, Guid.NewGuid().ToString("N") + ".icat-workspace");
        try
        {
            Assert.Equal(InterCatExitCode.Success, (await Run("workspace", "new", workspace)).Code);
            Assert.Equal(InterCatExitCode.Success, (await Run("workspace", "add", workspace, session.Path)).Code);
            InvestigationWorkspace.SetLayout(workspace, TestSessions.Session, [new WorkspacePin { Key = "group:a", X = 0.5, Y = 0.5 }],
                DateTimeOffset.UtcNow, RankingMetric.BytesSent, perSecond: true);

            (InterCatExitCode shown, string text, string said) = await Run("workspace", "show", workspace);
            Assert.True(shown == InterCatExitCode.Success, said);
            Assert.Contains("1 node pinned on its graph, and its rows ranked by bytes sent per second, put back when it is opened "
                + "from this investigation.", text, StringComparison.Ordinal);
            string json = (await Run("workspace", "show", workspace, "--json")).Output;
            Assert.Contains("\"contract\": \"workspace-resolution-v15\"", json, StringComparison.Ordinal);
            Assert.Contains("\"rankBy\": \"BytesSent\"", json, StringComparison.Ordinal);
            Assert.Contains("\"evidencePolicy\": null", json, StringComparison.Ordinal);

            // A layout that counts a reused PID's candidates says so too, and keeps it in its document.
            InvestigationWorkspace.SetLayout(workspace, TestSessions.Session, [], DateTimeOffset.UtcNow,
                evidencePolicy: EvidencePolicy.IncludeCandidates);
            Assert.Contains("Session " + TestSessions.Session.ToString("N")[..8] + ": its records counted with candidates, put back "
                + "when it is opened from this investigation.", (await Run("workspace", "show", workspace)).Output,
                StringComparison.Ordinal);
            Assert.Contains("\"evidencePolicy\": \"IncludeCandidates\"", (await Run("workspace", "show", workspace, "--json")).Output,
                StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(workspace);
        }
    }

    [Fact(DisplayName = "R5: icat overview and icat processes number a reused PID's holders alike, as the window does")]
    public async Task AReusedPidsHoldersAreNumberedAlike()
    {
        // PID 100 is created, exits and is created again, both times as client.exe.
        using var reused = new TemporarySession();
        Publish(reused.Store,
        [
            Lifecycle(10, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe" },
            Lifecycle(30, ObservationKind.Exit, 100, 2),
            Lifecycle(40, ObservationKind.Create, 100, 3) with { ResourceName = @"C:\Tools\client.exe" },
        ]);
        reused.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string overview, string said) = await Run("overview", reused.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("client.exe · PID 100 #1", overview, StringComparison.Ordinal);
        Assert.Contains("client.exe · PID 100 #2", overview, StringComparison.Ordinal);
        string processes = (await Run("processes", reused.Path)).Output;
        Assert.Contains("100 #1", processes, StringComparison.Ordinal);
        Assert.Contains("100 #2", processes, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "P2: icat content shows a part that is not whole with each gap in place, and never saves it as one")]
    public async Task APartThatIsNotWholeShowsItsGaps()
    {
        using var http = new TemporarySession();

        // Exchange 4's response body: buffer 0 kept whole, buffer 1 never recorded, buffer 2 kept whole and flagged last.
        ObservationRowV1[] rows = [Body(10, 1), Body(12, 2)];
        (long Sequence, long Flags)[] places = [(0, 1), (2, 2)];
        SourceFieldRowV1[] fields =
        [
            .. places.SelectMany((place, index) => new[]
            {
                Field(rows[index], SourceField.HttpExchangeId, 4),
                Field(rows[index], SourceField.ContentBufferSequence, place.Sequence),
                Field(rows[index], SourceField.ContentBufferFlags, place.Flags),
            }),
        ];
        Publish(http.Store, rows, fields: fields, content: (ContentHeader(recordLimit: 8),
            [Content(rows[0], "head"u8.ToArray(), 8, ContentEncodingV1.Binary), Content(rows[1], "tail"u8.ToArray(), 8, ContentEncodingV1.Binary)]));
        SessionEvidencePage page = SessionEvidenceQuery.Read(http.Store);
        http.Store.ReleaseSegmentReaders();
        SessionEvidenceRecord first = page.Records[0];
        string[] locator =
        [
            "content", http.Path, "--session-id", page.SessionId.ToString(), "--generation",
            page.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture), "--segment", first.SegmentName, "--row",
            first.SegmentRow.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ];
        const string Gap = "  -- Buffer 1 was not recorded: its length is not known, and nothing stands in for it";

        // Shown, the part is its buffers in order with the gap between them; the answer is partial, as the part is.
        (InterCatExitCode code, string output, string said) = await Run([.. locator, "--part", "--reveal"]);
        Assert.True(code == InterCatExitCode.PartialResultSuccess, said);
        string[] shown = output.Split(Environment.NewLine);
        int gap = Array.IndexOf(shown, Gap);
        Assert.True(gap > 0, output);
        Assert.Equal("  -- Buffer 0, the part's first: 4 bytes, kept whole; bytes 0 to 3 of the part", shown[gap - 2]);
        Assert.Equal("  " + ContentBytesView.Rows("head"u8, 0).Single().Line, shown[gap - 1]);
        Assert.Equal("  -- Buffer 2, the part's last: 4 bytes, kept whole", shown[gap + 1]);
        Assert.Equal("  " + ContentBytesView.Rows("tail"u8, 0).Single().Line, shown[gap + 2]);

        // --from and --to choose its buffers; a buffer past those recorded is refused in words.
        (code, output, _) = await Run([.. locator, "--part", "--reveal", "--from", "1"]);
        Assert.Equal(InterCatExitCode.PartialResultSuccess, code);
        Assert.DoesNotContain("-- Buffer 0", output, StringComparison.Ordinal);
        Assert.Contains(Gap, output, StringComparison.Ordinal);
        (code, _, said) = await Run([.. locator, "--part", "--reveal", "--to", "7"]);
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("The part's recorded buffers are 0 to 2; choose buffers within them.", said, StringComparison.Ordinal);

        // It is never written as one file, since nothing may stand in for its missing bytes.
        string target = Path.Combine(Path.GetDirectoryName(http.Path)!, Guid.NewGuid().ToString("N") + ".bin");
        (code, _, said) = await Run([.. locator, "--part", "--save", target]);
        Assert.Equal(InterCatExitCode.PartialResultSuccess, code);
        Assert.False(File.Exists(target));
        Assert.Contains("never saved as one file", said, StringComparison.Ordinal);

        // Its facts name the gap where it falls, and no byte.
        (code, output, _) = await Run([.. locator, "--json"]);
        Assert.Equal(InterCatExitCode.Success, code);
        using JsonDocument answer = JsonDocument.Parse(output);
        JsonElement part = answer.RootElement.GetProperty("part");
        Assert.False(part.GetProperty("complete").GetBoolean());
        JsonElement missing = Assert.Single(part.GetProperty("gaps").EnumerateArray().ToArray());
        Assert.Equal(("NotRecorded", 1L, 1L, JsonValueKind.Null, 4L),
            (missing.GetProperty("kind").GetString(), missing.GetProperty("firstBuffer").GetInt64(),
                missing.GetProperty("lastBuffer").GetInt64(), missing.GetProperty("length").ValueKind,
                missing.GetProperty("partOffset").GetInt64()));
        Assert.DoesNotContain("head", output, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "ADR-036: icat retain releases kept content only when told why, and icat session and icat content say so")]
    public async Task ContentIsReleasedOnlyWhenToldWhy()
    {
        using var kept = new TemporarySession();
        ObservationRowV1[] rows = [Body(10, 1), Body(12, 2)];
        Publish(kept.Store, rows, finished: true, content: (ContentHeader(recordLimit: 8),
            [Content(rows[0], "head"u8.ToArray(), 8, ContentEncodingV1.Binary), Content(rows[1], "tail"u8.ToArray(), 8, ContentEncodingV1.Binary)]));
        long generation = kept.Store.Current!.Generation;
        kept.Store.ReleaseSegmentReaders();
        long Current() => SessionStore.OpenExisting(LocalOwnedDirectory.Open(kept.Path)).Current!.Generation;

        // Measured, nothing is written, and what would go is named: every message's bytes and content facts.
        (InterCatExitCode code, string output, string said) = await Run("retain", kept.Path, "--release-content");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("2 records' content: 2 kept whole, 0 cut, 0 not kept; 8 B of message bytes", output, StringComparison.Ordinal);
        Assert.Contains("This was a measurement. Add --confirm and --reason to perform it.", output, StringComparison.Ordinal);
        Assert.Equal(generation, Current());

        // Performing it needs a confirmation and a reason, and one release at a time.
        (code, _, said) = await Run("retain", kept.Path, "--release-content", "--reason", "payloads held tokens");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Equal(generation, Current());
        (code, _, said) = await Run("retain", kept.Path, "--release-content", "--confirm");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("--confirm needs --reason", said, StringComparison.Ordinal);
        (code, _, said) = await Run("retain", kept.Path, "--release-content", "--release-journal-before-record", "1");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("two releases", said, StringComparison.Ordinal);
        Assert.Equal(generation, Current());

        // Told why, it releases, and says what went.
        (code, output, said) = await Run("retain", kept.Path, "--release-content", "--confirm", "--reason", "payloads held tokens", "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        using (JsonDocument answer = JsonDocument.Parse(output))
        {
            JsonElement root = answer.RootElement;
            Assert.Equal(("store-v1", "release-content", true), (root.GetProperty("contract").GetString(),
                root.GetProperty("action").GetString(), root.GetProperty("performed").GetBoolean()));
            Assert.Equal(2, root.GetProperty("result").GetProperty("releasedRecords").GetInt64());
        }

        Assert.Equal(generation + 1, Current());

        // The session states the release, and a record's content says when and why it went.
        (_, output, _) = await Run("session", kept.Path);
        Assert.Contains("the content kept of 2 records: their bytes and each one's content facts; every record's metadata is kept",
            output, StringComparison.Ordinal);
        Assert.Contains("payloads held tokens", output, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"Content\"", (await Run("session", kept.Path, "--json")).Output, StringComparison.Ordinal);
        SessionEvidencePage page = SessionEvidenceQuery.Read(SessionStore.OpenExisting(LocalOwnedDirectory.Open(kept.Path)));
        (code, _, said) = await Run("content", kept.Path, "--session-id", page.SessionId.ToString(), "--generation",
            page.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture), "--segment", page.Records[0].SegmentName,
            "--row", page.Records[0].SegmentRow.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(InterCatExitCode.PartialResultSuccess, code);
        Assert.Contains("Its content was released on", said, StringComparison.Ordinal);
        Assert.Contains("\"payloads held tokens\"", said, StringComparison.Ordinal);

        // Nothing is left to release.
        (code, output, _) = await Run("retain", kept.Path, "--release-content", "--confirm", "--reason", "again");
        Assert.Equal(InterCatExitCode.PartialResultSuccess, code);
        Assert.Contains("This session keeps no content, so there is nothing to release.", output, StringComparison.Ordinal);
        Assert.Equal(generation + 1, Current());

        // A later generation still states the release, and which generation made it.
        (code, _, said) = await Run("checkpoint", kept.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Equal(generation + 2, Current());
        (_, output, _) = await Run("session", kept.Path);
        Assert.Matches($@"(?m)^  Released by +generation {generation + 1}\r?$", output);
        Assert.Contains("payloads held tokens", output, StringComparison.Ordinal);
        page = SessionEvidenceQuery.Read(SessionStore.OpenExisting(LocalOwnedDirectory.Open(kept.Path)));
        (_, _, said) = await Run("content", kept.Path, "--session-id", page.SessionId.ToString(), "--generation",
            page.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture), "--segment", page.Records[0].SegmentName,
            "--row", page.Records[0].SegmentRow.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("Its content was released on", said, StringComparison.Ordinal);
    }

    /// <summary>A WinINet response body record of process 4242, whose payload names no owner.</summary>
    private static ObservationRowV1 Body(long ticks, ulong ordinal) =>
        Transfer(ticks, ObservationKind.Receive, AccountingSide.ReceiveSide, 1, null, ordinal) with
        {
            Mechanism = Mechanism.Http,
            Layer = ObservationLayer.Application,
            EventId = 2004,
            HeaderProcessId = 4_242,
            Direction = Direction.Inbound,
            ByteDomain = ByteDomain.ApplicationPayload,
        };

    /// <summary>An answer without what differs between two runs of one query: how long each took.</summary>
    private static string Comparable(string answer) =>
        System.Text.RegularExpressions.Regex.Replace(answer, "\"(elapsed|counted|duration)[A-Za-z]*\": [0-9.]+", "\"$1\": 0");

    [Fact(DisplayName = "R4: icat evidence opens an RPC relationship's linked calls at both ends by the key the relationship carries")]
    public async Task EvidenceOpensAnRpcRelationshipByItsKey()
    {
        using var linked = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = LinkedRpcCalls();
        Publish(linked.Store, rows, fields: fields, coverage: RpcLedger(alpc: true));
        CommunicationEdge edge = Assert.Single(SessionOverviewProjector.Project(linked.Store).Edges, candidate => candidate.Mechanism == Mechanism.Rpc);
        string key = Assert.Single(edge.Evidence);

        // The key a relationship rests on is the one --channel takes: two linked calls, a start and a stop at each end.
        (InterCatExitCode code, string answer, _) = await Run("evidence", linked.Path, "--channel", key, "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        using JsonDocument page = JsonDocument.Parse(answer);
        Assert.Equal(edge.ObservationCount, page.RootElement.GetProperty("page").GetProperty("records").GetArrayLength());
        Assert.Equal(key, page.RootElement.GetProperty("page").GetProperty("operationKey").GetString());
        (code, answer, _) = await Run("evidence", linked.Path, "--channel", key);
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches($@"(?m)^  RPC key +{Regex.Escape(key)}$", answer);
    }

    /// <summary>One icat invocation, with what it wrote to stdout and to stderr.</summary>
    /// <summary>The column a field's value starts in, in a report that has one field of that label.</summary>
    private static int ValueColumn(string report, string label)
    {
        string line = report.Split(Environment.NewLine).Single(line => line.StartsWith($"  {label}  ", StringComparison.Ordinal));
        int column = 2 + label.Length;
        while (line[column] == ' ')
        {
            column++;
        }

        return column;
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
