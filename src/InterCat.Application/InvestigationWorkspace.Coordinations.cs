namespace InterCat.Application;

/// <summary>What became of one host's part in a capture recorded across several hosts (`contracts/workspace-v26.md` §9).</summary>
public enum WorkspaceHostPart
{
    /// <summary>The host recorded, and its session is a member of the investigation.</summary>
    Collected = 1,

    /// <summary>The host recorded, and its evidence is not in the investigation yet: collecting it again goes on.</summary>
    NotCollected = 2,

    /// <summary>The host recorded nothing: it was not reached, or refused or failed to prepare or start.</summary>
    NotRecorded = 3,
}

/// <summary>
/// An agent's clock read across one request of a capture across hosts (`remote-agent-v1` §4.6): the controller's clock
/// when it sent the request and when the answer came, and the agent's when it answered.
/// </summary>
public sealed record WorkspaceClockReading(DateTimeOffset SentUtc, DateTimeOffset AnsweredUtc, DateTimeOffset ReceivedUtc);

/// <summary>One host's part in a capture across several hosts, as the investigation keeps it (§9).</summary>
public sealed record WorkspaceCoordinatedHost
{
    /// <summary>The host's name, as its agent said it.</summary>
    public required string MachineName { get; init; }

    /// <summary>The host's identity, as its agent said it: the identity its captures record (`remote-agent-v1` §10).</summary>
    public required Guid HostId { get; init; }

    /// <summary>The pin of the agent's key, in hexadecimal digits.</summary>
    public required string Agent { get; init; }

    /// <summary>The capture the host recorded; null where it recorded nothing.</summary>
    public Guid? CaptureId { get; init; }

    /// <summary>The member its capture became; null until collected.</summary>
    public Guid? SessionId { get; init; }

    public required WorkspaceHostPart Part { get; init; }

    /// <summary>Where its part stopped short - connect, prepare, start, keep, stop or collect - or null where it did not.</summary>
    public string? Stage { get; init; }

    /// <summary>Why, in the agent's, the connection's or the collection's own words; null where nothing stopped short.</summary>
    public string? Reason { get; init; }

    /// <summary>The start's acknowledgment, read across the start; null where the host did not start.</summary>
    public WorkspaceClockReading? Start { get; init; }

    /// <summary>The agent's last answer of the capture, read across it; null where none was kept.</summary>
    public WorkspaceClockReading? End { get; init; }
}

/// <summary>
/// One revision of a capture recorded across several hosts at once and collected into the investigation (§9, plan M11):
/// each host's part, what became of it and the clock readings its controller took. Collecting it again records a new
/// revision when anything changed; its latest revision is in force, every revision is kept, and none changes a session.
/// </summary>
public sealed record WorkspaceCoordination
{
    public required int Revision { get; init; }

    /// <summary>The capture across hosts, by the identity its controller gave it.</summary>
    public required Guid CoordinationId { get; init; }

    /// <summary>When its first start was sent, by its controller's clock.</summary>
    public required DateTimeOffset StartedUtc { get; init; }

    /// <summary>The profile asked of every host.</summary>
    public required string Profile { get; init; }

    public required IReadOnlyList<WorkspaceCoordinatedHost> Hosts { get; init; }

    public required DateTimeOffset RecordedUtc { get; init; }
}

public static partial class InvestigationWorkspace
{
    /// <summary>The stages a host's part may have stopped short at, in the words §9 uses.</summary>
    public static IReadOnlyList<string> CoordinationStages { get; } = ["connect", "prepare", "start", "keep", "stop", "collect"];

    /// <summary>The longest reason a host's part keeps, in characters.</summary>
    private const int MostReasonCharacters = 1_024;

    /// <summary>The coordinations in force: each one's latest revision, in the order each was first recorded.</summary>
    public static IReadOnlyList<WorkspaceCoordination> CoordinationsInForce(InvestigationWorkspaceFile workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return [.. workspace.Coordinations.GroupBy(coordination => coordination.CoordinationId)
            .Select(revisions => revisions.MaxBy(coordination => coordination.Revision)!)];
    }

    /// <summary>
    /// Records what collecting a capture across hosts found (§9): a revision of its own where anything changed since the
    /// one in force, and none where nothing did, which returns null. A host collected names a member already added.
    /// </summary>
    public static WorkspaceCoordination? RecordCoordination(string workspacePath, WorkspaceCoordination coordination, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(coordination);
        string full = System.IO.Path.GetFullPath(workspacePath);
        (InvestigationWorkspaceFile workspace, string text) = Load(full);
        if (CoordinationsInForce(workspace).FirstOrDefault(known => known.CoordinationId == coordination.CoordinationId) is { } current
            && current.StartedUtc == coordination.StartedUtc
            && current.Profile == coordination.Profile
            && current.Hosts.SequenceEqual(coordination.Hosts))
        {
            return null;
        }

        WorkspaceCoordination revision = coordination with
        {
            Revision = workspace.Coordinations.Count == 0 ? 1 : workspace.Coordinations.Max(known => known.Revision) + 1,
            RecordedUtc = now,
        };
        // Checked as it is written: an earlier version's file is written as the current one, which holds captures across hosts.
        InvestigationWorkspaceFile next = workspace with
        {
            Contract = Contract,
            Coordinations = [.. workspace.Coordinations, revision],
            UpdatedUtc = now,
        };
        if (CoordinationProblem(next) is { } problem)
        {
            throw new InvalidOperationException("The capture across hosts is not recorded: " + problem + ".");
        }

        Save(full, next, text);
        return revision;
    }

    /// <summary>What makes a file's coordinations contradict themselves, or null (`contracts/workspace-v26.md` §9).</summary>
    private static string? CoordinationProblem(InvestigationWorkspaceFile workspace)
    {
        // Captures across hosts arrived with the twenty-sixth version (revision 1099).
        if (workspace.Coordinations.Count > 0 && VersionOf(workspace) < 26)
        {
            return $"a {workspace.Contract} file holds no capture across hosts";
        }

        if (workspace.Coordinations.Any(coordination => coordination is null || coordination.Hosts is null
            || coordination.Hosts.Any(host => host is null)))
        {
            return "it lists an empty capture across hosts, or an empty host of one";
        }

        if (workspace.Coordinations.GroupBy(coordination => coordination.Revision)
            .FirstOrDefault(group => group.Key < 1 || group.Count() > 1) is { } revision)
        {
            return $"capture across hosts revision {revision.Key} is not a unique positive number";
        }

        Dictionary<Guid, WorkspaceMember> members = workspace.Members.ToDictionary(member => member.SessionId);
        foreach (WorkspaceCoordination coordination in workspace.Coordinations)
        {
            string? wrong = coordination.CoordinationId == Guid.Empty || string.IsNullOrWhiteSpace(coordination.Profile)
                || coordination.Hosts.Count == 0
                    ? "names no capture or profile, or no host"
                    : coordination.Hosts.Where(host => host.CaptureId is not null).GroupBy(host => host.CaptureId).Any(group => group.Count() > 1)
                        ? "names one capture for two hosts"
                        : coordination.Hosts.Select(host => CoordinatedHostProblem(host, members)).FirstOrDefault(problem => problem is not null);
            if (wrong is not null)
            {
                return $"capture across hosts revision {coordination.Revision} {wrong}";
            }
        }

        return null;
    }

    private static string? CoordinatedHostProblem(WorkspaceCoordinatedHost host, Dictionary<Guid, WorkspaceMember> members) =>
        string.IsNullOrWhiteSpace(host.MachineName) || host.HostId == Guid.Empty
            || host.Agent is not { Length: 64 } agent || !agent.All(char.IsAsciiHexDigit)
            ? "names a host without its name, identity or agent's pin"
            : !Enum.IsDefined(host.Part)
                ? $"says of {host.MachineName} a part no version defines"
                : (host.CaptureId is null) != (host.Part == WorkspaceHostPart.NotRecorded)
                    ? $"says {host.MachineName} recorded nothing yet names its capture, or names none it recorded"
                    : (host.SessionId is not null) != (host.Part == WorkspaceHostPart.Collected)
                        ? $"says {host.MachineName} was collected without naming its session, or names one it was not collected as"
                        : host.SessionId is { } session && !members.ContainsKey(session)
                            ? $"names a session of {host.MachineName} that is no member"
                            : host.Stage is { } stage && !CoordinationStages.Contains(stage, StringComparer.Ordinal)
                                ? $"says {host.MachineName} stopped short at '{stage}', which is no stage"
                                : (host.Part != WorkspaceHostPart.Collected && string.IsNullOrWhiteSpace(host.Reason))
                                    || host.Reason is { Length: > MostReasonCharacters }
                                    ? $"says no reason, or too long a one, why {host.MachineName} was not collected"
                                    : (host.End is not null && host.Start is null)
                                        || Disordered(host.Start) || Disordered(host.End)
                                        ? $"keeps a clock reading of {host.MachineName} out of order"
                                        : null;

    /// <summary>Whether a reading's answer came before it was sent, as the controller's clock read them.</summary>
    private static bool Disordered(WorkspaceClockReading? reading) => reading is not null && reading.ReceivedUtc < reading.SentUtc;
}
