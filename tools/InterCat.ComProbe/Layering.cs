using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using InterCat.Capture.Windows;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using static InterCat.ComProbe.Measure;

namespace InterCat.ComProbe;

/// <summary>
/// What carries a COM call (ADR-077's third addendum): FX-COM-001's exchange under one real-time session collecting
/// Microsoft-Windows-COM-Perf beside Microsoft-Windows-RPC, the RPC provider requested as the product's profiles request it.
/// Each COM call - a start joined to its process's stop of its trace number - is set beside the RPC calls of its process,
/// each a start joined to its stop by activity id (ADR-031): at the client the RPC call of its activity, at the server the
/// one on its thread around it; and each client COM call beside the server COM call carrying its activity, checked
/// against ADR-077's pairing by causality and rank. Only counters are written, and with --ledger each workload record's
/// metadata, its thread and activity as ordinals of the run - never a PID, a thread id, a pointer or an endpoint.
/// </summary>
internal static class Layering
{
    private const string Schema = "intercat.com-perf-layering.v1";
    private const int MaximumRecords = 4_000_000;

    internal static readonly Guid Rpc = Guid.Parse("6ad52b32-d609-4be9-ae07-ce8dae937e39");

    // The RPC call events the product admits and the fields of each this probe reads: those before the endpoint strings.
    private static readonly (int Id, string[] Fields)[] RpcNeeded =
    [
        (5, ["InterfaceUuid", "ProcNum", "Protocol"]),
        (6, ["InterfaceUuid", "ProcNum", "Protocol"]),
        (7, ["Status"]),
        (8, ["Status"]),
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record Layered(bool FromRpc, int Id, int ProcessId, int ThreadId, long UtcTicks, Guid Activity, Dictionary<string, object> Fields);

    private sealed record Call(Layered Start, Layered Stop);

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

        // Both layouts are the providers' registered schemas, read as the product's inventory reads them.
        var metadata = new TdhEtwMetadataSource();
        ProviderSchema? Registered(Guid provider) => metadata.TryReadManifest(provider).ManifestXml is { } xml ? ManifestParser.Parse(xml) : null;
        ProviderSchema? comSchema = Registered(ComPerf);
        ProviderSchema? rpcSchema = Registered(Rpc);
        string? problem = comSchema is null ? "COM-Perf's schema is not registered on this machine"
            : rpcSchema is null ? "RPC's schema is not registered on this machine"
            : CheckSchema(comSchema) ?? CheckRpc(rpcSchema);
        if (problem is not null)
        {
            Console.Error.WriteLine("The providers' events do not have the layout this probe reads: " + problem);
            return 4;
        }

        Dictionary<(int, int), ProviderSchemaEvent> comEvents = comSchema!.Events.ToDictionary(descriptor => (descriptor.EventId, descriptor.Version));
        Dictionary<(int, int), ProviderSchemaEvent> rpcEvents = rpcSchema!.Events.Where(descriptor => descriptor.EventId is >= 5 and <= 8)
            .ToDictionary(descriptor => (descriptor.EventId, descriptor.Version));
        var records = new ConcurrentQueue<Layered>();
        var otherRpcEvents = new ConcurrentDictionary<(int Process, int Id), int>();
        long unreadable = 0;
        long dropped = 0;
        string truth = Path.Combine(output, "truth");
        string sessionName = "InterCat-ComProbe-" + Guid.NewGuid().ToString("N");
        int lost;
        long? realTimeBuffersLost = null;
        long sourceEventsLost;
        void Take(TraceEvent data)
        {
            bool fromRpc = data.ProviderGuid == Rpc;
            if (!fromRpc && data.ProviderGuid != ComPerf)
            {
                return;
            }

            if (fromRpc && (int)data.ID is < 5 or > 8)
            {
                // RPC's other events are counted by process and id only: never read, and 10 and 11 carry binary fragments.
                otherRpcEvents.AddOrUpdate((data.ProcessID, (int)data.ID), 1, static (_, count) => count + 1);
                return;
            }

            if (!(fromRpc ? rpcEvents : comEvents).TryGetValue(((int)data.ID, data.Version), out ProviderSchemaEvent? declared)
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

            records.Enqueue(new(fromRpc, (int)data.ID, data.ProcessID, data.ThreadID, data.TimeStamp.ToUniversalTime().Ticks, data.ActivityID, fields));
        }

        using (var session = new TraceEventSession(sessionName) { StopOnDispose = true })
        {
            session.Source.AllEvents += Take;
            session.EnableProvider(ComPerf, TraceEventLevel.Verbose, ulong.MaxValue);

            // As the product's profiles request RPC: informational, with no keyword mask, since the provider declares none.
            session.EnableProvider(Rpc, TraceEventLevel.Informational, 0);
            var reader = new Thread(() => session.Source.Process()) { IsBackground = true, Name = "com-probe-layering" };
            reader.Start();
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
            if (Stop(sessionName) is { } final)
            {
                lost = (int)Math.Max(lost, final.EventsLost);
                realTimeBuffersLost = final.RealTimeBuffersLost;
            }

            // The reader takes what the stop delivered before the session is disposed (ADR-077's fourth decision).
            if (!reader.Join(TimeSpan.FromSeconds(30)))
            {
                Console.Error.WriteLine("The session's reader did not finish within 30 seconds of its stop; its counts may be short.");
            }

            sourceEventsLost = session.Source.EventsLost;
        }

        using JsonDocument scenario = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(truth, "scenario.json")));
        int server = scenario.RootElement.GetProperty("serverProcessId").GetInt32();
        int client = scenario.RootElement.GetProperty("clientProcessId").GetInt32();
        string Role(long process) => process == server ? "server" : process == client ? "client" : "another process";
        Layered[] all = [.. records];
        Layered[] workloads = [.. all.Where(record => record.ProcessId == server || record.ProcessId == client).OrderBy(record => record.UtcTicks)];

        // A COM call: a start joined to the next stop of its process with its trace number.
        List<Call> ComCalls(int process, int startId, int stopId)
        {
            var open = new Dictionary<ulong, Layered>();
            var made = new List<Call>();
            foreach (Layered record in workloads.Where(record => !record.FromRpc && record.ProcessId == process && (record.Id == startId || record.Id == stopId)))
            {
                ulong trace = (ulong)record.Fields["CallTraceId"];
                if (record.Id == startId)
                {
                    open[trace] = record;
                }
                else if (open.Remove(trace, out Layered? start))
                {
                    made.Add(new(start, record));
                }
            }

            return made;
        }

        // An RPC call: a start joined to the next stop of its process and side with its activity id (ADR-031).
        List<Call> RpcCalls(int process, int startId, int stopId)
        {
            var open = new Dictionary<Guid, Layered>();
            var made = new List<Call>();
            foreach (Layered record in workloads.Where(record => record.FromRpc && record.ProcessId == process && (record.Id == startId || record.Id == stopId)
                && record.Activity != Guid.Empty))
            {
                if (record.Id == startId)
                {
                    open[record.Activity] = record;
                }
                else if (open.Remove(record.Activity, out Layered? start))
                {
                    made.Add(new(start, record));
                }
            }

            return made;
        }

        string Interface(Layered start, string field) => ((Guid)start.Fields[field]).ToString("B", CultureInfo.InvariantCulture);
        string Procedure(Call call) => string.Create(CultureInfo.InvariantCulture, $"{Interface(call.Start, "InterfaceUuid")} procedure {call.Start.Fields["ProcNum"]}");
        string Method(Call call) => string.Create(CultureInfo.InvariantCulture, $"{Interface(call.Start, "TargetInterface")} method {call.Start.Fields["TargetMethod"]}");
        bool Carries(Call rpc, Call com) => (Guid)rpc.Start.Fields["InterfaceUuid"] == (Guid)com.Start.Fields["TargetInterface"]
            && (ulong)rpc.Start.Fields["ProcNum"] == (ulong)com.Start.Fields["TargetMethod"];
        static void Add(SortedDictionary<string, int> counts, string key) => counts[key] = counts.GetValueOrDefault(key) + 1;
        Dictionary<string, int> ByCount(IEnumerable<string> keys) => keys.GroupBy(key => key, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count());
        Dictionary<string, int> OtherRpcEvents(int process) => otherRpcEvents.Where(entry => entry.Key.Process == process).OrderBy(entry => entry.Key.Id)
            .ToDictionary(entry => entry.Key.Id.ToString(CultureInfo.InvariantCulture), entry => entry.Value);
        int Starts(int process, bool fromRpc, int id, bool withNoActivity) => workloads.Count(record => record.FromRpc == fromRpc && record.ProcessId == process
            && record.Id == id && (!withNoActivity || record.Activity == Guid.Empty));
        List<Call> clientCom = ComCalls(client, 39, 40);
        List<Call> serverCom = ComCalls(server, 43, 44);
        List<Call> clientRpc = RpcCalls(client, 5, 7);
        List<Call> serverRpc = RpcCalls(server, 6, 8);

        // At the client, the RPC call of a COM call's activity: the one that carries it, if it is one.
        var clientCarriers = new Dictionary<Call, Call>();
        int clientNone = 0, clientSeveral = 0, clientThread = 0, clientStartsInside = 0, clientStopsAfter = 0, clientStopActivity = 0, clientSame = 0;
        long lagMin = long.MaxValue, lagMax = long.MinValue;
        var clientProtocols = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (Call call in clientCom)
        {
            Call[] carriers = call.Start.Activity == Guid.Empty ? [] : [.. clientRpc.Where(rpc => rpc.Start.Activity == call.Start.Activity)];
            if (carriers.Length != 1)
            {
                clientNone += carriers.Length == 0 ? 1 : 0;
                clientSeveral += carriers.Length > 1 ? 1 : 0;
                continue;
            }

            Call carrier = carriers[0];
            clientCarriers[call] = carrier;
            clientThread += carrier.Start.ThreadId == call.Start.ThreadId && carrier.Stop.ThreadId == call.Stop.ThreadId ? 1 : 0;
            clientStartsInside += carrier.Start.UtcTicks >= call.Start.UtcTicks && carrier.Start.UtcTicks <= call.Stop.UtcTicks ? 1 : 0;
            clientStopsAfter += carrier.Stop.UtcTicks >= call.Stop.UtcTicks ? 1 : 0;
            lagMin = Math.Min(lagMin, carrier.Stop.UtcTicks - call.Stop.UtcTicks);
            lagMax = Math.Max(lagMax, carrier.Stop.UtcTicks - call.Stop.UtcTicks);
            clientStopActivity += call.Stop.Activity == carrier.Stop.Activity ? 1 : 0;
            clientSame += Carries(carrier, call) ? 1 : 0;
            Add(clientProtocols, Convert.ToString(carrier.Start.Fields["Protocol"], CultureInfo.InvariantCulture)!);
        }

        // At the server, the RPC call on a COM call's thread around it.
        var serverCarriers = new Dictionary<Call, Call>();
        int serverNone = 0, serverSeveral = 0, serverSame = 0, serverActivity = 0;
        var serverProtocols = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (Call call in serverCom)
        {
            Call[] carriers = [.. serverRpc.Where(rpc => rpc.Start.ThreadId == call.Start.ThreadId && rpc.Start.UtcTicks <= call.Start.UtcTicks
                && rpc.Stop.UtcTicks >= call.Stop.UtcTicks)];
            if (carriers.Length != 1)
            {
                serverNone += carriers.Length == 0 ? 1 : 0;
                serverSeveral += carriers.Length > 1 ? 1 : 0;
                continue;
            }

            Call carrier = carriers[0];
            serverCarriers[call] = carrier;
            serverSame += Carries(carrier, call) ? 1 : 0;
            serverActivity += carrier.Start.Activity == call.Start.Activity ? 1 : 0;
            Add(serverProtocols, Convert.ToString(carrier.Start.Fields["Protocol"], CultureInfo.InvariantCulture)!);
        }

        // Across the processes: the server's COM call carrying a client COM call's activity.
        Dictionary<Guid, Call[]> servedByActivity = serverCom.Where(call => call.Start.Activity != Guid.Empty).GroupBy(call => call.Start.Activity)
            .ToDictionary(group => group.Key, group => group.ToArray());
        HashSet<Guid> clientActivities = [.. clientCom.Select(call => call.Start.Activity).Where(activity => activity != Guid.Empty)];
        var servedBy = new Dictionary<Call, Call>();
        int unlinked = 0, linkedToSeveral = 0, namingEachOther = 0, sameMethod = 0, servedInside = 0, serverStopActivity = 0;
        foreach (Call call in clientCom)
        {
            Call[] matches = call.Start.Activity != Guid.Empty && servedByActivity.TryGetValue(call.Start.Activity, out Call[]? found) ? found : [];
            if (matches.Length != 1)
            {
                unlinked += matches.Length == 0 ? 1 : 0;
                linkedToSeveral += matches.Length > 1 ? 1 : 0;
                continue;
            }

            Call match = matches[0];
            servedBy[call] = match;
            namingEachOther += (ulong)call.Start.Fields["TargetProcessId"] == (ulong)server && (ulong)match.Start.Fields["SourceProcessId"] == (ulong)client ? 1 : 0;
            sameMethod += Method(call) == Method(match) ? 1 : 0;
            servedInside += match.Start.UtcTicks >= call.Start.UtcTicks && match.Stop.UtcTicks <= call.Stop.UtcTicks ? 1 : 0;
            serverStopActivity += match.Stop.Activity == call.Start.Activity ? 1 : 0;
        }

        // ADR-077's pairing of invokes by causality and rank, checked against the activity's.
        bool IsInvoke(Call call, string otherEnd, int other) => (Guid)call.Start.Fields["TargetInterface"] == Dispatch
            && (ulong)call.Start.Fields["TargetMethod"] == 6 && (ulong)call.Start.Fields[otherEnd] == (ulong)other;
        Dictionary<Guid, Call[]> servedInvokes = serverCom.Where(call => IsInvoke(call, "SourceProcessId", client))
            .GroupBy(call => (Guid)call.Start.Fields["CausalityId"]).ToDictionary(group => group.Key, group => group.OrderBy(call => call.Start.UtcTicks).ToArray());
        int rankPairs = 0, rankPairsLinkedAlike = 0;
        foreach (IGrouping<Guid, Call> chain in clientCom.Where(call => IsInvoke(call, "TargetProcessId", server)).GroupBy(call => (Guid)call.Start.Fields["CausalityId"]))
        {
            Call[] made = [.. chain.OrderBy(call => call.Start.UtcTicks)];
            if (!servedInvokes.TryGetValue(chain.Key, out Call[]? taken))
            {
                continue;
            }

            for (int rank = 0; rank < Math.Min(made.Length, taken.Length); rank++)
            {
                rankPairs++;
                rankPairsLinkedAlike += servedBy.TryGetValue(made[rank], out Call? linked) && ReferenceEquals(linked, taken[rank]) ? 1 : 0;
            }
        }

        // The RPC calls this links: a client's RPC call, through its COM call and the server's, to the RPC call that carried
        // the call served.
        var rpcLinks = new List<(Call Client, Call Server)>();
        foreach ((Call com, Call carrier) in clientCarriers)
        {
            if (servedBy.TryGetValue(com, out Call? match) && serverCarriers.TryGetValue(match, out Call? serving))
            {
                rpcLinks.Add((carrier, serving));
            }
        }

        HashSet<Call> carriedAtTheClient = [.. clientCarriers.Values];
        HashSet<Call> carriedAtTheServer = [.. serverCarriers.Values];

        string[] issued = [.. File.ReadLines(Path.Combine(truth, "truth-client.jsonl")).Where(line => line.Length > 0)
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("kind").GetString()!)];
        int served = File.ReadLines(Path.Combine(truth, "truth-server.jsonl")).Count(line => line.Contains("\"CallServed\"", StringComparison.Ordinal));
        var report = new
        {
            schema = Schema,
            measuredUtc = DateTimeOffset.UtcNow,
            build = CapabilityInventoryProbe.DescribeEnvironment().BuildId,
            workload = new { calls, delay, burst },
            session = new
            {
                eventsLost = lost,
                realTimeBuffersLost,
                sourceEventsLost,
                records = all.Length,
                comPerfRecords = all.Count(record => !record.FromRpc),
                rpcCallRecords = all.Count(record => record.FromRpc),
                unreadable,
                dropped,
                processesRaisingRecords = all.Select(record => record.ProcessId).Distinct().Count(),
                note = "COM-Perf enabled machine-wide at verbose, every keyword, and Microsoft-Windows-RPC at informational with no keyword mask, "
                    + "as the product's profiles request it, in one real-time session: the workload's processes do not exist before it starts, so "
                    + "their records are those raised in them.",
            },
            truth = new
            {
                issued = issued.Count(kind => kind == "CallIssued"),
                completedOk = issued.Count(kind => kind == "CallCompleted"),
                served,
            },
            client = new
            {
                comCalls = clientCom.Count,
                comStarts = Starts(client, fromRpc: false, 39, withNoActivity: false),
                comStartsWithNoActivity = Starts(client, fromRpc: false, 39, withNoActivity: true),
                rpcStarts = Starts(client, fromRpc: true, 5, withNoActivity: false),
                rpcStartsWithNoActivity = Starts(client, fromRpc: true, 5, withNoActivity: true),
                rpcCalls = clientRpc.Count,
                comCallsWithOneRpcCallOfTheirActivity = clientCarriers.Count,
                comCallsWithNoRpcCallOfTheirActivity = clientNone,
                comCallsWithSeveralRpcCallsOfTheirActivity = clientSeveral,
                carriersOnTheComCallsThread = clientThread,
                carriersStartingInsideTheComCall = clientStartsInside,
                carriersStoppingAtOrAfterTheComCallsStop = clientStopsAfter,
                carrierStopAfterTheComCallsStopMicroseconds = clientCarriers.Count == 0 ? null : new { min = lagMin / 10.0, max = lagMax / 10.0 },
                comStopsSharingTheirCarriersActivity = clientStopActivity,
                carriersOnTheComCallsInterfaceAndMethod = clientSame,
                carriersByProtocol = clientProtocols,
                comCallsByMethod = ByCount(clientCom.Select(Method)),
                rpcCallsCarryingNoComCall = clientRpc.Count(call => !carriedAtTheClient.Contains(call)),
                rpcCallsCarryingNoComCallByProcedure = ByCount(clientRpc.Where(call => !carriedAtTheClient.Contains(call)).Select(Procedure)),
                otherRpcEvents = OtherRpcEvents(client),
            },
            server = new
            {
                comCalls = serverCom.Count,
                comStarts = Starts(server, fromRpc: false, 43, withNoActivity: false),
                comStartsWithNoActivity = Starts(server, fromRpc: false, 43, withNoActivity: true),
                rpcStarts = Starts(server, fromRpc: true, 6, withNoActivity: false),
                rpcStartsWithNoActivity = Starts(server, fromRpc: true, 6, withNoActivity: true),
                rpcCalls = serverRpc.Count,
                comCallsWithOneRpcCallAroundThemOnTheirThread = serverCarriers.Count,
                comCallsWithNoRpcCallAroundThemOnTheirThread = serverNone,
                comCallsWithSeveralRpcCallsAroundThemOnTheirThread = serverSeveral,
                carriersOnTheComCallsInterfaceAndMethod = serverSame,
                carriersSharingTheComCallsActivity = serverActivity,
                carriersByProtocol = serverProtocols,
                rpcCallsCarryingNoComCall = serverRpc.Count(call => !carriedAtTheServer.Contains(call)),
                rpcCallsCarryingNoComCallByProcedure = ByCount(serverRpc.Where(call => !carriedAtTheServer.Contains(call)).Select(Procedure)),
                serverRpcCallsSharingAClientActivity = serverRpc.Count(call => clientActivities.Contains(call.Start.Activity)),
                otherRpcEvents = OtherRpcEvents(server),
            },
            across = new
            {
                clientCallsWhoseActivityOneServerCallCarries = servedBy.Count,
                clientCallsWhoseActivityNoServerCallCarries = unlinked,
                clientCallsWhoseActivitySeveralServerCallsCarry = linkedToSeveral,
                serverCallsOfAnActivityNoClientCallCarries = serverCom.Count(call => !clientActivities.Contains(call.Start.Activity)),
                serverCallsLinkedMoreThanOnce = servedBy.Values.GroupBy(call => call).Count(group => group.Count() > 1),
                linkedCallsNamingEachOthersProcess = namingEachOther,
                linkedCallsOnOneInterfaceAndMethod = sameMethod,
                linkedServerCallsInsideTheClientsCall = servedInside,
                linkedServerStopsCarryingTheActivity = serverStopActivity,
                invokePairsByCausalityAndRank = rankPairs,
                invokePairsTheActivityLinksAlike = rankPairsLinkedAlike,
                clientRpcCallsLinkedToTheServerRpcCallThatServedIt = rpcLinks.Count,
                linkedRpcCallsOnOneInterfaceAndProcedure = rpcLinks.Count(link => Procedure(link.Client) == Procedure(link.Server)),
                serverRpcCallsLinkedMoreThanOnce = rpcLinks.GroupBy(link => link.Server).Count(group => group.Count() > 1),
            },
        };
        await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, Json));
        if (ledger)
        {
            // Threads and activities as ordinals of the run, in the order they first appear: they say which records share
            // one, never which thread or activity it was.
            var threads = new Dictionary<(int Process, int Thread), int>();
            var activities = new Dictionary<Guid, int>();
            string ThreadOrdinal(Layered record)
            {
                if (!threads.TryGetValue((record.ProcessId, record.ThreadId), out int ordinal))
                {
                    ordinal = threads.Count(entry => entry.Key.Process == record.ProcessId) + 1;
                    threads[(record.ProcessId, record.ThreadId)] = ordinal;
                }

                return ordinal.ToString(CultureInfo.InvariantCulture);
            }

            string ActivityOrdinal(Layered record)
            {
                if (record.Activity == Guid.Empty)
                {
                    return string.Empty;
                }

                if (!activities.TryGetValue(record.Activity, out int ordinal))
                {
                    ordinal = activities.Count + 1;
                    activities[record.Activity] = ordinal;
                }

                return ordinal.ToString(CultureInfo.InvariantCulture);
            }

            string Field(Layered record, string name) => record.Fields.TryGetValue(name, out object? value)
                ? value is Guid guid ? guid.ToString("B", CultureInfo.InvariantCulture) : Convert.ToString(value, CultureInfo.InvariantCulture)!
                : string.Empty;
            await File.WriteAllLinesAsync(Path.Combine(output, "ledger.csv"),
                ["utcTicks,raisedIn,provider,event,thread,activity,callTrace,otherEnd,interface,method,protocol,status",
                    .. workloads.Select(record => string.Join(',',
                        record.UtcTicks.ToString(CultureInfo.InvariantCulture),
                        Role(record.ProcessId),
                        record.FromRpc ? "rpc" : "com-perf",
                        record.Id.ToString(CultureInfo.InvariantCulture),
                        ThreadOrdinal(record),
                        ActivityOrdinal(record),
                        Field(record, "CallTraceId"),
                        record.Fields.TryGetValue("TargetProcessId", out object? target) ? Role((long)(ulong)target)
                            : record.Fields.TryGetValue("SourceProcessId", out object? source) ? Role((long)(ulong)source) : string.Empty,
                        record.FromRpc ? Field(record, "InterfaceUuid") : Field(record, "TargetInterface"),
                        record.FromRpc ? Field(record, "ProcNum") : Field(record, "TargetMethod"),
                        Field(record, "Protocol"),
                        record.Fields.TryGetValue("HRESULT", out object? hresult) ? Hresult(hresult)
                            : record.Fields.TryGetValue("Status", out object? status) ? Hresult(status) : string.Empty))]);
        }

        Console.WriteLine(JsonSerializer.Serialize(new { report.session, report.truth, report.client, report.server, report.across }, Json));
        return 0;
    }

    private static string? CheckRpc(ProviderSchema schema)
    {
        foreach ((int id, string[] needed) in RpcNeeded)
        {
            if (schema.FindEvent(id) is not { } descriptor)
            {
                return $"RPC's event {id} is not declared";
            }

            string[] missing = [.. needed.Where(name => descriptor.Fields.All(field => field.Name != name))];
            if (missing.Length > 0)
            {
                return $"RPC's event {id} declares no {string.Join(", ", missing)}";
            }
        }

        return null;
    }
}
