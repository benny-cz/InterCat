using System.Reflection;
using InterCat.Capture.Recording;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;

namespace InterCat.CaptureBroker.Tests;

/// <summary>
/// The capture callback runs on ETW's delivery thread for every record a machine raises, so whatever it does costs every
/// process being observed (R9, P12, §9.3). It must copy what admission keeps into bounded pooled buffers and return: no
/// query, store, file, network, name or symbol lookup, no decoding beyond a descriptor and shape check, no wait, and no
/// allocation but from a pool. The broker gives it the capture's queue and the coverage tally; these tests walk everything
/// that can run from the admitter through both, and measure what the queue, counters and tally allocate per record.
/// </summary>
public sealed class CallbackPathTests
{
    /// <summary>Each allocation the callback can make outside a throw, and why it is bounded: none recurs per record.</summary>
    private static readonly string[] ReviewedAllocations =
    [
        // The coverage tally's entry for a descriptor and its counts of undecodable records by reason, made the first time
        // the descriptor is delivered: at most the ledger's 4,096 descriptors, and past them one overflow entry.
        "InterCat.Capture.Recording.CaptureCoverageTally+Entry..ctor: new System.Collections.Generic.Dictionary",
        "InterCat.Capture.Recording.CaptureCoverageTally.EntryFor: new InterCat.Capture.Recording.CaptureCoverageTally+Entry",

        // The capture's clock, checked against its first record once per capture, and a refusal said once.
        "InterCat.Capture.Windows.CaptureClockEvidence.<Clone>$: new InterCat.Capture.Windows.CaptureClockEvidence",
        "InterCat.Capture.Windows.EtwSourceClock.Check: System.Runtime.CompilerServices.DefaultInterpolatedStringHandler.ToStringAndClear",
        "InterCat.Capture.Windows.EtwSourceClock.Check: System.String.Concat",
        "InterCat.Capture.Windows.OwnedCaptureSession.AddDegradation: System.Linq.Enumerable.Contains",
    ];

    /// <summary>Each lock InterCat's own code takes on the callback, and why it is short: none is held across a wait.</summary>
    private static readonly string[] ReviewedLocks =
    [
        // The coverage tally's, which a publication holds only while it reads the tally into a ledger.
        "InterCat.Capture.Recording.CaptureCoverageTally.Admitted: Lock",
        "InterCat.Capture.Recording.CaptureCoverageTally.Delivered: Lock",
        "InterCat.Capture.Recording.CaptureCoverageTally.Omitted: Lock",
        "InterCat.Capture.Recording.CaptureCoverageTally.Undecodable: Lock",

        // The capture's own, taken once per capture to check its clock and to say a refusal.
        "InterCat.Capture.Windows.OwnedCaptureSession.AddDegradation: Lock",
        "InterCat.Capture.Windows.OwnedCaptureSession.ConfirmSourceClock: Lock",
    ];

    [Fact(DisplayName = "P12: the capture callback reaches no query, store, file, network, name or symbol lookup, decoder or wait, and only reviewed locks")]
    public void TheCallbackReachesNothingThatBlocks()
    {
        CallbackReach walk = Walk();
        Assert.True(walk.Forbidden.Count == 0, string.Join(Environment.NewLine, walk.Forbidden));
        Assert.Equal(ReviewedLocks.Order(StringComparer.Ordinal), walk.Locks);
    }

    [Fact(DisplayName = "P12: the capture callback allocates from a pool, and otherwise only where a reviewed bound holds")]
    public void TheCallbackAllocatesOnlyWhereReviewed()
    {
        CallbackReach walk = Walk();
        Assert.Equal(ReviewedAllocations.Order(StringComparer.Ordinal), walk.Allocations);
    }

    [Fact(DisplayName = "P12: once a capture's descriptors have been seen, a callback's queue, counters and coverage tally allocate nothing")]
    public async Task TheSinkAllocatesNothingPerRecord()
    {
        OwnedSessionPlan plan = Plan();
        using var host = new MeasuringEtwHost(plan.Sources[0].ProviderGuid, records: 4_096);
        var tally = new CaptureCoverageTally(plan, CoverageAcquisition.LiveCapture);
        await using var capture = new OwnedCaptureSession(plan, host, observer: tally);
        Assert.True((await capture.StartAsync()).Started);
        Assert.True(host.WaitUntilWarmed(TimeSpan.FromSeconds(30)), "The pump never finished its first records.");

        // Then the journal's writer drains the queue as the broker's does, so measured records are queued as well as
        // dropped.
        long drained = 0;
        Task drain = Task.Run(async () =>
        {
            await foreach (AdmittedEvent _ in capture.Records.ReadAllAsync())
            {
                drained++;
            }
        });

        host.StartMeasuring();
        Assert.True(host.WaitUntilMeasured(TimeSpan.FromSeconds(30)), "The pump never finished its measured records.");
        _ = await capture.StopAsync();
        await drain;

        Assert.Equal(0, host.AllocatedBytes);
        Assert.True(drained > 0);
    }

    private static CallbackReach Walk()
    {
        Assembly windows = typeof(OwnedCaptureSession).Assembly;
        Type admitter = windows.GetType("InterCat.Capture.Windows.TraceEventRecordAdmitter", throwOnError: true)!;
        CallbackReach walk = CallbackReach.From(
            [admitter.GetMethod("Admit", BindingFlags.Instance | BindingFlags.Public)!],
            windows, typeof(CaptureCoverageTally).Assembly, typeof(CaptureId).Assembly);

        // The walk reached the admitter's copying, the queue and the tally: a refactoring that hid them from it would
        // otherwise pass by reaching nothing.
        Assert.Contains("InterCat.Capture.Windows.TraceEventRecordAdmitter.ReadContent", walk.Reached);
        Assert.Contains("InterCat.Capture.Windows.EtwExtendedDataReader.Copy", walk.Reached);
        Assert.Contains("InterCat.Capture.Windows.OwnedCaptureSession+QueueSink.Admit", walk.Reached);
        Assert.Contains("InterCat.Capture.Windows.OwnedCaptureSession.Enqueue", walk.Reached);
        Assert.Contains("InterCat.Capture.Recording.CaptureCoverageTally.Admitted", walk.Reached);
        return walk;
    }

    private static OwnedSessionPlan Plan()
    {
        var request = new ProviderEnablementRequest
        {
            SourceId = "etw/manifest/Sample",
            ProviderName = "Sample",
            ProviderGuid = Guid.Parse("7dd42a49-5329-4832-8dfd-43d979153a88"),
            Level = 4,
            MatchAnyKeyword = 0x10,
            EventIdsToEnable = [10],
        };

        var source = new SourceAdmissionPlan
        {
            SourceId = request.SourceId,
            ProviderGuid = request.ProviderGuid,
            SourceIndex = 0,
            Events =
            [
                new()
                {
                    SourceIndex = 0,
                    ProviderGuid = request.ProviderGuid,
                    EventId = 10,
                    Version = 0,
                    Name = "sent",
                    Mechanism = Mechanism.Tcp,
                    Layer = ObservationLayer.Transport,
                    Kind = ObservationKind.Send,
                    Direction = Direction.Outbound,
                    MinimumBodyLength = 8,
                    SchemaFingerprint = "sha256:fixture-callback-path",
                    BodyPolicy = CaptureBodyAdmissionPolicies.MetadataOnly,
                    Slots = [],
                    FieldReport = [],
                },
            ],
            Diagnostics = [],
        };

        return new()
        {
            Identity = CaptureSessionIdentity.Create("callback-path", 4242),
            Providers = [request],
            Sources = [source],
            QueueCapacityRecords = 1_024,
        };
    }

    /// <summary>
    /// A session whose pump delivers its records twice. The first time nothing drains the queue, so every descriptor is
    /// seen and the queue grows to all it holds, as a burst grows it once in a capture; the second time the pump's thread
    /// counts what it allocates while the queue is drained.
    /// </summary>
    private sealed class MeasuringEtwHost(Guid provider, int records) : IEtwSessionHost, IDisposable
    {
        private readonly ManualResetEventSlim warmed = new(false);
        private readonly ManualResetEventSlim draining = new(false);
        private readonly ManualResetEventSlim measured = new(false);

        public void Dispose()
        {
            warmed.Dispose();
            draining.Dispose();
            measured.Dispose();
        }

        public Guid Provider { get; } = provider;

        public int Records { get; } = records;

        public bool? IsElevated => true;

        public long AllocatedBytes { get; private set; } = -1;

        public IReadOnlyList<string> ListActiveSessionNames() => [];

        public IOwnedEtwSession CreateExclusive(OwnedSessionPlan plan) => new Session(this, plan.Identity.SessionName);

        public bool WaitUntilWarmed(TimeSpan timeout) => warmed.Wait(timeout);

        public void StartMeasuring() => draining.Set();

        public bool WaitUntilMeasured(TimeSpan timeout) => measured.Wait(timeout);

        private sealed class Session(MeasuringEtwHost host, string name) : IOwnedEtwSession
        {
            private volatile bool stop;

            public string SessionName => name;

            public ProviderEnablementResult Enable(ProviderEnablementRequest request) => new(request.SourceId, true, null);

            public bool TryRequestCaptureState(ProviderEnablementRequest request, out string? failureReason)
            {
                failureReason = null;
                return true;
            }

            public void Pump(EventAdmissionTable table, IAdmittedEventSink sink, CancellationToken cancellationToken)
            {
                Deliver(sink);
                host.warmed.Set();
                host.draining.Wait(cancellationToken);
                long before = GC.GetAllocatedBytesForCurrentThread();
                Deliver(sink);
                host.AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
                host.measured.Set();
                while (!stop && !cancellationToken.IsCancellationRequested)
                {
                    Thread.Sleep(1);
                }
            }

            /// <summary>Every outcome a callback reports: a delivery, an admission, an omission, an undecodable record.</summary>
            private void Deliver(IAdmittedEventSink sink)
            {
                for (int index = 0; index < host.Records; index++)
                {
                    var delivered = new DeliveredRecord(host.Provider, 10, 0, 1_000 + index);
                    sink.OnObserved(in delivered);
                    var admitted = new AdmittedEvent
                    {
                        SourceIndex = 0,
                        EventId = 10,
                        TimestampQpc = 1_000 + index,
                        TimestampUtcTicks = 638_000_000_000_000_000 + index,
                    };
                    _ = sink.Admit(in admitted);
                    sink.OnCallbackCompleted(new(5, ExtendedDataAvailability.NotRequested, 0, 0));

                    var omitted = new DeliveredRecord(host.Provider, 11, 0, 1_000 + index);
                    sink.OnObserved(in omitted);
                    sink.OnOmitted(OmissionReason.DescriptorNotAdmitted, in omitted);
                    sink.OnObserved(in delivered);
                    sink.OnUndecodable(UndecodableReason.BodyShorterThanSchema, in delivered);
                }
            }

            public void RequestStopProcessing() => stop = true;

            public SourceLossReading ReadLoss() => new(0, 0);

            public void StopSession() => stop = true;

            public void Dispose()
            {
            }
        }
    }
}
