using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterCat.Analysis;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.SdkProbe;

/// <summary>
/// FX-SDK-001, or FX-SDK-002, recorded through the product: each run is the product's own <c>icat record</c> under the
/// opt-in sdk profile, the workload started once its session runs, and the run's session read as <c>icat messages</c>
/// reads one and held to the run's truth - every publication and every reader's consumption the workload logged, by its
/// channel, epoch, sequence and reader, with its length and the process that logged it; and, where the workload names
/// its operations, each one's start and completion as the capture admitted them.
/// </summary>
internal static class Product
{
    private static readonly JsonSerializerOptions TruthOptions = CreateTruthOptions();

    public static async Task<Dictionary<string, object?>> RunAsync(string icat, string workload, string output, int runs, int duration, int readers,
        string? native = null, string? transport = null, int batch = 1)
    {
        // FX-SDK-002 is the native sample, said through intercat_sdk.h, and FX-SDK-006 the same said in batches; FX-SDK-003 and
        // FX-SDK-004 the .NET stream over a pipe and a socket; FX-SDK-001 the .NET ring, and FX-SDK-005 the ring in batches.
        string fixture = native is not null ? (batch > 1 ? "FX-SDK-006" : "FX-SDK-002") : transport switch
        {
            "pipe" when batch == 1 => "FX-SDK-003",
            "socket" when batch == 1 => "FX-SDK-004",
            null => batch > 1 ? "FX-SDK-005" : "FX-SDK-001",
            _ => throw new ArgumentException("A stream's transport is pipe or socket, its messages each said alone.", nameof(transport)),
        };
        string[] batched = batch > 1 ? ["--batch", batch.ToString(CultureInfo.InvariantCulture)] : [];
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
                    // The native sample's batches are its own, of ten; the ring's are as many as asked.
                    ProcessStartInfo start = native is not null ? new(native, ["--truth", truth, .. batch > 1 ? ["--batch"] : Array.Empty<string>()])
                        : transport is not null ? new(workload, ["sdk-stream", "--truth", truth, "--transport", transport])
                        : new(workload, ["sdk-ring", "--truth", truth, "--readers", readers.ToString(CultureInfo.InvariantCulture), .. batched]);
                    start.UseShellExecute = false;
                    start.CreateNoWindow = true;
                    using (Process started = Process.Start(start) ?? throw new InvalidOperationException("The truth workload could not be started."))
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
            ["fixture"] = fixture,
            ["question"] = fixture switch
            {
                "FX-SDK-002" =>
                    "Whether FX-SDK-002 - a native application saying what it publishes and consumes through intercat_sdk.h, the SDK's "
                    + "native library - recorded through the product by icat record under the opt-in sdk profile, reads as icat messages "
                    + "reads it, as a .NET application's does: every message it published at either epoch one message of its channel, "
                    + "its reader's consumption of each one consumption, each bound to the process with the length it logged, and the "
                    + "one message no reader read open; and each operation it started one start, under its parent, and one completion "
                    + "with the status it first completed with.",
                "FX-SDK-003" or "FX-SDK-004" =>
                    $"Whether {fixture} - a client writing framed messages to a server over a {(transport == "socket" ? "loopback TCP connection" : "named pipe")}, "
                    + "a few to a write, read a few hundred bytes at a time, each process saying what it publishes and consumes through "
                    + "InterCat's cooperative SDK - recorded through the product by icat record under the opt-in sdk profile, reads as icat "
                    + "messages reads it: every message the client published at either connection's epoch one message, never a write or "
                    + "a read, with the logical length and the framed bytes it logged, the server's consumption of each one consumption, "
                    + "each bound to the process that logged it, the one message the server read and never consumed open; and each "
                    + "operation one start - the server's under the client's, whose identity its first frame carried - and one completion.",
                "FX-SDK-005" =>
                    $"Whether FX-SDK-005's ring - one producer and its readers saying what they publish and consume in batches of {batch}, "
                    + "each a run of messages in one call of InterCat's cooperative SDK - recorded through the product by icat record "
                    + "under the opt-in sdk profile, reads as icat messages reads it: every message the producer published at either "
                    + "epoch one message of a run its batch said, every reader's consumption of each one consumption, each batch's "
                    + "lengths together the truth's lengths of its messages summed, each bound to the process that logged it, and the "
                    + "one message said alone that no reader read open.",
                "FX-SDK-006" =>
                    "Whether FX-SDK-006 - FX-SDK-002's native application saying its messages in batches of ten through intercat_sdk.h's "
                    + "batch calls - recorded through the product by icat record under the opt-in sdk profile, reads as icat messages "
                    + "reads it: every message one message of a run its batch said, its reader's consumption of each one consumption, "
                    + "each batch's lengths together the truth's lengths of its messages summed, each bound to the process, the one "
                    + "message said alone that no reader read open, and its operations as FX-SDK-002's.",
                _ =>
                    "Whether FX-SDK-001's ring - one producer and its readers, each saying what it publishes and consumes through "
                    + "InterCat's cooperative SDK - recorded through the product by icat record under the opt-in sdk profile, reads as "
                    + "icat messages reads it: every message the producer published at either epoch one message of the ring's channel, "
                    + "every reader's consumption of it one consumption, each bound to the process that logged it with the length it "
                    + "logged, and the one message no reader read open.",
            },
            ["profile"] = "sdk",
            ["durationSeconds"] = duration,
            ["readers"] = fixture is "FX-SDK-001" or "FX-SDK-005" ? readers : 1,
            ["batch"] = batch > 1 && native is not null ? 10 : batch,
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
    internal static Dictionary<string, object?> Evaluate(string session, string truth)
    {
        Guid channel;
        string channelName;
        string? library = null;
        var said = new List<SaidOperation>();
        using (JsonDocument scenario = JsonDocument.Parse(File.ReadAllText(Path.Combine(truth, "scenario.json"))))
        {
            channel = scenario.RootElement.GetProperty("channel").GetGuid();
            channelName = scenario.RootElement.GetProperty("channelName").GetString()!;
            if (scenario.RootElement.TryGetProperty("library", out JsonElement named))
            {
                library = named.GetString();
            }

            // The operations a workload started, where it names them: each its identity, parent, name, status, how many
            // times it was completed, and where it says so, the side - the role of its truth log - that started it.
            if (scenario.RootElement.TryGetProperty("operations", out JsonElement operations))
            {
                foreach (JsonElement operation in operations.EnumerateArray())
                {
                    said.Add(new(
                        operation.GetProperty("operation").GetGuid(),
                        operation.GetProperty("parent").ValueKind == JsonValueKind.Null ? null : operation.GetProperty("parent").GetGuid(),
                        operation.GetProperty("name").GetString()!,
                        operation.GetProperty("status").GetInt64(),
                        operation.GetProperty("completions").GetInt32(),
                        operation.TryGetProperty("role", out JsonElement role) ? role.GetString() : null));
                }
            }
        }

        // The truth: what each process logged it published or consumed, and which process each truth log's role is.
        var published = new Dictionary<(long Epoch, long Sequence), (long Length, long? Copied, bool Read, int Process)>();
        var consumed = new Dictionary<(long Epoch, long Sequence, long Consumer), (long Length, int Process)>();
        var roles = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(truth, "truth-*.jsonl"))
        {
            foreach (string line in File.ReadLines(file))
            {
                TruthRecord record = JsonSerializer.Deserialize<TruthRecord>(line, TruthOptions)!;
                roles.TryAdd(record.Role, record.ProcessId);
                if (record.Message is not { } message || message.Channel != channel)
                {
                    continue;
                }

                if (record.Kind == TruthEventKind.MessageSent)
                {
                    published.Add((message.Epoch, message.Sequence), (record.DeclaredBytes!.Value, record.CompletedBytes, record.Status != "unread", record.ProcessId));
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
        List<AdmittedRecord> admitted;
        using (EvidenceLease lease = store.AcquireLease())
        {
            SessionManifestV1 manifest = lease.Manifest;
            SourceClockDescriptor clock = SessionSegments.SourceClock(store.Root, manifest)
                ?? throw new InvalidOperationException("The session names no source clock.");
            SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store.Root, manifest, name))];
            SegmentReaderV1[] fields = [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store.Root, manifest, name))];
            ProcessInstanceIndex processes = ProcessInstanceIndex.Derive(segments, clock, fields);
            index = CooperativeMessageIndex.Derive(segments, fields, processes);
            admitted = Admitted(segments);
        }

        store.ReleaseSegmentReaders();
        CooperativeMessage[] messages = [.. index.Messages.Where(message => message.Channel == channel)];
        CooperativeChannel[] channels = [.. index.Channels.Where(held => held.Channel == channel)];

        int matchedPublications = 0;
        int matchedCopies = 0;
        int matchedConsumptions = 0;
        int records = 0;
        int bound = 0;
        int withSourceTicks = 0;
        foreach (CooperativeMessage message in messages)
        {
            // A message, or a run said in batches: each of its sequences held to the truth by itself.
            long[] sequences = [.. Enumerable.Range(0, (int)message.Count).Select(at => message.Sequence + at)];
            if (message.Publication is { } publication)
            {
                records++;
                bound += publication.Process.IsBound ? 1 : 0;
                withSourceTicks += publication.SourceTicks is null ? 0 : 1;
                foreach (long sequence in sequences)
                {
                    if (published.TryGetValue((message.Epoch, sequence), out var logged) && logged.Process == publication.ProcessId
                        && Stated(publication, message, logged.Length,
                            each => published.TryGetValue((message.Epoch, each), out var other) ? other.Length : null))
                    {
                        matchedPublications++;

                        // The bytes the publisher copied for a message said alone, beside its logical length, as it logged them;
                        // a batch's are said together and never split among its messages.
                        matchedCopies += publication.Batch is null && logged.Copied == message.CopiedLength ? 1 : 0;
                    }
                }
            }

            foreach (CooperativeConsumption consumption in message.Consumptions)
            {
                records++;
                bound += consumption.Record.Process.IsBound ? 1 : 0;
                withSourceTicks += consumption.Record.SourceTicks is null ? 0 : 1;
                foreach (long sequence in sequences)
                {
                    if (consumed.TryGetValue((message.Epoch, sequence, consumption.Consumer), out var logged)
                        && logged.Process == consumption.Record.ProcessId
                        && Stated(consumption.Record, message, logged.Length,
                            each => consumed.TryGetValue((message.Epoch, each, consumption.Consumer), out var other) ? other.Length : null))
                    {
                        matchedConsumptions++;
                    }
                }
            }
        }

        (long Epoch, long Sequence)[] unread = [.. published.Where(entry => !entry.Value.Read).Select(entry => entry.Key)];
        Dictionary<string, object?> evaluated = new()
        {
            ["truthPublications"] = published.Count,
            ["truthConsumptions"] = consumed.Count,
            ["truthUnread"] = unread.Length,
            ["messages"] = messages.Sum(message => message.Count),
            ["rows"] = messages.Length,
            ["runs"] = messages.Count(message => message.Count > 1),
            ["messagesInBatches"] = messages.Where(message => message.Publication?.Batch is not null).Sum(message => message.Count),
            ["publications"] = messages.Sum(message => message.Publications * message.Count),
            ["consumptions"] = messages.Sum(message => message.Consumptions.Count * message.Count),
            ["matchedPublications"] = matchedPublications,
            ["matchedCopies"] = matchedCopies,
            ["matchedConsumptions"] = matchedConsumptions,
            ["states"] = Enum.GetValues<CooperativeMessageState>().ToDictionary(state => state.ToString(),
                state => messages.Where(message => message.State == state).Sum(message => message.Count)),
            ["unreadOpen"] = unread.All(key => messages.Any(message => message.Epoch == key.Epoch && message.Sequence <= key.Sequence
                && key.Sequence < message.Sequence + message.Count && message.State == CooperativeMessageState.Unconsumed)),
            ["epochs"] = channels.Select(held => held.Epoch).ToArray(),
            ["epochMessages"] = channels.Select(held => held.Messages).ToArray(),
            ["readers"] = channels.Select(held => held.Readers).ToArray(),
            ["openings"] = channels.Select(held => held.Openings).ToArray(),
            ["closings"] = channels.Select(held => held.Closings).ToArray(),
            ["named"] = channels.All(held => held.Name == channelName),
            ["kinds"] = channels.Select(held => held.Kind).ToArray(),
            ["records"] = records,
            ["boundRecords"] = bound,
            ["recordsWithSourceTicks"] = withSourceTicks,
            ["recordsNamingNoMessage"] = index.WithoutMessage,
        };

        // The schema each process announced, where the workload names its library: one announcement or more a process -
        // one a session enabling the provider - each read at the layout the capture knows, its library's version its last
        // field.
        HashSet<int> logging = [.. published.Values.Select(entry => entry.Process), .. consumed.Values.Select(entry => entry.Process)];
        if (library is not null)
        {
            AdmittedRecord[] announcements = [.. admitted.Where(record => record.Event == CooperativeSdk.SdkStarted
                && record.ProcessId is { } process && logging.Contains(process))];
            evaluated["announcements"] = announcements.Length;
            evaluated["announcedLibrary"] = announcements.Length > 0 && announcements.All(record => record.Name == library);
            evaluated["processesAnnounced"] = logging.Count(process => announcements.Any(record => record.ProcessId == process));
        }

        // Each operation the workload names, held to the capture's records of it: one start, owned by the process of the
        // side that started it - or, where the workload names no side, by one that logged its messages - carrying its name
        // and its parent as the related activity, and one completion with the status it was first completed with - a
        // second completion says nothing.
        if (said.Count > 0)
        {
            HashSet<Guid> named = [.. said.Select(operation => operation.Operation)];
            AdmittedRecord[] starts = [.. admitted.Where(record => record.Event == CooperativeSdk.OperationStarted
                && record.Identifier is { } operation && named.Contains(operation))];
            AdmittedRecord[] completions = [.. admitted.Where(record => record.Event == CooperativeSdk.OperationCompleted
                && record.Identifier is { } operation && named.Contains(operation))];
            bool Owned(SaidOperation operation, AdmittedRecord record) => record.ProcessId is { } process
                && (operation.Role is { } role ? roles.TryGetValue(role, out int side) && side == process : logging.Contains(process));
            evaluated["truthOperations"] = said.Count;
            evaluated["truthCompletionCalls"] = said.Sum(operation => operation.Completions);
            evaluated["operationStarts"] = starts.Length;
            evaluated["operationCompletions"] = completions.Length;
            evaluated["matchedStarts"] = said.Count(operation =>
                starts.Where(record => record.Identifier == operation.Operation).ToArray() is [var start]
                && start.Name == operation.Name && start.Parent == operation.Parent && Owned(operation, start));
            evaluated["matchedCompletions"] = said.Count(operation =>
                completions.Where(record => record.Identifier == operation.Operation).ToArray() is [var completion]
                && completion.Status == operation.Status && Owned(operation, completion));
        }

        return evaluated;
    }

    /// <summary>
    /// Whether a record states the length the truth logged: a message said alone its own; a batch, where the run is its whole
    /// batch, its messages' together - the truth's lengths of the run's sequences summed. A run a batch was split into says
    /// no length of its own to hold.
    /// </summary>
    private static bool Stated(CooperativeMessageRecord record, CooperativeMessage message, long own, Func<long, long?> logged)
    {
        if (record.Batch is null)
        {
            return own == record.Length;
        }

        if (message.Count != record.Batch)
        {
            return false;
        }

        long total = 0;
        for (long at = 0; at < message.Count; at++)
        {
            if (logged(message.Sequence + at) is not { } length)
            {
                return false;
            }

            total += length;
        }

        return total == record.Length;
    }

    /// <summary>
    /// Every announcement of the SDK's schema, start and completion of an operation the SDK's records name, as the capture
    /// admitted it.
    /// </summary>
    private static List<AdmittedRecord> Admitted(SegmentReaderV1[] segments)
    {
        var found = new List<AdmittedRecord>();
        foreach (SegmentReaderV1 segment in segments)
        {
            SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
            SegmentColumnSlice identifiers = segment.Slice(SegmentColumnId.SourceIdentifier);
            SegmentColumnSlice related = segment.Slice(SegmentColumnId.RelatedActivityId);
            SegmentColumnSlice owners = segment.Slice(SegmentColumnId.OwnerProcessId);
            SegmentColumnSlice statuses = segment.Slice(SegmentColumnId.StatusCode);
            for (int row = 0; row < segment.RowCount; row++)
            {
                if ((Mechanism)mechanisms.UnsignedAt(row)!.Value != Mechanism.ApplicationSdk)
                {
                    continue;
                }

                (Guid provider, ushort eventId, _, _) = segment.SchemaOf(row);
                if (provider != CooperativeSdk.ProviderId
                    || eventId is not (CooperativeSdk.SdkStarted or CooperativeSdk.OperationStarted or CooperativeSdk.OperationCompleted))
                {
                    continue;
                }

                // An announcement and a start are named - by the library's version and the operation's name - and a
                // completion carries its status.
                bool completion = eventId == CooperativeSdk.OperationCompleted;
                found.Add(new(
                    eventId,
                    identifiers.GuidAt(row),
                    related.GuidAt(row),
                    owners.SignedAt(row) is { } owner ? (int)owner : null,
                    completion ? null : segment.TextValue(SegmentColumnId.ResourceName, row),
                    completion ? statuses.SignedAt(row) : null));
            }
        }

        return found;
    }

    /// <summary>
    /// An operation a workload says it started: its identity, its parent's where it has one, its name, the status it first
    /// completed with, how many times it was completed, and the side that started it where the workload names one.
    /// </summary>
    private sealed record SaidOperation(Guid Operation, Guid? Parent, string Name, long Status, int Completions, string? Role);

    /// <summary>An announcement, or a start or a completion of an operation, as the capture admitted it.</summary>
    private sealed record AdmittedRecord(ushort Event, Guid? Identifier, Guid? Parent, int? ProcessId, string? Name, long? Status);

    private static JsonSerializerOptions CreateTruthOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
