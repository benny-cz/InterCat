using System.Globalization;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>How an alignment revision was made (`contracts/workspace-v3.md` §5).</summary>
public enum WorkspaceAlignmentMode
{
    /// <summary>A person stated that an instant of the member's clock is an instant of the time reference's, within a bound.</summary>
    Manual = 1,

    /// <summary>A person withdrew the member's alignment: from this revision the member has no workspace time.</summary>
    Withdrawn = 2,

    /// <summary>
    /// The member and the reference recorded one boot's token, so they read one counter and align exactly through their
    /// capture epochs (ADR-040).
    /// </summary>
    SameBoot = 3,

    /// <summary>
    /// The anchor pairs the two captures' recorded wall-clock samples, within the wall clocks' agreement a person stated,
    /// the samples' acquisition, and the stated drift over the time between the samples.
    /// </summary>
    WallClock = 4,
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

    /// <summary>The boot both captures recorded, for a same-boot alignment; null otherwise.</summary>
    public Guid? BootToken { get; init; }

    /// <summary>
    /// For a wall-clock alignment, the person's bound on how far apart the two captures' wall clocks read at one instant; a
    /// sample says nothing of it, so it is always a person's statement. Null otherwise.
    /// </summary>
    public long? SynchronizationNanoseconds { get; init; }

    /// <summary>For a wall-clock alignment, the two samples' acquisition bounds, added; null otherwise.</summary>
    public long? AcquisitionNanoseconds { get; init; }

    /// <summary>For a wall-clock alignment, how far apart in wall-clock time its two samples were taken; null otherwise.</summary>
    public long? GapNanoseconds { get; init; }

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
        bool exact = Result.Uncertainty is { HalfWidthNanoseconds: 0 };
        string apart = Result.DifferenceNanoseconds is { } difference ? OperationText.Duration(Math.Abs(difference), format) : string.Empty;
        string within = Result.Uncertainty is { } pair ? "±" + OperationText.DurationAtLeast(pair.HalfWidthNanoseconds, format) : string.Empty;
        return Result.Order switch
        {
            TimeOrder.Unknown => "No order is stated: "
                + (First.Why("the first instant's session", format) ?? Second.Why("the second instant's session", format)) + ".",
            TimeOrder.Before when oneClock => $"The first is {apart} before the second, on one clock.",
            TimeOrder.After when oneClock => $"The first is {apart} after the second, on one clock.",
            TimeOrder.Ambiguous when oneClock => "Both are one instant of one clock, so no order between them is stated.",
            TimeOrder.Before when exact => $"The first is {apart} before the second, exactly.",
            TimeOrder.After when exact => $"The first is {apart} after the second, exactly.",
            TimeOrder.Ambiguous when exact => "Both are one instant, exactly, so no order between them is stated.",
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
        CheckAlignable(workspace, sessionId, referenceSessionId);
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
    /// Aligns <paramref name="sessionId"/> to <paramref name="referenceSessionId"/> exactly when both captures recorded one
    /// boot's token (ADR-040): they read one counter, so an instant of one is an instant of the other through their capture
    /// epochs, with no drift and no bound but the rounding of a rate that is not a whole number of nanoseconds per tick.
    /// Refused when either recorded no calibration or no boot, or the boots or clocks differ.
    /// </summary>
    public static WorkspaceAlignment AlignSameBoot(
        string workspacePath,
        Guid sessionId,
        Guid referenceSessionId,
        string? note,
        DateTimeOffset now)
    {
        string full = System.IO.Path.GetFullPath(workspacePath);
        (InvestigationWorkspaceFile workspace, string text) = Load(full);
        CheckAlignable(workspace, sessionId, referenceSessionId);
        (SourceClockDescriptor clock, ClockCalibrationV1 calibration) = Recorded(full, workspace, sessionId);
        (SourceClockDescriptor referenceClock, ClockCalibrationV1 referenceCalibration) = Recorded(full, workspace, referenceSessionId);
        if (calibration.BootToken is not { } boot || referenceCalibration.BootToken is not { } referenceBoot)
        {
            throw new InvalidOperationException($"Session {(calibration.BootToken is null ? sessionId : referenceSessionId):N} "
                + "recorded no boot token - the process that captured it could keep none - so no other capture can be shown to "
                + "share its boot. Align it by its wall clock or by a stated instant.");
        }

        if (boot != referenceBoot)
        {
            throw new InvalidOperationException($"Sessions {sessionId:N} and {referenceSessionId:N} ran in different boots "
                + $"({boot:N} and {referenceBoot:N}), so their counters are not one clock. Align them by their wall clocks.");
        }

        if (clock.HostId != referenceClock.HostId || clock.Encoding != referenceClock.Encoding
            || clock.TicksPerSecond != referenceClock.TicksPerSecond)
        {
            throw new InvalidOperationException($"Sessions {sessionId:N} and {referenceSessionId:N} name one boot but not one "
                + "host, encoding and rate of their clocks, which one boot's counter cannot be; nothing is aligned.");
        }

        // An instant t of the member is its counter reading epoch + t * rate; the reference reads that same reading as
        // t + (epoch - referenceEpoch) / rate. Exact when a tick is a whole number of nanoseconds; otherwise each session's
        // own conversion and the offset each round by at most half a nanosecond.
        Int128 scaled = (Int128)(clock.CaptureEpochNativeTicks - referenceClock.CaptureEpochNativeTicks) * 1_000_000_000;
        Int128 quotient = Int128.DivRem(scaled, clock.TicksPerSecond).Quotient;
        Int128 remainder = scaled - (quotient * clock.TicksPerSecond);
        long offset = checked((long)(quotient + (Int128.Abs(remainder) * 2 >= clock.TicksPerSecond ? Int128.Sign(remainder) : 0)));
        var alignment = new WorkspaceAlignment
        {
            Revision = NextRevision(workspace),
            SessionId = sessionId,
            Mode = WorkspaceAlignmentMode.SameBoot,
            ReferenceSessionId = referenceSessionId,
            SessionNanoseconds = 0,
            ReferenceNanoseconds = offset,
            WithinNanoseconds = 1_000_000_000 % clock.TicksPerSecond == 0 ? 0 : 2,
            DriftPartsPerMillion = 0,
            BootToken = boot,
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
    /// Aligns <paramref name="sessionId"/> to <paramref name="referenceSessionId"/> through their recorded wall-clock samples:
    /// of every pair of one sample of each, the two taken closest in wall-clock time anchor the alignment. Its bound adds the
    /// wall clocks' agreement the person states, <paramref name="synchronizationNanoseconds"/> - which no sample can measure
    /// - the two samples' acquisition, and <paramref name="driftPartsPerMillion"/> over the time between them; away from the
    /// anchor the drift grows as a manual alignment's does.
    /// </summary>
    public static WorkspaceAlignment AlignByWallClock(
        string workspacePath,
        Guid sessionId,
        Guid referenceSessionId,
        long synchronizationNanoseconds,
        double driftPartsPerMillion,
        string? note,
        DateTimeOffset now)
    {
        if (synchronizationNanoseconds < 0 || !double.IsFinite(driftPartsPerMillion) || driftPartsPerMillion < 0)
        {
            throw new InvalidOperationException("A wall-clock alignment's synchronization and drift are non-negative: a half-width and a rate.");
        }

        string full = System.IO.Path.GetFullPath(workspacePath);
        (InvestigationWorkspaceFile workspace, string text) = Load(full);
        CheckAlignable(workspace, sessionId, referenceSessionId);
        (SourceClockDescriptor clock, ClockCalibrationV1 calibration) = Recorded(full, workspace, sessionId);
        (SourceClockDescriptor referenceClock, ClockCalibrationV1 referenceCalibration) = Recorded(full, workspace, referenceSessionId);
        (ClockCalibrationSampleV1 sample, ClockCalibrationSampleV1 referenceSample) = calibration.Samples
            .SelectMany(sample => referenceCalibration.Samples.Select(other => (sample, other)))
            .MinBy(pair => Math.Abs((pair.sample.Utc - pair.other.Utc).Ticks));
        long gap = checked((sample.Utc - referenceSample.Utc).Ticks * 100);
        long acquisition = checked(sample.AcquisitionUncertaintyNanoseconds + referenceSample.AcquisitionUncertaintyNanoseconds);
        long drifted = checked((long)Math.Ceiling(driftPartsPerMillion * Math.Abs((double)gap) / 1_000_000));
        var alignment = new WorkspaceAlignment
        {
            Revision = NextRevision(workspace),
            SessionId = sessionId,
            Mode = WorkspaceAlignmentMode.WallClock,
            ReferenceSessionId = referenceSessionId,
            SessionNanoseconds = SessionNanoseconds(clock, sample.NativeTicks),
            ReferenceNanoseconds = checked(SessionNanoseconds(referenceClock, referenceSample.NativeTicks) + gap),
            WithinNanoseconds = checked(synchronizationNanoseconds + acquisition + drifted),
            DriftPartsPerMillion = driftPartsPerMillion,
            SynchronizationNanoseconds = synchronizationNanoseconds,
            AcquisitionNanoseconds = acquisition,
            GapNanoseconds = Math.Abs(gap),
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
    /// The mapping of an alignment: an offset from its one anchor, with the bounds its mode names (§8.2). A manual one takes
    /// the person's bound on the anchor and on the drift - or an unknown drift, which leaves the uncertainty unknown away
    /// from the anchor; a same-boot one reads one counter, exactly; a wall-clock one takes the wall clocks' stated agreement,
    /// the samples' acquisition and the stated drift over the time between them, and the drift away from the anchor.
    /// </summary>
    public static ClockMapping MappingOf(WorkspaceAlignment alignment)
    {
        ArgumentNullException.ThrowIfNull(alignment);
        if (alignment is not { SessionNanoseconds: { } anchor, ReferenceNanoseconds: { } reference, WithinNanoseconds: { } within }
            || alignment.Mode == WorkspaceAlignmentMode.Withdrawn)
        {
            throw new InvalidOperationException($"Alignment revision {alignment.Revision} maps nothing: it withdraws one.");
        }

        IReadOnlyList<UncertaintyContribution> contributions = alignment.Mode switch
        {
            WorkspaceAlignmentMode.SameBoot =>
            [
                UncertaintyContribution.Fixed("one boot's counter, through the two captures' epochs", UncertaintyCombination.Bound, within),
            ],
            WorkspaceAlignmentMode.WallClock =>
            [
                UncertaintyContribution.Fixed("the two wall clocks' agreement, as a person stated it", UncertaintyCombination.Bound,
                    alignment.SynchronizationNanoseconds!.Value),
                UncertaintyContribution.Fixed("the calibration samples' acquisition", UncertaintyCombination.Bound,
                    alignment.AcquisitionNanoseconds!.Value),
                UncertaintyContribution.Fixed("the stated drift over the time between the samples", UncertaintyCombination.Bound,
                    within - alignment.SynchronizationNanoseconds!.Value - alignment.AcquisitionNanoseconds!.Value),
                UncertaintyContribution.Rate("the drift, as a person bounded it", UncertaintyCombination.Bound,
                    alignment.DriftPartsPerMillion!.Value),
            ],
            _ =>
            [
                UncertaintyContribution.Fixed("the anchor, as a person stated it", UncertaintyCombination.Bound, within),
                alignment.DriftPartsPerMillion is { } drift
                    ? UncertaintyContribution.Rate("the drift, as a person bounded it", UncertaintyCombination.Bound, drift)
                    : UncertaintyContribution.UnknownRate("the drift between the two clocks, not stated", UncertaintyCombination.Bound),
            ],
        };
        return new() { OffsetNanoseconds = checked(reference - anchor), AnchorNanoseconds = anchor, Contributions = contributions };
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
        alignments.Where(alignment => alignment.SessionId == sessionId).MaxBy(alignment => alignment.Revision) is { } latest
            && latest.Mode != WorkspaceAlignmentMode.Withdrawn
            ? latest
            : null;

    /// <summary>Refuses an alignment of a member that is not one, to itself, or to a member other than the time reference.</summary>
    private static void CheckAlignable(InvestigationWorkspaceFile workspace, Guid sessionId, Guid referenceSessionId)
    {
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
    }

    /// <summary>
    /// What a member's capture recorded of its clock: its source clock and its clock calibration, read from the session where
    /// the member was last found, which must hold its capture.
    /// </summary>
    private static (SourceClockDescriptor Clock, ClockCalibrationV1 Calibration) Recorded(
        string workspacePath,
        InvestigationWorkspaceFile workspace,
        Guid sessionId)
    {
        WorkspaceMember member = workspace.Members.Single(known => known.SessionId == sessionId);
        WorkspaceMemberResolution resolution = ResolveOne(System.IO.Path.GetDirectoryName(workspacePath)!, member);
        if (!resolution.HoldsItsCapture)
        {
            throw new InvalidOperationException($"Session {sessionId:N} is {resolution.State.ToString().ToLowerInvariant()}: "
                + $"{resolution.Reason} Relink it before aligning it by what it recorded.");
        }

        Found found = Open(resolution.FullPath);
        ClockCalibrationV1 calibration = ClockCalibrationV1.Read(found.Root, found.Manifest)
            ?? throw new InvalidOperationException($"Session {sessionId:N} records no clock calibration - it was imported, "
                + "packaged, or captured before revision 255 - so it has no recorded wall clock or boot to align by. Align it by "
                + "a stated instant.");
        return calibration.ClockId == found.Clock.Id.Value
            ? (found.Clock, calibration)
            : throw new InvalidDataException($"Session {sessionId:N}'s clock calibration names another clock than its journal.");
    }

    /// <summary>A source reading as session time, converted as every reader converts it (§8.1).</summary>
    private static long SessionNanoseconds(SourceClockDescriptor clock, long nativeTicks) =>
        SourceClockMath.ConvertToSession(clock, new NativeTimestamp(clock.Id, clock.Encoding, nativeTicks)).SessionTime?.Nanoseconds
            ?? throw new InvalidDataException($"A calibration sample's reading {nativeTicks} is not on its capture's clock.");

    private static int NextRevision(InvestigationWorkspaceFile workspace) =>
        workspace.Alignments.Count == 0 ? 1 : checked(workspace.Alignments.Max(alignment => alignment.Revision) + 1);

    /// <summary>What makes a file's time contradict itself, or null (`contracts/workspace-v3.md` §5).</summary>
    private static string? TimeProblem(InvestigationWorkspaceFile workspace)
    {
        if (workspace.Contract == FirstContract && (workspace.TimeReference is not null || workspace.Alignments.Count > 0))
        {
            return $"a {FirstContract} file holds no time reference or alignment";
        }

        if (workspace.Contract == SecondContract && workspace.Alignments.Any(alignment => alignment is not null
            && (alignment.Mode is not (WorkspaceAlignmentMode.Manual or WorkspaceAlignmentMode.Withdrawn) || alignment.BootToken is not null
                || alignment.SynchronizationNanoseconds is not null || alignment.AcquisitionNanoseconds is not null
                || alignment.GapNanoseconds is not null)))
        {
            return $"a {SecondContract} file holds only manual alignments";
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
            bool aligning = alignment.Mode is WorkspaceAlignmentMode.Manual or WorkspaceAlignmentMode.SameBoot
                or WorkspaceAlignmentMode.WallClock;
            bool wallClock = alignment.Mode == WorkspaceAlignmentMode.WallClock;
            string? problem = !members.Contains(alignment.SessionId) ? "names no member"
                : !aligning && alignment.Mode != WorkspaceAlignmentMode.Withdrawn ? "is of no known mode"
                : !aligning ? (alignment.ReferenceSessionId is not null || alignment.SessionNanoseconds is not null
                    || alignment.ReferenceNanoseconds is not null || alignment.WithinNanoseconds is not null
                    || alignment.DriftPartsPerMillion is not null || alignment.BootToken is not null
                    || alignment.SynchronizationNanoseconds is not null || alignment.AcquisitionNanoseconds is not null
                    || alignment.GapNanoseconds is not null ? "withdraws an alignment and states one" : null)
                : alignment.ReferenceSessionId is not { } other || !members.Contains(other) || other == alignment.SessionId
                    ? "is aligned to no other member"
                : alignment.SessionNanoseconds is null || alignment.ReferenceNanoseconds is null || alignment.WithinNanoseconds is not >= 0
                    ? "states no anchor or bound"
                : alignment.DriftPartsPerMillion is { } drift && (!double.IsFinite(drift) || drift < 0) ? "states a drift that is no rate"
                : (alignment.Mode == WorkspaceAlignmentMode.SameBoot) != (alignment.BootToken is { } token && token != Guid.Empty)
                    ? "names a boot only when, and exactly when, it aligns one boot's captures"
                : alignment.Mode == WorkspaceAlignmentMode.SameBoot && alignment.DriftPartsPerMillion is not 0.0
                    ? "aligns one boot's counter and states a drift"
                : wallClock != (alignment.SynchronizationNanoseconds is >= 0 && alignment.AcquisitionNanoseconds is >= 0
                    && alignment.GapNanoseconds is >= 0 && alignment.DriftPartsPerMillion is not null)
                    ? "states the wall clocks' agreement, the samples' acquisition, their gap and a drift only when, and exactly "
                        + "when, it aligns by wall clocks"
                : wallClock && alignment.WithinNanoseconds < alignment.SynchronizationNanoseconds + alignment.AcquisitionNanoseconds
                    ? "is bounded more narrowly than its wall clocks' agreement and acquisition"
                : null;
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
