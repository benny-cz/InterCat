using InterCat.Domain;

namespace InterCat.Capture.Windows;

/// <summary>
/// Builds named-pipe operations from admitted file records, outside the capture callback. Requested and
/// completed byte values keep their own domains, and a completion is paired by I/O request rather than by
/// time proximity (P3, P8).
/// </summary>
public static class PipeObservationBuilder
{
    public static PipeOperationObservation? TryBuild(
        in AdmittedEvent admitted,
        AdmittedEventPlan plan,
        CaptureId captureId,
        uint streamId,
        uint sourceEpoch)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Mechanism != Mechanism.NamedPipe)
        {
            return null;
        }

        ulong irp = 0;
        ulong fileObject = 0;
        ulong fileKey = 0;
        int threadId = 0;
        long? requested = null;
        long? completed = null;
        long? status = null;

        IReadOnlyList<AdmittedSlotPlan> slots = plan.Slots;
        for (int index = 0; index < slots.Count; index++)
        {
            AdmittedSlotPlan slot = slots[index];
            if (slot.Kind == AdmittedSlotKind.ResourceName || !admitted.TryGetSlot(index, out long raw))
            {
                continue;
            }

            switch (slot.FieldName)
            {
                case "Irp":
                    irp = (ulong)raw;
                    break;
                case "FileObject":
                    fileObject = (ulong)raw;
                    break;
                case "FileKey":
                    fileKey = (ulong)raw;
                    break;
                case "IssuingThreadId":
                    threadId = (int)raw;
                    break;
                case "IOSize":
                    requested = raw;
                    break;
                case "ExtraInformation":
                    completed = raw;
                    break;
                case "Status":
                    status = raw;
                    break;
                default:
                    break;
            }
        }

        string? resourceName = null;
        if (admitted.HasName)
        {
            Span<char> buffer = stackalloc char[AdmittedEvent.MaximumNameLength];
            int length = admitted.CopyName(buffer);
            resourceName = length == 0 ? null : new string(buffer[..length]);
        }

        return new()
        {
            Id = ObservationId.Create(
                new(captureId, streamId, sourceEpoch, (ulong)admitted.RecordOrdinal),
                NormalizerContractVersion.V1,
                "pipe-operation"),
            Kind = plan.Kind,
            Direction = plan.Direction,
            TimestampUtcTicks = admitted.TimestampUtcTicks,
            SourceTicks = admitted.TimestampQpc,
            ProcessId = admitted.HeaderProcessId,
            ThreadId = threadId == 0 ? admitted.HeaderThreadId : threadId,
            IrpKey = irp,
            FileObject = fileObject,
            FileKey = fileKey,
            ResourceName = resourceName,
            ResourceNameTruncated = admitted.NameTruncated,
            RequestedBytes = requested,
            CompletedBytes = completed,
            Status = status,

            // This source attributes an operation through its event header, which is the issuing thread's
            // process for file operations. No payload field names an owner, so attribution stays qualified.
            AttributionQuality = QualityLevel.Qualified,
        };
    }
}
