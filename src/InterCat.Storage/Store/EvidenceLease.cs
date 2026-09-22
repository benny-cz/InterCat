using System.Globalization;

namespace InterCat.Storage;

/// <summary>
/// What a lease is for. The kinds differ in what they promise, not only in how long they last: an
/// interactive lease is cheap and short, and a pinned one declares the disk allowance §10.1 requires
/// before it can promise anything at all.
/// </summary>
public enum EvidenceLeaseKind
{
    /// <summary>A query or a view. Short, renewable, and never a reservation.</summary>
    Interactive = 1,

    /// <summary>
    /// A user pin or a long export. It declares a byte allowance up front, because §10.1 forbids promising
    /// an indefinite pin inside a circular quota without reserving the space for it.
    /// </summary>
    Pinned = 2,
}

/// <summary>
/// One reader's hold on a generation and every dependency it names. A lease is the unit §20.1's fifth step
/// describes: a reader acquires the generation <em>and its dependencies</em> together, so retention cannot
/// take a segment out from under a result that is still being read (I18, S6).
/// </summary>
/// <remarks>
/// A lease expires. An expired interactive lease is not revivable, for the same reason the broker's owner
/// lease is not: a hold that can come back from expiry is not a bound on anything. Releasing a lease is
/// explicit, and disposing it releases it.
/// </remarks>
public sealed class EvidenceLease : IDisposable
{
    private readonly Action<EvidenceLease> release;
    private int released;

    internal EvidenceLease(
        Guid id,
        EvidenceLeaseKind kind,
        long generation,
        IReadOnlyList<string> dependencies,
        string manifestName,
        long reservedBytes,
        DateTimeOffset acquiredUtc,
        DateTimeOffset expiresUtc,
        Action<EvidenceLease> release)
    {
        Id = id;
        Kind = kind;
        Generation = generation;
        Dependencies = dependencies;
        ManifestName = manifestName;
        ReservedBytes = reservedBytes;
        AcquiredUtc = acquiredUtc;
        ExpiresUtc = expiresUtc;
        this.release = release;
    }

    public Guid Id { get; }

    public EvidenceLeaseKind Kind { get; }

    /// <summary>The generation this lease holds. It never moves to a later one.</summary>
    public long Generation { get; }

    /// <summary>Every file the held generation names, including its manifest.</summary>
    public IReadOnlyList<string> Dependencies { get; }

    public string ManifestName { get; }

    /// <summary>The disk allowance a pinned lease reserved, or zero for an interactive one.</summary>
    public long ReservedBytes { get; }

    public DateTimeOffset AcquiredUtc { get; }

    public DateTimeOffset ExpiresUtc { get; }

    public bool IsReleased => Volatile.Read(ref released) != 0;

    public bool HasExpiredAt(DateTimeOffset now) => now >= ExpiresUtc;

    /// <summary>Whether this lease still holds a file, which is what retention has to ask before removing one.</summary>
    public bool Holds(string name, DateTimeOffset now) =>
        !IsReleased
        && !HasExpiredAt(now)
        && (name.Equals(ManifestName, StringComparison.OrdinalIgnoreCase)
            || Dependencies.Any(dependency => dependency.Equals(name, StringComparison.OrdinalIgnoreCase)));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref released, 1) == 0)
        {
            release(this);
        }
    }

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Kind} lease {Id:N} on generation {Generation}, {Dependencies.Count} dependencies, expires {ExpiresUtc:u}");
}

/// <summary>The declared bounds a lease is taken under. Every one of them is a refusal, not a truncation.</summary>
public sealed record EvidenceLeaseRequest
{
    public static EvidenceLeaseRequest Interactive { get; } = new();

    public EvidenceLeaseKind Kind { get; init; } = EvidenceLeaseKind.Interactive;

    /// <summary>How long the lease holds. §20.2's default interactive lease is short.</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The disk allowance a pinned lease reserves. §10.1 forbids an indefinite pin inside a circular quota
    /// without one, so a pinned lease that declares none is refused rather than quietly promising nothing.
    /// </summary>
    public long ReservedBytes { get; init; }

    public string? Validate() =>
        !Enum.IsDefined(Kind)
            ? "A lease declares a known kind."
            : Duration <= TimeSpan.Zero || Duration > TimeSpan.FromDays(7)
                ? "A lease lasts between one tick and seven days."
                : Kind == EvidenceLeaseKind.Pinned && ReservedBytes <= 0
                    ? "A pinned lease declares the disk allowance it reserves; §10.1 does not allow an "
                        + "indefinite pin without one."
                    : Kind == EvidenceLeaseKind.Interactive && ReservedBytes != 0
                        ? "An interactive lease reserves nothing. A reservation is what makes a lease pinned."
                        : null;
}

/// <summary>
/// What a retention action did. It is a published record rather than a return value only, because §20.2
/// requires the released extent to be visible and ADR-010 requires it for a journal.
/// </summary>
public sealed record RetentionOutcome(
    SessionManifestV1 Manifest,
    IReadOnlyList<string> ReleasedFiles,
    IReadOnlyList<string> RemovedFiles,
    IReadOnlyList<string> HeldByLease,
    long ReclaimedBytes)
{
    /// <summary>
    /// Files retention released from the generation but could not remove, because a live lease still holds
    /// them. They are no longer part of any generation and are removed by a later sweep (I18, S6).
    /// </summary>
    public bool AwaitingRelease => HeldByLease.Count > 0;
}
