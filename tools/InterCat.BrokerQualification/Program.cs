using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using InterCat.Capture.Windows;
using InterCat.CaptureBroker;
using InterCat.Domain;
using InterCat.Storage;

// Elevated qualification of the composed broker with real ETW (contract broker-v1 §5.5, plan revision 81).
// `run` launches this tool's `serve-child` mode as a separate broker process over a qualification root, drives it as a
// client, and kills and relaunches it to prove restart recovery. It never touches the production root.
if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Broker qualification requires Windows.");
    return 3;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

return args.FirstOrDefault() switch
{
    "serve-child" => await Qualification.ServeChildAsync(args, cancellation.Token),
    "run" => await Qualification.RunAsync(args, cancellation.Token),
    _ => Qualification.Usage(),
};

[SupportedOSPlatform("windows")]
internal static class Qualification
{
    private const string RootName = "InterCat";
    private static readonly TimeSpan ListenTimeout = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static int Usage()
    {
        Console.Error.WriteLine("Usage: InterCat.BrokerQualification run --output <new directory> [--traffic-seconds <2-60>]");
        Console.Error.WriteLine("Runs elevated. Starts real ETW sessions in a broker child process and stops every one it started.");
        return 2;
    }

    /// <summary>The broker child: production composition, except that the root lives under the qualification parent.</summary>
    public static async Task<int> ServeChildAsync(string[] args, CancellationToken cancellationToken)
    {
        string parent = Option(args, "--parent") ?? throw new ArgumentException("--parent is required.");
        BrokerLaunchOptions options = BrokerLaunchOptions.Parse(
                ["serve", .. args.Skip(1).Where((_, index) => !IsParentArgument(args.Skip(1).ToArray(), index)),
                    BrokerLaunchOptions.UnqualifiedOptIn],
                out BrokerLaunchParseError? error)
            ?? throw new ArgumentException(error!.Message);
        BrokerProcessDependencies production = BrokerProcessDependencies.Production(options.Owner);
        BrokerProcessDependencies dependencies = production with
        {
            Root = production.Root with
            {
                TrustedParentDirectory = parent,
                RootDirectoryName = RootName,

                // The one declared deviation: the parent is a qualification directory, not %ProgramData%.
                Policy = BrokerRootSecurityPolicy.Production with { RequireTrustedParentOwner = false },
            },
        };
        return await BrokerProcess.ServeAsync(options, dependencies, Console.Out, Console.Error, cancellationToken);
    }

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        string? output = Option(args, "--output");
        int trafficSeconds = int.TryParse(Option(args, "--traffic-seconds") ?? "4", NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            && parsed is >= 2 and <= 60 ? parsed : -1;
        if (output is null || trafficSeconds < 0)
        {
            return Usage();
        }

        var etw = new TraceEventSessionHost();
        if (etw.IsElevated != true)
        {
            Console.Error.WriteLine("Qualification starts kernel ETW sessions and must run elevated.");
            return 3;
        }

        output = Path.GetFullPath(output);
        if (Directory.Exists(output) || File.Exists(output))
        {
            Console.Error.WriteLine($"'{output}' already exists; qualification writes only to a new directory.");
            return 2;
        }

        Directory.CreateDirectory(output);
        string parent = Path.Combine(output, "root-parent");
        Directory.CreateDirectory(parent);
        BrokerClientIdentity self = WindowsBrokerTokenIdentity.ReadCurrentProcess();
        var report = new QualificationReport
        {
            Schema = "intercat.broker-qualification.v1",
            StartedUtc = DateTimeOffset.UtcNow,
            Environment = CapabilityInventoryProbe.DescribeEnvironment(etw.IsElevated),
            BrokerVersion = BrokerProcess.Version,
            Notes =
            [
                "The broker child runs the production composition (real TraceEventSessionHost ETW, TDH plan source, "
                    + "file ownership log, evidence runtime, authenticated pipe) over a qualification root. Its only "
                    + "declared deviation from production is the root's parent directory.",
                "The client is this elevated harness, so the pipe owner is an elevated token; the ordinary-integrity "
                    + "client path is covered by the pipe fixtures, not by this run.",
            ],
        };

        IReadOnlyList<string> sessionsBefore = InterCatSessions(etw);
        try
        {
            report.CleanStop = await CleanStopAsync(parent, self, trafficSeconds, etw, cancellationToken);
            report.KilledBroker = await KilledBrokerAsync(parent, self, trafficSeconds, etw, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            report.Failure = exception.ToString();
        }
        finally
        {
            report.LeakedSessions = [.. InterCatSessions(etw).Except(sessionsBefore, StringComparer.OrdinalIgnoreCase)];
            report.FinishedUtc = DateTimeOffset.UtcNow;
            report.Passed = report.Failure is null
                && report.LeakedSessions.Count == 0
                && report.CleanStop?.Passed == true
                && report.KilledBroker?.Passed == true;
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, Json), CancellationToken.None);
        }

        Console.WriteLine(report.Passed ? "Broker qualification passed." : "Broker qualification FAILED; see report.json.");
        Console.WriteLine(Path.Combine(output, "report.json"));
        return report.Passed ? 0 : 1;
    }

    private static async Task<ScenarioResult> CleanStopAsync(
        string parent,
        BrokerClientIdentity self,
        int trafficSeconds,
        TraceEventSessionHost etw,
        CancellationToken cancellationToken)
    {
        var result = new ScenarioResult { Name = "clean-stop" };
        await using BrokerChild child = await BrokerChild.LaunchAsync(parent, self, result, cancellationToken);
        CaptureId captureId;
        await using (WindowsBrokerPipeClient client = await child.ConnectAsync(cancellationToken))
        {
            captureId = await StartAsync(client, result, cancellationToken);
            result.OwnedSessionsWhileRecording = InterCatSessions(etw).Count;
            result.TrafficBytes = await LoopbackTrafficAsync(client, captureId, trafficSeconds, cancellationToken);

            var stopwatch = Stopwatch.StartNew();
            var stopped = Expect<BrokerStopCaptureResponse>(await client.SendAsync(
                new BrokerStopCaptureRequest(captureId, Guid.NewGuid()), cancellationToken));
            result.StopMilliseconds = stopwatch.ElapsedMilliseconds;
            result.FinalState = stopped.State.ToString();
            result.FinalMilestones = stopped.Milestones;
            result.FailureReason = stopped.FailureReason;
        }

        result.BrokerExitCode = await child.WaitForIdleExitAsync(cancellationToken);
        result.Evidence = ReadEvidence(parent, self, captureId);
        result.Passed = result.FinalMilestones?.FullyFinalized == true
            && result.BrokerExitCode == 0
            && result.Evidence is { Finalized: true, JournaledRecords: > 0 };
        result.Diagnostics = child.Diagnostics;
        return result;
    }

    private static async Task<ScenarioResult> KilledBrokerAsync(
        string parent,
        BrokerClientIdentity self,
        int trafficSeconds,
        TraceEventSessionHost etw,
        CancellationToken cancellationToken)
    {
        var result = new ScenarioResult { Name = "killed-broker-restart" };
        IReadOnlyList<string> before = InterCatSessions(etw);
        CaptureId captureId;
        await using (BrokerChild first = await BrokerChild.LaunchAsync(parent, self, result, cancellationToken))
        {
            await using WindowsBrokerPipeClient client = await first.ConnectAsync(cancellationToken);
            captureId = await StartAsync(client, result, cancellationToken);
            result.TrafficBytes = await LoopbackTrafficAsync(client, captureId, trafficSeconds, cancellationToken);
            first.Kill();
            result.Diagnostics = first.Diagnostics;
        }

        result.OrphanedSessionsAfterKill = [.. InterCatSessions(etw).Except(before, StringComparer.OrdinalIgnoreCase)];
        var restart = Stopwatch.StartNew();
        await using BrokerChild second = await BrokerChild.LaunchAsync(parent, self, result, cancellationToken);
        result.RestartToListeningMilliseconds = restart.ElapsedMilliseconds;

        // Recovery completes before the pipe exists, so the orphan must already be gone when the broker listens.
        result.OrphanedSessionsWhenListening = [.. InterCatSessions(etw).Except(before, StringComparer.OrdinalIgnoreCase)];
        await using (WindowsBrokerPipeClient client = await second.ConnectAsync(cancellationToken))
        {
            await HelloAsync(client, cancellationToken);
            var status = Expect<BrokerCaptureStatusResponse>(await client.SendAsync(
                new BrokerGetStatusRequest(captureId), cancellationToken));
            result.FinalState = status.State.ToString();
            result.FinalMilestones = status.StopMilestones;
            result.FailureReason = status.FailureReason;
        }

        result.BrokerExitCode = await second.WaitForIdleExitAsync(cancellationToken);
        result.Evidence = ReadEvidence(parent, self, captureId);
        result.Diagnostics = [.. result.Diagnostics, .. second.Diagnostics];

        // A killed broker cannot prove its journal final: the honest result is providers stopped, journal unproven,
        // the published prefix still readable, and exit 1 (partial) from the broker that recovered it.
        result.Passed = result.OrphanedSessionsAfterKill.Count == 1
            && result.OrphanedSessionsWhenListening.Count == 0
            && result.FinalMilestones is { Requested: true, ProvidersStopped: true, JournalFinalized: false }
            && result.BrokerExitCode == 1
            && result.Evidence is { Finalized: false, JournaledRecords: > 0 };
        return result;
    }

    private static async Task<CaptureId> StartAsync(
        WindowsBrokerPipeClient client,
        ScenarioResult result,
        CancellationToken cancellationToken)
    {
        await HelloAsync(client, cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        var prepared = Expect<BrokerPrepareCaptureResponse>(await client.SendAsync(
            new BrokerPrepareCaptureRequest(
                "explore",
                null,
                [],
                false,
                false,
                new BrokerCaptureQuota(120, 256L * 1024 * 1024, 1024L * 1024 * 1024),
                BrokerRetentionPolicy.StopAtLimit,
                null,
                BrokerJournalPublication.Live),
            cancellationToken));
        result.PrepareMilliseconds = stopwatch.ElapsedMilliseconds;
        if (!prepared.Prepared)
        {
            throw new InvalidOperationException($"Prepare was refused: {prepared.RefusalCode} {prepared.RefusalReason}");
        }

        result.PublicationIntervalMilliseconds = prepared.Summary.PublicationIntervalMilliseconds;
        stopwatch.Restart();
        var started = Expect<BrokerStartCaptureResponse>(await client.SendAsync(
            new BrokerStartCaptureRequest(prepared.Grant!.Token, Guid.NewGuid()),
            cancellationToken));
        result.StartMilliseconds = stopwatch.ElapsedMilliseconds;
        return started.Code == BrokerOperationCode.Started && started.CaptureId is { } captureId
            ? captureId
            : throw new InvalidOperationException($"Start failed: {started.Code} {started.FailureReason}");
    }

    private static async Task HelloAsync(WindowsBrokerPipeClient client, CancellationToken cancellationToken) =>
        Expect<BrokerHelloResponse>(await client.SendAsync(
            new BrokerHelloRequest(
                Guid.NewGuid(),
                1,
                1,
                BrokerHelloNegotiator.SupportedFeatures,
                BrokerProtocolFeature.PreparedPlanDigest),
            cancellationToken));

    /// <summary>Loopback TCP the explore profile records, with the owner lease renewed as a real client would.</summary>
    private static async Task<long> LoopbackTrafficAsync(
        WindowsBrokerPipeClient client,
        CaptureId captureId,
        int seconds,
        CancellationToken cancellationToken)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        long total = 0;
        DateTimeOffset end = DateTimeOffset.UtcNow.AddSeconds(seconds);
        byte[] payload = new byte[4096];
        Random.Shared.NextBytes(payload);
        while (DateTimeOffset.UtcNow < end)
        {
            using var sender = new TcpClient();
            Task<TcpClient> accepted = listener.AcceptTcpClientAsync(cancellationToken).AsTask();
            await sender.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            using TcpClient receiver = await accepted;
            await sender.GetStream().WriteAsync(payload, cancellationToken);
            int read = 0;
            byte[] buffer = new byte[payload.Length];
            while (read < buffer.Length)
            {
                read += await receiver.GetStream().ReadAsync(buffer.AsMemory(read), cancellationToken);
            }

            total += read;
            _ = Expect<BrokerRenewOwnerLeaseResponse>(await client.SendAsync(
                new BrokerRenewOwnerLeaseRequest(captureId), cancellationToken));
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        return total;
    }

    private static EvidenceResult ReadEvidence(string parent, BrokerClientIdentity self, CaptureId captureId)
    {
        using WindowsBrokerRoot root = WindowsBrokerRoot.Provision(new()
        {
            TrustedParentDirectory = parent,
            RootDirectoryName = RootName,
            CapturingUserSid = self.UserSid,
            Policy = BrokerRootSecurityPolicy.Production with { RequireTrustedParentOwner = false },
        });
        using WindowsBrokerRoot evidence = root.OpenCaptureDirectory(captureId);
        SessionStore store = SessionStore.OpenExisting(evidence);
        SessionManifestV1? current = store.Current;
        StoreDependency[] journals = current?.Dependencies
            .Where(dependency => dependency.Kind == StoreDependencyKind.Journal)
            .ToArray() ?? [];

        // Live publication rolls the journal into chunks; the boundary counts only the last one, so every published
        // chunk is replayed (each must be complete) to count what the capture journaled.
        long records = 0;
        foreach (StoreDependency journal in journals)
        {
            using FileStream stream = evidence.OpenOwnedFile(
                journal.Name, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
            records += JournalV1Reader.ReplayBatches(stream, static (_, _, _, _) => { }, CancellationToken.None);
        }

        return new()
        {
            Generation = current?.Generation,
            JournalChunks = journals.Length,
            JournaledRecords = records,
            JournalBytes = journals.Sum(journal => journal.LengthBytes),
            Finalized = current?.Dependencies.Any(dependency =>
                dependency.Name.StartsWith("capture-finalization", StringComparison.Ordinal)) == true,
        };
    }

    private static T Expect<T>(BrokerWireResponse response)
        where T : BrokerWireResponse =>
        response as T ?? throw new InvalidOperationException(response is BrokerErrorResponse error
            ? $"Broker error {error.Code}: {error.UserMessage}"
            : $"Unexpected broker response {response.GetType().Name}.");

    private static IReadOnlyList<string> InterCatSessions(TraceEventSessionHost etw) =>
        [.. etw.ListActiveSessionNames().Where(name => name.StartsWith("InterCat-b", StringComparison.OrdinalIgnoreCase))];

    private static string? Option(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static bool IsParentArgument(string[] args, int index) =>
        args[index] == "--parent" || (index > 0 && args[index - 1] == "--parent");

    /// <summary>One broker child process, launched the way a client must: keeping its handle to check the pipe server.</summary>
    private sealed class BrokerChild : IAsyncDisposable
    {
        private readonly Process process;
        private readonly string pipeName;
        private readonly List<string> diagnostics = [];

        private BrokerChild(Process process, string pipeName)
        {
            this.process = process;
            this.pipeName = pipeName;
        }

        public IReadOnlyList<string> Diagnostics
        {
            get
            {
                lock (diagnostics)
                {
                    return [.. diagnostics];
                }
            }
        }

        public static async Task<BrokerChild> LaunchAsync(
            string parent,
            BrokerClientIdentity owner,
            ScenarioResult result,
            CancellationToken cancellationToken)
        {
            Guid instance = Guid.NewGuid();
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in new[]
            {
                "serve-child", "--parent", parent, "--owner-sid", owner.UserSid,
                "--owner-logon-session", owner.LogonSessionId.ToString(CultureInfo.InvariantCulture),
                "--instance", instance.ToString(), "--idle-exit-seconds", "10",
            })
            {
                start.ArgumentList.Add(argument);
            }

            var stopwatch = Stopwatch.StartNew();
            Process process = Process.Start(start) ?? throw new InvalidOperationException("The broker child did not start.");
            var child = new BrokerChild(process, $"InterCat.Broker.v1.{instance:N}");
            process.ErrorDataReceived += (_, line) =>
            {
                if (line.Data is not null)
                {
                    lock (child.diagnostics)
                    {
                        child.diagnostics.Add(line.Data);
                    }
                }
            };
            process.BeginErrorReadLine();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ListenTimeout);
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(timeout.Token)) is not null)
            {
                if (line.StartsWith("listening ", StringComparison.Ordinal))
                {
                    result.LaunchToListeningMilliseconds ??= stopwatch.ElapsedMilliseconds;
                    return child;
                }
            }

            await process.WaitForExitAsync(CancellationToken.None);
            string failure = $"The broker child exited ({process.ExitCode}) before listening: "
                + string.Join(" | ", child.Diagnostics);
            process.Dispose();
            throw new InvalidOperationException(failure);
        }

        public Task<WindowsBrokerPipeClient> ConnectAsync(CancellationToken cancellationToken) =>
            WindowsBrokerPipeClient.ConnectAsync(pipeName, process.Id, TimeSpan.FromSeconds(10), cancellationToken);

        public void Kill()
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }

        public async Task<int> WaitForIdleExitAsync(CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeout.Token);
            process.WaitForExit();
            return process.ExitCode;
        }

        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            process.Dispose();
        }
    }
}

internal sealed class QualificationReport
{
    public required string Schema { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset FinishedUtc { get; set; }
    public required ProbeEnvironment Environment { get; init; }
    public required string BrokerVersion { get; init; }
    public bool Passed { get; set; }
    public ScenarioResult? CleanStop { get; set; }
    public ScenarioResult? KilledBroker { get; set; }
    public IReadOnlyList<string> LeakedSessions { get; set; } = [];
    public string? Failure { get; set; }
    public required IReadOnlyList<string> Notes { get; init; }
}

internal sealed class ScenarioResult
{
    public required string Name { get; init; }
    public bool Passed { get; set; }
    public long? LaunchToListeningMilliseconds { get; set; }
    public long? RestartToListeningMilliseconds { get; set; }
    public long? PrepareMilliseconds { get; set; }
    public long? StartMilliseconds { get; set; }
    public long? StopMilliseconds { get; set; }
    public long? PublicationIntervalMilliseconds { get; set; }
    public long TrafficBytes { get; set; }
    public int? OwnedSessionsWhileRecording { get; set; }
    public IReadOnlyList<string> OrphanedSessionsAfterKill { get; set; } = [];
    public IReadOnlyList<string> OrphanedSessionsWhenListening { get; set; } = [];
    public string? FinalState { get; set; }
    public BrokerStopMilestones? FinalMilestones { get; set; }
    public string? FailureReason { get; set; }
    public int? BrokerExitCode { get; set; }
    public EvidenceResult? Evidence { get; set; }
    public IReadOnlyList<string> Diagnostics { get; set; } = [];
}

internal sealed class EvidenceResult
{
    public long? Generation { get; init; }
    public int JournalChunks { get; init; }
    public long JournaledRecords { get; init; }
    public long JournalBytes { get; init; }
    public bool Finalized { get; init; }
}
