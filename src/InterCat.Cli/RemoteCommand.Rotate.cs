using System.Security.Cryptography;
using System.Text.Json;
using InterCat.Domain;
using InterCat.Remote;

namespace InterCat.Cli;

/// <summary>
/// `icat remote rotate`: this controller's key replaced at every agent it enrolled with (`remote-agent-v1` §4.8), with no
/// administrator's token. At each agent the current key asks for a rotation's secret, the next key enrolls with it -
/// taking the current key's grants - and the agent revokes the current key as it admits the next. An agent not reached
/// keeps knowing the current key, which this controller keeps beside the next until every agent knows the next; then the
/// next becomes its key and the old one is gone. Run again, it goes on with the agents not rotated yet.
/// </summary>
internal static partial class RemoteCommand
{
    private static async Task<InterCatExitCode> RotateAsync(CommandLine command, CancellationToken cancellationToken)
    {
        string? root = command.TakeOption("--state");
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

        try
        {
            IReadOnlyList<KnownAgent> agents = state.Agents();
            if (agents.Count == 0)
            {
                ConsoleUi.Failure("This controller has enrolled with no agent, so no agent knows its key: icat remote enroll enrolls "
                    + "with one.");
                return InterCatExitCode.InvalidInvocation;
            }

            using RemoteIdentity current = state.Identity();
            using RemoteIdentity next = state.BeginRotation();
            if (!json)
            {
                ConsoleUi.Heading("Rotating this controller's key");
                ConsoleUi.Field("Key", $"{current.Pin.Abbreviated}, to be replaced by {next.Pin.Abbreviated}");
            }

            var outcomes = new List<(KnownAgent Agent, string? Problem)>();
            foreach (KnownAgent agent in agents)
            {
                string? problem = await RotateAtAsync(state, agent, next, cancellationToken).ConfigureAwait(false);
                outcomes.Add((agent, problem));
                if (!json)
                {
                    ConsoleUi.Field(agent.MachineName, problem is null
                        ? $"knows the new key, at {agent.Endpoint}"
                        : $"still knows the old key, at {agent.Endpoint}: {problem}");
                }
            }

            bool retired = outcomes.All(outcome => outcome.Problem is null);
            if (retired)
            {
                using RemoteIdentity done = state.FinishRotation();
            }

            if (json)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(new
                {
                    Contract = "remote-rotation-v1",
                    Controller = next.Pin.Hex,
                    Previous = current.Pin.Hex,
                    Retired = retired,
                    Agents = outcomes.Select(outcome => new
                    {
                        Endpoint = outcome.Agent.Endpoint.ToString(),
                        Pin = outcome.Agent.Pin.Hex,
                        outcome.Agent.MachineName,
                        Rotated = outcome.Problem is null,
                        Reason = outcome.Problem,
                    }),
                }, JsonContracts.Indented));
            }
            else if (retired)
            {
                ConsoleUi.Success($"Every agent knows this controller by its new key, {next.Pin.Abbreviated}; the old key is retired, and "
                    + "each agent revoked it.");
            }
            else
            {
                ConsoleUi.Warn("The old key is kept until every agent knows the new one: this controller reaches each agent with the key "
                    + "it knows meanwhile. Run icat remote rotate again once those agents can be reached.");
            }

            return retired
                ? InterCatExitCode.Success
                : outcomes.Any(outcome => outcome.Problem is null) ? InterCatExitCode.PartialResultSuccess : InterCatExitCode.PermissionOrCapabilityFailure;
        }
        catch (Exception exception) when (exception is CryptographicException or InvalidDataException)
        {
            ConsoleUi.Failure($"This controller's keys or list of agents in {state.Root} do not read: {exception.Message}");
            return InterCatExitCode.CorruptedInput;
        }
    }

    /// <summary>
    /// Moves <paramref name="agent"/> to <paramref name="next"/>: nothing where it knows it already - asked of the agent itself,
    /// so a rotation stopped after the agent admitted the next key and before this controller kept that is finished too -
    /// and otherwise the current key's rotation, the next key enrolled by its secret. Null where the agent knows the next key;
    /// otherwise why not.
    /// </summary>
    private static async Task<string?> RotateAtAsync(ControllerState state, KnownAgent agent, RemoteIdentity next, CancellationToken cancellationToken)
    {
        try
        {
            if (agent.Controller != next.Pin)
            {
                await using RemoteAgentConnection asking = await RemoteAgentConnection
                    .ConnectAsync(agent.Endpoint, next, agent.Pin, Timeout, cancellationToken).ConfigureAwait(false);
                if (await asking.HelloAsync(cancellationToken).ConfigureAwait(false) is { Enrolled: true, Grants: var grants })
                {
                    state.Remember(agent with { Grants = grants, Controller = next.Pin });
                    return null;
                }

                RemoteRotation rotation;
                using (RemoteIdentity known = state.IdentityFor(agent))
                {
                    await using RemoteAgentConnection old = await RemoteAgentConnection
                        .ConnectAsync(agent.Endpoint, known, agent.Pin, Timeout, cancellationToken).ConfigureAwait(false);
                    rotation = await old.RotateAsync(cancellationToken).ConfigureAwait(false);
                }

                RemoteEnrolled enrolled = await asking
                    .EnrollAsync(EnrollmentToken.ForRotation(agent.Pin, rotation.Secret), cancellationToken).ConfigureAwait(false);
                state.Remember(agent with { Grants = enrolled.Grants, Controller = next.Pin });
            }

            return null;
        }
        catch (RemoteAgentException exception)
        {
            return exception.Message;
        }
    }
}
