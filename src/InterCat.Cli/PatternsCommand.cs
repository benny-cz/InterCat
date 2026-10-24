using System.Globalization;
using System.Text.Json;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>
/// A session's recurring communication patterns (`contracts/patterns-v1.md`, plan M12's explainable pattern groups, R18):
/// each TCP connection the capture saw open, each UDP flow and each RPC call grouped by its mechanism, its service port or
/// RPC interface and procedure, and where its server end is, the heaviest first, with who took part in each role and how
/// far through the session it recurred - and, for one pattern, what its signature is made of and every participant.
/// </summary>
internal static class PatternsCommand
{
    /// <summary>The document --json writes.</summary>
    internal const string Contract = "communication-patterns-v1";

    private const int DefaultLimit = 20;

    public static Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken) =>
        Task.FromResult(Run(command, cancellationToken));

    private static InterCatExitCode Run(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? limitText = command.TakeOption("--limit");
        string? rankText = command.TakeOption("--pattern");
        string? policyText = command.TakeOption("--evidence-policy");
        bool all = command.TryTakeFlag("--all");
        bool json = command.TryTakeFlag("--json");
        string? directory = command.TakePositional();
        bool hasUnknown = command.TryReportUnknown(out string? unknown);
        if (directory is null || hasUnknown)
        {
            ConsoleUi.Failure(directory is null
                ? "A session directory is required: icat patterns <directory>."
                : command.Refusal(unknown!));
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        int limit = DefaultLimit;
        if (limitText is not null
            && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1))
        {
            ConsoleUi.Failure($"--limit expects a number of patterns of 1 or more; '{limitText}' is not one.");
            return InterCatExitCode.InvalidInvocation;
        }

        int? rank = null;
        if (rankText is not null)
        {
            if (!int.TryParse(rankText, NumberStyles.None, CultureInfo.InvariantCulture, out int asked) || asked < 1)
            {
                ConsoleUi.Failure($"--pattern names a pattern by its rank, 1 for the heaviest; '{rankText}' is not one.");
                return InterCatExitCode.InvalidInvocation;
            }

            if (limitText is not null)
            {
                ConsoleUi.Failure("--limit lists patterns and --pattern shows one; pass one of them.");
                return InterCatExitCode.InvalidInvocation;
            }

            rank = asked;
        }

        EvidencePolicy policy = EvidencePolicy.IncludeCorrelated;
        if (policyText is not null && (!Enum.TryParse(policyText, ignoreCase: true, out policy) || !Enum.IsDefined(policy)
            || int.TryParse(policyText, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
        {
            ConsoleUi.Failure(
                $"--evidence-policy expects one of: {string.Join(", ", Enum.GetNames<EvidencePolicy>())}. '{policyText}' is not one.");
            return InterCatExitCode.InvalidInvocation;
        }

        string path = Path.GetFullPath(directory);
        if (!Directory.Exists(path))
        {
            ConsoleUi.Failure($"No session directory at {path}.");
            return InterCatExitCode.InvalidInvocation;
        }

        ConsoleUi.Progress("Projecting the current generation, then grouping its connections, flows and RPC calls by signature.");
        PatternReading reading;
        try
        {
            reading = CommunicationPatterns.Read(SessionStore.OpenExisting(LocalOwnedDirectory.Open(path)), policy, cancellationToken);
        }
        catch (InvalidOperationException exception) when (exception is not NoSessionException)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        // A pattern that occurred once is no recurrence: it is listed only when asked for, and counted where it is not.
        CommunicationPattern[] shown = [.. reading.Patterns.Where(pattern => all || pattern.Recurring)];
        if (rank is { } one && one > shown.Length)
        {
            ConsoleUi.Failure(string.Create(CultureInfo.CurrentCulture, $"--pattern {one} names no pattern: the session has ")
                + CountText.Of(shown.Length, all ? "pattern" : "recurring pattern", all ? "patterns" : "recurring patterns")
                + (all || shown.Length == reading.Patterns.Count ? "." : "; --all counts those that occurred once."));
            return InterCatExitCode.InvalidInvocation;
        }

        CommunicationPattern[] listed = rank is { } chosen ? [shown[chosen - 1]] : [.. shown.Take(limit)];
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(Document(reading, path, shown, listed, rank, all), JsonContracts.Indented));
            return InterCatExitCode.Success;
        }

        if (rank is { } detailed)
        {
            PrintPattern(reading, listed[0], detailed);
        }
        else
        {
            PrintList(reading, path, shown, listed, all);
        }

        return InterCatExitCode.Success;
    }

    private static object Document(PatternReading reading, string path, IReadOnlyList<CommunicationPattern> shown,
        IReadOnlyList<CommunicationPattern> listed, int? rank, bool all) => new
    {
        Contract,
        SessionPath = path,
        reading.SessionId,
        reading.Generation,
        EvidencePolicy = reading.Policy.ToString(),
        Span = reading.Span is { } span ? new { span.StartTicks, span.EndTicks } : null,
        Parts = CommunicationPatterns.Parts,
        Coverage = reading.Coverage.Select(coverage => new
        {
            Mechanism = MechanismText.Name(coverage.Mechanism),
            State = coverage.State.ToString(),
            coverage.Reason,
        }),
        Patterns = reading.Patterns.Count,
        Recurring = reading.Patterns.Count(pattern => pattern.Recurring),
        OccurredOnce = reading.Patterns.Count(pattern => !pattern.Recurring),
        IncludesOnce = all,
        reading.UnopenedConnections,
        reading.DynamicFlows,
        reading.CallsWithoutInterface,
        reading.NotAdmitted,
        Listed = listed.Select(pattern => new
        {
            Rank = rank ?? Rank(shown, pattern),
            pattern.Key,
            Mechanism = MechanismText.Name(pattern.Mechanism),
            Place = pattern.Place.ToString(),
            pattern.Port,
            pattern.Interface,
            InterfaceName = pattern.Interface is { } uuid ? RpcInterfaceNames.Of(uuid)?.Name : null,
            pattern.Procedure,
            Signature = CommunicationPatterns.Describe(pattern),
            Explanation = CommunicationPatterns.Explain(pattern),
            pattern.Occurrences,
            pattern.Served,
            pattern.FirstTicks,
            pattern.LastTicks,
            pattern.Tenths,
            Clients = pattern.Clients.Select(participant => Participant(reading, participant)),
            Servers = pattern.Servers.Select(participant => Participant(reading, participant)),
        }),
    };

    private static object Participant(PatternReading reading, PatternParticipant participant) => new
    {
        Kind = participant.Process is not null ? "Process" : participant.Address is not null ? "Host" : "UnseenEnd",
        InstanceId = participant.Process?.ToString(),
        ProcessId = participant.Process is { } id && reading.Processes.TryGetValue(id, out ProcessNode? node) ? node.ProcessId : (int?)null,
        Name = participant.Process is { } named && reading.Processes.TryGetValue(named, out ProcessNode? process) ? process.Name : null,
        Program = participant.Process is { } grouped && reading.Processes.TryGetValue(grouped, out ProcessNode? member)
            && reading.Groups.TryGetValue(member.GroupKey, out ProcessGroup? group) ? group.Name : null,
        participant.Address,
        participant.Occurrences,
    };

    /// <summary>A pattern's rank among those shown, from 1.</summary>
    private static int Rank(IReadOnlyList<CommunicationPattern> shown, CommunicationPattern pattern)
    {
        for (int index = 0; index < shown.Count; index++)
        {
            if (ReferenceEquals(shown[index], pattern)) return index + 1;
        }

        throw new InvalidOperationException("A pattern listed is one of those shown.");
    }

    private static void PrintList(PatternReading reading, string path, CommunicationPattern[] shown,
        CommunicationPattern[] listed, bool all)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        ConsoleUi.Heading("Communication patterns");
        ConsoleUi.Field("Session", path);
        ConsoleUi.Field("Generation", reading.Generation.ToString(culture));
        if (reading.Span is { } span)
        {
            ConsoleUi.Field("Spread over", ComparisonText.Interval(span, null, culture) + ", in tenths");
        }

        string covered = CoverageText.Describe(reading.Coverage);
        ConsoleUi.Field("Coverage", covered.StartsWith("Coverage: ", StringComparison.Ordinal) ? covered["Coverage: ".Length..] : covered);
        int recurring = reading.Patterns.Count(pattern => pattern.Recurring);
        int once = reading.Patterns.Count - recurring;
        ConsoleUi.Field("Patterns", CountText.Of(recurring, "recurring pattern", "recurring patterns")
            + (once == 0 ? string.Empty
                : ", and " + CountText.Of(once, "that occurred once", "that each occurred once")
                    + (all ? ", listed too" : ", which --all lists")));
        if (listed.Length == 0)
        {
            ConsoleUi.Note(reading.Patterns.Count == 0
                ? "No TCP connection the capture saw open, UDP flow or named RPC call is in this session, so it holds no "
                    + "pattern; the coverage above says whether one could have been seen."
                : "No pattern occurred more than once; --all lists those that occurred once.");
            Apart(reading);
            return;
        }

        ConsoleUi.Table(
            ["Rank", "Pattern", "Occurred", "Clients", "Servers", "Tenths"],
            [.. listed.Select(pattern => (IReadOnlyList<string>)
            [
                Rank(shown, pattern).ToString(culture),
                CommunicationPatterns.Describe(pattern),
                Occurred(pattern, culture),
                Ends(reading, pattern.Clients, culture),
                Ends(reading, pattern.Servers, culture),
                string.Create(culture, $"{pattern.Tenths:N0} of {CommunicationPatterns.Parts:N0}"),
            ])]);
        if (shown.Length > listed.Length)
        {
            ConsoleUi.Note(string.Create(culture,
                $"{shown.Length - listed.Length:N0} more, lighter; --limit {shown.Length:N0} lists every one."));
        }

        ConsoleUi.Note("A pattern groups what recurs by its mechanism, its service port or RPC interface and procedure, and "
            + "where its server end is, never by a process, address or time; its clients and servers say who took part, a "
            + "process by its executable's processes counted as programs. Tenths says in how many tenths of the session an "
            + "occurrence began. --pattern <rank> says what a pattern's signature is made of and lists every participant.");
        Apart(reading);
    }

    /// <summary>What no pattern holds, each said where there is some, so a missing occurrence is never read as none.</summary>
    private static void Apart(PatternReading reading)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        var apart = new List<string>();
        if (reading.UnopenedConnections > 0)
        {
            apart.Add(CountText.Of(reading.UnopenedConnections, "TCP connection", "TCP connections")
                + " the capture saw open at neither end, so no end of them has a role");
        }

        if (reading.DynamicFlows > 0)
        {
            apart.Add(CountText.Of(reading.DynamicFlows, "UDP flow", "UDP flows")
                + " between two dynamic ports, which have no service port");
        }

        if (reading.CallsWithoutInterface > 0)
        {
            apart.Add(CountText.Of(reading.CallsWithoutInterface, "RPC call", "RPC calls")
                + " no recorded start named an interface for");
        }

        if (reading.NotAdmitted > 0)
        {
            apart.Add(string.Create(culture, $"{reading.NotAdmitted:N0} the {reading.Policy} evidence policy did not admit"));
        }

        if (apart.Count > 0)
        {
            ConsoleUi.Note("In no pattern: " + WorkspaceLayout.Series(apart) + ".");
        }
    }

    private static void PrintPattern(PatternReading reading, CommunicationPattern pattern, int rank)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        ConsoleUi.Heading(string.Create(culture, $"Pattern {rank:N0}: ") + CommunicationPatterns.Describe(pattern));
        ConsoleUi.Field("Signature", pattern.Key);
        ConsoleUi.Field("Made of", CommunicationPatterns.Explain(pattern));
        string when = pattern.FirstTicks is { } first && pattern.LastTicks is { } last && reading.Span is { } span
            ? ", the first beginning at " + WorkspaceTime.FormatInstant(first, span.SpanTicks, culture) + " and the last at "
                + WorkspaceTime.FormatInstant(last, span.SpanTicks, culture)
                + string.Create(culture, $", in {pattern.Tenths:N0} of the session's {CommunicationPatterns.Parts:N0} tenths")
            : string.Empty;
        ConsoleUi.Field("Occurred", Occurred(pattern, culture) + when);
        foreach ((string label, IReadOnlyList<PatternParticipant> participants, string unit) in new[]
        {
            ("Clients", pattern.Clients, pattern.Mechanism == Mechanism.Rpc ? "made" : Unit(pattern)),
            ("Servers", pattern.Servers, pattern.Mechanism == Mechanism.Rpc ? "served" : Unit(pattern)),
        })
        {
            ConsoleUi.Field(label, participants.Count == 0
                ? (label == "Servers" && pattern.Mechanism == Mechanism.Rpc
                    ? "none recorded: no call of it was recorded served on this machine"
                    : "none")
                : Ends(reading, participants, culture));
            if (participants.Count == 0) continue;
            ConsoleUi.Table(
                ["End", "Program", pattern.Mechanism == Mechanism.Rpc ? "Calls " + unit : Capitalized(unit)],
                [.. participants.Select(participant => (IReadOnlyList<string>)
                [
                    Named(reading, participant),
                    participant.Process is { } id && reading.Processes.TryGetValue(id, out ProcessNode? node)
                        && reading.Groups.TryGetValue(node.GroupKey, out ProcessGroup? group) ? group.Name : "-",
                    ConsoleUi.Count(participant.Occurrences),
                ])]);
        }

        if (pattern.Clients.Concat(pattern.Servers).Any(participant => participant.Address is not null))
        {
            ConsoleUi.Note("Another host is named by its address: who is at it is not observed.");
        }
    }

    /// <summary>How many times a pattern occurred, in its unit: "20 connections", "6 flows", "46 calls made · 46 served".</summary>
    private static string Occurred(CommunicationPattern pattern, IFormatProvider culture) => pattern.Mechanism == Mechanism.Rpc
        ? CountText.Of(pattern.Occurrences, "call made", "calls made") + string.Create(culture, $" · {pattern.Served:N0} served")
        : CountText.Of(pattern.Occurrences, pattern.Mechanism == Mechanism.Tcp ? "connection" : "flow",
            pattern.Mechanism == Mechanism.Tcp ? "connections" : "flows");

    private static string Unit(CommunicationPattern pattern) => pattern.Mechanism == Mechanism.Tcp ? "connections" : "flows";

    private static string Capitalized(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>
    /// One role's ends in one cell: "3 processes of 2 programs", "61 hosts", "ends no record holds", joined - each kind
    /// said only where there is one.
    /// </summary>
    private static string Ends(PatternReading reading, IReadOnlyList<PatternParticipant> participants, IFormatProvider culture)
    {
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

    /// <summary>One end as a table names it: a process as the overview does, a host by its address.</summary>
    private static string Named(PatternReading reading, PatternParticipant participant) => participant switch
    {
        { Process: { } id } => reading.Processes.TryGetValue(id, out ProcessNode? node)
            ? CollectorText.Named(node)
            : "a process the overview did not name",
        { Address: { } address } => address,
        _ => "ends on this machine no record holds",
    };

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat patterns <session-directory> [--limit <n> | --pattern <rank>] [--all]");
        ConsoleUi.Line("              [--evidence-policy <policy>] [--json]");
        ConsoleUi.Line("  The session's recurring communication patterns: every TCP connection the capture saw open, UDP");
        ConsoleUi.Line("  flow and RPC call grouped by its mechanism, its service port or RPC interface and procedure,");
        ConsoleUi.Line("  and where its server end is - never by a process, address or time - the heaviest first, with");
        ConsoleUi.Line("  who took part as a client and as a server and in how many tenths of the session it occurred.");
        ConsoleUi.Line("  --pattern <rank> says what one pattern's signature is made of and lists every participant.");
        ConsoleUi.Line("  --all lists patterns that occurred once too. --limit lists that many, 20 unless it says so.");
        ConsoleUi.Line("  --evidence-policy admits relations and calls as the overview does, IncludeCorrelated unless");
        ConsoleUi.Line("  it says so; --json writes them as a document.");
    }
}
