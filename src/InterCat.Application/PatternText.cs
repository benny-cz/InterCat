using System.Globalization;
using InterCat.Analysis;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>
/// A session's communication patterns as a person reads them, in one set of words for the command line and the window
/// (R18, `contracts/patterns-v1.md`): how often a pattern occurred, who took part in each role, when, and what no pattern
/// holds.
/// </summary>
public static class PatternText
{
    /// <summary>
    /// What a list of patterns says of itself beneath it, the command line and the window alike: its tenths are the
    /// session's, or the interval's where <paramref name="interval"/> keeps it to what began in one.
    /// </summary>
    public static string Note(TimeRange? interval) =>
        "A pattern groups what recurs by its mechanism, its service port or RPC interface and procedure, and where its server "
        + "end is, never by a process, address or time; its clients and servers say who took part, a process by its "
        + "executable's processes counted as programs. Tenths says in how many tenths of the "
        + (interval is null ? "session" : "interval") + " an occurrence began.";

    /// <summary>What a list of a pattern's ends says where one is another host.</summary>
    public const string HostNote = "Another host is named by its address: who is at it is not observed.";

    /// <summary>How many times a pattern occurred, in its unit: "20 connections", "6 flows", "46 calls made · 46 served".</summary>
    public static string Occurred(CommunicationPattern pattern, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        culture ??= CultureInfo.CurrentCulture;
        return pattern.Mechanism == Mechanism.Rpc
            ? CountText.Of(pattern.Occurrences, "call made", "calls made") + string.Create(culture, $" · {pattern.Served:N0} served")
            : CountText.Of(pattern.Occurrences, pattern.Mechanism == Mechanism.Tcp ? "connection" : "flow",
                pattern.Mechanism == Mechanism.Tcp ? "connections" : "flows");
    }

    /// <summary>A role's occurrences' unit as a column names it: "connections", "flows", "calls made" or "calls served".</summary>
    public static string Unit(CommunicationPattern pattern, PatternRole role)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return pattern.Mechanism switch
        {
            Mechanism.Rpc => role == PatternRole.Client ? "calls made" : "calls served",
            Mechanism.Tcp => "connections",
            _ => "flows",
        };
    }

    /// <summary>
    /// What a reading with no pattern says of itself, the command line and the window alike: that the session holds none,
    /// or none its evidence policy admits, and where to read whether one could have been seen.
    /// </summary>
    public static string None(PatternReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        string holds = reading.Interval is null ? "This session holds" : "The interval holds";
        return reading.NotAdmitted > 0
            ? $"{holds} no pattern the {CommandArgument.Typed(reading.Policy)} evidence policy admits; what it did not "
                + "admit is counted beneath, and the coverage above says whether any could have been seen."
            : $"{holds} no pattern: no TCP connection the capture saw open, UDP flow with a service port or RPC call "
                + "naming its interface is in it; the coverage above says whether one could have been seen.";
    }

    /// <summary>One end's occurrences in its role's unit: "1 connection", "2 flows", "3 calls served".</summary>
    public static string Count(CommunicationPattern pattern, PatternRole role, long occurrences)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return pattern.Mechanism switch
        {
            Mechanism.Rpc => role == PatternRole.Client
                ? CountText.Of(occurrences, "call made", "calls made")
                : CountText.Of(occurrences, "call served", "calls served"),
            Mechanism.Tcp => CountText.Of(occurrences, "connection", "connections"),
            _ => CountText.Of(occurrences, "flow", "flows"),
        };
    }

    /// <summary>
    /// When a pattern occurred: "3 connections, the first beginning at 0.100 ms and the last at 0.800 ms, in 3 of the
    /// session's 10 tenths", the interval's where the reading keeps to one; its occurrences alone where none had a session
    /// time.
    /// </summary>
    public static string When(PatternReading reading, CommunicationPattern pattern, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(pattern);
        culture ??= CultureInfo.CurrentCulture;
        return Occurred(pattern, culture) + (pattern.FirstTicks is { } first && pattern.LastTicks is { } last && reading.Span is { } span
            ? ", the first beginning at " + WorkspaceTime.FormatInstant(first, span.SpanTicks, culture) + " and the last at "
                + WorkspaceTime.FormatInstant(last, span.SpanTicks, culture)
                + string.Create(culture, $", in {pattern.Tenths:N0} of the ")
                + (reading.Interval is null ? "session's" : "interval's")
                + string.Create(culture, $" {CommunicationPatterns.Parts:N0} tenths")
            : string.Empty);
    }

    /// <summary>
    /// One role's ends in one phrase: "3 processes of 2 programs", "61 hosts", "ends no record holds", joined - each kind
    /// said only where there is one; "none" for a role no end took.
    /// </summary>
    public static string Ends(PatternReading reading, IReadOnlyList<PatternParticipant> participants)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(participants);
        if (participants.Count == 0) return "none";
        ProcessInstanceId[] processes = [.. participants.Select(participant => participant.Process).OfType<ProcessInstanceId>()];
        int programs = processes
            .Select(id => reading.Processes.TryGetValue(id, out ProcessNode? node) ? node.GroupKey : null)
            .OfType<string>()
            .Where(key => key != ProcessGroup.UnwitnessedExecutableKey)
            .Distinct(StringComparer.Ordinal)
            .Count();
        int hosts = participants.Count(participant => participant.Address is not null);
        var parts = new List<string>();
        if (processes.Length > 0)
        {
            parts.Add(CountText.Of(processes.Length, "process", "processes")
                + (programs > 0 ? " of " + CountText.Of(programs, "program", "programs") : string.Empty));
        }

        if (hosts > 0) parts.Add(CountText.Of(hosts, "host", "hosts"));
        if (participants.Any(participant => participant.Unseen)) parts.Add("ends no record holds");
        return string.Join(", ", parts);
    }

    /// <summary>
    /// Who took one role in a pattern, in one phrase: its ends as <see cref="Ends"/> says them, "none", or, for an RPC
    /// pattern's servers, that no call of it was recorded served on this machine.
    /// </summary>
    public static string Took(PatternReading reading, CommunicationPattern pattern, PatternRole role)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(pattern);
        IReadOnlyList<PatternParticipant> participants = role == PatternRole.Client ? pattern.Clients : pattern.Servers;
        return participants.Count > 0 ? Ends(reading, participants)
            : role == PatternRole.Server && pattern.Mechanism == Mechanism.Rpc
                ? "none recorded: no call of it was recorded served on this machine"
                : "none";
    }

    /// <summary>One end as a list names it: a process as the overview does, a host by its address.</summary>
    public static string Named(PatternReading reading, PatternParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(participant);
        return participant switch
        {
            { Process: { } id } => reading.Processes.TryGetValue(id, out ProcessNode? node)
                ? CollectorText.Named(node)
                : "a process the overview did not name",
            { Address: { } address } => address,
            _ => "ends on this machine no record holds",
        };
    }

    /// <summary>The program a participating process ran, as the overview groups it; null for a host or an unseen end.</summary>
    public static string? Program(PatternReading reading, PatternParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(participant);
        return participant.Process is { } id && reading.Processes.TryGetValue(id, out ProcessNode? node)
            && reading.Groups.TryGetValue(node.GroupKey, out ProcessGroup? group)
                ? group.Name
                : null;
    }

    /// <summary>How many patterns recur and how many occurred once: "5 recurring patterns, and 3 that each occurred once".</summary>
    public static string Counted(PatternReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        int recurring = reading.Patterns.Count(pattern => pattern.Recurring);
        int once = reading.Patterns.Count - recurring;
        return CountText.Of(recurring, "recurring pattern", "recurring patterns")
            + (once == 0 ? string.Empty : ", and " + CountText.Of(once, "that occurred once", "that each occurred once"));
    }

    /// <summary>Each mechanism's coverage over the session, in the overview's words, without its "Coverage:" label.</summary>
    public static string Coverage(PatternReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        return CoverageText.Unlabelled(reading.Coverage);
    }

    /// <summary>
    /// What no pattern holds, each said where there is some, so a missing occurrence is never read as none: "In no pattern:
    /// 2 TCP connections the capture saw open at neither end, …". Null where every occurrence is in a pattern.
    /// </summary>
    public static string? Apart(PatternReading reading, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(reading);
        culture ??= CultureInfo.CurrentCulture;
        var apart = new List<string>();
        if (reading.UnopenedConnections > 0)
        {
            apart.Add(CountText.Of(reading.UnopenedConnections, "TCP connection", "TCP connections")
                + " the capture saw open at neither end, so no end of them has a role");
        }

        if (reading.DynamicFlows > 0)
        {
            apart.Add(CountText.Of(reading.DynamicFlows, "UDP flow", "UDP flows") + " between two dynamic ports, which have no service port");
        }

        if (reading.CallsWithoutInterface > 0)
        {
            apart.Add(CountText.Of(reading.CallsWithoutInterface, "RPC call", "RPC calls") + " no recorded start named an interface for");
        }

        if (reading.NotAdmitted > 0)
        {
            string policy = CommandArgument.Typed(reading.Policy);
            apart.Add(string.Create(culture, $"{reading.NotAdmitted:N0} the {policy} evidence policy did not admit"));
        }

        if (reading.OutsideInterval > 0)
        {
            apart.Add(string.Create(culture, $"{reading.OutsideInterval:N0} connections, flows and calls that began outside the interval"));
        }

        return apart.Count > 0 ? "In no pattern: " + WorkspaceLayout.Series(apart) + "." : null;
    }
}
