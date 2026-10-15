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
/// address. An address is where the connections ran; who is at it is not observed (P7).
/// </summary>
internal static class HostsCommand
{
    /// <summary>
    /// The document --json writes: every host's count and the listed hosts, or the processes that reached an address, each
    /// address with the ports at it the connections ran to since its second version, each listed host with its address's
    /// scope since its third, and since its fourth the ranking the hosts are listed by and, under a byte ranking, what each
    /// host's connections' transfers measured.
    /// </summary>
    internal const string Contract = "other-hosts-v4";

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
        string? limitText = command.TakeOption("--limit");
        string? rankText = command.TakeOption("--rank-by");
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
        if (rankBy is null || (address is not null && rankText is not null))
        {
            ConsoleUi.Failure(rankBy is null
                ? $"--rank-by ranks the hosts by records, bytes-sent, bytes-received or bytes-sent-and-received; '{rankText}' is none of them."
                : "--rank-by ranks the hosts; --address lists the processes that reached one, the busiest by records first.");
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
        try
        {
            SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(path));
            overview = SessionOverviewProjector.Project(store, cancellationToken: cancellationToken);

            // Records the census holds, reading no segment; bytes take a pass over the segments the connections meet.
            if (address is null && rankBy != RankingMetric.Records)
            {
                traffic = SessionHostTraffic.Of(store, overview.HostLinks, rankBy.Value, cancellationToken: cancellationToken);
            }
        }
        catch (InvalidOperationException exception) when (exception is not NoSessionException)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        return address is null
            ? ListHosts(overview, traffic, rankBy.Value, path, limit, json)
            : ListReached(overview, path, address, limit, json);
    }

    /// <summary>
    /// Every other host, the busiest first by records, or by the bytes <paramref name="traffic"/> read for a byte ranking,
    /// as many as the limit allows.
    /// </summary>
    private static InterCatExitCode ListHosts(SessionOverviewBundle overview, IReadOnlyList<HostTraffic>? traffic,
        RankingMetric rankBy, string path, int limit, bool json)
    {
        IReadOnlyList<OtherHost> hosts = traffic is null ? OtherHost.Of(overview.HostLinks) : [.. traffic.Select(host => host.Host)];
        int processes = overview.HostLinks.Select(link => link.Process).Distinct().Count();
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                Contract = Contract,
                SessionPath = path,
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
                    host.Records,
                    host.Ports,
                    Transfers = traffic is null ? null : new
                    {
                        traffic[index].Sends,
                        traffic[index].SentBytes,
                        traffic[index].UnmeasuredSends,
                        traffic[index].Receives,
                        traffic[index].ReceivedBytes,
                        traffic[index].UnmeasuredReceives,
                    },
                }),
            }, JsonContracts.Indented));
            return InterCatExitCode.Success;
        }

        ConsoleUi.Heading("Other hosts");
        ConsoleUi.Field("Session", path);
        if (hosts.Count == 0)
        {
            ConsoleUi.Note("No process of this session reached another host: every TCP connection and UDP flow had a process "
                + "of the capture at its other end, or no record names where it ran.");
            return InterCatExitCode.Success;
        }

        ConsoleUi.Field("Hosts", CountText.Of(hosts.Count, "host") + ", reached by " + CountText.Of(processes, "process", "processes"));
        ConsoleUi.Field("Ranked by", traffic is null
            ? "the records their connections hold"
            : RankingMetrics.Phrase(rankBy) + ", as this machine's end of their connections measured them; hosts whose "
                + "transfers stated no size follow, then those that made none");
        if (traffic is null)
        {
            ConsoleUi.Table(
                ["Host", "Scope", "Connections", "Ports", "Records"],
                [.. hosts.Take(limit).Select(host => (IReadOnlyList<string>)
                    [host.Address, AddressScope.Of(host.Address) ?? "-", HostLinkText.Reached(host), HostLinkText.PortNumbers(host.Ports),
                        ConsoleUi.Count(host.Records)])]);
        }
        else
        {
            ConsoleUi.Table(
                ["Host", "Scope", "Connections", "Ports", "Records", "Sent", "Received"],
                [.. traffic.Take(limit).Select(host => (IReadOnlyList<string>)
                    [host.Host.Address, AddressScope.Of(host.Host.Address) ?? "-", HostLinkText.Reached(host.Host),
                        HostLinkText.PortNumbers(host.Host.Ports), ConsoleUi.Count(host.Host.Records),
                        Bytes(host.SentBytes, host.Sends, host.UnmeasuredSends, "no sends"),
                        Bytes(host.ReceivedBytes, host.Receives, host.UnmeasuredReceives, "no receives")])]);
        }
        if (hosts.Count > limit)
        {
            ConsoleUi.Note(string.Create(CultureInfo.CurrentCulture,
                $"{hosts.Count - limit:N0} more, quieter; --limit {hosts.Count:N0} lists every one."));
        }

        ConsoleUi.Note("An address is where the connections ran; who is at it is not observed. icat hosts <directory> "
            + "--address <address> lists the processes that reached one.");
        return InterCatExitCode.Success;
    }

    /// <summary>The processes whose connections reached an address the query names, each address apart, the busiest first.</summary>
    private static InterCatExitCode ListReached(SessionOverviewBundle overview, string path, string query, int limit, bool json)
    {
        Dictionary<ProcessInstanceId, ProcessNode> nodes = overview.Nodes.ToDictionary(node => node.Id);
        var reached = overview.HostLinks
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
                Address = query.Trim(),
                Reached = reached.Take(limit).Select(entry => new
                {
                    Process = entry.Link.Process.Value,
                    Name = nodes[entry.Link.Process].NameWithPid,
                    entry.Address.Address,
                    Mechanism = MechanismText.Name(entry.Link.Mechanism),
                    entry.Address.Connections,
                    entry.Address.Ports,
                    entry.Address.Records,
                    entry.Link.Strength,
                }),
                Matched = reached.Length,
            }, JsonContracts.Indented));
            return InterCatExitCode.Success;
        }

        ConsoleUi.Heading("Processes that reached " + query.Trim());
        ConsoleUi.Field("Session", path);
        if (reached.Length == 0)
        {
            ConsoleUi.Note(WorkspaceSearch.NamesPort(query)
                ? $"No process of this session reached {query.Trim()}: an address whole at that port, or any address at a port "
                    + "alone. icat hosts <directory> lists every host they reached, with its ports."
                : $"No process of this session reached an address {query.Trim()} names: the address whole, or begun up to a dot "
                    + "or colon. icat hosts <directory> lists every host they reached.");
            return InterCatExitCode.Success;
        }

        ConsoleUi.Table(
            ["Process", "Address", "Connections", "Ports", "Records"],
            [.. reached.Take(limit).Select(entry => (IReadOnlyList<string>)
                [CollectorText.Named(nodes[entry.Link.Process]), entry.Address.Address,
                    HostLinkText.Connections(entry.Link.Mechanism, entry.Address.Connections),
                    HostLinkText.PortNumbers(entry.Address.Ports), ConsoleUi.Count(entry.Address.Records)])]);
        if (reached.Length > limit)
        {
            ConsoleUi.Note(string.Create(CultureInfo.CurrentCulture,
                $"{reached.Length - limit:N0} more, quieter; --limit {reached.Length:N0} lists every one."));
        }

        ConsoleUi.Note("Who is at an address is not observed. icat channels <directory> --process <instance-guid> --one-sided "
            + "lists a process's connections.");
        return InterCatExitCode.Success;
    }

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
    private static string Bytes(long bytes, long transfers, long unmeasured, string none) =>
        transfers > unmeasured ? ConsoleUi.Bytes(bytes) : transfers > 0 ? "unmeasured" : none;

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat hosts <session-directory> [--address <address>] [--limit <n>]");
        ConsoleUi.Line("           [--rank-by records|bytes-sent|bytes-received|bytes-sent-and-received] [--json]");
        ConsoleUi.Line("  The other hosts the session's processes reached: each address their connections ran to with no");
        ConsoleUi.Line("  process of the capture at the other end, the busiest first by the records those connections hold,");
        ConsoleUi.Line("  as the window's Other hosts node names them, with the ports at each they ran to. --address lists");
        ConsoleUi.Line("  the processes that reached it - the address whole, or begun up to a dot or colon, as 142.250 or");
        ConsoleUi.Line("  2001:db8:, an address at a port, as 142.250.1.1:443 or [2001:db8::1]:443, or a port at any");
        ConsoleUi.Line("  address, as :443 - as the window's search finds them. --limit lists at most that many,");
        ConsoleUi.Line($"  {DefaultLimit} by default. --rank-by lists the hosts by the bytes their connections sent there,");
        ConsoleUi.Line("  received from there or both, as this machine's end of them measured, with both directions' bytes;");
        ConsoleUi.Line("  a host whose transfers recorded no size comes after those that did, never as a zero. Who is at an");
        ConsoleUi.Line("  address, and what a port serves there, are not observed.");
    }
}
