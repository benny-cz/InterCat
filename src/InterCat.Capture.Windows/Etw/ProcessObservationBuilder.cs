using InterCat.Domain;

namespace InterCat.Capture.Windows;

/// <summary>
/// Builds process lifecycle observations from admitted records. Fields the descriptor did not carry stay
/// null so a missing sequence number is never replaced by a PID-only identity (R3, R22).
/// </summary>
public static class ProcessObservationBuilder
{
    public static ProcessInstanceObservation? TryBuild(
        in AdmittedEvent admitted,
        AdmittedEventPlan plan,
        CaptureId captureId,
        uint streamId,
        uint sourceEpoch)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Mechanism != Mechanism.ProcessLifecycle)
        {
            return null;
        }

        int? processId = null;
        int? parentProcessId = null;
        long? sequenceNumber = null;
        long? createFileTime = null;
        long? exitFileTime = null;
        long? exitCode = null;

        IReadOnlyList<AdmittedSlotPlan> slots = plan.Slots;
        for (int index = 0; index < slots.Count; index++)
        {
            if (!admitted.TryGetSlot(index, out long raw))
            {
                continue;
            }

            AdmittedSlotPlan slot = slots[index];
            switch (slot.FieldName)
            {
                case "ProcessID":
                    processId = (int)raw;
                    break;
                case "ParentProcessID":
                    parentProcessId = (int)raw;
                    break;
                case "ProcessSequenceNumber":
                    sequenceNumber = raw;
                    break;
                case "CreateTime":
                    createFileTime = raw;
                    break;
                case "ExitTime":
                    exitFileTime = raw;
                    break;
                case "ExitCode":
                    exitCode = raw;
                    break;
                default:
                    break;
            }
        }

        if (processId is null)
        {
            return null;
        }

        return new()
        {
            Id = new(new(captureId, streamId, sourceEpoch, (ulong)admitted.RecordOrdinal), 0),
            Kind = plan.Kind,
            TimestampUtcTicks = admitted.TimestampUtcTicks,
            ProcessId = processId.Value,
            SequenceNumber = sequenceNumber,
            CreateFileTime = createFileTime,
            ExitFileTime = exitFileTime,
            ParentProcessId = parentProcessId,
            ExitCode = exitCode,
        };
    }
}
