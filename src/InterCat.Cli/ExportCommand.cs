using System.Globalization;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>
/// The Desktop's "Export this view" headlessly, in the same <c>intercat-export-v1</c> contract (R18). The rung is
/// reached by descending through row keys, as a person would by selecting rows; an interval ranks within it; and
/// <c>--evidence</c> exports the rung's source records instead of its ranked rows.
/// </summary>
internal static class ExportCommand
{
    public static async Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        var path = new List<string>();
        while (command.TakeOption("--at") is { } key) path.Add(key);
        string? intervalText = command.TakeOption("--interval");
        string? output = command.TakeOption("--output");
        string? formatText = command.TakeOption("--format");
        string? limitText = command.TakeOption("--limit");
        bool evidence = command.TryTakeFlag("--evidence");
        bool overwrite = command.TryTakeFlag("--overwrite");
        string? directory = command.TakePositional();
        bool hasUnknown = command.TryReportUnknown(out string? unknown);
        TimeRange? interval = ParseInterval(intervalText);
        ExportFormat? format = formatText switch
        {
            null => output?.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) == true ? ExportFormat.Csv : ExportFormat.Json,
            "json" => ExportFormat.Json,
            "csv" => ExportFormat.Csv,
            _ => null,
        };
        int limit = SessionExport.DefaultEvidenceLimit;
        bool invalidLimit = limitText is not null
            && (!evidence
                || !int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit)
                || limit is < 1 or > SessionExport.MaximumEvidenceLimit);
        string? problem = directory is null ? "A session directory is required: icat export <directory> --output <path>."
            : hasUnknown ? $"Unknown or incomplete option: {unknown}"
            : output is null ? "--output <path> is required; an export is written only where it is asked to be."
            : format is null ? "--format must be json or csv."
            : intervalText is not null && interval is null
                ? "--interval must be start:end in 100-nanosecond session-relative ticks, with end > start."
            : invalidLimit ? $"--limit applies to --evidence and must be an integer from 1 to {SessionExport.MaximumEvidenceLimit:N0}."
            : null;
        if (problem is not null)
        {
            ConsoleUi.Failure(problem);
            PrintHelp();
            return InterCatExitCode.InvalidInvocation;
        }

        string session = Path.GetFullPath(directory!);
        string destination = Path.GetFullPath(output!);
        if (!Directory.Exists(session))
        {
            ConsoleUi.Failure($"No session directory at {session}.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (File.Exists(destination) && !overwrite)
        {
            ConsoleUi.Failure($"{destination} already exists. Pass --overwrite to replace it.");
            return InterCatExitCode.InvalidInvocation;
        }

        SessionExportResult result;
        try
        {
            result = SessionExport.Build(
                SessionStore.OpenExisting(LocalOwnedDirectory.Open(session)),
                new(path, interval, evidence, format!.Value, limit),
                DateTimeOffset.UtcNow,
                cancellationToken);
        }
        catch (ArgumentException exception)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.InvalidInvocation;
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        await PublishAsync(destination, result.Content, overwrite, cancellationToken).ConfigureAwait(false);
        ConsoleUi.Heading("Export");
        ConsoleUi.Field("Written to", destination);
        ConsoleUi.Field("Contract", WorkspaceExport.Contract);
        ConsoleUi.Field("Rung", NavigationState.Name(result.Context.Rung));
        ConsoleUi.Field("Breadcrumb", result.Context.Breadcrumb);
        ConsoleUi.Field("Scope", result.Context.Scope);
        ConsoleUi.Field(evidence ? "Records" : "Rows", ConsoleUi.Count(result.Rows));
        ConsoleUi.Field("Complete", result.Context.Complete ? "yes" : "no");
        foreach (string caveat in result.Context.Caveats.Skip(1)) ConsoleUi.Note(caveat);
        if (evidence) ConsoleUi.Note("Normalized metadata and raw locators only: no body or extended-data bytes are exported.");
        return result.Context.Complete ? InterCatExitCode.Success : InterCatExitCode.PartialResultSuccess;
    }

    /// <summary>
    /// Writes the export beside its destination and moves it into place, so a cancelled or failed write never leaves a
    /// partial file under the name asked for (§20.4).
    /// </summary>
    private static async Task PublishAsync(string destination, string content, bool overwrite, CancellationToken cancellationToken)
    {
        string folder = Path.GetDirectoryName(destination) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(folder);
        string staged = Path.Combine(folder, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.partial");
        try
        {
            await File.WriteAllTextAsync(staged, content, cancellationToken).ConfigureAwait(false);
            File.Move(staged, destination, overwrite);
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
        }
    }

    private static TimeRange? ParseInterval(string? raw)
    {
        string[] parts = raw?.Split(':') ?? [];
        return parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long start)
            && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long end)
            && end > start
                ? new TimeRange(start, end)
                : null;
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat export <session-directory> --output <path> [--at <row-key>]... [--interval <start:end>]");
        ConsoleUi.Line("            [--evidence [--limit <1-1000000>]] [--format json|csv] [--overwrite]");
        ConsoleUi.Line("  Exports one rung of the Desktop's ladder in the intercat-export-v1 contract. Each --at descends");
        ConsoleUi.Line("  into the row with that key (group, process-instance, then channel keys from icat overview).");
        ConsoleUi.Line("  --interval ranks within [start,end) in 100-nanosecond session ticks. --evidence exports the");
        ConsoleUi.Line("  rung's source records instead of its rows, up to --limit (100,000 by default); an export that");
        ConsoleUi.Line("  stops short says so and exits with the partial-result code. CSV neutralizes formula-like text.");
    }
}
