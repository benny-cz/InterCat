using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using InterCat.Domain;
using InterCat.Remote;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>
/// `icat remote`: this user's InterCat as a controller of remote agents (ADR-117, `remote-agent-v1`). It enrolls with an
/// agent by the token its administrator gave, keeping the agent's key, and lists the agents it enrolled with, checking
/// each where asked; it records on an agent's host, and fetches what it recorded. It reaches an agent only where told
/// to, and only as the key it enrolled with.
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
            ConsoleUi.Failure("icat remote takes enroll, hosts, capture or fetch.");
            ConsoleUi.Explain(PrintHelp);
            return InterCatExitCode.InvalidInvocation;
        }

        return verb switch
        {
            "enroll" => await EnrollAsync(command, cancellationToken).ConfigureAwait(false),
            "hosts" => await HostsAsync(command, cancellationToken).ConfigureAwait(false),
            "capture" => await CaptureAsync(command, cancellationToken).ConfigureAwait(false),
            "fetch" => await FetchAsync(command, cancellationToken).ConfigureAwait(false),
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

    /// <summary>
    /// A capture on one enrolled agent's host or several (`remote-agent-v1` §4.4, §4.6): each host prepared and its preview
    /// shown, all started as one barrier, each control lease renewed while it records - its connection made again where it
    /// was lost - and every capture still open stopped together on Ctrl+C. Each start's acknowledgment and each agent's
    /// last answer is kept with the round trip that bounds it, never as proof that two hosts' clocks agree; a host that
    /// cannot record stops only its own part, and says why.
    /// </summary>
    private static async Task<InterCatExitCode> CaptureAsync(CommandLine command, CancellationToken cancellationToken)
    {
        string? root = command.TakeOption("--state");
        string profile = command.TakeOption("--profile") ?? "explore";
        int? seconds = command.TakeIntegerOption("--duration");
        int? journal = command.TakeIntegerOption("--max-journal-mib");
        int? lease = command.TakeIntegerOption("--lease");
        bool json = command.TryTakeFlag("--json");
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
                + "several to record on all at once.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (endpoints.Distinct().Count() != endpoints.Count)
        {
            ConsoleUi.Failure("Each agent is named once: a capture across several agents records once on each.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (seconds is < 1 or > 86_400 || journal is < 1 or > 1_048_576 || lease is < 1 or > 300)
        {
            ConsoleUi.Failure("--duration takes whole seconds from 1 to 86,400, --max-journal-mib whole mebibytes, and --lease the "
                + "control lease in seconds, 1 to 300; the agent's own bounds may be narrower.");
            return InterCatExitCode.InvalidInvocation;
        }

        ControllerState? state = Open(root, out InterCatExitCode failed);
        if (state is null)
        {
            return failed;
        }

        TimeSpan holds = TimeSpan.FromSeconds(lease ?? 30);
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

            using RemoteIdentity identity = state.Identity();
            var request = new RemoteCaptureRequest(profile, (uint)(seconds ?? 60), (uint)(journal ?? 1_024));
            bool one = agents.Count == 1;
            if (!json)
            {
                ConsoleUi.Heading(one ? "Remote capture" : $"Remote capture on {agents.Count:N0} hosts");
                if (one)
                {
                    ConsoleUi.Field("Agent", $"{agents[0].MachineName} at {agents[0].Endpoint}");
                }
            }

            IReadOnlyList<RemoteHostCapture> parts = await RemoteCoordinatedCapture.RunAsync(
                    [.. agents.Select(agent => new RemoteCaptureHost(agent.Endpoint, agent.Pin, agent.MachineName))],
                    identity, request, holds, new CaptureSaying(json, one), cancellationToken, Timeout)
                .ConfigureAwait(false);
            return one ? One(parts[0], agents[0], json) : Several(parts, agents, request, json);
        }
        catch (Exception exception) when (exception is CryptographicException or InvalidDataException)
        {
            ConsoleUi.Failure($"This controller's key or list of agents in {state.Root} does not read: {exception.Message}");
            return InterCatExitCode.CorruptedInput;
        }
    }

    /// <summary>A capture on one agent's host, said as icat remote capture always said it (`remote-capture-v1`).</summary>
    private static InterCatExitCode One(RemoteHostCapture part, KnownAgent agent, bool json)
    {
        if (!part.Started || part.State is not { } now)
        {
            ConsoleUi.Failure(part.Failure?.Reason ?? "The agent's capture did not start.");
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                Contract = "remote-capture-v1",
                Endpoint = agent.Endpoint.ToString(),
                Agent = agent.Pin.Hex,
                agent.HostId,
                agent.MachineName,
                CaptureId = part.CaptureId!.Value,
                part.Prepared!.Profile,
                part.Prepared.Seconds,
                part.Prepared.JournalMebibytes,
                Start = Reading(part.Start, start: true),
                End = Reading(part.End, start: false),
                State = now.State.ToString().ToLowerInvariant(),
                now.Finalized,
                now.Records,
                now.Lost,
                Reason = now.Reason.Length == 0 ? null : now.Reason,
                part.Reconnections,
                Failure = Failed(part),
            }, JsonContracts.Indented));
        }
        else
        {
            ConsoleUi.Field("State", Stands(now));
            ConsoleUi.Field("Captured", $"{now.Records:N0} records, {now.Lost:N0} known lost");
            ConsoleUi.Field("Capture", part.CaptureId!.Value.ToString("D"));
        }

        return now.Finalized && part.Failure is null ? InterCatExitCode.Success : InterCatExitCode.PartialResultSuccess;
    }

    /// <summary>
    /// A capture across several agents (`remote-coordinated-capture-v1`): each host's part, the barrier every started host
    /// was started within, and each started host's clock against the first's, within the bound the two samples leave.
    /// </summary>
    private static InterCatExitCode Several(IReadOnlyList<RemoteHostCapture> parts, List<KnownAgent> agents,
        RemoteCaptureRequest request, bool json)
    {
        RemoteHostCapture[] started = [.. parts.Where(part => part.Start is not null)];
        RemoteHostCapture? reference = started.FirstOrDefault();
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                Contract = "remote-coordinated-capture-v1",
                request.Profile,
                request.Seconds,
                request.JournalMebibytes,
                StartedHosts = started.Length,
                Barrier = started.Length == 0 ? null : new
                {
                    SentUtc = started.Min(part => part.Start!.SentUtc).UtcDateTime,
                    ReceivedUtc = started.Max(part => part.Start!.ReceivedUtc).UtcDateTime,
                },
                Reference = reference?.Host.Endpoint.ToString(),
                Hosts = parts.Select((part, at) => new
                {
                    Endpoint = part.Host.Endpoint.ToString(),
                    Agent = part.Host.Agent.Hex,
                    agents[at].HostId,
                    agents[at].MachineName,
                    part.CaptureId,
                    Profile = part.Prepared?.Profile,
                    Start = Reading(part.Start, start: true),
                    End = Reading(part.End, start: false),
                    Clock = reference is null || part == reference ? null : Clocks(part, reference),
                    State = part.State?.State.ToString().ToLowerInvariant(),
                    Finalized = part.State?.Finalized ?? false,
                    Records = part.State?.Records ?? 0,
                    Lost = part.State?.Lost ?? 0,
                    Reason = part.State is { Reason.Length: > 0 } stood ? stood.Reason : null,
                    part.StoppedByController,
                    part.Reconnections,
                    Failure = Failed(part),
                }),
            }, JsonContracts.Indented));
        }
        else
        {
            ConsoleUi.Line();
            foreach (RemoteHostCapture part in parts)
            {
                ConsoleUi.Field(part.Host.Name, part.State is { } now
                    ? $"{Stands(now)}: {now.Records:N0} records, {now.Lost:N0} known lost; capture {part.CaptureId:D}"
                        + (part.Failure is { } lost ? $" - {lost.Reason}" : string.Empty)
                    : $"{Stage(part.Failure)}: {part.Failure?.Reason}");
            }

            if (reference is not null)
            {
                ConsoleUi.Field("Started", $"{started.Length:N0} of {parts.Count:N0} hosts, between "
                    + $"{started.Min(part => part.Start!.SentUtc).UtcDateTime:HH:mm:ss.fff} and "
                    + $"{started.Max(part => part.Start!.ReceivedUtc).UtcDateTime:HH:mm:ss.fff} UTC by this host's clock");
                foreach (RemoteHostCapture part in started.Where(part => part != reference))
                {
                    ConsoleUi.Field("Clock", $"{part.Host.Name} reads {Clocked(part.Start!.Against(reference.Start!))} against "
                        + $"{reference.Host.Name} at the start"
                        + (part.End is { } end && reference.End is { } referenceEnd ? $", {Clocked(end.Against(referenceEnd))} at the end" : string.Empty));
                }

                ConsoleUi.Note("A start across hosts is a barrier: each host started between those instants, and a clock against "
                    + "another is bounded by the round trips that read them - never proof that two hosts' clocks agree.");
            }
        }

        return started.Length == 0
            ? InterCatExitCode.PermissionOrCapabilityFailure
            : started.Length == parts.Count && parts.All(part => part.Failure is null && part.State is { Finalized: true })
                ? InterCatExitCode.Success
                : InterCatExitCode.PartialResultSuccess;
    }

    /// <summary>A clock sample as remote-capture-v1 writes it: the start's reading is its acknowledgment.</summary>
    private static object? Reading(RemoteClockSample? sample, bool start) => sample switch
    {
        null => null,
        _ when start => new
        {
            SentUtc = sample.SentUtc.UtcDateTime,
            AcknowledgedUtc = sample.AnsweredUtc.UtcDateTime,
            ReceivedUtc = sample.ReceivedUtc.UtcDateTime,
            RoundTripMilliseconds = sample.RoundTrip.TotalMilliseconds,
            AgentAheadMilliseconds = sample.AgentAhead.TotalMilliseconds,
            WithinMilliseconds = sample.Within.TotalMilliseconds,
        },
        _ => new
        {
            SentUtc = sample.SentUtc.UtcDateTime,
            AnsweredUtc = sample.AnsweredUtc.UtcDateTime,
            ReceivedUtc = sample.ReceivedUtc.UtcDateTime,
            RoundTripMilliseconds = sample.RoundTrip.TotalMilliseconds,
            AgentAheadMilliseconds = sample.AgentAhead.TotalMilliseconds,
            WithinMilliseconds = sample.Within.TotalMilliseconds,
        },
    };

    /// <summary>A host's clock against the reference host's, at the start and, where both answered, at the end.</summary>
    private static object? Clocks(RemoteHostCapture part, RemoteHostCapture reference)
    {
        if (part.Start is null)
        {
            return null;
        }

        (TimeSpan ahead, TimeSpan within) = part.Start.Against(reference.Start!);
        (TimeSpan Ahead, TimeSpan Within)? atEnd = part.End is { } end && reference.End is { } referenceEnd ? end.Against(referenceEnd) : null;
        return new
        {
            AheadMilliseconds = ahead.TotalMilliseconds,
            WithinMilliseconds = within.TotalMilliseconds,
            EndAheadMilliseconds = atEnd?.Ahead.TotalMilliseconds,
            EndWithinMilliseconds = atEnd?.Within.TotalMilliseconds,
        };
    }

    private static object? Failed(RemoteHostCapture part) => part.Failure is { } failure
        ? new { Stage = failure.Stage.ToString().ToLowerInvariant(), failure.Reason }
        : null;

    private static string Stands(RemoteCaptureState now) => now.State.ToString().ToLowerInvariant()
        + (now.Finalized ? ", finished whole" : ", not finished whole")
        + (now.Reason.Length == 0 ? string.Empty : ": " + now.Reason);

    private static string Stage(RemoteHostFailure? failure) => failure?.Stage switch
    {
        RemoteHostStage.Connect => "not reached",
        RemoteHostStage.Prepare => "not prepared",
        RemoteHostStage.Start => "not started",
        RemoteHostStage.Keep => "lost",
        RemoteHostStage.Stop => "not stopped",
        _ => "not started",
    };

    private static string Clocked((TimeSpan Ahead, TimeSpan Within) clock) =>
        $"{clock.Ahead.TotalMilliseconds.ToString("+#,0;-#,0;0", CultureInfo.InvariantCulture)} ms, within "
        + $"±{Math.Ceiling(clock.Within.TotalMilliseconds).ToString("#,0", CultureInfo.InvariantCulture)} ms";

    /// <summary>
    /// What a capture says as it goes: each host's preview, its start's acknowledgment, its counts and what befell it. The
    /// preview is the command's output, which --json writes instead; the rest is progress, said either way.
    /// </summary>
    private sealed class CaptureSaying(bool json, bool one) : IProgress<RemoteHostEvent>
    {
        private readonly Lock gate = new();

        public void Report(RemoteHostEvent value)
        {
            RemoteHostCapture part = value.Host;
            string name = part.Host.Name;
            lock (gate)
            {
                switch (value.Step)
                {
                    case RemoteHostStep.Prepared when !json:
                        RemotePreparedCapture prepared = part.Prepared!;
                        ConsoleUi.Field(one ? "Records" : name,
                            $"{prepared.Profile}, for up to {prepared.Seconds:N0} s and {prepared.JournalMebibytes:N0} MiB");
                        ConsoleUi.Note(prepared.Collects);
                        break;
                    case RemoteHostStep.Started:
                        RemoteClockSample start = part.Start!;
                        ConsoleUi.Progress((one
                                ? $"Recording on {name} for up to {part.Prepared!.Seconds:N0} s; Ctrl+C stops it. The agent acknowledged the start "
                                : $"Recording on {name}; Ctrl+C stops every host. It acknowledged the start ")
                            + $"{start.AgentAhead.TotalMilliseconds.ToString("+0;-0;0", CultureInfo.InvariantCulture)} ms from this host's clock, "
                            + $"within ±{Math.Ceiling(start.Within.TotalMilliseconds):N0} ms: a barrier for "
                            + (one ? "both hosts" : "every host") + ", not proof that their clocks agree.");
                        break;
                    case RemoteHostStep.Recording:
                        ConsoleUi.Progress(one ? $"{part.State!.Records:N0} records so far." : $"{name}: {part.State!.Records:N0} records so far.");
                        break;
                    case RemoteHostStep.Reconnected:
                        ConsoleUi.Progress($"The connection to {name} was lost and made again; its capture's lease held.");
                        break;
                    case RemoteHostStep.Failed when part.Failure is { } failure && (!one || part.Started):
                        ConsoleUi.Warn($"{name}: {Stage(failure)}: {failure.Reason}");
                        break;
                    default:
                        break;
                }
            }
        }
    }

    /// <summary>
    /// The evidence of a capture this controller started on an agent's host, fetched into a folder of its own
    /// (`remote-agent-v1` §4.5): each file checked against the SHA-256 its listing gives, and the folder opened as the
    /// capture's evidence, hashed whole, before it takes its name. Run again, it goes on where it stopped. icat follow then
    /// derives a session from it, as from any capture's evidence.
    /// </summary>
    private static async Task<InterCatExitCode> FetchAsync(CommandLine command, CancellationToken cancellationToken)
    {
        string? root = command.TakeOption("--state");
        bool json = command.TryTakeFlag("--json");
        string? address = command.TakePositional();
        string? captureText = command.TakePositional();
        string? folderText = command.TakePositional();
        if (Refused(command) is { } refused)
        {
            return refused;
        }

        if (address is null || !IPEndPoint.TryParse(address, out IPEndPoint? endpoint) || endpoint.Port == 0
            || !Guid.TryParse(captureText, out Guid captureId) || folderText is null)
        {
            ConsoleUi.Failure("Give the address and port of an agent this controller enrolled with, the capture icat remote capture "
                + "started there, and a folder for its evidence: icat remote fetch 192.0.2.10:7457 <capture> <folder>.");
            return InterCatExitCode.InvalidInvocation;
        }

        string folder = Path.GetFullPath(folderText);
        if (OutputFiles.FolderRefusal(folder, "icat remote fetch keeps a capture's evidence in a folder") is { } blocked)
        {
            ConsoleUi.Failure(blocked);
            return InterCatExitCode.InvalidInvocation;
        }

        ControllerState? state = Open(root, out InterCatExitCode failed);
        if (state is null)
        {
            return failed;
        }

        try
        {
            KnownAgent? agent = state.Agents().FirstOrDefault(known => known.Endpoint.Equals(endpoint));
            if (agent is null)
            {
                ConsoleUi.Failure($"This controller has enrolled with no agent at {endpoint}: icat remote enroll it first; icat remote "
                    + "hosts lists the agents it enrolled with.");
                return InterCatExitCode.InvalidInvocation;
            }

            using RemoteIdentity identity = state.Identity();
            await using RemoteAgentConnection connection = await RemoteAgentConnection
                .ConnectAsync(endpoint, identity, agent.Pin, Timeout, cancellationToken).ConfigureAwait(false);
            if (!json)
            {
                ConsoleUi.Heading("Remote fetch");
                ConsoleUi.Field("Agent", $"{agent.MachineName} at {endpoint}");
                ConsoleUi.Field("Capture", captureId.ToString("D"));
            }

            var clock = Stopwatch.StartNew();
            TimeSpan said = TimeSpan.Zero;
            var progress = new SaidProgress(step =>
            {
                if (json || clock.Elapsed - said < TimeSpan.FromSeconds(2) || step.FilesDone == step.Files)
                {
                    return;
                }

                said = clock.Elapsed;
                ConsoleUi.Progress($"{step.FilesDone:N0} of {CountText.Of(step.Files, "file")} whole, "
                    + $"{step.BytesDone / 1_048_576.0:N1} of {step.Bytes / 1_048_576.0:N1} MiB.");
            });
            RemoteFetchResult result = await RemoteEvidenceFetch.FetchAsync(connection, captureId, folder,
                unfinished => EvidenceProblem(unfinished, captureId), progress, cancellationToken).ConfigureAwait(false);
            if (json)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(new
                {
                    Contract = "remote-fetch-v1",
                    Endpoint = endpoint.ToString(),
                    Agent = agent.Pin.Hex,
                    agent.HostId,
                    agent.MachineName,
                    CaptureId = captureId,
                    EvidencePath = folder,
                    result.Generation,
                    result.Files,
                    result.Bytes,
                    result.FetchedBytes,
                    result.KeptBytes,
                    result.AlreadyFetched,
                    ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds,
                }, JsonContracts.Indented));
            }
            else
            {
                ConsoleUi.Field("Evidence", folder);
                ConsoleUi.Field("Holds", $"{CountText.Of(result.Files, "file")}, {result.Bytes / 1_048_576.0:N1} MiB, generation {result.Generation:N0}");
                ConsoleUi.Field("Fetched", result.AlreadyFetched
                    ? "nothing: the folder held this evidence whole already"
                    : $"{result.FetchedBytes / 1_048_576.0:N1} MiB in {clock.Elapsed.TotalSeconds:N1} s"
                        + (result.KeptBytes > 0 ? $", {result.KeptBytes / 1_048_576.0:N1} MiB held from before" : string.Empty)
                        + "; every file matches its SHA-256, and the folder opens as the capture's evidence");
                ConsoleUi.Note($"Derive a session from it: icat follow \"{folder}\" <session-dir> --once");
            }

            return InterCatExitCode.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ConsoleUi.Warn($"Stopped. What was fetched is kept in {RemoteEvidenceFetch.UnfinishedFor(folder)}; icat remote fetch goes "
                + "on from there when run again.");
            return InterCatExitCode.Cancelled;
        }
        catch (RemoteFetchRefusedException exception)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }
        catch (RemoteAgentException exception)
        {
            ConsoleUi.Failure(exception.Message + (Directory.Exists(RemoteEvidenceFetch.UnfinishedFor(folder))
                ? " What was fetched is kept; icat remote fetch goes on from there when run again."
                : string.Empty));
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }
        catch (Exception exception) when (exception is CryptographicException or InvalidDataException)
        {
            ConsoleUi.Failure($"This controller's key or list of agents in {state.Root} does not read: {exception.Message}");
            return InterCatExitCode.CorruptedInput;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ConsoleUi.Failure($"The evidence cannot be kept at {folder}: {exception.Message}");
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }
    }

    /// <summary>
    /// Why a fetched folder is not the capture's evidence, or null where it is: it opens as the capture's session, every
    /// file of the generation its pointer names hashed against the manifest, with no fallback to an earlier one.
    /// </summary>
    private static string? EvidenceProblem(string folder, Guid captureId)
    {
        try
        {
            SessionStore store = SessionStore.OpenExisting(LocalOwnedDirectory.Open(folder));
            return store.Current is not { } current
                ? "it opens as no session."
                : store.Recovery.RolledBackToLastKnownGood
                    ? "its generation does not verify: " + store.Recovery.RollbackReason
                    : current.SessionId != captureId
                        ? $"it is the evidence of another capture, {current.SessionId:D}."
                        : null;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            return exception.Message;
        }
    }

    /// <summary>Progress said as it is reported, on the fetch's own thread, so lines keep their order.</summary>
    private sealed class SaidProgress(Action<RemoteFetchProgress> say) : IProgress<RemoteFetchProgress>
    {
        public void Report(RemoteFetchProgress value) => say(value);
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
        ConsoleUi.Failure($"icat remote has no '{verb}': it takes enroll, hosts, capture or fetch.");
        ConsoleUi.Explain(PrintHelp);
        return InterCatExitCode.InvalidInvocation;
    }

    private static void PrintHelp()
    {
        ConsoleUi.Line("icat remote enroll <address:port> <token> [--state <folder>] [--json]");
        ConsoleUi.Line("icat remote hosts [--check] [--state <folder>] [--json]");
        ConsoleUi.Line("icat remote capture <address:port>... [--profile <profile>] [--duration <seconds>] [--max-journal-mib <mib>]");
        ConsoleUi.Line("                    [--lease <seconds>] [--state <folder>] [--json]");
        ConsoleUi.Line("icat remote fetch <address:port> <capture> <folder> [--state <folder>] [--json]");
        ConsoleUi.Line();
        ConsoleUi.Line("  This InterCat as a controller of remote agents. enroll enrolls with the agent at the address given by");
        ConsoleUi.Line("  the token its administrator printed with icat agent token, going on only where the agent's key is the");
        ConsoleUi.Line("  one the token names. hosts lists the agents enrolled with; --check asks each what it allows now. capture");
        ConsoleUi.Line("  records on an enrolled agent's host - explore unless --profile names another metadata profile, 60 seconds");
        ConsoleUi.Line("  and 1,024 MiB unless said otherwise - renewing its control lease, 30 seconds unless --lease says, until it");
        ConsoleUi.Line("  ends or Ctrl+C stops it; the agent stops it should the lease lapse. Given several agents, capture prepares");
        ConsoleUi.Line("  each, starts every one prepared at once - a barrier, each acknowledgment kept with the round trip that");
        ConsoleUi.Line("  bounds it - and stops them together; a host that cannot record stops only its own part. fetch brings a capture this");
        ConsoleUi.Line("  controller started, once it closed, into a folder of its own, every file checked against its SHA-256;");
        ConsoleUi.Line("  run again, it goes on where it stopped. icat follow <folder> <session-dir> --once derives its session.");
        ConsoleUi.Line("  The controller keeps its key, protected for its user, and its agents in a folder only its user may use:");
        ConsoleUi.Line("  remote in InterCat's folder under the user's local application data, unless --state names another.");
    }
}
