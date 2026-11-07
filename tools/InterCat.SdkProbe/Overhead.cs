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
/// each call an event, every one of the enabled burst's calls looked for in the session it published.
/// </summary>
internal static class Overhead
{
    private static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The paced rates a recorded burst is held to, in calls a second, from one a capture keeps easily upward.</summary>
    private static readonly int[] Rates = [50_000, 100_000, 200_000, 400_000];

    /// <summary>How long a paced burst lasts, in seconds.</summary>
    private const int PacedSeconds = 4;

    public static async Task<Dictionary<string, object?>> RunAsync(string icat, string workload, string output, int rounds, int messages, int duration)
    {
        var disabled = new List<Dictionary<string, object?>>();
        var enabled = new List<Dictionary<string, object?>>();
        for (int round = 1; round <= rounds; round++)
        {
            string folder = Path.Combine(output, "round-" + round.ToString(CultureInfo.InvariantCulture));

            // With no session listening: the burst's cost is its enabled checks.
            Dictionary<string, object?> quiet = await BurstAsync(workload, Path.Combine(folder, "disabled"), messages, 0).ConfigureAwait(false);
            if (quiet["listened"] is true)
            {
                throw new InvalidOperationException("A session listened to the SDK during a burst meant to run with none: another capture runs.");
            }

            disabled.Add(quiet);

            // While the product records it, as fast as the SDK takes the calls: every call an event.
            enabled.Add(await RecordedAsync(icat, workload, Path.Combine(folder, "enabled"), messages, 0, duration).ConfigureAwait(false));
        }

        // The same, paced: the rate a capture keeps whole is the highest at which it kept every call.
        var paced = new List<Dictionary<string, object?>>();
        foreach (int rate in Rates)
        {
            paced.Add(await RecordedAsync(icat, workload, Path.Combine(output, "paced-" + rate.ToString(CultureInfo.InvariantCulture)),
                rate * PacedSeconds / 2, rate, duration).ConfigureAwait(false));
        }

        int[] keptWhole = [.. paced.Where(Whole).Select(burst => (int)burst["targetCallsPerSecond"]!)];
        return new()
        {
            ["question"] = "What the cooperative SDK costs an application: a burst of publications and consumptions on one thread with "
                + "no session listening, each call its enabled check, beside the same burst while icat record records it under the "
                + "opt-in sdk profile, each call an event - and, paced, the rate up to which the capture kept every call.",
            ["messages"] = messages,
            ["callsPerBurst"] = 2L * messages,
            ["rounds"] = rounds,
            ["disabled"] = new Dictionary<string, object?>
            {
                ["medianNanosecondsPerCall"] = Median(disabled, "nanosecondsPerCall"),
                ["medianCallsPerSecond"] = Median(disabled, "callsPerSecond"),
                ["bursts"] = disabled,
            },
            ["enabled"] = new Dictionary<string, object?>
            {
                ["medianNanosecondsPerCall"] = Median(enabled, "nanosecondsPerCall"),
                ["medianCallsPerSecond"] = Median(enabled, "callsPerSecond"),
                ["keptWhole"] = enabled.Count(Whole),
                ["bursts"] = enabled,
            },
            ["paced"] = new Dictionary<string, object?>
            {
                ["seconds"] = PacedSeconds,
                ["highestRateKeptWhole"] = keptWhole.Length == 0 ? null : keptWhole.Max(),
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

    /// <summary>Whether a recorded burst's capture kept every call, and said it lost nothing.</summary>
    private static bool Whole(Dictionary<string, object?> burst) =>
        (long)burst["recordedCalls"]! == (long)burst["calls"]! && ((Dictionary<string, object?>)burst["health"]!)["lossFree"] is true;

    /// <summary>A burst while the product records it, with what the capture said and kept of it.</summary>
    private static async Task<Dictionary<string, object?>> RecordedAsync(string icat, string workload, string folder, int messages, int rate, int duration)
    {
        string session = Path.Combine(Path.GetTempPath(), "sdk-overhead-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Dictionary<string, object?> recorded;
            Dictionary<string, object?> health;
            using (Workload.Recording recording = await Workload.Recording.StartAsync(icat, session, duration).ConfigureAwait(false))
            {
                recorded = await BurstAsync(workload, folder, messages, rate).ConfigureAwait(false);
                health = await recording.FinishAsync().ConfigureAwait(false);
            }

            if (recorded["listened"] is not true)
            {
                throw new InvalidOperationException("No session listened to the SDK during a burst meant to be recorded.");
            }

            recorded["health"] = health;
            recorded["recordedCalls"] = RecordedCalls(session);
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

    private static async Task<Dictionary<string, object?>> BurstAsync(string workload, string folder, int messages, int rate)
    {
        using (Process started = Process.Start(new ProcessStartInfo(workload,
            ["sdk-burst", "--truth", folder, "--messages", messages.ToString(CultureInfo.InvariantCulture), "--calls-per-second", rate.ToString(CultureInfo.InvariantCulture)])
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
        };
    }

    /// <summary>The publications and consumptions the session holds, as icat messages derives them.</summary>
    private static long RecordedCalls(string session)
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
            calls = index.Messages.Sum(message => (long)message.Publications + message.Consumptions.Count);
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
