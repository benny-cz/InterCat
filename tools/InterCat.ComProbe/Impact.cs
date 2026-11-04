using System.Diagnostics;
using InterCat.Capture.Windows;
using InterCat.Domain;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.ComProbe;

/// <summary>
/// What collecting Microsoft-Windows-COM-Perf costs the machine, before any product path exists for it (ADR-077): a COM-heavy
/// truth workload - FX-COM-001's exchange, unpaced - run in pairs, once with no session and once while a session of this
/// probe's own collects COM-Perf machine-wide at verbose with every keyword, a consumer only counting its records, in
/// alternating order, as the AFD probe measured AFD's (ADR-072). Machine processor time is §12's measure; the consumer does no
/// more than count, so this is the collection's own cost, a lower bound on any capture that admits and stores the records.
/// A process filter holds for COM-Perf (ADR-077's addendum), so machine-wide collection bounds a scoped capture's from above.
/// </summary>
internal static class Impact
{
    public static async Task<Dictionary<string, object?>> MeasureAsync(string workload, string[] arguments, int pairs)
    {
        var trials = new List<Dictionary<string, object?>>();
        var busyDeltas = new List<double>();
        var impacts = new List<double>();
        var slowdowns = new List<double>();
        var regressions = new List<double>();
        var rates = new List<double>();
        long lost = 0;
        for (int pair = 0; pair < pairs; pair++)
        {
            // Alternating which trial runs first keeps drift from favouring either (the capture-impact harness's rule).
            bool captureFirst = pair % 2 == 1;
            Trial first = await RunAsync(workload, arguments, capture: captureFirst).ConfigureAwait(false);
            Trial second = await RunAsync(workload, arguments, capture: !captureFirst).ConfigureAwait(false);
            Trial without = captureFirst ? second : first;
            Trial with = captureFirst ? first : second;

            // The observed differences are kept as measured; classification takes the product's own non-negative paired
            // impact and throughput regression (OverheadClassCalculator), as the capture-impact harness does.
            double impact = OverheadClassCalculator.PairedImpactPercentagePoints(without.BusyPercent, with.BusyPercent);
            double regression = OverheadClassCalculator.ThroughputRegressionPercent(1 / without.Seconds, 1 / with.Seconds) ?? 0;
            busyDeltas.Add(with.BusyPercent - without.BusyPercent);
            impacts.Add(impact);
            slowdowns.Add(100 * (with.Seconds - without.Seconds) / without.Seconds);
            regressions.Add(regression);
            rates.Add(with.EventsPerSecond);
            lost += with.EventsLost;
            trials.Add(new()
            {
                ["pair"] = pair + 1,
                ["captureFirst"] = captureFirst,
                ["withoutBusyPercent"] = Math.Round(without.BusyPercent, 3),
                ["withBusyPercent"] = Math.Round(with.BusyPercent, 3),
                ["captureImpactPercentagePoints"] = Math.Round(impact, 3),
                ["withoutSeconds"] = Math.Round(without.Seconds, 3),
                ["withSeconds"] = Math.Round(with.Seconds, 3),
                ["throughputRegressionPercent"] = Math.Round(regression, 2),
                ["comPerfEventsPerSecond"] = Math.Round(with.EventsPerSecond, 0),
            });
        }

        double medianImpact = Median(impacts);
        double medianRegression = Median(regressions);
        return new()
        {
            ["workload"] = string.Join(' ', arguments.Where(argument => !argument.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal))),
            ["pairs"] = trials,
            ["medianObservedCpuDifferencePercentagePoints"] = Math.Round(Median(busyDeltas), 3),
            ["medianCaptureImpactPercentagePoints"] = Math.Round(medianImpact, 3),
            ["medianObservedWorkloadSlowdownPercent"] = Math.Round(Median(slowdowns), 2),
            ["medianThroughputRegressionPercent"] = Math.Round(medianRegression, 2),
            ["overhead"] = OverheadClassCalculator.Classify(medianImpact).ToString(),
            ["cpuTargetMet"] = medianImpact < OverheadClassCalculator.ModerateCeilingPercentagePoints,
            ["throughputTargetMet"] = medianRegression < 5,
            ["medianComPerfEventsPerSecond"] = Math.Round(Median(rates), 0),
            ["eventsLost"] = lost,
            ["notes"] = new[]
            {
                "Machine CPU is the GetSystemTimes busy share over the workload alone; the session's start and stop are outside it.",
                "The session collects COM-Perf machine-wide at verbose with every keyword, and its consumer only counts events: this is the cost of collecting COM-Perf, not of admitting or storing it, a lower bound on a product capture; a capture scoped by a process filter, which holds for it, collects no more.",
                "A pair's observed difference is kept signed; its impact, as the product classifies it, is the difference or none, and its regression the slowdown or none.",
            },
        };
    }

    private static async Task<Trial> RunAsync(string workload, string[] arguments, bool capture)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "com-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        TraceEventSession? session = null;
        Task? processing = null;
        long events = 0;
        try
        {
            if (capture)
            {
                session = new TraceEventSession("InterCat-com-impact-" + Guid.NewGuid().ToString("N"))
                {
                    StopOnDispose = true,
                    BufferSizeMB = 128,
                };
                session.Source.AllEvents += data =>
                {
                    if (data.ProviderGuid == Measure.ComPerf) Interlocked.Increment(ref events);
                };
                session.EnableProvider(Measure.ComPerf, TraceEventLevel.Verbose, ulong.MaxValue);
                TraceEventSession owned = session;
                processing = Task.Run(() => owned.Source.Process());
            }

            await Task.Delay(1_500).ConfigureAwait(false);
            if (!MachineProcessorTime.TryRead(out MachineProcessorTimeReading before, out string? problem))
            {
                throw new InvalidOperationException(problem);
            }

            long eventsBefore = Interlocked.Read(ref events);
            var clock = Stopwatch.StartNew();
            var start = new ProcessStartInfo(workload) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in arguments.Select(argument => argument.Replace("{truth}", scratch, StringComparison.Ordinal)))
            {
                start.ArgumentList.Add(argument);
            }

            using (Process run = Process.Start(start)!)
            {
                await run.WaitForExitAsync().ConfigureAwait(false);
                if (run.ExitCode != 0)
                {
                    throw new InvalidOperationException($"The truth workload exited with {run.ExitCode}.");
                }
            }

            double seconds = clock.Elapsed.TotalSeconds;
            if (!MachineProcessorTime.TryRead(out MachineProcessorTimeReading after, out problem)
                || !MachineProcessorTime.TryMeasure(before, after, out MachineProcessorTimeInterval interval))
            {
                throw new InvalidOperationException(problem ?? "The machine processor clock did not advance.");
            }

            // The session's records of the run are counted once its buffers are handed over, outside the measured window.
            if (capture)
            {
                await Task.Delay(2_500).ConfigureAwait(false);
            }

            long counted = Interlocked.Read(ref events) - eventsBefore;
            int eventsLost = session?.EventsLost ?? 0;
            return new(interval.BusyPercentage, seconds, capture ? counted / seconds : 0, eventsLost);
        }
        finally
        {
            if (session is not null)
            {
                session.Stop();
                if (processing is not null)
                {
                    await processing.ConfigureAwait(false);
                }

                session.Dispose();
            }

            Directory.Delete(scratch, recursive: true);
        }
    }

    private static double Median(List<double> values)
    {
        List<double> sorted = [.. values.Order()];
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }

    private readonly record struct Trial(double BusyPercent, double Seconds, double EventsPerSecond, int EventsLost);
}
