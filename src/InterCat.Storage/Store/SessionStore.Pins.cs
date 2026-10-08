using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using InterCat.Domain;

namespace InterCat.Storage;

/// <summary>
/// The pins a person places on a session, and the check every release that could give up a pinned record makes while it
/// publishes (store-v1 §8, ADR-046).
/// </summary>
public sealed partial class SessionStore
{
    /// <summary>
    /// How long placing or removing a pin, or a release, waits while another holds the session's pins. Each holds them for
    /// one publication at most.
    /// </summary>
    private static readonly TimeSpan PinsLockWait = TimeSpan.FromSeconds(10);

    /// <summary>The pins standing on this session, earliest moment first, then in the order they were placed; empty when none does.</summary>
    /// <exception cref="InvalidDataException">
    /// The pins file cannot be read: it is damaged, of a later format, or names another session. No release that could give
    /// up a pinned record is made while it cannot be read.
    /// </exception>
    public IReadOnlyList<RetentionPin> Pins() =>
        ReadPins() is { } pins ? [.. pins.Pins.OrderBy(pin => pin.FromNanoseconds)] : [];

    /// <summary>
    /// Places a pin: while it stands, every record this session holds that was read at or after
    /// <paramref name="fromNanoseconds"/> stays through every retention, and the session may hold
    /// <paramref name="allowanceBytes"/>. Refused when records read at or after the moment were already released, when the
    /// session already holds more than the allowance, and when the session keeps as many pins as it can.
    /// </summary>
    public RetentionPin Pin(long fromNanoseconds, long allowanceBytes, string reason, DateTimeOffset placedUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var pin = new RetentionPin
        {
            Id = Guid.NewGuid(),
            FromNanoseconds = fromNanoseconds,
            AllowanceBytes = allowanceBytes,
            PlacedUtc = placedUtc.ToUniversalTime(),
            Reason = reason.Trim(),
        };
        if (pin.Validate() is { } problem)
        {
            throw new ArgumentException(problem);
        }

        using FileStream pinsLock = AcquirePinsLock();

        // Every release that gives up records holds the pins while it publishes, so the generation read now names the latest
        // release there is while this pin is written.
        using EvidenceLease lease = AcquireLease();
        SessionManifestV1 manifest = lease.Manifest;
        if (manifest.LatestRelease(RetentionExtentKind.Interval) is { Record: { Interval: { } interval } record }
            && fromNanoseconds < interval.BoundaryNanoseconds)
        {
            string boundary = Seconds(interval.BoundaryNanoseconds);
            throw new InvalidOperationException(
                $"The records read before {boundary} were released ({record.Reason}), so no pin can keep them: the earliest "
                + $"moment a pin keeps every record from is {boundary}. No pin was placed.");
        }

        long held = manifest.HeldBytes();
        if (allowanceBytes < held)
        {
            throw new InvalidOperationException(
                $"This session holds {ByteSizeText.Of(held)}, and a pin allowing it {ByteSizeText.Of(allowanceBytes)} would "
                + "allow less than it already keeps: a pin allows the session at least what it holds. No pin was placed.");
        }

        RetentionPinsV1 pins = ReadPinsToChange("No pin was placed.")
            ?? new() { FormatVersion = RetentionPinsV1.CurrentFormatVersion, SessionId = manifest.SessionId, Pins = [] };
        if (pins.Pins.Count >= RetentionPinsV1.MaximumPins)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.CurrentCulture,
                $"This session has {RetentionPinsV1.MaximumPins} pins, the most it keeps. Remove one before placing another. No pin was placed."));
        }

        Write(directory, RetentionPinsV1.FileName, pins with { Pins = [.. pins.Pins, pin] }, SessionManifestV1.Json);
        return pin;
    }

    /// <summary>
    /// Removes the pin with <paramref name="id"/>, which no longer keeps anything from the next release on, and returns it;
    /// null when no pin of this session has it.
    /// </summary>
    public RetentionPin? Unpin(Guid id)
    {
        using FileStream pinsLock = AcquirePinsLock();
        RetentionPinsV1? pins = ReadPinsToChange("No pin was removed.");
        if (pins?.Pins.FirstOrDefault(pin => pin.Id == id) is not { } removed)
        {
            return null;
        }

        Write(directory, RetentionPinsV1.FileName, pins with { Pins = [.. pins.Pins.Where(pin => pin.Id != id)] }, SessionManifestV1.Json);
        return removed;
    }

    /// <summary>
    /// Refuses an interval release whose boundary passes a pin - one that gives up a record read at or after the pin's
    /// moment, since every record it gives up was read before its boundary - and any release while the pins cannot be read.
    /// Called with the pins held, until the release has published.
    /// </summary>
    private void RequirePinsAllowInterval(long boundaryNanoseconds)
    {
        if (PinsForRelease().FirstOrDefault(pin => pin.FromNanoseconds < boundaryNanoseconds) is { } passed)
        {
            string from = Seconds(passed.FromNanoseconds);
            throw new RetentionPinnedException(
                $"A pin keeps every record read from {from} ({passed.Reason}), and this release would give up records read "
                + $"after it. Release before {from}, or remove the pin. Nothing was published.",
                passed);
        }
    }

    /// <summary>
    /// Refuses a release made by record number while any pin stands: it cannot say when its records were read, so it could
    /// give up a pinned one. Called with the pins held, until the release has published.
    /// </summary>
    private void RequireNoPins()
    {
        if (PinsForRelease() is [var first, ..])
        {
            throw new RetentionPinnedException(
                $"A pin keeps every record read from {Seconds(first.FromNanoseconds)} ({first.Reason}). A release by record "
                + "number cannot say when its records were read, so none is made while a pin stands: release the interval "
                + "before the pin by its session time instead, or remove the pin. Nothing was published.",
                first);
        }
    }

    private IReadOnlyList<RetentionPin> PinsForRelease()
    {
        try
        {
            return Pins();
        }
        catch (InvalidDataException exception)
        {
            throw new RetentionPinnedException(
                exception.Message + " No release is made that a pin might forbid, so nothing was published.", null);
        }
    }

    /// <summary>The pins as a change starts from: an unreadable file is refused rather than written over, losing its pins.</summary>
    private RetentionPinsV1? ReadPinsToChange(string nothingChanged)
    {
        try
        {
            return ReadPins();
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException(exception.Message + " " + nothingChanged, exception);
        }
    }

    private RetentionPinsV1? ReadPins()
    {
        if (!Exists(directory, RetentionPinsV1.FileName))
        {
            return null;
        }

        RetentionPinsV1? pins;
        try
        {
            using FileStream stream = directory.OpenOwnedFile(
                RetentionPinsV1.FileName, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
            if (stream.Length > RetentionPinsV1.MaximumFileBytes)
            {
                throw UnreadablePins("it is larger than a pins file can be");
            }

            pins = JsonSerializer.Deserialize<RetentionPinsV1>(stream, SessionManifestV1.Json);
        }
        catch (JsonException exception)
        {
            throw UnreadablePins("it is not a pins file this InterCat can read", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw UnreadablePins(exception.Message.TrimEnd('.'), exception);
        }

        Guid session = SessionId != Guid.Empty ? SessionId : Current?.SessionId ?? Guid.Empty;
        return (pins is null ? "it holds nothing" : pins.Validate(session)) is { } problem ? throw UnreadablePins(problem) : pins;
    }

    private static InvalidDataException UnreadablePins(string problem, Exception? inner = null) =>
        new($"This session's pins, in {RetentionPinsV1.FileName}, could not be read: {problem}.", inner);

    private FileStream AcquirePinsLock()
    {
        long waitStarted = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                return directory.OpenOwnedFile(
                    RetentionPinsV1.LockFileName, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, FileOptions.None);
            }
            catch (IOException exception) when (exception is not DirectoryNotFoundException
                && Stopwatch.GetElapsedTime(waitStarted) < PinsLockWait)
            {
                Thread.Sleep(10);
            }
            catch (IOException exception) when (exception is not DirectoryNotFoundException)
            {
                throw new IOException(
                    "Could not take the session's pins: another process is placing or removing a pin, or releasing records, "
                    + "and did not finish within ten seconds. Retry once it has.",
                    exception);
            }
        }
    }

    private static string Seconds(long nanoseconds) => SessionTimeText.Seconds(nanoseconds, CultureInfo.CurrentCulture);
}
