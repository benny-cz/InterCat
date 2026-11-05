using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.SyncProbe;

/// <summary>
/// FX-SYNC-001 under a private system logger of the probe's own (ADR-035), machine-wide: the object manager's handle group,
/// and with --objects its object group, added to the session's masks (<see cref="GroupMask"/>), beside the process, thread,
/// context switch and dispatcher flags. What the kernel records of three named objects two processes share - each process's
/// handles to them, with each object's name, address and type, and the threads one process's signals readied in the other -
/// set against each process's truth. It keeps the records of the run's objects and of the workload's processes, and counts
/// the rest.
/// </summary>
internal static class Measure
{
    private const string Schema = "intercat.sync-objects-measurement.v1";

    // Every FX-SYNC-001 object's name begins so after its namespace; the run's own name follows.
    private const string Prefix = "InterCat-FX-SYNC-001-";

    private static readonly Guid ObjectManagerClass = Guid.Parse("89497f50-effe-4440-8cf2-ce6b1cdcaca7");

    private static readonly string[] Kinds = ["event", "semaphore", "mutex"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    // How far from its truth record a signal's ready thread may lie: the record is written once the signal's call returned,
    // on the workload's clock, and the ready on the kernel's.
    private static readonly TimeSpan ReadyWindow = TimeSpan.FromMilliseconds(50);

    // How far outside a wait's span, as its waiter's log bounds it, its ready may lie once the clocks' offset is taken out:
    // the two clocks are read apart.
    private static readonly TimeSpan Slack = TimeSpan.FromMilliseconds(0.02);

    // The ready thread's flag for a thread readied from a DPC, as TraceEvent names it.
    private const int ReadiedFromDpc = 0x1;

    // KWAIT_REASON's UserRequest: a thread waiting in a user-mode wait on an object.
    private const int UserRequest = 6;

    public static async Task<int> RunAsync(string output, string[] args)
    {
        string workload = Option(args, "--workload") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "InterCat.TestWorkloads", "bin", "Release", "net10.0", "InterCat.TestWorkloads.exe"));
        int rounds = int.Parse(Option(args, "--rounds") ?? "64", CultureInfo.InvariantCulture);
        int hold = int.Parse(Option(args, "--hold-ms") ?? "5", CultureInfo.InvariantCulture);
        bool ledger = args.Contains("--ledger", StringComparer.Ordinal);
        bool lifetimes = args.Contains("--objects", StringComparer.Ordinal);
        string truth = Path.Combine(output, "truth");

        var capture = new Capture { Lifetimes = lifetimes };
        int lost;
        int coordinatorProcessId = 0;
        string[]? masks;
        string sessionName = "InterCat-sync-probe-" + Guid.NewGuid().ToString("N");

        // A session of its own name: a kernel provider in a session not named NT Kernel Logger is a private system logger,
        // which this probe owns and stops, and which touches no other session. The kernel flags start it in that mode; the
        // object manager's groups, which no flag names, are then added to its masks.
        using (var session = new TraceEventSession(sessionName) { StopOnDispose = true, BufferSizeMB = 512 })
        {
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread
                | KernelTraceEventParser.Keywords.ContextSwitch | KernelTraceEventParser.Keywords.Dispatcher);
            if (GroupMask.Add(sessionName, lifetimes ? [GroupMask.ObjectHandles, GroupMask.ObjectLifetimes] : [GroupMask.ObjectHandles]) is { } problem)
            {
                Console.Error.WriteLine("The object manager's groups could not be added to the probe's session. " + problem);
                return 5;
            }

            masks = GroupMask.Read(sessionName);
            capture.Subscribe(session.Source);
            Task processing = Task.Run(() => session.Source.Process());
            await Task.Delay(1_500).ConfigureAwait(false);
            capture.WorkloadStartedUtc = DateTime.UtcNow;
            using (Process coordinator = Process.Start(new ProcessStartInfo(workload,
                ["sync-objects", "--truth", truth, "--rounds", rounds.ToString(CultureInfo.InvariantCulture),
                    "--hold-ms", hold.ToString(CultureInfo.InvariantCulture)])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })!)
            {
                coordinatorProcessId = coordinator.Id;
                await coordinator.WaitForExitAsync().ConfigureAwait(false);
                if (coordinator.ExitCode != 0)
                {
                    Console.Error.WriteLine($"The truth workload exited with {coordinator.ExitCode}.");
                    return 4;
                }
            }

            capture.WorkloadEndedUtc = DateTime.UtcNow;
            await Task.Delay(1_500).ConfigureAwait(false);
            lost = session.EventsLost;
            session.Stop();
            await processing.ConfigureAwait(false);
        }

        using JsonDocument scenario = JsonDocument.Parse(File.ReadAllText(Path.Combine(truth, "scenario.json")));
        var run = new Run(
            scenario.RootElement.GetProperty("name").GetString()!,
            coordinatorProcessId,
            scenario.RootElement.GetProperty("creatorProcessId").GetInt32(),
            scenario.RootElement.GetProperty("openerProcessId").GetInt32(),
            Truth.Read(Path.Combine(truth, "truth-creator.jsonl")),
            Truth.Read(Path.Combine(truth, "truth-opener.jsonl")));
        Dictionary<string, object?> report = Analyze(capture, run);
        report["schema"] = Schema;
        report["measuredUtc"] = DateTimeOffset.UtcNow;
        report["machine"] = $"{Environment.ProcessorCount} logical processors · {Environment.OSVersion}";
        report["session"] = new Dictionary<string, object?>
        {
            ["mode"] = "private system logger, uniquely named, owned and stopped by the probe",
            ["kernelFlags"] = new[] { "Process", "Thread", "ContextSwitch", "Dispatcher" },
            ["groupMasks"] = lifetimes ? new[] { "PERF_OB_HANDLE", "PERF_OB_OBJECT" } : new[] { "PERF_OB_HANDLE" },
            ["masksRead"] = masks,
            ["eventsLost"] = lost,
        };
        report["workload"] = new Dictionary<string, object>
        {
            ["scenario"] = "FX-SYNC-001",
            ["rounds"] = rounds,
            ["holdMilliseconds"] = hold,
            ["seconds"] = Math.Round((capture.WorkloadEndedUtc - capture.WorkloadStartedUtc).TotalSeconds, 1),
        };
        await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, Json)).ConfigureAwait(false);
        if (ledger)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "ledger.csv"), Ledger(capture, run)).ConfigureAwait(false);
        }

        Console.WriteLine(JsonSerializer.Serialize(report, Json));
        return 0;
    }

    private static Dictionary<string, object?> Analyze(Capture capture, Run run)
    {
        var objects = new List<object>();
        int whole = 0;
        foreach (string kind in Kinds)
        {
            string name = run.ObjectName(kind);
            HandleRecord[] creates = [.. capture.Handles.Where(record => record.Kind == HandleKind.Create && Ends(record.ClosedAs ?? record.Name, name))];
            HandleRecord[] closes = [.. capture.Handles.Where(record => record.Kind == HandleKind.Close && Ends(record.Name, name))];
            var roles = new Dictionary<string, object>();
            bool objectWhole = true;
            foreach ((string role, int processId, TruthRecord[] records) in run.Roles)
            {
                int truthOpens = records.Count(record => record.Kind is "ObjectCreated" or "ObjectOpened" && record.ResourceName == name);
                int truthCloses = records.Count(record => record.Kind == "HandleClosed" && record.ResourceName == name);
                int opens = creates.Count(record => record.ProcessId == processId);
                int closed = closes.Count(record => record.ProcessId == processId);
                objectWhole &= opens == truthOpens && closed == truthCloses;
                roles[role] = new { truthOpens, recordedOpens = opens, truthCloses, recordedCloses = closed };
            }

            int elsewhere = creates.Concat(closes).Count(record => record.ProcessId != run.CreatorProcessId && record.ProcessId != run.OpenerProcessId);
            ulong[] addresses = [.. creates.Concat(closes).Select(static record => record.Object).Distinct()];
            whole += objectWhole && elsewhere == 0 && addresses.Length == 1 ? 1 : 0;

            // The object's own creation, the latest at its address before its first handle, and its deletion, the first after
            // its last handle's close: an address is reused once an object is gone.
            LifetimeRecord[] lives = [.. capture.ObjectLifetimes.Where(record => addresses.Contains(record.Object))];
            DateTime firstHandle = creates.Length > 0 ? creates.Min(static record => record.Utc) : DateTime.MaxValue;
            DateTime lastClose = closes.Length > 0 ? closes.Max(static record => record.Utc) : DateTime.MinValue;
            LifetimeRecord? born = lives.Where(record => record.Created && record.Utc <= firstHandle).MaxBy(static record => record.Utc);
            LifetimeRecord? died = lives.Where(record => !record.Created && record.Utc >= lastClose).MinBy(static record => record.Utc);
            objects.Add(new
            {
                kind,
                kernelName = creates.Concat(closes).Select(record => Generalize(record.ClosedAs ?? record.Name, run)).Distinct().ToArray(),
                types = creates.Concat(closes).Select(static record => record.ObjectType).Distinct().ToArray(),
                typeNames = creates.Concat(closes).Select(record => capture.TypeName(record.ObjectType)).Distinct().ToArray(),
                distinctAddresses = addresses.Length,
                recordedElsewhere = elsewhere,
                duplicates = capture.Duplicates.Count(record => addresses.Contains(record.Object)),
                createsNamed = creates.Count(static record => !string.IsNullOrEmpty(record.Name)),
                closesNamed = closes.Count(static record => !string.IsNullOrEmpty(record.Name)),
                byRole = roles,
                lifetime = !capture.Lifetimes ? null : new
                {
                    creationsAtItsAddress = lives.Count(static record => record.Created),
                    createdBeforeItsFirstHandle = born is not null,
                    createdInRole = born is null ? null : run.RoleOf(born.ProcessId),
                    creationBeforeFirstHandleMilliseconds = born is null ? (double?)null : Math.Round((firstHandle - born.Utc).TotalMilliseconds, 3),
                    deletionsAtItsAddress = lives.Count(static record => !record.Created),
                    deletedAfterItsLastClose = died is not null,
                    deletedInRole = died is null ? null : run.RoleOf(died.ProcessId),
                    deletionAfterLastCloseMilliseconds = died is null ? (double?)null : Math.Round((died.Utc - lastClose).TotalMilliseconds, 3),
                    typeAtCreation = born?.ObjectType,
                },
            });
        }

        // Each blocked wait the truth records, the span its log bounds it in - from the waiter's record before it, written
        // before it blocked, to its own, written once it woke - and the signal of the other process's that ended it: the
        // opener's waits on the event and the mutex end by the creator's signals of the same round; the creator's on the
        // semaphore by the opener's of the same round, and on the mutex by the opener's release in the round before.
        var waits = new List<Wait>();
        foreach ((string waiter, TruthRecord[] log, TruthRecord[] other, int waiterProcess, int signaler) in new[]
        {
            ("opener", run.Opener, run.Creator, run.OpenerProcessId, run.CreatorProcessId),
            ("creator", run.Creator, run.Opener, run.CreatorProcessId, run.OpenerProcessId),
        })
        {
            for (int index = 1; index < log.Length; index++)
            {
                if (log[index] is not { Kind: "WaitEnded", Status: "blocked", CallId: { } round } wait)
                {
                    continue;
                }

                string kind = run.KindOf(wait.ResourceName!) ?? "other";
                long signalRound = waiter == "creator" && kind == "mutex" ? round - 1 : round;
                if (other.FirstOrDefault(record => record.Kind == "ObjectSignaled" && record.ResourceName == wait.ResourceName && record.CallId == signalRound) is { } signal)
                {
                    waits.Add(new(waiter, kind, (int)round, signal.RecordedUtc, signaler, waiterProcess, log[index - 1].RecordedUtc, wait.RecordedUtc));
                }
            }
        }

        // A ready raised from a DPC (its flag 0x1) was readied by no thread: the thread running there only lent its processor,
        // so it is set apart and never a signal's.
        ReadyRecord[] between = [.. capture.Readies.Where(record =>
            (record.ReadyingProcessId == run.CreatorProcessId && record.AwakenedProcessId == run.OpenerProcessId)
            || (record.ReadyingProcessId == run.OpenerProcessId && record.AwakenedProcessId == run.CreatorProcessId))];
        ReadyRecord[] across = [.. between.Where(static record => (record.Flags & ReadiedFromDpc) == 0)];
        // Two passes: the first takes each wait's nearest ready inside the wide window and measures the clocks' offset as
        // the median distance, as the kernel's timestamps and the workload's wall clock are read apart; the second pairs
        // each wait with the ready nearest its signal's record that lies inside the wait's own span, on the kernel's clock
        // moved by that offset.
        // The clocks' offset from the handles' own records: each process created, opened and closed each handle in a call
        // whose truth record it wrote once the call returned, so the kernel's record of the call and the truth's record of
        // it are a pair of the two clocks; the offset is their median distance.
        double[] anchors = [.. Anchors(capture, run).Order()];
        TimeSpan offset = anchors.Length == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(anchors[anchors.Length / 2]);
        (List<(string Kind, double Distance)> paired, Dictionary<int, Wait> taken, HashSet<Wait> pairedWaits) = Match(waits, across, offset, ReadyWindow, withinWait: true);
        HashSet<int> used = [.. taken.Keys];

        // Each process's waiting thread - the thread the paired readies readied there - and whether the kernel blocked each
        // truth wait: a wait the truth calls blocked only failed its first look, and the signal may come as it begins to
        // wait, which the kernel satisfies without switching the thread out, so it has no thread to ready. A wait the kernel
        // blocked switched its thread out in a user-mode wait inside the wait's span.
        Dictionary<int, int> waiterThreads = taken.GroupBy(pair => pair.Value.WaiterProcess)
            .ToDictionary(static group => group.Key, group => group.GroupBy(pair => across[pair.Key].AwakenedThreadId)
                .MaxBy(static threads => threads.Count())!.Key);
        bool KernelBlocked(Wait wait) => waiterThreads.TryGetValue(wait.WaiterProcess, out int thread)
            && capture.WorkloadWaits.Any(switched => switched.ThreadId == thread && switched.WaitReason == UserRequest
                && switched.Utc - offset >= wait.BlockedAfterUtc - Slack && switched.Utc - offset <= wait.EndedUtc + Slack);
        Wait[] kernelBlocked = [.. waits.Where(KernelBlocked)];

        // Which wait took a ready, where one did.
        string TakenBy(ReadyRecord ready) => Array.IndexOf(across, ready) is var at && at >= 0 && taken.TryGetValue(at, out Wait other)
            ? $", taken by the {other.Waiter}'s {other.Kind} wait of round {other.Round}"
            : string.Empty;
        var matchedByKind = Kinds.ToDictionary(static kind => kind, kind => paired.Count(pair => pair.Kind == kind));
        double[] distances = [.. paired.Select(static pair => pair.Distance).Order()];
        int matched = distances.Length;
        return new Dictionary<string, object?>
        {
            ["question"] = "Whether the kernel's handle events record each process's handles to three named objects two processes "
                + "share - with the object's name, one address and its type - and whether its ready thread events join a "
                + "signal of one process's to the wait it ended in the other.",
            ["objects"] = objects,
            ["objectsWhole"] = whole,
            ["truthBlockedWaits"] = waits.Count,
            ["truthBlockedWaitsByKind"] = Kinds.ToDictionary(static kind => kind, kind => waits.Count(wait => wait.Kind == kind)),
            ["readiesAcross"] = new
            {
                creatorToOpener = across.Count(record => record.ReadyingProcessId == run.CreatorProcessId),
                openerToCreator = across.Count(record => record.ReadyingProcessId == run.OpenerProcessId),
            },
            ["clockOffsetMilliseconds"] = Math.Round(offset.TotalMilliseconds, 3),
            ["clockAnchors"] = new
            {
                count = anchors.Length,
                minimumMilliseconds = anchors.Length == 0 ? (double?)null : Math.Round(anchors[0], 3),
                maximumMilliseconds = anchors.Length == 0 ? (double?)null : Math.Round(anchors[^1], 3),
            },
            ["slackMilliseconds"] = Slack.TotalMilliseconds,
            ["blockedWaitsWithTheirReady"] = matched,
            ["blockedWaitsWithTheirReadyByKind"] = matchedByKind,
            ["readyFromSignalRecordMilliseconds"] = matched == 0 ? null : new
            {
                minimum = Math.Round(distances[0], 3),
                median = Math.Round(distances[matched / 2], 3),
                maximum = Math.Round(distances[^1], 3),
            },
            ["readiesAcrossUnmatched"] = across.Length - used.Count,
            ["readiesAcrossFromDpc"] = between.Length - across.Length,
            ["readiesAcrossByWaitReason"] = new
            {
                paired = WaitReasons(across.Where((_, index) => used.Contains(index))),
                unpaired = WaitReasons(across.Where((_, index) => !used.Contains(index))),
            },
            ["unpairedReadies"] = Unpaired(across.Where((_, index) => !used.Contains(index)), run, offset),
            ["unpairedReadiesNearestTruth"] = Nearest(across.Where((_, index) => !used.Contains(index)), run, offset,
                [.. across.Where((_, index) => used.Contains(index)).Select(static record => record.AwakenedThreadId).Distinct()]),
            ["truthBlockedWaitsTheKernelBlocked"] = kernelBlocked.Length,
            ["kernelBlockedWaitsWithTheirReady"] = kernelBlocked.Count(pairedWaits.Contains),
            ["readiesPairedToWaitsTheKernelDidNotBlock"] = pairedWaits.Count(wait => !KernelBlocked(wait)),
            ["unpairedBlockedWaits"] = waits.Where(wait => !pairedWaits.Contains(wait)).Select(wait => new
            {
                waiter = wait.Waiter,
                kind = wait.Kind,
                round = wait.Round,
                kernelBlocked = KernelBlocked(wait),
                spanMilliseconds = Math.Round((wait.EndedUtc - wait.BlockedAfterUtc).TotalMilliseconds, 3),

                // Every ready of a thread of the waiter's process inside the wait's span, from any process: whose running
                // thread raised it, from a DPC or not, and how far from the signal's record.
                readiesOfTheWaiterInItsSpan = capture.Readies.Where(ready => ready.AwakenedProcessId == wait.WaiterProcess
                        && ready.Utc - offset >= wait.BlockedAfterUtc - Slack && ready.Utc - offset <= wait.EndedUtc + Slack)
                    .Select(ready => string.Create(CultureInfo.InvariantCulture,
                        $"from {run.RoleOf(ready.ReadyingProcessId)}{((ready.Flags & ReadiedFromDpc) != 0 ? " DPC" : string.Empty)}, {WaitReasons([ready]).Keys.Single()}, {(ready.Utc - offset - wait.SignalUtc).TotalMilliseconds:0.000} ms from the signal{TakenBy(ready)}"))
                    .ToArray(),
            }).ToArray(),
            ["readiesAcrossByReason"] = new
            {
                paired = Reasons(across.Where((_, index) => used.Contains(index))),
                unpaired = Reasons(across.Where((_, index) => !used.Contains(index))),
                fromDpc = Reasons(between.Where(static record => (record.Flags & ReadiedFromDpc) != 0)),
            },
            ["readiesOfTheWorkloadByRoles"] = capture.Readies
                .GroupBy(record => run.RoleOf(record.ReadyingProcessId) + ">" + run.RoleOf(record.AwakenedProcessId))
                .OrderBy(static group => group.Key, StringComparer.Ordinal).ToDictionary(static group => group.Key, static group => group.Count()),
            ["counts"] = new
            {
                handleEvents = capture.HandleEvents,
                handleCreatesNamed = capture.NamedCreates,
                handleClosesNamed = capture.NamedCloses,
                objectManagerByOpcode = capture.ObjectManagerByOpcode.OrderBy(static pair => pair.Key)
                    .ToDictionary(static pair => pair.Key.ToString(CultureInfo.InvariantCulture), static pair => pair.Value),
                contextSwitches = capture.ContextSwitches,
                readyThreads = capture.ReadyEvents,
                readyThreadsWithTheirReadyingThread = capture.ReadiesResolved,
                readyThreadsKept = capture.Readies.Count,
                typeNames = capture.TypeNames.Count,
                objectLifetimesKept = capture.ObjectLifetimes.Count,
            },
        };
    }

    // Each handle's kernel record against the truth record of the call that made or closed it, in milliseconds, kernel
    // minus truth: the creator's creations, the opener's opens, and both processes' closes.
    private static IEnumerable<double> Anchors(Capture capture, Run run)
    {
        foreach (string kind in Kinds)
        {
            string name = run.ObjectName(kind);
            foreach ((string _, int processId, TruthRecord[] records) in run.Roles)
            {
                if (capture.Handles.FirstOrDefault(record => record.Kind == HandleKind.Create && record.ProcessId == processId && Ends(record.ClosedAs ?? record.Name, name)) is { } create
                    && records.FirstOrDefault(record => record.Kind is "ObjectCreated" or "ObjectOpened" && record.ResourceName == name) is { } made)
                {
                    yield return (create.Utc - made.RecordedUtc).TotalMilliseconds;
                }

                if (capture.Handles.FirstOrDefault(record => record.Kind == HandleKind.Close && record.ProcessId == processId && Ends(record.Name, name)) is { } close
                    && records.FirstOrDefault(record => record.Kind == "HandleClosed" && record.ResourceName == name) is { } closed)
                {
                    yield return (close.Utc - closed.RecordedUtc).TotalMilliseconds;
                }
            }
        }
    }

    // The readies no wait paired, by when they lie: during the rounds, from the first round's first truth record to the
    // last round's last, or after both processes' last truth records - their exit - or before the rounds.
    private static object Unpaired(IEnumerable<ReadyRecord> readies, Run run, TimeSpan offset)
    {
        TruthRecord[] rounds = [.. run.Creator.Concat(run.Opener).Where(static record => record.CallId is not null)];
        DateTime from = rounds.Min(static record => record.RecordedUtc);
        DateTime to = rounds.Max(static record => record.RecordedUtc);
        DateTime last = run.Creator.Concat(run.Opener).Max(static record => record.RecordedUtc);
        DateTime[] at = [.. readies.Select(record => record.Utc - offset)];
        return new
        {
            beforeTheRounds = at.Count(time => time < from),
            duringTheRounds = at.Count(time => time >= from && time <= to),
            betweenTheRoundsAndTheLastRecord = at.Count(time => time > to && time <= last),
            afterTheLastRecord = at.Count(time => time > last),
            afterTheLastRecordWithinMilliseconds = at.Where(time => time > last).Select(time => (time - last).TotalMilliseconds)
                .DefaultIfEmpty(0).Max() is var within ? Math.Round(within, 3) : 0,
        };
    }

    // For each ready no wait paired: the truth record nearest it of either process, on the moved clock, by its role, kind,
    // object and status, in bands of distance; and whether it readied a thread a paired ready readied too.
    private static Dictionary<string, int> Nearest(IEnumerable<ReadyRecord> readies, Run run, TimeSpan offset, int[] waiters)
    {
        (string Role, TruthRecord Record)[] truth = [.. run.Creator.Select(static record => ("creator", record)).Concat(run.Opener.Select(static record => ("opener", record)))];
        var bands = new Dictionary<string, int>();
        foreach (ReadyRecord ready in readies)
        {
            DateTime at = ready.Utc - offset;
            (string role, TruthRecord record) = truth.MinBy(pair => (pair.Record.RecordedUtc - at).Duration());
            double milliseconds = (record.RecordedUtc - at).TotalMilliseconds;
            string band = Math.Abs(milliseconds) < 0.1 ? "<0.1ms" : Math.Abs(milliseconds) < 1 ? "<1ms" : Math.Abs(milliseconds) < 10 ? "<10ms" : ">=10ms";
            string key = $"{run.RoleOf(ready.ReadyingProcessId)}>{run.RoleOf(ready.AwakenedProcessId)} "
                + $"{(waiters.Contains(ready.AwakenedThreadId) ? "waiter" : "another thread")} near {role} {record.Kind} "
                + $"{(record.ResourceName is null ? string.Empty : run.KindOf(record.ResourceName))} {record.Status} {(milliseconds < 0 ? "before" : "after")} {band}";
            bands[key] = bands.GetValueOrDefault(key) + 1;
        }

        return bands.OrderByDescending(static pair => pair.Value).ToDictionary(static pair => pair.Key, static pair => pair.Value);
    }

    // The wait reasons the readied threads were switched out in, by the kernel's names (KWAIT_REASON); "not seen waiting"
    // where the session saw no switch out of the thread in a wait.
    private static Dictionary<string, int> WaitReasons(IEnumerable<ReadyRecord> readies) =>
        readies.GroupBy(static record => record.AwakenedWaitReason switch
            {
                -1 => "not seen waiting",
                4 => "DelayExecution",
                6 => "UserRequest",
                11 => "WrDelayExecution",
                13 => "WrUserRequest",
                15 => "WrQueue",
                16 => "WrLpcReceive",
                17 => "WrLpcReply",
                21 => "WrKeyedEvent",
                27 => "WrResource",
                28 => "WrPushLock",
                29 => "WrMutex",
                37 => "WrAlertByThreadId",
                var other => "reason " + other.ToString(CultureInfo.InvariantCulture),
            })
            .OrderBy(static group => group.Key, StringComparer.Ordinal).ToDictionary(static group => group.Key, static group => group.Count());

    // The adjust reasons of ready threads, by name: none, an unwait's, a boost's.
    private static Dictionary<string, int> Reasons(IEnumerable<ReadyRecord> readies) =>
        readies.GroupBy(static record => record.AdjustReason switch { 1 => "unwait", 2 => "boost", 0 => "none", _ => "other" })
            .OrderBy(static group => group.Key, StringComparer.Ordinal).ToDictionary(static group => group.Key, static group => group.Count());

    // Each wait paired with the ready thread nearest its signal's truth record, from the signaler's process to the waiter's,
    // on the kernel's clock moved by the offset, inside the window either side - and, within the wait, inside the span the
    // waiter's log bounds it in, give or take the slack of two clocks read apart; a ready pairs once. The distances are from
    // the signal's record to its ready, on the moved clock.
    private static (List<(string Kind, double Distance)> Paired, Dictionary<int, Wait> Used, HashSet<Wait> PairedWaits) Match(
        List<Wait> waits,
        ReadyRecord[] across,
        TimeSpan offset,
        TimeSpan window,
        bool withinWait)
    {
        var used = new Dictionary<int, Wait>();
        var paired = new List<(string Kind, double Distance)>();
        var pairedWaits = new HashSet<Wait>();
        foreach (Wait wait in waits.OrderBy(static wait => wait.SignalUtc))
        {
            int best = -1;
            TimeSpan bestDistance = TimeSpan.MaxValue;
            for (int index = 0; index < across.Length; index++)
            {
                ReadyRecord ready = across[index];
                DateTime at = ready.Utc - offset;
                TimeSpan distance = (at - wait.SignalUtc).Duration();
                if (used.ContainsKey(index) || ready.ReadyingProcessId != wait.Signaler || ready.AwakenedProcessId != wait.WaiterProcess
                    || distance > window || distance >= bestDistance
                    || (withinWait && (at < wait.BlockedAfterUtc - Slack || at > wait.EndedUtc + Slack)))
                {
                    continue;
                }

                (best, bestDistance) = (index, distance);
            }

            if (best >= 0)
            {
                used[best] = wait;
                paired.Add((wait.Kind, (across[best].Utc - offset - wait.SignalUtc).TotalMilliseconds));
                pairedWaits.Add(wait);
            }
        }

        return (paired, used, pairedWaits);
    }

    // Each kept record of the run's objects, and each ready thread across the workload's processes, by role and object kind,
    // with its time from the first, never a PID, a thread, a handle's value or an address.
    private static string Ledger(Capture capture, Run run)
    {
        // An object by the order its address first appears among the kept records, and a handle by the order its process,
        // value and object first appear: identities the ledger can compare, never the address or the value.
        var objectOrdinals = new Dictionary<ulong, int>();
        var handleOrdinals = new Dictionary<(int, uint, ulong), int>();
        var rows = new List<(DateTime Utc, string Row)>();
        foreach (HandleRecord record in capture.Handles.OrderBy(static record => record.Utc))
        {
            int objectOrdinal = objectOrdinals.TryGetValue(record.Object, out int known) ? known : objectOrdinals[record.Object] = objectOrdinals.Count + 1;
            var key = (record.ProcessId, record.Handle, record.Object);
            int handleOrdinal = handleOrdinals.TryGetValue(key, out int seen) ? seen : handleOrdinals[key] = handleOrdinals.Count + 1;
            string kind = run.KindOf(record.ClosedAs ?? record.Name ?? string.Empty) ?? "other";
            rows.Add((record.Utc, string.Create(CultureInfo.InvariantCulture,
                $"{record.Kind},{run.RoleOf(record.ProcessId)},{kind},{objectOrdinal},{handleOrdinal},{record.ObjectType},{capture.TypeName(record.ObjectType)},{!string.IsNullOrEmpty(record.Name)},,,,,")));
        }

        foreach (LifetimeRecord record in capture.ObjectLifetimes.Where(record => objectOrdinals.ContainsKey(record.Object)))
        {
            rows.Add((record.Utc, string.Create(CultureInfo.InvariantCulture,
                $"{(record.Created ? "Created" : "Deleted")},{run.RoleOf(record.ProcessId)},,{objectOrdinals[record.Object]},,{record.ObjectType},{capture.TypeName(record.ObjectType)},,,,,,")));
        }

        // A thread by the order it first appears, never its id.
        var threadOrdinals = new Dictionary<int, int>();
        int Thread(int id) => threadOrdinals.TryGetValue(id, out int known) ? known : threadOrdinals[id] = threadOrdinals.Count + 1;

        // Each ready thread between the creator and the opener, either way, with the wait the readied thread was in and the
        // thread it readied; and each switch out of a creator's or opener's thread into a user-mode wait, which says whether
        // the kernel blocked a wait at all.
        foreach (ReadyRecord record in capture.Readies.Where(record =>
            (record.ReadyingProcessId == run.CreatorProcessId && record.AwakenedProcessId == run.OpenerProcessId)
            || (record.ReadyingProcessId == run.OpenerProcessId && record.AwakenedProcessId == run.CreatorProcessId)))
        {
            string waitReason = WaitReasons([record]).Keys.Single();
            string adjust = Reasons([record]).Keys.Single();
            rows.Add((record.Utc, string.Create(CultureInfo.InvariantCulture,
                $"Ready,{run.RoleOf(record.ReadyingProcessId)},,,,,,,{run.RoleOf(record.AwakenedProcessId)},{waitReason},{adjust},{(record.Flags & ReadiedFromDpc) != 0},{Thread(record.AwakenedThreadId)}")));
        }

        foreach ((DateTime utc, int threadId, int waitReason) in capture.WorkloadWaits.Where(wait => wait.WaitReason == UserRequest))
        {
            int processId = capture.ProcessOf(threadId);
            if (processId == run.CreatorProcessId || processId == run.OpenerProcessId)
            {
                rows.Add((utc, string.Create(CultureInfo.InvariantCulture, $"Waited,{run.RoleOf(processId)},,,,,,,,UserRequest,,,{Thread(threadId)}")));
            }
        }

        var text = new StringBuilder("utcTicks,record,role,kind,object,handle,type,typeName,nameCarried,awakenedRole,awakenedWait,adjustReason,fromDpc,thread\n");
        foreach ((DateTime utc, string row) in rows.OrderBy(static row => row.Utc))
        {
            text.Append(CultureInfo.InvariantCulture, $"{utc.Ticks},{row}\n");
        }

        return text.ToString();
    }

    private static bool Ends(string? kernelName, string name) =>
        kernelName is not null && kernelName.EndsWith(name[(name.IndexOf('\\', StringComparison.Ordinal) + 1)..], StringComparison.Ordinal);

    // A kernel name with the run's own part and the session's number taken out, which says nothing of the machine.
    private static string Generalize(string? kernelName, Run run) =>
        kernelName is null ? string.Empty
            : System.Text.RegularExpressions.Regex.Replace(kernelName.Replace(run.RunPart, "<run>", StringComparison.Ordinal), @"\\Sessions\\\d+\\", @"\Sessions\<n>\");

    private static string? Option(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    internal enum HandleKind
    {
        Create,
        Close,
        Rundown,
    }

    /// <summary>
    /// One handle record of the run's objects. <see cref="Name"/> is the name the record itself carried; a creation carries
    /// none, so <see cref="ClosedAs"/> is the name its handle's close carried, which joined it by process, handle and object.
    /// </summary>
    internal sealed record HandleRecord(HandleKind Kind, DateTime Utc, int ProcessId, int ThreadId, ulong Object, uint Handle, int ObjectType, string? Name)
    {
        public string? ClosedAs { get; init; }
    }

    internal sealed record DuplicateRecord(DateTime Utc, int SourceProcessId, int TargetProcessId, ulong Object, uint SourceHandle, uint TargetHandle);

    /// <summary>A ready thread: the thread running on its processor when it was raised, which readied the other, and that other.</summary>
    internal sealed record ReadyRecord(DateTime Utc, int ReadyingProcessId, int ReadyingThreadId, int AwakenedProcessId, int AwakenedThreadId, int AdjustReason, int Flags)
    {
        /// <summary>The wait reason the readied thread was switched out waiting in; -1 when it was not seen waiting.</summary>
        public int AwakenedWaitReason { get; init; }
    }

    internal sealed record LifetimeRecord(bool Created, DateTime Utc, int ProcessId, ulong Object, int ObjectType);

    /// <summary>
    /// A blocked wait of the truth's: whose and on what, in which round, the record of the signal that ended it and its
    /// process, and the span the waiter's log bounds it in - its record before the wait and its record of the wait's end.
    /// </summary>
    internal readonly record struct Wait(string Waiter, string Kind, int Round, DateTime SignalUtc, int Signaler, int WaiterProcess, DateTime BlockedAfterUtc, DateTime EndedUtc);

    internal sealed record TruthRecord(string Kind, long? CallId, string? Status, string? ResourceName, DateTime RecordedUtc);

    private static class Truth
    {
        public static TruthRecord[] Read(string path) => [.. File.ReadLines(path).Where(static line => line.Length > 0).Select(static line =>
        {
            using var document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            return new TruthRecord(
                root.GetProperty("kind").GetString()!,
                root.TryGetProperty("callId", out JsonElement call) && call.ValueKind == JsonValueKind.Number ? call.GetInt64() : null,
                root.TryGetProperty("status", out JsonElement status) && status.ValueKind == JsonValueKind.String ? status.GetString() : null,
                root.TryGetProperty("resourceName", out JsonElement name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null,
                root.GetProperty("recordedUtc").GetDateTimeOffset().UtcDateTime);
        })];
    }

    private sealed record Run(string Name, int CoordinatorProcessId, int CreatorProcessId, int OpenerProcessId, TruthRecord[] Creator, TruthRecord[] Opener)
    {
        public string RunPart => Name[(Name.IndexOf('\\', StringComparison.Ordinal) + 1)..];

        public IEnumerable<(string Role, int ProcessId, TruthRecord[] Records)> Roles =>
            [("creator", CreatorProcessId, Creator), ("opener", OpenerProcessId, Opener)];

        public string ObjectName(string kind) => Name + "-" + kind;

        public string? KindOf(string name) => Kinds.FirstOrDefault(kind => name.EndsWith(RunPart + "-" + kind, StringComparison.Ordinal));

        public string RoleOf(int processId) => processId == CreatorProcessId ? "creator" : processId == OpenerProcessId ? "opener"
            : processId == CoordinatorProcessId ? "coordinator" : "other";
    }

    /// <summary>What the probe's session delivered, gathered on the processing thread and read after it ends.</summary>
    private sealed class Capture
    {
        // Every handle a workload process created, by its process, handle and object, until its close says whose it is.
        private readonly Dictionary<(int ProcessId, uint Handle, ulong Object), HandleRecord> created = [];
        private readonly HashSet<ulong> objects = [];
        private readonly HashSet<int> workload = [];
        private readonly Dictionary<int, int> threads = [];
        private readonly Dictionary<int, int> running = [];
        private readonly Dictionary<int, (int State, int WaitReason)> switchedOut = [];

        public List<HandleRecord> Handles { get; } = [];

        public List<DuplicateRecord> Duplicates { get; } = [];

        public List<ReadyRecord> Readies { get; } = [];

        /// <summary>Whether the session collects the object manager's object group, each object's creation and deletion.</summary>
        public bool Lifetimes { get; init; }

        public List<LifetimeRecord> ObjectLifetimes { get; } = [];

        /// <summary>Each switch out, in a wait, of a thread of a workload process: when, which thread, and its wait reason.</summary>
        public List<(DateTime Utc, int ThreadId, int WaitReason)> WorkloadWaits { get; } = [];

        public Dictionary<int, string> TypeNames { get; } = [];

        public Dictionary<int, long> ObjectManagerByOpcode { get; } = [];

        public long HandleEvents { get; private set; }

        public long NamedCreates { get; private set; }

        public long NamedCloses { get; private set; }

        public long ContextSwitches { get; private set; }

        public long ReadyEvents { get; private set; }

        public long ReadiesResolved { get; private set; }

        public DateTime WorkloadStartedUtc { get; set; }

        public DateTime WorkloadEndedUtc { get; set; }

        public string? TypeName(int type) => TypeNames.GetValueOrDefault(type);

        /// <summary>The process a thread belongs to, from the thread events; -1 for a thread the session never saw start.</summary>
        public int ProcessOf(int threadId) => threads.GetValueOrDefault(threadId, -1);

        public void Subscribe(TraceEventDispatcher source)
        {
            source.Kernel.ProcessStart += data =>
            {
                if (string.Equals(data.ImageFileName, "InterCat.TestWorkloads.exe", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(data.ImageFileName, "InterCat.TestWorkloads", StringComparison.OrdinalIgnoreCase))
                {
                    workload.Add(data.ProcessID);
                }
            };

            // Each thread's process, and the thread each processor runs, so a ready thread - whose record names no thread -
            // is set from the thread that was running where it was raised to the thread it readied, each in its process.
            source.Kernel.ThreadStart += data => threads[data.ThreadID] = data.ProcessID;
            source.Kernel.ThreadDCStart += data => threads[data.ThreadID] = data.ProcessID;
            source.Kernel.ThreadCSwitch += data =>
            {
                ContextSwitches++;
                running[data.ProcessorNumber] = data.NewThreadID;

                // The state and wait reason each thread was switched out in, until it is switched in again.
                switchedOut[data.OldThreadID] = ((int)data.OldThreadState, (int)data.OldThreadWaitReason);
                if ((int)data.OldThreadState == 5 && workload.Contains(threads.GetValueOrDefault(data.OldThreadID, -1)))
                {
                    WorkloadWaits.Add((data.TimeStamp.ToUniversalTime(), data.OldThreadID, (int)data.OldThreadWaitReason));
                }
                switchedOut.Remove(data.NewThreadID);
            };
            source.Kernel.DispatcherReadyThread += data =>
            {
                ReadyEvents++;
                int readyingThread = running.GetValueOrDefault(data.ProcessorNumber, -1);
                int readying = threads.GetValueOrDefault(readyingThread, -1);
                int awakened = threads.GetValueOrDefault(data.AwakenedThreadID, -1);
                if (readying >= 0)
                {
                    ReadiesResolved++;
                }

                if (workload.Contains(readying) || workload.Contains(awakened))
                {
                    Readies.Add(new(data.TimeStamp.ToUniversalTime(), readying, readyingThread, awakened, data.AwakenedThreadID,
                        (int)data.AdjustReason, (int)data.Flags)
                    {
                        AwakenedWaitReason = switchedOut.TryGetValue(data.AwakenedThreadID, out var state) && state.State == 5 ? state.WaitReason : -1,
                    });
                }
            };

            source.Kernel.ObjectCreateHandle += data =>
            {
                HandleEvents++;
                string name = NameAt(data, data.PointerSize + 6);
                if (name.Length > 0)
                {
                    NamedCreates++;
                }

                if (!workload.Contains(data.ProcessID))
                {
                    return;
                }

                var record = new HandleRecord(HandleKind.Create, data.TimeStamp.ToUniversalTime(), data.ProcessID, data.ThreadID, data.Object,
                    (uint)data.Handle, data.ObjectType, name.Length > 0 ? name : null);
                created[(data.ProcessID, (uint)data.Handle, data.Object)] = record;
                if (name.Contains(Prefix, StringComparison.Ordinal))
                {
                    Handles.Add(record);
                    objects.Add(data.Object);
                }
            };
            source.Kernel.ObjectCloseHandle += data =>
            {
                HandleEvents++;
                string name = NameAt(data, data.PointerSize + 6);
                if (name.Length > 0)
                {
                    NamedCloses++;
                }

                var key = (data.ProcessID, (uint)data.Handle, data.Object);
                bool ours = name.Contains(Prefix, StringComparison.Ordinal) || objects.Contains(data.Object);
                bool held = created.Remove(key, out HandleRecord? create);
                if (!ours)
                {
                    return;
                }

                // The close names the object; the creation it closes, which named none, is the run's too.
                objects.Add(data.Object);
                if (held && !Handles.Contains(create!))
                {
                    Handles.Add(create! with { ClosedAs = name.Length > 0 ? name : null });
                }

                Handles.Add(new HandleRecord(HandleKind.Close, data.TimeStamp.ToUniversalTime(), data.ProcessID, data.ThreadID, data.Object,
                    (uint)data.Handle, data.ObjectType, name.Length > 0 ? name : null));
            };
            source.Kernel.ObjectDuplicateHandle += data =>
            {
                HandleEvents++;
                if (objects.Contains(data.Object) || workload.Contains(data.SourceProcessID) || workload.Contains(data.TargetProcessID))
                {
                    Duplicates.Add(new(data.TimeStamp.ToUniversalTime(), data.SourceProcessID, data.TargetProcessID, data.Object,
                        (uint)data.SourceHandle, (uint)data.TargetHandle));
                }
            };
            source.Kernel.ObjectHandleDCEnd += data =>
            {
                HandleEvents++;
                string name = NameAt(data, data.PointerSize + 10);
                if (name.Contains(Prefix, StringComparison.Ordinal))
                {
                    Handles.Add(new(HandleKind.Rundown, data.TimeStamp.ToUniversalTime(), data.ProcessID, data.ThreadID, data.Object,
                        (uint)data.Handle, data.ObjectType, name));
                }
            };
            source.Kernel.ObjectTypeDCEnd += data => TypeNames[data.ObjectType] = data.ObjectTypeName;
            source.AllEvents += data =>
            {
                if (data.TaskGuid != ObjectManagerClass)
                {
                    return;
                }

                int opcode = (int)data.Opcode;
                ObjectManagerByOpcode[opcode] = ObjectManagerByOpcode.GetValueOrDefault(opcode) + 1;

                // An object's creation and deletion, which TraceEvent does not parse, read by the layout the machine
                // registers (§4.2): the object's address, then its type. A creation is kept from the workload's processes,
                // before any handle says whose object it is; a deletion only of an object a kept handle named.
                if (Lifetimes && opcode is 48 or 49 && data.EventData() is { } payload && payload.Length >= data.PointerSize + 2)
                {
                    ulong address = data.PointerSize == 8 ? BitConverter.ToUInt64(payload, 0) : BitConverter.ToUInt32(payload, 0);
                    if (opcode == 48 ? workload.Contains(data.ProcessID) : objects.Contains(address))
                    {
                        ObjectLifetimes.Add(new(opcode == 48, data.TimeStamp.ToUniversalTime(), data.ProcessID, address,
                            BitConverter.ToUInt16(payload, data.PointerSize)));
                    }
                }
            };
        }

        // The name a handle record carries, read by the layout the machine registers (§4.2) after its fixed fields, which
        // TraceEvent does not read; empty when the record names none.
        private static string NameAt(TraceEvent data, int offset)
        {
            byte[] payload = data.EventData();
            if (payload.Length <= offset + sizeof(char))
            {
                return string.Empty;
            }

            string text = Encoding.Unicode.GetString(payload, offset, payload.Length - offset);
            int end = text.IndexOf('\0', StringComparison.Ordinal);
            return end >= 0 ? text[..end] : text;
        }
    }
}
