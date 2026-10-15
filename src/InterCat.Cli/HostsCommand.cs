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
/// the records those connections hold, or the processes that reached an address. An address is where the connections
/// ran; who is at it is not observed (P7).
/// </summary>
internal static class HostsCommand
{
    /// <summary>
    /// The document --json writes: every host's count and the listed hosts, or the processes that reached an address, each
    /// address with the ports at it the connections ran to since its second version.
    /// </summary>
    internal const string Contract = "other-hosts-v2";

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
        try
        {
            overview = SessionOverviewProjector.Project(
                SessionStore.OpenExisting(LocalOwnedDirectory.Open(path)), cancellationToken: cancellationToken);
        }
        catch (InvalidOperationException exception) when (exception is not NoSessionException)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        return address is null ? ListHosts(overview, path, limit, json) : ListReached(overview, path, address, limit, json);
    }

    /// <summary>Every other host, the busiest first, as many as the limit allows.</summary>
    private static InterCatExitCode ListHosts(SessionOverviewBundle overview, string path, int limit, bool json)
    {
        IReadOnlyList<OtherHost> hosts = OtherHost.Of(overview.HostLinks);
        int processes = overview.HostLinks.Select(link => link.Process).Distinct().Count();
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                Contract = Contract,
                SessionPath = path,
                Hosts = hosts.Count,
                Processes = processes,
                Listed = hosts.Take(limit),
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
        ConsoleUi.Table(
            ["Host", "Connections", "Ports", "Records"],
            [.. hosts.Take(limit).Select(host => (IReadOnlyList<string>)
                [host.Address, HostLinkText.Reached(host), HostLinkText.PortNumbers(host.Ports), ConsoleUi.Count(host.Records)])]);
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

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat hosts <session-directory> [--address <address>] [--limit <n>] [--json]");
        ConsoleUi.Line("  The other hosts the session's processes reached: each address their connections ran to with no");
        ConsoleUi.Line("  process of the capture at the other end, the busiest first by the records those connections hold,");
        ConsoleUi.Line("  as the window's Other hosts node names them, with the ports at each they ran to. --address lists");
        ConsoleUi.Line("  the processes that reached it - the address whole, or begun up to a dot or colon, as 142.250 or");
        ConsoleUi.Line("  2001:db8:, an address at a port, as 142.250.1.1:443 or [2001:db8::1]:443, or a port at any");
        ConsoleUi.Line("  address, as :443 - as the window's search finds them. --limit lists at most that many, "
            + $"{DefaultLimit} by default.");
        ConsoleUi.Line("  Who is at an address, and what a port serves there, are not observed.");
    }
}
