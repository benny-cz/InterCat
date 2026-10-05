using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>
/// The Desktop's zoomed timeline and its interval table, headlessly: any interval of a session's timeline in the columns
/// asked for, bucketed and judged for coverage exactly as the overview's own timeline (R18, plan §6.2). A scope lists the
/// rows a lane of the table lists - one mechanism's, one process instance's own records or one source direction of them,
/// one channel's or one of its ends' - and <c>--bytes</c> states what each interval's records sent and received.
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
        string? mechanismText = command.TakeOption("--mechanism");
        string? processText = command.TakeOption("--process");
        string? directionText = command.TakeOption("--direction");
        string? channelText = command.TakeOption("--channel");
        string? endText = command.TakeOption("--end");
        bool bytes = command.TryTakeFlag("--bytes");
        bool json = command.TryTakeFlag("--json");
        string? directory = command.TakePositional();
        bool hasUnknown = command.TryReportUnknown(out string? unknown);
        int columns = DefaultColumns;
        bool invalidColumns = columnsText is not null
            && (!int.TryParse(columnsText, NumberStyles.None, CultureInfo.InvariantCulture, out columns)
                || columns is < 1 or > SessionTimelineQuery.MaximumColumns);
        TimeRange? interval = TickInterval.Read(intervalText, out string? tooWide);
        string? scopeProblem = TryParseScope(mechanismText, processText, directionText, channelText, endText,
            out IntervalByteScope scope);
        if (directory is null || hasUnknown || invalidColumns || interval is null || scopeProblem is not null)
        {
            ConsoleUi.Failure(directory is null ? "A session directory is required: icat timeline <directory>."
                : hasUnknown ? $"Unknown or incomplete option: {unknown}"
                : invalidColumns ? $"--columns must be an integer from 1 to {SessionTimelineQuery.MaximumColumns:N0}."
                : interval is null ? tooWide ?? "--interval start:end is required, in 100-nanosecond session-relative ticks, with end > start."
                : scopeProblem!);
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        string path = Path.GetFullPath(directory);
        if (!Directory.Exists(path))
        {
            ConsoleUi.Failure($"No session directory at {path}.");
            return InterCatExitCode.InvalidInvocation;
        }

        long generation;
        IReadOnlyList<TimelineBucket> buckets;
        SessionIntervalByteMeasures? measured = null;
        long started = Stopwatch.GetTimestamp();
        try
        {
            SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(path));
            (generation, buckets) = Count(store, interval.Value, columns, scope, cancellationToken);
            if (bytes)
            {
                measured = SessionIntervalByteQuery.Measure(store, interval.Value, columns, scope, cancellationToken: cancellationToken);
            }
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
                Detail = new { Generation = generation, Interval = interval.Value, Scope = scope.Description, Buckets = buckets },
                Bytes = measured,
            }, JsonContracts.Indented));
            return InterCatExitCode.Success;
        }

        ConsoleUi.Heading("Session timeline");
        ConsoleUi.Field("Session", path);
        ConsoleUi.Field("Generation", ConsoleUi.Count(generation));
        ConsoleUi.Field("Interval", WorkspaceTime.FormatRange(interval.Value, CultureInfo.CurrentCulture));
        ConsoleUi.Field("Records", scope.Description);
        ConsoleUi.Field("Observed rows", ConsoleUi.Count(buckets.Sum(bucket => (long)bucket.ObservationCount)));
        ConsoleUi.Field("Counted in", $"{elapsed.TotalMilliseconds.ToString("N0", CultureInfo.CurrentCulture)} ms");
        if (buckets.Count == 0)
        {
            ConsoleUi.Note($"None of the {scope.Description} has a session time in this interval, so the lane has no bucket.");
            return InterCatExitCode.Success;
        }

        ConsoleUi.Table(
            measured is null ? ["Interval", "Records", "Mostly", "Coverage"] : ["Interval", "Records", "Mostly", "Coverage", "Sent", "Received"],
            [.. buckets.Select(bucket => Row(bucket, measured))]);
        ConsoleUi.Note("Rows without a usable session time have no place in any interval and are not counted.");
        ConsoleUi.Note("An empty bucket's coverage is the capture's there: covered means nothing it collects happened, a gap "
            + "that records were lost, unknown that the capture says nothing of it.");
        if (measured is not null)
        {
            ConsoleUi.Note("Sent is what send records measured (sender accounting), received what receive records measured "
                + "(receiver accounting); '-' is no such record. An unmeasured record stated no size: it is counted apart, "
                + "never as zero.");
            long other = measured.Columns.Sum(column => column.OtherMeasured + column.OtherUnmeasured);
            if (other > 0)
            {
                ConsoleUi.Note($"{other:N0} records state neither side and are in neither column.");
            }
        }

        return InterCatExitCode.Success;
    }

    /// <summary>One bucket as a table row, with its bytes when they were measured.</summary>
    private static List<string> Row(TimelineBucket bucket, SessionIntervalByteMeasures? measured)
    {
        var cells = new List<string>(6)
        {
            WorkspaceTime.FormatRange(bucket.Interval, CultureInfo.CurrentCulture),
            ConsoleUi.Count(bucket.ObservationCount),
            bucket.ObservationCount == 0 ? "-" : bucket.DominantMechanism.ToString(),
            bucket.Coverage.ToString(),
        };
        if (measured is not null)
        {
            TransportBytes? bytes = measured.For(bucket.Interval);
            cells.Add(bytes is null ? "?" : Side(bytes.SentBytes, bytes.SentMeasured, bytes.SentUnmeasured));
            cells.Add(bytes is null ? "?" : Side(bytes.ReceivedBytes, bytes.ReceivedMeasured, bytes.ReceivedUnmeasured));
        }

        return cells;
    }

    /// <summary>
    /// The scope's buckets from the same queries the Desktop's lanes are counted by: the whole timeline or a mechanism's
    /// lane from the timeline itself, and a process's or channel's rows from a focused count.
    /// </summary>
    private static (long Generation, IReadOnlyList<TimelineBucket> Buckets) Count(
        SessionStore store, TimeRange interval, int columns, IntervalByteScope scope, CancellationToken cancellationToken)
    {
        if (scope.Owner is null && scope.ChannelKey is null)
        {
            SessionTimelineDetail detail = SessionTimelineQuery.Detail(store, interval, columns, cancellationToken);
            return (detail.Generation, scope.Mechanism is { } mechanism
                ? detail.MechanismLanes.FirstOrDefault(lane => lane.Mechanism == mechanism)?.Buckets ?? []
                : detail.Buckets);
        }

        SessionFocusedTimeline focused = SessionTimelineQuery.Focused(store, interval, columns,
            new TimelineFocus(scope.ChannelKey, scope.Owner is { } owner ? [owner] : []), cancellationToken: cancellationToken);
        return (focused.Whole.Generation, scope.Direction is { } direction
            ? focused.DirectionLanes.Single(lane => lane.Direction == direction).Buckets
            : scope.Owner is not null
            ? focused.OwnerLane
            : scope.End is { } end
            ? focused.ChannelEndLanes.Single(lane => lane.End == end).Buckets
            : focused.Focus);
    }

    /// <summary>The scope a lane of the interval table lists, from the options that name it; a problem when they cannot.</summary>
    private static string? TryParseScope(
        string? mechanismText, string? processText, string? directionText, string? channelText, string? endText,
        out IntervalByteScope scope)
    {
        scope = IntervalByteScope.Whole;
        if (!MetricCommand.TryParseOptional(mechanismText, "--mechanism", out Mechanism? mechanism, out string? problem)
            || !MetricCommand.TryParseOptional(directionText, "--direction", out Direction? direction, out problem))
        {
            return problem;
        }

        if (processText is not null && !Guid.TryParse(processText, out _))
        {
            return $"--process requires a process instance id, as icat processes prints it; '{processText}' is not one. "
                + "A PID is not an instance identity.";
        }

        if (endText is not null && endText is not ("0" or "1"))
        {
            return $"--end is 0 or 1, the channel's first or second end; '{endText}' is neither.";
        }

        scope = new IntervalByteScope
        {
            Mechanism = mechanism,
            Owner = processText is null ? null : new ProcessInstanceId(Guid.Parse(processText)),
            Direction = direction,
            ChannelKey = channelText,
            End = endText is null ? null : endText == "1" ? 1 : 0,
        };
        return scope.Validate() is { } invalid
            ? invalid + " Use --mechanism, --process (with --direction) or --channel (with --end), at most one of them."
            : null;
    }

    /// <summary>One side's bytes in a table cell: the measured sum, with how many recorded no size, or '-' for no record.</summary>
    private static string Side(long bytes, long measured, long unmeasured) =>
        measured > 0
            ? $"{ConsoleUi.Count(bytes)} B" + (unmeasured > 0 ? $" (+{ConsoleUi.Count(unmeasured)} unmeasured)" : string.Empty)
            : unmeasured > 0 ? $"{ConsoleUi.Count(unmeasured)} unmeasured" : "-";

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat timeline <session-directory> --interval <start:end> [--columns <1-2000>]");
        ConsoleUi.Line("    [--mechanism <name> | --process <instance-id> [--direction <name>] | --channel <key> [--end <0|1>]]");
        ConsoleUi.Line("    [--bytes] [--json]");
        ConsoleUi.Line("  Read-only, leased timeline over [start,end) in 100-nanosecond session ticks.");
        ConsoleUi.Line("  Buckets partition the interval exactly, as the Desktop's zoomed timeline draws them.");
        ConsoleUi.Line("  A scope lists the records one lane of the Desktop's interval table lists; --bytes adds what");
        ConsoleUi.Line("  each interval's records sent and received, unmeasured sizes counted apart.");
    }
}
