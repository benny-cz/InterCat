using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// Waits as a person reads them, in one set of words for the command line and the window (R18, `contracts/waits-v1.md`):
/// who served a caller's calls or why none is named, how long the calls took on each side, and what no time is said for.
/// </summary>
public static class WaitText
{
    /// <summary>What a list of waits says of itself, the command line and the window alike.</summary>
    public const string Note = "A wait is the time from a call's start to its stop on the process that made it, for calls whose "
        + "start and stop were both recorded. A process can make several calls at once, and a call can return before its work "
        + "is done, so waits can add up to more than the time that passed, and none is said to have blocked or delayed "
        + "anything. Where a call is linked to the server call that served it, the server's time is that call's own; the rest "
        + "of the wait is not attributed.";

    /// <summary>A process as a list of waits names it, as the overview does.</summary>
    public static string Named(WaitReading reading, ProcessInstanceId process)
    {
        ArgumentNullException.ThrowIfNull(reading);
        return reading.Processes.TryGetValue(process, out ProcessNode? node) ? CollectorText.Named(node) : "a process the overview did not name";
    }

    /// <summary>
    /// Who served a wait's calls: the server process, or why none is named - "not linked: no ALPC send on its thread
    /// during the call" where one reason covers them all, else each reason with how many: "not linked: 2 served by a process
    /// the evidence policy does not admit; 1 no ALPC send on its thread during the call".
    /// </summary>
    public static string Served(WaitReading reading, CallWait wait)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(wait);
        if (wait.Server is { } server) return Named(reading, server);
        var why = new List<(long Calls, string Said)>();
        why.AddRange(wait.Unresolved.Select(entry => (entry.Value, OperationText.PeerState(entry.Key))));
        if (wait.ServerNotAdmitted > 0)
        {
            why.Add((wait.ServerNotAdmitted, "served by a process the evidence policy does not admit"));
        }

        return "not linked: " + (why.Count == 1 ? why[0].Said : string.Join("; ", why
            .OrderByDescending(reason => reason.Calls)
            .ThenBy(reason => reason.Said, StringComparer.Ordinal)
            .Select(reason => string.Create(CultureInfo.CurrentCulture, $"{reason.Calls:N0} {reason.Said}"))));
    }

    /// <summary>
    /// The server's time beside the caller's, over the calls linked to a server call that completed: "12.0 µs of 30.0 µs
    /// (40%)", with how many of the calls that is where not all of them; "-" where none is.
    /// </summary>
    public static string ServerRan(long linked, long calls, long linkedInCalls, long serverRan, IFormatProvider? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (linked == 0) return "-";
        long share = linkedInCalls == 0 ? 0 : serverRan * 100 / linkedInCalls;
        return OperationText.Duration(serverRan, culture) + " of " + OperationText.Duration(linkedInCalls, culture)
            + string.Create(culture, $" ({share:N0}%)")
            + (linked == calls ? string.Empty : string.Create(culture, $", over {linked:N0} of the {calls:N0} calls"));
    }

    /// <summary>
    /// What no time is said for, in one sentence: "No time is said for 2 calls whose span is not validated: 1 open at capture
    /// end and 1 start not observed." Null where every call is counted.
    /// </summary>
    public static string? NotCounted(WaitReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        var parts = new List<string>();
        long unvalidated = reading.NotValidated.Values.Sum();
        if (unvalidated > 0)
        {
            parts.Add(CountText.Of(unvalidated, "call", "calls") + " whose span is not validated: " + WorkspaceLayout.Series(
            [
                .. reading.NotValidated
                    .OrderByDescending(entry => entry.Value)
                    .ThenBy(entry => entry.Key)
                    .Select(entry => string.Create(CultureInfo.CurrentCulture, $"{entry.Value:N0} ") + OperationText.State(entry.Key)),
            ]));
        }

        if (reading.NotAdmitted > 0)
        {
            parts.Add(CountText.Of(reading.NotAdmitted, "call", "calls") + $" the {CommandArgument.Typed(reading.Policy)} evidence "
                + "policy does not admit the caller of");
        }

        return parts.Count > 0 ? "No time is said for " + string.Join(", or ", parts) + "." : null;
    }
}
