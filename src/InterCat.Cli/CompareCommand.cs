using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>
/// Two intervals of a session compared relationship by relationship (`contracts/comparison-v1.md`, plan M12): each one's
/// records, or the bytes sent across it, as a rate over each whole interval, the most changed first. A change is stated
/// only where both intervals measured alike; elsewhere both values stand, with the reason they cannot be compared.
/// </summary>
internal static class CompareCommand
{
    private const int DefaultLimit = 50;

    public static async Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? baselineText = command.TakeOption("--baseline");
        string? intervalText = command.TakeOption("--interval");
        string? rankByText = command.TakeOption("--rank-by");
        string? groupByText = command.TakeOption("--group-by");
        string? baselineSessionText = command.TakeOption("--baseline-session");
        string? limitText = command.TakeOption("--limit");
        bool json = command.TryTakeFlag("--json");
        string? output = command.TakeOption("--output");
        string? formatText = command.TakeOption("--format");
        bool redacted = command.TryTakeFlag("--share-redacted");
        bool overwrite = command.TryTakeFlag("--overwrite");
        string? directory = command.TakePositional();
        bool hasUnknown = command.TryReportUnknown(out string? unknown);
        TimeRange? baseline = TickInterval.Read(baselineText, out string? baselineTooWide);
        TimeRange? compared = TickInterval.Read(intervalText, out string? comparedTooWide);
        ComparisonMetric? metric = rankByText switch
        {
            null or "records" => ComparisonMetric.Records,
            "bytes-sent" => ComparisonMetric.BytesSent,
            _ => null,
        };
        ComparisonGrouping? grouping = groupByText switch
        {
            null => baselineSessionText is null ? ComparisonGrouping.Process : ComparisonGrouping.Executable,
            "process" => ComparisonGrouping.Process,
            "executable" => ComparisonGrouping.Executable,
            _ => null,
        };
        int limit = DefaultLimit;
        bool invalidLimit = limitText is not null
            && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1);
        ExportFormat? format = formatText switch
        {
            null => output?.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) == true ? ExportFormat.Csv : ExportFormat.Json,
            "json" => ExportFormat.Json,
            "csv" => ExportFormat.Csv,
            _ => null,
        };
        string? fileOnly = redacted ? "--share-redacted" : formatText is not null ? "--format" : overwrite ? "--overwrite" : null;
        string? problem = directory is null ? "A session directory is required: icat compare <directory>."
            : hasUnknown ? command.Refusal(unknown!)
            : baseline is null ? baselineTooWide?.Replace("--interval", "--baseline", StringComparison.Ordinal)
                ?? "--baseline is required: " + TickInterval.Takes + "."
            : compared is null ? comparedTooWide ?? "--interval is required: " + TickInterval.Takes + "."
            : metric is null ? $"--rank-by compares records or bytes-sent; '{rankByText}' is neither."
            : grouping is null ? $"--group-by compares process or executable rows; '{groupByText}' is neither."
            : baselineSessionText is not null && grouping == ComparisonGrouping.Process
                ? "--baseline-session compares two captures by executable: a process of one capture is never another's."
            : invalidLimit ? $"--limit expects a number of relationships of 1 or more; '{limitText}' is not one."
            : output is null && fileOnly is not null ? $"{fileOnly} shapes a file written with --output <path>, which was not given."
            : output is not null && json ? "--json prints the comparison and --output writes it to a file; pass one of them."
            : format is null ? $"--format writes json or csv; '{formatText}' is neither."
            : format == ExportFormat.Csv && !redacted
                ? "Only a redacted report is written as CSV: pass --share-redacted with it, or write the comparison as JSON."
            : null;
        if (problem is not null)
        {
            ConsoleUi.Failure(problem);
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        string path = Path.GetFullPath(directory!);
        if (!Directory.Exists(path))
        {
            ConsoleUi.Failure($"No session directory at {path}.");
            return InterCatExitCode.InvalidInvocation;
        }

        string? baselinePath = baselineSessionText is null ? null : Path.GetFullPath(baselineSessionText);
        if (baselinePath is not null && !Directory.Exists(baselinePath))
        {
            ConsoleUi.Failure($"No session directory at {baselinePath}.");
            return InterCatExitCode.InvalidInvocation;
        }

        string? destination = output is null ? null : Path.GetFullPath(output);
        if (destination is not null && File.Exists(destination) && !overwrite)
        {
            ConsoleUi.Failure($"{destination} already exists. Pass --overwrite to replace it.");
            return InterCatExitCode.InvalidInvocation;
        }

        IntervalComparison comparison;
        try
        {
            SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(path));
            comparison = baselinePath is null
                ? SessionComparison.Compare(store, baseline!.Value, compared!.Value, metric!.Value, grouping: grouping!.Value,
                    cancellationToken: cancellationToken)
                : SessionComparison.CompareCaptures(SessionStore.OpenExisting(LocalOwnedDirectory.Open(baselinePath)),
                    baseline!.Value, store, compared!.Value, metric!.Value, cancellationToken: cancellationToken);
        }
        catch (ArgumentException exception) when (exception.Message.StartsWith(SessionComparison.OneSession, StringComparison.Ordinal))
        {
            ConsoleUi.Failure(SessionComparison.OneSession);
            return InterCatExitCode.InvalidInvocation;
        }
        catch (InvalidDataException exception)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }
        catch (InvalidOperationException exception) when (exception is not NoSessionException)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        if (destination is not null)
        {
            // A file holds every row unless --limit says how many: it is read whole, not scrolled.
            int written = limitText is null ? int.MaxValue : limit;
            string content = redacted
                ? RedactedShareExport.Comparison(format!.Value, comparison, DateTimeOffset.UtcNow, written)
                : Document(comparison, path, baselinePath, written);
            await ExportFileWriter.WriteAsync(destination, content, overwrite, cancellationToken).ConfigureAwait(false);
            PrintWritten(comparison, destination, baselinePath, redacted, format!.Value, Math.Min(written, comparison.Relationships.Count));
            return InterCatExitCode.Success;
        }

        if (json)
        {
            Console.Out.WriteLine(Document(comparison, path, baselinePath, limit));
            return InterCatExitCode.Success;
        }

        Print(comparison, path, baselinePath, limit);
        return InterCatExitCode.Success;
    }

    /// <summary>The comparison as `comparison-v1` §8 states it, with the session it was read from, as many rows as the limit.</summary>
    private static string Document(IntervalComparison comparison, string path, string? baselinePath, int limit) =>
        JsonSerializer.Serialize(new
        {
            Contract = "comparison-v1",
            SessionPath = path,
            BaselineSessionPath = baselinePath,
            Comparison = Json(comparison, limit),
        }, JsonContracts.Indented);

    /// <summary>The comparison as `comparison-v1` states it, its rows the most changed first, as many as the limit.</summary>
    private static object Json(IntervalComparison comparison, int limit)
    {
        bool bytes = comparison.Metric == ComparisonMetric.BytesSent;
        return new
        {
            comparison.SessionId,
            comparison.Generation,
            comparison.BaselineSessionId,
            comparison.BaselineGeneration,
            comparison.SameHost,
            comparison.Metric,
            comparison.Policy,
            ByteDomain = bytes ? ByteDomain.TransportObserved : (ByteDomain?)null,
            Accounting = bytes ? AccountingSide.SendSide : (AccountingSide?)null,
            GroupedBy = comparison.Grouping,
            Baseline = new { Interval = comparison.Baseline, Coverage = comparison.BaselineCoverage },
            Compared = new { Interval = comparison.Compared, Coverage = comparison.ComparedCoverage },
            Relationships = comparison.Relationships.Count,
            comparison.Quiet,
            Unmatched = comparison.Grouping == ComparisonGrouping.Executable ? comparison.Unmatched : null,
            Rows = comparison.Relationships.Take(limit).Select(row => new
            {
                row.Key,
                Kind = row switch
                {
                    { Relationship: not null } => "Processes",
                    { Host: not null } => "OtherHost",
                    { Executables: not null } => "Executables",
                    _ => "ExecutableToOtherHost",
                },
                row.Mechanism,
                Source = row switch
                {
                    { Relationship: { } edge } => End(comparison.Processes[edge.SourceId]),
                    { Host: { } host } => End(comparison.Processes[host.Link.Process]),
                    { Executables: { } pair } => Executable(comparison.Groups[pair.Source]),
                    _ => Executable(comparison.Groups[row.ExecutableHost!.Executable]),
                },
                Target = row switch
                {
                    { Relationship: { } edge } => End(comparison.Processes[edge.TargetId]),
                    { Executables: { } pair } => Executable(comparison.Groups[pair.Target]),
                    _ => null,
                },
                Address = row.Host?.Address.Address ?? row.ExecutableHost?.Address,
                Connected = ComparisonText.Connected(row),
                AcceptedPorts = row.Relationship?.AcceptedPorts ?? row.Host?.Address.AcceptedPorts
                    ?? row.Executables?.AcceptedPorts ?? row.ExecutableHost!.AcceptedPorts,
                HostPorts = row.Host?.Address.Ports ?? row.ExecutableHost?.Ports ?? [],
                Baseline = Side(row.Baseline, comparison.Baseline),
                Compared = Side(row.Compared, comparison.Compared),
                row.Change,
                row.FromLowerBound,
                DifferencePerSecond = ComparisonText.DifferencePerSecond(row, comparison),
                row.Reason,
            }),
        };

        static object End(ProcessNode node) => new { Instance = node.Id, node.ProcessId, node.Name };

        // An executable is its group: its key, the name the window gives it, and the path its image was witnessed at.
        static object Executable(ProcessGroup group) => new { Group = group.Key, group.Name, Path = group.Detail };

        static object Side(ComparedMeasure measure, TimeRange interval) => new
        {
            measure.Records,
            measure.Value,
            measure.Measured,
            measure.Unmeasured,
            PerSecond = ComparisonText.Rounded(measure.PerSecond(interval)),
            measure.Coverage,
        };
    }

    /// <summary>
    /// What a file written holds: where it went, its contract, the two intervals and what they were compared by, and how
    /// many of the rows compared it lists; of a redacted report, that it is pseudonymized, not anonymous, and what the
    /// capture covered over each interval, which the report states too.
    /// </summary>
    private static void PrintWritten(IntervalComparison comparison, string destination, string? baselinePath, bool redacted,
        ExportFormat format, int rows)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        ConsoleUi.Heading(redacted ? "Redacted comparison report" : "Comparison");
        ConsoleUi.Field("Written to", destination);
        ConsoleUi.Field("Contract", (redacted ? RedactedShareExport.Contract : "comparison-v1")
            + (format == ExportFormat.Csv ? ", as CSV" : ", as JSON"));
        ConsoleUi.Field("Baseline", ComparisonText.Interval(comparison.Baseline, culture: culture)
            + (baselinePath is null ? string.Empty : " of the baseline's session"));
        if (baselinePath is not null) ConsoleUi.Field("Baseline session", baselinePath);
        ConsoleUi.Field("Compared", ComparisonText.Interval(comparison.Compared, culture: culture));
        ConsoleUi.Field("Compared by", ComparedBy(comparison.Metric));
        ConsoleUi.Field("Grouped by", GroupedBy(comparison.Grouping));
        int all = comparison.Relationships.Count;
        ConsoleUi.Field("Rows", rows == all
            ? ConsoleUi.Count(all) + (all == 1 ? ", the one compared" : ", every one compared")
            : string.Create(culture, $"{rows:N0} of {all:N0} compared, the most changed; --limit {all:N0} writes every one"));
        if (!redacted) return;
        ConsoleUi.Note("Metadata-only pseudonymized report: processes, executables and other hosts are random tokens, and no "
            + "name, PID, address or port is written. Counts, times and patterns can still identify a workload. Review it "
            + "before sharing it.");
        foreach (string? coverage in new[]
        {
            ComparisonText.Coverage("the baseline", comparison.BaselineCoverage),
            ComparisonText.Coverage("the compared interval", comparison.ComparedCoverage),
        })
        {
            if (coverage is not null) ConsoleUi.Note(coverage);
        }
    }

    /// <summary>What a comparison's rows are of, in words: processes, or executables.</summary>
    private static string GroupedBy(ComparisonGrouping grouping) => grouping == ComparisonGrouping.Executable
        ? "executable, each one's processes together by the path their image was witnessed at"
        : "process";

    /// <summary>What a comparison compared each row by, in words.</summary>
    private static string ComparedBy(ComparisonMetric metric) => metric == ComparisonMetric.BytesSent
        ? "bytes sent across per second, each transfer counted once at its sender"
        : "records per second, both ends' records";

    private static void Print(IntervalComparison comparison, string path, string? baselinePath, int limit)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        ConsoleUi.Heading(baselinePath is null ? "Comparison of two intervals" : "Comparison of two captures");
        ConsoleUi.Field(baselinePath is null ? "Session" : "Compared session", path);
        ConsoleUi.Field("Generation", ConsoleUi.Count(comparison.Generation));
        if (baselinePath is not null)
        {
            ConsoleUi.Field("Baseline session", baselinePath);
            ConsoleUi.Field("Its generation", ConsoleUi.Count(comparison.BaselineGeneration!.Value));
        }

        ConsoleUi.Field("Baseline", ComparisonText.Interval(comparison.Baseline, culture: culture)
            + (baselinePath is null ? string.Empty : " of the baseline's session"));
        ConsoleUi.Field("Compared", ComparisonText.Interval(comparison.Compared, culture: culture));
        ConsoleUi.Field("Compared by", ComparedBy(comparison.Metric));
        ConsoleUi.Field("Grouped by", GroupedBy(comparison.Grouping));
        int hosts = comparison.Relationships.Count(row => row.Host is not null || row.ExecutableHost is not null);
        string between = comparison.Grouping == ComparisonGrouping.Executable ? " between executables · " : " between processes · ";
        ConsoleUi.Field("Rows", ConsoleUi.Count(comparison.Relationships.Count - hosts) + between
            + ConsoleUi.Count(hosts) + " to other hosts, with records in either interval"
            + (comparison.Quiet > 0 ? " · " + ConsoleUi.Count(comparison.Quiet) + " quiet in both" : string.Empty));
        foreach (string? coverage in new[]
        {
            ComparisonText.Coverage("the baseline", comparison.BaselineCoverage),
            ComparisonText.Coverage("the compared interval", comparison.ComparedCoverage),
        })
        {
            if (coverage is not null) ConsoleUi.Note(coverage);
        }

        if (comparison.Relationships.Count == 0)
        {
            ConsoleUi.Note("No relationship the overview draws, and no process's connections to another host, has a record in "
                + "either interval, so none is compared.");
            foreach (string grouped in ComparisonText.Grouping(comparison, culture)) ConsoleUi.Note(grouped);
            return;
        }

        // Each reason two values cannot be compared is said once, beneath the table, and each row names it by its number.
        var reasons = new List<string>();
        ComparedRelationship[] listed = [.. comparison.Relationships.Take(limit)];
        ConsoleUi.Table(
            ["Relationship", "Over", "Baseline", "Compared", "Change"],
            [.. listed.Select(row => (IReadOnlyList<string>)
            [
                ComparisonText.Ends(row, comparison, "and"),
                ComparisonText.Over(row),
                ComparisonText.Value(row.Baseline, comparison.Baseline, comparison.Metric, culture),
                ComparisonText.Value(row.Compared, comparison.Compared, comparison.Metric, culture),
                ComparisonText.Change(row, comparison, culture) + Numbered(row, reasons, culture),
            ])]);
        for (int index = 0; index < reasons.Count; index++)
        {
            ConsoleUi.Note(string.Create(culture, $"[{index + 1}] {reasons[index]}"));
        }

        if (comparison.Relationships.Count > listed.Length)
        {
            ConsoleUi.Note(string.Create(culture, $"{comparison.Relationships.Count - listed.Length:N0} more, less changed; "
                + $"--limit {comparison.Relationships.Count:N0} lists every one."));
        }

        foreach (string note in ComparisonText.Notes(listed, comparison.Metric).Concat(ComparisonText.Grouping(comparison, culture)))
        {
            ConsoleUi.Note(note);
        }

        ConsoleUi.Note("icat compare <directory> --json returns each row with both intervals' coverage.");
    }

    /// <summary>
    /// The number a row whose values cannot be compared names its reason by, " [1]", said once beneath the table; nothing
    /// for a row whose change is stated.
    /// </summary>
    private static string Numbered(ComparedRelationship row, List<string> reasons, CultureInfo culture)
    {
        if (row.Change != ComparisonChange.NotComparable)
        {
            return string.Empty;
        }

        string reason = row.Reason ?? "its two values are not of the same measurement";
        int number = reasons.IndexOf(reason) + 1;
        if (number == 0)
        {
            reasons.Add(reason);
            number = reasons.Count;
        }

        return string.Create(culture, $" [{number}]");
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat compare <session-directory> --baseline <start:end> --interval <start:end>");
        ConsoleUi.Line("    [--rank-by records|bytes-sent] [--group-by process|executable] [--limit <n>] [--json]");
        ConsoleUi.Line("    [--baseline-session <session-directory>]");
        ConsoleUi.Line("    [--output <path> [--share-redacted] [--format json|csv] [--overwrite]]");
        ConsoleUi.Line("  Read-only comparison of two intervals of a session, relationship by relationship: how each one's");
        ConsoleUi.Line("  records, or the bytes its two ends sent across it, changed from the baseline to the interval, as");
        ConsoleUi.Line("  rates over each whole interval, the most changed first. Each bound is in 100-nanosecond session");
        ConsoleUi.Line("  ticks or a time with its unit (1.5s, 250ms, 40us), and the intervals may differ in length.");
        ConsoleUi.Line("  A change is stated only where both intervals covered the relationship's mechanism through the");
        ConsoleUi.Line("  same sources; elsewhere both values are shown with why they cannot be compared, so a mechanism");
        ConsoleUi.Line("  the capture did not collect in one interval never reads as activity that stopped. Sends that");
        ConsoleUi.Line("  recorded no size make a lower bound, which states a change only where it alone decides it.");
        ConsoleUi.Line("  --group-by executable compares executables rather than processes: each one's processes");
        ConsoleUi.Line("  together, by the path their image was witnessed at, so a process that restarted is one row;");
        ConsoleUi.Line("  processes whose executable was not witnessed are in no row, and are counted apart.");
        ConsoleUi.Line("  --baseline-session reads the baseline from another capture, and compares the two captures by");
        ConsoleUi.Line("  executable, each interval in its own session's time: the processes each ran from one path,");
        ConsoleUi.Line("  paired by that path - a grouping, never an identity, since the file at it may differ.");
        ConsoleUi.Line($"  --limit lists at most that many, {DefaultLimit} by default; a file written with --output holds");
        ConsoleUi.Line("  every row unless --limit is given. --output writes the --json document to a file, complete");
        ConsoleUi.Line("  before it replaces anything; with --share-redacted it writes a metadata-only, pseudonymized");
        ConsoleUi.Line("  report to share instead, as JSON or, with --format csv or a .csv path, CSV: processes,");
        ConsoleUi.Line("  executables and other hosts become random tokens, and no name, PID, address or port is kept.");
    }
}
