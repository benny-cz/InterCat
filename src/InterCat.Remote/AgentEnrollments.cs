using System.Security.Cryptography;

namespace InterCat.Remote;

/// <summary>
/// A controller's key the agent admitted, what the token it enrolled with granted, where it replaced its controller's
/// earlier key in a rotation that key, which the agent revoked as it admitted this one - and the agent's own next key it
/// told this controller while its rotation was under way (`remote-agent-v1` §4.9).
/// </summary>
public sealed record EnrolledController(RemotePin Pin, RemoteGrants Grants, DateTimeOffset EnrolledUtc, RemotePin? RotatedFrom = null,
    RemotePin? ToldNext = null);

/// <summary>
/// The agent's own policy over every grant (`remote-agent-v1` §5): what it allows at all, whatever a token says. Contents
/// are allowed only where the agent's administrator says so, and optional collectors never by a version 1 agent.
/// </summary>
public sealed record RemoteAgentPolicy(RemoteGrants Allowed)
{
    /// <summary>Metadata capture and download; contents only by the administrator's own choice.</summary>
    public static RemoteAgentPolicy Default { get; } = new(RemoteGrants.MetadataCapture | RemoteGrants.Download);

    /// <summary>What a controller holding <paramref name="granted"/> may do here.</summary>
    public RemoteGrants Effective(RemoteGrants granted) => granted & Allowed & ~RemoteGrants.Collectors;
}

/// <summary>
/// The agent's enrollments (`remote-agent-v1` §2): the tokens its administrator issued and none consumed, each kept as its
/// secret's hash with the grants it carries and when it lapses; the controllers' keys it admitted; and the keys revoked,
/// which no token enrolls again. Each operation is one step of its store, so several connections - and an administrator's
/// command in another process, where the store is the agent's folder - may use them at once.
/// </summary>
public sealed class AgentEnrollments
{
    /// <summary>A token lives ten minutes unless its administrator says otherwise.</summary>
    public static readonly TimeSpan DefaultTokenLifetime = TimeSpan.FromMinutes(10);

    /// <summary>And never more than an hour: a token is for one enrollment soon, not a standing credential.</summary>
    public static readonly TimeSpan MaximumTokenLifetime = TimeSpan.FromHours(1);

    private readonly IEnrollmentStore store;
    private readonly TimeProvider time;

    public AgentEnrollments(TimeProvider? time = null)
        : this(new MemoryEnrollmentStore(), time)
    {
    }

    public AgentEnrollments(IEnrollmentStore store, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// A token enrolling one controller with <paramref name="grants"/> within <paramref name="lifetime"/>, for the agent known
    /// by <paramref name="agent"/>. The token's text is shown once to the administrator; the agent keeps its secret's hash.
    /// </summary>
    public EnrollmentToken Issue(RemotePin agent, RemoteGrants grants, TimeSpan? lifetime = null)
    {
        TimeSpan lasts = lifetime ?? DefaultTokenLifetime;
        if (lasts <= TimeSpan.Zero || lasts > MaximumTokenLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), $"A token lasts more than nothing and at most {MaximumTokenLifetime.TotalMinutes:N0} minutes.");
        }

        if (grants == RemoteGrants.None || (grants & ~RemoteGrantsExtensions.Defined) != 0)
        {
            throw new ArgumentException("A token grants at least one thing version 1 defines.", nameof(grants));
        }

        EnrollmentToken token = EnrollmentToken.Create(agent);
        return store.Change(state =>
        {
            DateTimeOffset now = time.GetUtcNow();
            PendingToken[] pending = [.. Unexpired(state, now), new(EnrollmentToken.HashOf(token.Secret), grants, now + lasts)];
            return (state with { Pending = pending }, token);
        });
    }

    /// <summary>A key's rotation lives ten minutes: long enough for its controller to enroll its new key, and no longer.</summary>
    public static readonly TimeSpan RotationLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A secret by which <paramref name="controller"/>'s next key enrolls in its place (`remote-agent-v1` §2): the enrolled
    /// key's own grants, lapsing within ten minutes, consumed once. The key that asks must be enrolled and not revoked: null
    /// where it is not. The secret is returned to the key that asked, over its own connection; the agent keeps its hash.
    /// </summary>
    public (byte[] Secret, DateTimeOffset ExpiresUtc)? IssueRotation(RemotePin controller) => store.Change(state =>
    {
        DateTimeOffset now = time.GetUtcNow();
        if (state.Revoked.Contains(controller) || state.Enrolled.FirstOrDefault(entry => entry.Pin == controller) is not { } enrolled)
        {
            return (state, ((byte[], DateTimeOffset)?)null);
        }

        byte[] secret = RandomNumberGenerator.GetBytes(EnrollmentToken.SecretLength);
        DateTimeOffset expires = now + RotationLifetime;
        PendingToken[] pending = [.. Unexpired(state, now), new(EnrollmentToken.HashOf(secret), enrolled.Grants, expires, controller)];
        return (state with { Pending = pending }, (secret, expires));
    });

    /// <summary>
    /// Admits the controller known by <paramref name="controller"/> where <paramref name="secret"/> is an unexpired token's,
    /// consuming the token; a controller already enrolled holds the new token's grants. A revoked key is refused whatever
    /// it presents, and so is a secret no unexpired token has: the refusal does not say which. A rotation's secret admits
    /// another key than the one it rotates, and only while that one is not revoked; admitting it revokes that one.
    /// </summary>
    public EnrolledController? Admit(ReadOnlySpan<byte> secret, RemotePin controller)
    {
        if (controller.IsEmpty || secret.Length != EnrollmentToken.SecretLength)
        {
            return null;
        }

        byte[] hash = EnrollmentToken.HashOf(secret);
        return store.Change(state =>
        {
            DateTimeOffset now = time.GetUtcNow();
            List<PendingToken> pending = [.. Unexpired(state, now)];

            // Every pending hash is compared in full, so how long the search takes does not say which matched or how far.
            int match = -1;
            for (int at = 0; at < pending.Count; at++)
            {
                if (CryptographicOperations.FixedTimeEquals(pending[at].SecretHash, hash))
                {
                    match = at;
                }
            }

            if (match < 0 || state.Revoked.Contains(controller)
                || pending[match].RotatesFrom is { } rotated && (rotated == controller || state.Revoked.Contains(rotated)))
            {
                return (state with { Pending = pending }, (EnrolledController?)null);
            }

            PendingToken token = pending[match];
            pending.RemoveAt(match);
            var admitted = new EnrolledController(controller, token.Grants, now, token.RotatesFrom);
            EnrolledController[] enrolled = [.. state.Enrolled.Where(entry => entry.Pin != controller && entry.Pin != token.RotatesFrom), admitted];
            RemotePin[] revoked = token.RotatesFrom is { } replaced ? [.. state.Revoked, replaced] : [.. state.Revoked];
            return (state with { Pending = pending, Enrolled = enrolled, Revoked = revoked }, admitted);
        });
    }

    /// <summary>
    /// Keeps that <paramref name="controller"/> was told the agent's next key, <paramref name="next"/>: its administrator
    /// sees which controllers will follow the agent to it. False where the key is not enrolled, or was told it already.
    /// </summary>
    public bool Told(RemotePin controller, RemotePin next) => store.Change(state =>
    {
        if (state.Enrolled.FirstOrDefault(entry => entry.Pin == controller) is not { } enrolled || enrolled.ToldNext == next)
        {
            return (state, false);
        }

        return (state with { Enrolled = [.. state.Enrolled.Select(entry => entry.Pin == controller ? entry with { ToldNext = next } : entry)] }, true);
    });

    /// <summary>The controller's enrollment, or null where its key is not enrolled.</summary>
    public EnrolledController? Find(RemotePin controller) =>
        store.Read().Enrolled.FirstOrDefault(entry => entry.Pin == controller);

    /// <summary>Whether the key was revoked.</summary>
    public bool IsRevoked(RemotePin controller) => store.Read().Revoked.Contains(controller);

    /// <summary>
    /// Revokes the controller's key: its enrollment ends, and no token enrolls it again - a controller whose key was revoked
    /// enrolls with a new key. False where the key was neither enrolled nor revoked before.
    /// </summary>
    public bool Revoke(RemotePin controller) => store.Change(state =>
    {
        bool known = state.Enrolled.Any(entry => entry.Pin == controller) || state.Revoked.Contains(controller);
        RemotePin[] revoked = state.Revoked.Contains(controller) ? [.. state.Revoked] : [.. state.Revoked, controller];
        return (state with { Enrolled = [.. state.Enrolled.Where(entry => entry.Pin != controller)], Revoked = revoked }, known);
    });

    /// <summary>The enrolled controllers, in the order of their pins.</summary>
    public IReadOnlyList<EnrolledController> Controllers =>
        [.. store.Read().Enrolled.OrderBy(entry => entry.Pin.Hex, StringComparer.Ordinal)];

    /// <summary>The revoked keys, in the order of their pins.</summary>
    public IReadOnlyList<RemotePin> Revoked => [.. store.Read().Revoked.OrderBy(pin => pin.Hex, StringComparer.Ordinal)];

    /// <summary>How many issued tokens are still unconsumed and unexpired.</summary>
    public int PendingTokens => Unexpired(store.Read(), time.GetUtcNow()).Count();

    private static IEnumerable<PendingToken> Unexpired(EnrollmentState state, DateTimeOffset now) =>
        state.Pending.Where(entry => entry.ExpiresUtc > now);
}
