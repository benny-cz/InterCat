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

    public static string Coverage(string label) =>
        "coverage: " + (label.EndsWith(" coverage", StringComparison.Ordinal) ? label[..^" coverage".Length] : label);
}
