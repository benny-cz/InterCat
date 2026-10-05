using System.Globalization;
using InterCat.Domain;

namespace InterCat.Cli;

/// <summary>
/// An --interval in 100-nanosecond session-relative presentation ticks, start:end and half-open, as evidence, export and
/// timeline read it. Its end is after its start by at most a tick count: a wider one is refused in words, as one that
/// names no interval is, where measuring it once overflowed and failed the command.
/// </summary>
internal static class TickInterval
{
    /// <summary>
    /// The interval <paramref name="raw"/> names, or null when it names none: its bounds are not two integers, its end is
    /// not after its start, or - with <paramref name="tooWide"/> saying so - its end is further after its start than a
    /// tick count holds.
    /// </summary>
    public static TimeRange? Read(string? raw, out string? tooWide)
    {
        tooWide = null;
        string[] parts = raw?.Split(':') ?? [];
        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long start)
            || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long end)
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
}
