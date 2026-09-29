namespace InterCat.Domain;

/// <summary>How a contribution to a time uncertainty combines with the others (§8.2).</summary>
public enum UncertaintyCombination
{
    /// <summary>Known only as a bound - a person's statement, an unverified synchronization claim: added linearly.</summary>
    Bound = 1,

    /// <summary>An independent random contribution, such as a measured calibration: combined in quadrature.</summary>
    Random = 2,
}

/// <summary>
/// One contribution to a clock mapping's uncertainty: a fixed half-width, a rate that grows with the distance from the
/// mapping's nearer anchor (a drift) or only beyond its anchors, or unknown - which makes the whole uncertainty unknown,
/// never zero and never a default (§8.2, R3, R21).
/// </summary>
public sealed record UncertaintyContribution
{
    public required string Source { get; init; }

    public required UncertaintyCombination Combination { get; init; }

    /// <summary>A fixed half-width in nanoseconds; null when the contribution is a rate, or unknown.</summary>
    public double? Nanoseconds { get; init; }

    /// <summary>A rate in parts per million of the distance from the anchor; null when it is fixed, or unknown.</summary>
    public double? PartsPerMillion { get; init; }

    /// <summary>Whether the contribution grows with the distance from the anchor, as a drift does, known or not.</summary>
    public bool GrowsWithDistance { get; init; }

    /// <summary>
    /// Whether it grows only beyond the mapping's anchors - as their bounds do, carried along the rate two anchors measure -
    /// rather than from the nearer anchor; with one anchor the two are the same distance.
    /// </summary>
    public bool BeyondAnchors { get; init; }

    /// <summary>
    /// Whether each instant has its own - a rounding - rather than one error every instant of the mapping shares, which
    /// is what two instants mapped by one mapping may cancel.
    /// </summary>
    public bool PerInstant { get; init; }

    public bool IsUnknown => Nanoseconds is null && PartsPerMillion is null;

    public static UncertaintyContribution Fixed(string source, UncertaintyCombination combination, double nanoseconds) =>
        new() { Source = source, Combination = combination, Nanoseconds = NonNegative(nanoseconds) };

    /// <summary>A half-width each instant has of its own, such as a rounding: one mapping's two instants never cancel it.</summary>
    public static UncertaintyContribution Rounding(string source, double nanoseconds) =>
        Fixed(source, UncertaintyCombination.Bound, nanoseconds) with { PerInstant = true };

    public static UncertaintyContribution Rate(string source, UncertaintyCombination combination, double partsPerMillion) =>
        new() { Source = source, Combination = combination, PartsPerMillion = NonNegative(partsPerMillion), GrowsWithDistance = true };

    public static UncertaintyContribution Unknown(string source, UncertaintyCombination combination) =>
        new() { Source = source, Combination = combination };

    /// <summary>A rate that grows only beyond a mapping's anchors, and adds nothing between them.</summary>
    public static UncertaintyContribution Beyond(string source, UncertaintyCombination combination, double partsPerMillion) =>
        Rate(source, combination, partsPerMillion) with { BeyondAnchors = true };

    /// <summary>A rate not known, such as a drift no one bounded: nothing at the anchor itself, and unknown away from it.</summary>
    public static UncertaintyContribution UnknownRate(string source, UncertaintyCombination combination) =>
        new() { Source = source, Combination = combination, GrowsWithDistance = true };

    /// <summary>The half-width this contribution adds <paramref name="distanceNanoseconds"/> from the anchor; null when unknown.</summary>
    public double? At(long distanceNanoseconds) => IsUnknown
        ? GrowsWithDistance && distanceNanoseconds == 0 ? 0 : null
        : (Nanoseconds ?? 0) + ((PartsPerMillion ?? 0) * Math.Abs((double)distanceNanoseconds) / 1_000_000);

    private static double NonNegative(double value) => double.IsFinite(value) && value >= 0
        ? value
        : throw new ArgumentOutOfRangeException(nameof(value), value, "An uncertainty is a finite, non-negative half-width or rate.");
}

/// <summary>
/// A symmetric half-width of workspace time, in nanoseconds: the part known only as bounds, added linearly, and the part
/// from independent random contributions, combined in quadrature (§8.2). Never a percentage and never a quality word.
/// </summary>
public readonly record struct TimeUncertainty(double BoundNanoseconds, double RandomNanoseconds)
{
    public static TimeUncertainty Exact { get; } = new(0, 0);

    public double HalfWidthNanoseconds => BoundNanoseconds + RandomNanoseconds;

    /// <summary>
    /// The uncertainty of comparing instants on two independently mapped clocks: their random parts in quadrature and
    /// their bounds added, <c>u_pair = sqrt(rA^2 + rB^2) + sA + sB</c> (§8.2).
    /// </summary>
    public static TimeUncertainty Pair(TimeUncertainty first, TimeUncertainty second) => new(
        first.BoundNanoseconds + second.BoundNanoseconds,
        Math.Sqrt((first.RandomNanoseconds * first.RandomNanoseconds) + (second.RandomNanoseconds * second.RandomNanoseconds)));
}

/// <summary>
/// An affine mapping of one clock's session time into workspace time,
/// <c>workspace = anchor + offset + scale * (session - anchor)</c>, with its uncertainty budget. One anchor supplies an
/// offset and no rate; two separated anchors measure a rate too (§8.2). A mapping is an annotation: it rewrites no
/// timestamp (I9).
/// </summary>
public sealed record ClockMapping
{
    /// <summary>The workspace clock's own mapping: its reference member's session time, exactly.</summary>
    public static ClockMapping Reference { get; } = new() { OffsetNanoseconds = 0, AnchorNanoseconds = 0, Contributions = [] };

    /// <summary>1 when a single anchor set the mapping, which supplies an offset and no rate (§8.2).</summary>
    public double Scale { get; init; } = 1;

    /// <summary>The workspace instant of the anchor, less the anchor: what the mapping adds at its anchor.</summary>
    public required long OffsetNanoseconds { get; init; }

    /// <summary>The session instant the mapping was anchored at, from which a drift grows.</summary>
    public required long AnchorNanoseconds { get; init; }

    /// <summary>The second session instant, when two separated anchors measured the mapping's rate; null for one.</summary>
    public long? SecondAnchorNanoseconds { get; init; }

    /// <summary>
    /// How fast the mapping's error may change from one instant to another, as a fraction of the time between them: its
    /// drifts, and the slope a line through two uncertain anchors may have, which holds between them too. Null when a drift
    /// is unknown. Two instants mapped by one mapping differ by their own difference scaled, and by up to this times it.
    /// </summary>
    public double? Growth => Contributions.Where(contribution => contribution.GrowsWithDistance).Any(contribution => contribution.IsUnknown)
        ? null
        : Contributions.Where(contribution => contribution.GrowsWithDistance).Sum(contribution => contribution.PartsPerMillion!.Value) / 1_000_000;

    /// <summary>The half-width each instant has of its own - its roundings - which two instants never cancel.</summary>
    public double PerInstantNanoseconds => Contributions.Where(contribution => contribution.PerInstant).Sum(contribution => contribution.Nanoseconds ?? 0);

    public required IReadOnlyList<UncertaintyContribution> Contributions { get; init; }

    /// <summary>A session instant in workspace time; at a measured rate, rounded to the nearest nanosecond.</summary>
    public long ToWorkspace(long sessionNanoseconds) => Scale == 1
        ? checked(sessionNanoseconds + OffsetNanoseconds)
        : checked(AnchorNanoseconds + OffsetNanoseconds
            + (long)Math.Round(Scale * checked(sessionNanoseconds - AnchorNanoseconds), MidpointRounding.ToEven));

    /// <summary>The session instant a workspace instant maps from; at a measured rate, rounded to the nearest nanosecond.</summary>
    public long FromWorkspace(long workspaceNanoseconds) => Scale == 1
        ? checked(workspaceNanoseconds - OffsetNanoseconds)
        : checked(AnchorNanoseconds
            + (long)Math.Round(checked(workspaceNanoseconds - AnchorNanoseconds - OffsetNanoseconds) / Scale, MidpointRounding.ToEven));

    /// <summary>How far a session instant is from the nearer anchor, signed: negative before it.</summary>
    public long FromNearerAnchor(long sessionNanoseconds)
    {
        long first = checked(sessionNanoseconds - AnchorNanoseconds);
        if (SecondAnchorNanoseconds is not { } second)
        {
            return first;
        }

        long other = checked(sessionNanoseconds - second);
        return Math.Abs((double)other) < Math.Abs((double)first) ? other : first;
    }

    /// <summary>How far a session instant lies beyond the anchors: 0 at or between them.</summary>
    public long BeyondAnchors(long sessionNanoseconds)
    {
        long second = SecondAnchorNanoseconds ?? AnchorNanoseconds;
        long low = Math.Min(AnchorNanoseconds, second);
        long high = Math.Max(AnchorNanoseconds, second);
        return sessionNanoseconds < low ? checked(low - sessionNanoseconds)
            : sessionNanoseconds > high ? checked(sessionNanoseconds - high)
            : 0;
    }

    /// <summary>
    /// The widest uncertainty of any instant from <paramref name="first"/> to <paramref name="last"/>; null when any is
    /// unknown. It grows away from the nearer anchor and beyond both, so it is widest at an end or, between two anchors,
    /// midway between them.
    /// </summary>
    public TimeUncertainty? WidestUncertainty(long first, long last)
    {
        List<long> instants = [first, last];
        if (SecondAnchorNanoseconds is { } second)
        {
            long middle = checked(AnchorNanoseconds + ((second - AnchorNanoseconds) / 2));
            if (middle > Math.Min(first, last) && middle < Math.Max(first, last))
            {
                instants.Add(middle);
            }
        }

        TimeUncertainty? widest = null;
        foreach (long instant in instants)
        {
            if (UncertaintyAt(instant) is not { } uncertainty)
            {
                return null;
            }

            if (widest is not { } known || uncertainty.HalfWidthNanoseconds > known.HalfWidthNanoseconds)
            {
                widest = uncertainty;
            }
        }

        return widest;
    }

    /// <summary>The uncertainty of <paramref name="sessionNanoseconds"/> in workspace time; null when a contribution is unknown.</summary>
    public TimeUncertainty? UncertaintyAt(long sessionNanoseconds)
    {
        long nearer = FromNearerAnchor(sessionNanoseconds);
        long beyond = BeyondAnchors(sessionNanoseconds);
        double bound = 0;
        double random = 0;
        foreach (UncertaintyContribution contribution in Contributions)
        {
            if (contribution.At(contribution.BeyondAnchors ? beyond : nearer) is not { } halfWidth)
            {
                return null;
            }

            if (contribution.Combination == UncertaintyCombination.Bound)
            {
                bound += halfWidth;
            }
            else
            {
                random += halfWidth * halfWidth;
            }
        }

        return new TimeUncertainty(bound, Math.Sqrt(random));
    }
}

/// <summary>What comparing two instants may say of their order (§8.2).</summary>
public enum TimeOrder
{
    /// <summary>The first is before the second by more than their combined uncertainty.</summary>
    Before = 1,

    /// <summary>The first is after the second by more than their combined uncertainty.</summary>
    After = 2,

    /// <summary>They are closer than their combined uncertainty, so neither order is stated.</summary>
    Ambiguous = 3,

    /// <summary>An instant has no workspace time, or its uncertainty is unknown, so nothing is stated.</summary>
    Unknown = 4,
}

/// <summary>
/// The order of two instants in workspace time: stated only when they are further apart than their combined uncertainty,
/// and their difference withheld whenever that uncertainty is unknown (§8.2, R3, R21).
/// </summary>
public sealed record TimeComparison(TimeOrder Order, long? DifferenceNanoseconds, TimeUncertainty? Uncertainty)
{
    /// <summary>Compares two instants on independently mapped clocks; a null workspace time or uncertainty states nothing.</summary>
    public static TimeComparison Of(long? first, TimeUncertainty? firstUncertainty, long? second, TimeUncertainty? secondUncertainty)
    {
        if (first is not { } a || second is not { } b || firstUncertainty is not { } ua || secondUncertainty is not { } ub)
        {
            return new(TimeOrder.Unknown, null, null);
        }

        long difference = checked(b - a);
        TimeUncertainty pair = TimeUncertainty.Pair(ua, ub);
        return Math.Abs((double)difference) > pair.HalfWidthNanoseconds
            ? new(difference > 0 ? TimeOrder.Before : TimeOrder.After, difference, pair)
            : new(TimeOrder.Ambiguous, difference, pair);
    }

    /// <summary>Compares two instants of one clock, whose mapping's uncertainty is common to both and cancels.</summary>
    public static TimeComparison OnOneClock(long first, long second)
    {
        long difference = checked(second - first);
        return new(difference > 0 ? TimeOrder.Before : difference < 0 ? TimeOrder.After : TimeOrder.Ambiguous, difference, TimeUncertainty.Exact);
    }
}
