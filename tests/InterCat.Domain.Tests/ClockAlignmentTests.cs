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

        // The reference is exact, and a rate other than one is applied before the offset.
        Assert.Equal(TimeUncertainty.Exact, ClockMapping.Reference.UncertaintyAt(123));
        Assert.Equal(1_999_900_000, (mapping with { Scale = 1.0001 }).ToWorkspace(-1_000_000_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => UncertaintyContribution.Fixed("negative", UncertaintyCombination.Bound, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => UncertaintyContribution.Rate("not a rate", UncertaintyCombination.Bound, double.NaN));
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
