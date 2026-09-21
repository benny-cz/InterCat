using InterCat.Domain;

namespace InterCat.Capture.Windows;

/// <summary>
/// Builds RPC call observations from admitted records. No byte value is produced because the descriptors
/// carry none; an absent measurement stays absent rather than becoming a zero (R3, P2).
/// </summary>
public static class RpcObservationBuilder
{
    public static RpcCallObservation? TryBuild(
        in AdmittedEvent admitted,
        AdmittedEventPlan plan,
        CaptureId captureId,
        uint streamId,
        uint sourceEpoch)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Mechanism != Mechanism.Rpc)
        {
            return null;
        }

        long? procedureNumber = null;
        long? protocol = null;
        long? status = null;

        IReadOnlyList<AdmittedSlotPlan> slots = plan.Slots;
        for (int index = 0; index < slots.Count; index++)
        {
            AdmittedSlotPlan slot = slots[index];
            if (slot.Kind != AdmittedSlotKind.Numeric || !admitted.TryGetSlot(index, out long raw))
            {
                continue;
            }

            switch (slot.FieldName)
            {
                case "ProcNum":
                    procedureNumber = raw;
                    break;
                case "Protocol":
                    protocol = raw;
                    break;
                case "Status":
                    status = raw;
                    break;
                default:
                    break;
            }
        }

        return new()
        {
            Id = ObservationId.Create(
                new(captureId, streamId, sourceEpoch, (ulong)admitted.RecordOrdinal),
                NormalizerContractVersion.V1,
                "rpc-call"),
            Kind = plan.Kind,
            Direction = plan.Direction,
            TimestampUtcTicks = admitted.TimestampUtcTicks,
            SourceTicks = admitted.TimestampQpc,
            ProcessId = admitted.HeaderProcessId,
            ThreadId = admitted.HeaderThreadId,
            ActivityId = admitted.ActivityId,
            InterfaceUuid = admitted.HasIdentifier ? admitted.Identifier : null,
            ProcedureNumber = procedureNumber,
            Protocol = protocol,
            Status = status,

            // The event header names the process that issued the call for this source, and no payload
            // field contradicts it, so attribution is qualified rather than proven (section 4.1).
            AttributionQuality = QualityLevel.Qualified,
        };
    }
}
