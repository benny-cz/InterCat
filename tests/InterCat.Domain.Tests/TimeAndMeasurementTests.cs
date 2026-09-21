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
