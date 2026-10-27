using System.Globalization;
using InterCat.Analysis;
using InterCat.Application;
using InterCat.Domain;
using Xunit;

namespace InterCat.Application.Tests;

/// <summary>
/// A comparison's words for the values its fixtures seldom reach (`contracts/comparison-v1.md`): rates and byte rates at
/// each scale, and byte measures of sends that recorded no size, a lower bound or no send at all, each said rather than
/// read as a zero.
/// </summary>
public sealed class ComparisonWordsTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static readonly MechanismCoverage Covered = new(Mechanism.Tcp, CoverageState.Covered, "covered");

    [Fact(DisplayName = "R3: a rate is said at its scale, a byte rate under a byte a second as such, and sends that recorded no size as unmeasured, never a zero")]
    public void RatesAndUnmeasuredSendsAreSaid()
    {
        double[] rates = [0, 150, 12.5, 1.5, 0.05];
        Assert.Equal(["0", "150", "12.5", "1.50", "0.050"], rates.Select(rate => ComparisonText.Rate(rate, Invariant)));
        Assert.Equal(("0 B/s", "under 1 B/s", ByteSizeText.Of(2_048, Invariant) + "/s"),
            (ComparisonText.ByteRate(0, Invariant), ComparisonText.ByteRate(0.5, Invariant), ComparisonText.ByteRate(2_048, Invariant)));

        // Over a second, sends none of which recorded a size are counted, never a value; no send at all is nothing sent, and a
        // value some sends left out of is a lower bound.
        var second = new TimeRange(0, 10_000_000);
        Assert.Equal(["3 unmeasured sends", "1 unmeasured send", "nothing sent", "≥100 B · ≥100 B/s", "100 B · 100 B/s"],
            new ComparedMeasure[]
            {
                new(3, null, 0, 3, Covered), new(1, null, 0, 1, Covered), new(0, 0, 0, 0, Covered), new(2, 100, 1, 1, Covered),
                new(1, 100, 1, 0, Covered),
            }.Select(measure => ComparisonText.Value(measure, second, ComparisonMetric.BytesSent, Invariant)));
        Assert.Equal(["0", "4 · 4.00/s"], new ComparedMeasure[] { new(0, 0, 0, 0, Covered), new(4, 4, 4, 0, Covered) }
            .Select(measure => ComparisonText.Value(measure, second, ComparisonMetric.Records, Invariant)));
    }
}
