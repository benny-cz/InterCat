using System.Globalization;
using InterCat.Domain;

namespace InterCat.Storage;

/// <summary>
/// The clock descriptor for evidence recorded outside InterCat. Such a file carries readings but none of
/// our identities, and both of the identities it needs have to come from the evidence itself: minting a
/// fresh clock ID per import would make two imports of one file disagree about which clock its readings
/// are on - and because the clock ID is inside the canonical record key, their records would not even
/// have the same identity. Claiming the local host would be worse: it would let imported readings be
/// compared against local captures as though they shared a clock.
/// </summary>
public static class ImportedSourceClock
{
    public const string ClockDomain = "InterCat.Import.DerivedClock.v1";

    public const string HostDomain = "InterCat.Import.DerivedHost.v1";

    /// <summary>The largest session-relative distance a converted reading may reach before quarantine.</summary>
    public static readonly TimeSpan DefaultSessionBound = TimeSpan.FromDays(7);

    public static SourceClockDescriptor Derive(
        ImportSourceIdentity source,
        long ticksPerSecond,
        long referenceNativeTicks,
        TimestampEncoding encoding = TimestampEncoding.Qpc,
        SourceClockKind kind = SourceClockKind.Monotonic)
    {
        if (string.IsNullOrEmpty(source.ContentDigest))
        {
            throw new ArgumentException("A derived clock needs a hashed source.", nameof(source));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(ticksPerSecond, 1);
        string canonical = string.Create(CultureInfo.InvariantCulture, $"{source}|{encoding}|{ticksPerSecond}");
        return new(
            ClockId.Derive($"{ClockDomain}|{canonical}"),
            HostId.Derive($"{HostDomain}|{source}"),
            kind,
            encoding,
            ticksPerSecond,
            referenceNativeTicks,
            TimestampRounding.NearestEven,
            (long)(DefaultSessionBound.TotalSeconds * SourceClockMath.SessionTicksPerSecond));
    }
}
