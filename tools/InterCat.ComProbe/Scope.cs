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
/// What scopes Microsoft-Windows-COM-Perf's records (ADR-077's fourth decision). With --filter, whether a session's process
/// filter holds for it: FX-COM-001's exchange, its two processes the filter's, beside a decoy pair running the same exchange
/// outside it, every process waiting before it initializes COM so the filter is in place first. With --in-process, what
/// FX-COM-002's calls record - one process calling an apartment-threaded object COM hosts in an apartment of its own,
/// calls that cross an apartment and never a process. Only counters are written, and with --ledger each workload record's
/// metadata, never a PID, a thread or a pointer.
/// </summary>
internal static class Scope
{
    private const string Schema = "intercat.com-perf-scope.v1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static readonly string[] TruthFiles = ["truth-client.jsonl", "truth-server.jsonl"];

    private sealed record Collected(Record[] Records, long Unreadable, int EventsLost, long? RealTimeBuffersLost, long SourceEventsLost);

    public static async Task<int> RunAsync(string output, string[] args)
    {
        int calls = int.Parse(Option(args, "--calls") ?? "64", CultureInfo.InvariantCulture);
        int delay = int.Parse(Option(args, "--delay") ?? "5", CultureInfo.InvariantCulture);
        bool ledger = args.Contains("--ledger", StringComparer.Ordinal);
        string workload = Option(args, "--workload") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "InterCat.TestWorkloads", "bin", "Release", "net10.0", "InterCat.TestWorkloads.exe"));
        if (TraceEventSession.IsElevated() != true)
        {
            Console.Error.WriteLine("Enabling a provider for other processes needs elevation; nothing was started.");
            return 3;
        }

        ManifestReadResult manifest = new TdhEtwMetadataSource().TryReadManifest(ComPerf);
        ProviderSchema? schema = manifest.ManifestXml is null ? null : ManifestParser.Parse(manifest.ManifestXml);
        string? problem = schema is null ? "the provider's schema is not registered on this machine: " + manifest.FailureReason : CheckSchema(schema);
        if (problem is not null)
        {
            Console.Error.WriteLine("COM-Perf's events do not have the layout this probe reads: " + problem);
            return 4;
        }

        Dictionary<(int, int), ProviderSchemaEvent> events = schema!.Events.ToDictionary(descriptor => (descriptor.EventId, descriptor.Version));
        string arguments = string.Create(CultureInfo.InvariantCulture, $"--calls {calls} --delay {delay}");
        return args.Contains("--filter", StringComparer.Ordinal)
            ? await FilterAsync(output, workload, arguments, events, calls, delay, ledger)
            : await InProcessAsync(output, workload, arguments, events, calls, delay, ledger);
    }

    private static async Task<int> FilterAsync(string output, string workload, string arguments, Dictionary<(int, int), ProviderSchemaEvent> events,
        int calls, int delay, bool ledger)
    {
        string kept = Path.Combine(output, "truth-filtered");
        string decoy = Path.Combine(output, "truth-decoy");
        using Process server = Start(workload, $"com-loopback-server --truth \"{kept}\" {arguments} --wait-for-start", output: true);
        using Process decoyServer = Start(workload, $"com-loopback-server --truth \"{decoy}\" {arguments} --wait-for-start", output: true);
        using Process client = Start(workload, $"com-loopback-client --truth \"{kept}\" {arguments} --moniker {Moniker(server)} --wait-for-start", output: false);
        using Process decoyClient = Start(workload, $"com-loopback-client --truth \"{decoy}\" {arguments} --moniker {Moniker(decoyServer)} --wait-for-start", output: false);

        Collected collected = await CollectAsync(events, [server.Id, client.Id], async () =>
        {
            foreach (Process started in new[] { server, decoyServer })
            {
                await started.StandardInput.WriteLineAsync("start");
                await started.StandardInput.FlushAsync();
                _ = await started.StandardOutput.ReadLineAsync();
            }

            foreach (Process started in new[] { client, decoyClient })
            {
                await started.StandardInput.WriteLineAsync("start");
                await started.StandardInput.FlushAsync();
            }

            await Task.WhenAll(client.WaitForExitAsync(), decoyClient.WaitForExitAsync());
            foreach (Process started in new[] { server, decoyServer })
            {
                await started.StandardInput.WriteLineAsync("stop");
                await started.StandardInput.FlushAsync();
            }

            await Task.WhenAll(server.WaitForExitAsync(), decoyServer.WaitForExitAsync());
        });

        string Role(long process) => process == server.Id ? "server" : process == client.Id ? "client"
            : process == decoyServer.Id ? "decoy server" : process == decoyClient.Id ? "decoy client" : "another process";
        int Invokes(int raisedIn, int startId, string otherEnd, int other) => collected.Records.Count(record => record.ProcessId == raisedIn
            && record.Id == startId && (Guid)record.Fields["TargetInterface"] == Dispatch && (ulong)record.Fields["TargetMethod"] == 6
            && (ulong)record.Fields[otherEnd] == (ulong)other);
        var report = new
        {
            schema = Schema,
            measuredUtc = DateTimeOffset.UtcNow,
            build = CapabilityInventoryProbe.DescribeEnvironment().BuildId,
            mode = "filter",
            workload = new { calls, delay },
            session = Session(collected, "COM-Perf enabled at verbose, every keyword, scoped by the session's process filter to the filtered pair's two processes."),
            exitCodes = new { server = server.ExitCode, client = client.ExitCode, decoyServer = decoyServer.ExitCode, decoyClient = decoyClient.ExitCode },
            truth = new { filtered = Truth(kept), decoy = Truth(decoy) },
            byProcess = collected.Records.GroupBy(record => Role(record.ProcessId)).ToDictionary(group => group.Key, group => group.Count()),
            filteredInvokes = new
            {
                atTheClientNamingTheServer = Invokes(client.Id, 39, "TargetProcessId", server.Id),
                atTheServerNamingTheClient = Invokes(server.Id, 43, "SourceProcessId", client.Id),
            },
            decoyInvokes = new
            {
                atTheClientNamingTheServer = Invokes(decoyClient.Id, 39, "TargetProcessId", decoyServer.Id),
                atTheServerNamingTheClient = Invokes(decoyServer.Id, 43, "SourceProcessId", decoyClient.Id),
            },
        };
        await WriteAsync(output, report, ledger ? collected.Records.Where(record => record.ProcessId == server.Id || record.ProcessId == client.Id) : null, Role);
        Console.WriteLine(JsonSerializer.Serialize(new { report.byProcess, report.filteredInvokes, report.decoyInvokes, report.truth }, Json));
        return 0;
    }

    private static async Task<int> InProcessAsync(string output, string workload, string arguments, Dictionary<(int, int), ProviderSchemaEvent> events,
        int calls, int delay, bool ledger)
    {
        string truth = Path.Combine(output, "truth");
        int process = 0;
        Collected collected = await CollectAsync(events, null, async () =>
        {
            using Process inProcess = Start(workload, $"com-inproc --truth \"{truth}\" {arguments}", output: false);
            process = inProcess.Id;
            await inProcess.WaitForExitAsync();
        });

        Record[] own = [.. collected.Records.Where(record => record.ProcessId == process).OrderBy(record => record.UtcTicks)];
        string Role(long raisedIn) => raisedIn == process ? "the process" : "another process";
        object Calls(int startId, int stopId, string otherEnd)
        {
            Record[] starts = [.. own.Where(record => record.Id == startId)];
            HashSet<ulong> stopped = [.. own.Where(record => record.Id == stopId).Select(record => (ulong)record.Fields["CallTraceId"])];
            return new
            {
                starts = starts.Length,
                stops = own.Count(record => record.Id == stopId),
                startsJoinedToAStopByTraceNumber = starts.Count(record => stopped.Contains((ulong)record.Fields["CallTraceId"])),
                startsNamingTheirOwnProcess = starts.Count(record => (ulong)record.Fields[otherEnd] == (ulong)process),
                byInterfaceAndMethod = starts.GroupBy(record => $"{(Guid)record.Fields["TargetInterface"]:B} method {record.Fields["TargetMethod"]} to {Role((long)(ulong)record.Fields[otherEnd])}")
                    .OrderByDescending(group => group.Count()).ToDictionary(group => group.Key, group => group.Count()),
            };
        }

        int Invokes(int startId, string otherEnd) => own.Count(record => record.Id == startId && (Guid)record.Fields["TargetInterface"] == Dispatch
            && (ulong)record.Fields["TargetMethod"] == 6 && (ulong)record.Fields[otherEnd] == (ulong)process);
        var report = new
        {
            schema = Schema,
            measuredUtc = DateTimeOffset.UtcNow,
            build = CapabilityInventoryProbe.DescribeEnvironment().BuildId,
            mode = "in-process",
            workload = new { calls, delay },
            session = Session(collected, "COM-Perf enabled machine-wide at verbose, every keyword: the workload's process does not exist before the session starts."),
            truth = Truth(truth),
            byEvent = own.GroupBy(record => record.Id).OrderBy(group => group.Key).ToDictionary(group => group.Key.ToString(CultureInfo.InvariantCulture), group => group.Count()),
            clientCalls = Calls(39, 40, "TargetProcessId"),
            serverCalls = Calls(43, 44, "SourceProcessId"),
            invokes = new
            {
                atTheClientNamingItsOwnProcess = Invokes(39, "TargetProcessId"),
                atTheServerNamingItsOwnProcess = Invokes(43, "SourceProcessId"),
            },
        };
        await WriteAsync(output, report, ledger ? own : null, Role);
        Console.WriteLine(JsonSerializer.Serialize(new { report.truth, report.invokes, report.byEvent }, Json));
        return 0;
    }

    /// <summary>Runs <paramref name="run"/> under a real-time session of COM-Perf, drained after its stop before it is disposed.</summary>
    private static async Task<Collected> CollectAsync(Dictionary<(int, int), ProviderSchemaEvent> events, int[]? filter, Func<Task> run)
    {
        var records = new ConcurrentQueue<Record>();
        long unreadable = 0;
        string sessionName = "InterCat-ComProbe-" + Guid.NewGuid().ToString("N");
        int lost;
        long? realTimeBuffersLost = null;
        long sourceEventsLost;
        using (var session = new TraceEventSession(sessionName) { StopOnDispose = true })
        {
            session.Source.AllEvents += data =>
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

                records.Enqueue(new((int)data.ID, data.Version, data.ProcessID, data.ThreadID, data.TimeStamp.ToUniversalTime().Ticks, fields));
            };
            session.EnableProvider(ComPerf, TraceEventLevel.Verbose, ulong.MaxValue,
                filter is null ? null : new TraceEventProviderOptions { ProcessIDFilter = [.. filter] });
            var reader = new Thread(() => session.Source.Process()) { IsBackground = true, Name = "com-probe-scope" };
            reader.Start();
            await Task.Delay(1_000);
            await run();
            await Task.Delay(2_000);
            lost = session.EventsLost;
            if (Stop(sessionName) is { } final)
            {
                lost = (int)Math.Max(lost, final.EventsLost);
                realTimeBuffersLost = final.RealTimeBuffersLost;
            }

            if (!reader.Join(TimeSpan.FromSeconds(30)))
            {
                Console.Error.WriteLine("The session's reader did not finish within 30 seconds of its stop; its counts may be short.");
            }

            sourceEventsLost = session.Source.EventsLost;
        }

        return new([.. records], unreadable, lost, realTimeBuffersLost, sourceEventsLost);
    }

    private static object Session(Collected collected, string note) => new
    {
        eventsLost = collected.EventsLost,
        collected.RealTimeBuffersLost,
        collected.SourceEventsLost,
        records = collected.Records.Length,
        collected.Unreadable,
        processesRaisingRecords = collected.Records.Select(record => record.ProcessId).Distinct().Count(),
        note,
    };

    private static object Truth(string directory)
    {
        string[] kinds = [.. TruthFiles.Where(file => File.Exists(Path.Combine(directory, file)))
            .SelectMany(file => File.ReadLines(Path.Combine(directory, file)).Where(line => line.Length > 0)
                .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("kind").GetString()!))];
        return new
        {
            issued = kinds.Count(kind => kind == "CallIssued"),
            completed = kinds.Count(kind => kind == "CallCompleted"),
            served = kinds.Count(kind => kind == "CallServed"),
        };
    }

    private static async Task WriteAsync(string output, object report, IEnumerable<Record>? ledger, Func<long, string> role)
    {
        await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, Json));
        if (ledger is null)
        {
            return;
        }

        await File.WriteAllLinesAsync(Path.Combine(output, "ledger.csv"),
            ["utcTicks,raisedIn,event,callTrace,otherEnd,interface,method,causality,hresult",
                .. ledger.OrderBy(record => record.UtcTicks).Select(record => string.Join(',',
                    record.UtcTicks.ToString(CultureInfo.InvariantCulture),
                    role(record.ProcessId),
                    record.Id.ToString(CultureInfo.InvariantCulture),
                    record.Fields.TryGetValue("CallTraceId", out object? trace) ? Convert.ToString(trace, CultureInfo.InvariantCulture) : string.Empty,
                    record.Fields.TryGetValue("TargetProcessId", out object? target) ? role((long)(ulong)target)
                        : record.Fields.TryGetValue("SourceProcessId", out object? source) ? role((long)(ulong)source) : string.Empty,
                    record.Fields.TryGetValue("TargetInterface", out object? iid) ? ((Guid)iid).ToString("B", CultureInfo.InvariantCulture) : string.Empty,
                    record.Fields.TryGetValue("TargetMethod", out object? method) ? Convert.ToString(method, CultureInfo.InvariantCulture) : string.Empty,
                    record.Fields.TryGetValue("CausalityId", out object? causality) ? ((Guid)causality).ToString("N", CultureInfo.InvariantCulture) : string.Empty,
                    record.Fields.TryGetValue("HRESULT", out object? hresult) ? Hresult(hresult) : string.Empty))]);
    }

    private static string Moniker(Process server) => string.Create(CultureInfo.InvariantCulture, $"InterCat.FX-COM-001.{server.Id}");

    private static Process Start(string workload, string arguments, bool output) =>
        Process.Start(new ProcessStartInfo(workload, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = output,
        }) ?? throw new InvalidOperationException("The workload could not be started.");
}
