using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InterCat.Capture.Windows;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.WinInetProbe;

/// <summary>
/// M3's content-source feasibility (plan §11, ADR-036): whether WinINet's own capture provider,
/// Microsoft-Windows-WinINet-Capture, records an HTTP exchange's bytes as its client sent and read them - each request and
/// response head and body, whole and in order, with its length and direction - measured against FX-HTTP-001, whose
/// loopback server logs the exact bytes on the wire by length and SHA-256. The provider is enabled in one uniquely named
/// real-time session of this probe's own, scoped by a process filter to the workload alone, so no other process's traffic
/// reaches it; what it delivers is the workload's synthetic exchange, compared in memory and never written. Only counters
/// are.
/// </summary>
internal static class Program
{
    private const string Schema = "intercat.wininet-capture-feasibility.v1";

    // SEND, RECEIVE, PII_PRESENT and PACKET: the capture events' own keywords, and none of the operational channel's.
    private const ulong Keywords = 0x0000_0603_0000_0000;

    private static readonly Guid Provider = Guid.Parse("a70ff94f-570b-4979-ba5c-e59c9feab61b");
    private static readonly (int Id, string Part)[] Parts =
        [(2001, "request-head"), (2002, "request-body"), (2003, "response-head"), (2004, "response-body")];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        string? output = Option(args, "--output");
        int requests = int.Parse(Option(args, "--requests") ?? "16", CultureInfo.InvariantCulture);
        int seed = int.Parse(Option(args, "--seed") ?? "20260929", CultureInfo.InvariantCulture);
        int bytes = int.Parse(Option(args, "--bytes") ?? "98304", CultureInfo.InvariantCulture);
        string workload = Option(args, "--workload") ?? Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "InterCat.TestWorkloads", "bin", "Release", "net10.0",
            "InterCat.TestWorkloads.exe"));
        if (output is null || requests < 1)
        {
            Console.Error.WriteLine("InterCat.WinInetProbe --output <new directory> [--requests n] [--seed n] [--bytes n] [--workload <InterCat.TestWorkloads.exe>]");
            Console.Error.WriteLine("  [--impact [--pairs n]]: machine CPU and workload time with and without a scoped capture, in pairs");
            return 2;
        }

        if (TraceEventSession.IsElevated() != true)
        {
            Console.Error.WriteLine("Enabling a provider for another process needs elevation; nothing was started.");
            return 3;
        }

        output = Path.GetFullPath(output);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        {
            Console.Error.WriteLine($"{output} is not empty; a result is never written over another.");
            return 2;
        }

        if (!File.Exists(workload))
        {
            Console.Error.WriteLine($"No truth workload at {workload}; build src/InterCat.TestWorkloads in Release or pass --workload.");
            return 2;
        }

        Directory.CreateDirectory(output);

        // The layout is taken from the provider's registered schema, read as the product's inventory reads it; a
        // different layout on this build is a finding, and nothing is read by guess.
        ManifestReadResult manifest = new TdhEtwMetadataSource().TryReadManifest(Provider);
        string? schemaProblem = manifest.ManifestXml is null
            ? "the provider's schema is not registered on this machine: " + manifest.FailureReason
            : CheckSchema(ManifestParser.Parse(manifest.ManifestXml));
        if (schemaProblem is not null)
        {
            await WriteAsync(output, new { schema = Schema, measuredUtc = DateTimeOffset.UtcNow, schemaProblem });
            Console.Error.WriteLine("The capture events do not have the layout this probe reads: " + schemaProblem);
            return 4;
        }

        // The workload, and a decoy running the same exchange at the same time outside the process filter: a record of the
        // decoy reaching the session would show the filter does not hold the source to the processes it names.
        if (args.Contains("--impact", StringComparer.Ordinal))
        {
            int pairs = int.Parse(Option(args, "--pairs") ?? "7", CultureInfo.InvariantCulture);
            Dictionary<string, object?> impact = await Impact.MeasureAsync(workload, requests, bytes, pairs);
            await File.WriteAllTextAsync(Path.Combine(output, "impact.json"), JsonSerializer.Serialize(impact, Json));
            Console.WriteLine(JsonSerializer.Serialize(impact, Json));
            return 0;
        }

        string truth = Path.Combine(output, "truth");
        string decoyTruth = Path.Combine(output, "decoy-truth");
        Process Start(string truthDirectory, int workloadSeed) => Process.Start(new ProcessStartInfo(workload)
        {
            ArgumentList =
            {
                "http-wininet", "--truth", truthDirectory, "--requests", requests.ToString(CultureInfo.InvariantCulture),
                "--seed", workloadSeed.ToString(CultureInfo.InvariantCulture), "--bytes", bytes.ToString(CultureInfo.InvariantCulture),
                "--wait-for-start",
            },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("The workload did not start.");
        using Process process = Start(truth, seed);
        using Process decoy = Start(decoyTruth, seed + 1);

        var captured = new List<Buffer>();
        var gate = new Lock();
        long arrival = 0, others = 0, decoyRecords = 0, malformed = 0;
        int eventsLost;
        string sessionName = string.Create(CultureInfo.InvariantCulture,
            $"InterCat-WinInetProbe-{Environment.ProcessId}-{Guid.NewGuid():N}");
        try
        {
            using var session = new TraceEventSession(sessionName) { StopOnDispose = true };
            session.Source.AllEvents += data =>
            {
                if (data.ProviderGuid != Provider || (int)data.ID is < 2001 or > 2004) return;

                // The session's process filter admits the workload alone; a record of any other process is counted and
                // dropped unread, and is itself a finding.
                if (data.ProcessID != process.Id)
                {
                    Interlocked.Increment(ref data.ProcessID == decoy.Id ? ref decoyRecords : ref others);
                    return;
                }

                byte[] body = data.EventData();
                if (body.Length < 16)
                {
                    Interlocked.Increment(ref malformed);
                    return;
                }

                var buffer = new Buffer((int)data.ID, data.ThreadID, data.TimeStampRelativeMSec,
                    BinaryPrimitives.ReadUInt32LittleEndian(body), BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)), BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)),
                    body[16..]);
                lock (gate)
                {
                    captured.Add(buffer with { Arrival = arrival++ });
                }
            };
            session.EnableProvider(Provider, TraceEventLevel.Informational, Keywords,
                new TraceEventProviderOptions { ProcessIDFilter = [process.Id] });
            var reader = new Thread(() => session.Source.Process()) { IsBackground = true, Name = "wininet-probe-events" };
            reader.Start();

            // The enable reaches the workload's process asynchronously; then both may send.
            await Task.Delay(1_500);
            foreach (Process started in (Process[])[process, decoy])
            {
                await started.StandardInput.WriteLineAsync("start");
                await started.StandardInput.FlushAsync();
            }

            foreach ((Process started, string name) in new[] { (process, "workload"), (decoy, "decoy") })
            {
                Task<string> errors = started.StandardError.ReadToEndAsync();
                _ = await started.StandardOutput.ReadToEndAsync();
                if (!started.WaitForExit(180_000))
                {
                    started.Kill();
                    Console.Error.WriteLine($"The {name} did not finish within three minutes and was stopped.");
                }

                string workloadErrors = await errors;
                if (workloadErrors.Length > 0) Console.Error.WriteLine($"{name}: " + workloadErrors.Trim());
            }

            // A real-time session hands over its last buffers on its flush timer.
            await Task.Delay(2_500);
            eventsLost = session.EventsLost;
            session.Stop();
            reader.Join(TimeSpan.FromSeconds(10));
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            if (!decoy.HasExited) decoy.Kill();
        }

        int decoyExchanged = File.ReadLines(Path.Combine(decoyTruth, "truth-http-client.jsonl"))
            .Count(line => line.Contains("\"kind\":\"CallCompleted\"", StringComparison.Ordinal));
        object report = new
        {
            exchange = Compare(captured, truth, process.Id, requests, bytes, eventsLost, others, malformed),
            processFilter = new
            {
                decoyExchangedByTruth = decoyExchanged,
                recordsOfTheDecoy = decoyRecords,
                note = "The decoy ran the same exchange at the same time outside the session's process filter.",
            },
        };
        await WriteAsync(output, report);
        Console.WriteLine(JsonSerializer.Serialize(report, Json));
        return 0;
    }

    /// <summary>
    /// What the capture holds against what the wire held: each exchange opens at a request head, in reading order, and each
    /// of its four parts is the concatenation of its buffers, compared by length and SHA-256 with the server's truth.
    /// </summary>
    private static object Compare(List<Buffer> captured, string truth, int processId, int requests, int maximumBodyBytes, int eventsLost, long others,
        long malformed)
    {
        var wire = new Dictionary<(long Call, string Part), (long Length, string Hash)>();
        foreach (string line in File.ReadLines(Path.Combine(truth, "truth-http-server.jsonl")))
        {
            JsonElement record = JsonDocument.Parse(line).RootElement;
            if (record.GetProperty("status").ValueKind == JsonValueKind.String && record.GetProperty("callId").ValueKind == JsonValueKind.Number)
            {
                wire[(record.GetProperty("callId").GetInt64(), record.GetProperty("status").GetString()!)] =
                    (record.GetProperty("declaredBytes").GetInt64(), record.GetProperty("resourceName").GetString()!);
            }
        }

        int exchangedByTruth = File.ReadLines(Path.Combine(truth, "truth-http-client.jsonl"))
            .Count(line => line.Contains("\"kind\":\"CallCompleted\"", StringComparison.Ordinal));

        List<Buffer> ordered = [.. captured.OrderBy(buffer => buffer.Milliseconds).ThenBy(buffer => buffer.Arrival)];
        var exchanges = new List<List<Buffer>>();
        foreach (Buffer buffer in ordered)
        {
            if (buffer.EventId == 2001 && (exchanges.Count == 0 || exchanges[^1].Any(earlier => earlier.EventId != 2001)))
            {
                exchanges.Add([]);
            }

            if (exchanges.Count == 0) exchanges.Add([]);
            exchanges[^1].Add(buffer);
        }

        // What the flags and sequence numbers say, counted by where a buffer sits in its part - alone, first, between or
        // last - and checked as rules: a part's buffers are numbered from 0 in order, its first carries flag 1 and its last
        // flag 2, and one exchange's buffers share one session id that no other exchange has.
        var flagsByPosition = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var emptyBuffers = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int partsWithBuffers = 0, numberedInOrder = 0, flaggedFirstAndLast = 0, oneSessionId = 0;
        foreach (List<Buffer> exchange in exchanges)
        {
            oneSessionId += exchange.Select(buffer => buffer.SessionId).Distinct().Count() == 1 ? 1 : 0;
            foreach ((int id, string part) in Parts)
            {
                List<Buffer> buffers = [.. exchange.Where(buffer => buffer.EventId == id)];
                if (buffers.Count == 0) continue;
                partsWithBuffers++;
                bool inOrder = true, flagged = true;
                for (int index = 0; index < buffers.Count; index++)
                {
                    string position = buffers.Count == 1 ? "alone" : index == 0 ? "first" : index == buffers.Count - 1 ? "last" : "between";
                    string key = string.Create(CultureInfo.InvariantCulture, $"{part} {position} flags {buffers[index].Flags}");
                    flagsByPosition[key] = flagsByPosition.GetValueOrDefault(key) + 1;
                    if (buffers[index].Bytes.Length == 0)
                    {
                        emptyBuffers[$"{part} {position}"] = emptyBuffers.GetValueOrDefault($"{part} {position}") + 1;
                    }

                    inOrder &= buffers[index].Sequence == index;
                    uint expected = (index == 0 ? 1u : 0u) | (index == buffers.Count - 1 ? 2u : 0u);
                    flagged &= buffers[index].Flags == expected;
                }

                numberedInOrder += inOrder ? 1 : 0;
                flaggedFirstAndLast += flagged ? 1 : 0;
            }
        }

        int distinctExchangeSessions = exchanges.Select(exchange => exchange[0].SessionId).Distinct().Count();

        var parts = new Dictionary<string, object>();
        foreach ((int id, string part) in Parts)
        {
            int present = 0, matched = 0, lengthMatched = 0, missing = 0, unexpected = 0;
            int mostBuffers = 0;
            long totalBytes = 0;
            var lengthDeltas = new SortedDictionary<long, int>();
            for (int index = 0; index < Math.Max(exchanges.Count, requests); index++)
            {
                List<Buffer> buffers = index < exchanges.Count ? [.. exchanges[index].Where(buffer => buffer.EventId == id)] : [];
                byte[] bytes = [.. buffers.SelectMany(buffer => buffer.Bytes)];
                mostBuffers = Math.Max(mostBuffers, buffers.Count);
                totalBytes += bytes.Length;
                if (!wire.TryGetValue((index, part), out (long Length, string Hash) expected))
                {
                    unexpected += buffers.Count > 0 ? 1 : 0;
                    continue;
                }

                present++;
                if (buffers.Count == 0 && expected.Length > 0) missing++;
                if (bytes.Length == expected.Length) lengthMatched++;
                if ("sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)) == expected.Hash) matched++;
                else lengthDeltas[bytes.Length - expected.Length] = lengthDeltas.GetValueOrDefault(bytes.Length - expected.Length) + 1;
            }

            List<Buffer> all = [.. captured.Where(buffer => buffer.EventId == id)];
            parts[part] = new
            {
                eventId = id,
                truthParts = present,
                wholeMatches = matched,
                lengthMatches = lengthMatched,
                missing,
                unexpected,
                events = all.Count,
                mostBuffersInOnePart = mostBuffers,
                largestBuffer = all.Count == 0 ? 0 : all.Max(buffer => buffer.Bytes.Length),
                bytes = totalBytes,
                lengthDeltasOfMismatches = lengthDeltas.ToDictionary(pair => pair.Key.ToString(CultureInfo.InvariantCulture), pair => pair.Value),
            };
        }

        // The first exchange's heads as text, for the person running the probe: they are the fixture's own request to its
        // own server on loopback, and are printed here, never written.
        if (exchanges.Count > 0)
        {
            foreach (int id in (int[])[2001, 2003])
            {
                byte[] head = [.. exchanges[0].Where(buffer => buffer.EventId == id).SelectMany(buffer => buffer.Bytes)];
                Console.Error.WriteLine($"--- first exchange, event {id}, {head.Length} bytes:");
                Console.Error.WriteLine(Encoding.ASCII.GetString(head).Replace("\r", "\\r", StringComparison.Ordinal));
            }
        }

        var sequenceSteps = new SortedDictionary<long, int>();
        foreach (IGrouping<uint, Buffer> session in captured.OrderBy(buffer => buffer.Arrival).GroupBy(buffer => buffer.SessionId))
        {
            uint? previous = null;
            foreach (Buffer buffer in session.OrderBy(buffer => buffer.Milliseconds).ThenBy(buffer => buffer.Arrival))
            {
                if (previous is { } before)
                {
                    long step = (long)buffer.Sequence - before;
                    sequenceSteps[step] = sequenceSteps.GetValueOrDefault(step) + 1;
                }

                previous = buffer.Sequence;
            }
        }

        return new
        {
            schema = Schema,
            measuredUtc = DateTimeOffset.UtcNow,
            windows = Environment.OSVersion.VersionString,
            provider = "Microsoft-Windows-WinINet-Capture",
            providerGuid = Provider,
            keywords = "0x" + Keywords.ToString("X16", CultureInfo.InvariantCulture),
            processFilter = "EVENT_FILTER_TYPE_PID: the workload's process alone",
            workload = "FX-HTTP-001",
            requests,
            maximumBodyBytes,
            exchangedByTruth,
            exchangesCaptured = exchanges.Count,
            events = captured.Count,
            eventsLost,
            recordsOfOtherProcesses = others,
            malformedRecords = malformed,
            raisedByTheClientProcess = captured.Count,
            declaredLengthDiffersFromDelivered = captured.Count(buffer => buffer.DeclaredLength != buffer.Bytes.Length),
            parts,
            sessionIds = captured.Select(buffer => buffer.SessionId).Distinct().Count(),
            sequenceStepsWithinASessionId = sequenceSteps.ToDictionary(pair => pair.Key.ToString(CultureInfo.InvariantCulture), pair => pair.Value),
            flags = captured.Select(buffer => "0x" + buffer.Flags.ToString("X8", CultureInfo.InvariantCulture)).Distinct().Order().ToArray(),
            flagsByPosition,
            emptyBuffers,
            partsWithBuffers,
            partsNumberedFromZeroInOrder = numberedInOrder,
            partsFlaggedFirstAndLast = flaggedFirstAndLast,
            exchangesWithOneSessionId = oneSessionId,
            exchangesWithADistinctSessionId = distinctExchangeSessions,
            threads = captured.Select(buffer => buffer.ThreadId).Distinct().Count(),
            workloadProcessId = processId,
        };
    }

    /// <summary>Why the registered capture events are not the layout this probe reads; null when all four are.</summary>
    private static string? CheckSchema(ProviderSchema schema)
    {
        foreach ((int id, _) in Parts)
        {
            if (schema.FindEvent(id, 0) is not { } descriptor)
            {
                return $"event {id} version 0 is not declared";
            }

            string[] expected = ["SessionId win:UInt32", "SequenceNumber win:UInt32", "Flags win:UInt32", "PayloadByteLength win:UInt32",
                "Payload win:Binary PayloadByteLength"];
            string[] actual = [.. descriptor.Fields.Select(field => $"{field.Name} {field.InType}{(field.LengthField is { } length ? " " + length : string.Empty)}")];
            if (!expected.SequenceEqual(actual))
            {
                return $"event {id} declares {string.Join(", ", actual)}";
            }
        }

        return null;
    }

    private static async Task WriteAsync(string output, object report) =>
        await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, Json));

    private static string? Option(string[] args, string name)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal)) return args[index + 1];
        }

        return null;
    }

    /// <summary>One captured buffer: its event, where it came from, its header fields and the bytes it carried.</summary>
    private sealed record Buffer(int EventId, int ThreadId, double Milliseconds, uint SessionId, uint Sequence, uint Flags,
        uint DeclaredLength, byte[] Bytes)
    {
        public long Arrival { get; init; }
    }
}
