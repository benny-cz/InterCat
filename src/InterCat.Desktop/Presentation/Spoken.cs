using System.Globalization;

namespace InterCat.Desktop.Presentation;

/// <summary>
/// How a row's numbers and states read aloud (R15): a count with its noun in the right number, and a coverage state as
/// "coverage: unknown" rather than the column's "coverage unknown coverage". What a sentence says is the same fact the
/// row shows; only its wording is for the ear.
/// </summary>
internal static class Spoken
{
    public static string Count(long count, string noun) => count == 1
        ? $"1 {noun}"
        : string.Create(CultureInfo.CurrentCulture, $"{count:N0} {noun}s");

    /// <summary>A count with a noun whose plural is not the singular and an s: "1 process", "3 processes".</summary>
    public static string Count(long count, string one, string many) => count == 1
        ? $"1 {one}"
        : string.Create(CultureInfo.CurrentCulture, $"{count:N0} {many}");

    public static string Coverage(string label) =>
        "coverage: " + (label.EndsWith(" coverage", StringComparison.Ordinal) ? label[..^" coverage".Length] : label);

    /// <summary>Several names as a sentence lists them: "TCP", "TCP and UDP", "process lifecycle, TCP and UDP".</summary>
    public static string List(IReadOnlyList<string> names) => names.Count < 2
        ? string.Concat(names)
        : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];

    /// <summary>A position as a sentence says it: "1st", "2nd", "3rd", "4th", "11th", "12th", "21st".</summary>
    public static string Ordinal(int position)
    {
        string suffix = (position % 100) is 11 or 12 or 13 ? "th"
            : (position % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return string.Create(CultureInfo.CurrentCulture, $"{position:N0}{suffix}");
    }
}
