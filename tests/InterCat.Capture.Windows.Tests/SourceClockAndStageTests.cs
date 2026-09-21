using System.Diagnostics;
using InterCat.Capture.Windows;
using InterCat.Domain;
using Xunit;

namespace InterCat.Capture.Windows.Tests;

public sealed class SourceClockAndStageTests
{
    [Fact(DisplayName = "I8: a capture clock stays unconfirmed until a delivered record is checked against it")]
    public void DescribedClockIsAnAssumptionUntilChecked()
    {
        CaptureClockEvidence evidence = EtwSourceClock.DescribeLocal();

        Assert.Equal(SourceClockConfidence.Unconfirmed, evidence.Confidence);
        Assert.Null(evidence.CheckedNativeTicks);
        Assert.Equal(TimestampEncoding.Qpc, evidence.Descriptor.Encoding);
        Assert.Equal(SourceClockKind.Monotonic, evidence.Descriptor.Kind);
        Assert.Equal(Stopwatch.Frequency, evidence.Descriptor.TicksPerSecond);
        Assert.Equal(HostId.ForLocalMachine(), evidence.Descriptor.HostId);
    }

    [Fact(DisplayName = "I8: a reading from this process's own clock confirms the capture's encoding")]
    public void PlausibleReadingConfirmsTheClock()
    {
        CaptureClockEvidence evidence = EtwSourceClock.DescribeLocal();

        CaptureClockEvidence checkedEvidence = EtwSourceClock.Check(evidence, Stopwatch.GetTimestamp());

        Assert.Equal(SourceClockConfidence.Confirmed, checkedEvidence.Confidence);
        Assert.Null(checkedEvidence.RefusalReason);
        Assert.Equal(evidence.Descriptor, checkedEvidence.Descriptor);
    }

    [Fact(DisplayName = "I8: a reading on another clock refuses the assumed encoding instead of converting it")]
    public void ImplausibleReadingRefusesTheClock()
    {
        CaptureClockEvidence evidence = EtwSourceClock.DescribeLocal();

        // A session logging system time stamps records with a FILETIME, which is orders of magnitude away
        // from this process's performance counter. Converting it would stretch the whole viewport.
        CaptureClockEvidence checkedEvidence = EtwSourceClock.Check(evidence, DateTime.UtcNow.ToFileTimeUtc());

        Assert.Equal(SourceClockConfidence.Refused, checkedEvidence.Confidence);
        Assert.False(string.IsNullOrWhiteSpace(checkedEvidence.RefusalReason));
        Assert.NotNull(checkedEvidence.CheckedNativeTicks);
    }

    [Fact(DisplayName = "I8: a checked clock is never re-checked by a later record")]
    public void FirstCheckedReadingIsTheEvidence()
    {
        CaptureClockEvidence confirmed = EtwSourceClock.Check(
            EtwSourceClock.DescribeLocal(),
            Stopwatch.GetTimestamp());

        CaptureClockEvidence later = EtwSourceClock.Check(confirmed, DateTime.UtcNow.ToFileTimeUtc());

        Assert.Equal(SourceClockConfidence.Confirmed, later.Confidence);
        Assert.Equal(confirmed.CheckedNativeTicks, later.CheckedNativeTicks);
    }

    [Fact(DisplayName = "R8: the queue high-water mark is the deepest depth enqueued, not the last")]
    public void QueueHighWaterKeepsTheDeepestDepth()
    {
        var ledger = new CaptureStageLedger();

        ledger.RecordQueueDepth(3);
        ledger.RecordQueueDepth(41);
        ledger.RecordQueueDepth(1);

        Assert.Equal(41, ledger.Read(queueCapacity: 64).QueueHighWaterRecords);
    }

    [Fact(DisplayName = "R21: an unmeasured stage total reads as unknown rather than as zero")]
    public void UnmeasuredStageTotalsStayNull()
    {
        var ledger = new CaptureStageLedger();
        ledger.RecordCallback(new(0, ExtendedDataAvailability.UnavailableOnThisAdapter, 0, 0));
        ledger.RecordExtendedDataUnavailable("the accessor did not bind on this build");

        CaptureStageSnapshot before = ledger.Read(queueCapacity: 8);
        ledger.RecordDeliveryThread(4_096, new ThreadCpuReading(10, 20));
        CaptureStageSnapshot after = ledger.Read(queueCapacity: 8);

        Assert.Null(before.DeliveryThreadAllocatedBytes);
        Assert.Null(before.DeliveryThreadCpu);
        Assert.Equal(1, before.RecordsWithUnreachableExtendedData);
        Assert.Equal("the accessor did not bind on this build", before.ExtendedDataUnavailableReason);
        Assert.Equal(4_096, after.DeliveryThreadAllocatedBytes);
        Assert.Equal(30, after.DeliveryThreadCpu!.Value.TotalTicks);
    }

    [Fact(DisplayName = "R8: a capture reports per-thread processor time or says the host cannot")]
    public void ThreadProcessorTimeIsReadableOrDeclaredUnavailable()
    {
        bool read = ThreadCpuTime.TryReadCurrentThread(out ThreadCpuReading reading);

        if (!ThreadCpuTime.IsAvailable)
        {
            Assert.False(read);
            return;
        }

        Assert.True(read);
        Assert.True(reading.TotalTicks >= 0);
    }
}
