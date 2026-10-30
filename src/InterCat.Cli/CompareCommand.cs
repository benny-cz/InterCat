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

    /// <summary>How many columns a row's difference timeline cuts each interval into unless --columns says.</summary>
    private const int DefaultColumns = 10;

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
        string? workspaceText = command.TakeOption("--workspace");
        string? savedText = command.TakeOption("--saved");
        string? timelineText = command.TakeOption("--timeline");
        string? columnsText = command.TakeOption("--columns");
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
        int rank = 0;
        bool invalidRank = timelineText is not null
            && (!int.TryParse(timelineText, NumberStyles.None, CultureInfo.InvariantCulture, out rank) || rank < 1);
        int columns = DefaultColumns;
        bool invalidColumns = columnsText is not null
            && (!int.TryParse(columnsText, NumberStyles.None, CultureInfo.InvariantCulture, out columns) || columns < 1
                || columns > SessionComparison.MostTimelineColumns);
        ExportFormat? format = formatText switch
        {
            null => output?.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) == true ? ExportFormat.Csv : ExportFormat.Json,
            "json" => ExportFormat.Json,
            "csv" => ExportFormat.Csv,
            _ => null,
        };
        string? fileOnly = redacted ? "--share-redacted" : formatText is not null ? "--format" : overwrite ? "--overwrite" : null;

        // A saved comparison names its sessions, intervals, ranking and grouping; only how it is shown is chosen here.
        string? notSaved = directory is not null ? "session directory" : baselineText is not null ? "--baseline"
            : intervalText is not null ? "--interval" : rankByText is not null ? "--rank-by" : groupByText is not null ? "--group-by"
            : baselineSessionText is not null ? "--baseline-session" : null;
        string? problem = savedText is not null || workspaceText is not null
            ? hasUnknown ? command.Refusal(unknown!)
                : savedText is null ? "--workspace names the investigation a comparison was saved in; pass --saved <name> with it."
                : workspaceText is null ? "--saved names a comparison saved in an investigation; pass --workspace <workspace> with it."
                : notSaved is not null ? $"--saved runs the comparison as it was saved, its sessions and intervals with it; pass no {notSaved} with it."
                : null
            : directory is null ? "A session directory is required: icat compare <directory>."
            : hasUnknown ? command.Refusal(unknown!)
            : baseline is null ? baselineTooWide?.Replace("--interval", "--baseline", StringComparison.Ordinal)
                ?? CommandLine.Expects("--baseline", baselineText, TickInterval.Takes)
            : compared is null ? comparedTooWide ?? CommandLine.Expects("--interval", intervalText, TickInterval.Takes)
            : metric is null ? $"--rank-by compares records or bytes-sent; '{rankByText}' is neither."
            : grouping is null ? $"--group-by compares process or executable rows; '{groupByText}' is neither."
            : baselineSessionText is not null && grouping == ComparisonGrouping.Process
                ? "--baseline-session compares two captures by executable: a process of one capture is never another's."
            : null;
        problem ??= invalidLimit ? $"--limit expects a number of relationships of 1 or more; '{limitText}' is not one."
            : invalidRank ? $"--timeline names a row by its rank, 1 for the most changed; '{timelineText}' is not one."
            : columnsText is not null && timelineText is null
                ? "--columns cuts a row's difference timeline, which --timeline <rank> asks for."
            : invalidColumns
                ? $"--columns cuts each interval into 1 to {SessionComparison.MostTimelineColumns} columns; '{columnsText}' is not one."
            : timelineText is not null && limitText is not null ? "--limit lists rows and --timeline one row's columns; pass one of them."
            : timelineText is not null && redacted
                ? "A redacted report holds each row over the whole intervals, never a row's timeline; pass one of --timeline and "
                    + "--share-redacted."
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

        string path;
        string? baselinePath;
        SavedRun? saved = null;
        if (savedText is not null)
        {
            // An investigation's file that cannot be read is refused as corrupted input, as icat workspace refuses it.
            try
            {
                saved = SavedRun.Read(Path.GetFullPath(workspaceText!), savedText, cancellationToken);
            }
            catch (InvalidOperationException exception)
            {
                ConsoleUi.Failure(exception.Message);
                return InterCatExitCode.InvalidInvocation;
            }

            (path, baselinePath) = (saved.Path, saved.BaselinePath);
            (baseline, compared) = (saved.Comparison.Baseline!.Interval, saved.Comparison.Compared!.Interval);
            (metric, grouping) = (saved.Comparison.Metric, saved.Comparison.GroupedBy);
        }
        else
        {
            path = Path.GetFullPath(directory!);
            if (!Directory.Exists(path))
            {
                ConsoleUi.Failure($"No session directory at {path}.");
                return InterCatExitCode.InvalidInvocation;
            }

            baselinePath = baselineSessionText is null ? null : Path.GetFullPath(baselineSessionText);
            if (baselinePath is not null && !Directory.Exists(baselinePath))
            {
                ConsoleUi.Failure($"No session directory at {baselinePath}.");
                return InterCatExitCode.InvalidInvocation;
            }
        }

        string? destination = output is null ? null : Path.GetFullPath(output);
        if (OutputFiles.Refusal(destination, overwrite) is { } refusedOutput)
        {
            ConsoleUi.Failure(refusedOutput);
            return InterCatExitCode.InvalidInvocation;
        }

        IntervalComparison comparison;
        ComparisonTimeline? timeline = null;
        try
        {
            SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(path));
            SessionStore? baselineStore = baselinePath is null ? null : SessionStore.OpenExisting(LocalOwnedDirectory.Open(baselinePath));

            // A row's difference timeline is read with the comparison, at its generation, the rows to its rank kept.
            if (timelineText is not null)
            {
                timeline = baselineStore is null
                    ? SessionComparison.Timeline(store, baseline!.Value, compared!.Value, columns, metric!.Value,
                        grouping: grouping!.Value, listed: rank, cancellationToken: cancellationToken)
                    : SessionComparison.CaptureTimeline(baselineStore, baseline!.Value, store, compared!.Value, columns, metric!.Value,
                        listed: rank, cancellationToken: cancellationToken);
                comparison = timeline.Whole;
            }
            else
            {
                comparison = baselineStore is null
                    ? SessionComparison.Compare(store, baseline!.Value, compared!.Value, metric!.Value, grouping: grouping!.Value,
                        cancellationToken: cancellationToken)
                    : SessionComparison.CompareCaptures(baselineStore, baseline!.Value, store, compared!.Value, metric!.Value,
                        cancellationToken: cancellationToken);
            }
        }
        catch (ArgumentException exception) when (exception.Message.StartsWith(SessionComparison.OneSession, StringComparison.Ordinal))
        {
            ConsoleUi.Failure(SessionComparison.OneSession);
            return InterCatExitCode.InvalidInvocation;
        }
        catch (ArgumentException exception) when (exception.Message.StartsWith(SessionComparison.ColumnsPastTicks, StringComparison.Ordinal))
        {
            ConsoleUi.Failure(SessionComparison.ColumnsPastTicks);
            return InterCatExitCode.InvalidInvocation;
        }
        catch (InvalidOperationException exception) when (exception is not NoSessionException)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        // The row a timeline is asked of must be one the comparison ranks.
        ComparedRelationship? timed = null;
        if (timeline is not null)
        {
            if (rank > comparison.Relationships.Count)
            {
                ConsoleUi.Failure($"--timeline {rank} names no row: the comparison compares "
                    + $"{CountText.Of(comparison.Relationships.Count, "row")}.");
                return InterCatExitCode.InvalidInvocation;
            }

            timed = comparison.Relationships[rank - 1];
        }

        if (destination is not null)
        {
            // A file holds every row unless --limit says how many: it is read whole, not scrolled.
            int written = limitText is null ? int.MaxValue : limit;
            string content = redacted
                ? RedactedShareExport.Comparison(format!.Value, comparison, DateTimeOffset.UtcNow, written)
                : Document(comparison, path, baselinePath, saved, written, timeline, timed, rank);
            await ExportFileWriter.WriteAsync(destination, content, overwrite, cancellationToken).ConfigureAwait(false);
            PrintWritten(comparison, destination, baselinePath, saved, redacted, format!.Value,
                Math.Min(written, comparison.Relationships.Count));
            if (timeline is not null) ConsoleUi.Field("Timeline", $"of row {rank}, " + Columned(timeline, CultureInfo.CurrentCulture));
            return InterCatExitCode.Success;
        }

        if (json)
        {
            Console.Out.WriteLine(Document(comparison, path, baselinePath, saved, limit, timeline, timed, rank));
            return InterCatExitCode.Success;
        }

        if (timeline is not null)
        {
            PrintTimeline(timeline, timed!, rank, path, baselinePath, saved);
            return InterCatExitCode.Success;
        }

        Print(comparison, path, baselinePath, saved, limit);
        return InterCatExitCode.Success;
    }

    /// <summary>What a difference timeline's columns are, said beneath them in place of what the whole intervals' rates are.</summary>
    internal const string TimelineNote = "Each column compares the baseline's share with the compared interval's share in the "
        + "same place, as two intervals are compared: its rates are over the column, and a change is stated only where both "
        + "columns covered the row's mechanism through the same sources, so a column the capture did not collect never reads "
        + "as activity that stopped or started; a reason speaks of the column's share of each interval. The columns add up to "
        + "the whole intervals.";

    /// <summary>
    /// How a difference timeline cuts the intervals: "10 columns of each interval, 1.0 s of the baseline and 2.0 s of the
    /// compared interval each".
    /// </summary>
    private static string Columned(ComparisonTimeline timeline, CultureInfo culture)
    {
        int count = timeline.Columns.Count;
        return CountText.Of(count, "column") + " of each interval, "
            + WorkspaceTime.FormatDuration(timeline.Whole.Baseline.SpanTicks / count, culture) + " of the baseline and "
            + WorkspaceTime.FormatDuration(timeline.Whole.Compared.SpanTicks / count, culture) + " of the compared interval"
            + (count == 1 ? string.Empty : " each");
    }

    /// <summary>
    /// A row's difference timeline (`comparison-v1` §8d): what was compared, as the comparison says it, the row and how its
    /// whole intervals compare, then each column pair - the k-th of the baseline beside the k-th of the compared interval -
    /// with its values and change, each reason once beneath them.
    /// </summary>
    private static void PrintTimeline(ComparisonTimeline timeline, ComparedRelationship row, int rank, string path,
        string? baselinePath, SavedRun? saved)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        IntervalComparison comparison = timeline.Whole;
        ConsoleUi.Heading("Difference timeline");
        if (saved is not null) ConsoleUi.Field("Saved as", saved.Describe(culture));
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
        ConsoleUi.Field("Row", string.Create(culture, $"{rank:N0} of {comparison.Relationships.Count:N0}, ")
            + ComparisonText.Ends(row, comparison, "and") + " over " + ComparisonText.Over(row));
        ConsoleUi.Field("Whole intervals", ComparisonText.Value(row.Baseline, comparison.Baseline, comparison.Metric, culture) + " → "
            + ComparisonText.Value(row.Compared, comparison.Compared, comparison.Metric, culture) + ", "
            + ComparisonText.Change(row, comparison, culture));
        ConsoleUi.Field("Columns", Columned(timeline, culture));

        // Each reason two columns' values cannot be compared is said once, beneath the table, and each column names it.
        var reasons = new List<string>();
        IReadOnlyList<ComparedRelationship> cells = timeline.Of(row);
        ConsoleUi.Table(
            ["Column", "Baseline", "Value", "Compared", "Value", "Change"],
            [.. cells.Select((cell, index) =>
            {
                ComparisonColumn column = timeline.Columns[index];
                return (IReadOnlyList<string>)
                [
                    string.Create(culture, $"{index + 1:N0}"),
                    WorkspaceTime.FormatHalfOpenRange(column.Baseline, culture),
                    ComparisonText.Value(cell.Baseline, column.Baseline, comparison.Metric, culture),
                    WorkspaceTime.FormatHalfOpenRange(column.Compared, culture),
                    ComparisonText.Value(cell.Compared, column.Compared, comparison.Metric, culture),
                    ComparisonText.Change(cell, column.Baseline, column.Compared, comparison.Metric, culture) + Numbered(cell, reasons, culture),
                ];
            })]);
        for (int index = 0; index < reasons.Count; index++)
        {
            ConsoleUi.Note(string.Create(culture, $"[{index + 1}] {reasons[index]}"));
        }

        ConsoleUi.Note(TimelineNote);
        foreach (string note in ComparisonText.Notes([row], comparison.Metric).Where(note => note != ComparisonText.RatesNote)
            .Concat(ComparisonText.Grouping(comparison, culture)))
        {
            ConsoleUi.Note(note);
        }
    }

    /// <summary>
    /// The comparison as `comparison-v1` §8 states it, with the session it was read from and the saved comparison it ran, as
    /// many rows as the limit, and a row's difference timeline where one was asked for (§8d).
    /// </summary>
    private static string Document(IntervalComparison comparison, string path, string? baselinePath, SavedRun? saved, int limit,
        ComparisonTimeline? timeline = null, ComparedRelationship? timed = null, int rank = 0) =>
        JsonSerializer.Serialize(new
        {
            Contract = "comparison-v1",
            SessionPath = path,
            BaselineSessionPath = baselinePath,
            Saved = saved is null ? null : new
            {
                Workspace = saved.WorkspacePath,
                saved.Comparison.Name,
                saved.Comparison.Revision,
                Notes = saved.Notes.Select(note => new
                {
                    note.NoteId,
                    note.Text,
                    Row = note.About!.Row,
                    Rank = RankOf(comparison, note),
                }),
            },
            Comparison = Json(comparison, limit),
            Timeline = timeline is null || timed is null ? null : new
            {
                Rank = rank,
                timed.Key,
                Columns = timeline.Of(timed).Select((cell, index) => new
                {
                    BaselineInterval = timeline.Columns[index].Baseline,
                    ComparedInterval = timeline.Columns[index].Compared,
                    Baseline = Side(cell.Baseline, timeline.Columns[index].Baseline),
                    Compared = Side(cell.Compared, timeline.Columns[index].Compared),
                    cell.Change,
                    cell.FromLowerBound,
                    DifferencePerSecond = ComparisonText.DifferencePerSecond(cell, timeline.Columns[index].Baseline,
                        timeline.Columns[index].Compared),
                    cell.Reason,
                }),
            },
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
    }

    /// <summary>A row's measure over one interval, or one column of it, as `comparison-v1` §8 states it.</summary>
    private static object Side(ComparedMeasure measure, TimeRange interval) => new
    {
        measure.Records,
        measure.Value,
        measure.Measured,
        measure.Unmeasured,
        PerSecond = ComparisonText.Rounded(measure.PerSecond(interval)),
        measure.Coverage,
    };

    /// <summary>
    /// What a file written holds: where it went, its contract, the two intervals and what they were compared by, and how
    /// many of the rows compared it lists; of a redacted report, that it is pseudonymized, not anonymous, and what the
    /// capture covered over each interval, which the report states too.
    /// </summary>
    private static void PrintWritten(IntervalComparison comparison, string destination, string? baselinePath, SavedRun? saved,
        bool redacted, ExportFormat format, int rows)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        ConsoleUi.Heading(redacted ? "Redacted comparison report" : "Comparison");
        ConsoleUi.Field("Written to", destination);
        ConsoleUi.Field("Contract", (redacted ? RedactedShareExport.Contract : "comparison-v1")
            + (format == ExportFormat.Csv ? ", as CSV" : ", as JSON"));
        if (saved is not null) ConsoleUi.Field("Saved as", saved.Describe(culture));
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

    private static void Print(IntervalComparison comparison, string path, string? baselinePath, SavedRun? saved, int limit)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        ConsoleUi.Heading(baselinePath is null ? "Comparison of two intervals" : "Comparison of two captures");
        if (saved is not null) ConsoleUi.Field("Saved as", saved.Describe(culture));
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

        if (saved is { Notes.Count: > 0 })
        {
            // The notes a person wrote about the comparison and its rows, each row's named by its ends as this run ranks it.
            ConsoleUi.Line();
            ConsoleUi.Heading("Notes on this comparison");
            foreach (WorkspaceNote note in saved.Notes)
            {
                string id = note.NoteId.ToString("N")[..8];
                ConsoleUi.Note(note.About!.Row is not { } key ? $"{id}: {note.Text}"
                    : RankOf(comparison, note) is { } rank
                        ? string.Create(culture, $"{id}, about row {rank:N0}, ")
                            + ComparisonText.Ends(comparison.Relationships[rank - 1], comparison, "and") + $": {note.Text}"
                        : $"{id}, about row {key}, which this run does not compare: {note.Text}");
            }
        }

        ConsoleUi.Note((saved is null ? "icat compare <directory>" : "icat compare --workspace <workspace> --saved <name>")
            + " --json returns each row with both intervals' coverage.");
    }

    /// <summary>The rank a note's row has in a run of its comparison, the most changed 1; null where the run does not compare it.</summary>
    private static int? RankOf(IntervalComparison comparison, WorkspaceNote note)
    {
        int index = note.About?.Row is { } key
            ? comparison.Relationships.ToList().FindIndex(row => string.Equals(row.Key, key, StringComparison.Ordinal))
            : -1;
        return index < 0 ? null : index + 1;
    }

    /// <summary>
    /// A saved comparison about to run (`icat compare --saved`): the investigation it was saved in, the revision in force
    /// under its name, and the directories its sessions are found at now.
    /// </summary>
    private sealed record SavedRun(string WorkspacePath, SavedComparison Comparison, string Path, string? BaselinePath)
    {
        /// <summary>The notes in force about the comparison and its rows, in the order first written (`workspace-v23` §7).</summary>
        public IReadOnlyList<WorkspaceNote> Notes { get; init; } = [];

        /// <summary>
        /// The comparison in force under <paramref name="name"/> in the investigation at <paramref name="workspacePath"/>,
        /// its sessions where the investigation finds them; refused in words when it, or a session of it, is not found.
        /// </summary>
        public static SavedRun Read(string workspacePath, string name, CancellationToken cancellationToken)
        {
            InvestigationWorkspaceFile workspace = InvestigationWorkspace.Read(workspacePath);
            SavedComparison comparison = InvestigationWorkspace.ComparisonNamed(workspace, name);
            IReadOnlyList<WorkspaceMemberResolution> members = InvestigationWorkspace.Resolve(workspacePath, workspace, cancellationToken);
            string Found(Guid session, string role) =>
                members.First(member => member.Member.SessionId == session) is { HoldsItsCapture: true } found
                    ? found.FullPath
                    : throw new InvalidOperationException($"The comparison's {role} session {session.ToString("N")[..8]} is not "
                        + $"where the investigation recorded it: {members.First(member => member.Member.SessionId == session).Reason}");
            return new(workspacePath, comparison, Found(comparison.Compared!.SessionId, "compared"),
                comparison.AcrossCaptures ? Found(comparison.Baseline!.SessionId, "baseline") : null)
            {
                Notes = InvestigationWorkspace.NotesAbout(workspace, comparison.Name),
            };
        }

        /// <summary>"'Before the update', comparison revision 3 of C:\cases\update.icatws".</summary>
        public string Describe(IFormatProvider culture) =>
            string.Create(culture, $"'{Comparison.Name}', comparison revision {Comparison.Revision:N0} of {WorkspacePath}");
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
        ConsoleUi.Line("    [--timeline <rank> [--columns <1-100>]]");
        ConsoleUi.Line("icat compare --workspace <workspace> --saved <name> [--limit <n>] [--json]");
        ConsoleUi.Line("    [--output <path> [--share-redacted] [--format json|csv] [--overwrite]]");
        ConsoleUi.Line("    [--timeline <rank> [--columns <1-100>]]");
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
        ConsoleUi.Line("  --saved runs a comparison saved in an investigation (icat workspace comparison): its sessions,");
        ConsoleUi.Line("  intervals, ranking and grouping as saved, each session read where the investigation finds it");
        ConsoleUi.Line("  now, at the generation it holds then.");
        ConsoleUi.Line("  --timeline shows one row's difference timeline instead of the table, the row by its rank, 1 for");
        ConsoleUi.Line($"  the most changed: each interval cut into --columns equal columns, {DefaultColumns} by default, the");
        ConsoleUi.Line("  baseline's k-th compared with the compared interval's k-th as two intervals are, each column");
        ConsoleUi.Line("  judged by what the capture covered there; read with the comparison, its columns add up to it.");
        ConsoleUi.Line($"  --limit lists at most that many, {DefaultLimit} by default; a file written with --output holds");
        ConsoleUi.Line("  every row unless --limit is given. --output writes the --json document to a file, complete");
        ConsoleUi.Line("  before it replaces anything; with --share-redacted it writes a metadata-only, pseudonymized");
        ConsoleUi.Line("  report to share instead, as JSON or, with --format csv or a .csv path, CSV: processes,");
        ConsoleUi.Line("  executables and other hosts become random tokens, and no name, PID, address or port is kept.");
    }
}
