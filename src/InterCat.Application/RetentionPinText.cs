using System.Globalization;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>A pin on a session in a person's words, the same wherever it is stated (R18, ADR-046).</summary>
public static class RetentionPinText
{
    /// <summary>
    /// What a pin keeps and allows, when and why it was placed: "keeps every record from 312.500 s · allows the session
    /// 2.0 GiB · placed 2026-10-08 14:05 UTC · the failover at 14:03".
    /// </summary>
    /// <param name="moment">
    /// The pin's moment as the reader places instants, such as a wall-clock time beside session time; its session time when
    /// null.
    /// </param>
    public static string Describe(RetentionPin pin, string? moment = null)
    {
        ArgumentNullException.ThrowIfNull(pin);
        return "keeps every record from " + (moment ?? SessionTimeText.Seconds(pin.FromNanoseconds, CultureInfo.CurrentCulture))
            + " · allows the session " + ByteSizeText.Of(pin.AllowanceBytes)
            + string.Create(CultureInfo.InvariantCulture, $" · placed {pin.PlacedUtc.UtcDateTime:yyyy-MM-dd HH:mm} UTC")
            + " · " + pin.Reason;
    }

    /// <summary>
    /// What a follow keeping a rolling window says once a pin holds it back: from then on the session keeps more than its
    /// window, up to what the pin allows.
    /// </summary>
    public static string HoldsWindow(RetentionPin pin, RollingRetentionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(pin);
        ArgumentNullException.ThrowIfNull(policy);
        return $"A pin keeps every record read from {SessionTimeText.Seconds(pin.FromNanoseconds, CultureInfo.CurrentCulture)} "
            + $"({pin.Reason}), so the session keeps more than {policy.Window} from here on, up to the "
            + $"{ByteSizeText.Of(pin.AllowanceBytes)} the pin allows.";
    }
}
