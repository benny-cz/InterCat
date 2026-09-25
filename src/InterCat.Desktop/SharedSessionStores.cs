using InterCat.Storage;

namespace InterCat.Desktop;

/// <summary>
/// One verified reader store per session directory for this process. Opening a store re-measures and hashes every file
/// the current generation names (ADR-025): about 0.4 s for a million-record session. A store already open hashes only
/// files it has not verified, and every lease still reads whichever generation is current. So every workspace of a
/// session - one per live publication, one per reopen - shares one store instead of hashing the whole session again
/// before its first answer (§6.8).
/// </summary>
internal static class SharedSessionStores
{
    /// <summary>Sessions kept open at once; a store holds no file handle between reads, only what it has verified.</summary>
    private const int Capacity = 4;

    private static readonly Lock Gate = new();
    private static readonly List<(string Path, SessionStore Store)> Recent = [];

    /// <summary>
    /// The shared store of a session directory. When <paramref name="sessionId"/> is given and the directory now holds
    /// another session, a store for that one is opened instead: a store never reads one session as another.
    /// </summary>
    public static SessionStore Open(string sessionPath, Guid? sessionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionPath);
        string key = Path.GetFullPath(sessionPath);
        lock (Gate)
        {
            int index = Recent.FindIndex(entry => string.Equals(entry.Path, key, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                (string Path, SessionStore Store) entry = Recent[index];
                Recent.RemoveAt(index);
                if (sessionId is null || entry.Store.SessionId == sessionId)
                {
                    Recent.Insert(0, entry);
                    return entry.Store;
                }
            }
        }

        // Opening hashes the session, so it happens outside the lock; two first readers may both open, and the later one
        // is simply the one kept.
        SessionStore opened = SessionStore.OpenExisting(LocalOwnedDirectory.Open(key));
        lock (Gate)
        {
            Recent.RemoveAll(entry => string.Equals(entry.Path, key, StringComparison.OrdinalIgnoreCase));
            Recent.Insert(0, (key, opened));
            if (Recent.Count > Capacity)
            {
                Recent.RemoveAt(Recent.Count - 1);
            }
        }

        return opened;
    }
}
