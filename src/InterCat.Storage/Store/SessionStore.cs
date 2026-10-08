using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace InterCat.Storage;

/// <summary>
/// The dependencies one store instance has measured. A published file is immutable, so a dependency this instance
/// already measured - the same name, length and digest, unchanged in last-write time since - is not measured again.
/// Any other file, or one another instance published, is. A fresh instance hashes everything once, which is where
/// corruption that leaves a file's length and time alone is found (store-v1 §3). A viewer's instance first only lists
/// what a generation names and hashes it after its first view: a listed dependency holds for a lease as a hashed one
/// does, until its hash is taken or it is forgotten.
/// </summary>
internal sealed class DependencyMeasurements
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Measurement> measured = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this dependency was measured here, hashed or listed, and its file has not been written since.
    /// </summary>
    public bool Holds(StoreDependency dependency, long lastWriteTicks)
    {
        lock (gate)
        {
            return measured.TryGetValue(dependency.Name, out Measurement known) && known.Matches(dependency, lastWriteTicks);
        }
    }

    /// <summary>Whether this dependency's bytes were hashed here and its file has not been written since.</summary>
    public bool HasHashed(StoreDependency dependency, long lastWriteTicks)
    {
        lock (gate)
        {
            return measured.TryGetValue(dependency.Name, out Measurement known)
                && known.Hashed
                && known.Matches(dependency, lastWriteTicks);
        }
    }

    /// <summary>
    /// Records a dependency found present with its recorded length and not hashed. A hash already taken of the same
    /// unchanged file is kept.
    /// </summary>
    public void RecordListed(StoreDependency dependency, long lastWriteTicks)
    {
        lock (gate)
        {
            if (!(measured.TryGetValue(dependency.Name, out Measurement known) && known.Hashed
                && known.Matches(dependency, lastWriteTicks)))
            {
                measured[dependency.Name] = new(dependency.LengthBytes, dependency.Digest, lastWriteTicks, Hashed: false);
            }
        }
    }

    /// <summary>Drops what was measured of a file, so the next lease measures it again from its bytes.</summary>
    public void Forget(string name)
    {
        lock (gate)
        {
            _ = measured.Remove(name);
        }
    }

    private readonly record struct Measurement(long Length, string Digest, long LastWriteTicks, bool Hashed)
    {
        public bool Matches(StoreDependency dependency, long lastWriteTicks) =>
            Length == dependency.LengthBytes
            && LastWriteTicks == lastWriteTicks
            && string.Equals(Digest, dependency.Digest, StringComparison.Ordinal);
    }

    /// <summary>Whether nothing has been measured here yet, as in a fresh instance.</summary>
    public bool IsEmpty
    {
        get
        {
            lock (gate)
            {
                return measured.Count == 0;
            }
        }
    }

    /// <summary>Records a dependency whose bytes were just hashed and matched.</summary>
    public void Record(StoreDependency dependency, long lastWriteTicks)
    {
        lock (gate)
        {
            measured[dependency.Name] = new(dependency.LengthBytes, dependency.Digest, lastWriteTicks, Hashed: true);
        }
    }
}

/// <summary>What opening a session found. The legacy removed-staging field stays empty: open is read-only.</summary>
public sealed record StoreRecoveryReport(
    long Generation,
    bool RolledBackToLastKnownGood,
    string? RollbackReason,
    IReadOnlyList<string> RemovedStagingFiles,
    IReadOnlyList<string> OrphanFiles,
    long OrphanBytes)
{
    public static StoreRecoveryReport Empty { get; } = new(0, false, null, [], [], 0);
}

/// <summary>
/// What hashing a generation's files after opening it found (<see cref="SessionStore.VerifyContents"/>): how many files
/// and bytes it hashed, and each file whose bytes are not the ones its generation recorded.
/// </summary>
public sealed record StoreContentReport(long Generation, int HashedFiles, long HashedBytes, IReadOnlyList<string> Problems)
{
    /// <summary>Whether every file the generation names holds the bytes it recorded.</summary>
    public bool Verified => Problems.Count == 0;
}

/// <summary>What one commit published.</summary>
public sealed record StoreCommitResult(
    SessionManifestV1 Manifest,
    IReadOnlyList<string> PublishedFiles,
    long PreviousGeneration);

/// <summary>What an explicit pointer repair changed, and the preserved damaged pointer if one existed.</summary>
public sealed record PointerRepairOutcome(
    bool Repaired,
    long VerifiedGeneration,
    string? RollbackReason,
    string? DamagedPointerBackup);

/// <summary>Staging files proved abandoned by an unlockable ownership marker, and files not safe to clean.</summary>
public sealed record StagingCleanupReport(
    IReadOnlyList<string> AbandonedFiles,
    IReadOnlyList<string> ActiveFiles,
    IReadOnlyList<string> UnmarkedFiles,
    IReadOnlyList<string> MarkerOnlyFiles,
    IReadOnlyList<string> RemovedFiles,
    string CandidateDigest);

/// <summary>
/// One file staged for a generation. It is written under a unique staging name, flushed to the device
/// and measured; publication renames it. Until then it is not part of any generation, and a crash
/// leaves it as an unreferenced staging file. Opening reports it, but does not delete it: a second
/// process may have completed staging and not yet committed.
/// </summary>
public sealed class StoreStagingFile : IDisposable
{
    private readonly IOwnedDirectory directory;
    private readonly FileStream stream;
    private readonly FileStream ownership;
    private bool completed;
    private bool disposed;
    private bool published;

    internal StoreStagingFile(
        string stagingName,
        string ownershipName,
        string publishedName,
        StoreDependencyKind kind,
        IOwnedDirectory directory,
        FileStream stream,
        FileStream ownership)
    {
        StagingName = stagingName;
        OwnershipName = ownershipName;
        PublishedName = publishedName;
        Kind = kind;
        this.directory = directory;
        this.stream = stream;
        this.ownership = ownership;
    }

    public string StagingName { get; }

    public string OwnershipName { get; }

    public string PublishedName { get; }

    public StoreDependencyKind Kind { get; }

    public Stream Content
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return completed
                ? throw new InvalidOperationException("This staged file is complete and is no longer written.")
                : stream;
        }
    }

    internal StoreDependency? Dependency { get; private set; }

    internal bool IsDisposed => disposed;

    /// <summary>Flushes to the device and measures what was written. A staged file is published only after this.</summary>
    public StoreDependency Complete()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed)
        {
            return Dependency!;
        }

        stream.Flush(flushToDisk: true);
        stream.Position = 0;
        byte[] digest = SHA256.HashData(stream);
        long length = stream.Length;
        stream.Dispose();
        completed = true;
        Dependency = new(
            PublishedName,
            Kind,
            length,
            string.Concat("sha256:", Convert.ToHexStringLower(digest)));
        return Dependency;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            if (!completed)
            {
                stream.Dispose();
            }
        }
        finally
        {
            ownership.Dispose();
        }
    }

    internal void MarkPublished()
    {
        if (published)
        {
            return;
        }

        published = true;
        ownership.Dispose();
        try
        {
            _ = directory.RemoveOwnedFile(OwnershipName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Publication already succeeded. An orphaned ownership marker is recoverable cleanup,
            // not a reason to tell the caller its committed generation failed.
        }
    }
}

/// <summary>
/// The §20.1 commit protocol over an owned session directory. A generation is published in the order
/// the section states: stage and flush, publish the immutable files, write the manifest that references
/// only durable dependencies, replace the current-generation pointer, and only then announce it.
///
/// Nothing here trusts a rename to prove a dependency survived. The manifest carries its own digest and
/// the length and digest of every file it depends on, and the previous pointer is retained, so a torn
/// publication is detected on the next open and rolled back to the last complete generation rather than
/// read as though it had completed.
/// </summary>
public sealed partial class SessionStore
{
    public const string PublicationLockFileName = "session-publication.lock";

    public const string EvidenceLeaseLockFileName = "session-evidence-lease.lock";

    /// <summary>
    /// What a reader says of a folder in which no generation was ever published: one that is not a session's, or a capture's
    /// that has published nothing yet. Looking at it writes nothing there.
    /// </summary>
    public const string NoGeneration = "No InterCat session has been published in this folder. It is not a session's "
        + "folder, or its capture has not published a generation yet; nothing was written to it.";

    public const string StagingPrefix = "stg-";

    public const string StagingSuffix = ".tmp";

    public const string StagingOwnershipSuffix = ".lease";

    /// <summary>How many files one generation may publish at once.</summary>
    public const int MaximumStagedFiles = 4_096;

    /// <summary>
    /// Per open store, the verified immutable segment payload its queries keep in memory (§20.1): what the cached readers
    /// hold, their read columns, chunks and decoded dictionaries. A reader that does not fit is served without entering
    /// the cache, and when a query leaves the cached readers holding more, they give back every column but session time
    /// and mechanism (R8, §12). It is not an entry count or a managed-memory measurement. Half of §12's 512 MiB analysis
    /// budget; trimmed readers hold about nine bytes a row, so it keeps a reader for every segment of a session of
    /// tens of millions of rows.
    /// </summary>
    public const long DefaultSegmentReaderCacheBytes = 256L * 1024 * 1024;

    /// <summary>
    /// How long a reader waits while a writer holds the evidence guard exclusively. A writer holds it only while it
    /// removes files no reader can reach, for milliseconds; a reader waits that out rather than failing its lease.
    /// </summary>
    private static readonly TimeSpan EvidenceGuardWait = TimeSpan.FromSeconds(1);

    /// <summary>
    /// An upper bound on the metadata one publication writes besides its staged files: the generation's manifest
    /// (a fixed part plus one entry per dependency, whose name is at most <see cref="OwnedFileName.MaximumLength"/>
    /// characters) and the replaced current and last-known-good pointers. It excludes a retention record, which a
    /// live recording never publishes, and allocation-unit rounding, which depends on the volume. A free-space
    /// admission check reserves this so a stopped capture can still publish its last generation.
    /// </summary>
    public static long PublicationMetadataBound(int dependencies)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(dependencies);
        return checked(32_768 + (long)dependencies * 512);
    }

    private readonly IOwnedDirectory directory;
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, EvidenceLease> leases = [];
    private SegmentReaderCache segmentReaders = new(DefaultSegmentReaderCacheBytes);

    /// <summary>What this instance has read back and hashed, so an unchanged immutable file is hashed once.</summary>
    private readonly DependencyMeasurements measurements;
    private SessionManifestV1? current;
    private SessionManifestV1? publicationBaseline;
    private string? rollbackReason;

    private SessionStore(
        IOwnedDirectory directory,
        Guid sessionId,
        string sourceIdentity,
        SessionManifestV1? current,
        StoreRecoveryReport recovery,
        DependencyMeasurements measurements)
    {
        this.directory = directory;
        SessionId = sessionId;
        SourceIdentity = sourceIdentity;
        this.current = current;
        publicationBaseline = current;
        if (current is not null)
        {
            segmentReaders.Prune(current);
        }

        Recovery = recovery;
        rollbackReason = recovery.RolledBackToLastKnownGood ? recovery.RollbackReason : null;
        this.measurements = measurements;
    }

    public Guid SessionId { get; }

    public string SourceIdentity { get; }

    public StoreRecoveryReport Recovery { get; }

    /// <summary>
    /// Why the generation this store reads is not the one its current pointer names, or null when it is. Opening sets
    /// it, as <see cref="Recovery"/> reports; every lease sets it again, so a generation that fails after opening, as a
    /// viewer's hashing can find, is stated as a fallback rather than shown as the newest.
    /// </summary>
    public string? RollbackReason
    {
        get
        {
            lock (gate)
            {
                return rollbackReason;
            }
        }
    }

    /// <summary>The generation a reader acquires, or null when nothing has been published yet.</summary>
    public SessionManifestV1? Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    /// <summary>
    /// Opens a session, verifying the generation its pointer names and falling back to the retained
    /// last-known-good when that generation does not verify.
    /// </summary>
    public static SessionStore Open(IOwnedDirectory directory, Guid sessionId, string sourceIdentity = "")
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A session store needs the session it belongs to.", nameof(sessionId));
        }

        var measurements = new DependencyMeasurements();
        (SessionManifestV1? manifest, bool rolledBack, string? reason) = Acquire(directory, measurements);
        if (manifest is not null && manifest.SessionId != sessionId)
        {
            throw new InvalidDataException(
                $"This directory holds session {manifest.SessionId:N}, not {sessionId:N}. A session is "
                + "never opened as another one.");
        }

        (IReadOnlyList<string> removed, IReadOnlyList<string> orphans, long orphanBytes) =
            Sweep(directory, manifest);
        return new(
            directory,
            sessionId,
            sourceIdentity,
            manifest,
            new(manifest?.Generation ?? 0, rolledBack, reason, removed, orphans, orphanBytes),
            measurements);
    }

    /// <summary>
    /// The generation the next commit will publish. A caller needs it before the commit, because a
    /// generation's files are named after it.
    /// </summary>
    public long NextGeneration
    {
        get
        {
            lock (gate)
            {
                return NextAvailableGeneration();
            }
        }
    }

    /// <summary>The validated root this session is held open on, so a reader can open its dependencies.</summary>
    public IOwnedDirectory Root => directory;

    /// <summary>
    /// Diagnostics for the payload-admission-bounded immutable segment-reader cache. Counts are per store instance and
    /// are never evidence or query results; they exist so scale qualification can prove the cache stays bounded.
    /// </summary>
    public SegmentReaderCacheSnapshot SegmentReaderCache => segmentReaders.Snapshot;

    internal SegmentReaderCache SegmentReaders => segmentReaders;

    /// <summary>
    /// Gives up every cached segment reader and keeps what this store has verified. A viewer does this for a session it
    /// no longer shows, so it holds one session's readers rather than one set per session it has shown. A query already
    /// holding a reader keeps it.
    /// </summary>
    public void ReleaseSegmentReaders() => segmentReaders.Clear();

    /// <summary>Replaces the reader cache with an empty one of another budget, so a test can exercise admission.</summary>
    internal void UseSegmentReaderBudget(long budgetBytes)
    {
        lock (gate)
        {
            segmentReaders = new(budgetBytes);
            if (current is not null)
            {
                segmentReaders.Prune(current);
            }
        }
    }

    /// <summary>
    /// Opens a session for reading without being told which session it is. The identity comes from the
    /// generation the pointer names, so a viewer can open a directory it was handed. A store opened this way
    /// publishes nothing: a generation names the session it belongs to, and this one was not told which.
    /// </summary>
    public static SessionStore OpenExisting(IOwnedDirectory directory) => OpenExisting(directory, hashContents: true);

    /// <summary>
    /// Opens a session to view it, as <see cref="OpenExisting(IOwnedDirectory)"/> does, without hashing it first. The
    /// pointer, the manifest's digest and every dependency's presence and length are checked now, from one listing;
    /// what the files hold is hashed by <see cref="VerifyContents"/>, which a viewer runs after its first view. Until
    /// then a reader checks every byte it interprets against that file's own checksums (segment-v1 §9), so the first
    /// view costs what it reads rather than every byte of the session (S1).
    /// </summary>
    public static SessionStore OpenForViewing(IOwnedDirectory directory) => OpenExisting(directory, hashContents: false);

    private static SessionStore OpenExisting(IOwnedDirectory directory, bool hashContents)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var measurements = new DependencyMeasurements();
        (SessionManifestV1? manifest, bool rolledBack, string? reason) =
            Acquire(directory, measurements, hashContents: hashContents);
        (IReadOnlyList<string> removed, IReadOnlyList<string> orphans, long orphanBytes) =
            Sweep(directory, manifest);
        return new(
            directory,
            manifest?.SessionId ?? Guid.Empty,
            manifest?.SourceIdentity ?? string.Empty,
            manifest,
            new(manifest?.Generation ?? 0, rolledBack, reason, removed, orphans, orphanBytes),
            measurements);
    }

    /// <summary>Opens a file for a generation that has not been published yet.</summary>
    public StoreStagingFile Stage(string publishedName, StoreDependencyKind kind)
    {
        if (SessionId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "This store was opened for reading without being told which session it is, so it cannot "
                + "publish a generation. A generation names the session it belongs to.");
        }

        OwnedFileName.Require(publishedName, nameof(publishedName));
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "A staged file declares a known kind.");
        }

        if (publishedName.StartsWith(StagingPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"A published name never begins with '{StagingPrefix}'; that prefix marks a file no "
                + "generation references yet.",
                nameof(publishedName));
        }

        if (publishedName.Equals(SessionPointerV1.FileName, StringComparison.OrdinalIgnoreCase)
            || publishedName.Equals(SessionPointerV1.PreviousFileName, StringComparison.OrdinalIgnoreCase)
            || publishedName.Equals(PublicationLockFileName, StringComparison.OrdinalIgnoreCase)
            || publishedName.Equals(EvidenceLeaseLockFileName, StringComparison.OrdinalIgnoreCase)
            || publishedName.Equals(RetentionPinsV1.FileName, StringComparison.OrdinalIgnoreCase)
            || publishedName.Equals(RetentionPinsV1.LockFileName, StringComparison.OrdinalIgnoreCase)
            || publishedName.StartsWith("manifest-", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "A dependency cannot use the session's pointer, manifest, pins or lock namespace.",
                nameof(publishedName));
        }

        string prefix = string.Create(CultureInfo.InvariantCulture, $"{StagingPrefix}{Guid.NewGuid():N}");
        string stagingName = prefix + StagingSuffix;
        string ownershipName = prefix + StagingOwnershipSuffix;
        FileStream ownership = directory.OpenOwnedFile(
            ownershipName, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.None);
        try
        {
            FileStream stream = directory.OpenOwnedFile(
                stagingName, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                FileOptions.WriteThrough);
            return new(stagingName, ownershipName, publishedName, kind, directory, stream, ownership);
        }
        catch
        {
            ownership.Dispose();
            _ = directory.RemoveOwnedFile(ownershipName);
            throw;
        }
    }

    /// <summary>
    /// Publishes one generation. Every staged file must be complete; a name an earlier generation
    /// already published is refused, because a published file is immutable.
    /// </summary>
    public StoreCommitResult Commit(
        IReadOnlyList<StoreStagingFile> staged,
        CommittedBoundary boundary,
        DateTimeOffset committedUtc,
        long? expectedGeneration = null,
        CancellationToken cancellationToken = default) =>
        CommitCore(staged, boundary, committedUtc, CommitScope.Additive, cancellationToken,
            expectedGeneration: expectedGeneration);

    /// <summary>
    /// Publishes a new derivation of the current admitted journal. It retains the exact journal and its
    /// interpretation plan but replaces every previous segment, dictionary and derived index. Carrying the
    /// earlier segments would count the same capture twice; the previous manifest remains last-known-good.
    /// </summary>
    public StoreCommitResult CommitReplacingDerived(
        IReadOnlyList<StoreStagingFile> staged,
        long sourceGeneration,
        CommittedBoundary boundary,
        DateTimeOffset committedUtc,
        long? expectedGeneration = null,
        CancellationToken cancellationToken = default) =>
        CommitCore(staged, boundary, committedUtc, CommitScope.ReplacingDerived, cancellationToken, sourceGeneration,
            expectedGeneration);

    /// <summary>
    /// Publishes an index of generation <paramref name="sourceGeneration"/> as the next generation
    /// (`contracts/derivation-checkpoint-v1.md` §2). It names the same committed boundary, evidence and derived files,
    /// the staged indexes, and no earlier index: an index describes the files of the generation it was derived from.
    /// It is refused when that generation is no longer current, since a writer may have published since.
    /// </summary>
    public StoreCommitResult CommitIndex(
        IReadOnlyList<StoreStagingFile> staged,
        long sourceGeneration,
        DateTimeOffset committedUtc,
        long? expectedGeneration = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(staged);
        if (staged.Count == 0 || staged.Any(file => file.Kind != StoreDependencyKind.Index))
        {
            throw new ArgumentException("An index publication publishes one or more indexes and nothing else.", nameof(staged));
        }

        return CommitCore(staged, CommittedBoundary.None, committedUtc, CommitScope.ReplacingIndexes, cancellationToken,
            sourceGeneration, expectedGeneration);
    }

    /// <summary>What a commit carries from the generation before it.</summary>
    private enum CommitScope
    {
        /// <summary>Every dependency but an index: the staged files are added.</summary>
        Additive,

        /// <summary>The evidence and what describes it; the staged files replace every derived file.</summary>
        ReplacingDerived,

        /// <summary>Every dependency but the indexes, which the staged indexes replace; the boundary is the source's.</summary>
        ReplacingIndexes,
    }

    private StoreCommitResult CommitCore(
        IReadOnlyList<StoreStagingFile> staged,
        CommittedBoundary boundary,
        DateTimeOffset committedUtc,
        CommitScope scope,
        CancellationToken cancellationToken,
        long? sourceGeneration = null,
        long? expectedGeneration = null)
    {
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentNullException.ThrowIfNull(boundary);
        if (staged.Count > MaximumStagedFiles)
        {
            throw new ArgumentException(
                $"A generation publishes at most {MaximumStagedFiles} files; this one staged {staged.Count}.",
                nameof(staged));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            using FileStream publicationLock = AcquirePublicationLock();
            RequireFreshCurrent();
            // A read-only viewer must be able to open a shared guard after this generation
            // appears. The writer creates it before the pointer can name the generation.
            using (directory.OpenOwnedFile(
                EvidenceLeaseLockFileName,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite,
                FileOptions.None))
            {
            }

            long previousGeneration = current?.Generation ?? 0;
            long nextGeneration = NextAvailableGeneration();
            if (expectedGeneration is { } expected && expected != nextGeneration)
            {
                throw new InvalidOperationException(
                    $"Generation {expected} was staged, but generation {nextGeneration} is now the next "
                    + "unoccupied number. Reopen and stage again; a generation's file names must agree "
                    + "with its manifest.");
            }
            List<StoreDependency> carried;
            if (scope == CommitScope.ReplacingIndexes)
            {
                SessionManifestV1 previous = current
                    ?? throw new InvalidOperationException("No published generation exists to index.");
                if (previous.Generation != sourceGeneration)
                {
                    throw new InvalidOperationException(
                        $"Generation {sourceGeneration} is no longer current: generation {previous.Generation} was "
                        + "published while its index was being derived. Derive the index of the current generation.");
                }

                // An index describes the files of the generation it was derived from, so an earlier one describes an
                // earlier set of files and is not carried; everything else is, under the same committed boundary.
                boundary = previous.Boundary;
                carried = [.. previous.Dependencies.Where(dependency => dependency.Kind != StoreDependencyKind.Index)];
            }
            else if (scope == CommitScope.ReplacingDerived)
            {
                SessionManifestV1 previous = current
                    ?? throw new InvalidOperationException("No published generation exists to re-derive.");
                if (previous.Generation != sourceGeneration)
                {
                    throw new InvalidOperationException(
                        $"Generation {sourceGeneration} changed while its journal was being re-derived. "
                        + "Reopen the current generation and replay its evidence again.");
                }

                if (previous.Boundary != boundary)
                {
                    throw new InvalidOperationException(
                        "A replacement derivation must name the current generation's exact committed journal "
                        + "boundary; it cannot silently switch evidence while replacing derived files.");
                }

                // The admitted evidence - every journal chunk and the content kept beside it - and the plan, optional
                // coverage ledger, finalization marker, clock calibration, collectors and redaction policy that describe
                // the capture are not derivations of it, so replacement carries them unchanged. Carrying every journal means no
                // chunk's evidence is ever dropped by replacing the rows derived from it, and carrying a redaction policy
                // means a package never loses its provenance (I22).
                carried =
                [
                    .. previous.Dependencies.Where(dependency =>
                        dependency.Kind is StoreDependencyKind.DerivationPlan
                            or StoreDependencyKind.CoverageLedger
                            or StoreDependencyKind.CaptureFinalization
                            or StoreDependencyKind.ClockCalibration
                            or StoreDependencyKind.CollectorIdentities
                            or StoreDependencyKind.RedactionPolicy
                            or StoreDependencyKind.Journal
                            or StoreDependencyKind.Content),
                ];
                if (!carried.Any(dependency => dependency.Kind == StoreDependencyKind.Journal
                        && dependency.Name.Equals(boundary.JournalName, StringComparison.OrdinalIgnoreCase))
                    || carried.Count(dependency => dependency.Kind == StoreDependencyKind.DerivationPlan) != 1)
                {
                    throw new InvalidOperationException(
                        "A replacement derivation needs the journal its boundary names and one retained normalization "
                        + "plan; a legacy session needs a verified plan migration.");
                }
            }
            else
            {
                // An index describes the files of the generation it was published for and is not carried: a generation
                // that adds segments has no index until its writer publishes one, and a damaged index can then cost at
                // most a rollback to the generation before it, never every later generation (derivation-checkpoint-v1).
                carried = [.. current?.Dependencies.Where(dependency => dependency.Kind != StoreDependencyKind.Index) ?? []];
            }

            // A capture's calibration grows once, from its start sample alone to its start and stop: the one a generation
            // stages replaces the one it would carry, which holds nothing the new one does not (clock-calibration-v1 §1).
            if (staged.Any(file => file.Dependency?.Kind == StoreDependencyKind.ClockCalibration))
            {
                carried = [.. carried.Where(dependency => dependency.Kind != StoreDependencyKind.ClockCalibration)];
            }

            var names = new HashSet<string>(
                carried.Select(dependency => dependency.Name),
                StringComparer.OrdinalIgnoreCase);
            var published = new List<StoreDependency>(staged.Count);
            foreach (StoreStagingFile file in staged)
            {
                if (file.IsDisposed)
                {
                    throw new InvalidOperationException(
                        $"Staged file '{file.PublishedName}' no longer has an active owner. "
                        + "Disposed staging cannot be published after cleanup may have claimed it.");
                }

                RequireUnpublishedTarget(file.PublishedName);
                StoreDependency dependency = file.Dependency
                    ?? throw new InvalidOperationException(
                        $"Staged file '{file.PublishedName}' was not completed, so its contents are not "
                        + "durable and it cannot be published.");
                if (!names.Add(dependency.Name))
                {
                    throw new InvalidOperationException(
                        $"Generation {nextGeneration} would republish '{dependency.Name}', which "
                        + "an earlier generation already published. A published file is immutable.");
                }

                published.Add(dependency);
            }

            // Publish the immutable files first. Until the manifest exists, none of them is referenced,
            // so a crash here leaves unreferenced files rather than a generation missing a dependency.
            foreach (StoreStagingFile file in staged)
            {
                directory.ReplaceOwnedFile(file.StagingName, file.PublishedName);
            }

            SessionManifestV1 manifest = SessionManifestV1.Create(
                nextGeneration,
                SessionId,
                committedUtc,
                SourceIdentity,
                previousGeneration == 0 ? null : previousGeneration,
                boundary,
                [.. carried, .. published],
                earlierReleases: SessionManifestV1.ReleasesCarriedFrom(current));
            RequireUnpublishedTarget(SessionManifestV1.FileNameFor(manifest.Generation));

            // The manifest may reference only durable dependencies, so every one of them is read back
            // and measured before it is named. A rename is not a power-failure guarantee (§20.1).
            Unverified? unverifiable = VerifyDependencies(directory, manifest, measurements);
            if (unverifiable is not null)
            {
                throw new IOException(
                    $"Generation {manifest.Generation} was not published: {unverifiable}");
            }

            Write(directory, SessionManifestV1.FileNameFor(manifest.Generation), manifest, SessionManifestV1.Json);
            RetainPreviousPointer(directory);
            Write(directory, SessionPointerV1.FileName, SessionPointerV1.For(manifest), SessionManifestV1.Json);
            segmentReaders.Prune(manifest);
            current = manifest;
            publicationBaseline = manifest;
            foreach (StoreStagingFile file in staged)
            {
                file.MarkPublished();
            }

            _ = RemoveSupersededManifests(DateTimeOffset.UtcNow);
            return new(manifest, [.. published.Select(dependency => dependency.Name)], previousGeneration);
        }
    }

    /// <summary>
    /// Acquires the current generation and every dependency it names as one lease. §20.1's fifth step makes
    /// this the unit a reader holds, and I18 makes it the thing retention cannot break: while a lease is
    /// live, nothing this store does removes a file the lease names.
    /// </summary>
    public EvidenceLease AcquireLease(
        EvidenceLeaseRequest? request = null,
        DateTimeOffset? nowUtc = null)
    {
        EvidenceLeaseRequest bounds = request ?? EvidenceLeaseRequest.Interactive;
        string? problem = bounds.Validate();
        if (problem is not null)
        {
            throw new ArgumentException(problem, nameof(request));
        }

        DateTimeOffset now = nowUtc ?? DateTimeOffset.UtcNow;

        // Hold the shared marker before reading the pointer. Retention may publish concurrently, but it cannot remove
        // any old dependency while this handle is open. Readers need only read access to the broker-owned root; they
        // never take the writer's publication lock. Taking the marker can wait out a writer's removal, so it is taken
        // before this store's lock, which the store's other threads need meanwhile.
        FileStream hold = AcquireEvidenceReadHold();
        lock (gate)
        {
            try
            {
                (SessionManifestV1? disk, bool rolledBack, string? reason) = Acquire(directory, measurements, current);
                rollbackReason = rolledBack ? reason : null;
                SessionManifestV1 manifest = disk ?? throw new NoSessionException();
                if (manifest.SessionId != SessionId)
                {
                    throw new InvalidDataException("The session identity changed after this store was opened.");
                }

                if (current?.Generation == manifest.Generation
                    && string.Equals(current.Digest, manifest.Digest, StringComparison.Ordinal))
                {
                    // Keep the object identity of a still-verified generation for existing readers.
                    manifest = current;
                }
                else
                {
                    segmentReaders.Prune(manifest);
                }

                current = manifest;

                // A pin cannot promise less disk allowance than the evidence it already holds.
                long held = manifest.HeldBytes();
                if (bounds.Kind == EvidenceLeaseKind.Pinned && bounds.ReservedBytes < held)
                {
                    throw new InvalidOperationException(
                        $"Generation {manifest.Generation} holds {held} bytes of evidence and this pin reserves "
                        + $"{bounds.ReservedBytes}. A pin that reserves less than it pins is refused rather "
                        + "than promising space it does not have.");
                }

                Sweep(now);
                var lease = new EvidenceLease(
                    Guid.NewGuid(),
                    bounds.Kind,
                    manifest,
                    [.. manifest.Dependencies.Select(dependency => dependency.Name)],
                    SessionManifestV1.FileNameFor(manifest.Generation),
                    bounds.ReservedBytes,
                    now,
                    now + bounds.Duration,
                    hold,
                    Release);
                leases.Add(lease.Id, lease);
                lease.StartExpiryTimer(bounds.Duration);
                return lease;
            }
            catch
            {
                hold.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Hashes every dependency the current generation names that this store has not hashed, as a viewer does after
    /// its first view of a session it opened with <see cref="OpenForViewing"/>. The generation is held by a lease
    /// meanwhile, so retention cannot remove what is being checked. A dependency whose bytes disagree with its recorded
    /// digest is reported and forgotten: the next lease measures it again and fails its generation over to the
    /// last-known-good, exactly as opening would have (store-v1 §3).
    /// </summary>
    public StoreContentReport VerifyContents(CancellationToken cancellationToken = default)
    {
        if (Current is null)
        {
            return new(0, 0, 0, []);
        }

        using EvidenceLease lease = AcquireLease(new EvidenceLeaseRequest { Duration = TimeSpan.FromHours(1) });
        SessionManifestV1 manifest = lease.Manifest;
        var problems = new List<string>();
        int hashed = 0;
        long hashedBytes = 0;
        byte[] buffer = new byte[HashBufferBytes];
        foreach (StoreDependency dependency in manifest.Dependencies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? problem;
            try
            {
                using FileStream stream = directory.OpenOwnedFile(
                    dependency.Name, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
                long lastWrite = File.GetLastWriteTimeUtc(stream.SafeFileHandle).Ticks;
                if (measurements.HasHashed(dependency, lastWrite))
                {
                    continue;
                }

                problem = stream.Length != dependency.LengthBytes
                    ? dependency.LengthMismatch(stream.Length, manifest.Generation)
                    : DigestProblem(stream, dependency, manifest.Generation, buffer, cancellationToken);
                if (problem is null)
                {
                    measurements.Record(dependency, lastWrite);
                    hashed++;
                    hashedBytes += dependency.LengthBytes;
                    continue;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                problem = Unreadable(dependency, exception, manifest.Generation);
            }

            measurements.Forget(dependency.Name);
            problems.Add(problem);
        }

        return new(manifest.Generation, hashed, hashedBytes, problems);
    }

    /// <summary>
    /// How much of a file one read hands the hash. Hashing a stream directly read it a few kilobytes at a time, at about
    /// half the rate: 309 MiB took 420 ms that way and 216 ms this way (revision 152).
    /// </summary>
    private const int HashBufferBytes = 1024 * 1024;

    /// <summary>
    /// Whether a dependency's readers check every byte they interpret against checksums the file carries, so it can be
    /// read before its file is hashed: a segment (segment-v1 §9), a dictionary, whose decoder checks its own digest,
    /// a journal, whose frames and records carry theirs (journal-v1), an index, which its reader hashes against the
    /// digest its generation records before interpreting a byte (derivation-checkpoint-v1 §4) or, for a tile index read
    /// by the block, checks block by block (tile-index-v1 §4), and a content chunk, whose header and fragments carry
    /// theirs (content-v1 §2).
    /// </summary>
    private static bool ChecksItself(StoreDependencyKind kind) =>
        kind is StoreDependencyKind.Segment or StoreDependencyKind.Dictionary or StoreDependencyKind.Journal
            or StoreDependencyKind.Index or StoreDependencyKind.Content;

    /// <summary>Why a dependency's bytes are not the ones its generation recorded, or null when they are.</summary>
    private static string? DigestProblem(
        FileStream stream,
        StoreDependency dependency,
        long generation,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }

        string digest = string.Concat("sha256:", Convert.ToHexStringLower(hash.GetHashAndReset()));
        return string.Equals(digest, dependency.Digest, StringComparison.Ordinal)
            ? null
            : dependency.DigestMismatch(digest, generation);
    }

    /// <summary>
    /// Why a dependency could not be read. A missing one is said to be missing, where the exception would give the file's
    /// whole path in words of its own.
    /// </summary>
    private static string Unreadable(StoreDependency dependency, Exception exception, long generation) =>
        exception is FileNotFoundException
            ? string.Create(CultureInfo.InvariantCulture, $"'{dependency.Name}', which generation {generation} records, is missing")
            : $"'{dependency.Name}' could not be read: {exception.Message}";

    /// <summary>Every lease still holding evidence at this moment. An expired one holds nothing.</summary>
    public IReadOnlyList<EvidenceLease> LiveLeases(DateTimeOffset? nowUtc = null)
    {
        DateTimeOffset now = nowUtc ?? DateTimeOffset.UtcNow;
        lock (gate)
        {
            Sweep(now);
            return [.. leases.Values.Where(lease => !lease.HasExpiredAt(now))];
        }
    }

    /// <summary>
    /// Publishes a generation that no longer names the given dependencies, with the retention record that
    /// says what was released and why, and then removes each released file that no live lease holds.
    /// </summary>
    /// <remarks>
    /// Releasing and removing are separate steps on purpose. The generation stops naming a file immediately,
    /// which is what makes the retention visible; the bytes go when the last reader that acquired them lets
    /// go. A file a lease still holds is reported as awaiting release rather than removed behind the reader
    /// (I18, S6).
    /// </remarks>
    public RetentionOutcome ReleaseDependencies(
        IReadOnlyList<string> names,
        string reason,
        DateTimeOffset committedUtc,
        DateTimeOffset? nowUtc = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = nowUtc ?? DateTimeOffset.UtcNow;
        lock (gate)
        {
            using FileStream publicationLock = AcquirePublicationLock();
            RequireFreshCurrent();
            SessionManifestV1 manifest = current
                ?? throw new InvalidOperationException("This session has published no generation to retain from.");
            var released = new List<StoreDependency>();
            foreach (string name in names)
            {
                StoreDependency dependency = manifest.Dependencies.FirstOrDefault(candidate =>
                    candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException(
                        $"Generation {manifest.Generation} does not name '{name}', so retention has nothing "
                        + "to release. A retention that names a file the generation does not hold would "
                        + "publish a checkpoint for something that never existed.",
                        nameof(names));
                if (released.Any(already => already.Name.Equals(dependency.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new ArgumentException(
                        $"'{name}' is released twice in one retention action.",
                        nameof(names));
                }

                if (dependency.Kind == StoreDependencyKind.Journal)
                {
                    throw new ArgumentException(
                        "An admitted journal is not released by dropping its name. ADR-010 makes a journal "
                        + "release an extent with its own record; use the journal retention path.",
                        nameof(names));
                }

                if (dependency.Kind == StoreDependencyKind.DerivationPlan)
                {
                    throw new ArgumentException(
                        "A derivation plan interprets the retained journal and is not a disposable derived index. "
                        + "It remains with the admitted evidence so the journal can be re-derived later.",
                        nameof(names));
                }

                if (dependency.Kind == StoreDependencyKind.CoverageLedger)
                {
                    throw new ArgumentException(
                        "A coverage ledger states what the capture could observe and what it lost. No journal holds "
                        + "those facts, so it is not a rebuildable index and retention cannot release it (R21).",
                        nameof(names));
                }

                if (dependency.Kind == StoreDependencyKind.CaptureFinalization)
                {
                    throw new ArgumentException(
                        "A capture finalization marker is durable evidence that the recording reached its last "
                        + "publication. It is not a rebuildable derived index and retention cannot release it.",
                        nameof(names));
                }

                if (dependency.Kind == StoreDependencyKind.ClockCalibration)
                {
                    throw new ArgumentException(
                        "A clock calibration pairs the capture's clock with the wall clock and names its boot. No journal "
                        + "holds those facts, so it is not a rebuildable index and retention cannot release it.",
                        nameof(names));
                }

                if (dependency.Kind == StoreDependencyKind.CollectorIdentities)
                {
                    throw new ArgumentException(
                        "Collector identities name the processes that collected the capture. No journal holds them, so "
                        + "they are not a rebuildable index and retention cannot release them.",
                        nameof(names));
                }

                if (dependency.Kind == StoreDependencyKind.RedactionPolicy)
                {
                    throw new ArgumentException(
                        "A redaction policy is a redacted package's provenance: it says the journal is synthetic and "
                        + "what was pseudonymized. Releasing it would make the package read as an original capture (I22).",
                        nameof(names));
                }

                if (dependency.Kind == StoreDependencyKind.Content)
                {
                    throw new ArgumentException(
                        "Kept content is evidence, not a rebuildable derived file: it goes with the journal chunk whose "
                        + "records it belongs to, or all of it at once by a content release (content-v1 §2).",
                        nameof(names));
                }

                released.Add(dependency);
            }

            ReleaseIndexesWithSegments(manifest, released);
            return Publish(
                manifest,
                [.. manifest.Dependencies.Except(released)],
                manifest.Boundary,
                RetentionRecord.ForDerivedFiles(
                    now,
                    reason,
                    [.. released.Select(dependency => dependency.Name)],
                    released.Sum(dependency => dependency.LengthBytes)),
                committedUtc,
                released,
                now);
        }
    }

    /// <summary>
    /// Publishes a retention generation that replaces the journal with a shorter one, recording the extent
    /// that was released. The caller has already staged and completed the retained journal; this step is the
    /// publication and the checkpoint (ADR-010).
    /// </summary>
    public RetentionOutcome ReleaseJournalPrefix(
        StoreStagingFile retainedJournal,
        CommittedBoundary boundary,
        RetentionRecord record,
        DateTimeOffset committedUtc,
        DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(retainedJournal);
        ArgumentNullException.ThrowIfNull(boundary);
        ArgumentNullException.ThrowIfNull(record);
        if (record.Kind != RetentionExtentKind.JournalPrefix)
        {
            throw new ArgumentException(
                "A journal release publishes a journal-prefix retention record.",
                nameof(record));
        }

        // A rewrite gives up part of one journal rather than whole chunks, so no count of a recording's chunks follows from
        // it (ADR-048); a later chunk release states none.
        if (record.Recording is not null)
        {
            throw new ArgumentException(
                "A journal rewrite gives up part of one journal rather than whole chunks, so its record states no chunks "
                + "given up in all.",
                nameof(record));
        }

        DateTimeOffset now = nowUtc ?? DateTimeOffset.UtcNow;
        using FileStream pinsLock = AcquirePinsLock();
        lock (gate)
        {
            using FileStream publicationLock = AcquirePublicationLock();
            RequireFreshCurrent();
            SessionManifestV1 manifest = current
                ?? throw new InvalidOperationException("This session has published no generation to retain from.");
            RequireNoPins();
            if (manifest.Dependencies.Any(dependency => dependency.Kind == StoreDependencyKind.Content))
            {
                throw new InvalidOperationException(
                    "This session keeps content beside its journal. Rewriting the journal's prefix would leave content "
                    + "whose records it no longer holds, so its content goes only with whole recording chunks, or all of "
                    + "it on its own by a content release first (content-v1 §2). Nothing was published.");
            }

            StoreDependency previous = manifest.Dependencies.FirstOrDefault(dependency =>
                dependency.Kind == StoreDependencyKind.Journal
                && record.ReleasedFiles.Contains(dependency.Name, StringComparer.OrdinalIgnoreCase))
                ?? throw new ArgumentException(
                    "The retention record names no journal this generation holds.",
                    nameof(record));
            StoreDependency retained = retainedJournal.Dependency
                ?? throw new InvalidOperationException(
                    "The retained journal was not completed, so its contents are not durable and it cannot "
                    + "be published.");
            if (retainedJournal.IsDisposed)
            {
                throw new InvalidOperationException(
                    "The retained journal no longer has an active staging owner and cannot be published.");
            }

            long nextGeneration = NextAvailableGeneration();
            if (!retainedJournal.PublishedName.Equals(
                SegmentFormatV1.JournalFileName(nextGeneration), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The retained journal was staged for a generation that is no longer available. "
                    + "Retry retention against a newly staged generation.");
            }

            RequireUnpublishedTarget(retainedJournal.PublishedName);
            RequireUnpublishedTarget(SessionManifestV1.FileNameFor(nextGeneration));
            directory.ReplaceOwnedFile(retainedJournal.StagingName, retainedJournal.PublishedName);
            RetentionOutcome outcome = Publish(
                manifest,
                [.. manifest.Dependencies.Where(dependency => dependency != previous), retained],
                boundary,
                record,
                committedUtc,
                [previous],
                now,
                nextGeneration);
            retainedJournal.MarkPublished();
            return outcome;
        }
    }

    /// <summary>
    /// Publishes a retention generation that stops naming a live recording's oldest journal chunks (ADR-024). A chunk
    /// is a whole, immutable file, so nothing is rewritten: the generation names the chunks after them, keeps the
    /// committed boundary, and records the released extent. The caller counted the released records, because this
    /// store does not read inside the files it publishes.
    /// </summary>
    /// <remarks>
    /// Only a leading run of chunks is released, and never the one the boundary names. A chunk from the middle would
    /// leave the retained chunks describing a capture with a hole in it, and releasing the boundary's chunk would
    /// leave the generation naming evidence it no longer holds.
    /// </remarks>
    public RetentionOutcome ReleaseJournalChunks(
        IReadOnlyList<string> chunks,
        long releasedRecords,
        string reason,
        DateTimeOffset committedUtc,
        DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentOutOfRangeException.ThrowIfNegative(releasedRecords);
        DateTimeOffset now = nowUtc ?? DateTimeOffset.UtcNow;
        using FileStream pinsLock = AcquirePinsLock();
        lock (gate)
        {
            using FileStream publicationLock = AcquirePublicationLock();
            RequireFreshCurrent();
            SessionManifestV1 manifest = current
                ?? throw new InvalidOperationException("This session has published no generation to retain from.");
            RequireNoPins();
            if (chunks.Count == 0)
            {
                throw new ArgumentException(
                    "A chunk release gives up at least one chunk and keeps at least the newest, which the committed "
                    + "boundary names.",
                    nameof(chunks));
            }

            // The chunks sort in the order they were recorded, so the oldest are the first names; a chunk's kept content
            // goes with it, since the content chunk its generation published holds content of its records only
            // (content-v1 §2).
            StoreDependency[] releasedFiles = [.. LeadingChunks(manifest, chunks)];
            var record = new RetentionRecord(
                RetentionExtentKind.JournalPrefix,
                now,
                reason,
                [.. releasedFiles.Select(dependency => dependency.Name)],
                releasedFiles.Sum(dependency => dependency.LengthBytes),
                releasedRecords,
                ChunkSourceDigest(releasedFiles))
            {
                Recording = ReleasedInAll(manifest, chunks.Count, releasedRecords),
            };
            return Publish(
                manifest,
                [.. manifest.Dependencies.Except(releasedFiles)],
                manifest.Boundary,
                record,
                committedUtc,
                [.. releasedFiles],
                now);
        }
    }

    /// <summary>
    /// Publishes a retention generation that stops naming every content chunk and keeps everything else: every journal,
    /// row and derived file, the plan, the ledger, the calibration and the committed boundary (content-v1 §2). Kept content
    /// is restricted evidence a person may give up on its own once a capture has finished, keeping what the capture
    /// recorded about every message. The caller counted the released records, because this store does not read inside
    /// the files it publishes.
    /// </summary>
    /// <remarks>
    /// A capture still recording would find the session changed beneath it and fail its next publication, and one that
    /// stopped without finishing cannot be told from it, so a session whose capture has not finished is refused: its
    /// content goes with its journal chunks instead.
    /// </remarks>
    public RetentionOutcome ReleaseContent(
        long sourceGeneration,
        long releasedRecords,
        string reason,
        DateTimeOffset committedUtc,
        DateTimeOffset? nowUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentOutOfRangeException.ThrowIfNegative(releasedRecords);
        DateTimeOffset now = nowUtc ?? DateTimeOffset.UtcNow;
        lock (gate)
        {
            using FileStream publicationLock = AcquirePublicationLock();
            RequireFreshCurrent();
            SessionManifestV1 manifest = current
                ?? throw new InvalidOperationException("This session has published no generation to retain from.");
            if (manifest.Generation != sourceGeneration)
            {
                throw new InvalidOperationException(
                    $"Generation {sourceGeneration} changed while its content was being measured. Reopen the current "
                    + "generation and release its content again. Nothing was published.");
            }

            StoreDependency[] content =
            [
                .. manifest.Dependencies
                    .Where(dependency => dependency.Kind == StoreDependencyKind.Content)
                    .OrderBy(dependency => dependency.Name, StringComparer.OrdinalIgnoreCase),
            ];
            if (content.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Generation {manifest.Generation} keeps no content, so a content release has nothing to give up. "
                    + "Nothing was published.");
            }

            if (!manifest.Dependencies.Any(dependency => dependency.Kind == StoreDependencyKind.CaptureFinalization))
            {
                throw new InvalidOperationException(
                    "This session's capture has not finished. A capture still recording would find the session changed "
                    + "beneath it and fail, and one that stopped without finishing cannot be told from it, so its content "
                    + "is released with its journal chunks instead, or once it has finished. Nothing was published.");
            }

            var record = new RetentionRecord(
                RetentionExtentKind.Content,
                now,
                reason,
                [.. content.Select(dependency => dependency.Name)],
                content.Sum(dependency => dependency.LengthBytes),
                releasedRecords,
                ChunkSourceDigest(content));
            return Publish(
                manifest,
                [.. manifest.Dependencies.Except(content)],
                manifest.Boundary,
                record,
                committedUtc,
                [.. content],
                now);
        }
    }

    /// <summary>
    /// Publishes a compaction generation (§20.1, ADR-026). The staged segments and dictionaries hold exactly the rows of
    /// the derived files they replace, which the generation stops naming and releases like any retention: a file a
    /// live lease holds stays until that reader lets go (I18). The journals, the plan, the ledger and the committed
    /// boundary are carried unchanged, because only derived files are ever coalesced.
    /// </summary>
    public RetentionOutcome CommitCompaction(
        IReadOnlyList<StoreStagingFile> staged,
        IReadOnlyList<string> replaced,
        string reason,
        long sourceGeneration,
        DateTimeOffset committedUtc,
        long? expectedGeneration = null,
        DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentNullException.ThrowIfNull(replaced);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (staged.Count == 0 || staged.Count > MaximumStagedFiles)
        {
            throw new ArgumentException(
                $"A compaction publishes between 1 and {MaximumStagedFiles} files; this one staged {staged.Count}.",
                nameof(staged));
        }

        DateTimeOffset now = nowUtc ?? DateTimeOffset.UtcNow;
        lock (gate)
        {
            using FileStream publicationLock = AcquirePublicationLock();
            RequireFreshCurrent();
            SessionManifestV1 previous = current
                ?? throw new InvalidOperationException("No published generation exists to compact.");
            if (previous.Generation != sourceGeneration)
            {
                throw new InvalidOperationException(
                    $"Generation {sourceGeneration} changed while its segments were being compacted. Reopen the "
                    + "current generation and compact it again.");
            }

            var released = new List<StoreDependency>(replaced.Count);
            foreach (string name in replaced)
            {
                StoreDependency dependency = previous.Dependencies.FirstOrDefault(candidate =>
                    candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException(
                        $"Generation {previous.Generation} does not name '{name}', so compaction cannot replace it.",
                        nameof(replaced));
                if (dependency.Kind is not (StoreDependencyKind.Segment or StoreDependencyKind.Dictionary)
                    || released.Contains(dependency))
                {
                    throw new ArgumentException(
                        $"'{name}' is not a derived file this compaction can replace once. Compaction replaces "
                        + "segments and dictionaries only; admitted evidence and what describes it are never rewritten.",
                        nameof(replaced));
                }

                released.Add(dependency);
            }

            if (released.Count == 0)
            {
                throw new ArgumentException("A compaction replaces at least one derived file.", nameof(replaced));
            }

            ReleaseIndexesWithSegments(previous, released);
            long nextGeneration = NextAvailableGeneration();
            if (expectedGeneration is { } expected && expected != nextGeneration)
            {
                throw new InvalidOperationException(
                    $"Generation {expected} was staged, but generation {nextGeneration} is now the next "
                    + "unoccupied number. Reopen and stage again; a generation's file names must agree "
                    + "with its manifest.");
            }

            List<StoreDependency> carried = [.. previous.Dependencies.Except(released)];
            var names = new HashSet<string>(carried.Select(dependency => dependency.Name), StringComparer.OrdinalIgnoreCase);
            var published = new List<StoreDependency>(staged.Count);
            foreach (StoreStagingFile file in staged)
            {
                if (file.IsDisposed)
                {
                    throw new InvalidOperationException(
                        $"Staged file '{file.PublishedName}' no longer has an active owner. "
                        + "Disposed staging cannot be published after cleanup may have claimed it.");
                }

                RequireUnpublishedTarget(file.PublishedName);
                StoreDependency dependency = file.Dependency
                    ?? throw new InvalidOperationException(
                        $"Staged file '{file.PublishedName}' was not completed, so its contents are not "
                        + "durable and it cannot be published.");
                if (dependency.Kind is not (StoreDependencyKind.Segment or StoreDependencyKind.Dictionary)
                    || !names.Add(dependency.Name))
                {
                    throw new InvalidOperationException(
                        $"'{dependency.Name}' is not a new segment or dictionary, so a compaction cannot publish it.");
                }

                published.Add(dependency);
            }

            // Publish the immutable files first. Until the manifest exists, none of them is referenced,
            // so a crash here leaves unreferenced files rather than a generation missing a dependency.
            foreach (StoreStagingFile file in staged)
            {
                directory.ReplaceOwnedFile(file.StagingName, file.PublishedName);
            }

            RetentionOutcome outcome = Publish(
                previous,
                [.. carried, .. published],
                previous.Boundary,
                RetentionRecord.ForDerivedFiles(
                    now,
                    reason,
                    [.. released.Select(dependency => dependency.Name)],
                    released.Sum(dependency => dependency.LengthBytes)),
                committedUtc,
                released,
                now,
                nextGeneration);
            foreach (StoreStagingFile file in staged)
            {
                file.MarkPublished();
            }

            return outcome;
        }
    }

    /// <summary>
    /// Publishes an interval release (ADR-043). The generation stops naming the oldest units of the admitted journal - a
    /// recording's oldest chunks, with the content each one kept, or a single journal, which the retained suffix the caller
    /// staged replaces - and the derived files that held their rows, which the staged segments and dictionaries replace
    /// with every row the release keeps, unchanged. An index goes with a released segment, as in any retention. The
    /// caller derived what it releases and keeps, because this store does not read inside the files it publishes; this
    /// step checks that the evidence released is a leading run and that only derived files are replaced, and publishes the
    /// interval's record.
    /// </summary>
    public RetentionOutcome CommitIntervalRelease(
        IReadOnlyList<StoreStagingFile> staged,
        IReadOnlyList<string> replaced,
        IntervalJournalRelease journal,
        ReleasedInterval interval,
        string reason,
        long sourceGeneration,
        DateTimeOffset committedUtc,
        long? expectedGeneration = null,
        DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentNullException.ThrowIfNull(replaced);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(interval);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentOutOfRangeException.ThrowIfNegative(journal.Records);
        if (staged.Count > MaximumStagedFiles)
        {
            throw new ArgumentException(
                $"An interval release publishes at most {MaximumStagedFiles} files; this one staged {staged.Count}.",
                nameof(staged));
        }

        if (interval.Validate() is { } problem)
        {
            throw new ArgumentException(problem, nameof(interval));
        }

        if ((journal.RetainedJournal is null) != (journal.RetainedBoundary is null)
            || (journal.RetainedJournal is not null && journal.Chunks.Count > 0))
        {
            throw new ArgumentException(
                "An interval release gives up a recording's oldest chunks or a single journal's first batches, which a "
                + "retained journal and its boundary replace - one or the other.",
                nameof(journal));
        }

        DateTimeOffset now = nowUtc ?? DateTimeOffset.UtcNow;

        // The pins are held until the release has published, so a pin placed meanwhile is either read here or placed
        // against the generation this publishes, whose boundary then refuses it if it lies before.
        using FileStream pinsLock = AcquirePinsLock();
        lock (gate)
        {
            using FileStream publicationLock = AcquirePublicationLock();
            RequireFreshCurrent();
            SessionManifestV1 previous = current
                ?? throw new InvalidOperationException("This session has published no generation to release from.");
            if (previous.Generation != sourceGeneration)
            {
                throw new InvalidOperationException(
                    $"Generation {sourceGeneration} changed while its oldest interval was being released. Reopen the "
                    + "current generation and release again. Nothing was published.");
            }

            RequirePinsAllowInterval(interval.BoundaryNanoseconds);

            var released = new List<StoreDependency>();
            CommittedBoundary boundary = previous.Boundary;
            StoreDependency? retained = null;
            if (journal.Chunks.Count > 0)
            {
                released.AddRange(LeadingChunks(previous, journal.Chunks));
            }
            else if (journal.RetainedJournal is { } retainedJournal)
            {
                StoreDependency[] journals =
                [
                    .. previous.Dependencies.Where(dependency => dependency.Kind == StoreDependencyKind.Journal),
                ];
                if (journals.Length != 1)
                {
                    throw new ArgumentException(
                        $"Generation {previous.Generation} names {journals.Length} journals. A retained journal replaces a "
                        + "single one; a recording gives up its oldest chunks instead.",
                        nameof(journal));
                }

                if (previous.Dependencies.Any(dependency => dependency.Kind == StoreDependencyKind.Content))
                {
                    throw new InvalidOperationException(
                        "This session keeps content beside its journal. Rewriting the journal's first batches would leave "
                        + "content whose records it no longer holds, so its content is released on its own first "
                        + "(content-v1 §2). Nothing was published.");
                }

                retained = retainedJournal.Dependency
                    ?? throw new InvalidOperationException(
                        "The retained journal was not completed, so its contents are not durable and it cannot be published.");
                if (retainedJournal.IsDisposed)
                {
                    throw new InvalidOperationException(
                        "The retained journal no longer has an active staging owner and cannot be published.");
                }

                boundary = journal.RetainedBoundary!;
                if (!boundary.JournalName.Equals(retained.Name, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        "The retained boundary names another journal than the one staged to replace the released batches.",
                        nameof(journal));
                }

                released.Add(journals[0]);
            }

            foreach (string name in replaced)
            {
                StoreDependency dependency = previous.Dependencies.FirstOrDefault(candidate =>
                    candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException(
                        $"Generation {previous.Generation} does not name '{name}', so an interval release cannot replace it.",
                        nameof(replaced));
                if (dependency.Kind is not (StoreDependencyKind.Segment or StoreDependencyKind.Dictionary)
                    || released.Contains(dependency))
                {
                    throw new ArgumentException(
                        $"'{name}' is not a derived file this release can replace once. An interval release replaces "
                        + "segments and dictionaries, and gives up evidence only as whole journal units.",
                        nameof(replaced));
                }

                released.Add(dependency);
            }

            if (released.Count == 0)
            {
                throw new ArgumentException(
                    "An interval release gives up journal units or replaces derived files; this one names neither.",
                    nameof(replaced));
            }

            ReleaseIndexesWithSegments(previous, released);
            long nextGeneration = NextAvailableGeneration();
            if (expectedGeneration is { } expected && expected != nextGeneration)
            {
                throw new InvalidOperationException(
                    $"Generation {expected} was staged, but generation {nextGeneration} is now the next unoccupied number. "
                    + "Reopen and stage again; a generation's file names must agree with its manifest.");
            }

            if (journal.RetainedJournal is { } suffix)
            {
                if (!suffix.PublishedName.Equals(SegmentFormatV1.JournalFileName(nextGeneration), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "The retained journal was staged for a generation that is no longer available. Release again "
                        + "against a newly staged generation.");
                }

                RequireUnpublishedTarget(suffix.PublishedName);
            }

            List<StoreDependency> carried = [.. previous.Dependencies.Except(released)];
            var names = new HashSet<string>(carried.Select(dependency => dependency.Name), StringComparer.OrdinalIgnoreCase);
            if (retained is not null && !names.Add(retained.Name))
            {
                throw new InvalidOperationException($"'{retained.Name}' is already named, so it cannot be the retained journal.");
            }

            var published = new List<StoreDependency>(staged.Count);
            foreach (StoreStagingFile file in staged)
            {
                if (file.IsDisposed)
                {
                    throw new InvalidOperationException(
                        $"Staged file '{file.PublishedName}' no longer has an active owner. "
                        + "Disposed staging cannot be published after cleanup may have claimed it.");
                }

                RequireUnpublishedTarget(file.PublishedName);
                StoreDependency dependency = file.Dependency
                    ?? throw new InvalidOperationException(
                        $"Staged file '{file.PublishedName}' was not completed, so its contents are not "
                        + "durable and it cannot be published.");
                if (dependency.Kind is not (StoreDependencyKind.Segment or StoreDependencyKind.Dictionary)
                    || !names.Add(dependency.Name))
                {
                    throw new InvalidOperationException(
                        $"'{dependency.Name}' is not a new segment or dictionary, so an interval release cannot publish it.");
                }

                published.Add(dependency);
            }

            var record = new RetentionRecord(
                RetentionExtentKind.Interval,
                now,
                reason,
                [.. released.Select(dependency => dependency.Name)],
                released.Sum(dependency => dependency.LengthBytes),
                journal.Records,
                ChunkSourceDigest(released))
            {
                Interval = interval,
            };

            // The retained journal is published first, as a commit publishes its journal before the files derived from it;
            // until the manifest exists none of them is referenced, so a crash leaves unreferenced files and the previous
            // generation current.
            if (journal.RetainedJournal is { } written)
            {
                directory.ReplaceOwnedFile(written.StagingName, written.PublishedName);
            }

            foreach (StoreStagingFile file in staged)
            {
                directory.ReplaceOwnedFile(file.StagingName, file.PublishedName);
            }

            RetentionOutcome outcome = Publish(
                previous,
                [.. carried, .. retained is null ? [] : new[] { retained }, .. published],
                boundary,
                record,
                committedUtc,
                released,
                now,
                nextGeneration);
            journal.RetainedJournal?.MarkPublished();
            foreach (StoreStagingFile file in staged)
            {
                file.MarkPublished();
            }

            return outcome;
        }
    }

    /// <summary>
    /// The oldest chunks a release names, with the content each one kept (ADR-024, content-v1 §2): a leading run of the
    /// chunks the generation names, oldest first, which keeps at least the newest - the one the committed boundary names.
    /// </summary>
    private static List<StoreDependency> LeadingChunks(SessionManifestV1 manifest, IReadOnlyList<string> chunks)
    {
        StoreDependency[] journals =
        [
            .. manifest.Dependencies
                .Where(dependency => dependency.Kind == StoreDependencyKind.Journal)
                .OrderBy(dependency => dependency.Name, StringComparer.OrdinalIgnoreCase),
        ];
        if (chunks.Count >= journals.Length)
        {
            throw new ArgumentException(
                $"Generation {manifest.Generation} names {journals.Length} journal chunks. A release gives up at most all "
                + "but the newest, which the committed boundary names.",
                nameof(chunks));
        }

        StoreDependency[] released = journals[..chunks.Count];
        if (!released.Select(dependency => dependency.Name).SequenceEqual(chunks, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "A release gives up the oldest chunks, in the order they were recorded: "
                + $"{string.Join(", ", released.Select(dependency => dependency.Name))}. Releasing any other set would "
                + "leave the retained chunks describing a capture with a hole in it.",
                nameof(chunks));
        }

        if (released.Any(dependency => dependency.Name.Equals(manifest.Boundary.JournalName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "The committed boundary names one of these chunks. A generation cannot keep a boundary on evidence it no "
                + "longer holds.",
                nameof(chunks));
        }

        HashSet<long> generations = [.. released.Select(dependency => SegmentFormatV1.GenerationOfJournal(dependency.Name)
            ?? throw new InvalidOperationException($"'{dependency.Name}' is not named as a journal chunk is."))];
        return
        [
            .. released,
            .. manifest.Dependencies.Where(dependency => dependency.Kind == StoreDependencyKind.Content
                && ContentChunkV1.GenerationOf(dependency.Name) is { } generation && generations.Contains(generation)),
        ];
    }

    /// <summary>
    /// The recording's chunks and records given up in all once this chunk release gives up <paramref name="chunks"/> more,
    /// holding <paramref name="records"/> (ADR-048): the previous chunk release's statement and these, or these alone when
    /// it is the first. Null when an earlier release gave up chunks without stating them in all - a chunk release before
    /// this statement, a journal prefix rewritten or an interval released - since then no count is known.
    /// </summary>
    private static ReleasedRecording? ReleasedInAll(SessionManifestV1 manifest, int chunks, long records)
    {
        if (manifest.LatestRelease(RetentionExtentKind.Interval) is not null)
        {
            return null;
        }

        return manifest.LatestRelease(RetentionExtentKind.JournalPrefix) is not { } earlier
            ? new(chunks, records)
            : earlier.Record.Recording is { } stated
                ? new(checked(stated.Chunks + chunks), checked(stated.Records + records))
                : null;
    }

    /// <summary>
    /// What a chunk release started from. Each released file's bytes are gone, so the extent is identified by the
    /// digest of their dependency lines, <c>name|length|digest</c> joined by a line feed and oldest first. Each line is
    /// the exact entry the superseded manifest held for its chunk.
    /// </summary>
    public static string ChunkSourceDigest(IReadOnlyList<StoreDependency> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        string canonical = string.Join(
            '\n',
            chunks.Select(chunk => string.Create(
                CultureInfo.InvariantCulture,
                $"{chunk.Name}|{chunk.LengthBytes}|{chunk.Digest}")));
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>
    /// Removes files no generation references and no live lease holds. It is separate from opening on
    /// purpose: a file that is unreferenced now may be a dependency of a generation whose publication was
    /// interrupted, and deleting it would turn a recoverable interruption into lost evidence. Staging files
    /// are reported but skipped even by this explicit sweep: another process may not have committed them yet.
    /// </summary>
    public IReadOnlyList<string> RemoveOrphans(DateTimeOffset? nowUtc = null)
    {
        DateTimeOffset now = nowUtc ?? DateTimeOffset.UtcNow;
        lock (gate)
        {
            using FileStream publicationLock = AcquirePublicationLock();
            RequireFreshCurrent();
            Sweep(now);
            // A reader in another process may hold an older generation. Without its shared marker
            // an orphan sweep could erase a dependency that its in-memory lease still names.
            using FileStream? removalLock = TryAcquireRemovalLock();
            if (removalLock is null)
            {
                return [];
            }

            var removed = new List<string>();
            foreach (string name in Orphans(directory, current))
            {
                if (IsLeased(name, now))
                {
                    continue;
                }

                if (directory.RemoveOwnedFile(name))
                {
                    removed.Add(name);
                }
            }

            return removed;
        }
    }

    /// <summary>
    /// Removes the manifests of generations that were once current and that no pointer names any more (store-v1 §9).
    /// Each lists every dependency its generation named, so a live session that kept one per publication would hold
    /// manifest bytes growing with the square of its length.
    /// </summary>
    /// <remarks>
    /// A generation was current once exactly when a later one names it as its previous generation, so the walk follows
    /// that chain back from the current generation. A manifest an interrupted publication left was never current, and
    /// no chain reaches it, whatever its number: it stays an orphan for recovery to judge. Only manifests go, never a
    /// dependency, and only while no reader anywhere holds the evidence guard and no lease here names them; otherwise a
    /// later publication tries again. It never throws: cleanup is optional, preserving evidence is not.
    /// </remarks>
    /// <param name="removalLockHeld">Whether the caller already holds the evidence guard exclusively, as retention does.</param>
    /// <returns>How many manifests were removed.</returns>
    private int RemoveSupersededManifests(DateTimeOffset now, bool removalLockHeld = false)
    {
        try
        {
            if (current is not { } kept)
            {
                return 0;
            }

            var named = new HashSet<long> { kept.Generation };
            if (Read<SessionPointerV1>(directory, SessionPointerV1.PreviousFileName) is { } previous
                && previous.Validate() is null)
            {
                _ = named.Add(previous.Generation);
            }

            // Manifests are immutable, so the chain is read before the guard is taken; a reader waits only for deletion.
            var superseded = new List<string>();
            long? generation = kept.PreviousGeneration is { } first && first < kept.Generation ? first : null;
            while (generation is { } older)
            {
                string name = SessionManifestV1.FileNameFor(older);
                if (Read<SessionManifestV1>(directory, name) is not { } manifest
                    || manifest.Validate() is not null
                    || manifest.Generation != older
                    || manifest.SessionId != kept.SessionId)
                {
                    break;
                }

                if (!named.Contains(older))
                {
                    superseded.Add(name);
                }

                generation = manifest.PreviousGeneration is { } prior && prior < older ? prior : null;
            }

            if (superseded.Count == 0)
            {
                return 0;
            }

            using FileStream? removalLock = removalLockHeld ? null : TryAcquireRemovalLock();
            if (!removalLockHeld && removalLock is null)
            {
                return 0;
            }

            Sweep(now);
            return superseded.Count(name => !IsLeased(name, now) && directory.RemoveOwnedFile(name));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentOutOfRangeException)
        {
            return 0;
        }
    }

    /// <summary>Inspects staging without changing it. A preview is advisory; cleanup repeats every check.</summary>
    public StagingCleanupReport PreviewStagingCleanup()
    {
        lock (gate)
        {
            return InspectStaging(remove: false, CancellationToken.None);
        }
    }

    /// <summary>
    /// Removes only staging whose ownership marker can be held while deleting. Unmarked legacy files
    /// and files an active writer still owns remain untouched.
    /// </summary>
    public StagingCleanupReport CleanupAbandonedStaging(
        string? expectedCandidateDigest = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            using FileStream publicationLock = AcquirePublicationLock();
            RequireFreshCurrent();
            if (expectedCandidateDigest is null)
            {
                return InspectStaging(remove: true, cancellationToken);
            }

            StagingCleanupReport preview = InspectStaging(remove: false, cancellationToken);
            if (!string.Equals(preview.CandidateDigest, expectedCandidateDigest, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The abandoned staging set changed after the preview. Review it again before confirming "
                    + "cleanup; newly abandoned files are never added to an older decision.");
            }

            var reviewed = new HashSet<string>(
                preview.AbandonedFiles.Concat(preview.MarkerOnlyFiles),
                StringComparer.OrdinalIgnoreCase);
            return InspectStaging(remove: true, cancellationToken, reviewed);
        }
    }

    /// <summary>
    /// Explicitly re-points a damaged current pointer to the fully verified last-known-good generation.
    /// The original pointer bytes are preserved first. This is never part of ordinary open or query.
    /// </summary>
    public PointerRepairOutcome RepairCurrentPointer(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            using FileStream publicationLock = AcquirePublicationLock();
            (SessionManifestV1? manifest, bool rolledBack, string? reason) = Acquire(directory, measurements);
            if (manifest is null || manifest.SessionId != SessionId)
            {
                throw new InvalidDataException("Pointer repair needs a verified generation of this session.");
            }

            if (manifest.Generation != publicationBaseline?.Generation
                || !string.Equals(manifest.Digest, publicationBaseline.Digest, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The verified recovery generation changed after this store was opened. Reopen and review "
                    + "the current recovery state before confirming repair.");
            }

            if (!rolledBack)
            {
                return new(false, manifest.Generation, null, null);
            }

            cancellationToken.ThrowIfCancellationRequested();
            string? backupName = null;
            if (Exists(directory, SessionPointerV1.FileName))
            {
                backupName = $"recovery-pointer-{Guid.NewGuid():N}.json";
                using FileStream source = directory.OpenOwnedFile(
                    SessionPointerV1.FileName, FileMode.Open, FileAccess.Read,
                    FileShare.Read, FileOptions.SequentialScan);
                if (source.Length > 1024 * 1024)
                {
                    throw new InvalidDataException(
                        "The damaged current pointer exceeds 1 MiB. Preserve it manually before repair; "
                        + "this command will not copy an unbounded file or replace it.");
                }

                using FileStream backup = directory.OpenOwnedFile(
                    backupName, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, FileOptions.WriteThrough);
                source.CopyTo(backup);
                backup.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Write(directory, SessionPointerV1.FileName, SessionPointerV1.For(manifest), SessionManifestV1.Json);
            Unverified? verificationProblem = TryAcquire(directory, SessionPointerV1.FileName, out SessionManifestV1? repaired, measurements);
            if (verificationProblem is not null || repaired?.Digest != manifest.Digest)
            {
                throw new IOException(
                    $"Pointer repair was written but did not verify ({verificationProblem?.Reason ?? "generation mismatch"}). "
                    + "Do not publish until the session is inspected again.");
            }

            segmentReaders.Prune(manifest);
            current = manifest;
            publicationBaseline = manifest;
            return new(true, manifest.Generation, reason, backupName);
        }
    }

    /// <summary>
    /// Publishes a generation whose dependency list is given rather than added to, which is what a retention
    /// generation is. Every retained dependency is still re-measured before it is named, so a retention
    /// cannot publish a generation over a dependency that changed.
    /// </summary>
    private RetentionOutcome Publish(
        SessionManifestV1 previousManifest,
        IReadOnlyList<StoreDependency> dependencies,
        CommittedBoundary boundary,
        RetentionRecord record,
        DateTimeOffset committedUtc,
        List<StoreDependency> released,
        DateTimeOffset now,
        long? reservedGeneration = null)
    {
        long nextGeneration = reservedGeneration ?? NextAvailableGeneration();
        RequireUnpublishedTarget(SessionManifestV1.FileNameFor(nextGeneration));
        SessionManifestV1 manifest = SessionManifestV1.Create(
            nextGeneration,
            SessionId,
            committedUtc,
            SourceIdentity,
            previousManifest.Generation,
            boundary,
            dependencies,
            record,
            SessionManifestV1.ReleasesCarriedFrom(previousManifest, record));
        Unverified? unverifiable = VerifyDependencies(directory, manifest, measurements);
        if (unverifiable is not null)
        {
            throw new IOException($"Generation {manifest.Generation} was not published: {unverifiable}");
        }

        Write(directory, SessionManifestV1.FileNameFor(manifest.Generation), manifest, SessionManifestV1.Json);
        RetainPreviousPointer(directory);
        Write(directory, SessionPointerV1.FileName, SessionPointerV1.For(manifest), SessionManifestV1.Json);
        segmentReaders.Prune(manifest);
        current = manifest;
        publicationBaseline = manifest;

        // The generation has stopped naming the released files. Whether their bytes go now depends on
        // whether a reader is still holding them, and a reader that is wins (I18).
        Sweep(now);
        using FileStream? removalLock = TryAcquireRemovalLock();
        var removed = new List<string>();
        var heldByLease = new List<string>();
        long reclaimed = 0;
        foreach (StoreDependency dependency in released)
        {
            if (removalLock is null || IsLeased(dependency.Name, now))
            {
                heldByLease.Add(dependency.Name);
                continue;
            }

            if (directory.RemoveOwnedFile(dependency.Name))
            {
                removed.Add(dependency.Name);
                reclaimed += dependency.LengthBytes;
            }
        }

        // A retention has made the previous generation incomplete - now, or as soon as the lease still
        // holding its files lets go - so the retained last-known-good is re-aimed at this one. A pointer that
        // named a generation missing a dependency would promise a rollback that cannot be performed, which is
        // worse than naming no earlier one. The retention generation was re-measured before it was named, so
        // it is a complete last-known-good.
        if (released.Count > 0)
        {
            Write(directory, SessionPointerV1.PreviousFileName, SessionPointerV1.For(manifest), SessionManifestV1.Json);
        }

        if (removalLock is not null)
        {
            _ = RemoveSupersededManifests(now, removalLockHeld: true);
        }

        return new(manifest, [.. released.Select(dependency => dependency.Name)], removed, heldByLease, reclaimed);
    }

    private bool IsLeased(string name, DateTimeOffset now) =>
        leases.Values.Any(lease => lease.Holds(name, now));

    /// <summary>
    /// An index describes the segments of the generation it was derived from, so a publication that stops naming a
    /// segment stops naming every index as well, in the same retention record. No index outlives what it describes
    /// (§20.2, derivation-checkpoint-v1 §2).
    /// </summary>
    private static void ReleaseIndexesWithSegments(SessionManifestV1 manifest, List<StoreDependency> released)
    {
        if (!released.Any(dependency => dependency.Kind == StoreDependencyKind.Segment))
        {
            return;
        }

        released.AddRange(manifest.Dependencies.Where(dependency =>
            dependency.Kind == StoreDependencyKind.Index && !released.Contains(dependency)));
    }

    private StagingCleanupReport InspectStaging(
        bool remove,
        CancellationToken cancellationToken,
        HashSet<string>? reviewed = null)
    {
        IReadOnlyList<string> files = List(directory);
        var names = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        var abandoned = new List<string>();
        var active = new List<string>();
        var unmarked = new List<string>();
        var markerOnly = new List<string>();
        var removed = new List<string>();

        foreach (string name in files.Where(IsManagedStagingFile))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string markerName = name[..^StagingSuffix.Length] + StagingOwnershipSuffix;
            if (!names.Contains(markerName))
            {
                unmarked.Add(name);
                continue;
            }

            using FileStream? marker = TryOpenStagingMarker(markerName);
            if (marker is null)
            {
                active.Add(name);
                continue;
            }

            abandoned.Add(name);
            if (remove && (reviewed is null || reviewed.Contains(name)))
            {
                if (directory.RemoveOwnedFile(name))
                {
                    removed.Add(name);
                }

                if (directory.RemoveOwnedFile(markerName))
                {
                    removed.Add(markerName);
                }
            }
        }

        foreach (string name in files.Where(IsManagedStagingMarker))
        {
            string stagedName = name[..^StagingOwnershipSuffix.Length] + StagingSuffix;
            if (names.Contains(stagedName))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            using FileStream? marker = TryOpenStagingMarker(name);
            if (marker is null)
            {
                active.Add(name);
                continue;
            }

            markerOnly.Add(name);
            if (remove && (reviewed is null || reviewed.Contains(name)) && directory.RemoveOwnedFile(name))
            {
                removed.Add(name);
            }
        }

        return new(abandoned, active, unmarked, markerOnly, removed,
            CandidateDigest(abandoned.Concat(markerOnly)));
    }

    private static string CandidateDigest(IEnumerable<string> names)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("InterCat.Store.StagingCleanup.v1\n"u8);
        foreach (string name in names.Order(StringComparer.OrdinalIgnoreCase))
        {
            hash.AppendData(Encoding.ASCII.GetBytes(name.ToLowerInvariant()));
            hash.AppendData("\n"u8);
        }

        return "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private FileStream? TryOpenStagingMarker(string name)
    {
        try
        {
            return directory.OpenOwnedFile(
                name, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, FileOptions.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsManagedStagingFile(string name) => IsManagedStagingName(name, StagingSuffix);

    private static bool IsManagedStagingMarker(string name) => IsManagedStagingName(name, StagingOwnershipSuffix);

    private static bool IsManagedStagingName(string name, string suffix) =>
        name.StartsWith(StagingPrefix, StringComparison.OrdinalIgnoreCase)
        && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
        && name.Length == StagingPrefix.Length + 32 + suffix.Length
        && Guid.TryParseExact(name.AsSpan(StagingPrefix.Length, 32), "N", out _);

    private long NextAvailableGeneration()
    {
        IReadOnlyList<string> files = List(directory);
        for (long candidate = (current?.Generation ?? 0) + 1; candidate <= 9_999_999_999; candidate++)
        {
            string token = string.Create(CultureInfo.InvariantCulture, $"-{candidate:D10}");
            if (!files.Any(name => !IsStaging(name) && name.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("The session has exhausted its generation-number space.");
    }

    /// <summary>Forgets released and expired leases. An expired lease is not revivable.</summary>
    private void Sweep(DateTimeOffset now)
    {
        foreach (Guid id in leases
            .Where(entry => entry.Value.IsReleased || entry.Value.HasExpiredAt(now))
            .Select(entry => entry.Key)
            .ToList())
        {
            EvidenceLease lease = leases[id];
            _ = leases.Remove(id);
            lease.Dispose();
        }
    }

    private void Release(EvidenceLease lease)
    {
        lock (gate)
        {
            _ = leases.Remove(lease.Id);
        }

        // A query may have read many columns of the cached readers; what they hold comes back within the budget when it
        // ends, down to the columns every projection reads again.
        _ = segmentReaders.Trim(ResidentColumns);
    }

    /// <summary>
    /// The columns a cached reader keeps when the cache trims: session time and mechanism, which every projection's time
    /// tiles read (S4). Every other column is read, and checked, again when a query next asks for it.
    /// </summary>
    internal static IReadOnlySet<SegmentColumnId> ResidentColumns { get; } =
        new HashSet<SegmentColumnId> { SegmentColumnId.SessionRelativeTicks, SegmentColumnId.Mechanism };

    /// <param name="verified">
    /// A manifest this instance already verified. When the current pointer still names it, its dependencies are
    /// re-measured but the manifest is not read and digested again: the pointer's digest pins every byte of it.
    /// </param>
    private static (SessionManifestV1? Manifest, bool RolledBack, string? Reason) Acquire(
        IOwnedDirectory directory,
        DependencyMeasurements? measurements,
        SessionManifestV1? verified = null,
        bool hashContents = true)
    {
        Unverified? currentProblem = TryAcquire(
            directory, SessionPointerV1.FileName, out SessionManifestV1? manifest, measurements, verified, hashContents);
        if (currentProblem is null)
        {
            return (manifest, false, null);
        }

        Unverified? previousProblem = TryAcquire(
            directory, SessionPointerV1.PreviousFileName, out manifest, measurements, hashContents: hashContents);
        if (previousProblem is null && manifest is not null)
        {
            return (manifest, true, currentProblem.Reason);
        }

        // A session whose pointer names nothing is empty; one whose pointer names something that does
        // not verify, with no usable last-known-good, is refused rather than silently reset.
        return Exists(directory, SessionPointerV1.FileName) || Exists(directory, SessionPointerV1.PreviousFileName)
            ? throw new InvalidDataException(NoGenerationVerifies(directory, currentProblem, previousProblem))
            : (null, false, null);
    }

    /// <summary>
    /// What a reader says of a session no generation of which verifies. A generation and the one kept to fall back to
    /// share most of their files, so a file both need is named once, and what is wrong with it is said once.
    /// </summary>
    private static string NoGenerationVerifies(IOwnedDirectory directory, Unverified current, Unverified? previous)
    {
        string fallback = previous is null || !Exists(directory, SessionPointerV1.PreviousFileName)
            ? "No earlier generation is kept to fall back to."
            : string.Equals(previous.File, current.File, StringComparison.OrdinalIgnoreCase)
                ? "The generation kept to fall back to needs the same file."
                : $"The generation kept to fall back to does not verify either: {previous.Reason}.";
        return $"This session cannot be read, because no generation of it verifies: {current.Reason}. {fallback} "
            + "Its files are left as they are.";
    }

    /// <summary>Why a generation did not verify, and the file that kept it from verifying.</summary>
    private sealed record Unverified(string File, string Reason)
    {
        public override string ToString() => Reason;
    }

    private static Unverified? TryAcquire(
        IOwnedDirectory directory,
        string pointerName,
        out SessionManifestV1? manifest,
        DependencyMeasurements? measurements,
        SessionManifestV1? verified = null,
        bool hashContents = true)
    {
        manifest = null;
        SessionPointerV1? pointer = Read<SessionPointerV1>(directory, pointerName);
        if (pointer is null)
        {
            return new(pointerName, $"'{pointerName}' is missing or unreadable");
        }

        string? problem = pointer.Validate();
        if (problem is not null)
        {
            return new(pointerName, problem);
        }

        bool unchanged = verified is not null
            && verified.Generation == pointer.Generation
            && string.Equals(verified.Digest, pointer.ManifestDigest, StringComparison.Ordinal)
            && string.Equals(
                SessionManifestV1.FileNameFor(verified.Generation), pointer.ManifestName, StringComparison.OrdinalIgnoreCase);
        SessionManifestV1? candidate = unchanged ? verified : Read<SessionManifestV1>(directory, pointer.ManifestName);
        if (candidate is null)
        {
            return new(pointer.ManifestName, $"generation {pointer.Generation} names manifest '{pointer.ManifestName}', "
                + "which is missing or unreadable");
        }

        problem = unchanged ? null : candidate.Validate() ?? candidate.VerifyDigest();
        if (problem is not null)
        {
            return new(pointer.ManifestName, problem);
        }

        if (!string.Equals(candidate.Digest, pointer.ManifestDigest, StringComparison.Ordinal))
        {
            return new(pointer.ManifestName, $"the pointer expects manifest digest {pointer.ManifestDigest} but "
                + $"'{pointer.ManifestName}' carries {candidate.Digest}");
        }

        if (candidate.Generation != pointer.Generation)
        {
            return new(pointer.ManifestName, $"the pointer names generation {pointer.Generation} but its manifest is "
                + $"generation {candidate.Generation}");
        }

        Unverified? unverified = VerifyDependencies(directory, candidate, measurements, hashContents);
        if (unverified is not null)
        {
            return unverified;
        }

        manifest = candidate;
        return null;
    }

    /// <summary>
    /// Re-measures every dependency a generation names: each file's presence and length is checked, and its bytes are
    /// hashed unless these measurements already hashed the same immutable file, unchanged in length and last-write
    /// time since. Without that, every commit and every lease would hash the whole session, and a long live recording
    /// would spend more time hashing than recording (store-v1 §3).
    /// </summary>
    /// <remarks>
    /// One listing of the directory confirms a file these measurements already hashed: listed, not a reparse point,
    /// and with the length and last-write time they recorded. Every other file is opened and checked from its handle.
    /// A live session names every journal chunk it published, so one open per file made each lease cost more as the
    /// capture grew: 33 ms at 292 chunks (ADR-025, revision 129).
    /// </remarks>
    /// <param name="hashContents">
    /// False for a viewer's first open: a segment, dictionary or journal present with its recorded length is recorded as
    /// listed and hashed later, by <see cref="VerifyContents"/>, so opening costs one listing rather than every byte of
    /// the session (S1). Every other dependency is small and carries no checksum of its own, so it is hashed now.
    /// </param>
    private static Unverified? VerifyDependencies(
        IOwnedDirectory directory,
        SessionManifestV1 manifest,
        DependencyMeasurements? measurements,
        bool hashContents = true)
    {
        IReadOnlyDictionary<string, OwnedFileFacts>? listed = measurements is { IsEmpty: false } || !hashContents
            ? directory.DescribeOwnedFiles()
            : null;
        byte[]? buffer = null;
        foreach (StoreDependency dependency in manifest.Dependencies)
        {
            if (listed is not null
                && listed.TryGetValue(dependency.Name, out OwnedFileFacts facts)
                && !facts.IsReparsePoint
                && facts.LengthBytes == dependency.LengthBytes)
            {
                if (measurements?.Holds(dependency, facts.LastWriteUtcTicks) == true)
                {
                    continue;
                }

                if (!hashContents && measurements is not null && ChecksItself(dependency.Kind))
                {
                    measurements.RecordListed(dependency, facts.LastWriteUtcTicks);
                    continue;
                }
            }

            try
            {
                using FileStream stream = directory.OpenOwnedFile(
                    dependency.Name,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    FileOptions.SequentialScan);
                if (stream.Length != dependency.LengthBytes)
                {
                    return new(dependency.Name, dependency.LengthMismatch(stream.Length, manifest.Generation));
                }

                long lastWrite = File.GetLastWriteTimeUtc(stream.SafeFileHandle).Ticks;
                if (measurements?.Holds(dependency, lastWrite) == true)
                {
                    continue;
                }

                if (!hashContents && measurements is not null && ChecksItself(dependency.Kind))
                {
                    measurements.RecordListed(dependency, lastWrite);
                    continue;
                }

                buffer ??= new byte[HashBufferBytes];
                string? problem = DigestProblem(stream, dependency, manifest.Generation, buffer, CancellationToken.None);
                if (problem is not null)
                {
                    return new(dependency.Name, problem);
                }

                measurements?.Record(dependency, lastWrite);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new(dependency.Name, Unreadable(dependency, exception, manifest.Generation));
            }
        }

        return null;
    }

    private static (IReadOnlyList<string> Removed, IReadOnlyList<string> Orphans, long OrphanBytes) Sweep(
        IOwnedDirectory directory,
        SessionManifestV1? manifest)
    {
        var removed = new List<string>();
        var orphans = new List<string>();
        long orphanBytes = 0;
        HashSet<string> referenced = Referenced(directory, manifest);
        foreach (string name in List(directory))
        {
            if (referenced.Contains(name))
            {
                continue;
            }

            orphans.Add(name);
            try
            {
                orphanBytes += new FileInfo(Path.Combine(directory.Path, name)).Length;
            }
            catch (IOException)
            {
                // An orphan whose length cannot be read is still reported; its size is simply unknown.
            }
        }

        return (removed, orphans, orphanBytes);
    }

    private static IEnumerable<string> Orphans(IOwnedDirectory directory, SessionManifestV1? manifest)
    {
        HashSet<string> referenced = Referenced(directory, manifest);
        return List(directory).Where(name => !IsStaging(name)
            && !IsManagedStagingMarker(name)
            && !referenced.Contains(name));
    }

    /// <summary>
    /// Every file the session still needs: both pointers, the locks, the pins a person placed, and the manifest and
    /// dependencies of each generation a pointer names.
    /// </summary>
    /// <remarks>
    /// The retained last-known-good counts. It is the whole point of keeping it: a sweep that treated the
    /// generation it names as unreferenced would let <c>RemoveOrphans</c> delete the one thing a rollback
    /// needs, and the next torn pointer would turn a recoverable interruption into a refused session.
    /// </remarks>
    private static HashSet<string> Referenced(IOwnedDirectory directory, SessionManifestV1? manifest)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            SessionPointerV1.FileName,
            SessionPointerV1.PreviousFileName,
            PublicationLockFileName,
            EvidenceLeaseLockFileName,
            RetentionPinsV1.FileName,
            RetentionPinsV1.LockFileName,
        };
        Add(referenced, manifest);
        if (Read<SessionPointerV1>(directory, SessionPointerV1.PreviousFileName) is { } previous
            && previous.Validate() is null)
        {
            referenced.Add(previous.ManifestName);
            Add(referenced, Read<SessionManifestV1>(directory, previous.ManifestName));
        }

        return referenced;
    }

    private static void Add(HashSet<string> referenced, SessionManifestV1? manifest)
    {
        if (manifest is null || manifest.Validate() is not null)
        {
            return;
        }

        referenced.Add(SessionManifestV1.FileNameFor(manifest.Generation));
        foreach (StoreDependency dependency in manifest.Dependencies)
        {
            referenced.Add(dependency.Name);
        }
    }

    private static bool IsStaging(string name) =>
        name.StartsWith(StagingPrefix, StringComparison.OrdinalIgnoreCase)
        && name.EndsWith(StagingSuffix, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> List(IOwnedDirectory directory) =>
        directory is LocalOwnedDirectory local
            ? local.ListOwnedFiles()
            :
            [
                .. Directory.EnumerateFiles(directory.Path)
                    .Select(Path.GetFileName)
                    .Where(name => name is not null && OwnedFileName.Validate(name) is null)
                    .Select(name => name!)
                    .Order(StringComparer.Ordinal),
            ];

    private static bool Exists(IOwnedDirectory directory, string name) =>
        File.Exists(Path.Combine(directory.Path, name));

    private FileStream AcquirePublicationLock()
    {
        try
        {
            return directory.OpenOwnedFile(
                PublicationLockFileName,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                FileOptions.WriteThrough);
        }
        catch (IOException exception)
        {
            throw new IOException(
                "Could not acquire the session's exclusive publication lock. Another process may be "
                + "committing or retaining this session; retry after it finishes.", exception);
        }
    }

    private FileStream? TryAcquireRemovalLock()
    {
        try
        {
            return directory.OpenOwnedFile(
                EvidenceLeaseLockFileName,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                FileOptions.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Any shared reader, including one in another process, wins. An unrelated I/O refusal
            // also fails closed: physical cleanup is optional, preserving evidence is not.
            return null;
        }
    }

    private FileStream AcquireEvidenceReadHold()
    {
        // A writer holds the guard exclusively only while it removes files no reader can reach, which takes
        // milliseconds; a reader waits that out rather than failing its lease.
        long waitStarted = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                return directory.OpenOwnedFile(
                    EvidenceLeaseLockFileName,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    FileOptions.None);
            }
            catch (FileNotFoundException)
            {
                // A folder no generation was published in is no session to establish a guard in: a reader that only
                // looks, at a folder someone chose by mistake, writes nothing there.
                if (!Exists(directory, SessionPointerV1.FileName) && !Exists(directory, SessionPointerV1.PreviousFileName))
                {
                    throw new NoSessionException();
                }

                // Older sessions predate the guard. A writable local reader can establish it;
                // a read-only viewer must ask the owner to upgrade the session first.
                try
                {
                    return directory.OpenOwnedFile(
                        EvidenceLeaseLockFileName,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.ReadWrite,
                        FileOptions.None);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new IOException(
                        "This older session has no evidence lease guard and the reader could not create one. "
                        + "Have the session owner establish the guard with write access before viewing "
                        + "concurrently with retention.",
                        exception);
                }
            }
            catch (IOException exception) when (exception is not DirectoryNotFoundException
                && Stopwatch.GetElapsedTime(waitStarted) < EvidenceGuardWait)
            {
                Thread.Sleep(5);
            }
            catch (IOException exception)
            {
                throw new IOException(
                    "Could not acquire the session's shared evidence lease guard. Retention may be removing "
                    + "released files; retry once it finishes.", exception);
            }
        }
    }

    private void RequireFreshCurrent()
    {
        (SessionManifestV1? disk, bool rolledBack, _) = Acquire(directory, measurements);
        if (rolledBack)
        {
            throw new InvalidOperationException(
                "The current pointer failed verification and opening selected last-known-good. Publication "
                + "is refused until recovery resolves that pointer; it cannot be treated as a fresh branch.");
        }

        if (disk?.Generation != publicationBaseline?.Generation
            || !string.Equals(disk?.Digest, publicationBaseline?.Digest, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The session's published generation changed after this store instance opened. Reopen the "
                + "session before committing or retaining; an old writer cannot overwrite a newer one.");
        }
    }

    private void RequireUnpublishedTarget(string name)
    {
        if (Exists(directory, name))
        {
            throw new InvalidOperationException(
                $"'{name}' already exists in the session root. A published or interrupted generation's "
                + "immutable file is never overwritten; inspect the orphan before retrying.");
        }
    }

    private static void RetainPreviousPointer(IOwnedDirectory directory)
    {
        if (!Exists(directory, SessionPointerV1.FileName))
        {
            return;
        }

        using FileStream source = directory.OpenOwnedFile(
            SessionPointerV1.FileName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.SequentialScan);
        using FileStream destination = directory.OpenOwnedFile(
            SessionPointerV1.PreviousFileName,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            FileOptions.WriteThrough);
        source.CopyTo(destination);
        destination.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Writes one small immutable document through a staging name and a replacing rename, so a reader
    /// sees either the previous complete file or the new complete one and never a half-written name.
    /// The staging file carries an ownership marker like every staged dependency: a writer killed between
    /// the write and the rename otherwise leaves an unmarked file that no cleanup can prove abandoned
    /// (found when a qualification run killed a live broker mid-publication).
    /// </summary>
    private static void Write<T>(IOwnedDirectory directory, string name, T document, JsonSerializerOptions options)
    {
        byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document, options));
        string prefix = string.Create(CultureInfo.InvariantCulture, $"{StagingPrefix}{Guid.NewGuid():N}");
        string stagingName = prefix + StagingSuffix;
        string ownershipName = prefix + StagingOwnershipSuffix;
        using (directory.OpenOwnedFile(
            ownershipName, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.None))
        {
            using (FileStream stream = directory.OpenOwnedFile(
                stagingName,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                FileOptions.WriteThrough))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }

            directory.ReplaceOwnedFile(stagingName, name);
        }

        try
        {
            _ = directory.RemoveOwnedFile(ownershipName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The document is published. A marker left behind is marker-only staging, which cleanup removes.
        }
    }

    private static T? Read<T>(IOwnedDirectory directory, string name)
        where T : class
    {
        if (OwnedFileName.Validate(name) is not null || !Exists(directory, name))
        {
            return null;
        }

        try
        {
            using FileStream stream = directory.OpenOwnedFile(
                name,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.SequentialScan);
            return stream.Length > 64L * 1024 * 1024
                ? null
                : JsonSerializer.Deserialize<T>(stream, SessionManifestV1.Json);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
