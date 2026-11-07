using System.Globalization;

namespace InterCat.Domain;

/// <summary>Which capture's record a shared marker says was taken after the other's (ADR-108).</summary>
public enum MarkerOrder
{
    /// <summary>The member's record followed the reference's: an operation the member started under one the reference started.</summary>
    MemberAfter = 1,

    /// <summary>The reference's record followed the member's: an operation the reference started under one the member started.</summary>
    ReferenceAfter = 2,
}

/// <summary>
/// One ordering two captures' records give their clocks (§8.2, ADR-108): a record of one capture known to be taken after a
/// record of the other - an operation's start, which the SDK says after the identity it starts under existed, and that
/// identity's own start, said before the identity could be passed on. Each instant is its own capture's session time.
/// </summary>
public readonly record struct MarkerOrdering(long MemberNanoseconds, long ReferenceNanoseconds, MarkerOrder Order);

/// <summary>
/// What shared markers bound of the offset between two captures' clocks (§8.2's shared-marker mode, ADR-108): at an anchor
/// instant of the member's session time, the same instant of the reference's lies within <see cref="WithinNanoseconds"/> of
/// <see cref="ReferenceNanoseconds"/>. Every ordering holds under a stated drift bound, which widens each by the drift over
/// its distance from the anchor; orderings both ways are needed, as one way bounds the offset from one side only.
/// </summary>
public sealed record MarkerBound(long AnchorNanoseconds, long OffsetNanoseconds, long WithinNanoseconds, int MemberAfter, int ReferenceAfter)
{
    /// <summary>The anchor's instant in the reference's session time.</summary>
    public long ReferenceNanoseconds => checked(AnchorNanoseconds + OffsetNanoseconds);

    /// <summary>
    /// The bound <paramref name="orderings"/> set under <paramref name="driftPartsPerMillion"/> - a bound on how fast the two
    /// clocks drift apart - or null, with why in <paramref name="refusal"/>: orderings one way only bound the offset from one
    /// side, which orders nothing, and orderings that leave no offset contradict one another - a clock stepped, drifted faster
    /// than stated, or a record was misread.
    /// </summary>
    public static MarkerBound? Of(IReadOnlyList<MarkerOrdering> orderings, double driftPartsPerMillion, IFormatProvider culture,
        out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(orderings);
        ArgumentNullException.ThrowIfNull(culture);
        if (!double.IsFinite(driftPartsPerMillion) || driftPartsPerMillion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(driftPartsPerMillion), driftPartsPerMillion, "A drift bound is a non-negative rate.");
        }

        MarkerOrdering[] after = [.. orderings.Where(ordering => ordering.Order == MarkerOrder.MemberAfter)];
        MarkerOrdering[] before = [.. orderings.Where(ordering => ordering.Order == MarkerOrder.ReferenceAfter)];
        if (after.Length == 0 || before.Length == 0)
        {
            refusal = after.Length + before.Length == 0
                ? "The two captures share no operation: none of either's was started under one the other recorded, so nothing "
                    + "orders their clocks."
                : "The shared operations order the two captures one way only - each bounds their clocks' offset from one side, "
                    + "which orders nothing. An exchange both ways is needed: an operation each side starts under one the other started.";
            return null;
        }

        // Each ordering bounds the offset at its member instant m by r - m: from below where the member's record followed, from
        // above where the reference's did. The anchor lies midway between the tightest of each, where the drift widens them least.
        MarkerOrdering lowest = after.MaxBy(Gap);
        MarkerOrdering highest = before.MinBy(Gap);
        long anchor = (long)(((Int128)lowest.MemberNanoseconds + highest.MemberNanoseconds) / 2);
        double rate = driftPartsPerMillion / 1_000_000;
        double lower = after.Max(ordering => Gap(ordering) - (rate * Math.Abs((double)ordering.MemberNanoseconds - anchor)));
        double upper = before.Min(ordering => Gap(ordering) + (rate * Math.Abs((double)ordering.MemberNanoseconds - anchor)));
        if (lower >= upper)
        {
            refusal = string.Create(culture, $"The shared operations leave no offset between the two clocks under a drift of ")
                + string.Create(culture, $"{driftPartsPerMillion:0.###} ppm: one places the reference at least {Seconds(lower)} past the ")
                + string.Create(culture, $"member, another at most {Seconds(upper)}. A clock stepped, drifted faster than stated, or a ")
                + "record was misread; nothing is aligned.";
            return null;
        }

        // The offset midway, and a half-width reaching both ends, each rounded outward so the bound never reads narrower.
        long low = (long)Math.Floor(lower);
        long high = (long)Math.Ceiling(upper);
        long offset = (long)(((Int128)low + high) / 2);
        refusal = null;
        return new(anchor, offset, Math.Max(offset - low, high - offset), after.Length, before.Length);

        static double Gap(MarkerOrdering ordering) => (double)ordering.ReferenceNanoseconds - ordering.MemberNanoseconds;

        string Seconds(double nanoseconds) => string.Create(culture, $"{nanoseconds / 1_000_000_000:0.000000000} s");
    }
}
