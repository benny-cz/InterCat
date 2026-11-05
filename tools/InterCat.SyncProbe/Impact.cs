using System.Diagnostics;
using InterCat.Capture.Windows;
using InterCat.Domain;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.SyncProbe;

/// <summary>
/// What collecting the kernel's object manager groups costs the machine, before any product path exists for them (ADR-082):
/// a handle-heavy truth workload - FX-SYNC-001's opener opening and closing each object by its name thousands of times, then
/// its rounds unpaced - run in pairs, once with no session and once while a private system logger of this probe's own
/// collects the handle group machine-wide, and in a second series the object group beside it, a consumer only counting the
/// object manager's records, in alternating order, as the COM and AFD probes measured theirs (ADR-077, ADR-072). The session
/// starts a system logger with the kernel's process flag, the least a system logger takes, and its groups are added to its
/// masks. Machine processor time is §12's measure; the consumer does no more than count, so this is the collection's own
/// cost, a lower bound on any capture that admits and stores the records. A kernel group has no process filter, so this is
/// what any capture collecting it pays.
/// </summary>
internal static class Impact
{
    private static readonly Guid ObjectManagerClass = Guid.Parse("89497f50-effe-4440-8cf2-ce6b1cdcaca7");

    public static async Task<Dictionary<string, object?>> MeasureAsync(string workload, string[] arguments, int pairs, uint[] groups)
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
            Trial first = await RunAsync(workload, arguments, captureFirst ? groups : null).ConfigureAwait(false);
            Trial second = await RunAsync(workload, arguments, captureFirst ? null : groups).ConfigureAwait(false);
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
                ["objectManagerEventsPerSecond"] = Math.Round(with.EventsPerSecond, 0),
            });
        }

        double medianImpact = Median(impacts);
        double medianRegression = Median(regressions);
        return new()
        {
            ["workload"] = string.Join(' ', arguments.Where(argument => !argument.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal))),
            ["groups"] = groups.Select(static group => group == GroupMask.ObjectHandles ? "PERF_OB_HANDLE" : "PERF_OB_OBJECT").ToArray(),
            ["pairs"] = trials,
            ["medianObservedCpuDifferencePercentagePoints"] = Math.Round(Median(busyDeltas), 3),
            ["medianCaptureImpactPercentagePoints"] = Math.Round(medianImpact, 3),
            ["medianObservedWorkloadSlowdownPercent"] = Math.Round(Median(slowdowns), 2),
            ["medianThroughputRegressionPercent"] = Math.Round(medianRegression, 2),
            ["overhead"] = OverheadClassCalculator.Classify(medianImpact).ToString(),
            ["cpuTargetMet"] = medianImpact < OverheadClassCalculator.ModerateCeilingPercentagePoints,
            ["throughputTargetMet"] = medianRegression < 5,
            ["medianObjectManagerEventsPerSecond"] = Math.Round(Median(rates), 0),
            ["eventsLost"] = lost,
            ["notes"] = new[]
            {
                "Machine CPU is the GetSystemTimes busy share over the workload alone; the session's start and stop are outside it.",
                "The session is a private system logger started by the kernel's process flag, with the object manager's groups added to its masks, machine-wide, and its consumer only counts the object manager's records: this is the cost of collecting the groups, not of admitting or storing them, a lower bound on a product capture. A kernel group has no process filter.",
                "A pair's observed difference is kept signed; its impact, as the product classifies it, is the difference or none, and its regression the slowdown or none.",
            },
        };
    }

    private static async Task<Trial> RunAsync(string workload, string[] arguments, uint[]? groups)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "sync-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        TraceEventSession? session = null;
        Task? processing = null;
        long events = 0;
        try
        {
            if (groups is not null)
            {
                string name = "InterCat-sync-impact-" + Guid.NewGuid().ToString("N");
                // The kernel flags first, which start a system logger, then the groups; the source is opened only after
                // both, as TraceEvent refuses kernel flags in a session whose source is open.
                session = new TraceEventSession(name) { StopOnDispose = true, BufferSizeMB = 256 };
                session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process);
                if (GroupMask.Add(name, groups) is { } problem)
                {
                    throw new InvalidOperationException("The object manager's groups could not be added to the probe's session. " + problem);
                }

                session.Source.AllEvents += data =>
                {
                    if (data.TaskGuid == ObjectManagerClass) Interlocked.Increment(ref events);
                };

                TraceEventSession owned = session;
                processing = Task.Run(() => owned.Source.Process());
            }

            await Task.Delay(1_500).ConfigureAwait(false);
            if (!MachineProcessorTime.TryRead(out MachineProcessorTimeReading before, out string? unread))
            {
                throw new InvalidOperationException(unread);
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
            if (!MachineProcessorTime.TryRead(out MachineProcessorTimeReading after, out unread)
                || !MachineProcessorTime.TryMeasure(before, after, out MachineProcessorTimeInterval interval))
            {
                throw new InvalidOperationException(unread ?? "The machine processor clock did not advance.");
            }

            // The session's records of the run are counted once its buffers are handed over, outside the measured window.
            if (session is not null)
            {
                await Task.Delay(2_500).ConfigureAwait(false);
            }

            long counted = Interlocked.Read(ref events) - eventsBefore;
            int eventsLost = session?.EventsLost ?? 0;
            return new(interval.BusyPercentage, seconds, session is not null ? counted / seconds : 0, eventsLost);
        }
        finally
        {
            session?.Stop();
            if (processing is not null)
            {
                await processing.ConfigureAwait(false);
            }

            session?.Dispose();
            Directory.Delete(scratch, recursive: true);
        }
    }

    // The median as the COM and AFD probes take it: the middle value, the upper of two.
    private static double Median(List<double> values)
    {
        List<double> sorted = [.. values.Order()];
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }

    private readonly record struct Trial(double BusyPercent, double Seconds, double EventsPerSecond, int EventsLost);
}
