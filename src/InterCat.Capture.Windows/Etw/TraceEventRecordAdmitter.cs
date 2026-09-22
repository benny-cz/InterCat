using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;

namespace InterCat.Capture.Windows;

/// <summary>
/// Applies the same bounded admission policy to live ETW callbacks and offline ETL replay. The IC-009
/// comparison must measure acquisition and persistence differences, not two subtly different decoders.
/// </summary>
internal sealed class TraceEventRecordAdmitter
{
    private readonly EventAdmissionTable table;
    private readonly IAdmittedEventSink sink;
    private readonly HashSet<DeniedKey> denied;
    private readonly TraceEventRecordAccessor? recordAccessor;
    private long ordinal;

    public TraceEventRecordAdmitter(
        EventAdmissionTable table,
        IReadOnlyList<ProviderEnablementRequest> providers,
        IAdmittedEventSink sink,
        bool preserveExtendedData = false,
        TraceEventRecordAccessor? recordAccessor = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(sink);

        this.table = table;
        this.sink = sink;
        this.recordAccessor = preserveExtendedData ? recordAccessor ?? TraceEventRecordAccessor.Shared : null;
        denied = [];
        foreach (ProviderEnablementRequest request in providers)
        {
            foreach (int eventId in request.EventIdsToDisable)
            {
                denied.Add(new(request.ProviderGuid, eventId));
            }
        }
    }

    public long AssignedRecordCount => Interlocked.Read(ref ordinal);

    /// <summary>Why extended data cannot be copied on this build, or null when it can or was not asked for.</summary>
    public string? ExtendedDataUnavailableReason =>
        recordAccessor is null || recordAccessor.IsAvailable ? null : recordAccessor.UnavailableReason;

    public void Admit(TraceEvent data)
    {
        ArgumentNullException.ThrowIfNull(data);
        long callbackStarted = Stopwatch.GetTimestamp();
        AdmitCore(data, callbackStarted);
    }

    private void AdmitCore(TraceEvent data, long callbackStarted)
    {
        sink.OnObserved();

        Guid provider = data.ProviderGuid;
        int eventId = (int)data.ID;
        if (denied.Contains(new(provider, eventId)))
        {
            sink.OnOmitted(OmissionReason.DescriptorDenied);
            ReportRejectedCallback(callbackStarted);
            return;
        }

        DescriptorAdmissionResolution resolution = table.Resolve(provider, eventId, data.Version);
        if (resolution.Outcome != DescriptorAdmissionOutcome.Admitted)
        {
            switch (resolution.Outcome)
            {
                case DescriptorAdmissionOutcome.UnrequestedProvider:
                    sink.OnOmitted(OmissionReason.UnrequestedProvider);
                    break;
                case DescriptorAdmissionOutcome.DescriptorNotAdmitted:
                    sink.OnOmitted(OmissionReason.DescriptorNotAdmitted);
                    break;
                case DescriptorAdmissionOutcome.UnknownDescriptorVersion:
                    sink.OnUndecodable(UndecodableReason.UnknownDescriptorVersion);
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected descriptor outcome {resolution.Outcome}.");
            }

            ReportRejectedCallback(callbackStarted);
            return;
        }

        AdmittedEventPlan descriptorPlan = resolution.Plan
            ?? throw new InvalidOperationException("An admitted descriptor resolution has no compiled plan.");

        if (data.EventDataLength < descriptorPlan.MinimumBodyLength)
        {
            sink.OnUndecodable(UndecodableReason.BodyShorterThanSchema);
            ReportRejectedCallback(callbackStarted);
            return;
        }

        if (data.PointerSize != descriptorPlan.PointerSize)
        {
            sink.OnUndecodable(UndecodableReason.PointerWidthMismatch);
            ReportRejectedCallback(callbackStarted);
            return;
        }

        AdmittedEvent admitted = default;
        admitted.SourceIndex = descriptorPlan.SourceIndex;
        admitted.EventId = eventId;
        admitted.Version = data.Version;
        admitted.Opcode = (int)data.Opcode;
#pragma warning disable CS0618 // I8 requires the original clock reading; relative milliseconds cannot replace it.
        admitted.TimestampQpc = data.TimeStampQPC;
#pragma warning restore CS0618
        admitted.TimestampUtcTicks = data.TimeStamp.ToUniversalTime().Ticks;
        admitted.ActivityId = data.ActivityID;
        admitted.RelatedActivityId = data.RelatedActivityID;
        admitted.HeaderProcessId = data.ProcessID;
        admitted.HeaderThreadId = data.ThreadID;
        admitted.ProcessorNumber = data.ProcessorNumber;
        admitted.RecordOrdinal = Interlocked.Increment(ref ordinal);

        IntPtr body = data.DataStart;
        if (body == IntPtr.Zero)
        {
            sink.OnUndecodable(UndecodableReason.AdmissionReadFailed);
            ReportRejectedCallback(callbackStarted);
            return;
        }

        IReadOnlyList<AdmittedSlotPlan> slots = descriptorPlan.Slots;
        for (int index = 0; index < slots.Count; index++)
        {
            AdmittedSlotPlan slot = slots[index];
            if (slot.Kind == AdmittedSlotKind.ResourceName)
            {
                ReadBoundedName(body, slot.Offset, data.EventDataLength, ref admitted);
                continue;
            }

            if (slot.Kind == AdmittedSlotKind.AnsiResourceName)
            {
                ReadBoundedAnsiName(body, slot.Offset, data.EventDataLength, ref admitted);
                continue;
            }

            if (slot.Kind == AdmittedSlotKind.ResourceNameAfterSid)
            {
                if (!TryOffsetAfterSid(body, slot.Offset, data.EventDataLength, out int nameOffset))
                {
                    // A SID that claims more sub-authorities than a SID has, or runs past the record, is a record whose
                    // shape does not match its schema. It is undecodable rather than read at a guessed offset.
                    sink.OnUndecodable(UndecodableReason.AdmissionReadFailed);
                    ReportRejectedCallback(callbackStarted);
                    return;
                }

                ReadBoundedName(body, nameOffset, data.EventDataLength, ref admitted);
                continue;
            }

            if (slot.Kind == AdmittedSlotKind.Identifier)
            {
                ReadIdentifier(body, slot.Offset, ref admitted);
                continue;
            }

            switch (slot.Width)
            {
                case 1:
                    admitted.SetSlot(index, Marshal.ReadByte(body, slot.Offset));
                    break;
                case 2:
                    admitted.SetSlot(index, (ushort)Marshal.ReadInt16(body, slot.Offset));
                    break;
                case 4:
                    admitted.SetSlot(index, (uint)Marshal.ReadInt32(body, slot.Offset));
                    break;
                case 8:
                    admitted.SetSlot(index, Marshal.ReadInt64(body, slot.Offset));
                    break;
                default:
                    sink.OnUndecodable(UndecodableReason.AdmissionReadFailed);
                    ReportRejectedCallback(callbackStarted);
                    return;
            }
        }

        CopyExtendedData(data, ref admitted);
        _ = sink.Admit(admitted);
        ReportCallback(callbackStarted, in admitted);
    }

    /// <summary>
    /// Copies the record's extended-data items when the plan asks for them. An adapter that cannot reach
    /// the items says so on the record, because zero copied items would otherwise read as a record that
    /// carried none (R3, R21).
    /// </summary>
    private void CopyExtendedData(TraceEvent data, ref AdmittedEvent admitted)
    {
        if (recordAccessor is null)
        {
            admitted.BeginExtendedData(ExtendedDataAvailability.NotRequested, 0);
            return;
        }

        EtwExtendedDataReader.Copy(recordAccessor.TryGetEventRecord(data), ref admitted);
    }

    private void ReportCallback(long startedTicks, in AdmittedEvent admitted) =>
        sink.OnCallbackCompleted(new(
            Stopwatch.GetTimestamp() - startedTicks,
            admitted.ExtendedData,
            admitted.ExtendedItemsCopied,
            admitted.ExtendedItemsOmitted));

    private void ReportRejectedCallback(long startedTicks) =>
        sink.OnCallbackCompleted(new(
            Stopwatch.GetTimestamp() - startedTicks,
            ExtendedDataAvailability.NotRequested,
            0,
            0));

    private static void ReadBoundedName(IntPtr body, int offset, int bodyLength, ref AdmittedEvent admitted)
    {
        Span<char> buffer = stackalloc char[AdmittedEvent.MaximumNameLength];
        int length = 0;
        bool truncated = false;
        for (int position = offset; position + 1 < bodyLength; position += 2)
        {
            char value = (char)(ushort)Marshal.ReadInt16(body, position);
            if (value == 0)
            {
                break;
            }

            if (length == buffer.Length)
            {
                truncated = true;
                break;
            }

            buffer[length++] = value;
        }

        if (length > 0)
        {
            admitted.SetName(buffer[..length], truncated);
        }
    }

    /// <summary>
    /// Copies an 8-bit name, widening each byte to one character. ASCII - every image file name the stop descriptor
    /// has been seen to carry - reads exactly; any other byte is kept as the character of the same code point rather
    /// than guessed through a code page the recording machine may not share.
    /// </summary>
    private static void ReadBoundedAnsiName(IntPtr body, int offset, int bodyLength, ref AdmittedEvent admitted)
    {
        Span<char> buffer = stackalloc char[AdmittedEvent.MaximumNameLength];
        int length = 0;
        bool truncated = false;
        for (int position = offset; position < bodyLength; position++)
        {
            byte value = Marshal.ReadByte(body, position);
            if (value == 0)
            {
                break;
            }

            if (length == buffer.Length)
            {
                truncated = true;
                break;
            }

            buffer[length++] = (char)value;
        }

        if (length > 0)
        {
            admitted.SetName(buffer[..length], truncated);
        }
    }

    /// <summary>
    /// The offset just past a SID, from the SID's own sub-authority count: revision, count, a six-byte authority,
    /// then four bytes per sub-authority. One byte is read, and the result is bounded by the record's length.
    /// </summary>
    internal static bool TryOffsetAfterSid(IntPtr body, int sidOffset, int bodyLength, out int offsetAfter)
    {
        offsetAfter = 0;
        if (sidOffset < 0 || bodyLength - sidOffset < AdmissionPlanCompiler.MinimumSidLength)
        {
            return false;
        }

        // Windows SID revision 1 is the only revision whose count and sub-authority layout this reader knows.
        if (Marshal.ReadByte(body, sidOffset) != 1)
        {
            return false;
        }

        int subAuthorities = Marshal.ReadByte(body, sidOffset + 1);
        if (subAuthorities > AdmissionPlanCompiler.MaximumSidSubAuthorities)
        {
            return false;
        }

        int after = sidOffset + AdmissionPlanCompiler.MinimumSidLength + (4 * subAuthorities);
        if (after > bodyLength)
        {
            return false;
        }

        offsetAfter = after;
        return true;
    }

    private static void ReadIdentifier(IntPtr body, int offset, ref AdmittedEvent admitted)
    {
        Span<byte> buffer = stackalloc byte[16];
        for (int index = 0; index < buffer.Length; index++)
        {
            buffer[index] = Marshal.ReadByte(body, offset + index);
        }

        admitted.SetIdentifier(new Guid(buffer));
    }

    private readonly record struct DeniedKey(Guid ProviderGuid, int EventId);
}
