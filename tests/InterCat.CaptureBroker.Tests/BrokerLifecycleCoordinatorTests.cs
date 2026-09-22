using InterCat.Domain;
using Xunit;
using static InterCat.CaptureBroker.Tests.BrokerPlanFixture;

namespace InterCat.CaptureBroker.Tests;

public sealed class BrokerLifecycleCoordinatorTests
{
    private static readonly DateTimeOffset StartTime = new(2026, 9, 22, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task StartIntentIsPersistedBeforeRuntimeAndDuplicateReplaysExactResult()
    {
        var clock = new ManualTimeProvider(StartTime);
        var store = new InMemoryBrokerLifecycleStore();
        var runtime = new FakeRuntime();
        var registry = new PreparedPlanRegistry(clock);
        PreparedPlanGrant grant = registry.Issue(PreparedFocused(), OwnerA);
        runtime.BeforeStart = async captureId =>
        {
            BrokerCaptureOwnership? intent = await store.FindCaptureAsync(captureId, CancellationToken.None);
            Assert.NotNull(intent);
            Assert.Equal(CaptureLifecycle.Starting, intent.State);
        };
        using var coordinator = new BrokerLifecycleCoordinator(
            registry,
            store,
            runtime,
            clock,
            TimeSpan.FromSeconds(20));
        Guid requestId = Guid.NewGuid();

        BrokerStartOutcome first = await coordinator.StartAsync(grant.Token, requestId, OwnerA);
        BrokerStartOutcome duplicate = await coordinator.StartAsync(grant.Token, requestId, OwnerA);

        Assert.Equal(BrokerOperationCode.Started, first.Code);
        Assert.Equal(first, duplicate);
        Assert.Equal(1, runtime.StartCount);
        BrokerCaptureOwnership status = Assert.IsType<BrokerCaptureOwnership>(
            await coordinator.GetStatusAsync(first.CaptureId!.Value, OwnerA));
        Assert.Equal(CaptureLifecycle.Recording, status.State);
        Assert.Equal(grant.PlanDigest, status.PlanDigest);
    }

    [Fact]
    public async Task TokenIsBoundToOwnerAndExpiryWithoutRevealingWhichCheckFailed()
    {
        var clock = new ManualTimeProvider(StartTime);
        var runtime = new FakeRuntime();
        var registry = new PreparedPlanRegistry(clock, TimeSpan.FromSeconds(5));
        PreparedPlanGrant wrongOwnerGrant = registry.Issue(PreparedFocused(), OwnerA);
        using var coordinator = new BrokerLifecycleCoordinator(
            registry,
            new InMemoryBrokerLifecycleStore(),
            runtime,
            clock);

        BrokerStartOutcome wrongOwner = await coordinator.StartAsync(
            wrongOwnerGrant.Token,
            Guid.NewGuid(),
            OwnerB);
        PreparedPlanGrant expiredGrant = registry.Issue(PreparedFocused(), OwnerA);
        clock.Advance(TimeSpan.FromSeconds(6));
        BrokerStartOutcome expired = await coordinator.StartAsync(
            expiredGrant.Token,
            Guid.NewGuid(),
            OwnerA);

        Assert.Equal(BrokerOperationCode.PreparedTokenRejected, wrongOwner.Code);
        Assert.Equal(BrokerOperationCode.PreparedTokenRejected, expired.Code);
        Assert.Equal(wrongOwner.FailureReason, expired.FailureReason);
        Assert.Equal(0, runtime.StartCount);
    }

    [Fact]
    public async Task PreparedTokenIsSingleUseAndRequestIdCannotBeRebound()
    {
        var clock = new ManualTimeProvider(StartTime);
        var runtime = new FakeRuntime();
        var registry = new PreparedPlanRegistry(clock);
        PreparedPlanGrant firstGrant = registry.Issue(PreparedFocused(), OwnerA);
        PreparedPlanGrant secondGrant = registry.Issue(PreparedFocused(), OwnerA);
        using var coordinator = new BrokerLifecycleCoordinator(
            registry,
            new InMemoryBrokerLifecycleStore(),
            runtime,
            clock);
        Guid requestId = Guid.NewGuid();

        BrokerStartOutcome started = await coordinator.StartAsync(firstGrant.Token, requestId, OwnerA);
        BrokerStartOutcome reusedToken = await coordinator.StartAsync(
            firstGrant.Token,
            Guid.NewGuid(),
            OwnerA);
        BrokerStartOutcome reboundRequest = await coordinator.StartAsync(
            secondGrant.Token,
            requestId,
            OwnerA);

        Assert.Equal(BrokerOperationCode.Started, started.Code);
        Assert.Equal(BrokerOperationCode.PreparedPlanAlreadyUsed, reusedToken.Code);
        Assert.Equal(started.CaptureId, reusedToken.CaptureId);
        Assert.Equal(BrokerOperationCode.RequestConflict, reboundRequest.Code);
        Assert.Equal(1, runtime.StartCount);
    }

    [Fact]
    public async Task StopMilestonesAndDuplicateRequestRemainExact()
    {
        var clock = new ManualTimeProvider(StartTime);
        var runtime = new FakeRuntime
        {
            StopOutcome = new(new(true, true, true, false, false), "Journal flush is still pending."),
        };
        var registry = new PreparedPlanRegistry(clock);
        PreparedPlanGrant grant = registry.Issue(PreparedFocused(), OwnerA);
        using var coordinator = new BrokerLifecycleCoordinator(
            registry,
            new InMemoryBrokerLifecycleStore(),
            runtime,
            clock);
        BrokerStartOutcome start = await coordinator.StartAsync(grant.Token, Guid.NewGuid(), OwnerA);
        Guid stopRequestId = Guid.NewGuid();

        BrokerStopOutcome first = await coordinator.StopAsync(
            start.CaptureId!.Value,
            stopRequestId,
            OwnerA);
        BrokerStopOutcome duplicate = await coordinator.StopAsync(
            start.CaptureId.Value,
            stopRequestId,
            OwnerA);

        Assert.Equal(BrokerOperationCode.StopPartial, first.Code);
        Assert.Equal(CaptureLifecycle.Finalizing, first.State);
        Assert.True(first.Milestones.ProvidersStopped);
        Assert.False(first.Milestones.JournalFinalized);
        Assert.Equal(first, duplicate);
        Assert.Equal(1, runtime.StopCount);
    }

    [Fact]
    public async Task NewStopRequestCanRetryPartialFinalization()
    {
        var clock = new ManualTimeProvider(StartTime);
        var runtime = new FakeRuntime();
        runtime.StopOutcomes.Enqueue(new(new(true, true, true, false, false), "Flush pending."));
        runtime.StopOutcomes.Enqueue(new(new(true, true, true, true, true)));
        var registry = new PreparedPlanRegistry(clock);
        PreparedPlanGrant grant = registry.Issue(PreparedFocused(), OwnerA);
        using var coordinator = new BrokerLifecycleCoordinator(
            registry,
            new InMemoryBrokerLifecycleStore(),
            runtime,
            clock);
        BrokerStartOutcome start = await coordinator.StartAsync(grant.Token, Guid.NewGuid(), OwnerA);

        BrokerStopOutcome partial = await coordinator.StopAsync(
            start.CaptureId!.Value,
            Guid.NewGuid(),
            OwnerA);
        BrokerStopOutcome completed = await coordinator.StopAsync(
            start.CaptureId.Value,
            Guid.NewGuid(),
            OwnerA);

        Assert.Equal(BrokerOperationCode.StopPartial, partial.Code);
        Assert.Equal(BrokerOperationCode.Stopped, completed.Code);
        Assert.Equal(CaptureLifecycle.Closed, completed.State);
        Assert.Equal(2, runtime.StopCount);
    }

    [Fact]
    public async Task OtherOwnerCannotReadRenewOrStopCapture()
    {
        var clock = new ManualTimeProvider(StartTime);
        var runtime = new FakeRuntime();
        var registry = new PreparedPlanRegistry(clock);
        PreparedPlanGrant grant = registry.Issue(PreparedFocused(), OwnerA);
        using var coordinator = new BrokerLifecycleCoordinator(
            registry,
            new InMemoryBrokerLifecycleStore(),
            runtime,
            clock);
        BrokerStartOutcome start = await coordinator.StartAsync(grant.Token, Guid.NewGuid(), OwnerA);
        CaptureId captureId = start.CaptureId!.Value;

        BrokerCaptureOwnership? status = await coordinator.GetStatusAsync(captureId, OwnerB);
        BrokerLeaseOutcome renewal = await coordinator.RenewOwnerLeaseAsync(captureId, OwnerB);
        BrokerStopOutcome stop = await coordinator.StopAsync(captureId, Guid.NewGuid(), OwnerB);

        Assert.Null(status);
        Assert.Equal(BrokerOperationCode.CaptureUnavailable, renewal.Code);
        Assert.Equal(BrokerOperationCode.CaptureUnavailable, stop.Code);
        Assert.Equal(0, runtime.StopCount);
    }

    [Fact]
    public async Task LeaseRenewsBeforeExpiryButCannotBeRevivedAfterExpiry()
    {
        var clock = new ManualTimeProvider(StartTime);
        var runtime = new FakeRuntime();
        var registry = new PreparedPlanRegistry(clock);
        PreparedPlanGrant grant = registry.Issue(PreparedFocused(), OwnerA);
        using var coordinator = new BrokerLifecycleCoordinator(
            registry,
            new InMemoryBrokerLifecycleStore(),
            runtime,
            clock,
            TimeSpan.FromSeconds(20));
        BrokerStartOutcome start = await coordinator.StartAsync(grant.Token, Guid.NewGuid(), OwnerA);
        CaptureId captureId = start.CaptureId!.Value;

        clock.Advance(TimeSpan.FromSeconds(15));
        BrokerLeaseOutcome renewed = await coordinator.RenewOwnerLeaseAsync(captureId, OwnerA);
        clock.Advance(TimeSpan.FromSeconds(21));
        BrokerLeaseOutcome expired = await coordinator.RenewOwnerLeaseAsync(captureId, OwnerA);

        Assert.Equal(BrokerOperationCode.LeaseRenewed, renewed.Code);
        Assert.Equal(StartTime.AddSeconds(35), renewed.LeaseExpiresAtUtc);
        Assert.Equal(BrokerOperationCode.LeaseExpired, expired.Code);
        Assert.Equal(0, runtime.StopCount);
    }

    [Fact]
    public async Task ExpiredLeaseSweepStopsOrphanAndPersistsFinalStatus()
    {
        var clock = new ManualTimeProvider(StartTime);
        var runtime = new FakeRuntime();
        var registry = new PreparedPlanRegistry(clock);
        PreparedPlanGrant grant = registry.Issue(PreparedFocused(), OwnerA);
        using var coordinator = new BrokerLifecycleCoordinator(
            registry,
            new InMemoryBrokerLifecycleStore(),
            runtime,
            clock,
            TimeSpan.FromSeconds(10));
        BrokerStartOutcome start = await coordinator.StartAsync(grant.Token, Guid.NewGuid(), OwnerA);
        clock.Advance(TimeSpan.FromSeconds(11));

        IReadOnlyList<BrokerStopOutcome> outcomes = await coordinator.StopExpiredLeasesAsync();

        BrokerStopOutcome outcome = Assert.Single(outcomes);
        Assert.Equal(BrokerOperationCode.Stopped, outcome.Code);
        Assert.True(outcome.Milestones.FullyFinalized);
        Assert.Equal(1, runtime.StopCount);
        BrokerCaptureOwnership status = Assert.IsType<BrokerCaptureOwnership>(
            await coordinator.GetStatusAsync(start.CaptureId!.Value, OwnerA));
        Assert.Equal(CaptureLifecycle.Closed, status.State);
    }

    [Fact]
    public async Task FailedStartIsPersistedAndDoesNotRunTwice()
    {
        var clock = new ManualTimeProvider(StartTime);
        var runtime = new FakeRuntime
        {
            StartOutcome = new(false, "Provider enablement failed."),
        };
        var registry = new PreparedPlanRegistry(clock);
        PreparedPlanGrant grant = registry.Issue(PreparedFocused(), OwnerA);
        using var coordinator = new BrokerLifecycleCoordinator(
            registry,
            new InMemoryBrokerLifecycleStore(),
            runtime,
            clock);
        Guid requestId = Guid.NewGuid();

        BrokerStartOutcome first = await coordinator.StartAsync(grant.Token, requestId, OwnerA);
        BrokerStartOutcome duplicate = await coordinator.StartAsync(grant.Token, requestId, OwnerA);

        Assert.Equal(BrokerOperationCode.StartFailed, first.Code);
        Assert.Equal(CaptureLifecycle.Closed, first.State);
        Assert.Equal(first, duplicate);
        Assert.Equal(1, runtime.StartCount);
    }

    [Fact]
    public async Task IntentPersistenceFailurePreventsRuntimeStart()
    {
        var clock = new ManualTimeProvider(StartTime);
        var runtime = new FakeRuntime();
        var store = new FaultingStore { FailStartIntent = true };
        var registry = new PreparedPlanRegistry(clock);
        PreparedPlanGrant grant = registry.Issue(PreparedFocused(), OwnerA);
        using var coordinator = new BrokerLifecycleCoordinator(registry, store, runtime, clock);

        BrokerStartOutcome outcome = await coordinator.StartAsync(grant.Token, Guid.NewGuid(), OwnerA);

        Assert.Equal(BrokerOperationCode.PersistenceFailure, outcome.Code);
        Assert.Equal(0, runtime.StartCount);
        Assert.Equal(0, runtime.StopCount);
    }

    [Fact]
    public async Task CompletionPersistenceFailureTriggersCompensatingStopAndNoSuccess()
    {
        var clock = new ManualTimeProvider(StartTime);
        var runtime = new FakeRuntime();
        var store = new FaultingStore { FailStartCompletion = true };
        var registry = new PreparedPlanRegistry(clock);
        PreparedPlanGrant grant = registry.Issue(PreparedFocused(), OwnerA);
        using var coordinator = new BrokerLifecycleCoordinator(registry, store, runtime, clock);

        BrokerStartOutcome outcome = await coordinator.StartAsync(grant.Token, Guid.NewGuid(), OwnerA);

        Assert.Equal(BrokerOperationCode.PersistenceFailure, outcome.Code);
        Assert.Equal(1, runtime.StartCount);
        Assert.Equal(1, runtime.StopCount);
    }

    private sealed class FakeRuntime : IBrokerCaptureRuntime
    {
        public BrokerRuntimeStartOutcome StartOutcome { get; init; } = new(true);
        public BrokerRuntimeStopOutcome StopOutcome { get; init; } = new(new(true, true, true, true, true));
        public Func<CaptureId, Task>? BeforeStart { get; set; }
        public Queue<BrokerRuntimeStopOutcome> StopOutcomes { get; } = [];
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }

        public async Task<BrokerRuntimeStartOutcome> StartAsync(
            CaptureId captureId,
            PreparedCapturePlan plan,
            CancellationToken cancellationToken)
        {
            Assert.NotNull(plan);
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            if (BeforeStart is not null)
            {
                await BeforeStart(captureId);
            }

            return StartOutcome;
        }

        public Task<BrokerRuntimeStopOutcome> StopAsync(
            CaptureId captureId,
            CancellationToken cancellationToken)
        {
            Assert.NotEqual(Guid.Empty, captureId.Value);
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            return Task.FromResult(StopOutcomes.Count > 0 ? StopOutcomes.Dequeue() : StopOutcome);
        }
    }

    private sealed class FaultingStore : InMemoryBrokerLifecycleStore
    {
        public bool FailStartIntent { get; init; }
        public bool FailStartCompletion { get; init; }

        public override ValueTask SaveStartIntentAsync(
            BrokerCaptureOwnership ownership,
            BrokerStoredRequest request,
            CancellationToken cancellationToken) =>
            FailStartIntent
                ? ValueTask.FromException(new IOException("fixture intent failure"))
                : base.SaveStartIntentAsync(ownership, request, cancellationToken);

        public override ValueTask SaveStartCompletionAsync(
            BrokerCaptureOwnership ownership,
            BrokerStoredRequest request,
            CancellationToken cancellationToken) =>
            FailStartCompletion
                ? ValueTask.FromException(new IOException("fixture completion failure"))
                : base.SaveStartCompletionAsync(ownership, request, cancellationToken);
    }
}
