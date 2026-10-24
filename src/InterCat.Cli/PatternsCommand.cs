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
        Program = PatternText.Program(reading, participant),
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

        ConsoleUi.Field("Coverage", PatternText.Coverage(reading));
        bool once = reading.Patterns.Any(pattern => !pattern.Recurring);
        ConsoleUi.Field("Patterns", PatternText.Counted(reading) + (!once ? string.Empty : all ? ", listed too" : ", which --all lists"));
        if (listed.Length == 0)
        {
            ConsoleUi.Note(reading.Patterns.Count == 0
                ? PatternText.None(reading)
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
                PatternText.Occurred(pattern, culture),
                PatternText.Ends(reading, pattern.Clients),
                PatternText.Ends(reading, pattern.Servers),
                string.Create(culture, $"{pattern.Tenths:N0} of {CommunicationPatterns.Parts:N0}"),
            ])]);
        if (shown.Length > listed.Length)
        {
            ConsoleUi.Note(string.Create(culture,
                $"{shown.Length - listed.Length:N0} more, lighter; --limit {shown.Length:N0} lists every one."));
        }

        ConsoleUi.Note(PatternText.Note + " --pattern <rank> says what a pattern's signature is made of and lists every participant.");
        Apart(reading);
    }

    /// <summary>What no pattern holds, said where there is some, so a missing occurrence is never read as none.</summary>
    private static void Apart(PatternReading reading)
    {
        if (PatternText.Apart(reading, CultureInfo.CurrentCulture) is { } apart)
        {
            ConsoleUi.Note(apart);
        }
    }

    private static void PrintPattern(PatternReading reading, CommunicationPattern pattern, int rank)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        ConsoleUi.Heading(string.Create(culture, $"Pattern {rank:N0}: ") + CommunicationPatterns.Describe(pattern));
        ConsoleUi.Field("Signature", pattern.Key);
        ConsoleUi.Field("Made of", CommunicationPatterns.Explain(pattern));
        ConsoleUi.Field("Occurred", PatternText.When(reading, pattern, culture));
        foreach ((string label, PatternRole role, IReadOnlyList<PatternParticipant> participants) in new[]
        {
            ("Clients", PatternRole.Client, pattern.Clients),
            ("Servers", PatternRole.Server, pattern.Servers),
        })
        {
            ConsoleUi.Field(label, PatternText.Took(reading, pattern, role));
            if (participants.Count == 0) continue;
            string unit = PatternText.Unit(pattern, role);
            ConsoleUi.Table(
                ["End", "Program", char.ToUpperInvariant(unit[0]) + unit[1..]],
                [.. participants.Select(participant => (IReadOnlyList<string>)
                [
                    PatternText.Named(reading, participant),
                    PatternText.Program(reading, participant) ?? "-",
                    ConsoleUi.Count(participant.Occurrences),
                ])]);
        }

        if (pattern.Clients.Concat(pattern.Servers).Any(participant => participant.Address is not null))
        {
            ConsoleUi.Note(PatternText.HostNote);
        }
    }

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
