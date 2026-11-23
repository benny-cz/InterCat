using System.Security.Cryptography;

namespace InterCat.Remote;

/// <summary>A controller's key the agent admitted, and what the token it enrolled with granted.</summary>
public sealed record EnrolledController(RemotePin Pin, RemoteGrants Grants, DateTimeOffset EnrolledUtc);

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
/// which no token enrolls again. Safe to use from several connections at once.
/// </summary>
public sealed class AgentEnrollments
{
    /// <summary>A token lives ten minutes unless its administrator says otherwise.</summary>
    public static readonly TimeSpan DefaultTokenLifetime = TimeSpan.FromMinutes(10);

    /// <summary>And never more than an hour: a token is for one enrollment soon, not a standing credential.</summary>
    public static readonly TimeSpan MaximumTokenLifetime = TimeSpan.FromHours(1);

    private readonly Lock gate = new();
    private readonly List<PendingToken> pending = [];
    private readonly Dictionary<RemotePin, EnrolledController> enrolled = [];
    private readonly HashSet<RemotePin> revoked = [];
    private readonly TimeProvider time;

    public AgentEnrollments(TimeProvider? time = null) => this.time = time ?? TimeProvider.System;

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
        lock (gate)
        {
            DateTimeOffset now = time.GetUtcNow();
            pending.RemoveAll(entry => entry.ExpiresUtc <= now);
            pending.Add(new(EnrollmentToken.HashOf(token.Secret), grants, now + lasts));
        }

        return token;
    }

    /// <summary>
    /// Admits the controller known by <paramref name="controller"/> where <paramref name="secret"/> is an unexpired token's,
    /// consuming the token; a controller already enrolled holds the new token's grants. A revoked key is refused whatever
    /// it presents, and so is a secret no unexpired token has: the refusal does not say which.
    /// </summary>
    public EnrolledController? Admit(ReadOnlySpan<byte> secret, RemotePin controller)
    {
        if (controller.IsEmpty || secret.Length != EnrollmentToken.SecretLength)
        {
            return null;
        }

        byte[] hash = EnrollmentToken.HashOf(secret);
        lock (gate)
        {
            DateTimeOffset now = time.GetUtcNow();
            pending.RemoveAll(entry => entry.ExpiresUtc <= now);

            // Every pending hash is compared in full, so how long the search takes does not say which matched or how far.
            int match = -1;
            for (int at = 0; at < pending.Count; at++)
            {
                if (CryptographicOperations.FixedTimeEquals(pending[at].SecretHash, hash))
                {
                    match = at;
                }
            }

            if (match < 0 || revoked.Contains(controller))
            {
                return null;
            }

            PendingToken token = pending[match];
            pending.RemoveAt(match);
            var admitted = new EnrolledController(controller, token.Grants, now);
            enrolled[controller] = admitted;
            return admitted;
        }
    }

    /// <summary>The controller's enrollment, or null where its key is not enrolled.</summary>
    public EnrolledController? Find(RemotePin controller)
    {
        lock (gate)
        {
            return enrolled.GetValueOrDefault(controller);
        }
    }

    /// <summary>Whether the key was revoked.</summary>
    public bool IsRevoked(RemotePin controller)
    {
        lock (gate)
        {
            return revoked.Contains(controller);
        }
    }

    /// <summary>
    /// Revokes the controller's key: its enrollment ends, and no token enrolls it again - a controller whose key was revoked
    /// enrolls with a new key. False where the key was neither enrolled nor revoked before.
    /// </summary>
    public bool Revoke(RemotePin controller)
    {
        lock (gate)
        {
            bool known = enrolled.Remove(controller) | revoked.Contains(controller);
            revoked.Add(controller);
            return known;
        }
    }

    /// <summary>The enrolled controllers, in the order of their pins.</summary>
    public IReadOnlyList<EnrolledController> Controllers
    {
        get
        {
            lock (gate)
            {
                return [.. enrolled.Values.OrderBy(entry => entry.Pin.Hex, StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>How many issued tokens are still unconsumed and unexpired.</summary>
    public int PendingTokens
    {
        get
        {
            lock (gate)
            {
                DateTimeOffset now = time.GetUtcNow();
                return pending.Count(entry => entry.ExpiresUtc > now);
            }
        }
    }

    private sealed record PendingToken(byte[] SecretHash, RemoteGrants Grants, DateTimeOffset ExpiresUtc);
}
