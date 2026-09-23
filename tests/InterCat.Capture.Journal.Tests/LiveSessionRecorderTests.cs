using System.Diagnostics;
using InterCat.Capture.Journal;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;

namespace InterCat.Capture.Journal.Tests;

/// <summary>
/// The live recorder publishes an owned capture's admitted records as a session a reader opens, with a live coverage
/// epoch built from the capture's own counters (IC-014). A scripted host stands in for ETW, so no elevation is needed.
/// </summary>
public sealed class LiveSessionRecorderTests
{
    private static readonly Guid Provider = Guid.Parse("7dd42a49-5329-4832-8dfd-43d979153a88");
    private static readonly Guid Unrequested = Guid.Parse("11111111-2222-4333-8444-555555555555");

    [Fact(DisplayName = "R21: a live recording publishes its admitted records and what its sources delivered and lost")]
    public async Task RecordingPublishesASessionWithItsCoverage()
    {
        using var directory = new TemporaryDirectory();
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory.Path), Guid.NewGuid(), "live-tests");
        var host = new ScriptedHost();
        long now = Stopwatch.GetTimestamp();
        for (int index = 0; index < 3; index++)
        {
            host.Admit(new AdmittedEvent
            {
                SourceIndex = 0,
                EventId = 10,
                Version = 0,
                TimestampQpc = now + index,
                TimestampUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
                RecordOrdinal = index + 1,
            });
        }

        host.Omit(new DeliveredRecord(Unrequested, 99, 0, now + 10), OmissionReason.UnrequestedProvider);

        LiveRecordingResult result = await LiveSessionRecorder.RecordAsync(
            Plan(),
            host,
            store,
            _ => host.Delivered.Task,
            DateTimeOffset.UtcNow);

        Assert.True(result.Start.Started);
        Assert.Equal(3, result.JournaledRecords);
        Assert.Equal(1, result.Generation!.Manifest.Generation);

        // The published generation reopens with its journal, rows and ledger, like an imported one.
        SessionStore reopened = SessionStore.OpenExisting(LocalOwnedDirectory.Open(directory.Path));
        SessionManifestV1 manifest = reopened.Current!;
        Assert.Equal(3, manifest.Boundary.CommittedRecords);
        Assert.Equal(3, SessionSegments.Names(manifest).Sum(name => SessionSegments.Open(reopened.Root, manifest, name).RowCount));
        CoverageLedgerV1 ledger = SessionSegments.CoverageLedger(reopened.Root, manifest)!;
        CoverageEpochV1 epoch = Assert.Single(ledger.Epochs);
        Assert.Equal(CoverageAcquisition.LiveCapture, epoch.Acquisition);
        Assert.Equal("Sample-Network", Assert.Single(epoch.Collected).ProviderName);
        Assert.Equal(
            [(Provider, (int?)10, 3L, 3L, 0L), (Unrequested, (int?)null, 1L, 0L, 1L)],
            epoch.Deliveries.OrderBy(delivery => delivery.ProviderId == Unrequested)
                .Select(delivery => (delivery.ProviderId, delivery.EventId, delivery.Delivered, delivery.Admitted, delivery.Omitted)));
        Assert.Equal(
            [LossLayer.SourceSession, LossLayer.ConsumerBuffers, LossLayer.CallbackQueue, LossLayer.Storage],
            epoch.Losses.Select(loss => loss.Layer));
        Assert.All(epoch.Losses, loss => Assert.Equal(0, loss.Lost));

        // One capture per session: a second recording into it is refused before anything starts.
        await Assert.ThrowsAsync<InvalidOperationException>(() => LiveSessionRecorder.RecordAsync(
            Plan(), new ScriptedHost(), reopened, _ => Task.CompletedTask, DateTimeOffset.UtcNow));
    }

    [Fact(DisplayName = "R21: a recording published in chunks can be followed, and its last generation holds every chunk")]
    public async Task ChunkedRecordingPublishesAsItGoes()
    {
        using var directory = new TemporaryDirectory();
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory.Path), Guid.NewGuid(), "live-tests");
        var host = new ScriptedHost();
        long now = Stopwatch.GetTimestamp();
        for (int index = 0; index < 4; index++)
        {
            if (index == 2)
            {
                host.Pause(TimeSpan.FromMilliseconds(400));
            }

            host.Admit(new AdmittedEvent
            {
                SourceIndex = 0,
                EventId = 10,
                Version = 0,
                TimestampQpc = now + index,
                TimestampUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
                RecordOrdinal = index + 1,
            });
        }

        LiveRecordingResult result = await LiveSessionRecorder.RecordAsync(
            Plan(),
            host,
            store,
            _ => host.Delivered.Task,
            DateTimeOffset.UtcNow,
            publishEvery: TimeSpan.FromMilliseconds(100));

        // A generation was published while the capture was still recording, and each carried every chunk before it.
        Assert.Equal(4, result.JournaledRecords);
        Assert.InRange(result.Publications, 2, 3);
        SessionStore reopened = SessionStore.OpenExisting(LocalOwnedDirectory.Open(directory.Path));
        SessionManifestV1 manifest = reopened.Current!;
        Assert.Equal(result.Publications, manifest.Generation);
        Assert.Equal(result.Publications, manifest.Dependencies.Count(dependency => dependency.Kind == StoreDependencyKind.Journal));
        Assert.Equal(4, SessionSegments.Names(manifest).Sum(name => SessionSegments.Open(reopened.Root, manifest, name).RowCount));

        // The ledger describes the whole capture, so there is one, published with the last chunk.
        Assert.Single(manifest.Dependencies, dependency => dependency.Kind == StoreDependencyKind.CoverageLedger);
        Assert.Equal(4, SessionSegments.CoverageLedger(reopened.Root, manifest)!.Epochs[0].Deliveries.Sum(delivery => delivery.Admitted));

        // Re-derivation replays one journal at this version, so it names the chunks rather than guessing over them.
        JournalRederivationReadiness readiness = JournalRederivation.Assess(manifest);
        Assert.False(readiness.CanAttempt);
        Assert.Contains("chunks", readiness.Explanation, StringComparison.Ordinal);

        // Nor does a journal-prefix release guess which chunk a boundary falls in: it refuses, naming them.
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => JournalRetention.Preview(reopened, 1));
        Assert.Contains("journal chunks", refused.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R21: a live capture that cannot start publishes nothing and leaves its session empty")]
    public async Task ACaptureThatCannotStartPublishesNothing()
    {
        using var directory = new TemporaryDirectory();
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory.Path), Guid.NewGuid(), "live-tests");

        LiveRecordingResult result = await LiveSessionRecorder.RecordAsync(
            Plan(),
            new ScriptedHost { FailCreate = true },
            store,
            _ => Task.CompletedTask,
            DateTimeOffset.UtcNow);

        Assert.False(result.Start.Started);
        Assert.Null(result.Generation);
        Assert.Null(store.Current);
    }

    [Fact(DisplayName = "R21: a recording whose loss counters could not be read publishes its records and no coverage claim")]
    public async Task UnreadableLossCountersPublishNoLedger()
    {
        using var directory = new TemporaryDirectory();
        SessionStore store = SessionStore.Open(LocalOwnedDirectory.Open(directory.Path), Guid.NewGuid(), "live-tests");
        var host = new ScriptedHost { FailLossRead = true };
        host.Admit(new AdmittedEvent
        {
            SourceIndex = 0,
            EventId = 10,
            Version = 0,
            TimestampQpc = Stopwatch.GetTimestamp(),
            TimestampUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
            RecordOrdinal = 1,
        });

        LiveRecordingResult result = await LiveSessionRecorder.RecordAsync(
            Plan(), host, store, _ => host.Delivered.Task, DateTimeOffset.UtcNow);

        // The records are the capture's evidence either way; what it lost cannot be claimed, so coverage stays unknown.
        Assert.Equal(1, result.JournaledRecords);
        Assert.NotNull(result.Generation);
        Assert.Null(result.Coverage);
        Assert.Null(SessionSegments.CoverageLedger(store.Root, store.Current!));
    }

    [Fact(DisplayName = "R21: a record the full queue dropped is callback-queue loss, not a delivery of its descriptor")]
    public void QueueDropsAreLossNotDeliveries()
    {
        // A plan with no provider request, as an import's is, admitting the catalog's network source.
        OwnedSessionPlan requested = Plan();
        OwnedSessionPlan unnamed = requested with
        {
            Providers = [],
            Sources = [requested.Sources[0] with { SourceId = WindowsSourceCatalog.KernelNetworkSourceId }],
        };
        var tally = new CaptureCoverageTally(unnamed, CoverageAcquisition.LiveCapture);
        var delivered = new DeliveredRecord(Provider, 10, 0, 1_000);
        for (int index = 0; index < 3; index++)
        {
            tally.Delivered(in delivered);
        }

        tally.Admitted(Provider, 10, 0, queued: true);
        tally.Admitted(Provider, 10, 0, queued: true);
        tally.Admitted(Provider, 10, 0, queued: false);

        CoverageLedgerV1 ledger = tally.ToLedger(
        [
            new() { Layer = LossLayer.SourceSession, Lost = 0 },
            new() { Layer = LossLayer.ConsumerBuffers, Lost = 0 },
            new() { Layer = LossLayer.CallbackQueue, Lost = tally.QueueDrops },
            new() { Layer = LossLayer.Storage, Lost = 0 },
        ]);

        CoverageDeliveryV1 delivery = Assert.Single(Assert.Single(ledger.Epochs).Deliveries);
        Assert.Equal((2L, 2L), (delivery.Delivered, delivery.Admitted));
        Assert.Equal(1, tally.QueueDrops);

        // With no provider request to name it, the source catalog names the provider admission compiled.
        Assert.Equal("Microsoft-Windows-Kernel-Network", Assert.Single(ledger.Epochs[0].Collected).ProviderName);
    }

    private static OwnedSessionPlan Plan()
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
                            MinimumBodyLength = 0,
                            SchemaFingerprint = "sha256:fixture-live-recording",
                            BodyPolicy = CaptureBodyAdmissionPolicies.MetadataOnly,
                            Slots = [],
                            FieldReport = [],
                        },
                    ],
                    Diagnostics = [],
                },
            ],
        };
    }

    /// <summary>A host whose one session delivers a scripted sequence of outcomes, then waits to be stopped.</summary>
    private sealed class ScriptedHost : IEtwSessionHost
    {
        private readonly List<Action<IAdmittedEventSink>> script = [];

        public bool FailCreate { get; init; }

        public bool FailLossRead { get; init; }

        /// <summary>Completes once every scripted outcome reached the sink.</summary>
        public TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool? IsElevated => true;

        public void Admit(AdmittedEvent admitted) => script.Add(sink =>
        {
            sink.OnObserved(new DeliveredRecord(Provider, admitted.EventId, admitted.Version, admitted.TimestampQpc));
            _ = sink.Admit(admitted);
        });

        public void Pause(TimeSpan pause) => script.Add(_ => Thread.Sleep(pause));

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

            public void RequestStopProcessing() => stopRequested = true;

            public SourceLossReading ReadLoss() =>
                host.FailLossRead ? throw new EtwSessionException("the counters could not be read in this test") : new(0, 0);

            public void StopSession()
            {
            }

            public void Dispose()
            {
            }
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "intercat-live-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
