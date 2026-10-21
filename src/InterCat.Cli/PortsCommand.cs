using System.Globalization;
using System.Text.Json;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>
/// The ports this machine's processes accepted TCP connections at (§6.3, §7.4, R18): each port with the process instance
/// that accepted connections there, how many, and who connected - processes of the capture, other hosts by address, and
/// processes on this machine the capture did not observe - the busiest first. It is the session's netstat of listeners,
/// read from the connections the capture saw open, as each process's card counts them.
/// </summary>
internal static class PortsCommand
{
    /// <summary>The document --json writes: how many ports and listeners there are, and the listeners listed.</summary>
    internal const string Contract = "listening-ports-v1";

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

        string? limitText = command.TakeOption("--limit");
        string? investigationText = command.TakeOption("--investigation");
        bool json = command.TryTakeFlag("--json");
        string? directory = command.TakePositional();
        bool hasUnknown = command.TryReportUnknown(out string? unknown);
        if (directory is null || hasUnknown)
        {
            ConsoleUi.Failure(directory is null
                ? "A session directory is required: icat ports <directory>."
                : command.Refusal(unknown!));
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        int limit = DefaultLimit;
        if (limitText is not null
            && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1))
        {
            ConsoleUi.Failure($"--limit expects a number of listeners of 1 or more; '{limitText}' is not one.");
            return InterCatExitCode.InvalidInvocation;
        }

        string path = Path.GetFullPath(directory);
        if (!Directory.Exists(path))
        {
            ConsoleUi.Failure($"No session directory at {path}.");
            return InterCatExitCode.InvalidInvocation;
        }

        SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(path));
        SessionOverviewBundle overview;
        try
        {
            overview = SessionOverviewProjector.Project(store, cancellationToken: cancellationToken);
        }
        catch (InvalidOperationException exception) when (exception is not NoSessionException)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        // Which process accepted a connection is known only where the capture saw it open (relations-v1 §5): a session that
        // records no open says so, rather than listing no port as though none was listened at.
        if (!overview.ConnectionOpensRecorded || SessionMetricDerivations.For(store, cancellationToken) is not { } derivations)
        {
            ConsoleUi.Failure("This session records no connection's open, so at which ports its processes accepted connections is "
                + "unknown rather than none.");
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        // An investigation the session is a member of proposes, from its other members' captures, which of their processes
        // connected to each listener here (§8.3).
        IReadOnlyDictionary<(ProcessInstanceId Process, ushort Port), IReadOnlyList<CandidateEnd>>? candidates = null;
        if (investigationText is not null)
        {
            string investigation = Path.GetFullPath(investigationText);
            try
            {
                InvestigationWorkspaceFile workspace = InvestigationWorkspace.Read(investigation);
                if (workspace.Members.All(member => member.SessionId != overview.SessionId))
                {
                    ConsoleUi.Failure($"This session is no member of {investigation}, so it proposes no other end for the "
                        + "connections its processes accepted. icat workspace add puts it in.");
                    return InterCatExitCode.InvalidInvocation;
                }

                ConsoleUi.Progress("Reading every member's one-sided connections and comparing their endpoints, mirrored.");
                candidates = InvestigationHosts.ForListeners(workspace,
                    WorkspaceCorrelation.Candidates(investigation, overview.Policy, cancellationToken), overview.SessionId);
            }
            catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException
                or UnauthorizedAccessException)
            {
                ConsoleUi.Failure($"The investigation {investigation} could not be read: {exception.Message}");
                return InterCatExitCode.InvalidInvocation;
            }
        }

        IReadOnlyList<PortListener> listeners = ConnectionOpens.Listeners(derivations.Relations(cancellationToken),
            EvidencePolicy.IncludeCorrelated);
        var names = new Names(overview);
        int ports = listeners.Select(listener => listener.Port).Distinct().Count();
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                Contract = Contract,
                SessionPath = path,
                Ports = ports,
                Listeners = listeners.Count,
                Listed = listeners.Take(limit).Select(listener => new
                {
                    listener.Port,
                    Process = names.Described(listener.Process),
                    listener.Accepted,
                    listener.FromProcesses,
                    listener.FromHosts,
                    listener.FromUnseenLocalEnds,
                    Connectors = listener.Connectors.Select(connector => new
                    {
                        Process = names.Described(connector.Process),
                        connector.Connections,
                    }),
                    Hosts = listener.Hosts.Select(host => new { host.Address, host.Connections }),
                    CandidateOtherEnds = candidates is null ? null
                        : candidates.GetValueOrDefault((listener.Process.Id, listener.Port), []).Select(end => new
                        {
                            Session = end.SessionId,
                            end.HostId,
                            Host = end.HostAlias,
                            end.Process,
                            end.ProcessId,
                            end.Connections,
                            end.Accepted,
                        }).ToArray(),
                }),
            }, JsonContracts.Indented));
            return InterCatExitCode.Success;
        }

        ConsoleUi.Heading("Listening ports");
        ConsoleUi.Field("Session", path);
        if (listeners.Count == 0)
        {
            ConsoleUi.Note("No process of this session accepted a TCP connection the capture saw open: each it saw open was made "
                + "to an end no record holds, most often another host's. icat hosts <directory> lists the hosts its processes "
                + "connected to.");
            return InterCatExitCode.Success;
        }

        int instances = listeners.Select(listener => listener.Process.Id).Distinct().Count();
        ConsoleUi.Field("Ports", CountText.Of(ports, "port") + ", at which " + CountText.Of(instances, "process", "processes")
            + " accepted " + CountText.Of(listeners.Sum(listener => listener.Accepted), "TCP connection"));
        ConsoleUi.Field("Ranked by", "the connections each accepted, the most first, then by port");
        ConsoleUi.Table(
            ["Port", "Accepted by", "Connections", "From", .. candidates is null ? Array.Empty<string>() : ["Candidate other ends"]],
            [.. listeners.Take(limit).Select(listener => (IReadOnlyList<string>)
            [
                listener.Port.ToString(CultureInfo.InvariantCulture), names.Named(listener.Process),
                ConsoleUi.Count(listener.Accepted), From(listener, names),
                .. candidates is null ? Array.Empty<string>()
                    : [listener.FromHosts == 0 ? "-"
                        : candidates.TryGetValue((listener.Process.Id, listener.Port), out IReadOnlyList<CandidateEnd>? ends)
                            ? CandidateEnd.Listed(ends)
                            : "none"],
            ])]);
        if (listeners.Count > limit)
        {
            ConsoleUi.Note(string.Create(CultureInfo.CurrentCulture,
                $"{listeners.Count - limit:N0} more, quieter; --limit {listeners.Count:N0} lists every one."));
        }

        ConsoleUi.Note("Each port is the process's own, which the TCP connections it accepted arrived at, as the capture saw "
            + "them open, whoever connected: a process of this session, another host by its address, or a process on this "
            + "machine the capture did not observe. icat processes <directory> --port <port> lists a port's listeners with "
            + "their traffic, and --pid <PID> whom each connected with.");
        if (candidates is not null)
        {
            ConsoleUi.Note("A candidate other end is a process of another member's capture whose connection mirrors one the "
                + "listener accepted from another host, never an established join: mirrored endpoints can be two connections "
                + "behind an address translation, and a port used again is another connection. A join a person rejected names "
                + "none, and - is a listener no other host connected to. icat workspace correlate <investigation> lists each "
                + "candidate with its evidence.");
        }

        return InterCatExitCode.Success;
    }

    /// <summary>
    /// Who connected to a listener, in a cell, each with the connections it made there (<see cref="ListenerText.From"/>):
    /// "34 from 34 processes of chrome.exe", "13 from 13 processes of svchost.exe and services.exe · 1 from 10.0.0.6".
    /// </summary>
    private static string From(PortListener listener, Names names) => string.Join(" · ", ListenerText
        .From(listener, names.Named, names.Program)
        .Select(part => ConsoleUi.Count(part.Connections) + " from " + part.Who));

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat ports <session-directory> [--limit <n>] [--investigation <file>] [--json]");
        ConsoleUi.Line("  The ports this machine's processes accepted TCP connections at, as the capture saw them open:");
        ConsoleUi.Line("  each port with the process that accepted connections there, how many, and who connected - its");
        ConsoleUi.Line("  processes, other hosts by address, or processes the capture did not observe - the busiest");
        ConsoleUi.Line("  first. --limit lists that many, 50 unless it says so; --json writes them as a document, each");
        ConsoleUi.Line("  with every process and host that connected. --investigation names, for a session that is one");
        ConsoleUi.Line("  of its members, the processes of its other captures whose connections mirror those each");
        ConsoleUi.Line("  listener accepted: candidates for who connected from another machine, never established.");
    }

    /// <summary>The processes and programs of a session's overview, as its tables name them.</summary>
    private sealed class Names(SessionOverviewBundle overview)
    {
        private readonly Dictionary<ProcessInstanceId, ProcessNode> nodes = overview.Nodes.ToDictionary(node => node.Id);
        private readonly Dictionary<string, ProcessGroup> groups = overview.Groups.ToDictionary(group => group.Key, StringComparer.Ordinal);

        /// <summary>A process as the overview's tables name it: "svchost.exe · PID 1020".</summary>
        public string Named(ProcessInstance process) => nodes.TryGetValue(process.Id, out ProcessNode? node)
            ? CollectorText.Named(node)
            : string.Create(CultureInfo.InvariantCulture, $"PID {process.ProcessId}");

        /// <summary>The program a process ran, as the overview groups it, or the process itself where it holds none.</summary>
        public string Program(ProcessInstance process) => nodes.TryGetValue(process.Id, out ProcessNode? node)
            ? groups[node.GroupKey].Name
            : Named(process);

        /// <summary>A process as the JSON names it: its PID, program and instance.</summary>
        public object Described(ProcessInstance process) => new
        {
            process.ProcessId,
            Name = nodes.TryGetValue(process.Id, out ProcessNode? node) ? node.Name : process.ImageName,
            InstanceId = process.Id.ToString(),
        };
    }
}
