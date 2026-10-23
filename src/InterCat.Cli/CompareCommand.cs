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

    public static Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken) =>
        Task.FromResult(Run(command, cancellationToken));

    private static InterCatExitCode Run(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? baselineText = command.TakeOption("--baseline");
        string? intervalText = command.TakeOption("--interval");
        string? rankByText = command.TakeOption("--rank-by");
        string? limitText = command.TakeOption("--limit");
        bool json = command.TryTakeFlag("--json");
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
        int limit = DefaultLimit;
        bool invalidLimit = limitText is not null
            && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1);
        if (directory is null || hasUnknown || baseline is null || compared is null || metric is null || invalidLimit)
        {
            ConsoleUi.Failure(directory is null ? "A session directory is required: icat compare <directory>."
                : hasUnknown ? command.Refusal(unknown!)
                : baseline is null ? baselineTooWide?.Replace("--interval", "--baseline", StringComparison.Ordinal)
                    ?? "--baseline is required: " + TickInterval.Takes + "."
                : compared is null ? comparedTooWide ?? "--interval is required: " + TickInterval.Takes + "."
                : metric is null ? $"--rank-by compares records or bytes-sent; '{rankByText}' is neither."
                : $"--limit expects a number of relationships of 1 or more; '{limitText}' is not one.");
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        string path = Path.GetFullPath(directory);
        if (!Directory.Exists(path))
        {
            ConsoleUi.Failure($"No session directory at {path}.");
            return InterCatExitCode.InvalidInvocation;
        }

        IntervalComparison comparison;
        try
        {
            comparison = SessionComparison.Compare(SessionStore.OpenExisting(LocalOwnedDirectory.Open(path)), baseline.Value,
                compared.Value, metric.Value, cancellationToken: cancellationToken);
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
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                Contract = "comparison-v1",
                SessionPath = path,
                Comparison = Json(comparison, limit),
            }, JsonContracts.Indented));
            return InterCatExitCode.Success;
        }

        Print(comparison, path, limit);
        return InterCatExitCode.Success;
    }

    /// <summary>The comparison as `comparison-v1` states it, its rows the most changed first, as many as the limit.</summary>
    private static object Json(IntervalComparison comparison, int limit)
    {
        bool bytes = comparison.Metric == ComparisonMetric.BytesSent;
        return new
        {
            comparison.SessionId,
            comparison.Generation,
            comparison.Metric,
            comparison.Policy,
            ByteDomain = bytes ? ByteDomain.TransportObserved : (ByteDomain?)null,
            Accounting = bytes ? AccountingSide.SendSide : (AccountingSide?)null,
            Baseline = new { Interval = comparison.Baseline, Coverage = comparison.BaselineCoverage },
            Compared = new { Interval = comparison.Compared, Coverage = comparison.ComparedCoverage },
            Relationships = comparison.Relationships.Count,
            comparison.Quiet,
            Rows = comparison.Relationships.Take(limit).Select(row => new
            {
                row.Key,
                Kind = row.Host is null ? "Processes" : "OtherHost",
                row.Mechanism,
                Source = End(comparison.Processes[row.Relationship?.SourceId ?? row.Host!.Link.Process]),
                Target = row.Relationship is { } edge ? End(comparison.Processes[edge.TargetId]) : null,
                Address = row.Host?.Address.Address,
                Connected = row.Relationship is { } joined
                    ? Connected(joined.SourceConnected > 0, joined.TargetConnected > 0)
                    : Connected(row.Host!.Address.Connected > 0, row.Host.Address.Accepted > 0),
                AcceptedPorts = row.Relationship?.AcceptedPorts ?? row.Host!.Address.AcceptedPorts,
                HostPorts = row.Host?.Address.Ports ?? [],
                Baseline = Side(row.Baseline, comparison.Baseline),
                Compared = Side(row.Compared, comparison.Compared),
                row.Change,
                row.FromLowerBound,
                DifferencePerSecond = row.Change == ComparisonChange.NotComparable
                    ? null
                    : Rounded((row.Compared.PerSecond(comparison.Compared) ?? 0) - (row.Baseline.PerSecond(comparison.Baseline) ?? 0)),
                row.Reason,
            }),
        };

        static object End(ProcessNode node) => new { Instance = node.Id, node.ProcessId, node.Name };

        // A rate is for presentation, rounded half to even to three decimal places; the integers it comes from are the
        // row's (metrics-v1 §7).
        static double? Rounded(double? perSecond) => perSecond is { } value ? Math.Round(value, 3, MidpointRounding.ToEven) : null;

        static object Side(ComparedMeasure measure, TimeRange interval) => new
        {
            measure.Records,
            measure.Value,
            measure.Measured,
            measure.Unmeasured,
            PerSecond = Rounded(measure.PerSecond(interval)),
            measure.Coverage,
        };
    }

    /// <summary>
    /// Which end made a row's connections or calls, as the capture saw them: its arrow, in words. A process's connections
    /// to another host are from the process where it connected, and to it where it accepted them.
    /// </summary>
    private static string Connected(bool source, bool target) => (source, target) switch
    {
        (true, false) => "SourceToTarget",
        (false, true) => "TargetToSource",
        (true, true) => "Both",
        _ => "Unobserved",
    };

    private static void Print(IntervalComparison comparison, string path, int limit)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        bool bytes = comparison.Metric == ComparisonMetric.BytesSent;
        ConsoleUi.Heading("Comparison of two intervals");
        ConsoleUi.Field("Session", path);
        ConsoleUi.Field("Generation", ConsoleUi.Count(comparison.Generation));
        ConsoleUi.Field("Baseline", Interval(comparison.Baseline, culture));
        ConsoleUi.Field("Compared", Interval(comparison.Compared, culture));
        ConsoleUi.Field("Compared by", bytes
            ? "bytes sent across per second, each transfer counted once at its sender"
            : "records per second, both ends' records");
        int hosts = comparison.Relationships.Count(row => row.Host is not null);
        ConsoleUi.Field("Rows", ConsoleUi.Count(comparison.Relationships.Count - hosts) + " between processes · "
            + ConsoleUi.Count(hosts) + " to other hosts, with records in either interval"
            + (comparison.Quiet > 0 ? " · " + ConsoleUi.Count(comparison.Quiet) + " quiet in both" : string.Empty));
        Coverage("the baseline", comparison.BaselineCoverage);
        Coverage("the compared interval", comparison.ComparedCoverage);
        if (comparison.Relationships.Count == 0)
        {
            ConsoleUi.Note("No relationship the overview draws, and no process's connections to another host, has a record in "
                + "either interval, so none is compared.");
            return;
        }

        // Each reason two values cannot be compared is said once, beneath the table, and each row names it by its number.
        var reasons = new List<string>();
        ComparedRelationship[] listed = [.. comparison.Relationships.Take(limit)];
        ConsoleUi.Table(
            ["Relationship", "Over", "Baseline", "Compared", "Change"],
            [.. listed.Select(row => (IReadOnlyList<string>)
            [
                row.Relationship is { } edge ? OverviewCommand.Joined(edge, comparison.Processes) : Reached(row.Host!, comparison),
                row.Relationship is { } over ? DirectionText.Over(over.Mechanism, over.AcceptedPorts) : HostOver(row.Host!),
                Value(row.Baseline, comparison.Baseline, bytes, culture),
                Value(row.Compared, comparison.Compared, bytes, culture),
                Change(row, comparison, bytes, reasons, culture),
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

        ConsoleUi.Note("Rates are over each whole interval, never over the part of it the capture covered. A change is stated "
            + "only where both intervals covered a relationship's mechanism through the same sources; elsewhere both values "
            + "stand with the reason they cannot be compared, so a mechanism the capture did not collect in one interval never "
            + "reads as activity that stopped or started.");
        // Each note says what the listed rows need: of other hosts, bounds and arrows only where a row shows one.
        bool hostsListed = listed.Any(row => row.Host is not null);
        if (bytes)
        {
            ConsoleUi.Note("Bytes are transport-observed."
                + (hostsListed
                    ? " Of another host, only what the process sent there is counted: what the host sent has no sender among "
                        + "this machine's records."
                    : string.Empty)
                + (listed.Any(row => !row.Baseline.Exact || !row.Compared.Exact)
                    ? " A value marked ≥ is a lower bound: sends that recorded no size are counted apart, never as zero, and a "
                        + "change is stated from a bound only where the bound alone decides it."
                    : string.Empty));
        }

        if (listed.Any(row => row.Relationship is { SourceConnected: > 0 } or { TargetConnected: > 0 }
            || row.Host?.Address is { Connected: > 0 } or { Accepted: > 0 }))
        {
            ConsoleUi.Note("An arrow points at what was connected to, or called, where the capture saw it; ⇄ joins two that each "
                + "did so to the other.");
        }

        if (hostsListed)
        {
            ConsoleUi.Note("Another host is named by its address: who is at it is not observed.");
        }

        ConsoleUi.Note("icat compare <directory> --json returns each row with both intervals' coverage.");
    }

    /// <summary>
    /// A process's connections to another host as the row names them, the end that made them first, as icat overview names
    /// a relationship: "browser.exe · PID 100 → 142.250.1.1", "10.0.0.5 → svchost.exe · PID 300" where the process
    /// accepted them, "⇄" where it did both, and "and" where the capture saw none open.
    /// </summary>
    private static string Reached(OtherHostReach host, IntervalComparison comparison)
    {
        string process = CollectorText.Named(comparison.Processes[host.Link.Process]);
        return (host.Address.Connected > 0, host.Address.Accepted > 0) switch
        {
            (true, false) => $"{process} → {host.Address.Address}",
            (false, true) => $"{host.Address.Address} → {process}",
            (true, true) => $"{process} ⇄ {host.Address.Address}",
            _ => $"{process} and {host.Address.Address}",
        };
    }

    /// <summary>
    /// The transport and ports of a process's connections to another host: the host's ports it connected to, and its own
    /// ports the host's connections arrived at, each once, the lowest first: "TCP at 443".
    /// </summary>
    private static string HostOver(OtherHostReach host) =>
        DirectionText.Over(host.Link.Mechanism, [.. host.Address.Ports.Concat(host.Address.AcceptedPorts).Distinct().Order()]);

    /// <summary>An interval as the comparison states it: "[0.000, 10.000) s, 10.000 s long".</summary>
    private static string Interval(TimeRange interval, CultureInfo culture) =>
        WorkspaceTime.FormatHalfOpenRange(interval, culture) + ", " + WorkspaceTime.FormatDuration(interval.SpanTicks, culture)
        + " long";

    /// <summary>What the capture covered over one interval, in the words icat overview states it (R5, R21).</summary>
    private static void Coverage(string interval, IReadOnlyList<MechanismCoverage> coverage)
    {
        string described = CoverageText.Describe(coverage);
        if (described.StartsWith("Coverage", StringComparison.Ordinal))
        {
            ConsoleUi.Note("Coverage in " + interval + described["Coverage".Length..]);
        }
    }

    /// <summary>
    /// One interval's measure as a cell: "1,200 · 120/s"; under bytes "1.2 MiB · 120 KiB/s", "≥300 B · ≥30 B/s" for a lower
    /// bound, "nothing sent" for no send, or how many sends recorded no size where none measured one.
    /// </summary>
    private static string Value(ComparedMeasure measure, TimeRange interval, bool bytes, CultureInfo culture)
    {
        if (!bytes)
        {
            return measure.Records == 0 ? "0" : $"{ConsoleUi.Count(measure.Records)} · {Rate(measure.PerSecond(interval)!.Value, culture)}/s";
        }

        if (measure.Value is not { } sent)
        {
            return string.Create(culture, $"{measure.Unmeasured:N0} unmeasured {CountText.Agree(measure.Unmeasured, "send", "sends")}");
        }

        if (measure.None)
        {
            return "nothing sent";
        }

        string bound = measure.Exact ? string.Empty : "≥";
        return $"{bound}{ByteSizeText.Of(sent, culture)} · {bound}{ByteRate(measure.PerSecond(interval)!.Value, culture)}";
    }

    /// <summary>
    /// How a row changed: "unchanged", "up 90/s (+300%)", "down at least 1 KiB/s", "appeared", "disappeared", or "not
    /// comparable [1]", naming the reason beneath the table.
    /// </summary>
    private static string Change(
        ComparedRelationship row, IntervalComparison comparison, bool bytes, List<string> reasons, CultureInfo culture)
    {
        switch (row.Change)
        {
            case ComparisonChange.Unchanged:
                return "unchanged";
            case ComparisonChange.Appeared:
                return "appeared";
            case ComparisonChange.Disappeared:
                return "disappeared";
            case ComparisonChange.NotComparable:
                string reason = row.Reason ?? "its two values are not of the same measurement";
                int number = reasons.IndexOf(reason) + 1;
                if (number == 0)
                {
                    reasons.Add(reason);
                    number = reasons.Count;
                }

                return string.Create(culture, $"not comparable [{number}]");
        }

        double then = row.Baseline.PerSecond(comparison.Baseline) ?? 0;
        double now = row.Compared.PerSecond(comparison.Compared) ?? 0;
        double difference = Math.Abs(now - then);
        string by = bytes ? ByteRate(difference, culture) : Rate(difference, culture) + "/s";
        string direction = row.Change == ComparisonChange.Increased ? "up" : "down";
        return row.FromLowerBound
            ? $"{direction} at least {by}"
            : $"{direction} {by} ({Percent((now - then) / then * 100, culture)})";
    }

    /// <summary>A rate of records as a cell states it, to two significant digits below one: "120", "12.5", "0.25", "0.00028".</summary>
    private static string Rate(double perSecond, CultureInfo culture) => perSecond switch
    {
        0 => "0",
        >= 100 => perSecond.ToString("N0", culture),
        >= 10 => perSecond.ToString("N1", culture),
        >= 1 => perSecond.ToString("N2", culture),
        _ => perSecond.ToString("F" + Math.Min(15, 1 - (int)Math.Floor(Math.Log10(perSecond))).ToString(CultureInfo.InvariantCulture), culture),
    };

    /// <summary>A rate of bytes: "120 KiB/s", "3 B/s", or "under 1 B/s" for less than a byte a second.</summary>
    private static string ByteRate(double perSecond, CultureInfo culture) => perSecond switch
    {
        0 => "0 B/s",
        < 1 => "under 1 B/s",
        _ => ByteSizeText.Of((long)Math.Round(perSecond), culture) + "/s",
    };

    /// <summary>A relative change with its sign: "+300%", "-75%", "+2.5%" below ten percent.</summary>
    private static string Percent(double percent, CultureInfo culture) =>
        percent.ToString(Math.Abs(percent) >= 10 ? "+#,0;-#,0" : "+0.0;-0.0", culture) + "%";

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat compare <session-directory> --baseline <start:end> --interval <start:end>");
        ConsoleUi.Line("    [--rank-by records|bytes-sent] [--limit <n>] [--json]");
        ConsoleUi.Line("  Read-only comparison of two intervals of a session, relationship by relationship: how each one's");
        ConsoleUi.Line("  records, or the bytes its two ends sent across it, changed from the baseline to the interval, as");
        ConsoleUi.Line("  rates over each whole interval, the most changed first. Each bound is in 100-nanosecond session");
        ConsoleUi.Line("  ticks or a time with its unit (1.5s, 250ms, 40us), and the intervals may differ in length.");
        ConsoleUi.Line("  A change is stated only where both intervals covered the relationship's mechanism through the");
        ConsoleUi.Line("  same sources; elsewhere both values are shown with why they cannot be compared, so a mechanism");
        ConsoleUi.Line("  the capture did not collect in one interval never reads as activity that stopped. Sends that");
        ConsoleUi.Line("  recorded no size make a lower bound, which states a change only where it alone decides it.");
        ConsoleUi.Line($"  --limit lists at most that many, {DefaultLimit} by default.");
    }
}
