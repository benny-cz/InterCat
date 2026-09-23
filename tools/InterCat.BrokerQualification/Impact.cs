using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using InterCat.Capture.Windows;
using InterCat.CaptureBroker;
using InterCat.Domain;

/// <summary>
/// Capture impact of the broker at its compiled journal-publication cadence (plan revisions 78 and 82). The same seeded
/// workload runs with no capture, with a broker capture that publishes once on stop, and with one that publishes live;
/// trial order rotates inside each triplet. The measured window is the workload plus a grace second, while the capture
/// records and (for Live) publishes; prepare, start and stop are outside it. Live minus OnStop is the cost of the
/// cadence itself.
/// </summary>
internal static partial class Qualification
{
    private const double CpuTargetPercentagePoints = 5;
    private const double ThroughputTargetPercent = 5;
    private const int Connections = 4;
    private const int MessagesPerConnection = 768;
    private const int MaximumMessageBytes = 4_096;
    private const int InterMessageDelayMilliseconds = 15;
    private const int Concurrency = 4;
    private const int Seed = 20_260_923;
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(1);

    private enum Variant
    {
        NoCapture,
        BrokerOnStop,
        BrokerLive,
    }

    public static async Task<int> ImpactAsync(string[] args, CancellationToken cancellationToken)
    {
        string? output = Option(args, "--output");
        int triplets = int.TryParse(Option(args, "--triplets") ?? "5", NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            && parsed is >= 1 and <= 15 ? parsed : -1;
        string? workload = Option(args, "--workload") ?? FindWorkload();
        if (output is null || triplets < 0 || workload is null || !File.Exists(workload))
        {
            Console.Error.WriteLine(
                "Usage: InterCat.BrokerQualification impact --output <new directory> [--triplets <1-15>] [--workload <InterCat.TestWorkloads.exe>]");
            Console.Error.WriteLine("Build InterCat.TestWorkloads first, or pass --workload.");
            return 2;
        }

        var etw = new TraceEventSessionHost();
        if (etw.IsElevated != true)
        {
            Console.Error.WriteLine("Impact measurement starts kernel ETW sessions and must run elevated.");
            return 3;
        }

        output = Path.GetFullPath(output);
        if (Directory.Exists(output) || File.Exists(output))
        {
            Console.Error.WriteLine($"'{output}' already exists; measurement writes only to a new directory.");
            return 2;
        }

        Directory.CreateDirectory(output);
        string parent = Path.Combine(output, "root-parent");
        Directory.CreateDirectory(parent);
        BrokerClientIdentity self = WindowsBrokerTokenIdentity.ReadCurrentProcess();
        var trials = new List<ImpactTrial>();
        var scenario = new ScenarioResult { Name = "impact" };
        IReadOnlyList<string> sessionsBefore = InterCatSessions(etw);
        string? failure = null;
        long? publicationInterval = null;
        try
        {
            await using BrokerChild child = await BrokerChild.LaunchAsync(parent, self, scenario, cancellationToken);
            await using WindowsBrokerPipeClient client = await child.ConnectAsync(cancellationToken);
            await HelloAsync(client, cancellationToken);
            Variant[] order = [Variant.NoCapture, Variant.BrokerOnStop, Variant.BrokerLive];
            for (int triplet = 1; triplet <= triplets; triplet++)
            {
                for (int position = 0; position < order.Length; position++)
                {
                    Variant variant = order[(position + triplet - 1) % order.Length];
                    Console.Error.WriteLine($"  triplet {triplet}/{triplets}: {variant}");
                    ImpactTrial trial = await RunTrialAsync(
                        child, client, parent, self, workload, output, triplet, variant, cancellationToken);
                    if (variant == Variant.BrokerLive)
                    {
                        publicationInterval ??= trial.PublicationIntervalMilliseconds;
                    }

                    trials.Add(trial);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failure = exception.ToString();
        }

        IReadOnlyList<string> leaked = [.. InterCatSessions(etw).Except(sessionsBefore, StringComparer.OrdinalIgnoreCase)];
        ImpactSummary? live = Summarize(trials, Variant.BrokerLive, Variant.NoCapture);
        ImpactSummary? onStop = Summarize(trials, Variant.BrokerOnStop, Variant.NoCapture);
        ImpactSummary? cadence = Summarize(trials, Variant.BrokerLive, Variant.BrokerOnStop);
        bool evidenceValid = trials.Where(trial => trial.Variant != nameof(Variant.NoCapture))
            .All(trial => trial.FullyFinalized && trial.JournaledRecords > 0);
        var report = new ImpactReport
        {
            Schema = "intercat.broker-capture-impact.v1",
            CompletedUtc = DateTimeOffset.UtcNow,
            Environment = CapabilityInventoryProbe.DescribeEnvironment(etw.IsElevated),
            BrokerVersion = BrokerProcess.Version,
            Workload = $"tcp-loopback seed {Seed}, {Connections} connections x {MessagesPerConnection} messages, "
                + $"<= {MaximumMessageBytes} bytes, {InterMessageDelayMilliseconds} ms pacing, concurrency {Concurrency}",
            Profile = "explore",
            PublicationIntervalMilliseconds = publicationInterval,
            LogicalProcessors = Environment.ProcessorCount,
            Triplets = triplets,
            Trials = trials,
            LiveVersusNoCapture = live,
            OnStopVersusNoCapture = onStop,
            LiveVersusOnStop = cadence,
            LiveOverhead = live is null ? null : OverheadClassCalculator.Classify(live.MedianCpuImpactPercentagePoints).ToString(),
            LeakedSessions = leaked,
            EvidenceValid = evidenceValid,
            Failure = failure,
            TargetsMet = failure is null
                && leaked.Count == 0
                && evidenceValid
                && live is { MedianCpuImpactPercentagePoints: < CpuTargetPercentagePoints, MedianThroughputRegressionPercent: < ThroughputTargetPercent },
            Notes =
            [
                "Total-machine CPU is the GetSystemTimes busy share over the workload plus a one-second grace; the broker, ETW and the workload are all inside it.",
                "Paired figures compare trials within one triplet; trial order rotates across triplets. Every trial is kept.",
                "Signed differences are kept; only the classification input is clamped at zero, as in the IC-010a impact artifact.",
                "No benchmark can prove the machine was otherwise quiet; this ran on the development machine, not an isolated lab host.",
                "Broker process CPU is reported beside machine CPU because it attributes cost to the broker directly; it excludes the kernel-side ETW cost, which only the machine figure contains.",
            ],
        };
        await File.WriteAllTextAsync(Path.Combine(output, "impact.json"), JsonSerializer.Serialize(report, Json), CancellationToken.None);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Live vs no capture: {live?.MedianCpuImpactPercentagePoints:F2} CPU pp, {live?.MedianThroughputRegressionPercent:F2}% throughput; "
            + $"Live vs OnStop: {cadence?.MedianObservedCpuDifferencePercentagePoints:F2} CPU pp. Targets met: {report.TargetsMet}."));
        Console.WriteLine(Path.Combine(output, "impact.json"));
        return report.TargetsMet ? 0 : 1;
    }

    private static async Task<ImpactTrial> RunTrialAsync(
        BrokerChild child,
        WindowsBrokerPipeClient client,
        string parent,
        BrokerClientIdentity self,
        string workload,
        string output,
        int triplet,
        Variant variant,
        CancellationToken cancellationToken)
    {
        CaptureId? captureId = null;
        long? interval = null;
        if (variant != Variant.NoCapture)
        {
            (captureId, interval) = await PrepareAndStartAsync(
                client,
                variant == Variant.BrokerLive ? BrokerJournalPublication.Live : BrokerJournalPublication.OnStop,
                cancellationToken);
        }

        using var renew = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task renewing = captureId is { } id ? RenewLeaseAsync(client, id, renew.Token) : Task.CompletedTask;
        string truth = Path.Combine(output, "truth", $"t{triplet:D2}-{variant}");
        Directory.CreateDirectory(truth);
        if (!MachineProcessorTime.TryRead(out MachineProcessorTimeReading before, out string? unavailable))
        {
            throw new InvalidOperationException($"Machine processor time is unavailable: {unavailable}");
        }

        TimeSpan brokerBefore = child.ProcessorTime;
        long started = Stopwatch.GetTimestamp();
        int exit = await RunWorkloadAsync(workload, truth, cancellationToken);
        TimeSpan workloadElapsed = Stopwatch.GetElapsedTime(started);
        await Task.Delay(Grace, cancellationToken);
        if (!MachineProcessorTime.TryRead(out MachineProcessorTimeReading after, out unavailable)
            || !MachineProcessorTime.TryMeasure(before, after, out MachineProcessorTimeInterval processor))
        {
            throw new InvalidOperationException($"Machine processor time could not be measured: {unavailable}");
        }

        TimeSpan brokerProcessor = child.ProcessorTime - brokerBefore;
        await renew.CancelAsync();
        await renewing;
        if (exit != 0)
        {
            throw new InvalidOperationException($"The workload exited with {exit}.");
        }

        var trial = new ImpactTrial
        {
            Triplet = triplet,
            Variant = variant.ToString(),
            MachineBusyPercentage = processor.BusyPercentage,
            BrokerProcessorMilliseconds = brokerProcessor.TotalMilliseconds,
            WorkloadMilliseconds = workloadElapsed.TotalMilliseconds,
            MessagesPerSecond = Connections * MessagesPerConnection / workloadElapsed.TotalSeconds,
            PublicationIntervalMilliseconds = interval,
        };
        if (captureId is not { } capture)
        {
            return trial;
        }

        var stopped = Expect<BrokerStopCaptureResponse>(await client.SendAsync(
            new BrokerStopCaptureRequest(capture, Guid.NewGuid()), cancellationToken));
        EvidenceResult evidence = ReadEvidence(parent, self, capture);
        return trial with
        {
            FullyFinalized = stopped.Milestones.FullyFinalized,
            JournaledRecords = evidence.JournaledRecords,
            JournalChunks = evidence.JournalChunks,
        };
    }

    private static async Task<(CaptureId CaptureId, long IntervalMilliseconds)> PrepareAndStartAsync(
        WindowsBrokerPipeClient client,
        BrokerJournalPublication publication,
        CancellationToken cancellationToken)
    {
        var prepared = Expect<BrokerPrepareCaptureResponse>(await client.SendAsync(
            new BrokerPrepareCaptureRequest(
                "explore",
                null,
                [],
                false,
                false,
                new BrokerCaptureQuota(300, 1024L * 1024 * 1024, 1024L * 1024 * 1024),
                BrokerRetentionPolicy.StopAtLimit,
                null,
                publication),
            cancellationToken));
        if (!prepared.Prepared)
        {
            throw new InvalidOperationException($"Prepare was refused: {prepared.RefusalCode} {prepared.RefusalReason}");
        }

        var started = Expect<BrokerStartCaptureResponse>(await client.SendAsync(
            new BrokerStartCaptureRequest(prepared.Grant!.Token, Guid.NewGuid()),
            cancellationToken));
        return started.Code == BrokerOperationCode.Started && started.CaptureId is { } captureId
            ? (captureId, prepared.Summary.PublicationIntervalMilliseconds)
            : throw new InvalidOperationException($"Start failed: {started.Code} {started.FailureReason}");
    }

    private static async Task RenewLeaseAsync(WindowsBrokerPipeClient client, CaptureId captureId, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                _ = Expect<BrokerRenewOwnerLeaseResponse>(await client.SendAsync(
                    new BrokerRenewOwnerLeaseRequest(captureId), cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task<int> RunWorkloadAsync(string workload, string truth, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(workload) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in new[]
        {
            "tcp-loopback", "--truth", truth,
            "--seed", Seed.ToString(CultureInfo.InvariantCulture),
            "--connections", Connections.ToString(CultureInfo.InvariantCulture),
            "--messages", MessagesPerConnection.ToString(CultureInfo.InvariantCulture),
            "--bytes", MaximumMessageBytes.ToString(CultureInfo.InvariantCulture),
            "--delay", InterMessageDelayMilliseconds.ToString(CultureInfo.InvariantCulture),
            "--concurrency", Concurrency.ToString(CultureInfo.InvariantCulture),
        })
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("The workload did not start.");
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }

    private static ImpactSummary? Summarize(IReadOnlyList<ImpactTrial> trials, Variant measured, Variant reference)
    {
        var pairs = trials
            .GroupBy(trial => trial.Triplet)
            .Select(group => (
                Measured: group.SingleOrDefault(trial => trial.Variant == measured.ToString()),
                Reference: group.SingleOrDefault(trial => trial.Variant == reference.ToString())))
            .Where(pair => pair.Measured is not null && pair.Reference is not null)
            .ToArray();
        if (pairs.Length == 0)
        {
            return null;
        }

        double[] observed = [.. pairs.Select(pair => pair.Measured!.MachineBusyPercentage - pair.Reference!.MachineBusyPercentage)];
        double[] regression = [.. pairs.Select(pair =>
            100d * (pair.Reference!.MessagesPerSecond - pair.Measured!.MessagesPerSecond) / pair.Reference.MessagesPerSecond)];
        return new()
        {
            Measured = measured.ToString(),
            Reference = reference.ToString(),
            Pairs = pairs.Length,
            MedianObservedCpuDifferencePercentagePoints = Median(observed),
            MedianCpuImpactPercentagePoints = Math.Max(0, Median(observed)),
            MedianObservedThroughputRegressionPercent = Median(regression),
            MedianThroughputRegressionPercent = Math.Max(0, Median(regression)),
            MedianMeasuredBrokerProcessorMilliseconds = Median([.. pairs.Select(pair => pair.Measured!.BrokerProcessorMilliseconds)]),
            MedianReferenceBrokerProcessorMilliseconds = Median([.. pairs.Select(pair => pair.Reference!.BrokerProcessorMilliseconds)]),
            MedianBrokerProcessorPercentagePointsOfMachine = Median([.. pairs.Select(pair =>
                100d * (pair.Measured!.BrokerProcessorMilliseconds - pair.Reference!.BrokerProcessorMilliseconds)
                / ((pair.Measured.WorkloadMilliseconds + Grace.TotalMilliseconds) * Environment.ProcessorCount))]),
        };
    }

    private static double Median(double[] values)
    {
        double[] ordered = [.. values.Order()];
        int middle = ordered.Length / 2;
        return ordered.Length % 2 == 0 ? (ordered[middle - 1] + ordered[middle]) / 2d : ordered[middle];
    }

    private static string? FindWorkload()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            foreach (string configuration in (string[])["Release", "Debug"])
            {
                string candidate = Path.Combine(
                    directory.FullName, "src", "InterCat.TestWorkloads", "bin", configuration, "net10.0", "InterCat.TestWorkloads.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}

internal sealed record ImpactTrial
{
    public required int Triplet { get; init; }
    public required string Variant { get; init; }
    public required double MachineBusyPercentage { get; init; }

    /// <summary>The broker process's own user+kernel CPU over the measured window; ETW's kernel-side cost is not in it.</summary>
    public required double BrokerProcessorMilliseconds { get; init; }
    public required double WorkloadMilliseconds { get; init; }
    public required double MessagesPerSecond { get; init; }
    public long? PublicationIntervalMilliseconds { get; init; }
    public bool FullyFinalized { get; init; }
    public long JournaledRecords { get; init; }
    public int JournalChunks { get; init; }
}

internal sealed record ImpactSummary
{
    public required string Measured { get; init; }
    public required string Reference { get; init; }
    public required int Pairs { get; init; }
    public required double MedianObservedCpuDifferencePercentagePoints { get; init; }
    public required double MedianCpuImpactPercentagePoints { get; init; }
    public required double MedianObservedThroughputRegressionPercent { get; init; }
    public required double MedianThroughputRegressionPercent { get; init; }

    /// <summary>The broker process's own CPU: far less noisy than machine CPU on a shared host, but blind to ETW's kernel work.</summary>
    public required double MedianMeasuredBrokerProcessorMilliseconds { get; init; }
    public required double MedianReferenceBrokerProcessorMilliseconds { get; init; }
    public required double MedianBrokerProcessorPercentagePointsOfMachine { get; init; }
}

internal sealed class ImpactReport
{
    public required string Schema { get; init; }
    public required DateTimeOffset CompletedUtc { get; init; }
    public required ProbeEnvironment Environment { get; init; }
    public required string BrokerVersion { get; init; }
    public required string Workload { get; init; }
    public required string Profile { get; init; }
    public long? PublicationIntervalMilliseconds { get; init; }
    public required int LogicalProcessors { get; init; }
    public required int Triplets { get; init; }
    public required IReadOnlyList<ImpactTrial> Trials { get; init; }
    public ImpactSummary? LiveVersusNoCapture { get; init; }
    public ImpactSummary? OnStopVersusNoCapture { get; init; }
    public ImpactSummary? LiveVersusOnStop { get; init; }
    public string? LiveOverhead { get; init; }
    public required IReadOnlyList<string> LeakedSessions { get; init; }
    public required bool EvidenceValid { get; init; }
    public string? Failure { get; init; }
    public required bool TargetsMet { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}
