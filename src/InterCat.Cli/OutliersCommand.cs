using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>
/// An interval's rows judged against their baseline's windows (`contracts/outliers-v1.md`, plan M12's optional outlier
/// detection): each relationship's records over the interval set beside the records it held in each window of its
/// baseline, every window as long as the interval, and those above or below every window listed, the most records outside
/// their windows' range first, with the baseline, its windows and how often chance alone would put a row there.
/// </summary>
internal static class OutliersCommand
{
    /// <summary>The document --json writes.</summary>
    internal const string Contract = "outliers-v1";

    private const int DefaultLimit = 20;

    private static readonly string[] Columns = ["Relationship", "Over", "Compared", "Its windows", "Beyond", "Observed"];

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
        string? baselineText = command.TakeOption("--baseline");
        string? groupByText = command.TakeOption("--group-by");
        string? policyText = command.TakeOption("--evidence-policy");
        string? limitText = command.TakeOption("--limit");
        bool json = command.TryTakeFlag("--json");
        string? directory = command.TakePositional();
        bool hasUnknown = command.TryReportUnknown(out string? unknown);
        TimeRange? compared = TickInterval.Read(intervalText, out string? comparedTooWide);
        TimeRange? named = TickInterval.Read(baselineText, out string? baselineTooWide);
        ComparisonGrouping? grouping = groupByText switch
        {
            null or "process" => ComparisonGrouping.Process,
            "executable" => ComparisonGrouping.Executable,
            _ => null,
        };
        int limit = DefaultLimit;
        bool invalidLimit = limitText is not null
            && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1);
        string? problem = directory is null ? "A session directory is required: icat outliers <directory>."
            : hasUnknown ? command.Refusal(unknown!)
            : compared is null ? comparedTooWide ?? "--interval is required: " + TickInterval.Takes + "."
            : baselineText is not null && named is null
                ? baselineTooWide?.Replace("--interval", "--baseline", StringComparison.Ordinal)
                    ?? "--baseline names no interval: " + TickInterval.Takes + "."
            : grouping is null ? $"--group-by judges process or executable rows; '{groupByText}' is neither."
            : invalidLimit ? $"--limit expects a number of rows of 1 or more; '{limitText}' is not one."
            : null;
        if (problem is not null)
        {
            ConsoleUi.Failure(problem);
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        if (!EvidencePolicyOption.TryParse(policyText, out EvidencePolicy policy, out string? refusal))
        {
            ConsoleUi.Failure(refusal!);
            return InterCatExitCode.InvalidInvocation;
        }

        string path = Path.GetFullPath(directory!);
        if (!Directory.Exists(path))
        {
            ConsoleUi.Failure($"No session directory at {path}.");
            return InterCatExitCode.InvalidInvocation;
        }

        OutlierReading reading;
        try
        {
            SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(path));

            // Without --baseline, everything the session keeps before the interval is its baseline. Reading where that starts
            // reads the session's manifest, so a folder holding no session says so before anything else is judged.
            TimeRange? before = ComparisonOutliers.Before(store, compared!.Value);
            if ((named ?? before) is not { } baseline)
            {
                ConsoleUi.Failure("Nothing the session keeps is before --interval, so it has no baseline unless --baseline names one.");
                return InterCatExitCode.InvalidInvocation;
            }

            if (ComparisonOutliers.Windows(baseline, compared.Value, out string? unwindowed, CultureInfo.CurrentCulture) is null)
            {
                ConsoleUi.Failure(unwindowed!);
                return InterCatExitCode.InvalidInvocation;
            }

            ConsoleUi.Progress("Projecting the current generation, then reading the interval, its baseline and each of the baseline's windows.");
            reading = ComparisonOutliers.Read(store, baseline, compared.Value, policy, grouping!.Value, cancellationToken);
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

        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(Document(reading, path, limit), JsonContracts.Indented));
            return InterCatExitCode.Success;
        }

        Print(reading, path, limit);
        return InterCatExitCode.Success;
    }

    private static object Document(OutlierReading reading, string path, int limit)
    {
        IntervalComparison comparison = reading.Comparison;
        IReadOnlyList<OutlierJudgement> outliers = reading.Outliers;
        return new
        {
            Contract,
            SessionPath = path,
            comparison.SessionId,
            comparison.Generation,
            EvidencePolicy = comparison.Policy.ToString(),
            GroupedBy = comparison.Grouping.ToString(),
            Baseline = new { Interval = comparison.Baseline, Coverage = comparison.BaselineCoverage },
            Compared = new { Interval = comparison.Compared, Coverage = comparison.ComparedCoverage },
            Windows = reading.Windows,
            reading.Chance,
            Rows = comparison.Relationships.Count,
            Judged = reading.Judged.Count,
            Above = reading.Judged.Count(row => row.Side == OutlierSide.Above),
            Below = reading.Judged.Count(row => row.Side == OutlierSide.Below),
            NotJudged = reading.NotJudged
                .GroupBy(row => row.Reason, StringComparer.Ordinal)
                .OrderByDescending(reason => reason.Count())
                .ThenBy(reason => reason.Key, StringComparer.Ordinal)
                .Select(reason => new { Reason = reason.Key, Rows = reason.Count() }),
            Outliers = outliers.Count,
            Listed = outliers.Take(limit).Select((judged, index) => new
            {
                Rank = index + 1,
                judged.Row.Key,
                Kind = judged.Row switch
                {
                    { Relationship: not null } => "Processes",
                    { Host: not null } => "OtherHost",
                    { Executables: not null } => "Executables",
                    _ => "ExecutableToOtherHost",
                },
                judged.Row.Mechanism,
                Source = judged.Row switch
                {
                    { Relationship: { } edge } => End(comparison.Processes[edge.SourceId]),
                    { Host: { } host } => End(comparison.Processes[host.Link.Process]),
                    { Executables: { } pair } => Executable(comparison.Groups[pair.Source]),
                    _ => Executable(comparison.Groups[judged.Row.ExecutableHost!.Executable]),
                },
                Target = judged.Row switch
                {
                    { Relationship: { } edge } => End(comparison.Processes[edge.TargetId]),
                    { Executables: { } pair } => Executable(comparison.Groups[pair.Target]),
                    _ => null,
                },
                Address = judged.Row.Host?.Address.Address ?? judged.Row.ExecutableHost?.Address,
                Connected = ComparisonText.Connected(judged.Row),
                Side = judged.Side.ToString(),
                ComparedRecords = judged.Row.Compared.Records,
                BaselineRecords = judged.Row.Baseline.Records,
                WindowRecords = judged.Windows,
                judged.Fewest,
                judged.Median,
                judged.Most,
                judged.Beyond,
            }),
        };

        static object End(ProcessNode node) => new { Instance = node.Id, node.ProcessId, node.Name };

        // An executable is its group: its key, the name the window gives it, and the path its image was witnessed at.
        static object Executable(ProcessGroup group) => new { Group = group.Key, group.Name, Path = group.Detail };
    }

    private static void Print(OutlierReading reading, string path, int limit)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        IntervalComparison comparison = reading.Comparison;
        ConsoleUi.Heading("Outliers");
        ConsoleUi.Field("Session", path);
        ConsoleUi.Field("Generation", ConsoleUi.Count(comparison.Generation));
        ConsoleUi.Field("Compared", ComparisonText.Interval(comparison.Compared, culture: culture));
        ConsoleUi.Field("Baseline", OutlierText.Baseline(reading, culture: culture));
        ConsoleUi.Field("Judged by", "records, both ends' records, " + (comparison.Grouping == ComparisonGrouping.Executable
            ? "a row per executable, each one's processes together by the path their image was witnessed at"
            : "a row per process"));
        ConsoleUi.Field("Rows", OutlierText.Found(reading, culture));
        foreach (string? coverage in new[]
        {
            ComparisonText.Coverage("the baseline", comparison.BaselineCoverage),
            ComparisonText.Coverage("the compared interval", comparison.ComparedCoverage),
        })
        {
            if (coverage is not null) ConsoleUi.Note(coverage);
        }

        IReadOnlyList<OutlierJudgement> outliers = reading.Outliers;
        if (comparison.Relationships.Count == 0)
        {
            ConsoleUi.Note(OutlierText.NoRow(comparison));
        }
        else if (outliers.Count == 0)
        {
            ConsoleUi.Note(reading.Judged.Count == 0 ? OutlierText.NoneJudged : OutlierText.NoneOutside);
        }
        else
        {
            ConsoleUi.Table(Columns, [.. outliers.Take(limit).Select(judged => (IReadOnlyList<string>)
            [
                ComparisonText.Ends(judged.Row, comparison, "and"),
                ComparisonText.Over(judged.Row),
                judged.Row.Compared.Records.ToString("N0", culture),
                OutlierText.Windows(judged, culture),
                OutlierText.Beyond(judged, culture),
                OutlierText.Side(judged.Side),
            ])]);
            if (outliers.Count > limit)
            {
                ConsoleUi.Note(string.Create(culture,
                    $"{outliers.Count - limit:N0} more, fewer records outside their windows' range; --limit {outliers.Count:N0} lists every one."));
            }
        }

        if (OutlierText.Chance(reading, culture) is { } chance) ConsoleUi.Note(chance);
        foreach (string notJudged in OutlierText.NotJudged(reading, culture)) ConsoleUi.Note(notJudged);
        foreach (string grouped in ComparisonText.Grouping(comparison, culture)) ConsoleUi.Note(grouped);
        ConsoleUi.Note(OutlierText.Note);
        ConsoleUi.Note("icat outliers <directory> --json returns each row listed with its records in every window.");
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat outliers <session-directory> --interval <start:end> [--baseline <start:end>]");
        ConsoleUi.Line("              [--group-by process|executable] [--evidence-policy <policy>] [--limit <n>] [--json]");
        ConsoleUi.Line("  Sets each relationship's records over --interval beside the records it held in each window of its");
        ConsoleUi.Line("  baseline, every window as long as the interval: as many as fit, 19 to 199, spread evenly across it.");
        ConsoleUi.Line("  Lists the rows above or below every window, the most records outside their windows' range first.");
        ConsoleUi.Line("  Chance alone puts a row above every window at most 1 time in one more than the windows, and below");
        ConsoleUi.Line("  every one as rarely; nothing is said of why a row differs.");
        ConsoleUi.Line("  --baseline is everything the session keeps before the interval unless it names another, which must");
        ConsoleUi.Line("  not overlap it; --group-by executable judges each executable's processes together.");
        ConsoleUi.Line("  --evidence-policy takes a policy as written or in kebab form; --json writes outliers-v1.");
    }
}
