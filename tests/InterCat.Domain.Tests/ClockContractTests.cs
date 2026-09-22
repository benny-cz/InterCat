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

    [Theory(DisplayName = "I3: a session-time interval maps to exactly the native readings inside it")]
    [InlineData(10_000_000L, 18_683_281_748_319L, TimestampRounding.NearestEven)]
    [InlineData(3L, 1_000L, TimestampRounding.NearestEven)]
    [InlineData(2_000_000_000L, -40L, TimestampRounding.NearestEven)]
    [InlineData(7L, 0L, TimestampRounding.TowardZero)]
    public void ASessionIntervalMapsToTheReadingsInsideIt(long ticksPerSecond, long epoch, TimestampRounding rounding)
    {
        var clock = new SourceClockDescriptor(
            new ClockId(Guid.Parse("66666666-6666-4666-8666-666666666666")),
            new HostId(Guid.Parse("11111111-1111-4111-8111-111111111111")),
            SourceClockKind.Monotonic,
            TimestampEncoding.Qpc,
            ticksPerSecond,
            epoch,
            rounding,
            maximumAbsoluteSessionNanoseconds: SourceClockMath.SessionTicksPerSecond * 3_600);
        var random = new Random(20260922);

        // For any half-open session interval [a, b), a reading n satisfies a <= session(n) < b exactly when it lies
        // in [first(a), first(b)). Checked against every reading near both bounds, so an off-by-one in either
        // direction - admitting a reading just before a or refusing one just before b - fails the case.
        for (int trial = 0; trial < 200; trial++)
        {
            long a = random.NextInt64(-5_000_000_000, 5_000_000_000);
            long b = a + random.NextInt64(1, 3_000_000_000);
            long first = SourceClockMath.FirstNativeAtOrAfter(clock, new SessionTimestamp(a));
            long end = SourceClockMath.FirstNativeAtOrAfter(clock, new SessionTimestamp(b));
            foreach (long boundary in new[] { first, end })
            {
                for (long reading = boundary - 3; reading <= boundary + 3; reading++)
                {
                    Int128 session = SourceClockMath.SessionNanoseconds(clock, reading);
                    bool inside = session >= a && session < b;
                    Assert.True(
                        inside == (reading >= first && reading < end),
                        $"reading {reading} at {session} ns against [{a}, {b}) mapped to [{first}, {end})");
                }
            }
        }

        // Session time zero starts at the capture epoch, give or take the one reading that rounds onto zero when a
        // tick is finer than a nanosecond: the boundary is where the conversion says it is, not where it looks.
        long zero = SourceClockMath.FirstNativeAtOrAfter(clock, new SessionTimestamp(0));
        Assert.True(SourceClockMath.SessionNanoseconds(clock, zero) >= 0);
        Assert.True(SourceClockMath.SessionNanoseconds(clock, zero - 1) < 0);
        Assert.InRange(zero, epoch - 1, epoch);
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
