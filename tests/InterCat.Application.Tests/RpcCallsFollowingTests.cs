using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// S3 for a session's RPC calls: following every call through ALPC reads every segment's ALPC records, which a session still
/// being recorded keeps no summary of, so a first view that does not wait for them draws the session without their edges
/// and says they are to come; a projection that follows them draws exactly what one that waited draws. They clear the
/// shared derivation cache, so they run alone.
/// </summary>
[Collection(SharedDerivationCache.Name)]
public sealed class RpcCallsFollowingTests
{
    private const string Following = "Which process called which over RPC is still being followed through ALPC";

    [Fact(DisplayName = "S3: a first view that does not wait for a live session's RPC calls opens only its newest chunk's segments, draws no RPC edge and says they are still being followed; following them draws what waiting drew")]
    public void AFirstViewDoesNotWaitForRpcCalls()
    {
        // The linked calls in three chunks by their readings, as a live follow publishes them; the twin holds them in one
        // generation and keeps nothing, so its overview follows every call.
        (ObservationRowV1[] linked, SourceFieldRowV1[] fields) = LinkedRpcCalls();
        ObservationRowV1[] rows = [.. linked.Select(row => row with { SessionRelativeTicks = row.NativeTicks * 100 })];
        long[] ends = [150, 260, long.MaxValue];
        using var session = new TemporarySession();
        using var twin = new TemporarySession();
        for (int chunk = 0; chunk < ends.Length; chunk++)
        {
            long from = chunk == 0 ? long.MinValue : ends[chunk - 1];
            bool Holds(long ticks) => ticks >= from && ticks < ends[chunk];
            Publish(session.Store, [.. rows.Where(row => Holds(row.NativeTicks))], rowsPerSegment: 4,
                fields: [.. fields.Where(field => Holds(field.NativeTicks))], coverage: chunk == 0 ? RpcLedger(alpc: true) : null,
                indexer: LiveCheckpoints.Instance);
        }

        Publish(twin.Store, rows, fields: fields, coverage: RpcLedger(alpc: true));
        SessionDerivationCache.Clear();
        SessionOverviewBundle waited = SessionOverviewProjector.Project(twin.Store);
        CommunicationEdge[] calls = [.. waited.Edges.Where(edge => edge.Mechanism == Mechanism.Rpc)];
        Assert.NotEmpty(calls);
        Assert.False(waited.RpcCallsPending);

        // Opened cold: its processes and counts come from what the generation before it staged, so only the newest
        // chunk's segments are read, and the calls are not followed.
        SessionDerivationCache.Clear();
        SessionStore viewer = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(session.Path));
        SessionOverviewBundle first = SessionOverviewProjector.Project(viewer, followRpcCalls: false);
        SessionManifestV1 viewed = viewer.Current!;
        Assert.InRange(viewer.SegmentReaderCache.Entries, 1, SessionSegments.Names(viewed).Concat(SessionSegments.FieldNames(viewed))
            .Count(name => SessionSegments.PublishingGeneration(name) == viewed.Generation));
        Assert.Null(SessionDerivationCache.For(viewed).DerivedRpcPeers);
        Assert.True(first.RpcCallsPending);
        Assert.DoesNotContain(first.Edges, edge => edge.Mechanism == Mechanism.Rpc);
        Assert.EndsWith(":rpc:pending", first.GraphIdentity, StringComparison.Ordinal);
        Assert.Contains(first.Caveats, caveat => caveat.StartsWith(Following, StringComparison.Ordinal));
        Assert.Equal(waited.Census! with { }, first.Census);
        Assert.Null(RelationshipCensusText.RpcGuidance(first.Census!));

        // What it does draw is what waiting drew, but for the calls' edges and the rows they hold.
        Assert.Equal(Without(waited.Edges), first.Edges);
        Assert.Equal(waited.Nodes.Select(node => (node.Id, node.Records)), first.Nodes.Select(node => (node.Id, node.Records)));
        Assert.Equal(waited.GraphEligibleRows - calls.Sum(edge => edge.ObservationCount), first.GraphEligibleRows);

        // Followed, its calls' edges are drawn as waiting drew them, and a view that does not wait draws them from then on.
        SessionOverviewBundle followed = SessionOverviewProjector.Project(viewer);
        Assert.False(followed.RpcCallsPending);
        Assert.Equal(waited.Edges, followed.Edges);
        Assert.DoesNotContain(followed.Caveats, caveat => caveat.StartsWith(Following, StringComparison.Ordinal));
        Assert.DoesNotContain(":rpc:", followed.GraphIdentity, StringComparison.Ordinal);
        SessionOverviewBundle again = SessionOverviewProjector.Project(viewer, followRpcCalls: false);
        Assert.Equal((false, followed.GraphIdentity), (again.RpcCallsPending, again.GraphIdentity));
        Assert.Equal(waited.Edges, again.Edges);
        viewer.ReleaseSegmentReaders();
    }

    [Fact(DisplayName = "S3: a first view draws RPC edges at once from the links a finished session keeps, and has nothing to follow where its capture collected no ALPC")]
    public void NothingIsPendingWhereNothingIsToFollow()
    {
        (ObservationRowV1[] rows, SourceFieldRowV1[] fields) = LinkedRpcCalls();
        using var finished = new TemporarySession();
        Publish(finished.Store, rows, fields: fields, coverage: RpcLedger(alpc: true));
        SessionDerivationCache.Clear();
        SessionOverviewBundle waited = SessionOverviewProjector.Project(finished.Store);
        Assert.Equal(CheckpointOutcome.Published, SessionCheckpoints.Publish(finished.Store, Committed).Outcome);

        // Its overview keeps the links, so a first view draws them opening no segment.
        SessionDerivationCache.Clear();
        SessionStore reopened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(finished.Path));
        SessionOverviewBundle first = SessionOverviewProjector.Project(reopened, followRpcCalls: false);
        Assert.Equal((false, 0), (first.RpcCallsPending, reopened.SegmentReaderCache.Entries));
        Assert.Equal(waited.Edges, first.Edges);
        Assert.DoesNotContain(first.Caveats, caveat => caveat.StartsWith(Following, StringComparison.Ordinal));

        // Without ALPC no call is followed, so none is to come, and the graph says how to draw them instead.
        using var plain = new TemporarySession();
        Publish(plain.Store, rows, fields: fields, coverage: RpcLedger(alpc: false));
        SessionDerivationCache.Clear();
        SessionOverviewBundle unlinked = SessionOverviewProjector.Project(plain.Store, followRpcCalls: false);
        Assert.False(unlinked.RpcCallsPending);
        Assert.DoesNotContain(unlinked.Edges, edge => edge.Mechanism == Mechanism.Rpc);
        Assert.NotNull(RelationshipCensusText.RpcGuidance(unlinked.Census!));
        Assert.Null(SessionDerivationCache.For(plain.Store.Current!).DerivedRpcPeers);
    }

    private static CommunicationEdge[] Without(IEnumerable<CommunicationEdge> edges) =>
        [.. edges.Where(edge => edge.Mechanism != Mechanism.Rpc)];
}
