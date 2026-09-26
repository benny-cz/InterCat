using System.Globalization;

namespace InterCat.Desktop.Presentation;

/// <summary>Moments said as a person says them, in the viewer's own zone and day.</summary>
internal static class Moments
{
    /// <summary>"today at 17:35", "yesterday at 09:02", or a date: "21 Sep at 23:59".</summary>
    public static string When(DateTimeOffset momentUtc, DateTimeOffset nowUtc, TimeZoneInfo zone, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(culture);
        DateTime moment = TimeZoneInfo.ConvertTime(momentUtc, zone).DateTime;
        DateTime today = TimeZoneInfo.ConvertTime(nowUtc, zone).Date;
        string time = moment.ToString("t", culture);
        return moment.Date == today ? "today at " + time
            : moment.Date == today.AddDays(-1) ? "yesterday at " + time
            : moment.ToString("d MMM", culture) + " at " + time;
    }
}
