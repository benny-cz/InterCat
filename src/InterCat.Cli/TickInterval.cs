using System.Globalization;
using System.Text.RegularExpressions;
using InterCat.Domain;

namespace InterCat.Cli;

/// <summary>
/// An --interval, start:end and half-open, as evidence, export, package, timeline and the investigation's timeline read it:
/// each bound a count of 100-nanosecond presentation ticks, or a time with its unit - 1.5s, 250ms, 40us, 800ns - as the
/// window states an interval and `icat metric` takes one. A time is taken outward to whole ticks, a start down and an end
/// up, so the interval holds every record of the time typed. Its end is after its start by at most a tick count: a wider
/// one is refused in words, as one that names no interval is, where measuring it once overflowed and failed the command.
/// </summary>
internal static partial class TickInterval
{
    /// <summary>What an --interval takes, as a command says it when one names no interval.</summary>
    public const string Takes = "start:end, each bound a count of 100-nanosecond session ticks or a time with its unit, "
        + "such as 1.5s, 250ms or 40us, with its end after its start";

    /// <summary>
    /// The interval <paramref name="raw"/> names, or null when it names none: a bound is neither a whole number nor a time
    /// with its unit, its end is not after its start, or - with <paramref name="tooWide"/> saying so - its end is further
    /// after its start than a tick count holds.
    /// </summary>
    public static TimeRange? Read(string? raw, out string? tooWide)
    {
        tooWide = null;
        string[] parts = raw?.Split(':') ?? [];
        if (parts.Length != 2
            || Bound(parts[0], end: false) is not { } start
            || Bound(parts[1], end: true) is not { } end
            || end <= start)
        {
            return null;
        }

        if (TimeRange.TryCreate(start, end, out TimeRange interval))
        {
            return interval;
        }

        tooWide = $"--interval {raw} ends more than {ConsoleUi.Count(long.MaxValue)} ticks after it starts, some 29,000 "
            + "years, and no interval is that long; narrow it.";
        return null;
    }

    /// <summary>
    /// One bound in ticks: a whole number as it is, or a time with its unit at the tick holding it for a start and at the
    /// first tick after it for an end that falls inside one; null for anything else, or a time no tick count holds.
    /// </summary>
    private static long? Bound(string text, bool end)
    {
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks))
        {
            return ticks;
        }

        Match timed = Timed().Match(text.Trim());
        if (!timed.Success)
        {
            return null;
        }

        decimal exact = decimal.Parse(timed.Groups["value"].Value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture) * timed.Groups["unit"].Value switch
            {
                "s" => 10_000_000m,
                "ms" => 10_000m,
                "us" or "µs" => 10m,
                _ => 0.01m,
            };
        decimal whole = end ? Math.Ceiling(exact) : Math.Floor(exact);
        return whole < long.MinValue || whole > long.MaxValue ? null : (long)whole;
    }

    [GeneratedRegex(@"^(?<value>[+-]?\d{1,18}(?:\.\d{1,9})?)\s*(?<unit>s|ms|us|µs|ns)$", RegexOptions.CultureInvariant)]
    private static partial Regex Timed();
}
