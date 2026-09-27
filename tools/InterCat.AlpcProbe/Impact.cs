using System.Diagnostics;
using System.Globalization;
using InterCat.Capture.Windows;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.AlpcProbe;

/// <summary>
/// What collecting the kernel's ALPC flag group costs the machine, before any product path exists for it: the same RPC
/// workload run in pairs, once with no session and once while a private system logger collects ALPC events and a
/// consumer counts them, in alternating order. Machine processor time is §12's measure; the consumer does no more than
/// count, so this is the collection's own cost - a lower bound on any capture that admits and stores the records.
/// </summary>
internal static class Impact
{
    public static async Task<Dictionary<string, object?>> MeasureAsync(string workload, int calls, int pairs)
    {
        var trials = new List<Dictionary<string, object?>>();
        var busyDeltas = new List<double>();
        var slowdowns = new List<double>();
        var rates = new List<double>();
        long lost = 0;
        for (int pair = 0; pair < pairs; pair++)
        {
            // Alternating which trial runs first keeps drift from favouring either (the capture-impact harness's rule).
            bool captureFirst = pair % 2 == 1;
            Trial first = await RunAsync(workload, calls, capture: captureFirst).ConfigureAwait(false);
            Trial second = await RunAsync(workload, calls, capture: !captureFirst).ConfigureAwait(false);
            Trial without = captureFirst ? second : first;
            Trial with = captureFirst ? first : second;
            busyDeltas.Add(with.BusyPercent - without.BusyPercent);
            slowdowns.Add(100 * (with.Seconds - without.Seconds) / without.Seconds);
            rates.Add(with.AlpcEventsPerSecond);
            lost += with.EventsLost;
            trials.Add(new()
            {
                ["pair"] = pair + 1,
                ["captureFirst"] = captureFirst,
                ["withoutBusyPercent"] = Math.Round(without.BusyPercent, 3),
                ["withBusyPercent"] = Math.Round(with.BusyPercent, 3),
                ["withoutSeconds"] = Math.Round(without.Seconds, 3),
                ["withSeconds"] = Math.Round(with.Seconds, 3),
                ["alpcEventsPerSecond"] = Math.Round(with.AlpcEventsPerSecond, 0),
            });
        }

        return new()
        {
            ["pairs"] = trials,
            ["medianCaptureCpuPercentagePoints"] = Math.Round(Median(busyDeltas), 3),
            ["medianWorkloadSlowdownPercent"] = Math.Round(Median(slowdowns), 2),
            ["medianAlpcEventsPerSecond"] = Math.Round(Median(rates), 0),
            ["eventsLost"] = lost,
            ["notes"] = new[]
            {
                "Machine CPU is the GetSystemTimes busy share over the workload alone; the session's start and stop are outside it.",
                "The consumer only counts events, so this is the cost of collecting ALPC, not of admitting or storing it: a lower bound on a product capture.",
                "Signed differences are kept; a pair can show a negative cost on a noisy machine.",
            },
        };
    }

    private static async Task<Trial> RunAsync(string workload, int calls, bool capture)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "InterCat-alpc-impact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        TraceEventSession? session = null;
        Task? processing = null;
        long events = 0;
        try
        {
            if (capture)
            {
                session = new TraceEventSession("InterCat-alpc-impact-" + Guid.NewGuid().ToString("N"))
                {
                    StopOnDispose = true,
                    BufferSizeMB = 128,
                };
                session.EnableKernelProvider(KernelTraceEventParser.Keywords.AdvancedLocalProcedureCalls);
                session.Source.Kernel.ALPCSendMessage += _ => Interlocked.Increment(ref events);
                session.Source.Kernel.ALPCReceiveMessage += _ => Interlocked.Increment(ref events);
                session.Source.Kernel.ALPCWaitForReply += _ => Interlocked.Increment(ref events);
                session.Source.Kernel.ALPCWaitForNewMessage += _ => Interlocked.Increment(ref events);
                session.Source.Kernel.ALPCUnwait += _ => Interlocked.Increment(ref events);
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
            using (Process run = Process.Start(new ProcessStartInfo(
                workload,
                ["rpc-local", "--truth", scratch, "--calls", calls.ToString(CultureInfo.InvariantCulture)])
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
            if (!MachineProcessorTime.TryRead(out MachineProcessorTimeReading after, out problem)
                || !MachineProcessorTime.TryMeasure(before, after, out MachineProcessorTimeInterval interval))
            {
                throw new InvalidOperationException(problem ?? "The machine processor clock did not advance.");
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

    private readonly record struct Trial(double BusyPercent, double Seconds, double AlpcEventsPerSecond, int EventsLost);
}
