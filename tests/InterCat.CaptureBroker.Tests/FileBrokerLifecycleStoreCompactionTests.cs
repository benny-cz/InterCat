using System.Runtime.Versioning;
using InterCat.Domain;
using Xunit;
using static InterCat.CaptureBroker.Tests.BrokerPlanFixture;

namespace InterCat.CaptureBroker.Tests;

[SupportedOSPlatform("windows")]
public sealed class FileBrokerLifecycleStoreCompactionTests
{
    private static readonly DateTimeOffset StartTime = new(2026, 9, 22, 16, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "R16: compaction reclaims superseded frames and keeps every record")]
    public async Task CompactionReclaimsSupersededFramesAndKeepsEveryRecord()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        using var store = new FileBrokerLifecycleStore(temporary.Root);
        var clock = new ManualTimeProvider(StartTime);
        CaptureId captureId = await RunLifecycle(store, clock);
        BrokerLifecycleSnapshot before = await store.ReadSnapshotAsync(CancellationToken.None);

        BrokerStoreCompactionReport report = store.Compact();
        BrokerLifecycleSnapshot after = await store.ReadSnapshotAsync(CancellationToken.None);

        Assert.Equal(1, report.Generation);
        Assert.True(report.SupersededFrames >= 3, $"only {report.SupersededFrames} frames were superseded");
        Assert.True(
            report.BytesAfter < report.BytesBefore,
            $"{report.BytesAfter} is not smaller than {report.BytesBefore}");
        Assert.Equal(before.Captures.Count, report.Captures);
        Assert.Equal(before.Requests.Count, report.Requests);
        AssertSameState(before, after);
        Assert.Contains(after.Captures, capture => capture.CaptureId == captureId);
        Assert.False(File.Exists(Path.Combine(temporary.Root.Path, FileBrokerLifecycleStore.CompactionFileName)));
    }

    [Fact(DisplayName = "R16: a compacted log reopens at its new generation with the same state")]
    public async Task CompactedLogReopensAtItsNewGeneration()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        var clock = new ManualTimeProvider(StartTime);
        BrokerLifecycleSnapshot before;
        using (var store = new FileBrokerLifecycleStore(temporary.Root))
        {
            _ = await RunLifecycle(store, clock);
            _ = store.Compact();
            _ = store.Compact();
            before = await store.ReadSnapshotAsync(CancellationToken.None);
            Assert.Equal(2, store.Generation);
        }

        using var reopened = new FileBrokerLifecycleStore(temporary.Root);

        Assert.Equal(2, reopened.Recovery.Generation);
        Assert.Equal(1, reopened.Recovery.RecoveredSequence);
        Assert.Equal(0, reopened.Recovery.TruncatedTailBytes);
        Assert.Equal(0, reopened.Recovery.DiscardedCompactionBytes);
        Assert.Null(reopened.Recovery.RecoveryReason);
        AssertSameState(before, await reopened.ReadSnapshotAsync(CancellationToken.None));
    }

    [Fact(DisplayName = "R16: a completed request replays after compaction instead of starting a second capture")]
    public async Task CompletedRequestStillReplaysAfterCompaction()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        var clock = new ManualTimeProvider(StartTime);
        var registry = new PreparedPlanRegistry(clock);
        PreparedPlanGrant grant = registry.Issue(PreparedFocused(), OwnerA);
        Guid requestId = Guid.NewGuid();
        BrokerStartOutcome first;
        using (var store = new FileBrokerLifecycleStore(temporary.Root))
        using (var coordinator = new BrokerLifecycleCoordinator(registry, store, new BrokerFakeRuntime(), clock))
        {
            first = await coordinator.StartAsync(grant.Token, requestId, OwnerA);
            _ = store.Compact();
        }

        var runtime = new BrokerFakeRuntime();
        using var reopened = new FileBrokerLifecycleStore(temporary.Root);
        using var restarted = new BrokerLifecycleCoordinator(
            new PreparedPlanRegistry(clock),
            reopened,
            runtime,
            clock);

        BrokerStartOutcome duplicate = await restarted.StartAsync(grant.Token, requestId, OwnerA);

        Assert.Equal(BrokerOperationCode.Started, first.Code);
        Assert.Equal(first, duplicate);
        Assert.Equal(0, runtime.StartCount);
    }

    [Theory(DisplayName = "R16: a compaction interrupted before its replacement keeps the last durable snapshot")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InterruptedCompactionKeepsTheLastDurableSnapshot(bool temporaryIsComplete)
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        var clock = new ManualTimeProvider(StartTime);
        BrokerLifecycleSnapshot before;
        long liveLength;
        using (var store = new FileBrokerLifecycleStore(temporary.Root))
        {
            _ = await RunLifecycle(store, clock);
            before = await store.ReadSnapshotAsync(CancellationToken.None);
            liveLength = new FileInfo(store.FilePath).Length;
        }

        // What a restart actually finds after a crash between writing the temporary and replacing the
        // live log: a temporary that is either a complete published generation or a torn prefix of one.
        byte[] live = await File.ReadAllBytesAsync(Path.Combine(temporary.Root.Path, FileBrokerLifecycleStore.FileName));
        byte[] planted = temporaryIsComplete ? live : live[..(live.Length / 3)];
        string temporaryPath = Path.Combine(temporary.Root.Path, FileBrokerLifecycleStore.CompactionFileName);
        using (FileStream write = temporary.Root.OpenOwnedFile(
            FileBrokerLifecycleStore.CompactionFileName,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            FileOptions.WriteThrough))
        {
            write.Write(planted);
            write.Flush(flushToDisk: true);
        }

        using var reopened = new FileBrokerLifecycleStore(temporary.Root);

        AssertSameState(before, await reopened.ReadSnapshotAsync(CancellationToken.None));
        Assert.Equal(0, reopened.Recovery.Generation);
        Assert.Equal(0, reopened.Recovery.TruncatedTailBytes);
        Assert.Equal(planted.Length, reopened.Recovery.DiscardedCompactionBytes);
        Assert.False(File.Exists(temporaryPath));
        Assert.Equal(liveLength, new FileInfo(reopened.FilePath).Length);
    }

    [Fact(DisplayName = "R16: a log that would pass its bound compacts itself instead of stopping the broker")]
    public async Task ALogThatWouldPassItsBoundCompactsItself()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        var clock = new ManualTimeProvider(StartTime);
        CaptureId captureId;
        long bound;
        using (var sizing = new FileBrokerLifecycleStore(temporary.Root))
        {
            captureId = await RunLifecycle(sizing, clock);
            bound = new FileInfo(sizing.FilePath).Length;
        }

        using var store = new FileBrokerLifecycleStore(temporary.Root, bound);
        using var coordinator = new BrokerLifecycleCoordinator(
            new PreparedPlanRegistry(clock),
            store,
            new BrokerFakeRuntime(),
            clock);

        for (int renewal = 0; renewal < 8; renewal++)
        {
            BrokerLeaseOutcome outcome = await coordinator.RenewOwnerLeaseAsync(captureId, OwnerA);
            Assert.Equal(BrokerOperationCode.LeaseRenewed, outcome.Code);
        }

        Assert.True(store.Generation >= 1, "the log never compacted itself");
        Assert.True(new FileInfo(store.FilePath).Length <= bound);
        Assert.Contains(
            (await store.ReadSnapshotAsync(CancellationToken.None)).Captures,
            capture => capture.CaptureId == captureId);
    }

    [Fact(DisplayName = "R16: a bound one frame cannot fit is a named refusal, not a silent compaction loop")]
    public async Task ABoundOneFrameCannotFitIsRefused()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        var clock = new ManualTimeProvider(StartTime);
        var registry = new PreparedPlanRegistry(clock);
        PreparedPlanGrant grant = registry.Issue(PreparedFocused(), OwnerA);
        using var store = new FileBrokerLifecycleStore(temporary.Root, 128);
        using var coordinator = new BrokerLifecycleCoordinator(registry, store, new BrokerFakeRuntime(), clock);

        BrokerStartOutcome outcome = await coordinator.StartAsync(grant.Token, Guid.NewGuid(), OwnerA);

        Assert.Equal(BrokerOperationCode.PersistenceFailure, outcome.Code);
        Assert.Contains("cannot be compacted within", outcome.FailureReason!, StringComparison.Ordinal);
        Assert.Empty((await store.ReadSnapshotAsync(CancellationToken.None)).Captures);
        Assert.Equal(0, store.Generation);
        Assert.Equal(0, new FileInfo(store.FilePath).Length);
    }

    [Fact(DisplayName = "R16: a store bound outside its declared range is refused before the log is opened")]
    public void AStoreBoundOutsideItsDeclaredRangeIsRefused()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();

        Assert.Throws<ArgumentOutOfRangeException>(() => new FileBrokerLifecycleStore(temporary.Root, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FileBrokerLifecycleStore(temporary.Root, FileBrokerLifecycleStore.MaximumFileBytes + 1));
        Assert.Empty(Directory.GetFiles(temporary.Root.Path));
    }

    [Fact(DisplayName = "R16: a frame from another generation is refused rather than read as a later state")]
    public async Task AFrameFromAnotherGenerationIsRefused()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        var clock = new ManualTimeProvider(StartTime);
        string path = Path.Combine(temporary.Root.Path, FileBrokerLifecycleStore.FileName);
        using (var store = new FileBrokerLifecycleStore(temporary.Root))
        {
            _ = await RunLifecycle(store, clock);
        }

        // The second frame of generation 0: a frame whose own sequence continues a compacted log and
        // whose checksum is valid, so only its generation says it is not this log's next state.
        byte[] foreignFrame = Frames(await File.ReadAllBytesAsync(path))[1];
        using (var store = new FileBrokerLifecycleStore(temporary.Root))
        {
            _ = store.Compact();
        }

        long compactedLength = new FileInfo(path).Length;
        await File.AppendAllBytesAsync(path, foreignFrame);

        using var reopened = new FileBrokerLifecycleStore(temporary.Root);

        Assert.Equal(1, reopened.Recovery.Generation);
        Assert.Equal(1, reopened.Recovery.RecoveredSequence);
        Assert.Equal(foreignFrame.Length, reopened.Recovery.TruncatedTailBytes);
        Assert.Contains("generation", reopened.Recovery.RecoveryReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(compactedLength, new FileInfo(path).Length);
    }

    private static void AssertSameState(BrokerLifecycleSnapshot expected, BrokerLifecycleSnapshot actual)
    {
        Assert.Equal(expected.Captures, actual.Captures);
        Assert.Equal(expected.Requests, actual.Requests);
    }

    /// <summary>Splits a log into its frames: a 56-byte header whose payload length sits at offset 20, then a 12-byte trailer.</summary>
    private static List<byte[]> Frames(byte[] log)
    {
        List<byte[]> frames = [];
        int offset = 0;
        while (offset + 56 + 12 <= log.Length)
        {
            int payloadLength = BitConverter.ToInt32(log, offset + 20);
            int frameLength = 56 + payloadLength + 12;
            Assert.InRange(frameLength, 69, log.Length - offset + 1);
            frames.Add(log[offset..(offset + frameLength)]);
            offset += frameLength;
        }

        Assert.Equal(log.Length, offset);
        return frames;
    }

    private static async Task<CaptureId> RunLifecycle(FileBrokerLifecycleStore store, ManualTimeProvider clock)
    {
        var registry = new PreparedPlanRegistry(clock);
        PreparedPlanGrant grant = registry.Issue(PreparedFocused(), OwnerA);
        using var coordinator = new BrokerLifecycleCoordinator(
            registry,
            store,
            new BrokerFakeRuntime(),
            clock);
        BrokerStartOutcome start = await coordinator.StartAsync(grant.Token, Guid.NewGuid(), OwnerA);
        CaptureId captureId = start.CaptureId!.Value;
        _ = await coordinator.RenewOwnerLeaseAsync(captureId, OwnerA);
        _ = await coordinator.RenewOwnerLeaseAsync(captureId, OwnerA);
        return captureId;
    }
}
