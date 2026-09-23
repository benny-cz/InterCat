using InterCat.Capture.Windows;
using InterCat.Domain;

namespace InterCat.CaptureComparison;

/// <summary>
/// Counts what a replay delivers and keeps nothing. Attribution replays both runs through this, because a
/// sink that stores records would swamp the difference it is trying to measure with its own list.
/// </summary>
internal sealed class CountingSink : IAdmittedEventSink
{
    public long Observed { get; private set; }

    public long Admitted { get; private set; }

    public void OnObserved(in DeliveredRecord delivered) => Observed++;

    public bool Admit(in AdmittedEvent admitted)
    {
        Admitted++;
        return true;
    }

    public void OnOmitted(OmissionReason reason, in DeliveredRecord delivered)
    {
    }

    public void OnUndecodable(UndecodableReason reason, in DeliveredRecord delivered)
    {
    }
}

/// <summary>
/// Collects admitted records from offline replay and measures the same stage the live callback measures,
/// so the two variants are compared by one instrumented admission path rather than two.
/// </summary>
internal sealed class ReplaySink(int recordBudget) : IAdmittedEventSink
{
    private readonly CaptureHealthLedger ledger = new();
    private readonly CaptureStageLedger stages = new();

    public List<AdmittedEvent> Records { get; } = new(Math.Min(recordBudget, 16_384));

    public void OnObserved(in DeliveredRecord delivered) => ledger.RecordObserved();

    public bool Admit(in AdmittedEvent admitted)
    {
        if (Records.Count >= recordBudget)
        {
            ledger.RecordApplicationDrop();
            return false;
        }

        Records.Add(admitted);
        ledger.RecordAdmitted();
        stages.RecordQueueDepth(Records.Count);
        return true;
    }

    public void OnOmitted(OmissionReason reason, in DeliveredRecord delivered) => ledger.RecordOmission(reason);

    public void OnUndecodable(UndecodableReason reason, in DeliveredRecord delivered) => ledger.RecordUndecodable(reason);

    public void OnCallbackCompleted(in CallbackCost cost) => stages.RecordCallback(in cost);

    public CaptureHealthSnapshot Snapshot(long providerLoss, long consumerLoss)
    {
        ledger.RecordSourceLoss(providerLoss, consumerLoss);
        return ledger.Read(0, recordBudget);
    }

    /// <summary>
    /// The replay stage's own cost. Its thread totals are supplied by the caller, which owns the thread;
    /// an unmeasurable total stays null rather than becoming a zero.
    /// </summary>
    public CaptureStageSnapshot ReadStages(long? allocatedBytes, ThreadCpuReading? cpu)
    {
        stages.RecordDeliveryThread(allocatedBytes ?? -1, cpu);
        return stages.Read(recordBudget);
    }
}
