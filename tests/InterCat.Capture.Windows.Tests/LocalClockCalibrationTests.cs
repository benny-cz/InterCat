using System.Diagnostics;
using InterCat.Capture.Windows;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

/// <summary>This machine's clock against its wall clock, and its boot, as a live capture here records them.</summary>
public sealed class LocalClockCalibrationTests
{
    [Fact(DisplayName = "R3: a clock reading pairs the counter with the precise wall clock, bracketed within a measured interval")]
    public void AReadingPairsTheCounterWithTheWallClock()
    {
        long before = Stopwatch.GetTimestamp();
        DateTime wallBefore = DateTime.UtcNow;
        (long ticks, long fileTime, long halfInterval) = LocalClockReading.Read();
        DateTime wallAfter = DateTime.UtcNow;
        long after = Stopwatch.GetTimestamp();

        // The pair's counter reading falls between the reads around it, its wall-clock reading is today's, and its bound
        // is at least one counter tick: two reads on one tick still leave that tick unknown.
        Assert.InRange(ticks, before, after);
        Assert.InRange(DateTime.FromFileTimeUtc(fileTime), wallBefore.AddSeconds(-1), wallAfter.AddSeconds(1));
        Assert.InRange(halfInterval, 1, Stopwatch.Frequency / 1_000);
        Assert.Equal(100, LocalClockReading.WallClockResolutionNanoseconds);
    }

    [Fact(DisplayName = "R22: a boot is named by a token kept only until the machine restarts, and its count is no identity")]
    public void ABootIsNamedByItsToken()
    {
        // Reading never mints: two reads agree, whether or not a capture of this boot kept a token.
        Assert.Equal(LocalBootIdentity.Token(mint: false), LocalBootIdentity.Token(mint: false));
        Assert.Equal(@"SOFTWARE\InterCat.Boot", LocalBootIdentity.TokenKey);

        // Windows counts its boots; the count is kept for people, and read the same twice within one boot.
        long? count = LocalBootIdentity.Count();
        Assert.NotNull(count);
        Assert.Equal(count, LocalBootIdentity.Count());
    }
}
