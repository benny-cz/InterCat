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
/// Reads the process now holding an ID: the user, logon session and integrity its token states, and when it started.
/// Windows's reader opens it with limited query rights.
/// </summary>
public interface IBrokerProcessReader
{
    /// <summary>
    /// The process now holding <paramref name="processId"/>; null when none is running or it cannot be read, with why in
    /// a clause that names it: "process 700 is not running, so its ID could be given to any process".
    /// </summary>
    BrokerProcessReading? Read(int processId, out string? problem);
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
            if (Owned(client, processId, reader, out refusal) is not { } reading)
            {
                return null;
            }

            pinned.Add(new(processId, reading.StartedUtc));
        }

        refusal = null;
        return pinned;
    }

    /// <summary>
    /// Why the processes a plan pinned may no longer be recorded: one is no longer the process the plan named - its ID
    /// now names one that started at another time - or no longer the client's own. Null when every one still is.
    /// </summary>
    public static string? Verify(BrokerClientIdentity client, IReadOnlyList<BrokerNamedProcess> pinned, IBrokerProcessReader reader)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(pinned);
        ArgumentNullException.ThrowIfNull(reader);
        foreach (BrokerNamedProcess named in pinned)
        {
            if (Owned(client, named.ProcessId, reader, out string? refusal) is not { } reading)
            {
                return refusal;
            }

            if (reading.StartedUtc != named.StartedUtc)
            {
                return string.Create(CultureInfo.InvariantCulture,
                    $"Process {named.ProcessId} is no longer the process the capture was prepared for: the one holding its ID ")
                    + string.Create(CultureInfo.InvariantCulture,
                        $"started at {reading.StartedUtc.UtcDateTime:yyyy-MM-dd HH:mm:ss.fff} UTC, not {named.StartedUtc.UtcDateTime:yyyy-MM-dd HH:mm:ss.fff} UTC. ")
                    + "Prepare the capture again for the process now running.";
            }
        }

        return null;
    }

    private static BrokerProcessReading? Owned(BrokerClientIdentity client, int processId, IBrokerProcessReader reader, out string? refusal)
    {
        if (reader.Read(processId, out string? problem) is not { } reading)
        {
            string why = string.IsNullOrWhiteSpace(problem)
                ? string.Create(CultureInfo.InvariantCulture, $"process {processId} could not be read")
                : problem.TrimEnd('.');
            refusal = char.ToUpperInvariant(why[0]) + why[1..] + ". Content is kept only from processes the broker can show are yours.";
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
}
