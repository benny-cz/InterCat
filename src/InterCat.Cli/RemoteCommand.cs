using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using InterCat.Domain;
using InterCat.Remote;

namespace InterCat.Cli;

/// <summary>
/// `icat remote`: this user's InterCat as a controller of remote agents (ADR-117, `remote-agent-v1`). It enrolls with an
/// agent by the token its administrator gave, keeping the agent's key, and lists the agents it enrolled with, checking
/// each where asked. It reaches an agent only where told to, and only as the key it enrolled with.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class RemoteCommand
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task<InterCatExitCode> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.TryTakeFlag("--help") || command.TryTakeFlag("-h"))
        {
            PrintHelp();
            return InterCatExitCode.Success;
        }

        string? verb = command.TakeVerb();
        if (verb is null)
        {
            ConsoleUi.Failure("icat remote takes enroll or hosts.");
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        return verb switch
        {
            "enroll" => await EnrollAsync(command, cancellationToken).ConfigureAwait(false),
            "hosts" => await HostsAsync(command, cancellationToken).ConfigureAwait(false),
            _ => Unknown(verb),
        };
    }

    private static async Task<InterCatExitCode> EnrollAsync(CommandLine command, CancellationToken cancellationToken)
    {
        string? root = command.TakeOption("--state");
        bool json = command.TryTakeFlag("--json");
        string? address = command.TakePositional();
        string? tokenText = command.TakePositional();
        if (Refused(command) is { } refused)
        {
            return refused;
        }

        if (address is null || !IPEndPoint.TryParse(address, out IPEndPoint? endpoint) || endpoint.Port == 0)
        {
            ConsoleUi.Failure("Give the agent's address and port, such as 192.0.2.10:7457, then its token: an address, not a name - "
                + "InterCat looks up no name.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (!EnrollmentToken.TryParse(tokenText, out EnrollmentToken? token, out string? problem))
        {
            ConsoleUi.Failure((problem ?? "Give the agent's token.") + " Its administrator prints it with icat agent token.");
            return InterCatExitCode.InvalidInvocation;
        }

        ControllerState? state = Open(root, out InterCatExitCode failed);
        if (state is null)
        {
            return failed;
        }

        try
        {
            using RemoteIdentity identity = state.Identity();
            await using RemoteAgentConnection connection = await RemoteAgentConnection
                .ConnectAsync(endpoint, identity, token!.Agent, Timeout, cancellationToken).ConfigureAwait(false);
            RemoteEnrolled enrolled = await connection.EnrollAsync(token, cancellationToken).ConfigureAwait(false);
            RemoteInventory inventory = await connection.InventoryAsync(cancellationToken).ConfigureAwait(false);
            var known = new KnownAgent(endpoint, token.Agent, enrolled.HostId, inventory.MachineName, enrolled.Grants, DateTimeOffset.UtcNow);
            state.Remember(known);
            if (json)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(new
                {
                    Contract = "remote-enrollment-v1",
                    Endpoint = endpoint.ToString(),
                    Agent = token.Agent.Hex,
                    HostId = enrolled.HostId,
                    inventory.MachineName,
                    Grants = enrolled.Grants.Names(),
                    Controller = identity.Pin.Hex,
                }, JsonContracts.Indented));
                return InterCatExitCode.Success;
            }

            ConsoleUi.Heading("Enrolled");
            ConsoleUi.Field("Agent", $"{inventory.MachineName} at {endpoint}");
            ConsoleUi.Field("Agent's key", token.Agent.Hex);
            ConsoleUi.Field("Grants", enrolled.Grants == RemoteGrants.None ? "none this agent allows" : string.Join(", ", enrolled.Grants.Names()));
            ConsoleUi.Field("This controller", identity.Pin.Hex);
            ConsoleUi.Note("The agent knows this controller by its key from now on; icat remote hosts lists the agents enrolled with.");
            return InterCatExitCode.Success;
        }
        catch (RemoteAgentException exception)
        {
            ConsoleUi.Failure(exception.Code switch
            {
                RemoteErrorCode.TokenRefused => "The agent refused the token: it was used, it lapsed, or the agent never issued it. Ask its administrator for a new one.",
                RemoteErrorCode.Revoked => "The agent revoked this controller's key; it enrolls with no token. Its administrator decides whether it may enroll again, with a new key.",
                _ => exception.Message,
            });
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }
        catch (Exception exception) when (exception is CryptographicException or InvalidDataException)
        {
            ConsoleUi.Failure($"This controller's key or list of agents in {state.Root} does not read: {exception.Message}");
            return InterCatExitCode.CorruptedInput;
        }
    }

    private static async Task<InterCatExitCode> HostsAsync(CommandLine command, CancellationToken cancellationToken)
    {
        string? root = command.TakeOption("--state");
        bool check = command.TryTakeFlag("--check");
        bool json = command.TryTakeFlag("--json");
        if (Refused(command) is { } refused)
        {
            return refused;
        }

        ControllerState? state = Open(root, out InterCatExitCode failed);
        if (state is null)
        {
            return failed;
        }

        IReadOnlyList<KnownAgent> agents;
        RemoteIdentity? identity = null;
        try
        {
            agents = state.Agents();
            identity = check && agents.Count > 0 ? state.Identity() : null;
        }
        catch (Exception exception) when (exception is CryptographicException or InvalidDataException)
        {
            ConsoleUi.Failure($"This controller's key or list of agents in {state.Root} does not read: {exception.Message}");
            return InterCatExitCode.CorruptedInput;
        }

        using (identity)
        {
            var checks = new List<string?>();
            foreach (KnownAgent agent in agents)
            {
                checks.Add(identity is null ? null : await CheckAsync(agent, identity, cancellationToken).ConfigureAwait(false));
            }

            if (json)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(new
                {
                    Contract = "remote-hosts-v1",
                    Agents = agents.Select((agent, at) => new
                    {
                        Endpoint = agent.Endpoint.ToString(),
                        Pin = agent.Pin.Hex,
                        HostId = agent.HostId,
                        agent.MachineName,
                        Grants = agent.Grants.Names(),
                        EnrolledUtc = agent.EnrolledUtc.UtcDateTime,
                        Check = checks[at],
                    }),
                }, JsonContracts.Indented));
                return InterCatExitCode.Success;
            }

            ConsoleUi.Heading("Agents enrolled with");
            if (agents.Count == 0)
            {
                ConsoleUi.Note("None: icat remote enroll <address:port> <token> enrolls with one.");
                return InterCatExitCode.Success;
            }

            ConsoleUi.Table(check ? ["Host", "Address", "Key", "Grants", "Now"] : ["Host", "Address", "Key", "Grants", "Enrolled"],
                [.. agents.Select((agent, at) => (IReadOnlyList<string>)
                [
                    agent.MachineName,
                    agent.Endpoint.ToString(),
                    agent.Pin.Abbreviated,
                    string.Join(", ", agent.Grants.Names()),
                    checks[at] ?? agent.EnrolledUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
                ])]);
            return InterCatExitCode.Success;
        }
    }

    /// <summary>What an agent says of this controller now, in the words icat remote hosts uses.</summary>
    private static async Task<string> CheckAsync(KnownAgent agent, RemoteIdentity identity, CancellationToken cancellationToken)
    {
        try
        {
            await using RemoteAgentConnection connection = await RemoteAgentConnection
                .ConnectAsync(agent.Endpoint, identity, agent.Pin, Timeout, cancellationToken).ConfigureAwait(false);
            RemoteHello hello = await connection.HelloAsync(cancellationToken).ConfigureAwait(false);
            if (hello.Enrolled)
            {
                return hello.HostId == agent.HostId
                    ? "enrolled: " + (hello.Grants == RemoteGrants.None ? "nothing allowed" : string.Join(", ", hello.Grants.Names()))
                    : "enrolled, but the agent names another host than it did";
            }

            // Hello tells a key not enrolled nothing more; inventory says whether it was revoked.
            try
            {
                _ = await connection.InventoryAsync(cancellationToken).ConfigureAwait(false);
                return "not enrolled";
            }
            catch (RemoteAgentException refusal) when (refusal.Code == RemoteErrorCode.Revoked)
            {
                return "revoked";
            }
        }
        catch (RemoteAgentException exception) when (exception.Code is null)
        {
            return exception.Presented.IsEmpty ? "unreachable" : "another key answers there";
        }
        catch (RemoteAgentException exception) when (exception.Code == RemoteErrorCode.NotEnrolled)
        {
            return "not enrolled";
        }
        catch (RemoteAgentException exception)
        {
            return "refused: " + RemoteAgentServer.Words(exception.Code!.Value);
        }
    }

    private static ControllerState? Open(string? root, out InterCatExitCode failed)
    {
        failed = InterCatExitCode.PermissionOrCapabilityFailure;
        try
        {
            return ControllerState.Open(root ?? ControllerState.DefaultRoot());
        }
        catch (UnauthorizedAccessException exception)
        {
            ConsoleUi.Failure(exception.Message);
            return null;
        }
    }

    private static InterCatExitCode? Refused(CommandLine command)
    {
        if (!command.TryReportUnknown(out string? unknown))
        {
            return null;
        }

        ConsoleUi.Failure(command.Refusal(unknown!));
        ConsoleUi.Explain(PrintHelp);
        return InterCatExitCode.InvalidInvocation;
    }

    private static InterCatExitCode Unknown(string verb)
    {
        ConsoleUi.Failure($"icat remote has no '{verb}': it takes enroll or hosts.");
        ConsoleUi.Explain(PrintHelp);
        return InterCatExitCode.InvalidInvocation;
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat remote enroll <address:port> <token> [--state <folder>] [--json]");
        ConsoleUi.Line("icat remote hosts [--check] [--state <folder>] [--json]");
        ConsoleUi.Line();
        ConsoleUi.Line("  This InterCat as a controller of remote agents. enroll enrolls with the agent at the address given by");
        ConsoleUi.Line("  the token its administrator printed with icat agent token, going on only where the agent's key is the");
        ConsoleUi.Line("  one the token names. hosts lists the agents enrolled with; --check asks each what it allows now. The");
        ConsoleUi.Line("  controller keeps its key, protected for its user, and its agents in a folder only its user may use:");
        ConsoleUi.Line("  remote in InterCat's folder under the user's local application data, unless --state names another.");
    }
}
