using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.AlpcProbe;

/// <summary>
/// What a profile costs the machine through the product itself (ADR-035's fifth decision): the RPC workload in rounds,
/// once with no capture, once while <c>icat record --profile rpc-peers</c> records it and once while
/// <c>icat record --profile explore</c> does, in an order that rotates each round. Machine processor time over the workload
/// is §12's measure, as it is for the capture-impact harness: a capture's start - compiling its profile, enabling its
/// session, the kernel's rundown - and its stop and last publication are outside it, and its live publications inside
/// it. Each recorded session stays in scratch until its counters are read, and is then deleted; only counters are written.
/// </summary>
internal static partial class ProductImpact
{
    private const string NoCapture = "none";
    private static readonly string[] Kinds = [NoCapture, "rpc-peers", "explore"];

    public static async Task<Dictionary<string, object?>> MeasureAsync(
        string workload,
        string icat,
        int calls,
        int rounds,
        string scratch)
    {
        double quietBusy = await QuietBusyAsync().ConfigureAwait(false);

        // One unmeasured run first: a cold workload's start would otherwise weigh on the first round's first trial.
        _ = await RunAsync(workload, icat, Math.Min(calls, 50), 0, NoCapture, null, scratch).ConfigureAwait(false);
        var trials = new List<Trial>();
        double? workloadSeconds = null;
        for (int round = 0; round < rounds; round++)
        {
            // Rotating the order keeps drift from favouring any one of the three.
            foreach (string kind in Enumerable.Range(0, Kinds.Length).Select(index => Kinds[(index + round) % Kinds.Length]))
            {
                Trial trial = await RunAsync(workload, icat, calls, round + 1, kind, workloadSeconds, scratch).ConfigureAwait(false);
                workloadSeconds ??= trial.Seconds;
                trials.Add(trial);
                string captured = trial.Capture is { } capture
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $", icat {capture.ProcessPercentagePoints:F2} pp, {capture.AlpcAdmittedPerSecond:F0} ALPC/s admitted, lost {capture.Lost}")
                    : "";
                Console.Error.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  round {round + 1} {kind}: {trial.BusyPercent:F2}% busy over {trial.Seconds:F1} s{captured}"));
            }
        }

        return new()
        {
            ["quietMachineBusyPercent"] = Math.Round(quietBusy, 3),
            ["trials"] = trials.Select(trial => trial.ToJson()).ToList(),
            ["profiles"] = Kinds.Where(kind => kind != NoCapture).ToDictionary(kind => kind, kind => Summary(trials, kind, NoCapture)),
            ["rpcPeersOverExplore"] = Summary(trials, "rpc-peers", "explore"),
            ["notes"] = new[]
            {
                "Machine CPU is the GetSystemTimes busy share over the workload alone. A capture's start (profile compilation, session enablement, the kernel's rundown) and its stop, last publication and checkpoint are outside it; its live publications every 5 s are inside it.",
                "Each capture is the product's own icat record process, recording into a scratch session that is deleted once its counters are read.",
                "A profile's cost is the median over rounds of its busy share minus the same round's no-capture share; signed differences are kept, and only the class clamps a negative median at zero.",
                "rpcPeersOverExplore is the same median between the two captures: what the opt-in adds to Explore on this workload.",
                "captureProcessCpuPercentagePoints is the icat record process's own processor time over the same window, as a share of the whole machine. It excludes the kernel's work of raising events in other processes' threads, so it is a lower bound on the capture's cost that background load cannot move, where the machine share is the whole cost and the noisier measure.",
            },
        };
    }

    private static Dictionary<string, object?> Summary(List<Trial> trials, string kind, string against)
    {
        List<double> deltas = [];
        foreach (IGrouping<int, Trial> round in trials.GroupBy(trial => trial.Round))
        {
            Trial? measured = round.SingleOrDefault(trial => trial.Kind == kind);
            Trial? baseline = round.SingleOrDefault(trial => trial.Kind == against);
            if (measured is not null && baseline is not null)
            {
                deltas.Add(measured.BusyPercent - baseline.BusyPercent);
            }
        }

        double median = Median(deltas);
        List<Trial> captures = [.. trials.Where(trial => trial.Kind == kind && trial.Capture is not null)];
        var summary = new Dictionary<string, object?>
        {
            ["against"] = against,
            ["busyDifferencesPercentagePoints"] = deltas.Select(delta => Math.Round(delta, 3)).ToList(),
            ["medianCpuPercentagePoints"] = Math.Round(median, 3),
        };
        if (against == NoCapture)
        {
            summary["overhead"] = OverheadClassCalculator.Classify(Math.Max(0, median)).ToString();
            summary["medianObservedPerSecond"] = Math.Round(Median([.. captures.Select(trial => trial.Capture!.ObservedPerSecond)]), 0);
            summary["medianAdmittedPerSecond"] = Math.Round(Median([.. captures.Select(trial => trial.Capture!.AdmittedPerSecond)]), 0);
            summary["medianAlpcAdmittedPerSecond"] = Math.Round(Median([.. captures.Select(trial => trial.Capture!.AlpcAdmittedPerSecond)]), 0);
            summary["lossFree"] = captures.All(trial => trial.Capture!.Lost == 0);
            summary["medianCaptureProcessCpuPercentagePoints"] =
                Math.Round(Median([.. captures.Select(trial => trial.Capture!.ProcessPercentagePoints)]), 3);
        }

        return summary;
    }

    private static async Task<Trial> RunAsync(
        string workload,
        string icat,
        int calls,
        int round,
        string kind,
        double? workloadSeconds,
        string scratch)
    {
        string truth = Path.Combine(scratch, $"truth-{round:D2}-{kind}");
        string session = Path.Combine(scratch, $"session-{round:D2}-{kind}");
        Directory.CreateDirectory(truth);
        Recording? recording = null;
        try
        {
            if (kind != NoCapture)
            {
                // Long enough for the workload and the settling before it; the capture's own stop is not measured.
                int duration = (int)Math.Ceiling((workloadSeconds ?? (calls / 20.0)) + 20);
                recording = await Recording.StartAsync(icat, session, kind, duration).ConfigureAwait(false);
                await Task.Delay(2_000).ConfigureAwait(false);
            }
            else
            {
                await Task.Delay(1_500).ConfigureAwait(false);
            }

            if (!MachineProcessorTime.TryRead(out MachineProcessorTimeReading before, out string? problem))
            {
                throw new InvalidOperationException(problem);
            }

            TimeSpan captureBefore = recording?.ProcessorTime() ?? TimeSpan.Zero;
            var clock = Stopwatch.StartNew();
            using (Process run = Process.Start(new ProcessStartInfo(
                workload,
                ["rpc-local", "--truth", truth, "--calls", calls.ToString(CultureInfo.InvariantCulture)])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })!)
            {
                await run.WaitForExitAsync().ConfigureAwait(false);
                if (run.ExitCode != 0)
                {
                    throw new InvalidOperationException($"The truth workload exited with {run.ExitCode}.");
                }
            }

            double seconds = clock.Elapsed.TotalSeconds;
            TimeSpan captureAfter = recording?.ProcessorTime() ?? TimeSpan.Zero;
            if (!MachineProcessorTime.TryRead(out MachineProcessorTimeReading after, out problem)
                || !MachineProcessorTime.TryMeasure(before, after, out MachineProcessorTimeInterval interval))
            {
                throw new InvalidOperationException(problem ?? "The machine processor clock did not advance.");
            }

            // Its share of the whole machine over the workload, in the machine measure's units.
            double processPoints = 100 * (captureAfter - captureBefore).TotalSeconds / (seconds * Environment.ProcessorCount);
            CaptureCounters? counters = recording is null
                ? null
                : await recording.FinishAsync(session, processPoints).ConfigureAwait(false);
            return new(round, kind, interval.BusyPercentage, seconds, counters);
        }
        finally
        {
            recording?.Dispose();
            Directory.Delete(truth, recursive: true);
            if (Directory.Exists(session))
            {
                Directory.Delete(session, recursive: true);
            }
        }
    }

    /// <summary>The machine's busy share over five quiet seconds, so a loaded series can be told from a quiet one.</summary>
    private static async Task<double> QuietBusyAsync()
    {
        if (!MachineProcessorTime.TryRead(out MachineProcessorTimeReading before, out string? problem))
        {
            throw new InvalidOperationException(problem);
        }

        await Task.Delay(5_000).ConfigureAwait(false);
        return MachineProcessorTime.TryRead(out MachineProcessorTimeReading after, out problem)
            && MachineProcessorTime.TryMeasure(before, after, out MachineProcessorTimeInterval interval)
            ? interval.BusyPercentage
            : throw new InvalidOperationException(problem ?? "The machine processor clock did not advance.");
    }

    private static double Median(List<double> values)
    {
        List<double> sorted = [.. values.Order()];
        return sorted.Count == 0 ? 0 : sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
    }

    [GeneratedRegex(@"under owned session (InterCat-[A-Za-z0-9-]+)\.")]
    private static partial Regex SessionName();

    /// <summary>One product capture: an <c>icat record</c> process, the session it names, and what it wrote.</summary>
    private sealed class Recording : IDisposable
    {
        private readonly Process process;
        private readonly Task<string> output;
        private readonly string sessionName;

        private Recording(Process process, Task<string> output, string sessionName)
        {
            this.process = process;
            this.output = output;
            this.sessionName = sessionName;
        }

        /// <summary>Starts the capture and returns once its session runs, so the workload starts inside it.</summary>
        public static async Task<Recording> StartAsync(string icat, string session, string profile, int duration)
        {
            var named = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var process = new Process
            {
                StartInfo = new ProcessStartInfo(
                    icat,
                    ["record", session, "--profile", profile, "--duration", duration.ToString(CultureInfo.InvariantCulture), "--json"])
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            process.ErrorDataReceived += (_, line) =>
            {
                if (line.Data is { } text && SessionName().Match(text) is { Success: true } match)
                {
                    named.TrySetResult(match.Groups[1].Value);
                }
            };
            process.Start();
            process.BeginErrorReadLine();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task finished = await Task.WhenAny(named.Task, process.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(120)))
                .ConfigureAwait(false);
            if (finished != named.Task)
            {
                process.Kill(entireProcessTree: true);
                process.Dispose();
                throw new InvalidOperationException($"icat record --profile {profile} never announced its session.");
            }

            var recording = new Recording(process, output, named.Task.Result);

            // The name is announced before the session starts: wait until ETW reports it running.
            var waited = Stopwatch.StartNew();
            while (SessionCheck.LogFileMode(recording.sessionName) is null)
            {
                if (process.HasExited || waited.Elapsed > TimeSpan.FromSeconds(60))
                {
                    recording.Dispose();
                    throw new InvalidOperationException($"icat record --profile {profile} did not start its session.");
                }

                await Task.Delay(100).ConfigureAwait(false);
            }

            return recording;
        }

        /// <summary>Waits for the capture to stop and publish, and reads its counters from what it wrote.</summary>
        /// <summary>The capture process's processor time so far, user and kernel.</summary>
        public TimeSpan ProcessorTime()
        {
            process.Refresh();
            return process.TotalProcessorTime;
        }

        public async Task<CaptureCounters> FinishAsync(string session, double processPoints)
        {
            using var patience = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await process.WaitForExitAsync(patience.Token).ConfigureAwait(false);
            string json = await output.ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(json[json.IndexOf('{', StringComparison.Ordinal)..]);
            JsonElement health = document.RootElement.GetProperty("health");
            long lost = health.GetProperty("providerReportedEventLoss").GetInt64()
                + health.GetProperty("consumerReportedBufferLoss").GetInt64()
                + health.GetProperty("applicationDrops").GetInt64();

            // The ledger's readings bound what the sources delivered, which is the capture's own span.
            SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(session));
            CoverageLedgerV1 ledger = SessionSegments.CoverageLedger(store.Root, store.Current!)
                ?? throw new InvalidOperationException("The recording published no coverage ledger.");
            CoverageEpochV1 epoch = ledger.Epochs.Single();
            double seconds = (double)(epoch.LastDeliveredNativeTicks!.Value - epoch.FirstDeliveredNativeTicks!.Value) / Stopwatch.Frequency;
            long alpc = epoch.Deliveries.Where(delivery => delivery.ProviderId == WindowsSourceCatalog.AlpcEventClass).Sum(delivery => delivery.Admitted);
            return new(
                process.ExitCode,
                health.GetProperty("observedRecords").GetInt64(),
                health.GetProperty("admittedRecords").GetInt64(),
                alpc,
                lost,
                seconds,
                processPoints);
        }

        public void Dispose()
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }

            process.Dispose();

            // A killed capture cannot stop its session; this one is the measurement's own, so it is stopped by its name.
            if (SessionCheck.LogFileMode(sessionName) is not null)
            {
                using var leftover = new TraceEventSession(sessionName, TraceEventSessionOptions.Attach);
                leftover.Stop();
            }
        }
    }

    private sealed record CaptureCounters(
        int ExitCode,
        long Observed,
        long Admitted,
        long AlpcAdmitted,
        long Lost,
        double Seconds,
        double ProcessPercentagePoints)
    {
        public double ObservedPerSecond => Seconds > 0 ? Observed / Seconds : 0;

        public double AdmittedPerSecond => Seconds > 0 ? Admitted / Seconds : 0;

        public double AlpcAdmittedPerSecond => Seconds > 0 ? AlpcAdmitted / Seconds : 0;
    }

    private sealed record Trial(int Round, string Kind, double BusyPercent, double Seconds, CaptureCounters? Capture)
    {
        public Dictionary<string, object?> ToJson() => new()
        {
            ["round"] = Round,
            ["kind"] = Kind,
            ["busyPercent"] = Math.Round(BusyPercent, 3),
            ["workloadSeconds"] = Math.Round(Seconds, 3),
            ["capture"] = Capture is null ? null : new Dictionary<string, object?>
            {
                ["exitCode"] = Capture.ExitCode,
                ["capturedSeconds"] = Math.Round(Capture.Seconds, 3),
                ["observedRecords"] = Capture.Observed,
                ["admittedRecords"] = Capture.Admitted,
                ["alpcAdmitted"] = Capture.AlpcAdmitted,
                ["lost"] = Capture.Lost,
                ["captureProcessCpuPercentagePoints"] = Math.Round(Capture.ProcessPercentagePoints, 3),
            },
        };
    }
}
