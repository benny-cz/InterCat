using System.Runtime.Versioning;
using InterCat.Capture.Recording;
using InterCat.Capture.Windows;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.CaptureBroker.Tests.BrokerPlanFixture;

namespace InterCat.CaptureBroker.Tests;

[SupportedOSPlatform("windows")]
public sealed class BrokerEvidenceCaptureRuntimeTests
{
    [Fact(DisplayName = "R16: broker runtime starts only owned ETW and finalizes evidence in its protected directory")]
    public async Task StartStopPublishesEvidenceOnly()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        var host = new ScriptedEtwHost();
        await using var runtime = new BrokerEvidenceCaptureRuntime(temporary.Root, host, host);
        PreparedCapturePlan plan = SmallPlan();
        BrokerCaptureOwnership ownership = Ownership(plan);

        BrokerRuntimeStartOutcome started = await runtime.StartAsync(ownership, plan, CancellationToken.None);
        Assert.True(started.Started, started.FailureReason);
        Assert.Equal([ownership.Session.SessionName], host.Active);
        Assert.Equal([ownership.Session.SessionName], host.Created);

        BrokerRuntimeStopOutcome stopped = await runtime.StopAsync(ownership, CancellationToken.None);
        Assert.True(stopped.Milestones.FullyFinalized, stopped.FailureReason);
        Assert.Empty(host.Active);
        using WindowsBrokerRoot evidence = temporary.Root.OpenCaptureDirectory(ownership.CaptureId);
        Assert.False(evidence.Report.CreatedByThisBroker);
        SessionStore reopened = SessionStore.OpenExisting(evidence);
        Assert.NotNull(reopened.Current);
        Assert.Equal(ownership.CaptureId.Value, reopened.Current.SessionId);
        Assert.Equal(plan.Digest, reopened.Current.SourceIdentity);
    }

    [Fact(DisplayName = "R16: restart cleanup stops exact owned ETW but leaves journal finalization unproven")]
    public async Task RecoveryStopIsHonestAboutInterruptedJournal()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        var host = new ScriptedEtwHost();
        await using var runtime = new BrokerEvidenceCaptureRuntime(temporary.Root, host, host);
        BrokerCaptureOwnership ownership = Ownership(SmallPlan());
        host.Active.Add(ownership.Session.SessionName);

        BrokerRuntimeStopOutcome stopped = await runtime.StopAsync(ownership, CancellationToken.None);

        Assert.True(stopped.Milestones.Requested);
        Assert.True(stopped.Milestones.ProvidersStopped);
        Assert.False(stopped.Milestones.CallbacksDrained);
        Assert.False(stopped.Milestones.JournalFinalized);
        Assert.Empty(host.Active);
        Assert.Equal([ownership.Session.SessionName], host.Reclaimed);
        Assert.False(Directory.Exists(Path.Combine(temporary.Root.Path, $"capture-{ownership.CaptureId.Value:N}")));
        Assert.True(stopped.Terminal);
        Assert.Contains("No evidence had been published", stopped.FailureReason, StringComparison.Ordinal);

        BrokerCaptureOwnership wrong = ownership with { Session = new("InterCat-unrelated", Guid.NewGuid()) };
        BrokerRuntimeStopOutcome refused = await runtime.StopAsync(wrong, CancellationToken.None);
        Assert.Equal(BrokerStopMilestones.None, refused.Milestones);
        Assert.Single(host.Reclaimed);
    }

    [Fact(DisplayName = "R16: restart verifies a committed final publication instead of replaying the old runtime")]
    public async Task RestartVerifiesCommittedFinalPublication()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        var host = new ScriptedEtwHost();
        PreparedCapturePlan plan = SmallPlan(seconds: 1);
        BrokerCaptureOwnership ownership = Ownership(plan) with { State = CaptureLifecycle.Recording };

        await using (var first = new BrokerEvidenceCaptureRuntime(temporary.Root, host, host))
        {
            Assert.True((await first.StartAsync(ownership, plan, CancellationToken.None)).Started);
            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        await using var recovery = new BrokerEvidenceCaptureRuntime(temporary.Root, host, host);
        BrokerRuntimeStopOutcome stopped = await recovery.StopAsync(ownership, CancellationToken.None);

        Assert.True(stopped.Milestones.FullyFinalized, stopped.FailureReason);
        Assert.True(stopped.Milestones.JournalFinalized);
        Assert.True(stopped.Milestones.CallbacksDrained);
        Assert.Null(stopped.FailureReason);
        Assert.Empty(host.Active);
        Assert.Equal([ownership.Session.SessionName], host.Reclaimed);
    }

    [Fact(DisplayName = "R16: restart never treats a complete chunk without finalization evidence as final")]
    public async Task RestartRefusesUnfinalizedOrForeignEvidence()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        var host = new ScriptedEtwHost();
        await using var recovery = new BrokerEvidenceCaptureRuntime(temporary.Root, host, host);
        PreparedCapturePlan plan = SmallPlan();

        // An intermediate publication: the chunk committed, but nothing says it was the last one.
        BrokerCaptureOwnership intermediate = Ownership(plan);
        CommitSyntheticEvidence(temporary.Root, intermediate, marker: null);
        BrokerRuntimeStopOutcome unfinalized = await recovery.StopAsync(intermediate, CancellationToken.None);
        Assert.True(unfinalized.Milestones.ProvidersStopped);
        Assert.False(unfinalized.Milestones.CallbacksDrained);
        Assert.False(unfinalized.Milestones.JournalFinalized);
        Assert.Contains("No capture-finalization marker", unfinalized.FailureReason, StringComparison.Ordinal);

        // A marker naming another capture proves nothing about this one.
        BrokerCaptureOwnership foreign = Ownership(plan);
        CommitSyntheticEvidence(temporary.Root, foreign, Marker(CaptureId.New()));
        BrokerRuntimeStopOutcome refused = await recovery.StopAsync(foreign, CancellationToken.None);
        Assert.False(refused.Milestones.JournalFinalized);
        Assert.False(refused.Milestones.CallbacksDrained);
        Assert.Contains("different capture", refused.FailureReason, StringComparison.Ordinal);

        // The right marker is still not enough when the boundary journal does not replay to its terminal.
        BrokerCaptureOwnership unreplayable = Ownership(plan);
        CommitSyntheticEvidence(temporary.Root, unreplayable, Marker(unreplayable.CaptureId));
        BrokerRuntimeStopOutcome damaged = await recovery.StopAsync(unreplayable, CancellationToken.None);
        Assert.False(damaged.Milestones.JournalFinalized);
        Assert.False(damaged.Milestones.CallbacksDrained);
        Assert.Contains("could not be verified", damaged.FailureReason, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R16: an interrupted capture closes with its published prefix and the dead writer's staging released")]
    public async Task InterruptedCaptureReleasesAbandonedStagingAndIsTerminal()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        var host = new ScriptedEtwHost();
        await using var recovery = new BrokerEvidenceCaptureRuntime(temporary.Root, host, host);
        BrokerCaptureOwnership ownership = Ownership(SmallPlan());
        CommitSyntheticEvidence(temporary.Root, ownership, marker: null);
        string abandoned = $"stg-{Guid.NewGuid():N}";
        using (WindowsBrokerRoot directory = temporary.Root.OpenCaptureDirectory(ownership.CaptureId))
        {
            // What a killed writer leaves: its staged tail and an ownership marker nobody holds any more.
            foreach (string suffix in new[] { ".tmp", ".lease" })
            {
                using FileStream file = directory.OpenOwnedFile(
                    abandoned + suffix, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileOptions.None);
                file.Write("tail"u8);
            }
        }

        BrokerRuntimeStopOutcome stopped = await recovery.StopAsync(ownership, CancellationToken.None);

        Assert.True(stopped.Terminal, stopped.FailureReason);
        Assert.True(stopped.Milestones.ProvidersStopped);
        Assert.False(stopped.Milestones.JournalFinalized);
        Assert.StartsWith("Interrupted:", stopped.FailureReason, StringComparison.Ordinal);
        Assert.Contains("1 journal chunk(s)", stopped.FailureReason, StringComparison.Ordinal);
        string captureDirectory = Path.Combine(temporary.Root.Path, $"capture-{ownership.CaptureId.Value:N}");
        Assert.Empty(Directory.GetFiles(captureDirectory, "stg-*"));
        Assert.NotEmpty(Directory.GetFiles(captureDirectory, "journal-*"));
    }

    [Fact(DisplayName = "R16: staging a live writer still owns keeps an interrupted capture retryable")]
    public async Task StagingStillOwnedKeepsTheCaptureRetryable()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        var host = new ScriptedEtwHost();
        await using var recovery = new BrokerEvidenceCaptureRuntime(temporary.Root, host, host);
        BrokerCaptureOwnership ownership = Ownership(SmallPlan());
        CommitSyntheticEvidence(temporary.Root, ownership, marker: null);
        using WindowsBrokerRoot directory = temporary.Root.OpenCaptureDirectory(ownership.CaptureId);
        SessionStore store = SessionStore.Open(directory, ownership.CaptureId.Value, ownership.PlanDigest);
        using StoreStagingFile owned = store.Stage("journal-0000000002.icatj", StoreDependencyKind.Journal);

        BrokerRuntimeStopOutcome stopped = await recovery.StopAsync(ownership, CancellationToken.None);

        Assert.False(stopped.Terminal);
        Assert.True(stopped.Milestones.ProvidersStopped);
        Assert.NotEmpty(Directory.GetFiles(directory.Path, "stg-*"));
    }

    [Fact(DisplayName = "R16: restart keeps milestones the previous process already made durable")]
    public async Task RestartKeepsDurableMilestones()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        var host = new ScriptedEtwHost();
        await using var recovery = new BrokerEvidenceCaptureRuntime(temporary.Root, host, host);
        BrokerCaptureOwnership ownership = Ownership(SmallPlan()) with
        {
            StopMilestones = new(Requested: true, ProvidersStopped: true, CallbacksDrained: true,
                JournalFinalized: true, AnalysisFinalized: false),
        };

        BrokerRuntimeStopOutcome stopped = await recovery.StopAsync(ownership, CancellationToken.None);

        // Provider shutdown was already proven, so no ETW session is touched again.
        Assert.True(stopped.Milestones.FullyFinalized, stopped.FailureReason);
        Assert.Empty(host.Reclaimed);
        Assert.False(Directory.Exists(Path.Combine(temporary.Root.Path, $"capture-{ownership.CaptureId.Value:N}")));
    }

    [Fact(DisplayName = "R16: a start that could not finish above the free-disk reserve is refused with what to do")]
    public async Task StartRefusesWithoutRoomToFinish()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        var host = new ScriptedEtwHost();
        PreparedCapturePlan plan = SmallPlan();
        long reserve = plan.Quota.MinimumFreeDiskBytes;
        await using var runtime = new BrokerEvidenceCaptureRuntime(
            temporary.Root, host, host, _ => new VolumeSpace(reserve + 1, 4_096));
        BrokerCaptureOwnership ownership = Ownership(plan);

        BrokerRuntimeStartOutcome outcome = await runtime.StartAsync(ownership, plan, CancellationToken.None);

        Assert.False(outcome.Started);
        Assert.Contains("to finish the recording", outcome.FailureReason, StringComparison.Ordinal);
        Assert.Contains("Free space on that volume or lower the reserve", outcome.FailureReason, StringComparison.Ordinal);
        Assert.Empty(host.Created);
        Assert.False(Directory.Exists(Path.Combine(temporary.Root.Path, $"capture-{ownership.CaptureId.Value:N}")));
    }

    [Fact(DisplayName = "R16: free space lost while a capture is idle ends it with a reason and finalized evidence")]
    public async Task IdleFreeSpaceLossStopsCapture()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        var host = new ScriptedEtwHost();
        PreparedCapturePlan plan = SmallPlan();
        long free = 1L << 40;
        await using var runtime = new BrokerEvidenceCaptureRuntime(
            temporary.Root, host, host, _ => new VolumeSpace(Interlocked.Read(ref free), 4_096));
        BrokerCaptureOwnership ownership = Ownership(plan);
        Assert.True((await runtime.StartAsync(ownership, plan, CancellationToken.None)).Started);

        // Another program fills the volume; InterCat is writing nothing, so only the idle monitor can notice.
        Interlocked.Exchange(ref free, plan.Quota.MinimumFreeDiskBytes - 1);
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!await runtime.HasCompletedAsync(ownership.CaptureId, CancellationToken.None))
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "the idle monitor did not end the capture");
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        BrokerRuntimeStopOutcome stopped = await runtime.StopAsync(ownership, CancellationToken.None);
        Assert.True(stopped.Milestones.FullyFinalized, stopped.FailureReason);
        Assert.Contains("fell below the configured", stopped.FailureReason, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R16: a new capture never adopts a pre-existing evidence directory")]
    public async Task StartRefusesPreExistingEvidenceDirectory()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        var host = new ScriptedEtwHost();
        await using var runtime = new BrokerEvidenceCaptureRuntime(temporary.Root, host, host);
        PreparedCapturePlan plan = SmallPlan();
        BrokerCaptureOwnership ownership = Ownership(plan);
        using WindowsBrokerRoot existing = temporary.Root.OpenCaptureDirectory(ownership.CaptureId);

        BrokerRuntimeStartOutcome outcome = await runtime.StartAsync(ownership, plan, CancellationToken.None);

        Assert.False(outcome.Started);
        Assert.Contains("already exists", outcome.FailureReason, StringComparison.Ordinal);
        Assert.Empty(host.Created);
    }

    [Fact(DisplayName = "R16: duration expiry finalizes evidence before a later broker stop reconciles it")]
    public async Task DurationExpiryCanBeReconciledByStop()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        var host = new ScriptedEtwHost();
        await using var runtime = new BrokerEvidenceCaptureRuntime(temporary.Root, host, host);
        PreparedCapturePlan plan = SmallPlan(seconds: 1);
        BrokerCaptureOwnership ownership = Ownership(plan);
        Assert.True((await runtime.StartAsync(ownership, plan, CancellationToken.None)).Started);

        await Task.Delay(TimeSpan.FromSeconds(2));
        BrokerRuntimeStopOutcome stopped = await runtime.StopAsync(ownership, CancellationToken.None);

        Assert.True(stopped.Milestones.FullyFinalized, stopped.FailureReason);
        Assert.Contains("maximum capture duration", stopped.FailureReason, StringComparison.Ordinal);
        Assert.Empty(host.Active);
    }

    [Fact(DisplayName = "R16: real evidence runtime duration stop becomes durable lifecycle status")]
    public async Task DurationStopIsPersistedThroughCoordinator()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        var host = new ScriptedEtwHost();
        await using var runtime = new BrokerEvidenceCaptureRuntime(temporary.Root, host, host);
        using var store = new FileBrokerLifecycleStore(temporary.Root);
        var registry = new PreparedPlanRegistry();
        PreparedPlanGrant grant = registry.Issue(SmallPlan(seconds: 1), OwnerA);
        using var coordinator = new BrokerLifecycleCoordinator(registry, store, runtime);
        BrokerStartOutcome started = await coordinator.StartAsync(grant.Token, Guid.NewGuid(), OwnerA);
        Assert.Equal(BrokerOperationCode.Started, started.Code);

        await Task.Delay(TimeSpan.FromSeconds(2));
        BrokerCaptureOwnership status = (await coordinator.GetStatusAsync(started.CaptureId!.Value, OwnerA))!;

        Assert.Equal(CaptureLifecycle.Closed, status.State);
        Assert.True(status.StopMilestones.FullyFinalized);
        Assert.Contains("maximum capture duration", status.FailureReason, StringComparison.Ordinal);
        Assert.Empty(host.Active);
        BrokerStoredRequest request = Assert.Single(
            (await store.ReadSnapshotAsync(CancellationToken.None)).Requests,
            item => item.Kind == BrokerRequestKind.AutonomousStop);
        Assert.True(request.Completed);
    }

    private static CaptureFinalizationV1 Marker(CaptureId captureId) => new()
    {
        Contract = CaptureFinalizationV1.ContractName,
        CaptureId = captureId.Value,
        FinalizedUtc = DateTimeOffset.UtcNow,
        ProvidersStopped = true,
        CallbacksDrained = true,
    };

    /// <summary>
    /// Commits a store generation whose journal bytes are not a replayable journal. Recovery must reach its
    /// finalization and identity checks before the journal, and must still refuse when only the replay fails.
    /// </summary>
    private static void CommitSyntheticEvidence(
        WindowsBrokerRoot root,
        BrokerCaptureOwnership ownership,
        CaptureFinalizationV1? marker)
    {
        using WindowsBrokerRoot directory = root.OpenCaptureDirectory(ownership.CaptureId);
        SessionStore store = SessionStore.Open(directory, ownership.CaptureId.Value, ownership.PlanDigest);
        byte[] journalBytes = "not a journal"u8.ToArray();
        const string journalName = "journal-0000000001.icatj";
        using StoreStagingFile journal = store.Stage(journalName, StoreDependencyKind.Journal);
        journal.Content.Write(journalBytes);
        _ = journal.Complete();
        var staged = new List<StoreStagingFile> { journal };
        StoreStagingFile? finalization = null;
        try
        {
            if (marker is not null)
            {
                finalization = store.Stage(
                    DerivedGenerationBuilder.CaptureFinalizationFileName(1), StoreDependencyKind.CaptureFinalization);
                finalization.Content.Write(marker.Encode());
                _ = finalization.Complete();
                staged.Add(finalization);
            }

            var boundary = new CommittedBoundary(
                journalName,
                journalBytes.Length,
                1,
                "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(journalBytes)));
            _ = store.Commit(staged, boundary, DateTimeOffset.UtcNow);
        }
        finally
        {
            finalization?.Dispose();
        }
    }

    private static PreparedCapturePlan SmallPlan(int seconds = 30) => BrokerPrepareCompiler.Prepare(
        CompileFocused(),
        new(seconds, 1_048_576, 16_777_216),
        BrokerRetentionPolicy.StopAtLimit,
        Runtime).PreparedPlan!;

    private static BrokerCaptureOwnership Ownership(PreparedCapturePlan plan)
    {
        CaptureId id = CaptureId.New();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new()
        {
            CaptureId = id,
            Owner = OwnerA.Owner,
            Session = BrokerSessionOwnership.Create(id),
            PlanDigest = plan.Digest,
            State = CaptureLifecycle.Starting,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            LeaseExpiresAtUtc = now.AddMinutes(1),
            StopMilestones = BrokerStopMilestones.None,
        };
    }
}
