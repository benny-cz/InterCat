using System.Globalization;
using System.Text.RegularExpressions;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// A moment a person typed, read as an instant of a session (§6.2's time base, §6.7's navigation): a time of day on the
/// wall clock the capture's machine read - "14:32:05.120", with its date or its offset from UTC where they are needed -
/// or session time - "312.5 s", "250 ms". It is how a time copied from that machine's own logs finds its place in the
/// session. A bare whole number is no moment: a search reads it as a PID.
/// </summary>
public static partial class SessionMoment
{
    /// <summary>
    /// Whether <paramref name="text"/> reads as a moment at all: a time of day, or a number of seconds with a fraction or a
    /// unit. False for anything else, which a search reads as a name, a PID or an endpoint.
    /// </summary>
    public static bool IsMoment(string? text, IFormatProvider? culture = null) =>
        text is not null && (WallTime().IsMatch(text.Trim()) || SessionTime(text.Trim(), culture) is not null);

    /// <summary>
    /// Places <paramref name="text"/> in a session whose records span <paramref name="extent"/>: a time of day on
    /// <paramref name="wallClock"/>, said in <paramref name="zone"/> unless it names its own offset, on the session's one
    /// day it falls in unless it names its date; or session time, in seconds unless it names its unit. False, with why,
    /// when it is no moment (and <paramref name="problem"/> is null), names a time of day in a session that recorded no
    /// wall clock, falls outside the session, falls on more than one of its days, or names a time the zone skipped or
    /// repeated. A moment before <paramref name="retainedFromNanoseconds"/>, the session time a session that released its
    /// oldest interval keeps every record from, is said to lie in what the session released.
    /// </summary>
    public static bool TryPlace(string text, SessionWallClock? wallClock, TimeZoneInfo zone, TimeRange extent,
        IFormatProvider? culture, out long ticks, out string? problem, long? retainedFromNanoseconds = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(zone);
        IFormatProvider provider = culture ?? CultureInfo.CurrentCulture;
        string typed = text.Trim();
        ticks = 0;
        problem = null;
        if (SessionTime(typed, provider) is { } session)
        {
            if (Released(session, extent, retainedFromNanoseconds) is { } retained)
            {
                problem = $"{typed} lies in what this session released: it keeps every record from "
                    + $"{SessionTimeText.Seconds(retained, provider)} on.";
                return false;
            }

            if (session < extent.StartTicks || session >= extent.EndTicks)
            {
                problem = $"{typed} is outside this session, which runs {WorkspaceTime.FormatRange(extent, provider)} in session time.";
                return false;
            }

            ticks = session;
            return true;
        }

        Match wall = WallTime().Match(typed);
        if (!wall.Success)
        {
            return false;
        }

        if (wallClock is null)
        {
            problem = $"This session recorded no wall clock, so {typed} places nothing in it: type a session time, such as "
                + "312.5 s.";
            return false;
        }

        if (!TimeOf(wall, out TimeSpan time) || !OffsetOf(wall.Groups["offset"].Value, out TimeSpan? offset))
        {
            problem = $"{typed} is no time of day: hours run to 23, minutes and seconds to 59, and an offset is written as "
                + "UTC+02:00.";
            return false;
        }

        SessionClock reads = SessionClock.Wall(wallClock, zone, extent);
        List<DateTime> days;
        if (wall.Groups["date"].Success)
        {
            if (!DateOf(wall.Groups["date"].Value, provider, out DateTime date))
            {
                problem = $"{wall.Groups["date"].Value} is no date: write it as {DateTime.Today.ToString("d", provider)} or "
                    + $"{DateTime.Today:yyyy-MM-dd}.";
                return false;
            }

            days = [date];
        }
        else
        {
            days = DaysOf(wallClock, zone, offset, extent);
        }

        var placed = new List<long>();
        long? released = null;
        foreach (DateTime day in days)
        {
            DateTime local = day.Date + time;
            DateTimeOffset utc;
            if (offset is { } named)
            {
                utc = new DateTimeOffset(local, named);
            }
            else if (zone.IsInvalidTime(local))
            {
                problem = $"{typed} did not happen on {local.ToString("d", provider)} in this computer's zone: its clocks "
                    + "skipped it.";
                return false;
            }
            else if (zone.IsAmbiguousTime(local))
            {
                TimeSpan[] both = zone.GetAmbiguousTimeOffsets(local);
                problem = $"{typed} happened twice on {local.ToString("d", provider)} in this computer's zone; add its offset, "
                    + string.Join(" or ", both.OrderByDescending(candidate => candidate).Select(SessionClock.OffsetText)) + ".";
                return false;
            }
            else
            {
                utc = new DateTimeOffset(local, zone.GetUtcOffset(local));
            }

            long at = wallClock.TicksAt(utc);
            if (at >= extent.StartTicks && at < extent.EndTicks)
            {
                placed.Add(at);
            }

            released ??= Released(at, extent, retainedFromNanoseconds);
        }

        if (placed.Count == 1)
        {
            ticks = placed[0];
            return true;
        }

        if (placed.Count == 0 && released is { } boundary)
        {
            problem = $"{typed} lies in what this session released: it keeps every record from "
                + $"{reads.Moment(boundary, provider)} on.";
            return false;
        }

        problem = placed.Count == 0
            ? $"{typed} is outside this session, which the wall clock read from {reads.Range(extent, provider)}."
            : $"{typed} falls on {placed.Count.ToString("N0", provider)} of this session's days; add its date, as "
                + $"{days[0].ToString("d", provider)} {typed}.";
        return false;
    }

    /// <summary>
    /// The boundary, in session time, a moment lies before when it falls in the stretch a session released: after the
    /// session began, before the first tick wholly at or after the boundary and before its extent begins; null for any
    /// other moment.
    /// </summary>
    private static long? Released(long ticks, TimeRange extent, long? retainedFromNanoseconds) =>
        retainedFromNanoseconds is { } retained && ticks >= 0 && ticks < (retained + 99) / 100 && ticks < extent.StartTicks
            ? retained
            : null;

    /// <summary>
    /// The days a time of day with no date may fall on: each from the session's first to its last, in the offset it names
    /// or else in the zone.
    /// </summary>
    private static List<DateTime> DaysOf(SessionWallClock wallClock, TimeZoneInfo zone, TimeSpan? offset, TimeRange extent)
    {
        DateTimeOffset first = wallClock.At(extent.StartTicks);
        DateTimeOffset last = wallClock.At(Math.Max(extent.StartTicks, extent.EndTicks - 1));
        DateTime from = (offset is { } named ? first.ToOffset(named) : TimeZoneInfo.ConvertTime(first, zone)).Date;
        DateTime to = (offset is { } same ? last.ToOffset(same) : TimeZoneInfo.ConvertTime(last, zone)).Date;
        var days = new List<DateTime>();
        for (DateTime day = from; day <= to && days.Count < 32; day = day.AddDays(1))
        {
            days.Add(day);
        }

        return days;
    }

    /// <summary>Session time in presentation ticks, from a number with a fraction or a unit; null for anything else.</summary>
    private static long? SessionTime(string text, IFormatProvider? culture)
    {
        Match match = SessionNumber().Match(text);
        if (!match.Success || (!match.Groups["fraction"].Success && !match.Groups["unit"].Success))
        {
            return null;
        }

        // The fraction is parted by a point, or by the culture's decimal separator where that is a comma.
        string separator = NumberFormatInfo.GetInstance(culture ?? CultureInfo.CurrentCulture).NumberDecimalSeparator;
        if (match.Groups["fraction"].Success && match.Groups["point"].Value != "." && match.Groups["point"].Value != separator)
        {
            return null;
        }

        decimal value = decimal.Parse(match.Groups["whole"].Value + (match.Groups["fraction"].Success
            ? "." + match.Groups["fraction"].Value
            : string.Empty), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        decimal perUnit = match.Groups["unit"].Value switch
        {
            "ms" => WorkspaceTime.TicksPerSecond / 1_000m,
            "µs" or "us" => WorkspaceTime.TicksPerSecond / 1_000_000m,
            "ns" => WorkspaceTime.TicksPerSecond / 1_000_000_000m,
            _ => WorkspaceTime.TicksPerSecond,
        };
        decimal ticks = Math.Round(value * perUnit, MidpointRounding.ToEven);
        if (ticks > long.MaxValue / 2)
        {
            return null;
        }

        return match.Groups["sign"].Value is "-" or "−" ? -(long)ticks : (long)ticks;
    }

    private static bool TimeOf(Match wall, out TimeSpan time)
    {
        int hours = int.Parse(wall.Groups["h"].Value, CultureInfo.InvariantCulture);
        int minutes = int.Parse(wall.Groups["m"].Value, CultureInfo.InvariantCulture);
        int seconds = wall.Groups["s"].Success ? int.Parse(wall.Groups["s"].Value, CultureInfo.InvariantCulture) : 0;
        long fraction = wall.Groups["f"].Success
            ? long.Parse(wall.Groups["f"].Value.PadRight(7, '0'), CultureInfo.InvariantCulture)
            : 0;
        time = default;
        if (hours > 23 || minutes > 59 || seconds > 59)
        {
            return false;
        }

        time = new TimeSpan(hours, minutes, seconds) + TimeSpan.FromTicks(fraction);
        return true;
    }

    /// <summary>The offset a time names: none, UTC itself ("Z", "UTC"), or one written as "UTC+02:00" or "+02:00".</summary>
    private static bool OffsetOf(string text, out TimeSpan? offset)
    {
        offset = null;
        if (text.Length == 0)
        {
            return true;
        }

        if (text is "Z" or "z" || text.Equals("UTC", StringComparison.OrdinalIgnoreCase))
        {
            offset = TimeSpan.Zero;
            return true;
        }

        Match match = Offset().Match(text);
        int hours = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        int minutes = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        if (hours > 14 || minutes > 59)
        {
            return false;
        }

        TimeSpan magnitude = new(hours, minutes, 0);
        offset = match.Groups["sign"].Value == "+" ? magnitude : -magnitude;
        return true;
    }

    /// <summary>A date as ISO 8601 writes it, or as the culture writes a short date.</summary>
    private static bool DateOf(string text, IFormatProvider culture, out DateTime date) =>
        DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
        || DateTime.TryParseExact(text, DateTimeFormatInfo.GetInstance(culture).ShortDatePattern, culture, DateTimeStyles.None,
            out date);

    [GeneratedRegex(@"^(?<sign>[+\-−])?(?<whole>\d{1,15})(?:(?<point>[.,])(?<fraction>\d{1,9}))?\s*(?<unit>s|ms|µs|us|ns)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SessionNumber();

    [GeneratedRegex(@"^(?:(?<date>\S+)\s+)?(?<h>\d{1,2}):(?<m>\d{2})(?::(?<s>\d{2})(?:[.,](?<f>\d{1,7}))?)?\s*(?<offset>[Zz]|UTC(?:[+\-−]\d{1,2}:\d{2})?|[+\-−]\d{1,2}:\d{2})?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex WallTime();

    [GeneratedRegex(@"(?<sign>[+\-−])(?<h>\d{1,2}):(?<m>\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex Offset();
}
