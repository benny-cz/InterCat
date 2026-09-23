using System.Text.Json;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>
/// The Desktop's exact leased overview, headlessly reachable at the same default evidence policy (R18).
/// JSON retains the graph identity, eligible set, channels and caveats rather than re-counting them here.
/// </summary>
internal static class OverviewCommand
{
    public static Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken) =>
        Task.FromResult(Run(command, cancellationToken));

    private static InterCatExitCode Run(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? directory = command.TakePositional();
        bool json = command.TryTakeFlag("--json");
        bool hasUnknown = command.TryReportUnknown(out string? unknown);
        if (directory is null || hasUnknown)
        {
            ConsoleUi.Failure(directory is null
                ? "A session directory is required: icat overview <directory>."
                : $"Unknown or incomplete option: {unknown}");
            PrintHelp();
            return InterCatExitCode.InvalidInvocation;
        }

        string path = Path.GetFullPath(directory);
        if (!Directory.Exists(path))
        {
            ConsoleUi.Failure($"No session directory at {path}.");
            return InterCatExitCode.InvalidInvocation;
        }

        SessionOverviewBundle overview;
        try
        {
            overview = SessionOverviewProjector.Project(
                SessionStore.OpenExisting(LocalOwnedDirectory.Open(path)), cancellationToken: cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                Contract = "overview-v1",
                SessionPath = path,
                Bundle = overview,
            }, JsonContracts.Indented));
            return InterCatExitCode.Success;
        }

        ConsoleUi.Heading("Published session overview");
        ConsoleUi.Field("Session", path);
        ConsoleUi.Field("Generation", ConsoleUi.Count(overview.Generation));
        ConsoleUi.Field("Process instances", ConsoleUi.Count(overview.Nodes.Count));
        ConsoleUi.Field("Paired TCP edges", ConsoleUi.Count(overview.Edges.Count));
        ConsoleUi.Field("Paired TCP channels", overview.ChannelProjectionProblem is null
            ? ConsoleUi.Count(overview.Channels.Count) : "unavailable above overview bound");
        ConsoleUi.Field("Observed rows", ConsoleUi.Count(overview.ObservationRows));
        ConsoleUi.Field("Graph-eligible rows", ConsoleUi.Count(overview.GraphEligibleRows));
        ConsoleUi.Field("Coverage ledger", overview.CoverageLedgerPublished ? "published" : "not published");
        foreach (string caveat in overview.Caveats) ConsoleUi.Note(caveat);
        ConsoleUi.Note("icat overview <directory> --json returns the exact bundle the Desktop projects.");
        return InterCatExitCode.Success;
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat overview <session-directory> [--json]");
        ConsoleUi.Line("  Read-only, leased process graph, admitted paired TCP channels and all-observations timeline.");
        ConsoleUi.Line("  The graph is narrower than the session. --json returns the Desktop's exact numeric bundle.");
    }
}
