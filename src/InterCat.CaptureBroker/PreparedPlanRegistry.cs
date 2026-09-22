using System.Security.Cryptography;
using System.Text;
using InterCat.Domain;

namespace InterCat.CaptureBroker;

/// <summary>OS-authenticated identity supplied by the future local-pipe authentication layer.</summary>
public sealed record BrokerClientIdentity(
    string UserSid,
    ulong LogonSessionId,
    int IntegrityLevel,
    bool IsElevated)
{
    public BrokerOwnerIdentity Owner => new(UserSid.ToUpperInvariant(), LogonSessionId);

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(UserSid)
            || UserSid.Length > 184
            || UserSid.Any(char.IsControl)
            || !UserSid.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
        {
            return "The authenticated user SID is invalid.";
        }

        if (LogonSessionId == 0)
        {
            return "The authenticated logon-session identifier is invalid.";
        }

        if (IntegrityLevel <= 0)
        {
            return "The authenticated integrity level is invalid.";
        }

        return null;
    }
}

/// <summary>Stable owner binding. A client PID is intentionally absent because it is not authentication.</summary>
public readonly record struct BrokerOwnerIdentity(string UserSid, ulong LogonSessionId)
{
    public bool Matches(BrokerOwnerIdentity other) =>
        LogonSessionId == other.LogonSessionId
        && string.Equals(UserSid, other.UserSid, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The only time the raw prepared token is returned. Logs and durable records use its hash.</summary>
public sealed record PreparedPlanGrant(
    string Token,
    string PlanDigest,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);

internal enum PreparedPlanResolutionCode
{
    Available = 1,
    Rejected = 2,
    AlreadyUsed = 3,
}

internal sealed record PreparedPlanResolution(
    PreparedPlanResolutionCode Code,
    string TokenFingerprint,
    PreparedCapturePlan? Plan,
    CaptureId? ExistingCaptureId);

/// <summary>
/// Short-lived in-memory prepared-token registry. Tokens are 256-bit random values and only their
/// SHA-256 fingerprints are retained. Restart invalidates every grant by design.
/// </summary>
public sealed class PreparedPlanRegistry
{
    public static readonly TimeSpan MinimumLifetime = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Lock gate = new();
    private readonly TimeProvider clock;
    private readonly TimeSpan lifetime;

    public PreparedPlanRegistry(TimeProvider? clock = null, TimeSpan? lifetime = null)
    {
        this.clock = clock ?? TimeProvider.System;
        this.lifetime = lifetime ?? DefaultLifetime;
        if (this.lifetime < MinimumLifetime || this.lifetime > MaximumLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lifetime),
                $"Prepared-plan lifetime must be between {MinimumLifetime} and {MaximumLifetime}.");
        }
    }

    public PreparedPlanGrant Issue(PreparedCapturePlan plan, BrokerClientIdentity client)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(client);
        string? identityProblem = client.Validate();
        if (identityProblem is not null)
        {
            throw new ArgumentException(identityProblem, nameof(client));
        }

        byte[] secret = RandomNumberGenerator.GetBytes(32);
        string token = Convert.ToBase64String(secret)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        CryptographicOperations.ZeroMemory(secret);
        string fingerprint = Fingerprint(token);
        DateTimeOffset issuedAtUtc = clock.GetUtcNow();
        DateTimeOffset expiresAtUtc = issuedAtUtc.Add(lifetime);
        lock (gate)
        {
            RemoveExpired(issuedAtUtc);
            entries.Add(fingerprint, new(plan, client.Owner, issuedAtUtc, expiresAtUtc, null));
        }

        return new(token, plan.Digest, issuedAtUtc, expiresAtUtc);
    }

    internal PreparedPlanResolution Resolve(string token, BrokerOwnerIdentity owner)
    {
        if (!TryFingerprint(token, out string fingerprint))
        {
            return new(PreparedPlanResolutionCode.Rejected, string.Empty, null, null);
        }

        DateTimeOffset now = clock.GetUtcNow();
        lock (gate)
        {
            if (!entries.TryGetValue(fingerprint, out Entry? entry)
                || entry.ExpiresAtUtc <= now
                || !entry.Owner.Matches(owner))
            {
                if (entry?.ExpiresAtUtc <= now)
                {
                    entries.Remove(fingerprint);
                }

                return new(PreparedPlanResolutionCode.Rejected, fingerprint, null, null);
            }

            return entry.CaptureId is null
                ? new(PreparedPlanResolutionCode.Available, fingerprint, entry.Plan, null)
                : new(PreparedPlanResolutionCode.AlreadyUsed, fingerprint, null, entry.CaptureId);
        }
    }

    internal bool MarkUsed(string fingerprint, BrokerOwnerIdentity owner, CaptureId captureId)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(fingerprint, out Entry? entry)
                || !entry.Owner.Matches(owner)
                || entry.CaptureId is not null)
            {
                return false;
            }

            entries[fingerprint] = entry with { CaptureId = captureId };
            return true;
        }
    }

    internal static string Fingerprint(string token)
    {
        byte[] digest = SHA256.HashData(Encoding.ASCII.GetBytes(token));
        return "sha256:" + Convert.ToHexStringLower(digest);
    }

    internal static bool TryFingerprint(string? token, out string fingerprint)
    {
        fingerprint = string.Empty;
        if (token is null || token.Length != 43 || token.Any(character =>
            !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
        {
            return false;
        }

        fingerprint = Fingerprint(token);
        return true;
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (string fingerprint in entries
            .Where(item => item.Value.ExpiresAtUtc <= now)
            .Select(item => item.Key)
            .ToArray())
        {
            entries.Remove(fingerprint);
        }
    }

    private sealed record Entry(
        PreparedCapturePlan Plan,
        BrokerOwnerIdentity Owner,
        DateTimeOffset IssuedAtUtc,
        DateTimeOffset ExpiresAtUtc,
        CaptureId? CaptureId);
}
