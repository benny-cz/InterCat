using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Application;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// The graph's RPC edges: a process and the process that served its calls, joined by calls linked through ALPC, counted
/// by the call records at both ends and drawn only when the capture collected ALPC (`contracts/operations-v1.md` §5c).
/// </summary>
public sealed class RpcPeerEdgesTests
{
    [Fact(DisplayName = "§7.4: the overview joins a caller and the process that served it by an RPC edge of the linked calls' records")]
    public void TheOverviewDrawsAnRpcEdge()
    {
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = LinkedCalls();
        Publish(session.Store, rows, fields: fields, coverage: RpcLedger(alpc: true));

        SessionOverviewBundle overview = SessionOverviewProjector.Project(session.Store);
        ProcessInstanceId client = overview.Nodes.Single(node => node.ProcessId == 400).Id;
        ProcessInstanceId host = overview.Nodes.Single(node => node.ProcessId == 1_960).Id;
        CommunicationEdge edge = Assert.Single(overview.Edges, candidate => candidate.Mechanism == Mechanism.Rpc);

        // Two linked calls, each a client start and stop and a server start and stop; the ALPC records are not counted.
        Assert.Equal((8L, (long?)null, RelationStrength.Correlated), (edge.ObservationCount, edge.KnownBytes, edge.Strength));
        Assert.Equal(new HashSet<ProcessInstanceId> { client, host }, [edge.SourceId, edge.TargetId]);

        // The caller made both calls, and the process that served them none, whichever end the edge names first.
        Assert.Equal((2, 0), edge.SourceId == client ? (edge.SourceConnected, edge.TargetConnected) : (edge.TargetConnected, edge.SourceConnected));

        // It names the rule that linked its calls, and rests on its own key, which opens them.
        Assert.Equal((RelationRule.RpcCallPeer, RpcPeerIndex.PeerRule), (edge.Rule, edge.Rule.ToString()));
        Assert.Equal([edge.Key], edge.Evidence);
        Assert.Contains(overview.Caveats, caveat => caveat.StartsWith("RPC edges join a process", StringComparison.Ordinal));

        // Under a brush holding only the first call, the edge counts only that link's records.
        SessionIntervalCounts scoped = SessionIntervalQuery.Count(session.Store, new TimeRange(0, 150));
        Assert.Equal(4, scoped.EdgeRecords[edge.Key]);
    }

    [Fact(DisplayName = "§7.4: a capture whose ledger names no ALPC draws no RPC edge, whatever its records hold")]
    public void WithoutCollectedAlpcNoRpcEdgeIsDrawn()
    {
        using var withoutAlpc = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = LinkedCalls();
        Publish(withoutAlpc.Store, rows, fields: fields, coverage: RpcLedger(alpc: false));
        Assert.DoesNotContain(SessionOverviewProjector.Project(withoutAlpc.Store).Edges, edge => edge.Mechanism == Mechanism.Rpc);

        using var legacy = new TemporarySession();
        Publish(legacy.Store, rows, fields: fields);
        Assert.DoesNotContain(SessionOverviewProjector.Project(legacy.Store).Edges, edge => edge.Mechanism == Mechanism.Rpc);
        Assert.DoesNotContain(SessionIntervalQuery.Count(legacy.Store, new TimeRange(0, 150)).EdgeRecords.Keys,
            key => key.StartsWith(RpcPeerEdges.KeyPrefix, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "§7.4: a capture still recording draws RPC edges once the plan it records under admits ALPC, before its ledger says what it collected")]
    public void ALiveCaptureDrawsRpcEdgesByItsPlan()
    {
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = LinkedCalls();

        // An RPC peers capture publishes its plan with its first chunk and its ledger with its last: in between, the plan
        // says it collects ALPC, and the graph draws the calls' other ends as they arrive, a brush counting them too.
        using var recording = new TemporarySession();
        Publish(recording.Store, rows, fields: fields, plan: Plan(Mechanism.ProcessLifecycle, Mechanism.Rpc, Mechanism.Alpc));
        SessionOverviewBundle overview = SessionOverviewProjector.Project(recording.Store);
        CommunicationEdge edge = Assert.Single(overview.Edges, candidate => candidate.Mechanism == Mechanism.Rpc);
        Assert.Equal(8, edge.ObservationCount);
        Assert.True(overview.Census!.RpcLinked);
        Assert.Equal(4, SessionIntervalQuery.Count(recording.Store, new TimeRange(0, 150)).EdgeRecords[edge.Key]);
        Assert.Equal(
            new HashSet<Mechanism> { Mechanism.ProcessLifecycle, Mechanism.Rpc, Mechanism.Alpc },
            CaptureAdmissions.Of(recording.Store.Root, recording.Store.Current!));

        // An Explore capture's plan admits no ALPC: no RPC edge, whatever its records hold, and the census says so.
        using var explore = new TemporarySession();
        Publish(explore.Store, rows, fields: fields, plan: Plan(Mechanism.ProcessLifecycle, Mechanism.Rpc, Mechanism.Tcp));
        SessionOverviewBundle without = SessionOverviewProjector.Project(explore.Store);
        Assert.DoesNotContain(without.Edges, candidate => candidate.Mechanism == Mechanism.Rpc);
        Assert.False(without.Census!.RpcLinked);
        Assert.DoesNotContain(SessionIntervalQuery.Count(explore.Store, new TimeRange(0, 150)).EdgeRecords.Keys,
            key => key.StartsWith(RpcPeerEdges.KeyPrefix, StringComparison.Ordinal));

        // Once a ledger is published it decides: one naming no ALPC draws none, whatever the plan admitted.
        using var finished = new TemporarySession();
        Publish(finished.Store, rows, fields: fields, coverage: RpcLedger(alpc: false),
            plan: Plan(Mechanism.ProcessLifecycle, Mechanism.Rpc, Mechanism.Alpc));
        Assert.DoesNotContain(SessionOverviewProjector.Project(finished.Store).Edges, candidate => candidate.Mechanism == Mechanism.Rpc);

        // A plan that is no normalizer-plan-v1 is refused, never read as admitting nothing: one without its descriptors, or
        // without the contract it is written to.
        foreach (byte[] refused in new[]
        {
            "{\"contract\":\"normalizer-plan-v1\"}"u8.ToArray(),
            "{\"descriptors\":[{\"mechanism\":9}]}"u8.ToArray(),
        })
        {
            using var broken = new TemporarySession();
            Publish(broken.Store, rows, fields: fields, plan: refused);
            Assert.Throws<InvalidDataException>(() => SessionOverviewProjector.Project(broken.Store));
        }

        // One written to another contract, as a later writer's may be, says nothing this reader can read of what it
        // admitted: its RPC edges wait for the ledger rather than be misread.
        using var later = new TemporarySession();
        Publish(later.Store, rows, fields: fields, plan: System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            contract = "normalizer-plan-v2",
            descriptors = new[] { new { mechanism = (int)Mechanism.Alpc } },
        }));
        Assert.Null(CaptureAdmissions.Of(later.Store.Root, later.Store.Current!));
        Assert.DoesNotContain(SessionOverviewProjector.Project(later.Store).Edges, candidate => candidate.Mechanism == Mechanism.Rpc);

        // A session that retains no plan, as one imported before plans were kept, has nothing to say of what it admitted.
        using var legacy = new TemporarySession();
        Publish(legacy.Store, rows, fields: fields);
        Assert.Null(CaptureAdmissions.Of(legacy.Store.Root, legacy.Store.Current!));
    }

    [Fact(DisplayName = "§7.4: a capture that never published its ledger keeps its RPC links with its checkpoint by the plan it admitted ALPC under, so its first view follows no call")]
    public void AnUnfinishedCaptureKeepsItsRpcLinks()
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = LinkedCalls();
        Publish(session.Store, rows, fields: fields, plan: Plan(Mechanism.ProcessLifecycle, Mechanism.Rpc, Mechanism.Alpc));
        Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(session.Store, Committed).Outcome);

        SessionDerivationCache.Clear();
        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        SessionOverviewBundle overview = SessionOverviewProjector.Project(reopened);
        Assert.Equal(8, Assert.Single(overview.Edges, edge => edge.Mechanism == Mechanism.Rpc).ObservationCount);
        Assert.Equal(0, reopened.SegmentReaderCache.Entries);
        reopened.ReleaseSegmentReaders();
    }

    /// <summary>A normalizer plan's descriptors, as much of `normalizer-plan-v1` as says what a capture admits.</summary>
    private static byte[] Plan(params Mechanism[] mechanisms) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
    {
        contract = "normalizer-plan-v1",
        descriptors = mechanisms.Select(mechanism => new { mechanism = (int)mechanism }),
    });

    /// <summary>The service control manager's calls, linked through ALPC (<see cref="TestSessions.LinkedRpcCalls"/>).</summary>
    internal static (ObservationRowV1[] Rows, SourceFieldRowV1[] Fields) LinkedCalls() => TestSessions.LinkedRpcCalls();
}
