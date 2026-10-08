using System.Globalization;
using System.Text.Json.Serialization;

namespace InterCat.Storage;

/// <summary>
/// A pin a person placed on a session (store-v1 §8, ADR-046). While it stands, no retention gives up a record read at or
/// after its moment: no interval release passes it, and no journal release, which cannot say when its records were read,
/// is made at all. The session may hold up to its allowance meanwhile; a follow that keeps a rolling window, and that the
/// pin holds back, stops rather than let the session grow past it. Session times are nanoseconds.
/// </summary>
public sealed record RetentionPin
{
    /// <summary>How many characters a pin's reason may hold.</summary>
    public const int MaximumReasonLength = 500;

    public required Guid Id { get; init; }

    /// <summary>The session time from which every record the session holds is kept.</summary>
    public required long FromNanoseconds { get; init; }

    /// <summary>
    /// The most the session may hold while the pin stands: every file its generation names, at the length its manifest
    /// measured, which is the size the session is stated at.
    /// </summary>
    public required long AllowanceBytes { get; init; }

    public required DateTimeOffset PlacedUtc { get; init; }

    /// <summary>Why the records are kept, in the words of whoever pinned them.</summary>
    public required string Reason { get; init; }

    /// <summary>How a person names the pin: the first eight hexadecimal digits of its identity.</summary>
    [JsonIgnore]
    public string ShortId => Id.ToString("N", CultureInfo.InvariantCulture)[..8];

    /// <summary>Why this pin cannot stand, or null when it can.</summary>
    public string? Validate() =>
        Id == Guid.Empty
            ? "A pin has an identity."
            : FromNanoseconds < 0
                ? "A pin keeps records from a session time, which is never before the session began."
                : AllowanceBytes <= 0
                    ? "A pin declares how much it lets the session hold."
                    : string.IsNullOrWhiteSpace(Reason) || Reason.Length > MaximumReasonLength
                        ? $"A pin says why it keeps the records, in at most {MaximumReasonLength} characters."
                        : null;
}

/// <summary>
/// `retention-pins.json` (store-v1 §8): the pins standing on a session. It lies beside the session's generations rather
/// than in them, because a person pins while a follow writes the session, and a generation any other writer published
/// would fail the follow. It is replaced whole under `retention-pins.lock`, which every release that could give up a
/// pinned record holds while it publishes.
/// </summary>
public sealed record RetentionPinsV1
{
    public const string FileName = "retention-pins.json";

    public const string LockFileName = "retention-pins.lock";

    public const int CurrentFormatVersion = 1;

    /// <summary>How many pins a session keeps at once.</summary>
    public const int MaximumPins = 64;

    /// <summary>The most a pins file may take: its pins and their reasons, with room to spare.</summary>
    internal const long MaximumFileBytes = 1024 * 1024;

    public required int FormatVersion { get; init; }

    public required Guid SessionId { get; init; }

    /// <summary>The pins, in the order they were placed.</summary>
    public required IReadOnlyList<RetentionPin> Pins { get; init; }

    /// <summary>Why this file cannot be the pins of <paramref name="sessionId"/>, or null when it can.</summary>
    public string? Validate(Guid sessionId)
    {
        if (FormatVersion != CurrentFormatVersion)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"it declares format {FormatVersion}, and this InterCat reads format {CurrentFormatVersion}");
        }

        if (SessionId == Guid.Empty || (sessionId != Guid.Empty && SessionId != sessionId))
        {
            return "it names another session";
        }

        if (Pins is null || Pins.Count > MaximumPins)
        {
            return string.Create(CultureInfo.InvariantCulture, $"it holds more than the {MaximumPins} pins a session keeps");
        }

        var identities = new HashSet<Guid>();
        foreach (RetentionPin pin in Pins)
        {
            if (pin is null || pin.Validate() is not null)
            {
                return "one of its pins is damaged";
            }

            if (!identities.Add(pin.Id))
            {
                return "it names one pin twice";
            }
        }

        return null;
    }
}

/// <summary>
/// A retention refused because a pin keeps what it would give up, or because the session's pins could not be read and so
/// might. It is an <see cref="InvalidOperationException"/>, as a release's other refusals are; a rolling policy can tell it
/// apart, since a pin placed while it planned is honored by planning again.
/// </summary>
public sealed class RetentionPinnedException(string message, RetentionPin? pin) : InvalidOperationException(message)
{
    /// <summary>The pin that refused the release; null when the pins could not be read.</summary>
    public RetentionPin? Pin { get; } = pin;
}
