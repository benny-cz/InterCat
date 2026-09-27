using System.Globalization;
using InterCat.Analysis;
using InterCat.Application;
using Xunit;

namespace InterCat.Application.Tests;

public sealed class OperationTextTests
{
    [Fact]
    public void ADurationKeepsItsFiguresInTheUnitItNeeds()
    {
        CultureInfo invariant = CultureInfo.InvariantCulture;
        Assert.Equal("0 ns", OperationText.Duration(0, invariant));
        Assert.Equal("999 ns", OperationText.Duration(999, invariant));
        Assert.Equal("1.0 µs", OperationText.Duration(1_000, invariant));
        Assert.Equal("4.8 µs", OperationText.Duration(4_800, invariant));
        Assert.Equal("26.1 µs", OperationText.Duration(26_100, invariant));
        Assert.Equal("99.9 µs", OperationText.Duration(99_940, invariant));
        Assert.Equal("100 µs", OperationText.Duration(99_950, invariant));
        Assert.Equal("457 µs", OperationText.Duration(456_600, invariant));

        // A value that would round to 1,000 of a unit is written in the next one.
        Assert.Equal("999 µs", OperationText.Duration(999_499, invariant));
        Assert.Equal("1.0 ms", OperationText.Duration(999_500, invariant));
        Assert.Equal("12.3 ms", OperationText.Duration(12_345_678, invariant));
        Assert.Equal("1.2 s", OperationText.Duration(1_234_000_000, invariant));
        Assert.Equal("4,8 µs", OperationText.Duration(4_800, CultureInfo.GetCultureInfo("cs-CZ")));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => OperationText.Duration(-1, invariant));
    }

    [Fact]
    public void EveryCallStateHasWordsAndNoOtherValueDoes()
    {
        Assert.Equal(
            ["completed", "open at capture end", "start not observed", "no activity id", "ambiguous: its activity id was reused before its stop"],
            Enum.GetValues<RpcCallState>().Select(OperationText.State));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => OperationText.State((RpcCallState)99));
    }
}
