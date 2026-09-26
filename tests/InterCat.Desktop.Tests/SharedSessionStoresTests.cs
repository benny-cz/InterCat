using InterCat.Analysis.Tests;
using InterCat.Desktop;
using InterCat.Domain;
using InterCat.Storage;
using Xunit;
using static InterCat.Analysis.Tests.TestSessions;

namespace InterCat.Desktop.Tests;

/// <summary>
/// One verified store per session directory (§6.8): a store hashes everything its generation names when it opens, so a
/// workspace per live publication must not open, and hash, the session again before its first answer.
/// </summary>
public sealed class SharedSessionStoresTests
{
    [Fact(DisplayName = "§6.8: every workspace of a session shares one verified store, and none is read as another session")]
    public void WorkspacesOfASessionShareOneStore()
    {
        using var session = new TemporarySession();
        Publish(session.Store, [Lifecycle(1, ObservationKind.Create, 100, 1) with { SessionRelativeTicks = 100 }]);

        // The same directory, however it is spelled, is one store for every reader that expects its session.
        SessionStore first = SharedSessionStores.Open(session.Path);
        Assert.Same(first, SharedSessionStores.Open(session.Path, first.SessionId));
        Assert.Same(first, SharedSessionStores.Open(session.Path.ToUpperInvariant()));

        // A reader that expects another session is not handed this one's store; it opens what the directory now holds,
        // and its own session check then refuses to read it as the session it wanted.
        SessionStore other = SharedSessionStores.Open(session.Path, Guid.NewGuid());
        Assert.NotSame(first, other);
        Assert.Equal(first.SessionId, other.SessionId);
        Assert.Same(other, SharedSessionStores.Open(session.Path));
    }

    [Fact(DisplayName = "§20.1: only the session opened last keeps its cached segment readers, and returning hashes nothing again")]
    public void OnlyTheSessionOpenedLastKeepsItsReaders()
    {
        using var shown = new TemporarySession();
        using var next = new TemporarySession();
        using var third = new TemporarySession();
        foreach (TemporarySession session in new[] { shown, next, third })
        {
            Publish(session.Store, [Lifecycle(1, ObservationKind.Create, 100, 1) with { SessionRelativeTicks = 100 }]);
        }

        // A registry of its own: the process-wide one is shared with every test that opens a workspace meanwhile.
        var registry = new SessionStoreRegistry(capacity: 2);
        SessionStore first = registry.Open(shown.Path);
        ReadEverySegment(first);
        Assert.Equal(SessionSegments.Names(first.Current!).Count + SessionSegments.FieldNames(first.Current!).Count,
            first.SegmentReaderCache.Entries);

        SessionStore second = registry.Open(next.Path);
        ReadEverySegment(second);
        Assert.Equal(0, first.SegmentReaderCache.Entries);
        Assert.Equal(0, first.SegmentReaderCache.AdmittedPayloadBytes);
        Assert.NotEqual(0, second.SegmentReaderCache.Entries);

        // Back to the first session: the same verified store, whose next reads open the segments again.
        Assert.Same(first, registry.Open(shown.Path));
        Assert.Equal(0, second.SegmentReaderCache.Entries);
        ReadEverySegment(first);
        Assert.NotEqual(0, first.SegmentReaderCache.Entries);

        // A window still holding the second store reads through it without the registry. Once the registry stops
        // keeping that store, it gives those readers up too.
        ReadEverySegment(second);
        Assert.NotEqual(0, second.SegmentReaderCache.Entries);
        _ = registry.Open(third.Path);
        Assert.Equal(0, second.SegmentReaderCache.Entries);
        Assert.NotSame(second, registry.Open(next.Path));
    }

    private static void ReadEverySegment(SessionStore store)
    {
        using EvidenceLease lease = store.AcquireLease();
        foreach (string name in SessionSegments.Names(lease.Manifest).Concat(SessionSegments.FieldNames(lease.Manifest)))
        {
            _ = SessionSegments.Open(store, lease.Manifest, name);
        }
    }
}
