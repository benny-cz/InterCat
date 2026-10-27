using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// A process's links as a person reads them, in one set of words for the command line and the window (R18,
/// `contracts/links-v1.md`): what each link's other end is to the process, the evidence it rests on, how strong it is,
/// what was created near the process without a link, and what of it links to nothing.
/// </summary>
public static class LinkText
{
    /// <summary>What a view of links says of itself, the command line and the window alike.</summary>
    public const string Note = "A link is a recorded fact by one documented rule: a child's creation record naming its parent, a "
        + "call reaching the call that served it through its one ALPC message, a TCP connection the capture saw one end make. "
        + "Only the links the evidence policy admits are followed; a chain of them claims no more than its links, never a "
        + "cause of what a process did, and a process created near another in time is never linked to it by that alone.";

    /// <summary>An end as a list of links names it: a process as the overview does, a host by its address.</summary>
    public static string Named(LinkReading reading, LinkEnd end)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(end);
        return end.Process is { } id
            ? reading.Processes.TryGetValue(id, out ProcessNode? node) ? CollectorText.Named(node) : "a process the overview did not name"
            : end.Address ?? "an end on this machine no record holds";
    }

    /// <summary>The end of <paramref name="link"/> that is not <paramref name="step"/>: what a list of the step's links names.</summary>
    public static LinkEnd Other(EvidenceLink link, ProcessInstanceId step)
    {
        ArgumentNullException.ThrowIfNull(link);
        return link.From.Process == step ? link.To : link.From;
    }

    /// <summary>
    /// What a link's other end is to <paramref name="step"/>: "its parent", "called it", "connected to it" for what led to
    /// it; "its child", "served its calls", "it connected to" for what it led to.
    /// </summary>
    public static string Role(EvidenceLink link, ProcessInstanceId step)
    {
        ArgumentNullException.ThrowIfNull(link);
        bool led = link.From.Process == step;
        return link.Kind switch
        {
            LinkKind.Started => led ? "its child" : "its parent",
            LinkKind.Called => led ? "served its calls" : "called it",
            _ => led ? "it connected to" : "connected to it",
        };
    }

    /// <summary>
    /// The evidence a link rests on, from <paramref name="step"/>'s side, with how often it holds: "the child's creation record
    /// names the parent by its start key", "3 calls to svcctl (Service Control Manager), each reaching the call that served it
    /// through its one ALPC message", "2 connections to port 8080 that it made, as the capture saw them open".
    /// </summary>
    public static string Evidence(EvidenceLink link, ProcessInstanceId step, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(link);
        culture ??= CultureInfo.CurrentCulture;
        if (link.Kind == LinkKind.Started)
        {
            return link.Strength == RelationStrength.Direct
                ? "the child's creation record names the parent by its start key"
                : "the child's creation record names the parent's PID alone, which the parent held when the child was created"
                    + (link.Strength == RelationStrength.Correlated ? string.Empty : ", after another process had held it");
        }

        if (link.Kind == LinkKind.Called)
        {
            return CountText.Of(link.Occurrences, "call", "calls") + " to " + RpcInterfaceNames.Describe(link.Interface)
                + (link.Occurrences == 1 ? ", reaching the call that served it" : ", each reaching the call that served it")
                + " through its one ALPC message";
        }

        bool led = link.From.Process == step;
        LinkEnd other = Other(link, step);
        string port = (link.Port ?? 0).ToString(culture);
        string connections = CountText.Of(link.Occurrences, "connection", "connections");
        string them = link.Occurrences == 1 ? "it" : "them";
        return (led, other) switch
        {
            (true, { Process: not null }) => $"{connections} to port {port} that it made, as the capture saw {them} open",
            (false, { Process: not null }) => $"{connections} to its port {port} that the other end made, as the capture saw {them} open",
            (true, { Address: not null }) => $"{connections} to port {port} at that address that it made; who is at an address is not observed",
            (false, { Address: not null }) => $"{connections} to its port {port} that it accepted from that address; who is at an "
                + "address is not observed",
            (true, _) => $"{connections} to port {port} on this machine that it made; no record holds the other end",
            _ => $"{connections} to its port {port} that it accepted from an end on this machine no record holds",
        };
    }

    /// <summary>
    /// What of a process began outside the interval its links were read over, in one sentence: "3 of the starts, calls,
    /// connections and flows it took part in began outside the interval, so none of them is listed here." Null where none
    /// did, or where no interval was read.
    /// </summary>
    public static string? Outside(StepLinks links)
    {
        ArgumentNullException.ThrowIfNull(links);
        return links.OutsideInterval == 0 ? null
            : string.Create(CultureInfo.CurrentCulture, $"{links.OutsideInterval:N0} of the starts, calls, connections and flows it ")
                + "took part in began outside the interval, so " + (links.OutsideInterval == 1 ? "it is not" : "none of them is")
                + " listed here.";
    }

    /// <summary>A link's strength as a word: "direct", "correlated", "candidate".</summary>
    public static string Strength(RelationStrength strength) => strength.ToString().ToLowerInvariant();

    /// <summary>
    /// One link of a chain as a sentence, from the process it leaves to the one it reaches: "explorer.exe · PID 300 started
    /// app.exe · PID 400", "app.exe · PID 400's calls were served by services.exe · PID 1960", "browser.exe · PID 3000
    /// connected to server.exe · PID 4000".
    /// </summary>
    public static string Step(LinkReading reading, EvidenceLink link)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(link);
        string from = Named(reading, link.From);
        string to = Named(reading, link.To);
        return link.Kind switch
        {
            LinkKind.Started => $"{from} started {to}",
            LinkKind.Called => $"{from}'s calls were served by {to}",
            _ => $"{from} connected to {to}",
        };
    }

    /// <summary>How near in time another process was created: "created 120 ms after it", "… before it", or at the same reading.</summary>
    public static string Near(NearProcess near, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(near);
        return near.OffsetTicks == 0
            ? "created at the same reading as it"
            : "created " + WorkspaceTime.FormatDuration(Math.Abs(near.OffsetTicks), culture) + (near.OffsetTicks > 0 ? " after it" : " before it");
    }

    /// <summary>
    /// What of a process links it to nothing, in one sentence beneath a heading that says so: "2 TCP connections whose
    /// opening no record shows, …; 1 UDP flow, which nothing opens." Null where nothing of it is unlinked.
    /// </summary>
    public static string? Unlinked(UnlinkedActivity unlinked)
    {
        ArgumentNullException.ThrowIfNull(unlinked);
        var parts = new List<string>();
        if (unlinked.UnheldParent is { } parent)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"its creation record names parent PID {parent}, and no process of the ")
                + "capture is linked as that parent");
        }

        if (unlinked.UnopenedConnections > 0)
        {
            parts.Add(CountText.Of(unlinked.UnopenedConnections, "TCP connection", "TCP connections")
                + " whose opening no record shows, so neither end is known to have made them");
        }

        if (unlinked.Flows > 0)
        {
            parts.Add(CountText.Of(unlinked.Flows, "UDP flow", "UDP flows") + ", which nothing opens");
        }

        if (unlinked.UnresolvedCalls.Count > 0)
        {
            // One reason is said alone; several each with how many calls it left unresolved, the most first.
            long calls = unlinked.UnresolvedCalls.Values.Sum();
            parts.Add(CountText.Of(calls, "call", "calls") + " whose other end is unresolved: " + (unlinked.UnresolvedCalls.Count == 1
                ? OperationText.PeerState(unlinked.UnresolvedCalls.Keys.Single())
                : WorkspaceLayout.Series(
                [
                    .. unlinked.UnresolvedCalls
                        .OrderByDescending(entry => entry.Value)
                        .ThenBy(entry => entry.Key)
                        .Select(entry => string.Create(CultureInfo.CurrentCulture, $"{entry.Value:N0} ") + OperationText.PeerState(entry.Key)),
                ])));
        }

        if (unlinked.CallsNotReached > 0)
        {
            parts.Add(CountText.Of(unlinked.CallsNotReached, "call", "calls")
                + " it served that no linked client call reached: a client may be remote, not use ALPC, or not be in the capture");
        }

        string said = string.Join("; ", parts);
        return parts.Count > 0 ? char.ToUpperInvariant(said[0]) + said[1..] + "." : null;
    }
}
