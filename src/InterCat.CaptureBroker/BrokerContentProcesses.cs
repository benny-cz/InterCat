using System.Globalization;

namespace InterCat.CaptureBroker;

/// <summary>
/// A process a content request names, as the broker read it from the process itself: its ID and when it started, which
/// together name it where its ID alone would not (R22).
/// </summary>
public sealed record BrokerNamedProcess(int ProcessId, DateTimeOffset StartedUtc);

/// <summary>What the broker reads of a running process from its own token and its own start, never from a request.</summary>
public sealed record BrokerProcessReading(BrokerClientIdentity Token, DateTimeOffset StartedUtc);

/// <summary>
/// The processes a content capture holds open while it records, each read through the handle that holds it, so no other
/// process can be given their IDs until it is disposed (ADR-037's holds).
/// </summary>
public interface IBrokerHeldProcesses : IDisposable
{
    /// <summary>What each held process's token and start said, read through the handle that holds it, by its ID.</summary>
    IReadOnlyDictionary<int, BrokerProcessReading> Readings { get; }
}

/// <summary>
/// Reads the process now holding an ID: the user, logon session and integrity its token states, and when it started.
/// Windows's reader holds it open with limited query rights and reads it through that handle.
/// </summary>
public interface IBrokerProcessReader
{
    /// <summary>
    /// The process now holding <paramref name="processId"/>; null when none is running or it cannot be read, with why in
    /// a clause that names it: "process 700 is not running, so its ID could be given to any process".
    /// </summary>
    BrokerProcessReading? Read(int processId, out string? problem);

    /// <summary>
    /// Holds each of <paramref name="processIds"/> open, read through the handle that holds it, until the result is
    /// disposed; null, with why as <see cref="Read"/> says it, when one cannot be held or read - and then none is held.
    /// </summary>
    IBrokerHeldProcesses? Hold(IReadOnlyList<int> processIds, out string? problem);
}

/// <summary>
/// ADR-049: the broker keeps content only from processes its client could read itself - its own user's, in its own logon
/// session, at no higher integrity - each named by its ID and start, read from the process rather than the request (P19).
/// </summary>
public static class BrokerContentProcesses
{
    /// <summary>
    /// The processes <paramref name="processIds"/> names, each pinned by its start, when every one is the client's own;
    /// otherwise null and why, naming the first that is not.
    /// </summary>
    public static IReadOnlyList<BrokerNamedProcess>? Pin(
        BrokerClientIdentity client,
        IReadOnlyList<int> processIds,
        IBrokerProcessReader reader,
        out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(processIds);
        ArgumentNullException.ThrowIfNull(reader);
        var pinned = new List<BrokerNamedProcess>(processIds.Count);
        foreach (int processId in processIds.Order())
        {
            BrokerProcessReading? reading = reader.Read(processId, out string? problem);
            if (Owned(client, processId, reading, problem, out refusal) is null)
            {
                return null;
            }

            pinned.Add(new(processId, reading!.StartedUtc));
        }

        refusal = null;
        return pinned;
    }

    /// <summary>
    /// Holds the processes a prepared content capture names and checks, through the handles that hold them, that each is
    /// still the process it was prepared for - started when it was, not another given its ID - and still the client's
    /// own (ADR-049 decision 2). Null, with why, when one is not; then nothing is held.
    /// </summary>
    public static IBrokerHeldProcesses? Hold(
        BrokerClientIdentity client,
        IReadOnlyList<BrokerNamedProcess> pinned,
        IBrokerProcessReader reader,
        out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(pinned);
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.Hold([.. pinned.Select(named => named.ProcessId)], out string? problem) is not { } held)
        {
            refusal = Unread(pinned.Count == 0 ? 0 : pinned[0].ProcessId, problem);
            return null;
        }

        refusal = Verify(client, pinned, held.Readings);
        if (refusal is not null)
        {
            held.Dispose();
            return null;
        }

        return held;
    }

    /// <summary>
    /// Why the processes a plan pinned may no longer be recorded, as read through the handles that hold them: one is no
    /// longer the process the plan named - its ID now names one that started at another time - or no longer the client's
    /// own. Null when every one still is.
    /// </summary>
    private static string? Verify(
        BrokerClientIdentity client,
        IReadOnlyList<BrokerNamedProcess> pinned,
        IReadOnlyDictionary<int, BrokerProcessReading> readings)
    {
        foreach (BrokerNamedProcess named in pinned)
        {
            BrokerProcessReading? reading = readings.GetValueOrDefault(named.ProcessId);
            if (Owned(client, named.ProcessId, reading, null, out string? refusal) is null)
            {
                return refusal;
            }

            if (reading!.StartedUtc != named.StartedUtc)
            {
                return string.Create(CultureInfo.InvariantCulture,
                    $"Process {named.ProcessId} is no longer the process the capture was prepared for: the one holding its ID ")
                    + $"started at {Instant(reading.StartedUtc)}, not {Instant(named.StartedUtc)}. "
                    + "Prepare the capture again for the process now running.";
            }
        }

        return null;
    }

    private static BrokerProcessReading? Owned(
        BrokerClientIdentity client,
        int processId,
        BrokerProcessReading? reading,
        string? problem,
        out string? refusal)
    {
        if (reading is null)
        {
            refusal = Unread(processId, problem);
            return null;
        }

        BrokerOwnerIdentity owner = client.Owner;
        BrokerOwnerIdentity its = reading.Token.Owner;
        refusal = !string.Equals(its.UserSid, owner.UserSid, StringComparison.Ordinal)
            ? string.Create(CultureInfo.InvariantCulture, $"Process {processId} runs as another user. ")
                + "Content is kept only from your own processes."
            : its.LogonSessionId != owner.LogonSessionId
                ? string.Create(CultureInfo.InvariantCulture, $"Process {processId} runs in another sign-in than the one asking, ")
                    + "such as an elevated one or another session. Content is kept only from processes of the sign-in that asks."
                : reading.Token.IntegrityLevel > client.IntegrityLevel
                    ? string.Create(CultureInfo.InvariantCulture, $"Process {processId} runs at a higher integrity than the one asking, ")
                        + "such as an elevated one. Content is kept only from processes you could read yourself."
                    : null;
        return refusal is null ? reading : null;
    }

    private static string Instant(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " UTC";

    /// <summary>Why a process that could not be held or read is not recorded, in the clause the reader gave.</summary>
    private static string Unread(int processId, string? problem)
    {
        string why = string.IsNullOrWhiteSpace(problem)
            ? string.Create(CultureInfo.InvariantCulture, $"process {processId} could not be read")
            : problem.TrimEnd('.');
        return char.ToUpperInvariant(why[0]) + why[1..] + ". Content is kept only from processes the broker can show are yours.";
    }
}
