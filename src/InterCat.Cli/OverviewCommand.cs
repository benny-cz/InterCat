using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>
/// The Desktop's exact leased overview, headlessly reachable at the same default evidence policy (R18).
/// JSON retains the graph identity, eligible set, channels and caveats rather than re-counting them here.
/// </summary>
internal static class OverviewCommand
{
    public static Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken) =>
        Task.FromResult(Run(command, cancellationToken));

    private static InterCatExitCode Run(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? directory = command.TakePositional();
        bool json = command.TryTakeFlag("--json");
        bool hasUnknown = command.TryReportUnknown(out string? unknown);
        if (directory is null || hasUnknown)
        {
            ConsoleUi.Failure(directory is null
                ? "A session directory is required: icat overview <directory>."
                : command.Refusal(unknown!));
            ConsoleUi.Explain(PrintHelp);
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

        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                Contract = "overview-v1",
                SessionPath = path,
                Bundle = overview,
            }, JsonContracts.Indented));
            return InterCatExitCode.Success;
        }

        ConsoleUi.Heading("Published session overview");
        ConsoleUi.Field("Session", path);
        ConsoleUi.Field("Generation", ConsoleUi.Count(overview.Generation));
        ConsoleUi.Field("Process instances", ConsoleUi.Count(overview.Nodes.Count));
        ConsoleUi.Field("Relationships", ConsoleUi.Count(overview.Edges.Count));
        ConsoleUi.Field("Paired channels", overview.ChannelProjectionProblem is null
            ? ConsoleUi.Count(overview.Channels.Count) : "unavailable above overview bound");
        ConsoleUi.Field("Observed rows", ConsoleUi.Count(overview.ObservationRows));
        ConsoleUi.Field("Graph-eligible rows", ConsoleUi.Count(overview.GraphEligibleRows));
        ConsoleUi.Field("Rows held by none", ConsoleUi.Count(overview.RowsNoProcessHolds));

        // Why the graph draws no relationship, in the words the window says beneath its empty graph (§6.8, R18).
        if (overview.Edges.Count == 0 && overview.Census is { Tcp.Relationships: 0, Udp.Relationships: 0 } census)
        {
            ConsoleUi.Note(RelationshipCensusText.Explain(census, CultureInfo.CurrentCulture));
        }
        else if (overview.Census is { } drawn && RelationshipCensusText.RpcGuidance(drawn) is { } guidance)
        {
            // A graph that draws relationships still leaves RPC calls undrawn that no capture linked, and says how to draw them.
            ConsoleUi.Note(guidance);
        }

        // Which processes collected the capture: each by the instance it is, as the window labels it, or why none is it
        // (collector-binding-v1, §19.5). A capture that names none says nothing of them, rather than that it had none.
        if (overview.Collectors.Recorded)
        {
            ConsoleUi.Heading("Collected by");
            foreach (CollectorInstance collector in overview.Collectors.Instances)
            {
                ConsoleUi.Field(CollectorText.RoleName(collector.Role),
                    overview.Nodes.First(node => node.Id == collector.Instance).NameWithPid);
            }

            foreach (CollectorProcessV1 collector in overview.Collectors.Unfound)
            {
                ConsoleUi.Field(CollectorText.RoleName(collector.Role),
                    string.Create(CultureInfo.InvariantCulture, $"PID {collector.ProcessId}: ") + CollectorText.UnfoundReason(collector));
            }
        }

        // What the capture covered over the session, mechanism by mechanism, in the words the window's inspector states
        // beneath its time scope (R18, R21): a generation without a ledger says it judged nothing.
        if (CoverageText.Describe(overview.MechanismCoverage) is { Length: > 0 } coverage) ConsoleUi.Note(coverage);

        // The Desktop's ranked table in brief: the busiest instances by their own records (process-activity-v1).
        ProcessNode[] busiest = [.. overview.Nodes
            .Where(node => node.Records > 0)
            .OrderByDescending(node => node.Records)
            .ThenBy(node => node.Id.ToString(), StringComparer.Ordinal)
            .Take(5)];
        if (busiest.Length > 0)
        {
            ConsoleUi.Table(
                ["Busiest process", "Own records", "Mostly"],
                [.. busiest.Select(node => (IReadOnlyList<string>)
                    [CollectorText.Named(node), ConsoleUi.Count(node.Records), EvidenceRowText.MechanismName(node.Activity[0].Mechanism)])]);
        }

        // The window's relationship table in brief: the busiest relationships, each arrow pointing at the process its
        // connections were made to where the capture saw them open, never at display order (relations-v1 section 5).
        Dictionary<ProcessInstanceId, ProcessNode> nodes = overview.Nodes.ToDictionary(node => node.Id);
        if (overview.Edges.Count > 0)
        {
            ConsoleUi.Table(
                ["Busiest relationship", "Over", "Records"],
                [.. overview.Edges
                    .OrderByDescending(edge => edge.ObservationCount)
                    .ThenBy(edge => edge.Key, StringComparer.Ordinal)
                    .Take(5)
                    .Select(edge => (IReadOnlyList<string>)
                        [Joined(edge, nodes), MechanismText.Name(edge.Mechanism), ConsoleUi.Count(edge.ObservationCount)])]);
            ConsoleUi.Note(CountText.Of(overview.Edges.Count, "relationship") + " in all. An arrow points at the process "
                + "connected to, where the capture saw the connections open, and ⇄ joins two that each connected to the other; "
                + "two joined by \"and\" were seen opening none. Records are both ends'.");
        }

        // The processes whose connections ran to other hosts, as the window's graph draws them to its Other hosts node: the
        // most connected first, each by its connections over each transport and those it accepted from the hosts, whichever
        // end opened them (relations-v1 section 5c and 5d).
        if (overview.HostLinks.Count > 0)
        {
            ConsoleUi.Field("Other hosts", HostLinkText.Line(overview.HostLinks));
            IGrouping<ProcessInstanceId, HostLink>[] holders = [.. overview.HostLinks
                .GroupBy(link => link.Process)
                .OrderByDescending(links => links.Sum(link => (long)link.Connections))
                .ThenBy(links => links.Key.ToString(), StringComparer.Ordinal)
                .Take(5)];
            bool accepted = holders.Any(links => links.Any(link => link.Addresses.Any(address => address.Accepted > 0)));
            ConsoleUi.Table(
                ["Process", "Connections to other hosts", .. accepted ? ["Accepted here"] : Array.Empty<string>()],
                [.. holders.Select(links => (IReadOnlyList<string>)
                [
                    CollectorText.Named(nodes[links.Key]), HostLinkText.Line(links),
                    .. accepted ? [Accepted(links)] : Array.Empty<string>(),
                ])]);

            // The hosts they ran to, the busiest first by the records their connections hold, as the Other hosts node names
            // them, each with the busiest process there: where the connections ran, and never who is at them (P7).
            IReadOnlyList<OtherHost> hosts = OtherHost.Of(overview.HostLinks);
            IReadOnlyDictionary<string, IReadOnlyList<(ProcessInstanceId Process, long Records)>> reachers =
                HostLinkText.Reachers(overview.HostLinks);
            bool accepting = hosts.Take(5).Any(host => host.Accepted > 0);
            ConsoleUi.Table(
                ["Busiest other host", "Processes", "Connections", "Ports", .. accepting ? ["Accepted here"] : Array.Empty<string>(),
                    "Records"],
                [.. hosts.Take(5).Select(host => (IReadOnlyList<string>)
                [
                    host.Address, HostsCommand.ProcessesCell(reachers.GetValueOrDefault(host.Address), nodes),
                    HostLinkText.Reached(host, countsProcesses: false),
                    host.Ports.Count == 0 ? "-" : HostLinkText.PortNumbers(host.Ports),
                    .. accepting ? [HostLinkText.AcceptedCell(host.Accepted, host.AcceptedPorts)] : Array.Empty<string>(),
                    ConsoleUi.Count(host.Records),
                ])]);
            ConsoleUi.Note(CountText.Of(hosts.Count, "other host") + " in all, counted by address; who is at them is not "
                + "observed. icat hosts <directory> lists every one, and with --address <address> every process that reached one.");
        }

        foreach (string caveat in overview.Caveats) ConsoleUi.Note(caveat);
        ConsoleUi.Note("icat overview <directory> --json returns the exact bundle the Desktop projects.");
        return InterCatExitCode.Success;
    }

    /// <summary>
    /// A relationship's two processes as the overview lists it, the one that made its connections first: "browser.exe · PID
    /// 100 → server.exe · PID 200"; "a ⇄ b" where each connected to the other; and "a and b" where the capture saw none
    /// open, as for a UDP flow or RPC calls (relations-v1 section 5).
    /// </summary>
    internal static string Joined(CommunicationEdge edge, IReadOnlyDictionary<ProcessInstanceId, ProcessNode> nodes)
    {
        string source = CollectorText.Named(nodes[edge.SourceId]);
        string target = CollectorText.Named(nodes[edge.TargetId]);
        return (edge.SourceConnected > 0, edge.TargetConnected > 0) switch
        {
            (true, false) => $"{source} → {target}",
            (false, true) => $"{target} → {source}",
            (true, true) => $"{source} ⇄ {target}",
            _ => $"{source} and {target}",
        };
    }

    /// <summary>The connections a process accepted from other hosts, with its own ports they arrived at: "2 at 443 and 8443", "-".</summary>
    private static string Accepted(IEnumerable<HostLink> links)
    {
        HostAddress[] addresses = [.. links.SelectMany(link => link.Addresses)];
        return HostLinkText.AcceptedCell(addresses.Sum(address => address.Accepted),
            [.. addresses.SelectMany(address => address.AcceptedPorts).Distinct().Order()]);
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat overview <session-directory> [--json]");
        ConsoleUi.Line("  Read-only, leased process graph, admitted paired TCP and UDP channels and all-observations");
        ConsoleUi.Line("  timeline.");
        ConsoleUi.Line("  The graph is narrower than the session. --json returns the Desktop's exact numeric bundle.");
        ConsoleUi.Line("  It says what the capture covered, mechanism by mechanism, in the words the window's inspector");
        ConsoleUi.Line("  uses beneath its time scope, and lists the busiest relationships, each arrow pointing at the");
        ConsoleUi.Line("  process connected to where the capture saw the connections open.");
    }
}
