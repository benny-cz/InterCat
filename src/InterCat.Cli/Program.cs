using InterCat.Cli;
using InterCat.Domain;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    ConsoleUi.Warn("Cancelling. Any owned capture session is stopped before exit.");
    cancellation.Cancel();
};

return (int)await RunAsync(args, cancellation.Token).ConfigureAwait(false);

static async Task<InterCatExitCode> RunAsync(string[] args, CancellationToken cancellationToken)
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
    {
        PrintHelp();
        return InterCatExitCode.Success;
    }

    var command = new CommandLine(args[1..]);
    try
    {
        return args[0].ToLowerInvariant() switch
        {
            "capabilities" => await CapabilitiesCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
            "profiles" => await ProfilesCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
            "measure" => await MeasureCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
            "import" => await ImportCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
            "session" => await SessionCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
            "retain" => await RetainCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
            "metric" => await MetricCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
            "processes" => await ProcessesCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
            "verify" => await VerifyCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
            "bench" => await BenchCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
            _ => UnknownCommand(args[0]),
        };
    }
    catch (OperationCanceledException)
    {
        ConsoleUi.Warn("Cancelled before a result was published.");
        return InterCatExitCode.Cancelled;
    }
    catch (UnauthorizedAccessException exception)
    {
        ConsoleUi.Failure(exception.Message);
        return InterCatExitCode.PermissionOrCapabilityFailure;
    }
    catch (IOException exception)
    {
        ConsoleUi.Failure(exception.Message);
        return InterCatExitCode.CorruptedInput;
    }
    catch (InvalidDataException exception)
    {
        // Every format reader refuses what it cannot read with this exception and a reason. It is not an
        // IOException, so without this a damaged session would end the process with a stack trace instead of
        // the documented exit code and the reader's own explanation (§20.4, §20.6 Storage).
        ConsoleUi.Failure(exception.Message);
        return InterCatExitCode.CorruptedInput;
    }
}

static InterCatExitCode UnknownCommand(string name)
{
    ConsoleUi.Failure($"Unknown command: {name}");
    PrintHelp();
    return InterCatExitCode.InvalidInvocation;
}

static void PrintHelp()
{
    ConsoleUi.Line("InterCat command line (M1)");
    ConsoleUi.Line();
    ConsoleUi.Line("  icat capabilities [--output <path>] [--overwrite] [--json]");
    ConsoleUi.Line("      Read-only source inventory. Starts no capture.");
    ConsoleUi.Line();
    ConsoleUi.Line("  icat profiles [profile] [--diagnostic-etl] [--output <path>] [--overwrite] [--json]");
    ConsoleUi.Line("  icat profiles content --help");
    ConsoleUi.Line("      Lists capture intents or previews requested/effective settings and omissions.");
    ConsoleUi.Line("      Reads schemas only; never enables a provider or starts a capture.");
    ConsoleUi.Line("      Focused TCP: focused-transport --mechanism tcp [--pid <id> ...]");
    ConsoleUi.Line();
    ConsoleUi.Line("  icat measure <tcp|pipe|rpc> [--output <dir>] [--overwrite] [--json]");
    ConsoleUi.Line("      Runs the named fixture under one owned ETW session and computes its tier.");
    ConsoleUi.Line();
    ConsoleUi.Line("  icat measure tcp [--output <dir>] [--seed <n>] [--connections <n>] [--messages <n>]");
    ConsoleUi.Line("                   [--bytes <n>] [--workload <path>] [--overwrite] [--json]");
    ConsoleUi.Line("      Runs the seeded TCP loopback truth workload under an owned ETW session and");
    ConsoleUi.Line("      reports measured coverage against the independent truth log. Needs elevation.");
    ConsoleUi.Line();
    ConsoleUi.Line("  icat import <source.etl> [--into <session-dir>] [--rows-per-segment <n>]");
    ConsoleUi.Line("             [--output <path>] [--overwrite] [--json]");
    ConsoleUi.Line("             [--max-entries-in-memory <n>] [--spill-directory <dir>]");
    ConsoleUi.Line("      Reads a standalone ETL through the canonical import contract and reports its");
    ConsoleUi.Line("      source identity, import identity, derived clock, counts and multiplicity.");
    ConsoleUi.Line("      With --into it publishes a session: the admitted journal-v1 evidence and the");
    ConsoleUi.Line("      observation-v1 segments derived from it, as one committed generation.");
    ConsoleUi.Line();
    ConsoleUi.Line("  icat session <directory> [--rows <n>] [--output <path>] [--overwrite] [--json]");
    ConsoleUi.Line("      Opens a published session, verifies every dependency the current generation");
    ConsoleUi.Line("      names, and reports its evidence boundary, its segments, their column");
    ConsoleUi.Line("      availability and their byte metrics. Read-only; repairs nothing.");
    ConsoleUi.Line();
    ConsoleUi.Line("  icat retain <directory> --release-journal-before-record <n>");
    ConsoleUi.Line("             [--confirm --reason <text>] [--output <path>] [--overwrite] [--json]");
    ConsoleUi.Line("      Measures what releasing a prefix of the admitted journal would give up, and");
    ConsoleUi.Line("      performs it only with --confirm and a stated reason (ADR-010).");
    ConsoleUi.Line();
    ConsoleUi.Line("  icat metric <directory> --metric <name> [--basis <name>] [--byte-domain <name>]");
    ConsoleUi.Line("             [--side <name>] [--rate-numerator <name>] [--layer <name>]");
    ConsoleUi.Line("             [--mechanism <name>] [--interval <start>:<end>] [--evidence <n>]");
    ConsoleUi.Line("             [--output <path>] [--overwrite] [--json]");
    ConsoleUi.Line("  icat metric --matrix [--json]");
    ConsoleUi.Line("      Answers one metric over a published session against section 5.3's matrix. A");
    ConsoleUi.Line("      metric outside its basis is rejected with the compatible ones named; one this");
    ConsoleUi.Line("      session cannot derive is reported as unavailable, with what it needs.");
    ConsoleUi.Line("      --group-by process|mechanism ranks it, with an exact remainder past --top.");
    ConsoleUi.Line();
    ConsoleUi.Line("  icat processes <directory> [--top <n>] [--pid <id>] [--json]");
    ConsoleUi.Line("      Lists the process instances the session's evidence supports, with each one's");
    ConsoleUi.Line("      lifetime, records and transport bytes. A reused PID is two instances.");
    ConsoleUi.Line();
    ConsoleUi.Line("  icat verify tcp --run <raw-run-dir> --output <curated-dir> [--overwrite] [--json]");
    ConsoleUi.Line("      Re-evaluates a run offline and writes only fixture-scoped shareable evidence.");
    ConsoleUi.Line();
    ConsoleUi.Line("  icat bench [--output <path>] [--series <series.json>] [--overwrite] [--json]");
    ConsoleUi.Line("             [--no-storage-probe]");
    ConsoleUi.Line("      Publishes the IC-010 baseline: this machine against the section 12 reference,");
    ConsoleUi.Line("      every section 12 budget with what measured it, and the per-build validation");
    ConsoleUi.Line("      backlog. Starts no capture and needs no elevation.");
    ConsoleUi.Line();
    ConsoleUi.Line("  Exit codes: 0 success, 1 partial result, 2 invalid invocation,");
    ConsoleUi.Line("              3 permission or capability failure, 4 corrupted input, 5 cancelled.");
    ConsoleUi.Line();
    ConsoleUi.Line("  Machine-readable data goes to stdout; progress and status go to stderr.");
}
