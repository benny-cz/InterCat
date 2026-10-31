using System.Globalization;
using System.Text.Json;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>
/// One process's links (`contracts/links-v1.md`, plan M12's causal navigation): what led to it - its parent, the processes
/// whose calls it served, what connected to it - and what it led to, each by the evidence it rests on and its strength;
/// the links the evidence policy does not follow, apart; the processes created near it that nothing links to it; and what
/// of it links to nothing. With --to, the shortest chain of followed links from it to another process.
/// </summary>
internal static class LinksCommand
{
    /// <summary>The document --json writes.</summary>
    internal const string Contract = "evidence-links-v1";

    public static Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken) =>
        Task.FromResult(Run(command, cancellationToken));

    private static InterCatExitCode Run(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? processText = command.TakeOption("--process");
        string? toText = command.TakeOption("--to");
        string? policyText = command.TakeOption("--evidence-policy");
        string? intervalText = command.TakeOption("--interval");
        bool json = command.TryTakeFlag("--json");
        string? directory = command.TakePositional();
        bool hasUnknown = command.TryReportUnknown(out string? unknown);
        if (directory is null || hasUnknown || processText is null)
        {
            ConsoleUi.Failure(directory is null ? "A session directory is required: icat links <directory> --process <pid | instance>."
                : hasUnknown ? command.Refusal(unknown!)
                : "--process names the process whose links to list: its PID, or its instance id where the PID was reused. "
                    + $"icat processes \"{directory}\" lists the session's processes by both.");
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        if (!EvidencePolicyOption.TryParse(policyText, out EvidencePolicy policy, out string? refusal))
        {
            ConsoleUi.Failure(refusal!);
            return InterCatExitCode.InvalidInvocation;
        }

        // An interval keeps the links to the starts, calls and connections that began in it (`links-v1` §1).
        TimeRange? interval = null;
        if (intervalText is not null)
        {
            interval = TickInterval.Read(intervalText, out string? tooWide);
            if (interval is null)
            {
                ConsoleUi.Failure(tooWide ?? CommandLine.Expects("--interval", intervalText, TickInterval.Takes));
                return InterCatExitCode.InvalidInvocation;
            }
        }

        string path = Path.GetFullPath(directory);
        if (!Directory.Exists(path))
        {
            ConsoleUi.Failure($"No session directory at {path}.");
            return InterCatExitCode.InvalidInvocation;
        }

        ConsoleUi.Progress("Projecting the current generation, then reading its processes, connections and the other ends of its calls.");
        LinkReading reading;
        try
        {
            reading = EvidenceLinks.Read(SessionStore.OpenExisting(LocalOwnedDirectory.Open(path)), policy, interval, cancellationToken);
        }
        catch (InvalidOperationException exception) when (exception is not NoSessionException)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        if (ProcessOption.Resolve(reading.Processes, "--process", processText, out string? unresolved) is not { } step)
        {
            ConsoleUi.Failure(unresolved!);
            return InterCatExitCode.InvalidInvocation;
        }

        ProcessInstanceId? to = null;
        if (toText is not null)
        {
            if (ProcessOption.Resolve(reading.Processes, "--to", toText, out unresolved) is not { } target)
            {
                ConsoleUi.Failure(unresolved!);
                return InterCatExitCode.InvalidInvocation;
            }

            if (target == step)
            {
                ConsoleUi.Failure("--to names the process --process names: a chain leads from one process to another.");
                return InterCatExitCode.InvalidInvocation;
            }

            to = target;
        }

        StepLinks links = EvidenceLinks.Of(reading, step);
        IReadOnlyList<EvidenceLink>? chain = to is { } end ? EvidenceLinks.Chain(reading, step, end) : null;
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(Document(reading, path, links, to, chain), JsonContracts.Indented));
            return InterCatExitCode.Success;
        }

        if (to is { } reached)
        {
            PrintChain(reading, path, step, reached, chain);
        }
        else
        {
            PrintLinks(reading, path, links);
        }

        return InterCatExitCode.Success;
    }

    private static object Document(LinkReading reading, string path, StepLinks links, ProcessInstanceId? to,
        IReadOnlyList<EvidenceLink>? chain) => new
    {
        Contract,
        SessionPath = path,
        reading.SessionId,
        reading.Generation,
        Rule = EvidenceLinks.Rule,
        EvidencePolicy = reading.Policy.ToString(),
        Interval = reading.Interval is { } read ? new { read.StartTicks, read.EndTicks } : null,
        Coverage = reading.Coverage.Select(coverage => new
        {
            Mechanism = MechanismText.Name(coverage.Mechanism),
            State = coverage.State.ToString(),
            coverage.Reason,
        }),
        Process = End(reading, LinkEnd.Of(links.Step)),
        links.OutsideInterval,
        LedToIt = links.LedToIt.Select(link => Link(reading, link, links.Step)),
        ItLedTo = links.ItLedTo.Select(link => Link(reading, link, links.Step)),
        NotFollowed = links.NotFollowed.Select(link => Link(reading, link, links.Step)),
        links.CreationWitnessed,
        NearTicks = EvidenceLinks.NearTicks,
        Near = links.Near.Select(near => new
        {
            Process = End(reading, LinkEnd.Of(near.Process)),
            near.OffsetTicks,
        }),
        Unlinked = new
        {
            links.Unlinked.UnheldParent,
            links.Unlinked.UnopenedConnections,
            links.Unlinked.Flows,
            UnresolvedCalls = links.Unlinked.UnresolvedCalls
                .OrderBy(entry => entry.Key)
                .ToDictionary(entry => entry.Key.ToString(), entry => entry.Value),
            links.Unlinked.CallsNotReached,
        },
        Chain = to is { } end ? new
        {
            To = End(reading, LinkEnd.Of(end)),
            Found = chain is not null,
            Weakest = chain is null ? null : EvidenceLinks.Weakest(chain).ToString(),
            Links = chain?.Select(link => Link(reading, link, link.From.Process!.Value)),
        } : null,
    };

    private static object Link(LinkReading reading, EvidenceLink link, ProcessInstanceId step) => new
    {
        Kind = link.Kind.ToString(),
        From = End(reading, link.From),
        To = End(reading, link.To),
        Strength = link.Strength.ToString(),
        link.Occurrences,
        link.Interface,
        InterfaceName = link.Interface is { } uuid ? RpcInterfaceNames.Of(uuid)?.Name : null,
        link.Port,
        Evidence = LinkText.Evidence(link, step, CultureInfo.InvariantCulture),
    };

    private static object End(LinkReading reading, LinkEnd end) => new
    {
        Kind = end.Process is not null ? "Process" : end.Address is not null ? "Host" : "UnseenEnd",
        InstanceId = end.Process?.ToString(),
        ProcessId = end.Process is { } id && reading.Processes.TryGetValue(id, out ProcessNode? node) ? node.ProcessId : (int?)null,
        Name = end.Process is { } named && reading.Processes.TryGetValue(named, out ProcessNode? process) ? process.Name : null,
        end.Address,
    };

    private static void PrintLinks(LinkReading reading, string path, StepLinks links)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        ProcessInstanceId step = links.Step;
        ConsoleUi.Heading("Links of " + LinkText.Named(reading, LinkEnd.Of(step)));
        Header(reading, path);
        ConsoleUi.Field("Process", string.Create(culture, $"{LinkText.Named(reading, LinkEnd.Of(step))}, instance {step}"));
        foreach ((string heading, IReadOnlyList<EvidenceLink> listed, string none) in new[]
        {
            ("Led to it", links.LedToIt, "nothing the evidence policy follows led to it"),
            ("It led to", links.ItLedTo, "it led to nothing the evidence policy follows"),
        })
        {
            ConsoleUi.Heading(heading);
            if (listed.Count == 0)
            {
                ConsoleUi.Note(char.ToUpperInvariant(none[0]) + none[1..] + ".");
                continue;
            }

            Table(reading, listed, step, culture);
        }

        if (links.NotFollowed.Count > 0)
        {
            ConsoleUi.Heading("Not followed");
            ConsoleUi.Note(string.Create(culture, $"The {CommandArgument.Typed(reading.Policy)} evidence policy does not admit ")
                + "these links' strength: each is possible, not established, and no chain is made through it.");
            Table(reading, links.NotFollowed, step, culture);
        }

        ConsoleUi.Heading("Near in time");
        ConsoleUi.Note(!links.CreationWitnessed
            ? "Its creation was not witnessed, so no process is said to have been created near it."
            : links.Near.Count == 0
                ? "No other process was created within a second of it without a link to it."
                : "Created within a second of it, and linked to it by nothing; nearness in time is never a link:");
        foreach (NearProcess near in links.Near)
        {
            ConsoleUi.Line("  " + LinkText.Named(reading, LinkEnd.Of(near.Process)) + ", " + LinkText.Near(near, culture));
        }

        if (LinkText.Unlinked(links.Unlinked) is { } unlinked)
        {
            ConsoleUi.Heading("Not linked");
            ConsoleUi.Note(unlinked);
        }

        if (LinkText.Outside(links) is { } outside)
        {
            ConsoleUi.Heading("Outside the interval");
            ConsoleUi.Note(outside);
        }

        ConsoleUi.Line();
        if (links.LedToIt.Concat(links.ItLedTo).Concat(links.NotFollowed).Any(link => LinkText.Other(link, step).Address is not null))
        {
            ConsoleUi.Note("Another host is named by its address: who is at it is not observed.");
        }

        ConsoleUi.Note(LinkText.Note + " --to <process> finds a chain of them to another process.");
    }

    private static void Table(LinkReading reading, IReadOnlyList<EvidenceLink> listed, ProcessInstanceId step, CultureInfo culture) =>
        ConsoleUi.Table(
            ["Link", "End", "Evidence", "Strength"],
            [.. listed.Select(link => (IReadOnlyList<string>)
            [
                LinkText.Role(link, step),
                LinkText.Named(reading, LinkText.Other(link, step)),
                LinkText.Evidence(link, step, culture),
                LinkText.Strength(link.Strength),
            ])]);

    private static void Header(LinkReading reading, string path)
    {
        ConsoleUi.Field("Session", path);
        ConsoleUi.Field("Generation", reading.Generation.ToString(CultureInfo.CurrentCulture));
        if (reading.Interval is { } interval)
        {
            ConsoleUi.Field("Interval", ComparisonText.Interval(interval, null, CultureInfo.CurrentCulture) + ", what began in it alone");
        }

        ConsoleUi.Field("Follows", "links the " + CommandArgument.Typed(reading.Policy) + " evidence policy admits: "
            + WorkspaceLayout.Series([.. EvidenceLinks.Followed(reading.Policy).Select(LinkText.Strength)]));
        ConsoleUi.Field("Coverage", CoverageText.Unlabelled(reading.Coverage));
    }

    private static void PrintChain(LinkReading reading, string path, ProcessInstanceId from, ProcessInstanceId to,
        IReadOnlyList<EvidenceLink>? chain)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        string fromName = LinkText.Named(reading, LinkEnd.Of(from));
        string toName = LinkText.Named(reading, LinkEnd.Of(to));
        ConsoleUi.Heading($"Chain from {fromName} to {toName}");
        Header(reading, path);
        if (chain is not null)
        {
            ConsoleUi.Table(
                ["Step", "Link", "Evidence", "Strength"],
                [.. chain.Select((link, index) => (IReadOnlyList<string>)
                [
                    (index + 1).ToString(culture),
                    LinkText.Step(reading, link),
                    LinkText.Evidence(link, link.From.Process!.Value, culture),
                    LinkText.Strength(link.Strength),
                ])]);
            ConsoleUi.Note(string.Create(culture, $"{CountText.Of(chain.Count, "link", "links")} lead from {fromName} to {toName}; ")
                + $"the chain is as strong as its weakest link: {LinkText.Strength(EvidenceLinks.Weakest(chain))}. "
                + LinkText.Note);
            return;
        }

        ConsoleUi.Note($"No chain of links the {CommandArgument.Typed(reading.Policy)} evidence policy follows leads from {fromName} "
            + $"to {toName}" + (reading.Interval is null ? "." : " in the interval."));
        if (EvidenceLinks.Chain(reading, from, to, everyLink: true) is { } unfollowed)
        {
            RelationStrength weakest = EvidenceLinks.Weakest(unfollowed);
            EvidencePolicy admits = EvidenceLinks.Following(weakest);
            ConsoleUi.Note($"One leads there through a link it does not follow, the weakest {LinkText.Strength(weakest)}: "
                + $"--evidence-policy {CommandArgument.Typed(admits)} follows it.");
        }

        if (EvidenceLinks.Chain(reading, to, from) is not null)
        {
            ConsoleUi.Note($"One leads the other way, from {toName} to {fromName}: --process {to} --to {from} lists it.");
        }

        ConsoleUi.Note(LinkText.Note);
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat links <session-directory> --process <pid | instance> [--to <pid | instance>]");
        ConsoleUi.Line("           [--interval <start:end>] [--evidence-policy <policy>] [--json]");
        ConsoleUi.Line("  A process's links, each a recorded fact by one documented rule: its parent and children, the");
        ConsoleUi.Line("  processes whose calls it served or that served its calls, through each call's one ALPC message,");
        ConsoleUi.Line("  and the ends it connected to or that connected to it, as the capture saw each connection open.");
        ConsoleUi.Line("  Links the evidence policy does not admit are listed apart and never followed; processes created");
        ConsoleUi.Line("  within a second of it that nothing links to it are listed as near in time, never as a link.");
        ConsoleUi.Line("  --to <process> finds the shortest chain of followed links from it to another process.");
        ConsoleUi.Line("  --evidence-policy follows links as the overview admits them, include-correlated unless it says");
        ConsoleUi.Line("  so; --json writes them as a document. --interval keeps them to the starts, calls and connections");
        ConsoleUi.Line("  that began in it, and counts those that began outside it.");
    }
}
