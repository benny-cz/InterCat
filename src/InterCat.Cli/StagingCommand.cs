using System.Text.Json;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

internal sealed record StagingDocument
{
    public required string Contract { get; init; }
    public required string Path { get; init; }
    public required bool Performed { get; init; }
    public required StagingCleanupReport Report { get; init; }
    public required string? ConfirmCommand { get; init; }
    public required string Explanation { get; init; }
}

/// <summary>Reviews staging ownership and cleans only an explicitly reviewed abandoned set.</summary>
internal static class StagingCommand
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

        string? path = command.TakePositional();
        string? expectedSet = command.TakeOption("--expect-set");
        bool confirm = command.TryTakeFlag("--confirm");
        bool json = command.TryTakeFlag("--json");
        if (command.TryReportUnknown(out string? unknown))
        {
            ConsoleUi.Failure($"Unknown or incomplete option: {unknown}");
            return InterCatExitCode.InvalidInvocation;
        }

        if (path is null || (confirm && expectedSet is null) || (!confirm && expectedSet is not null))
        {
            ConsoleUi.Failure(
                "Use icat staging <directory> to preview. Cleanup needs --confirm --expect-set <digest> "
                + "from that preview; --expect-set has no meaning without --confirm.");
            return InterCatExitCode.InvalidInvocation;
        }

        string full = Path.GetFullPath(path);
        if (!Directory.Exists(full))
        {
            ConsoleUi.Failure($"No session directory at {full}.");
            return InterCatExitCode.InvalidInvocation;
        }

        cancellationToken.ThrowIfCancellationRequested();
        SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(full));
        StagingCleanupReport report;
        try
        {
            report = confirm
                ? store.CleanupAbandonedStaging(expectedSet, cancellationToken)
                : store.PreviewStagingCleanup();
        }
        catch (InvalidOperationException exception)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.InvalidInvocation;
        }

        bool eligible = report.AbandonedFiles.Count + report.MarkerOnlyFiles.Count > 0;
        var document = new StagingDocument
        {
            Contract = "staging-cleanup-v1",
            Path = full,
            Performed = confirm && report.RemovedFiles.Count > 0,
            Report = report,
            ConfirmCommand = !confirm && eligible
                ? $"icat staging \"{full}\" --confirm --expect-set {report.CandidateDigest}"
                : null,
            Explanation = confirm
                ? "Only files with an unlocked ownership marker from the reviewed set were removed. "
                    + "Active and unmarked legacy staging stayed in place."
                : "Preview only: no files were changed. An active writer keeps its marker locked; "
                    + "unmarked legacy staging cannot be proven abandoned and is never cleaned automatically.",
        };

        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(document, JsonContracts.Indented));
        }
        else
        {
            ConsoleUi.Heading(confirm ? "Staging cleanup" : "Staging cleanup preview");
            ConsoleUi.Field("Session", full);
            ConsoleUi.Field("Abandoned staged files", ConsoleUi.Count(report.AbandonedFiles.Count));
            ConsoleUi.Field("Marker-only files", ConsoleUi.Count(report.MarkerOnlyFiles.Count));
            ConsoleUi.Field("Active writer files", ConsoleUi.Count(report.ActiveFiles.Count));
            ConsoleUi.Field("Unmarked legacy files", ConsoleUi.Count(report.UnmarkedFiles.Count));
            ConsoleUi.Field("Files removed", ConsoleUi.Count(report.RemovedFiles.Count));
            foreach (string name in report.AbandonedFiles.Take(12))
            {
                ConsoleUi.Line($"  abandoned: {name}");
            }

            if (report.AbandonedFiles.Count > 12)
            {
                ConsoleUi.Note($"Showing 12 of {report.AbandonedFiles.Count:N0} abandoned files; --json lists all.");
            }

            ConsoleUi.Note(document.Explanation);
            if (document.ConfirmCommand is not null)
            {
                ConsoleUi.Line(document.ConfirmCommand);
            }
        }

        return report.ActiveFiles.Count > 0 || report.UnmarkedFiles.Count > 0
            ? InterCatExitCode.PartialResultSuccess
            : InterCatExitCode.Success;
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat staging <directory> [--confirm --expect-set <digest>] [--json]");
        ConsoleUi.Line("  Previews abandoned, active, unmarked legacy and marker-only staging without writing.");
        ConsoleUi.Line("  Digest-bound confirmation cleans only reviewed files whose writer has let go.");
        ConsoleUi.Line("  Published evidence and unmarked legacy staging are never removed by this command.");
    }
}
