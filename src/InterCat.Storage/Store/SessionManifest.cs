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

    /// <summary>
    /// What the capture's sources could observe and what they lost (`contracts/coverage-v2.md`). Evidence about the
    /// capture, kept with it: no journal holds these facts, so nothing can rebuild them.
    /// </summary>
    CoverageLedger = 6,

    /// <summary>
    /// Durable evidence that a live capture reached its final publication. A complete journal proves only one
    /// committed chunk; this companion distinguishes the capture's final chunk from an intermediate live chunk.
    /// </summary>
    CaptureFinalization = 7,

    /// <summary>
    /// The allowlist and provenance of a redacted session package (`contracts/redacted-session-v1.md`): what the package
    /// kept, pseudonymized, redacted and left out, and that its journal holds synthetic records. Never released.
    /// </summary>
    RedactionPolicy = 8,

    /// <summary>
    /// Restricted evidence beside the journal: the content a capture kept of one journal chunk's records
    /// (`contracts/content-v1.md`, ADR-036). Released only with that journal chunk, never read by anything that reads
    /// metadata, and never in a redacted package (I22).
    /// </summary>
    Content = 9,

    /// <summary>
    /// A live capture's source clock paired with the wall clock, and the boot it ran in (`contracts/clock-calibration-v1.md`):
    /// evidence about the capture that no journal holds, carried unchanged and never released. Never in a redacted package.
    /// </summary>
    ClockCalibration = 10,

    /// <summary>
    /// The processes that collected a live capture - its broker and the client that asked for it, or `icat record` - by
    /// PID and creation time (`contracts/collector-identities-v1.md`), so a reader can label their own activity in it
    /// (§19.5): evidence about the capture that no journal holds, carried unchanged and never released. Never in a
    /// redacted package.
    /// </summary>
    CollectorIdentities = 11,
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

    /// <summary>
    /// Why the file is not the one its generation records, in words a person reads: its SHA-256 differs. Each digest is
    /// given by its first twelve digits, which tell two apart; the whole of the recorded one is in the manifest.
    /// </summary>
    public string DigestMismatch(string digest, long? generation = null) =>
        Mismatch($"its SHA-256 begins {DigestStart(digest)}, not {DigestStart(Digest)}", generation);

    /// <summary>Why the file is not the one its generation records: its length differs.</summary>
    public string LengthMismatch(long length, long? generation = null) =>
        Mismatch(string.Create(CultureInfo.InvariantCulture, $"it is {length:N0} bytes, not {LengthBytes:N0}"), generation);

    private string Mismatch(string difference, long? generation) => string.Create(CultureInfo.InvariantCulture,
        $"'{Name}' does not match what {(generation is { } number ? $"generation {number}" : "its generation")} records ({difference})");

    private static string DigestStart(string digest) =>
        digest.StartsWith("sha256:", StringComparison.Ordinal) && digest.Length >= 19 ? digest[7..19] : digest;
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

    /// <summary>
    /// Every kept content chunk, released on its own (`contracts/content-v1.md` §2): the messages' bytes and each
    /// record's content facts go, and every journal, row and derived file stays. Nothing can rebuild them.
    /// </summary>
    Content = 3,

    /// <summary>
    /// A session's oldest interval (ADR-043): the oldest units of its admitted journal - a recording's chunks, or a single
    /// journal's first batches - whose records all read before a boundary, with their kept content and every row derived
    /// from them but the identity evidence later records rest on, and the derived files that held those rows. Nothing can
    /// rebuild them.
    /// </summary>
    Interval = 4,
}

/// <summary>
/// What an interval release states beyond the files and records it gave up (ADR-043): the instant from which every record
/// of the session is retained, how many rows went with the records it released, and how many of their rows it kept as the
/// identity evidence of the processes and connections later records belong to, so those keep the instances, holders and
/// pairings the whole capture gave them.
/// </summary>
/// <param name="BoundaryNanoseconds">
/// Session time: every record read at or after it is retained. Before it the session holds the kept rows and the records
/// delivered with later ones, so an interval before it is released, not empty.
/// </param>
public sealed record ReleasedInterval(long BoundaryNanoseconds, long ReleasedRows, long KeptRows)
{
    /// <summary>Returns the reason this statement is not readable, or null when it is.</summary>
    public string? Validate() =>
        ReleasedRows < 0 || KeptRows < 0
            ? "An interval release states how many rows it released and kept, neither negative."
            : null;

    internal string CanonicalForm => string.Create(
        CultureInfo.InvariantCulture,
        $"interval|{BoundaryNanoseconds}|{ReleasedRows}|{KeptRows}");
}

/// <summary>
/// What a recording's chunk releases gave up in all (ADR-048): how many of its oldest chunks, and how many records they held,
/// the release stating it and every chunk release before it gave up. Only the latest release of a kind is carried, so a
/// reader places the chunks a session still holds among the recording's by it - a follow does, whose evidence released
/// chunks it had mirrored - without the chunks that are gone.
/// </summary>
public sealed record ReleasedRecording(long Chunks, long Records)
{
    /// <summary>Returns the reason this statement is not readable, or null when it is.</summary>
    public string? Validate() =>
        Chunks < 1 || Records < 0
            ? "A chunk release states the recording's chunks and records it gave up in all: at least one chunk, and no "
                + "fewer than no records."
            : null;

    internal string CanonicalForm => string.Create(CultureInfo.InvariantCulture, $"recording|{Chunks}|{Records}");
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

    /// <summary>
    /// What an interval release states of the interval it released: present on that kind alone, and absent from the file
    /// and the digest otherwise, so every other record is the shape it always was.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReleasedInterval? Interval { get; init; }

    /// <summary>
    /// What a chunk release states of the recording's chunks it and every chunk release before it gave up: present on a
    /// release of a recording's oldest chunks whose earlier releases each stated it, and absent from the file and the
    /// digest otherwise, so every other record is the shape it always was.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReleasedRecording? Recording { get; init; }

    /// <summary>Returns the reason this record is not readable, or null when it is.</summary>
    public string? Validate()
    {
        if (!Enum.IsDefined(Kind))
        {
            return $"A retention record declares kind {(int)Kind}, which this reader does not implement.";
        }

        if (Recording is { } recording)
        {
            if (Kind != RetentionExtentKind.JournalPrefix)
            {
                return "Only a release of a recording's oldest chunks states the chunks the recording gave up in all.";
            }

            if (recording.Validate() is { } problem)
            {
                return problem;
            }

            if (recording.Records < ReleasedRecords)
            {
                return "A chunk release states fewer records given up in all than it gave up itself.";
            }
        }

        if ((Kind == RetentionExtentKind.Interval) != Interval is not null)
        {
            return Interval is null
                ? "An interval release states the boundary from which every record is retained, and the rows it released and kept."
                : "Only an interval release states an interval.";
        }

        if (Interval?.Validate() is { } interval)
        {
            return interval;
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
                : Kind == RetentionExtentKind.Content && !SessionManifestV1.IsDigest(SourceDigest)
                    ? "A content release names the digest of the content chunks it released, which are gone afterwards."
                : Kind == RetentionExtentKind.Interval && !SessionManifestV1.IsDigest(SourceDigest)
                    ? "An interval release names the digest of the files it released, which are gone afterwards."
                : Kind == RetentionExtentKind.DerivedFiles && SourceDigest.Length > 0
                    ? "A derived-file release names no source digest; the files it released are listed instead."
                    : null,
        };
    }

    internal string CanonicalForm => string.Create(
        CultureInfo.InvariantCulture,
        $"{(int)Kind}|{ReleasedUtc.ToUniversalTime().UtcTicks}|{Reason}|{string.Join(',', ReleasedFiles)}"
        + $"|{ReleasedBytes}|{ReleasedRecords}|{SourceDigest}")
        + (Interval is { } interval ? "|" + interval.CanonicalForm : string.Empty)
        + (Recording is { } recording ? "|" + recording.CanonicalForm : string.Empty);
}

/// <summary>
/// A retention record and the generation that published it. A later generation carries the latest release of each extent
/// nothing can rebuild - a journal prefix, kept content, an interval - so what was given up, when and why is still stated
/// once the generation that released it is superseded (store-v1 §8).
/// </summary>
public sealed record GenerationRelease(long Generation, RetentionRecord Record)
{
    internal string CanonicalForm =>
        string.Create(CultureInfo.InvariantCulture, $"{Generation}|{Record.CanonicalForm}");
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

    /// <summary>
    /// The latest release of a journal prefix, of kept content and of an interval that earlier generations published, each
    /// with the generation that published it: every later generation carries them, so a release is still stated once the
    /// generation that made it is superseded. Absent when there are none - from the file as well as the digest - so a
    /// manifest that carries none is the shape it always was, and verifies with the digest it always had.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<GenerationRelease>? EarlierReleases { get; init; }

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
        RetentionRecord? retention = null,
        IReadOnlyList<GenerationRelease>? earlierReleases = null)
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
            EarlierReleases = earlierReleases is { Count: > 0 } ? earlierReleases : null,
            Digest = string.Empty,
        };
        string? problem = manifest.Validate();
        return problem is not null
            ? throw new ArgumentException(problem)
            : manifest with { Digest = manifest.ComputeDigest() };
    }

    /// <summary>
    /// What a generation that follows <paramref name="previous"/> carries: the latest release of each extent nothing can
    /// rebuild, the previous generation's own replacing an earlier one of its kind, and none of the kind the new
    /// generation releases itself, whose own record is then the latest.
    /// </summary>
    public static IReadOnlyList<GenerationRelease>? ReleasesCarriedFrom(SessionManifestV1? previous, RetentionRecord? own = null)
    {
        if (previous is null)
        {
            return null;
        }

        var releases = new List<GenerationRelease>(previous.EarlierReleases ?? []);
        if (previous.Retention is { } released && IsCarried(released.Kind))
        {
            _ = releases.RemoveAll(release => release.Record.Kind == released.Kind);
            releases.Add(new(previous.Generation, released));
        }

        if (own is not null)
        {
            _ = releases.RemoveAll(release => release.Record.Kind == own.Kind);
        }

        return releases.Count == 0 ? null : [.. releases.OrderBy(release => release.Generation)];
    }

    /// <summary>
    /// The latest release of <paramref name="kind"/> this generation states: its own record when it published one, else
    /// the one it carries; null when it states none.
    /// </summary>
    public GenerationRelease? LatestRelease(RetentionExtentKind kind) =>
        Retention is { } own && own.Kind == kind
            ? new(Generation, own)
            : EarlierReleases?.LastOrDefault(release => release.Record.Kind == kind);

    /// <summary>
    /// What this generation holds on disk: every file it names, at the length it measured before naming it. It is the size
    /// a session is stated at, and what a pin's allowance is measured against.
    /// </summary>
    public long HeldBytes() => Dependencies.Aggregate(0L, (held, dependency) => checked(held + dependency.LengthBytes));

    /// <summary>The releases a later generation carries: those nothing can rebuild. Derived files can be rebuilt.</summary>
    private static bool IsCarried(RetentionExtentKind kind) =>
        kind is RetentionExtentKind.JournalPrefix or RetentionExtentKind.Content or RetentionExtentKind.Interval;

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

        return Retention?.Validate() ?? EarlierReleasesProblem();
    }

    /// <summary>Why the releases this manifest carries are not ones a generation can carry, or null when they are.</summary>
    private string? EarlierReleasesProblem()
    {
        if (EarlierReleases is not { } earlier)
        {
            return null;
        }

        if (earlier.Count == 0)
        {
            return "A manifest that carries no earlier release names none, rather than an empty list.";
        }

        var kinds = new HashSet<RetentionExtentKind>();
        long last = 0;
        foreach (GenerationRelease? release in earlier)
        {
            if (release?.Record is null)
            {
                return "An earlier release names the generation that published it and its retention record.";
            }

            if (release.Generation <= last || release.Generation >= Generation)
            {
                return $"Generation {Generation} carries a release of generation {release.Generation}: a generation carries "
                    + "releases of earlier generations, oldest first.";
            }

            if (!IsCarried(release.Record.Kind))
            {
                return $"Generation {Generation} carries a {release.Record.Kind} release: a generation carries only the "
                    + "releases nothing can rebuild, of a journal prefix, of kept content and of an interval.";
            }

            if (!kinds.Add(release.Record.Kind) || Retention?.Kind == release.Record.Kind)
            {
                return $"Generation {Generation} carries more than the latest {release.Record.Kind} release.";
            }

            if (release.Record.Validate() is { } problem)
            {
                return problem;
            }

            last = release.Generation;
        }

        return null;
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

        // So are the releases carried from earlier generations, so a manifest that carries none hashes as it did before
        // releases were carried.
        foreach (GenerationRelease release in EarlierReleases ?? [])
        {
            canonical.Append("earlier|").Append(release.CanonicalForm).Append('\n');
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
