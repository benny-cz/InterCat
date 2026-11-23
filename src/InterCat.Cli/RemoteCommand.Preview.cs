using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using InterCat.Domain;
using InterCat.Remote;

namespace InterCat.Cli;

/// <summary>
/// `icat remote preview`: what a capture request would record on each enrolled agent's host, before anything is prepared
/// (`remote-agent-v1` §4.10) - each host's preview compiled there from its own schemas, starting nothing, and whether its
/// agent would prepare the request as asked, and why not. A content request is refused for a key not granted contents and
/// by an agent whose policy keeps none, and is not recorded remotely at this version; no request is ever recorded as
/// something it did not ask for.
/// </summary>
internal static partial class RemoteCommand
{
    private static async Task<InterCatExitCode> PreviewAsync(CommandLine command, CancellationToken cancellationToken)
    {
        string? root = command.TakeOption("--state");
        string profile = command.TakeOption("--profile") ?? "explore";
        int? seconds = command.TakeIntegerOption("--duration");
        int? journal = command.TakeIntegerOption("--max-journal-mib");
        bool json = command.TryTakeFlag("--json");
        ProfileRequestOptions? options = ProfileRequestOptions.Take(command);
        if (options is null)
        {
            return InterCatExitCode.InvalidInvocation;
        }

        var addresses = new List<string>();
        while (command.TakePositional() is { } address)
        {
            addresses.Add(address);
        }

        if (Refused(command) is { } refused)
        {
            return refused;
        }

        var endpoints = new List<IPEndPoint>();
        foreach (string address in addresses)
        {
            if (!IPEndPoint.TryParse(address, out IPEndPoint? endpoint) || endpoint.Port == 0)
            {
                ConsoleUi.Failure($"'{address}' is not an address and port, such as 192.0.2.10:7457: an address, not a name - "
                    + "InterCat looks up no name.");
                return InterCatExitCode.InvalidInvocation;
            }

            endpoints.Add(endpoint);
        }

        if (endpoints.Count == 0)
        {
            ConsoleUi.Failure("Give the address and port of an agent this controller enrolled with, such as 192.0.2.10:7457, or of "
                + "several to preview the capture on each.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (endpoints.Distinct().Count() != endpoints.Count)
        {
            ConsoleUi.Failure("Each agent is named once: a preview asks each agent once.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (seconds is < 1 or > 86_400 || journal is < 1 or > 1_048_576)
        {
            ConsoleUi.Failure("--duration takes whole seconds from 1 to 86,400, and --max-journal-mib whole mebibytes; the agent's "
                + "own bounds may be narrower.");
            return InterCatExitCode.InvalidInvocation;
        }

        ControllerState? state = Open(root, out InterCatExitCode failed);
        if (state is null)
        {
            return failed;
        }

        var request = new RemotePreviewRequest(profile, (uint)(seconds ?? 60), (uint)(journal ?? 1_024))
        {
            Mechanism = options.Mechanism is { } mechanism ? ProfileRequestOptions.NameOf(mechanism) : null,
            ProcessIds = options.ProcessIds,
            AllowBroaderCapture = options.AllowBroaderCapture,
            ContentSource = options.ContentSource,
            Channels = options.Channels,
            MaximumRecordBytes = options.MaximumRecordBytes is { } recordBytes ? (uint)recordBytes : null,
            MaximumSessionBytes = options.MaximumSessionBytes,
            Retention = options.Retention is { } retention ? ProfileRequestOptions.NameOf(retention) : null,
            Inspection = options.Inspection is { } inspection ? ProfileRequestOptions.NameOf(inspection) : null,
        };
        try
        {
            IReadOnlyList<KnownAgent> known = state.Agents();
            var agents = new List<KnownAgent>();
            foreach (IPEndPoint endpoint in endpoints)
            {
                if (known.FirstOrDefault(candidate => candidate.Endpoint.Equals(endpoint)) is not { } agent)
                {
                    ConsoleUi.Failure($"This controller has enrolled with no agent at {endpoint}: icat remote enroll it first; icat "
                        + "remote hosts lists the agents it enrolled with.");
                    return InterCatExitCode.InvalidInvocation;
                }

                agents.Add(agent);
            }

            // Each agent is asked in turn: a preview prepares nothing, and following an agent to a key it rotated to
            // writes this controller's list one agent at a time.
            var previews = new List<HostPreview>();
            foreach (KnownAgent agent in agents)
            {
                previews.Add(await PreviewAtAsync(state, agent, request, cancellationToken).ConfigureAwait(false));
            }

            int served = previews.Count(host => host.Answer is { Served: true });
            if (json)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(new
                {
                    Contract = "remote-preview-v1",
                    request.Profile,
                    request.Seconds,
                    request.JournalMebibytes,
                    ServedHosts = served,
                    Hosts = previews.Select(host => new
                    {
                        Endpoint = host.Agent.Endpoint.ToString(),
                        Agent = host.Key.Hex,
                        host.Agent.HostId,
                        host.Agent.MachineName,
                        Served = host.Answer?.Served ?? false,
                        host.Answer?.Reason,
                        Preview = host.Preview,
                        host.Failure,
                    }),
                }, JsonContracts.Indented));
            }
            else
            {
                ConsoleUi.Heading(agents.Count == 1 ? "Capture preview" : $"Capture preview on {agents.Count:N0} hosts");
                foreach (HostPreview host in previews)
                {
                    ConsoleUi.Field(host.Agent.MachineName, host switch
                    {
                        { Answer.Served: true } => "would record it: " + host.Statement,
                        { Answer: { } answer } => "would not record it: " + answer.Reason,
                        _ => "not asked: " + host.Failure,
                    });
                }

                ConsoleUi.Note("A preview prepares and starts nothing; icat remote capture records what each agent would prepare.");
            }

            return served == previews.Count
                ? InterCatExitCode.Success
                : served > 0 ? InterCatExitCode.PartialResultSuccess : InterCatExitCode.PermissionOrCapabilityFailure;
        }
        catch (Exception exception) when (exception is CryptographicException or InvalidDataException)
        {
            ConsoleUi.Failure($"This controller's key or list of agents in {state.Root} does not read: {exception.Message}");
            return InterCatExitCode.CorruptedInput;
        }
    }

    /// <summary>
    /// Asks <paramref name="agent"/> to preview <paramref name="request"/> on its host, reached by the key this controller
    /// knows it by or the next key it told of; what it said, or why it was not asked.
    /// </summary>
    private static async Task<HostPreview> PreviewAtAsync(ControllerState state, KnownAgent agent, RemotePreviewRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            using RemoteIdentity identity = state.IdentityFor(agent);
            await using RemoteAgentConnection connection = await ReachAsync(state, agent, identity, cancellationToken).ConfigureAwait(false);
            RemoteCapturePreview answer = await connection.PreviewCaptureAsync(request, cancellationToken).ConfigureAwait(false);
            JsonElement? preview = null;
            if (answer.Preview is { } document)
            {
                try
                {
                    using JsonDocument parsed = JsonDocument.Parse(document);
                    preview = parsed.RootElement.Clone();
                }
                catch (JsonException)
                {
                    return new(agent, connection.Agent, null, null, "Its agent's preview does not read as a preview.");
                }
            }

            return new(agent, connection.Agent, answer, preview, null);
        }
        catch (RemoteAgentException exception)
        {
            return new(agent, agent.Pin, null, null, exception.Message);
        }
    }

    /// <summary>One host's preview: the agent asked, by the key that answered, what it said and its preview - or why it was not asked.</summary>
    private sealed record HostPreview(KnownAgent Agent, RemotePin Key, RemoteCapturePreview? Answer, JsonElement? Preview, string? Failure)
    {
        /// <summary>What the host's preview says it collects; its agent's own words where the preview says none.</summary>
        public string Statement => Preview is { } preview && preview.TryGetProperty("collectionStatement", out JsonElement statement)
            && statement.ValueKind == JsonValueKind.String
                ? statement.GetString()!
                : "its preview says no statement.";
    }
}
