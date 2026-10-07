using System.Globalization;

namespace InterCat.Domain;

/// <summary>
/// A count as a person reads it wherever InterCat states one: the number in the reader's culture with its noun in the
/// number it takes - "1 record", "1,234 records" - and a verb that agrees with it, so no sentence reads "1 records are".
/// </summary>
public static class CountText
{
    /// <summary>
    /// A count with its noun: "1 record", "3 records", "0 records"; <paramref name="many"/> where the plural is not the noun
    /// and an s - "1 process", "3 processes".
    /// </summary>
    public static string Of(long count, string one, string? many = null)
    {
        ArgumentNullException.ThrowIfNull(one);
        return count == 1 ? "1 " + one : string.Create(CultureInfo.CurrentCulture, $"{count:N0} {many ?? one + "s"}");
    }

    /// <summary>The word that agrees with <paramref name="count"/>: <paramref name="one"/> for one, <paramref name="many"/> otherwise.</summary>
    public static string Agree(long count, string one, string many) => count == 1 ? one : many;

    /// <summary>A position as a sentence says it: "1st", "2nd", "3rd", "4th", "11th", "12th", "21st".</summary>
    public static string Ordinal(int position)
    {
        string suffix = (position % 100) is 11 or 12 or 13 ? "th"
            : (position % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return string.Create(CultureInfo.CurrentCulture, $"{position:N0}{suffix}");
    }
}
