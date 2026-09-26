using InterCat.Storage;

namespace InterCat.Desktop;

/// <summary>
/// One reader store per session directory for this process. A viewer opens a store by listing what the current
/// generation names, and hashes it after the first view (store-v1 §6): at a million records the open takes 1 ms and the
/// hashing 0.2 s. A store already open hashes only files it has not measured, and every lease still reads whichever
/// generation is current. So every workspace of a session - one per live publication, one per reopen - shares one store
/// instead of measuring the whole session again before its first answer (§6.8).
/// </summary>
internal static class SharedSessionStores
{
    /// <summary>Sessions kept open at once.</summary>
    private const int Capacity = 4;

    /// <summary>The process-wide registry behind <see cref="Open"/>.</summary>
    internal static SessionStoreRegistry Registry { get; } = new(Capacity);

    /// <summary>
    /// The shared store of a session directory. When <paramref name="sessionId"/> is given and the directory now holds
    /// another session, a store for that one is opened instead: a store never reads one session as another.
    /// </summary>
    public static SessionStore Open(string sessionPath, Guid? sessionId = null) => Registry.Open(sessionPath, sessionId);
}

/// <summary>
/// The session stores a viewer keeps open, most recently opened first. A kept store holds no file handle between reads;
/// it keeps what it has verified, so returning to its session hashes nothing again. Only the session opened last keeps
/// its cached segment readers: the others give theirs up, so the process holds one session's working set within
/// §20.1's admission bound rather than one per session it has shown.
/// </summary>
internal sealed class SessionStoreRegistry
{
    private readonly int capacity;
    private readonly Lock gate = new();
    private readonly List<(string Path, SessionStore Store)> recent = [];

    public SessionStoreRegistry(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        this.capacity = capacity;
    }

    /// <inheritdoc cref="SharedSessionStores.Open"/>
    public SessionStore Open(string sessionPath, Guid? sessionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionPath);
        string key = Path.GetFullPath(sessionPath);
        lock (gate)
        {
            SessionStore? kept = recent.Find(entry => SameDirectory(entry.Path, key)).Store;
            if (kept is not null && (sessionId is null || kept.SessionId == sessionId))
            {
                Keep(key, kept);
                return kept;
            }
        }

        // Opening reads the pointer, the manifest and a listing, so it happens outside the lock; two first readers may
        // both open, and the later one is simply the one kept.
        SessionStore opened = SessionStore.OpenForViewing(LocalOwnedDirectory.Open(key));
        lock (gate)
        {
            Keep(key, opened);
        }

        return opened;
    }

    /// <summary>
    /// Makes an open store the shared store of its session directory, as a live capture's writer becomes: every later
    /// <see cref="Open"/> of the directory returns it while it is kept, and it is the session opened last.
    /// </summary>
    public void Adopt(string sessionPath, SessionStore store)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionPath);
        ArgumentNullException.ThrowIfNull(store);
        lock (gate)
        {
            Keep(Path.GetFullPath(sessionPath), store);
        }
    }

    /// <summary>
    /// Keeps a store as its directory's, first. Every other store gives its readers up: those of other sessions, one it
    /// replaces for the same directory, and one that falls off the end, whoever still holds it.
    /// </summary>
    private void Keep(string key, SessionStore store)
    {
        foreach ((string _, SessionStore other) in recent)
        {
            if (!ReferenceEquals(other, store))
            {
                other.ReleaseSegmentReaders();
            }
        }

        _ = recent.RemoveAll(entry => SameDirectory(entry.Path, key));
        recent.Insert(0, (key, store));
        if (recent.Count > capacity)
        {
            recent.RemoveAt(recent.Count - 1);
        }
    }

    private static bool SameDirectory(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
