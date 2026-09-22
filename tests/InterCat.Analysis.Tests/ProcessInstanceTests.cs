using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Analysis.Tests;

/// <summary>
/// Process instances derived from a session's own lifecycle records, and the rule that binds every other record to
/// one of them. A PID is never an identity on its own: a reused PID is two instances, and a record of a reused PID is
/// attributed to one of them only as a labelled candidate (R22, I12, identity-v1).
/// </summary>
public sealed class ProcessInstanceTests
{
    [Fact(DisplayName = "I12: a created and exited process is one instance whose lifetime holds both records")]
    public void ACreatedAndExitedProcessIsOneInstance()
    {
        using var session = new TemporarySession();
        Publish(
            session.Store,
            [
                Lifecycle(100, ObservationKind.Create, 400, 1),
                Transfer(150, ObservationKind.Send, AccountingSide.SendSide, 10, 400, 2),
                Lifecycle(200, ObservationKind.Exit, 400, 3, exitCode: 0),
            ]);

        ProcessInstanceIndex index = Derive(session.Store);

        ProcessInstance instance = Assert.Single(index.Instances);
        Assert.Equal((400, 1u, ProcessWitness.Created), (instance.ProcessId, instance.LifecycleEpoch, instance.Witness));
        Assert.Equal((100L, 200L, 0L), (instance.CreatedNativeTicks, instance.ExitedNativeTicks, instance.ExitCode));

        // The lifetime is half-open and ends one tick after the exit, so the exit record is the instance's last.
        Assert.Equal((100L, 201L), (instance.LifetimeStartNativeTicks, instance.LifetimeEndNativeTicks));
        Assert.Equal(ProcessIdentityEvidenceKind.ObservedCreationTime, instance.Key.EvidenceKind);
        Assert.Equal(ProcessEvidenceGaps.None, instance.Gaps);

        Assert.Equal(new ProcessBinding(0, RelationStrength.Direct, ProcessBindingReason.Bound), index.Bind(400, 100, isLifecycleRecord: true));
        Assert.Equal(new ProcessBinding(0, RelationStrength.Direct, ProcessBindingReason.Bound), index.Bind(400, 200, isLifecycleRecord: true));
        Assert.Equal(new ProcessBinding(0, RelationStrength.Correlated, ProcessBindingReason.Bound), index.Bind(400, 150, isLifecycleRecord: false));
    }

    [Fact(DisplayName = "P6: a PID seen only in its own records is one provisional instance, never an invented start")]
    public void APidSeenOnlyInActivityIsProvisional()
    {
        using var session = new TemporarySession();
        Publish(
            session.Store,
            [
                Transfer(300, ObservationKind.Send, AccountingSide.SendSide, 10, 777, 9),
                Transfer(100, ObservationKind.Receive, AccountingSide.ReceiveSide, 20, 777, 5),
                Transfer(200, ObservationKind.Send, AccountingSide.SendSide, 30, 777, 7),
            ]);

        ProcessInstanceIndex index = Derive(session.Store);

        ProcessInstance instance = Assert.Single(index.Instances);
        Assert.Equal(ProcessWitness.ActivityOnly, instance.Witness);
        Assert.Null(instance.CreatedNativeTicks);
        Assert.Null(instance.LifetimeStartNativeTicks);
        Assert.Null(instance.LifetimeEndNativeTicks);

        // Its key names the earliest record that witnesses it, by reading - not the first one delivered.
        Assert.Equal(ProcessIdentityEvidenceKind.ProvisionalInventoryWitness, instance.Key.EvidenceKind);
        Assert.Equal(new RawRecordId(Capture, 1, 1, 5), instance.Key.ProvisionalWitness);
        Assert.Equal(RelationStrength.Correlated, index.Bind(777, 1, isLifecycleRecord: false).Strength);
    }

    [Fact(DisplayName = "I12: 21.1 scenario 5 - a reused PID is two instances, and a late record never moves the newer one's totals")]
    public void AReusedPidIsTwoInstances()
    {
        // PID 400 is created, exits and is reused. A record timestamped inside the first lifetime is delivered last.
        using var session = new TemporarySession();
        Publish(
            session.Store,
            [
                Lifecycle(100, ObservationKind.Create, 400, 1),
                Lifecycle(200, ObservationKind.Exit, 400, 2, exitCode: 1),
                Lifecycle(300, ObservationKind.Create, 400, 3),
                Transfer(350, ObservationKind.Send, AccountingSide.SendSide, 7, 400, 4),
                Transfer(150, ObservationKind.Send, AccountingSide.SendSide, 40, 400, 5),
            ]);

        ProcessInstanceIndex index = Derive(session.Store);

        Assert.Equal(2, index.InstancesOf(400));
        ProcessInstance first = index.Instances[0];
        ProcessInstance second = index.Instances[1];
        Assert.Equal((1u, 2u), (first.LifecycleEpoch, second.LifecycleEpoch));
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal((100L, 201L), (first.LifetimeStartNativeTicks, first.LifetimeEndNativeTicks));
        Assert.Equal(300L, second.LifetimeStartNativeTicks);
        Assert.Null(second.LifetimeEndNativeTicks);

        // The late record lies in the first lifetime, where only the first instance - or a holder the capture never
        // saw - could have made it. It binds there as firmly as a record of a PID that was never reused, whatever
        // order it was delivered in.
        ProcessBinding late = index.Bind(400, 150, isLifecycleRecord: false);
        Assert.Equal((0, RelationStrength.Correlated), (late.Instance, late.Strength));

        // A record in the later lifetime could also be a late record of the first instance, and PID and time cannot
        // tell them apart, so it is a candidate the default policy does not admit.
        ProcessBinding reused = index.Bind(400, 350, isLifecycleRecord: false);
        Assert.Equal((1, RelationStrength.Candidate), (reused.Instance, reused.Strength));
        Assert.False(reused.IsAdmittedUnder(EvidencePolicy.IncludeCorrelated));
        Assert.True(reused.IsAdmittedUnder(EvidencePolicy.IncludeCandidates));

        // Between the exit and the reuse no process held the PID, and before the first creation the capture has no
        // evidence of who did.
        Assert.Equal(ProcessBindingReason.BetweenInstances, index.Bind(400, 250, isLifecycleRecord: false).Reason);
        Assert.Equal(ProcessBindingReason.BeforeFirstEvidence, index.Bind(400, 50, isLifecycleRecord: false).Reason);

        // Grouped by instance, the late record binds to the earlier instance and the newer instance's total never
        // includes it: by default the newer instance's own record is an unadmitted candidate, and with candidates it
        // holds exactly that record's 7 bytes.
        MetricResult byDefault = SentByProcess(session.Store, EvidencePolicy.IncludeCorrelated);
        Assert.Equal(40, ValueOf(byDefault, first.Id));
        Assert.DoesNotContain(byDefault.Groups, group => group.Process?.Id == second.Id);
        MetricGroup notAdmitted = Assert.Single(byDefault.Unattributed);
        Assert.Equal((ProcessBindingReason.NotAdmittedByPolicy, 7L), (notAdmitted.Reason!.Value, notAdmitted.Value!.Value));

        MetricResult withCandidates = SentByProcess(session.Store, EvidencePolicy.IncludeCandidates);
        Assert.Equal(40, ValueOf(withCandidates, first.Id));
        Assert.Equal(7, ValueOf(withCandidates, second.Id));
        Assert.Empty(withCandidates.Unattributed);
        Assert.Equal(1, withCandidates.Groups.Single(group => group.Process?.Id == second.Id).Bindings[RelationStrength.Candidate]);
    }

    [Fact(DisplayName = "I12: a creation with no exit before it ends the earlier instance, and says the exit was not seen")]
    public void AMissingExitIsAGapNotAMerge()
    {
        using var session = new TemporarySession();
        Publish(
            session.Store,
            [
                Lifecycle(100, ObservationKind.Create, 400, 1),
                Lifecycle(300, ObservationKind.Create, 400, 2),
                Lifecycle(400, ObservationKind.Exit, 400, 3),
                Lifecycle(500, ObservationKind.Exit, 400, 4),
            ]);

        ProcessInstanceIndex index = Derive(session.Store);

        Assert.Equal(3, index.Instances.Count);
        Assert.Equal((100L, 300L), (index.Instances[0].LifetimeStartNativeTicks, index.Instances[0].LifetimeEndNativeTicks));
        Assert.Equal(ProcessEvidenceGaps.ExitNotWitnessed, index.Instances[0].Gaps);
        Assert.Equal((300L, 401L), (index.Instances[1].LifetimeStartNativeTicks, index.Instances[1].LifetimeEndNativeTicks));

        // A second exit with no creation between opens an instance flagged with the creation it is missing, bounded
        // below by the end of the one before it.
        ProcessInstance third = index.Instances[2];
        Assert.Equal(ProcessWitness.ExitOnly, third.Witness);
        Assert.Equal(ProcessEvidenceGaps.CreationNotWitnessed, third.Gaps);
        Assert.Equal((401L, 501L), (third.LifetimeStartNativeTicks, third.LifetimeEndNativeTicks));
    }

    [Fact(DisplayName = "I12: a rundown and an exit are one instance that was running before the capture")]
    public void ARundownAndItsExitAreOneInstance()
    {
        using var session = new TemporarySession();
        Publish(
            session.Store,
            [
                Lifecycle(10, ObservationKind.Inventory, 900, 1),
                Transfer(20, ObservationKind.Send, AccountingSide.SendSide, 5, 900, 2),
                Lifecycle(30, ObservationKind.Exit, 900, 3),
                Lifecycle(5, ObservationKind.Exit, 901, 4),
                Transfer(4, ObservationKind.Receive, AccountingSide.ReceiveSide, 6, 901, 5),
                Transfer(9, ObservationKind.Receive, AccountingSide.ReceiveSide, 6, 901, 6),
            ]);

        ProcessInstanceIndex index = Derive(session.Store);

        ProcessInstance rundown = index.Instances.Single(instance => instance.ProcessId == 900);
        Assert.Equal(ProcessWitness.Rundown, rundown.Witness);
        Assert.Null(rundown.LifetimeStartNativeTicks);
        Assert.Equal(31L, rundown.LifetimeEndNativeTicks);
        Assert.NotNull(rundown.RundownRecord);
        Assert.Equal(RelationStrength.Correlated, index.Bind(900, 20, isLifecycleRecord: false).Strength);

        ProcessInstance exitOnly = index.Instances.Single(instance => instance.ProcessId == 901);
        Assert.Equal(ProcessWitness.ExitOnly, exitOnly.Witness);
        Assert.Equal(RelationStrength.Correlated, index.Bind(901, 4, isLifecycleRecord: false).Strength);
        Assert.Equal(ProcessBindingReason.AfterExit, index.Bind(901, 9, isLifecycleRecord: false).Reason);
    }

    [Fact(DisplayName = "R22: a record whose payload names no owner binds to no instance, whatever its header says")]
    public void ARecordWithoutAnOwnerIsUnattributed()
    {
        using var session = new TemporarySession();
        Publish(
            session.Store,
            [
                Transfer(100, ObservationKind.Send, AccountingSide.SendSide, 10, owner: null, 1),
                Transfer(200, ObservationKind.Send, AccountingSide.SendSide, 20, owner: 55, 2),
            ]);

        ProcessInstanceIndex index = Derive(session.Store);

        // The header of the ownerless record names PID 4, and it is never promoted into an owner (§4.1).
        Assert.DoesNotContain(index.Instances, instance => instance.ProcessId == 4);
        Assert.Equal(ProcessBindingReason.NoOwner, index.Bind(null, 100, isLifecycleRecord: false).Reason);

        MetricResult sent = SentByProcess(session.Store, EvidencePolicy.IncludeCorrelated);
        Assert.Equal(30, sent.Value);
        MetricGroup noOwner = Assert.Single(sent.Unattributed);
        Assert.Equal((ProcessBindingReason.NoOwner, 10L), (noOwner.Reason!.Value, noOwner.Value!.Value));
    }

    [Fact(DisplayName = "I14: instance identities do not depend on how the evidence was split into segments")]
    public void IdentitiesDoNotDependOnSegmentation()
    {
        ObservationRowV1[] rows =
        [
            Lifecycle(100, ObservationKind.Create, 400, 1),
            Transfer(120, ObservationKind.Send, AccountingSide.SendSide, 1, 400, 2),
            Lifecycle(200, ObservationKind.Exit, 400, 3),
            Lifecycle(300, ObservationKind.Create, 400, 4),
            Transfer(310, ObservationKind.Send, AccountingSide.SendSide, 1, 500, 5),
            Transfer(320, ObservationKind.Send, AccountingSide.SendSide, 1, 400, 6),
            Lifecycle(330, ObservationKind.Inventory, 600, 7),
        ];

        using var whole = new TemporarySession();
        Publish(whole.Store, rows);
        using var split = new TemporarySession();
        Publish(split.Store, [.. rows.Reverse()], rowsPerSegment: 2);

        ProcessInstanceIndex first = Derive(whole.Store);
        ProcessInstanceIndex second = Derive(split.Store);

        Assert.Equal(first.Instances.Select(instance => instance.Id), second.Instances.Select(instance => instance.Id));
        Assert.Equal(first.Instances, second.Instances);
        foreach (ObservationRowV1 row in rows)
        {
            bool lifecycle = ProcessInstanceIndex.IsLifecycleRecord(row.Mechanism, row.Kind);
            Assert.Equal(
                first.Bind(row.OwnerProcessId, row.NativeTicks, lifecycle),
                second.Bind(row.OwnerProcessId, row.NativeTicks, lifecycle));
        }
    }

    [Fact(DisplayName = "I12: the same PID and creation on another capture's clock is another instance")]
    public void AnotherClockIsAnotherBoot()
    {
        ObservationRowV1[] rows = [Lifecycle(100, ObservationKind.Create, 400, 1)];
        using var here = new TemporarySession();
        Publish(here.Store, rows);
        var otherClock = ClockFor(new ClockId(Guid.Parse("99998888-7777-4666-8555-444433332222")), "session-metrics-tests");
        using var there = new TemporarySession();
        Publish(there.Store, rows, clock: otherClock);

        ProcessInstance a = Assert.Single(Derive(here.Store).Instances);
        ProcessInstance b = Assert.Single(ProcessInstanceIndex.Derive(TestSessions.Segments(there.Store), otherClock).Instances);

        // One monotonic clock scopes one boot, so equal PIDs and equal readings on two clocks are two processes.
        Assert.NotEqual(a.Key.BootId, b.Key.BootId);
        Assert.NotEqual(a.Id, b.Id);
    }

    private static ProcessInstanceIndex Derive(SessionStore store) =>
        ProcessInstanceIndex.Derive(TestSessions.Segments(store), TestClock);

    private static MetricResult SentByProcess(SessionStore store, EvidencePolicy policy) =>
        SessionMetrics.Evaluate(
            store,
            new()
            {
                Basis = AnalysisBasis.SourceObservations,
                Metric = Metric.BytesSent,
                ByteDomain = ByteDomain.TransportObserved,
                AccountingSide = AccountingSide.SendSide,
                Grouping = LaneGrouping.InstanceOnly,
                EvidencePolicy = policy,
            });

    private static long? ValueOf(MetricResult result, ProcessInstanceId id) =>
        result.Groups.Single(group => group.Process?.Id == id).Value;
}
