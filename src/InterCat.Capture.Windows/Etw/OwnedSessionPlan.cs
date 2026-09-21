using System.Globalization;
using InterCat.Domain;

namespace InterCat.Capture.Windows;

/// <summary>
/// The name and ownership token of one capture attempt. The name is unique per attempt and the token is
/// the only proof of ownership: a similar name never authorises stopping a session (section 9.2, P14, P19).
/// </summary>
public sealed record CaptureSessionIdentity
{
    public const string NamePrefix = "InterCat";

    private CaptureSessionIdentity(
        string sessionName,
        Guid ownershipToken,
        int ownerProcessId,
        DateTimeOffset createdAtUtc,
        CaptureId captureId)
    {
        SessionName = sessionName;
        OwnershipToken = ownershipToken;
        OwnerProcessId = ownerProcessId;
        CreatedAtUtc = createdAtUtc;
        CaptureId = captureId;
    }

    public string SessionName { get; }
    public Guid OwnershipToken { get; }
    public int OwnerProcessId { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public CaptureId CaptureId { get; }

    public static CaptureSessionIdentity Create(string purpose, int ownerProcessId, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);

        Guid token = Guid.NewGuid();
        string suffix = token.ToString("N", CultureInfo.InvariantCulture)[..8];
        string name = string.Create(
            CultureInfo.InvariantCulture,
            $"{NamePrefix}-{Sanitize(purpose)}-{ownerProcessId}-{suffix}");
        return new(
            name,
            token,
            ownerProcessId,
            (clock ?? TimeProvider.System).GetUtcNow(),
            CaptureId.New());
    }

    /// <summary>
    /// True only for a session this identity created. Name similarity alone is never ownership, so an
    /// InterCat build never stops a session it did not start (P14).
    /// </summary>
    public bool Owns(string sessionName) => string.Equals(SessionName, sessionName, StringComparison.Ordinal);

    private static string Sanitize(string purpose)
    {
        Span<char> buffer = stackalloc char[Math.Min(purpose.Length, 24)];
        int length = 0;
        foreach (char character in purpose)
        {
            if (length == buffer.Length)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(character))
            {
                buffer[length++] = character;
            }
        }

        return length == 0 ? "capture" : new string(buffer[..length]);
    }
}

/// <summary>One provider enablement exactly as the capture will request it (section 18.2).</summary>
public sealed record ProviderEnablementRequest
{
    public required string SourceId { get; init; }
    public required string ProviderName { get; init; }
    public required Guid ProviderGuid { get; init; }
    public required int Level { get; init; }
    public required ulong MatchAnyKeyword { get; init; }
    public ulong MatchAllKeyword { get; init; }
    public IReadOnlyList<int> EventIdsToEnable { get; init; } = [];
    public IReadOnlyList<int> EventIdsToDisable { get; init; } = [];

    /// <summary>Whether to ask the provider for its state rundown after delivery starts (section 18.5).</summary>
    public bool RequestCaptureState { get; init; }

    /// <summary>
    /// Whether to ask ETW for call-stack extended data on this provider's events. Extended-data items are
    /// opt-in per enablement, not a property of the event, so a capture that does not request them observes
    /// none; the cost per event is materially higher, which is why it is a recorded setting (section 18.2).
    /// </summary>
    public bool RequestCallStacks { get; init; }
}

/// <summary>The outcome of enabling one provider. A refusal is recorded, never retried silently.</summary>
public sealed record ProviderEnablementResult(
    string SourceId,
    bool Enabled,
    string? FailureReason);

/// <summary>Loss quantities as the source reports them. They are stored separately, never summed (section 9.3).</summary>
public sealed record SourceLossReading(long ProviderReportedEventLoss, long ConsumerReportedBufferLoss);

/// <summary>
/// An immutable capture plan. Every budget is bounded and recorded, so the effective settings stay part
/// of the evidence rather than a literal in code (section 1.4, R8).
/// </summary>
public sealed record OwnedSessionPlan
{
    public required CaptureSessionIdentity Identity { get; init; }
    public required IReadOnlyList<ProviderEnablementRequest> Providers { get; init; }
    public required IReadOnlyList<SourceAdmissionPlan> Sources { get; init; }
    public AdmissionMode Admission { get; init; } = AdmissionMode.MetadataOnly;

    /// <summary>ETW buffer size in kilobytes. Recorded in the manifest as a capture-affecting setting.</summary>
    public int BufferSizeKilobytes { get; init; } = 128;

    /// <summary>Bounded in-process queue between the callback and the decode stage (R8).</summary>
    public int QueueCapacityRecords { get; init; } = 65_536;

    /// <summary>Hard stop for an M0 experiment. A capture never runs unbounded by accident (section 9.2).</summary>
    public TimeSpan MaximumDuration { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Health sampling interval (section 18.5 tunable default).</summary>
    public TimeSpan HealthSamplingInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Grace period applied after the workload stops, before the session stops (section 18.5).</summary>
    public TimeSpan ReorderGrace { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Whether admission copies a bounded prefix of each record's extended-data items. It is off by
    /// default because copying costs callback time that a measurement must not pay unasked; the IC-009
    /// envelope candidate turns it on (section 18.1).
    /// </summary>
    public bool PreserveExtendedData { get; init; }
}
