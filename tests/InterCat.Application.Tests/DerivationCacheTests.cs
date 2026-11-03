using InterCat.Analysis;
using InterCat.Analysis.Tests;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Application.Tests;

/// <summary>
/// The derivations kept between a live session's generations (ADR-054 decision 5): each kept generation holds a copy of its
/// own, so of one session only the newest derived is kept, which the next generation's derivations extend, beside the one
/// asked for (revision 881).
/// </summary>
[Collection(SharedDerivationCache.Name)]
public sealed class DerivationCacheTests
{
    [Fact(DisplayName = "§12: of a live session's generations the derivation cache keeps the newest derived, which the next extends, and lets go each earlier one it covers")]
    public void TheDerivationCacheLetsGoWhatTheNewestCovers()
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        ObservationRowV1[] rows = DerivedRankingTests.Conversation();
        var manifests = new List<SessionManifestV1>();
        void Publication(int chunk)
        {
            _ = Publish(session.Store, rows[(chunk * 3)..((chunk + 1) * 3)], rowsPerSegment: 3);
            manifests.Add(session.Store.Current!);
            _ = SessionOverviewProjector.Project(session.Store);
        }

        // A live view projects each publication, its derivations extending the generation's before: the newest is kept,
        // and at most the one it extended, never an older one besides, however many publications go by.
        Guid id = Guid.Empty;
        for (int chunk = 0; chunk < 5; chunk++)
        {
            Publication(chunk);
            id = manifests[^1].SessionId;
            IReadOnlyList<long> kept = SessionDerivationCache.KeptGenerations(id);
            Assert.Equal(manifests[^1].Generation, kept[0]);
            Assert.InRange(kept.Count, 1, 2);
            Assert.All(kept.Skip(1), generation => Assert.Equal(manifests[^2].Generation, generation));
        }

        Assert.True(SessionDerivationCache.For(manifests[4]).ProcessesExtended);
        Assert.True(SessionDerivationCache.For(manifests[4]).RelationsExtended);
        Assert.True(SessionDerivationCache.For(manifests[4]).ActivityExtended);

        // A view asking for an earlier generation is kept the generation it asked for, which a newer generation's then
        // covers and lets go; the newest's derivations extend the newest derived all the while.
        _ = SessionDerivationCache.For(manifests[1]);
        Assert.Contains(manifests[1].Generation, SessionDerivationCache.KeptGenerations(id));
        Publication(5);
        IReadOnlyList<long> after = SessionDerivationCache.KeptGenerations(id);
        Assert.Equal(manifests[5].Generation, after[0]);
        Assert.DoesNotContain(manifests[1].Generation, after);
        Assert.True(SessionDerivationCache.For(manifests[5]).ProcessesExtended);
    }

    [Fact(DisplayName = "§12: a generation holding a derivation the newest has not made is kept, so the newest extends it rather than deriving it whole")]
    public void AGenerationHoldingWhatTheNewestLacksIsKept()
    {
        SessionDerivationCache.Clear();
        using var session = new TemporarySession();
        ObservationRowV1[] rows = DerivedRankingTests.Conversation();
        _ = Publish(session.Store, rows[..9], rowsPerSegment: 3);
        _ = SessionOverviewProjector.Project(session.Store);
        SessionManifestV1 earlier = session.Store.Current!;
        _ = RpcCallsOf(earlier);

        // The next generation's view derives its processes, relations and activity but not yet its calls: the generation
        // whose calls are derived stays, for the newer one's calls to extend.
        _ = Publish(session.Store, rows[9..], rowsPerSegment: 3);
        _ = SessionOverviewProjector.Project(session.Store);
        SessionManifestV1 newer = session.Store.Current!;
        _ = SessionDerivationCache.For(newer);
        Assert.Contains(earlier.Generation, SessionDerivationCache.KeptGenerations(newer.SessionId));
        _ = RpcCallsOf(newer);
        Assert.True(SessionDerivationCache.For(newer).RpcCallsExtended);

        // Once the newer one holds them too, it covers the earlier one, which goes.
        _ = SessionDerivationCache.For(newer);
        Assert.DoesNotContain(earlier.Generation, SessionDerivationCache.KeptGenerations(newer.SessionId));

        RpcCallIndex RpcCallsOf(SessionManifestV1 manifest)
        {
            SegmentReaderV1[] segments = [.. SessionSegments.Names(manifest).Select(name => SessionSegments.Open(session.Store.Root, manifest, name))];
            SegmentReaderV1[] fields = [.. SessionSegments.FieldNames(manifest).Select(name => SessionSegments.Open(session.Store.Root, manifest, name))];
            return SessionDerivationCache.For(manifest).RpcCalls(
                session.Store.Root, segments, SessionSegments.SourceClock(session.Store.Root, manifest)!.Value, fields, CancellationToken.None);
        }
    }
}
