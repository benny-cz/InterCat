using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using InterCat.Analysis;
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
        "checkpoint", "follow", "metric", "processes", "operations", "exchanges", "workspace", "verify", "bench", "support",
        "demo",
    ];

    /// <summary>The fields of icat staging's preview, whose labels run past the column a short label takes.</summary>
    private static readonly string[] StagingFields =
        ["Session", "Abandoned staged files", "Marker-only files", "Active writer files", "Unmarked legacy files", "Files removed"];

    /// <summary>The fields an export's report states before its caveats.</summary>
    private static readonly string[] ExportFields =
        ["Written to", "Contract", "Rung", "Breadcrumb", "Scope", "Ranked by", "Grouped by", "Rows", "Complete"];

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

        // An export's fields line up, and its caveats follow them, the capture's coverage over its scope last (R21).
        string destination = Path.Combine(Path.GetDirectoryName(session.Path)!, Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            (InterCatExitCode exported, string report, _) = await Run("export", session.Path, "--output", destination);
            Assert.Equal(InterCatExitCode.Success, exported);
            Assert.Single(ExportFields.Select(label => ValueColumn(report, label)).Distinct());
            Assert.EndsWith("Coverage unknown: this generation publishes no coverage ledger, so a count of none here is not proof "
                + "of inactivity", report.TrimEnd(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(destination);
        }

        // A checkpoint's report ends with its fields, which nothing written after them would bring out.
        (InterCatExitCode checkpointed, string written, _) = await Run("checkpoint", session.Path);
        Assert.Equal(InterCatExitCode.Success, checkpointed);
        Assert.Matches(@"\n  Size +\S[^\n]*$", written.TrimEnd());
    }

    [Fact(DisplayName = "R18: icat export --group-by session reaches a session's rung by its key, as the window groups it, and never guesses a session")]
    public async Task AnExportGroupsBySession()
    {
        using var named = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = TerminalSessions();
        Publish(named.Store, rows, fields: fields);
        using var unnamed = new TemporarySession();
        Publish(unnamed.Store, rows);
        string output = Path.Combine(Path.GetDirectoryName(named.Path)!, Guid.NewGuid().ToString("N") + ".json");
        try
        {
            // Grouped by session, terminal session 1's rung lists its two processes, and the report and the file say how.
            (InterCatExitCode exported, string report, string said) =
                await Run("export", named.Path, "--output", output, "--group-by", "session", "--at", "session:1");
            Assert.True(exported == InterCatExitCode.Success, said);
            Assert.Contains("Machine › Group: Terminal session 1", report, StringComparison.Ordinal);
            Assert.Matches(new Regex(@"\n  Grouped by +terminal session\r?\n"), report);
            using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(output)))
            {
                Assert.Equal("session", document.RootElement.GetProperty("groupedBy").GetString());
                Assert.Equal(["client.exe", "server.exe"], document.RootElement.GetProperty("rows").EnumerateArray()
                    .Select(row => row.GetProperty("label").GetString()).Order(StringComparer.Ordinal));
            }

            // By executable, the default, a session's key names no row; a grouping the window does not offer is refused, and
            // a session whose records name no terminal session is never grouped by one.
            (InterCatExitCode byExecutable, _, string noRow) = await Run("export", named.Path, "--output", output, "--overwrite",
                "--at", "session:1");
            Assert.Equal(InterCatExitCode.InvalidInvocation, byExecutable);
            Assert.Contains("'session:1' is not a row of the Machine rung", noRow, StringComparison.Ordinal);
            (InterCatExitCode unknown, _, string notOffered) = await Run("export", named.Path, "--output", output, "--overwrite",
                "--group-by", "host");
            Assert.Equal(InterCatExitCode.InvalidInvocation, unknown);
            Assert.Contains("--group-by must be executable or session.", notOffered, StringComparison.Ordinal);
            (InterCatExitCode never, _, string guessed) = await Run("export", unnamed.Path, "--output", output, "--overwrite",
                "--group-by", "session");
            Assert.Equal(InterCatExitCode.InvalidInvocation, never);
            Assert.Contains("a session is never guessed", guessed, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact(DisplayName = "R21: icat export's CSV states what the capture covered on every line, and its sharing report keeps it")]
    public async Task AnExportsCsvAndSharingReportStateTheirCoverage()
    {
        const string NoLedger = "Coverage unknown: this generation publishes no coverage ledger, so a count of none here is not "
            + "proof of inactivity";
        string folder = Path.GetDirectoryName(session.Path)!;
        string rows = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".csv");
        string shared = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            (InterCatExitCode exported, _, _) = await Run("export", session.Path, "--output", rows);
            (InterCatExitCode reported, string report, _) = await Run("export", session.Path, "--output", shared, "--share-redacted");
            Assert.Equal((InterCatExitCode.Success, InterCatExitCode.Success), (exported, reported));

            // The sharing report's own report says what its file keeps, as the detailed export's does.
            Assert.EndsWith(NoLedger, report.TrimEnd(), StringComparison.Ordinal);
            // A ranking's lines then count what its view set aside, which came later, so each earlier column kept its place.
            foreach (string file in new[] { rows, shared })
            {
                string[] lines = File.ReadAllLines(file);
                Assert.EndsWith(",scope_coverage,set_aside_processes,set_aside_records", lines[0], StringComparison.Ordinal);
                Assert.All(lines.Skip(1), line => Assert.EndsWith($",\"{NoLedger}\",0,0", line, StringComparison.Ordinal));
            }
        }
        finally
        {
            File.Delete(rows);
            File.Delete(shared);
        }
    }

    [Fact(DisplayName = "R21: icat operations, exchanges and channels say what the capture covered of what they list, never from a count of none")]
    public async Task AListingSaysWhatTheCaptureCoveredOfIt()
    {
        using var tcp = new TemporarySession();
        Publish(tcp.Store,
        [
            Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 1).Between("127.0.0.1:50000", "127.0.0.1:8080")
                with { SessionRelativeTicks = 10_000 },
            Transfer(110, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 2).Between("127.0.0.1:8080", "127.0.0.1:50000")
                with { SessionRelativeTicks = 11_000 },
        ], coverage: new CoverageLedgerV1 { Contract = CoverageLedgerV1.ContractName, Epochs = [TcpEpoch()] });
        tcp.Store.ReleaseSegmentReaders();
        const string NotCollected = " was not collected over the session: no admitted descriptor records it.";

        // A capture that collected TCP alone lists no RPC call and no HTTP exchange because it could see none, and says so
        // from its ledger: what it holds never stands for what it collected.
        (InterCatExitCode listed, string calls, _) = await Run("operations", tcp.Path);
        Assert.Equal(InterCatExitCode.Success, listed);
        Assert.Contains("RPC" + NotCollected, calls, StringComparison.Ordinal);
        Assert.Contains("This session holds no RPC call record.", calls, StringComparison.Ordinal);
        Assert.Contains("none resolved, the capture did not collect ALPC (icat record --profile rpc-peers does)", calls,
            StringComparison.Ordinal);
        Assert.Contains("HTTP" + NotCollected, (await Run("exchanges", tcp.Path)).Output, StringComparison.Ordinal);

        // One that collected ALPC and was delivered none of it says it holds none to follow, not that it collected none.
        using var quiet = new TemporarySession();
        Publish(quiet.Store, [Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 1)
                .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = 10_000 }],
            coverage: new CoverageLedgerV1 { Contract = CoverageLedgerV1.ContractName, Epochs = [TcpEpoch(alpc: true)] });
        quiet.Store.ReleaseSegmentReaders();
        Assert.Contains("none resolved, the generation holds no ALPC record (ALPC coverage: covered)",
            (await Run("operations", quiet.Path)).Output, StringComparison.Ordinal);
        Assert.Contains("TCP was covered over the session: 2 records from its 1 admitted descriptor, and nothing was reported lost.",
            (await Run("channels", tcp.Path)).Output, StringComparison.Ordinal);

        // A script reads the same coverage by the enumerations' names.
        foreach ((string[] args, Func<JsonElement, JsonElement> coverage, (string, string)[] expected) in new (string[], Func<JsonElement, JsonElement>, (string, string)[])[]
        {
            (["operations", tcp.Path, "--json"], root => root.GetProperty("coverage"), [("Rpc", "NotCollected"), ("Alpc", "NotCollected")]),
            (["exchanges", tcp.Path, "--json"], root => root.GetProperty("coverage"), [("Http", "NotCollected")]),
            (["channels", tcp.Path, "--json"], root => root.GetProperty("page").GetProperty("coverage"), [("Tcp", "Covered")]),
        })
        {
            using JsonDocument document = JsonDocument.Parse((await Run(args)).Output);
            Assert.Equal(expected, coverage(document.RootElement).EnumerateArray().Select(entry =>
                (entry.GetProperty("mechanism").GetString()!, entry.GetProperty("state").GetString()!)));
        }

        // A generation without a ledger judged nothing, and each listing says that, not that a source was left out.
        (_, string unjudged, _) = await Run("operations", session.Path);
        Assert.Contains("RPC's coverage over the session is unknown: this generation publishes no coverage ledger.", unjudged,
            StringComparison.Ordinal);
        Assert.Contains("none resolved, the generation holds no ALPC record (ALPC coverage: unknown)", unjudged,
            StringComparison.Ordinal);
        Assert.DoesNotContain("did not", unjudged, StringComparison.Ordinal);
        Assert.Contains("HTTP's coverage over the session is unknown", (await Run("exchanges", session.Path)).Output,
            StringComparison.Ordinal);
        Assert.Contains("TCP's coverage over the session is unknown", (await Run("channels", session.Path)).Output,
            StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R21: icat processes says a process's transport bytes are unmeasured, or that it made none, as the window's rows do")]
    public async Task AProcessesBytesAreUnmeasuredOrNone()
    {
        // PID 100 sends 64 bytes to PID 200, which receives them; PID 300 sends once, and its source exposed no size.
        ObservationRowV1 unsizedSend = Transfer(90, ObservationKind.Send, AccountingSide.SendSide, 0, 300, 3)
            .Between("127.0.0.1:50001", "127.0.0.1:9090")
            with { ByteValue = null, ByteAvailability = FieldAvailability.NotExposed, SessionRelativeTicks = 9_000 };
        using var traffic = new TemporarySession();
        Publish(traffic.Store,
        [
            unsizedSend,
            Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 1).Between("127.0.0.1:50000", "127.0.0.1:8080")
                with { SessionRelativeTicks = 10_000 },
            Transfer(110, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 2).Between("127.0.0.1:8080", "127.0.0.1:50000")
                with { SessionRelativeTicks = 11_000 },
        ]);
        traffic.Store.ReleaseSegmentReaders();

        // Neither a send that recorded no size nor no send at all is a zero, and each says which it is.
        (InterCatExitCode code, string table, string said) = await Run("processes", traffic.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  100 .* 64 B +no receives\r?$", table);
        Assert.Matches(@"(?m)^  200 .* no sends +64 B\r?$", table);
        Assert.Matches(@"(?m)^  300 .* unmeasured +no receives\r?$", table);
        using (JsonDocument json = JsonDocument.Parse((await Run("processes", traffic.Path, "--json")).Output))
        {
            JsonElement idle = json.RootElement.GetProperty("instances").EnumerateArray()
                .Single(item => item.GetProperty("process").GetProperty("processId").GetInt32() == 300);
            Assert.Equal(JsonValueKind.Null, idle.GetProperty("transportBytesSent").ValueKind);
            Assert.Equal((0L, 1L), (idle.GetProperty("sends").GetProperty("measured").GetInt64(),
                idle.GetProperty("sends").GetProperty("unmeasured").GetInt64()));
            Assert.Equal((0L, 0L), (idle.GetProperty("receives").GetProperty("measured").GetInt64(),
                idle.GetProperty("receives").GetProperty("unmeasured").GetInt64()));
        }

        // Where no send measured a size, the answer has nothing measured, and its sender is still unmeasured, not silent.
        using var unsized = new TemporarySession();
        Publish(unsized.Store, [unsizedSend]);
        unsized.Store.ReleaseSegmentReaders();
        (code, table, said) = await Run("processes", unsized.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  300 .* unmeasured +no receives\r?$", table);
    }

    [Fact(DisplayName = "R18: icat overview says what the capture covered over the session in the words the window's inspector uses")]
    public async Task TheOverviewSaysWhatTheCaptureCovered()
    {
        using var tcp = new TemporarySession();
        Publish(tcp.Store,
        [
            Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 1).Between("127.0.0.1:50000", "127.0.0.1:8080")
                with { SessionRelativeTicks = 10_000 },
        ], coverage: new CoverageLedgerV1 { Contract = CoverageLedgerV1.ContractName, Epochs = [TcpEpoch()] });
        tcp.Store.ReleaseSegmentReaders();

        // The window states beneath its time scope the words its snapshot's coverage reads as, the overview's own list.
        foreach ((TemporarySession held, string said) in new[]
        {
            (tcp, "Coverage: covered for TCP · no other mechanism collected"),
            (session, "Coverage unknown: this generation publishes no coverage ledger, so a count of none here is not proof of "
                + "inactivity"),
        })
        {
            Assert.Equal(said, CoverageText.Describe(OverviewWorkspace.From(SessionOverviewProjector.Project(held.Store)).MechanismCoverage));
            (InterCatExitCode code, string overview, string error) = await Run("overview", held.Path);
            Assert.True(code == InterCatExitCode.Success, error);
            Assert.Contains("  " + said + "\n", overview.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "R21: icat processes says what the capture covered as icat metric does, and never that a process sent nothing")]
    public async Task ProcessesSayWhatTheCaptureCovered()
    {
        // PID 300 sent once, and its source did not expose the size: none of its transport bytes was measured.
        using var tcp = new TemporarySession();
        Publish(tcp.Store,
        [
            Transfer(90, ObservationKind.Send, AccountingSide.SendSide, 0, 300, 3).Between("127.0.0.1:50001", "127.0.0.1:9090")
                with { ByteValue = null, ByteAvailability = FieldAvailability.NotExposed, SessionRelativeTicks = 9_000 },
            Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 1).Between("127.0.0.1:50000", "127.0.0.1:8080")
                with { SessionRelativeTicks = 10_000 },
            Transfer(110, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, 2).Between("127.0.0.1:8080", "127.0.0.1:50000")
                with { SessionRelativeTicks = 11_000 },
        ], coverage: new CoverageLedgerV1 { Contract = CoverageLedgerV1.ContractName, Epochs = [TcpEpoch()] });
        tcp.Store.ReleaseSegmentReaders();

        // The coverage of the counts it lists is what icat metric says of the same grouped answer, word for word.
        foreach ((string path, string said) in new[]
        {
            (tcp.Path, "Coverage: covered for TCP · no other mechanism collected"),
            (session.Path, "Coverage unknown: this generation publishes no coverage ledger, so a count of none here is not proof "
                + "of inactivity"),
        })
        {
            (InterCatExitCode listed, string processes, _) = await Run("processes", path);
            Assert.Equal(InterCatExitCode.Success, listed);
            Assert.Contains("  " + said + "\n", processes.ReplaceLineEndings("\n"), StringComparison.Ordinal);
            Assert.Contains("  " + said + "\n", (await Run("metric", path, "--metric", "observations", "--group-by", "process")).Output
                .ReplaceLineEndings("\n"), StringComparison.Ordinal);
        }

        using (JsonDocument json = JsonDocument.Parse((await Run("processes", tcp.Path, "--json")).Output))
        {
            JsonElement coverage = json.RootElement.GetProperty("coverage");
            Assert.True(coverage.GetProperty("ledgerPublished").GetBoolean());
            JsonElement tcpCoverage = coverage.GetProperty("mechanisms").EnumerateArray()
                .Single(entry => entry.GetProperty("mechanism").GetString() == "Tcp");
            Assert.Equal(("Covered", "2 records from its 1 admitted descriptor, and nothing was reported lost"),
                (tcpCoverage.GetProperty("state").GetString(), tcpCoverage.GetProperty("reason").GetString()));
        }

        // A process whose one send recorded no size lists the end it sent to, its bytes unmeasured rather than none.
        (_, string idle, _) = await Run("processes", tcp.Path, "--pid", "300");
        Assert.Matches(@"(?m)^  unresolved: no record in this capture holds the other end \(a remote or unobserved peer\) +unmeasured +none\r?$",
            idle);
        Assert.DoesNotContain("sent and received no transport bytes", idle, StringComparison.Ordinal);

        // A receiver sent its sender nothing, which reads as none beside the bytes it received from it.
        Assert.Matches(@"(?m)^  PID 100 \(image not witnessed\) +none +64 B\r?$", (await Run("processes", tcp.Path, "--pid", "200")).Output);

        // One that made no transport record at all says so, which is not proof it was idle.
        using var quiet = new TemporarySession();
        Publish(quiet.Store, [Lifecycle(10, ObservationKind.Create, 400, 1) with { ResourceName = @"C:\Tools\idle.exe" }]);
        quiet.Store.ReleaseSegmentReaders();
        Assert.Contains("It made no transport send or receive record in this session, which is not proof it sent or received "
            + "nothing.", (await Run("processes", quiet.Path, "--pid", "400")).Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A live epoch that collected one TCP descriptor, delivered two records and lost nothing, and with <paramref name="alpc"/>
    /// an ALPC descriptor too, of which it was delivered nothing.
    /// </summary>
    private static CoverageEpochV1 TcpEpoch(bool alpc = false) => new()
    {
        Epoch = 1,
        Acquisition = CoverageAcquisition.LiveCapture,
        FirstDeliveredNativeTicks = 100,
        LastDeliveredNativeTicks = 110,
        Collected =
        [
            new CoverageCollectedV1 { ProviderId = NetworkProvider, ProviderName = "network", EventId = 10, Version = 0, Mechanism = Mechanism.Tcp },
            .. alpc
                ? new[] { new CoverageCollectedV1 { ProviderId = NetworkProvider, ProviderName = "network", EventId = 20, Version = 0, Mechanism = Mechanism.Alpc } }
                : [],
        ],
        Deliveries = [new CoverageDeliveryV1 { ProviderId = NetworkProvider, EventId = 10, Version = 0, Delivered = 2, Admitted = 2, Omitted = 0 }],
        Losses =
        [
            new CoverageLossV1 { Layer = LossLayer.SourceSession, Lost = 0 },
            new CoverageLossV1 { Layer = LossLayer.ConsumerBuffers, Lost = 0 },
            new CoverageLossV1 { Layer = LossLayer.CallbackQueue, Lost = 0 },
            new CoverageLossV1 { Layer = LossLayer.Storage, Lost = 0 },
        ],
    };

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

    [Fact(DisplayName = "R21: icat workspace correlate says an end's sends that measured no size are unmeasured, never 0 B, in the window's words")]
    public async Task CorrelateSaysUnmeasuredBytes()
    {
        string folder = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(session.Path)!, Guid.NewGuid().ToString("N"))).FullName;
        string workspace = Path.Combine(folder, "case.icat-workspace");
        try
        {
            // The client's one send recorded no size, and two captures of a server - of two hosts - each hold its other end,
            // receiving it with no size either and replying with 200 bytes and 50.
            const string Client = "10.0.0.1:50000";
            const string Server = "10.0.0.2:443";
            string client = Held(folder, "client", "lab-1",
            [
                Transfer(1_000, ObservationKind.Send, AccountingSide.SendSide, null, 100, 1).Between(Client, Server) with { SessionRelativeTicks = 100_000 },
                Transfer(1_001, ObservationKind.Receive, AccountingSide.ReceiveSide, 200, 100, 2).Between(Client, Server) with { SessionRelativeTicks = 100_100 },
                Transfer(1_002, ObservationKind.Receive, AccountingSide.ReceiveSide, 50, 100, 3).Between(Client, Server) with { SessionRelativeTicks = 100_200 },
            ]);
            ObservationRowV1[] served =
            [
                Transfer(1_000, ObservationKind.Receive, AccountingSide.ReceiveSide, null, 200, 1).Between(Server, Client) with { SessionRelativeTicks = 100_000 },
                Transfer(1_001, ObservationKind.Send, AccountingSide.SendSide, 200, 200, 2).Between(Server, Client) with { SessionRelativeTicks = 100_100 },
                Transfer(1_002, ObservationKind.Send, AccountingSide.SendSide, 50, 200, 3).Between(Server, Client) with { SessionRelativeTicks = 100_200 },
            ];
            Assert.Equal(InterCatExitCode.Success, (await Run("workspace", "new", workspace)).Code);
            foreach (string member in new[] { client, Held(folder, "server", "lab-2", served), Held(folder, "replica", "lab-3", served) })
            {
                Assert.Equal(InterCatExitCode.Success, (await Run("workspace", "add", workspace, member)).Code);
            }

            (InterCatExitCode code, string text, string said) = await Run("workspace", "correlate", workspace);
            Assert.True(code == InterCatExitCode.Success, said);
            Assert.Contains("(PID 100): 1 send unmeasured, 250 B received; open before the capture and after it", text, StringComparison.Ordinal);
            Assert.Contains("(PID 200): 250 B sent, 1 receive unmeasured; open before the capture and after it", text, StringComparison.Ordinal);
            Assert.Contains(" · lifetimes not comparable · not the only match: 1 other candidate" + Environment.NewLine, text, StringComparison.Ordinal);
            Assert.DoesNotContain(": 0 B sent", text, StringComparison.Ordinal);

            // Its document counts each direction's transfers and those of no size, and leaves out a sum none measured.
            using JsonDocument answer = JsonDocument.Parse((await Run("workspace", "correlate", workspace, "--json")).Output);
            Assert.Equal(WorkspaceCommand.CorrelationContract, answer.RootElement.GetProperty("contract").GetString());
            JsonElement candidate = answer.RootElement.GetProperty("candidates")[0];
            JsonElement[] ends = [candidate.GetProperty("first"), candidate.GetProperty("second")];
            (long, long, JsonValueKind, long, long, string) Counted(int pid)
            {
                JsonElement end = ends.Single(each => each.GetProperty("processId").GetInt32() == pid);
                return (end.GetProperty("sends").GetInt64(), end.GetProperty("unmeasuredSends").GetInt64(), end.GetProperty("sentBytes").ValueKind,
                    end.GetProperty("receives").GetInt64(), end.GetProperty("unmeasuredReceives").GetInt64(), end.GetProperty("receivedBytes").ToString());
            }

            Assert.Equal((1L, 1L, JsonValueKind.Null, 2L, 0L, "250"), Counted(100));
            Assert.Equal((2L, 0L, JsonValueKind.Number, 1L, 1L, string.Empty), Counted(200));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact(DisplayName = "R21: icat workspace correlate states what each compared capture covered of TCP and UDP, and that no candidate is then no proof")]
    public async Task CorrelateStatesEachCapturesCoverage()
    {
        string folder = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(session.Path)!, Guid.NewGuid().ToString("N"))).FullName;
        string workspace = Path.Combine(folder, "case.icat-workspace");
        try
        {
            // Two hosts' captures whose connections mirror none of each other's: the first lost 2 events and collected no UDP,
            // and the second covered both.
            Assert.Equal(InterCatExitCode.Success, (await Run("workspace", "new", workspace)).Code);
            foreach (string member in new[]
            {
                Held(folder, "first", "lab-1",
                    [Transfer(1_000, ObservationKind.Send, AccountingSide.SendSide, 10, 100, 1).Between("10.0.0.1:50000", "10.0.0.2:443") with { SessionRelativeTicks = 100_000 }],
                    TransportLedger(tcp: true, udp: false, lost: 2)),
                Held(folder, "second", "lab-2",
                    [Transfer(1_000, ObservationKind.Send, AccountingSide.SendSide, 10, 200, 1).Between("10.0.0.3:50000", "10.0.0.4:443") with { SessionRelativeTicks = 100_000 }],
                    TransportLedger(tcp: true, udp: true)),
            })
            {
                Assert.Equal(InterCatExitCode.Success, (await Run("workspace", "add", workspace, member)).Code);
            }

            (InterCatExitCode code, string text, string said) = await Run("workspace", "correlate", workspace);
            Assert.True(code == InterCatExitCode.Success, said);
            Assert.Matches(@"Coverage +[0-9a-f]{8}: TCP partial gap, not extrapolated · UDP not collected; [0-9a-f]{8}: TCP covered · UDP covered", text);
            Assert.Contains(WorkspaceCorrelation.NoCandidate(coverageShort: true), text, StringComparison.Ordinal);
            Assert.Contains(" has a partial gap in TCP (the session reported 2 lost events, which may be any mechanism's), so the mirror of a "
                + "TCP connection may be among the records it lost.", text, StringComparison.Ordinal);
            Assert.Contains(" did not collect UDP (no admitted descriptor records it), so it holds no mirror of a UDP connection.", text,
                StringComparison.Ordinal);

            // Its document gives each capture's state of each mechanism, and the fact behind it.
            using JsonDocument answer = JsonDocument.Parse((await Run("workspace", "correlate", workspace, "--json")).Output);
            Assert.Equal("workspace-correlation-v6", answer.RootElement.GetProperty("contract").GetString());
            JsonElement coverage = answer.RootElement.GetProperty("coverage");
            Assert.Equal(["Tcp PartialGap", "Udp NotCollected", "Tcp Covered", "Udp Covered"],
                coverage.EnumerateArray().SelectMany(member => member.GetProperty("mechanisms").EnumerateArray()
                    .Select(entry => entry.GetProperty("mechanism").GetString() + " " + entry.GetProperty("state").GetString())));
            Assert.Equal("no admitted descriptor records it", coverage[0].GetProperty("mechanisms")[1].GetProperty("reason").GetString());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact(DisplayName = "R18: icat workspace timeline draws the merged time as the investigation window does, its lanes' records, places and coverage in its words")]
    public async Task WorkspaceTimelineDrawsTheMergedTime()
    {
        string folder = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(session.Path)!, Guid.NewGuid().ToString("N"))).FullName;
        string workspace = Path.Combine(folder, "case.icat-workspace");
        try
        {
            // Alpha's capture covered its first readings, delivered none from 200 µs to 400 µs, and then lost 3 events; beta,
            // aligned to it 50 µs later, has no ledger; gamma is aligned to nothing; and a note is about the whole.
            ObservationRowV1[] Datagrams(params long[] ticks) =>
            [
                .. ticks.Select((at, index) => Transfer(at, ObservationKind.Send, AccountingSide.SendSide, 10, 100, (ulong)(index + 1))
                    .Between("192.168.1.5:61000", "8.8.8.8:53") with { Mechanism = Mechanism.Udp, SessionRelativeTicks = at * 100 }),
            ];
            CoverageEpochV1 covered = TransportLedger(tcp: false, udp: true).Epochs[0];
            CoverageEpochV1 lossy = TransportLedger(tcp: false, udp: true, lost: 3).Epochs[0];
            var ledger = new CoverageLedgerV1
            {
                Contract = CoverageLedgerV1.ContractName,
                Epochs =
                [
                    covered with { FirstDeliveredNativeTicks = 0, LastDeliveredNativeTicks = 2_000 },
                    lossy with { Epoch = 2, FirstDeliveredNativeTicks = 4_000, LastDeliveredNativeTicks = 6_000 },
                ],
            };
            InvestigationWorkspace.Create(workspace, DateTimeOffset.UtcNow);
            Guid alpha = InvestigationWorkspace.Add(workspace,
                Held(folder, "alpha", "lab-1", Datagrams(1_000, 1_010, 1_020, 1_030, 5_000, 5_010, 5_020, 5_030), ledger), DateTimeOffset.UtcNow).SessionId;
            Guid beta = InvestigationWorkspace.Add(workspace, Held(folder, "beta", "lab-2", Datagrams(1_000, 1_010)), DateTimeOffset.UtcNow).SessionId;
            InvestigationWorkspace.Add(workspace, Held(folder, "gamma", "lab-3", Datagrams(1_000)), DateTimeOffset.UtcNow);
            InvestigationWorkspace.Align(workspace, beta, 0, alpha, 50_000, 1_000, 0, null, DateTimeOffset.UtcNow);
            InvestigationWorkspace.AddNote(workspace, "The retry storm begins here.", null, DateTimeOffset.UtcNow);

            // The lanes are said in the window's words, then drawn: the columns that hold records, and the runs the window hatches.
            InvestigationTimelineView view = InvestigationTimeline.Read(workspace, WorkspaceCommand.TimelineColumns);
            (_, IReadOnlyList<string> sentences) = InvestigationTimelineText.Describe(InvestigationWorkspace.Read(workspace), view, System.Globalization.CultureInfo.CurrentCulture);
            (InterCatExitCode code, string text, string said) = await Run("workspace", "timeline", workspace);
            Assert.True(code == InterCatExitCode.Success, said);
            Assert.Contains("0.0001 s to 0.0005031 s of the investigation's time, in 160 columns", text, StringComparison.Ordinal);
            Assert.All(sentences, sentence => Assert.Contains("  " + sentence + Environment.NewLine, text, StringComparison.Ordinal));
            Assert.EndsWith("not placed: not aligned to the investigation's time.", sentences[2], StringComparison.Ordinal);
            Assert.EndsWith(", about the whole investigation: The retry storm begins here.", sentences[3], StringComparison.Ordinal);
            Assert.Contains(" Coverage over its 160 columns: ", sentences[0], StringComparison.Ordinal);

            // A lane's records run from its first to its last, each at its own instant: never the tick past the last.
            Assert.Contains($": 8 records, from {0.0001m.ToString("0.000######", System.Globalization.CultureInfo.CurrentCulture)} s to "
                + $"{0.000503m.ToString("0.000######", System.Globalization.CultureInfo.CurrentCulture)} s of the investigation's time, ", sentences[0],
                StringComparison.Ordinal);
            List<TimelineBucket> lane = [.. view.Lanes[0].Buckets];
            int first = lane.FindIndex(bucket => bucket.ObservationCount > 0);
            Assert.Matches($@"(?m)^\s+{first + 1}\s+0\.0001 s\s+\S+ s\s+{lane[first].ObservationCount}\s+covered\s*$", text);
            Assert.DoesNotMatch($@"(?m)^\s+{first + 3}\s+\S+ s\s", text);
            (int unknown, int gap) = (lane.FindIndex(bucket => bucket.Coverage == CoverageState.UnknownCoverage),
                lane.FindIndex(bucket => bucket.Coverage == CoverageState.PartialGap));
            Assert.Contains($"Not covered: columns {unknown + 1}–{gap} unknown; columns {gap + 1}–160 partial gap, not extrapolated.", text,
                StringComparison.Ordinal);
            Assert.Contains("Not covered: columns 1–160 unknown.", text, StringComparison.Ordinal);

            // Its document carries every column of every lane, its records and coverage, and the lane's sentence.
            using JsonDocument answer = JsonDocument.Parse((await Run("workspace", "timeline", workspace, "--json")).Output);
            JsonElement root = answer.RootElement;
            Assert.Equal(("workspace-timeline-v1", 160, 3), (root.GetProperty("contract").GetString(), root.GetProperty("columns").GetInt32(),
                root.GetProperty("lanes").GetArrayLength()));
            JsonElement first0 = root.GetProperty("lanes")[0];
            Assert.Equal((sentences[0], 8L, 160), (first0.GetProperty("sentence").GetString(), first0.GetProperty("records").GetInt64(),
                first0.GetProperty("buckets").GetArrayLength()));
            Assert.Equal(lane.Select(bucket => bucket.Coverage.ToString()),
                first0.GetProperty("buckets").EnumerateArray().Select(bucket => bucket.GetProperty("coverage").GetString()));
            Assert.Equal(2, root.GetProperty("snapshotVector").GetArrayLength());
            Assert.Equal([sentences[3]], root.GetProperty("statements").EnumerateArray().Select(statement => statement.GetString()));
            JsonElement second = root.GetProperty("lanes")[1];
            JsonElement unplaced = root.GetProperty("lanes")[2];
            Assert.Equal((1_000L, true, 1_000d, "None"), (first0.GetProperty("extentStartTicks").GetInt64(), second.GetProperty("placed").GetBoolean(),
                second.GetProperty("uncertaintyNanoseconds").GetDouble(), second.GetProperty("gap").GetString()));
            JsonElement column = second.GetProperty("buckets")[0];
            Assert.Equal(column.GetProperty("startTicks").GetInt64() - 500, column.GetProperty("ownStartTicks").GetInt64());
            Assert.Equal((false, "NotAligned", 0, JsonValueKind.Null), (unplaced.GetProperty("placed").GetBoolean(), unplaced.GetProperty("gap").GetString(),
                unplaced.GetProperty("buckets").GetArrayLength(), unplaced.GetProperty("extentStartTicks").ValueKind));

            // Fewer columns, or an interval of the investigation's time, are drawn as asked; a wrong one is refused in words.
            using JsonDocument narrow = JsonDocument.Parse((await Run("workspace", "timeline", workspace, "--columns", "20", "--interval", "1000:2000", "--json")).Output);
            Assert.Equal((20, 1_000L, 2_000L, 20), (narrow.RootElement.GetProperty("columns").GetInt32(), narrow.RootElement.GetProperty("startTicks").GetInt64(),
                narrow.RootElement.GetProperty("endTicks").GetInt64(), narrow.RootElement.GetProperty("lanes")[0].GetProperty("buckets").GetArrayLength()));
            (InterCatExitCode refused, _, string why) = await Run("workspace", "timeline", workspace, "--columns", "0");
            Assert.Equal((InterCatExitCode.InvalidInvocation, true), (refused, why.Contains("--columns must be an integer from 1 to 2,000.", StringComparison.Ordinal)));
            (refused, _, why) = await Run("workspace", "timeline", workspace, "--interval", "5:1");
            Assert.Equal((InterCatExitCode.InvalidInvocation, true), (refused, why.Contains("--interval 5:1 names no interval", StringComparison.Ordinal)));
            (refused, _, why) = await Run("workspace", "show", workspace, "--columns", "5");
            Assert.Equal((InterCatExitCode.InvalidInvocation, true), (refused, why.Contains("Use icat workspace show <workspace>.", StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact(DisplayName = "R21: icat workspace timeline names each run of columns a capture did not cover, a single column as one, and none where it covered all")]
    public void UncoveredRunsAreNamed()
    {
        static InvestigationLane Lane(params CoverageState[] states) => new(Guid.Empty, new TimeRange(0, states.Length),
            [.. states.Select((state, column) => new TimelineBucket(new TimeRange(column, column + 1), 0, null, Mechanism.Udp, state))], null,
            WorkspaceTimeGap.None, null);
        System.Globalization.CultureInfo culture = System.Globalization.CultureInfo.InvariantCulture;
        Assert.Equal("column 2 unknown; columns 4–5 partial gap, not extrapolated; column 6 not collected",
            WorkspaceCommand.Uncovered(Lane(CoverageState.Covered, CoverageState.UnknownCoverage, CoverageState.Covered, CoverageState.PartialGap,
                CoverageState.PartialGap, CoverageState.NotCollected), culture));
        Assert.Null(WorkspaceCommand.Uncovered(Lane(CoverageState.Covered, CoverageState.Covered), culture));
    }

    [Fact(DisplayName = "R21: icat processes says how each instance's records were bound, and what the evidence policy left out of a reused PID's later holder, as the inspector does")]
    public async Task ProcessesSayHowEachInstanceIsCounted()
    {
        string folder = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(session.Path)!, Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            // PID 100 runs first.exe, exits, and is created again running later.exe, whose two sends could be late records of
            // first.exe, so they bind to it only as candidates.
            string path = Held(folder, "reused", "lab-1",
            [
                Lifecycle(10, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\first.exe", SessionRelativeTicks = 1_000 },
                Lifecycle(30, ObservationKind.Exit, 100, 2) with { SessionRelativeTicks = 3_000 },
                Lifecycle(40, ObservationKind.Create, 100, 3) with { ResourceName = @"C:\Tools\later.exe", SessionRelativeTicks = 4_000 },
                Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 4).Between("10.0.0.1:50000", "10.0.0.2:443") with { SessionRelativeTicks = 5_000 },
                Transfer(60, ObservationKind.Send, AccountingSide.SendSide, 8, 100, 5).Between("10.0.0.1:50000", "10.0.0.2:443") with { SessionRelativeTicks = 6_000 },
            ]);
            ProcessNode Holder(int holder, EvidencePolicy policy) => SessionOverviewProjector.Project(
                SessionStore.OpenForViewing(LocalOwnedDirectory.Open(path)), policy).Nodes.Single(node => node.PidHolder == holder);
            (ProcessNode first, ProcessNode later) = (Holder(1, EvidencePolicy.IncludeCorrelated), Holder(2, EvidencePolicy.IncludeCorrelated));
            Assert.EndsWith("leaving out 2 records bound to it over the session. Coverage over the session: unknown.",
                ProcessBindingText.Explain(later), StringComparison.Ordinal);

            // The list says, beneath its table, what the policy left out of the later holder, in the inspector's words, and how
            // to count it; the first holder left nothing out, so the list says nothing of it.
            (InterCatExitCode code, string text, string said) = await Run("processes", path);
            Assert.True(code == InterCatExitCode.Success, said);
            Assert.Contains("  " + ProcessBindingText.Explain(later) + Environment.NewLine, text, StringComparison.Ordinal);
            Assert.Contains("  --evidence-policy IncludeCandidates counts them." + Environment.NewLine, text, StringComparison.Ordinal);
            Assert.DoesNotContain(ProcessBindingText.Explain(first), text, StringComparison.Ordinal);

            // Asked about the PID, each instance says how it was counted.
            (code, text, said) = await Run("processes", path, "--pid", "100");
            Assert.True(code == InterCatExitCode.Success, said);
            Assert.Matches("Counted +" + Regex.Escape(ProcessBindingText.Explain(first)) + "\\r?\\n", text);
            Assert.Matches("Counted +" + Regex.Escape(ProcessBindingText.Explain(later) + " --evidence-policy IncludeCandidates counts them."), text);

            // Its document carries the facts behind the words, under either policy.
            (long, int, long, long, string) Counted(string json, int epoch)
            {
                using JsonDocument answer = JsonDocument.Parse(json);
                JsonElement item = answer.RootElement.GetProperty("instances").EnumerateArray()
                    .Single(instance => instance.GetProperty("process").GetProperty("lifecycleEpoch").GetInt32() == epoch);
                return (item.GetProperty("records").GetInt64(), item.GetProperty("pidHolders").GetInt32(), item.GetProperty("candidateRecords").GetInt64(),
                    item.GetProperty("withheldRecords").GetInt64(), item.GetProperty("binding").GetString()!);
            }

            string withheld = (await Run("processes", path, "--json")).Output;
            Assert.Equal((1L, 2, 2L, 2L, ProcessBindingText.Explain(later)), Counted(withheld, 2));
            Assert.Equal((2L, 2, 0L, 0L, ProcessBindingText.Explain(first)), Counted(withheld, 1));
            ProcessNode counted = Holder(2, EvidencePolicy.IncludeCandidates);
            Assert.Contains("The evidence policy counts candidates, so its total includes the 2 records bound to it over the session.",
                ProcessBindingText.Explain(counted), StringComparison.Ordinal);
            Assert.Equal((3L, 2, 2L, 0L, ProcessBindingText.Explain(counted)),
                Counted((await Run("processes", path, "--evidence-policy", "include-candidates", "--json")).Output, 2));
            Assert.DoesNotContain("counts them.", (await Run("processes", path, "--evidence-policy", "include-candidates")).Output,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact(DisplayName = "R18: icat channels says how each channel was paired - its rule, strength, lifetime and coverage - in the inspector's words")]
    public async Task ChannelsSayHowEachWasPaired()
    {
        // The fixture's one paired channel: a send and its receipt, neither end seen opening or closing it.
        Channel channel = Assert.Single(SessionChannelQuery.Read(session.Store).Channels);
        string paired = ChannelPairingText.Explain(channel);
        Assert.Equal($"Each end's records bind to one process and name the other end: paired by {ProcessBindingText.Rule(channel.Rule!.Value)}, "
            + "correlated. Open before the capture and after it. Coverage over the session: unknown.", paired);
        (InterCatExitCode code, string text, string said) = await Run("channels", session.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains($"  {channel.Key} · {channel.Name} · 2 observed records{Environment.NewLine}    {paired}{Environment.NewLine}", text,
            StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R18: icat demo writes the demo saying it was generated, as icat session then says of each of its sessions")]
    public async Task IcatDemoSaysItWasGenerated()
    {
        string folder = Path.Combine(Path.GetTempPath(), "intercat-demo-cli-" + Guid.NewGuid().ToString("N"));
        string answered = folder + "-json";
        try
        {
            // It says what it is before it writes, and where everything went after.
            (InterCatExitCode code, string output, string said) = await Run("demo", folder);
            Assert.True(code == InterCatExitCode.Success, said);
            Assert.StartsWith("… " + DemoInvestigation.Disclosure, said, StringComparison.Ordinal);
            Assert.Contains(DemoInvestigation.Disclosure, output, StringComparison.Ordinal);
            string investigation = Path.Combine(folder, DemoInvestigation.WorkspaceFileName);
            Assert.Matches($@"(?m)^  Investigation +{Regex.Escape(investigation)}\r?$", output);

            // Each of its sessions says so wherever icat reads it.
            foreach (string session in new[] { "demo-client", "demo-server" })
            {
                (code, output, said) = await Run("session", Path.Combine(folder, session));
                Assert.True(code == InterCatExitCode.Success, said);
                Assert.Contains(DemoInvestigation.Disclosure, output, StringComparison.Ordinal);
            }

            // Its investigation says so on a line of its own, apart from the note the demo writes in it.
            (code, output, said) = await Run("workspace", "show", investigation);
            Assert.True(code == InterCatExitCode.Success, said);
            Assert.Matches($@"(?m)^  {Regex.Escape(DemoInvestigation.Disclosure)}\r?$", output);
            using (JsonDocument shown = JsonDocument.Parse((await Run("workspace", "show", investigation, "--json")).Output))
            {
                Assert.All(shown.RootElement.GetProperty("members").EnumerateArray(), member =>
                    Assert.True(member.GetProperty("demo").GetBoolean()));
            }

            // A folder that holds anything is refused, with nothing written.
            (code, _, said) = await Run("demo", folder);
            Assert.Equal(InterCatExitCode.InvalidInvocation, code);
            Assert.Contains("only into a new or empty folder", said, StringComparison.Ordinal);

            // As JSON, one object naming its contract, the investigation and its two sessions, and what it is.
            (code, output, said) = await Run("demo", answered, "--json");
            Assert.True(code == InterCatExitCode.Success, said);
            using JsonDocument document = JsonDocument.Parse(output);
            JsonElement root = document.RootElement;
            Assert.Equal((DemoInvestigation.SourceIdentity, DemoInvestigation.Disclosure),
                (root.GetProperty("contract").GetString(), root.GetProperty("disclosure").GetString()));
            Assert.All(root.GetProperty("sessions").EnumerateArray(), session => Assert.True(Directory.Exists(session.GetString())));
            Assert.True(File.Exists(root.GetProperty("investigation").GetString()));
        }
        finally
        {
            foreach (string written in new[] { folder, answered })
            {
                if (Directory.Exists(written)) Directory.Delete(written, recursive: true);
            }
        }
    }

    [Fact(DisplayName = "R18: icat session states a session's size on disk, bytes per record and tier in the window's words")]
    public async Task IcatSessionStatesItsSizeAsTheWindowDoes()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            Lifecycle(1, ObservationKind.Create, 100, 1) with { SessionRelativeTicks = 100 },
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 10) with { SessionRelativeTicks = 1_000 },
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 100, 11) with { SessionRelativeTicks = 2_000 },
        ]);

        // What the window states of the generation it shows.
        SessionSize shown = SessionOverviewProjector.Project(session.Store).Size!;
        session.Store.ReleaseSegmentReaders();
        Assert.Equal(3, shown.Records);

        (InterCatExitCode code, string output, string said) = await Run("session", session.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches($@"(?m)^  On disk +{Regex.Escape(SessionGrowth.Describe(shown))}\r?$", output);

        (code, output, said) = await Run("session", session.Path, "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement size = document.RootElement.GetProperty("size");
        Assert.Equal((shown.Bytes, shown.Files, shown.JournalBytes, shown.Records, shown.BytesPerRecord!.Value),
            (size.GetProperty("bytes").GetInt64(), size.GetProperty("files").GetInt32(), size.GetProperty("journalBytes").GetInt64(),
                size.GetProperty("records").GetInt64(), size.GetProperty("bytesPerRecord").GetInt64()));
        Assert.Equal(("Interactive", SessionGrowth.Describe(shown)),
            (size.GetProperty("tier").GetString(), size.GetProperty("statement").GetString()));
    }

    [Fact(DisplayName = "§19.5: icat session names the processes that collected a capture by role, PID and creation time, and nothing of a session naming none")]
    public async Task IcatSessionNamesItsCollectors()
    {
        using var session = new TemporarySession();
        DateTimeOffset created = new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero).AddTicks(3);
        Publish(session.Store, [Lifecycle(1, ObservationKind.Create, 100, 1) with { SessionRelativeTicks = 100 }],
            collectors: new CollectorIdentitiesV1
            {
                Contract = CollectorIdentitiesV1.ContractName,
                CaptureId = TestSessions.Capture.Value,
                Processes =
                [
                    new() { Role = CollectorRole.Broker, ProcessId = 4_120, CreatedUtc = created },
                    new() { Role = CollectorRole.Client, ProcessId = 7_008 },
                ],
            });
        session.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string output, string said) = await Run("session", session.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^COLLECTED BY\r?$", output);
        Assert.Matches(@"(?m)^  Broker +PID 4120, created 2026-10-07 09:00:00\.0000003 UTC\r?$", output);
        Assert.Matches(@"(?m)^  Its client +PID 7008, its creation time unread, so no instance of the capture can be shown to be it\r?$",
            output);

        (code, output, said) = await Run("session", session.Path, "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        using (JsonDocument document = JsonDocument.Parse(output))
        {
            JsonElement processes = document.RootElement.GetProperty("collectors").GetProperty("processes");
            Assert.Equal(("Broker", 4_120, created), (processes[0].GetProperty("role").GetString(),
                processes[0].GetProperty("processId").GetInt32(), processes[0].GetProperty("createdUtc").GetDateTimeOffset()));
            Assert.Equal(JsonValueKind.Null, processes[1].GetProperty("createdUtc").ValueKind);
        }

        // A session that names none says nothing of them, rather than that it had none.
        using var plain = new TemporarySession();
        Publish(plain.Store, [Lifecycle(1, ObservationKind.Create, 100, 1) with { SessionRelativeTicks = 100 }]);
        plain.Store.ReleaseSegmentReaders();
        (code, output, said) = await Run("session", plain.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.DoesNotContain("COLLECTED BY", output, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§19.5: icat processes labels InterCat's broker in the window's words and names its role in its document, and no other process")]
    public async Task IcatProcessesLabelsTheCollector()
    {
        using var session = new TemporarySession();
        PublishCollected(session.Store);
        ProcessNode broker = SessionOverviewProjector.Project(session.Store).Nodes.Single(node => node.Collector is not null);
        session.Store.ReleaseSegmentReaders();

        // The list names it beside its executable, and asked about its PID, it says how it was counted.
        (InterCatExitCode code, string text, string said) = await Run("processes", session.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("intercat-broker.exe (InterCat's broker)", text, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(text, "InterCat's broker"));
        (code, text, said) = await Run("processes", session.Path, "--pid", "4120");
        Assert.True(code == InterCatExitCode.Success, said);
        IReadOnlyList<CollectorProcessV1> unfound = SessionOverviewProjector.Project(session.Store).Collectors.Unfound;
        session.Store.ReleaseSegmentReaders();
        Assert.Matches("Counted +" + Regex.Escape(ProcessBindingText.Explain(broker, unfound)), text);

        (code, text, said) = await Run("processes", session.Path, "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        using JsonDocument document = JsonDocument.Parse(text);
        JsonElement[] instances = [.. document.RootElement.GetProperty("instances").EnumerateArray()];
        JsonElement labelled = Assert.Single(instances, instance => instance.GetProperty("collector").ValueKind != JsonValueKind.Null);
        Assert.Equal(("Broker", 4120, 1, ProcessBindingText.Explain(broker, unfound)), (labelled.GetProperty("collector").GetString(),
            labelled.GetProperty("process").GetProperty("processId").GetInt32(),
            labelled.GetProperty("process").GetProperty("lifecycleEpoch").GetInt32(), labelled.GetProperty("binding").GetString()));

        // The overview's document, the bundle the window projects, carries the same: which instance each collector is,
        // and the collectors no instance is.
        (code, text, said) = await Run("overview", session.Path, "--json");
        Assert.True(code == InterCatExitCode.Success, said);
        using JsonDocument overview = JsonDocument.Parse(text);
        JsonElement bundle = overview.RootElement.GetProperty("bundle");
        JsonElement node = Assert.Single(bundle.GetProperty("nodes").EnumerateArray(),
            candidate => candidate.GetProperty("collector").ValueKind != JsonValueKind.Null);
        Assert.Equal(("Broker", broker.Id.Value), (node.GetProperty("collector").GetString(),
            node.GetProperty("id").GetProperty("value").GetGuid()));
        JsonElement collectors = bundle.GetProperty("collectors");
        Assert.True(collectors.GetProperty("recorded").GetBoolean());
        JsonElement instance = Assert.Single(collectors.GetProperty("instances").EnumerateArray());
        Assert.Equal((broker.Id.Value, "Broker"), (instance.GetProperty("instance").GetProperty("value").GetGuid(),
            instance.GetProperty("role").GetString()));
        Assert.Equal([7008, 4120], collectors.GetProperty("unfound").EnumerateArray()
            .Select(collector => collector.GetProperty("processId").GetInt32()));
    }

    [Fact(DisplayName = "§19.5: icat overview names each collector by the instance it is or why none is, and icat processes says it of a process holding its PID")]
    public async Task IcatOverviewNamesItsCollectors()
    {
        using var session = new TemporarySession();
        PublishCollected(session.Store);
        IReadOnlyList<CollectorProcessV1> unfound = SessionOverviewProjector.Project(session.Store).Collectors.Unfound;
        session.Store.ReleaseSegmentReaders();

        // Each collector by the instance it is, or by its PID and why no instance is it, in icat session's role names.
        (InterCatExitCode code, string text, string said) = await Run("overview", session.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("COLLECTED BY", text, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"\n  Broker +intercat-broker\.exe · PID 4120 #1\r?\n"), text);
        Assert.Matches(new Regex(@"\n  Its client +PID 7008: its creation time was not read, so no process can be shown to be it\r?\n"), text);
        Assert.Matches(new Regex(@"\n  Recorder +PID 4120: no lifecycle record of PID 4120 carries the creation time it names, so no process here is it\r?\n"), text);
        Assert.Contains("intercat-broker.exe · PID 4120 #1 (InterCat's broker)", text, StringComparison.Ordinal);

        // The process holding the client's PID is explained as the inspector explains it: not taken for the client.
        (code, text, said) = await Run("processes", session.Path, "--pid", "7008");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains(CollectorText.Unfound(unfound[0]), text, StringComparison.Ordinal);

        // A capture that names no collector says nothing of them.
        using var plain = new TemporarySession();
        PublishCollected(plain.Store, named: false);
        plain.Store.ReleaseSegmentReaders();
        (code, text, said) = await Run("overview", plain.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.DoesNotContain("COLLECTED BY", text, StringComparison.Ordinal);
        Assert.DoesNotContain("(InterCat's broker)", text, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§19.5: icat export --set-aside-collectors sets InterCat's own processes aside from the rows, as the window does, and says what it set aside")]
    public async Task AnExportSetsInterCatsOwnAside()
    {
        using var session = new TemporarySession();
        PublishCollected(session.Store);
        session.Store.ReleaseSegmentReaders();
        using var unnamed = new TemporarySession();
        PublishCollected(unnamed.Store, named: false);
        unnamed.Store.ReleaseSegmentReaders();
        string output = Path.Combine(Path.GetDirectoryName(session.Path)!, Guid.NewGuid().ToString("N") + ".json");
        try
        {
            // The broker's group is no row, the file counts what was set aside, and the report says so in the window's words.
            (InterCatExitCode exported, string report, string said) =
                await Run("export", session.Path, "--output", output, "--set-aside-collectors");
            Assert.True(exported == InterCatExitCode.Success, said);
            Assert.Contains("  " + CollectorText.SetAsideCaveat(1, 3), report, StringComparison.Ordinal);
            Assert.Matches(new Regex(@"\n  Rows +2\r?\n"), report);
            using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(output)))
            {
                Assert.Equal(["InterCat.exe", "tool.exe"], document.RootElement.GetProperty("rows").EnumerateArray()
                    .Select(row => row.GetProperty("label").GetString()).Order(StringComparer.Ordinal));
                Assert.Equal(1, document.RootElement.GetProperty("setAside").GetProperty("processes").GetInt32());
            }

            // Without the flag the broker is a row as any process is; and its redacted report states the counts alone.
            (exported, report, said) = await Run("export", session.Path, "--output", output, "--overwrite");
            Assert.True(exported == InterCatExitCode.Success, said);
            Assert.DoesNotContain("InterCat's own", report, StringComparison.Ordinal);
            Assert.Matches(new Regex(@"\n  Rows +3\r?\n"), report);
            (exported, report, said) = await Run("export", session.Path, "--output", output, "--overwrite",
                "--set-aside-collectors", "--share-redacted");
            Assert.True(exported == InterCatExitCode.Success, said);
            Assert.Contains("  " + CollectorText.SetAsideCaveat(1, 3), report, StringComparison.Ordinal);

            // An evidence export lists every record, InterCat's own included, and says that it does.
            (exported, report, said) = await Run("export", session.Path, "--output", output, "--overwrite",
                "--set-aside-collectors", "--evidence");
            Assert.True(exported == InterCatExitCode.Success, said);
            Assert.Matches(new Regex(@"\n  Records +6\r?\n"), report);
            Assert.Contains("an evidence export lists every record of its scope, InterCat's own included", report,
                StringComparison.Ordinal);

            // A capture that names no collector has nothing to set aside, and says so rather than leave it unsaid.
            (exported, report, said) = await Run("export", unnamed.Path, "--output", output, "--overwrite",
                "--set-aside-collectors");
            Assert.True(exported == InterCatExitCode.Success, said);
            Assert.Contains("No process of this session is known to be InterCat's own, so none was set aside", report,
                StringComparison.Ordinal);
            Assert.Matches(new Regex(@"\n  Rows +3\r?\n"), report);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact(DisplayName = "P16: icat support writes versions, capabilities and each session's counters, and no name, endpoint, host or path")]
    public async Task ASupportBundleHoldsNoRecordsContent()
    {
        string folder = Path.Combine(Path.GetTempPath(), "intercat-support-" + Guid.NewGuid().ToString("N"));
        try
        {
            // A session whose records name a person's folder, a tool, two endpoints and a host, all of which a bundle leaves out.
            CoverageLedgerV1 ledger = TestSessions.TransportLedger(tcp: true, udp: false, lost: 3);
            string held = Held(folder, "case-session", "alice-laptop",
            [
                Lifecycle(1, ObservationKind.Create, 4_242, 1) with
                {
                    ResourceName = @"C:\Users\alice\Secret Tools\leaky-tool.exe", SessionRelativeTicks = 100,
                },
                Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 300, 4_242, 10)
                    .Between("10.11.12.13:40404", "10.99.98.97:50505") with { SessionRelativeTicks = 1_000 },
                Transfer(11, ObservationKind.Send, AccountingSide.SendSide, null, 4_242, 11)
                    .Between("10.11.12.13:40404", "10.99.98.97:50505") with { SessionRelativeTicks = 1_100 },
            ], ledger);
            string bundle = Path.Combine(folder, "bundle.json");
            (InterCatExitCode code, string text, string said) = await Run("support", held, "--output", bundle);
            Assert.True(code == InterCatExitCode.Success, said);

            // What it holds and leaves out is listed first, and the bundle says the same.
            Assert.Contains("Holds: InterCat's version", text, StringComparison.Ordinal);
            Assert.Contains("Leaves out: message content and payload bytes; endpoint addresses and ports; command lines;", text,
                StringComparison.Ordinal);
            string written = File.ReadAllText(bundle);
            using JsonDocument document = JsonDocument.Parse(written);
            JsonElement root = document.RootElement;
            Assert.Equal(SupportBundle.Contract, root.GetProperty("contract").GetString());
            Assert.Equal(SupportBundle.ProductVersion, root.GetProperty("version").GetString());
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("runtime").GetProperty("operatingSystem").GetString()));
            Assert.Equal("2", root.GetProperty("capabilities").GetProperty("reportVersion").GetString());
            Assert.Equal(SupportBundle.LeftOut, root.GetProperty("leftOut").EnumerateArray().Select(entry => entry.GetString()));

            // The session by its folder's name: its rows by mechanism, with what the capture covered of each, its ledger's
            // loss, its clock and its files.
            JsonElement session = root.GetProperty("sessions").EnumerateArray().Single();
            Assert.Equal("case-session", session.GetProperty("folder").GetString());
            Assert.Equal(JsonValueKind.Null, session.GetProperty("problem").ValueKind);
            Assert.Equal(3, session.GetProperty("rows").GetInt64());
            Dictionary<string, (long Rows, string Coverage)> mechanisms = session.GetProperty("mechanisms").EnumerateArray()
                .ToDictionary(entry => entry.GetProperty("mechanism").GetString()!,
                    entry => (entry.GetProperty("rows").GetInt64(), entry.GetProperty("coverage").GetString()!));
            (Mechanism mechanism, CoverageState state, string _) tcp = SessionCoverage.ByMechanism(ledger)
                .Select(entry => (entry.Mechanism, entry.State, entry.Reason)).Single(entry => entry.Mechanism == Mechanism.Tcp);
            Assert.Equal((2L, CoverageStateText.Value(tcp.state)), mechanisms[MechanismText.Name(Mechanism.Tcp)]);
            Assert.Equal(1L, mechanisms[MechanismText.Name(Mechanism.ProcessLifecycle)].Rows);
            JsonElement loss = session.GetProperty("ledger").GetProperty("epochs")[0].GetProperty("losses")[0];
            Assert.Equal(3, loss.GetProperty("lost").GetInt64());
            Assert.Equal(JsonValueKind.String, loss.GetProperty("layer").ValueKind);
            Assert.Equal(10_000_000, session.GetProperty("clock").GetProperty("ticksPerSecond").GetInt64());
            Assert.Contains(session.GetProperty("dependencies").EnumerateArray(),
                dependency => dependency.GetProperty("kind").GetString() == "Segment");

            // Nothing a record holds, nor the host's name or the folders above the session.
            foreach (string needle in new[] { "alice", "leaky-tool", "Secret Tools", "10.11.12.13", "40404", "10.99.98.97", "50505", folder })
            {
                Assert.DoesNotContain(needle, written, StringComparison.OrdinalIgnoreCase);
            }

            // Printed, the bundle is the answer on stdout, and its listing goes to stderr beside it.
            (InterCatExitCode printed, string answer, string beside) = await Run("support", held, "--json");
            Assert.Equal(InterCatExitCode.Success, printed);
            Assert.Equal(SupportBundle.Contract, JsonDocument.Parse(answer).RootElement.GetProperty("contract").GetString());
            Assert.Contains("Holds: InterCat's version", beside, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact(DisplayName = "P16: icat support lists what a bundle holds before writing it, and says a session it cannot read by its folder")]
    public async Task ASupportBundleIsListedFirstAndSaysWhatItCannotRead()
    {
        string folder = Path.Combine(Path.GetTempPath(), "intercat-support-" + Guid.NewGuid().ToString("N"));
        try
        {
            // --check lists, reads nothing and writes nothing.
            string bundle = Path.Combine(folder, "bundle.json");
            (InterCatExitCode checkedCode, string listed, string quiet) = await Run("support", session.Path, "--check");
            Assert.Equal(InterCatExitCode.Success, checkedCode);
            Assert.Empty(quiet);
            Assert.DoesNotContain(Path.GetFileName(session.Path), listed, StringComparison.Ordinal);
            Assert.Contains("Holds: InterCat's version, and this machine's operating system, architecture, runtime and processor "
                + "count; this machine's capability report", listed, StringComparison.Ordinal);
            Assert.Contains("1 session: its folder's name, generation and files with their sizes", listed, StringComparison.Ordinal);
            Assert.Contains("Leaves out: " + string.Join("; ", SupportBundle.LeftOut) + ".", listed, StringComparison.Ordinal);
            Assert.False(File.Exists(bundle));

            // A bundle must be told where to go, once, and replaces nothing unless told to; a session must be a folder.
            string[][] refused =
            [
                ["support", session.Path],
                ["support", session.Path, "--check", "--json"],
                ["support", session.Path, "--check", "--output", bundle],
                ["support", Path.Combine(folder, "nowhere"), "--json"],
            ];
            foreach (string[] args in refused)
            {
                (InterCatExitCode code, string output, string error) = await Run(args);
                Assert.True(code == InterCatExitCode.InvalidInvocation, $"icat {string.Join(' ', args)} exited {code}.");
                Assert.Empty(output);
                Assert.StartsWith("x ", error, StringComparison.Ordinal);
            }

            Directory.CreateDirectory(folder);
            File.WriteAllText(bundle, "kept");
            Assert.Equal(InterCatExitCode.InvalidInvocation, (await Run("support", "--output", bundle)).Code);
            Assert.Equal("kept", File.ReadAllText(bundle));
            Assert.Equal(InterCatExitCode.Success, (await Run("support", "--output", bundle, "--overwrite")).Code);
            Assert.Empty(JsonDocument.Parse(File.ReadAllText(bundle)).RootElement.GetProperty("sessions").EnumerateArray());

            // A folder that holds no session is said to hold none, by its name, and the bundle is still made.
            string empty = Directory.CreateDirectory(Path.Combine(folder, "not-a-session")).FullName;
            (InterCatExitCode partial, string answer, _) = await Run("support", empty, session.Path, "--json");
            Assert.Equal(InterCatExitCode.PartialResultSuccess, partial);
            JsonElement[] sessions = [.. JsonDocument.Parse(answer).RootElement.GetProperty("sessions").EnumerateArray()];
            Assert.Equal(("not-a-session", SessionStore.NoGeneration),
                (sessions[0].GetProperty("folder").GetString(), sessions[0].GetProperty("problem").GetString()));
            Assert.Equal(JsonValueKind.Null, sessions[1].GetProperty("problem").ValueKind);
            Assert.DoesNotContain(folder, answer, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    /// <summary>A session of one host whose rows these are, in a folder of its own beneath <paramref name="folder"/>.</summary>
    private static string Held(string folder, string name, string host, ObservationRowV1[] rows, CoverageLedgerV1? coverage = null)
    {
        string directory = Directory.CreateDirectory(Path.Combine(folder, name)).FullName;
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory), Guid.NewGuid(), "command-line-tests");
        Publish(store, rows, capture: CaptureId.New(), clock: ClockFor(ClockId.New(), host), coverage: coverage);
        store.ReleaseSegmentReaders();
        return directory;
    }

    [Fact(DisplayName = "R22: icat workspace show states what each session's layout keeps, its pins, pinned lanes, grouping, ranking, evidence policy and lane scale, and the window's panes")]
    public async Task WorkspaceShowStatesEachLayout()
    {
        string workspace = Path.Combine(Path.GetDirectoryName(session.Path)!, Guid.NewGuid().ToString("N") + ".icat-workspace");
        try
        {
            Assert.Equal(InterCatExitCode.Success, (await Run("workspace", "new", workspace)).Code);
            Assert.Equal(InterCatExitCode.Success, (await Run("workspace", "add", workspace, session.Path)).Code);
            Guid lane = Guid.Parse("a1b2c3d4-0000-4000-8000-000000000001");
            InvestigationWorkspace.SetLayout(workspace, TestSessions.Session, [new WorkspacePin { Key = "group:a", X = 0.5, Y = 0.5 }],
                DateTimeOffset.UtcNow, RankingMetric.BytesSent, perSecond: true, pinnedLanes: [lane], grouping: LaneGrouping.UserSession);

            (InterCatExitCode shown, string text, string said) = await Run("workspace", "show", workspace);
            Assert.True(shown == InterCatExitCode.Success, said);
            Assert.Contains("Session " + TestSessions.Session.ToString("N")[..8] + ": 1 node pinned on its graph, 1 process lane pinned "
                + "on its timeline, its processes grouped by terminal session and its rows ranked by bytes sent per second, put back "
                + "when it is opened from this investigation.", text, StringComparison.Ordinal);
            string json = (await Run("workspace", "show", workspace, "--json")).Output;
            Assert.Contains($"\"contract\": \"{WorkspaceCommand.ResolutionContract}\"", json, StringComparison.Ordinal);
            Assert.Equal("workspace-resolution-v22", WorkspaceCommand.ResolutionContract);
            Assert.Contains("\"collectorsAside\": false", json, StringComparison.Ordinal);
            Assert.Contains("\"wallClock\": false", json, StringComparison.Ordinal);
            Assert.Matches(new Regex($"\"pinnedLanes\": \\[\\s*\"{lane}\"\\s*\\]"), json);
            Assert.Contains("\"grouping\": \"UserSession\"", json, StringComparison.Ordinal);
            Assert.Contains("\"rankBy\": \"BytesSent\"", json, StringComparison.Ordinal);
            Assert.Contains("\"evidencePolicy\": null", json, StringComparison.Ordinal);
            Assert.Contains("\"panes\": null", json, StringComparison.Ordinal);
            Assert.DoesNotContain("The window:", text, StringComparison.Ordinal);

            // A layout that counts a reused PID's candidates, and reads each lane on its own scale, says so too, and keeps
            // both in its document.
            InvestigationWorkspace.SetLayout(workspace, TestSessions.Session, [], DateTimeOffset.UtcNow,
                evidencePolicy: EvidencePolicy.IncludeCandidates, scalesEachLane: true);
            Assert.Contains("Session " + TestSessions.Session.ToString("N")[..8] + ": its records counted with candidates and each "
                + "of its timeline lanes on its own scale, put back when it is opened from this investigation.",
                (await Run("workspace", "show", workspace)).Output, StringComparison.Ordinal);
            Assert.Contains("\"scalesEachLane\": true", (await Run("workspace", "show", workspace, "--json")).Output,
                StringComparison.Ordinal);
            Assert.Contains("\"evidencePolicy\": \"IncludeCandidates\"", (await Run("workspace", "show", workspace, "--json")).Output,
                StringComparison.Ordinal);

            // The window's panes, kept once for every session, are said before any session's layout, and kept in the document.
            InvestigationWorkspace.SetPanes(workspace, 0.3712, WorkspacePane.Timeline, DateTimeOffset.UtcNow);
            string panes = (await Run("workspace", "show", workspace)).Output;
            Assert.Contains("The window: the timeline filling the column and the graph at "
                + 0.37.ToString("P0", System.Globalization.CultureInfo.CurrentCulture) + " of the panes' height when both are shown, "
                + "put back when any session is opened from this investigation.", panes, StringComparison.Ordinal);
            Assert.True(panes.IndexOf("The window:", StringComparison.Ordinal)
                < panes.IndexOf("Session " + TestSessions.Session.ToString("N")[..8] + ": its records", StringComparison.Ordinal));
            json = (await Run("workspace", "show", workspace, "--json")).Output;
            Assert.Contains("\"graphShare\": 0.3712", json, StringComparison.Ordinal);
            Assert.Contains("\"expanded\": \"Timeline\"", json, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(workspace);
        }
    }

    [Fact(DisplayName = "R18: icat evidence --from lists the rows from a moment on, placed as the window's search places it, on the wall clock or in session time")]
    public async Task EvidenceStartsFromAMoment()
    {
        // Three UDP sends 10, 20 and 30 µs into a capture whose wall clock read noon 100 µs before its epoch's reading.
        var clock = new SourceClockDescriptor(TestSessions.Clock, HostId.Derive("cli-at"), SourceClockKind.Monotonic,
            TimestampEncoding.Qpc, 10_000_000, 1_000, TimestampRounding.NearestEven, SourceClockMath.SessionTicksPerSecond * 60);
        DateTimeOffset noon = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        using var calibrated = new TemporarySession();
        Publish(calibrated.Store,
        [
            .. Enumerable.Range(1, 3).Select(index => Transfer(1_000L + (index * 100), ObservationKind.Send,
                AccountingSide.SendSide, 10, 100, (ulong)index).Between("192.168.1.5:61000", "8.8.8.8:53") with
            {
                Mechanism = Mechanism.Udp,
                SessionRelativeTicks = index * 10_000L,
            }),
        ],
            clock: clock,
            calibration: new ClockCalibrationV1
            {
                Contract = ClockCalibrationV1.ContractName,
                CaptureId = TestSessions.Capture.Value,
                ClockId = clock.Id.Value,
                WallClock = "test-wall-clock",
                Samples = [new() { NativeTicks = 1_000, Utc = noon, AcquisitionUncertaintyNanoseconds = 200 }],
            });
        calibrated.Store.ReleaseSegmentReaders();
        System.Globalization.CultureInfo culture = System.Globalization.CultureInfo.CurrentCulture;
        var extent = new TimeRange(100, 301);
        SessionClock wall = SessionClock.Wall(SessionRecording.WallClock(calibrated.Store)!, TimeZoneInfo.Local, extent);
        calibrated.Store.ReleaseSegmentReaders();
        string said = wall.Moment(20_000, culture) + " · session time "
            + SessionClock.Session(TimeZoneInfo.Local).Record(20_000, culture);

        // The time of day 20 µs in, as this computer's zone writes it, lists the second and third sends and says where it falls.
        string typed = TimeZoneInfo.ConvertTime(noon.AddTicks(200), TimeZoneInfo.Local).ToString("HH:mm:ss.fffffff",
            System.Globalization.CultureInfo.InvariantCulture);
        (InterCatExitCode code, string text, string why) = await Run("evidence", calibrated.Path, "--from", typed);
        Assert.True(code == InterCatExitCode.Success, why);
        Assert.Matches(new Regex(@"^\s*From\s+" + Regex.Escape(said) + @"\r?$", RegexOptions.Multiline), text);
        Assert.Matches(new Regex(@"^\s*Rows on page\s+2\r?$", RegexOptions.Multiline), text);
        Assert.Matches(new Regex(@"^\s*Session-time interval\s+\[200, 301\) · 100 ns ticks\r?$", RegexOptions.Multiline), text);

        // Session time places it alike, and --json carries the instant in ticks and as the wall clock read it.
        (code, text, why) = await Run("evidence", calibrated.Path, "--from", "0.00002 s", "--json");
        Assert.True(code == InterCatExitCode.Success, why);
        using (JsonDocument page = JsonDocument.Parse(text))
        {
            Assert.Equal("evidence-page-v5", page.RootElement.GetProperty("contract").GetString());
            Assert.Equal(200, page.RootElement.GetProperty("from").GetProperty("ticks").GetInt64());
            Assert.Equal(noon.AddTicks(200), page.RootElement.GetProperty("from").GetProperty("utc").GetDateTimeOffset());
            Assert.Equal(2, page.RootElement.GetProperty("page").GetProperty("records").GetArrayLength());
        }

        // Within an interval asked for, it lists from the later of it and the interval's start to the interval's end, and
        // at or after that end it is refused.
        (code, text, why) = await Run("evidence", calibrated.Path, "--from", "0.00002 s", "--interval", "0:250");
        Assert.True(code == InterCatExitCode.Success, why);
        Assert.Matches(new Regex(@"^\s*Session-time interval\s+\[200, 250\) · 100 ns ticks\r?$", RegexOptions.Multiline), text);
        Assert.Matches(new Regex(@"^\s*Rows on page\s+1\r?$", RegexOptions.Multiline), text);
        (code, text, why) = await Run("evidence", calibrated.Path, "--from", "0.00001 s", "--interval", "250:400");
        Assert.True(code == InterCatExitCode.Success, why);
        Assert.Matches(new Regex(@"^\s*Session-time interval\s+\[250, 400\) · 100 ns ticks\r?$", RegexOptions.Multiline), text);
        (code, _, why) = await Run("evidence", calibrated.Path, "--from", "0.00002 s", "--interval", "0:200");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("0.00002 s comes after --interval's end, so the interval holds no row from it on.", why, StringComparison.Ordinal);

        // A finished session's persisted overview holds the same extent, and the moment is placed alike.
        Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(calibrated.Store, noon.AddMinutes(1)).Outcome);
        calibrated.Store.ReleaseSegmentReaders();
        (code, text, why) = await Run("evidence", calibrated.Path, "--from", typed);
        Assert.True(code == InterCatExitCode.Success, why);
        Assert.Matches(new Regex(@"^\s*Session-time interval\s+\[200, 301\) · 100 ns ticks\r?$", RegexOptions.Multiline), text);

        // A moment the session cannot place is said as the window's search says it; text that is no moment is named.
        (code, _, why) = await Run("evidence", calibrated.Path, "--from", "1 s");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("1 s is outside this session, which runs ", why, StringComparison.Ordinal);
        (code, _, why) = await Run("evidence", session.Path, "--from", "12:00");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("This session recorded no wall clock, so 12:00 places nothing in it: type a session time, such as 312.5 s.",
            why, StringComparison.Ordinal);
        (code, _, why) = await Run("evidence", calibrated.Path, "--from", "client.exe");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("--from takes a moment: ", why, StringComparison.Ordinal);
        Assert.Contains("'client.exe' is neither.", why, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§6.2: icat timeline states the time base its intervals count in, as the window's axis does")]
    public async Task TimelineStatesItsTimeBase()
    {
        // The shared session recorded no wall clock: its intervals are session time, and no moment is guessed.
        (InterCatExitCode code, string text, string said) = await Run("timeline", session.Path, "--interval", "0:1000");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(new Regex(@"^\s*Time base\s+session time\r?$", RegexOptions.Multiline), text);

        // A capture that recorded its wall clock says since when its session time counts, in the reader's zone with its
        // offset from UTC, in the window's words.
        var clock = new SourceClockDescriptor(TestSessions.Clock, HostId.Derive("cli-time-base"), SourceClockKind.Monotonic,
            TimestampEncoding.Qpc, 10_000_000, 1_000, TimestampRounding.NearestEven, SourceClockMath.SessionTicksPerSecond * 60);
        DateTimeOffset noon = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        using var calibrated = new TemporarySession();
        Publish(calibrated.Store,
            [Transfer(1_100, ObservationKind.Send, AccountingSide.SendSide, 10, 100).Between("192.168.1.5:61000", "8.8.8.8:53") with
            {
                Mechanism = Mechanism.Udp,
                SessionRelativeTicks = 10_000,
            }],
            clock: clock,
            calibration: new ClockCalibrationV1
            {
                Contract = ClockCalibrationV1.ContractName,
                CaptureId = TestSessions.Capture.Value,
                ClockId = clock.Id.Value,
                WallClock = "test-wall-clock",
                Samples = [new() { NativeTicks = 1_500, Utc = noon.AddTicks(1_500), AcquisitionUncertaintyNanoseconds = 200 }],
            });
        calibrated.Store.ReleaseSegmentReaders();
        (code, text, said) = await Run("timeline", calibrated.Path, "--interval", "0:1000");
        Assert.True(code == InterCatExitCode.Success, said);
        string since = WorkspaceTime.TimeBase(noon.AddTicks(1_000), TimeZoneInfo.Local, System.Globalization.CultureInfo.CurrentCulture);
        Assert.StartsWith("session time since ", since, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"^\s*Time base\s+" + Regex.Escape(since) + @"\r?$", RegexOptions.Multiline), text);

        // --wall-clock reads it on the wall clock its capture's machine read, as the window's Wall clock does (R18): the
        // interval, the time base with its day and offset, each row's interval, and how the clock was placed.
        (code, text, said) = await Run("timeline", calibrated.Path, "--interval", "0:1000", "--wall-clock");
        Assert.True(code == InterCatExitCode.Success, said);
        var interval = new TimeRange(0, 1_000);
        SessionClock wall = SessionClock.Wall(SessionRecording.WallClock(calibrated.Store)!, TimeZoneInfo.Local, interval);
        calibrated.Store.ReleaseSegmentReaders();
        System.Globalization.CultureInfo culture = System.Globalization.CultureInfo.CurrentCulture;
        Assert.Matches(new Regex(@"^\s*Interval\s+" + Regex.Escape(wall.Range(interval, culture)) + @"\r?$", RegexOptions.Multiline), text);
        Assert.Matches(new Regex(@"^\s*Time base\s+" + Regex.Escape(wall.Base(interval, noon.AddTicks(1_000), culture)) + @"\r?$",
            RegexOptions.Multiline), text);
        Assert.StartsWith("wall clock on ", wall.Base(interval, null, culture), StringComparison.Ordinal);
        Assert.Contains(wall.Basis(culture), text, StringComparison.Ordinal);

        // A session that recorded none is refused with why, never read on a guessed clock.
        (code, _, said) = await Run("timeline", session.Path, "--interval", "0:1000", "--wall-clock");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains(SessionClock.NotRecorded, said, StringComparison.Ordinal);

        // icat evidence lists each record's time on it, dated, and names the offset and how the clock was placed.
        (code, text, said) = await Run("evidence", calibrated.Path, "--wall-clock");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(new Regex(@"^\s*Time base\s+wall clock, " + Regex.Escape(wall.OffsetsAt([100])) + @"\. ", RegexOptions.Multiline),
            text);
        Assert.Contains("  " + wall.Record(10_000, culture, dated: true) + " · UDP send", text, StringComparison.Ordinal);
        (code, _, said) = await Run("evidence", session.Path, "--wall-clock");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains(SessionClock.NotRecorded, said, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "§6.8: the command line says a count of one in the singular: one PID, one record, one record of neither side, one row without a peer")]
    public async Task ACountOfOneReadsInTheSingular()
    {
        // client.exe, the only process, connects once to another host and sends one datagram to a third: one PID, two
        // connections of one record each, one record that is neither a send nor a receive, and one TCP row with no peer.
        using var single = new TemporarySession();
        Publish(single.Store,
        [
            Lifecycle(10, ObservationKind.Create, 100, 1) with { ResourceName = @"C:\Tools\client.exe", SessionRelativeTicks = 1_000 },
            Transfer(20, ObservationKind.Connect, AccountingSide.EndpointActivity, 0, 100, 2)
                .Between("192.168.1.5:52000", "10.0.0.9:443") with { SessionRelativeTicks = 2_000 },
            Transfer(30, ObservationKind.Send, AccountingSide.SendSide, 40, 100, 3)
                .Between("192.168.1.5:61000", "8.8.8.8:53") with { Mechanism = Mechanism.Udp, SessionRelativeTicks = 3_000 },
        ]);
        single.Store.ReleaseSegmentReaders();
        ProcessInstanceId client = SessionOverviewProjector.Project(single.Store).Nodes.Single().Id;

        (InterCatExitCode code, string text, string said) = await Run("processes", single.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(new Regex(@"^\s*Instances\s+1 across 1 PID, never reused\r?$", RegexOptions.Multiline), text);

        (code, text, said) = await Run("channels", single.Path, "--process", client.ToString(), "--one-sided");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Equal(2, Regex.Count(text, " · 1 record · "));

        (code, text, said) = await Run("timeline", single.Path, "--interval", "0:1000", "--bytes");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("1 record states neither side and is in neither column.", text, StringComparison.Ordinal);

        (code, text, said) = await Run("overview", single.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Contains("1 TCP row has no admitted peer; 0 paired relationships were withheld", text, StringComparison.Ordinal);
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

    [Fact(DisplayName = "R5: icat metric names its basis, byte domain and accounting in the words the window uses, never by the enumeration")]
    public async Task AMetricNamesWhatItCountsInWords()
    {
        (InterCatExitCode code, string answer, string said) = await Run("metric", session.Path, "--metric", "bytes-sent",
            "--byte-domain", "TransportObserved", "--side", "SendSide");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  Basis +source observations\r?$", answer);
        Assert.Matches(@"(?m)^  Byte domain +carried by the transport\r?$", answer);
        Assert.Matches(@"(?m)^  Accounting +sender-accounted: each transfer is measured at its sending end\r?$", answer);

        // The records' sides, the one taken and the one left out, are named as the accounting is.
        Assert.Matches(@"(?m)^  Contributions of bytes carried by the transport, by the side each record measured:\r?$", answer);
        Assert.Matches(@"(?m)^  sender-accounted +yes +1 +0 +64 B\r?$", answer);
        Assert.Matches(@"(?m)^  receiver-accounted +no +1 +0 +64 B\r?$", answer);
        foreach (string name in new[] { "SourceObservations", "Source observations", "TransportObserved", "SendSide", "ReceiveSide" })
            Assert.DoesNotContain(name, answer, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R21: icat operations and exchanges say a duration none was timed for, and a server call none reached, never a dash")]
    public async Task AnUntimedDurationIsSaid()
    {
        // A server call of PID 2020 that never stopped, and an exchange of PID 4242 whose response end was not recorded.
        ObservationRowV1[] bodies = [Body(10, 1), Body(12, 2)];
        using var open = new TemporarySession();
        Publish(open.Store,
        [
            RpcCall(100, ObservationKind.RequestStart, Direction.Inbound, raisedBy: 2_020, 3,
                activity: Guid.Parse("21212121-2222-4333-8444-555555555555"), interfaceUuid: Guid.Parse("12345678-1234-4123-8123-123456789abc")),
            .. bodies,
        ], fields: [.. bodies.Select(row => Field(row, SourceField.HttpExchangeId, 4))]);
        open.Store.ReleaseSegmentReaders();

        // Its group's durations are untimed, and its other end is unknown for want of ALPC to follow.
        (InterCatExitCode code, string calls, string said) = await Run("operations", open.Path);
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  .*2020 +server +.* untimed +untimed +untimed +no ALPC\r?$", calls);

        // Where the capture collected ALPC to follow, a server group no client call reached says so instead.
        (ObservationRowV1[] linkedRows, SourceFieldRowV1[] linkedFields) = LinkedRpcCalls();
        using var linked = new TemporarySession();
        Publish(linked.Store,
        [
            .. linkedRows,
            RpcCall(400, ObservationKind.RequestStart, Direction.Inbound, raisedBy: 2_020, 70,
                activity: Guid.Parse("21212121-2222-4333-8444-555555555555"), interfaceUuid: Guid.Parse("12345678-1234-4123-8123-123456789abc")),
        ], fields: linkedFields);
        linked.Store.ReleaseSegmentReaders();
        Assert.Matches(@"(?m)^  .*2020 +server +.* untimed +untimed +untimed +not reached\r?$", (await Run("operations", linked.Path)).Output);

        // The exchange's median is untimed, and its process, whose image no record names, reads as a call's does.
        (code, string exchanges, said) = await Run("exchanges", open.Path, "--pid", "4242");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(@"(?m)^  executable not witnessed · 4242 .* untimed\r?$", exchanges);
        Assert.DoesNotMatch(@"(?m) - *\r?$", calls + exchanges);
    }

    [Fact(DisplayName = "R5: icat operations, exchanges and processes say why a group belongs to no process, in the window's words")]
    public async Task AnUnattributedGroupSaysWhyInWords()
    {
        // An RPC call raised by PID 1960 and an HTTP exchange of PID 4242, neither of whose processes a lifecycle record names.
        Guid activity = Guid.Parse("11111111-2222-4333-8444-555555555555");
        ObservationRowV1[] bodies = [Body(10, 1), Body(12, 2)];
        using var unbound = new TemporarySession();
        Publish(unbound.Store,
        [
            RpcCall(100, ObservationKind.RequestStart, Direction.Outbound, raisedBy: 1_960, 3, activity: activity),
            RpcCall(110, ObservationKind.RequestEnd, Direction.Outbound, raisedBy: 1_960, 4, activity: activity, status: 0),
            .. bodies,
        ], fields: [.. bodies.Select(row => Field(row, SourceField.HttpExchangeId, 4))]);
        unbound.Store.ReleaseSegmentReaders();

        // Direct evidence only admits a lifecycle record's own binding alone, so each group belongs to no process it admits,
        // and says so in the words the window's evidence rows use, never the enumeration's name.
        string said = BindingText.Reason(ProcessBindingReason.NotAdmittedByPolicy);
        (_, string calls, _) = await Run("operations", unbound.Path, "--evidence-policy", "DirectOnly");
        Assert.Contains($"PID 1960 · {said}", calls, StringComparison.Ordinal);
        (_, string exchanges, _) = await Run("exchanges", unbound.Path, "--evidence-policy", "DirectOnly");
        Assert.Contains($"PID 4242 ({said})", exchanges, StringComparison.Ordinal);
        (_, string processes, _) = await Run("processes", unbound.Path, "--evidence-policy", "DirectOnly");
        Assert.Contains(said, processes, StringComparison.Ordinal);
        (_, string grouped, _) = await Run("metric", unbound.Path, "--metric", "observations", "--group-by", "process",
            "--evidence-policy", "DirectOnly");
        Assert.Contains(said, grouped, StringComparison.Ordinal);
        Assert.All(new[] { calls, exchanges, processes, grouped },
            answer => Assert.DoesNotContain(nameof(ProcessBindingReason.NotAdmittedByPolicy), answer, StringComparison.Ordinal));
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

    [Fact(DisplayName = "§6.4: icat evidence lists a timeline lane's records, as the window lists a chosen cell's: one mechanism's, one source direction's, or those made at one end of a paired channel")]
    public async Task EvidenceListsALanesRecords()
    {
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            .. Enumerable.Range(0, 4).SelectMany(index => new[]
            {
                Transfer(10 + (3 * index), ObservationKind.Send, AccountingSide.SendSide, 64, 100, (ulong)(10 + (3 * index)))
                    .Between("127.0.0.1:50000", "127.0.0.1:8080") with { SessionRelativeTicks = (10 + (3 * index)) * 100L },
                Transfer(11 + (3 * index), ObservationKind.Receive, AccountingSide.ReceiveSide, 64, 200, (ulong)(11 + (3 * index)))
                    .Between("127.0.0.1:8080", "127.0.0.1:50000") with { SessionRelativeTicks = (11 + (3 * index)) * 100L },
                Transfer(12 + (3 * index), ObservationKind.Send, AccountingSide.SendSide, 16, 100, (ulong)(12 + (3 * index)))
                    .Between("127.0.0.1:50001", "127.0.0.1:53") with { Mechanism = Mechanism.Udp, SessionRelativeTicks = (12 + (3 * index)) * 100L },
            }),
        ]);
        string channel = SessionOverviewProjector.Project(session.Store).Channels.Single().Key;

        // A mechanism's and a source direction's records, by the names a person types, carried by the JSON page.
        (InterCatExitCode code, string answer, _) = await Run("evidence", session.Path, "--mechanism", "udp", "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        using (JsonDocument page = JsonDocument.Parse(answer))
        {
            Assert.Equal("evidence-page-v5", page.RootElement.GetProperty("contract").GetString());
            Assert.Equal("Udp", page.RootElement.GetProperty("page").GetProperty("mechanism").GetString());
            Assert.Equal(4, page.RootElement.GetProperty("page").GetProperty("records").GetArrayLength());
        }

        (code, answer, _) = await Run("evidence", session.Path, "--direction", "inbound", "--page-size", "2");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^  Source direction +inbound$", answer);
        Assert.Matches(@"(?m)^  Rows on page +2$", answer);
        Assert.Contains(" --direction inbound", answer, StringComparison.Ordinal);

        // One end of the paired channel holds half its records.
        (code, answer, _) = await Run("evidence", session.Path, "--channel", channel, "--end", "1", "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        using (JsonDocument page = JsonDocument.Parse(answer))
        {
            Assert.Equal(1, page.RootElement.GetProperty("page").GetProperty("end").GetInt32());
            Assert.Equal(4, page.RootElement.GetProperty("page").GetProperty("records").GetArrayLength());
        }

        // A name that is none, an end without a channel and a third end are refused, saying what is expected.
        (code, _, string refused) = await Run("evidence", session.Path, "--mechanism", "pigeon");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("--mechanism expects one of:", refused, StringComparison.Ordinal);
        (code, _, refused) = await Run("evidence", session.Path, "--end", "0");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("name the channel by its key", refused, StringComparison.Ordinal);
        (code, _, refused) = await Run("evidence", session.Path, "--channel", channel, "--end", "2");
        Assert.Equal(InterCatExitCode.InvalidInvocation, code);
        Assert.Contains("--end takes 0", refused, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R21: icat evidence states the capture's coverage over its time scope, in the inspector's words")]
    public async Task EvidenceStatesItsScopesCoverage()
    {
        using TemporarySession gapped = Gapped();

        // Over the whole session, and over a range within an epoch, the capture covered what it collected.
        const string Covered = "Coverage: covered for TCP · no other mechanism collected";
        (InterCatExitCode code, string answer, _) = await Run("evidence", gapped.Path);
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches($@"(?m)^  {Regex.Escape(Covered)}$", answer);
        (code, answer, _) = await Run("evidence", gapped.Path, "--interval", "5:15");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches($@"(?m)^  {Regex.Escape(Covered)}$", answer);

        // A range it delivered nothing in lists no record, and says that is no proof of inactivity, as the inspector does.
        const string Unknown = "Coverage unknown: outside the readings the capture's sources delivered, so a count of none here "
            + "is not proof of inactivity";
        (code, answer, _) = await Run("evidence", gapped.Path, "--interval", "25:35");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^  Rows on page +0$", answer);
        Assert.Matches($@"(?m)^  {Regex.Escape(Unknown)}$", answer);
        Assert.Contains("its coverage states what the capture's sources covered over its time scope", answer, StringComparison.Ordinal);

        // Its JSON carries each mechanism's state and the fact behind it.
        (code, answer, _) = await Run("evidence", gapped.Path, "--interval", "25:35", "--json");
        Assert.Equal(InterCatExitCode.Success, code);
        using (JsonDocument page = JsonDocument.Parse(answer))
        {
            Assert.Equal("evidence-page-v5", page.RootElement.GetProperty("contract").GetString());
            JsonElement coverage = page.RootElement.GetProperty("page").GetProperty("coverage");
            Assert.Equal(Enum.GetValues<Mechanism>().Length, coverage.GetArrayLength());
            Assert.All(coverage.EnumerateArray(), entry => Assert.Equal(
                ("UnknownCoverage", "outside the readings the capture's sources delivered"),
                (entry.GetProperty("state").GetString(), entry.GetProperty("reason").GetString())));
        }

        // A generation without a ledger has judged nothing, and says so.
        (code, answer, _) = await Run("evidence", session.Path);
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^  Coverage unknown: this generation publishes no coverage ledger, so a count of none here is not proof of inactivity$",
            answer);
    }

    [Fact(DisplayName = "R5: icat metric states its coverage in the inspector's words, and names a mechanism as the window does")]
    public async Task MetricStatesItsCoverageInWords()
    {
        using TemporarySession gapped = Gapped();

        // Every mechanism's coverage, as the inspector states it beneath its time scope.
        (InterCatExitCode code, string answer, _) = await Run("metric", gapped.Path, "--metric", "observations");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^  Coverage: covered for TCP · no other mechanism collected$", answer);

        // One mechanism's, over a range the capture delivered nothing in, as one sentence; the projection names it as a
        // lane does, never by its enumeration's name.
        (code, answer, _) = await Run("metric", gapped.Path, "--metric", "observations", "--mechanism", "Tcp", "--interval", "25:35");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^  TCP's coverage over the selected interval is unknown: outside the readings the capture's sources delivered\.$",
            answer);
        Assert.Matches(@"(?m)^  Projection +TCP$", answer);
        Assert.DoesNotContain("Tcp", answer, StringComparison.Ordinal);
        (code, answer, _) = await Run("metric", gapped.Path, "--metric", "observations", "--mechanism", "Tcp");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^  TCP was covered over the session: 2 records from its 1 admitted descriptor, and nothing was reported lost\.$",
            answer);

        // A generation without a ledger has judged nothing.
        (code, answer, _) = await Run("metric", session.Path, "--metric", "observations");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^  Coverage unknown: this generation publishes no coverage ledger, so a count of none here is not proof of inactivity$",
            answer);
    }

    [Fact(DisplayName = "R18: an --interval takes each bound as ticks or as a time with its unit, as the window states an interval, taken outward to whole ticks")]
    public async Task IntervalsTakeTimesWithTheirUnit()
    {
        using TemporarySession gapped = Gapped();

        // Six microseconds from the epoch is the interval sixty ticks are, counted alike.
        (InterCatExitCode code, string ticks, string said) = await Run("timeline", gapped.Path, "--interval", "0:60", "--columns", "6");
        Assert.True(code == InterCatExitCode.Success, said);
        (code, string timed, said) = await Run("timeline", gapped.Path, "--interval", "0s:6us", "--columns", "6");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Equal(Buckets(ticks), Buckets(timed));
        Assert.Equal(6, Buckets(timed).Length);
        (code, timed, said) = await Run("timeline", gapped.Path, "--interval", "0ms:0.006ms", "--columns", "6");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Equal(Buckets(ticks), Buckets(timed));

        // A time inside a tick is taken outward, so the interval holds every record of the time typed.
        (code, string page, said) = await Run("evidence", gapped.Path, "--interval", "150ns:250ns");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(new Regex(@"^\s*Session-time interval\s+\[1, 3\) · 100 ns ticks\r?$", RegexOptions.Multiline), page);
        (code, page, said) = await Run("evidence", gapped.Path, "--interval", "-1us:1µs");
        Assert.True(code == InterCatExitCode.Success, said);
        Assert.Matches(new Regex(@"^\s*Session-time interval\s+\[-10, 10\) · 100 ns ticks\r?$", RegexOptions.Multiline), page);

        // A bound that is neither, or an interval whose end is not after its start, is refused, saying what it takes.
        foreach (string refused in new[] { "1.5:2", "1s:1000ms", "1 h:2 h", "1s" })
        {
            (code, _, said) = await Run("evidence", gapped.Path, "--interval", refused);
            Assert.Equal(InterCatExitCode.InvalidInvocation, code);
            Assert.Contains("--interval must be start:end, each bound a count of 100-nanosecond session ticks or a time with its "
                + "unit, such as 1.5s, 250ms or 40us, with its end after its start.", said, StringComparison.Ordinal);
        }

        static string[] Buckets(string answer) =>
            [.. answer.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Contains(" µs  ", StringComparison.Ordinal))];
    }

    [Fact(DisplayName = "R21: icat timeline counts a mechanism's lane where none of its records falls, each column saying what the capture covered of it, as the window's lane does")]
    public async Task AQuietMechanismsLaneIsCounted()
    {
        using TemporarySession gapped = Gapped();

        // The capture collected TCP alone, so UDP's lane holds no record: each of its columns is counted all the same,
        // not collected where the capture delivered readings and unknown between its epochs, rather than left out.
        (InterCatExitCode code, string answer, _) = await Run("timeline", gapped.Path, "--interval", "0:60", "--columns", "6",
            "--mechanism", "udp");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^  Records +UDP records$", answer);
        Assert.Matches(@"(?m)^  0\.0 – 1\.0 µs +0 +- +not collected$", answer);
        Assert.Matches(@"(?m)^  2\.0 – 3\.0 µs +0 +- +unknown$", answer);
        Assert.Matches(@"(?m)^  5\.0 – 6\.0 µs +0 +- +not collected$", answer);
        Assert.DoesNotContain("has no bucket", answer, StringComparison.Ordinal);

        // TCP's quiet columns are covered: nothing it collects happened there.
        (code, answer, _) = await Run("timeline", gapped.Path, "--interval", "0:60", "--columns", "6", "--mechanism", "tcp");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^  Records +TCP records$", answer);
        Assert.Matches(@"(?m)^  0\.0 – 1\.0 µs +0 +- +covered$", answer);
        Assert.Matches(@"(?m)^  1\.0 – 2\.0 µs +1 +TCP +covered$", answer);
    }

    [Fact(DisplayName = "R5: icat's tables name a mechanism, a record's kind and layer, a coverage state and a capability as the window does, never by the enumeration")]
    public async Task TablesNameMechanismsAndCoverageInWords()
    {
        using TemporarySession gapped = Gapped();

        // A timeline's buckets: the mechanism a bucket mostly holds and the capture's coverage there, in the window's words.
        (InterCatExitCode code, string answer, _) = await Run("timeline", gapped.Path, "--interval", "0:60", "--columns", "6");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^  1\.0 – 2\.0 µs +1 +TCP +covered$", answer);
        Assert.Matches(@"(?m)^  2\.0 – 3\.0 µs +0 +- +unknown$", answer);

        // A session's summary, its coverage table and what was not collected, each epoch's losses named for it.
        (code, answer, _) = await Run("session", gapped.Path, "--rows", "2");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^  Mechanisms +TCP$", answer);
        Assert.Matches(@"(?m)^  Layers +transport$", answer);
        Assert.Matches(@"(?m)^  TCP +covered +2 records from its 1 admitted descriptor", answer);
        Assert.Contains("says nothing about their activity: process lifecycle, thread lifecycle, UDP, Unix socket, named pipe,",
            answer, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^  Epoch 1 +live capture, delivered readings \[0, 20\] source ticks$", answer);
        Assert.Matches(@"(?m)^  Epoch 1 losses +the session reported 0 lost events, which may be any mechanism's; the consumer lost 0 "
            + "buffers of unknown size; InterCat's full queue dropped 0 records; 0 admitted records could not be stored$", answer);
        Assert.Matches(@"(?m)^  Epoch 2 undecodable +0$", answer);

        // An imported file's epoch, a provider it delivered that the capture did not request, and what the file reported
        // lost, each in the words a coverage reason uses, never an enumeration's name split apart ("Etl import").
        using var imported = new TemporarySession();
        Publish(imported.Store,
            [Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 1).Between("127.0.0.1:50000", "127.0.0.1:8080")],
            coverage: new CoverageLedgerV1
            {
                Contract = CoverageLedgerV1.ContractName,
                Epochs =
                [
                    new CoverageEpochV1
                    {
                        Epoch = 1,
                        Acquisition = CoverageAcquisition.EtlImport,
                        FirstDeliveredNativeTicks = 10,
                        LastDeliveredNativeTicks = 10,
                        Collected =
                        [
                            new CoverageCollectedV1 { ProviderId = NetworkProvider, ProviderName = "network", EventId = 10, Version = 0, Mechanism = Mechanism.Tcp },
                        ],
                        Deliveries =
                        [
                            new CoverageDeliveryV1 { ProviderId = NetworkProvider, EventId = 10, Version = 0, Delivered = 1, Admitted = 1, Omitted = 0 },
                            new CoverageDeliveryV1
                            {
                                ProviderId = Guid.Parse("0c0ffee0-1111-4222-8333-444444444444"), Delivered = 3, Admitted = 0, Omitted = 3,
                                Omission = OmissionReason.UnrequestedProvider,
                            },
                        ],
                        Losses = [new CoverageLossV1 { Layer = LossLayer.SourceSession, Lost = 2 }],
                    },
                ],
            });
        imported.Store.ReleaseSegmentReaders();
        (code, answer, _) = await Run("session", imported.Path);
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^  Epoch +ETL import, delivered readings \[10, 10\] source ticks$", answer);
        Assert.Matches(@"(?m)^  Losses +the file reported 2 lost events, which may be any mechanism's$", answer);
        Assert.Matches(@"(?m) 3 +a provider the capture did not request$", answer);
        Assert.DoesNotContain("Etl", answer, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^  10 +TCP +send +100 ", answer);

        // A record without a size says why, as the window does: its source withheld it, or a package redacted it.
        using var unsized = new TemporarySession();
        Publish(unsized.Store,
        [
            Transfer(10, ObservationKind.Receive, AccountingSide.ReceiveSide, null, 200, 1).Between("127.0.0.1:8080", "127.0.0.1:50000"),
            Transfer(11, ObservationKind.Receive, AccountingSide.ReceiveSide, null, 200, 2).Between("127.0.0.1:8080", "127.0.0.1:50000")
                with { ByteAvailability = FieldAvailability.Redacted },
        ]);
        (code, answer, _) = await Run("session", unsized.Path, "--rows", "2");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^  10 +TCP +receive +200 +unknown \(not exposed\) ", answer);
        Assert.Matches(@"(?m)^  11 +TCP +receive +200 +unknown \(redacted\) ", answer);
        (code, answer, _) = await Run("metric", unsized.Path, "--metric", "observations", "--evidence", "2", "--layer", "transport");
        Assert.Equal(InterCatExitCode.Success, code);
        Assert.Matches(@"(?m)^  \S.* +TCP +receive +200 +unknown \(redacted\) ", answer);
        Assert.Matches(@"(?m)^  Projection +transport$", answer);

        // A machine's sources and mechanisms, whatever this machine reports of them, read in words; and a content request's.
        (_, answer, _) = await Run("capabilities");
        string[] tcp = Regex.Split(answer.Split('\n').Single(line => line.StartsWith("  TCP ", StringComparison.Ordinal)).Trim(), " {2,}");
        Assert.Equal(5, tcp.Length);
        Assert.Contains(tcp[1], Enum.GetValues<CapabilityState>().Select(CapabilityText.State));
        Assert.Contains(tcp[2], Enum.GetValues<CapabilityTier>().Select(CapabilityText.Tier));
        Assert.Contains(tcp[3], Enum.GetValues<CoverageState>().Select(CoverageStateText.Value));
        Assert.All(answer.Split('\n').Where(line => line.StartsWith("  etw/", StringComparison.Ordinal) && line.Contains("  ", StringComparison.Ordinal)
            && Regex.Split(line.Trim(), " {2,}").Length == 5), line =>
        {
            string[] source = Regex.Split(line.Trim(), " {2,}");
            Assert.Contains(source[1], Enum.GetValues<CapabilityState>().Select(CapabilityText.State));
            Assert.Contains(source[2], Enum.GetValues<OverheadClass>().Select(CapabilityText.Overhead));
        });
        Assert.Matches(@"(?m)^  Named pipe +", answer);
        (_, answer, _) = await Run("profiles", "content", "--source", "etw/manifest/Microsoft-Windows-WinINet-Capture",
            "--mechanism", "http", "--pid", "1234", "--channel", "*", "--max-record-bytes", "4096", "--max-session-bytes", "65536",
            "--retention", "stop-at-limit", "--inspection", "hex-text");
        Assert.Matches(@"(?m)^  Mechanism +HTTP$", answer);

        // None of them names a mechanism, a kind, a layer, a size's reason or a state by its enumeration.
        foreach (string[] asked in (string[][])[["timeline", gapped.Path, "--interval", "0:60", "--columns", "6"], ["session", gapped.Path, "--rows", "2"],
            ["session", unsized.Path, "--rows", "2"],
            ["metric", unsized.Path, "--metric", "observations", "--evidence", "2", "--layer", "transport"], ["capabilities"]])
        {
            (_, answer, _) = await Run(asked);
            Assert.DoesNotMatch(@"\b(Tcp|Udp|NamedPipe|ProcessLifecycle|UnknownCoverage|PartialGap|NotCollected|Send|Receive|Transport|NotExposed"
                + @"|Redacted|DisabledByProfile|PermissionDenied|TrafficVisualization|Unsupported|Unmeasured|Moderate)\b", answer);
        }
    }

    /// <summary>A capture that delivered readings from 0 to 20 and from 40 to 60, and none between, and lost nothing.</summary>
    private static TemporarySession Gapped()
    {
        var gapped = new TemporarySession();
        Publish(gapped.Store,
        [
            Transfer(10, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 1).Between("127.0.0.1:50000", "127.0.0.1:8080")
                with { SessionRelativeTicks = 1_000 },
            Transfer(50, ObservationKind.Send, AccountingSide.SendSide, 64, 100, 2).Between("127.0.0.1:50000", "127.0.0.1:8080")
                with { SessionRelativeTicks = 5_000 },
        ], coverage: new CoverageLedgerV1
        {
            Contract = CoverageLedgerV1.ContractName,
            Epochs = [TcpEpoch(1, 0, 20), TcpEpoch(2, 40, 60)],
        });
        return gapped;
    }

    /// <summary>A live epoch between two delivered readings that collected TCP, delivered two records and lost nothing.</summary>
    private static CoverageEpochV1 TcpEpoch(int number, long first, long last) => new()
    {
        Epoch = number,
        Acquisition = CoverageAcquisition.LiveCapture,
        FirstDeliveredNativeTicks = first,
        LastDeliveredNativeTicks = last,
        Collected =
        [
            new CoverageCollectedV1 { ProviderId = NetworkProvider, ProviderName = "network", EventId = 10, Version = 0, Mechanism = Mechanism.Tcp },
        ],
        Deliveries = [new CoverageDeliveryV1 { ProviderId = NetworkProvider, EventId = 10, Version = 0, Delivered = 2, Admitted = 2, Omitted = 0 }],

        // In an order the ledger does not promise: a report lists them by layer, as a coverage reason does.
        Losses =
        [
            new CoverageLossV1 { Layer = LossLayer.Storage, Lost = 0 },
            new CoverageLossV1 { Layer = LossLayer.CallbackQueue, Lost = 0 },
            new CoverageLossV1 { Layer = LossLayer.ConsumerBuffers, Lost = 0 },
            new CoverageLossV1 { Layer = LossLayer.SourceSession, Lost = 0 },
        ],
    };

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

    [Fact(DisplayName = "R18: icat package --interval holds an interval's records and its processes', and icat session says so in the window's words")]
    public async Task AnIntervalPackageSaysWhatItHolds()
    {
        string folder = Path.Combine(Path.GetTempPath(), "intercat-interval-package-" + Guid.NewGuid().ToString("N"));
        try
        {
            // The client's send at tick 5,000 lies in the interval; its creation at tick 10 is its process's lifecycle
            // record; its send at tick 100 and the other process's records lie outside it and are left out.
            string held = Held(folder, "source", "interval-host",
            [
                Timed(Lifecycle(10, ObservationKind.Create, 4_242, 1) with { ResourceName = @"C:\Tools\client.exe" }),
                Timed(Lifecycle(20, ObservationKind.Create, 5_353, 2) with { ResourceName = @"C:\Tools\other.exe" }),
                Timed(Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 300, 4_242, 10).Between("10.0.0.1:40000", "10.0.0.2:443")),
                Timed(Transfer(5_000, ObservationKind.Send, AccountingSide.SendSide, 400, 4_242, 11).Between("10.0.0.1:40000", "10.0.0.2:443")),
                Timed(Transfer(9_000, ObservationKind.Send, AccountingSide.SendSide, 500, 5_353, 12).Between("10.0.0.3:40001", "10.0.0.4:443")),
            ], TestSessions.TransportLedger(tcp: true, udp: false));
            string package = Path.Combine(folder, "package");
            (InterCatExitCode code, string output, string said) = await Run("package", held, "--redacted", "--interval", "4000:6000",
                "--output", package, "--json");
            Assert.True(code == InterCatExitCode.Success, said);
            using JsonDocument document = JsonDocument.Parse(output);
            JsonElement root = document.RootElement;
            Assert.Equal((2L, 5L), (root.GetProperty("rows").GetInt64(), root.GetProperty("sourceRows").GetInt64()));
            JsonElement interval = root.GetProperty("interval");
            Assert.Equal((4_000L, 6_000L, 1L), (interval.GetProperty("startTicks").GetInt64(), interval.GetProperty("endTicks").GetInt64(),
                interval.GetProperty("lifecycleRowsOutside").GetInt64()));
            var holds = new RedactedSessionInterval { StartTicks = 4_000, EndTicks = 6_000, LifecycleRowsOutside = 1 };
            Assert.Contains(SessionRedaction.Holds(holds, CultureInfo.CurrentCulture),
                root.GetProperty("notes").EnumerateArray().Select(note => note.GetString()));

            // The package says what it holds wherever it is read, in the sentence the window shows.
            SessionStore opened = SessionStore.OpenExisting(LocalOwnedDirectory.Open(package));
            SessionRedaction redaction = SessionRedaction.Read(opened.Root, opened.Current!)!;
            opened.ReleaseSegmentReaders();
            (InterCatExitCode shown, string text, string error) = await Run("session", package);
            Assert.True(shown == InterCatExitCode.Success, error);
            Assert.Contains(redaction.Statement(CultureInfo.CurrentCulture), text, StringComparison.Ordinal);
            Assert.Contains(string.Create(CultureInfo.CurrentCulture, $"an interval of its source, ticks {4_000:N0} to {6_000:N0}"),
                text, StringComparison.Ordinal);

            // Its ledger's states are those within the interval, said to be, where its whole time's coverage is unknown.
            Assert.Contains("Within this package's interval, where its coverage speaks; outside it, coverage is unknown:", text,
                StringComparison.Ordinal);
            Assert.Matches(@"(?m)^\s*TCP\s+covered\s", text);

            // An original package copies the whole generation, and an interval is a start before an end.
            (InterCatExitCode original, _, string refused) = await Run("package", held, "--original", "--interval", "4000:6000",
                "--output", Path.Combine(folder, "original"));
            Assert.Equal(InterCatExitCode.InvalidInvocation, original);
            Assert.Contains("--interval scopes a redacted package", refused, StringComparison.Ordinal);
            (InterCatExitCode reversed, _, string backwards) = await Run("package", held, "--redacted", "--interval", "6000:4000",
                "--output", Path.Combine(folder, "reversed"));
            Assert.Equal(InterCatExitCode.InvalidInvocation, reversed);
            Assert.Contains("--interval must be start:end", backwards, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }

        static ObservationRowV1 Timed(ObservationRowV1 row) => row with { SessionRelativeTicks = row.NativeTicks * 100 };
    }

    [Fact(DisplayName = "§6.3: icat metric --group-by session ranks each terminal session's records and states the processes whose records named none")]
    public async Task MetricGroupsByTerminalSession()
    {
        ObservationRowV1 user = Lifecycle(10, ObservationKind.Create, 100, 1);
        ObservationRowV1 service = Lifecycle(12, ObservationKind.Create, 300, 3);
        ObservationRowV1 unnamed = Lifecycle(13, ObservationKind.Create, 400, 4);
        using var session = new TemporarySession();
        Publish(session.Store,
        [
            user, service, unnamed,
            Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 100, 100, 5).Between("127.0.0.1:50000", "10.0.0.5:443"),
            Transfer(40, ObservationKind.Send, AccountingSide.SendSide, 9, 400, 9).Between("127.0.0.1:50002", "10.0.0.6:443"),
        ], fields: [Field(user, SourceField.ProcessSessionId, 1), Field(service, SourceField.ProcessSessionId, 0)]);
        session.Store.ReleaseSegmentReaders();

        (InterCatExitCode code, string output, string error) = await Run("metric", session.Path, "--metric", "observations", "--group-by", "session");
        Assert.True(code == InterCatExitCode.Success, error);
        string text = output.ReplaceLineEndings("\n");
        Assert.Contains("\nOBSERVATIONS BY TERMINAL SESSION\n", text, StringComparison.Ordinal);
        Assert.Contains("  By terminal session (", text, StringComparison.Ordinal);
        Assert.Matches(@"\n\s+1\s+Terminal session 1\s+2\s", text);
        Assert.Matches(@"\n\s+2\s+Terminal session 0\s+1\s", text);
        Assert.Contains("  Not attributed to a terminal session, by reason (never a peer, never ranked):", text, StringComparison.Ordinal);
        Assert.Contains(BindingText.Reason(ProcessBindingReason.SessionUnknown), text, StringComparison.Ordinal);

        using JsonDocument json = JsonDocument.Parse((await Run("metric", session.Path, "--metric", "observations", "--group-by", "session", "--json")).Output);
        JsonElement grouping = json.RootElement.GetProperty("grouping");
        Assert.Equal("UserSession", grouping.GetProperty("grouping").GetString());
        Assert.Equal(
            [("UserSession", 1u, 2L), ("UserSession", 0u, 1L)],
            grouping.GetProperty("groups").EnumerateArray().Select(group => (
                group.GetProperty("kind").GetString(), group.GetProperty("terminalSession").GetUInt32(), group.GetProperty("value").GetInt64())));
        Assert.Contains(grouping.GetProperty("unattributed").EnumerateArray(),
            group => group.GetProperty("reason").GetString() == nameof(ProcessBindingReason.SessionUnknown) && group.GetProperty("value").GetInt64() == 2);
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
