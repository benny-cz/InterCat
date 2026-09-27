using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.AlpcProbe;

/// <summary>
/// IC-006's ALPC spike (plan §7.4, ADR-004): whether the kernel's ALPC events, read through a private system logger of this
/// probe's own, link an RPC client call to the server call that served it. It runs FX-RPC-001's workload - calls to the
/// service control manager - while one uniquely named session collects the ALPC flag group, process names and the RPC
/// provider's call events, then follows each client call's ALPC send by message id to the thread that received it, and
/// that thread to the server call it began. A link is checked against what both calls carry: the interface and the
/// procedure. Only counters are written; the records themselves hold every process on the machine and stay in memory.
/// </summary>
internal static class Program
{
    private const string Schema = "intercat.alpc-feasibility.v1";
    private static readonly Guid RpcProvider = Guid.Parse("6ad52b32-d609-4be9-ae07-ce8dae937e39");
    private static readonly Guid ServiceControl = Guid.Parse("367abb81-9844-35f1-ad32-98f038001003");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        string? output = Option(args, "--output");
        int calls = int.Parse(Option(args, "--calls") ?? "200", CultureInfo.InvariantCulture);
        string workload = Option(args, "--workload") ?? Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "InterCat.TestWorkloads", "bin", "Release", "net10.0",
            "InterCat.TestWorkloads.exe"));
        if (output is null || calls < 1)
        {
            Console.Error.WriteLine("InterCat.AlpcProbe --output <new directory> [--calls n] [--workload <InterCat.TestWorkloads.exe>]");
            return 2;
        }

        if (TraceEventSession.IsElevated() != true)
        {
            Console.Error.WriteLine("An ETW system logger needs elevation; nothing was started.");
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

        if (args.Contains("--impact"))
        {
            int pairs = int.Parse(Option(args, "--pairs") ?? "5", CultureInfo.InvariantCulture);
            Dictionary<string, object?> impact = await Impact.MeasureAsync(workload, calls, pairs).ConfigureAwait(false);
            impact["schema"] = "intercat.alpc-impact.v1";
            impact["measuredUtc"] = DateTimeOffset.UtcNow;
            impact["machine"] = $"{Environment.ProcessorCount} logical processors · {Environment.OSVersion}";
            impact["workload"] = new Dictionary<string, object> { ["scenario"] = "FX-RPC-001", ["callsPerTrial"] = calls };
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "impact.json"), JsonSerializer.Serialize(impact, Json)).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(impact, Json));
            return 0;
        }

        string scratch = Path.Combine(Path.GetTempPath(), "InterCat-alpc-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var capture = new Capture();
            var clock = Stopwatch.StartNew();
            int lost;
            string sessionName = "InterCat-alpc-probe-" + Guid.NewGuid().ToString("N");

            // A session of its own name: on Windows 8 and later a kernel provider in a session not named NT Kernel Logger
            // is a private system logger, which this probe owns and stops, and which touches no other session.
            using (var session = new TraceEventSession(sessionName) { StopOnDispose = true, BufferSizeMB = 128 })
            {
                session.EnableKernelProvider(
                    KernelTraceEventParser.Keywords.AdvancedLocalProcedureCalls | KernelTraceEventParser.Keywords.Process);
                session.EnableProvider(
                    RpcProvider,
                    TraceEventLevel.Informational,
                    ulong.MaxValue,
                    new TraceEventProviderOptions { EventIDsToEnable = [5, 6, 7, 8] });
                capture.Subscribe(session.Source);
                Task processing = Task.Run(() => session.Source.Process());
                await Task.Delay(1_500).ConfigureAwait(false);
                double started = clock.Elapsed.TotalSeconds;
                using (Process run = Process.Start(new ProcessStartInfo(workload, ["rpc-local", "--truth", scratch, "--calls", calls.ToString(CultureInfo.InvariantCulture)])
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                })!)
                {
                    await run.WaitForExitAsync().ConfigureAwait(false);
                    if (run.ExitCode != 0)
                    {
                        Console.Error.WriteLine($"The truth workload exited with {run.ExitCode}.");
                        return 4;
                    }
                }

                capture.WorkloadSeconds = clock.Elapsed.TotalSeconds - started;
                await Task.Delay(1_500).ConfigureAwait(false);
                lost = session.EventsLost;
                session.Stop();
                await processing.ConfigureAwait(false);
            }

            (int clientProcessId, int truthCalls) = ReadTruth(Path.Combine(scratch, "truth-client.jsonl"));
            Dictionary<string, object?> report = Analysis.Report(capture, clientProcessId, truthCalls, ServiceControl);
            report["schema"] = Schema;
            report["measuredUtc"] = DateTimeOffset.UtcNow;
            report["machine"] = $"{Environment.ProcessorCount} logical processors · {Environment.OSVersion}";
            report["session"] = new Dictionary<string, object>
            {
                ["mode"] = "private system logger, uniquely named, owned and stopped by the probe",
                ["kernelFlags"] = new[] { "AdvancedLocalProcedureCalls", "Process" },
                ["userProviders"] = new[] { "Microsoft-Windows-RPC events 5, 6, 7 and 8" },
                ["eventsLost"] = lost,
            };
            report["workload"] = new Dictionary<string, object>
            {
                ["scenario"] = "FX-RPC-001",
                ["callsRequested"] = calls,
                ["truthCalls"] = truthCalls,
                ["seconds"] = Math.Round(capture.WorkloadSeconds, 1),
            };
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, Json)).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(report, Json));
            return 0;
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static string? Option(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    /// <summary>The workload's client process and how many calls its truth log completed.</summary>
    private static (int ProcessId, int Calls) ReadTruth(string path)
    {
        int processId = 0;
        int calls = 0;
        foreach (string line in File.ReadLines(path))
        {
            using var document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            string kind = root.GetProperty("kind").GetString()!;
            if (kind == "ProcessStarted")
            {
                processId = root.GetProperty("processId").GetInt32();
            }
            else if (kind == "CallCompleted")
            {
                calls++;
            }
        }

        return (processId, calls);
    }
}

internal enum AlpcKind
{
    Send,
    Receive,
    WaitForReply,
    WaitForNewMessage,
    Unwait,
}

/// <summary>One ALPC event: when, in which process and thread, and the message it names.</summary>
internal readonly record struct AlpcEvent(AlpcKind Kind, double Milliseconds, int ProcessId, int ThreadId, int MessageId);

/// <summary>One RPC call record: a start or stop, on the client or the server side, with what the provider carried.</summary>
internal readonly record struct RpcRecord(
    bool IsStart,
    bool IsServer,
    double Milliseconds,
    int ProcessId,
    int ThreadId,
    Guid Activity,
    Guid Interface,
    int Procedure);

/// <summary>What the probe's session delivered, gathered on the processing thread and read after it ends.</summary>
internal sealed class Capture
{
    public List<AlpcEvent> Alpc { get; } = [];

    public List<RpcRecord> Rpc { get; } = [];

    public Dictionary<int, string> ImageNames { get; } = [];

    public double WorkloadSeconds { get; set; }

    public double FirstMilliseconds { get; private set; } = double.MaxValue;

    public double LastMilliseconds { get; private set; }

    public void Subscribe(TraceEventDispatcher source)
    {
        source.Kernel.ALPCSendMessage += data => Add(AlpcKind.Send, data, data.MessageID);
        source.Kernel.ALPCReceiveMessage += data => Add(AlpcKind.Receive, data, data.MessageID);
        source.Kernel.ALPCWaitForReply += data => Add(AlpcKind.WaitForReply, data, data.MessageID);
        source.Kernel.ALPCWaitForNewMessage += data => Add(AlpcKind.WaitForNewMessage, data, 0);
        source.Kernel.ALPCUnwait += data => Add(AlpcKind.Unwait, data, 0);
        source.Kernel.ProcessStart += data => ImageNames[data.ProcessID] = data.ImageFileName;
        source.Kernel.ProcessDCStart += data => ImageNames[data.ProcessID] = data.ImageFileName;
        var registered = new RegisteredTraceEventParser(source);
        registered.All += data =>
        {
            if (data.ProviderGuid != Guid.Parse("6ad52b32-d609-4be9-ae07-ce8dae937e39") || (int)data.ID is < 5 or > 8)
            {
                return;
            }

            bool isStart = (int)data.ID is 5 or 6;
            Rpc.Add(new(
                isStart,
                (int)data.ID is 6 or 8,
                data.TimeStampRelativeMSec,
                data.ProcessID,
                data.ThreadID,
                data.ActivityID,
                isStart && data.PayloadByName("InterfaceUuid") is Guid uuid ? uuid : Guid.Empty,
                isStart ? Convert.ToInt32(data.PayloadByName("ProcNum") ?? -1, CultureInfo.InvariantCulture) : -1));
        };
    }

    private void Add(AlpcKind kind, TraceEvent data, int messageId)
    {
        Alpc.Add(new(kind, data.TimeStampRelativeMSec, data.ProcessID, data.ThreadID, messageId));
        FirstMilliseconds = Math.Min(FirstMilliseconds, data.TimeStampRelativeMSec);
        LastMilliseconds = Math.Max(LastMilliseconds, data.TimeStampRelativeMSec);
    }
}

/// <summary>What the capture establishes about pairing: counters only.</summary>
internal static class Analysis
{
    /// <summary>How long after a server thread's receive its call may start and still be the call that receive began.</summary>
    private const double DispatchWindowMilliseconds = 5;

    public static Dictionary<string, object?> Report(Capture capture, int clientProcessId, int truthCalls, Guid serviceControl)
    {
        double seconds = Math.Max(0.001, (capture.LastMilliseconds - capture.FirstMilliseconds) / 1000);
        var byKind = capture.Alpc.GroupBy(record => record.Kind).ToDictionary(group => group.Key.ToString(), group => (long)group.Count());
        List<AlpcEvent> sends = [.. capture.Alpc.Where(record => record.Kind == AlpcKind.Send)];
        List<AlpcEvent> receives = [.. capture.Alpc.Where(record => record.Kind == AlpcKind.Receive)];
        ILookup<int, AlpcEvent> receivesByMessage = receives.ToLookup(record => record.MessageId);
        ILookup<int, AlpcEvent> sendsByMessage = sends.ToLookup(record => record.MessageId);

        // Message ids: how often one id names more than one send, and more than one process's sends.
        long reusedIds = sendsByMessage.Count(group => group.Count() > 1);
        long idsOfSeveralProcesses = sendsByMessage.Count(group => group.Select(record => record.ProcessId).Distinct().Count() > 1);

        List<(RpcRecord Start, RpcRecord Stop)> clientCalls = Pair(capture.Rpc.Where(record => !record.IsServer && record.ProcessId == clientProcessId));
        List<(RpcRecord Start, RpcRecord Stop)> serverCalls = Pair(capture.Rpc.Where(record => record.IsServer));
        ILookup<(int, int), (RpcRecord Start, RpcRecord Stop)> serverCallsByThread =
            serverCalls.ToLookup(call => (call.Start.ProcessId, call.Start.ThreadId));
        ILookup<(int, int), AlpcEvent> clientSendsByThread = sends
            .Where(record => record.ProcessId == clientProcessId)
            .ToLookup(record => (record.ProcessId, record.ThreadId));

        var sendsPerCall = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var unlinked = new SortedDictionary<string, long>(StringComparer.Ordinal);
        long receiversOne = 0, receiversNone = 0, receiversSeveral = 0;
        long linked = 0, matching = 0, mismatching = 0, roundTrips = 0, sharedWithinSecond = 0;
        var claimed = new Dictionary<(int, int, double), int>();
        foreach ((RpcRecord start, RpcRecord stop) in clientCalls)
        {
            List<AlpcEvent> inCall = [.. clientSendsByThread[(start.ProcessId, start.ThreadId)]
                .Where(send => send.Milliseconds >= start.Milliseconds && send.Milliseconds <= stop.Milliseconds)];
            Increment(sendsPerCall, inCall.Count switch { 0 => "0", 1 => "1", _ => "2+" });
            if (inCall.Count != 1)
            {
                Increment(unlinked, inCall.Count == 0 ? "no ALPC send on the call's thread" : "several ALPC sends on the call's thread");
                continue;
            }

            AlpcEvent send = inCall[0];
            sharedWithinSecond += sendsByMessage[send.MessageId]
                .Any(other => other.ProcessId != send.ProcessId && Math.Abs(other.Milliseconds - send.Milliseconds) <= 1_000) ? 1 : 0;
            List<AlpcEvent> receivers = [.. receivesByMessage[send.MessageId]
                .Where(receive => receive.ProcessId != send.ProcessId
                    && receive.Milliseconds >= send.Milliseconds && receive.Milliseconds <= stop.Milliseconds)];
            if (receivers.Count != 1)
            {
                (receivers.Count == 0 ? ref receiversNone : ref receiversSeveral)++;
                Increment(unlinked, receivers.Count == 0 ? "no receive of the message in another process" : "several receives of the message");
                continue;
            }

            receiversOne++;
            AlpcEvent receive = receivers[0];
            (RpcRecord Start, RpcRecord Stop)[] served =
            [
                .. serverCallsByThread[(receive.ProcessId, receive.ThreadId)]
                    .Where(call => call.Start.Milliseconds >= receive.Milliseconds
                        && call.Start.Milliseconds <= receive.Milliseconds + DispatchWindowMilliseconds
                        && call.Start.Milliseconds <= stop.Milliseconds)
                    .OrderBy(call => call.Start.Milliseconds)
                    .Take(1),
            ];
            if (served.Length == 0)
            {
                Increment(unlinked, "no server call began on the receiving thread");
                continue;
            }

            linked++;
            (RpcRecord serverStart, RpcRecord serverStop) = served[0];
            var key = (serverStart.ProcessId, serverStart.ThreadId, serverStart.Milliseconds);
            claimed[key] = claimed.GetValueOrDefault(key) + 1;
            bool same = serverStart.Interface == start.Interface && serverStart.Procedure == start.Procedure;
            matching += same ? 1 : 0;
            mismatching += same ? 0 : 1;

            // The reply: the server sends the same message id back, and the client receives it, inside the client call.
            bool replied = sendsByMessage[send.MessageId].Any(reply => reply.ProcessId == receive.ProcessId
                    && reply.Milliseconds >= serverStop.Milliseconds - DispatchWindowMilliseconds && reply.Milliseconds <= stop.Milliseconds)
                && receivesByMessage[send.MessageId].Any(back => back.ProcessId == clientProcessId
                    && back.Milliseconds >= receive.Milliseconds && back.Milliseconds <= stop.Milliseconds);
            roundTrips += replied ? 1 : 0;
        }

        return new()
        {
            ["volume"] = new Dictionary<string, object>
            {
                ["alpcSeconds"] = Math.Round(seconds, 1),
                ["alpcEventsByKind"] = byKind,
                ["alpcEventsPerSecond"] = Math.Round(capture.Alpc.Count / seconds, 0),
                ["rpcCallRecords"] = capture.Rpc.Count,
                ["processesNamed"] = capture.ImageNames.Count,
            },
            ["messageIds"] = new Dictionary<string, object>
            {
                ["sends"] = sends.Count,
                ["distinctIds"] = sendsByMessage.Count,
                ["idsNamingSeveralSends"] = reusedIds,
                ["idsSentBySeveralProcesses"] = idsOfSeveralProcesses,
                ["clientSendsWhoseIdAnotherProcessSentWithinOneSecond"] = sharedWithinSecond,
            },
            ["linkage"] = new Dictionary<string, object>
            {
                ["clientProcessFound"] = clientProcessId != 0 && capture.ImageNames.ContainsKey(clientProcessId),
                ["clientCallsPaired"] = clientCalls.Count,
                ["clientCallsToServiceControl"] = clientCalls.Count(call => call.Start.Interface == serviceControl),
                ["truthCallsPerClientCall"] = clientCalls.Count == 0 ? 0 : Math.Round((double)truthCalls / clientCalls.Count, 3),
                ["alpcSendsPerClientCall"] = sendsPerCall,
                ["sendsWithOneReceiverInAnotherProcess"] = receiversOne,
                ["sendsWithNoReceiver"] = receiversNone,
                ["sendsWithSeveralReceivers"] = receiversSeveral,
                ["linkedToAServerCall"] = linked,
                ["linkedWithTheSameInterfaceAndProcedure"] = matching,
                ["linkedWithAnotherInterfaceOrProcedure"] = mismatching,
                ["serverCallsLinkedTwice"] = claimed.Count(entry => entry.Value > 1),
                ["repliesObservedBackToTheClient"] = roundTrips,
                ["unlinkedByReason"] = unlinked,
            },
            ["notes"] = new[]
            {
                "A client call's ALPC send is the one send on the call's own thread between its start and stop records; "
                    + "its receive is the one receive of the same message id in another process before the call stops; "
                    + $"the server call it began is the first to start on the receiving thread within {DispatchWindowMilliseconds} ms. "
                    + "A link is checked, never assumed: the linked calls must carry one interface and one procedure.",
                "Records stay in memory and are never written; only these counters are.",
            },
        };
    }

    /// <summary>Starts and stops paired by process, side and activity id, the next stop closing the open start.</summary>
    private static List<(RpcRecord Start, RpcRecord Stop)> Pair(IEnumerable<RpcRecord> records)
    {
        var pairs = new List<(RpcRecord Start, RpcRecord Stop)>();
        var open = new Dictionary<(int, bool, Guid), RpcRecord>();
        foreach (RpcRecord record in records.Where(record => record.Activity != Guid.Empty).OrderBy(record => record.Milliseconds))
        {
            var key = (record.ProcessId, record.IsServer, record.Activity);
            if (record.IsStart)
            {
                open[key] = record;
            }
            else if (open.Remove(key, out RpcRecord start))
            {
                pairs.Add((start, record));
            }
        }

        return pairs;
    }

    private static void Increment(SortedDictionary<string, long> counts, string key) =>
        counts[key] = counts.GetValueOrDefault(key) + 1;
}
