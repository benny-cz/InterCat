using InterCat.Capture.Windows;
using Xunit;

namespace InterCat.CaptureComparison.Tests;

/// <summary>The comparison harness's own replay sink: what it counts and what it refuses to guess.</summary>
public sealed class ReplaySinkTests
{
    [Fact(DisplayName = "R8: ETL replay sink records its own bounded-budget drops")]
    public void ReplaySinkCountsBudgetDrops()
    {
        var sink = new ReplaySink(recordBudget: 1);
        sink.OnObserved(new DeliveredRecord(Guid.Empty, 0, 0, 0));
        Assert.True(sink.Admit(new AdmittedEvent { RecordOrdinal = 1 }));
        sink.OnObserved(new DeliveredRecord(Guid.Empty, 0, 0, 0));
        Assert.False(sink.Admit(new AdmittedEvent { RecordOrdinal = 2 }));

        CaptureHealthSnapshot health = sink.Snapshot(providerLoss: 3, consumerLoss: 0);

        Assert.Equal(2, health.ObservedRecords);
        Assert.Equal(1, health.AdmittedRecords);
        Assert.Equal(1, health.ApplicationDrops);
        Assert.Equal(3, health.ProviderReportedEventLoss);
    }

    [Fact(DisplayName = "R21: the replay sink reports an unreadable stage total as unknown, not as zero")]
    public void ReplaySinkKeepsUnmeasuredStageTotalsNull()
    {
        var sink = new ReplaySink(recordBudget: 4);
        sink.OnObserved(new DeliveredRecord(Guid.Empty, 0, 0, 0));
        Assert.True(sink.Admit(new AdmittedEvent { RecordOrdinal = 1 }));
        sink.OnCallbackCompleted(new(100, ExtendedDataAvailability.RecordCarriedNone, 0, 0));

        CaptureStageSnapshot stages = sink.ReadStages(allocatedBytes: null, cpu: null);

        Assert.Null(stages.DeliveryThreadAllocatedBytes);
        Assert.Null(stages.DeliveryThreadCpu);
        Assert.Equal(1, stages.CallbackLatency.Samples);
        Assert.Equal(1, stages.QueueHighWaterRecords);
        Assert.Equal(1, stages.RecordsWithoutExtendedData);
    }
}
