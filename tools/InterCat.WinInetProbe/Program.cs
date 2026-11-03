using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
/// are, and with --ledger each buffer's metadata, never its bytes. With --tls the same exchange runs over TLS as FX-HTTP-002, and the server's truth is the bytes above the
/// encryption: a capture that matches it holds the plaintext its client sent and read. With --async the client works
/// WinINet asynchronously as FX-HTTP-005, and each record is counted by whether a thread that made a request raised it.
/// With --http2 the exchange runs over HTTP/2 as FX-HTTP-006, served by Kestrel, whose truth holds no head's bytes: the
/// heads kept are described by their start lines' shapes and their fields' names, against the fields the server decoded.
/// </summary>
internal static partial class Program
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
        bool tls = args.Contains("--tls", StringComparer.Ordinal);
        bool chunked = args.Contains("--chunked", StringComparer.Ordinal);
        bool gzip = args.Contains("--gzip", StringComparer.Ordinal);
        bool decode = args.Contains("--decode", StringComparer.Ordinal);
        bool async = args.Contains("--async", StringComparer.Ordinal);
        bool http2 = args.Contains("--http2", StringComparer.Ordinal);
        int pace = int.Parse(Option(args, "--pace") ?? "0", CultureInfo.InvariantCulture);
        bool ledger = args.Contains("--ledger", StringComparer.Ordinal);
        int concurrency = int.Parse(Option(args, "--concurrency") ?? "1", CultureInfo.InvariantCulture);
        string workload = Option(args, "--workload") ?? Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "InterCat.TestWorkloads", "bin", "Release", "net10.0",
            "InterCat.TestWorkloads.exe"));
        if (output is null || requests < 1)
        {
            Console.Error.WriteLine("InterCat.WinInetProbe --output <new directory> [--requests n] [--seed n] [--bytes n] [--tls] [--concurrency n] [--chunked]");
            Console.Error.WriteLine("  [--gzip [--decode]]: responses coded in gzip, read coded or decoded by WinINet (FX-HTTP-004)");
            Console.Error.WriteLine("  [--async]: the client works WinINet asynchronously, with any of the others (FX-HTTP-005)");
            Console.Error.WriteLine("  [--http2]: the client enables HTTP/2 and Kestrel serves it over TLS (FX-HTTP-006)");
            Console.Error.WriteLine("  [--pace ms]: the server waits that long before each 16 KiB of a response body, so reads wait or pend");
            Console.Error.WriteLine("  [--ledger]: writes each captured buffer's metadata - time, event, exchange, place, ends, length, thread -");
            Console.Error.WriteLine("             never its bytes, as buffers.csv beside the report");
            Console.Error.WriteLine("  [--workload <InterCat.TestWorkloads.exe>]");
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
            Dictionary<string, object?> impact = await Impact.MeasureAsync(workload, requests, bytes, pairs, tls);
            await File.WriteAllTextAsync(Path.Combine(output, "impact.json"), JsonSerializer.Serialize(impact, Json));
            Console.WriteLine(JsonSerializer.Serialize(impact, Json));
            return 0;
        }

        string truth = Path.Combine(output, "truth");
        string decoyTruth = Path.Combine(output, "decoy-truth");
        Process Start(string truthDirectory, int workloadSeed)
        {
            var start = new ProcessStartInfo(workload)
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
            };
            if (tls) start.ArgumentList.Add("--tls");
            if (chunked) start.ArgumentList.Add("--chunked");
            if (gzip) start.ArgumentList.Add("--gzip");
            if (decode) start.ArgumentList.Add("--decode");
            if (async) start.ArgumentList.Add("--async");
            if (http2) start.ArgumentList.Add("--http2");
            if (pace > 0)
            {
                start.ArgumentList.Add("--pace");
                start.ArgumentList.Add(pace.ToString(CultureInfo.InvariantCulture));
            }

            if (concurrency > 1)
            {
                start.ArgumentList.Add("--concurrency");
                start.ArgumentList.Add(concurrency.ToString(CultureInfo.InvariantCulture));
            }

            return Process.Start(start) ?? throw new InvalidOperationException("The workload did not start.");
        }

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
            exchange = Compare(captured, truth, process.Id, requests, bytes, tls || http2, concurrency, chunked, gzip, decode, async, http2, pace, eventsLost, others,
                malformed, ledger ? Path.Combine(output, "buffers.csv") : null),
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
    /// What the capture holds against what the wire held. An exchange is the capture's own identity - a SessionId - taken in
    /// time order, and a request head flagged first under an id already seen opens another exchange, so an id WinINet uses
    /// again is counted rather than merged. Each exchange is matched with its request by the path its head names, and each
    /// of its four parts is the concatenation of its buffers in sequence order, compared by length and SHA-256 with the
    /// server's truth; a chunked response body is compared both with the body and with its framed bytes, and a coded one
    /// with the body before its coding and with the coded bytes.
    /// </summary>
    private static object Compare(List<Buffer> captured, string truth, int processId, int requests, int maximumBodyBytes, bool tls,
        int concurrency, bool chunked, bool gzip, bool decode, bool async, bool http2, int pace, int eventsLost, long others, long malformed,
        string? ledger)
    {
        // The threads that made the requests, by the workload's own account: a record raised on any other thread of its
        // process was raised by WinINet working a request on a thread of its own.
        using JsonDocument scenario = JsonDocument.Parse(File.ReadAllText(Path.Combine(truth, "scenario.json")));
        HashSet<int> clientThreads = scenario.RootElement.TryGetProperty("clientThreads", out JsonElement threadIds)
            ? [.. threadIds.EnumerateArray().Select(thread => (int)thread.GetUInt32())]
            : [];

        // What the client read of each response body, by its own truth: the coded bytes, or what WinINet decoded them to.
        var read = new Dictionary<long, string>();
        foreach (string line in File.ReadLines(Path.Combine(truth, "truth-http-client.jsonl")))
        {
            JsonElement record = JsonDocument.Parse(line).RootElement;
            if (record.GetProperty("kind").GetString() == "CallCompleted" && record.GetProperty("callId").ValueKind == JsonValueKind.Number)
            {
                read[record.GetProperty("callId").GetInt64()] = record.GetProperty("resourceName").GetString()!;
            }
        }

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

        var exchanges = new List<List<Buffer>>();
        var open = new Dictionary<uint, List<Buffer>>();
        var opened = new Dictionary<uint, int>();
        foreach (Buffer buffer in captured.OrderBy(buffer => buffer.Milliseconds).ThenBy(buffer => buffer.Arrival))
        {
            bool opens = buffer.EventId == 2001 && (buffer.Flags & 1) != 0;
            if (opens || !open.TryGetValue(buffer.SessionId, out List<Buffer>? current))
            {
                current = [];
                open[buffer.SessionId] = current;
                exchanges.Add(current);
                opened[buffer.SessionId] = opened.GetValueOrDefault(buffer.SessionId) + 1;
            }

            current.Add(buffer);
        }

        // Which request each exchange is, by the path its request head names: "POST /fx/{index} HTTP/1.1".
        int? RequestOf(List<Buffer> exchange)
        {
            byte[] head = [.. exchange.Where(buffer => buffer.EventId == 2001).OrderBy(buffer => buffer.Sequence).SelectMany(buffer => buffer.Bytes)];
            string text = Encoding.ASCII.GetString(head);
            int start = text.IndexOf("/fx/", StringComparison.Ordinal);
            int end = start < 0 ? -1 : text.IndexOf(' ', start);
            return end > start && int.TryParse(text.AsSpan(start + 4, end - start - 4), NumberStyles.None, CultureInfo.InvariantCulture,
                out int index) ? index : null;
        }

        var byRequest = new Dictionary<int, List<List<Buffer>>>();
        int unmatched = 0;
        foreach (List<Buffer> exchange in exchanges)
        {
            if (RequestOf(exchange) is { } index)
            {
                if (!byRequest.TryGetValue(index, out List<List<Buffer>>? found)) byRequest[index] = found = [];
                found.Add(exchange);
            }
            else
            {
                unmatched++;
            }
        }

        // How many exchanges were in flight at once: each spans its first buffer to its last.
        var edges = exchanges.SelectMany(exchange => new[] { (At: exchange[0].Milliseconds, Step: 1), (At: exchange[^1].Milliseconds, Step: -1) })
            .OrderBy(edge => edge.At).ThenBy(edge => edge.Step);
        int inFlight = 0, mostAtOnce = 0;
        foreach ((double _, int step) in edges)
        {
            inFlight += step;
            mostAtOnce = Math.Max(mostAtOnce, inFlight);
        }

        // Whether buffers of different exchanges interleave: a buffer whose predecessor in time belongs to another exchange
        // while its own exchange is still open.
        var owner = new Dictionary<Buffer, int>(ReferenceEqualityComparer.Instance);
        for (int index = 0; index < exchanges.Count; index++)
        {
            foreach (Buffer buffer in exchanges[index]) owner[buffer] = index;
        }

        List<Buffer> timeline = [.. captured.OrderBy(buffer => buffer.Milliseconds).ThenBy(buffer => buffer.Arrival)];
        int interleavedBuffers = 0;
        for (int index = 1; index < timeline.Count; index++)
        {
            int mine = owner[timeline[index]];
            if (owner[timeline[index - 1]] != mine && exchanges[mine][0] != timeline[index]) interleavedBuffers++;
        }

        // Each buffer's metadata in time order, never its bytes, for a test to group as the product groups them: when it was
        // raised after the first, its event, exchange number, place and ends, length, the request its exchange's head names,
        // and whether a thread that made a request raised it.
        if (ledger is not null)
        {
            double start = timeline.Count == 0 ? 0 : timeline[0].Milliseconds;
            int?[] requestOf = [.. exchanges.Select(RequestOf)];
            File.WriteAllLines(ledger,
            [
                "ticks,event,exchange,sequence,flags,bytes,request,thread",
                .. timeline.Select(buffer => string.Create(CultureInfo.InvariantCulture,
                    $"{(long)Math.Round((buffer.Milliseconds - start) * 10_000)},{buffer.EventId},{buffer.SessionId},{buffer.Sequence},{buffer.Flags},{buffer.Bytes.Length},{requestOf[owner[buffer]] ?? -1},{(clientThreads.Contains(buffer.ThreadId) ? "client" : "wininet")}")),
            ]);
        }

        // What the flags and sequence numbers say, counted by where a buffer sits in its part - alone, first, between or
        // last - and checked as rules: a part's buffers are numbered from 0 in order, its first carries flag 1 and its last
        // flag 2.
        var flagsByPosition = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var emptyBuffers = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int partsWithBuffers = 0, numberedInOrder = 0, flaggedFirstAndLast = 0;
        foreach (List<Buffer> exchange in exchanges)
        {
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

        var parts = new Dictionary<string, object>();
        foreach ((int id, string part) in Parts)
        {
            int present = 0, matched = 0, lengthMatched = 0, missing = 0, unexpected = 0, framedMatches = 0, codedMatches = 0;
            int clientReadMatches = 0;
            int mostBuffers = 0;
            long totalBytes = 0;
            var lengthDeltas = new SortedDictionary<long, int>();
            for (int index = 0; index < requests; index++)
            {
                List<Buffer> buffers = byRequest.TryGetValue(index, out List<List<Buffer>>? found) && found.Count == 1
                    ? [.. found[0].Where(buffer => buffer.EventId == id).OrderBy(buffer => buffer.Sequence)]
                    : [];
                byte[] bytes = [.. buffers.SelectMany(buffer => buffer.Bytes)];
                mostBuffers = Math.Max(mostBuffers, buffers.Count);
                totalBytes += bytes.Length;
                if (!wire.TryGetValue((index, part), out (long Length, string Hash) expected))
                {
                    unexpected += buffers.Count > 0 ? 1 : 0;
                    continue;
                }

                present++;
                string hash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
                if (buffers.Count == 0 && expected.Length > 0) missing++;
                if (bytes.Length == expected.Length) lengthMatched++;
                if (hash == expected.Hash) matched++;
                else lengthDeltas[bytes.Length - expected.Length] = lengthDeltas.GetValueOrDefault(bytes.Length - expected.Length) + 1;
                if (wire.TryGetValue((index, part + "-chunked"), out (long Length, string Hash) framed) && hash == framed.Hash) framedMatches++;
                if (wire.TryGetValue((index, part + "-coded"), out (long Length, string Hash) codedBody) && hash == codedBody.Hash) codedMatches++;
                if (part == "response-body" && read.TryGetValue(index, out string? clientRead) && hash == clientRead) clientReadMatches++;
            }

            List<Buffer> all = [.. captured.Where(buffer => buffer.EventId == id)];
            parts[part] = new
            {
                eventId = id,
                truthParts = present,
                wholeMatches = matched,
                matchesOfTheChunkedFraming = framedMatches,
                matchesOfTheCodedBody = codedMatches,
                matchesOfWhatTheClientRead = clientReadMatches,
                lengthMatches = lengthMatched,
                missing,
                unexpected,
                events = all.Count,
                mostBuffersInOnePart = mostBuffers,
                largestBuffer = all.Count == 0 ? 0 : all.Max(buffer => buffer.Bytes.Length),
                raisedOnAThreadThatMadeARequest = all.Count(buffer => clientThreads.Contains(buffer.ThreadId)),
                raisedOnAnotherThread = all.Count(buffer => !clientThreads.Contains(buffer.ThreadId)),
                bytes = totalBytes,
                lengthDeltasOfMismatches = lengthDeltas.ToDictionary(pair => pair.Key.ToString(CultureInfo.InvariantCulture), pair => pair.Value),
            };
        }

        // The first exchange's heads as text, for the person running the probe: they are the fixture's own request to its
        // own server on loopback, and are printed here, never written.
        if (byRequest.TryGetValue(0, out List<List<Buffer>>? first))
        {
            foreach (int id in (int[])[2001, 2003])
            {
                byte[] head = [.. first[0].Where(buffer => buffer.EventId == id).SelectMany(buffer => buffer.Bytes)];
                Console.Error.WriteLine($"--- request 0, event {id}, {head.Length} bytes:");
                Console.Error.WriteLine(Encoding.ASCII.GetString(head).Replace("\r", "\\r", StringComparison.Ordinal));
            }
        }

        // What every captured head holds, as text: the shapes of its start lines, numbers as n, and the names of its fields,
        // each with how many heads hold it - the fixture's own synthetic heads, never their values.
        (SortedDictionary<string, int> StartLines, SortedDictionary<string, int> FieldNames) HeadsOf(int id)
        {
            var startLines = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var fieldNames = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (List<Buffer> exchange in exchanges)
            {
                byte[] head = [.. exchange.Where(buffer => buffer.EventId == id).OrderBy(buffer => buffer.Sequence).SelectMany(buffer => buffer.Bytes)];
                if (head.Length == 0) continue;
                string[] lines = Encoding.ASCII.GetString(head).Split("\r\n");
                string shape = Numbers().Replace(lines[0], "n");
                startLines[shape] = startLines.GetValueOrDefault(shape) + 1;
                foreach (string line in lines.Skip(1).TakeWhile(line => line.Length > 0))
                {
                    int colon = line.IndexOf(':', StringComparison.Ordinal);
                    string name = colon < 0 ? "(no colon)" : line[..colon];
                    fieldNames[name] = fieldNames.GetValueOrDefault(name) + 1;
                }
            }

            return (startLines, fieldNames);
        }

        (SortedDictionary<string, int> requestStartLines, SortedDictionary<string, int> requestFieldNames) = HeadsOf(2001);
        (SortedDictionary<string, int> responseStartLines, SortedDictionary<string, int> responseFieldNames) = HeadsOf(2003);

        // Over HTTP/2, which protocol each request was made over by the client's account and by the server's, and the
        // names of the fields the server decoded from the first request's compressed head.
        int ProtocolCount(string file, string status, string protocol) => File.ReadLines(Path.Combine(truth, file))
            .Count(line => line.Contains($"\"status\":\"{status}\"", StringComparison.Ordinal)
                && line.Contains($"\"resourceName\":\"{protocol}\"", StringComparison.Ordinal));
        string? decodedFields = File.ReadLines(Path.Combine(truth, "truth-http-server.jsonl"))
            .Where(line => line.Contains("\"status\":\"request-fields\"", StringComparison.Ordinal))
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("resourceName").GetString())
            .FirstOrDefault();
        int connections = File.ReadLines(Path.Combine(truth, "truth-http-server.jsonl"))
            .Where(line => line.Contains("\"status\":\"request-connection\"", StringComparison.Ordinal))
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("resourceName").GetString())
            .Distinct(StringComparer.Ordinal).Count();

        return new
        {
            schema = Schema,
            measuredUtc = DateTimeOffset.UtcNow,
            windows = Environment.OSVersion.VersionString,
            provider = "Microsoft-Windows-WinINet-Capture",
            providerGuid = Provider,
            keywords = "0x" + Keywords.ToString("X16", CultureInfo.InvariantCulture),
            processFilter = "EVENT_FILTER_TYPE_PID: the workload's process alone",
            workload = http2 ? "FX-HTTP-006" : async ? "FX-HTTP-005" : gzip ? "FX-HTTP-004" : concurrency > 1 || chunked ? "FX-HTTP-003"
                : tls ? "FX-HTTP-002" : "FX-HTTP-001",
            http2,
            requestsOverHttp2ByTheClient = http2 ? ProtocolCount("truth-http-client.jsonl", "protocol-used", "HTTP/2") : (int?)null,
            requestsOverHttp2ByTheServer = http2 ? ProtocolCount("truth-http-server.jsonl", "request-protocol", "HTTP/2") : (int?)null,
            fieldsTheServerDecodedOfTheFirstRequest = decodedFields,
            connectionsByTheServer = http2 ? connections : (int?)null,
            requestHeadStartLines = requestStartLines,
            requestHeadFieldNames = requestFieldNames,
            responseHeadStartLines = responseStartLines,
            responseHeadFieldNames = responseFieldNames,
            async,
            paceMilliseconds = pace,
            tls,
            concurrency,
            chunked,
            gzip,
            decode,
            requests,
            maximumBodyBytes,
            exchangedByTruth,
            exchangesCaptured = exchanges.Count,
            exchangesMatchedToOneRequest = byRequest.Count(pair => pair.Value.Count == 1),
            requestsCapturedAsSeveralExchanges = byRequest.Count(pair => pair.Value.Count > 1),
            exchangesNamingNoRequest = unmatched,
            sessionIds = opened.Count,
            sessionIdsUsedForMoreThanOneExchange = opened.Count(pair => pair.Value > 1),
            smallestSessionId = opened.Count == 0 ? 0 : opened.Keys.Min(),
            largestSessionId = opened.Count == 0 ? 0 : opened.Keys.Max(),
            mostExchangesAtOnce = mostAtOnce,
            buffersFollowingAnotherExchangesBuffer = interleavedBuffers,
            events = captured.Count,
            eventsLost,
            recordsOfOtherProcesses = others,
            malformedRecords = malformed,
            raisedByTheClientProcess = captured.Count,
            declaredLengthDiffersFromDelivered = captured.Count(buffer => buffer.DeclaredLength != buffer.Bytes.Length),
            buffersOpeningLikeATlsRecord = captured.Count(buffer => OpensLikeATlsRecord(buffer.Bytes)),
            parts,
            flags = captured.Select(buffer => "0x" + buffer.Flags.ToString("X8", CultureInfo.InvariantCulture)).Distinct().Order().ToArray(),
            flagsByPosition,
            emptyBuffers,
            partsWithBuffers,
            partsNumberedFromZeroInOrder = numberedInOrder,
            partsFlaggedFirstAndLast = flaggedFirstAndLast,
            threads = captured.Select(buffer => buffer.ThreadId).Distinct().Count(),
            threadsThatMadeRequests = clientThreads.Count,
            raisedOnAThreadThatMadeARequest = captured.Count(buffer => clientThreads.Contains(buffer.ThreadId)),
            raisedOnAnotherThread = captured.Count(buffer => !clientThreads.Contains(buffer.ThreadId)),
            workloadProcessId = processId,
        };
    }

    /// <summary>
    /// Whether bytes begin as a TLS record does - a content type from 20 to 23 and a version of 3.1 to 3.4 - which a
    /// capture of the encrypted stream, rather than of what the client sent and read, would show.
    /// </summary>
    private static bool OpensLikeATlsRecord(byte[] bytes) =>
        bytes.Length >= 5 && bytes[0] is >= 20 and <= 23 && bytes[1] == 3 && bytes[2] is >= 1 and <= 4;

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

    [GeneratedRegex("[0-9]+")]
    private static partial Regex Numbers();

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
