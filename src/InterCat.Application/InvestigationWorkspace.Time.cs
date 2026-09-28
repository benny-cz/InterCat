using System.Globalization;
using InterCat.Domain;

namespace InterCat.Application;

/// <summary>How an alignment revision was made (`contracts/workspace-v2.md` §5).</summary>
public enum WorkspaceAlignmentMode
{
    /// <summary>A person stated that an instant of the member's clock is an instant of the time reference's, within a bound.</summary>
    Manual = 1,

    /// <summary>A person withdrew the member's alignment: from this revision the member has no workspace time.</summary>
    Withdrawn = 2,
}

/// <summary>
/// One revision of a member's alignment to the workspace's time (§8.2): an annotation, never a rewrite of a timestamp (I9).
/// A manual one says that the member's <see cref="SessionNanoseconds"/> is the reference's <see cref="ReferenceNanoseconds"/>
/// within <see cref="WithinNanoseconds"/>, and bounds how far the two clocks drift apart when the person states it.
/// </summary>
public sealed record WorkspaceAlignment
{
    public required int Revision { get; init; }

    public required Guid SessionId { get; init; }

    public required WorkspaceAlignmentMode Mode { get; init; }

    /// <summary>The member whose clock is the workspace's time; null for a withdrawal.</summary>
    public Guid? ReferenceSessionId { get; init; }

    /// <summary>The anchor: an instant of the member's session time, in nanoseconds; null for a withdrawal.</summary>
    public long? SessionNanoseconds { get; init; }

    /// <summary>The same instant in the reference's session time; null for a withdrawal.</summary>
    public long? ReferenceNanoseconds { get; init; }

    /// <summary>The person's bound on the anchor, a half-width; null for a withdrawal.</summary>
    public long? WithinNanoseconds { get; init; }

    /// <summary>The person's bound on how fast the clocks drift apart; null when not stated, which leaves it unknown.</summary>
    public double? DriftPartsPerMillion { get; init; }

    public string? Note { get; init; }

    public required DateTimeOffset RecordedUtc { get; init; }
}

/// <summary>Why an instant has no workspace time, or no known uncertainty in it.</summary>
public enum WorkspaceTimeGap
{
    /// <summary>It has both.</summary>
    None = 0,

    /// <summary>No member is aligned, so the workspace has no time across members.</summary>
    NoTimeReference = 1,

    /// <summary>Its member is not aligned to the workspace's time.</summary>
    NotAligned = 2,

    /// <summary>Its member's alignment bounds no drift, and the instant is away from the anchor.</summary>
    DriftUnknown = 3,
}

/// <summary>An instant of one member's session time, and where it falls in the workspace's time, when it falls anywhere.</summary>
public sealed record WorkspaceInstant(
    Guid SessionId,
    long SessionNanoseconds,
    long? WorkspaceNanoseconds,
    TimeUncertainty? Uncertainty,
    WorkspaceTimeGap Gap,
    long? FromAnchorNanoseconds)
{
    /// <summary>Why the instant has no workspace time or no known uncertainty, of <paramref name="session"/>; null when it has both.</summary>
    public string? Why(string session, IFormatProvider? culture = null) => Gap switch
    {
        WorkspaceTimeGap.None => null,
        WorkspaceTimeGap.NoTimeReference => "no member is aligned, so the workspace has no time across members",
        WorkspaceTimeGap.NotAligned => $"{session} is not aligned to the workspace's time",
        _ => $"{session}'s drift from the time reference is not stated, so "
            + OperationText.Duration(Math.Abs(FromAnchorNanoseconds ?? 0), culture ?? CultureInfo.CurrentCulture)
            + " from its anchor its uncertainty is unknown",
    };
}

/// <summary>Two instants compared in the workspace's time, and what may be said of their order (§8.2).</summary>
public sealed record WorkspaceComparison(WorkspaceInstant First, WorkspaceInstant Second, TimeComparison Result)
{
    /// <summary>What the comparison says, in words: an order only beyond the pair's uncertainty, and why nothing when nothing.</summary>
    public string Statement(IFormatProvider? culture = null)
    {
        IFormatProvider format = culture ?? CultureInfo.CurrentCulture;
        bool oneClock = First.SessionId == Second.SessionId;
        string apart = Result.DifferenceNanoseconds is { } difference ? OperationText.Duration(Math.Abs(difference), format) : string.Empty;
        string within = Result.Uncertainty is { } pair ? "±" + OperationText.DurationAtLeast(pair.HalfWidthNanoseconds, format) : string.Empty;
        return Result.Order switch
        {
            TimeOrder.Unknown => "No order is stated: "
                + (First.Why("the first instant's session", format) ?? Second.Why("the second instant's session", format)) + ".",
            TimeOrder.Before when oneClock => $"The first is {apart} before the second, on one clock.",
            TimeOrder.After when oneClock => $"The first is {apart} after the second, on one clock.",
            TimeOrder.Ambiguous when oneClock => "Both are one instant of one clock, so no order between them is stated.",
            TimeOrder.Before => $"The first is before the second by {apart}, more than their combined uncertainty of {within}.",
            TimeOrder.After => $"The first is after the second by {apart}, more than their combined uncertainty of {within}.",
            _ => $"Their order is ambiguous: they are {apart} apart, within their combined uncertainty of {within}.",
        };
    }
}

public static partial class InvestigationWorkspace
{
    /// <summary>
    /// Records a person's alignment of <paramref name="sessionId"/> to the workspace's time: its instant
    /// <paramref name="sessionNanoseconds"/> is the reference's <paramref name="referenceNanoseconds"/> within
    /// <paramref name="withinNanoseconds"/>, the clocks drifting apart by at most <paramref name="driftPartsPerMillion"/>
    /// when stated. The first alignment makes its reference the workspace's time; every later one aligns to it.
    /// </summary>
    public static WorkspaceAlignment Align(
        string workspacePath,
        Guid sessionId,
        long sessionNanoseconds,
        Guid referenceSessionId,
        long referenceNanoseconds,
        long withinNanoseconds,
        double? driftPartsPerMillion,
        string? note,
        DateTimeOffset now)
    {
        string full = System.IO.Path.GetFullPath(workspacePath);
        (InvestigationWorkspaceFile workspace, string text) = Load(full);
        foreach (Guid named in new[] { sessionId, referenceSessionId })
        {
            if (workspace.Members.All(member => member.SessionId != named))
            {
                throw new InvalidOperationException($"No member of this workspace is session {named:N}.");
            }
        }

        if (sessionId == referenceSessionId)
        {
            throw new InvalidOperationException("A member is aligned to another member's clock, not to its own.");
        }

        if (workspace.TimeReference is { } reference && referenceSessionId != reference)
        {
            throw new InvalidOperationException(sessionId == reference
                ? $"Session {reference:N} is the workspace's time reference: its clock is the workspace's time, so it is not "
                    + "aligned; align the other members to it."
                : $"The workspace's time is session {reference:N}'s clock, so a member is aligned to it, not to session "
                    + $"{referenceSessionId:N}.");
        }

        if (withinNanoseconds < 0 || driftPartsPerMillion is { } drift && (!double.IsFinite(drift) || drift < 0))
        {
            throw new InvalidOperationException("An alignment's bound and drift are non-negative: a half-width and a rate.");
        }

        var alignment = new WorkspaceAlignment
        {
            Revision = NextRevision(workspace),
            SessionId = sessionId,
            Mode = WorkspaceAlignmentMode.Manual,
            ReferenceSessionId = referenceSessionId,
            SessionNanoseconds = sessionNanoseconds,
            ReferenceNanoseconds = referenceNanoseconds,
            WithinNanoseconds = withinNanoseconds,
            DriftPartsPerMillion = driftPartsPerMillion,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            RecordedUtc = now,
        };
        Save(full, workspace with
        {
            TimeReference = referenceSessionId,
            Alignments = [.. workspace.Alignments, alignment],
            UpdatedUtc = now,
        }, text);
        return alignment;
    }

    /// <summary>
    /// Withdraws a member's alignment, kept as a revision of its own. When no member is aligned any more, the workspace has
    /// no time reference, so the next alignment may choose another.
    /// </summary>
    public static WorkspaceAlignment Withdraw(string workspacePath, Guid sessionId, DateTimeOffset now)
    {
        string full = System.IO.Path.GetFullPath(workspacePath);
        (InvestigationWorkspaceFile workspace, string text) = Load(full);
        if (ActiveAlignment(workspace, sessionId) is null)
        {
            throw new InvalidOperationException($"Session {sessionId:N} is not aligned to the workspace's time.");
        }

        var withdrawal = new WorkspaceAlignment
        {
            Revision = NextRevision(workspace),
            SessionId = sessionId,
            Mode = WorkspaceAlignmentMode.Withdrawn,
            RecordedUtc = now,
        };
        WorkspaceAlignment[] alignments = [.. workspace.Alignments, withdrawal];
        bool anyAligned = workspace.Members.Any(member => Active(alignments, member.SessionId) is not null);
        Save(full, workspace with
        {
            TimeReference = anyAligned ? workspace.TimeReference : null,
            Alignments = alignments,
            UpdatedUtc = now,
        }, text);
        return withdrawal;
    }

    /// <summary>A member's alignment in force: its latest revision, when that is not a withdrawal.</summary>
    public static WorkspaceAlignment? ActiveAlignment(InvestigationWorkspaceFile workspace, Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return Active(workspace.Alignments, sessionId);
    }

    /// <summary>
    /// The mapping of a manual alignment: an offset from its one anchor, with the person's bound on the anchor and, away from
    /// it, their bound on the drift - or an unknown drift, which leaves the uncertainty unknown away from the anchor (§8.2).
    /// </summary>
    public static ClockMapping MappingOf(WorkspaceAlignment alignment)
    {
        ArgumentNullException.ThrowIfNull(alignment);
        if (alignment is not { Mode: WorkspaceAlignmentMode.Manual, SessionNanoseconds: { } anchor, ReferenceNanoseconds: { } reference, WithinNanoseconds: { } within })
        {
            throw new InvalidOperationException($"Alignment revision {alignment.Revision} maps nothing: it withdraws one.");
        }

        return new()
        {
            OffsetNanoseconds = checked(reference - anchor),
            AnchorNanoseconds = anchor,
            Contributions =
            [
                UncertaintyContribution.Fixed("the anchor, as a person stated it", UncertaintyCombination.Bound, within),
                alignment.DriftPartsPerMillion is { } drift
                    ? UncertaintyContribution.Rate("the drift, as a person bounded it", UncertaintyCombination.Bound, drift)
                    : UncertaintyContribution.UnknownRate("the drift between the two clocks, not stated", UncertaintyCombination.Bound),
            ],
        };
    }

    /// <summary>Each member's mapping into the workspace's time: the reference's exact, an aligned member's its alignment's.</summary>
    public static IReadOnlyDictionary<Guid, ClockMapping> Mappings(InvestigationWorkspaceFile workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var mappings = new Dictionary<Guid, ClockMapping>();
        if (workspace.TimeReference is { } reference)
        {
            mappings[reference] = ClockMapping.Reference;
        }

        foreach (WorkspaceMember member in workspace.Members)
        {
            if (ActiveAlignment(workspace, member.SessionId) is { } alignment)
            {
                mappings[member.SessionId] = MappingOf(alignment);
            }
        }

        return mappings;
    }

    /// <summary>An instant of a member placed in the workspace's time, or why it cannot be.</summary>
    public static WorkspaceInstant Place(InvestigationWorkspaceFile workspace, Guid sessionId, long sessionNanoseconds)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.Members.All(member => member.SessionId != sessionId))
        {
            throw new InvalidOperationException($"No member of this workspace is session {sessionId:N}.");
        }

        if (!Mappings(workspace).TryGetValue(sessionId, out ClockMapping? mapping))
        {
            return new(sessionId, sessionNanoseconds, null, null,
                workspace.TimeReference is null ? WorkspaceTimeGap.NoTimeReference : WorkspaceTimeGap.NotAligned, null);
        }

        long placed = mapping.ToWorkspace(sessionNanoseconds);
        long? fromAnchor = sessionId == workspace.TimeReference ? null : checked(sessionNanoseconds - mapping.AnchorNanoseconds);
        return mapping.UncertaintyAt(sessionNanoseconds) is { } uncertainty
            ? new(sessionId, sessionNanoseconds, placed, uncertainty, WorkspaceTimeGap.None, fromAnchor)
            : new(sessionId, sessionNanoseconds, placed, null, WorkspaceTimeGap.DriftUnknown, fromAnchor);
    }

    /// <summary>
    /// Compares two members' instants: exactly when they share one clock, and otherwise in the workspace's time, stating an
    /// order only beyond the pair's uncertainty and nothing where an instant has no time or its uncertainty is unknown.
    /// </summary>
    public static WorkspaceComparison Compare(
        InvestigationWorkspaceFile workspace,
        Guid first,
        long firstNanoseconds,
        Guid second,
        long secondNanoseconds)
    {
        WorkspaceInstant a = Place(workspace, first, firstNanoseconds);
        WorkspaceInstant b = Place(workspace, second, secondNanoseconds);
        return new(a, b, first == second
            ? TimeComparison.OnOneClock(firstNanoseconds, secondNanoseconds)
            : TimeComparison.Of(a.Uncertainty is null ? null : a.WorkspaceNanoseconds, a.Uncertainty,
                b.Uncertainty is null ? null : b.WorkspaceNanoseconds, b.Uncertainty));
    }

    private static WorkspaceAlignment? Active(IEnumerable<WorkspaceAlignment> alignments, Guid sessionId) =>
        alignments.Where(alignment => alignment.SessionId == sessionId).MaxBy(alignment => alignment.Revision) is
            { Mode: WorkspaceAlignmentMode.Manual } active
            ? active
            : null;

    private static int NextRevision(InvestigationWorkspaceFile workspace) =>
        workspace.Alignments.Count == 0 ? 1 : checked(workspace.Alignments.Max(alignment => alignment.Revision) + 1);

    /// <summary>What makes a file's time contradict itself, or null (`contracts/workspace-v2.md` §5).</summary>
    private static string? TimeProblem(InvestigationWorkspaceFile workspace)
    {
        if (workspace.Contract == FirstContract && (workspace.TimeReference is not null || workspace.Alignments.Count > 0))
        {
            return $"a {FirstContract} file holds no time reference or alignment";
        }

        if (workspace.Alignments.Any(alignment => alignment is null))
        {
            return "it lists an empty alignment";
        }

        HashSet<Guid> members = [.. workspace.Members.Select(member => member.SessionId)];
        if (workspace.TimeReference is { } reference && !members.Contains(reference))
        {
            return $"its time reference {reference:N} is no member";
        }

        if (workspace.Alignments.GroupBy(alignment => alignment.Revision).FirstOrDefault(group => group.Key < 1 || group.Count() > 1)
            is { } revision)
        {
            return $"alignment revision {revision.Key} is not a unique positive number";
        }

        foreach (WorkspaceAlignment alignment in workspace.Alignments)
        {
            string? problem = !members.Contains(alignment.SessionId) ? "names no member"
                : alignment.Mode switch
                {
                    WorkspaceAlignmentMode.Manual when alignment.ReferenceSessionId is not { } other || !members.Contains(other)
                        || other == alignment.SessionId => "is aligned to no other member",
                    WorkspaceAlignmentMode.Manual when alignment.SessionNanoseconds is null || alignment.ReferenceNanoseconds is null
                        || alignment.WithinNanoseconds is not >= 0 => "states no anchor or bound",
                    WorkspaceAlignmentMode.Manual when alignment.DriftPartsPerMillion is { } drift && (!double.IsFinite(drift) || drift < 0)
                        => "states a drift that is no rate",
                    WorkspaceAlignmentMode.Manual => null,
                    WorkspaceAlignmentMode.Withdrawn when alignment.ReferenceSessionId is not null || alignment.SessionNanoseconds is not null
                        || alignment.ReferenceNanoseconds is not null || alignment.WithinNanoseconds is not null
                        || alignment.DriftPartsPerMillion is not null => "withdraws an alignment and states one",
                    WorkspaceAlignmentMode.Withdrawn => null,
                    _ => "is of no known mode",
                };
            if (problem is not null)
            {
                return $"alignment revision {alignment.Revision} {problem}";
            }
        }

        foreach (WorkspaceMember member in workspace.Members)
        {
            if (ActiveAlignment(workspace, member.SessionId) is { } active)
            {
                if (workspace.TimeReference is not { } time || active.ReferenceSessionId != time)
                {
                    return $"session {member.SessionId:N} is aligned to a member that is not the workspace's time reference";
                }

                if (member.SessionId == time)
                {
                    return $"the time reference {time:N} is aligned to another member";
                }
            }
        }

        return null;
    }
}
