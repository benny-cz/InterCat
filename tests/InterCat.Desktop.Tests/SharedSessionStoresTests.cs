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
}
