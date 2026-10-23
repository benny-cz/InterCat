using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// A comparison of two intervals as a person reads it, in one set of words for the command line and the window (R5, R18,
/// `contracts/comparison-v1.md` §8): each interval and its length, what the capture covered over it, each row's two ends,
/// what it ran over, its value and rate in each interval, and how it changed.
/// </summary>
public static class ComparisonText
{
    /// <summary>What a comparison says of its rates, beneath its rows.</summary>
    public const string RatesNote = "Rates are over each whole interval, never over the part of it the capture covered. A "
        + "change is stated only where both intervals covered a relationship's mechanism through the same sources; elsewhere "
        + "both values stand with the reason they cannot be compared, so a mechanism the capture did not collect in one "
        + "interval never reads as activity that stopped or started.";

    /// <summary>
    /// An interval as a comparison states it, on <paramref name="clock"/>'s time base, session time by default: "[0.0,
    /// 10.0) s, 10.0 s long".
    /// </summary>
    public static string Interval(TimeRange interval, SessionClock? clock = null, IFormatProvider? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        string range = clock is null ? WorkspaceTime.FormatHalfOpenRange(interval, culture) : clock.HalfOpenRange(interval, culture);
        return range + ", " + WorkspaceTime.FormatDuration(interval.SpanTicks, culture) + " long";
    }

    /// <summary>
    /// What the capture covered over one interval, in the words the overview states it in (R5, R21), named by the interval:
    /// "Coverage in the baseline: covered for TCP and UDP · no other mechanism collected"; null where nothing was judged.
    /// </summary>
    public static string? Coverage(string interval, IReadOnlyList<MechanismCoverage> coverage)
    {
        ArgumentException.ThrowIfNullOrEmpty(interval);
        string described = CoverageText.Describe(coverage);
        return described.StartsWith("Coverage", StringComparison.Ordinal)
            ? "Coverage in " + interval + described["Coverage".Length..]
            : null;
    }

    /// <summary>
    /// A row's two ends, the one that made its connections or calls first, as the overview names a relationship: "browser.exe
    /// · PID 100 → server.exe · PID 200", "a ⇄ b" where each made some, and the two joined by <paramref name="unopened"/>
    /// where the capture saw none open. Another host is named by its address: "browser.exe · PID 100 → 142.250.1.1", and
    /// "10.0.0.5 → svchost.exe · PID 300" where the process accepted the host's connections.
    /// </summary>
    public static string Ends(ComparedRelationship row, IntervalComparison comparison, string unopened)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentException.ThrowIfNullOrEmpty(unopened);
        (string source, string target, bool fromSource, bool fromTarget) = row.Relationship is { } edge
            ? (CollectorText.Named(comparison.Processes[edge.SourceId]), CollectorText.Named(comparison.Processes[edge.TargetId]),
                edge.SourceConnected > 0, edge.TargetConnected > 0)
            : (CollectorText.Named(comparison.Processes[row.Host!.Link.Process]), row.Host.Address.Address,
                row.Host.Address.Connected > 0, row.Host.Address.Accepted > 0);
        return (fromSource, fromTarget) switch
        {
            (true, false) => $"{source} → {target}",
            (false, true) => $"{target} → {source}",
            (true, true) => $"{source} ⇄ {target}",
            _ => $"{source} {unopened} {target}",
        };
    }

    /// <summary>
    /// What a row ran over and the ports its connections arrived at: a relationship's at the end that accepted each, and of
    /// another host its ports the process connected to and the process's own the host's connections arrived at, each once,
    /// the lowest first: "TCP at 443"; the mechanism alone where none was seen arriving.
    /// </summary>
    public static string Over(ComparedRelationship row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Relationship is { } edge
            ? DirectionText.Over(edge.Mechanism, edge.AcceptedPorts)
            : DirectionText.Over(row.Host!.Link.Mechanism,
                [.. row.Host.Address.Ports.Concat(row.Host.Address.AcceptedPorts).Distinct().Order()]);
    }

    /// <summary>
    /// One interval's measure: "1,200 · 120/s", or "0"; under bytes "1.2 MiB · 120 KiB/s", "≥300 B · ≥30 B/s" for a lower
    /// bound, "nothing sent" for no send, or how many sends recorded no size where none measured one.
    /// </summary>
    public static string Value(ComparedMeasure measure, TimeRange interval, ComparisonMetric metric, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(measure);
        culture ??= CultureInfo.CurrentCulture;
        if (metric != ComparisonMetric.BytesSent)
        {
            return measure.Records == 0
                ? "0"
                : string.Create(culture, $"{measure.Records:N0} · {Rate(measure.PerSecond(interval)!.Value, culture)}/s");
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
        return $"{bound}{ByteSizeText.Of(sent, culture as CultureInfo)} · {bound}{ByteRate(measure.PerSecond(interval)!.Value, culture)}";
    }

    /// <summary>
    /// How a row changed: "unchanged", "up 90/s (+300%)", "down at least 1 KiB/s" where a lower bound decided it,
    /// "appeared", "disappeared", or "not comparable", whose reason the row carries.
    /// </summary>
    public static string Change(ComparedRelationship row, IntervalComparison comparison, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(comparison);
        culture ??= CultureInfo.CurrentCulture;
        switch (row.Change)
        {
            case ComparisonChange.Unchanged:
                return "unchanged";
            case ComparisonChange.Appeared:
                return "appeared";
            case ComparisonChange.Disappeared:
                return "disappeared";
            case ComparisonChange.NotComparable:
                return "not comparable";
        }

        double then = row.Baseline.PerSecond(comparison.Baseline) ?? 0;
        double now = row.Compared.PerSecond(comparison.Compared) ?? 0;
        double difference = Math.Abs(now - then);
        string by = comparison.Metric == ComparisonMetric.BytesSent ? ByteRate(difference, culture) : Rate(difference, culture) + "/s";
        string direction = row.Change == ComparisonChange.Increased ? "up" : "down";
        return row.FromLowerBound
            ? $"{direction} at least {by}"
            : $"{direction} {by} ({Percent((now - then) / then * 100, culture)})";
    }

    /// <summary>
    /// What a comparison says beneath the rows it lists, each only where a row needs it: its rates; under bytes, what they
    /// count, of another host what the process sent, and a lower bound's mark; the arrows; and how another host is named.
    /// </summary>
    public static IReadOnlyList<string> Notes(IReadOnlyCollection<ComparedRelationship> listed, ComparisonMetric metric)
    {
        ArgumentNullException.ThrowIfNull(listed);
        List<string> notes = [RatesNote];
        bool hosts = listed.Any(row => row.Host is not null);
        if (metric == ComparisonMetric.BytesSent)
        {
            notes.Add("Bytes are transport-observed."
                + (hosts
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
            notes.Add("An arrow points at what was connected to, or called, where the capture saw it; ⇄ joins two that each did "
                + "so to the other.");
        }

        if (hosts)
        {
            notes.Add("Another host is named by its address: who is at it is not observed.");
        }

        return notes;
    }

    /// <summary>A rate of records, to two significant digits below one: "120", "12.5", "1.25", "0.25", "0.00028".</summary>
    public static string Rate(double perSecond, IFormatProvider? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        return perSecond switch
        {
            0 => "0",
            >= 100 => perSecond.ToString("N0", culture),
            >= 10 => perSecond.ToString("N1", culture),
            >= 1 => perSecond.ToString("N2", culture),
            _ => perSecond.ToString("F" + Math.Min(15, 1 - (int)Math.Floor(Math.Log10(perSecond))).ToString(CultureInfo.InvariantCulture),
                culture),
        };
    }

    /// <summary>A rate of bytes: "120 KiB/s", "3 B/s", or "under 1 B/s" for less than a byte a second.</summary>
    public static string ByteRate(double perSecond, IFormatProvider? culture = null) => perSecond switch
    {
        0 => "0 B/s",
        < 1 => "under 1 B/s",
        _ => ByteSizeText.Of((long)Math.Round(perSecond), culture as CultureInfo) + "/s",
    };

    /// <summary>A relative change with its sign: "+300%", "-75%", "+2.5%" below ten percent.</summary>
    public static string Percent(double percent, IFormatProvider? culture = null) =>
        percent.ToString(Math.Abs(percent) >= 10 ? "+#,0;-#,0" : "+0.0;-0.0", culture ?? CultureInfo.CurrentCulture) + "%";
}
