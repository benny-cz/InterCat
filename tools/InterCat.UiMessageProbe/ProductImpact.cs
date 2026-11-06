using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.UiMessageProbe;

/// <summary>
/// What the window messages profile costs the machine through the product itself, as the synchronization objects profile's
/// was measured (ADR-083's fifth addendum): FX-WMSG-001's sender sending and posting thousands of messages unpaced, in rounds,
/// once with no capture, once while <c>icat record --profile window-messages</c> records it and once while <c>icat record
/// --profile explore</c> does, in an order that rotates each round. Machine processor time over the workload is §12's
/// measure, and the workload's own time its throughput's: a capture's start - compiling its profile, enabling its session -
/// and its stop and last publication are outside both, and its live publications inside them. Each recorded session stays in
/// scratch until its counters are read, and is then deleted, as it holds every window's messages on the machine; only
/// counters are written.
/// </summary>
internal static partial class ProductImpact
{
    private const string NoCapture = "none";
    private static readonly string[] Kinds = [NoCapture, "window-messages", "explore"];
    private static readonly Guid Win32k = Guid.Parse("8c416c79-d49b-4f01-a467-e56d3aa8234c");

    public static async Task<Dictionary<string, object?>> MeasureAsync(string workload, string icat, int copies, int posts, int rounds, string scratch)
    {
        double quietBusy = await QuietBusyAsync().ConfigureAwait(false);

        // One unmeasured run first: a cold workload's start would otherwise weigh on the first round's first trial.
        _ = await RunAsync(workload, icat, Math.Min(copies, 256), Math.Min(posts, 256), 0, NoCapture, null, scratch).ConfigureAwait(false);
        var trials = new List<Trial>();
        double? workloadSeconds = null;
        for (int round = 0; round < rounds; round++)
        {
            // Rotating the order keeps drift from favouring any one of the three.
            foreach (string kind in Enumerable.Range(0, Kinds.Length).Select(index => Kinds[(index + round) % Kinds.Length]))
            {
                Trial trial = await RunAsync(workload, icat, copies, posts, round + 1, kind, workloadSeconds, scratch).ConfigureAwait(false);
                workloadSeconds ??= trial.Seconds;
                trials.Add(trial);
                string captured = trial.Capture is { } capture
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $", icat {capture.ProcessPercentagePoints:F2} pp, {capture.Observed} observed, {capture.ApplicationDrops} dropped, lost at the source {capture.SourceLoss}, {capture.GateOmissions} omitted by a gate")
                    : "";
                Console.Error.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  round {round + 1} {kind}: {trial.BusyPercent:F2}% busy over {trial.Seconds:F2} s{captured}"));
            }
        }

        return new()
        {
            ["quietMachineBusyPercent"] = Math.Round(quietBusy, 3),
            ["workload"] = new Dictionary<string, object?>
            {
                ["verb"] = "window-message",
                ["copies"] = copies,
                ["posts"] = posts,
                ["bytes"] = 64,
            },
            ["trials"] = trials.Select(trial => trial.ToJson()).ToList(),
            ["profiles"] = Kinds.Where(kind => kind != NoCapture).ToDictionary(kind => kind, kind => Summary(trials, kind, NoCapture)),
            ["windowMessagesOverExplore"] = Summary(trials, "window-messages", "explore"),
            ["notes"] = new[]
            {
                "Machine CPU is the GetSystemTimes busy share over the workload alone. A capture's start (profile compilation, session enablement) and its stop, last publication and checkpoint are outside it; its live publications every 5 s are inside it.",
                "Each capture is the product's own icat record process, recording into a scratch session that is deleted once its counters are read: it holds every window's messages on the machine.",
                "A profile's cost is the median over rounds of its busy share minus the same round's no-capture share; signed differences are kept, and only the class clamps a negative median at zero.",
                "A profile's throughput regression is the median over rounds of the workload's messages a second against the same round's no-capture run, a faster run counting none, as OverheadClassCalculator reads one; the signed slowdown is kept beside it.",
                "windowMessagesOverExplore is the same medians between the two captures: what the opt-in adds to Explore on this workload.",
                "A capture's observed and admitted rates are over the span its sources delivered in, from its first record to its last: the whole capture, longer than the workload, so they are averages, not the workload's peak.",
                "applicationDrops are the records InterCat's own full queue dropped, and sourceLoss what ETW reported lost at a provider or in its buffers: different quantities, kept apart (§9.3); gateOmissions are the window message gate's, out of order or beyond its bound. A capture with any states the gap in its coverage; its cost figures are then those of a capture that did not keep every record.",
                "captureProcessCpuPercentagePoints is the icat record process's own processor time over the same window, as a share of the whole machine. It excludes the kernel's and the workload's own threads' work of raising events, so it is a lower bound on the capture's cost.",
            },
        };
    }

    private static Dictionary<string, object?> Summary(List<Trial> trials, string kind, string against)
    {
        List<double> deltas = [];
        List<double> slowdowns = [];
        List<double> regressions = [];
        foreach (IGrouping<int, Trial> round in trials.GroupBy(trial => trial.Round))
        {
            Trial? measured = round.SingleOrDefault(trial => trial.Kind == kind);
            Trial? baseline = round.SingleOrDefault(trial => trial.Kind == against);
            if (measured is not null && baseline is not null)
            {
                deltas.Add(measured.BusyPercent - baseline.BusyPercent);
                slowdowns.Add(100 * (measured.Seconds - baseline.Seconds) / baseline.Seconds);
                regressions.Add(OverheadClassCalculator.ThroughputRegressionPercent(1 / baseline.Seconds, 1 / measured.Seconds) ?? 0);
            }
        }

        double median = Median(deltas);
        double regression = Median(regressions);
        var summary = new Dictionary<string, object?>
        {
            ["against"] = against,
            ["busyDifferencesPercentagePoints"] = deltas.Select(delta => Math.Round(delta, 3)).ToList(),
            ["medianCpuPercentagePoints"] = Math.Round(median, 3),
            ["workloadSlowdownsPercent"] = slowdowns.Select(slowdown => Math.Round(slowdown, 2)).ToList(),
            ["medianWorkloadSlowdownPercent"] = Math.Round(Median(slowdowns), 2),
            ["medianThroughputRegressionPercent"] = Math.Round(regression, 2),
        };
        if (against == NoCapture)
        {
            List<Trial> captures = [.. trials.Where(trial => trial.Kind == kind && trial.Capture is not null)];
            summary["overhead"] = OverheadClassCalculator.Classify(Math.Max(0, median)).ToString();
            summary["cpuTargetMet"] = Math.Max(0, median) <= OverheadClassCalculator.ModerateCeilingPercentagePoints;
            summary["throughputTargetMet"] = regression < 5;
            summary["medianObservedPerSecond"] = Math.Round(Median([.. captures.Select(trial => trial.Capture!.ObservedPerSecond)]), 0);
            summary["medianAdmittedPerSecond"] = Math.Round(Median([.. captures.Select(trial => trial.Capture!.AdmittedPerSecond)]), 0);
            summary["medianWin32kAdmittedPerSecond"] = Math.Round(Median([.. captures.Select(trial => trial.Capture!.Win32kAdmittedPerSecond)]), 0);
            summary["lossFree"] = captures.All(trial => trial.Capture!.ApplicationDrops + trial.Capture!.SourceLoss == 0);
            summary["capturesWithApplicationDrops"] = captures.Count(trial => trial.Capture!.ApplicationDrops > 0);
            summary["capturesWithSourceLoss"] = captures.Count(trial => trial.Capture!.SourceLoss > 0);
            summary["capturesWithGateOmissions"] = captures.Count(trial => trial.Capture!.GateOmissions > 0);
            summary["medianDroppedSharePercent"] = Math.Round(Median([.. captures.Select(trial => trial.Capture!.Observed == 0
                ? 0 : 100d * trial.Capture!.ApplicationDrops / trial.Capture!.Observed)]), 2);
            summary["medianCaptureProcessCpuPercentagePoints"] =
                Math.Round(Median([.. captures.Select(trial => trial.Capture!.ProcessPercentagePoints)]), 3);
        }

        return summary;
    }

    private static async Task<Trial> RunAsync(string workload, string icat, int copies, int posts, int round, string kind, double? workloadSeconds,
        string scratch)
    {
        string truth = Path.Combine(scratch, $"truth-{round:D2}-{kind}");
        string session = Path.Combine(scratch, $"session-{round:D2}-{kind}");
        Recording? recording = null;
        try
        {
            if (kind != NoCapture)
            {
                // Long enough for the workload and the settling before it; the capture's own stop is not measured.
                int duration = (int)Math.Ceiling((workloadSeconds ?? 5) * 2 + 12);
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
                ["window-message", "--truth", truth, "--copies", copies.ToString(CultureInfo.InvariantCulture),
                    "--posts", posts.ToString(CultureInfo.InvariantCulture), "--bytes", "64"])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })!)
            {
                // A workload that has not finished in five minutes never will: it is stopped, and the series with it.
                using var patience = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                try
                {
                    await run.WaitForExitAsync(patience.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    run.Kill(entireProcessTree: true);
                    throw new InvalidOperationException("The truth workload did not finish in five minutes, so it was stopped.");
                }

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
            if (Directory.Exists(truth))
            {
                Directory.Delete(truth, recursive: true);
            }

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

            // What the sources lost, what InterCat's own full queue dropped and what a gate omitted are different quantities,
            // kept apart (§9.3).
            JsonElement health = document.RootElement.GetProperty("health");
            long Count(string name) => health.TryGetProperty(name, out JsonElement value) ? value.GetInt64() : 0;
            long sourceLoss = Count("providerReportedEventLoss") + Count("consumerReportedBufferLoss");
            long drops = Count("applicationDrops");
            long observed = Count("observedRecords");
            long omissions = Count("gateOmissionsBeyondBound") + Count("gateOmissionsOutOfOrder");

            // The ledger's readings bound what the sources delivered, which is the capture's own span.
            SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(session));
            CoverageLedgerV1 ledger = SessionSegments.CoverageLedger(store.Root, store.Current!)
                ?? throw new InvalidOperationException("The recording published no coverage ledger.");
            CoverageEpochV1 epoch = ledger.Epochs.Single();
            double seconds = Math.Max(1e-3,
                (double)((epoch.LastDeliveredNativeTicks ?? 0) - (epoch.FirstDeliveredNativeTicks ?? 0)) / Stopwatch.Frequency);
            long delivered = epoch.Deliveries.Sum(delivery => delivery.Delivered);
            long admitted = epoch.Deliveries.Sum(delivery => delivery.Admitted);
            long win32k = epoch.Deliveries.Where(delivery => delivery.ProviderId == Win32k).Sum(delivery => delivery.Admitted);
            store.ReleaseSegmentReaders();
            return new(delivered / seconds, admitted / seconds, win32k / seconds, observed, drops, sourceLoss, omissions, processPoints);
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

    /// <summary>
    /// A capture's rates over its sources' span, the records it observed, those its own full queue dropped, those its sources
    /// reported lost and those a gate omitted, and its process's share of the machine over the workload.
    /// </summary>
    private sealed record CaptureCounters(
        double ObservedPerSecond,
        double AdmittedPerSecond,
        double Win32kAdmittedPerSecond,
        long Observed,
        long ApplicationDrops,
        long SourceLoss,
        long GateOmissions,
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
                ["win32kAdmittedPerSecond"] = Math.Round(Capture.Win32kAdmittedPerSecond, 0),
                ["observedRecords"] = Capture.Observed,
                ["applicationDrops"] = Capture.ApplicationDrops,
                ["sourceLoss"] = Capture.SourceLoss,
                ["gateOmissions"] = Capture.GateOmissions,
                ["captureProcessCpuPercentagePoints"] = Math.Round(Capture.ProcessPercentagePoints, 3),
            },
        };
    }
}
