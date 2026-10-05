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

    [Fact(DisplayName = "R18: a refused invocation says why on stderr, and leaves stdout to the answer it did not give")]
    public async Task ARefusedInvocationLeavesStdoutToTheAnswer()
    {
        // Every command reads its arguments before it refuses one it does not know, so each is read here once.
        string[] commands =
        [
            "capabilities", "profiles", "measure", "import", "record", "capture", "rederive", "session", "overview", "channels",
            "evidence", "timeline", "export", "package", "raw", "content", "recover", "staging", "retain", "compact",
            "checkpoint", "follow", "metric", "processes", "operations", "exchanges", "workspace", "verify", "bench",
        ];
        foreach (string command in commands)
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

    [Fact(DisplayName = "R22: icat workspace show states what each session's layout keeps, its pins and its ranking")]
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
            Assert.Contains("\"contract\": \"workspace-resolution-v14\"", json, StringComparison.Ordinal);
            Assert.Contains("\"rankBy\": \"BytesSent\"", json, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(workspace);
        }
    }

    /// <summary>An answer without what differs between two runs of one query: how long each took.</summary>
    private static string Comparable(string answer) =>
        System.Text.RegularExpressions.Regex.Replace(answer, "\"(elapsed|counted|duration)[A-Za-z]*\": [0-9.]+", "\"$1\": 0");

    /// <summary>One icat invocation, with what it wrote to stdout and to stderr.</summary>
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
