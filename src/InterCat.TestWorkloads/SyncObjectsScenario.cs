using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using InterCat.Domain;

namespace InterCat.TestWorkloads;

/// <summary>Parameters of one FX-SYNC-001 run: how many rounds the two processes take turns, and how long a turn holds the mutex.</summary>
internal sealed record SyncObjectsOptions
{
    public const string ScenarioId = "FX-SYNC-001";

    public required string TruthDirectory { get; init; }

    /// <summary>How many rounds the two processes take turns through the objects.</summary>
    public int Rounds { get; init; } = 64;

    /// <summary>How long the creator holds the mutex after it sets the event, so the opener's wait on the mutex blocks.</summary>
    public int HoldMilliseconds { get; init; } = 5;

    /// <summary>The objects' shared name, which the coordinator chooses and gives both processes; null for the coordinator.</summary>
    public string? Name { get; init; }
}

/// <summary>
/// FX-SYNC-001: two processes take turns through three named kernel objects of the session's namespace - an event, a
/// semaphore and a mutex - that one creates and the other opens by name. Each round the creator, holding the mutex, sets
/// the event and releases the mutex a hold later; the opener's wait on the event ends, its wait on the mutex ends once the
/// creator releases it, and it releases the mutex and then the semaphore, on which the creator waits. Each process logs
/// every object it created or opened, every signal it gave and every wait that ended - whether it blocked first - and
/// every handle it closed, by the object's name. Neither reads InterCat state.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SyncObjectsScenario
{
    private const string EventKind = "event";
    private const string SemaphoreKind = "semaphore";
    private const string MutexKind = "mutex";

    private static readonly JsonSerializerOptions SummaryOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

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
        string name = $@"Local\InterCat-{SyncObjectsOptions.ScenarioId}-{Guid.NewGuid():N}";
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

        foreach (Process process in new[] { creator, opener })
        {
            await process.StandardInput.WriteLineAsync("go").ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        await Task.WhenAll(creator.WaitForExitAsync(cancellationToken), opener.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
        var summary = new
        {
            scenarioId = SyncObjectsOptions.ScenarioId,
            options.Rounds,
            options.HoldMilliseconds,
            name,
            objects = new[] { (name + "-" + EventKind), (name + "-" + SemaphoreKind), (name + "-" + MutexKind) },
            creatorProcessId = creator.Id,
            openerProcessId = opener.Id,
            creatorExitCode = creator.ExitCode,
            openerExitCode = opener.ExitCode,
        };
        await File.WriteAllTextAsync(Path.Combine(options.TruthDirectory, "scenario.json"),
            JsonSerializer.Serialize(summary, SummaryOptions), cancellationToken).ConfigureAwait(false);
        return creator.ExitCode != 0 || opener.ExitCode != 0 ? 1 : 0;
    }

    /// <summary>The creator: makes the three objects under the run's name, then, holding the mutex, opens each round.</summary>
    public static async Task<int> RunCreatorAsync(SyncObjectsOptions options, CancellationToken cancellationToken)
    {
        string name = options.Name ?? throw new InvalidOperationException("The creator takes the objects' name from --name.");
        await using var truth = new TruthLog(Path.Combine(options.TruthDirectory, "truth-creator.jsonl"), SyncObjectsOptions.ScenarioId, "creator");
        truth.Write(TruthEventKind.ProcessStarted);

        using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-" + EventKind, out bool newEvent);
        using var answer = new Semaphore(0, 1, name + "-" + SemaphoreKind, out bool newSemaphore);
        using var mutex = new Mutex(false, name + "-" + MutexKind, out bool newMutex);
        if (!newEvent || !newSemaphore || !newMutex)
        {
            // An object of the name existed already: another run's or another program's, which this run must not share.
            truth.Write(TruthEventKind.Failure, status: "An object of the run's name existed already.");
            return 1;
        }

        truth.Write(TruthEventKind.ObjectCreated, status: EventKind, resourceName: name + "-" + EventKind);
        truth.Write(TruthEventKind.ObjectCreated, status: SemaphoreKind, resourceName: name + "-" + SemaphoreKind);
        truth.Write(TruthEventKind.ObjectCreated, status: MutexKind, resourceName: name + "-" + MutexKind);
        Console.WriteLine("created");
        Console.Out.Flush();
        if (await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not "go")
        {
            return 1;
        }

        // Every round on this one thread, which owns the mutex while it holds it.
        for (int round = 1; round <= options.Rounds; round++)
        {
            Wait(truth, mutex, name + "-" + MutexKind, round);
            signal.Set();
            truth.Write(TruthEventKind.ObjectSignaled, callId: round, status: EventKind, resourceName: name + "-" + EventKind);
            Thread.Sleep(options.HoldMilliseconds);
            mutex.ReleaseMutex();
            truth.Write(TruthEventKind.ObjectSignaled, callId: round, status: MutexKind, resourceName: name + "-" + MutexKind);
            Wait(truth, answer, name + "-" + SemaphoreKind, round);
        }

        Close(truth, signal, name + "-" + EventKind, EventKind);
        Close(truth, answer, name + "-" + SemaphoreKind, SemaphoreKind);
        Close(truth, mutex, name + "-" + MutexKind, MutexKind);
        truth.Write(TruthEventKind.ProcessExiting);
        return 0;
    }

    /// <summary>The opener: opens the creator's three objects by name, then answers each round.</summary>
    public static async Task<int> RunOpenerAsync(SyncObjectsOptions options, CancellationToken cancellationToken)
    {
        string name = options.Name ?? throw new InvalidOperationException("The opener takes the objects' name from --name.");
        await using var truth = new TruthLog(Path.Combine(options.TruthDirectory, "truth-opener.jsonl"), SyncObjectsOptions.ScenarioId, "opener");
        truth.Write(TruthEventKind.ProcessStarted);

        using var signal = EventWaitHandle.OpenExisting(name + "-" + EventKind);
        truth.Write(TruthEventKind.ObjectOpened, status: EventKind, resourceName: name + "-" + EventKind);
        using var answer = Semaphore.OpenExisting(name + "-" + SemaphoreKind);
        truth.Write(TruthEventKind.ObjectOpened, status: SemaphoreKind, resourceName: name + "-" + SemaphoreKind);
        using var mutex = Mutex.OpenExisting(name + "-" + MutexKind);
        truth.Write(TruthEventKind.ObjectOpened, status: MutexKind, resourceName: name + "-" + MutexKind);
        Console.WriteLine("opened");
        Console.Out.Flush();
        if (await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not "go")
        {
            return 1;
        }

        for (int round = 1; round <= options.Rounds; round++)
        {
            Wait(truth, signal, name + "-" + EventKind, round);
            Wait(truth, mutex, name + "-" + MutexKind, round);
            mutex.ReleaseMutex();
            truth.Write(TruthEventKind.ObjectSignaled, callId: round, status: MutexKind, resourceName: name + "-" + MutexKind);
            answer.Release();
            truth.Write(TruthEventKind.ObjectSignaled, callId: round, status: SemaphoreKind, resourceName: name + "-" + SemaphoreKind);
        }

        Close(truth, signal, name + "-" + EventKind, EventKind);
        Close(truth, answer, name + "-" + SemaphoreKind, SemaphoreKind);
        Close(truth, mutex, name + "-" + MutexKind, MutexKind);
        truth.Write(TruthEventKind.ProcessExiting);
        return 0;
    }

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
            "--rounds", options.Rounds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--hold-ms", options.HoldMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        })
        {
            start.ArgumentList.Add(argument);
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
