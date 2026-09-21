using InterCat.Benchmarks;
using InterCat.Capture.Windows;
using InterCat.Domain;
using Xunit;

namespace InterCat.CaptureComparison.Tests;

public sealed class LoadSeriesTests
{
    [Fact(DisplayName = "R8: a series declares every setting that changes what it measures")]
    public void EveryLevelDeclaresItsSettings()
    {
        Assert.True(LoadSeries.Default.Count >= 2, "One level is a point, not a series.");
        Assert.Equal(
            LoadSeries.Default.Select(level => level.Name).Distinct(StringComparer.Ordinal).Count(),
            LoadSeries.Default.Count);

        foreach (LoadLevel level in LoadSeries.Default)
        {
            Assert.False(string.IsNullOrWhiteSpace(level.Intent), $"{level.Name} declares no intent.");
            Assert.True(level.Connections > 0);
            Assert.True(level.MessagesPerConnection > 0);
            Assert.True(level.MaximumMessageBytes > 0);
            Assert.True(level.QueueCapacityRecords > 0);
            Assert.True(level.JournalBatchRecords > 0);
            Assert.Equal(level.Connections * level.MessagesPerConnection, level.DeclaredMessages);
        }

        // A series that only ever raises the rate cannot reach a bound on a machine faster than the
        // fixture, so at least one level must shrink a bound instead.
        LoadLevel first = LoadSeries.Default[0];
        Assert.Contains(LoadSeries.Default, level => level.QueueCapacityRecords < first.QueueCapacityRecords);
        Assert.Contains(LoadSeries.Default, level => level.JournalBatchRecords < first.JournalBatchRecords);
    }

    [Fact(DisplayName = "R3: the impact workload is paced and long enough to measure separately")]
    public void ImpactWorkloadIsDeclaredSeparately()
    {
        Assert.DoesNotContain(LoadSeries.Impact, LoadSeries.Default);
        Assert.True(LoadSeries.Impact.InterMessageDelayMilliseconds > 0);
        Assert.True(
            LoadSeries.Impact.MessagesPerConnection * LoadSeries.Impact.InterMessageDelayMilliseconds >= 3_000,
            "Each concurrent connection needs a multi-second processor-time interval.");
        Assert.InRange(LoadSeries.Impact.DeclaredMessages, 1_000, 5_000);
        Assert.Contains("paired", LoadSeries.Impact.Intent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "R3: impact summaries use the median without hiding signed observations")]
    public void ImpactMedianKeepsTheMiddleReading()
    {
        Assert.Equal(-1, CaptureImpactMath.Median([-10, -1, 8]));
        Assert.Equal(3, CaptureImpactMath.Median([2, 4]));
        Assert.Throws<ArgumentException>(() => CaptureImpactMath.Median([]));
    }

    [Fact(DisplayName = "R8: an unknown series or level is refused rather than silently reduced")]
    public void UnknownNamesAreRefused()
    {
        Assert.Null(LoadSeries.Find("nonexistent"));
        Assert.Null(LoadSeries.Select("peak,nonexistent"));
        Assert.Null(LoadSeries.Select(string.Empty));
        Assert.NotNull(LoadSeries.Find("quick"));
        IReadOnlyList<LoadLevel> chosen = LoadSeries.Select("paced,peak")!;
        Assert.Equal(["paced", "peak"], chosen.Select(level => level.Name));
    }

    [Fact(DisplayName = "R8: a full bounded queue is reported as a counted drop, not as loss")]
    public void QueueSaturationIsDistinctFromSourceLoss()
    {
        SaturationAssessment saturated = LoadSeries.Assess(
            Health(admitted: 1_000, drops: 42),
            Stage(highWater: 512, capacity: 512),
            new(4, 12, 5),
            acquisitionMilliseconds: 1_000);

        Assert.Equal(SaturationOutcome.QueueSaturated, saturated.Outcome);
        Assert.Contains("42", saturated.Evidence, StringComparison.Ordinal);
        Assert.Equal(1_000, saturated.AdmittedRecordsPerSecond);
        Assert.Equal(1d, saturated.QueueUtilization);
        Assert.True(saturated.WriterKeptUp);
        Assert.Equal(0.017, saturated.WriterBusyFraction!.Value, 3);
    }

    [Fact(DisplayName = "R8: loss before admission outranks every queue outcome")]
    public void SourceLossOutranksQueueOutcomes()
    {
        SaturationAssessment lost = LoadSeries.Assess(
            Health(admitted: 10, drops: 7, providerLoss: 3),
            Stage(highWater: 512, capacity: 512),
            new(1, 1, 1),
            acquisitionMilliseconds: 500);

        Assert.Equal(SaturationOutcome.SourceLoss, lost.Outcome);
        Assert.Contains("before admission", lost.Evidence, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R8: a queue past half its bound is pressure, and below it is headroom")]
    public void PressureAndHeadroomAreDistinct()
    {
        SaturationAssessment pressure = LoadSeries.Assess(
            Health(admitted: 100),
            Stage(highWater: 60, capacity: 100),
            writer: null,
            acquisitionMilliseconds: 1_000);
        SaturationAssessment headroom = LoadSeries.Assess(
            Health(admitted: 100),
            Stage(highWater: 4, capacity: 100),
            writer: null,
            acquisitionMilliseconds: 1_000);

        Assert.Equal(SaturationOutcome.QueuePressure, pressure.Outcome);
        Assert.Equal(SaturationOutcome.Headroom, headroom.Outcome);
        Assert.Null(pressure.WriterKeptUp);
    }

    [Fact(DisplayName = "R21: a variant with no queue reports no utilization rather than zero")]
    public void AbsentQueueIsNotZeroUtilization()
    {
        SaturationAssessment assessment = LoadSeries.Assess(
            Health(admitted: 500),
            stages: null,
            writer: null,
            acquisitionMilliseconds: 250);

        Assert.Null(assessment.QueueUtilization);
        Assert.Equal(SaturationOutcome.Headroom, assessment.Outcome);
        Assert.Equal(2_000, assessment.AdmittedRecordsPerSecond);
    }

    [Fact(DisplayName = "R8: a writer whose flushes outlast the acquisition window set the pace")]
    public void WriterBoundIsReportedSeparately()
    {
        SaturationAssessment assessment = LoadSeries.Assess(
            Health(admitted: 100),
            Stage(highWater: 10, capacity: 100),
            new(50, 180, 40),
            acquisitionMilliseconds: 400);

        Assert.False(assessment.WriterKeptUp);
        Assert.Equal(0.55, assessment.WriterBusyFraction);
        Assert.Equal(SaturationOutcome.Headroom, assessment.Outcome);
    }

    [Fact(DisplayName = "R3: an unreadable loss counter leaves loss unknown rather than zero")]
    public void UnreadableLossIsNotZeroLoss()
    {
        SaturationAssessment unknown = LoadSeries.Assess(
            Health(admitted: 100),
            Stage(highWater: 4, capacity: 100),
            writer: null,
            acquisitionMilliseconds: 1_000,
            sourceLossKnown: false);

        Assert.False(unknown.SourceLossKnown);
        Assert.Contains("could read", unknown.Evidence, StringComparison.Ordinal);
        Assert.Equal(SaturationOutcome.Headroom, unknown.Outcome);
    }

    [Fact(DisplayName = "R3: a throughput with no elapsed time is unmeasured rather than infinite")]
    public void RateRefusesAZeroInterval()
    {
        Assert.Null(MachineProbe.Rate(1_000, TimeSpan.Zero));
        Assert.Null(MachineProbe.Rate(0, TimeSpan.FromSeconds(1)));
        Assert.Equal(1_000d, MachineProbe.Rate(1_000, TimeSpan.FromSeconds(1)));
    }

    private static CaptureHealthSnapshot Health(
        long admitted,
        long drops = 0,
        long providerLoss = 0,
        long consumerLoss = 0) => new()
    {
        AdmittedRecords = admitted,
        ObservedRecords = admitted + drops,
        PolicyOmissionsUnrequestedProvider = 0,
        PolicyOmissionsDescriptorNotAdmitted = 0,
        PolicyOmissionsDescriptorDenied = 0,
        UndecodableBodyShorterThanSchema = 0,
        UndecodableUnknownDescriptorVersion = 0,
        UndecodableAdmissionReadFailed = 0,
        UndecodablePointerWidthMismatch = 0,
        ProviderReportedEventLoss = providerLoss,
        ConsumerReportedBufferLoss = consumerLoss,
        ApplicationDrops = drops,
        QuarantinedTimestamps = 0,
        QueueDepth = 0,
        QueueCapacity = 0,
    };

    private static CaptureStageSnapshot Stage(int highWater, int capacity) => new()
    {
        CallbackLatency = new LatencyHistogram().Read(),
        DeliveryThreadAllocatedBytes = null,
        DeliveryThreadCpu = null,
        QueueHighWaterRecords = highWater,
        QueueCapacityRecords = capacity,
        ExtendedItemsCopied = 0,
        ExtendedItemsOmitted = 0,
        RecordsWithUnreachableExtendedData = 0,
        RecordsWithoutExtendedData = 0,
        ExtendedDataUnavailableReason = null,
    };
}
