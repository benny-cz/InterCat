using System.Globalization;

namespace InterCat.Domain;

/// <summary>A session time as a person reads it: seconds after the capture's epoch, to the nanosecond where it has them.</summary>
public static class SessionTimeText
{
    /// <summary>"12.345 s", "0.000010001 s": at least milliseconds, and every nanosecond the time has, in the reader's culture.</summary>
    public static string Seconds(long nanoseconds, IFormatProvider? culture = null) =>
        (nanoseconds / 1_000_000_000m).ToString("0.000######", culture ?? CultureInfo.CurrentCulture) + " s";

    /// <summary>
    /// A native reading as seconds after the capture epoch through <paramref name="clock"/>, to the microsecond and
    /// invariant - "12.345678" - as InterCat's documents and tables write a time: only through the clock the session
    /// describes (I8).
    /// </summary>
    public static string EpochSeconds(SourceClockDescriptor clock, long nativeTicks)
    {
        decimal seconds = (decimal)SourceClockMath.SessionNanoseconds(clock, nativeTicks) / SourceClockMath.SessionTicksPerSecond;
        return seconds.ToString("0.000000", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// "0.000100 s to 0.000103 s": a range's two ends to one precision, the finer end's - at least milliseconds, and every
    /// nanosecond either has - so neither reads coarser than the other, as "0.0001 s to 0.000103 s" did.
    /// </summary>
    public static string Range(long fromNanoseconds, long toNanoseconds, IFormatProvider? culture = null)
    {
        string format = "F" + Math.Max(Decimals(fromNanoseconds), Decimals(toNanoseconds)).ToString(CultureInfo.InvariantCulture);
        IFormatProvider reader = culture ?? CultureInfo.CurrentCulture;
        return (fromNanoseconds / 1_000_000_000m).ToString(format, reader) + " s to "
            + (toNanoseconds / 1_000_000_000m).ToString(format, reader) + " s";
    }

    /// <summary>The decimals a time needs to be said whole: from three, for milliseconds, to nine, for nanoseconds.</summary>
    private static int Decimals(long nanoseconds)
    {
        long fraction = Math.Abs(nanoseconds % 1_000_000_000);
        int decimals = 9;
        while (decimals > 3 && fraction % 10 == 0)
        {
            fraction /= 10;
            decimals--;
        }

        return decimals;
    }
}
