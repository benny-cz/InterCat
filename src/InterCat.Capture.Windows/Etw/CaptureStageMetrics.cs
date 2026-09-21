using System.Diagnostics;
using InterCat.Domain;

namespace InterCat.Capture.Windows;

/// <summary>
/// What one completed callback cost and what it copied. The admitter reports it; the sink decides whether
/// to keep it, so replay and live capture are measured by the same code (section 18.1).
/// </summary>
public readonly record struct CallbackCost(
    long StopwatchTicks,
    ExtendedDataAvailability ExtendedData,
    int ExtendedItemsCopied,
    int ExtendedItemsOmitted);

/// <summary>
/// Isolated stage overhead for one capture. Callback cost is measured on the delivery thread only, so it
/// is not the process's total; queue depth is the high-water mark the callback itself created, not a
/// one-second sample; and an unmeasurable quantity is reported as null rather than as zero (R21).
/// </summary>
public sealed record CaptureStageSnapshot
{
    /// <summary>Time spent inside the admission callback, per record.</summary>
    public required LatencyHistogramSnapshot CallbackLatency { get; init; }

    /// <summary>Bytes allocated on the delivery thread across the whole capture, or null while it runs.</summary>
    public required long? DeliveryThreadAllocatedBytes { get; init; }

    /// <summary>Processor time of the delivery thread, or null when the host cannot report it.</summary>
    public required ThreadCpuReading? DeliveryThreadCpu { get; init; }

    /// <summary>The deepest the bounded queue was seen at the moment a record was enqueued.</summary>
    public required int QueueHighWaterRecords { get; init; }

    public required int QueueCapacityRecords { get; init; }

    /// <summary>Extended-data items copied out of callback memory across the capture.</summary>
    public required long ExtendedItemsCopied { get; init; }

    /// <summary>Items a record declared that admission did not copy. Counted, never silently dropped.</summary>
    public required long ExtendedItemsOmitted { get; init; }

    /// <summary>Records whose extended items this adapter could not reach at all.</summary>
    public required long RecordsWithUnreachableExtendedData { get; init; }

    /// <summary>Records that declared no extended items. Distinct from records that could not be read.</summary>
    public required long RecordsWithoutExtendedData { get; init; }

    /// <summary>Why extended data was not read, when it was not. Null when it was read or not requested.</summary>
    public required string? ExtendedDataUnavailableReason { get; init; }
}

/// <summary>
/// The live stage ledger. Every quantity is recorded where it is incurred, and none is derived from
/// another, so the resulting overhead ledger can be read one stage at a time (section 12, section 20.6).
/// </summary>
public sealed class CaptureStageLedger
{
    private readonly LatencyHistogram callbackLatency = new();
    private long extendedCopied;
    private long extendedOmitted;
    private long extendedUnreachable;
    private long withoutExtended;
    private long queueHighWater;
    private long deliveryAllocatedBytes = -1;
    private ThreadCpuReading? deliveryCpu;
    private string? extendedUnavailableReason;

    /// <summary>Records one completed callback. Called on the delivery thread, allocation-free.</summary>
    public void RecordCallback(in CallbackCost cost)
    {
        callbackLatency.RecordTicks(Math.Max(cost.StopwatchTicks, 0), Stopwatch.Frequency);
        if (cost.ExtendedItemsCopied > 0)
        {
            Interlocked.Add(ref extendedCopied, cost.ExtendedItemsCopied);
        }

        if (cost.ExtendedItemsOmitted > 0)
        {
            Interlocked.Add(ref extendedOmitted, cost.ExtendedItemsOmitted);
        }

        switch (cost.ExtendedData)
        {
            case ExtendedDataAvailability.UnavailableOnThisAdapter:
                Interlocked.Increment(ref extendedUnreachable);
                break;
            case ExtendedDataAvailability.RecordCarriedNone:
                Interlocked.Increment(ref withoutExtended);
                break;
            default:
                break;
        }
    }

    /// <summary>Records the queue depth observed at the moment a record was enqueued.</summary>
    public void RecordQueueDepth(int depth)
    {
        long current = Interlocked.Read(ref queueHighWater);
        while (depth > current)
        {
            long seen = Interlocked.CompareExchange(ref queueHighWater, depth, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }

    /// <summary>Records the delivery thread's own totals once its pump has finished.</summary>
    public void RecordDeliveryThread(long allocatedBytes, ThreadCpuReading? cpu)
    {
        Interlocked.Exchange(ref deliveryAllocatedBytes, allocatedBytes);
        deliveryCpu = cpu;
    }

    /// <summary>Records why extended data could not be read, so a zero count is never read as an absence.</summary>
    public void RecordExtendedDataUnavailable(string reason) =>
        Interlocked.CompareExchange(ref extendedUnavailableReason, reason, null);

    public CaptureStageSnapshot Read(int queueCapacity) => new()
    {
        CallbackLatency = callbackLatency.Read(),
        DeliveryThreadAllocatedBytes = Interlocked.Read(ref deliveryAllocatedBytes) is var bytes && bytes < 0
            ? null
            : bytes,
        DeliveryThreadCpu = deliveryCpu,
        QueueHighWaterRecords = (int)Interlocked.Read(ref queueHighWater),
        QueueCapacityRecords = queueCapacity,
        ExtendedItemsCopied = Interlocked.Read(ref extendedCopied),
        ExtendedItemsOmitted = Interlocked.Read(ref extendedOmitted),
        RecordsWithUnreachableExtendedData = Interlocked.Read(ref extendedUnreachable),
        RecordsWithoutExtendedData = Interlocked.Read(ref withoutExtended),
        ExtendedDataUnavailableReason = Volatile.Read(ref extendedUnavailableReason),
    };
}
