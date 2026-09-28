using System.Globalization;
using System.Text.RegularExpressions;

namespace InterCat.Application;

/// <summary>
/// What a person types for an investigation's time - a duration with its unit, an instant in seconds, a rate in parts per
/// million - read the same way by the command line and the Desktop. A comma is read as a decimal point, so either
/// culture's habit works, and a duration without a unit is refused rather than guessed.
/// </summary>
public static partial class InvestigationInput
{
    /// <summary>A duration written with its unit - ns, us, µs, ms or s - in whole nanoseconds, rounded up; null when it is not one.</summary>
    public static long? Duration(string? text)
    {
        if (text is null)
        {
            return null;
        }

        Match match = DurationPattern().Match(text.Trim());
        if (!match.Success || !decimal.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out decimal value))
        {
            return null;
        }

        decimal scale = match.Groups[2].Value switch
        {
            "ns" => 1m,
            "us" or "µs" => 1_000m,
            "ms" => 1_000_000m,
            _ => 1_000_000_000m,
        };
        decimal nanoseconds = value * scale;
        return nanoseconds > long.MaxValue ? null : (long)Math.Ceiling(nanoseconds);
    }

    /// <summary>An instant of session time written in seconds, in nanoseconds; null when it is not one.</summary>
    public static long? Seconds(string? text) =>
        text is not null && decimal.TryParse(text.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out decimal seconds) && Math.Abs(seconds) <= 9_000_000_000m
            ? (long)Math.Round(seconds * 1_000_000_000m, MidpointRounding.ToEven)
            : null;

    /// <summary>A non-negative rate in parts per million; null when it is not one.</summary>
    public static double? PartsPerMillion(string? text) =>
        text is not null && double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture,
            out double rate) && double.IsFinite(rate) && rate >= 0
            ? rate
            : null;

    [GeneratedRegex(@"^([0-9]+(?:[.,][0-9]+)?)\s*(ns|us|µs|ms|s)$", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();
}
