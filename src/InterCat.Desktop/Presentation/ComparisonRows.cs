using System.Globalization;
using InterCat.Application;
using InterCat.Domain;

namespace InterCat.Desktop.Presentation;

/// <summary>
/// One row of the window's comparison of two intervals, in the words `icat compare` states it in (R5, R18): its two ends,
/// what it ran over, its value and rate in each interval, and its change - or "not comparable", with the number of its
/// reason beneath the rows.
/// </summary>
public sealed record ComparisonRow(
    ComparedRelationship Compared,
    string Ends,
    string Over,
    string BaselineValue,
    string ComparedValue,
    string Change) : IAccessibleRow
{
    /// <summary>Why its two values cannot be compared, said beneath the rows by <see cref="ReasonNumber"/>; null where a change is stated.</summary>
    public string? Reason { get; init; }

    /// <summary>The number of its reason beneath the rows, from 1; null where a change is stated.</summary>
    public int? ReasonNumber { get; init; }

    /// <summary>
    /// How many notes in force are about the row, where the comparison is one its investigation saved
    /// (`contracts/workspace-v23.md` §7); none otherwise.
    /// </summary>
    public int Noted { get; init; }

    /// <summary>What the relationship column shows: its two ends, and how many notes are about it where any is.</summary>
    public string EndsCell => Noted == 0 ? Ends : Ends + " · " + CountText.Of(Noted, "note");

    /// <summary>What the change column shows: the change, and the number of the reason where none is stated.</summary>
    public string ChangeCell => ReasonNumber is { } number
        ? string.Create(CultureInfo.CurrentCulture, $"{Change} [{number}]")
        : Change;

    /// <summary>
    /// The row as a screen reader says it, a lower bound said in words, the reason in full and the notes about it counted:
    /// "browser.exe · PID 100 → 10.0.0.20 over TCP at 443: 21 · 1.05/s in the baseline, 40 · 1.00/s in the compared
    /// interval; down 0.050/s (-4.8%)".
    /// </summary>
    public string AccessibleName =>
        $"{Ends} over {Over}: {Spoken(BaselineValue)} in the baseline, {Spoken(ComparedValue)} in the compared interval; {Change}"
        + (Reason is null ? string.Empty : ": " + Reason)
        + (Noted == 0 ? string.Empty : "; " + CountText.Of(Noted, "note") + " about it, beneath the rows");

    private static string Spoken(string value) => value.Replace("≥", "at least ", StringComparison.Ordinal);
}

/// <summary>A comparison's rows in its order, each stated as `icat compare` states it, and its reasons, each once.</summary>
public static class ComparisonRows
{
    /// <summary>
    /// The rows of <paramref name="comparison"/>, the most changed first, and beneath them each reason a row's two values
    /// cannot be compared, numbered in the order the rows first name them. Two processes the capture saw open no connection
    /// between are joined by "↔", as the window's relationship table joins them.
    /// </summary>
    public static (IReadOnlyList<ComparisonRow> Rows, IReadOnlyList<string> Reasons) Of(IntervalComparison comparison, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        culture ??= CultureInfo.CurrentCulture;
        var reasons = new List<string>();
        var rows = new List<ComparisonRow>(comparison.Relationships.Count);
        foreach (ComparedRelationship compared in comparison.Relationships)
        {
            var row = new ComparisonRow(
                compared,
                ComparisonText.Ends(compared, comparison, "↔"),
                ComparisonText.Over(compared),
                ComparisonText.Value(compared.Baseline, comparison.Baseline, comparison.Metric, culture),
                ComparisonText.Value(compared.Compared, comparison.Compared, comparison.Metric, culture),
                ComparisonText.Change(compared, comparison, culture));
            if (compared.Change == ComparisonChange.NotComparable)
            {
                string reason = compared.Reason ?? "Its two values are not of the same measurement.";
                int number = reasons.IndexOf(reason) + 1;
                if (number == 0)
                {
                    reasons.Add(reason);
                    number = reasons.Count;
                }

                row = row with { Reason = reason, ReasonNumber = number };
            }

            rows.Add(row);
        }

        return (rows, reasons);
    }
}
