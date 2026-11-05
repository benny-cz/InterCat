using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using InterCat.Domain;
using Microsoft.Win32.SafeHandles;

namespace InterCat.TestWorkloads;

/// <summary>Parameters of one FX-SYNC-001 run: how many rounds the two processes take turns, and how long a turn holds the mutex.</summary>
internal sealed record SyncObjectsOptions
{
    public const string ScenarioId = "FX-SYNC-001";

    /// <summary>The scenario of a run whose opener is also given a handle of each object by a duplication.</summary>
    public const string DuplicationScenarioId = "FX-SYNC-002";

    /// <summary>The scenario of a run whose objects are made and ended one generation after another.</summary>
    public const string GenerationsScenarioId = "FX-SYNC-003";

    public required string TruthDirectory { get; init; }

    /// <summary>How many rounds the two processes take turns through the objects.</summary>
    public int Rounds { get; init; } = 64;

    /// <summary>How long the creator holds the mutex after it sets the event, so the opener's wait on the mutex blocks.</summary>
    public int HoldMilliseconds { get; init; } = 5;

    /// <summary>The objects' shared name, which the coordinator chooses and gives both processes; null for the coordinator.</summary>
    public string? Name { get; init; }

    /// <summary>
    /// When above zero, the opener opens and closes each object by its name that many times before the rounds, logging each:
    /// a workload heavy in handles, for what collecting the kernel's handle records costs.
    /// </summary>
    public int Churn { get; init; }

    /// <summary>
    /// When set, the run is FX-SYNC-002: besides the handles it opens by name, the opener is given one of each object by a
    /// duplication - the event's pushed into it by the creator, the semaphore's pulled from the creator by itself, and the
    /// mutex's duplicated from the creator into it by the coordinator, a process at neither end - each logged by the process
    /// that asked for it with both ends; the opener closes each at its end, after its own.
    /// </summary>
    public bool Duplicate { get; init; }

    /// <summary>
    /// When above one, the run is FX-SYNC-003: the creator makes the three objects that many times one after another, each
    /// generation under names of its own, the opener opening each generation's and the two taking their rounds through them,
    /// and both closing them before the next is made - so the kernel makes a later generation's objects where an earlier
    /// generation's were.
    /// </summary>
    public int Generations { get; init; } = 1;

    /// <summary>
    /// The run's scenario: FX-SYNC-002 when the opener is also given handles by duplication, FX-SYNC-003 when its objects are
    /// made in generations, FX-SYNC-001 otherwise.
    /// </summary>
    public string Scenario => Duplicate ? DuplicationScenarioId : Generations > 1 ? GenerationsScenarioId : ScenarioId;

    /// <summary>An object's name: the run's name and the object's kind, and in FX-SYNC-003 its generation between them.</summary>
    public string ObjectName(string name, int generation, string kind) => Generations > 1
        ? string.Create(CultureInfo.InvariantCulture, $"{name}-{generation:D3}-{kind}")
        : name + "-" + kind;
}

/// <summary>
/// FX-SYNC-001: two processes take turns through three named kernel objects of the session's namespace - an event, a
/// semaphore and a mutex - that one creates and the other opens by name. Each round the creator, holding the mutex, sets
/// the event and releases the mutex a hold later; the opener's wait on the event ends, its wait on the mutex ends once the
/// creator releases it, and it releases the mutex and then the semaphore, on which the creator waits. Each process logs
/// every object it created or opened, every signal it gave and every wait that ended - whether it blocked first - and
/// every handle it closed, by the object's name. Neither reads InterCat state. FX-SYNC-002 is the same run with each object's
/// handle also duplicated into the opener three ways (<see cref="SyncObjectsOptions.Duplicate"/>), and FX-SYNC-003 the same run
/// made one generation of objects after another, each ended before the next (<see cref="SyncObjectsOptions.Generations"/>).
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class SyncObjectsScenario
{
    // OpenProcess's right to duplicate a process's handles, and a duplication's option to keep the source's access.
    private const uint ProcessDuplicateHandles = 0x0040;
    private const uint SameAccess = 0x0002;

    private const string EventKind = "event";
    private const string SemaphoreKind = "semaphore";
    private const string MutexKind = "mutex";

    // A generation's three objects by kind, in the order each process makes and closes them.
    private static readonly string[] Kinds = [EventKind, SemaphoreKind, MutexKind];

    private static readonly JsonSerializerOptions SummaryOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    // How the opener opens an object of each kind by its name.
    private static readonly (string Kind, Func<string, WaitHandle> Open)[] Openers =
    [
        (EventKind, static name => EventWaitHandle.OpenExisting(name)),
        (SemaphoreKind, static name => Semaphore.OpenExisting(name)),
        (MutexKind, static name => Mutex.OpenExisting(name)),
    ];

    /// <summary>
    /// Starts the creator, then the opener once the creator's objects exist, and lets both begin once the opener holds a
    /// handle to each, so every round is the two of them.
    /// </summary>
    public static async Task<int> RunCoordinatorAsync(SyncObjectsOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.TruthDirectory);
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The workload executable path could not be resolved.");

        // A name of its own each run, in the session's namespace, so no other run's objects or any other program's share it.
        string name = $@"Local\InterCat-{options.Scenario}-{Guid.NewGuid():N}";
        using Process creator = StartChild(executable, "sync-objects-creator", options with { Name = name });
        if (await creator.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not "created")
        {
            await creator.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Console.Error.WriteLineAsync("The creator exited before it created its objects.").ConfigureAwait(false);
            return 1;
        }

        using Process opener = StartChild(executable, "sync-objects-opener", options with { Name = name });
        if (await opener.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not "opened")
        {
            await StopAsync(creator, cancellationToken).ConfigureAwait(false);
            await opener.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Console.Error.WriteLineAsync("The opener exited before it opened the objects.").ConfigureAwait(false);
            return 1;
        }

        if (options.Duplicate && !await DuplicateAsync(options, name, creator, opener, cancellationToken).ConfigureAwait(false))
        {
            await StopAsync(creator, cancellationToken).ConfigureAwait(false);
            await StopAsync(opener, cancellationToken).ConfigureAwait(false);
            await Console.Error.WriteLineAsync("The objects' handles were not duplicated into the opener.").ConfigureAwait(false);
            return 1;
        }

        for (int generation = 1; generation <= options.Generations; generation++)
        {
            // From the second generation on, the creator makes its objects once both processes closed the last one's, and the
            // opener then opens them.
            if (generation > 1
                && (await AskAsync(creator, "next", cancellationToken).ConfigureAwait(false) is not ["created"]
                    || await AskAsync(opener, "open", cancellationToken).ConfigureAwait(false) is not ["opened"]))
            {
                await StopAsync(creator, cancellationToken).ConfigureAwait(false);
                await StopAsync(opener, cancellationToken).ConfigureAwait(false);
                await Console.Error.WriteLineAsync($"Generation {generation}'s objects were not made and opened.").ConfigureAwait(false);
                return 1;
            }

            foreach (Process process in new[] { creator, opener })
            {
                await process.StandardInput.WriteLineAsync("go").ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // Every generation but the last ends with both processes closing its objects, so none is left when the next is made.
            if (generation < options.Generations
                && (await creator.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not "closed"
                    || await opener.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not "closed"))
            {
                await StopAsync(creator, cancellationToken).ConfigureAwait(false);
                await StopAsync(opener, cancellationToken).ConfigureAwait(false);
                await Console.Error.WriteLineAsync($"Generation {generation}'s objects were not closed by both processes.").ConfigureAwait(false);
                return 1;
            }
        }

        await Task.WhenAll(creator.WaitForExitAsync(cancellationToken), opener.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);

        // Each generation's three objects, the generations in the order they were made.
        string[] objects = [.. Enumerable.Range(1, options.Generations).SelectMany(generation => Kinds.Select(kind => options.ObjectName(name, generation, kind)))];
        var summary = new
        {
            scenarioId = options.Scenario,
            options.Rounds,
            options.HoldMilliseconds,
            options.Churn,
            options.Duplicate,
            options.Generations,
            name,
            objects,
            coordinatorProcessId = Environment.ProcessId,
            creatorProcessId = creator.Id,
            openerProcessId = opener.Id,
            creatorExitCode = creator.ExitCode,
            openerExitCode = opener.ExitCode,
        };
        await File.WriteAllTextAsync(Path.Combine(options.TruthDirectory, "scenario.json"),
            JsonSerializer.Serialize(summary, SummaryOptions), cancellationToken).ConfigureAwait(false);
        return creator.ExitCode != 0 || opener.ExitCode != 0 ? 1 : 0;
    }

    /// <summary>
    /// The creator: makes the three objects under the run's name, then, holding the mutex, opens each round; in FX-SYNC-003 a
    /// generation at a time, each made once it is told the last one's objects are gone.
    /// </summary>
    public static async Task<int> RunCreatorAsync(SyncObjectsOptions options, CancellationToken cancellationToken)
    {
        string name = options.Name ?? throw new InvalidOperationException("The creator takes the objects' name from --name.");
        await using var truth = new TruthLog(Path.Combine(options.TruthDirectory, "truth-creator.jsonl"), options.Scenario, "creator");
        truth.Write(TruthEventKind.ProcessStarted);
        for (int generation = 1; generation <= options.Generations; generation++)
        {
            if (generation > 1 && await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false) != "next")
            {
                return 1;
            }

            if (await RunCreatorGenerationAsync(options, truth, name, generation, cancellationToken).ConfigureAwait(false) is not 0 and var failed)
            {
                return failed;
            }

            // Its objects closed in this process: once the opener's are too, none of them is left.
            if (generation < options.Generations)
            {
                Console.WriteLine("closed");
                Console.Out.Flush();
            }
        }

        truth.Write(TruthEventKind.ProcessExiting);
        return 0;
    }

    // One generation of the creator's: its three objects made, the rounds, and its handles to them closed.
    private static async Task<int> RunCreatorGenerationAsync(SyncObjectsOptions options, TruthLog truth, string name, int generation,
        CancellationToken cancellationToken)
    {
        string eventName = options.ObjectName(name, generation, EventKind);
        string semaphoreName = options.ObjectName(name, generation, SemaphoreKind);
        string mutexName = options.ObjectName(name, generation, MutexKind);
        using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, eventName, out bool newEvent);
        using var answer = new Semaphore(0, 1, semaphoreName, out bool newSemaphore);
        using var mutex = new Mutex(false, mutexName, out bool newMutex);
        if (!newEvent || !newSemaphore || !newMutex)
        {
            // An object of the name existed already: another run's or another program's, which this run must not share.
            truth.Write(TruthEventKind.Failure, status: "An object of the run's name existed already.");
            return 1;
        }

        truth.Write(TruthEventKind.ObjectCreated, status: EventKind, resourceName: eventName);
        truth.Write(TruthEventKind.ObjectCreated, status: SemaphoreKind, resourceName: semaphoreName);
        truth.Write(TruthEventKind.ObjectCreated, status: MutexKind, resourceName: mutexName);
        Console.WriteLine("created");
        if (options.Duplicate)
        {
            // Its handles' values, from which the duplications start: the event's, the semaphore's and the mutex's.
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"handles {Value(signal)} {Value(answer)} {Value(mutex)}"));
        }

        Console.Out.Flush();

        // Until told to begin: in FX-SYNC-002 first asked to push its event's handle into the opener.
        while (true)
        {
            string? command = await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (command == "go")
            {
                break;
            }

            if (!options.Duplicate || command?.Split(' ') is not ["push", string target])
            {
                return 1;
            }

            int opener = int.Parse(target, CultureInfo.InvariantCulture);
            long made = Duplicate(truth.ProcessId, Value(signal), opener);
            truth.Write(TruthEventKind.HandleDuplicated, status: EventKind, resourceName: eventName,
                duplication: new(truth.ProcessId, Value(signal), opener, made));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"pushed {made}"));
            Console.Out.Flush();
        }

        // Every round on this one thread, which owns the mutex while it holds it.
        for (int round = 1; round <= options.Rounds; round++)
        {
            Wait(truth, mutex, mutexName, round);
            signal.Set();
            truth.Write(TruthEventKind.ObjectSignaled, callId: round, status: EventKind, resourceName: eventName);
            Thread.Sleep(options.HoldMilliseconds);
            mutex.ReleaseMutex();
            truth.Write(TruthEventKind.ObjectSignaled, callId: round, status: MutexKind, resourceName: mutexName);
            Wait(truth, answer, semaphoreName, round);
        }

        Close(truth, signal, eventName, EventKind);
        Close(truth, answer, semaphoreName, SemaphoreKind);
        Close(truth, mutex, mutexName, MutexKind);
        return 0;
    }

    /// <summary>
    /// The opener: opens the creator's three objects by name, then answers each round; in FX-SYNC-003 a generation at a time,
    /// each opened once it is told the creator made it.
    /// </summary>
    public static async Task<int> RunOpenerAsync(SyncObjectsOptions options, CancellationToken cancellationToken)
    {
        string name = options.Name ?? throw new InvalidOperationException("The opener takes the objects' name from --name.");
        await using var truth = new TruthLog(Path.Combine(options.TruthDirectory, "truth-opener.jsonl"), options.Scenario, "opener");
        truth.Write(TruthEventKind.ProcessStarted);
        for (int generation = 1; generation <= options.Generations; generation++)
        {
            if (generation > 1 && await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false) != "open")
            {
                return 1;
            }

            if (await RunOpenerGenerationAsync(options, truth, name, generation, cancellationToken).ConfigureAwait(false) is not 0 and var failed)
            {
                return failed;
            }

            // Its handles closed in this process: once the creator's are too, none of the generation's objects is left.
            if (generation < options.Generations)
            {
                Console.WriteLine("closed");
                Console.Out.Flush();
            }
        }

        truth.Write(TruthEventKind.ProcessExiting);
        return 0;
    }

    // One generation of the opener's: its three objects opened by name, the rounds, and its handles to them closed.
    private static async Task<int> RunOpenerGenerationAsync(SyncObjectsOptions options, TruthLog truth, string name, int generation,
        CancellationToken cancellationToken)
    {
        using var signal = EventWaitHandle.OpenExisting(options.ObjectName(name, generation, EventKind));
        truth.Write(TruthEventKind.ObjectOpened, status: EventKind, resourceName: options.ObjectName(name, generation, EventKind));
        using var answer = Semaphore.OpenExisting(options.ObjectName(name, generation, SemaphoreKind));
        truth.Write(TruthEventKind.ObjectOpened, status: SemaphoreKind, resourceName: options.ObjectName(name, generation, SemaphoreKind));
        using var mutex = Mutex.OpenExisting(options.ObjectName(name, generation, MutexKind));
        truth.Write(TruthEventKind.ObjectOpened, status: MutexKind, resourceName: options.ObjectName(name, generation, MutexKind));
        Console.WriteLine("opened");
        Console.Out.Flush();

        // Until told to begin: in FX-SYNC-002 first asked to pull the semaphore's handle from the creator, then given the
        // handles the creator and the coordinator made in it, each with the duplication that made it.
        var duplicated = new List<(string Kind, SafeWaitHandle Handle, HandleDuplication Made)>();
        try
        {
            while (true)
            {
                string? command = await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (command == "go")
                {
                    break;
                }

                switch (options.Duplicate ? command?.Split(' ') : null)
                {
                    case ["pull", string creatorId, string sourceHandle]:
                    {
                        int creator = int.Parse(creatorId, CultureInfo.InvariantCulture);
                        long source = long.Parse(sourceHandle, CultureInfo.InvariantCulture);
                        var made = new HandleDuplication(creator, source, truth.ProcessId, Duplicate(creator, source, truth.ProcessId));
                        duplicated.Add((SemaphoreKind, new SafeWaitHandle((nint)made.TargetHandle, ownsHandle: true), made));
                        truth.Write(TruthEventKind.HandleDuplicated, status: SemaphoreKind, resourceName: options.ObjectName(name, generation, SemaphoreKind),
                            duplication: made);
                        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"pulled {made.TargetHandle}"));
                        Console.Out.Flush();
                        break;
                    }

                    case ["hold", string creatorId, string eventSource, string pushed, string mutexSource, string brokered]:
                    {
                        int creator = int.Parse(creatorId, CultureInfo.InvariantCulture);
                        foreach ((string kind, string source, string made) in new[] { (EventKind, eventSource, pushed), (MutexKind, mutexSource, brokered) })
                        {
                            long handle = long.Parse(made, CultureInfo.InvariantCulture);
                            duplicated.Add((kind, new SafeWaitHandle((nint)handle, ownsHandle: true),
                                new HandleDuplication(creator, long.Parse(source, CultureInfo.InvariantCulture), truth.ProcessId, handle)));
                        }

                        Console.WriteLine("held");
                        Console.Out.Flush();
                        break;
                    }

                    default:
                        return 1;
                }
            }

            return RunOpenerRounds(options, name, generation, truth, signal, answer, mutex, duplicated);
        }
        finally
        {
            foreach ((_, SafeWaitHandle handle, _) in duplicated)
            {
                handle.Dispose();
            }
        }
    }

    // The opener's rounds of one generation and its closes: its own handles, then those duplications made in it, each logged
    // with the duplication that made it.
    private static int RunOpenerRounds(SyncObjectsOptions options, string name, int generation, TruthLog truth, EventWaitHandle signal,
        Semaphore answer, Mutex mutex, List<(string Kind, SafeWaitHandle Handle, HandleDuplication Made)> duplicated)
    {
        string Named(string kind) => options.ObjectName(name, generation, kind);

        // A handle of each object opened by its name and closed again, as many times as asked, each logged by its turn.
        for (int turn = 1; turn <= options.Churn; turn++)
        {
            foreach ((string kind, Func<string, WaitHandle> open) in Openers)
            {
                using (open(Named(kind)))
                {
                    truth.Write(TruthEventKind.ObjectOpened, callId: turn, status: kind, resourceName: Named(kind));
                }

                truth.Write(TruthEventKind.HandleClosed, callId: turn, status: kind, resourceName: Named(kind));
            }
        }

        for (int round = 1; round <= options.Rounds; round++)
        {
            Wait(truth, signal, Named(EventKind), round);
            Wait(truth, mutex, Named(MutexKind), round);
            mutex.ReleaseMutex();
            truth.Write(TruthEventKind.ObjectSignaled, callId: round, status: MutexKind, resourceName: Named(MutexKind));
            answer.Release();
            truth.Write(TruthEventKind.ObjectSignaled, callId: round, status: SemaphoreKind, resourceName: Named(SemaphoreKind));
        }

        Close(truth, signal, Named(EventKind), EventKind);
        Close(truth, answer, Named(SemaphoreKind), SemaphoreKind);
        Close(truth, mutex, Named(MutexKind), MutexKind);
        foreach ((string kind, SafeWaitHandle handle, HandleDuplication made) in duplicated)
        {
            handle.Dispose();
            truth.Write(TruthEventKind.HandleClosed, status: kind, resourceName: Named(kind), duplication: made);
        }

        return 0;
    }

    /// <summary>
    /// FX-SYNC-002's three duplications, between the opener's opens and the rounds: the creator pushes its event's handle into
    /// the opener, the opener pulls the semaphore's from the creator, and this process, at neither end, duplicates the
    /// mutex's from the creator into the opener; the opener is then given the handles the other two made in it.
    /// </summary>
    private static async Task<bool> DuplicateAsync(SyncObjectsOptions options, string name, Process creator, Process opener,
        CancellationToken cancellationToken)
    {
        await using var truth = new TruthLog(Path.Combine(options.TruthDirectory, "truth-coordinator.jsonl"), options.Scenario, "coordinator");
        truth.Write(TruthEventKind.ProcessStarted);

        // The creator's handles by their values in it: its event's, its semaphore's and its mutex's.
        if (await creator.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not { } line
            || line.Split(' ') is not ["handles", string eventHandle, string semaphoreHandle, string mutexHandle])
        {
            return false;
        }

        if (await AskAsync(creator, string.Create(CultureInfo.InvariantCulture, $"push {opener.Id}"), cancellationToken).ConfigureAwait(false)
                is not ["pushed", string pushed]
            || await AskAsync(opener, string.Create(CultureInfo.InvariantCulture, $"pull {creator.Id} {semaphoreHandle}"), cancellationToken).ConfigureAwait(false)
                is not ["pulled", _])
        {
            return false;
        }

        long mutexSource = long.Parse(mutexHandle, CultureInfo.InvariantCulture);
        long brokered = Duplicate(creator.Id, mutexSource, opener.Id);
        truth.Write(TruthEventKind.HandleDuplicated, status: MutexKind, resourceName: options.ObjectName(name, 1, MutexKind),
            duplication: new(creator.Id, mutexSource, opener.Id, brokered));
        return await AskAsync(opener, string.Create(CultureInfo.InvariantCulture,
            $"hold {creator.Id} {eventHandle} {pushed} {mutexHandle} {brokered}"), cancellationToken).ConfigureAwait(false) is ["held"];
    }

    // One command to a child and its answer's words; none when it answered nothing.
    private static async Task<string[]> AskAsync(Process child, string command, CancellationToken cancellationToken)
    {
        await child.StandardInput.WriteLineAsync(command).ConfigureAwait(false);
        await child.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        return (await child.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Split(' ') ?? [];
    }

    // A handle duplicated from one process into another with the source's access, by this process: one of the two ends, or
    // neither. The pseudo-handle of this process needs no opening.
    private static long Duplicate(int sourceProcessId, long sourceHandle, int targetProcessId)
    {
        nint source = sourceProcessId == Environment.ProcessId ? CurrentProcess() : OpenForDuplication(sourceProcessId);
        nint target = targetProcessId == Environment.ProcessId ? CurrentProcess() : OpenForDuplication(targetProcessId);
        try
        {
            return DuplicateHandle(source, (nint)sourceHandle, target, out nint made, 0, false, SameAccess)
                ? made
                : throw new Win32Exception(Marshal.GetLastPInvokeError(), "The handle could not be duplicated.");
        }
        finally
        {
            if (source != CurrentProcess())
            {
                _ = CloseHandle(source);
            }

            if (target != CurrentProcess())
            {
                _ = CloseHandle(target);
            }
        }
    }

    private static nint OpenForDuplication(int processId) =>
        OpenProcess(ProcessDuplicateHandles, false, processId) is var process and not 0
            ? process
            : throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Process {processId} could not be opened to duplicate its handles.");

    // A handle's value in this process, as a duplication names it.
    private static long Value(WaitHandle handle) => handle.SafeWaitHandle.DangerousGetHandle();

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
    private static partial nint CurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateHandle(nint sourceProcess, nint sourceHandle, nint targetProcess, out nint targetHandle,
        uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint options);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    // A wait that first asks without waiting, so the log says whether it blocked: only a wait that blocked has a thread for
    // another's signal to ready.
    private static void Wait(TruthLog truth, WaitHandle handle, string objectName, int round)
    {
        bool blocked = !handle.WaitOne(0);
        if (blocked)
        {
            handle.WaitOne();
        }

        truth.Write(TruthEventKind.WaitEnded, callId: round, status: blocked ? "blocked" : "immediate", resourceName: objectName);
    }

    private static void Close(TruthLog truth, WaitHandle handle, string objectName, string kind)
    {
        handle.Dispose();
        truth.Write(TruthEventKind.HandleClosed, status: kind, resourceName: objectName);
    }

    private static Process StartChild(string executable, string verb, SyncObjectsOptions options)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        foreach (string argument in new[]
        {
            verb, "--truth", options.TruthDirectory, "--name", options.Name!,
            "--rounds", options.Rounds.ToString(CultureInfo.InvariantCulture),
            "--hold-ms", options.HoldMilliseconds.ToString(CultureInfo.InvariantCulture),
            "--churn", options.Churn.ToString(CultureInfo.InvariantCulture),
            "--generations", options.Generations.ToString(CultureInfo.InvariantCulture),
        })
        {
            start.ArgumentList.Add(argument);
        }

        if (options.Duplicate)
        {
            start.ArgumentList.Add("--duplicate");
        }

        return Process.Start(start) ?? throw new InvalidOperationException($"The {verb} process could not be started.");
    }

    private static async Task StopAsync(Process process, CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync("stop").ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }
}
