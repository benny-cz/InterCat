using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using InterCat.Capture.Windows;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.ComProbe;

/// <summary>
/// What Microsoft-Windows-COM-Perf records of an out-of-process COM exchange, measured against FX-COM-001, whose two
/// processes log every call the client issued and the server served. The provider is enabled machine-wide in one uniquely
/// named real-time session of this probe's own - the workload's processes do not exist before it starts - and a record is
/// read by the provider's registered layout and attributed to the process that raised it. Only counters are written, and
/// with --ledger each workload record's metadata: its time, the process it was raised in, its event, call trace number,
/// the process at its other end, interface, method, causality and HRESULT - never a pointer or an IPID.
/// </summary>
internal static partial class Measure
{
    private const string Schema = "intercat.com-perf-feasibility.v1";
    private const int Invoke = 6;
    private const int MaximumRecords = 4_000_000;

    private static readonly Guid ComPerf = Guid.Parse("b8d6861b-d20f-4eec-bbae-87e0dd80602b");
    private static readonly Guid Dispatch = Guid.Parse("00020400-0000-0000-c000-000000000046");

    // The call events this probe reads, and the fields each must declare for it to read them.
    private static readonly (int Id, string[] Fields)[] Needed =
    [
        (39, ["CallTraceId", "TargetProcessId", "TargetThreadId", "CausalityId", "TargetMethod", "TargetInterface"]),
        (40, ["CallTraceId", "HRESULT"]),
        (43, ["CallTraceId", "SourceProcessId", "SourceThreadId", "CausalityId", "TargetMethod", "TargetInterface"]),
        (44, ["CallTraceId", "HRESULT"]),
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record Record(int Id, int Version, int ProcessId, int ThreadId, long UtcTicks, Dictionary<string, object> Fields);

    public static async Task<int> RunAsync(string output, string[] args)
    {
        int calls = int.Parse(Option(args, "--calls") ?? "64", CultureInfo.InvariantCulture);
        int delay = int.Parse(Option(args, "--delay") ?? "5", CultureInfo.InvariantCulture);
        int burst = int.Parse(Option(args, "--burst") ?? "0", CultureInfo.InvariantCulture);
        bool ledger = args.Contains("--ledger", StringComparer.Ordinal);
        string workload = Option(args, "--workload") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "InterCat.TestWorkloads", "bin", "Release", "net10.0", "InterCat.TestWorkloads.exe"));
        if (TraceEventSession.IsElevated() != true)
        {
            Console.Error.WriteLine("Enabling a provider for other processes needs elevation; nothing was started.");
            return 3;
        }

        if (!File.Exists(workload))
        {
            Console.Error.WriteLine($"No truth workload at {workload}; build src/InterCat.TestWorkloads in Release or pass --workload.");
            return 2;
        }

        // The layout is the provider's registered schema, read as the product's inventory reads it; an event this build
        // declares otherwise is a finding, and nothing is read by guess.
        ManifestReadResult manifest = new TdhEtwMetadataSource().TryReadManifest(ComPerf);
        ProviderSchema? schema = manifest.ManifestXml is null ? null : ManifestParser.Parse(manifest.ManifestXml);
        string? problem = schema is null ? "the provider's schema is not registered on this machine: " + manifest.FailureReason : CheckSchema(schema);
        if (problem is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"),
                JsonSerializer.Serialize(new { schema = Schema, measuredUtc = DateTimeOffset.UtcNow, schemaProblem = problem }, Json));
            Console.Error.WriteLine("COM-Perf's events do not have the layout this probe reads: " + problem);
            return 4;
        }

        Dictionary<(int, int), ProviderSchemaEvent> events = schema!.Events.ToDictionary(descriptor => (descriptor.EventId, descriptor.Version));
        var records = new ConcurrentQueue<Record>();
        long unreadable = 0;
        long dropped = 0;
        string truth = Path.Combine(output, "truth");
        string sessionName = "InterCat-ComProbe-" + Guid.NewGuid().ToString("N");
        int lost;
        long? realTimeBuffersLost = null;
        long sourceEventsLost;
        Thread? reader = null;

        // With --file the session writes its records to a file of this run's own, read whole after its stop: what a
        // real-time delivery might not hand over, a file holds.
        string? file = args.Contains("--file", StringComparer.Ordinal) ? Path.Combine(output, "session.etl") : null;
        void Take(TraceEvent data)
        {
            if (data.ProviderGuid != ComPerf)
            {
                return;
            }

            if (!events.TryGetValue(((int)data.ID, data.Version), out ProviderSchemaEvent? declared)
                || Fields(declared, data.EventData(), data.PointerSize) is not { } fields)
            {
                Interlocked.Increment(ref unreadable);
                return;
            }

            if (records.Count >= MaximumRecords)
            {
                Interlocked.Increment(ref dropped);
                return;
            }

            records.Enqueue(new((int)data.ID, data.Version, data.ProcessID, data.ThreadID, data.TimeStamp.ToUniversalTime().Ticks, fields));
        }

        using (var session = file is null ? new TraceEventSession(sessionName) { StopOnDispose = true }
            : new TraceEventSession(sessionName, file) { StopOnDispose = true })
        {
            session.EnableProvider(ComPerf, TraceEventLevel.Verbose, ulong.MaxValue);
            if (file is null)
            {
                session.Source.AllEvents += Take;
                reader = new Thread(() => session.Source.Process()) { IsBackground = true, Name = "com-probe-events" };
                reader.Start();
            }

            await Task.Delay(1_000);

            using (Process coordinator = Process.Start(new ProcessStartInfo(workload)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "com-loopback", "--truth", truth, "--calls", calls.ToString(CultureInfo.InvariantCulture), "--delay",
                    delay.ToString(CultureInfo.InvariantCulture), "--burst", burst.ToString(CultureInfo.InvariantCulture) },
            }) ?? throw new InvalidOperationException("The workload could not be started."))
            {
                await coordinator.WaitForExitAsync();
                if (coordinator.ExitCode != 0)
                {
                    Console.Error.WriteLine($"The workload exited {coordinator.ExitCode}.");
                }
            }

            await Task.Delay(2_000);
            lost = session.EventsLost;

            // ETW states a session's final losses only in the answer to its stop - among them the real-time buffers its
            // consumer could not take, which a source's own count misses - so the session is stopped by name, as the
            // product's capture stops one, before it is disposed.
            if (Stop(sessionName) is { } final)
            {
                lost = (int)Math.Max(lost, final.EventsLost);
                realTimeBuffersLost = final.RealTimeBuffersLost;
            }

            // Stopping the session delivers its last buffers to the reader, which returns once it has taken every one.
            // Disposing the session closes its source, which would drop what the reader has not taken yet, so the reader
            // is waited for first: a run's last records are counted, never discarded uncounted.
            if (reader is not null && !reader.Join(TimeSpan.FromSeconds(30)))
            {
                Console.Error.WriteLine("The session's reader did not finish within 30 seconds of its stop; its counts may be short.");
            }

            sourceEventsLost = file is null ? session.Source.EventsLost : 0;
        }

        // A file is read whole once the session has stopped.

        if (file is not null)
        {
            using var recorded = new ETWTraceEventSource(file);
            recorded.AllEvents += Take;
            recorded.Process();
            lost = Math.Max(lost, recorded.EventsLost);
        }

        using JsonDocument scenario = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(truth, "scenario.json")));
        int server = scenario.RootElement.GetProperty("serverProcessId").GetInt32();
        int client = scenario.RootElement.GetProperty("clientProcessId").GetInt32();
        string Role(long process) => process == server ? "server" : process == client ? "client" : "another process";
        Record[] all = [.. records];
        Record[] workloads = [.. all.Where(record => record.ProcessId == server || record.ProcessId == client).OrderBy(record => record.UtcTicks)];

        (string Kind, long Call, long Utc)[] Truth(string file) =>
        [
            .. File.ReadLines(Path.Combine(truth, file)).Where(line => line.Length > 0).Select(line =>
            {
                using JsonDocument record = JsonDocument.Parse(line);
                JsonElement root = record.RootElement;
                return (root.GetProperty("kind").GetString()!, root.GetProperty("callId").ValueKind == JsonValueKind.Number ? root.GetProperty("callId").GetInt64() : -1,
                    DateTimeOffset.Parse(root.GetProperty("recordedUtc").GetString()!, CultureInfo.InvariantCulture).UtcTicks);
            }),
        ];
        (string Kind, long Call, long Utc)[] issued = Truth("truth-client.jsonl");
        (string Kind, long Call, long Utc)[] served = Truth("truth-server.jsonl");

        // A call's start and stop in one process, joined by its trace number; its other end's process; its interface and method.
        object Calls(int process, int startId, int stopId, string otherEnd, int other)
        {
            Record[] starts = [.. workloads.Where(record => record.ProcessId == process && record.Id == startId)];
            Record[] stops = [.. workloads.Where(record => record.ProcessId == process && record.Id == stopId)];
            HashSet<ulong> stopped = [.. stops.Select(record => (ulong)record.Fields["CallTraceId"])];
            return new
            {
                starts = starts.Length,
                stops = stops.Length,
                startsJoinedToAStopByTraceNumber = starts.Count(record => stopped.Contains((ulong)record.Fields["CallTraceId"])),
                startsNamingTheOtherWorkloadProcess = starts.Count(record => (ulong)record.Fields[otherEnd] == (ulong)other),
                byInterfaceAndMethod = starts.GroupBy(record => $"{(Guid)record.Fields["TargetInterface"]:B} method {record.Fields["TargetMethod"]} to {Role((long)(ulong)record.Fields[otherEnd])}")
                    .OrderByDescending(group => group.Count()).ToDictionary(group => group.Key, group => group.Count()),
                stopsByHresult = stops.GroupBy(record => Hresult(record.Fields["HRESULT"])).ToDictionary(group => group.Key, group => group.Count()),
            };
        }

        bool IsInvoke(Record record, string otherEnd, int other) =>
            (Guid)record.Fields["TargetInterface"] == Dispatch && (ulong)record.Fields["TargetMethod"] == Invoke && (ulong)record.Fields[otherEnd] == (ulong)other;

        Record[] clientInvokes = [.. workloads.Where(record => record.ProcessId == client && record.Id == 39 && IsInvoke(record, "TargetProcessId", server))];
        Record[] serverInvokes = [.. workloads.Where(record => record.ProcessId == server && record.Id == 43 && IsInvoke(record, "SourceProcessId", client))];
        HashSet<Guid> serverCausalities = [.. serverInvokes.Select(record => (Guid)record.Fields["CausalityId"])];
        // A causality names a chain of calls, not one: each client invoke is paired with the server invoke of its causality
        // and rank, and a pair is a call's two ends when each names the other's thread - the client's start its server
        // thread, the server's start its client thread - and the server's start falls inside the client's call.
        Dictionary<Guid, Record[]> servedByCausality = serverInvokes.GroupBy(record => (Guid)record.Fields["CausalityId"])
            .ToDictionary(group => group.Key, group => group.OrderBy(record => record.UtcTicks).ToArray());
        Dictionary<ulong, long> clientStops = workloads.Where(record => record.ProcessId == client && record.Id == 40)
            .GroupBy(record => (ulong)record.Fields["CallTraceId"]).ToDictionary(group => group.Key, group => group.Min(record => record.UtcTicks));
        int pairs = 0, threadsNameEachOther = 0, servedInsideTheCall = 0;
        foreach (IGrouping<Guid, Record> chain in clientInvokes.GroupBy(record => (Guid)record.Fields["CausalityId"]))
        {
            Record[] made = [.. chain.OrderBy(record => record.UtcTicks)];
            if (!servedByCausality.TryGetValue(chain.Key, out Record[]? taken))
            {
                continue;
            }

            for (int rank = 0; rank < Math.Min(made.Length, taken.Length); rank++)
            {
                pairs++;
                threadsNameEachOther += (ulong)made[rank].Fields["TargetThreadId"] == (ulong)taken[rank].ThreadId
                    && (ulong)taken[rank].Fields["SourceThreadId"] == (ulong)made[rank].ThreadId ? 1 : 0;
                servedInsideTheCall += taken[rank].UtcTicks >= made[rank].UtcTicks
                    && clientStops.TryGetValue((ulong)made[rank].Fields["CallTraceId"], out long stopped) && taken[rank].UtcTicks <= stopped ? 1 : 0;
            }
        }

        long[] issuedAt = [.. issued.Where(record => record.Kind == "CallIssued").Select(record => record.Utc)];
        long[] servedAt = [.. served.Where(record => record.Kind == "CallServed").Select(record => record.Utc)];

        var report = new
        {
            schema = Schema,
            measuredUtc = DateTimeOffset.UtcNow,
            build = CapabilityInventoryProbe.DescribeEnvironment().BuildId,
            workload = new { calls, delay, burst },
            delivery = file is null ? "real-time" : "file",
            session = new
            {
                eventsLost = lost,
                realTimeBuffersLost,
                sourceEventsLost,
                records = all.Length,
                unreadable,
                dropped,
                byProcess = all.GroupBy(record => Role(record.ProcessId)).ToDictionary(group => group.Key, group => group.Count()),
                processesRaisingRecords = all.Select(record => record.ProcessId).Distinct().Count(),
                note = "COM-Perf was enabled machine-wide at verbose, every keyword: the workload's processes do not exist before the session starts, so its records are those raised in them.",
            },
            truth = new
            {
                issued = issuedAt.Length,
                completedOk = issued.Count(record => record.Kind == "CallCompleted"),
                served = servedAt.Length,
            },
            client = new
            {
                byEvent = workloads.Where(record => record.ProcessId == client).GroupBy(record => Name(events, record)).ToDictionary(group => group.Key, group => group.Count()),
                syncCalls = Calls(client, 39, 40, "TargetProcessId", server),
                asyncCalls = Calls(client, 41, 42, "TargetProcessId", server),
            },
            server = new
            {
                byEvent = workloads.Where(record => record.ProcessId == server).GroupBy(record => Name(events, record)).ToDictionary(group => group.Key, group => group.Count()),
                syncCalls = Calls(server, 43, 44, "SourceProcessId", client),
                asyncCalls = Calls(server, 45, 46, "SourceProcessId", client),
            },
            invokes = new
            {
                clientInvokesToTheServer = clientInvokes.Length,
                serverInvokesFromTheClient = serverInvokes.Length,
                clientInvokesWhoseCausalityAServerInvokeCarries = clientInvokes.Count(record => serverCausalities.Contains((Guid)record.Fields["CausalityId"])),
                distinctCausalitiesOfClientInvokes = clientInvokes.Select(record => (Guid)record.Fields["CausalityId"]).Distinct().Count(),
                pairedByCausalityAndRank = pairs,
                pairsWhoseThreadsNameEachOther = threadsNameEachOther,
                pairsServedInsideTheClientsCall = servedInsideTheCall,
                // What the thread fields hold: none is written as a number, only how it relates to the threads recorded.
                clientInvokesNamingNoServerThread = clientInvokes.Count(record => (ulong)record.Fields["TargetThreadId"] == 0),
                clientInvokesNamingAServingThreadOfTheServer = clientInvokes.Count(record => serverInvokes.Any(served => (ulong)served.ThreadId == (ulong)record.Fields["TargetThreadId"])),
                serverInvokesNamingNoClientThread = serverInvokes.Count(record => (ulong)record.Fields["SourceThreadId"] == 0),
                serverInvokesNamingACallingThreadOfTheClient = serverInvokes.Count(record => clientInvokes.Any(made => (ulong)made.ThreadId == (ulong)record.Fields["SourceThreadId"])),
                distinctServingThreads = serverInvokes.Select(record => record.ThreadId).Distinct().Count(),
                distinctCallingThreads = clientInvokes.Select(record => record.ThreadId).Distinct().Count(),
                // Each truth call to the client's next invoke start at or after it, and each served call to the server's.
                truthCallsWithAnInvokeWithin50Ms = issuedAt.Count(at => clientInvokes.Any(record => record.UtcTicks >= at - 10_000 && record.UtcTicks <= at + 500_000)),
                servedCallsWithAServerInvokeWithin50Ms = servedAt.Count(at => serverInvokes.Any(record => record.UtcTicks >= at - 500_000 && record.UtcTicks <= at + 10_000)),
            },
        };
        await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, Json));
        if (ledger)
        {
            await File.WriteAllLinesAsync(Path.Combine(output, "ledger.csv"),
                ["utcTicks,raisedIn,event,callTrace,otherEnd,interface,method,causality,hresult",
                    .. workloads.Select(record => string.Join(',',
                        record.UtcTicks.ToString(CultureInfo.InvariantCulture),
                        Role(record.ProcessId),
                        record.Id.ToString(CultureInfo.InvariantCulture),
                        record.Fields.TryGetValue("CallTraceId", out object? trace) ? Convert.ToString(trace, CultureInfo.InvariantCulture) : string.Empty,
                        record.Fields.TryGetValue("TargetProcessId", out object? target) ? Role((long)(ulong)target)
                            : record.Fields.TryGetValue("SourceProcessId", out object? source) ? Role((long)(ulong)source) : string.Empty,
                        record.Fields.TryGetValue("TargetInterface", out object? iid) ? ((Guid)iid).ToString("B", CultureInfo.InvariantCulture) : string.Empty,
                        record.Fields.TryGetValue("TargetMethod", out object? method) ? Convert.ToString(method, CultureInfo.InvariantCulture) : string.Empty,
                        record.Fields.TryGetValue("CausalityId", out object? causality) ? ((Guid)causality).ToString("N", CultureInfo.InvariantCulture) : string.Empty,
                        record.Fields.TryGetValue("HRESULT", out object? hresult) ? Hresult(hresult) : string.Empty))]);
        }

        Console.WriteLine(JsonSerializer.Serialize(report.invokes, Json));
        return 0;
    }

    private static string Name(Dictionary<(int, int), ProviderSchemaEvent> events, Record record) =>
        events.TryGetValue((record.Id, record.Version), out ProviderSchemaEvent? declared)
            ? string.Create(CultureInfo.InvariantCulture, $"{record.Id} {declared.TaskName} {declared.OpcodeName}")
            : record.Id.ToString(CultureInfo.InvariantCulture);

    private static string Hresult(object value) => string.Create(CultureInfo.InvariantCulture, $"0x{(uint)(ulong)value:X8}");

    private static Dictionary<string, object>? Fields(ProviderSchemaEvent declared, byte[] body, int pointerSize)
    {
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        int offset = 0;
        foreach (ProviderSchemaField field in declared.Fields)
        {
            int width = field.WidthKind switch
            {
                FieldWidthKind.Fixed => field.FixedWidth,
                FieldWidthKind.PointerSized => pointerSize,
                _ => -1,
            };
            if (width < 0)
            {
                // A variable field ends what can be read by offset; the call events this probe reads have none.
                break;
            }

            if (offset + width > body.Length) return null;
            ReadOnlySpan<byte> slice = body.AsSpan(offset, width);
            values[field.Name] = (field.InType, width) switch
            {
                ("win:GUID", 16) => new Guid(slice),
                ("win:UInt32" or "win:HexInt32" or "win:Int32", 4) => (ulong)BinaryPrimitives.ReadUInt32LittleEndian(slice),
                ("win:UInt16" or "win:Int16", 2) => (ulong)BinaryPrimitives.ReadUInt16LittleEndian(slice),
                ("win:UInt8" or "win:Int8", 1) => (ulong)slice[0],
                ("win:Boolean", 4) => (ulong)BinaryPrimitives.ReadUInt32LittleEndian(slice),
                ("win:Pointer", 8) or ("win:UInt64" or "win:Int64" or "win:HexInt64", 8) => BinaryPrimitives.ReadUInt64LittleEndian(slice),
                ("win:Pointer", 4) => (ulong)BinaryPrimitives.ReadUInt32LittleEndian(slice),
                _ => slice.ToArray(),
            };
            offset += width;
        }

        return values;
    }

    private static string? CheckSchema(ProviderSchema schema)
    {
        foreach ((int id, string[] needed) in Needed)
        {
            if (schema.FindEvent(id) is not { } descriptor)
            {
                return $"event {id} is not declared";
            }

            string[] missing = [.. needed.Where(name => descriptor.Fields.All(field => field.Name != name))];
            if (missing.Length > 0)
            {
                return $"event {id} declares no {string.Join(", ", missing)}";
            }
        }

        return null;
    }

    private static string? Option(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    /// <summary>
    /// Stops the session by name and reads its loss counters from the stop's answer, as the product's EtwSessionControl does:
    /// EVENT_TRACE_PROPERTIES on 64-bit Windows, its counters at the offsets the product reads, room for both names after.
    /// </summary>
    private static (long EventsLost, long RealTimeBuffersLost)? Stop(string sessionName)
    {
        const int PropertiesBytes = 120;
        const int NameBytes = 1_024 * sizeof(char);
        const int Bytes = PropertiesBytes + (2 * NameBytes);
        byte[] properties = new byte[Bytes];
        BinaryPrimitives.WriteUInt32LittleEndian(properties, Bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(properties.AsSpan(116), PropertiesBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(properties.AsSpan(112), PropertiesBytes + NameBytes);
        return ControlTrace(0, sessionName, properties, 1) == 0
            ? (BinaryPrimitives.ReadUInt32LittleEndian(properties.AsSpan(88)), BinaryPrimitives.ReadUInt32LittleEndian(properties.AsSpan(100)))
            : null;
    }

    [System.Runtime.InteropServices.LibraryImport("advapi32.dll", EntryPoint = "ControlTraceW",
        StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16)]
    private static partial uint ControlTrace(ulong traceHandle, string instanceName, [System.Runtime.InteropServices.In, System.Runtime.InteropServices.Out] byte[] properties, uint controlCode);
}
