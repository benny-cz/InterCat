using InterCat.Analysis;
using InterCat.Domain;
using Xunit;

namespace InterCat.Analysis.Tests;

/// <summary>An accounting side as a person reads it: the label the window uses, and what it measures after it (R5).</summary>
public sealed class AccountingTextTests
{
    [Fact(DisplayName = "R5: an accounting reads as the window labels it, its meaning after the label, an unknown one by its number")]
    public void AnAccountingReadsInWords()
    {
        Assert.Equal(["sender-accounted", "receiver-accounted", "endpoint activity", "canonical owner"],
            Enum.GetValues<AccountingSide>().Select(MetricCompatibility.Label));
        Assert.All(Enum.GetValues<AccountingSide>(), side =>
            Assert.StartsWith(MetricCompatibility.Label(side) + ": ", MetricCompatibility.Describe(side), StringComparison.Ordinal));
        Assert.Equal("accounting 9", MetricCompatibility.Label((AccountingSide)9));
        Assert.Equal("accounting 9", MetricCompatibility.Describe((AccountingSide)9));
    }
}
