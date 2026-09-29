using InterCat.Domain;
using Xunit;

namespace InterCat.Domain.Tests;

/// <summary>§8.2's alignment model: a clock mapping's uncertainty and the order two mapped instants may be given.</summary>
public sealed class ClockAlignmentTests
{
    [Fact(DisplayName = "R3: a mapping's uncertainty adds its bounds, combines its random parts in quadrature, and is unknown when any part is")]
    public void AMappingsUncertaintyCombinesItsContributions()
    {
        var mapping = new ClockMapping
        {
            OffsetNanoseconds = 3_000_000_000,
            AnchorNanoseconds = 1_000_000_000,
            Contributions =
            [
                UncertaintyContribution.Fixed("stated anchor", UncertaintyCombination.Bound, 500),
                UncertaintyContribution.Rate("drift bound", UncertaintyCombination.Bound, 50),
                UncertaintyContribution.Fixed("calibration", UncertaintyCombination.Random, 300),
                UncertaintyContribution.Fixed("synchronization", UncertaintyCombination.Random, 400),
            ],
        };

        Assert.Equal(4_000_000_000, mapping.ToWorkspace(1_000_000_000));
        Assert.Equal(new TimeUncertainty(500, 500), mapping.UncertaintyAt(1_000_000_000));

        // Two seconds from the anchor, a 50 ppm drift bound adds 100 µs to the bound part; either side of it alike.
        Assert.Equal(new TimeUncertainty(100_500, 500), mapping.UncertaintyAt(3_000_000_000));
        Assert.Equal(new TimeUncertainty(100_500, 500), mapping.UncertaintyAt(-1_000_000_000));
        Assert.Equal(101_000, mapping.UncertaintyAt(3_000_000_000)!.Value.HalfWidthNanoseconds);

        // One unknown contributor makes the whole unknown - not zero, not a default; an unknown drift adds nothing at the
        // anchor itself.
        ClockMapping unknownDrift = mapping with
        {
            Contributions = [mapping.Contributions[0], UncertaintyContribution.UnknownRate("drift", UncertaintyCombination.Bound)],
        };
        Assert.Equal(new TimeUncertainty(500, 0), unknownDrift.UncertaintyAt(1_000_000_000));
        Assert.Null(unknownDrift.UncertaintyAt(1_000_000_001));
        Assert.Null((mapping with { Contributions = [UncertaintyContribution.Unknown("synchronization", UncertaintyCombination.Random)] })
            .UncertaintyAt(1_000_000_000));

        // The reference is exact, and a rate other than one pivots on the anchor, which stays where the offset puts it.
        Assert.Equal(TimeUncertainty.Exact, ClockMapping.Reference.UncertaintyAt(123));
        Assert.Equal(4_000_000_000, (mapping with { Scale = 1.0001 }).ToWorkspace(1_000_000_000));
        Assert.Equal(1_999_800_000, (mapping with { Scale = 1.0001 }).ToWorkspace(-1_000_000_000));
        Assert.Equal(-1_000_000_000, (mapping with { Scale = 1.0001 }).FromWorkspace(1_999_800_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => UncertaintyContribution.Fixed("negative", UncertaintyCombination.Bound, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => UncertaintyContribution.Rate("not a rate", UncertaintyCombination.Bound, double.NaN));
    }

    [Fact(DisplayName = "R3: two anchors measure a rate: their bound holds between them and grows beyond, and a wander grows from the nearer")]
    public void TwoAnchorsMeasureARate()
    {
        // Anchored at 1 s and 11 s of the session, which are 3 s and 13.0001 s of the workspace: a rate of +10 ppm.
        var mapping = new ClockMapping
        {
            Scale = 10_000_100_000 / 10_000_000_000d,
            OffsetNanoseconds = 2_000_000_000,
            AnchorNanoseconds = 1_000_000_000,
            SecondAnchorNanoseconds = 11_000_000_000,
            Contributions =
            [
                UncertaintyContribution.Fixed("the anchors", UncertaintyCombination.Bound, 1_000),
                UncertaintyContribution.Beyond("the anchors' bound beyond them", UncertaintyCombination.Bound, 0.25),
                UncertaintyContribution.Rate("the rate's wander, twice", UncertaintyCombination.Bound, 1),
            ],
        };
        Assert.Equal((3_000_000_000L, 8_000_050_000L, 13_000_100_000L),
            (mapping.ToWorkspace(1_000_000_000), mapping.ToWorkspace(6_000_000_000), mapping.ToWorkspace(11_000_000_000)));
        Assert.Equal(6_000_000_000, mapping.FromWorkspace(8_000_050_000));

        // At an anchor, the anchors' bound alone; midway, a wander of 1 ppm over 5 s adds 5 µs; 2 s beyond the second anchor,
        // 0.25 ppm of it adds 0.5 µs and the wander 2 µs; 1 s before the first, 0.25 µs and 1 µs.
        Assert.Equal(new TimeUncertainty(1_000, 0), mapping.UncertaintyAt(11_000_000_000));
        Assert.Equal(new TimeUncertainty(6_000, 0), mapping.UncertaintyAt(6_000_000_000));
        Assert.Equal(new TimeUncertainty(3_500, 0), mapping.UncertaintyAt(13_000_000_000));
        Assert.Equal(new TimeUncertainty(2_250, 0), mapping.UncertaintyAt(0));
        Assert.Equal((5_000_000_000L, 2_000_000_000L, -1_000_000_000L), (mapping.FromNearerAnchor(6_000_000_000),
            mapping.FromNearerAnchor(13_000_000_000), mapping.FromNearerAnchor(0)));
        Assert.Equal((0L, 2_000_000_000L, 1_000_000_000L), (mapping.BeyondAnchors(6_000_000_000),
            mapping.BeyondAnchors(13_000_000_000), mapping.BeyondAnchors(0)));

        // Over a span its uncertainty is widest at an end or midway between the anchors; with one anchor, at an end.
        Assert.Equal(new TimeUncertainty(6_000, 0), mapping.WidestUncertainty(0, 12_000_000_000));
        Assert.Equal(new TimeUncertainty(3_000, 0), mapping.WidestUncertainty(0, 3_000_000_000));
        Assert.Equal(new TimeUncertainty(3_500, 0), (mapping with { SecondAnchorNanoseconds = null }).WidestUncertainty(0, 3_000_000_000));

        // A wander no one bounded leaves only the anchors themselves placed with a known uncertainty.
        ClockMapping unbounded = mapping with
        {
            Contributions = [mapping.Contributions[0], mapping.Contributions[1], UncertaintyContribution.UnknownRate("wander", UncertaintyCombination.Bound)],
        };
        Assert.Equal(new TimeUncertainty(1_000, 0), unbounded.UncertaintyAt(1_000_000_000));
        Assert.Equal(new TimeUncertainty(1_000, 0), unbounded.UncertaintyAt(11_000_000_000));
        Assert.Null(unbounded.UncertaintyAt(6_000_000_000));
        Assert.Null(unbounded.WidestUncertainty(1_000_000_000, 11_000_000_000));
    }

    [Fact(DisplayName = "R21: two clocks aligned through one compare as that alignment allows: its drift between them, and their roundings")]
    public void ClocksAlignedThroughOneCompareAsItAllows()
    {
        Guid bClock = Guid.NewGuid(), cClock = Guid.NewGuid(), dClock = Guid.NewGuid();

        // B is on the reference's clock within 1 ms, drifting 10 ppm; C and D are on B's, within 2 µs with a 1 ns rounding,
        // and within 3 µs. C's 0 s and D's -1 s are both B's 1 s, which is the reference's 6 s.
        var b = new ClockMapping
        {
            OffsetNanoseconds = 5_000_000_000,
            AnchorNanoseconds = 0,
            Contributions =
            [
                UncertaintyContribution.Fixed("b's anchor", UncertaintyCombination.Bound, 1_000_000),
                UncertaintyContribution.Rate("b's drift", UncertaintyCombination.Bound, 10),
            ],
        };
        var c = new ClockMapping
        {
            OffsetNanoseconds = 1_000_000_000,
            AnchorNanoseconds = 0,
            Contributions = [UncertaintyContribution.Fixed("c's anchor", UncertaintyCombination.Bound, 2_000), UncertaintyContribution.Rounding("c's rounding", 1)],
        };
        var d = new ClockMapping
        {
            OffsetNanoseconds = 2_000_000_000,
            AnchorNanoseconds = 0,
            Contributions = [UncertaintyContribution.Fixed("d's anchor", UncertaintyCombination.Bound, 3_000)],
        };
        var chainC = new ClockChain([new(cClock, c), new(bClock, b)]);
        var chainD = new ClockChain([new(dClock, d), new(bClock, b)]);
        Assert.Equal(6_000_000_000, chainC.ToWorkspace(0));
        Assert.Equal(0, chainC.FromWorkspace(6_000_000_000));
        Assert.Equal(1_012_001.02001, chainC.UncertaintyAt(0).Uncertainty!.Value.HalfWidthNanoseconds, 6);

        // B's alignment, shared, moves both alike: only its 10 ppm over the 5 µs the two may lie apart adds to their own sides.
        TimeComparison tie = ClockChain.Compare(chainC, 0, chainD, -1_000_000_000);
        Assert.Equal((TimeOrder.Ambiguous, 0L), (tie.Order, tie.DifferenceNanoseconds!.Value));
        Assert.Equal(5_001.05001, tie.Uncertainty!.Value.HalfWidthNanoseconds, 6);
        Assert.Equal(TimeOrder.Before, ClockChain.Compare(chainC, 0, chainD, -1_000_000_000 + 5_002).Order);

        // A rounding of the shared link is each instant's own, so it counts twice; an unknown drift there leaves no order.
        ClockChain roundedC = new([new(cClock, c), new(bClock, b with { Contributions = [.. b.Contributions, UncertaintyContribution.Rounding("b's rounding", 1)] })]);
        ClockChain roundedD = new([new(dClock, d), roundedC.Links[1]]);
        Assert.Equal(5_003.05001, ClockChain.Compare(roundedC, 0, roundedD, -1_000_000_000).Uncertainty!.Value.HalfWidthNanoseconds, 6);
        ClockMapping unbounded = b with { Contributions = [b.Contributions[0], UncertaintyContribution.UnknownRate("b's drift", UncertaintyCombination.Bound)] };
        Assert.Equal(TimeOrder.Unknown, ClockChain.Compare(new([new(cClock, c), new(bClock, unbounded)]), 0,
            new([new(dClock, d), new(bClock, unbounded)]), -1_000_000_000).Order);
        Assert.Equal(new ChainUncertainty(null, bClock), new ClockChain([new(cClock, c), new(bClock, unbounded)]).UncertaintyAt(0));

        // Against the reference nothing is shared; one clock's two instants are ordered on it, exactly.
        Assert.Equal(1_012_001.02001, ClockChain.Compare(ClockChain.Reference, 6_000_000_000, chainC, 0).Uncertainty!.Value.HalfWidthNanoseconds, 6);
        Assert.Equal(new TimeComparison(TimeOrder.Before, 5, TimeUncertainty.Exact), ClockChain.Compare(chainC, 0, chainC, 5));
    }

    [Fact(DisplayName = "R21: two clocks' instants are ordered only beyond their combined uncertainty, and never when it is unknown")]
    public void TwoClocksInstantsAreOrderedOnlyBeyondTheirUncertainty()
    {
        var first = new TimeUncertainty(1_000, 300);
        var second = new TimeUncertainty(500, 400);

        // u_pair = sqrt(300^2 + 400^2) + 1,000 + 500 = 2,000 ns.
        Assert.Equal(new TimeUncertainty(1_500, 500), TimeUncertainty.Pair(first, second));
        Assert.Equal(TimeOrder.Before, TimeComparison.Of(0, first, 2_001, second).Order);
        Assert.Equal(TimeOrder.After, TimeComparison.Of(2_001, first, 0, second).Order);
        Assert.Equal((TimeOrder.Ambiguous, 2_000L), Pair(TimeComparison.Of(0, first, 2_000, second)));
        Assert.Equal((TimeOrder.Ambiguous, -1_999L), Pair(TimeComparison.Of(1_999, first, 0, second)));

        // An unknown uncertainty, or an instant with no workspace time, states nothing - not even the difference.
        Assert.Equal(new TimeComparison(TimeOrder.Unknown, null, null), TimeComparison.Of(0, null, 1_000_000_000, second));
        Assert.Equal(new TimeComparison(TimeOrder.Unknown, null, null), TimeComparison.Of(null, first, 0, second));

        // Two instants of one clock share its mapping, whose uncertainty cancels: they are ordered exactly, and a tie is no order.
        Assert.Equal(new TimeComparison(TimeOrder.Before, 1, TimeUncertainty.Exact), TimeComparison.OnOneClock(5, 6));
        Assert.Equal(new TimeComparison(TimeOrder.Ambiguous, 0, TimeUncertainty.Exact), TimeComparison.OnOneClock(5, 5));
    }

    private static (TimeOrder, long?) Pair(TimeComparison comparison) => (comparison.Order, comparison.DifferenceNanoseconds);
}
