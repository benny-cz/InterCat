using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using InterCat.Domain;
using InterCat.Remote;

namespace InterCat.Cli;

/// <summary>
/// `icat remote follow`: the evidence of a capture this controller started, followed while it records (`remote-agent-v1`
/// §4.11) - its newest generation listed every few seconds, only what it adds fetched and checked, its pointer written last,
/// so the folder holds one whole generation of the capture's evidence at every round, which `icat follow` derives as it
/// goes - until the capture closes and its last generation is held and checked.
/// </summary>
internal static partial class RemoteCommand
{
    private static async Task<InterCatExitCode> FollowAsync(CommandLine command, CancellationToken cancellationToken)
    {
        string? root = command.TakeOption("--state");
        int? interval = command.TakeIntegerOption("--interval");
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
                + "started there, and a folder for its evidence: icat remote follow 192.0.2.10:7457 <capture> <folder>.");
            return InterCatExitCode.InvalidInvocation;
        }

        if (interval is < 1 or > 60)
        {
            ConsoleUi.Failure("--interval takes whole seconds from 1 to 60: how often the capture's evidence is listed while it records.");
            return InterCatExitCode.InvalidInvocation;
        }

        string folder = Path.GetFullPath(folderText);
        if (OutputFiles.FolderRefusal(folder, "icat remote follow keeps a capture's evidence in a folder") is { } blocked)
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

            using RemoteIdentity identity = state.IdentityFor(agent);
            await using RemoteAgentConnection connection = await ReachAsync(state, agent, identity, cancellationToken).ConfigureAwait(false);
            if (!json)
            {
                ConsoleUi.Heading("Remote follow");
                ConsoleUi.Field("Agent", $"{agent.MachineName} at {endpoint}");
                ConsoleUi.Field("Capture", captureId.ToString("D"));
                ConsoleUi.Field("Evidence", folder);
            }

            var clock = Stopwatch.StartNew();
            var rounds = new SaidRounds(round =>
            {
                if (!json)
                {
                    ConsoleUi.Progress($"Generation {round.Generation:N0}: {CountText.Of(round.Files, "file")}, {round.Bytes / 1_048_576.0:N1} MiB, "
                        + $"{round.FetchedBytes / 1_048_576.0:N1} MiB fetched" + (round.StillRecording ? "; still recording." : "; the capture closed."));
                }
            });
            RemoteFollowResult result = await RemoteEvidenceFollow.FollowAsync(connection, captureId, folder,
                followed => EvidenceProblem(followed, captureId), rounds, TimeSpan.FromSeconds(interval ?? 2), cancellationToken).ConfigureAwait(false);
            if (json)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(new
                {
                    Contract = "remote-follow-v1",
                    Endpoint = endpoint.ToString(),
                    Agent = connection.Agent.Hex,
                    agent.HostId,
                    agent.MachineName,
                    CaptureId = captureId,
                    EvidencePath = folder,
                    result.Rounds,
                    result.Generation,
                    result.Files,
                    result.Bytes,
                    result.FetchedBytes,
                    result.AlreadyHeld,
                    ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds,
                }, JsonContracts.Indented));
            }
            else
            {
                ConsoleUi.Field("Holds", $"{CountText.Of(result.Files, "file")}, {result.Bytes / 1_048_576.0:N1} MiB, generation {result.Generation:N0}");
                ConsoleUi.Field("Followed", result.AlreadyHeld
                    ? "nothing: the folder held the closed capture's evidence whole already"
                    : $"{CountText.Of(result.Rounds, "generation")} in {clock.Elapsed.TotalSeconds:N1} s, {result.FetchedBytes / 1_048_576.0:N1} MiB "
                        + "fetched; every file matches its SHA-256, and the folder opens as the capture's evidence");
                ConsoleUi.Note($"The capture closed. Derive its session: icat follow \"{folder}\" <session-dir> --once");
            }

            return InterCatExitCode.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ConsoleUi.Warn($"Stopped. {folder} holds the capture's evidence as of the last generation followed; icat remote follow goes on "
                + "from there when run again.");
            return InterCatExitCode.Cancelled;
        }
        catch (RemoteFetchRefusedException exception)
        {
            ConsoleUi.Failure(exception.Message);
            return InterCatExitCode.PermissionOrCapabilityFailure;
        }
        catch (RemoteAgentException exception)
        {
            ConsoleUi.Failure(exception.Message + " What was followed is kept; icat remote follow goes on from there when run again.");
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

    /// <summary>Each round of a follow, said as it ends.</summary>
    private sealed class SaidRounds(Action<RemoteFollowRound> say) : IProgress<RemoteFollowRound>
    {
        public void Report(RemoteFollowRound value) => say(value);
    }
}
