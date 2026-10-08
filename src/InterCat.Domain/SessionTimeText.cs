using System.Globalization;

namespace InterCat.Domain;

/// <summary>A session time as a person reads it: seconds after the capture's epoch, to the nanosecond where it has them.</summary>
public static class SessionTimeText
{
    /// <summary>"12.345 s", "0.000010001 s": at least milliseconds, and every nanosecond the time has, in the reader's culture.</summary>
    public static string Seconds(long nanoseconds, IFormatProvider? culture = null) =>
        (nanoseconds / 1_000_000_000m).ToString("0.000######", culture ?? CultureInfo.CurrentCulture) + " s";
}
