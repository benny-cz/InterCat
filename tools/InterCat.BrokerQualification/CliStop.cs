using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;

// Ctrl+C stops `icat capture` early and keeps everything it published (plan revision 864). Each run records through
// `icat capture --broker <this tool>` and presses Ctrl+C in the console the capture runs in, as a person at it does. A
// pass of the capture's follow derives and publishes what was published, reads status, renews the lease when due and
// waits 250 ms; a press must stop the capture by request and finish its session wherever in the pass it lands. Half the
// runs press while the follow publishes - a staging file of the session's own in its folder - and half at a stated time.
[SupportedOSPlatform("windows")]
internal static partial class Qualification
{
    /// <summary>When each run presses Ctrl+C: a time after the session's first generation, and whether at the follow's next publication then.</summary>
    private static readonly (int AfterFirstMs, bool AtPublication)[] CtrlCMoments =
    [
        (0, true), (500, true), (1_000, true), (1_500, true),
        (0, false), (250, false), (500, false), (1_000, false),
    ];

    public static async Task<int> CliStopAsync(string[] args, CancellationToken cancellationToken)
    {
        string? output = Option(args, "--output");
        if (output is null)
        {
            return Usage();
        }

        var etw = new TraceEventSessionHost();
        if (etw.IsElevated != true)
        {
            Console.Error.WriteLine("The Ctrl+C qualification starts kernel ETW sessions and must run elevated.");
            return 3;
        }

        output = Path.GetFullPath(output);
        if (Directory.Exists(output) || File.Exists(output))
        {
            Console.Error.WriteLine($"'{output}' already exists; qualification writes only to a new directory.");
            return 2;
        }

        if (FindSibling("InterCat.Cli", "InterCat.Cli.exe") is not { } cli)
        {
            Console.Error.WriteLine("InterCat.Cli.exe was not found; build src/InterCat.Cli first.");
            return 2;
        }

        Directory.CreateDirectory(output);
        var report = new CliStopReport
        {
            Schema = "intercat.cli-stop.v1",
            StartedUtc = DateTimeOffset.UtcNow,
            Environment = CapabilityInventoryProbe.DescribeEnvironment(etw.IsElevated),
            Cli = cli,
            Notes =
            [
                "Each run is `icat capture <session> --duration 60 --broker <this tool> --json`, started with a console of "
                    + "its own and no window, its broker this tool's serve mode over a qualification root beside the tool, "
                    + "with one paced loopback TCP stream (1 KiB every 20 ms) and a new connection each second while it records.",
                "Ctrl+C is pressed in that console by a child of this tool, which joins it as the capture starts, ignores "
                    + "the event itself, and presses when asked: icat sees what a person pressing Ctrl+C at it would make it see.",
                "A run that presses at a publication waits the stated time after the session's first manifest and presses "
                    + "as soon as the session's folder holds a staging lease (store-v1 §4), which the follow's writer holds "
                    + "while it publishes. The other runs press the stated time after the first manifest, in another part "
                    + "of a pass.",
                "A run passes when icat requested the stop, finished the session from every chunk the capture published, "
                    + "exited 0, and left no follow ticket: nothing for a later launch to finish.",
            ],
        };

        IReadOnlyList<string> sessionsBefore = InterCatSessions(etw);
        try
        {
            foreach ((int afterFirst, bool atPublication) in CtrlCMoments)
            {
                CliStopRun run = await CliStopRunAsync(cli, output, report.Runs.Count + 1, afterFirst, atPublication,
                    cancellationToken);
                report.Runs.Add(run);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"run {run.Run}: Ctrl+C {afterFirst} ms after the first generation"
                    + $"{(atPublication ? $", at a publication ({(run.PressedDuringPublication ? "seen" : "none seen")})" : string.Empty)}; "
                    + $"exit {run.ExitCode}, {(run.StopRequested ? "stop requested" : "no stop requested")}, "
                    + $"{run.DerivedChunks} of {run.EvidenceChunks} chunks derived, {(run.Finished ? "finished" : "not finished")}"
                    + $"{(run.TicketLeft ? ", ticket left" : string.Empty)}"
                    + $"{(run.FailureReason is { } reason ? ": " + reason : string.Empty)}"));
                if (!await WaitForCliBrokerExitAsync(cancellationToken))
                {
                    report.Failure = "The broker did not exit within 90 s of a capture's end, so no further run was made.";
                    break;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            report.Failure = exception.ToString();
        }
        finally
        {
            report.LeakedSessions = [.. InterCatSessions(etw).Except(sessionsBefore, StringComparer.OrdinalIgnoreCase)];
            report.FinishedUtc = DateTimeOffset.UtcNow;
            report.Passed = report.Failure is null && report.LeakedSessions.Count == 0
                && report.Runs.Count == CtrlCMoments.Length && report.Runs.All(run => run.Passed);
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, Json),
                CancellationToken.None);
            TryDelete(CliRootParent);
        }

        Console.WriteLine(report.Passed ? "Ctrl+C qualification passed." : "Ctrl+C qualification FAILED; see report.json.");
        Console.WriteLine(Path.Combine(output, "report.json"));
        return report.Passed ? 0 : 1;
    }

    private static async Task<CliStopRun> CliStopRunAsync(
        string cli, string output, int run, int afterFirst, bool atPublication, CancellationToken cancellationToken)
    {
        var result = new CliStopRun { Run = run, CtrlCAfterFirstGenerationMs = afterFirst, AtPublication = atPublication };
        string session = Path.Combine(output, $"run-{run}");
        var start = new ProcessStartInfo(cli)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "capture", session, "--duration", "60", "--broker", Environment.ProcessPath!, "--json" })
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("icat did not start.");
        using CtrlCPress press = CtrlCPress.Join(process.Id);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var traffic = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task sending = PacedLoopbackAsync(TimeSpan.FromSeconds(60), process.WaitForExitAsync(traffic.Token), traffic.Token);
        try
        {
            // The session's first generation: a manifest in the folder the capture derives into.
            var waited = Stopwatch.StartNew();
            while (!process.HasExited && !(Directory.Exists(session) && Directory.EnumerateFiles(session, "manifest-*.json").Any()))
            {
                if (waited.Elapsed > TimeSpan.FromSeconds(45))
                {
                    result.FailureReason = "The capture published no generation within 45 s.";
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            }

            if (result.FailureReason is null && !process.HasExited)
            {
                await Task.Delay(afterFirst, cancellationToken);
                if (atPublication)
                {
                    // The follow publishes each chunk the capture publishes, every 2 s; its writer holds a lease meanwhile.
                    var watching = Stopwatch.StartNew();
                    while (!process.HasExited && watching.Elapsed < TimeSpan.FromSeconds(10))
                    {
                        if (Directory.EnumerateFiles(session, SessionStore.StagingPrefix + "*.lease").Any())
                        {
                            result.PressedDuringPublication = true;
                            break;
                        }

                        Thread.SpinWait(1_000);
                    }
                }

                var stopping = Stopwatch.StartNew();
                result.CtrlCPressed = press.Press();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(120));
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                    result.CtrlCToExitMs = stopping.ElapsedMilliseconds;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    result.FailureReason = "icat did not exit within 120 s of Ctrl+C.";
                }
            }
            else if (process.HasExited)
            {
                result.FailureReason ??= "icat exited before Ctrl+C was pressed.";
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                // Its capture is then left to the owner lease, which the broker-exit wait after this run allows for.
                process.Kill(entireProcessTree: true);
            }

            await traffic.CancelAsync();
            try
            {
                await sending;
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException)
            {
                // The traffic ends with the run.
            }
        }

        result.ExitCode = process.ExitCode;
        string said = AnsiEscape().Replace(await stderr, string.Empty);
        result.Said = [.. said.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith('!') || line.StartsWith('+') || line.StartsWith('x'))];
        result.StopRequested = said.Contains("Stopping the capture; deriving what it published.", StringComparison.Ordinal);
        result.TicketLeft = File.Exists(session + ".follow.json");
        string json = await stdout;
        if (!string.IsNullOrWhiteSpace(json))
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            result.State = root.GetProperty("state").GetString();
            result.Finished = root.GetProperty("finished").GetBoolean();
            result.EvidenceChunks = root.GetProperty("evidenceChunks").GetInt32();
            result.DerivedChunks = root.GetProperty("derivedChunks").GetInt32();
            result.DerivedRecords = root.GetProperty("derivedRecords").GetInt64();
        }

        result.Passed = result.FailureReason is null && result.CtrlCPressed && result.ExitCode == 0 && result.StopRequested
            && result.State == "Closed" && result.Finished && result.DerivedChunks == result.EvidenceChunks
            && result.DerivedRecords > 0 && !result.TicketLeft;
        result.FailureReason ??= result.Passed ? null : "The capture did not stop by request and finish its session.";
        return result;
    }

    /// <summary>
    /// The Ctrl+C child: it leaves its own console and joins the one <c>--pid</c> runs in, ignoring the event itself, and
    /// presses Ctrl+C there for every process in that console once a line arrives on its standard input. Exits 0 once the
    /// event was generated.
    /// </summary>
    public static int CtrlCChild(string[] args)
    {
        if (!uint.TryParse(Option(args, "--pid"), NumberStyles.None, CultureInfo.InvariantCulture, out uint processId))
        {
            return 2;
        }

        // Asked for on standard input, a pipe that leaving this console leaves open.
        using Stream asked = Console.OpenStandardInput();
        _ = FreeConsole();
        if (!AttachConsole(processId))
        {
            return 3;
        }

        _ = SetConsoleCtrlHandler(IntPtr.Zero, true);
        Span<byte> line = stackalloc byte[1];
        if (asked.Read(line) == 0)
        {
            _ = FreeConsole();
            return 4;
        }

        bool pressed = GenerateConsoleCtrlEvent(CtrlCEvent, 0);

        // The event reaches each process in the console on a thread of its own; this one stays attached until it has.
        Thread.Sleep(TimeSpan.FromMilliseconds(500));
        _ = FreeConsole();
        return pressed ? 0 : 1;
    }

    /// <summary>A Ctrl+C child joined to one console from the start, so a press comes when asked, not once a process has started.</summary>
    private sealed class CtrlCPress : IDisposable
    {
        private readonly Process child;

        private CtrlCPress(Process child) => this.child = child;

        public static CtrlCPress Join(int processId)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
            };
            foreach (string argument in new[] { "ctrl-c-child", "--pid", processId.ToString(CultureInfo.InvariantCulture) })
            {
                start.ArgumentList.Add(argument);
            }

            return new(Process.Start(start) ?? throw new InvalidOperationException("The Ctrl+C child did not start."));
        }

        /// <summary>Presses Ctrl+C, true once the child generated it.</summary>
        public bool Press()
        {
            if (child.HasExited)
            {
                return false;
            }

            child.StandardInput.WriteLine();
            child.StandardInput.Flush();
            return child.WaitForExit(TimeSpan.FromSeconds(10)) && child.ExitCode == 0;
        }

        public void Dispose()
        {
            if (!child.HasExited)
            {
                child.Kill();
            }

            child.Dispose();
        }
    }

    private const uint CtrlCEvent = 0;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleCtrlHandler(IntPtr handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);

    [GeneratedRegex(@"\x1b\[[0-9;]*m")]
    private static partial Regex AnsiEscape();
}

internal sealed class CliStopReport
{
    public required string Schema { get; init; }

    public required DateTimeOffset StartedUtc { get; init; }

    public DateTimeOffset FinishedUtc { get; set; }

    public required ProbeEnvironment Environment { get; init; }

    public required string Cli { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }

    public List<CliStopRun> Runs { get; } = [];

    public IReadOnlyList<string> LeakedSessions { get; set; } = [];

    public bool Passed { get; set; }

    public string? Failure { get; set; }
}

internal sealed class CliStopRun
{
    public int Run { get; init; }

    /// <summary>How long after the session's first manifest appeared the run began to press Ctrl+C.</summary>
    public int CtrlCAfterFirstGenerationMs { get; init; }

    /// <summary>Whether the run pressed at the follow's next publication after that time.</summary>
    public bool AtPublication { get; init; }

    /// <summary>Whether the session's folder held a staging lease as Ctrl+C was pressed.</summary>
    public bool PressedDuringPublication { get; set; }

    public bool CtrlCPressed { get; set; }

    /// <summary>From asking the child to press Ctrl+C to icat's exit.</summary>
    public long? CtrlCToExitMs { get; set; }

    public int ExitCode { get; set; }

    public bool StopRequested { get; set; }

    public string? State { get; set; }

    public bool Finished { get; set; }

    public int EvidenceChunks { get; set; }

    public int DerivedChunks { get; set; }

    public long DerivedRecords { get; set; }

    public bool TicketLeft { get; set; }

    /// <summary>What icat said beyond its progress: its warnings, outcomes and failures, colour removed.</summary>
    public IReadOnlyList<string> Said { get; set; } = [];

    public bool Passed { get; set; }

    public string? FailureReason { get; set; }
}
