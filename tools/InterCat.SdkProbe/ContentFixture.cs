using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.SdkProbe;

/// <summary>
/// FX-SDK-007 recorded through the product (ADR-107's second addendum): each run starts the publisher and a decoy, both
/// waiting, then the product's own <c>icat record</c> keeping the SDK's content under a request that names the publisher
/// alone, then lets both say their messages. The run's session is held to the publisher's truth as FX-SDK-001's is - every
/// publication and consumption by its length and process - and its content to the truth's lengths and digests: each
/// message the SDK said with its bytes one fragment of its content record, whole and its bytes the truth's where the record
/// limit holds it, cut to the limit and its prefix the truth's where not, and none for a message the SDK never said with
/// them; the decoy's records and marker nowhere in the session; and the publisher's marker in no file but a content chunk.
/// Each run's session is deleted once read; what is kept is counts, and the first run's truth, which holds no byte.
/// </summary>
internal static class ContentFixture
{
    private const string Source = "etw/eventsource/InterCat-Sdk";

    /// <summary>The most content a run's session keeps before it stops: far more than any run says.</summary>
    private const long SessionLimit = 64L * 1024 * 1024;

    private static readonly JsonSerializerOptions TruthOptions = CreateTruthOptions();

    public static async Task<Dictionary<string, object?>> RunAsync(string icat, string workload, string output, int runs, int duration, int recordLimit,
        string? native = null)
    {
        // FX-SDK-008 is the native sample saying its messages with their bytes through intercat_sdk.h; FX-SDK-007 the .NET
        // workload. The decoy is the .NET one either way.
        string fixture = native is null ? "FX-SDK-007" : "FX-SDK-008";
        string role = native is null ? "publisher" : "native";
        var results = new List<Dictionary<string, object?>>();
        for (int run = 1; run <= runs; run++)
        {
            string truth = Path.Combine(output, run == 1 ? "evidence" : "run-" + run.ToString(CultureInfo.InvariantCulture));
            string decoyTruth = Path.Combine(Path.GetTempPath(), "sdk-content-decoy-" + Guid.NewGuid().ToString("N")[..8]);
            string session = Path.Combine(Path.GetTempPath(), "sdk-content-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(truth);
            Process? publisher = null;
            Process? decoy = null;
            try
            {
                publisher = native is null ? Start(workload, truth, recordLimit, decoy: false) : StartNative(native, truth, recordLimit);
                decoy = Start(workload, decoyTruth, recordLimit, decoy: true);
                Dictionary<string, object?> health;
                string[] request =
                [
                    "--profile", "content", "--source", Source, "--mechanism", "sdk",
                    "--pid", publisher.Id.ToString(CultureInfo.InvariantCulture), "--channel", "*",
                    "--max-record-bytes", recordLimit.ToString(CultureInfo.InvariantCulture),
                    "--max-session-bytes", SessionLimit.ToString(CultureInfo.InvariantCulture), "--inspection", "hex-text",
                ];
                using (Workload.Recording recording = await Workload.Recording.StartAsync(icat, session, duration, request).ConfigureAwait(false))
                {
                    // Both say their messages once the capture runs, the decoy's never named by it.
                    foreach (Process process in new[] { publisher, decoy })
                    {
                        await process.StandardInput.WriteLineAsync("start").ConfigureAwait(false);
                        await process.StandardInput.FlushAsync().ConfigureAwait(false);
                    }

                    await Workload.FinishedAsync(publisher).ConfigureAwait(false);
                    await Workload.FinishedAsync(decoy).ConfigureAwait(false);
                    health = await recording.FinishAsync().ConfigureAwait(false);
                }

                Dictionary<string, object?> evaluated = Product.Evaluate(session, truth);
                foreach ((string name, object? value) in Evaluate(session, truth, decoyTruth, recordLimit, role))
                {
                    evaluated[name] = value;
                }

                evaluated["run"] = run;
                evaluated["health"] = health;
                results.Add(evaluated);
            }
            finally
            {
                foreach (Process? process in new[] { publisher, decoy })
                {
                    if (process is not null)
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                        process.Dispose();
                    }
                }

                if (Directory.Exists(session)) Directory.Delete(session, recursive: true);
                if (Directory.Exists(decoyTruth)) Directory.Delete(decoyTruth, recursive: true);
            }
        }

        return new()
        {
            ["fixture"] = fixture,
            ["question"] = native is null
                ? "Whether FX-SDK-007 - one process publishing messages with their bytes through InterCat's cooperative SDK, a decoy "
                    + "beside it doing the same - recorded through the product by icat record keeping the SDK's content under a request "
                    + "that names the publisher alone, keeps each message the SDK said with its bytes as the truth's: whole where the "
                    + "record limit holds it and cut to the limit where not, its bytes or its kept prefix the truth's by SHA-256, none "
                    + "for a message the SDK never said with them; every publication and consumption as FX-SDK-001's; nothing of the "
                    + "decoy's; and no byte of a message outside the content kept."
                : "Whether FX-SDK-008 - FX-SDK-002's native application publishing each message with its bytes through intercat_sdk.h, "
                    + "a .NET decoy beside it doing the same - recorded through the product by icat record keeping the SDK's content "
                    + "under a request that names the native process alone, keeps each message the library said with its bytes as the "
                    + "truth's, at either epoch: whole where the record limit holds it and cut to the limit where not, by SHA-256, none "
                    + "for a message the library never said with them; its messages and operations as FX-SDK-002's; nothing of the "
                    + "decoy's; and no byte of a message outside the content kept.",
            ["profile"] = "content",
            ["recordLimit"] = recordLimit,
            ["sessionLimit"] = SessionLimit,
            ["durationSeconds"] = duration,
            ["runs"] = results,
            ["notes"] = new[]
            {
                "Each capture is the product's own icat record process, recording into a scratch session that is deleted once it is read.",
                "A run's health is the capture's own report: what its sources lost, what its queue dropped, and what was undecodable.",
                "Counts and digests only: no byte of a message, process ID, path or name of the machine is kept; the first run's truth holds lengths and SHA-256 digests.",
            },
        };
    }

    private static Process Start(string workload, string truth, int recordLimit, bool decoy)
    {
        var start = new ProcessStartInfo(workload)
        {
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in new[] { "sdk-content-fixture", "--truth", truth, "--record-limit", recordLimit.ToString(CultureInfo.InvariantCulture), "--wait-for-start" })
        {
            start.ArgumentList.Add(argument);
        }

        if (decoy) start.ArgumentList.Add("--decoy");
        return Process.Start(start) ?? throw new InvalidOperationException("The content fixture's workload did not start.");
    }

    /// <summary>The native sample saying its messages with their bytes, waiting for its start (FX-SDK-008).</summary>
    private static Process StartNative(string native, string truth, int recordLimit)
    {
        var start = new ProcessStartInfo(native)
        {
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in new[] { "--truth", truth, "--content", "--record-limit", recordLimit.ToString(CultureInfo.InvariantCulture), "--wait-for-start" })
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start) ?? throw new InvalidOperationException("The native sample did not start.");
    }

    /// <summary>The run's content, held to the publisher's truth, and the decoy's absence.</summary>
    private static Dictionary<string, object?> Evaluate(string session, string truth, string decoyTruth, int recordLimit, string role)
    {
        (Guid channel, string marker, int publisherProcess) = Scenario(truth, role);
        (Guid decoyChannel, string decoyMarker, int decoyProcess) = Scenario(decoyTruth, "decoy");

        // Each message by its epoch and sequence: a truth naming no epoch says its channel's one, the first.
        var said = new Dictionary<(long Epoch, long Sequence), SaidContent>();
        foreach (string line in File.ReadLines(Path.Combine(truth, "content.jsonl")))
        {
            SaidContent content = JsonSerializer.Deserialize<SaidContent>(line, TruthOptions)!;
            said.Add((content.Epoch ?? 1, content.Sequence), content);
        }

        // The session's content records and the content kept of each, read as the product reads them.
        SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(session));
        var held = new List<(ContentRecord Record, SessionContentEntry? Entry, byte[]? Bytes)>();
        int fragments;
        long decoyRecords;
        using (EvidenceLease lease = store.AcquireLease())
        {
            SessionManifestV1 manifest = lease.Manifest;
            SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(store.Root, manifest, name))];
            SegmentReaderV1[] fields = [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(store.Root, manifest, name))];
            SessionContentIndex content = SessionContentIndex.Read(store.Root, manifest, CancellationToken.None);
            if (content.Problem is { } problem)
            {
                throw new InvalidOperationException("The session's content could not be read: " + problem);
            }

            fragments = content.Fragments;
            (List<ContentRecord> records, long other) = Records(segments, fields, decoyProcess, decoyChannel);
            decoyRecords = other;
            foreach (ContentRecord record in records)
            {
                SessionContentEntry? entry = content.Find(record.Raw);
                held.Add((record, entry, entry is null ? null : SessionContentIndex.ReadBytes(store.Root, entry, recordLimit)));
            }
        }

        store.ReleaseSegmentReaders();

        // Each message the SDK said with its bytes: one content record of the publisher's channel, bound to the publisher,
        // whose fragment is its bytes whole where the limit holds them, or its prefix cut to the limit.
        var ofChannel = held.Where(item => item.Record.Channel == channel).ToList();
        int whole = 0, cut = 0, bound = 0;
        foreach ((ContentRecord record, SessionContentEntry? entry, byte[]? bytes) in ofChannel)
        {
            bound += record.Owner == publisherProcess ? 1 : 0;
            if (record.Epoch is not { } epoch || record.Sequence is not { } sequence || !said.TryGetValue((epoch, sequence), out SaidContent? truthOf)
                || entry is null || bytes is null)
            {
                continue;
            }

            ContentFragmentV1 fragment = entry.Entry.Fragment;
            string digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (truthOf.Length <= recordLimit && fragment.Disposition == ContentDispositionV1.Whole && fragment.Kept == truthOf.Length
                && fragment.OriginalLength == truthOf.Length && digest == truthOf.Sha256)
            {
                whole++;
            }
            else if (truthOf.Length > recordLimit && fragment.Disposition == ContentDispositionV1.TruncatedByRecordLimit
                && fragment.Kept == recordLimit && fragment.OriginalLength == truthOf.Length && digest == truthOf.PrefixSha256)
            {
                cut++;
            }
        }

        // No message's byte outside the content kept: the publisher's marker in content chunks alone, the decoy's nowhere.
        (long inContent, long elsewhere, long decoyAnywhere) = Markers(session, marker, decoyMarker);
        return new()
        {
            ["truthMessages"] = said.Count,
            ["truthContentsSaid"] = said.Values.Count(content => content.ContentSaid),
            ["truthPastTheMost"] = said.Values.Count(content => content.Length > 32_768),

            // What the library said it said, held to the bound itself: content past the most never, and within it always.
            ["truthSaidPastTheMost"] = said.Values.Count(content => content.ContentSaid && content.Length > 32_768),
            ["truthUnsaidWithinTheMost"] = said.Values.Count(content => !content.ContentSaid && content.Length <= 32_768),
            ["truthWholeWithinLimit"] = said.Values.Count(content => content.ContentSaid && content.Length <= recordLimit),
            ["truthCutToLimit"] = said.Values.Count(content => content.ContentSaid && content.Length > recordLimit),
            ["contentRecords"] = ofChannel.Count,
            ["contentRecordsBound"] = bound,
            ["fragments"] = fragments,
            ["matchedWhole"] = whole,
            ["matchedCut"] = cut,
            ["contentForUnsaid"] = ofChannel.Count(item => item.Record.Epoch is { } epoch && item.Record.Sequence is { } sequence
                && said.TryGetValue((epoch, sequence), out SaidContent? truthOf) && !truthOf.ContentSaid),
            ["decoyRecords"] = decoyRecords,
            ["markerInContent"] = inContent,
            ["markerElsewhere"] = elsewhere,
            ["decoyMarkerAnywhere"] = decoyAnywhere,
        };
    }

    /// <summary>
    /// Every content record of the SDK's, with its channel, epoch, sequence, owner and raw identity; and how many SDK records
    /// of any kind the decoy's process owns or its channel names, which a request naming the publisher alone keeps none of.
    /// </summary>
    private static (List<ContentRecord> Records, long Decoy) Records(SegmentReaderV1[] segments, SegmentReaderV1[] fields, int decoyProcess, Guid decoyChannel)
    {
        var carried = new Dictionary<(uint, uint, ulong, ulong, ulong), Carried>();
        foreach (SegmentReaderV1 segment in fields)
        {
            SegmentColumnSlice codes = segment.Slice(SegmentColumnId.SourceField);
            SegmentColumnSlice streams = segment.Slice(SegmentColumnId.RawStreamId);
            SegmentColumnSlice epochs = segment.Slice(SegmentColumnId.RawSourceEpoch);
            SegmentColumnSlice ordinals = segment.Slice(SegmentColumnId.RawRecordOrdinal);
            SegmentColumnSlice high = segment.Slice(SegmentColumnId.FactKeyHigh);
            SegmentColumnSlice low = segment.Slice(SegmentColumnId.FactKeyLow);
            SegmentColumnSlice values = segment.Slice(SegmentColumnId.FieldValue);
            for (int row = 0; row < segment.RowCount; row++)
            {
                var code = (SourceField)(ushort)codes.UnsignedAt(row)!.Value;
                if (code is not (SourceField.ChannelEpoch or SourceField.MessageSequence))
                {
                    continue;
                }

                (uint, uint, ulong, ulong, ulong) key = ((uint)streams.UnsignedAt(row)!.Value, (uint)epochs.UnsignedAt(row)!.Value,
                    ordinals.UnsignedAt(row)!.Value, high.UnsignedAt(row)!.Value, low.UnsignedAt(row)!.Value);
                Carried value = carried.GetValueOrDefault(key);
                carried[key] = code == SourceField.ChannelEpoch ? value with { Epoch = values.SignedAt(row) } : value with { Sequence = values.SignedAt(row) };
            }
        }

        var found = new List<ContentRecord>();
        long decoy = 0;
        foreach (SegmentReaderV1 segment in segments)
        {
            SegmentColumnSlice mechanisms = segment.Slice(SegmentColumnId.Mechanism);
            SegmentColumnSlice identifiers = segment.Slice(SegmentColumnId.SourceIdentifier);
            SegmentColumnSlice owners = segment.Slice(SegmentColumnId.OwnerProcessId);
            SegmentColumnSlice streams = segment.Slice(SegmentColumnId.RawStreamId);
            SegmentColumnSlice epochs = segment.Slice(SegmentColumnId.RawSourceEpoch);
            SegmentColumnSlice ordinals = segment.Slice(SegmentColumnId.RawRecordOrdinal);
            SegmentColumnSlice high = segment.Slice(SegmentColumnId.FactKeyHigh);
            SegmentColumnSlice low = segment.Slice(SegmentColumnId.FactKeyLow);
            for (int row = 0; row < segment.RowCount; row++)
            {
                if ((Mechanism)mechanisms.UnsignedAt(row)!.Value != Mechanism.ApplicationSdk)
                {
                    continue;
                }

                (Guid provider, ushort eventId, _, _) = segment.SchemaOf(row);
                if (provider != CooperativeSdk.ProviderId)
                {
                    continue;
                }

                int? owner = owners.SignedAt(row) is { } value ? (int)value : null;
                Guid? channel = identifiers.GuidAt(row);
                if (owner == decoyProcess || channel == decoyChannel)
                {
                    decoy++;
                }

                if (eventId != CooperativeSdk.MessageContent)
                {
                    continue;
                }

                uint stream = (uint)streams.UnsignedAt(row)!.Value;
                uint epoch = (uint)epochs.UnsignedAt(row)!.Value;
                ulong ordinal = ordinals.UnsignedAt(row)!.Value;
                Carried fieldsOf = carried.GetValueOrDefault((stream, epoch, ordinal, high.UnsignedAt(row)!.Value, low.UnsignedAt(row)!.Value));
                found.Add(new(channel, fieldsOf.Epoch, fieldsOf.Sequence, owner, new RawRecordId(segment.CaptureId, stream, epoch, ordinal)));
            }
        }

        return (found, decoy);
    }

    /// <summary>
    /// How often the publisher's marker is in the session's content chunks and in any other of its files, and the decoy's in
    /// any file at all: a message's bytes are kept only as content, and the decoy's not at all.
    /// </summary>
    private static (long InContent, long Elsewhere, long Decoy) Markers(string session, string marker, string decoyMarker)
    {
        byte[] named = Encoding.ASCII.GetBytes(marker);
        byte[] decoyNamed = Encoding.ASCII.GetBytes(decoyMarker);
        long inContent = 0, elsewhere = 0, decoy = 0;
        foreach (string file in Directory.EnumerateFiles(session, "*", SearchOption.AllDirectories))
        {
            byte[] bytes;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
            }

            long found = Count(bytes, named);
            if (Path.GetFileName(file).StartsWith("content-", StringComparison.Ordinal) && file.EndsWith(".icatc", StringComparison.Ordinal))
            {
                inContent += found;
            }
            else
            {
                elsewhere += found;
            }

            decoy += Count(bytes, decoyNamed);
        }

        return (inContent, elsewhere, decoy);
    }

    private static long Count(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> marker)
    {
        long count = 0;
        for (int at = bytes.IndexOf(marker); at >= 0; at = bytes[(at + 1)..].IndexOf(marker) is var next and >= 0 ? at + 1 + next : -1)
        {
            count++;
        }

        return count;
    }

    /// <summary>The channel, marker and process of a run's publisher or decoy, as its scenario and truth log name them.</summary>
    private static (Guid Channel, string Marker, int Process) Scenario(string truth, string role)
    {
        using JsonDocument scenario = JsonDocument.Parse(File.ReadAllText(Path.Combine(truth, "scenario.json")));
        TruthRecord first = JsonSerializer.Deserialize<TruthRecord>(File.ReadLines(Path.Combine(truth, $"truth-{role}.jsonl")).First(), TruthOptions)!;
        return (scenario.RootElement.GetProperty("channel").GetGuid(), scenario.RootElement.GetProperty("marker").GetString()!, first.ProcessId);
    }

    private static JsonSerializerOptions CreateTruthOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>A message's content as the publisher logged it: its length and digests, never its bytes, and whether it was said.</summary>
    private sealed record SaidContent(long? Epoch, long Sequence, long Length, string Sha256, string? PrefixSha256, bool ContentSaid);

    /// <summary>A content record of the SDK's, as the capture admitted it.</summary>
    private sealed record ContentRecord(Guid? Channel, long? Epoch, long? Sequence, int? Owner, RawRecordId Raw);

    private readonly record struct Carried(long? Epoch, long? Sequence);
}
