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
/// mapping's anchor (a drift), or unknown - which makes the whole uncertainty unknown, never zero and never a default
/// (§8.2, R3, R21).
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

    public bool IsUnknown => Nanoseconds is null && PartsPerMillion is null;

    public static UncertaintyContribution Fixed(string source, UncertaintyCombination combination, double nanoseconds) =>
        new() { Source = source, Combination = combination, Nanoseconds = NonNegative(nanoseconds) };

    public static UncertaintyContribution Rate(string source, UncertaintyCombination combination, double partsPerMillion) =>
        new() { Source = source, Combination = combination, PartsPerMillion = NonNegative(partsPerMillion), GrowsWithDistance = true };

    public static UncertaintyContribution Unknown(string source, UncertaintyCombination combination) =>
        new() { Source = source, Combination = combination };

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
/// An affine mapping of one clock's session time into workspace time, <c>workspace = scale * session + offset</c>, with its
/// uncertainty budget. A mapping is an annotation: it rewrites no timestamp (I9).
/// </summary>
public sealed record ClockMapping
{
    /// <summary>The workspace clock's own mapping: its reference member's session time, exactly.</summary>
    public static ClockMapping Reference { get; } = new() { OffsetNanoseconds = 0, AnchorNanoseconds = 0, Contributions = [] };

    /// <summary>1 when a single anchor set the mapping, which supplies an offset and no rate (§8.2).</summary>
    public double Scale { get; init; } = 1;

    public required long OffsetNanoseconds { get; init; }

    /// <summary>The session instant the mapping was anchored at, from which a drift grows.</summary>
    public required long AnchorNanoseconds { get; init; }

    public required IReadOnlyList<UncertaintyContribution> Contributions { get; init; }

    /// <summary>A session instant in workspace time.</summary>
    public long ToWorkspace(long sessionNanoseconds) => Scale == 1
        ? checked(sessionNanoseconds + OffsetNanoseconds)
        : checked((long)Math.Round(Scale * sessionNanoseconds, MidpointRounding.ToEven) + OffsetNanoseconds);

    /// <summary>The uncertainty of <paramref name="sessionNanoseconds"/> in workspace time; null when a contribution is unknown.</summary>
    public TimeUncertainty? UncertaintyAt(long sessionNanoseconds)
    {
        long distance = checked(sessionNanoseconds - AnchorNanoseconds);
        double bound = 0;
        double random = 0;
        foreach (UncertaintyContribution contribution in Contributions)
        {
            if (contribution.At(distance) is not { } halfWidth)
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
