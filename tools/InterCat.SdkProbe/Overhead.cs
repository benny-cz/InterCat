using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.SdkProbe;

/// <summary>
/// The cooperative SDK's cost, as M7's overhead guide states it: rounds of one burst with no session listening - each call
/// its enabled check alone - beside one while the product's own <c>icat record</c> records it under the opt-in sdk profile,
/// each call an event, every one of the enabled burst's messages looked for in the session it published. With a batch, each
/// call says that many messages (ADR-106), and the bursts and the paced rates are the same calls saying more messages.
/// </summary>
internal static class Overhead
{
    private static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The paced rates a recorded burst is held to, in calls a second, from one a capture keeps easily upward.</summary>
    private static readonly int[] Rates = [50_000, 100_000, 200_000, 400_000];

    /// <summary>How long a paced burst lasts, in seconds.</summary>
    private const int PacedSeconds = 4;

    public static async Task<Dictionary<string, object?>> RunAsync(string icat, string workload, string output, int rounds, int messages, int duration,
        int batch = 1)
    {
        var disabled = new List<Dictionary<string, object?>>();
        var enabled = new List<Dictionary<string, object?>>();
        for (int round = 1; round <= rounds; round++)
        {
            string folder = Path.Combine(output, "round-" + round.ToString(CultureInfo.InvariantCulture));

            // With no session listening: the burst's cost is its enabled checks.
            Dictionary<string, object?> quiet = await BurstAsync(workload, Path.Combine(folder, "disabled"), messages, 0, batch).ConfigureAwait(false);
            if (quiet["listened"] is true)
            {
                throw new InvalidOperationException("A session listened to the SDK during a burst meant to run with none: another capture runs.");
            }

            disabled.Add(quiet);

            // While the product records it, as fast as the SDK takes the calls: every call an event.
            enabled.Add(await RecordedAsync(icat, workload, Path.Combine(folder, "enabled"), messages, 0, duration, batch).ConfigureAwait(false));
        }

        // The same, paced: the rate a capture keeps whole is the highest at which it kept every message. A paced burst is
        // as many calls as single messages' would be, each saying a batch's messages.
        var paced = new List<Dictionary<string, object?>>();
        foreach (int rate in Rates)
        {
            paced.Add(await RecordedAsync(icat, workload, Path.Combine(output, "paced-" + rate.ToString(CultureInfo.InvariantCulture)),
                rate * PacedSeconds / 2 * batch, rate, duration, batch).ConfigureAwait(false));
        }

        int[] keptWhole = [.. paced.Where(Whole).Select(burst => (int)burst["targetCallsPerSecond"]!)];
        return new()
        {
            ["question"] = "What the cooperative SDK costs an application: a burst of publications and consumptions on one thread with "
                + "no session listening, each call its enabled check, beside the same burst while icat record records it under the "
                + "opt-in sdk profile, each call an event - and, paced, the rate up to which the capture kept every message"
                + (batch == 1 ? "." : string.Create(CultureInfo.InvariantCulture, $", each call a batch of {batch} messages (ADR-106).")),
            ["batch"] = batch,
            ["messages"] = messages,
            ["callsPerBurst"] = 2L * messages / batch,
            ["rounds"] = rounds,
            ["disabled"] = new Dictionary<string, object?>
            {
                ["medianNanosecondsPerCall"] = Median(disabled, "nanosecondsPerCall"),
                ["medianCallsPerSecond"] = Median(disabled, "callsPerSecond"),
                ["medianNanosecondsPerMessage"] = Median(disabled, "nanosecondsPerMessage"),
                ["medianMessagesPerSecond"] = Median(disabled, "messagesPerSecond"),
                ["bursts"] = disabled,
            },
            ["enabled"] = new Dictionary<string, object?>
            {
                ["medianNanosecondsPerCall"] = Median(enabled, "nanosecondsPerCall"),
                ["medianCallsPerSecond"] = Median(enabled, "callsPerSecond"),
                ["medianNanosecondsPerMessage"] = Median(enabled, "nanosecondsPerMessage"),
                ["medianMessagesPerSecond"] = Median(enabled, "messagesPerSecond"),
                ["keptWhole"] = enabled.Count(Whole),
                ["bursts"] = enabled,
            },
            ["paced"] = new Dictionary<string, object?>
            {
                ["seconds"] = PacedSeconds,
                ["highestRateKeptWhole"] = keptWhole.Length == 0 ? null : keptWhole.Max(),
                ["highestMessageRateKeptWhole"] = keptWhole.Length == 0 ? null : (long)keptWhole.Max() * batch / 2,
                ["bursts"] = paced,
            },
            ["notes"] = new[]
            {
                "A burst is one process opening one channel and, on one thread, publishing and consuming each message as fast as the SDK takes the calls, or held to a paced schedule.",
                "A recorded burst's recorded calls are the publications and consumptions the session it published holds, as icat messages derives them; a capture that could not keep them all says so in its health, as icat record does, and is never read as having kept them.",
                "Each capture is the product's own icat record process, recording into a scratch session that is deleted once read.",
            },
        };
    }

    /// <summary>
    /// Whether a recorded burst's capture kept every message - each one's publication and consumption, a batch's each of its
    /// messages - and said it lost nothing.
    /// </summary>
    private static bool Whole(Dictionary<string, object?> burst) =>
        (long)burst["recordedMessageEvents"]! == (long)burst["messageEvents"]! && ((Dictionary<string, object?>)burst["health"]!)["lossFree"] is true;

    /// <summary>A burst while the product records it, with what the capture said and kept of it.</summary>
    private static async Task<Dictionary<string, object?>> RecordedAsync(string icat, string workload, string folder, int messages, int rate, int duration,
        int batch)
    {
        string session = Path.Combine(Path.GetTempPath(), "sdk-overhead-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Dictionary<string, object?> recorded;
            Dictionary<string, object?> health;
            using (Workload.Recording recording = await Workload.Recording.StartAsync(icat, session, duration).ConfigureAwait(false))
            {
                recorded = await BurstAsync(workload, folder, messages, rate, batch).ConfigureAwait(false);
                health = await recording.FinishAsync().ConfigureAwait(false);
            }

            if (recorded["listened"] is not true)
            {
                throw new InvalidOperationException("No session listened to the SDK during a burst meant to be recorded.");
            }

            recorded["health"] = health;
            recorded["messageEvents"] = 2L * messages;
            recorded["recordedMessageEvents"] = RecordedMessageEvents(session);
            return recorded;
        }
        finally
        {
            if (Directory.Exists(session))
            {
                Directory.Delete(session, recursive: true);
            }
        }
    }

    private static async Task<Dictionary<string, object?>> BurstAsync(string workload, string folder, int messages, int rate, int batch)
    {
        using (Process started = Process.Start(new ProcessStartInfo(workload,
            ["sdk-burst", "--truth", folder, "--messages", messages.ToString(CultureInfo.InvariantCulture), "--calls-per-second", rate.ToString(CultureInfo.InvariantCulture),
                "--batch", batch.ToString(CultureInfo.InvariantCulture)])
            { UseShellExecute = false, CreateNoWindow = true }) ?? throw new InvalidOperationException("The burst could not be started."))
        {
            await Workload.FinishedAsync(started).ConfigureAwait(false);
        }

        using JsonDocument report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "burst.json")).ConfigureAwait(false));
        JsonElement root = report.RootElement;
        return new()
        {
            ["listened"] = root.GetProperty("listened").GetBoolean(),
            ["targetCallsPerSecond"] = root.GetProperty("targetCallsPerSecond").GetInt32(),
            ["calls"] = root.GetProperty("calls").GetInt64(),
            ["elapsedNanoseconds"] = root.GetProperty("elapsedNanoseconds").GetInt64(),
            ["nanosecondsPerCall"] = root.GetProperty("nanosecondsPerCall").GetDouble(),
            ["callsPerSecond"] = root.GetProperty("callsPerSecond").GetDouble(),
            ["nanosecondsPerMessage"] = root.GetProperty("nanosecondsPerMessage").GetDouble(),
            ["messagesPerSecond"] = root.GetProperty("messagesPerSecond").GetDouble(),
        };
    }

    /// <summary>
    /// The publications and consumptions of messages the session holds, as icat messages derives them: each message of a run
    /// a batch said counted, as its publication and each consumption of it.
    /// </summary>
    private static long RecordedMessageEvents(string session)
    {
        SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(session));
        long calls;
        using (EvidenceLease lease = store.AcquireLease())
        {
            SessionManifestV1 manifest = lease.Manifest;
            SourceClockDescriptor clock = SessionSegments.SourceClock(store.Root, manifest)
                ?? throw new InvalidOperationException("The session names no source clock.");
            SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store.Root, manifest, name))];
            SegmentReaderV1[] fields = [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store.Root, manifest, name))];
            CooperativeMessageIndex index = CooperativeMessageIndex.Derive(segments, fields, ProcessInstanceIndex.Derive(segments, clock, fields));
            calls = index.Messages.Sum(message => message.Count * ((long)message.Publications + message.Consumptions.Count));
        }

        store.ReleaseSegmentReaders();
        return calls;
    }

    private static double Median(List<Dictionary<string, object?>> bursts, string name)
    {
        double[] values = [.. bursts.Select(burst => (double)burst[name]!).Order()];
        return values.Length % 2 == 1 ? values[values.Length / 2] : (values[(values.Length / 2) - 1] + values[values.Length / 2]) / 2;
    }

    /// <summary>The measurement, written beside the bursts it summarizes.</summary>
    public static Task WriteAsync(string output, Dictionary<string, object?> measurement) =>
        File.WriteAllTextAsync(Path.Combine(output, "overhead.json"), JsonSerializer.Serialize(measurement, Indented));
}
