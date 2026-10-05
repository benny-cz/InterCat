using InterCat.Domain;
using Xunit;

namespace InterCat.Domain.Tests;

public sealed class TimeAndMeasurementTests
{
    [Fact(DisplayName = "I3: time ranges are half-open")]
    public void TimeRangeIsHalfOpen()
    {
        var range = new TimeRange(10, 20);

        Assert.True(range.Contains(10));
        Assert.True(range.Contains(19));
        Assert.False(range.Contains(20));
    }

    [Fact(DisplayName = "I3: a time range's span is a tick count, and a wider interval is held around an instant")]
    public void ATimeRangesSpanIsATickCount()
    {
        // The widest range spans as many ticks as a count can say; one tick wider is refused where it is made, rather than
        // overflowing wherever it is later measured.
        Assert.Equal(long.MaxValue, new TimeRange(long.MinValue, -1).SpanTicks);
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeRange(long.MinValue, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeRange(-1, long.MaxValue));
        Assert.True(TimeRange.TryCreate(0, long.MaxValue, out TimeRange upper));
        Assert.Equal(new TimeRange(0, long.MaxValue), upper);
        Assert.False(TimeRange.TryCreate(long.MinValue, long.MaxValue, out _));
        Assert.False(TimeRange.TryCreate(long.MinValue, 0, out _));
        Assert.False(TimeRange.TryCreate(5, 5, out _));
        Assert.False(TimeRange.TryCreate(6, 5, out _));
        Assert.False(TimeRange.TryCreate(long.MaxValue, long.MinValue, out _));

        // Held around an instant, a range it holds is itself and an empty one is none; a wider one keeps the widest part,
        // centred on the instant as far as its own bounds allow, or else reaching from the bound nearest it.
        Assert.Equal(new TimeRange(10, 20), TimeRange.Around(10, 20, 1_000));
        Assert.Null(TimeRange.Around(20, 20, 0));
        Assert.Null(TimeRange.Around(long.MaxValue, long.MinValue, 0));
        Assert.Equal(new TimeRange(-(1L << 62), (1L << 62) - 1), TimeRange.Around(long.MinValue, long.MaxValue, 0));
        Assert.Equal(new TimeRange(1_000 - (1L << 62), 999 + (1L << 62)), TimeRange.Around(long.MinValue, long.MaxValue, 1_000));
        Assert.Equal(new TimeRange(100 - long.MaxValue, 100), TimeRange.Around(long.MinValue, 100, 0));
        Assert.Equal(new TimeRange(-100, long.MaxValue - 100), TimeRange.Around(-100, long.MaxValue, 0));
        Assert.Equal(new TimeRange(long.MinValue, -1), TimeRange.Around(long.MinValue, 10, long.MinValue));
        Assert.Equal(new TimeRange(0, long.MaxValue), TimeRange.Around(long.MinValue, long.MaxValue, long.MaxValue));
    }

    [Fact(DisplayName = "R3: unknown and observed zero remain distinct")]
    public void UnknownAndZeroAreDistinct()
    {
        var unknown = new TypedMeasurement(null, MeasurementUnit.Bytes, Metric.BytesSent, AccountingSide.SendSide, "fixture", QualityLevel.UnknownQuality, ByteDomain.TransportObserved);
        var zero = new TypedMeasurement(0, MeasurementUnit.Bytes, Metric.BytesSent, AccountingSide.SendSide, "fixture", QualityLevel.Proven, ByteDomain.TransportObserved);

        Assert.False(unknown.IsKnown);
        Assert.True(zero.IsKnown);
        Assert.Equal(0, zero.Value);
    }

    [Fact(DisplayName = "R5: initial normative wire codes stay stable")]
    public void NormativeWireCodesAreStable()
    {
        Assert.Equal(3, (int)Mechanism.Tcp);
        Assert.Equal(6, (int)Mechanism.NamedPipe);
        Assert.Equal(99, (int)Mechanism.UnknownMechanism);
        Assert.Equal(5, (int)CoverageState.UnknownCoverage);
        Assert.Equal(4, (int)CapabilityTier.Unsupported);
    }
}
