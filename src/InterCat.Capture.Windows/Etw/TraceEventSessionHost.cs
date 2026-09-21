using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace InterCat.Capture.Windows;

/// <summary>
/// The live ETW host. It creates a uniquely named real-time session, never restarts or adopts an existing
/// one, and stops only the session it created (section 9.2, P14).
/// </summary>
public sealed class TraceEventSessionHost : IEtwSessionHost
{
    public bool? IsElevated => OperatingSystem.IsWindows() ? TraceEventSession.IsElevated() : false;

    public IReadOnlyList<string> ListActiveSessionNames()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            return TraceEventSession.GetActiveSessionNames();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new EtwSessionException($"Active ETW sessions could not be listed: {exception.Message}", exception);
        }
    }

    public IOwnedEtwSession CreateExclusive(OwnedSessionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!OperatingSystem.IsWindows())
        {
            throw new EtwSessionException("ETW capture is available only on Windows.");
        }

        TraceEventSession? created = null;
        try
        {
            created = new(
                plan.Identity.SessionName,
                TraceEventSessionOptions.Create | TraceEventSessionOptions.NoRestartOnCreate)
            {
                StopOnDispose = true,
                BufferQuantumKB = plan.BufferSizeKilobytes,
            };
            return new TraceEventOwnedSession(created, plan);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            created?.Dispose();
            throw new EtwSessionException(
                $"The session '{plan.Identity.SessionName}' could not be created: {exception.Message}",
                exception);
        }
    }

    private sealed class TraceEventOwnedSession(TraceEventSession session, OwnedSessionPlan plan) : IOwnedEtwSession
    {
        private readonly Dictionary<DeniedKey, bool> denied = BuildDenyList(plan);
        private ETWTraceEventSource? source;
        private long ordinal;
        private bool stopped;

        public string SessionName => session.SessionName;

        public ProviderEnablementResult Enable(ProviderEnablementRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            try
            {
                // The option lists default to null, so the capture-side scope is assigned rather than appended.
                var options = new TraceEventProviderOptions();
                if (request.EventIdsToEnable.Count > 0)
                {
                    // One ETW event-id filter is either an allow list or a deny list, never both, and a
                    // provider refuses the request when both are supplied. An allow list already excludes
                    // every other descriptor, and the deny list stays in the plan and in the callback so a
                    // denied descriptor is refused even if the source delivers it anyway (P28).
                    options.EventIDsToEnable = [.. request.EventIdsToEnable];
                }
                else if (request.EventIdsToDisable.Count > 0)
                {
                    options.EventIDsToDisable = [.. request.EventIdsToDisable];
                }

                session.EnableProvider(
                    request.ProviderGuid,
                    (TraceEventLevel)request.Level,
                    request.MatchAnyKeyword,
                    options);
                return new(request.SourceId, true, null);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return new(request.SourceId, false, $"{exception.GetType().Name}: {exception.Message}");
            }
        }

        public bool TryRequestCaptureState(ProviderEnablementRequest request, out string? failureReason)
        {
            ArgumentNullException.ThrowIfNull(request);
            try
            {
                session.CaptureState(request.ProviderGuid, request.MatchAnyKeyword, 0, null);
                failureReason = null;
                return true;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                failureReason = exception.Message;
                return false;
            }
        }

        public void Pump(EventAdmissionTable table, IAdmittedEventSink sink, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(table);
            ArgumentNullException.ThrowIfNull(sink);

            ETWTraceEventSource live = session.Source;
            source = live;
            void OnEvent(TraceEvent data) => Admit(table, sink, data);

            live.AllEvents += OnEvent;
            using CancellationTokenRegistration registration = cancellationToken.Register(live.StopProcessing);
            try
            {
                live.Process();
            }
            finally
            {
                live.AllEvents -= OnEvent;
            }
        }

        public void RequestStopProcessing()
        {
            try
            {
                source?.StopProcessing();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Stopping an already finished pump is not a defect.
            }
        }

        public SourceLossReading ReadLoss()
        {
            try
            {
                return new(session.EventsLost, source?.EventsLost ?? 0);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                throw new EtwSessionException($"Loss counters could not be read: {exception.Message}", exception);
            }
        }

        public void StopSession()
        {
            if (stopped)
            {
                return;
            }

            stopped = true;
            try
            {
                session.Stop(noThrow: true);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                throw new EtwSessionException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The owned session '{session.SessionName}' could not be stopped: {exception.Message}"),
                    exception);
            }
        }

        public void Dispose()
        {
            source?.Dispose();
            session.Dispose();
        }

        private static Dictionary<DeniedKey, bool> BuildDenyList(OwnedSessionPlan plan)
        {
            Dictionary<DeniedKey, bool> result = [];
            foreach (ProviderEnablementRequest request in plan.Providers)
            {
                foreach (int eventId in request.EventIdsToDisable)
                {
                    result[new(request.ProviderGuid, eventId)] = true;
                }
            }

            return result;
        }

        private void Admit(EventAdmissionTable table, IAdmittedEventSink sink, TraceEvent data)
        {
            sink.OnObserved();

            Guid provider = data.ProviderGuid;
            if (!table.IsKnownProvider(provider))
            {
                sink.OnOmitted(OmissionReason.UnrequestedProvider);
                return;
            }

            int eventId = (int)data.ID;
            if (denied.ContainsKey(new(provider, eventId)))
            {
                sink.OnOmitted(OmissionReason.DescriptorDenied);
                return;
            }

            AdmittedEventPlan? descriptorPlan = table.Find(provider, eventId, data.Version);
            if (descriptorPlan is null)
            {
                if (table.HasDescriptor(provider, eventId))
                {
                    sink.OnUndecodable(UndecodableReason.UnknownDescriptorVersion);
                }
                else
                {
                    sink.OnOmitted(OmissionReason.DescriptorNotAdmitted);
                }

                return;
            }

            if (data.EventDataLength < descriptorPlan.MinimumBodyLength)
            {
                sink.OnUndecodable(UndecodableReason.BodyShorterThanSchema);
                return;
            }

            if (data.PointerSize != descriptorPlan.PointerSize)
            {
                // Offsets were computed for another pointer width, so no field is read at a guessed place.
                sink.OnUndecodable(UndecodableReason.PointerWidthMismatch);
                return;
            }

            AdmittedEvent admitted = default;
            admitted.SourceIndex = descriptorPlan.SourceIndex;
            admitted.EventId = eventId;
            admitted.Version = data.Version;
            admitted.Opcode = (int)data.Opcode;
#pragma warning disable CS0618 // The raw source clock reading is required by I8; the relative-millisecond
            // alternative is a derived value and cannot preserve the original encoding.
            admitted.TimestampQpc = data.TimeStampQPC;
#pragma warning restore CS0618
            admitted.TimestampUtcTicks = data.TimeStamp.ToUniversalTime().Ticks;
            admitted.ActivityId = data.ActivityID;
            admitted.RelatedActivityId = data.RelatedActivityID;
            admitted.HeaderProcessId = data.ProcessID;
            admitted.HeaderThreadId = data.ThreadID;
            admitted.ProcessorNumber = data.ProcessorNumber;
            admitted.RecordOrdinal = ++ordinal;

            IntPtr body = data.DataStart;
            if (body == IntPtr.Zero)
            {
                sink.OnUndecodable(UndecodableReason.AdmissionReadFailed);
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
                        return;
                }
            }

            _ = sink.Admit(admitted);
        }

        /// <summary>
        /// Copies a UTF-16 name out of callback-owned memory into the record's inline buffer. The read is
        /// bounded by the body length and by the buffer, and truncation is recorded rather than hidden (R9, I21).
        /// </summary>
        private static void ReadBoundedName(IntPtr body, int offset, int bodyLength, ref AdmittedEvent admitted)
        {
            Span<char> buffer = stackalloc char[AdmittedEvent.MaximumNameLength];
            int length = 0;
            bool truncated = false;
            for (int position = offset; position + 1 < bodyLength; position += 2)
            {
                char value = (char)(ushort)Marshal.ReadInt16(body, position);
                if (value == ' ')
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

        /// <summary>Copies a 16-byte identifier out of callback memory with a bounded, fixed-size read.</summary>
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
}
