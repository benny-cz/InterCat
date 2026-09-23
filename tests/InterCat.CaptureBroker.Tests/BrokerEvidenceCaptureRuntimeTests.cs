using System.Runtime.Versioning;
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
        var host = new ScriptedHost();
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
        var host = new ScriptedHost();
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

        BrokerCaptureOwnership wrong = ownership with { Session = new("InterCat-unrelated", Guid.NewGuid()) };
        BrokerRuntimeStopOutcome refused = await runtime.StopAsync(wrong, CancellationToken.None);
        Assert.Equal(BrokerStopMilestones.None, refused.Milestones);
        Assert.Single(host.Reclaimed);
    }

    [Fact(DisplayName = "R16: a new capture never adopts a pre-existing evidence directory")]
    public async Task StartRefusesPreExistingEvidenceDirectory()
    {
        using TemporaryBrokerRoot temporary = TemporaryBrokerRoot.Create();
        var host = new ScriptedHost();
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
        var host = new ScriptedHost();
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
        var host = new ScriptedHost();
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

    private sealed class ScriptedHost : IEtwSessionHost, IEtwSessionReclaimer
    {
        public List<string> Active { get; } = [];
        public List<string> Created { get; } = [];
        public List<string> Reclaimed { get; } = [];
        public bool? IsElevated => true;

        public IReadOnlyList<string> ListActiveSessionNames() => Active;

        public IOwnedEtwSession CreateExclusive(OwnedSessionPlan plan)
        {
            if (Active.Contains(plan.Identity.SessionName, StringComparer.Ordinal))
            {
                throw new EtwSessionException("The exact session already exists.");
            }

            Active.Add(plan.Identity.SessionName);
            Created.Add(plan.Identity.SessionName);
            return new Session(this, plan.Identity.SessionName);
        }

        public bool StopPreviouslyOwnedSession(string sessionName, Guid ownershipToken)
        {
            Assert.EndsWith(ownershipToken.ToString("N"), sessionName, StringComparison.Ordinal);
            Reclaimed.Add(sessionName);
            return Active.Remove(sessionName);
        }

        private sealed class Session(ScriptedHost host, string name) : IOwnedEtwSession
        {
            private volatile bool stop;
            public string SessionName => name;
            public ProviderEnablementResult Enable(ProviderEnablementRequest request) =>
                new(request.SourceId, true, null);
            public bool TryRequestCaptureState(ProviderEnablementRequest request, out string? failureReason)
            {
                failureReason = null;
                return true;
            }

            public void Pump(EventAdmissionTable table, IAdmittedEventSink sink, CancellationToken token)
            {
                while (!stop && !token.IsCancellationRequested)
                {
                    Thread.Sleep(1);
                }
            }

            public void RequestStopProcessing() => stop = true;
            public SourceLossReading ReadLoss() => new(0, 0);
            public void StopSession() => host.Active.Remove(name);
            public void Dispose() { }
        }
    }
}
