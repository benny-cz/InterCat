using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>
/// The other hosts a session's processes reached, as the window's Other hosts node and its search state them (§6.3, §6.7,
/// R18): every address their connections ran to with no process of the capture at the other end, the busiest first by
/// the records those connections hold, or by the bytes they sent or received there, or the processes that reached an
/// address - every process's, or one program's, as a chosen program's inspector lists them. An address is where the
/// connections ran; who is at it is not observed (P7).
/// </summary>
internal static class HostsCommand
{
    /// <summary>
    /// The document --json writes: every host's count and the listed hosts, or the processes that reached an address, each
    /// address with the ports at it the connections ran to since its second version, each listed host with its address's
    /// scope since its third, since its fourth the ranking the hosts are listed by and, under a byte ranking, what each
    /// host's connections' transfers measured, since its fifth, read with an investigation, each listed host's candidate
    /// other ends in its other members' captures, and since its sixth how many of each host's TCP connections the capture
    /// saw opened by a connect or accepted from it, with the ports the accepted ones arrived at, which its ports leave out,
    /// and since its seventh the processes whose connections ran to each listed host, the busiest there first, and since
    /// its eighth the program its hosts were limited to with --program, null without it.
    /// </summary>
    internal const string Contract = "other-hosts-v8";

    private const int DefaultLimit = 50;

    public static Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken) =>
        Task.FromResult(Run(command, cancellationToken));

    private static InterCatExitCode Run(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? address = command.TakeOption("--address");
        string? programText = command.TakeOption("--program");
        string? limitText = command.TakeOption("--limit");
        string? rankText = command.TakeOption("--rank-by");
        string? investigationText = command.TakeOption("--investigation");
        bool json = command.TryTakeFlag("--json");
        string? directory = command.TakePositional();
        bool hasUnknown = command.TryReportUnknown(out string? unknown);
        if (directory is null || hasUnknown)
        {
            ConsoleUi.Failure(directory is null
                ? "A session directory is required: icat hosts <directory>."
                : command.Refusal(unknown!));
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        RankingMetric? rankBy = rankText switch
        {
            null or "records" => RankingMetric.Records,
            "bytes-sent" => RankingMetric.BytesSent,
            "bytes-received" => RankingMetric.BytesReceived,
            "bytes-sent-and-received" => RankingMetric.EndpointBytes,
            _ => null,
        };
        if (rankBy is null || (address is not null && (rankText is not null || investigationText is not null)))
        {
            ConsoleUi.Failure(rankBy is null
                ? $"--rank-by ranks the hosts by records, bytes-sent, bytes-received or bytes-sent-and-received; '{rankText}' is none of them."
                : rankText is not null
                ? "--rank-by ranks the hosts; --address lists the processes that reached one, the busiest by records first."
                : "--investigation names the hosts' candidate other ends; --address lists the processes that reached one.");
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        int limit = DefaultLimit;
        if (limitText is not null
            && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1))
        {
            ConsoleUi.Failure($"--limit expects a number of hosts of 1 or more; '{limitText}' is not one.");
            return InterCatExitCode.InvalidInvocation;
        }

        string? asked = programText?.Trim();
        if (asked is { Length: 0 })
        {
            ConsoleUi.Failure(ProgramsCommand.BlankRefusal);
            return InterCatExitCode.InvalidInvocation;
        }

        if (address is not null && !address.Contains('.', StringComparison.Ordinal) && !address.Contains(':', StringComparison.Ordinal))
        {
            ConsoleUi.Failure($"--address takes an address, or its beginning up to a dot or colon - 142.250 or 2001:db8: - an "
                + $"address with a port - 142.250.1.1:443 or [2001:db8::1]:443 - or a port alone - :443 - and '{address}' names "
                + "none.");
            return InterCatExitCode.InvalidInvocation;
        }

        string path = Path.GetFullPath(directory);
        if (!Directory.Exists(path))
        {
            ConsoleUi.Failure($"No session directory at {path}.");
            return InterCatExitCode.InvalidInvocation;
        }

        SessionOverviewBundle overview;
        IReadOnlyList<HostTraffic>? traffic = null;
        ProcessGroup? program = null;
        IReadOnlyList<HostLink> links;
        try
        {
            SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(path));
            overview = SessionOverviewProjector.Project(store, cancellationToken: cancellationToken);

            // One program's hosts are those its processes' connections ran to, as its inspector in the window lists them.
            if (asked is not null)
            {
                program = ProgramsCommand.One(overview.Groups, asked, out string? refusal);
                if (program is null)
                {
                    ConsoleUi.Failure(refusal!);
                    return InterCatExitCode.InvalidInvocation;
                }
            }

            HashSet<ProcessInstanceId>? members = program is null ? null
                : [.. overview.Nodes.Where(node => node.GroupKey == program.Key).Select(node => node.Id)];
            links = members is null ? overview.HostLinks : [.. overview.HostLinks.Where(link => members.Contains(link.Process))];

            // Records the census holds, reading no segment; bytes take a pass over the segments the connections meet.
            if (address is null && rankBy != RankingMetric.Records)
            {
                traffic = SessionHostTraffic.Of(store, links, rankBy.Value, cancellationToken: cancellationToken);
            }
        }
        catch (InvalidOperationException exception) when (exception is not NoSessionException)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        // An investigation the session is a member of proposes candidate other ends for its hosts, from its other members'
        // captures (§8.3).
        IReadOnlyDictionary<string, CandidateHost>? candidates = null;
        if (investigationText is not null)
        {
            string investigation = Path.GetFullPath(investigationText);
            try
            {
                InvestigationWorkspaceFile workspace = InvestigationWorkspace.Read(investigation);
                if (workspace.Members.All(member => member.SessionId != overview.SessionId))
                {
                    ConsoleUi.Failure($"This session is no member of {investigation}, so it proposes no other end for its hosts. icat "
                        + "workspace add puts it in.");
                    return InterCatExitCode.InvalidInvocation;
                }

                ConsoleUi.Progress("Reading every member's one-sided connections and comparing their endpoints, mirrored.");
                candidates = InvestigationHosts.Of(workspace,
                        WorkspaceCorrelation.Candidates(investigation, overview.Policy, cancellationToken), overview.SessionId)
                    .ToDictionary(host => host.Address, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException
                or UnauthorizedAccessException)
            {
                ConsoleUi.Failure($"The investigation {investigation} could not be read: {exception.Message}");
                return InterCatExitCode.InvalidInvocation;
            }
        }

        return address is null
            ? ListHosts(overview, links, program, traffic, candidates, rankBy.Value, path, limit, json)
            : ListReached(overview, links, program, path, address, limit, json);
    }

    /// <summary>
    /// Every other host <paramref name="links"/> reached - every process's, or <paramref name="program"/>'s processes' - the
    /// busiest first by records, or by the bytes <paramref name="traffic"/> read for a byte ranking, as many as the limit
    /// allows.
    /// </summary>
    private static InterCatExitCode ListHosts(SessionOverviewBundle overview, IReadOnlyList<HostLink> links, ProcessGroup? program,
        IReadOnlyList<HostTraffic>? traffic, IReadOnlyDictionary<string, CandidateHost>? candidates, RankingMetric rankBy,
        string path, int limit, bool json)
    {
        IReadOnlyList<OtherHost> hosts = traffic is null ? OtherHost.Of(links) : [.. traffic.Select(host => host.Host)];
        int processes = links.Select(link => link.Process).Distinct().Count();
        Dictionary<ProcessInstanceId, ProcessNode> nodes = overview.Nodes.ToDictionary(node => node.Id);
        IReadOnlyDictionary<string, IReadOnlyList<(ProcessInstanceId Process, long Records)>> reachers = HostLinkText.Reachers(links);
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                Contract = Contract,
                SessionPath = path,
                Program = Described(overview, program),
                RankedBy = RankName(rankBy),
                Hosts = hosts.Count,
                Processes = processes,
                Listed = hosts.Take(limit).Select((host, index) => new
                {
                    host.Address,
                    Scope = AddressScope.Of(host.Address),
                    host.TcpConnections,
                    host.UdpFlows,
                    host.Processes,
                    ReachedBy = (reachers.GetValueOrDefault(host.Address) ?? []).Select(reach => new
                    {
                        Process = reach.Process.Value,
                        Name = nodes[reach.Process].NameWithPid,
                        reach.Records,
                    }),
                    host.Records,
                    host.Ports,
                    host.Connected,
                    host.Accepted,
                    host.AcceptedPorts,
                    Transfers = traffic is null ? null : new
                    {
                        traffic[index].Sends,
                        traffic[index].SentBytes,
                        traffic[index].UnmeasuredSends,
                        traffic[index].Receives,
                        traffic[index].ReceivedBytes,
                        traffic[index].UnmeasuredReceives,
                    },
                    CandidateOtherEnds = candidates is null ? null
                        : candidates.TryGetValue(host.Address, out CandidateHost? proposed)
                            ? proposed.Ends.Select(end => new
                            {
                                Session = end.SessionId,
                                end.HostId,
                                Host = end.HostAlias,
                                end.Process,
                                end.ProcessId,
                                end.Connections,
                                end.Accepted,
                            }).ToArray()
                            : [],
                }),
            }, JsonContracts.Indented));
            return InterCatExitCode.Success;
        }

        ConsoleUi.Heading(program is null ? "Other hosts" : $"Other hosts {program.Name}'s processes reached");
        ConsoleUi.Field("Session", path);
        FieldProgram(overview, program);
        if (hosts.Count == 0)
        {
            ConsoleUi.Note((program is null ? "No process of this session" : $"No process of {program.Name}")
                + " reached another host: every TCP connection and UDP flow had a process of the capture at its other end, or "
                + "no record names where it ran.");
            return InterCatExitCode.Success;
        }

        ConsoleUi.Field("Hosts", CountText.Of(hosts.Count, "host") + ", reached by " + CountText.Of(processes, "process", "processes"));
        ConsoleUi.Field("Ranked by", traffic is null
            ? "the records their connections hold"
            : RankingMetrics.Phrase(rankBy) + ", as this machine's end of their connections measured them; hosts whose "
                + "transfers stated no size follow, then those that made none");
        // Each row: who reached the host and over what, the connections accepted from it where any listed host has some, its
        // bytes under a byte ranking, and the candidate other ends an investigation proposes.
        bool accepting = hosts.Take(limit).Any(host => host.Accepted > 0);
        string[] heads = ["Host", "Scope", "Processes", "Connections", "Ports", .. accepting ? ["Accepted here"] : Array.Empty<string>(),
            "Records",
            .. traffic is null ? Array.Empty<string>() : ["Sent", "Received"],
            .. candidates is null ? Array.Empty<string>() : ["Candidate other ends"]];
        ConsoleUi.Table(heads, [.. hosts.Take(limit).Select((host, index) => (IReadOnlyList<string>)
        [
            host.Address, AddressScope.Of(host.Address) ?? "-", ProcessesCell(reachers.GetValueOrDefault(host.Address), nodes),
            HostLinkText.Reached(host, countsProcesses: false),
            host.Ports.Count == 0 ? "-" : HostLinkText.PortNumbers(host.Ports),
            .. accepting ? [HostLinkText.AcceptedCell(host.Accepted, host.AcceptedPorts)] : Array.Empty<string>(),
            ConsoleUi.Count(host.Records),
            .. traffic is null ? Array.Empty<string>() :
            [
                Bytes(traffic[index].SentBytes, traffic[index].Sends, traffic[index].UnmeasuredSends, "no sends"),
                Bytes(traffic[index].ReceivedBytes, traffic[index].Receives, traffic[index].UnmeasuredReceives, "no receives"),
            ],
            .. candidates is null ? Array.Empty<string>()
                : [candidates.TryGetValue(host.Address, out CandidateHost? proposed) ? proposed.Words : "none"],
        ])]);
        if (hosts.Count > limit)
        {
            ConsoleUi.Note(string.Create(CultureInfo.CurrentCulture,
                $"{hosts.Count - limit:N0} more, quieter; --limit {hosts.Count:N0} lists every one."));
        }

        ConsoleUi.Note("An address is where the connections ran; who is at it is not observed. Processes names the busiest "
            + "process whose connections ran there and how many more there are; icat hosts <directory> --address <address> "
            + "lists every one.");
        if (accepting)
        {
            ConsoleUi.Note("Accepted here counts the TCP connections a process of this session accepted from the host, as the "
                + "capture saw them open, with its own ports they arrived at; their ports at the host are the other end's "
                + "own and not among its Ports.");
        }
        if (candidates is not null)
        {
            ConsoleUi.Note("A candidate other end is a process of another member's capture whose connection mirrors one to the "
                + "host, never an established join: mirrored endpoints can be two connections behind an address translation, "
                + "and a port used again is another connection. A join a person rejected names none. icat workspace correlate "
                + "<investigation> lists each candidate with its evidence.");
        }

        return InterCatExitCode.Success;
    }

    /// <summary>
    /// The processes whose connections reached an address the query names - every process's, or <paramref name="program"/>'s
    /// processes' - each address apart, the busiest first.
    /// </summary>
    private static InterCatExitCode ListReached(SessionOverviewBundle overview, IReadOnlyList<HostLink> links, ProcessGroup? program,
        string path, string query, int limit, bool json)
    {
        Dictionary<ProcessInstanceId, ProcessNode> nodes = overview.Nodes.ToDictionary(node => node.Id);
        var reached = links
            .SelectMany(link => link.Addresses
                .Where(address => WorkspaceSearch.NamesHost(address, query))
                .Select(address => (Link: link, Address: address)))
            .OrderByDescending(entry => entry.Address.Records)
            .ThenBy(entry => entry.Link.Process.ToString(), StringComparer.Ordinal)
            .ThenBy(entry => entry.Address.Address, StringComparer.Ordinal)
            .ThenBy(entry => entry.Link.Mechanism)
            .ToArray();
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                Contract = Contract,
                SessionPath = path,
                Program = Described(overview, program),
                Address = query.Trim(),
                Reached = reached.Take(limit).Select(entry => new
                {
                    Process = entry.Link.Process.Value,
                    Name = nodes[entry.Link.Process].NameWithPid,
                    entry.Address.Address,
                    Mechanism = MechanismText.Name(entry.Link.Mechanism),
                    entry.Address.Connections,
                    entry.Address.Ports,
                    entry.Address.Connected,
                    entry.Address.Accepted,
                    entry.Address.AcceptedPorts,
                    entry.Address.Records,
                    entry.Link.Strength,
                }),
                Matched = reached.Length,
            }, JsonContracts.Indented));
            return InterCatExitCode.Success;
        }

        ConsoleUi.Heading((program is null ? "Processes" : $"{program.Name}'s processes") + " that reached " + query.Trim());
        ConsoleUi.Field("Session", path);
        FieldProgram(overview, program);
        if (reached.Length == 0)
        {
            string none = program is null ? "No process of this session" : $"No process of {program.Name}";
            string every = "icat hosts <directory>" + (program is null ? string.Empty : " --program " + CommandArgument.Of(program.Name));
            ConsoleUi.Note(WorkspaceSearch.NamesPort(query)
                ? $"{none} reached {query.Trim()}: an address whole at that port, or any address at a port "
                    + $"alone, nor accepted connections at a port alone of its own. {every} lists every host they reached, "
                    + "with its ports."
                : $"{none} reached an address {query.Trim()} names: the address whole, or begun up to a dot "
                    + $"or colon. {every} lists every host they reached.");
            return InterCatExitCode.Success;
        }

        bool accepting = reached.Take(limit).Any(entry => entry.Address.Accepted > 0);
        ConsoleUi.Table(
            ["Process", "Address", "Connections", "Ports", .. accepting ? ["Accepted here"] : Array.Empty<string>(), "Records"],
            [.. reached.Take(limit).Select(entry => (IReadOnlyList<string>)
                [CollectorText.Named(nodes[entry.Link.Process]), entry.Address.Address,
                    HostLinkText.Connections(entry.Link.Mechanism, entry.Address.Connections),
                    entry.Address.Ports.Count == 0 ? "-" : HostLinkText.PortNumbers(entry.Address.Ports),
                    .. accepting ? [HostLinkText.AcceptedCell(entry.Address.Accepted, entry.Address.AcceptedPorts)] : Array.Empty<string>(),
                    ConsoleUi.Count(entry.Address.Records)])]);
        if (reached.Length > limit)
        {
            ConsoleUi.Note(string.Create(CultureInfo.CurrentCulture,
                $"{reached.Length - limit:N0} more, quieter; --limit {reached.Length:N0} lists every one."));
        }

        ConsoleUi.Note("Who is at an address is not observed. icat processes <directory> --pid <pid> lists every other host a "
            + "process reached.");
        return InterCatExitCode.Success;
    }

    /// <summary>
    /// The processes that reached a host as a table's cell names them (<see cref="HostLinkText.ReachedBy"/>): the busiest,
    /// "updater.exe · PID 310", and how many more there are, "browser.exe · PID 300 and 4 more"; "-" for none.
    /// </summary>
    internal static string ProcessesCell(IReadOnlyList<(ProcessInstanceId Process, long Records)>? reachers,
        IReadOnlyDictionary<ProcessInstanceId, ProcessNode> nodes) =>
        reachers is not { Count: > 0 } ? "-" : HostLinkText.ReachedBy(reachers, process => CollectorText.Named(nodes[process]));

    /// <summary>A ranking as --rank-by names it.</summary>
    private static string RankName(RankingMetric metric) => metric switch
    {
        RankingMetric.BytesSent => "bytes-sent",
        RankingMetric.BytesReceived => "bytes-received",
        RankingMetric.EndpointBytes => "bytes-sent-and-received",
        _ => "records",
    };

    /// <summary>
    /// One direction's bytes as the window's rows say them (R21): what its measured transfers carried, "unmeasured" when
    /// none of them stated a size, or <paramref name="none"/> when it made none.
    /// </summary>
    internal static string Bytes(long bytes, long transfers, long unmeasured, string none) =>
        transfers > unmeasured ? ConsoleUi.Bytes(bytes) : transfers > 0 ? "unmeasured" : none;

    /// <summary>The program its hosts were limited to, as icat programs describes one; null without --program.</summary>
    private static object? Described(SessionOverviewBundle overview, ProcessGroup? program) => program is null ? null
        : new { program.Name, Path = program.Detail, Processes = overview.Nodes.Count(node => node.GroupKey == program.Key) };

    /// <summary>Names the program its hosts were limited to, and how many processes it ran, as icat programs does.</summary>
    private static void FieldProgram(SessionOverviewBundle overview, ProcessGroup? program)
    {
        if (program is not null)
        {
            ConsoleUi.Field("Program", program.Name + " · "
                + CountText.Of(overview.Nodes.Count(node => node.GroupKey == program.Key), "process", "processes"));
        }
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat hosts <session-directory> [--address <address>] [--program <name>] [--limit <n>]");
        ConsoleUi.Line("           [--rank-by records|bytes-sent|bytes-received|bytes-sent-and-received]");
        ConsoleUi.Line("           [--investigation <file>] [--json]");
        ConsoleUi.Line("  The other hosts the session's processes reached: each address their connections ran to with no");
        ConsoleUi.Line("  process of the capture at the other end, the busiest first by the records those connections hold,");
        ConsoleUi.Line("  as the window's Other hosts node names them, with the ports at each they ran to and, where a");
        ConsoleUi.Line("  process accepted connections from a host, how many and at which of its own ports. Each names the");
        ConsoleUi.Line("  process whose connections there hold the most records and how many more reached it. --address");
        ConsoleUi.Line("  lists the processes that reached it - the address whole, or begun up to a dot or colon, as 142.250");
        ConsoleUi.Line("  or 2001:db8:, an address at a port, as 142.250.1.1:443 or [2001:db8::1]:443, or a port at any");
        ConsoleUi.Line("  address, as :443, or which accepted connections at that port of their own - as the window's");
        ConsoleUi.Line($"  search finds them. --limit lists at most that many, {DefaultLimit} by default. --rank-by lists the");
        ConsoleUi.Line("  hosts by the bytes their connections sent there, received from there or both, as this machine's");
        ConsoleUi.Line("  end of them measured, with both directions' bytes; a host whose transfers recorded no size comes");
        ConsoleUi.Line("  after those that did, never as a zero.");
        ConsoleUi.Line("  --program takes one program's processes alone, named as icat programs lists it - svchost.exe - or by");
        ConsoleUi.Line("  its executable's path: the hosts they reached, or with --address those of them that reached one.");
        ConsoleUi.Line("  --investigation names, for a session that is one of its members, each host's candidate other");
        ConsoleUi.Line("  ends: the processes of its other members' captures whose connections mirror one to the host, never");
        ConsoleUi.Line("  an established join. Who is at an address, and what a port serves there, are not observed.");
    }
}
