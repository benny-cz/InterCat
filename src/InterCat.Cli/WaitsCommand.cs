using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>
/// The time processes spent inside the calls they made (`contracts/waits-v1.md`, plan M12's bottleneck views): each
/// caller's completed calls by interface and by the process that served them, the most time first, with the server's own
/// time where its call completed too; the servers callers waited on; and what no time is said for. With --process, one
/// process's calls and the calls it served.
/// </summary>
internal static class WaitsCommand
{
    /// <summary>The document --json writes.</summary>
    internal const string Contract = "call-waits-v1";

    private const int DefaultLimit = 20;

    /// <summary>The columns of the session's waits, each caller named.</summary>
    private static readonly string[] SessionColumns =
        ["Caller", "Calls to", "Served by", "Calls", "Failed", "In calls", "Median", "p95", "Longest", "Server ran"];

    /// <summary>The columns of one process's own calls, which name no caller.</summary>
    private static readonly string[] ProcessColumns =
        ["Calls to", "Served by", "Calls", "Failed", "In calls", "Median", "p95", "Longest", "Server ran"];

    public static Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken) =>
        Task.FromResult(Run(command, cancellationToken));

    private static InterCatExitCode Run(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? processText = command.TakeOption("--process");
        string? limitText = command.TakeOption("--limit");
        string? policyText = command.TakeOption("--evidence-policy");
        string? intervalText = command.TakeOption("--interval");
        bool json = command.TryTakeFlag("--json");
        string? directory = command.TakePositional();
        bool hasUnknown = command.TryReportUnknown(out string? unknown);
        if (directory is null || hasUnknown)
        {
            ConsoleUi.Failure(directory is null ? "A session directory is required: icat waits <directory>." : command.Refusal(unknown!));
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        int limit = DefaultLimit;
        if (limitText is not null
            && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1))
        {
            ConsoleUi.Failure($"--limit expects a number of rows of 1 or more; '{limitText}' is not one.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (!EvidencePolicyOption.TryParse(policyText, out EvidencePolicy policy, out string? refusal))
        {
            ConsoleUi.Failure(refusal!);
            return InterCatExitCode.InvalidInvocation;
        }

        // An interval keeps the waits to the calls that began in it (`waits-v1` §1).
        TimeRange? interval = null;
        if (intervalText is not null)
        {
            interval = TickInterval.Read(intervalText, out string? tooWide);
            if (interval is null)
            {
                ConsoleUi.Failure(tooWide ?? "--interval names no interval: " + TickInterval.Takes + ".");
                return InterCatExitCode.InvalidInvocation;
            }
        }

        string path = Path.GetFullPath(directory);
        if (!Directory.Exists(path))
        {
            ConsoleUi.Failure($"No session directory at {path}.");
            return InterCatExitCode.InvalidInvocation;
        }

        ConsoleUi.Progress("Projecting the current generation, then reading its calls and the other ends of its calls.");
        WaitReading reading;
        try
        {
            reading = CallWaits.Read(SessionStore.OpenExisting(LocalOwnedDirectory.Open(path)), policy, interval, cancellationToken);
        }
        catch (InvalidOperationException exception) when (exception is not NoSessionException)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        ProcessInstanceId? process = null;
        if (processText is not null)
        {
            if (ProcessOption.Resolve(reading.Processes, "--process", processText, out string? unresolved) is not { } named)
            {
                ConsoleUi.Failure(unresolved!);
                return InterCatExitCode.InvalidInvocation;
            }

            process = named;
        }

        CallWait[] made = [.. reading.Waits.Where(wait => process is not { } one || wait.Caller == one)];
        CallWait[] served = process is { } server ? [.. reading.Waits.Where(wait => wait.Server == server)] : [];
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(Document(reading, path, process, made, served, limit), JsonContracts.Indented));
            return InterCatExitCode.Success;
        }

        Print(reading, path, process, made, served, limit);
        return InterCatExitCode.Success;
    }

    private static object Document(WaitReading reading, string path, ProcessInstanceId? process, CallWait[] made,
        CallWait[] served, int limit) => new
    {
        Contract,
        SessionPath = path,
        reading.SessionId,
        reading.Generation,
        EvidencePolicy = reading.Policy.ToString(),
        Interval = reading.Interval is { } read ? new { read.StartTicks, read.EndTicks } : null,
        reading.OutsideInterval,
        Coverage = reading.Coverage.Select(coverage => new
        {
            Mechanism = MechanismText.Name(coverage.Mechanism),
            State = coverage.State.ToString(),
            coverage.Reason,
        }),
        Process = process is { } one ? Process(reading, one) : null,
        Waits = made.Length,
        Listed = made.Take(limit).Select(wait => Wait(reading, wait)),
        Served = process is null ? null : served.Select(wait => Wait(reading, wait)),
        Servers = process is not null ? null : CallWaits.Servers(reading).Take(limit).Select(server => new
        {
            Server = Process(reading, server.Server),
            server.Callers,
            server.Calls,
            InCallsNanoseconds = server.InCalls,
            server.Linked,
            LinkedInCallsNanoseconds = server.LinkedInCalls,
            ServerRanNanoseconds = server.ServerRan,
        }),
        NotValidated = reading.NotValidated.OrderBy(entry => entry.Key).ToDictionary(entry => entry.Key.ToString(), entry => entry.Value),
        reading.NotAdmitted,
    };

    private static object Wait(WaitReading reading, CallWait wait) => new
    {
        Caller = Process(reading, wait.Caller),
        wait.Interface,
        InterfaceName = wait.Interface is { } uuid ? RpcInterfaceNames.Of(uuid)?.Name : null,
        Server = wait.Server is { } server ? Process(reading, server) : null,
        wait.Calls,
        wait.Failed,
        InCallsNanoseconds = wait.InCalls,
        DurationsNanoseconds = wait.Durations,
        wait.Linked,
        LinkedInCallsNanoseconds = wait.LinkedInCalls,
        ServerRanNanoseconds = wait.ServerRan,
        Unresolved = wait.Unresolved.OrderBy(entry => entry.Key).ToDictionary(entry => entry.Key.ToString(), entry => entry.Value),
        wait.ServerNotAdmitted,
    };

    private static object Process(WaitReading reading, ProcessInstanceId process) => new
    {
        InstanceId = process.ToString(),
        ProcessId = reading.Processes.TryGetValue(process, out ProcessNode? node) ? node.ProcessId : (int?)null,
        Name = reading.Processes.TryGetValue(process, out ProcessNode? named) ? named.Name : null,
    };

    private static void Print(WaitReading reading, string path, ProcessInstanceId? process, CallWait[] made, CallWait[] served,
        int limit)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        ConsoleUi.Heading(process is { } one ? "Waits of " + WaitText.Named(reading, one) : "Waits");
        ConsoleUi.Field("Session", path);
        ConsoleUi.Field("Generation", reading.Generation.ToString(culture));
        if (reading.Interval is { } interval)
        {
            ConsoleUi.Field("Interval", ComparisonText.Interval(interval, null, culture) + ", the calls that began in it alone");
        }

        ConsoleUi.Field("Coverage", CoverageText.Unlabelled(reading.Coverage));
        int callers = made.Select(wait => wait.Caller).Distinct().Count();
        ConsoleUi.Field("Counted", process is null
            ? CountText.Of(made.Sum(wait => wait.Calls), "completed call", "completed calls")
                + string.Create(culture, $" of {callers:N0} {CountText.Agree(callers, "process", "processes")}, the most time first")
            : CountText.Of(made.Sum(wait => wait.Calls), "completed call", "completed calls") + " it made and "
                + CountText.Of(served.Sum(wait => wait.Calls), "call", "calls") + " it served, the most time first");
        if (process is not null) ConsoleUi.Heading("Its calls");
        if (made.Length == 0)
        {
            ConsoleUi.Note(process is null ? WaitText.None(reading)
                : reading.Interval is null ? "It made no completed call." : "It began no completed call in the interval.");
        }
        else
        {
            ConsoleUi.Table(
                process is null ? SessionColumns : ProcessColumns,
                [.. made.Take(limit).Select(wait => (IReadOnlyList<string>)
                [
                    .. process is null ? [WaitText.Named(reading, wait.Caller)] : Array.Empty<string>(),
                    RpcInterfaceNames.Describe(wait.Interface),
                    WaitText.Served(reading, wait),
                    wait.Calls.ToString("N0", culture),
                    wait.Failed.ToString("N0", culture),
                    OperationText.Duration(wait.InCalls, culture),
                    OperationText.Duration(wait.Durations.Median, culture),
                    OperationText.Duration(wait.Durations.Percentile95, culture),
                    OperationText.Duration(wait.Durations.Maximum, culture),
                    WaitText.ServerRan(wait.Linked, wait.Calls, wait.LinkedInCalls, wait.ServerRan, culture),
                ])]);
            if (made.Length > limit)
            {
                ConsoleUi.Note(string.Create(culture, $"{made.Length - limit:N0} more, with less time; --limit {made.Length:N0} lists every one."));
            }
        }

        if (process is not null)
        {
            ConsoleUi.Heading("Calls it served");
            if (served.Length == 0)
            {
                ConsoleUi.Note("No linked client call reached it.");
            }
            else
            {
                ConsoleUi.Table(
                    ["Caller", "Calls to", "Calls", "In calls", "It ran"],
                    [.. served.Select(wait => (IReadOnlyList<string>)
                    [
                        WaitText.Named(reading, wait.Caller),
                        RpcInterfaceNames.Describe(wait.Interface),
                        wait.Calls.ToString("N0", culture),
                        OperationText.Duration(wait.InCalls, culture),
                        WaitText.ServerRan(wait.Linked, wait.Calls, wait.LinkedInCalls, wait.ServerRan, culture),
                    ])]);
            }
        }
        else if (CallWaits.Servers(reading) is { Count: > 0 } servers)
        {
            ConsoleUi.Heading("Servers");
            ConsoleUi.Table(
                ["Server", "Callers", "Calls", "In calls", "Server ran"],
                [.. servers.Take(limit).Select(server => (IReadOnlyList<string>)
                [
                    WaitText.Named(reading, server.Server),
                    server.Callers.ToString("N0", culture),
                    server.Calls.ToString("N0", culture),
                    OperationText.Duration(server.InCalls, culture),
                    WaitText.ServerRan(server.Linked, server.Calls, server.LinkedInCalls, server.ServerRan, culture),
                ])]);
        }

        if (WaitText.NotCounted(reading) is { } uncounted)
        {
            ConsoleUi.Note(uncounted);
        }

        ConsoleUi.Note(WaitText.Note);
        if (reading.Interval is not null)
        {
            ConsoleUi.Note(WaitText.IntervalNote);
        }
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat waits <session-directory> [--process <pid | instance>] [--limit <n>]");
        ConsoleUi.Line("           [--interval <start:end>] [--evidence-policy <policy>] [--json]");
        ConsoleUi.Line("  The time each process spent inside the RPC calls it made, from each call's start to its stop,");
        ConsoleUi.Line("  by interface and by the process that served them through ALPC, the most time first, with the");
        ConsoleUi.Line("  server's own time where its call completed too, and the servers callers waited on. No wait is");
        ConsoleUi.Line("  said to have blocked or delayed anything. --process lists one process's calls and those it");
        ConsoleUi.Line("  served; --limit lists that many rows, 20 unless it says so; --json writes them as a document.");
        ConsoleUi.Line("  --interval keeps them to the calls that began in it, each timed to its stop, and counts those that");
        ConsoleUi.Line("  began outside it.");
    }
}
