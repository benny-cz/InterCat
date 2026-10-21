using System.Globalization;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// Who made a relationship's connections, in the words its edge's card, its row in the relationship table and the row's
/// explanation share (§6.3, §6.8, `contracts/relations-v1.md` §5): the end whose connect - or whose peer's accept - the
/// capture saw, counted where they went both ways or some opened unseen, and display order only where it saw none open.
/// The graph draws the same as its arrowheads.
/// </summary>
public static class DirectionText
{
    /// <summary>
    /// The line a card and an explanation state: "Direction: client.exe · PID 100 connected to server.exe · PID 200",
    /// "..., in all 4 of its connections" where there were more, each way counted where they went both ways or some opened
    /// unseen; display order only where none was seen to open, as for a UDP flow, which nothing opens. Of RPC calls, which
    /// end called the other (<see cref="Calls"/>).
    /// </summary>
    /// <param name="fromSource">How many of its connections <paramref name="source"/> made, or of its RPC calls.</param>
    /// <param name="fromTarget">How many <paramref name="target"/> made.</param>
    /// <param name="connections">How many it holds, opened where the capture saw it or not; an RPC relationship's calls
    /// are its two ends' together.</param>
    public static string Line(Mechanism mechanism, int fromSource, int fromTarget, int connections, string source, string target)
    {
        if (mechanism == Mechanism.Rpc)
        {
            return Calls(fromSource, fromTarget, source, target);
        }

        if (fromSource + fromTarget == 0)
        {
            return "Direction: display order only, not who initiated or sent";
        }

        string all = connections > 1
            ? string.Create(CultureInfo.CurrentCulture, $", in all {connections:N0} of its connections")
            : string.Empty;
        if (fromSource == connections)
        {
            return $"Direction: {source} connected to {target}{all}";
        }

        if (fromTarget == connections)
        {
            return $"Direction: {target} connected to {source}{all}";
        }

        int unseen = connections - fromSource - fromTarget;
        return "Direction: " + string.Join(", ", new[]
            {
                fromSource > 0 ? $"{source} connected to {target} in " + CountText.Of(fromSource, "connection") : null,
                fromTarget > 0 ? $"{target} connected to {source} in " + CountText.Of(fromTarget, "connection") : null,
            }.OfType<string>())
            + (unseen > 0 ? "; the capture saw " + CountText.Of(unseen, "connection") + " open neither way" : string.Empty);
    }

    /// <summary>
    /// An RPC relationship's direction line: which end called the other, as each call's link to the call that served it
    /// says (`contracts/operations-v1.md` §5c) - "Direction: caller.exe · PID 400 called services.exe · PID 700, in all 2
    /// of its calls", each way counted where both called - and display order only where no call of it names its caller.
    /// </summary>
    private static string Calls(int fromSource, int fromTarget, string source, string target)
    {
        int calls = fromSource + fromTarget;
        if (calls == 0)
        {
            return "Direction: display order only; each end's RPC rows say which calls and which served";
        }

        string all = calls > 1 ? string.Create(CultureInfo.CurrentCulture, $", in all {calls:N0} of its calls") : string.Empty;
        return fromTarget == 0 ? $"Direction: {source} called {target}{all}"
            : fromSource == 0 ? $"Direction: {target} called {source}{all}"
            : $"Direction: {source} called {target} in " + CountText.Of(fromSource, "call") + $", {target} called {source} in "
                + CountText.Of(fromTarget, "call");
    }

    /// <summary>
    /// The mark a table sets between a relationship's ends: "→" where only the source connected, "←" where only the
    /// target did, "⇄" where each did, and none where the capture saw none open, so no direction is invented (§6.6).
    /// </summary>
    public static string Glyph(int fromSource, int fromTarget) => (fromSource > 0, fromTarget > 0) switch
    {
        (true, false) => "→",
        (false, true) => "←",
        (true, true) => "⇄",
        _ => string.Empty,
    };

    /// <summary>
    /// Two ends named as the relationship table names a relationship's, the end that made its connections first: "client.exe
    /// · PID 100 → server.exe · PID 200" whichever end <paramref name="source"/> is, "a ⇄ b" where each made some, and "a ↔
    /// b", in the order given, where the capture saw none open, so a card's title never contradicts its direction line.
    /// </summary>
    public static string Joined(int fromSource, int fromTarget, string source, string target) => (fromSource > 0, fromTarget > 0) switch
    {
        (true, false) => $"{source} → {target}",
        (false, true) => $"{target} → {source}",
        (true, true) => $"{source} ⇄ {target}",
        _ => $"{source} ↔ {target}",
    };

    /// <summary>
    /// How many TCP connections a set of processes made and accepted over the whole session, as the capture saw each open at
    /// either end (`relations-v1` §5): their own, each counted for the end that connected and the end that accepted, so one
    /// between two of them is one the set made and one it accepted, and one a process made to itself both. A ranking of the
    /// whole session by connections made or accepted reads the same.
    /// </summary>
    public static (long Made, long Accepted) Opened(WorkspaceSnapshot snapshot, IReadOnlySet<ProcessInstanceId> members)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(members);
        long made = 0;
        long accepted = 0;
        foreach (ProcessNode process in snapshot.Processes.Where(process => members.Contains(process.Id)))
        {
            made += process.ConnectionsMade;
            accepted += process.ConnectionsAccepted;
        }

        return (made, accepted);
    }

    /// <summary>
    /// The processes' own ports the connections a set of them accepted arrived at, each once, the lowest first: the ports
    /// they listened at, as each process's <see cref="ProcessNode.AcceptedPorts"/> holds them.
    /// </summary>
    public static IReadOnlyList<ushort> AcceptedPorts(WorkspaceSnapshot snapshot, IReadOnlySet<ProcessInstanceId> members)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(members);
        return [.. snapshot.Processes.Where(process => members.Contains(process.Id))
            .SelectMany(process => process.AcceptedPorts).Distinct().Order()];
    }

    /// <summary>
    /// What a node's card says of them: "Connections: 3 made · 12 accepted at port 443, as the capture saw them open"; null
    /// where it saw none of theirs open.
    /// </summary>
    public static string? OpenedLine(long made, long accepted, IReadOnlyList<ushort>? acceptedPorts = null) =>
        OpenedCounts(made, accepted, acceptedPorts) is { } counts ? "Connections: " + counts : null;

    /// <summary>
    /// The same counts as the inspector states them beneath its own heading, the accepted ones at the ports they arrived
    /// at, which the processes listened at: "3 made · 12 accepted at ports 443 and 8443, as the capture saw them open";
    /// null where it saw none of theirs open.
    /// </summary>
    public static string? OpenedCounts(long made, long accepted, IReadOnlyList<ushort>? acceptedPorts = null) => made + accepted == 0
        ? null
        : string.Create(CultureInfo.CurrentCulture, $"{made:N0} made · {accepted:N0} accepted")
            + (acceptedPorts is { Count: > 0 } ports ? " at " + HostLinkText.Ports(ports) : string.Empty)
            + ", as the capture saw them open";

    /// <summary>
    /// The same as a screen reader says it within a row's name: "client.exe · PID 100 connected to server.exe · PID 200",
    /// or "each connected to the other"; null where none was seen to open, so the name says nothing of it.
    /// </summary>
    public static string? Spoken(int fromSource, int fromTarget, string source, string target) =>
        Spoken(fromSource, fromTarget, source, target, Mechanism.Tcp);

    /// <summary>
    /// The same of a relationship over <paramref name="mechanism"/>: of RPC calls, "caller.exe · PID 400 called
    /// services.exe · PID 700", or "each called the other".
    /// </summary>
    public static string? Spoken(int fromSource, int fromTarget, string source, string target, Mechanism mechanism)
    {
        string verb = mechanism == Mechanism.Rpc ? "called" : "connected to";
        return (fromSource > 0, fromTarget > 0) switch
        {
            (true, false) => $"{source} {verb} {target}",
            (false, true) => $"{target} {verb} {source}",
            (true, true) => $"each {verb} the other",
            _ => null,
        };
    }
}
