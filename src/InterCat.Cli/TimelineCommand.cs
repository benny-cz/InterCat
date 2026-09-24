using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>
/// The Desktop's zoomed timeline, headlessly: any interval of a session's all-observations timeline in the columns
/// asked for, bucketed and judged for coverage exactly as the overview's own timeline (R18, plan §6.2).
/// </summary>
internal static class TimelineCommand
{
    private const int DefaultColumns = 64;

    public static Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken) =>
        Task.FromResult(Run(command, cancellationToken));

    private static InterCatExitCode Run(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? intervalText = command.TakeOption("--interval");
        string? columnsText = command.TakeOption("--columns");
        bool json = command.TryTakeFlag("--json");
        string? directory = command.TakePositional();
        bool hasUnknown = command.TryReportUnknown(out string? unknown);
        int columns = DefaultColumns;
        bool invalidColumns = columnsText is not null
            && (!int.TryParse(columnsText, NumberStyles.None, CultureInfo.InvariantCulture, out columns)
                || columns is < 1 or > SessionTimelineQuery.MaximumColumns);
        TimeRange? interval = ParseInterval(intervalText);
        if (directory is null || hasUnknown || invalidColumns || interval is null)
        {
            ConsoleUi.Failure(directory is null ? "A session directory is required: icat timeline <directory>."
                : hasUnknown ? $"Unknown or incomplete option: {unknown}"
                : invalidColumns ? $"--columns must be an integer from 1 to {SessionTimelineQuery.MaximumColumns:N0}."
                : "--interval start:end is required, in 100-nanosecond session-relative ticks, with end > start.");
            PrintHelp();
            return InterCatExitCode.InvalidInvocation;
        }

        string path = Path.GetFullPath(directory);
        if (!Directory.Exists(path))
        {
            ConsoleUi.Failure($"No session directory at {path}.");
            return InterCatExitCode.InvalidInvocation;
        }

        SessionTimelineDetail detail;
        long started = Stopwatch.GetTimestamp();
        try
        {
            detail = SessionTimelineQuery.Detail(
                SessionStore.OpenExisting(LocalOwnedDirectory.Open(path)), interval.Value, columns, cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }
        catch (InvalidOperationException exception)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                Contract = "timeline-v1",
                SessionPath = path,
                Detail = detail,
            }, JsonContracts.Indented));
            return InterCatExitCode.Success;
        }

        ConsoleUi.Heading("Session timeline");
        ConsoleUi.Field("Session", path);
        ConsoleUi.Field("Generation", ConsoleUi.Count(detail.Generation));
        ConsoleUi.Field("Interval", WorkspaceTime.FormatRange(detail.Interval, CultureInfo.CurrentCulture));
        ConsoleUi.Field("Observed rows", ConsoleUi.Count(detail.Buckets.Sum(bucket => (long)bucket.ObservationCount)));
        ConsoleUi.Field("Counted in", $"{elapsed.TotalMilliseconds.ToString("N0", CultureInfo.CurrentCulture)} ms");
        ConsoleUi.Table(
            ["Interval", "Records", "Mostly", "Coverage"],
            [.. detail.Buckets.Select(bucket => (IReadOnlyList<string>)
            [
                WorkspaceTime.FormatRange(bucket.Interval, CultureInfo.CurrentCulture),
                ConsoleUi.Count(bucket.ObservationCount),
                bucket.ObservationCount == 0 ? "-" : bucket.DominantMechanism.ToString(),
                bucket.Coverage.ToString(),
            ])]);
        ConsoleUi.Note("Rows without a usable session time have no place in any interval and are not counted.");
        ConsoleUi.Note("An empty bucket is not proof of inactivity: its coverage stays unknown.");
        return InterCatExitCode.Success;
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
        ConsoleUi.Line("icat timeline <session-directory> --interval <start:end> [--columns <1-2000>] [--json]");
        ConsoleUi.Line("  Read-only, leased all-observations timeline over [start,end) in 100-nanosecond session ticks.");
        ConsoleUi.Line("  Buckets partition the interval exactly, as the Desktop's zoomed timeline draws them.");
    }
}
