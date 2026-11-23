using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using InterCat.Application;
using InterCat.Capture.Journal;
using InterCat.Domain;
using InterCat.Remote;
using InterCat.Storage;

namespace InterCat.Cli;

/// <summary>
/// `icat remote collect`: a capture across several agents, as `icat remote capture --json` wrote it
/// (`remote-coordinated-capture-v1`), gathered into an investigation in a folder of its own (`remote-agent-v1` §4.7,
/// `workspace-v26` §9). Each host's evidence is fetched from its agent into the folder, its session derived beside it and
/// added to the investigation, and what became of every host's part is recorded in the investigation's file - a host that
/// recorded nothing, or whose evidence is not collected yet, stays in view. Run again, it goes on where it stopped.
/// </summary>
internal static partial class RemoteCommand
{
    private const string CoordinatedContract = "remote-coordinated-capture-v1";

    private static async Task<InterCatExitCode> CollectAsync(CommandLine command, CancellationToken cancellationToken)
    {
        string? root = command.TakeOption("--state");
        string? workspaceOption = command.TakeOption("--workspace");
        bool json = command.TryTakeFlag("--json");
        string? recordPath = command.TakePositional();
        string? folderText = command.TakePositional();
        if (Refused(command) is { } refused)
        {
            return refused;
        }

        if (recordPath is null || folderText is null)
        {
            ConsoleUi.Failure("Give the file icat remote capture --json wrote of a capture across several agents, and a folder for "
                + "the investigation: icat remote collect capture.json <folder>.");
            return InterCatExitCode.InvalidInvocation;
        }

        CoordinatedRecord record;
        try
        {
            record = CoordinatedRecord.Read(Path.GetFullPath(recordPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.InvalidInvocation;
        }

        if (record.StartedUtc is not { } started)
        {
            ConsoleUi.Failure("No host of that capture recorded anything, so there is nothing to collect.");
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }

        string folder = Path.GetFullPath(folderText);
        string workspacePath = workspaceOption is null
            ? Path.Combine(folder, "investigation" + InvestigationWorkspace.Extension)
            : InvestigationWorkspace.PathFor(workspaceOption);
        if (OutputFiles.FolderRefusal(folder, "icat remote collect gathers an investigation in a folder") is { } blocked)
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
            if (!File.Exists(workspacePath))
            {
                _ = InvestigationWorkspace.Create(workspacePath, DateTimeOffset.UtcNow);
            }

            IReadOnlyList<KnownAgent> known = state.Agents();
            using RemoteIdentity identity = state.Identity();
            if (!json)
            {
                ConsoleUi.Heading("Collecting a capture across hosts");
                ConsoleUi.Field("Capture", $"{record.CoordinationId:D}: {record.Profile} on {record.Hosts.Count:N0} hosts");
                ConsoleUi.Field("Investigation", workspacePath);
            }

            var outcomes = new List<CollectedHost>();
            foreach (RecordedHost host in record.Hosts)
            {
                CollectedHost outcome = await CollectHostAsync(host, folder, workspacePath, known, identity, cancellationToken)
                    .ConfigureAwait(false);
                outcomes.Add(outcome);
                if (!json)
                {
                    ConsoleUi.Field(host.MachineName, Said(outcome.Part));
                }
            }

            WorkspaceCoordination? revision = InvestigationWorkspace.RecordCoordination(workspacePath, new WorkspaceCoordination
            {
                Revision = 0,
                CoordinationId = record.CoordinationId,
                StartedUtc = started,
                Profile = record.Profile,
                Hosts = [.. outcomes.Select(outcome => outcome.Part)],
                RecordedUtc = DateTimeOffset.UtcNow,
            }, DateTimeOffset.UtcNow);
            int collected = outcomes.Count(outcome => outcome.Part.Part == WorkspaceHostPart.Collected);
            if (json)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(new
                {
                    Contract = "remote-collection-v1",
                    Workspace = workspacePath,
                    record.CoordinationId,
                    Revision = revision?.Revision,
                    CollectedHosts = collected,
                    Hosts = outcomes.Select(outcome => new
                    {
                        outcome.Part.MachineName,
                        outcome.Part.HostId,
                        outcome.Part.CaptureId,
                        outcome.Part.SessionId,
                        Part = outcome.Part.Part.ToString(),
                        outcome.Part.Stage,
                        outcome.Part.Reason,
                        outcome.EvidencePath,
                        outcome.SessionPath,
                        outcome.FetchedBytes,
                    }),
                }, JsonContracts.Indented));
            }
            else
            {
                ConsoleUi.Field("Collected", $"{collected:N0} of {outcomes.Count:N0} hosts"
                    + (revision is null ? "; the investigation held this already" : $"; recorded as revision {revision.Revision:N0}"));
                ConsoleUi.Note($"icat workspace show \"{workspacePath}\" says every host's part"
                    + (collected < outcomes.Count(outcome => outcome.Part.CaptureId is not null)
                        ? "; icat remote collect again goes on with the hosts not collected yet."
                        : "."));
            }

            return collected == 0
                ? InterCatExitCode.PermissionOrCapabilityFailure
                : collected == outcomes.Count ? InterCatExitCode.Success : InterCatExitCode.PartialResultSuccess;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ConsoleUi.Warn("Stopped. What was fetched is kept; icat remote collect goes on from there when run again.");
            return InterCatExitCode.Cancelled;
        }
        catch (Exception exception) when (exception is CryptographicException or InvalidDataException or InvalidOperationException)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.CorruptedInput;
        }
    }

    /// <summary>
    /// One host's part collected: its evidence fetched into <c>&lt;folder&gt;/&lt;host&gt;-&lt;capture&gt;/evidence</c>, its session
    /// derived into <c>session</c> beside it and added to the investigation - or why not, in words that name no path or
    /// address, since an investigation's file may be shared.
    /// </summary>
    private static async Task<CollectedHost> CollectHostAsync(RecordedHost host, string folder, string workspacePath,
        IReadOnlyList<KnownAgent> known, RemoteIdentity identity, CancellationToken cancellationToken)
    {
        WorkspaceCoordinatedHost part = new()
        {
            MachineName = host.MachineName,
            HostId = host.HostId,
            Agent = host.Agent.Hex,
            CaptureId = host.CaptureId,
            Part = WorkspaceHostPart.NotRecorded,
            Stage = host.Stage,
            Reason = host.Reason,
            Start = host.Start,
            End = host.End,
        };
        if (host.CaptureId is not { } capture)
        {
            return new CollectedHost(part with { Reason = host.Reason ?? "It recorded nothing." }, null, null, 0);
        }

        // A capture's session is named by its capture (remote-agent-v1 §4.5): one collected already is not fetched again.
        if (InvestigationWorkspace.Read(workspacePath).Members.Any(member => member.SessionId == capture))
        {
            return new CollectedHost(part with { Part = WorkspaceHostPart.Collected, SessionId = capture }, null, null, 0);
        }

        string label = Label(host.MachineName, capture);
        string evidence = Path.Combine(folder, label, "evidence");
        string session = Path.Combine(folder, label, "session");
        WorkspaceCoordinatedHost notCollected = part with { Part = WorkspaceHostPart.NotCollected, Stage = "collect" };
        if (known.FirstOrDefault(agent => agent.Endpoint.Equals(host.Endpoint) && agent.Pin == host.Agent) is not { } agent)
        {
            return new CollectedHost(notCollected with { Reason = "This controller is not enrolled with the agent the capture names, "
                + "or knows it by another key." }, null, null, 0);
        }

        RemoteFetchResult fetched;
        try
        {
            await using RemoteAgentConnection connection = await RemoteAgentConnection
                .ConnectAsync(agent.Endpoint, identity, agent.Pin, Timeout, cancellationToken).ConfigureAwait(false);
            fetched = await RemoteEvidenceFetch.FetchAsync(connection, capture, evidence,
                unfinished => EvidenceProblem(unfinished, capture), progress: null, cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteAgentException exception) when (exception.Code is null)
        {
            return new CollectedHost(notCollected with { Reason = exception.Presented.IsEmpty
                ? "Its agent could not be reached."
                : "Another key answers where its agent was." }, null, null, 0);
        }
        catch (RemoteAgentException exception)
        {
            // An agent's refusal says nothing of where on its host or its network anything is (remote-agent-v1 §4.5).
            return new CollectedHost(notCollected with { Reason = exception.Message }, null, null, 0);
        }
        catch (RemoteFetchRefusedException)
        {
            return new CollectedHost(notCollected with { Reason = "Its evidence was not fetched whole as its listing says; icat remote "
                + "fetch of the capture says why." }, null, null, 0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new CollectedHost(notCollected with { Reason = "The investigation's folder could not hold its evidence." }, null, null, 0);
        }

        try
        {
            _ = Derive(evidence, session, cancellationToken);
            _ = InvestigationWorkspace.Add(workspacePath, session, DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            return new CollectedHost(notCollected with { Reason = "Its session could not be derived from its evidence, or added to "
                + "the investigation." }, evidence, null, fetched.FetchedBytes);
        }

        return new CollectedHost(part with { Part = WorkspaceHostPart.Collected, SessionId = capture }, evidence, session, fetched.FetchedBytes);
    }

    /// <summary>
    /// Derives the session of a fetched evidence folder, as `icat follow &lt;evidence&gt; &lt;session&gt; --once` does: every
    /// committed chunk copied and checked, its rows derived, and a finished session's checkpoint published.
    /// </summary>
    private static FollowStep Derive(string evidencePath, string sessionPath, CancellationToken cancellationToken)
    {
        SessionStore evidence = SessionStore.OpenExisting(LocalOwnedDirectory.Open(evidencePath));
        SessionManifestV1 source = evidence.Current ?? throw new InvalidDataException("The fetched evidence published nothing.");
        _ = Directory.CreateDirectory(sessionPath);
        SessionStore derived = SessionStore.Open(LocalOwnedDirectory.Open(sessionPath), source.SessionId, source.SourceIdentity);
        FollowStep step = LiveSessionFollower.Open(evidence, derived, indexer: LiveCheckpoints.Instance, cancellationToken: cancellationToken)
            .CatchUp(cancellationToken: cancellationToken);
        if (step.Finished)
        {
            _ = CheckpointStep.Publish(derived, cancellationToken);
        }

        return step;
    }

    /// <summary>A host's folder in the investigation's: its name in letters, digits, '-' and '_', and its capture's first 8 digits.</summary>
    private static string Label(string machineName, Guid capture)
    {
        string name = new([.. machineName.Take(40).Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '-')]);
        return (name.Trim('-').Length == 0 ? "host" : name.Trim('-')) + "-" + capture.ToString("N")[..8];
    }

    private static string Said(WorkspaceCoordinatedHost part) => part.Part switch
    {
        WorkspaceHostPart.Collected => $"collected as session {part.SessionId!.Value.ToString("N")[..8]}"
            + (part.Reason is { } kept ? $" - {part.Stage}: {kept}" : string.Empty),
        WorkspaceHostPart.NotCollected => $"recorded, not collected yet - {part.Reason}",
        _ => $"recorded nothing - {part.Stage}: {part.Reason}",
    };

    /// <summary>One host's part collected, and where its evidence and session were kept, with the bytes fetched for it.</summary>
    private sealed record CollectedHost(WorkspaceCoordinatedHost Part, string? EvidencePath, string? SessionPath, long FetchedBytes);

    /// <summary>A host as a capture across agents named it: where its agent was, its key, and its part.</summary>
    private sealed record RecordedHost(
        IPEndPoint Endpoint,
        RemotePin Agent,
        Guid HostId,
        string MachineName,
        Guid? CaptureId,
        string? Stage,
        string? Reason,
        WorkspaceClockReading? Start,
        WorkspaceClockReading? End);

    /// <summary>A capture across agents, read from what icat remote capture --json wrote of it (`remote-coordinated-capture-v1`).</summary>
    private sealed record CoordinatedRecord(Guid CoordinationId, string Profile, DateTimeOffset? StartedUtc, IReadOnlyList<RecordedHost> Hosts)
    {
        public static CoordinatedRecord Read(string path)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            JsonElement root = document.RootElement;
            try
            {
                if (root.ValueKind != JsonValueKind.Object || Text(root, "contract") != CoordinatedContract)
                {
                    throw new InvalidDataException("it is not a capture across several agents");
                }

                Guid coordination = Guid.Parse(Text(root, "coordinationId")
                    ?? throw new InvalidDataException("it names no identity of the capture, as an earlier InterCat wrote it; record the "
                        + "capture again to collect it"));
                DateTimeOffset? started = root.TryGetProperty("barrier", out JsonElement barrier) && barrier.ValueKind == JsonValueKind.Object
                    ? barrier.GetProperty("sentUtc").GetDateTimeOffset()
                    : null;
                RecordedHost[] hosts = [.. root.GetProperty("hosts").EnumerateArray().Select(HostOf)];
                return hosts.Length == 0
                    ? throw new InvalidDataException("it names no host")
                    : new CoordinatedRecord(coordination, Text(root, "profile") ?? throw new InvalidDataException("it names no profile"), started, hosts);
            }
            catch (Exception exception) when (exception is KeyNotFoundException or FormatException or InvalidOperationException)
            {
                throw new InvalidDataException($"{path} is not what icat remote capture --json wrote of a capture across several agents: "
                    + exception.Message, exception);
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException($"{path} is not what icat remote capture --json wrote of a capture across several agents: "
                    + exception.Message, exception);
            }
        }

        private static RecordedHost HostOf(JsonElement host)
        {
            JsonElement? failure = host.TryGetProperty("failure", out JsonElement failed) && failed.ValueKind == JsonValueKind.Object ? failed : null;
            return new RecordedHost(
                IPEndPoint.Parse(Text(host, "endpoint") ?? throw new InvalidDataException("a host names no address")),
                RemotePin.TryParse(Text(host, "agent"), out RemotePin pin) ? pin : throw new InvalidDataException("a host names no agent's pin"),
                host.GetProperty("hostId").GetGuid(),
                Text(host, "machineName") ?? throw new InvalidDataException("a host has no name"),
                host.TryGetProperty("captureId", out JsonElement capture) && capture.ValueKind == JsonValueKind.String ? capture.GetGuid() : null,
                failure is { } stage ? Text(stage, "stage") : null,
                failure is { } reason ? Text(reason, "reason") : null,
                Reading(host, "start", "acknowledgedUtc"),
                Reading(host, "end", "answeredUtc"));
        }

        private static WorkspaceClockReading? Reading(JsonElement host, string name, string answered) =>
            host.TryGetProperty(name, out JsonElement reading) && reading.ValueKind == JsonValueKind.Object
                ? new WorkspaceClockReading(reading.GetProperty("sentUtc").GetDateTimeOffset(), reading.GetProperty(answered).GetDateTimeOffset(),
                    reading.GetProperty("receivedUtc").GetDateTimeOffset())
                : null;

        private static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
