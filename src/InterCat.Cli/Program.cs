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
            "measure" => await MeasureCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
            "verify" => await VerifyCommand.RunAsync(command, cancellationToken).ConfigureAwait(false),
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
}

static InterCatExitCode UnknownCommand(string name)
{
    ConsoleUi.Failure($"Unknown command: {name}");
    PrintHelp();
    return InterCatExitCode.InvalidInvocation;
}

static void PrintHelp()
{
    ConsoleUi.Line("InterCat command line (M0)");
    ConsoleUi.Line();
    ConsoleUi.Line("  icat capabilities [--output <path>] [--overwrite] [--json]");
    ConsoleUi.Line("      Read-only source inventory. Starts no capture.");
    ConsoleUi.Line();
    ConsoleUi.Line("  icat measure <tcp|pipe|rpc> [--output <dir>] [--overwrite] [--json]");
    ConsoleUi.Line("      Runs the named fixture under one owned ETW session and computes its tier.");
    ConsoleUi.Line();
    ConsoleUi.Line("  icat measure tcp [--output <dir>] [--seed <n>] [--connections <n>] [--messages <n>]");
    ConsoleUi.Line("                   [--bytes <n>] [--workload <path>] [--overwrite] [--json]");
    ConsoleUi.Line("      Runs the seeded TCP loopback truth workload under an owned ETW session and");
    ConsoleUi.Line("      reports measured coverage against the independent truth log. Needs elevation.");
    ConsoleUi.Line();
    ConsoleUi.Line("  icat verify tcp --run <raw-run-dir> --output <curated-dir> [--overwrite] [--json]");
    ConsoleUi.Line("      Re-evaluates a run offline and writes only fixture-scoped shareable evidence.");
    ConsoleUi.Line();
    ConsoleUi.Line("  Exit codes: 0 success, 1 partial result, 2 invalid invocation,");
    ConsoleUi.Line("              3 permission or capability failure, 4 corrupted input, 5 cancelled.");
    ConsoleUi.Line();
    ConsoleUi.Line("  Machine-readable data goes to stdout; progress and status go to stderr.");
}
