using System.Diagnostics;
using System.Globalization;
using InterCat.Capture.Windows;
using InterCat.Domain;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.WinInetProbe;

/// <summary>
/// What capturing WinINet's exchanges costs the machine (§12, ADR-037): FX-HTTP-001 run in pairs, once with no session
/// and once while a session scoped to the workload's process collects the capture provider's records and a consumer
/// copies each one's bytes, as the product's admission would, in alternating order. Machine processor time over the
/// exchange alone is §12's measure; the workload's own time says what the client pays for its buffers being copied.
/// </summary>
internal static class Impact
{
    private const ulong Keywords = 0x0000_0603_0000_0000;
    private static readonly Guid Provider = Guid.Parse("a70ff94f-570b-4979-ba5c-e59c9feab61b");

    public static async Task<Dictionary<string, object?>> MeasureAsync(string workload, int requests, int bytes, int pairs, bool tls)
    {
        var trials = new List<Dictionary<string, object?>>();
        var busyDeltas = new List<double>();
        var slowdowns = new List<double>();
        long lost = 0, records = 0, copied = 0;
        for (int pair = 0; pair < pairs; pair++)
        {
            // Alternating which trial runs first keeps drift from favouring either (the capture-impact harness's rule).
            bool captureFirst = pair % 2 == 1;
            Trial first = await RunAsync(workload, requests, bytes, tls, capture: captureFirst);
            Trial second = await RunAsync(workload, requests, bytes, tls, capture: !captureFirst);
            Trial without = captureFirst ? second : first;
            Trial with = captureFirst ? first : second;
            busyDeltas.Add(with.BusyPercent - without.BusyPercent);
            slowdowns.Add(100 * (with.Seconds - without.Seconds) / without.Seconds);
            lost += with.EventsLost;
            records += with.Records;
            copied += with.Bytes;
            trials.Add(new()
            {
                ["pair"] = pair + 1,
                ["captureFirst"] = captureFirst,
                ["withoutBusyPercent"] = Math.Round(without.BusyPercent, 3),
                ["withBusyPercent"] = Math.Round(with.BusyPercent, 3),
                ["withoutSeconds"] = Math.Round(without.Seconds, 3),
                ["withSeconds"] = Math.Round(with.Seconds, 3),
                ["records"] = with.Records,
                ["bytes"] = with.Bytes,
            });
        }

        double median = Median(busyDeltas);
        return new()
        {
            ["schema"] = "intercat.wininet-capture-impact.v1",
            ["measuredUtc"] = DateTimeOffset.UtcNow,
            ["windows"] = Environment.OSVersion.VersionString,
            ["processors"] = Environment.ProcessorCount,
            ["workload"] = tls ? "FX-HTTP-002" : "FX-HTTP-001",
            ["tls"] = tls,
            ["requests"] = requests,
            ["maximumBodyBytes"] = bytes,
            ["pairs"] = trials,
            ["medianCaptureCpuPercentagePoints"] = Math.Round(median, 3),
            ["medianWorkloadSlowdownPercent"] = Math.Round(Median(slowdowns), 2),
            ["recordsCaptured"] = records,
            ["bytesCopied"] = copied,
            ["eventsLost"] = lost,
            ["overheadClass"] = OverheadClassCalculator.Classify(Math.Max(0, median)).ToString(),
            ["notes"] = new[]
            {
                "Machine CPU is the GetSystemTimes busy share over the exchange alone; process starts and the session's start and stop are outside it.",
                "The session is scoped to the workload's process, as a product capture of this source must be, and its consumer copies every record's bytes.",
                "Signed differences are kept; a pair can show a negative cost on a noisy machine. A negative median classifies as no cost.",
            },
        };
    }

    private static async Task<Trial> RunAsync(string workload, int requests, int bytes, bool tls, bool capture)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "InterCat-wininet-impact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var start = new ProcessStartInfo(workload)
        {
            ArgumentList =
            {
                "http-wininet", "--truth", scratch, "--requests", requests.ToString(CultureInfo.InvariantCulture),
                "--bytes", bytes.ToString(CultureInfo.InvariantCulture), "--wait-for-start",
            },
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (tls) start.ArgumentList.Add("--tls");
        using Process run = Process.Start(start) ?? throw new InvalidOperationException("The workload did not start.");
        TraceEventSession? session = null;
        Thread? reader = null;
        long records = 0, copied = 0;
        try
        {
            if (capture)
            {
                session = new TraceEventSession("InterCat-WinInetProbe-impact-" + Guid.NewGuid().ToString("N"))
                {
                    StopOnDispose = true,
                };
                session.Source.AllEvents += data =>
                {
                    if (data.ProviderGuid != Provider) return;
                    byte[] body = data.EventData();
                    Interlocked.Increment(ref records);
                    Interlocked.Add(ref copied, body.Length);
                };
                session.EnableProvider(Provider, TraceEventLevel.Informational, Keywords,
                    new TraceEventProviderOptions { ProcessIDFilter = [run.Id] });
                TraceEventSession owned = session;
                reader = new Thread(() => owned.Source.Process()) { IsBackground = true };
                reader.Start();
            }

            await Task.Delay(1_500);
            if (!MachineProcessorTime.TryRead(out MachineProcessorTimeReading before, out string? problem))
            {
                throw new InvalidOperationException(problem);
            }

            var clock = Stopwatch.StartNew();
            await run.StandardInput.WriteLineAsync("start");
            await run.StandardInput.FlushAsync();
            await run.WaitForExitAsync();
            double seconds = clock.Elapsed.TotalSeconds;
            if (!MachineProcessorTime.TryRead(out MachineProcessorTimeReading after, out problem)
                || !MachineProcessorTime.TryMeasure(before, after, out MachineProcessorTimeInterval interval))
            {
                throw new InvalidOperationException(problem ?? "The machine processor clock did not advance.");
            }

            if (run.ExitCode != 0)
            {
                throw new InvalidOperationException($"The truth workload exited with {run.ExitCode}.");
            }

            // The session's last buffers arrive on its flush timer; they are counted, and no record goes unread.
            if (session is not null) await Task.Delay(2_000);
            return new(interval.BusyPercentage, seconds, session?.EventsLost ?? 0, Interlocked.Read(ref records),
                Interlocked.Read(ref copied));
        }
        finally
        {
            if (!run.HasExited) run.Kill();
            if (session is not null)
            {
                session.Stop();
                reader?.Join(TimeSpan.FromSeconds(10));
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

    private readonly record struct Trial(double BusyPercent, double Seconds, int EventsLost, long Records, long Bytes);
}
