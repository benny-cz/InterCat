using System.Buffers.Binary;
using InterCat.Domain;

namespace InterCat.Capture.Windows;

/// <summary>
/// Turns admitted records into observations outside the capture callback (section 9.3 stages). A documented
/// transform such as a network-order port is applied here, never to the admitted value itself (R1).
/// </summary>
public static class NetworkObservationBuilder
{
    public static NetworkTransferObservation? TryBuild(
        in AdmittedEvent admitted,
        AdmittedEventPlan plan,
        CaptureId captureId,
        uint streamId,
        uint sourceEpoch)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Mechanism is not (Mechanism.Tcp or Mechanism.Udp))
        {
            return null;
        }

        int? ownerProcessId = null;
        long? byteCount = null;
        ByteDomain? byteDomain = null;
        uint sourceAddress = 0;
        uint destinationAddress = 0;
        int sourcePort = 0;
        int destinationPort = 0;

        IReadOnlyList<AdmittedSlotPlan> slots = plan.Slots;
        for (int index = 0; index < slots.Count; index++)
        {
            if (!admitted.TryGetSlot(index, out long raw))
            {
                continue;
            }

            AdmittedSlotPlan slot = slots[index];
            switch (slot.Role)
            {
                case FieldRole.ProcessAttribution:
                    ownerProcessId ??= (int)raw;
                    break;
                case FieldRole.ByteCount:
                    byteCount = raw;
                    byteDomain = slot.ByteDomain;
                    break;
                case FieldRole.SourceEndpoint when slot.Width == 4:
                    sourceAddress = ApplyAddressTransform(raw, slot.Transform);
                    break;
                case FieldRole.SourceEndpoint:
                    sourcePort = ApplyPortTransform(raw, slot.Transform);
                    break;
                case FieldRole.DestinationEndpoint when slot.Width == 4:
                    destinationAddress = ApplyAddressTransform(raw, slot.Transform);
                    break;
                case FieldRole.DestinationEndpoint:
                    destinationPort = ApplyPortTransform(raw, slot.Transform);
                    break;
                default:
                    break;
            }
        }

        // Measured on FX-TCP-001: for this source the saddr and sport pair is the owning process's own
        // endpoint on send and on receive descriptors alike, so the flow is not re-oriented by direction.
        // The source-named values above stay exactly as delivered (R1).
        var flow = new FlowKey(sourceAddress, sourcePort, destinationAddress, destinationPort);

        return new()
        {
            Id = new(new(captureId, streamId, sourceEpoch, (ulong)admitted.RecordOrdinal), 0),
            Mechanism = plan.Mechanism,
            Kind = plan.Kind,
            Direction = plan.Direction,
            TimestampUtcTicks = admitted.TimestampUtcTicks,
            SourceTicks = admitted.TimestampQpc,
            OwnerProcessId = ownerProcessId,
            HeaderProcessId = admitted.HeaderProcessId,
            SourceAddress = sourceAddress,
            SourcePort = sourcePort,
            DestinationAddress = destinationAddress,
            DestinationPort = destinationPort,
            Flow = flow,

            // Null stays unknown: a descriptor without a size field never reports a zero transfer (R3).
            ByteCount = byteCount,
            ByteDomain = byteDomain,

            // The payload names the owner, so attribution is proven for this source; the event header
            // process is kept separately and is never promoted into the owner (section 4.1).
            AttributionQuality = ownerProcessId is null ? QualityLevel.UnknownQuality : QualityLevel.Proven,
        };
    }

    private static int ApplyPortTransform(long raw, SlotTransform transform) => transform switch
    {
        SlotTransform.NetworkOrderPort => BinaryPrimitives.ReverseEndianness((ushort)raw),
        _ => (int)raw,
    };

    private static uint ApplyAddressTransform(long raw, SlotTransform transform) => transform switch
    {
        SlotTransform.NetworkOrderIpv4Address => BinaryPrimitives.ReverseEndianness((uint)raw),
        _ => (uint)raw,
    };
}
