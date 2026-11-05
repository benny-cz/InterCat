using System.Diagnostics;
using System.Globalization;
using InterCat.Capture.Windows;
using InterCat.Domain;

namespace InterCat.SyncProbe;

/// <summary>
/// The product's own capture path on this machine's ETW, for a kernel group with no flag of its own (ADR-082's third
/// addendum): an owned capture session, through the product's session host, whose plan holds only the object manager's
/// handle group - admitting none of its events, as it is inventoried - while FX-SYNC-001 runs. It counts what the session
/// delivered by class and opcode and what it omitted, reads the session's group masks while it records, and says whether
/// the capture started, stopped its own session and left none behind.
/// </summary>
internal static class SessionCheck
{
    public static async Task<Dictionary<string, object?>> RunAsync(string workload, string truth)
    {
        CaptureSessionIdentity identity = CaptureSessionIdentity.Create("sync-check", Environment.ProcessId);
        var handles = new SourceAdmissionPlan
        {
            SourceId = WindowsSourceCatalog.KernelObjectHandlesSourceId,
            ProviderGuid = WindowsSourceCatalog.ObjectManagerEventClass,
            SourceIndex = 0,
            Events = [],
            Diagnostics = [],
        };
        var plan = new OwnedSessionPlan
        {
            Identity = identity,
            Sources = [handles],
            Providers = [],
            MaximumDuration = TimeSpan.FromMinutes(2),
        };

        var observer = new Observer();
        string[]? masks = null;
        CaptureStartResult start;
        CaptureStopResult? stop = null;
        await using (var session = new OwnedCaptureSession(plan, new TraceEventSessionHost(), observer: observer))
        {
            start = await session.StartAsync().ConfigureAwait(false);
            if (start.Started)
            {
                masks = GroupMask.Read(identity.SessionName);
                await Task.Delay(1_000).ConfigureAwait(false);
                using (Process run = Process.Start(new ProcessStartInfo(workload, ["sync-objects", "--truth", truth, "--rounds", "64", "--hold-ms", "5"])
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                })!)
                {
                    await run.WaitForExitAsync().ConfigureAwait(false);
                }

                await Task.Delay(1_500).ConfigureAwait(false);
                stop = await session.StopAsync().ConfigureAwait(false);
            }
        }

        return new()
        {
            ["question"] = "Whether a capture whose plan holds the object manager's handle group, a kernel group with no flag of "
                + "its own, starts its private system logger with the kernel's process flag, sets the group's mask on it by "
                + "its handle, and is delivered the object manager's handle records through the product's own session.",
            ["started"] = start.Started,
            ["failureReason"] = start.FailureReason,
            ["enablements"] = start.Providers.Select(static result => new { result.SourceId, result.Enabled, result.FailureReason }).ToArray(),
            ["planKernelFlags"] = "0x" + plan.KernelFlags.ToString("X", CultureInfo.InvariantCulture),
            ["planGroupMasks"] = plan.KernelGroupMasks.Select(static mask => "0x" + mask.ToString("X8", CultureInfo.InvariantCulture)).ToArray(),
            ["masksReadWhileRecording"] = masks,
            ["deliveredByClass"] = observer.DeliveredByClass(),
            ["objectManagerByOpcode"] = observer.ObjectManagerByOpcode(),
            ["admitted"] = observer.AdmittedCount,
            ["observedRecords"] = stop?.Health.ObservedRecords,
            ["omittedNotAdmitted"] = stop?.Health.PolicyOmissionsDescriptorNotAdmitted,
            ["omittedUnrequested"] = stop?.Health.PolicyOmissionsUnrequestedProvider,
            ["lossFree"] = stop?.Health.IsLossFree,
            ["omittedByReason"] = observer.OmittedByReason(),
            ["stopped"] = stop is not null,
            ["providersStopped"] = stop?.ProvidersStopped,
            ["deliveredThroughStop"] = stop?.DeliveredThroughStop,
            ["eventsLost"] = stop?.Health.ProviderReportedEventLoss,
            ["buffersLost"] = stop?.Health.ConsumerReportedBufferLoss,
            ["sessionLeftBehind"] = GroupMask.Read(identity.SessionName) is not null,
        };
    }

    /// <summary>Counts every delivery by class and the object manager's by opcode, and every omission by its reason.</summary>
    private sealed class Observer : IDeliveryObserver
    {
        private readonly Lock gate = new();
        private readonly Dictionary<Guid, long> byClass = [];
        private readonly Dictionary<int, long> byOpcode = [];
        private readonly Dictionary<OmissionReason, long> omitted = [];

        public long AdmittedCount { get; private set; }

        public void Delivered(in DeliveredRecord delivered)
        {
            lock (gate)
            {
                byClass[delivered.ProviderId] = byClass.GetValueOrDefault(delivered.ProviderId) + 1;
                if (delivered.ProviderId == WindowsSourceCatalog.ObjectManagerEventClass)
                {
                    byOpcode[delivered.Opcode] = byOpcode.GetValueOrDefault(delivered.Opcode) + 1;
                }
            }
        }

        public void Admitted(Guid providerId, int eventId, int version, int opcode, bool queued)
        {
            lock (gate)
            {
                AdmittedCount++;
            }
        }

        public void Omitted(OmissionReason reason, in DeliveredRecord delivered)
        {
            lock (gate)
            {
                omitted[reason] = omitted.GetValueOrDefault(reason) + 1;
            }
        }

        public void Undecodable(UndecodableReason reason, in DeliveredRecord delivered)
        {
        }

        public void Gated(GateOmission reason, in DeliveredRecord delivered)
        {
        }

        public Dictionary<string, long> DeliveredByClass()
        {
            lock (gate)
            {
                return byClass.ToDictionary(pair => WindowsSourceCatalog.ClassicEventClassName(pair.Key) ?? pair.Key.ToString("D"), pair => pair.Value);
            }
        }

        public Dictionary<string, long> ObjectManagerByOpcode()
        {
            lock (gate)
            {
                return byOpcode.OrderBy(pair => pair.Key).ToDictionary(pair => pair.Key.ToString(CultureInfo.InvariantCulture), pair => pair.Value);
            }
        }

        public Dictionary<string, long> OmittedByReason()
        {
            lock (gate)
            {
                return omitted.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value);
            }
        }
    }
}
