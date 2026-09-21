using InterCat.Domain;
using Xunit;

namespace InterCat.Domain.Tests;

public sealed class ClockContractTests
{
    [Fact(DisplayName = "I8: native clock, local time and workspace time remain distinct")]
    public void ClockRepresentationsRemainDistinct()
    {
        IdentityScenario scenario = IdentityContractTests.LoadScenario();
        IdentityExpected expected = IdentityContractTests.LoadExpected();
        SourceClockDescriptor clock = CreateClock(scenario);
        var native = new NativeTimestamp(scenario.Clock, TimestampEncoding.Qpc, scenario.QpcSample);

        TimestampConversionResult converted = SourceClockMath.ConvertToSession(clock, native);
        var source = new ObservationTime(native, converted.SessionTime!.Value, WallClock: null);
        WorkspaceTimestamp workspace = SourceClockMath.Align(
            source.LocalRelative,
            scaleNumerator: 1,
            scaleDenominator: 1,
            offsetNanoseconds: 50,
            alignmentRevision: 3);

        Assert.True(converted.IsValid);
        Assert.Equal(expected.QpcSampleSessionNanoseconds, source.LocalRelative.Nanoseconds);
        Assert.Equal(scenario.QpcSample, source.Native.Ticks);
        Assert.Equal(TimestampEncoding.Qpc, source.Native.Encoding);
        Assert.Equal(scenario.Clock, source.Native.ClockId);
        Assert.Equal(1_234_550, workspace.Nanoseconds);
        Assert.Equal(3u, workspace.AlignmentRevision);
    }

    [Fact(DisplayName = "I9: alignment revisions never rewrite source time")]
    public void AlignmentRevisionLeavesSourceTimeUnchanged()
    {
        IdentityScenario scenario = IdentityContractTests.LoadScenario();
        var source = new ObservationTime(
            new NativeTimestamp(scenario.Clock, TimestampEncoding.Qpc, scenario.QpcSample),
            new SessionTimestamp(1_234_500),
            new WallClockEstimate(638_000_000_000_000_000, 20));

        WorkspaceTimestamp first = SourceClockMath.Align(source.LocalRelative, 1, 1, 10, 1);
        WorkspaceTimestamp revised = SourceClockMath.Align(source.LocalRelative, 1, 1, 500, 2);

        Assert.Equal(scenario.QpcSample, source.Native.Ticks);
        Assert.Equal(1_234_500, source.LocalRelative.Nanoseconds);
        Assert.NotEqual(first, revised);
    }

    [Fact(DisplayName = "P10: applying alignment creates a projection and leaves source time unchanged")]
    public void AlignmentProducesProjectionOnly()
    {
        IdentityScenario scenario = IdentityContractTests.LoadScenario();
        var native = new NativeTimestamp(scenario.Clock, TimestampEncoding.Qpc, scenario.QpcSample);
        var local = new SessionTimestamp(1_234_500);
        var source = new ObservationTime(native, local, WallClock: null);

        WorkspaceTimestamp aligned = SourceClockMath.Align(local, 1_000_001, 1_000_000, -50, 9);

        Assert.Equal(native, source.Native);
        Assert.Equal(local, source.LocalRelative);
        Assert.Equal(9u, aligned.AlignmentRevision);
    }

    [Fact(DisplayName = "Clock contract quarantines mismatched and implausible timestamps")]
    public void InvalidTimestampIsQuarantined()
    {
        IdentityScenario scenario = IdentityContractTests.LoadScenario();
        SourceClockDescriptor clock = CreateClock(scenario);
        TimestampConversionResult wrongClock = SourceClockMath.ConvertToSession(
            clock,
            new NativeTimestamp(ClockId.New(), TimestampEncoding.Qpc, scenario.QpcSample));
        TimestampConversionResult implausible = SourceClockMath.ConvertToSession(
            clock,
            new NativeTimestamp(
                scenario.Clock,
                TimestampEncoding.Qpc,
                scenario.QpcEpoch + (20 * scenario.QpcFrequency)));

        Assert.Equal(TimestampQuarantineReason.ClockMismatch, wrongClock.QuarantineReason);
        Assert.Equal(TimestampQuarantineReason.ImplausibleDistance, implausible.QuarantineReason);
        Assert.Null(wrongClock.SessionTime);
        Assert.Null(implausible.SessionTime);
    }

    [Theory(DisplayName = "Clock contract uses explicit nearest-even rounding")]
    [InlineData(5, 2)]
    [InlineData(15, 8)]
    [InlineData(25, 12)]
    [InlineData(-5, -2)]
    public void ConversionUsesNearestEven(long nativeDelta, long expectedNanoseconds)
    {
        var host = new HostId(Guid.Parse("11111111-1111-4111-8111-111111111111"));
        var clockId = new ClockId(Guid.Parse("55555555-5555-4555-8555-555555555555"));
        var clock = new SourceClockDescriptor(
            clockId,
            host,
            SourceClockKind.Monotonic,
            TimestampEncoding.Qpc,
            ticksPerSecond: 2_000_000_000,
            captureEpochNativeTicks: 100,
            TimestampRounding.NearestEven,
            maximumAbsoluteSessionNanoseconds: 1_000);

        TimestampConversionResult result = SourceClockMath.ConvertToSession(
            clock,
            new NativeTimestamp(clockId, TimestampEncoding.Qpc, 100 + nativeDelta));

        Assert.Equal(expectedNanoseconds, result.SessionTime!.Value.Nanoseconds);
    }

    private static SourceClockDescriptor CreateClock(IdentityScenario scenario) =>
        new(
            scenario.Clock,
            scenario.HostA,
            SourceClockKind.Monotonic,
            TimestampEncoding.Qpc,
            scenario.QpcFrequency,
            scenario.QpcEpoch,
            TimestampRounding.NearestEven,
            scenario.MaximumSessionNanoseconds);
}
