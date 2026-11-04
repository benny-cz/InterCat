using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.AfdProbe;

/// <summary>
/// What the Unix sockets profile costs the machine through the product itself, as RPC peers' was measured (ADR-035's fifth
/// decision): FX-UNIX-001's exchange in rounds, once with no capture, once while <c>icat record --profile unix-sockets</c>
/// records it and once while <c>icat record --profile explore</c> does, in an order that rotates each round. Machine
/// processor time over the workload is §12's measure: a capture's start - compiling its profile, enabling its session -
/// and its stop and last publication are outside it, and its live publications inside it. Each recorded session stays in
/// scratch until its counters are read, and is then deleted; only counters are written.
/// </summary>
internal static partial class ProductImpact
{
    private const string NoCapture = "none";
    private static readonly string[] Kinds = [NoCapture, "unix-sockets", "explore"];
    private static readonly Guid Afd = Guid.Parse("e53c6823-7bb8-44bb-90dc-3f86090d48a6");

    public static async Task<Dictionary<string, object?>> MeasureAsync(
        string workload,
        string icat,
        int messages,
        int rounds,
        string scratch)
    {
        double quietBusy = await QuietBusyAsync().ConfigureAwait(false);

        // One unmeasured run first: a cold workload's start would otherwise weigh on the first round's first trial.
        _ = await RunAsync(workload, icat, Math.Min(messages, 64), 0, NoCapture, null, scratch).ConfigureAwait(false);
        var trials = new List<Trial>();
        double? workloadSeconds = null;
        for (int round = 0; round < rounds; round++)
        {
            // Rotating the order keeps drift from favouring any one of the three.
            foreach (string kind in Enumerable.Range(0, Kinds.Length).Select(index => Kinds[(index + round) % Kinds.Length]))
            {
                Trial trial = await RunAsync(workload, icat, messages, round + 1, kind, workloadSeconds, scratch).ConfigureAwait(false);
                workloadSeconds ??= trial.Seconds;
                trials.Add(trial);
                string captured = trial.Capture is { } capture
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $", icat {capture.ProcessPercentagePoints:F2} pp, {capture.UnixAdmittedPerSecond:F0} AF_UNIX/s admitted, lost {capture.Lost}")
                    : "";
                Console.Error.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  round {round + 1} {kind}: {trial.BusyPercent:F2}% busy over {trial.Seconds:F1} s{captured}"));
            }
        }

        return new()
        {
            ["quietMachineBusyPercent"] = Math.Round(quietBusy, 3),
            ["workload"] = new Dictionary<string, object?>
            {
                ["verb"] = "unix-loopback",
                ["connections"] = 8,
                ["concurrency"] = 8,
                ["messagesPerConnection"] = messages,
                ["delayMilliseconds"] = 0,
            },
            ["trials"] = trials.Select(trial => trial.ToJson()).ToList(),
            ["profiles"] = Kinds.Where(kind => kind != NoCapture).ToDictionary(kind => kind, kind => Summary(trials, kind, NoCapture)),
            ["unixSocketsOverExplore"] = Summary(trials, "unix-sockets", "explore"),
            ["notes"] = new[]
            {
                "Machine CPU is the GetSystemTimes busy share over the workload alone. A capture's start (profile compilation, session enablement) and its stop, last publication and checkpoint are outside it; its live publications every 5 s are inside it.",
                "Each capture is the product's own icat record process, recording into a scratch session that is deleted once its counters are read.",
                "A profile's cost is the median over rounds of its busy share minus the same round's no-capture share; signed differences are kept, and only the class clamps a negative median at zero.",
                "unixSocketsOverExplore is the same median between the two captures: what the opt-in adds to Explore on this workload.",
                "captureProcessCpuPercentagePoints is the icat record process's own processor time over the same window, as a share of the whole machine. It excludes the kernel's work of raising events in other processes' threads, so it is a lower bound on the capture's cost.",
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
            summary["medianUnixAdmittedPerSecond"] = Math.Round(Median([.. captures.Select(trial => trial.Capture!.UnixAdmittedPerSecond)]), 0);
            summary["lossFree"] = captures.All(trial => trial.Capture!.Lost == 0);
            summary["medianCaptureProcessCpuPercentagePoints"] =
                Math.Round(Median([.. captures.Select(trial => trial.Capture!.ProcessPercentagePoints)]), 3);
            summary["medianWorkloadSecondsSlowdownPercent"] = Math.Round(Median(
                [.. trials.GroupBy(trial => trial.Round)
                    .Select(round => (Measured: round.SingleOrDefault(trial => trial.Kind == kind), Baseline: round.SingleOrDefault(trial => trial.Kind == against)))
                    .Where(pair => pair.Measured is not null && pair.Baseline is not null)
                    .Select(pair => 100 * (pair.Measured!.Seconds - pair.Baseline!.Seconds) / pair.Baseline.Seconds)]), 2);
        }

        return summary;
    }

    private static async Task<Trial> RunAsync(
        string workload,
        string icat,
        int messages,
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
                int duration = (int)Math.Ceiling((workloadSeconds ?? 5) + 12);
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
                ["unix-loopback", "--truth", truth, "--connections", "8", "--concurrency", "8", "--messages",
                    messages.ToString(CultureInfo.InvariantCulture), "--delay", "0"])
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

        private Recording(Process process, Task<string> output)
        {
            this.process = process;
            this.output = output;
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

            var recording = new Recording(process, output);

            // The name is announced before the session starts: wait until ETW lists it running.
            string sessionName = named.Task.Result;
            var waited = Stopwatch.StartNew();
            while (!TraceEventSession.GetActiveSessionNames().Contains(sessionName, StringComparer.OrdinalIgnoreCase))
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

        /// <summary>The capture process's processor time so far, user and kernel.</summary>
        public TimeSpan ProcessorTime()
        {
            process.Refresh();
            return process.TotalProcessorTime;
        }

        /// <summary>Waits for the capture to stop and publish, and reads its counters from what it wrote.</summary>
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
            double seconds = Math.Max(1e-3,
                (double)((epoch.LastDeliveredNativeTicks ?? 0) - (epoch.FirstDeliveredNativeTicks ?? 0)) / Stopwatch.Frequency);
            long delivered = epoch.Deliveries.Sum(delivery => delivery.Delivered);
            long admitted = epoch.Deliveries.Sum(delivery => delivery.Admitted);
            long unix = epoch.Deliveries.Where(delivery => delivery.ProviderId == Afd).Sum(delivery => delivery.Admitted);
            store.ReleaseSegmentReaders();
            return new(delivered / seconds, admitted / seconds, unix / seconds, lost, processPoints);
        }

        public void Dispose()
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            process.Dispose();
        }
    }

    private sealed record CaptureCounters(
        double ObservedPerSecond,
        double AdmittedPerSecond,
        double UnixAdmittedPerSecond,
        long Lost,
        double ProcessPercentagePoints);

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
                ["observedPerSecond"] = Math.Round(Capture.ObservedPerSecond, 0),
                ["admittedPerSecond"] = Math.Round(Capture.AdmittedPerSecond, 0),
                ["unixAdmittedPerSecond"] = Math.Round(Capture.UnixAdmittedPerSecond, 0),
                ["lost"] = Capture.Lost,
                ["captureProcessCpuPercentagePoints"] = Math.Round(Capture.ProcessPercentagePoints, 3),
            },
        };
    }
}
