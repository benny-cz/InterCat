using System.Globalization;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// Outliers as a person reads them, in one set of words for the command line and the window (R18,
/// `contracts/outliers-v1.md` §5): the baseline and its windows, where a row's records fall among the windows', how far
/// outside their range, how often chance alone would put a row there, and what was not judged. Every label says what was
/// counted, never why (plan M12).
/// </summary>
public static class OutlierText
{
    /// <summary>What a list of outliers says of itself, the command line and the window alike.</summary>
    public const string Note = "Each row's records over the compared interval are set beside the records it held in each "
        + "window of its baseline, every window as long as the compared interval. A row above or below every window differs "
        + "from its baseline by more than any window did, and that is all it says: nothing of what its records mean or why "
        + "they differ, and chance alone puts a few rows there. Only rows whose two intervals were covered and collected "
        + "alike are judged.";

    /// <summary>What a reading that judged no row says of itself where its rows were all ones it could not judge.</summary>
    public const string NoneJudged = "No row is judged: none was covered and collected alike over both intervals.";

    /// <summary>What a reading says where every row it judged is within its windows' range.</summary>
    public const string NoneOutside = "No row is above or below every window of its baseline.";

    /// <summary>
    /// What a reading says where it holds no row at all: no relationship, or by executable no executable's, has a record in
    /// either interval.
    /// </summary>
    public static string NoRow(IntervalComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        return comparison.Grouping == ComparisonGrouping.Executable
            ? "No relationship between executables, and no executable's connections to another host, has a record in either "
                + "interval, so none is judged."
            : "No relationship the overview draws, and no process's connections to another host, has a record in either "
                + "interval, so none is judged.";
    }

    /// <summary>Where a row's records over the compared interval fall among its windows': "above every window of its baseline".</summary>
    public static string Side(OutlierSide side) => side switch
    {
        OutlierSide.Above => "above every window of its baseline",
        OutlierSide.Below => "below every window of its baseline",
        OutlierSide.Within => "within its baseline's windows",
        _ => throw new ArgumentOutOfRangeException(nameof(side)),
    };

    /// <summary>
    /// The baseline as it was judged by, on <paramref name="clock"/>'s time base, session time by default: "[0.0, 1.0) s,
    /// 1.0 s long, as 20 windows of 50.0 ms, end to end", or "spread evenly across it" where the windows do not fill it.
    /// </summary>
    public static string Baseline(OutlierReading reading, SessionClock? clock = null, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(reading);
        culture ??= CultureInfo.CurrentCulture;
        long span = reading.Comparison.Compared.SpanTicks;
        bool filled = (Int128)span * reading.Windows.Count == reading.Comparison.Baseline.SpanTicks;
        return ComparisonText.Interval(reading.Comparison.Baseline, clock, culture)
            + string.Create(culture, $", as {reading.Windows.Count:N0} windows of ")
            + WorkspaceTime.FormatDuration(span, culture)
            + (filled ? ", end to end" : ", spread evenly across it");
    }

    /// <summary>A row's records in its windows: "40 to 61, median 50", or "10 in each" where every window held as many.</summary>
    public static string Windows(OutlierJudgement judged, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(judged);
        culture ??= CultureInfo.CurrentCulture;
        return judged.Fewest == judged.Most
            ? string.Create(culture, $"{judged.Most:N0} in each")
            : string.Create(culture, $"{judged.Fewest:N0} to {judged.Most:N0}, median {judged.Median:N0}");
    }

    /// <summary>
    /// How far a row's records are outside its windows' range: "40 more than the most", "10 fewer than the fewest"; "-"
    /// within it.
    /// </summary>
    public static string Beyond(OutlierJudgement judged, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(judged);
        culture ??= CultureInfo.CurrentCulture;
        return judged.Side switch
        {
            OutlierSide.Above => string.Create(culture, $"{judged.Beyond:N0} more than the most"),
            OutlierSide.Below => string.Create(culture, $"{judged.Beyond:N0} fewer than the fewest"),
            _ => "-",
        };
    }

    /// <summary>
    /// What was judged and found: "5 rows judged · 2 above every window of their baseline and 1 below every one", "5
    /// rows judged · none above or below every window" where none was, or "no row judged".
    /// </summary>
    public static string Found(OutlierReading reading, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(reading);
        culture ??= CultureInfo.CurrentCulture;
        int judged = reading.Judged.Count;
        if (judged == 0)
        {
            return "no row judged";
        }

        int above = reading.Judged.Count(row => row.Side == OutlierSide.Above);
        int below = reading.Judged.Count(row => row.Side == OutlierSide.Below);
        string found = (above, below) switch
        {
            (0, 0) => "none above or below every window",
            (_, 0) => string.Create(culture, $"{above:N0} above every window of {Their(above)} baseline"),
            (0, _) => string.Create(culture, $"{below:N0} below every window of {Their(below)} baseline"),
            _ => string.Create(culture, $"{above:N0} above every window of {Their(above)} baseline and {below:N0} below every one"),
        };
        return string.Create(culture, $"{judged:N0} {CountText.Agree(judged, "row", "rows")} judged · ") + found;

        static string Their(int rows) => CountText.Agree(rows, "its", "their");
    }

    /// <summary>
    /// How often chance alone would put a row outside its windows' range, and so how many of the rows judged it would on
    /// average: "Were the compared interval like the baseline's windows, chance alone would put a row above every one of
    /// them at most 1 time in 20, and below every one at most as often: on average no more than 0.40 of the 8 rows judged
    /// each way." Null where no row was judged.
    /// </summary>
    public static string? Chance(OutlierReading reading, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(reading);
        culture ??= CultureInfo.CurrentCulture;
        int judged = reading.Judged.Count;
        return judged == 0
            ? null
            : string.Create(culture, $"Were the compared interval like the baseline's windows, chance alone would put a row above every "
                + $"one of them at most 1 time in {reading.Chance:N0}, and below every one at most as often: on average no more than "
                + $"{(double)judged / reading.Chance:N2} of the {judged:N0} {CountText.Agree(judged, "row", "rows")} judged each way.");
    }

    /// <summary>
    /// The rows not judged, by why, each reason with how many: "2 rows not judged: TCP's coverage over the baseline is
    /// unknown: …", the most first; none where every row was judged.
    /// </summary>
    public static IReadOnlyList<string> NotJudged(OutlierReading reading, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(reading);
        culture ??= CultureInfo.CurrentCulture;
        return [.. reading.NotJudged
            .GroupBy(row => row.Reason ?? "its two values are not of the same measurement", StringComparer.Ordinal)
            .OrderByDescending(reason => reason.Count())
            .ThenBy(reason => reason.Key, StringComparer.Ordinal)
            .Select(reason => string.Create(culture,
                $"{reason.Count():N0} {CountText.Agree(reason.Count(), "row", "rows")} not judged: {reason.Key}"))];
    }
}
