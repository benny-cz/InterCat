using InterCat.Analysis;

namespace InterCat.Application;

/// <summary>What a person decided of a candidate join (`contracts/workspace-v12.md` §6, ADR-041).</summary>
public enum WorkspaceJoinDecision
{
    /// <summary>A person accepted the candidate as one connection: a manual join, never evidence.</summary>
    Accepted = 1,

    /// <summary>A person rejected the candidate: the two are not one connection, whatever matches.</summary>
    Rejected = 2,

    /// <summary>A person withdrew their decision: from this revision the pair is undecided again.</summary>
    Withdrawn = 3,
}

/// <summary>One end of a join: a member and its one-sided connection's stable key (`relations-v1` §5b).</summary>
public sealed record WorkspaceJoinEnd(Guid SessionId, string Key);

/// <summary>The alignment revision in force for a session when a decision was made; 0 when none was.</summary>
public sealed record WorkspaceAlignmentInForce(Guid SessionId, int Revision);

/// <summary>
/// One revision of a person's decision about a candidate join: kept when a later one replaces or withdraws it, recorded
/// with the alignments it was made under, so a decision made before the investigation's time changed reads as one to
/// review (§8.3). It changes no session and establishes nothing: an accepted join stays a person's.
/// </summary>
public sealed record WorkspaceJoin
{
    public required int Revision { get; init; }

    public required WorkspaceJoinDecision Decision { get; init; }

    public required WorkspaceJoinEnd First { get; init; }

    public required WorkspaceJoinEnd Second { get; init; }

    /// <summary>The alignment in force for each of its two sessions when it was decided; empty for a withdrawal.</summary>
    public IReadOnlyList<WorkspaceAlignmentInForce> DecidedUnder { get; init; } = [];

    public string? Note { get; init; }

    public required DateTimeOffset RecordedUtc { get; init; }
}

public static partial class InvestigationWorkspace
{
    /// <summary>
    /// Records a person's decision about the join of <paramref name="first"/> and <paramref name="second"/> - accepted,
    /// rejected, or a withdrawal of the decision in force - as a revision of the investigation, with the alignments in force
    /// for both sessions. The two ends are one-sided connections of two different members.
    /// </summary>
    public static WorkspaceJoin Decide(
        string workspacePath,
        WorkspaceJoinEnd first,
        WorkspaceJoinEnd second,
        WorkspaceJoinDecision decision,
        string? note,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (!Enum.IsDefined(decision))
        {
            throw new ArgumentOutOfRangeException(nameof(decision), decision, "A join is accepted, rejected or withdrawn.");
        }

        string full = System.IO.Path.GetFullPath(workspacePath);
        (InvestigationWorkspaceFile workspace, string text) = Load(full);
        foreach (WorkspaceJoinEnd end in new[] { first, second })
        {
            if (workspace.Members.All(member => member.SessionId != end.SessionId))
            {
                throw new InvalidOperationException($"No member of this workspace is session {end.SessionId:N}.");
            }

            if (!TransportConnection.IsKey(end.Key))
            {
                throw new InvalidOperationException($"'{end.Key}' names no connection: a join is of two one-sided connections.");
            }
        }

        if (first.SessionId == second.SessionId)
        {
            throw new InvalidOperationException("A join is of two sessions' connections; one session's own are its channels.");
        }

        if (decision == WorkspaceJoinDecision.Withdrawn && DecisionFor(workspace, first, second) is null)
        {
            throw new InvalidOperationException("No decision about this join is in force, so there is none to withdraw.");
        }

        var join = new WorkspaceJoin
        {
            Revision = workspace.Joins.Count == 0 ? 1 : checked(workspace.Joins.Max(known => known.Revision) + 1),
            Decision = decision,
            First = first,
            Second = second,
            DecidedUnder = decision == WorkspaceJoinDecision.Withdrawn
                ? []
                : [.. new[] { first.SessionId, second.SessionId }.Concat(Through(workspace, first.SessionId))
                    .Concat(Through(workspace, second.SessionId)).Distinct().Select(session => InForce(workspace, session))],
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            RecordedUtc = now,
        };
        Save(full, workspace with { Joins = [.. workspace.Joins, join], UpdatedUtc = now }, text);
        return join;
    }

    /// <summary>The decision in force about a pair of connections, in either order: its latest revision, unless a withdrawal.</summary>
    public static WorkspaceJoin? DecisionFor(InvestigationWorkspaceFile workspace, WorkspaceJoinEnd first, WorkspaceJoinEnd second)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return workspace.Joins.Where(join => SamePair(join, first, second)).MaxBy(join => join.Revision) is { } latest
            && latest.Decision != WorkspaceJoinDecision.Withdrawn
            ? latest
            : null;
    }

    /// <summary>Every decision in force, one per pair of connections, in the order first made.</summary>
    public static IReadOnlyList<WorkspaceJoin> DecisionsInForce(InvestigationWorkspaceFile workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return [.. workspace.Joins
            .GroupBy(join => Pair(join.First, join.Second))
            .Select(group => group.MaxBy(join => join.Revision)!)
            .Where(join => join.Decision != WorkspaceJoinDecision.Withdrawn)
            .OrderBy(join => join.Revision)];
    }

    /// <summary>
    /// Whether a decision was made under the alignments in force now: false when either session's alignment, or that of a
    /// session either is aligned through, has been revised, withdrawn or made since, which is when the timing it was decided
    /// on may no longer hold (§8.3).
    /// </summary>
    public static bool DecidedUnderCurrentTime(InvestigationWorkspaceFile workspace, WorkspaceJoin join)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(join);
        return join.DecidedUnder.All(under => InForce(workspace, under.SessionId).Revision == under.Revision);
    }

    private static WorkspaceAlignmentInForce InForce(InvestigationWorkspaceFile workspace, Guid sessionId) =>
        new(sessionId, ActiveAlignment(workspace, sessionId)?.Revision ?? 0);

    private static bool SamePair(WorkspaceJoin join, WorkspaceJoinEnd first, WorkspaceJoinEnd second) =>
        Pair(join.First, join.Second) == Pair(first, second);

    private static (Guid, string, Guid, string) Pair(WorkspaceJoinEnd a, WorkspaceJoinEnd b) =>
        (a.SessionId, a.Key).CompareTo((b.SessionId, b.Key)) <= 0
            ? (a.SessionId, a.Key, b.SessionId, b.Key)
            : (b.SessionId, b.Key, a.SessionId, a.Key);

    /// <summary>What makes a file's joins contradict themselves, or null (`contracts/workspace-v12.md` §6).</summary>
    private static string? JoinProblem(InvestigationWorkspaceFile workspace)
    {
        // Join decisions arrived with the fourth version (revision 260).
        if (VersionOf(workspace) < 4 && workspace.Joins.Count > 0)
        {
            return $"a {workspace.Contract} file holds no join decision";
        }

        if (workspace.Joins.Any(join => join is null || join.First is null || join.Second is null || join.DecidedUnder is null))
        {
            return "it lists an empty join or end";
        }

        if (workspace.Joins.GroupBy(join => join.Revision).FirstOrDefault(group => group.Key < 1 || group.Count() > 1) is { } revision)
        {
            return $"join revision {revision.Key} is not a unique positive number";
        }

        HashSet<Guid> members = [.. workspace.Members.Select(member => member.SessionId)];
        foreach (WorkspaceJoin join in workspace.Joins)
        {
            string? problem = !Enum.IsDefined(join.Decision) ? "is of no known decision"
                : !members.Contains(join.First.SessionId) || !members.Contains(join.Second.SessionId) ? "names no member"
                : join.First.SessionId == join.Second.SessionId ? "joins one session's connections"
                : !TransportConnection.IsKey(join.First.Key) || !TransportConnection.IsKey(join.Second.Key) ? "names no connection"
                : (join.Decision == WorkspaceJoinDecision.Withdrawn) != (join.DecidedUnder.Count == 0)
                    ? "states the alignments it was decided under only when, and exactly when, it decides"
                : join.DecidedUnder.Count > 0 && (join.DecidedUnder.Any(under => under is null || under.Revision < 0 || !members.Contains(under.SessionId))
                    || join.DecidedUnder.GroupBy(under => under.SessionId).Any(group => group.Count() > 1)
                    || join.DecidedUnder.All(under => under.SessionId != join.First.SessionId)
                    || join.DecidedUnder.All(under => under.SessionId != join.Second.SessionId))
                    ? "states the alignments it was decided under other than once each, its own two sessions' among them"
                : join.DecidedUnder.Count > 2 && VersionOf(workspace) < 6
                    ? $"states the alignments of other sessions than its own, which a {workspace.Contract} file does not"
                : null;
            if (problem is not null)
            {
                return $"join revision {join.Revision} {problem}";
            }
        }

        return null;
    }
}
