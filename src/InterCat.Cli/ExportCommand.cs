using System.Globalization;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>
/// The Desktop's "Export this view" headlessly, in the same <c>intercat-export-v1</c> contract (R18). The rung is
/// reached by descending through row keys, as a person would by selecting rows; an interval ranks within it;
/// <c>--rank-by</c> ranks the machine and group rungs as the Desktop's selector does; and <c>--evidence</c> exports the
/// rung's source records instead of its ranked rows.
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
        string? rankText = command.TakeOption("--rank-by");
        bool evidence = command.TryTakeFlag("--evidence");
        bool redacted = command.TryTakeFlag("--share-redacted");
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
        RankingMetric? rankBy = rankText switch
        {
            null or "records" => RankingMetric.Records,
            "bytes-sent" => RankingMetric.BytesSent,
            "bytes-received" => RankingMetric.BytesReceived,
            "rpc-calls-made" => RankingMetric.RpcCallsMade,
            "rpc-calls-served" => RankingMetric.RpcCallsServed,
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
            : rankBy is null ? "--rank-by must be records, bytes-sent, bytes-received, rpc-calls-made or rpc-calls-served."
            : evidence && rankBy != RankingMetric.Records
                ? "--rank-by ranks rows; an --evidence export lists its records in reading order."
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
                new(path, interval, evidence, format!.Value, limit, redacted, rankBy!.Value),
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

        await ExportFileWriter.WriteAsync(destination, result.Content, overwrite, cancellationToken).ConfigureAwait(false);
        ConsoleUi.Heading("Export");
        ConsoleUi.Field("Written to", destination);
        ConsoleUi.Field("Contract", redacted ? RedactedShareExport.Contract : WorkspaceExport.Contract);
        ConsoleUi.Field("Rung", NavigationState.Name(result.Context.Rung));
        if (!redacted)
        {
            ConsoleUi.Field("Breadcrumb", result.Context.Breadcrumb);
            ConsoleUi.Field("Scope", result.Context.Scope);
        }
        if (!evidence) ConsoleUi.Field("Ranked by", WorkspaceExport.RankingName(result.Context.RankedBy));
        ConsoleUi.Field(evidence ? "Records" : "Rows", ConsoleUi.Count(result.Rows));
        ConsoleUi.Field("Complete", result.Context.Complete ? "yes" : "no");
        if (redacted)
            ConsoleUi.Note("Metadata-only pseudonymized report; no original sources or raw locators. Counts, times and patterns can still identify a workload. Review before sharing.");
        else
        {
            foreach (string caveat in result.Context.Caveats.Skip(1)) ConsoleUi.Note(caveat);
            if (evidence) ConsoleUi.Note("Normalized metadata and raw locators only: no body or extended-data bytes are exported.");
        }
        return result.Context.Complete ? InterCatExitCode.Success : InterCatExitCode.PartialResultSuccess;
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
        ConsoleUi.Line("            [--rank-by records|bytes-sent|bytes-received|rpc-calls-made|rpc-calls-served]");
        ConsoleUi.Line("            [--evidence [--limit <1-1000000>]] [--format json|csv] [--share-redacted] [--overwrite]");
        ConsoleUi.Line("  By default, exports one rung in the detailed intercat-export-v1 contract. Each --at descends");
        ConsoleUi.Line("  into the row with that key (group, process-instance, then channel keys from icat overview).");
        ConsoleUi.Line("  --interval ranks within [start,end) in 100-nanosecond session ticks. --rank-by orders the machine");
        ConsoleUi.Line("  and group rungs by records (the default), by each process's transport-observed bytes on its own");
        ConsoleUi.Line("  send or receive records - a row whose records recorded no size ranks after every measured row -");
        ConsoleUi.Line("  or by the RPC calls it completed as a client (made) or as a server (served), counted by their stop.");
        ConsoleUi.Line("  --evidence exports the rung's source records instead of its rows, up to --limit (100,000 by");
        ConsoleUi.Line("  default); an export that stops short says so and exits with the partial-result code. CSV");
        ConsoleUi.Line("  neutralizes formula-like text.");
        ConsoleUi.Line("  --share-redacted writes an allowlisted, pseudonymized report without raw IDs, names, addresses,");
        ConsoleUi.Line("  source files or record locators. This report is not anonymous or a reopenable session.");
    }
}
