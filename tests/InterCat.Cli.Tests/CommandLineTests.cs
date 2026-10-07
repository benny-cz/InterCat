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
        "checkpoint", "follow", "metric", "processes", "operations", "exchanges", "workspace", "verify", "bench",
    ];

    /// <summary>The fields of icat staging's preview, whose labels run past the column a short label takes.</summary>
    private static readonly string[] StagingFields =
        ["Session", "Abandoned staged files", "Marker-only files", "Active writer files", "Unmarked legacy files", "Files removed"];

    /// <summary>The fields an export's report states before its caveats.</summary>
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
            foreach (string file in new[] { rows, shared })
            {
                string[] lines = File.ReadAllLines(file);
                Assert.EndsWith(",scope_coverage", lines[0], StringComparison.Ordinal);
                Assert.All(lines.Skip(1), line => Assert.EndsWith($",\"{NoLedger}\"", line, StringComparison.Ordinal));
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

        // A process none of whose transport bytes was measured is not said to have sent or received none.
        (_, string idle, _) = await Run("processes", tcp.Path, "--pid", "300");
        Assert.Contains("No transport byte it sent or received was measured in this session, which is not proof it sent or "
            + "received none.", idle, StringComparison.Ordinal);
        Assert.DoesNotContain("sent and received no transport bytes", idle, StringComparison.Ordinal);
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

    [Fact(DisplayName = "R22: icat workspace show states what each session's layout keeps, its pins, ranking, evidence policy and lane scale, and the window's panes")]
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
            Assert.Contains("Session " + TestSessions.Session.ToString("N")[..8] + ": 1 node pinned on its graph and its rows ranked by "
                + "bytes sent per second, put back when it is opened from this investigation.", text, StringComparison.Ordinal);
            string json = (await Run("workspace", "show", workspace, "--json")).Output;
            Assert.Contains("\"contract\": \"workspace-resolution-v17\"", json, StringComparison.Ordinal);
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
            Assert.Equal("evidence-page-v3", page.RootElement.GetProperty("contract").GetString());
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
