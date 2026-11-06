using System.Diagnostics;
using InterCat.Capture.Windows;
using InterCat.Domain;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.SectionProbe;

/// <summary>
/// What collecting the kernel's file view flag costs the machine, before any product path exists for it (ADR-089): a view-heavy
/// truth workload - FX-SECT-001's opener mapping and unmapping each section's view thousands of times - run in pairs, once with
/// no session and once while a private system logger of this probe's own collects the file view flag (VAMAP) machine-wide, a
/// consumer only counting the mappings and unmappings by when each was raised, in alternating order, as the window message,
/// synchronization, COM and AFD probes measured theirs. Machine processor time is §12's measure; the consumer does no more than
/// count, so this is the collection's own cost, a lower bound on any capture that admits and stores the records. Every process
/// maps views of files and images, so this is what any capture collecting the flag pays, whatever its workload. Beside the
/// machine's busy share, which whatever else the machine runs moves, each trial accounts the processor time of the workload's
/// own processes, through a job holding them, and of this probe's, which is the consumer's.
/// </summary>
internal static class Impact
{
    private static readonly Guid FileIoClass = Guid.Parse("90cbdc39-4a3e-11d1-84f4-0000f80464e3");

    // The workload's coordinator, its creator and its opener, and the console host Windows starts for each, a console process
    // started with no window.
    private const int WorkloadProcesses = 6;

    public static async Task<Dictionary<string, object?>> MeasureAsync(string workload, string[] arguments, int pairs)
    {
        var trials = new List<Dictionary<string, object?>>();
        var busyDeltas = new List<double>();
        var impacts = new List<double>();
        var slowdowns = new List<double>();
        var regressions = new List<double>();
        var workloadIncreases = new List<double>();
        var consumers = new List<double>();
        var perEvent = new List<double>();
        var attributed = new List<double>();
        var rates = new List<double>();
        var backgrounds = new List<double>();
        long lost = 0;

        // A pair run first and not kept: the consumer's first session compiles its code, and the workload's first start loads it
        // from disk, neither of which a later trial pays.
        await RunAsync(workload, arguments, collect: true).ConfigureAwait(false);
        await RunAsync(workload, arguments, collect: false).ConfigureAwait(false);
        for (int pair = 0; pair < pairs; pair++)
        {
            // Alternating which trial runs first keeps drift from favouring either (the capture-impact harness's rule).
            bool captureFirst = pair % 2 == 1;
            Trial first = await RunAsync(workload, arguments, captureFirst).ConfigureAwait(false);
            Trial second = await RunAsync(workload, arguments, !captureFirst).ConfigureAwait(false);
            Trial without = captureFirst ? second : first;
            Trial with = captureFirst ? first : second;

            // The observed differences are kept as measured; classification takes the product's own non-negative paired impact
            // and throughput regression (OverheadClassCalculator), as the capture-impact harness does.
            double impact = OverheadClassCalculator.PairedImpactPercentagePoints(without.BusyPercent, with.BusyPercent);
            double regression = OverheadClassCalculator.ThroughputRegressionPercent(1 / without.Seconds, 1 / with.Seconds) ?? 0;

            // What the session cost in processor time, the workload's own and the consumer's, apart from anything else the
            // machine ran: over the workload's time and the machine's processors, the share of the machine it took.
            double workloadIncrease = 100 * (with.WorkloadCpuSeconds - without.WorkloadCpuSeconds) / without.WorkloadCpuSeconds;
            double consumer = with.ProbeCpuSeconds - without.ProbeCpuSeconds;
            double share = 100 * (with.WorkloadCpuSeconds - without.WorkloadCpuSeconds + consumer) / (Environment.ProcessorCount * with.Seconds);
            busyDeltas.Add(with.BusyPercent - without.BusyPercent);
            impacts.Add(impact);
            slowdowns.Add(100 * (with.Seconds - without.Seconds) / without.Seconds);
            regressions.Add(regression);
            workloadIncreases.Add(workloadIncrease);
            consumers.Add(consumer);
            perEvent.Add(1e6 * consumer / Math.Max(1, with.EventsConsumed));
            attributed.Add(share);
            rates.Add(with.EventsPerSecond);
            backgrounds.Add(with.EventsInTheSecondBefore);
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
                ["withoutWorkloadCpuSeconds"] = Math.Round(without.WorkloadCpuSeconds, 3),
                ["withWorkloadCpuSeconds"] = Math.Round(with.WorkloadCpuSeconds, 3),
                ["workloadCpuIncreasePercent"] = Math.Round(workloadIncrease, 2),
                ["consumerCpuSeconds"] = Math.Round(consumer, 3),
                ["attributedImpactPercentagePoints"] = Math.Round(share, 3),
                ["viewEventsPerSecond"] = Math.Round(with.EventsPerSecond, 0),
                ["viewEventsInTheSecondBefore"] = with.EventsInTheSecondBefore,
                ["viewEventsConsumed"] = with.EventsConsumed,
            });
        }

        double medianImpact = Median(impacts);
        double medianRegression = Median(regressions);
        double medianAttributed = Median(attributed);
        return new()
        {
            ["workload"] = string.Join(' ', arguments.Where(argument => !argument.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal))),
            ["flag"] = "VAMap (0x8000)",
            ["pairs"] = trials,
            ["medianObservedCpuDifferencePercentagePoints"] = Math.Round(Median(busyDeltas), 3),
            ["medianCaptureImpactPercentagePoints"] = Math.Round(medianImpact, 3),
            ["medianObservedWorkloadSlowdownPercent"] = Math.Round(Median(slowdowns), 2),
            ["medianThroughputRegressionPercent"] = Math.Round(medianRegression, 2),
            ["overhead"] = OverheadClassCalculator.Classify(medianImpact).ToString(),
            ["cpuTargetMet"] = medianImpact < OverheadClassCalculator.ModerateCeilingPercentagePoints,
            ["throughputTargetMet"] = medianRegression < 5,
            ["medianWorkloadCpuIncreasePercent"] = Math.Round(Median(workloadIncreases), 2),
            ["medianConsumerCpuSeconds"] = Math.Round(Median(consumers), 3),
            ["medianConsumerMicrosecondsPerEvent"] = Math.Round(Median(perEvent), 2),
            ["medianAttributedImpactPercentagePoints"] = Math.Round(medianAttributed, 3),
            ["attributedOverhead"] = OverheadClassCalculator.Classify(medianAttributed).ToString(),
            ["medianViewEventsPerSecond"] = Math.Round(Median(rates), 0),
            ["medianViewEventsInTheSecondBefore"] = Math.Round(Median(backgrounds), 0),
            ["eventsLost"] = lost,
            ["notes"] = new[]
            {
                "Machine CPU is the GetSystemTimes busy share over the workload alone; the session's start and stop are outside it.",
                "A pair, one trial with the session and one without, ran first and is not kept: it warms the consumer and the workload's first start.",
                "The session is a private system logger of the probe's own collecting the kernel's file view flag, machine-wide, and its consumer only counts the view mappings and unmappings by when each was raised: this is the cost of collecting the flag, not of admitting or storing its records, a lower bound on a product capture.",
                "A trial's records a second are those raised while its workload ran, over its time; the records in the second before it are the machine's own, its other processes' views, as no workload of the trial's ran then.",
                "A pair's observed difference is kept signed; its impact, as the product classifies it, is the difference or none, and its regression the slowdown or none.",
                "The workload's processor time is its coordinator's, creator's and opener's together, and their console hosts', user and kernel, read from a job holding the six; the consumer's is this probe's own over the trial with the session, less its own over the trial without, the session's settling before the workload and its draining after it included.",
                "A pair's attributed impact is the processor time the session added, the workload's and the consumer's, over the workload's time and the machine's processors: what the busy share measures, without what else the machine ran, and with the consumer's settling and draining counted against the workload.",
            },
        };
    }

    private static async Task<Trial> RunAsync(string workload, string[] arguments, bool collect)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "sect-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        TraceEventSession? session = null;
        Task? processing = null;
        TimeSpan probeBefore = ProbeProcessorTime();

        // When each mapping and unmapping was raised, so that a trial counts the records raised while its workload ran, and in
        // the second before it, whatever the session's buffers delay.
        var stamps = new List<long>();
        try
        {
            if (collect)
            {
                // A private system logger of its own name, which this probe owns and stops, and which touches no other session.
                session = new TraceEventSession("InterCat-section-impact-" + Guid.NewGuid().ToString("N")) { StopOnDispose = true, BufferSizeMB = 256 };
                session.EnableKernelProvider(KernelTraceEventParser.Keywords.VAMap);
                session.Source.AllEvents += data =>
                {
                    if (data.TaskGuid == FileIoClass && (int)data.Opcode is 37 or 38)
                    {
                        long stamp = data.TimeStamp.Ticks;
                        lock (stamps)
                        {
                            stamps.Add(stamp);
                        }
                    }
                };

                TraceEventSession owned = session;
                processing = Task.Run(() => owned.Source.Process());
            }

            await Task.Delay(1_500).ConfigureAwait(false);
            if (!MachineProcessorTime.TryRead(out MachineProcessorTimeReading before, out string? unread))
            {
                throw new InvalidOperationException(unread);
            }

            long started = DateTime.Now.Ticks;
            var clock = Stopwatch.StartNew();
            var start = new ProcessStartInfo(workload) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in arguments.Select(argument => argument.Replace("{truth}", scratch, StringComparison.Ordinal)))
            {
                start.ArgumentList.Add(argument);
            }

            using var job = new WorkloadJob();
            using (Process run = Process.Start(start)!)
            {
                job.Add(run);
                await run.WaitForExitAsync().ConfigureAwait(false);
                if (run.ExitCode != 0)
                {
                    throw new InvalidOperationException($"The truth workload exited with {run.ExitCode}.");
                }
            }

            double seconds = clock.Elapsed.TotalSeconds;
            long ended = DateTime.Now.Ticks;
            if (!MachineProcessorTime.TryRead(out MachineProcessorTimeReading after, out unread)
                || !MachineProcessorTime.TryMeasure(before, after, out MachineProcessorTimeInterval interval))
            {
                throw new InvalidOperationException(unread ?? "The machine processor clock did not advance.");
            }

            // A process the coordinator started before the job held it would be missing from the job's time.
            (TimeSpan workloadTime, int processes) = job.Read();
            if (processes != WorkloadProcesses)
            {
                throw new InvalidOperationException($"The workload's job held {processes} processes, not its coordinator, creator and opener and their console hosts.");
            }

            // The session's records of the run are counted once its buffers are handed over, outside the measured window, and the
            // consumer's time read once it has drained them; what the session lost is read while it still runs.
            int eventsLost = 0;
            if (session is not null)
            {
                await Task.Delay(2_500).ConfigureAwait(false);
                eventsLost = session.EventsLost;
                session.Stop();
                await processing!.ConfigureAwait(false);
            }

            TimeSpan probeTime = ProbeProcessorTime() - probeBefore;
            long during;
            long ahead;
            long consumed;
            lock (stamps)
            {
                during = stamps.Count(stamp => stamp >= started && stamp <= ended);
                ahead = stamps.Count(stamp => stamp >= started - TimeSpan.TicksPerSecond && stamp < started);
                consumed = stamps.Count;
            }

            return new(interval.BusyPercentage, seconds, during / seconds, ahead, consumed, eventsLost, workloadTime.TotalSeconds, probeTime.TotalSeconds);
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

    private static TimeSpan ProbeProcessorTime()
    {
        using Process probe = Process.GetCurrentProcess();
        return probe.TotalProcessorTime;
    }

    // The median as the window message, synchronization, COM and AFD probes take it: the middle value, the upper of two.
    private static double Median(List<double> values)
    {
        List<double> sorted = [.. values.Order()];
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }

    private readonly record struct Trial(
        double BusyPercent,
        double Seconds,
        double EventsPerSecond,
        long EventsInTheSecondBefore,
        long EventsConsumed,
        int EventsLost,
        double WorkloadCpuSeconds,
        double ProbeCpuSeconds);
}
