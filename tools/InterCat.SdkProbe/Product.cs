using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.SdkProbe;

/// <summary>
/// FX-SDK-001 recorded through the product: each run is the product's own <c>icat record</c> under the opt-in sdk profile,
/// the workload started once its session runs, and the run's session read as <c>icat messages</c> reads one and held to the
/// run's truth - every publication and every reader's consumption the workload logged, by its channel, epoch, sequence and
/// reader, with its length and the process that logged it.
/// </summary>
internal static class Product
{
    private static readonly JsonSerializerOptions TruthOptions = CreateTruthOptions();

    public static async Task<Dictionary<string, object?>> RunAsync(string icat, string workload, string output, int runs, int duration, int readers)
    {
        var results = new List<Dictionary<string, object?>>();
        for (int run = 1; run <= runs; run++)
        {
            string truth = Path.Combine(output, run == 1 ? "evidence" : "run-" + run.ToString(CultureInfo.InvariantCulture));
            string session = Path.Combine(Path.GetTempPath(), "sdk-product-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(truth);
            try
            {
                Dictionary<string, object?> health;
                using (Workload.Recording recording = await Workload.Recording.StartAsync(icat, session, duration).ConfigureAwait(false))
                {
                    using (Process started = Process.Start(new ProcessStartInfo(workload,
                        ["sdk-ring", "--truth", truth, "--readers", readers.ToString(CultureInfo.InvariantCulture)])
                        { UseShellExecute = false, CreateNoWindow = true }) ?? throw new InvalidOperationException("The truth workload could not be started."))
                    {
                        await Workload.FinishedAsync(started).ConfigureAwait(false);
                    }

                    health = await recording.FinishAsync().ConfigureAwait(false);
                }

                Dictionary<string, object?> evaluated = Evaluate(session, truth);
                evaluated["run"] = run;
                evaluated["health"] = health;
                results.Add(evaluated);
            }
            finally
            {
                if (Directory.Exists(session))
                {
                    Directory.Delete(session, recursive: true);
                }
            }
        }

        return new()
        {
            ["fixture"] = "FX-SDK-001",
            ["question"] = "Whether FX-SDK-001's ring - one producer and its readers, each saying what it publishes and consumes through "
                + "InterCat's cooperative SDK - recorded through the product by icat record under the opt-in sdk profile, reads as "
                + "icat messages reads it: every message the producer published at either epoch one message of the ring's channel, "
                + "every reader's consumption of it one consumption, each bound to the process that logged it with the length it "
                + "logged, and the one message no reader read open.",
            ["profile"] = "sdk",
            ["durationSeconds"] = duration,
            ["readers"] = readers,
            ["runs"] = results,
            ["notes"] = new[]
            {
                "Each capture is the product's own icat record process, recording into a scratch session that is deleted once it is read.",
                "A run's health is the capture's own report: what its sources lost, what its queue dropped, and what was undecodable.",
                "Counts only: no process ID, path or name of the machine is kept; the first run's truth logs and scenario are kept beside it.",
            },
        };
    }

    /// <summary>The run's session, read as icat messages reads one, held to every publication and consumption its truth logged.</summary>
    private static Dictionary<string, object?> Evaluate(string session, string truth)
    {
        Guid channel;
        using (JsonDocument scenario = JsonDocument.Parse(File.ReadAllText(Path.Combine(truth, "scenario.json"))))
        {
            channel = scenario.RootElement.GetProperty("channel").GetGuid();
        }

        // The truth: what each process logged it published or consumed.
        var published = new Dictionary<(long Epoch, long Sequence), (long Length, bool Read, int Process)>();
        var consumed = new Dictionary<(long Epoch, long Sequence, long Consumer), (long Length, int Process)>();
        foreach (string file in Directory.EnumerateFiles(truth, "truth-*.jsonl"))
        {
            foreach (string line in File.ReadLines(file))
            {
                TruthRecord record = JsonSerializer.Deserialize<TruthRecord>(line, TruthOptions)!;
                if (record.Message is not { } message || message.Channel != channel)
                {
                    continue;
                }

                if (record.Kind == TruthEventKind.MessageSent)
                {
                    published.Add((message.Epoch, message.Sequence), (record.DeclaredBytes!.Value, record.Status != "unread", record.ProcessId));
                }
                else if (record.Kind == TruthEventKind.MessageReceived)
                {
                    consumed.Add((message.Epoch, message.Sequence, message.Consumer!.Value), (record.DeclaredBytes!.Value, record.ProcessId));
                }
            }
        }

        // The session, read as the product reads one, its readers let go once its messages are derived.
        SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(session));
        CooperativeMessageIndex index;
        using (EvidenceLease lease = store.AcquireLease())
        {
            SessionManifestV1 manifest = lease.Manifest;
            SourceClockDescriptor clock = SessionSegments.SourceClock(store.Root, manifest)
                ?? throw new InvalidOperationException("The session names no source clock.");
            SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store.Root, manifest, name))];
            SegmentReaderV1[] fields = [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store.Root, manifest, name))];
            ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(segments, clock, fields);
            index = CooperativeMessageIndex.Derive(segments, fields, processes);
        }

        store.ReleaseSegmentReaders();
        CooperativeMessage[] messages = [.. index.Messages.Where(message => message.Channel == channel)];
        CooperativeChannel[] channels = [.. index.Channels.Where(held => held.Channel == channel)];

        int matchedPublications = 0;
        int matchedConsumptions = 0;
        int records = 0;
        int bound = 0;
        int withSourceTicks = 0;
        foreach (CooperativeMessage message in messages)
        {
            if (message.Publication is { } publication)
            {
                records++;
                bound += publication.Process.IsBound ? 1 : 0;
                withSourceTicks += publication.SourceTicks is null ? 0 : 1;
                if (published.TryGetValue((message.Epoch, message.Sequence), out var logged)
                    && logged.Length == publication.Length && logged.Process == publication.ProcessId)
                {
                    matchedPublications++;
                }
            }

            foreach (CooperativeConsumption consumption in message.Consumptions)
            {
                records++;
                bound += consumption.Record.Process.IsBound ? 1 : 0;
                withSourceTicks += consumption.Record.SourceTicks is null ? 0 : 1;
                if (consumed.TryGetValue((message.Epoch, message.Sequence, consumption.Consumer), out var logged)
                    && logged.Length == consumption.Record.Length && logged.Process == consumption.Record.ProcessId)
                {
                    matchedConsumptions++;
                }
            }
        }

        (long Epoch, long Sequence)[] unread = [.. published.Where(entry => !entry.Value.Read).Select(entry => entry.Key)];
        return new()
        {
            ["truthPublications"] = published.Count,
            ["truthConsumptions"] = consumed.Count,
            ["truthUnread"] = unread.Length,
            ["messages"] = messages.Length,
            ["publications"] = messages.Sum(message => message.Publications),
            ["consumptions"] = messages.Sum(message => message.Consumptions.Count),
            ["matchedPublications"] = matchedPublications,
            ["matchedConsumptions"] = matchedConsumptions,
            ["states"] = Enum.GetValues<CooperativeMessageState>().ToDictionary(state => state.ToString(), state => messages.Count(message => message.State == state)),
            ["unreadOpen"] = unread.All(key => messages.Any(message => (message.Epoch, message.Sequence) == key && message.State == CooperativeMessageState.Unconsumed)),
            ["epochs"] = channels.Select(held => held.Epoch).ToArray(),
            ["epochMessages"] = channels.Select(held => held.Messages).ToArray(),
            ["readers"] = channels.Select(held => held.Readers).ToArray(),
            ["openings"] = channels.Select(held => held.Openings).ToArray(),
            ["closings"] = channels.Select(held => held.Closings).ToArray(),
            ["named"] = channels.All(held => held.Name == "fx-sdk-ring" && held.Kind == 3),
            ["records"] = records,
            ["boundRecords"] = bound,
            ["recordsWithSourceTicks"] = withSourceTicks,
            ["recordsNamingNoMessage"] = index.WithoutMessage,
        };
    }

    private static JsonSerializerOptions CreateTruthOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
