using System.Diagnostics.CodeAnalysis;

namespace InterCat.CaptureBroker;

// Types both sides of protocol v1 name. They live with the wire codec so an ordinary-integrity client can
// speak the protocol without referencing the privileged runtime or the ETW adapter.

public enum BrokerOperationCode
{
    Started = 1,
    StartFailed = 2,
    PreparedTokenRejected = 3,
    PreparedPlanAlreadyUsed = 4,
    RequestConflict = 5,
    OperationPending = 6,
    CaptureUnavailable = 7,
    LeaseRenewed = 8,
    LeaseExpired = 9,
    Stopped = 10,
    AlreadyStopped = 11,
    StopPartial = 12,
    PersistenceFailure = 13,
    InvalidRequest = 14,
}

/// <summary>Stop progress is never collapsed into one optimistic success flag.</summary>
public sealed record BrokerStopMilestones(
    bool Requested,
    bool ProvidersStopped,
    bool CallbacksDrained,
    bool JournalFinalized,
    bool AnalysisFinalized)
{
    public static BrokerStopMilestones None { get; } = new(false, false, false, false, false);

    public bool FullyFinalized =>
        Requested && ProvidersStopped && CallbacksDrained && JournalFinalized && AnalysisFinalized;
}

public enum BrokerPrepareRefusalCode
{
    InvalidRuntimeIdentity = 1,
    PlanNotStartable = 2,
    UnsupportedBuild = 3,
    RuntimeBuildMismatch = 4,
    RuntimeArchitectureMismatch = 5,
    RuntimeAdapterMismatch = 6,
    EffectivePlanMismatch = 7,
    UnsupportedAdmissionPolicy = 8,
    UnsupportedContentCapture = 9,
    UnsupportedOriginalEvidence = 10,
    InvalidCapturePlan = 11,
    InvalidOperationalLimits = 12,
}

/// <summary>The only time the raw prepared token is returned. Logs and durable records use its hash.</summary>
public sealed record PreparedPlanGrant(
    string Token,
    string PlanDigest,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);

/// <summary>The shape of a prepared-plan token on the wire: 32 random bytes as unpadded base64url.</summary>
public static class PreparedPlanTokenFormat
{
    public const int Length = 43;

    public static bool IsWellFormed([NotNullWhen(true)] string? token) =>
        token is { Length: Length }
        && token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}

/// <summary>An identity read from a Windows access token: the pipe client's on the broker side, the caller's own on the client side.</summary>
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
