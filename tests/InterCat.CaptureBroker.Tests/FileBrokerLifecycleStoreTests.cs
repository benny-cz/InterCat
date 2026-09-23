using System.Runtime.Versioning;
using System.Text;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.CaptureBroker.Tests.BrokerPlanFixture;

namespace InterCat.CaptureBroker.Tests;

[SupportedOSPlatform("windows")]
public sealed class FileBrokerLifecycleStoreTests
{
    private static readonly DateTimeOffset StartTime = new(2026, 9, 22, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CompletedStartSurvivesReopenAndDuplicateDoesNotRunAgain()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        var clock = new ManualTimeProvider(StartTime);
        var registry = new PreparedPlanRegistry(clock);
        PreparedPlanGrant grant = registry.Issue(PreparedFocused(), OwnerA);
        Guid requestId = Guid.NewGuid();
        BrokerStartOutcome first;
        using (var store = new FileBrokerLifecycleStore(temporary.Root))
        using (var coordinator = new BrokerLifecycleCoordinator(
            registry,
            store,
            new BrokerFakeRuntime(),
            clock))
        {
            first = await coordinator.StartAsync(grant.Token, requestId, OwnerA);
            Assert.Equal(BrokerOperationCode.Started, first.Code);
        }

        var restartedRuntime = new BrokerFakeRuntime();
        using (var reopened = new FileBrokerLifecycleStore(temporary.Root))
        using (var coordinator = new BrokerLifecycleCoordinator(
            new PreparedPlanRegistry(clock),
            reopened,
            restartedRuntime,
            clock))
        {
            BrokerStartOutcome duplicate = await coordinator.StartAsync(
                grant.Token,
                requestId,
                OwnerA);

            Assert.Equal(first, duplicate);
            Assert.Equal(0, restartedRuntime.StartCount);
            Assert.True(reopened.Recovery.RecoveredSequence >= 2);
            Assert.Equal(0, reopened.Recovery.TruncatedTailBytes);
        }

        string persisted = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(
            Path.Combine(temporary.Root.Path, FileBrokerLifecycleStore.FileName)));
        Assert.DoesNotContain(grant.Token, persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncompleteTailIsTruncatedToLastDurableSnapshot()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        BrokerStartOutcome start = await WriteStartedCapture(temporary.Root);
        string path = Path.Combine(temporary.Root.Path, FileBrokerLifecycleStore.FileName);
        long completeLength = new FileInfo(path).Length;
        await File.AppendAllBytesAsync(path, [0x49, 0x43, 0x42]);

        using var reopened = new FileBrokerLifecycleStore(temporary.Root);
        BrokerLifecycleSnapshot snapshot = await reopened.ReadSnapshotAsync(CancellationToken.None);

        Assert.Equal(3, reopened.Recovery.TruncatedTailBytes);
        Assert.Contains("incomplete", reopened.Recovery.RecoveryReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(completeLength, new FileInfo(path).Length);
        Assert.Contains(snapshot.Captures, item => item.CaptureId == start.CaptureId);
    }

    [Fact]
    public async Task CorruptCompletionFallsBackToIntentAndRecoveryStopsByOwnershipToken()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        BrokerStartOutcome start = await WriteStartedCapture(temporary.Root);
        string path = Path.Combine(temporary.Root.Path, FileBrokerLifecycleStore.FileName);
        CorruptLastByte(path);

        var clock = new ManualTimeProvider(StartTime);
        var runtime = new BrokerFakeRuntime();
        using var reopened = new FileBrokerLifecycleStore(temporary.Root);
        BrokerLifecycleSnapshot before = await reopened.ReadSnapshotAsync(CancellationToken.None);
        BrokerCaptureOwnership ownership = Assert.Single(before.Captures);
        BrokerStoredRequest pending = Assert.Single(before.Requests);
        Assert.False(pending.Completed);
        Assert.True(reopened.Recovery.TruncatedTailBytes > 0);
        using var coordinator = new BrokerLifecycleCoordinator(
            new PreparedPlanRegistry(clock),
            reopened,
            runtime,
            clock);

        BrokerRecoveryReport report = await coordinator.RecoverAsync();

        BrokerRecoveryItem item = Assert.Single(report.Items);
        Assert.Equal(BrokerRecoveryAction.InterruptedStartStopped, item.Action);
        Assert.Equal(start.CaptureId, item.CaptureId);
        Assert.Equal(1, runtime.StopCount);
        Assert.Equal(ownership.Session, Assert.Single(runtime.StoppedSessions));
        BrokerCaptureOwnership status = Assert.IsType<BrokerCaptureOwnership>(
            await coordinator.GetStatusAsync(start.CaptureId!.Value, OwnerA));
        Assert.Equal(CaptureLifecycle.Closed, status.State);
    }

    [Fact]
    public async Task RestartRetriesACompletedPartialStopWithANewRecoveryRequest()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        var clock = new ManualTimeProvider(StartTime);
        var firstRuntime = new BrokerFakeRuntime
        {
            StopOutcome = new(new(true, true, true, false, false), "Flush pending."),
        };
        var registry = new PreparedPlanRegistry(clock);
        PreparedPlanGrant grant = registry.Issue(PreparedFocused(), OwnerA);
        CaptureId captureId;
        using (var store = new FileBrokerLifecycleStore(temporary.Root))
        using (var coordinator = new BrokerLifecycleCoordinator(
            registry,
            store,
            firstRuntime,
            clock))
        {
            BrokerStartOutcome start = await coordinator.StartAsync(
                grant.Token,
                Guid.NewGuid(),
                OwnerA);
            captureId = start.CaptureId!.Value;
            BrokerStopOutcome partial = await coordinator.StopAsync(
                captureId,
                Guid.NewGuid(),
                OwnerA);
            Assert.Equal(BrokerOperationCode.StopPartial, partial.Code);
        }

        var recoveryRuntime = new BrokerFakeRuntime();
        using var reopened = new FileBrokerLifecycleStore(temporary.Root);
        using var recovery = new BrokerLifecycleCoordinator(
            new PreparedPlanRegistry(clock),
            reopened,
            recoveryRuntime,
            clock);

        BrokerRecoveryReport report = await recovery.RecoverAsync();

        Assert.Contains(report.Items, item =>
            item.CaptureId == captureId
            && item.Action == BrokerRecoveryAction.PartialStopRetried
            && item.State == CaptureLifecycle.Closed);
        Assert.Equal(1, recoveryRuntime.StopCount);
    }

    [Fact]
    public async Task RestartStopsRecordingEvenWhenOwnerLeaseIsUnexpired()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        BrokerStartOutcome start = await WriteStartedCapture(temporary.Root);
        var clock = new ManualTimeProvider(StartTime);
        var runtime = new BrokerFakeRuntime();
        using var reopened = new FileBrokerLifecycleStore(temporary.Root);
        using var coordinator = new BrokerLifecycleCoordinator(
            new PreparedPlanRegistry(clock),
            reopened,
            runtime,
            clock);

        BrokerRecoveryReport report = await coordinator.RecoverAsync();

        BrokerRecoveryItem item = Assert.Single(report.Items);
        Assert.Equal(start.CaptureId, item.CaptureId);
        Assert.Equal(BrokerRecoveryAction.RestartedActiveCaptureStopRequested, item.Action);
        Assert.Equal(CaptureLifecycle.Closed, item.State);
        Assert.Equal(1, report.StoppedOrRetriedCount);
        Assert.Equal(1, runtime.StopCount);
        BrokerCaptureOwnership status = Assert.IsType<BrokerCaptureOwnership>(
            await coordinator.GetStatusAsync(start.CaptureId!.Value, OwnerA));
        Assert.Equal(CaptureLifecycle.Closed, status.State);
    }

    [Fact]
    public async Task RestartCannotCallAnUnstoppedRecordingClosed()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        BrokerStartOutcome start = await WriteStartedCapture(temporary.Root);
        var clock = new ManualTimeProvider(StartTime);
        var runtime = new BrokerFakeRuntime();
        runtime.StopOutcomes.Enqueue(new(new(true, false, false, false, false), "ETW stop was refused."));
        using var reopened = new FileBrokerLifecycleStore(temporary.Root);
        using var coordinator = new BrokerLifecycleCoordinator(
            new PreparedPlanRegistry(clock),
            reopened,
            runtime,
            clock);

        BrokerRecoveryItem partial = Assert.Single((await coordinator.RecoverAsync()).Items);

        Assert.Equal(BrokerRecoveryAction.RestartedActiveCaptureStopRequested, partial.Action);
        Assert.Equal(CaptureLifecycle.Stopping, partial.State);
        Assert.Equal(1, runtime.StopCount);
        BrokerCaptureOwnership status = Assert.IsType<BrokerCaptureOwnership>(
            await coordinator.GetStatusAsync(start.CaptureId!.Value, OwnerA));
        Assert.Equal(CaptureLifecycle.Stopping, status.State);
        Assert.Contains("ETW stop was refused", status.FailureReason, StringComparison.Ordinal);

        BrokerRecoveryItem retried = Assert.Single((await coordinator.RecoverAsync()).Items);
        Assert.Equal(BrokerRecoveryAction.PartialStopRetried, retried.Action);
        Assert.Equal(CaptureLifecycle.Closed, retried.State);
        Assert.Equal(2, runtime.StopCount);
    }

    [Fact]
    public void NonemptyStoreWithoutAValidFrameIsRefusedAndNotReset()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        string path = Path.Combine(temporary.Root.Path, FileBrokerLifecycleStore.FileName);
        byte[] invalid = [0x49, 0x43, 0x42, 0x00, 0xff];
        File.WriteAllBytes(path, invalid);

        Assert.Throws<InvalidDataException>(() => new FileBrokerLifecycleStore(temporary.Root));
        Assert.Equal(invalid, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task RestartStopsRecordingWhoseOwnerLeaseExpiredWhileBrokerWasDown()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        var clock = new ManualTimeProvider(StartTime);
        var registry = new PreparedPlanRegistry(clock);
        PreparedPlanGrant grant = registry.Issue(PreparedFocused(), OwnerA);
        CaptureId captureId;
        using (var store = new FileBrokerLifecycleStore(temporary.Root))
        using (var coordinator = new BrokerLifecycleCoordinator(
            registry,
            store,
            new BrokerFakeRuntime(),
            clock,
            TimeSpan.FromSeconds(10)))
        {
            BrokerStartOutcome start = await coordinator.StartAsync(
                grant.Token,
                Guid.NewGuid(),
                OwnerA);
            captureId = start.CaptureId!.Value;
        }

        clock.Advance(TimeSpan.FromSeconds(11));
        var recoveryRuntime = new BrokerFakeRuntime();
        using var reopened = new FileBrokerLifecycleStore(temporary.Root);
        using var recovery = new BrokerLifecycleCoordinator(
            new PreparedPlanRegistry(clock),
            reopened,
            recoveryRuntime,
            clock,
            TimeSpan.FromSeconds(10));

        BrokerRecoveryReport report = await recovery.RecoverAsync();

        Assert.Contains(report.Items, item =>
            item.CaptureId == captureId
            && item.Action == BrokerRecoveryAction.ExpiredLeaseStopped
            && item.State == CaptureLifecycle.Closed);
        Assert.Equal(1, recoveryRuntime.StopCount);
    }

    private static async Task<BrokerStartOutcome> WriteStartedCapture(IOwnedDirectory directory)
    {
        var clock = new ManualTimeProvider(StartTime);
        var registry = new PreparedPlanRegistry(clock);
        PreparedPlanGrant grant = registry.Issue(PreparedFocused(), OwnerA);
        using var store = new FileBrokerLifecycleStore(directory);
        using var coordinator = new BrokerLifecycleCoordinator(
            registry,
            store,
            new BrokerFakeRuntime(),
            clock);
        return await coordinator.StartAsync(grant.Token, Guid.NewGuid(), OwnerA);
    }

    private static void CorruptLastByte(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Position = stream.Length - 1;
        int value = stream.ReadByte();
        stream.Position--;
        stream.WriteByte((byte)(value ^ 0xff));
        stream.Flush(flushToDisk: true);
    }
}
