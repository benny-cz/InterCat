using System.Diagnostics;
using InterCat.Capture.Recording;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;

namespace InterCat.Capture.Journal.Tests;

/// <summary>
/// Evidence-only recordings made with a scripted host in place of ETW, as the broker publishes them (ADR-027), so a
/// follower, or a viewer finishing one, is tested against real evidence sessions without elevation. Linked into the
/// Desktop test projects as well.
/// </summary>
internal static class EvidenceRecordings
{
    public static readonly Guid Provider = Guid.Parse("7dd42a49-5329-4832-8dfd-43d979153a88");

    /// <summary>The boot a test calibration names.</summary>
    public static readonly Guid BootToken = Guid.Parse("b0071111-2222-4333-8444-555555555555");

    /// <summary>
    /// A calibration source for a scripted capture: this process's counter, which the scripted records are stamped with,
    /// against the wall clock, and a fixed boot; it touches no registry.
    /// </summary>
    public static ClockCalibrationSource Calibration() => new()
    {
        WallClock = "test-wall-clock",
        Sample = () => new()
        {
            NativeTicks = Stopwatch.GetTimestamp(),
            Utc = DateTimeOffset.UtcNow,
            AcquisitionUncertaintyNanoseconds = 1_000,
        },
        Boot = () => (BootToken, 42),
    };

    /// <summary>
    /// Records bursts of records as evidence only, publishing every 100 ms, and finalizes it. Each burst after the first
    /// starts at a record <paramref name="bursts"/> names - by default the third - and waits for the bursts before it to be
    /// published, so the recording has a chunk of its own for every burst but the last, which ends with its finality: a
    /// fixed pause did not guarantee that, because under load one publication could take every record and the finality.
    /// </summary>
    public static async Task<LiveRecordingResult> RecordEvidence(
        string directory,
        int[] ordinals,
        bool failLossRead = false,
        ClockCalibrationSource? calibration = null,
        IReadOnlyList<CollectorProcessV1>? collectors = null,
        IReadOnlyList<int>? bursts = null)
    {
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory), Guid.NewGuid(), "live-tests");
        var host = new ScriptedHost { FailLossRead = failLossRead };
        long now = Stopwatch.GetTimestamp();
        int[] starts = [.. bursts ?? [2]];
        for (int index = 0; index < ordinals.Length; index++)
        {
            int burst = Array.IndexOf(starts, index);
            if (burst >= 0)
            {
                host.PauseUntil(
                    () => store.Current is { } current
                        && current.Dependencies.Count(dependency => dependency.Kind == StoreDependencyKind.Journal) > burst,
                    TimeSpan.FromSeconds(30));
            }

            var admitted = new AdmittedEvent
            {
                SourceIndex = 0,
                EventId = 10,
                Version = 0,
                TimestampQpc = now + index,
                TimestampUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
                RecordOrdinal = ordinals[index],
            };
            admitted.SetSlot(0, 100 * (index + 1));
            admitted.SetSlot(1, 0x7000 + index);
            host.Admit(admitted);
        }

        return await LiveSessionRecorder.RecordAsync(
            Plan(withFields: true),
            host,
            store,
            _ => host.Delivered.Task,
            DateTimeOffset.UtcNow,
            publishEvery: TimeSpan.FromMilliseconds(100),
            output: LiveRecordingOutput.EvidenceOnly,
            calibration: calibration,
            collectors: collectors);
    }

    /// <summary>
    /// Turns a finalized evidence recording back into what a broker that died before finalizing leaves: its current
    /// pointer names the generation before the last, which the retained pointer keeps, so its manifest was never
    /// removed. Finality comes only with the last chunk, so that generation has none. Returns its manifest.
    /// </summary>
    public static SessionManifestV1 RewindToUnfinalized(string evidenceDirectory)
    {
        SessionPointerV1 previous = System.Text.Json.JsonSerializer.Deserialize<SessionPointerV1>(
            File.ReadAllText(Path.Combine(evidenceDirectory, SessionPointerV1.PreviousFileName)), SessionManifestV1.Json)!;
        SessionManifestV1 manifest = System.Text.Json.JsonSerializer.Deserialize<SessionManifestV1>(
            File.ReadAllText(Path.Combine(evidenceDirectory, previous.ManifestName)), SessionManifestV1.Json)!;
        File.WriteAllText(
            Path.Combine(evidenceDirectory, SessionPointerV1.FileName),
            System.Text.Json.JsonSerializer.Serialize(SessionPointerV1.For(manifest), SessionManifestV1.Json));
        return manifest;
    }

    public static OwnedSessionPlan Plan(bool withFields = false)
    {
        var request = new ProviderEnablementRequest
        {
            SourceId = "etw/manifest/Sample",
            ProviderName = "Sample-Network",
            ProviderGuid = Provider,
            Level = 4,
            MatchAnyKeyword = 0x10,
            EventIdsToEnable = [10],
        };

        return new()
        {
            Identity = CaptureSessionIdentity.Create("test", 4242),
            Providers = [request],
            Sources =
            [
                new()
                {
                    SourceId = request.SourceId,
                    ProviderGuid = Provider,
                    SourceIndex = 0,
                    Events =
                    [
                        new()
                        {
                            SourceIndex = 0,
                            ProviderGuid = Provider,
                            EventId = 10,
                            Version = 0,
                            Name = "sent",
                            Mechanism = Mechanism.Tcp,
                            Layer = ObservationLayer.Transport,
                            Kind = ObservationKind.Send,
                            Direction = Direction.Outbound,
                            MinimumBodyLength = withFields ? 12 : 0,
                            SchemaFingerprint = "sha256:fixture-live-recording",
                            BodyPolicy = CaptureBodyAdmissionPolicies.MetadataOnly,
                            Slots = withFields
                                ?
                                [
                                    new("size", FieldRole.ByteCount, 0, 4, MeasurementUnit.Bytes,
                                        ByteDomain.TransportObserved, SlotTransform.None),
                                    new("connid", FieldRole.CorrelationKey, 4, 8, null, null, SlotTransform.None)
                                    {
                                        SourceField = SourceField.ConnectionId,
                                    },
                                ]
                                : [],
                            FieldReport = withFields
                                ?
                                [
                                    new("size", FieldAvailability.Present, FieldRole.ByteCount,
                                        Unit: MeasurementUnit.Bytes, ByteDomain: ByteDomain.TransportObserved),
                                    new("connid", FieldAvailability.Present, FieldRole.CorrelationKey),
                                ]
                                : [],
                        },
                    ],
                    Diagnostics = [],
                },
            ],
        };
    }
}

/// <summary>A host whose one session delivers a scripted sequence of outcomes, then waits to be stopped.</summary>
internal sealed class ScriptedHost : IEtwSessionHost
{
    private readonly List<Action<IAdmittedEventSink>> script = [];

    public bool FailCreate { get; init; }

    public bool FailLossRead { get; init; }

    /// <summary>Completes once every scripted outcome reached the sink.</summary>
    public TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Whether the capture asked its session to stop, or to stop delivering.</summary>
    public bool StopRequested { get; private set; }

    public bool? IsElevated => true;

    public void Admit(AdmittedEvent admitted) =>
        Admit(admitted, new DeliveredRecord(EvidenceRecordings.Provider, admitted.EventId, admitted.Version, admitted.TimestampQpc));

    /// <summary>Delivers a record as the admitter names it, as a classic record is by its class and opcode.</summary>
    public void Admit(AdmittedEvent admitted, DeliveredRecord delivered) => script.Add(sink =>
    {
        sink.OnObserved(in delivered);
        _ = sink.Admit(admitted);
    });

    public void Pause(TimeSpan pause) => script.Add(_ => Thread.Sleep(pause));

    /// <summary>Holds delivery until <paramref name="condition"/> holds, and refuses to wait past <paramref name="timeout"/>.</summary>
    public void PauseUntil(Func<bool> condition, TimeSpan timeout) => script.Add(_ =>
    {
        if (!SpinWait.SpinUntil(condition, timeout))
        {
            throw new TimeoutException($"The scripted capture waited {timeout.TotalSeconds:N0} s for a condition that never held.");
        }
    });

    public void Omit(DeliveredRecord delivered, OmissionReason reason) => script.Add(sink =>
    {
        sink.OnObserved(in delivered);
        sink.OnOmitted(reason, in delivered);
    });

    public IReadOnlyList<string> ListActiveSessionNames() => [];

    public IOwnedEtwSession CreateExclusive(OwnedSessionPlan plan) =>
        FailCreate ? throw new EtwSessionException("the session could not be created in this test") : new Session(this, plan);

    private sealed class Session(ScriptedHost host, OwnedSessionPlan plan) : IOwnedEtwSession
    {
        private volatile bool stopRequested;

        public string SessionName => plan.Identity.SessionName;

        public ProviderEnablementResult Enable(ProviderEnablementRequest request) => new(request.SourceId, true, null);

        public ProviderEnablementResult EnableKernelFlags(string sourceId, ulong flags) => new(sourceId, true, null);

        public bool TryRequestCaptureState(ProviderEnablementRequest request, out string? failureReason)
        {
            failureReason = null;
            return true;
        }

        public void Pump(EventAdmissionTable table, IAdmittedEventSink sink, CancellationToken cancellationToken)
        {
            foreach (Action<IAdmittedEventSink> step in host.script)
            {
                step(sink);
            }

            host.Delivered.TrySetResult();
            while (!stopRequested && !cancellationToken.IsCancellationRequested)
            {
                Thread.Sleep(5);
            }
        }

        public void RequestStopProcessing()
        {
            stopRequested = true;
            host.StopRequested = true;
        }

        public SourceLossReading ReadLoss() =>
            host.FailLossRead ? throw new EtwSessionException("the counters could not be read in this test") : new(0, 0);

        // A stopped session ends its pump, as ETW ends a real-time consumer's once it has delivered the last buffers.
        public void StopSession()
        {
            stopRequested = true;
            host.StopRequested = true;
        }

        public void Dispose()
        {
        }
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "intercat-live-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
