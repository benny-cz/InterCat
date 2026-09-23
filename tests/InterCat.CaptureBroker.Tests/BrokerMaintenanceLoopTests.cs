using InterCat.Domain;
using Xunit;
using static InterCat.CaptureBroker.Tests.BrokerPlanFixture;

namespace InterCat.CaptureBroker.Tests;

public sealed class BrokerMaintenanceLoopTests
{
    private static readonly DateTimeOffset StartTime = new(2026, 9, 22, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OnePassMakesAutonomousStopsAndExpiredLeasesDurable()
    {
        var clock = new ManualTimeProvider(StartTime);
        var runtime = new BrokerFakeRuntime();
        var store = new InMemoryBrokerLifecycleStore();
        var registry = new PreparedPlanRegistry(clock);
        using var coordinator = new BrokerLifecycleCoordinator(registry, store, runtime, clock);
        CaptureId finished = await Start(coordinator, registry);
        CaptureId abandoned = await Start(coordinator, registry);
        runtime.CompletedCaptures.Add(finished);
        clock.Advance(TimeSpan.FromHours(1));
        var loop = new BrokerMaintenanceLoop(coordinator, clock);

        BrokerMaintenanceReport report = await loop.RunOnceAsync();

        Assert.True(report.IsNotable);
        Assert.Null(report.Failure);
        Assert.Equal(finished, Assert.Single(report.Reconciled).CaptureId);
        Assert.Equal(abandoned, Assert.Single(report.ExpiredLeaseStops).CaptureId);
        Assert.Equal(CaptureLifecycle.Closed, (await store.FindCaptureAsync(finished, CancellationToken.None))!.State);
        Assert.Equal(CaptureLifecycle.Closed, (await store.FindCaptureAsync(abandoned, CancellationToken.None))!.State);
        Assert.Equal(2, runtime.StopCount);

        BrokerMaintenanceReport quiet = await loop.RunOnceAsync();
        Assert.False(quiet.IsNotable);
        Assert.Equal(2, runtime.StopCount);
    }

    [Fact]
    public async Task QueuedStopCompletionIsRetriedWithoutAClientAndFailuresAreCounted()
    {
        var clock = new ManualTimeProvider(StartTime);
        var runtime = new BrokerFakeRuntime();
        var store = new FlakyStore();
        var registry = new PreparedPlanRegistry(clock);
        using var coordinator = new BrokerLifecycleCoordinator(registry, store, runtime, clock);
        CaptureId captureId = await Start(coordinator, registry);
        store.FailStopCompletions = 2;
        Assert.Equal(
            BrokerOperationCode.PersistenceFailure,
            (await coordinator.StopAsync(captureId, Guid.NewGuid(), OwnerA)).Code);
        var loop = new BrokerMaintenanceLoop(coordinator, clock);

        BrokerMaintenanceReport stillFailing = await loop.RunOnceAsync();
        Assert.Contains("still could not be persisted", stillFailing.Failure, StringComparison.Ordinal);
        Assert.Equal(1, stillFailing.ConsecutiveFailures);

        BrokerMaintenanceReport retried = await loop.RunOnceAsync();
        Assert.Null(retried.Failure);
        Assert.Equal(0, retried.ConsecutiveFailures);
        Assert.Equal(BrokerOperationCode.Stopped, Assert.Single(retried.Reconciled).Code);
        Assert.Equal(CaptureLifecycle.Closed, (await store.FindCaptureAsync(captureId, CancellationToken.None))!.State);
        Assert.Equal(1, runtime.StopCount);
    }

    [Fact]
    public async Task AFailingHalfDoesNotSkipTheOther()
    {
        var clock = new ManualTimeProvider(StartTime);
        var runtime = new BrokerFakeRuntime();
        var store = new FlakyStore { FailExpiredLeaseReads = 1 };
        var registry = new PreparedPlanRegistry(clock);
        using var coordinator = new BrokerLifecycleCoordinator(registry, store, runtime, clock);
        CaptureId finished = await Start(coordinator, registry);
        runtime.CompletedCaptures.Add(finished);
        var loop = new BrokerMaintenanceLoop(coordinator, clock);

        BrokerMaintenanceReport report = await loop.RunOnceAsync();

        Assert.Contains("Expired owner leases could not be swept", report.Failure, StringComparison.Ordinal);
        Assert.Equal(finished, Assert.Single(report.Reconciled).CaptureId);
        Assert.Empty(report.ExpiredLeaseStops);
    }

    [Fact]
    public async Task TheLoopKeepsRunningThroughAFailingReportSinkAndEndsOnCancellation()
    {
        var runtime = new BrokerFakeRuntime();
        var store = new InMemoryBrokerLifecycleStore();
        var registry = new PreparedPlanRegistry();
        using var coordinator = new BrokerLifecycleCoordinator(registry, store, runtime);
        CaptureId first = await Start(coordinator, registry);
        CaptureId second = await Start(coordinator, registry);
        int reports = 0;
        var loop = new BrokerMaintenanceLoop(
            coordinator,
            interval: TimeSpan.FromMilliseconds(10),
            onReport: _ =>
            {
                Interlocked.Increment(ref reports);
                throw new InvalidOperationException("the log sink is broken");
            });
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);

        runtime.CompletedCaptures.Add(first);
        await WaitUntilClosed(store, first);
        runtime.CompletedCaptures.Add(second);
        await WaitUntilClosed(store, second);
        await cancellation.CancelAsync();

        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Volatile.Read(ref reports) >= 2);
    }

    [Fact]
    public void AnIntervalOutsideItsBoundIsRefused()
    {
        using var coordinator = new BrokerLifecycleCoordinator(
            new PreparedPlanRegistry(), new InMemoryBrokerLifecycleStore(), new BrokerFakeRuntime());

        Assert.Throws<ArgumentOutOfRangeException>(() => new BrokerMaintenanceLoop(coordinator, interval: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrokerMaintenanceLoop(coordinator, interval: TimeSpan.FromHours(1)));
    }

    private static async Task<CaptureId> Start(BrokerLifecycleCoordinator coordinator, PreparedPlanRegistry registry)
    {
        PreparedPlanGrant grant = registry.Issue(PreparedFocused(), OwnerA);
        BrokerStartOutcome start = await coordinator.StartAsync(grant.Token, Guid.NewGuid(), OwnerA);
        return start.CaptureId ?? throw new InvalidOperationException(start.FailureReason);
    }

    private static async Task WaitUntilClosed(InMemoryBrokerLifecycleStore store, CaptureId captureId)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while ((await store.FindCaptureAsync(captureId, CancellationToken.None))?.State != CaptureLifecycle.Closed)
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "the maintenance loop did not close the capture");
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
    }

    private sealed class FlakyStore : InMemoryBrokerLifecycleStore
    {
        public int FailStopCompletions { get; set; }

        public int FailExpiredLeaseReads { get; set; }

        public override ValueTask SaveStopCompletionAsync(
            BrokerCaptureOwnership ownership,
            BrokerStoredRequest request,
            CancellationToken cancellationToken)
        {
            if (FailStopCompletions > 0)
            {
                FailStopCompletions--;
                return ValueTask.FromException(new IOException("fixture stop completion failure"));
            }

            return base.SaveStopCompletionAsync(ownership, request, cancellationToken);
        }

        public override ValueTask<IReadOnlyList<BrokerCaptureOwnership>> FindExpiredLeasesAsync(
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken)
        {
            if (FailExpiredLeaseReads > 0)
            {
                FailExpiredLeaseReads--;
                return ValueTask.FromException<IReadOnlyList<BrokerCaptureOwnership>>(
                    new IOException("fixture lease read failure"));
            }

            return base.FindExpiredLeasesAsync(nowUtc, cancellationToken);
        }
    }
}
