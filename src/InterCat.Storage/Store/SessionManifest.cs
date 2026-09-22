using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InterCat.Storage;

/// <summary>What a manifest dependency is. An unknown kind is refused, never published or read past.</summary>
public enum StoreDependencyKind
{
    /// <summary>An admitted journal, the evidence derived files were built from.</summary>
    Journal = 1,

    /// <summary>A published immutable segment.</summary>
    Segment = 2,

    /// <summary>A dictionary a segment's columns reference.</summary>
    Dictionary = 3,

    /// <summary>An index over published segments.</summary>
    Index = 4,

    /// <summary>The compiled descriptor interpretation retained with admitted evidence for re-derivation.</summary>
    DerivationPlan = 5,
}

/// <summary>
/// One immutable file a generation depends on. Its length and digest are recorded in the manifest, so
/// recovery can tell a dependency that survived from one that was renamed into place and then lost its
/// contents: a filesystem rename is not a power-failure guarantee (§20.1).
/// </summary>
public sealed record StoreDependency(
    string Name,
    StoreDependencyKind Kind,
    long LengthBytes,
    string Digest)
{
    internal string CanonicalForm =>
        string.Create(CultureInfo.InvariantCulture, $"{Name}|{(int)Kind}|{LengthBytes}|{Digest}");
}

/// <summary>
/// How far the admitted journal is durable at the moment a generation was published. §20.1 makes the
/// committed boundary the first step of the commit sequence, so a generation states exactly which
/// admitted evidence it was derived from rather than implying the whole file.
/// </summary>
public sealed record CommittedBoundary(
    string JournalName,
    long CommittedBytes,
    long CommittedRecords,
    string Digest)
{
    public static CommittedBoundary None { get; } = new(string.Empty, 0, 0, string.Empty);

    public bool IsDeclared => JournalName.Length > 0;

    internal string CanonicalForm => string.Create(
        CultureInfo.InvariantCulture,
        $"{JournalName}|{CommittedBytes}|{CommittedRecords}|{Digest}");
}

/// <summary>What kind of evidence a retention action released.</summary>
public enum RetentionExtentKind
{
    /// <summary>Derived files — segments, dictionaries or indices. They are rebuildable from the journal.</summary>
    DerivedFiles = 1,

    /// <summary>
    /// A prefix of the admitted journal. ADR-010 makes this the explicit action it is: releasing it gives up
    /// the ability to re-derive those records after a normalizer revision.
    /// </summary>
    JournalPrefix = 2,
}

/// <summary>
/// What one retention action released, published in the generation that no longer names it. §20.2 requires
/// the released extent to be visible, and ADR-010 requires a journal release to state exactly what it gave
/// up: a retention that cannot be read afterwards is indistinguishable from data loss.
/// </summary>
/// <remarks>
/// A checkpoint records what was released and nothing about what the released interval contained. It does
/// not prove activity in the removed extent and does not turn a missing start into an observed start
/// (§20.2); what still-live entity and pending-operation state a rolling eviction must carry across the
/// boundary needs the entity and operation revisions IC-015 owns, and is deliberately absent here rather
/// than present and empty.
/// </remarks>
public sealed record RetentionRecord(
    RetentionExtentKind Kind,
    DateTimeOffset ReleasedUtc,
    string Reason,
    IReadOnlyList<string> ReleasedFiles,
    long ReleasedBytes,
    long ReleasedRecords,
    string SourceDigest)
{
    public static RetentionRecord ForDerivedFiles(
        DateTimeOffset releasedUtc,
        string reason,
        IReadOnlyList<string> releasedFiles,
        long releasedBytes)
    {
        ArgumentNullException.ThrowIfNull(releasedFiles);
        return new(
            RetentionExtentKind.DerivedFiles,
            releasedUtc,
            reason ?? string.Empty,
            releasedFiles,
            releasedBytes,
            0,
            string.Empty);
    }

    /// <summary>Returns the reason this record is not readable, or null when it is.</summary>
    public string? Validate()
    {
        if (!Enum.IsDefined(Kind))
        {
            return $"A retention record declares kind {(int)Kind}, which this reader does not implement.";
        }

        if (ReleasedBytes < 0 || ReleasedRecords < 0)
        {
            return "A retention record releases a non-negative extent.";
        }

        if (ReleasedFiles.Count is 0 or > 65_536)
        {
            return "A retention record names between one and 65,536 released files.";
        }

        foreach (string file in ReleasedFiles)
        {
            if (OwnedFileName.Validate(file) is { } problem)
            {
                return $"Released file '{file}' is not an owned file name: {problem}";
            }
        }

        return Reason.Length switch
        {
            0 => "A retention record states why the extent was released. A release with no stated reason is "
                + "indistinguishable from data loss.",
            > 512 => "A retention reason is at most 512 characters.",
            _ => Kind == RetentionExtentKind.JournalPrefix && !SessionManifestV1.IsDigest(SourceDigest)
                ? "A journal release names the digest of the journal it started from. The released bytes are "
                    + "gone, so the file that held them is what stays identifiable afterwards."
                : Kind == RetentionExtentKind.DerivedFiles && SourceDigest.Length > 0
                    ? "A derived-file release names no source digest; the files it released are listed instead."
                    : null,
        };
    }

    internal string CanonicalForm => string.Create(
        CultureInfo.InvariantCulture,
        $"{(int)Kind}|{ReleasedUtc.ToUniversalTime().UtcTicks}|{Reason}|{string.Join(',', ReleasedFiles)}"
        + $"|{ReleasedBytes}|{ReleasedRecords}|{SourceDigest}");
}

/// <summary>
/// One immutable generation of a session. A manifest carries its own digest and its complete dependency
/// list, which is what lets a torn publication be detected and rolled back instead of trusted.
/// </summary>
public sealed record SessionManifestV1
{
    public const int CurrentFormatVersion = 1;

    private const string Domain = "InterCat.Store.SessionManifest.v1";

    /// <summary>The on-disk form of a manifest and a pointer, so a tool can read one without this class.</summary>
    public static JsonSerializerOptions Json { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        MaxDepth = 32,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    public required int FormatVersion { get; init; }

    public required long Generation { get; init; }

    public required Guid SessionId { get; init; }

    public required DateTimeOffset CommittedUtc { get; init; }

    /// <summary>The capture or import identity this session derives from, as its own contract renders it.</summary>
    public required string SourceIdentity { get; init; }

    public required long? PreviousGeneration { get; init; }

    public required CommittedBoundary Boundary { get; init; }

    public required IReadOnlyList<StoreDependency> Dependencies { get; init; }

    /// <summary>
    /// What this generation released, when it is a retention generation. Absent on an ordinary one, and
    /// absent from the digest when it is absent, so a manifest written before retention existed still
    /// verifies unchanged.
    /// </summary>
    public RetentionRecord? Retention { get; init; }

    /// <summary>`sha256:` and 64 lowercase hexadecimal characters over everything above.</summary>
    public required string Digest { get; init; }

    public static SessionManifestV1 Create(
        long generation,
        Guid sessionId,
        DateTimeOffset committedUtc,
        string sourceIdentity,
        long? previousGeneration,
        CommittedBoundary boundary,
        IReadOnlyList<StoreDependency> dependencies,
        RetentionRecord? retention = null)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(boundary);
        var manifest = new SessionManifestV1
        {
            FormatVersion = CurrentFormatVersion,
            Generation = generation,
            SessionId = sessionId,
            CommittedUtc = committedUtc,
            SourceIdentity = sourceIdentity ?? string.Empty,
            PreviousGeneration = previousGeneration,
            Boundary = boundary,
            Dependencies = dependencies,
            Retention = retention,
            Digest = string.Empty,
        };
        string? problem = manifest.Validate();
        return problem is not null
            ? throw new ArgumentException(problem)
            : manifest with { Digest = manifest.ComputeDigest() };
    }

    /// <summary>The manifest file name for a generation. Fixed width, so the names sort as the numbers do.</summary>
    public static string FileNameFor(long generation) =>
        generation is < 1 or > 9_999_999_999
            ? throw new ArgumentOutOfRangeException(
                nameof(generation),
                generation,
                "A published generation is between 1 and 9,999,999,999.")
            : string.Create(CultureInfo.InvariantCulture, $"manifest-{generation:D10}.json");

    /// <summary>Returns the reason this manifest is not readable, or null when it is.</summary>
    public string? Validate()
    {
        if (FormatVersion != CurrentFormatVersion)
        {
            return $"This manifest declares format {FormatVersion}; this reader implements "
                + $"{CurrentFormatVersion}. An unknown format is refused, never read at a guessed layout.";
        }

        if (Generation is < 1 or > 9_999_999_999)
        {
            return "A published generation is between 1 and 9,999,999,999.";
        }

        if (PreviousGeneration is { } previous && (previous < 1 || previous >= Generation))
        {
            return $"Generation {Generation} names {previous} as its previous generation, which is not "
                + "an earlier published generation.";
        }

        if (SessionId == Guid.Empty)
        {
            return "A manifest names the session it belongs to.";
        }

        if (Dependencies.Count > 65_536)
        {
            return $"A generation carries at most 65,536 dependencies; this one names {Dependencies.Count}.";
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (StoreDependency dependency in Dependencies)
        {
            string? name = OwnedFileName.Validate(dependency.Name);
            if (name is not null)
            {
                return $"Dependency '{dependency.Name}' is not an owned file name: {name}";
            }

            if (!seen.Add(dependency.Name))
            {
                return $"Dependency '{dependency.Name}' appears twice in one generation.";
            }

            if (!Enum.IsDefined(dependency.Kind))
            {
                return $"Dependency '{dependency.Name}' has kind {(int)dependency.Kind}, which this "
                    + "reader does not implement.";
            }

            if (dependency.LengthBytes < 0 || !IsDigest(dependency.Digest))
            {
                return $"Dependency '{dependency.Name}' has no readable length or digest.";
            }
        }

        if (Boundary.IsDeclared
            && (OwnedFileName.Validate(Boundary.JournalName) is not null
                || Boundary.CommittedBytes < 0
                || Boundary.CommittedRecords < 0
                || !IsDigest(Boundary.Digest)))
        {
            return "The committed boundary names a journal, a non-negative extent and a digest, or none.";
        }

        return Retention?.Validate();
    }

    /// <summary>Returns the reason this manifest's own bytes do not match its digest, or null.</summary>
    public string? VerifyDigest()
    {
        string expected = ComputeDigest();
        return string.Equals(expected, Digest, StringComparison.Ordinal)
            ? null
            : $"This manifest records digest {Digest} but its contents compute {expected}.";
    }

    internal static bool IsDigest(string value) =>
        value.Length == 71
        && value.StartsWith("sha256:", StringComparison.Ordinal)
        && value.AsSpan(7).ToString().All(Uri.IsHexDigit);

    private string ComputeDigest()
    {
        var canonical = new StringBuilder(Domain);
        canonical.Append(CultureInfo.InvariantCulture, $"\n{FormatVersion}\n{Generation}\n{SessionId:N}\n");
        canonical.Append(CultureInfo.InvariantCulture, $"{CommittedUtc.ToUniversalTime().UtcTicks}\n");
        canonical.Append(SourceIdentity).Append('\n');
        canonical.Append(CultureInfo.InvariantCulture, $"{PreviousGeneration?.ToString(CultureInfo.InvariantCulture) ?? "-"}\n");
        canonical.Append(Boundary.CanonicalForm).Append('\n');

        // Dependencies are hashed in their recorded order, because a generation's dependency list is
        // itself part of what was published: reordering it would be a different manifest.
        foreach (StoreDependency dependency in Dependencies)
        {
            canonical.Append(dependency.CanonicalForm).Append('\n');
        }

        // A retention record is appended only when there is one, so a generation published before retention
        // existed hashes exactly as it did. An absent optional field that changed every digest would make
        // every already-published manifest unverifiable.
        if (Retention is { } retention)
        {
            canonical.Append(retention.CanonicalForm).Append('\n');
        }

        return string.Concat(
            "sha256:",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))));
    }
}

/// <summary>
/// The current-generation pointer. It is the one mutable file in a session, replaced in a single
/// directory operation, and the previous one is retained beside it as last-known-good.
/// </summary>
public sealed record SessionPointerV1
{
    public const string FileName = "current-generation.json";

    public const string PreviousFileName = "previous-generation.json";

    public required int FormatVersion { get; init; }

    public required long Generation { get; init; }

    public required string ManifestName { get; init; }

    public required string ManifestDigest { get; init; }

    public static SessionPointerV1 For(SessionManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new()
        {
            FormatVersion = SessionManifestV1.CurrentFormatVersion,
            Generation = manifest.Generation,
            ManifestName = SessionManifestV1.FileNameFor(manifest.Generation),
            ManifestDigest = manifest.Digest,
        };
    }

    public string? Validate() =>
        FormatVersion != SessionManifestV1.CurrentFormatVersion
            ? $"This pointer declares format {FormatVersion}; this reader implements "
                + $"{SessionManifestV1.CurrentFormatVersion}."
            : Generation < 1
                ? "A pointer names a published generation."
                : OwnedFileName.Validate(ManifestName) is { } problem
                    ? $"The pointer's manifest name is not an owned file name: {problem}"
                    : !SessionManifestV1.IsDigest(ManifestDigest)
                        ? "The pointer carries no readable manifest digest."
                        : null;
}
