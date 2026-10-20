using System.Globalization;
using InterCat.Domain;
using Xunit;

namespace InterCat.Domain.Tests;

public sealed class SessionTimeTextTests
{
    [Theory(DisplayName = "§6.8: a range of time states its two ends to one precision, the finer end's, at least milliseconds")]
    [InlineData(100_000, 103_000, "0.000100 s to 0.000103 s")]
    [InlineData(1_000_100_000, 1_000_105_000, "1.000100 s to 1.000105 s")]
    [InlineData(0, 2_000_000_000, "0.000 s to 2.000 s")]
    [InlineData(12_345_000_000, 12_345_600_000, "12.3450 s to 12.3456 s")]
    [InlineData(100_000, 503_100, "0.0001000 s to 0.0005031 s")]
    [InlineData(1, 1_000_000_000, "0.000000001 s to 1.000000000 s")]
    [InlineData(-1_500_000, 1_500_000, "-0.0015 s to 0.0015 s")]
    public void ARangeStatesBothEndsAlike(long from, long to, string said) =>
        Assert.Equal(said, SessionTimeText.Range(from, to, CultureInfo.InvariantCulture));

    [Fact(DisplayName = "§6.8: a range's digits are the reader's culture's, an instant on its own keeps its own precision")]
    public void ARangeReadsInTheReadersCulture()
    {
        Assert.Equal("0,000100 s to 0,000103 s", SessionTimeText.Range(100_000, 103_000, CultureInfo.GetCultureInfo("de-DE")));
        Assert.Equal(("0.0001 s", "0.000103 s"),
            (SessionTimeText.Seconds(100_000, CultureInfo.InvariantCulture), SessionTimeText.Seconds(103_000, CultureInfo.InvariantCulture)));
    }
}
