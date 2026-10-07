using System.Globalization;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Application;

/// <summary>How an alignment revision was made (`contracts/workspace-v17.md` §5).</summary>
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
/// within <see cref="WithinNanoseconds"/>, and bounds how far the two clocks drift apart when the person states it. With a
/// second such instant, well apart from the first, it measures the two clocks' rate, and a stated drift then bounds how far
/// that rate may wander.
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

    /// <summary>
    /// The person's bound on how fast the clocks drift apart - with a second anchor, on how far their rate may wander from
    /// the one the anchors measure; null when not stated, which leaves it unknown.
    /// </summary>
    public double? DriftPartsPerMillion { get; init; }

    /// <summary>A manual alignment's second anchor, an instant of the member's session time; null with one anchor.</summary>
    public long? SecondSessionNanoseconds { get; init; }

    /// <summary>The same instant in the reference's session time; null with one anchor.</summary>
    public long? SecondReferenceNanoseconds { get; init; }

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

    /// <summary>Its member's alignment, or one it is aligned through, bounds no drift, and the instant is away from its anchors.</summary>
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
    /// <summary>When its uncertainty is unknown through another member it is aligned through, that member; null otherwise.</summary>
    public Guid? UnknownThrough { get; init; }

    /// <summary>Why the instant has no workspace time or no known uncertainty, of <paramref name="session"/>; null when it has both.</summary>
    public string? Why(string session, IFormatProvider? culture = null) => Gap switch
    {
        WorkspaceTimeGap.None => null,
        WorkspaceTimeGap.NoTimeReference => "no member is aligned, so the workspace has no time across members",
        WorkspaceTimeGap.NotAligned => $"{session} is not aligned to the workspace's time",
        _ when UnknownThrough is { } through => $"{session} is aligned through session {through.ToString("N")[..8]}, whose drift "
            + "is not stated, so its uncertainty there is unknown",
        _ => $"{session}'s drift from the time reference is not stated, so "
            + OperationText.Duration(Math.Abs(FromAnchorNanoseconds ?? 0), culture ?? CultureInfo.CurrentCulture)
            + " from the nearest instant it was aligned at, its uncertainty is unknown",
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
    /// when stated. With <paramref name="second"/>, a second such instant well apart from the first, the two measure the
    /// clocks' rate, and the drift bounds how far that rate may wander (§8.2). The first alignment makes its reference the
    /// workspace's time; every later one aligns to it.
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
        DateTimeOffset now,
        (long SessionNanoseconds, long ReferenceNanoseconds)? second = null)
    {
        string full = System.IO.Path.GetFullPath(workspacePath);
        (InvestigationWorkspaceFile workspace, string text) = Load(full);
        CheckAlignable(workspace, sessionId, referenceSessionId);
        if (withinNanoseconds < 0 || driftPartsPerMillion is { } drift && (!double.IsFinite(drift) || drift < 0))
        {
            throw new InvalidOperationException("An alignment's bound and drift are non-negative: a half-width and a rate.");
        }

        if (second is { } other && RateProblem(sessionNanoseconds, referenceNanoseconds, other.SessionNanoseconds,
            other.ReferenceNanoseconds, CultureInfo.CurrentCulture) is { } refused)
        {
            throw new InvalidOperationException(refused + " Nothing is aligned.");
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
            SecondSessionNanoseconds = second?.SessionNanoseconds,
            SecondReferenceNanoseconds = second?.ReferenceNanoseconds,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            RecordedUtc = now,
        };
        Save(full, workspace with
        {
            TimeReference = workspace.TimeReference ?? referenceSessionId,
            Alignments = [.. workspace.Alignments, alignment],
            UpdatedUtc = now,
        }, text);
        return alignment;
    }

    /// <summary>
    /// The largest rate two anchors may measure between two clocks, in parts per million: well past any working computer
    /// clock's, so a rate beyond it says an instant was misread, not a clock's behaviour.
    /// </summary>
    public const double MostPartsPerMillion = 1_000;

    /// <summary>The rate of the member's clock against the reference's that two anchors measure, as parts per million off 1.</summary>
    public static double MeasuredPartsPerMillion(WorkspaceAlignment alignment)
    {
        ArgumentNullException.ThrowIfNull(alignment);
        return alignment is { SessionNanoseconds: { } first, ReferenceNanoseconds: { } reference,
            SecondSessionNanoseconds: { } second, SecondReferenceNanoseconds: { } secondReference }
            ? (((double)checked(secondReference - reference) / checked(second - first)) - 1) * 1_000_000
            : throw new InvalidOperationException($"Alignment revision {alignment.Revision} has one anchor, which measures no rate.");
    }

    /// <summary>Why two anchors measure no rate: one instant twice, or a rate no working clock runs at; null when they do.</summary>
    private static string? RateProblem(long first, long reference, long second, long secondReference, IFormatProvider culture)
    {
        if (second == first || secondReference == reference)
        {
            return "The second instant is the first one again, in one session or the other, so the two measure no rate.";
        }

        double rate = (((double)checked(secondReference - reference) / checked(second - first)) - 1) * 1_000_000;
        return Math.Abs(rate) > MostPartsPerMillion
            ? string.Create(culture, $"The two instants say one clock runs {Math.Abs(rate):N0} ppm {(rate > 0 ? "faster" : "slower")} ")
                + string.Create(culture, $"than the other, more than the {MostPartsPerMillion:N0} ppm any working clock stays within, ")
                + "so an instant was likely misread."
            : null;
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
            TimeReference = workspace.TimeReference ?? referenceSessionId,
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
            TimeReference = workspace.TimeReference ?? referenceSessionId,
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

        Guid[] dependents = [.. workspace.Members.Select(member => member.SessionId)
            .Where(member => ActiveAlignment(workspace, member)?.ReferenceSessionId == sessionId)];
        if (dependents.Length > 0)
        {
            throw new InvalidOperationException($"Session{(dependents.Length == 1 ? string.Empty : "s")} "
                + string.Join(", ", dependents.Select(member => member.ToString("N")[..8]))
                + $" {(dependents.Length == 1 ? "is" : "are")} aligned through session {sessionId:N}, so withdrawing its alignment "
                + (dependents.Length == 1
                    ? "would leave it with no place. Withdraw or re-align its first."
                    : "would leave them with no place. Withdraw or re-align theirs first."));
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
    /// the samples' acquisition and the stated drift over the time between them, and the drift away from the anchor. A
    /// manual one with two anchors has the rate they measure: between them its anchors' bound holds, beyond them it grows
    /// along the rate's own uncertainty, and a rate that may wander by w moves an instant by up to 2w times its distance from
    /// the nearer anchor - or by an unknown amount when no one bounded the wander.
    /// </summary>
    public static ClockMapping MappingOf(WorkspaceAlignment alignment)
    {
        ArgumentNullException.ThrowIfNull(alignment);
        if (alignment is not { SessionNanoseconds: { } anchor, ReferenceNanoseconds: { } reference, WithinNanoseconds: { } within }
            || alignment.Mode == WorkspaceAlignmentMode.Withdrawn)
        {
            throw new InvalidOperationException($"Alignment revision {alignment.Revision} maps nothing: it withdraws one.");
        }

        if (alignment is { Mode: WorkspaceAlignmentMode.Manual, SecondSessionNanoseconds: { } second, SecondReferenceNanoseconds: { } secondReference })
        {
            // Each anchor may be off by up to the bound; the line through them is then off by the bound between them, and
            // beyond them by the bound plus twice the bound over their distance for every unit further out.
            double apart = Math.Abs((double)checked(second - anchor));
            return new()
            {
                Scale = (double)checked(secondReference - reference) / checked(second - anchor),
                OffsetNanoseconds = checked(reference - anchor),
                AnchorNanoseconds = anchor,
                SecondAnchorNanoseconds = second,
                Contributions =
                [
                    UncertaintyContribution.Fixed("the two anchors, as a person stated them", UncertaintyCombination.Bound, within),
                    UncertaintyContribution.Beyond("the anchors' bound, carried along the rate they measure beyond them",
                        UncertaintyCombination.Bound, 2 * within / apart * 1_000_000),
                    alignment.DriftPartsPerMillion is { } wander
                        ? UncertaintyContribution.Rate("the rate's wander from the one the anchors measure, as a person bounded it",
                            UncertaintyCombination.Bound, 2 * wander)
                        : UncertaintyContribution.UnknownRate("the rate's wander from the one the anchors measure, not stated",
                            UncertaintyCombination.Bound),
                    UncertaintyContribution.Rounding("the rounding of an instant placed at the measured rate", 1),
                ],
            };
        }

        IReadOnlyList<UncertaintyContribution> contributions = alignment.Mode switch
        {
            WorkspaceAlignmentMode.SameBoot =>
            [
                UncertaintyContribution.Rounding("one boot's counter, through the two captures' epochs, rounded", within),
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

    /// <summary>
    /// How a member reaches the workspace's time: the time reference's chain is empty; an aligned member's runs through its
    /// alignment to the member it is aligned to, and on through that member's, to the time reference. Null when the member
    /// has no place in the workspace's time.
    /// </summary>
    public static ClockChain? ChainOf(InvestigationWorkspaceFile workspace, Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.TimeReference is not { } reference)
        {
            return null;
        }

        var links = new List<ClockLink>();
        for (Guid current = sessionId; current != reference;)
        {
            if (ActiveAlignment(workspace, current) is not { ReferenceSessionId: { } next } alignment
                || links.Any(link => link.Clock == current) || links.Count > workspace.Members.Count)
            {
                return null;
            }

            links.Add(new(current, MappingOf(alignment)));
            current = next;
        }

        return new ClockChain(links);
    }

    /// <summary>Each placed member's chain into the workspace's time.</summary>
    public static IReadOnlyDictionary<Guid, ClockChain> Chains(InvestigationWorkspaceFile workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var chains = new Dictionary<Guid, ClockChain>();
        foreach (WorkspaceMember member in workspace.Members)
        {
            if (ChainOf(workspace, member.SessionId) is { } chain)
            {
                chains[member.SessionId] = chain;
            }
        }

        return chains;
    }

    /// <summary>The members a member is aligned through to the time reference, nearest first; empty for one aligned to it.</summary>
    internal static IReadOnlyList<Guid> Through(InvestigationWorkspaceFile workspace, Guid sessionId) =>
        ChainOf(workspace, sessionId) is { Links.Count: > 1 } chain ? [.. chain.Links.Skip(1).Select(link => link.Clock)] : [];

    /// <summary>An instant of a member placed in the workspace's time, or why it cannot be.</summary>
    public static WorkspaceInstant Place(InvestigationWorkspaceFile workspace, Guid sessionId, long sessionNanoseconds)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.Members.All(member => member.SessionId != sessionId))
        {
            throw new InvalidOperationException($"No member of this workspace is session {sessionId:N}.");
        }

        if (ChainOf(workspace, sessionId) is not { } chain)
        {
            return new(sessionId, sessionNanoseconds, null, null,
                workspace.TimeReference is null ? WorkspaceTimeGap.NoTimeReference : WorkspaceTimeGap.NotAligned, null);
        }

        long placed = chain.ToWorkspace(sessionNanoseconds);
        long? fromAnchor = chain.Links.Count == 0 ? null : chain.Links[0].Mapping.FromNearerAnchor(sessionNanoseconds);
        ChainUncertainty uncertainty = chain.UncertaintyAt(sessionNanoseconds);
        return uncertainty.Uncertainty is { } known
            ? new(sessionId, sessionNanoseconds, placed, known, WorkspaceTimeGap.None, fromAnchor)
            : new(sessionId, sessionNanoseconds, placed, null, WorkspaceTimeGap.DriftUnknown, fromAnchor)
            {
                UnknownThrough = uncertainty.UnknownAt == sessionId ? null : uncertainty.UnknownAt,
            };
    }

    /// <summary>
    /// Compares two members' instants: exactly when they share one clock, and otherwise in the workspace's time, stating an
    /// order only beyond the pair's uncertainty and nothing where an instant has no time or its uncertainty is unknown. Where
    /// the two are aligned through one member, what its alignment shares with both counts once (§8.2).
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
            : ChainOf(workspace, first) is { } firstChain && ChainOf(workspace, second) is { } secondChain
                ? ClockChain.Compare(firstChain, firstNanoseconds, secondChain, secondNanoseconds)
                : new TimeComparison(TimeOrder.Unknown, null, null));
    }

    private static WorkspaceAlignment? Active(IEnumerable<WorkspaceAlignment> alignments, Guid sessionId) =>
        alignments.Where(alignment => alignment.SessionId == sessionId).MaxBy(alignment => alignment.Revision) is { } latest
            && latest.Mode != WorkspaceAlignmentMode.Withdrawn
            ? latest
            : null;

    /// <summary>
    /// Refuses an alignment of a member that is not one, to itself, of the time reference, to a member with no place in the
    /// workspace's time, or to one aligned through the member itself, which would make each the other's reference.
    /// </summary>
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

        if (workspace.TimeReference is not { } reference)
        {
            return;
        }

        if (sessionId == reference)
        {
            throw new InvalidOperationException($"Session {reference:N} is the workspace's time reference: its clock is the "
                + "workspace's time, so it is not aligned; align the other members to it.");
        }

        if (ChainOf(workspace, referenceSessionId) is not { } chain)
        {
            throw new InvalidOperationException($"Session {referenceSessionId:N} has no place in the workspace's time, so nothing "
                + $"aligned to it would have one. Align it first, or align session {sessionId:N} to session {reference:N}.");
        }

        if (chain.Links.Any(link => link.Clock == sessionId))
        {
            throw new InvalidOperationException($"Session {referenceSessionId:N} is aligned through session {sessionId:N}, so "
                + "aligning that session to it would make each the other's reference. Align it to another member.");
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
                + "packaged, or captured by an earlier version of InterCat - so it has no recorded wall clock or boot to align "
                + "by. Align it by a stated instant.");
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

    /// <summary>What makes a file's time contradict itself, or null (`contracts/workspace-v17.md` §5).</summary>
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

        // Two anchors arrived with the fifth version (revision 264).
        if (VersionOf(workspace) < 5 && workspace.Alignments.Any(alignment =>
            alignment.SecondSessionNanoseconds is not null || alignment.SecondReferenceNanoseconds is not null))
        {
            return $"a {workspace.Contract} file holds no alignment with a second anchor";
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
                    || alignment.GapNanoseconds is not null || alignment.SecondSessionNanoseconds is not null
                    || alignment.SecondReferenceNanoseconds is not null ? "withdraws an alignment and states one" : null)
                : alignment.ReferenceSessionId is not { } other || !members.Contains(other) || other == alignment.SessionId
                    ? "is aligned to no other member"
                : alignment.SessionNanoseconds is null || alignment.ReferenceNanoseconds is null || alignment.WithinNanoseconds is not >= 0
                    ? "states no anchor or bound"
                : alignment.DriftPartsPerMillion is { } drift && (!double.IsFinite(drift) || drift < 0) ? "states a drift that is no rate"
                : (alignment.SecondSessionNanoseconds is null) != (alignment.SecondReferenceNanoseconds is null)
                    ? "states half of a second anchor"
                : alignment.SecondSessionNanoseconds is not null && alignment.Mode != WorkspaceAlignmentMode.Manual
                    ? "states a second anchor, which only a person's alignment has"
                : alignment is { SecondSessionNanoseconds: { } second, SecondReferenceNanoseconds: { } secondReference }
                    && RateProblem(alignment.SessionNanoseconds!.Value, alignment.ReferenceNanoseconds!.Value, second,
                        secondReference, CultureInfo.InvariantCulture) is { } rate
                    ? "has a second anchor that measures no rate: " + rate
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
                if (workspace.TimeReference is not { } time)
                {
                    return $"session {member.SessionId:N} is aligned, and the workspace has no time reference";
                }

                if (member.SessionId == time)
                {
                    return $"the time reference {time:N} is aligned to another member";
                }

                // Aligning through another member arrived with the sixth version (revision 265).
                if (VersionOf(workspace) < 6 && active.ReferenceSessionId != time)
                {
                    return $"session {member.SessionId:N} is aligned to a member that is not the workspace's time reference, "
                        + $"which a {workspace.Contract} file does not";
                }

                if (ChainOf(workspace, member.SessionId) is null)
                {
                    return $"session {member.SessionId:N} is aligned to a member with no place in the workspace's time, or "
                        + "through itself";
                }
            }
        }

        return null;
    }
}
