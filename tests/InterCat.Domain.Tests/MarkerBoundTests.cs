using Xunit;

namespace InterCat.Domain.Tests;

/// <summary>
/// What shared markers bound of two captures' clock offset (§8.2's shared-marker mode, ADR-108): each ordering of a record of
/// one capture after a record of the other bounds the offset from one side; both ways bound it from both, under a stated
/// drift bound; one way alone, or orderings that leave no offset, align nothing.
/// </summary>
public sealed class MarkerBoundTests
{
    /// <summary>How far the member's clock reads behind the reference's: the true offset each bound must hold.</summary>
    private const long Truth = 5_000_000;

    private static readonly System.Globalization.CultureInfo Invariant = System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>
    /// The member started an operation 300 µs after a parent of the reference's and another 900 µs after one; the reference
    /// started one 200 µs after a parent of the member's and another 700 µs after one.
    /// </summary>
    private static readonly MarkerOrdering[] Orderings =
    [
        new(1_000_000_000 - Truth + 300_000, 1_000_000_000, MarkerOrder.MemberAfter),
        new(1_500_000_000 - Truth + 900_000, 1_500_000_000, MarkerOrder.MemberAfter),
        new(2_000_000_000, 2_000_000_000 + Truth + 200_000, MarkerOrder.ReferenceAfter),
        new(2_200_000_000, 2_200_000_000 + Truth + 700_000, MarkerOrder.ReferenceAfter),
    ];

    [Fact(DisplayName = "§8.2: shared markers both ways bound two clocks' offset from both sides by the tightest ordering each way, the true offset within it, its anchor midway between those two")]
    public void MarkersBothWaysBoundTheOffset()
    {
        MarkerBound bound = MarkerBound.Of(Orderings, 0, Invariant, out string? refusal)!;
        Assert.Null(refusal);

        // From below by 4.7 ms, the member's start 300 µs after its parent; from above by 5.2 ms, the reference's 200 µs after.
        Assert.Equal((Truth - 50_000, 250_000L, 2, 2), (bound.OffsetNanoseconds, bound.WithinNanoseconds, bound.MemberAfter, bound.ReferenceAfter));
        Assert.InRange(Truth, bound.OffsetNanoseconds - bound.WithinNanoseconds, bound.OffsetNanoseconds + bound.WithinNanoseconds);
        Assert.Equal(((1_000_000_000 - Truth + 300_000) + 2_000_000_000) / 2, bound.AnchorNanoseconds);
        Assert.Equal(bound.AnchorNanoseconds + bound.OffsetNanoseconds, bound.ReferenceNanoseconds);

        // Ends an odd number of nanoseconds apart: the offset rounded down, the half-width reaching the farther end - 250,000
        // ns above it, 249,999 below - so both ends stay within.
        MarkerBound odd = MarkerBound.Of([new(0, 4_700_001, MarkerOrder.MemberAfter), new(0, 5_200_000, MarkerOrder.ReferenceAfter)], 0,
            Invariant, out _)!;
        Assert.Equal((4_950_000L, 250_000L), (odd.OffsetNanoseconds, odd.WithinNanoseconds));
    }

    [Fact(DisplayName = "§8.2: a stated drift widens each ordering by the drift over its distance from the anchor, rounded outward, so two clocks drifting apart within it stay within the bound")]
    public void ADriftWidensEachOrdering()
    {
        // 10 ppm over the tightest orderings' 502.35 ms from the anchor: 5,023.5 ns each way, rounded outward.
        MarkerBound bound = MarkerBound.Of(Orderings, 10, Invariant, out string? refusal)!;
        Assert.Null(refusal);
        Assert.Equal((Truth - 50_000, 255_024L), (bound.OffsetNanoseconds, bound.WithinNanoseconds));

        // Clocks whose offset grows by 10 ppm of the member's time: each ordering true of its own instant, the bound holds the
        // offset at the anchor.
        long anchor = bound.AnchorNanoseconds;
        long At(long member) => Truth + (long)Math.Round((member - anchor) * 10e-6);
        MarkerOrdering[] drifting =
        [
            new(1_000_000_000, 1_000_000_000 + At(1_000_000_000) - 300_000, MarkerOrder.MemberAfter),
            new(2_000_000_000, 2_000_000_000 + At(2_000_000_000) + 200_000, MarkerOrder.ReferenceAfter),
        ];
        MarkerBound drifted = MarkerBound.Of(drifting, 10, Invariant, out refusal)!;
        Assert.Null(refusal);
        Assert.InRange(At(drifted.AnchorNanoseconds), drifted.OffsetNanoseconds - drifted.WithinNanoseconds,
            drifted.OffsetNanoseconds + drifted.WithinNanoseconds);
    }

    [Fact(DisplayName = "R3: markers one way only bound two clocks' offset from one side, and markers that leave no offset contradict one another - neither aligns anything, the missing side never defaulted, and each says why")]
    public void NothingIsAlignedFromOneSideOrAContradiction()
    {
        Assert.Null(MarkerBound.Of([], 0, Invariant, out string? refusal));
        Assert.Contains("share no operation", refusal, StringComparison.Ordinal);

        Assert.Null(MarkerBound.Of([.. Orderings.Where(ordering => ordering.Order == MarkerOrder.MemberAfter)], 0, Invariant, out refusal));
        Assert.Contains("one way only", refusal, StringComparison.Ordinal);
        Assert.Null(MarkerBound.Of([.. Orderings.Where(ordering => ordering.Order == MarkerOrder.ReferenceAfter)], 0, Invariant, out refusal));
        Assert.Contains("one way only", refusal, StringComparison.Ordinal);

        // The member's start 6 ms past the reference's parent, yet the reference's 5 ms past the member's: no offset is both.
        MarkerOrdering[] contradicting =
        [
            new(1_000_000_000, 1_000_000_000 + 6_000_000, MarkerOrder.MemberAfter),
            new(1_100_000_000, 1_100_000_000 + 5_000_000, MarkerOrder.ReferenceAfter),
        ];
        Assert.Null(MarkerBound.Of(contradicting, 1, Invariant, out refusal));
        Assert.Contains("leave no offset between the two clocks under a drift of 1 ppm", refusal, StringComparison.Ordinal);
        Assert.Contains("nothing is aligned", refusal, StringComparison.Ordinal);

        Assert.Throws<ArgumentOutOfRangeException>(() => MarkerBound.Of(Orderings, -1, Invariant, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => MarkerBound.Of(Orderings, double.NaN, Invariant, out _));
    }
}
