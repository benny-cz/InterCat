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
    /// The size line's sentence about the pins standing on a session (§12.1 S5): from when the earliest keeps every record,
    /// and the least any lets the session hold. Null when none stands.
    /// </summary>
    public static string? SizeLine(IReadOnlyList<RetentionPin> pins)
    {
        ArgumentNullException.ThrowIfNull(pins);
        if (pins.Count == 0)
        {
            return null;
        }

        RetentionPin earliest = pins.MinBy(pin => pin.FromNanoseconds)!;
        return $"Pinned from {SessionTimeText.Seconds(earliest.FromNanoseconds, CultureInfo.CurrentCulture)} ({earliest.Reason})"
            + (pins.Count > 1 ? " and by " + CountText.Of(pins.Count - 1, "more pin") : string.Empty)
            + ": every record from then on is kept through every release, and the session may hold "
            + ByteSizeText.Of(pins.Min(pin => pin.AllowanceBytes)) + ".";
    }

    /// <summary>What the size line says of pins that could not be read: why, and that nothing is released meanwhile.</summary>
    public static string Unreadable(string problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(problem);
        return problem + " No release is made until they can be read.";
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
