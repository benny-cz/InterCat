using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using InterCat.Capture.Windows;
using InterCat.Domain;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.SdkProbe;

/// <summary>
/// What keeping the cooperative SDK's content costs the machine (§12, ADR-107), measured as WinINet's capture impact was: a
/// burst publishing each message with its bytes, run in pairs - once with no session, and once while a session scoped to
/// the burst's process asks for EventSource's keywords and every one of the SDK's, content's included, and a consumer copies
/// each record's bytes, as the product's admission would - in alternating order, each burst paced to the rate a capture keeps
/// every single message at. Machine processor time over the burst alone is §12's measure; the burst's own time says what the
/// application pays. Then the scope: a decoy publishing with its bytes beside a named burst, none of whose records may
/// reach the session, and which no session asks to say anything.
/// </summary>
internal static class ContentImpact
{
    /// <summary>EventSource's keywords and every one of the SDK's, content's included: what a capture keeping content asks for.</summary>
    private const ulong Keywords = 0x0000_F000_FFFF_FFFF;

    /// <summary>The rate each burst is held to, in calls a second: the one a capture keeps every single message at (sdk-v1 §8).</summary>
    private const int CallsPerSecond = 50_000;

    /// <summary>The bytes each burst's message holds, its content said whole (sdk-burst's).</summary>
    private const int MessageBytes = 256;

    public static async Task<Dictionary<string, object?>> MeasureAsync(string workload, int messages, int pairs)
    {
        var trials = new List<Dictionary<string, object?>>();
        var busyDeltas = new List<double>();
        var slowdowns = new List<double>();
        long lost = 0, records = 0, contents = 0, copied = 0, said = 0;
        for (int pair = 0; pair < pairs; pair++)
        {
            // Alternating which trial runs first keeps drift from favouring either (the capture-impact harness's rule).
            bool captureFirst = pair % 2 == 1;
            Trial first = await RunAsync(workload, messages, capture: captureFirst).ConfigureAwait(false);
            Trial second = await RunAsync(workload, messages, capture: !captureFirst).ConfigureAwait(false);
            Trial without = captureFirst ? second : first;
            Trial with = captureFirst ? first : second;
            busyDeltas.Add(with.BusyPercent - without.BusyPercent);
            slowdowns.Add(100 * (with.Seconds - without.Seconds) / without.Seconds);
            lost += with.EventsLost;
            records += with.Records;
            contents += with.Contents;
            copied += with.Bytes;
            said += with.ContentSaid;
            if (without.ContentSaid != 0)
            {
                throw new InvalidOperationException("A burst said content with no session asking for it: another session listens.");
            }

            trials.Add(new()
            {
                ["pair"] = pair + 1,
                ["captureFirst"] = captureFirst,
                ["withoutBusyPercent"] = Math.Round(without.BusyPercent, 3),
                ["withBusyPercent"] = Math.Round(with.BusyPercent, 3),
                ["withoutSeconds"] = Math.Round(without.Seconds, 3),
                ["withSeconds"] = Math.Round(with.Seconds, 3),
                ["records"] = with.Records,
                ["contents"] = with.Contents,
                ["contentsSaid"] = with.ContentSaid,
                ["bytes"] = with.Bytes,
                ["eventsLost"] = with.EventsLost,
            });
        }

        Scope scope = await ScopeAsync(workload, messages).ConfigureAwait(false);
        double median = Median(busyDeltas);
        return new()
        {
            ["schema"] = "intercat.sdk-content-impact.v1",
            ["measuredUtc"] = DateTimeOffset.UtcNow,
            ["windows"] = Environment.OSVersion.VersionString,
            ["processors"] = Environment.ProcessorCount,
            ["workload"] = "sdk-burst --content",
            ["messages"] = messages,
            ["messageBytes"] = MessageBytes,
            ["callsPerSecond"] = CallsPerSecond,
            ["pairs"] = trials,
            ["medianCaptureCpuPercentagePoints"] = Math.Round(median, 3),
            ["medianWorkloadSlowdownPercent"] = Math.Round(Median(slowdowns), 2),
            ["recordsCaptured"] = records,
            ["contentsCaptured"] = contents,
            ["contentsSaid"] = said,
            ["bytesCopied"] = copied,
            ["eventsLost"] = lost,
            ["overheadClass"] = OverheadClassCalculator.Classify(Math.Max(0, median)).ToString(),
            ["scope"] = new Dictionary<string, object?>
            {
                ["namedRecords"] = scope.Named,
                ["namedContents"] = scope.NamedContents,
                ["decoyRecords"] = scope.Decoy,
                ["decoyListened"] = scope.DecoyListened,
                ["decoyContentsSaid"] = scope.DecoySaid,
            },
            ["notes"] = new[]
            {
                "Machine CPU is the GetSystemTimes busy share over the burst alone; process starts and the session's start and stop are outside it.",
                "The session is scoped to the burst's process, as a capture keeping the SDK's content must be, and its consumer copies every record's bytes.",
                "Each message is published with its 256 bytes, its content said whole, and consumed: three events, two calls.",
                "Signed differences are kept; a pair can show a negative cost on a noisy machine. A negative median classifies as no cost.",
            },
        };
    }

    private static async Task<Trial> RunAsync(string workload, int messages, bool capture)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "InterCat-sdk-content-impact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        using Process run = Start(workload, scratch, messages);
        TraceEventSession? session = null;
        Thread? reader = null;
        long records = 0, contents = 0, copied = 0;
        try
        {
            if (capture)
            {
                session = new TraceEventSession("InterCat-SdkProbe-impact-" + Guid.NewGuid().ToString("N")) { StopOnDispose = true };
                session.Source.AllEvents += data =>
                {
                    if (data.ProviderGuid != CooperativeSdk.ProviderId) return;
                    byte[] body = data.EventData();
                    Interlocked.Increment(ref records);
                    Interlocked.Add(ref copied, body.Length);
                    if ((int)data.ID == CooperativeSdk.MessageContent) Interlocked.Increment(ref contents);
                };
                session.EnableProvider(CooperativeSdk.ProviderId, TraceEventLevel.Informational, Keywords,
                    new TraceEventProviderOptions { ProcessIDFilter = [run.Id] });
                TraceEventSession owned = session;
                reader = new Thread(() => owned.Source.Process()) { IsBackground = true };
                reader.Start();
            }

            await Task.Delay(1_500).ConfigureAwait(false);
            if (!MachineProcessorTime.TryRead(out MachineProcessorTimeReading before, out string? problem))
            {
                throw new InvalidOperationException(problem);
            }

            var clock = Stopwatch.StartNew();
            await run.StandardInput.WriteLineAsync("start").ConfigureAwait(false);
            await run.StandardInput.FlushAsync().ConfigureAwait(false);
            await run.WaitForExitAsync().ConfigureAwait(false);
            double seconds = clock.Elapsed.TotalSeconds;
            if (!MachineProcessorTime.TryRead(out MachineProcessorTimeReading after, out problem)
                || !MachineProcessorTime.TryMeasure(before, after, out MachineProcessorTimeInterval interval))
            {
                throw new InvalidOperationException(problem ?? "The machine processor clock did not advance.");
            }

            if (run.ExitCode != 0)
            {
                throw new InvalidOperationException($"The burst exited with {run.ExitCode}.");
            }

            // The session's last buffers arrive on its flush timer, and its consumer reads them after the burst ends: it is
            // stopped only once its count has settled, so no record delivered goes unread.
            if (session is not null) await SettledAsync(() => Interlocked.Read(ref records)).ConfigureAwait(false);
            (bool _, long contentSaid) = await ReportAsync(scratch).ConfigureAwait(false);
            return new(interval.BusyPercentage, seconds, session?.EventsLost ?? 0, Interlocked.Read(ref records),
                Interlocked.Read(ref contents), Interlocked.Read(ref copied), contentSaid);
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

    /// <summary>
    /// A named burst and a decoy beside it, both publishing with their bytes, under a session scoped to the named one: what
    /// reached it from each, and whether the decoy was asked to say anything at all.
    /// </summary>
    private static async Task<Scope> ScopeAsync(string workload, int messages)
    {
        string named = Path.Combine(Path.GetTempPath(), "InterCat-sdk-content-scope-" + Guid.NewGuid().ToString("N"));
        string decoy = named + "-decoy";
        Directory.CreateDirectory(named);
        Directory.CreateDirectory(decoy);
        using Process run = Start(workload, named, messages);
        using Process other = Start(workload, decoy, messages);
        long namedRecords = 0, namedContents = 0, decoyRecords = 0;
        var session = new TraceEventSession("InterCat-SdkProbe-scope-" + Guid.NewGuid().ToString("N")) { StopOnDispose = true };
        Thread? reader = null;
        try
        {
            int namedId = run.Id;
            session.Source.AllEvents += data =>
            {
                if (data.ProviderGuid != CooperativeSdk.ProviderId) return;
                if (data.ProcessID != namedId)
                {
                    Interlocked.Increment(ref decoyRecords);
                    return;
                }

                Interlocked.Increment(ref namedRecords);
                if ((int)data.ID == CooperativeSdk.MessageContent) Interlocked.Increment(ref namedContents);
            };
            session.EnableProvider(CooperativeSdk.ProviderId, TraceEventLevel.Informational, Keywords,
                new TraceEventProviderOptions { ProcessIDFilter = [run.Id] });
            reader = new Thread(() => session.Source.Process()) { IsBackground = true };
            reader.Start();
            await Task.Delay(1_500).ConfigureAwait(false);
            foreach (Process process in new[] { run, other })
            {
                await process.StandardInput.WriteLineAsync("start").ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
            }

            await run.WaitForExitAsync().ConfigureAwait(false);
            await other.WaitForExitAsync().ConfigureAwait(false);
            if (run.ExitCode != 0 || other.ExitCode != 0)
            {
                throw new InvalidOperationException($"A burst exited with {run.ExitCode} and {other.ExitCode}.");
            }

            await SettledAsync(() => Interlocked.Read(ref namedRecords) + Interlocked.Read(ref decoyRecords)).ConfigureAwait(false);
            (bool decoyListened, long decoySaid) = await ReportAsync(decoy).ConfigureAwait(false);
            return new(Interlocked.Read(ref namedRecords), Interlocked.Read(ref namedContents), Interlocked.Read(ref decoyRecords),
                decoyListened, decoySaid);
        }
        finally
        {
            if (!run.HasExited) run.Kill();
            if (!other.HasExited) other.Kill();
            session.Stop();
            reader?.Join(TimeSpan.FromSeconds(10));
            session.Dispose();
            Directory.Delete(named, recursive: true);
            Directory.Delete(decoy, recursive: true);
        }
    }

    private static Process Start(string workload, string truth, int messages) =>
        Process.Start(new ProcessStartInfo(workload)
        {
            ArgumentList =
            {
                "sdk-burst", "--truth", truth, "--messages", messages.ToString(CultureInfo.InvariantCulture),
                "--calls-per-second", CallsPerSecond.ToString(CultureInfo.InvariantCulture), "--content", "--wait-for-start",
            },
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("The burst did not start.");

    /// <summary>Whether a burst found a session listening, and how many of its messages' contents it said.</summary>
    private static async Task<(bool Listened, long ContentSaid)> ReportAsync(string truth)
    {
        using JsonDocument report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(truth, "burst.json")).ConfigureAwait(false));
        return (report.RootElement.GetProperty("listened").GetBoolean(), report.RootElement.GetProperty("contentSaid").GetInt64());
    }

    /// <summary>
    /// Waits until a session's consumer has read what it was delivered: its count unchanged for two seconds, past the
    /// session's flush timer, or a minute at most.
    /// </summary>
    private static async Task SettledAsync(Func<long> count)
    {
        long seen = -1;
        var still = Stopwatch.StartNew();
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < TimeSpan.FromMinutes(1))
        {
            await Task.Delay(250).ConfigureAwait(false);
            long now = count();
            if (now != seen)
            {
                seen = now;
                still.Restart();
            }
            else if (still.Elapsed >= TimeSpan.FromSeconds(2))
            {
                return;
            }
        }
    }

    private static double Median(List<double> values)
    {
        List<double> sorted = [.. values.Order()];
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }

    private readonly record struct Trial(double BusyPercent, double Seconds, int EventsLost, long Records, long Contents, long Bytes, long ContentSaid);

    private readonly record struct Scope(long Named, long NamedContents, long Decoy, bool DecoyListened, long DecoySaid);
}
